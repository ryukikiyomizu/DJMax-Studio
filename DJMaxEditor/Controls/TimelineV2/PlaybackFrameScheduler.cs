namespace DJMaxEditor.Controls.TimelineV2
{
    // Uncapped playback render policy. Playback draws every update the UI can
    // consume; WinForms coalesces rapid Invalidate() calls into a single WM_PAINT,
    // so the former 30 Hz throttle is gone. Retained as a small, testable seam so a
    // future adaptive throttle can be reintroduced without touching call sites.
    internal sealed class PlaybackFrameScheduler
    {
        internal PlaybackFrameScheduler(long frameIntervalMilliseconds)
        {
            // Interval kept for signature compatibility; the policy is currently
            // uncapped, so it is intentionally not used to drop frames.
        }

        internal bool ShouldRenderAt(long elapsedMilliseconds)
        {
            return true;
        }
    }
}
