using System;
using System.Threading;
using NAudio.Wave;

namespace DJMaxEditor.Studio.Audio
{
    /// <summary>
    /// The same meter as <see cref="DeviceHandoffMeter"/>, one stage earlier: in the float domain,
    /// wrapped directly around the mixer group with no NAudio code in between.
    ///
    /// <para>
    /// Two meters were needed because the first one produced a result that could not be read on its
    /// own. The group counted forty buffers cleared while paused; the bytes reaching NAudio in that
    /// same window were full length, never short, and carried audio from sample 242 of 966 onwards.
    /// Between the group and that meter sit NAudio's sample-to-byte conversion and, because the
    /// endpoint runs at a different rate than the graph, its resampler - each with a scratch buffer of
    /// its own that could equally have been the one holding the leftovers.
    /// </para>
    ///
    /// <para>
    /// This tap settled it: the same 242-of-966 pattern showed here too, so the range mismatch was
    /// ours and the fix belonged in the graph. It was the byte-versus-sample confusion described in
    /// <see cref="SampleBuffers"/>. Both meters stay: they cost one pass over each buffer, they are
    /// what the loopback probe reads, and this is exactly the class of fault that hides from every
    /// offline test.
    /// </para>
    /// </summary>
    internal sealed class GraphTapSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;

        private long _reads;
        private long _shortReads;
        private long _requestedSamples;
        private long _returnedSamples;
        private long _silentReads;
        private int _peakBits;
        private string _firstSounding;

        public GraphTapSampleProvider(ISampleProvider source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public long Reads => Interlocked.Read(ref _reads);

        public long ShortReads => Interlocked.Read(ref _shortReads);

        public long RequestedSamples => Interlocked.Read(ref _requestedSamples);

        public long ReturnedSamples => Interlocked.Read(ref _returnedSamples);

        public long SilentReads => Interlocked.Read(ref _silentReads);

        public float Peak => BitConverter.Int32BitsToSingle(Volatile.Read(ref _peakBits));

        /// <summary>See <see cref="DeviceHandoffMeter.FirstSoundingRead"/>.</summary>
        public string FirstSoundingRead => Volatile.Read(ref _firstSounding);

        public void Reset()
        {
            Interlocked.Exchange(ref _reads, 0);
            Interlocked.Exchange(ref _shortReads, 0);
            Interlocked.Exchange(ref _requestedSamples, 0);
            Interlocked.Exchange(ref _returnedSamples, 0);
            Interlocked.Exchange(ref _silentReads, 0);
            Volatile.Write(ref _peakBits, 0);
            Volatile.Write(ref _firstSounding, null);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);

            Interlocked.Increment(ref _reads);
            Interlocked.Add(ref _requestedSamples, count);
            Interlocked.Add(ref _returnedSamples, read);
            if (read < count)
            {
                Interlocked.Increment(ref _shortReads);
            }

            int first = -1;
            int last = -1;
            int sounding = 0;
            float loudest = 0f;

            for (int i = 0; i < read; i++)
            {
                float sample = buffer[offset + i];
                if (sample == 0f)
                {
                    continue;
                }

                if (first < 0)
                {
                    first = i;
                }

                last = i;
                sounding++;

                float magnitude = sample >= 0f ? sample : -sample;
                if (magnitude > loudest)
                {
                    loudest = magnitude;
                }
            }

            if (sounding == 0)
            {
                Interlocked.Increment(ref _silentReads);
                return read;
            }

            RaisePeak(loudest);
            if (Volatile.Read(ref _firstSounding) == null)
            {
                Volatile.Write(ref _firstSounding,
                    "read " + read + "/" + count + " samples, " + sounding +
                    " non-zero, first at " + first + ", last at " + last);
            }

            return read;
        }

        private void RaisePeak(float candidate)
        {
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
