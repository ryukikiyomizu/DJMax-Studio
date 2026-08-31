using System;
using System.Diagnostics;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DJMaxEditor.Studio.Audio
{
    /// <summary>
    /// An <see cref="IAudioOutput"/> that consumes the mixer graph without a device.
    /// <para>
    /// Two uses:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// Tests: construct with <c>realTimePump: false</c> and call <see cref="Pump(int)"/> to advance
    /// the graph by an exact number of frames on the calling thread. Every mixing decision then
    /// becomes deterministic and inspectable, with no device and no clock involved.
    /// </description></item>
    /// <item><description>
    /// The no-sound fallback: constructed with <c>realTimePump: true</c> when no output endpoint
    /// exists, so the editor still runs. This mirrors what the FMOD Ex backend did when
    /// <c>getNumDrivers</c> returned 0 - it switched the system to <c>OUTPUTTYPE.NOSOUND</c>
    /// rather than refusing to start. Pumping in (approximate) real time also keeps voice
    /// bookkeeping honest: positions advance and finished voices still leave the mixer.
    /// </description></item>
    /// </list>
    /// </summary>
    public sealed class NullAudioOutput : IAudioOutput
    {
        private const int PumpIntervalMs = 20;

        private readonly bool _realTimePump;
        private readonly object _pumpGate = new object();

        private ISampleProvider _source;
        private float[] _pumpBuffer;
        private IWaveProvider _deviceLike;
        private byte[] _deviceBytes;
        private Thread _pumpThread;
        private volatile bool _running;
        private volatile bool _disposed;
        private long _framesRendered;

        /// <param name="realTimePump">
        /// When true a background thread pulls the graph at wall-clock speed (the silent-driver
        /// case). When false nothing is pulled until <see cref="Pump(int)"/> is called, which is
        /// what tests want.
        /// </param>
        public NullAudioOutput(bool realTimePump = false)
        {
            _realTimePump = realTimePump;
        }

        /// <inheritdoc/>
        public string Name => _realTimePump ? "Null (no device, real-time)" : "Null (no device, manual pump)";

        /// <inheritdoc/>
        public int LatencyMilliseconds => _realTimePump ? PumpIntervalMs : 0;

        /// <inheritdoc/>
        public bool IsRealDevice => false;

        /// <summary>Total frames pulled out of the graph since <see cref="Init"/>.</summary>
        public long FramesRendered => Interlocked.Read(ref _framesRendered);

        /// <summary>The format the graph was bound with, or null before <see cref="Init"/>.</summary>
        public WaveFormat WaveFormat => _source?.WaveFormat;

        /// <inheritdoc/>
        public void Init(ISampleProvider source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            lock (_pumpGate)
            {
                _source = source;
                _framesRendered = 0;
            }
        }

        /// <inheritdoc/>
        public void Play()
        {
            if (!_realTimePump || _running || _disposed)
            {
                return;
            }

            _running = true;
            _pumpThread = new Thread(PumpLoop)
            {
                // Background so a stuck pump can never keep the process alive; above normal
                // because it stands in for a device thread and drives voice retirement.
                IsBackground = true,
                Name = "DJMax silent audio pump",
                Priority = ThreadPriority.AboveNormal
            };
            _pumpThread.Start();
        }

        /// <inheritdoc/>
        public void Stop()
        {
            _running = false;
            Thread thread = _pumpThread;
            _pumpThread = null;
            if (thread != null && thread.IsAlive)
            {
                thread.Join(200);
            }
        }

        /// <summary>
        /// Pulls exactly <paramref name="frames"/> frames through the graph and returns the
        /// interleaved samples that came out. Test-facing: the returned buffer is what lets a
        /// headless assertion measure amplitude and per-channel energy.
        /// </summary>
        public float[] Pump(int frames)
        {
            if (frames <= 0)
            {
                return new float[0];
            }

            ISampleProvider source = _source;
            if (source == null)
            {
                throw new InvalidOperationException("Pump called before Init.");
            }

            int channels = source.WaveFormat.Channels;
            var buffer = new float[frames * channels];
            int read = source.Read(buffer, 0, buffer.Length);
            if (read < buffer.Length)
            {
                // Array.Clear is correct here, and only here: this buffer was allocated three lines up
                // as a float[], so the runtime agrees with the static type. A device's buffer does not -
                // see SampleBuffers, and PumpThroughDeviceBuffer below for the difference it makes.
                Array.Clear(buffer, read, buffer.Length - read);
            }

            Interlocked.Add(ref _framesRendered, frames);
            return buffer;
        }

        /// <summary>Pumps a duration instead of a frame count. Rounds down to whole frames.</summary>
        public float[] PumpMilliseconds(double milliseconds)
        {
            ISampleProvider source = _source;
            if (source == null)
            {
                throw new InvalidOperationException("PumpMilliseconds called before Init.");
            }

            int frames = (int)(source.WaveFormat.SampleRate * milliseconds / 1000.0);
            return Pump(frames);
        }

        /// <summary>
        /// Pumps the graph the way a driver does, and returns the same samples for measuring.
        /// <para>
        /// <see cref="Pump(int)"/> hands the graph a freshly allocated, genuinely typed
        /// <c>float[]</c>. No device ever does that, and the difference is not academic: a real output
        /// goes through NAudio's <c>SampleToWaveProvider</c>, which wraps the driver's <c>byte[]</c> in
        /// a <c>WaveBuffer</c> and passes its float view down the graph - the same object, retyped. A
        /// range argument that is correct in samples covers a quarter of it in bytes, and the byte
        /// buffer is reused between reads, so whatever the previous read left there is still there.
        /// That combination hid the pause bug from every headless case for five rounds; see
        /// <see cref="SampleBuffers"/>.
        /// </para>
        /// <para>
        /// So this pump reproduces both halves deliberately: one reused byte buffer, reached through
        /// the same NAudio stage the device uses. Any assertion that passes under <see cref="Pump(int)"/>
        /// and fails here is measuring exactly what the owner hears.
        /// </para>
        /// </summary>
        public float[] PumpThroughDeviceBuffer(int frames)
        {
            if (frames <= 0)
            {
                return new float[0];
            }

            ISampleProvider source = _source;
            if (source == null)
            {
                throw new InvalidOperationException("PumpThroughDeviceBuffer called before Init.");
            }

            if (_deviceLike == null)
            {
                _deviceLike = new SampleToWaveProvider(source);
            }

            int bytes = frames * source.WaveFormat.Channels * sizeof(float);
            if (_deviceBytes == null || _deviceBytes.Length < bytes)
            {
                _deviceBytes = new byte[bytes];
            }

            int read = _deviceLike.Read(_deviceBytes, 0, bytes);
            var samples = new float[read / sizeof(float)];
            Buffer.BlockCopy(_deviceBytes, 0, samples, 0, samples.Length * sizeof(float));

            Interlocked.Add(ref _framesRendered, frames);
            return samples;
        }

        private void PumpLoop()
        {
            var clock = Stopwatch.StartNew();
            long renderedFrames = 0;
            int sampleRate = _source?.WaveFormat.SampleRate ?? 48000;
            int channels = _source?.WaveFormat.Channels ?? 2;

            while (_running && !_disposed)
            {
                ISampleProvider source = _source;
                if (source == null)
                {
                    break;
                }

                // Render only as much as wall-clock time has actually passed, so voice positions
                // advance at the same rate they would on a real device.
                long dueFrames = (long)(clock.Elapsed.TotalSeconds * sampleRate);
                int frames = (int)Math.Min(dueFrames - renderedFrames, sampleRate); // clamp: never more than 1s
                if (frames > 0)
                {
                    int needed = frames * channels;
                    if (_pumpBuffer == null || _pumpBuffer.Length < needed)
                    {
                        _pumpBuffer = new float[needed];
                    }

                    try
                    {
                        source.Read(_pumpBuffer, 0, needed);
                    }
                    catch (Exception)
                    {
                        // A throwing graph must not take the process down from a background thread.
                        break;
                    }

                    renderedFrames += frames;
                    Interlocked.Add(ref _framesRendered, frames);
                }

                Thread.Sleep(PumpIntervalMs);
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop();
            _source = null;
            _pumpBuffer = null;
        }
    }
}
