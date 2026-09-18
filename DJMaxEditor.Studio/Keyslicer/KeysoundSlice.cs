using System;
using System.Text.Json.Serialization;

namespace DJMaxEditor.Studio.Keyslicer
{
    /// <summary>
    /// A non-destructive virtual keysound: a named interval inside a source recording.
    /// The original audio file is never duplicated until export. During editing the
    /// player reads only [StartMs, EndMs) from the source, with gain and fades applied
    /// at render time. Mirrors the JSON shape proposed in the keyslicer spec.
    /// </summary>
    public sealed class KeysoundSlice
    {
        /// <summary>Stable id, e.g. "ks_0042". Used by chart notes.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Path to the source file, as stored in the project (relative when portable).</summary>
        public string SourceFile { get; set; } = string.Empty;

        /// <summary>Absolute resolved path, not serialized.</summary>
        [JsonIgnore]
        public string ResolvedSourcePath { get; set; } = string.Empty;

        /// <summary>Start of the interval in milliseconds from the source start.</summary>
        public double StartMs { get; set; }

        /// <summary>End of the interval in milliseconds.</summary>
        public double EndMs { get; set; }

        /// <summary>Linear gain (1.0 = unity).</summary>
        public double Gain { get; set; } = 1.0;

        /// <summary>Fade in duration in milliseconds to avoid clicks.</summary>
        public double FadeInMs { get; set; } = 2.0;

        /// <summary>Fade out duration in milliseconds.</summary>
        public double FadeOutMs { get; set; } = 5.0;

        /// <summary>Designated lane (0-based). -1 = unassigned / library only.</summary>
        public int Lane { get; set; } = -1;

        /// <summary>Human tag, e.g. "kick", "snare", "piano C4".</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>Whether the slice has been confirmed (promoted from draft).</summary>
        public bool IsConfirmed { get; set; } = true;

        /// <summary>Optional color override, otherwise derived from lane.</summary>
        public string ColorKey { get; set; } = string.Empty;

        /// <summary>When the slice was created (for sort / review order).</summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [JsonIgnore]
        public double DurationMs => Math.Max(0, EndMs - StartMs);

        [JsonIgnore]
        public bool IsValid => !string.IsNullOrEmpty(Id)
                               && !string.IsNullOrEmpty(SourceFile)
                               && EndMs > StartMs
                               && StartMs >= 0;

        public KeysoundSlice Clone()
        {
            return new KeysoundSlice
            {
                Id = Id,
                SourceFile = SourceFile,
                ResolvedSourcePath = ResolvedSourcePath,
                StartMs = StartMs,
                EndMs = EndMs,
                Gain = Gain,
                FadeInMs = FadeInMs,
                FadeOutMs = FadeOutMs,
                Lane = Lane,
                Label = Label,
                IsConfirmed = IsConfirmed,
                ColorKey = ColorKey,
                CreatedAt = CreatedAt
            };
        }

        public override string ToString()
        {
            return string.Format("{0} [{1:F1}-{2:F1} ms] lane {3} \"{4}\"",
                Id, StartMs, EndMs, Lane, Label);
        }
    }

    /// <summary>
    /// A note placed on the chart timeline that references a virtual keysound.
    /// Chart time is independent from source time: a note at 45s in the song may
    /// reference a slice captured at 11m32s in the source recording.
    /// </summary>
    public sealed class KeysoundMappedNote
    {
        /// <summary>Chart time in milliseconds (song timeline, BPM-derived).</summary>
        public double ChartTimeMs { get; set; }

        /// <summary>Converted tick in the shared model (for export).</summary>
        public int VirtualTick { get; set; }

        /// <summary>Target lane index in the chart (0-based).</summary>
        public int Lane { get; set; }

        /// <summary>The slice this note triggers.</summary>
        public string KeysoundId { get; set; } = string.Empty;

        /// <summary>Optional per-note gain (multiplied with slice gain).</summary>
        public double VelocityGain { get; set; } = 1.0;

        /// <summary>For long notes: duration in ms (0 = tap).</summary>
        public double DurationMs { get; set; }
    }
}
