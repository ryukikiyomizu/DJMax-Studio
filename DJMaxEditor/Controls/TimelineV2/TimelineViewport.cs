using System;

namespace DJMaxEditor.Controls.TimelineV2
{
    /// <summary>
    /// Owns horizontal timeline origin and zoom. It retains sub-pixel state so panning does not
    /// accumulate integer rounding error.
    /// </summary>
    public sealed class TimelineViewport
    {
        private double _originTick;
        private double _pixelsPerTick = 0.25;
        private int _viewportWidth;
        private int _headerWidth;

        public TimelineViewport(
            double documentStartTick,
            double documentEndTick,
            int viewportWidth,
            int headerWidth)
        {
            DocumentStartTick = Math.Min(documentStartTick, documentEndTick);
            DocumentEndTick = Math.Max(documentStartTick, documentEndTick);
            _viewportWidth = Math.Max(1, viewportWidth);
            _headerWidth = Math.Max(0, headerWidth);
            _originTick = DocumentStartTick;
            ClampOrigin();
        }

        public double MinPixelsPerTick { get; set; } = 0.01;

        public double MaxPixelsPerTick { get; set; } = 16.0;

        public double DocumentStartTick { get; private set; }

        public double DocumentEndTick { get; private set; }

        public int ViewportWidth
        {
            get { return _viewportWidth; }
            set
            {
                _viewportWidth = Math.Max(1, value);
                ClampOrigin();
            }
        }

        public int HeaderWidth
        {
            get { return _headerWidth; }
            set
            {
                _headerWidth = Math.Max(0, value);
                ClampOrigin();
            }
        }

        public double PixelsPerTick
        {
            get { return _pixelsPerTick; }
            set
            {
                _pixelsPerTick = Math.Max(MinPixelsPerTick, Math.Min(MaxPixelsPerTick, value));
                ClampOrigin();
            }
        }

        public double OriginTick
        {
            get { return _originTick; }
            set
            {
                _originTick = value;
                ClampOrigin();
            }
        }

        public double VisibleTickCount
        {
            get { return ContentWidth / PixelsPerTick; }
        }

        public TimelineTimeRange VisibleTimeRange
        {
            get
            {
                return new TimelineTimeRange(
                    OriginTick,
                    Math.Min(DocumentEndTick, OriginTick + VisibleTickCount));
            }
        }

        public double TickAtScreenX(double screenX)
        {
            return OriginTick + ((screenX - HeaderWidth) / PixelsPerTick);
        }

        public double ScreenXAtTick(double tick)
        {
            return HeaderWidth + ((tick - OriginTick) * PixelsPerTick);
        }

        public void ZoomAt(double cursorScreenX, double factor)
        {
            if (factor <= 0) throw new ArgumentOutOfRangeException("factor");

            double anchorTick = TickAtScreenX(cursorScreenX);
            PixelsPerTick = PixelsPerTick * factor;
            OriginTick = anchorTick - ((cursorScreenX - HeaderWidth) / PixelsPerTick);
        }

        public void PanByPixels(double deltaPixels)
        {
            OriginTick = OriginTick - (deltaPixels / PixelsPerTick);
        }

        public void ScrollByPixels(double deltaPixels)
        {
            OriginTick = OriginTick + (deltaPixels / PixelsPerTick);
        }

        public void FitDocument()
        {
            double length = Math.Max(1, DocumentEndTick - DocumentStartTick);
            PixelsPerTick = ContentWidth / length;
            OriginTick = DocumentStartTick;
        }

        private int ContentWidth
        {
            get { return Math.Max(1, ViewportWidth - HeaderWidth); }
        }

        private void ClampOrigin()
        {
            double maxOrigin = Math.Max(DocumentStartTick, DocumentEndTick - VisibleTickCount);
            _originTick = Math.Max(DocumentStartTick, Math.Min(maxOrigin, _originTick));
        }
    }
}
