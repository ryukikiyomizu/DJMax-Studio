using DJMaxEditor.DJMax;
using DJMaxEditor.Files.FormatDetection;

namespace DJMaxEditor.Editor
{
    /// <summary>
    /// Immutable view of what the current document may safely do. This supplements, and never
    /// replaces, the existing save-time read-only guard.
    /// </summary>
    public sealed class DocumentCapabilities
    {
        private DocumentCapabilities()
        {
        }

        public ChartFormat? SourceFormat { get; private set; }

        public bool IsEncrypted { get; private set; }

        public bool IsReadOnly { get; private set; }

        public bool IsRespectV { get; private set; }

        public bool CanEdit { get; private set; }

        public bool CanSave { get; private set; }

        public bool CanConvert { get; private set; }

        public string StatusLabel { get; private set; }

        public string EditBlockReason { get; private set; }

        public static DocumentCapabilities Resolve(PlayerData model)
        {
            if (model == null)
            {
                return new DocumentCapabilities
                {
                    StatusLabel = "NO DOCUMENT",
                    EditBlockReason = "Open a chart before editing."
                };
            }

            bool isRespectV = model.SourceFormat == ChartFormat.TrailerRespectV;
            bool canEdit = !model.IsReadOnly;

            return new DocumentCapabilities
            {
                SourceFormat = model.SourceFormat,
                IsEncrypted = model.Encrypted,
                IsReadOnly = model.IsReadOnly,
                IsRespectV = isRespectV,
                CanEdit = canEdit,
                CanSave = canEdit,
                CanConvert = model.SourceFormat.HasValue,
                StatusLabel = isRespectV
                    ? (canEdit ? "RESPECT V - EDITABLE (BMS EXPORT)" : "RESPECT V - READ ONLY")
                    : (model.IsReadOnly ? "READ ONLY" : "EDITABLE"),
                EditBlockReason = canEdit ? string.Empty :
                    (isRespectV
                        ? "Respect V charts are read-only because lossless round-trip compatibility is not verified."
                        : "This chart is read-only and cannot be modified.")
            };
        }
    }
}
