using DJMaxEditor.Editor;

namespace DJMaxEditor.Controls.TimelineV2.Projection
{
    public interface ITimelineProjection
    {
        string Id { get; }

        bool IsVerifiedFor(EditorDocumentContext document);

        TimelineProjectionResult Build(EditorDocumentContext document);
    }
}
