namespace DJMaxEditor.Editor
{
    public sealed class EditorViewState
    {
        public double PixelsPerTick { get; set; }

        public double OriginTick { get; set; }

        public int FirstVisibleRow { get; set; }

        public int PlayheadVirtualTick { get; set; }
    }
}
