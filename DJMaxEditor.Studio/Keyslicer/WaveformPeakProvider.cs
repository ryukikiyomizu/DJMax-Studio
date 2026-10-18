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

                // Downmix to mono for peak building — 24/32-bit float is already normalized by NAudio.
                if (srcCh > 1)
                    reader = KeyslicerAudioReader.DownmixToMono(reader);

                reader = BoundReader(reader, readerDisp);

                // Try to estimate total mono samples for streaming tile-cache build (avoids List for >2GB).
                long estimatedTotal = EstimateTotalMonoSamples(readerDisp, srcRate, srcCh);
                // If estimate available and file is large, use streaming direct-to-peaks to avoid >2GB List growth.
                if (estimatedTotal > 8_000_000)
                {
                    return BuildStreaming(reader, readerDisp, srcRate, srcCh, widthPixels, estimatedTotal, token);
                }

                // Small/medium file: keep existing in-memory path (simpler, gives exact ZoomSamples).
                const int blockSize = 4096;
                float[] block = new float[blockSize];
                var samples = new List<float>(1 << 20);

                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    int read = reader.Read(block, 0, block.Length);
                    if (read <= 0) break;
                    for (int i = 0; i < read; i++)
                    {
                        samples.Add(block[i]);
                        if (samples.Count > 8_000_000)
                        {
                            for (int j = 0, k = 0; j < samples.Count; j += 2, k++)
                                samples[k] = samples[j];
                            samples.RemoveRange(samples.Count / 2, samples.Count - samples.Count / 2);
                        }
                    }
                }

                long totalMonoSamples = samples.Count;
                double durationMs = srcRate > 0 ? (totalMonoSamples / (double)srcRate) * 1000.0 : 0;
                // If we halved, estimate true duration from estimatedTotal if available
                if (estimatedTotal > 0 && totalMonoSamples < estimatedTotal / 2)
                {
                    // We decimated, so duration should reflect original rate
                    durationMs = srcRate > 0 ? (estimatedTotal / (double)srcRate) * 1000.0 : durationMs;
                    totalMonoSamples = estimatedTotal;
                }

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

        private static WaveformData BuildStreaming(ISampleProvider reader, IDisposable disp, int srcRate, int srcCh, int widthPixels, long estimatedTotal, CancellationToken token)
        {
            // Tile-cache streaming: one pass, O(width) memory, supports >2GB (tile = blockSize)
            var peaks = new WaveformPeak[widthPixels];
            var mins = new float[widthPixels];
            var maxs = new float[widthPixels];
            var sumSq = new double[widthPixels];
            var counts = new int[widthPixels];
            for (int i = 0; i < widthPixels; i++) { mins[i] = float.MaxValue; maxs[i] = float.MinValue; }

            // ZoomSamples: collect decimated copy up to 200k via reservoir (every Nth)
            const int zoomCap = 200_000;
            var zoomBuf = new float[zoomCap];
            long zoomWritten = 0;
            long zoomStride = Math.Max(1, estimatedTotal / zoomCap);
            if (zoomStride < 1) zoomStride = 1;

            long sampleIdx = 0;
            const int blockSize = 8192;
            float[] block = new float[blockSize];
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int read = reader.Read(block, 0, block.Length);
                if (read <= 0) break;
                for (int i = 0; i < read; i++, sampleIdx++)
                {
                    float v = block[i];
                    int px = (int)(sampleIdx * widthPixels / Math.Max(1, estimatedTotal));
                    if (px < 0) px = 0; if (px >= widthPixels) px = widthPixels - 1;
                    if (v < mins[px]) mins[px] = v;
                    if (v > maxs[px]) maxs[px] = v;
                    sumSq[px] += v * v;
                    counts[px]++;

                    // Zoom decimation: keep every zoomStride-th
                    if (sampleIdx % zoomStride == 0 && zoomWritten < zoomCap)
                        zoomBuf[zoomWritten++] = v;
                }
            }

            for (int i = 0; i < widthPixels; i++)
            {
                peaks[i] = new WaveformPeak
                {
                    Min = mins[i] == float.MaxValue ? 0 : mins[i],
                    Max = maxs[i] == float.MinValue ? 0 : maxs[i],
                    Rms = counts[i] > 0 ? (float)Math.Sqrt(sumSq[i] / counts[i]) : 0
                };
            }
            float[] zoom = zoomWritten == zoomCap ? zoomBuf : new ArraySegment<float>(zoomBuf, 0, (int)zoomWritten).ToArray();
            double durationMs = srcRate > 0 ? (estimatedTotal / (double)srcRate) * 1000.0 : 0;
            return new WaveformData
            {
                Peaks = peaks,
                SampleRate = srcRate,
                Channels = srcCh,
                DurationMs = durationMs,
                TotalSamples = estimatedTotal,
                ZoomSamples = zoom
            };
        }

        private static long EstimateTotalMonoSamples(IDisposable disp, int srcRate, int srcCh)
        {
            try
            {
                if (disp is VorbisWaveReader vbr && vbr.TotalTime.Ticks > 0)
                    return (long)(vbr.TotalTime.TotalSeconds * srcRate);
                if (disp is AudioFileReader afr)
                {
                    // AudioFileReader.Length is bytes of decoded PCM (after MediaFoundation). Use it if available.
                    if (afr.Length > 0 && afr.WaveFormat.BitsPerSample > 0 && afr.WaveFormat.Channels > 0)
                    {
                        long bytesPerSample = afr.WaveFormat.BitsPerSample / 8;
                        long totalFrames = afr.Length / (bytesPerSample * afr.WaveFormat.Channels);
                        // Mono estimate: frames == mono samples after downmix
                        return totalFrames;
                    }
                    if (afr.TotalTime.Ticks > 0)
                        return (long)(afr.TotalTime.TotalSeconds * srcRate);
                }
                if (disp is MediaFoundationReader mfr && mfr.TotalTime.Ticks > 0)
                    return (long)(mfr.TotalTime.TotalSeconds * srcRate);
            } catch {}
            return 0;
        }

        private static ISampleProvider OpenReader(string path, out IDisposable disposable)
        {
            return KeyslicerAudioReader.OpenSampleProvider(path, out disposable);
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
