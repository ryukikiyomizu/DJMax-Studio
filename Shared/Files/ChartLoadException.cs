using System;

namespace DJMaxEditor.Files
{
    /// <summary>Category of a chart-loading failure, used to drive actionable, typed diagnostics.</summary>
    public enum ChartLoadError
    {
        UnsupportedFormat,
        MalformedHeader,
        InvalidTrailerOffset,
        InvalidCount,
        TruncatedInstrumentTable,
        TruncatedTrack,
        UnsupportedOpcode,
        MissingAudio,
        PartiallyResolvedAudio,
        EncryptedWithoutCodec,
        Unexpected
    }

    /// <summary>
    /// Raised when a chart file cannot be loaded for a *known, categorised* reason. Callers at the
    /// application boundary translate <see cref="Kind"/> into a specific user-facing message instead of
    /// swallowing the failure or showing a single generic error. Offsets are included where meaningful.
    /// </summary>
    [Serializable]
    public sealed class ChartLoadException : Exception
    {
        public ChartLoadError Kind { get; }

        /// <summary>Byte offset the failure relates to, when applicable.</summary>
        public long? Offset { get; }

        public ChartLoadException(ChartLoadError kind, string message, long? offset = null, Exception inner = null)
            : base(message, inner)
        {
            Kind = kind;
            Offset = offset;
        }

        public override string ToString()
        {
            var at = Offset.HasValue ? $" (offset 0x{Offset.Value:X})" : "";
            return $"{Kind}{at}: {Message}";
        }
    }
}
