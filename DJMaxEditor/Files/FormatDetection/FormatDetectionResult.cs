using System;

namespace DJMaxEditor.Files.FormatDetection
{
    /// <summary>
    /// Typed result of <see cref="ChartFormatDetector"/>. Carries not just the format but the
    /// evidence used to reach it, so routing decisions are deterministic and auditable and so
    /// diagnostics can explain *why* a file was classified the way it was.
    /// </summary>
    public sealed class FormatDetectionResult
    {
        public ChartFormat Format { get; }

        public DetectionConfidence Confidence { get; }

        /// <summary>True when the file is (positively) identified as encrypted. Never inferred from a generic parse failure.</summary>
        public bool IsEncrypted { get; }

        /// <summary>True when the format must be opened read-only (no safe save-back path exists yet).</summary>
        public bool IsReadOnly { get; }

        /// <summary>Human-readable evidence trail (signature bytes, offsets, structural checks that passed).</summary>
        public string Evidence { get; }

        /// <summary>Why detection failed, when <see cref="Format"/> is Unknown/Malformed. Null otherwise.</summary>
        public string FailureReason { get; }

        /// <summary>A byte offset relevant to the decision or the failure, when applicable.</summary>
        public long? Offset { get; }

        public FormatDetectionResult(
            ChartFormat format,
            DetectionConfidence confidence,
            bool isEncrypted,
            bool isReadOnly,
            string evidence,
            string failureReason = null,
            long? offset = null)
        {
            Format = format;
            Confidence = confidence;
            IsEncrypted = isEncrypted;
            IsReadOnly = isReadOnly;
            Evidence = evidence ?? string.Empty;
            FailureReason = failureReason;
            Offset = offset;
        }

        public bool IsOpenable =>
            Format == ChartFormat.PtffDecrypted ||
            Format == ChartFormat.TrailerRespectV ||
            Format == ChartFormat.CyclonXml ||
            Format == ChartFormat.BmsClassic;

        public override string ToString()
        {
            var at = Offset.HasValue ? $" @0x{Offset.Value:X}" : "";
            var reason = FailureReason != null ? $" reason='{FailureReason}'" : "";
            return $"{Format} (confidence={Confidence}, encrypted={IsEncrypted}, readOnly={IsReadOnly}){at} evidence='{Evidence}'{reason}";
        }
    }
}
