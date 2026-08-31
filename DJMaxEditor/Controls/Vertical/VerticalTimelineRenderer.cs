using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using DJMaxEditor.DJMax;

namespace DJMaxEditor.Controls.Vertical
{
    /// <summary>Extra chart context the frame itself does not carry.</summary>
    public sealed class VerticalRenderOptions
    {
        public VerticalRenderOptions()
        {
            BeatsPerMeasure = 4;
            ShowColumnNames = true;
            ShowGrid = true;
        }

        /// <summary>Virtual ticks per measure, or 0 to skip measure lines.</summary>
        public int TicksPerMeasure { get; set; }

        public int BeatsPerMeasure { get; set; }

        /// <summary>Identity predicate for the shared selection; null means nothing is selected.</summary>
        public Func<EventData, bool> IsSelected { get; set; }

        public bool IsReadOnly { get; set; }

        public bool ShowColumnNames { get; set; }

        public bool ShowGrid { get; set; }
    }

    /// <summary>
    /// Draws an already-positioned <see cref="VerticalTimelineFrame"/>. All geometry comes
    /// from the frame, so on-screen alignment cannot drift from hit testing: the renderer
    /// performs no coordinate maths of its own beyond the measure grid.
    /// </summary>
    /// <remarks>
    /// Every pen and brush is owned for the renderer's lifetime. Fills whose colour depends
    /// on the note go through <see cref="Fill"/>, a small colour-keyed brush cache: the
    /// palette is a fixed handful of theme colours, so it converges after one frame instead
    /// of allocating a <see cref="SolidBrush"/> per visible note per frame.
    /// </remarks>
    public sealed class VerticalTimelineRenderer : IDisposable
    {
        /// <summary>
        /// Smallest note edge that still gets an outline. At 1-2px the border would be the
        /// entire note and the fill colour - which carries the note's kind - would vanish.
        /// </summary>
        private const int MinimumOutlinedSize = 3;

        private readonly Font _nameFont = StudioFont(7.5f);
        private readonly Font _gutterFont = StudioFont(7f);
        private readonly StringFormat _centered;
        private readonly StringFormat _gutter;
        private readonly Dictionary<int, SolidBrush> _fills = new Dictionary<int, SolidBrush>();
        private readonly Dictionary<int, Pen> _outlines = new Dictionary<int, Pen>();
        private readonly Pen _columnBorder = new Pen(VerticalRenderTheme.ColumnBorder);
        private readonly Pen _gridMinor = new Pen(VerticalRenderTheme.GridMinor);
        private readonly Pen _gridMajor = new Pen(VerticalRenderTheme.GridMajor);
        private readonly Pen _selectionOutline =
            new Pen(VerticalRenderTheme.SelectionOutline, 1f);
        private readonly Pen _playheadPen = new Pen(VerticalRenderTheme.Playhead, 1.5f);
        private readonly Pen _gutterBorder = new Pen(VerticalRenderTheme.GutterBorder);
        private readonly Pen _readOnlyBorder =
            new Pen(StudioColor(VerticalRenderTheme.MutedText), 1f);
        private bool _disposed;

        public VerticalTimelineRenderer()
        {
            _centered = new StringFormat(StringFormatFlags.NoWrap)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.Character
            };
            _gutter = new StringFormat(StringFormatFlags.NoWrap)
            {
                Alignment = StringAlignment.Far,
                LineAlignment = StringAlignment.Near,
                Trimming = StringTrimming.None
            };
        }

        public void Render(Graphics graphics, VerticalTimelineFrame frame)
        {
            Render(graphics, frame, new VerticalRenderOptions());
        }

        public void Render(
            Graphics graphics,
            VerticalTimelineFrame frame,
            VerticalRenderOptions options)
        {
            if (graphics == null) throw new ArgumentNullException("graphics");
            if (frame == null) throw new ArgumentNullException("frame");
            if (options == null) options = new VerticalRenderOptions();

            graphics.SmoothingMode = SmoothingMode.None;
            graphics.Clear(VerticalRenderTheme.Canvas);

            int rulerHeight = frame.Coordinates.RulerHeight;
            int headerWidth = frame.Coordinates.HeaderWidth;
            var body = new Rectangle(
                headerWidth,
                rulerHeight,
                Math.Max(0, frame.Width - headerWidth),
                Math.Max(0, frame.Height - rulerHeight));

            DrawColumnBackgrounds(graphics, frame, body);
            if (options.ShowGrid)
            {
                DrawGrid(graphics, frame, options, body);
            }
            DrawItems(graphics, frame, options, body);
            DrawPlayhead(graphics, frame, body);
            DrawGutter(graphics, frame, options, rulerHeight, headerWidth);
            if (options.ShowColumnNames)
            {
                DrawColumnNames(graphics, frame, rulerHeight, headerWidth);
            }
            if (options.IsReadOnly)
            {
                graphics.DrawRectangle(
                    _readOnlyBorder, 0, 0,
                    Math.Max(1, frame.Width - 1), Math.Max(1, frame.Height - 1));
            }
        }

        /// <summary>Colour-keyed brush cache; the vertical palette is a fixed small set.</summary>
        private SolidBrush Fill(Color color)
        {
            int key = color.ToArgb();
            SolidBrush brush;
            if (!_fills.TryGetValue(key, out brush))
            {
                brush = new SolidBrush(color);
                _fills.Add(key, brush);
            }
            return brush;
        }

        /// <summary>
        /// Colour-keyed pen cache for note outlines, keyed by the note's fill so the outline is
        /// a darkened version of it. Same rationale as <see cref="Fill"/>: a fixed palette, so
        /// it converges after one frame rather than allocating a pen per note per frame.
        /// </summary>
        private Pen Outline(Color fill)
        {
            int key = fill.ToArgb();
            Pen pen;
            if (!_outlines.TryGetValue(key, out pen))
            {
                pen = new Pen(Darken(fill, 0.45f), 1f);
                _outlines.Add(key, pen);
            }
            return pen;
        }

        private static Color Darken(Color color, float factor)
        {
            return Color.FromArgb(
                color.A,
                (int)(color.R * factor),
                (int)(color.G * factor),
                (int)(color.B * factor));
        }

        private void DrawColumnBackgrounds(
            Graphics graphics,
            VerticalTimelineFrame frame,
            Rectangle body)
        {
            if (frame.FirstVisibleColumn < 0 || body.Width <= 0 || body.Height <= 0) return;

            for (int i = frame.FirstVisibleColumn; i <= frame.LastVisibleColumn; i++)
            {
                VerticalColumn column = frame.Layout.Columns[i];
                double left = frame.Coordinates.NativeXToScreen(
                    column.NativeLeft, frame.OriginNativeX);
                int width = frame.Coordinates.ColumnWidthToScreen(column.Width);
                if (width <= 0) continue;

                var rectangle = new Rectangle(
                    (int)Math.Round(left), body.Top, width, body.Height);
                graphics.FillRectangle(Fill(VerticalRenderTheme.ForColumn(column)), rectangle);
                graphics.DrawLine(
                    _columnBorder, rectangle.Right - 1, body.Top, rectangle.Right - 1, body.Bottom);
            }
        }

        private void DrawGrid(
            Graphics graphics,
            VerticalTimelineFrame frame,
            VerticalRenderOptions options,
            Rectangle body)
        {
            if (options.TicksPerMeasure <= 0 || body.Height <= 0) return;

            int beats = Math.Max(1, options.BeatsPerMeasure);
            double ticksPerBeat = (double)options.TicksPerMeasure / beats;
            if (ticksPerBeat <= 0) return;

            // Skip beat lines once they would be denser than 4 px apart.
            bool drawBeats = ticksPerBeat * frame.Coordinates.PixelsPerTick >= 4.0;
            double firstBeat = Math.Floor(frame.OriginTick / ticksPerBeat) * ticksPerBeat;

            for (double tick = firstBeat; tick <= frame.LastVisibleTick; tick += ticksPerBeat)
            {
                bool isMeasure = Math.Abs(tick % options.TicksPerMeasure) < 0.5;
                if (!isMeasure && !drawBeats) continue;

                int y = (int)Math.Round(
                    frame.Coordinates.TickToY(tick, frame.OriginTick));
                if (y < body.Top || y >= body.Bottom) continue;
                graphics.DrawLine(isMeasure ? _gridMajor : _gridMinor, body.Left, y, body.Right, y);
            }
        }

        private void DrawItems(
            Graphics graphics,
            VerticalTimelineFrame frame,
            VerticalRenderOptions options,
            Rectangle body)
        {
            if (frame.Items.Count == 0 || body.Height <= 0) return;

            Func<EventData, bool> isSelected = options.IsSelected;
            foreach (VerticalPlacedItem placed in frame.Items)
            {
                int left = (int)Math.Round(placed.Left);
                int width = Math.Max(1, (int)Math.Round(placed.Width));
                int top = (int)Math.Round(placed.Top);
                int height = Math.Max(1, (int)Math.Round(placed.Height));

                // Clip to the body so nothing bleeds into the ruler or the gutter.
                int clippedTop = Math.Max(body.Top, top);
                int clippedBottom = Math.Min(body.Bottom, top + height);
                if (clippedBottom <= clippedTop) continue;

                int clippedLeft = Math.Max(body.Left, left);
                int clippedRight = Math.Min(body.Right, left + width);
                if (clippedRight <= clippedLeft) continue;

                var rectangle = new Rectangle(
                    clippedLeft,
                    clippedTop,
                    clippedRight - clippedLeft,
                    clippedBottom - clippedTop);

                bool selected = isSelected != null && isSelected(placed.Item.SourceEvent);
                Color fill = FillFor(placed, selected);
                graphics.FillRectangle(Fill(fill), rectangle);

                // Outline every note, not just the selected one. Adjacent columns share an
                // edge and a run of same-coloured notes used to fuse into one solid bar with
                // no readable boundary, which is what "add an outline so I can see the
                // separation" was about. Skipped below 3px, where a border would be the
                // whole note and the fill colour would stop being readable.
                if (rectangle.Width >= MinimumOutlinedSize && rectangle.Height >= MinimumOutlinedSize)
                {
                    // Only the edges that survived clipping are stroked. A border along a
                    // clipped edge would claim a hold ends at the window edge when it in fact
                    // runs past it.
                    DrawItemOutline(
                        graphics,
                        selected ? _selectionOutline : Outline(fill),
                        rectangle,
                        top >= body.Top,
                        top + height <= body.Bottom,
                        left >= body.Left,
                        left + width <= body.Right);
                }
                else if (selected)
                {
                    graphics.DrawRectangle(
                        _selectionOutline,
                        rectangle.Left,
                        rectangle.Top,
                        Math.Max(1, rectangle.Width - 1),
                        Math.Max(1, rectangle.Height - 1));
                }
            }
        }

        /// <summary>
        /// Strokes a note border one side at a time, skipping any side that was clipped away
        /// by the chart body. Drawn as four lines rather than a rectangle precisely so a
        /// clipped hold keeps looking like it continues off screen.
        /// </summary>
        private static void DrawItemOutline(
            Graphics graphics,
            Pen pen,
            Rectangle rectangle,
            bool drawTop,
            bool drawBottom,
            bool drawLeft,
            bool drawRight)
        {
            int right = rectangle.Right - 1;
            int bottom = rectangle.Bottom - 1;

            if (drawLeft)
            {
                graphics.DrawLine(pen, rectangle.Left, rectangle.Top, rectangle.Left, bottom);
            }
            if (drawRight)
            {
                graphics.DrawLine(pen, right, rectangle.Top, right, bottom);
            }
            if (drawTop)
            {
                graphics.DrawLine(pen, rectangle.Left, rectangle.Top, right, rectangle.Top);
            }
            if (drawBottom)
            {
                graphics.DrawLine(pen, rectangle.Left, bottom, right, bottom);
            }
        }

        private void DrawPlayhead(
            Graphics graphics,
            VerticalTimelineFrame frame,
            Rectangle body)
        {
            double playheadY = frame.PlayheadY;
            if (playheadY < body.Top || playheadY >= body.Bottom) return;

            int y = (int)Math.Round(playheadY);
            graphics.DrawLine(_playheadPen, body.Left, y, body.Right, y);
        }

        private void DrawGutter(
            Graphics graphics,
            VerticalTimelineFrame frame,
            VerticalRenderOptions options,
            int rulerHeight,
            int headerWidth)
        {
            if (headerWidth <= 0) return;

            graphics.FillRectangle(
                Fill(VerticalRenderTheme.Gutter), 0, 0, headerWidth, frame.Height);
            graphics.DrawLine(_gutterBorder, headerWidth - 1, 0, headerWidth - 1, frame.Height);

            if (options.TicksPerMeasure <= 0) return;

            int measure = (int)Math.Floor(frame.OriginTick / options.TicksPerMeasure);
            if (measure < 0) measure = 0;
            SolidBrush brush = Fill(VerticalRenderTheme.MutedText);
            for (double tick = (double)measure * options.TicksPerMeasure;
                tick <= frame.LastVisibleTick;
                tick += options.TicksPerMeasure, measure++)
            {
                int y = (int)Math.Round(frame.Coordinates.TickToY(tick, frame.OriginTick));
                if (y < rulerHeight - 1 || y >= frame.Height) continue;
                graphics.DrawString(
                    measure.ToString(),
                    _gutterFont,
                    brush,
                    new RectangleF(0, y + 1, headerWidth - 4, 12),
                    _gutter);
            }
        }

        private void DrawColumnNames(
            Graphics graphics,
            VerticalTimelineFrame frame,
            int rulerHeight,
            int headerWidth)
        {
            if (rulerHeight <= 0 || frame.FirstVisibleColumn < 0) return;

            var strip = new Rectangle(headerWidth, 0, Math.Max(0, frame.Width - headerWidth), rulerHeight);
            graphics.FillRectangle(Fill(VerticalRenderTheme.NameStrip), strip);
            graphics.DrawLine(_gutterBorder, strip.Left, rulerHeight - 1, strip.Right, rulerHeight - 1);

            SolidBrush text = Fill(VerticalRenderTheme.Text);
            SolidBrush muted = Fill(VerticalRenderTheme.MutedText);

            // The first visible column is usually scrolled part-way off the left edge, so its
            // label rect starts left of `headerWidth` and a centred glyph run lands on top of the
            // gutter corner. Clip to the strip: names now slide under the gutter instead of over it.
            Region previousClip = graphics.Clip;
            graphics.SetClip(strip);
            try
            {
                for (int i = frame.FirstVisibleColumn; i <= frame.LastVisibleColumn; i++)
                {
                    VerticalColumn column = frame.Layout.Columns[i];
                    if (column.ShortName.Length == 0) continue;

                    double left = frame.Coordinates.NativeXToScreen(
                        column.NativeLeft, frame.OriginNativeX);
                    int width = frame.Coordinates.ColumnWidthToScreen(column.Width);
                    if (width <= 2) continue;

                    graphics.DrawString(
                        column.ShortName,
                        _nameFont,
                        column.Bold > 0 ? text : muted,
                        new RectangleF((float)left, 2f, width, rulerHeight - 4f),
                        _centered);
                }
            }
            finally
            {
                graphics.Clip = previousClip;
                previousClip.Dispose();
            }
        }

        private static Color FillFor(VerticalPlacedItem placed, bool selected)
        {
            if (selected) return VerticalRenderTheme.SelectionFill;

            EventData sourceEvent = placed.Item.SourceEvent;
            switch (sourceEvent.EventType)
            {
                case EventType.Note:
                    Color role;
                    if (placed.Column.Kind == VerticalColumnKind.Background ||
                        placed.Column.Kind == VerticalColumnKind.Mr)
                    {
                        role = VerticalRenderTheme.Sample;
                    }
                    else
                    {
                        role = sourceEvent.VirtualDuration > 0
                            ? VerticalRenderTheme.LongNote
                            : VerticalRenderTheme.Note;
                    }
                    // Shaded by velocity, the same way V2 shades its glyphs, so changing note
                    // volume shows up on the strip immediately rather than only in the Inspector.
                    return VerticalRenderTheme.ShadeByVelocity(role, sourceEvent.Vel);
                case EventType.Tempo:
                case EventType.Beat:
                case EventType.Volume:
                    // Velocity is not a loudness on these, so they keep the flat signal colour
                    // rather than being dimmed by a byte nobody authored.
                    return VerticalRenderTheme.Tempo;
                default:
                    return VerticalRenderTheme.Unknown;
            }
        }

        private static Color StudioColor(Color color)
        {
            return Color.FromArgb(140, color);
        }

        private static Font StudioFont(float size)
        {
            return new Font("Segoe UI", size, FontStyle.Regular, GraphicsUnit.Point);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _nameFont.Dispose();
            _gutterFont.Dispose();
            _centered.Dispose();
            _gutter.Dispose();
            _columnBorder.Dispose();
            _gridMinor.Dispose();
            _gridMajor.Dispose();
            _selectionOutline.Dispose();
            _playheadPen.Dispose();
            _gutterBorder.Dispose();
            _readOnlyBorder.Dispose();
            foreach (SolidBrush brush in _fills.Values)
            {
                brush.Dispose();
            }
            _fills.Clear();
            foreach (Pen pen in _outlines.Values)
            {
                pen.Dispose();
            }
            _outlines.Clear();
        }
    }
}
