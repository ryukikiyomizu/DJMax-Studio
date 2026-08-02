using DJMaxEditor.DJMax;
using DJMaxEditor.Editor;
using DJMaxEditor.Files.FormatDetection;

namespace DJMaxEditor.Tests
{
    internal static partial class Program
    {
        private static void RunTimelineFoundationTests()
        {
            Test("TimelineFlag_MissingOrInvalid_FallsBackToLegacy", () =>
            {
                AssertTrue(!FeatureFlags.ParseUseTimelineV2(null), "missing setting must select legacy");
                AssertTrue(!FeatureFlags.ParseUseTimelineV2(string.Empty), "empty setting must select legacy");
                AssertTrue(!FeatureFlags.ParseUseTimelineV2("not-a-bool"), "invalid setting must select legacy");
                AssertTrue(!FeatureFlags.ParseUseTimelineV2("false"), "false must select legacy");
            });

            Test("TimelineFlag_True_SelectsV2_WithoutMutatingModel", () =>
            {
                var model = new PlayerData
                {
                    SourceFormat = ChartFormat.PtffDecrypted,
                    IsReadOnly = false,
                    Encrypted = true
                };

                var beforeFormat = model.SourceFormat;
                var beforeReadOnly = model.IsReadOnly;
                var beforeEncrypted = model.Encrypted;

                AssertTrue(FeatureFlags.ParseUseTimelineV2("true"), "true must select Timeline V2");
                AssertTrue(model.SourceFormat == beforeFormat, "surface selection changed source format");
                AssertTrue(model.IsReadOnly == beforeReadOnly, "surface selection changed read-only state");
                AssertTrue(model.Encrypted == beforeEncrypted, "surface selection changed encryption state");
            });

            Test("Capabilities_RespectV_IsEditableForConversion", () =>
            {
                var respect = new PlayerData
                {
                    SourceFormat = ChartFormat.TrailerRespectV,
                    IsReadOnly = false
                };

                var capabilities = DocumentCapabilities.Resolve(respect);
                AssertTrue(capabilities.IsRespectV, "proven Respect V was not identified");
                AssertTrue(capabilities.CanEdit, "Respect V should be editable in memory");
                AssertTrue(capabilities.CanSave, "Respect V should allow safe export");
                AssertTrue(capabilities.CanConvert, "Respect V should be convertible");
                AssertTrue(capabilities.StatusLabel.Contains("RESPECT V"), "Respect V status is not persistent");
                AssertTrue(capabilities.StatusLabel.Contains("EDITABLE"), "Respect V editable state is not visible");
                AssertTrue(capabilities.EditBlockReason.Length == 0, "editable Respect chart has a block reason");
            });

            Test("Capabilities_BareReadOnly_IsGenericLock", () =>
            {
                var generic = new PlayerData
                {
                    SourceFormat = ChartFormat.PtffDecrypted,
                    IsReadOnly = true
                };

                var capabilities = DocumentCapabilities.Resolve(generic);
                AssertTrue(!capabilities.IsRespectV, "bare read-only flag falsely claimed Respect V");
                AssertTrue(!capabilities.CanEdit, "generic read-only document must not be editable");
                AssertTrue(capabilities.StatusLabel == "READ ONLY", "generic lock needs a generic label");
            });

            Test("DocumentContext_PreservesAuthoritativeModelIdentity", () =>
            {
                var model = new PlayerData { SourceFormat = ChartFormat.CyclonXml };
                var context = new EditorDocumentContext(model, "chart.xml");

                AssertTrue(object.ReferenceEquals(model, context.Model), "context copied/replaced PlayerData");
                AssertTrue(context.SourcePath == "chart.xml", "source path was not retained");
                AssertTrue(context.Capabilities.SourceFormat == ChartFormat.CyclonXml,
                    "capabilities lost source format");
            });

            Test("DocumentMutationGuard_BlocksReadOnlyAndViewOnlyPaths", () =>
            {
                string reason;
                var editable = new PlayerData { IsReadOnly = false };
                var respect = new PlayerData
                {
                    SourceFormat = ChartFormat.TrailerRespectV,
                    IsReadOnly = false
                };

                AssertTrue(DocumentMutationGuard.CanMutate(editable, true, out reason),
                    "editable legacy surface was blocked");
                AssertTrue(!DocumentMutationGuard.CanMutate(editable, false, out reason),
                    "view-only surface allowed mutation");
                AssertTrue(reason.Contains("view-only"), "view-only block lacks a reason");
                AssertTrue(DocumentMutationGuard.CanMutate(respect, true, out reason),
                    "editable Respect V model was blocked");
                AssertTrue(!DocumentMutationGuard.CanMutate(null, true, out reason),
                    "missing document allowed mutation");
            });
        }
    }
}
