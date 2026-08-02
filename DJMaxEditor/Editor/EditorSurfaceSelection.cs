namespace DJMaxEditor.Editor
{
    public enum EditorSurfaceKind
    {
        Legacy,
        TimelineV2
    }

    public static class EditorSurfaceSelection
    {
        public static EditorSurfaceKind Resolve(bool useTimelineV2)
        {
            return useTimelineV2 ? EditorSurfaceKind.TimelineV2 : EditorSurfaceKind.Legacy;
        }
    }
}
