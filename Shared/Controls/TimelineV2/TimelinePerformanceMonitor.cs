namespace DJMaxEditor.Controls.TimelineV2
{
    public sealed class TimelinePerformanceMonitor
    {
        public double LastIndexBuildMilliseconds { get; internal set; }
        public double LastQueryMilliseconds { get; internal set; }
        public double LastFrameMilliseconds { get; internal set; }
        public int IndexedItemCount { get; internal set; }
        public int LastVisibleItemCount { get; internal set; }
        public int LastQueryCandidateCount { get; internal set; }
        public int FullRebuildCount { get; internal set; }
    }
}
