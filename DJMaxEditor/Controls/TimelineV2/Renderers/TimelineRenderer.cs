using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using DJMaxEditor.Controls.Editor;

namespace DJMaxEditor.Controls.TimelineV2.Renderers
{
    public sealed class TimelineRenderer : IDisposable
    {
        private readonly GridRenderer _grid = new GridRenderer();
        private readonly RulerRenderer _ruler = new RulerRenderer();
        private readonly ItemRenderer _items = new ItemRenderer();
        private readonly PlayheadRenderer _playhead = new PlayheadRenderer();
        private readonly MinimapRenderer _minimap = new MinimapRenderer();
        private Bitmap _staticFrame;
        private StaticFrameKey _staticFrameKey;
        private bool _hasStaticFrame;

        public int StaticFrameRebuildCount { get; private set; }

        public void Render(Graphics graphics, TimelineFrame frame)
        {
            graphics.SmoothingMode = SmoothingMode.None;
            StaticFrameKey key = StaticFrameKey.Create(frame);
            if (!_hasStaticFrame || !_staticFrameKey.Equals(key))
            {
                RebuildStaticFrame(frame, key);
            }

            graphics.DrawImageUnscaled(_staticFrame, 0, 0);
            _playhead.Render(graphics, frame);
        }

        public void Dispose()
        {
            if (_staticFrame != null)
            {
                _staticFrame.Dispose();
                _staticFrame = null;
            }
            _hasStaticFrame = false;
        }

        private void RebuildStaticFrame(
            TimelineFrame frame,
            StaticFrameKey key)
        {
            if (_staticFrame == null ||
                _staticFrame.Width != frame.Width ||
                _staticFrame.Height != frame.Height)
            {
                if (_staticFrame != null)
                    _staticFrame.Dispose();
                _staticFrame = new Bitmap(
                    frame.Width,
                    frame.Height,
                    PixelFormat.Format32bppPArgb);
            }

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

        private static void RenderZones(Graphics graphics, TimelineFrame frame)
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
                var wrapper = new GraphicsWrapper();
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

        private static void RenderHeaders(Graphics graphics, TimelineFrame frame)
        {
            using (var background = new SolidBrush(TimelineRenderTheme.Header))
            using (var border = new Pen(TimelineRenderTheme.HeaderBorder))
            using (var text = new SolidBrush(TimelineRenderTheme.Text))
            using (var font = UI.StudioDesignSystem.BodyFont(8f))
            {
                graphics.FillRectangle(
                    background,
                    0,
                    frame.Coordinates.RulerHeight,
                    frame.Coordinates.HeaderWidth,
                    frame.CanvasBottom - frame.Coordinates.RulerHeight);
                graphics.DrawLine(
                    border,
                    frame.Coordinates.HeaderWidth - 1,
                    0,
                    frame.Coordinates.HeaderWidth - 1,
                    frame.CanvasBottom);

                for (int rowIndex = frame.FirstVisibleRow; rowIndex < frame.Rows.Count; rowIndex++)
                {
                    int y = frame.Coordinates.RowToY(rowIndex, frame.FirstVisibleRow);
                    if (y >= frame.CanvasBottom) break;
                    graphics.DrawString(frame.Rows[rowIndex].Name, font, text, 8, y + 7);
                    graphics.DrawLine(border, 0, y + frame.Coordinates.RowHeight - 1,
                        frame.Coordinates.HeaderWidth, y + frame.Coordinates.RowHeight - 1);
                }
            }
        }

        private static void RenderReadOnlyState(Graphics graphics, TimelineFrame frame)
        {
            if (!frame.IsReadOnly) return;

            using (var background = new SolidBrush(Color.FromArgb(235, TimelineRenderTheme.ReadOnly)))
            using (var text = new SolidBrush(UI.StudioDesignSystem.Frost))
            using (var font = UI.StudioDesignSystem.BodyFont(8f, FontStyle.Bold))
            {
                var size = graphics.MeasureString(frame.LockLabel, font);
                var rectangle = new RectangleF(
                    frame.Width - size.Width - 22,
                    6,
                    size.Width + 14,
                    size.Height + 4);
                graphics.FillRectangle(background, rectangle);
                graphics.DrawString(frame.LockLabel, font, text, rectangle.X + 7, rectangle.Y + 2);
            }
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
                return new StaticFrameKey
                {
                    _width = frame.Width,
                    _height = frame.Height,
                    _firstVisibleRow = frame.FirstVisibleRow,
                    _rowHeight = frame.Coordinates.RowHeight,
                    _headerWidth = frame.Coordinates.HeaderWidth,
                    _rulerHeight = frame.Coordinates.RulerHeight,
                    _originTickBits =
                        BitConverter.DoubleToInt64Bits(frame.Viewport.OriginTick),
                    _pixelsPerTickBits =
                        BitConverter.DoubleToInt64Bits(frame.Viewport.PixelsPerTick),
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
