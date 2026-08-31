using System;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DJMaxEditor.Studio.Audio
{
    /// <summary>
    /// The real device output: one shared endpoint for the whole application.
    /// <para>
    /// Tier order, decided at <see cref="Init"/> time because that is where NAudio actually opens
    /// the driver and therefore where failures surface:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// WASAPI in <b>shared</b> mode with event-driven callbacks. Shared (not exclusive) is
    /// deliberate - an editor must never seize the endpoint and mute the rest of the desktop, and
    /// the user is usually watching a reference video in a browser while charting.
    /// </description></item>
    /// <item><description>waveOut with a float format - for legacy or unusual drivers.</description></item>
    /// <item><description>waveOut converted to 16-bit - for drivers that reject IEEE float.</description></item>
    /// </list>
    /// <para>
    /// If every tier fails, <see cref="Init"/> throws and <see cref="NAudioKeysoundPlayer"/>
    /// swaps in a <see cref="NullAudioOutput"/> so the editor still opens charts silently.
    /// </para>
    /// </summary>
    public sealed class NAudioDeviceOutput : IAudioOutput
    {
        private readonly int _requestedLatencyMs;

        private IWavePlayer _device;
        private string _name = "not initialised";
        private bool _disposed;

        /// <param name="latencyMilliseconds">
        /// Buffer size to ask the driver for. 60 ms is the default: keysound triggers are already
        /// quantised to the sequencer's tick, so a few extra milliseconds of buffer are inaudible,
        /// while a too-small buffer glitches badly the moment a chart loads several hundred
        /// samples on the UI thread.
        /// </param>
        public NAudioDeviceOutput(int latencyMilliseconds = 60)
        {
            _requestedLatencyMs = Math.Max(10, latencyMilliseconds);
        }

        /// <inheritdoc/>
        public string Name => _name;

        /// <inheritdoc/>
        public int LatencyMilliseconds => _requestedLatencyMs;

        /// <inheritdoc/>
        public bool IsRealDevice => true;

        /// <summary>
        /// Which tier ended up being used, plus the errors from the tiers that were skipped.
        /// Surfaced through the diagnostics overlay so "why is there no sound" is answerable
        /// without a debugger.
        /// </summary>
        public string SelectionLog { get; private set; } = string.Empty;

        /// <summary>
        /// What crossed into NAudio, once <see cref="Init"/> has picked a tier. Null before that.
        /// See <see cref="DeviceHandoffMeter"/> for why the boundary is worth metering.
        /// </summary>
        internal DeviceHandoffMeter Handoff { get; private set; }

        /// <summary>The same, one stage earlier and in floats. See <see cref="GraphTapSampleProvider"/>.</summary>
        internal GraphTapSampleProvider Tap { get; private set; }

        /// <inheritdoc/>
        public void Init(ISampleProvider source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(NAudioDeviceOutput));
            }

            var log = new StringBuilder();

            // Both meters are inert unless something reads them; they exist because the pause tail
            // could not be attributed without knowing which side of NAudio it comes from.
            var tap = new GraphTapSampleProvider(source);
            Tap = tap;

            if (TryInit(log, "WASAPI shared",
                    () => new WasapiOut(AudioClientShareMode.Shared, true, _requestedLatencyMs),
                    () => new SampleToWaveProvider(tap)))
            {
                return;
            }

            if (TryInit(log, "waveOut float",
                    () => new WaveOutEvent { DesiredLatency = _requestedLatencyMs, NumberOfBuffers = 3 },
                    () => new SampleToWaveProvider(tap)))
            {
                return;
            }

            if (TryInit(log, "waveOut 16-bit",
                    () => new WaveOutEvent { DesiredLatency = _requestedLatencyMs, NumberOfBuffers = 3 },
                    () => new SampleToWaveProvider16(tap)))
            {
                return;
            }

            SelectionLog = log.ToString();
            throw new InvalidOperationException("No usable audio output device. " + SelectionLog);
        }

        private bool TryInit(StringBuilder log, string tier, Func<IWavePlayer> createDevice, Func<IWaveProvider> createProvider)
        {
            IWavePlayer device = null;
            try
            {
                device = createDevice();
                var handoff = new DeviceHandoffMeter(createProvider());
                device.Init(handoff);
                _device = device;
                _name = tier;
                Handoff = handoff;
                log.Append(tier).Append(": selected");
                SelectionLog = log.ToString();
                return true;
            }
            catch (Exception ex)
            {
                // Any failure here means "this tier is unavailable on this machine", never a bug
                // worth crashing over - fall through to the next tier.
                log.Append(tier).Append(": ").Append(ex.GetType().Name).Append(" - ").Append(ex.Message).Append("; ");
                if (device != null)
                {
                    try
                    {
                        device.Dispose();
                    }
                    catch (Exception)
                    {
                        // A half-open driver can throw on Dispose; nothing useful to do.
                    }
                }

                return false;
            }
        }

        /// <inheritdoc/>
        public void Play()
        {
            _device?.Play();
        }

        /// <inheritdoc/>
        public void Stop()
        {
            try
            {
                _device?.Stop();
            }
            catch (Exception)
            {
                // Stopping a device that the OS already took away throws; shutdown continues.
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
            try
            {
                _device?.Dispose();
            }
            catch (Exception)
            {
                // Same reasoning as Stop.
            }

            _device = null;
        }
    }
}
