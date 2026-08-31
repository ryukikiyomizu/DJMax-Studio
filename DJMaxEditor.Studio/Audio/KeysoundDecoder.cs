using System;
using System.IO;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DJMaxEditor.Studio.Audio
{
    /// <summary>
    /// Turns a charted keysound name into a decoded <see cref="KeysoundSample"/> in the mixer's
    /// format. Never throws for bad input: every failure is reported through the return value and
    /// an error string, because a chart with one missing sample must still open and play.
    /// </summary>
    internal static class KeysoundDecoder
    {
        /// <summary>
        /// Resolves the file a charted name refers to, or null if nothing matches.
        /// <para>
        /// Probe order, and why:
        /// </para>
        /// <list type="number">
        /// <item><description><c>name + ".ogg"</c> then <c>name + ".wav"</c> - .pt and .tq charts
        /// routinely store instrument names without an extension, and DJMAX ships Vorbis.</description></item>
        /// <item><description><c>name</c> exactly as charted - so an explicit extension always wins
        /// over a same-stem file of another type.</description></item>
        /// <item><description><c>name</c> with its extension swapped for .ogg then .wav - BMS-derived
        /// charts habitually declare <c>#WAVxx foo.wav</c> for a <c>foo.ogg</c> that is what actually
        /// shipped. Last resort, so it can never shadow a file that really exists.</description></item>
        /// </list>
        /// </summary>
        internal static string ResolvePath(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            if (Exists(name + ".ogg", out string resolved) ||
                Exists(name + ".wav", out resolved) ||
                Exists(name, out resolved) ||
                Exists(ChangeExtensionSafe(name, ".ogg"), out resolved) ||
                Exists(ChangeExtensionSafe(name, ".wav"), out resolved))
            {
                return resolved;
            }

            return null;
        }

        /// <summary>
        /// Decodes <paramref name="name"/> into <paramref name="target"/>'s rate and channel count.
        /// </summary>
        /// <param name="maxBytesPerSound">
        /// Hard ceiling on one decoded buffer. Guards against a chart pointing at a multi-hour file:
        /// decoded 48 kHz stereo float costs ~23 MB per minute, and the legacy backend used to
        /// stream long files instead of holding them (see <see cref="NAudioKeysoundPlayer.LoadSound"/>).
        /// </param>
        internal static bool TryLoad(string name, WaveFormat target, int mode, long maxBytesPerSound,
            out KeysoundSample sample, out string error)
        {
            sample = null;
            error = null;

            string path = ResolvePath(name);
            if (path == null)
            {
                error = "file not found: " + (name ?? "<null>");
                return false;
            }

            IDisposable reader = null;
            try
            {
                ISampleProvider source = OpenReader(path, out reader);
                int sourceSampleRate = source.WaveFormat.SampleRate;
                int sourceChannels = source.WaveFormat.Channels;

                // Bound the decode to the length the container declares, before any conversion.
                // See BoundedSampleProvider: reading one block past the end is what hangs NVorbis.
                source = Bound(source, reader);

                source = ConvertRate(source, target.SampleRate);
                source = ConvertChannels(source, target.Channels);

                float[] samples = ReadToEnd(source, target, maxBytesPerSound, out error);
                if (samples == null)
                {
                    return false;
                }

                if (samples.Length == 0)
                {
                    // FMOD's createSound also refused empty/zero-length files, and a zero-length
                    // voice would be dropped by the mixer on its first read anyway.
                    error = "decoded to 0 samples";
                    return false;
                }

                sample = new KeysoundSample(samples, target.SampleRate, target.Channels,
                    sourceSampleRate, sourceChannels, path, mode);
                return true;
            }
            catch (Exception ex)
            {
                // Corrupt Vorbis, a .wav with a bogus header, a locked file, an unsupported codec:
                // all of it is "this keysound is unavailable", never a crash.
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally
            {
                if (reader != null)
                {
                    try
                    {
                        reader.Dispose();
                    }
                    catch (Exception)
                    {
                        // Nothing useful to do while unwinding a failed decode.
                    }
                }
            }
        }

        private static bool Exists(string candidate, out string resolved)
        {
            resolved = null;
            if (string.IsNullOrEmpty(candidate))
            {
                return false;
            }

            try
            {
                if (File.Exists(candidate))
                {
                    resolved = candidate;
                    return true;
                }
            }
            catch (Exception)
            {
                // Chart data is untrusted: an unmappable path must read as "missing", not throw.
            }

            return false;
        }

        private static string ChangeExtensionSafe(string name, string extension)
        {
            try
            {
                string current = Path.GetExtension(name);
                if (string.IsNullOrEmpty(current) ||
                    string.Equals(current, extension, StringComparison.OrdinalIgnoreCase))
                {
                    return null; // nothing new to try
                }

                return Path.ChangeExtension(name, extension);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static ISampleProvider OpenReader(string path, out IDisposable disposable)
        {
            bool looksVorbis = string.Equals(Path.GetExtension(path), ".ogg", StringComparison.OrdinalIgnoreCase);
            if (looksVorbis)
            {
                var vorbis = new VorbisWaveReader(path);
                disposable = vorbis;
                return vorbis;
            }

            try
            {
                var file = new AudioFileReader(path);
                disposable = file;
                return file;
            }
            catch (Exception)
            {
                // The extension lied, or there was none. Charts do point at extension-less names
                // whose bytes are Vorbis, so give the Ogg decoder a turn before giving up.
                var vorbis = new VorbisWaveReader(path);
                disposable = vorbis;
                return vorbis;
            }
        }

        /// <summary>
        /// Caps <paramref name="source"/> at the sample count its container declares, when the
        /// reader knows one.
        /// <para>
        /// This is not an optimisation, it is a hang fix. NVorbis 0.10.x (via NAudio.Vorbis 1.5.0)
        /// does not always return 0 for the read that starts past the last audio sample: on some
        /// files it spins inside <c>StreamDecoder.Read</c> and never comes back. A real chart in the
        /// wild trips it - <c>sonoflong</c>'s <c>10_fazz_synth 10-174.ogg</c>, 8 KB and 0.15 s long,
        /// whose header parses perfectly. Loading that folder wedged the editor's UI thread
        /// permanently, which Windows reports as <c>AppHangTransient</c> with no exception and no
        /// stack. Measured on that file: read-until-zero never returns (byte reads, sample reads,
        /// with or without the resampler, at every block size tried), while stopping at the declared
        /// length yields the full 13,230 samples in 91 ms. Across all 276 keysounds in that folder
        /// the bound removes the hang and costs nothing - 10.8 s total versus 24.7 s.
        /// </para>
        /// <para>
        /// The bound sits on the reader rather than on the converted chain so the ratio arithmetic
        /// stays out of it: the resampler and channel mixers see an ordinary end-of-stream and flush
        /// themselves. A chained Ogg (several logical streams concatenated) would be truncated to
        /// whatever its first stream declares; no DJMAX keysound is built that way, and silently
        /// clipping a tail beats hanging the editor.
        /// </para>
        /// </summary>
        private static ISampleProvider Bound(ISampleProvider source, IDisposable reader)
        {
            // Both readers we open expose Length in bytes of their 32-bit float output.
            WaveStream stream = reader as WaveStream;
            if (stream == null)
            {
                return source;
            }

            long declared;
            try
            {
                declared = stream.Length / sizeof(float);
            }
            catch (Exception)
            {
                // A stream that cannot report its length is the unbounded case.
                return source;
            }

            return declared > 0 ? new BoundedSampleProvider(source, declared) : source;
        }

        private static ISampleProvider ConvertRate(ISampleProvider source, int targetSampleRate)        {
            if (source.WaveFormat.SampleRate == targetSampleRate)
            {
                return source;
            }

            // WDL's resampler is fully managed - no Media Foundation, no COM - so load-time
            // conversion works on headless build agents and Windows N editions too.
            return new WdlResamplingSampleProvider(source, targetSampleRate);
        }

        private static ISampleProvider ConvertChannels(ISampleProvider source, int targetChannels)
        {
            int channels = source.WaveFormat.Channels;
            if (channels == targetChannels)
            {
                return source;
            }

            if (channels == 1 && targetChannels == 2)
            {
                // Duplicates the mono signal into both channels at unity. Combined with the linear
                // pan law in KeysoundVoice this reproduces FMOD's behaviour exactly: a centred mono
                // keysound is at full level in both speakers.
                return new MonoToStereoSampleProvider(source);
            }

            if (channels == 2 && targetChannels == 1)
            {
                return new StereoToMonoSampleProvider(source);
            }

            // Anything exotic (5.1 stems do occasionally turn up in user folders): take the first
            // target-count channels, which is what MultiplexingSampleProvider maps by default.
            return new MultiplexingSampleProvider(new[] { source }, targetChannels);
        }

        private static float[] ReadToEnd(ISampleProvider source, WaveFormat target, long maxBytesPerSound, out string error)
        {
            error = null;

            int blockSamples = Math.Max(target.Channels, target.SampleRate / 4 * target.Channels); // 250 ms
            var block = new float[blockSamples];
            var accumulated = new float[blockSamples];
            int length = 0;
            long maxSamples = Math.Max(1, maxBytesPerSound / sizeof(float));

            while (true)
            {
                int read = source.Read(block, 0, block.Length);
                if (read <= 0)
                {
                    break;
                }

                if (length + read > maxSamples)
                {
                    error = "decoded size exceeds the per-sound limit of " + maxBytesPerSound + " bytes";
                    return null;
                }

                if (length + read > accumulated.Length)
                {
                    int capacity = accumulated.Length;
                    while (capacity < length + read)
                    {
                        capacity *= 2;
                    }

                    Array.Resize(ref accumulated, capacity);
                }

                Array.Copy(block, 0, accumulated, length, read);
                length += read;
            }

            if (length != accumulated.Length)
            {
                Array.Resize(ref accumulated, length);
            }

            return accumulated;
        }

        /// <summary>
        /// Passes at most a fixed number of samples through, then reports end-of-stream without
        /// asking its source for more. See <see cref="Bound"/> for why that matters.
        /// </summary>
        private sealed class BoundedSampleProvider : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private readonly long _limit;
            private long _delivered;

            internal BoundedSampleProvider(ISampleProvider source, long limit)
            {
                _source = source;
                _limit = limit;
            }

            public WaveFormat WaveFormat => _source.WaveFormat;

            public int Read(float[] buffer, int offset, int count)
            {
                long remaining = _limit - _delivered;
                if (remaining <= 0)
                {
                    return 0;
                }

                int want = count < remaining ? count : (int)remaining;
                int read = _source.Read(buffer, offset, want);
                if (read > 0)
                {
                    _delivered += read;
                }

                return read;
            }
        }
    }
}
