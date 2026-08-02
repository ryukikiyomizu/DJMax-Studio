using System.Windows.Forms;

namespace DJMaxEditor.Editor
{
    public interface IEditorSurface
    {
        Control View { get; }

        bool SupportsEditing { get; }

        void Bind(EditorDocumentContext document);

        void InvalidateView();

        bool TrySetTimeZoom(float zoom);

        EditorViewState CaptureViewState();

        void RestoreViewState(EditorViewState state);

        int PlayheadVirtualTick { get; set; }
    }
}
