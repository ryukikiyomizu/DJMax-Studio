using System;
using System.Collections.Generic;

namespace DJMaxEditor.Studio.Keyslicer
{
    /// <summary>
    /// Lightweight onset/transient detector for the keyslicer.
    /// Woslicer/sayaslicer offer manual cut placement with a few automation helpers:
    /// silence detection, transient peaks, zero-crossing. We provide a simple
    /// amplitude + spectral-flux-free detector that runs on the already-decoded
    /// mono peak data, so no native FFT dependency is required. Results are
    /// suggestions; the user confirms each slice.
    /// </summary>
    public sealed class TransientDetectorSettings
    {
        /// <summary>0.0-1.0, higher = stricter (fewer detections).</summary>
        public double Sensitivity { get; set; } = 0.55;

        /// <summary>Minimum gap between two onsets in ms (debounce).</summary>
        public double MinGapMs { get; set; } = 80;

        /// <summary>Silence threshold as linear amplitude (0-1).</summary>
        public double SilenceThreshold { get; set; } = 0.02;

        /// <summary>Pre-roll before the detected transient (ms) to keep the attack.</summary>
        public double PreRollMs { get; set; } = 6;

        /// <summary>Maximum slice duration (ms) before auto-split.</summary>
        public double MaxDurationMs { get; set; } = 4000;

        /// <summary>Tail preservation (ms) after the next onset or silence.</summary>
        public double TailMs { get; set; } = 30;

        /// <summary>Minimum slice duration to keep (ms).</summary>
        public double MinDurationMs { get; set; } = 30;
    }

    public sealed class SuggestedSlice
    {
        public double StartMs { get; set; }
        public double EndMs { get; set; }
        public double Confidence { get; set; }
        public double PeakAmplitude { get; set; }
    }

    public static class TransientDetector
    {
        public static List<SuggestedSlice> Detect(WaveformData data, TransientDetectorSettings settings)
        {
            if (data == null || data.Peaks == null || data.Peaks.Length < 16 || data.DurationMs <= 0)
                return new List<SuggestedSlice>();
            if (settings == null) settings = new TransientDetectorSettings();

            double msPerPixel = data.DurationMs / data.Peaks.Length;
            double sensitivity = Math.Max(0.0, Math.Min(1.0, settings.Sensitivity));
            // Map sensitivity to a threshold factor: low sensitivity -> high threshold.
            double threshold = 0.03 + sensitivity * 0.18; // approx 0.03..0.21
            double silence = Math.Max(0.001, settings.SilenceThreshold);

            var onsets = new List<int>();

            // Compute short-time RMS envelope + peak amplitude per pixel.
            // Onset when: amplitude rises sharply, exceeds threshold, and is a local max.
            for (int i = 2; i < data.Peaks.Length - 2; i++)
            {
                var p = data.Peaks[i];
                float amp = Math.Max(Math.Abs(p.Max), Math.Abs(p.Min));
                float prevAmp = Math.Max(Math.Abs(data.Peaks[i - 1].Max), Math.Abs(data.Peaks[i - 1].Min));
                float nextAmp = Math.Max(Math.Abs(data.Peaks[i + 1].Max), Math.Abs(data.Peaks[i + 1].Min));
                float rms = p.Rms;

                // Quick reject silence.
                if (amp < silence && rms < silence * 0.7f) continue;
                if (amp < threshold) continue;

                // Local max of amplitude (simple 3-pt).
                bool localMax = amp >= prevAmp && amp >= nextAmp;
                if (!localMax) continue;

                // Flux: how much larger than recent background.
                double bg = 0;
                int bgWindow = Math.Min(8, i);
                for (int k = i - bgWindow; k < i; k++)
                {
                    float a = Math.Max(Math.Abs(data.Peaks[k].Max), Math.Abs(data.Peaks[k].Min));
                    bg += a;
                }
                bg = bgWindow > 0 ? bg / bgWindow : 0;
                double flux = amp - bg;
                double need = threshold * 0.6; // flux must clear ~60% of threshold
                if (flux < need) continue;

                // Debounce against last onset.
                if (onsets.Count > 0)
                {
                    int last = onsets[onsets.Count - 1];
                    double gapMs = (i - last) * msPerPixel;
                    if (gapMs < settings.MinGapMs) continue;
                }

                onsets.Add(i);
            }

            if (onsets.Count == 0) return new List<SuggestedSlice>();

            var slices = new List<SuggestedSlice>(onsets.Count);
            for (int idx = 0; idx < onsets.Count; idx++)
            {
                int px = onsets[idx];
                double onsetMs = px * msPerPixel;
                double startMs = Math.Max(0, onsetMs - settings.PreRollMs);

                double nextOnsetMs = idx + 1 < onsets.Count
                    ? onsets[idx + 1] * msPerPixel - settings.PreRollMs
                    : data.DurationMs;

                // Find next silence after onset to decide end.
                double silenceStartMs = startMs;
                // Search forward for a run of quiet pixels.
                int quietRun = 0;
                int quietNeeded = Math.Max(1, (int)Math.Ceiling(30.0 / msPerPixel)); // ~30ms silence = slice end
                int scanEndPx = idx + 1 < onsets.Count ? onsets[idx + 1] : data.Peaks.Length - 1;
                int silencePx = -1;
                for (int p = px + 1; p < scanEndPx; p++)
                {
                    var pk = data.Peaks[p];
                    float a = Math.Max(Math.Abs(pk.Max), Math.Abs(pk.Min));
                    if (a < silence) quietRun++; else quietRun = 0;
                    if (quietRun >= quietNeeded) { silencePx = p - quietNeeded + 1; break; }
                }

                double endMs;
                if (silencePx >= 0)
                    endMs = silencePx * msPerPixel - settings.TailMs;
                else
                    endMs = nextOnsetMs - settings.TailMs;

                // Clamp to max duration and also ensure not reaching into next onset's pre-roll too closely.
                if (endMs > startMs + settings.MaxDurationMs)
                    endMs = startMs + settings.MaxDurationMs;
                if (idx + 1 < onsets.Count) endMs = Math.Min(endMs, nextOnsetMs);
                endMs = Math.Min(endMs, data.DurationMs);
                endMs = Math.Max(endMs, startMs + settings.MinDurationMs);

                // Confidence based on flux and amplitude.
                float amp0 = Math.Max(Math.Abs(data.Peaks[px].Max), Math.Abs(data.Peaks[px].Min));
                // background for confidence
                double bg2 = 0;
                int c = 0;
                for (int k = Math.Max(0, px - 8); k < px; k++)
                {
                    bg2 += Math.Max(Math.Abs(data.Peaks[k].Max), Math.Abs(data.Peaks[k].Min));
                    c++;
                }
                bg2 = c > 0 ? bg2 / c : 0;
                double flux2 = Math.Max(0, amp0 - bg2);
                double conf = Math.Min(1.0, 0.45 + flux2 * 2.2 + amp0 * 0.6);
                conf = Math.Max(0.05, Math.Min(0.99, conf));

                slices.Add(new SuggestedSlice
                {
                    StartMs = startMs,
                    EndMs = endMs,
                    Confidence = conf,
                    PeakAmplitude = amp0
                });
            }

            // Filter tiny slices.
            slices.RemoveAll(s => (s.EndMs - s.StartMs) < settings.MinDurationMs);
            return slices;
        }
    }
}
