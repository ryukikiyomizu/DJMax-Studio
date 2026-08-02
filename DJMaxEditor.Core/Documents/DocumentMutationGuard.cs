using DJMaxEditor.DJMax;

namespace DJMaxEditor.Editor
{
    /// <summary>
    /// Shared final gate for every chart mutation path introduced or touched by Timeline V2.
    /// Views may add stricter restrictions, but must never bypass the document read-only state.
    /// </summary>
    public static class DocumentMutationGuard
    {
        public static bool CanMutate(
            PlayerData model,
            bool surfaceSupportsEditing,
            out string reason)
        {
            if (model == null)
            {
                reason = "Open a chart before editing.";
                return false;
            }

            if (model.IsReadOnly)
            {
                reason = DocumentCapabilities.Resolve(model).EditBlockReason;
                return false;
            }

            if (!surfaceSupportsEditing)
            {
                reason = "The active editor surface is view-only.";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
