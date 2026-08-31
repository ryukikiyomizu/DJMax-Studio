using System;
using System.Threading;
using NAudio.Wave;

namespace DJMaxEditor.Studio.Audio
{
    /// <summary>
    /// Sits between the graph and NAudio's device and records what actually crossed the boundary.
    ///
    /// <para>
    /// This exists because of a measurement that made no sense: after a pause, the mixer group could be
    /// shown to return nothing but cleared buffers - forty of them, all counted, all handed to
    /// <see cref="Array.Clear(Array,int,int)"/> - while a loopback capture of the endpoint's own mix
    /// showed a flat level for four hundred milliseconds afterwards, with an idle control run in the
    /// same session reading the noise floor. Both observations could not be describing the same samples.
    /// </para>
    ///
    /// <para>
    /// Every managed stage between the two is either this or inside NAudio, so putting a meter here
    /// split the problem in half. The answer was that the clear itself was the lie: the buffer is a
    /// <c>byte[]</c> wearing a <c>float[]</c>'s type, so the call cleared 966 bytes of a 966-sample
    /// range and left three quarters of the previous buffer in place. See <see cref="SampleBuffers"/>.
    /// </para>
    ///
    /// <para>
    /// The cost is one pass over a buffer that the caller is about to copy anyway, on the device
    /// thread. That is affordable for a diagnostic that answered this, and the counters are plain
    /// interlocked longs so reading them from the UI thread cannot tear.
    /// </para>
    /// </summary>
    internal sealed class DeviceHandoffMeter : IWaveProvider
    {
        private readonly IWaveProvider _source;
        private readonly int _bytesPerSample;
        private readonly bool _float;

        private long _reads;
        private long _shortReads;
        private long _requestedBytes;
        private long _returnedBytes;
        private long _silentReads;
        private int _peakBits;
        private string _firstSounding;

        public DeviceHandoffMeter(IWaveProvider source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));

            WaveFormat format = source.WaveFormat;
            _float = format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32;
            _bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        /// <summary>Reads the device has asked for since the last <see cref="Reset"/>.</summary>
        public long Reads => Interlocked.Read(ref _reads);

        /// <summary>
        /// How many of those came back short. Any at all is a finding: NAudio releases the frame count
        /// it asked for whatever it was given, so a short read leaves the tail of the driver's buffer
        /// holding the previous cycle's audio and the endpoint plays it again.
        /// </summary>
        public long ShortReads => Interlocked.Read(ref _shortReads);

        public long RequestedBytes => Interlocked.Read(ref _requestedBytes);

        public long ReturnedBytes => Interlocked.Read(ref _returnedBytes);

        /// <summary>Reads whose every byte was zero - digital silence, not merely quiet.</summary>
        public long SilentReads => Interlocked.Read(ref _silentReads);

        /// <summary>Loudest magnitude handed over since the last <see cref="Reset"/>, 0 when silent throughout.</summary>
        public float Peak => BitConverter.Int32BitsToSingle(Volatile.Read(ref _peakBits));

        /// <summary>The runtime chain this meter is actually watching, in case it is not the one assumed.</summary>
        public string SourceName => _source.GetType().Name + " (" + _source.WaveFormat + ")";

        /// <summary>
        /// Where the audio sat in the first read after a <see cref="Reset"/> that was not silent, or
        /// null when every read since was silent.
        /// <para>
        /// A level alone cannot tell a mix from a leftover. Audio filling the whole buffer means the
        /// graph really produced it; audio confined to a tail past the point the caller stopped
        /// writing means the buffer was handed back only partly filled and the rest is whatever was
        /// there before. The two need completely different fixes, and one line of detail from the
        /// first offending buffer separates them.
        /// </para>
        /// </summary>
        public string FirstSoundingRead => Volatile.Read(ref _firstSounding);

        public void Reset()
        {
            Interlocked.Exchange(ref _reads, 0);
            Interlocked.Exchange(ref _shortReads, 0);
            Interlocked.Exchange(ref _requestedBytes, 0);
            Interlocked.Exchange(ref _returnedBytes, 0);
            Interlocked.Exchange(ref _silentReads, 0);
            Volatile.Write(ref _peakBits, 0);
            Volatile.Write(ref _firstSounding, null);
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);

            Interlocked.Increment(ref _reads);
            Interlocked.Add(ref _requestedBytes, count);
            Interlocked.Add(ref _returnedBytes, read);
            if (read < count)
            {
                Interlocked.Increment(ref _shortReads);
            }

            float loudest = Loudest(buffer, offset, read);
            if (loudest <= 0f)
            {
                Interlocked.Increment(ref _silentReads);
            }
            else
            {
                RaisePeak(loudest);
                if (Volatile.Read(ref _firstSounding) == null)
                {
                    Volatile.Write(ref _firstSounding, Describe(buffer, offset, read, count));
                }
            }

            return read;
        }

        /// <summary>
        /// Where the sound is inside one buffer, as text. Built on the device thread, which is why it
        /// is done at most once per <see cref="Reset"/>.
        /// </summary>
        private string Describe(byte[] buffer, int offset, int read, int count)
        {
            int first = -1;
            int last = -1;
            int sounding = 0;
            int samples = 0;
            int end = offset + read - _bytesPerSample + 1;

            for (int at = offset; at < end; at += _bytesPerSample, samples++)
            {
                float sample = _float
                    ? BitConverter.ToSingle(buffer, at)
                    : BitConverter.ToInt16(buffer, at) / 32768f;
                if (sample == 0f)
                {
                    continue;
                }

                if (first < 0)
                {
                    first = samples;
                }

                last = samples;
                sounding++;
            }

            return "read " + read + "/" + count + " bytes, " + samples + " samples, " +
                sounding + " non-zero, first at " + first + ", last at " + last;
        }

        private float Loudest(byte[] buffer, int offset, int bytes)
        {
            float loudest = 0f;
            int end = offset + bytes - _bytesPerSample + 1;

            for (int at = offset; at < end; at += _bytesPerSample)
            {
                float sample = _float
                    ? BitConverter.ToSingle(buffer, at)
                    : BitConverter.ToInt16(buffer, at) / 32768f;
                float magnitude = sample >= 0f ? sample : -sample;
                if (magnitude > loudest)
                {
                    loudest = magnitude;
                }
            }

            return loudest;
        }

        private void RaisePeak(float candidate)
        {
            // Compare-and-swap rather than a lock: the device thread is the only writer today, but a
            // meter that would corrupt if that changed is a trap left for later.
            while (true)
            {
                int seen = Volatile.Read(ref _peakBits);
                if (BitConverter.Int32BitsToSingle(seen) >= candidate)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _peakBits, BitConverter.SingleToInt32Bits(candidate), seen) == seen)
                {
                    return;
                }
            }
        }
    }
}
