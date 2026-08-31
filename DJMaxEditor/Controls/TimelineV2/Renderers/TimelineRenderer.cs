using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using DJMaxEditor.Controls.Editor;

namespace DJMaxEditor.Controls.TimelineV2.Renderers
{
    /// <summary>
    /// Composites one Timeline V2 frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two caches, split by what makes them stale. The <em>band</em> holds everything positioned in
    /// ticks — row fills, grid, quantize lines, zones, notes, ruler marks — rendered over an area
    /// wider than the viewport by <see cref="TimelineFrame.ScrollBandMargin"/> on each side. The
    /// <em>chrome</em> holds everything positioned in screen pixels: the track-name column, the
    /// ruler corner above it, and the minimap density bars.
    /// </para>
    /// <para>
    /// Scrolling then costs two blits instead of a re-render. That is the whole point: the cache was
    /// previously keyed on the viewport origin, so continuous follow-scroll re-rendered every
    /// visible note and label 60 times a second (13-18ms a frame against 2.5ms for a cache hit),
    /// which is why follow shipped as a paging band first. Widening the cached area and moving the
    /// read window instead costs roughly one rebuild per <see cref="TimelineFrame.ScrollBandMargin"/>
    /// pixels of travel.
    /// </para>
    /// <para>
    /// A frame with <see cref="TimelineFrame.ScrollBandMargin"/> of zero — which is every frame a
    /// test builds by hand — falls back to caching exactly one screen, origin included in the key.
    /// </para>
    /// </remarks>
    public sealed class TimelineRenderer : IDisposable
    {
        /// <summary>
        /// Lower bound on the scroll band margin. Below roughly this much lookahead the rebuilds
        /// come often enough that the blit stops paying for the extra pixels rendered.
        /// </summary>
        public const int MinScrollBandMarginPixels = ScrollBand.MinMarginPixels;

        /// <summary>
        /// Upper bound on the scroll band margin. The band is a bitmap and a render pass, so both
        /// its memory and its rebuild cost grow with it; past half a screen either side the extra
        /// lookahead buys very little.
        /// </summary>
        public const int MaxScrollBandMarginPixels = ScrollBand.MaxMarginPixels;

        private readonly GridRenderer _grid = new GridRenderer();
        private readonly RulerRenderer _ruler = new RulerRenderer();
        private readonly ItemRenderer _items = new ItemRenderer();
        private readonly PlayheadRenderer _playhead = new PlayheadRenderer();
        private readonly MinimapRenderer _minimap = new MinimapRenderer();

        // Chrome resources. A static-frame rebuild happens on every scroll and zoom frame,
        // so anything allocated inside RenderStatic is effectively allocated per frame.
        // Fonts are the worst offenders: constructing one creates a native font handle and
        // the finalizer thread has to reclaim it.
        private readonly SolidBrush _headerBackground =
            new SolidBrush(TimelineRenderTheme.Header);
        private readonly Pen _headerBorder = new Pen(TimelineRenderTheme.HeaderBorder);
        private readonly SolidBrush _headerText = new SolidBrush(TimelineRenderTheme.Text);
        private readonly Font _headerFont = UI.StudioDesignSystem.BodyFont(8f);
        private readonly SolidBrush _rulerBackground =
            new SolidBrush(TimelineRenderTheme.Ruler);
        private readonly SolidBrush _lockBackground =
            new SolidBrush(Color.FromArgb(235, TimelineRenderTheme.ReadOnly));
        private readonly SolidBrush _lockText = new SolidBrush(UI.StudioDesignSystem.Frost);
        private readonly Font _lockFont =
            UI.StudioDesignSystem.BodyFont(8f, FontStyle.Bold);
        private readonly SolidBrush _marqueeFill =
            new SolidBrush(TimelineRenderTheme.MarqueeFill);
        private readonly Pen _marqueeBorder = new Pen(TimelineRenderTheme.MarqueeBorder, 1f);
        private readonly GraphicsWrapper _wrapper = new GraphicsWrapper();

        private Bitmap _staticFrame;
        private Bitmap _chromeFrame;
        private StaticFrameKey _staticFrameKey;
        private bool _hasStaticFrame;

        public int StaticFrameRebuildCount { get; private set; }

        /// <summary>
        /// How wide a scroll band to cache either side of the viewport, for a given width of chart
        /// area. Half a screen is the balance point: the band is ~2x the viewport, so a rebuild
        /// costs about two frames, and it buys half a screen of scrolling before the next one.
        /// </summary>
        /// <remarks>
        /// The owning control calls this too, because it has to widen its interval-index query to
        /// match — an item list that only covers the viewport would leave the band's margins empty
        /// and notes would pop in at the edges as it scrolled.
        /// </remarks>
        public static int ScrollBandMarginPixels(int contentWidth)
        {
            return ScrollBand.MarginPixels(contentWidth);
        }

        public void Render(Graphics graphics, TimelineFrame frame)
        {
            if (graphics == null) throw new ArgumentNullException("graphics");
            if (frame == null) throw new ArgumentNullException("frame");

            graphics.SmoothingMode = SmoothingMode.None;
            if (frame.ScrollBandMargin > 0)
            {
                RenderBanded(graphics, frame);
            }
            else
            {
                StaticFrameKey key = StaticFrameKey.Create(frame);
                if (!_hasStaticFrame || !_staticFrameKey.Equals(key))
                {
                    RebuildWholeScreen(frame, key);
                }
                graphics.DrawImageUnscaled(_staticFrame, 0, 0);
            }

            _playhead.Render(graphics, frame);
            RenderMarquee(graphics, frame);
        }

        /// <summary>
        /// The rubber-band selection band. Drawn as an overlay on top of the cached static
        /// frame, not into it: it changes on every mouse-move, and baking it in would rebuild
        /// every visible note for each pixel the pointer travels.
        /// </summary>
        private void RenderMarquee(Graphics graphics, TimelineFrame frame)
        {
            if (!frame.Marquee.HasValue) return;

            Rectangle band = frame.Marquee.Value;
            if (band.Width <= 0 || band.Height <= 0) return;

            graphics.FillRectangle(_marqueeFill, band);
            graphics.DrawRectangle(
                _marqueeBorder, band.X, band.Y, band.Width - 1, band.Height - 1);
        }

        public void Dispose()
        {
            if (_staticFrame != null)
            {
                _staticFrame.Dispose();
                _staticFrame = null;
            }
            if (_chromeFrame != null)
            {
                _chromeFrame.Dispose();
                _chromeFrame = null;
            }
            _hasStaticFrame = false;

            _grid.Dispose();
            _ruler.Dispose();
            _items.Dispose();
            _playhead.Dispose();
            _minimap.Dispose();

            _headerBackground.Dispose();
            _headerBorder.Dispose();
            _headerText.Dispose();
            _headerFont.Dispose();
            _rulerBackground.Dispose();
            _lockBackground.Dispose();
            _lockText.Dispose();
            _lockFont.Dispose();
            _marqueeFill.Dispose();
            _marqueeBorder.Dispose();
        }

        private void RenderBanded(Graphics graphics, TimelineFrame frame)
        {
            int margin = frame.ScrollBandMargin;
            int bandWidth = frame.Width + (margin * 2);
            StaticFrameKey key = StaticFrameKey.Create(frame);
            if (!_hasStaticFrame ||
                !_staticFrameKey.Equals(key) ||
                _staticFrame == null ||
                _staticFrame.Width != bandWidth ||
                _staticFrame.Height != frame.Height)
            {
                RebuildBand(frame, key, bandWidth);
            }

            int headerWidth = Math.Min(frame.Width, frame.Coordinates.HeaderWidth);
            int canvasBottom = Math.Min(frame.Height, frame.CanvasBottom);
            int chartWidth = Math.Max(1, frame.Width - headerWidth);

            // Where in the band the viewport currently sits. Clamped rather than trusted: the
            // control keeps the band centred, but a zoom applied between the rebuild and this blit
            // would otherwise read outside the bitmap.
            int offset = (int)Math.Round(
                (frame.Viewport.OriginTick - frame.BandOriginTick) *
                frame.Viewport.PixelsPerTick);
            offset = Math.Max(0, Math.Min(bandWidth - frame.Width, offset));

            graphics.DrawImage(
                _staticFrame,
                new Rectangle(headerWidth, 0, chartWidth, canvasBottom),
                headerWidth + offset,
                0,
                chartWidth,
                canvasBottom,
                GraphicsUnit.Pixel);

            if (headerWidth > 0)
            {
                graphics.DrawImage(
                    _chromeFrame,
                    new Rectangle(0, 0, headerWidth, canvasBottom),
                    0,
                    0,
                    headerWidth,
                    canvasBottom,
                    GraphicsUnit.Pixel);
            }

            int minimapHeight = Math.Max(0, frame.Height - canvasBottom);
            if (minimapHeight > 0)
            {
                graphics.DrawImage(
                    _chromeFrame,
                    new Rectangle(0, canvasBottom, frame.Width, minimapHeight),
                    0,
                    canvasBottom,
                    frame.Width,
                    minimapHeight,
                    GraphicsUnit.Pixel);
                // The density bars are cached, but the "you are here" box tracks the origin, so it
                // is the one part of the minimap that has to be drawn every frame.
                _minimap.RenderViewportIndicator(graphics, frame);
            }

            RenderReadOnlyState(graphics, frame);
        }

        private void RebuildBand(TimelineFrame frame, StaticFrameKey key, int bandWidth)
        {
            EnsureBitmap(ref _staticFrame, bandWidth, frame.Height);
            EnsureBitmap(ref _chromeFrame, frame.Width, frame.Height);

            TimelineFrame bandFrame = CreateBandFrame(frame, bandWidth);
            using (Graphics graphics = Graphics.FromImage(_staticFrame))
            {
                graphics.SmoothingMode = SmoothingMode.None;
                graphics.Clear(TimelineRenderTheme.Canvas);
                var marks = TimelineRulerCalculator.Build(
                    bandFrame.Viewport.VisibleTimeRange,
                    bandFrame.TicksPerMeasure,
                    bandFrame.BeatsPerMeasure,
                    bandFrame.Viewport.PixelsPerTick);
                _grid.Render(graphics, bandFrame, marks);
                RenderZones(graphics, bandFrame);
                _ruler.Render(graphics, bandFrame, marks);
                _items.Render(graphics, bandFrame);
            }

            using (Graphics graphics = Graphics.FromImage(_chromeFrame))
            {
                graphics.SmoothingMode = SmoothingMode.None;
                graphics.Clear(Color.Transparent);
                RenderChrome(graphics, frame);
            }

            _staticFrameKey = key;
            _hasStaticFrame = true;
            StaticFrameRebuildCount++;
        }

        /// <summary>
        /// Re-frames the caller's frame into band space: the same rows, items, coordinates and
        /// theme, seen through a viewport that starts at <see cref="TimelineFrame.BandOriginTick"/>
        /// and is <paramref name="bandWidth"/> pixels wide. Band pixel <c>x</c> therefore maps to
        /// screen pixel <c>x - offset</c>, which is what makes the blit a pure translation.
        /// </summary>
        private static TimelineFrame CreateBandFrame(TimelineFrame frame, int bandWidth)
        {
            TimelineViewport source = frame.Viewport;
            double pixelsPerTick = Math.Max(1e-9, source.PixelsPerTick);
            double bandSpan = bandWidth / pixelsPerTick;

            // The document range is widened past the band on both sides purely to defeat
            // TimelineViewport's origin clamp: the band deliberately starts before the document to
            // leave room to scroll backwards, and a clamped origin would silently shift everything
            // it draws relative to the offset the blit uses.
            var viewport = new TimelineViewport(
                Math.Min(source.DocumentStartTick, frame.BandOriginTick) - bandSpan,
                Math.Max(source.DocumentEndTick, frame.BandOriginTick + bandSpan) + bandSpan,
                bandWidth,
                frame.Coordinates.HeaderWidth);
            viewport.MinPixelsPerTick = source.MinPixelsPerTick;
            viewport.MaxPixelsPerTick = source.MaxPixelsPerTick;
            viewport.PixelsPerTick = source.PixelsPerTick;
            viewport.OriginTick = frame.BandOriginTick;

            return new TimelineFrame(
                bandWidth,
                frame.Height,
                frame.Coordinates,
                viewport,
                frame.Rows,
                frame.VisibleItems,
                frame.FirstVisibleRow,
                frame.PlayheadTick,
                frame.IsReadOnly,
                frame.LockLabel,
                frame.StatusText,
                frame.TicksPerMeasure,
                frame.BeatsPerMeasure,
                frame.MinimapDensity,
                frame.QuantizeDivision,
                frame.EventTheme,
                frame.ZoneTheme,
                frame.EventDisplayMode,
                frame.Selection);
        }

        private static void EnsureBitmap(ref Bitmap bitmap, int width, int height)
        {
            if (bitmap != null && bitmap.Width == width && bitmap.Height == height)
            {
                return;
            }
            if (bitmap != null)
            {
                bitmap.Dispose();
            }
            bitmap = new Bitmap(
                Math.Max(1, width), Math.Max(1, height), PixelFormat.Format32bppPArgb);
        }

        private void RebuildWholeScreen(
            TimelineFrame frame,
            StaticFrameKey key)
        {
            EnsureBitmap(ref _staticFrame, frame.Width, frame.Height);

            using (Graphics graphics = Graphics.FromImage(_staticFrame))
            {
                RenderStatic(graphics, frame);
            }
            _staticFrameKey = key;
            _hasStaticFrame = true;
            StaticFrameRebuildCount++;
        }

        private void RenderStatic(Graphics graphics, TimelineFrame frame)
        {
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.Clear(TimelineRenderTheme.Canvas);

            var marks = TimelineRulerCalculator.Build(
                frame.Viewport.VisibleTimeRange,
                frame.TicksPerMeasure,
                frame.BeatsPerMeasure,
                frame.Viewport.PixelsPerTick);

            _grid.Render(graphics, frame, marks);
            RenderZones(graphics, frame);
            RenderHeaders(graphics, frame);
            _ruler.Render(graphics, frame, marks);
            _items.Render(graphics, frame);
            RenderReadOnlyState(graphics, frame);
            _minimap.Render(graphics, frame);
        }

        /// <summary>
        /// Everything whose position is fixed in screen pixels rather than ticks, so it can be
        /// cached once and blitted back over a band that has scrolled underneath it.
        /// </summary>
        private void RenderChrome(Graphics graphics, TimelineFrame frame)
        {
            // The ruler fill has to reach over the header column too, otherwise the corner above
            // the track names shows whatever the band happened to have scrolled into it.
            graphics.FillRectangle(
                _rulerBackground,
                0,
                0,
                Math.Max(1, frame.Coordinates.HeaderWidth),
                frame.Coordinates.RulerHeight);
            graphics.DrawLine(
                _headerBorder,
                0,
                frame.Coordinates.RulerHeight - 1,
                frame.Coordinates.HeaderWidth,
                frame.Coordinates.RulerHeight - 1);

            RenderHeaders(graphics, frame);
            _minimap.RenderDensity(graphics, frame);
        }

        private void RenderZones(Graphics graphics, TimelineFrame frame)
        {
            if (frame.ZoneTheme == null) return;

            GraphicsState state = graphics.Save();
            try
            {
                var bounds = new Rectangle(
                    frame.Coordinates.HeaderWidth,
                    frame.Coordinates.RulerHeight,
                    Math.Max(1, frame.Width - frame.Coordinates.HeaderWidth),
                    Math.Max(1, frame.CanvasBottom - frame.Coordinates.RulerHeight));
                graphics.SetClip(bounds);
                GraphicsWrapper wrapper = _wrapper;
                wrapper.UpdateGraphics(graphics);

                for (int rowIndex = frame.FirstVisibleRow;
                    rowIndex < frame.Rows.Count;
                    rowIndex++)
                {
                    int y = frame.Coordinates.RowToY(rowIndex, frame.FirstVisibleRow);
                    if (y >= frame.CanvasBottom) break;
                    frame.ZoneTheme.DrawZones(
                        wrapper,
                        rowIndex,
                        frame.Coordinates.HeaderWidth,
                        y,
                        frame.Width - frame.Coordinates.HeaderWidth,
                        frame.Coordinates.RowHeight,
                        bounds);
                }
            }
            finally
            {
                graphics.Restore(state);
            }
        }

        private void RenderHeaders(Graphics graphics, TimelineFrame frame)
        {
            graphics.FillRectangle(
                _headerBackground,
                0,
                frame.Coordinates.RulerHeight,
                frame.Coordinates.HeaderWidth,
                frame.CanvasBottom - frame.Coordinates.RulerHeight);
            graphics.DrawLine(
                _headerBorder,
                frame.Coordinates.HeaderWidth - 1,
                0,
                frame.Coordinates.HeaderWidth - 1,
                frame.CanvasBottom);

            for (int rowIndex = frame.FirstVisibleRow; rowIndex < frame.Rows.Count; rowIndex++)
            {
                int y = frame.Coordinates.RowToY(rowIndex, frame.FirstVisibleRow);
                if (y >= frame.CanvasBottom) break;
                graphics.DrawString(frame.Rows[rowIndex].Name, _headerFont, _headerText, 8, y + 7);
                graphics.DrawLine(_headerBorder, 0, y + frame.Coordinates.RowHeight - 1,
                    frame.Coordinates.HeaderWidth, y + frame.Coordinates.RowHeight - 1);
            }
        }

        private void RenderReadOnlyState(Graphics graphics, TimelineFrame frame)
        {
            if (!frame.IsReadOnly) return;

            SizeF size = graphics.MeasureString(frame.LockLabel, _lockFont);
            var rectangle = new RectangleF(
                frame.Width - size.Width - 22,
                6,
                size.Width + 14,
                size.Height + 4);
            graphics.FillRectangle(_lockBackground, rectangle);
            graphics.DrawString(
                frame.LockLabel, _lockFont, _lockText, rectangle.X + 7, rectangle.Y + 2);
        }

        private struct StaticFrameKey : IEquatable<StaticFrameKey>
        {
            private int _width;
            private int _height;
            private int _firstVisibleRow;
            private int _rowHeight;
            private int _headerWidth;
            private int _rulerHeight;
            private long _originTickBits;
            private long _pixelsPerTickBits;
            private int _scrollBandMargin;
            private int _ticksPerMeasure;
            private int _beatsPerMeasure;
            private int _quantizeDivision;
            private int _eventThemeId;
            private int _zoneThemeId;
            private int _eventDisplayMode;
            private int _isReadOnly;
            private int _contentHash;

            internal static StaticFrameKey Create(TimelineFrame frame)
            {
                // Banded frames key on the band origin, not the viewport origin. That is the whole
                // trick: the viewport origin moves every playback frame, the band origin only moves
                // when the viewport reaches the edge of what was cached.
                double origin = frame.ScrollBandMargin > 0
                    ? frame.BandOriginTick
                    : frame.Viewport.OriginTick;

                return new StaticFrameKey
                {
                    _width = frame.Width,
                    _height = frame.Height,
                    _firstVisibleRow = frame.FirstVisibleRow,
                    _rowHeight = frame.Coordinates.RowHeight,
                    _headerWidth = frame.Coordinates.HeaderWidth,
                    _rulerHeight = frame.Coordinates.RulerHeight,
                    _originTickBits = BitConverter.DoubleToInt64Bits(origin),
                    _pixelsPerTickBits =
                        BitConverter.DoubleToInt64Bits(frame.Viewport.PixelsPerTick),
                    _scrollBandMargin = frame.ScrollBandMargin,
                    _ticksPerMeasure = frame.TicksPerMeasure,
                    _beatsPerMeasure = frame.BeatsPerMeasure,
                    _quantizeDivision = frame.QuantizeDivision,
                    _eventThemeId = ReferenceId(frame.EventTheme),
                    _zoneThemeId = ReferenceId(frame.ZoneTheme),
                    _eventDisplayMode = (int)frame.EventDisplayMode,
                    _isReadOnly = frame.IsReadOnly ? 1 : 0,
                    _contentHash = ComputeContentHash(frame)
                };
            }

            public bool Equals(StaticFrameKey other)
            {
                return _width == other._width &&
                    _height == other._height &&
                    _firstVisibleRow == other._firstVisibleRow &&
                    _rowHeight == other._rowHeight &&
                    _headerWidth == other._headerWidth &&
                    _rulerHeight == other._rulerHeight &&
                    _originTickBits == other._originTickBits &&
                    _pixelsPerTickBits == other._pixelsPerTickBits &&
                    _scrollBandMargin == other._scrollBandMargin &&
                    _ticksPerMeasure == other._ticksPerMeasure &&
                    _beatsPerMeasure == other._beatsPerMeasure &&
                    _quantizeDivision == other._quantizeDivision &&
                    _eventThemeId == other._eventThemeId &&
                    _zoneThemeId == other._zoneThemeId &&
                    _eventDisplayMode == other._eventDisplayMode &&
                    _isReadOnly == other._isReadOnly &&
                    _contentHash == other._contentHash;
            }

            public override bool Equals(object obj)
            {
                return obj is StaticFrameKey && Equals((StaticFrameKey)obj);
            }

            public override int GetHashCode()
            {
                return _contentHash;
            }

            private static int ComputeContentHash(TimelineFrame frame)
            {
                unchecked
                {
                    int hash = 17;
                    hash = Combine(hash, StringHash(frame.LockLabel));
                    hash = Combine(hash, StringHash(frame.StatusText));
                    hash = Combine(hash, ReferenceId(frame.MinimapDensity));
                    hash = Combine(hash, frame.VisibleItems.Count);

                    foreach (TimelineItem item in frame.VisibleItems)
                    {
                        hash = Combine(hash, item.RowIndex);
                        hash = Combine(hash, item.StartTick);
                        hash = Combine(hash, item.EndTick);
                        hash = Combine(hash, item.IsUnknown ? 1 : 0);
                        var source = item.SourceEvent;
                        if (source == null)
                            continue;
                        hash = Combine(hash, (int)source.EventType);
                        hash = Combine(hash, (int)source.TrackId);
                        hash = Combine(hash, source.VirtualTick);
                        hash = Combine(hash, source.VirtualDuration);
                        hash = Combine(hash, source.Attribute);
                        hash = Combine(hash, source.Pan);
                        hash = Combine(hash, source.Vel);
                        // Selection is baked into the static frame (selected bodies change
                        // colour), so it has to invalidate the cache like any other pixel input.
                        hash = Combine(hash, frame.IsSelected(source) ? 1 : 0);
                        hash = Combine(
                            hash,
                            source.Instrument == null
                                ? -1
                                : source.Instrument.InsNum);
                    }

                    int lastRow = Math.Min(
                        frame.Rows.Count,
                        frame.FirstVisibleRow +
                            Math.Max(
                                1,
                                (frame.CanvasBottom -
                                    frame.Coordinates.RulerHeight) /
                                frame.Coordinates.RowHeight + 1));
                    for (int index = frame.FirstVisibleRow;
                        index < lastRow;
                        index++)
                    {
                        hash = Combine(
                            hash,
                            StringHash(frame.Rows[index].Name));
                    }

                    return hash;
                }
            }

            private static int Combine(int hash, int value)
            {
                unchecked
                {
                    return (hash * 31) + value;
                }
            }

            private static int ReferenceId(object value)
            {
                return value == null ? 0 : RuntimeHelpers.GetHashCode(value);
            }

            private static int StringHash(string value)
            {
                return value == null
                    ? 0
                    : StringComparer.Ordinal.GetHashCode(value);
            }
        }
    }
}
