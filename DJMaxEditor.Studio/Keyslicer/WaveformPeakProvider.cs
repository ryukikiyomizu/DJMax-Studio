using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DJMaxEditor.Studio.Keyslicer
{
    /// <summary>
    /// Produces min/max peak pairs for a waveform overview and a zoomed view.
    /// Peaks are computed from decoded PCM; the cache is per-pixel for the current
    /// viewport width and zoom, not per-sample, to keep draw cost O(width) rather
    /// than O(samples). Comparable to Woslicer/sayaslicer waveform caches.
    /// </summary>
    public sealed class WaveformPeak
    {
        public float Min { get; set; }
        public float Max { get; set; }
        public float Rms { get; set; }
    }

    public sealed class WaveformData
    {
        public WaveformPeak[] Peaks { get; set; } = Array.Empty<WaveformPeak>();
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public double DurationMs { get; set; }
        public long TotalSamples { get; set; }
        // Raw mono samples for detailed zoom view (downsampled to at most 200k points).
        public float[] ZoomSamples { get; set; } = Array.Empty<float>();
    }

    public static class WaveformPeakProvider
    {
        /// <summary>
        /// Decode the file and produce peaks for a viewport <paramref name="widthPixels"/> wide.
        /// Runs off the UI thread; caller marshals the result back via Dispatcher.
        /// </summary>
        public static async Task<WaveformData> BuildAsync(string path, int widthPixels, CancellationToken token)
        {
            return await Task.Run(() => Build(path, widthPixels, token), token);
        }

        public static WaveformData Build(string path, int widthPixels, CancellationToken token)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return new WaveformData();

            widthPixels = Math.Max(64, Math.Min(4096, widthPixels));

            IDisposable readerDisp = null;
            try
            {
                ISampleProvider reader = OpenReader(path, out readerDisp);
                int srcRate = reader.WaveFormat.SampleRate;
                int srcCh = reader.WaveFormat.Channels;

                // Resample to 44.1k mono for peak building to bound memory on large sources.
                if (srcCh > 1)
                    reader = new StereoToMonoSampleProvider(reader) { LeftVolume = 0.5f, RightVolume = 0.5f };
                // Keep original rate; duration calc uses it directly. Downmix only.

                // Read in blocks.
                const int blockSize = 4096;
                float[] block = new float[blockSize];
                var samples = new List<float>(1 << 20); // up to ~1M before downsample

                // Bound with file length where possible (see KeysoundDecoder.Bound).
                reader = BoundReader(reader, readerDisp);

                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    int read = reader.Read(block, 0, block.Length);
                    if (read <= 0) break;
                    // Append, but cap at ~10 minutes at 44.1k mono = 26M floats ~100 MB.
                    // For hour-long sessions, downsample as we go.
                    for (int i = 0; i < read; i++)
                    {
                        // Simple decimation for huge files: keep every Nth if growing too large.
                        samples.Add(block[i]);
                        if (samples.Count > 8_000_000)
                        {
                            // Halve by keeping every 2nd sample.
                            for (int j = 0, k = 0; j < samples.Count; j += 2, k++)
                                samples[k] = samples[j];
                            samples.RemoveRange(samples.Count / 2, samples.Count - samples.Count / 2);
                        }
                    }
                }

                long totalMonoSamples = samples.Count;
                double durationMs = srcRate > 0 ? (totalMonoSamples / (double)srcRate) * 1000.0 : 0;

                // Build per-pixel peaks.
                WaveformPeak[] peaks = new WaveformPeak[widthPixels];
                if (samples.Count == 0)
                {
                    for (int i = 0; i < peaks.Length; i++) peaks[i] = new WaveformPeak();
                }
                else
                {
                    double samplesPerPixel = (double)samples.Count / widthPixels;
                    for (int px = 0; px < widthPixels; px++)
                    {
                        int start = (int)(px * samplesPerPixel);
                        int end = (int)((px + 1) * samplesPerPixel);
                        if (end <= start) end = start + 1;
                        if (start >= samples.Count) { peaks[px] = new WaveformPeak(); continue; }
                        if (end > samples.Count) end = samples.Count;
                        float min = float.MaxValue, max = float.MinValue;
                        double sumSq = 0;
                        for (int s = start; s < end; s++)
                        {
                            float v = samples[s];
                            if (v < min) min = v;
                            if (v > max) max = v;
                            sumSq += v * v;
                        }
                        int count = end - start;
                        peaks[px] = new WaveformPeak
                        {
                            Min = min == float.MaxValue ? 0 : min,
                            Max = max == float.MinValue ? 0 : max,
                            Rms = count > 0 ? (float)Math.Sqrt(sumSq / count) : 0
                        };
                    }
                }

                // Zoom samples: keep a downsampled copy for detailed view (max ~200k).
                float[] zoom = samples.ToArray();
                if (zoom.Length > 200_000)
                {
                    int step = zoom.Length / 200_000;
                    var down = new float[200_000];
                    for (int i = 0, j = 0; i < down.Length; i++, j += step)
                        down[i] = zoom[j];
                    zoom = down;
                }

                return new WaveformData
                {
                    Peaks = peaks,
                    SampleRate = srcRate,
                    Channels = srcCh,
                    DurationMs = durationMs,
                    TotalSamples = totalMonoSamples,
                    ZoomSamples = zoom
                };
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return new WaveformData();
            }
            finally
            {
                try { readerDisp?.Dispose(); } catch { }
            }
        }

        private static ISampleProvider OpenReader(string path, out IDisposable disposable)
        {
            bool looksVorbis = string.Equals(Path.GetExtension(path), ".ogg", StringComparison.OrdinalIgnoreCase);
            if (looksVorbis)
            {
                var v = new VorbisWaveReader(path);
                disposable = v;
                return v;
            }
            try
            {
                var a = new AudioFileReader(path);
                disposable = a;
                return a;
            }
            catch
            {
                var v = new VorbisWaveReader(path);
                disposable = v;
                return v;
            }
        }

        private static ISampleProvider BoundReader(ISampleProvider source, IDisposable reader)
        {
            // If we can get sample count from reader, clamp.
            try
            {
                if (reader is VorbisWaveReader vbr)
                {
                    long total = vbr.TotalTime.Ticks > 0 ? (long)(vbr.TotalTime.TotalSeconds * vbr.WaveFormat.SampleRate) : 0;
                    if (total > 0)
                        return new CappedSampleProvider(source, total);
                }
                if (reader is AudioFileReader afr && afr.Length > 0)
                {
                    long total = afr.Length / (afr.WaveFormat.BitsPerSample / 8) / afr.WaveFormat.Channels;
                    // AudioFileReader already bounded; no wrap needed.
                }
            }
            catch { }
            return source;
        }

        private sealed class CappedSampleProvider : ISampleProvider
        {
            private readonly ISampleProvider _inner;
            private readonly long _capSamples;
            private long _read;
            public CappedSampleProvider(ISampleProvider inner, long cap) { _inner = inner; _capSamples = cap; WaveFormat = inner.WaveFormat; }
            public WaveFormat WaveFormat { get; }
            public int Read(float[] buffer, int offset, int count)
            {
                if (_read >= _capSamples) return 0;
                long remain = _capSamples - _read;
                int want = (int)Math.Min(count, remain * WaveFormat.Channels);
                // For mono we normalized; for multi-ch, samples = frames*channels.
                // Simpler: cap by count directly for our mono case.
                want = (int)Math.Min(count, remain);
                int read = _inner.Read(buffer, offset, want);
                _read += read;
                return read;
            }
        }
    }
}
