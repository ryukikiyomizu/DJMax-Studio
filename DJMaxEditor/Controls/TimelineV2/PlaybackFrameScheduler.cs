namespace DJMaxEditor.Controls.TimelineV2
{
    internal sealed class PlaybackFrameScheduler
    {
        private readonly long _frameIntervalMilliseconds;
        private bool _hasRendered;
        private long _lastRenderedAt;

        internal PlaybackFrameScheduler(long frameIntervalMilliseconds)
        {
            _frameIntervalMilliseconds = frameIntervalMilliseconds < 1
                ? 1
                : frameIntervalMilliseconds;
        }

        internal bool ShouldRenderAt(long elapsedMilliseconds)
        {
            if (!_hasRendered ||
                elapsedMilliseconds < _lastRenderedAt ||
                elapsedMilliseconds - _lastRenderedAt >= _frameIntervalMilliseconds)
            {
                _hasRendered = true;
                _lastRenderedAt = elapsedMilliseconds;
                return true;
            }

            return false;
        }
    }
}
