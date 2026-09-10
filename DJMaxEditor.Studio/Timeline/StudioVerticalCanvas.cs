using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DJMaxEditor.Controls.TimelineV2;
using DJMaxEditor.Controls.Vertical;
using DJMaxEditor.DJMax;
using DJMaxEditor.Editor;
using DJMaxEditor.Studio.Design;
using DJMaxEditor.Studio.Editing;

namespace DJMaxEditor.Studio.Timeline
{
    /// <summary>
    /// The vertical (ptSequencer-geometry) chart surface, drawn in WPF retained mode.
    ///
    /// Why this shape. The legacy GDI+ control repainted every pixel of the canvas on every
    /// frame, including the ruler text and the whole grid, because WM_PAINT gives you one
    /// invalidation and no way to say "only the playhead moved". During playback the playhead
    /// moves 60 times a second, so the old surface redrew ~3000 note rectangles and ~40 shaped
    /// text runs 60 times a second to move a single 1px line - which is exactly the stutter the
    /// playtests reported.
    ///
    /// WPF gives us the missing tool: a <see cref="DrawingVisual"/> keeps its own retained
    /// command list, and re-opening one visual does not touch the others. So the surface is
    /// split into three layers by *how often each changes*:
    ///
    ///   Layer 0  band     lane fields, grid, notes      - changes on scroll/zoom/edit
    ///   Layer 1  chrome   lane-name strip, tick ruler   - changes on scroll/zoom/layout
    ///   Layer 2  overlay  playhead, marquee, hover      - changes every frame
    ///
    /// A playhead move re-records ~4 drawing instructions and leaves the other two layers
    /// entirely alone. That is the whole performance story.
    ///
    /// The other two costs are handled in <see cref="StudioTimelineTheme"/> (frozen brushes and
    /// a FormattedText cache) and by drawing the grid as three <see cref="StreamGeometry"/>
    /// objects - one per line class - instead of several hundred individual DrawLine calls.
    ///
    /// "Vertical" in the name is the *projection model* - <c>VerticalTimelineProjection</c>,
    /// <c>VerticalCoordinateSystem</c>, <c>VerticalTimelineFrame</c>, time along Y and lanes along
    /// X - and not the screen axis. Setting <see cref="Orientation"/> to
    /// <see cref="TimelineOrientation.Horizontal"/> reflects that surface across its diagonal on
    /// the way to the screen, so time runs across and lanes down, with the same projection, the
    /// same frame and the same hit testing underneath. Every draw goes through
    /// <see cref="TimelineSurfaceMap.ScreenFromSurface"/> and every pointer position comes back
    /// through <see cref="TimelineSurfaceMap.ToSurface(Point)"/>; nothing else in the class knows
    /// which way the screen runs.
    /// </summary>
    internal sealed class StudioVerticalCanvas : FrameworkElement
    {
        private const int LayerBand = 0;
        private const int LayerChrome = 1;
        private const int LayerOverlay = 2;

        /// <summary>
        /// Below this device-pixel spacing the sub-grid stops being information and becomes a
        /// flat grey wash that costs real time to draw. ptSequencer simply stops drawing them.
        /// </summary>
        private const double MinimumGridSpacing = 4.0;

        /// <summary>A note narrower or shorter than this cannot fit a legible label.</summary>
        private const double MinimumLabelWidth = 26.0;
        private const double MinimumLabelHeight = 9.0;

        /// <summary>
        /// Lane thickness, in DIPs, below which note art stops being art. The glyphs are authored
        /// against a 90 px frame, so a 9 px lane is a tenth-scale bitmap: the head's ring closes to
        /// a smudge and the difference between a repeat and a hold - the thing the art exists to
        /// show - is gone. A flat rectangle is both cheaper and more honest at that size.
        /// </summary>
        private const double MinimumNoteArtThickness = 12.0;

        /// <summary>
        /// The frame every timeline glyph is drawn from, as a phase into a ten-frame strip: the
        /// last one.
        ///
        /// <para>
        /// The playfield animates these strips against the music. A timeline does not have a beat to
        /// animate against, so it needs one still frame, and the last is the one to pick: measured,
        /// <c>long_note_line</c> is brightest and tallest at column 9 and <c>long_note_end</c> frame
        /// 9 carries the matching 16..74 cross-section, so choosing it is what makes a run's body
        /// and its cap meet without a seam. One phase for every piece of every note, so head, body,
        /// cap and stem can never index different frames of the same animation.
        /// </para>
        /// </summary>
        private const double StillPhase = 0.95;

        /// <summary>
        /// Chrome thicknesses for the horizontal orientation, swapped relative to the vertical
        /// defaults because the transpose swaps which gutter you see them as: the surface's
        /// header gutter is drawn as the time ruler across the top (so it wants the ruler's 22 px)
        /// and the surface's ruler strip becomes the track-name column down the left, where 22 px
        /// would clip every name to two characters. 72 px is the width a full lane name from a
        /// .pst preset needs at 10 pt.
        /// </summary>
        private const int HorizontalHeaderWidth = 22;
        private const int HorizontalRulerHeight = 72;

        private readonly VisualCollection _layers;
        private readonly DrawingVisual _band = new DrawingVisual();
        private readonly DrawingVisual _chrome = new DrawingVisual();
        private readonly DrawingVisual _overlay = new DrawingVisual();
        private readonly StudioTimelineTheme _theme = StudioTimelineTheme.Default;

        private TextCache _rulerText;
        private TextCache _laneText;
        private TextCache _noteText;
        private double _textDpi = 1.0;

        private VerticalTimelineViewModel _viewModel;
        private VerticalTimelineFrame _frame;
        private TimelineOrientation _orientation = TimelineOrientation.Vertical;
        private TimelineSurfaceMap _map = TimelineSurfaceMap.VerticalIdentity;

        private bool _bandDirty = true;
        private bool _chromeDirty = true;

        private Point? _hoverPoint;
        private Rect? _marquee;
        private Point _dragOrigin;
        private bool _dragging;
        private bool _panning;
        private Point _panOrigin;
        private double _panOriginTick;
        private double _panOriginNativeX;

        public StudioVerticalCanvas()
        {
            _layers = new VisualCollection(this);
            _layers.Add(_band);
            _layers.Add(_chrome);
            _layers.Add(_overlay);

            // Hairlines and lane fields are axis-aligned rectangles, so antialiasing them buys
            // nothing and costs a coverage pass per edge. Aliased is both faster and crisper.
            RenderOptions.SetEdgeMode(_band, EdgeMode.Aliased);
            RenderOptions.SetEdgeMode(_chrome, EdgeMode.Aliased);

            // Note art is authored against a 90 px frame and drawn into a lane a fraction of that,
            // so every glyph on the band is a downscale. Fant rather than the default bilinear:
            // bilinear at better than 2:1 reduction drops whole source rows, which is what turns a
            // repeat note's dotted ring into an uneven scatter of dots.
            RenderOptions.SetBitmapScalingMode(_band, BitmapScalingMode.HighQuality);

            ClipToBounds = true;
            SnapsToDevicePixels = true;
            Focusable = true;
            FocusVisualStyle = null;
        }

        /// <summary>Raised when the user clicks the tick ruler, so the shell can seek audio.</summary>
        public event EventHandler<VerticalSeekEventArgs> SeekRequested;

        /// <summary>Raised after any interaction that changed the selection or the chart.</summary>
        public event EventHandler InteractionCompleted;

        /// <summary>Raised on right-click, carrying whatever was under the pointer.</summary>
        public event EventHandler<VerticalHitResult> ContextRequested;

        /// <summary>
        /// Raised at the end of every render pass, after <see cref="Frame"/> has been rebuilt.
        ///
        /// <para>
        /// The column scrollbar exists for this: how much of the layout fits is only known once a
        /// frame has been built for the size the canvas actually got, because that is when the
        /// viewport width, the fitted column scale and the clamped offset are all settled. A
        /// scrollbar synced any earlier - from a resize, or from the offset's setter - would be
        /// describing the previous frame.
        /// </para>
        /// </summary>
        public event EventHandler FrameBuilt;

        public ToolMode Tool { get; set; }

        public GridDivision Grid { get; set; }

        public BeatDisplay Beats { get; set; }

        /// <summary>
        /// Which screen axis time runs along. Vertical is the ptSequencer reading and the default;
        /// horizontal reflects the same surface across its diagonal into the DAW reading.
        /// </summary>
        public TimelineOrientation Orientation
        {
            get { return _orientation; }
            set
            {
                if (_orientation == value)
                {
                    return;
                }

                _orientation = value;
                InvalidateAll();
            }
        }

        /// <summary>
        /// The surface extent along the time axis, in DIPs: the canvas height, or its width when
        /// transposed. The shell zooms and pages against this, so those gestures keep anchoring at
        /// the middle of the visible span in either orientation instead of at some point off-screen.
        /// </summary>
        public double TimeAxisExtent
        {
            get { return _orientation == TimelineOrientation.Horizontal ? ActualWidth : ActualHeight; }
        }

        /// <summary>The surface extent across the lanes: the canvas width, or its height when
        /// transposed. This is what "fit all lanes to the window" has to fit into.</summary>
        public double LaneAxisExtent
        {
            get { return _orientation == TimelineOrientation.Horizontal ? ActualHeight : ActualWidth; }
        }

        /// <summary>Draw a keysound/instrument label inside each note. Off by default: at 100%
        /// zoom the labels are the single most expensive thing on the canvas, and the playtest
        /// specifically flagged them as the cause of rough playback.</summary>
        public bool ShowNoteLabels { get; set; }

        /// <summary>
        /// Draw each note as the arcade's own art - head, run body, end cap - instead of a flat
        /// rectangle. On by default: it is how the legacy WinForms timeline reads a chart and how
        /// TechMania's editor reads one, and a coloured box tells you a note is there without
        /// telling you what it is.
        ///
        /// <para>
        /// The rectangle is not gone. It is what a note falls back to when the art has nothing for
        /// it - anything the TECHNIKA attribute table does not name, a lane too thin for a glyph to
        /// survive, a chart in a mode whose attributes mean something else entirely - so every event
        /// in the chart is still visible and still clickable in exactly the place it was.
        /// </para>
        /// </summary>
        public bool ShowNoteAssets { get; set; } = true;

        public VerticalTimelineViewModel ViewModel
        {
            get { return _viewModel; }
            set
            {
                if (_viewModel == value)
                {
                    return;
                }

                if (_viewModel != null)
                {
                    _viewModel.RepaintRequested -= OnRepaintRequested;
                    _viewModel.SeekRequested -= OnViewModelSeekRequested;
                }

                _viewModel = value;

                if (_viewModel != null)
                {
                    _viewModel.RepaintRequested += OnRepaintRequested;
                    _viewModel.SeekRequested += OnViewModelSeekRequested;
                }

                InvalidateAll();
            }
        }

        /// <summary>The frame last built, for the shell's status readout and for tests.</summary>
        public VerticalTimelineFrame Frame
        {
            get { return _frame; }
        }

        protected override int VisualChildrenCount
        {
            get { return _layers.Count; }
        }

        /// <summary>
        /// Marks the note/grid layer stale. Call after an edit, a scroll or a zoom - anything
        /// that changes what is in the tick window.
        /// </summary>
        public void InvalidateBand()
        {
            _bandDirty = true;
            InvalidateVisual();
        }

        public void InvalidateAll()
        {
            _bandDirty = true;
            _chromeDirty = true;
            _textDpi = 0;
            InvalidateVisual();
        }

        /// <summary>
        /// Redraws only the overlay. This is the playback path: it must not touch the band or
        /// the chrome, or we are back to repainting the world once per frame.
        /// </summary>
        public void InvalidateOverlay()
        {
            if (_frame == null)
            {
                InvalidateVisual();
                return;
            }
            DrawOverlay();
        }

        protected override Visual GetVisualChild(int index)
        {
            return _layers[index];
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            // The canvas fills whatever it is given; it has no natural size. Guard the infinite
            // case a ScrollViewer would hand us.
            double width = double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width;
            double height = double.IsInfinity(availableSize.Height) ? 480 : availableSize.Height;
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            InvalidateAll();
            return finalSize;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            Rebuild();
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            InvalidateAll();
        }

        private void OnRepaintRequested(object sender, EventArgs e)
        {
            InvalidateBand();
        }

        private void OnViewModelSeekRequested(object sender, VerticalSeekEventArgs e)
        {
            EventHandler<VerticalSeekEventArgs> handler = SeekRequested;
            if (handler != null)
            {
                handler(this, e);
            }
        }

        private void Rebuild()
        {
            int width = (int)Math.Round(ActualWidth);
            int height = (int)Math.Round(ActualHeight);

            // Rebuilt every pass rather than cached: it depends on the element's size, and a
            // stale map is a canvas whose hit testing is a resize behind what you can see.
            _map = TimelineSurfaceMap.Create(_orientation, width, height);

            if (_viewModel == null || !_viewModel.HasLayout || width <= 0 || height <= 0)
            {
                _frame = null;
                DrawEmpty(width, height);
                RaiseFrameBuilt();
                return;
            }

            EnsureTextCaches();

            // WPF already works in device-independent units and scales the whole visual tree, so
            // the coordinate system must be told the scale is 1. Feeding it the monitor DPI here
            // would apply the scale twice and the canvas would be visibly too large on a 150%
            // display while hit testing stayed correct - a subtle, maddening mismatch.
            _viewModel.DpiScale = 1.0f;

            // Cheap when nothing changed, so it costs one comparison per frame and cannot get out
            // of step with the orientation the way a one-shot call from the setter could.
            _viewModel.SetChromeSizes(
                _map.IsTransposed ? HorizontalHeaderWidth : VerticalTimelineViewModel.DefaultHeaderWidth,
                _map.IsTransposed ? HorizontalRulerHeight : VerticalTimelineViewModel.DefaultRulerHeight);

            // Surface dimensions, not screen ones: transposed, the frame is as wide as the canvas
            // is tall. Everything downstream - AutoFitColumns, OriginY, LastVisibleTick - then
            // measures the axis it is actually about.
            _frame = _viewModel.BuildFrame((int)_map.SurfaceWidth, (int)_map.SurfaceHeight);

            if (_bandDirty)
            {
                DrawBand();
                _bandDirty = false;
            }
            if (_chromeDirty)
            {
                DrawChrome();
                _chromeDirty = false;
            }
            DrawOverlay();
            RaiseFrameBuilt();
        }

        private void RaiseFrameBuilt()
        {
            EventHandler handler = FrameBuilt;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void EnsureTextCaches()
        {
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            if (dpi <= 0)
            {
                dpi = 1.0;
            }
            if (_rulerText != null && Math.Abs(dpi - _textDpi) < 0.0001)
            {
                return;
            }

            _textDpi = dpi;
            _rulerText = new TextCache(_theme.MonoTypeface, 10, _theme.TextMuted, dpi);
            _laneText = new TextCache(_theme.UiTypeface, 10, _theme.TextSecondary, dpi);
            _noteText = new TextCache(_theme.MonoTypeface, 9, _theme.TextOnNote, dpi);
        }

        private void DrawEmpty(int width, int height)
        {
            using (DrawingContext dc = _band.RenderOpen())
            {
                dc.DrawRectangle(_theme.Backdrop, null, new Rect(0, 0, Math.Max(0, width), Math.Max(0, height)));
            }
            using (_chrome.RenderOpen())
            {
            }
            using (_overlay.RenderOpen())
            {
            }
        }

        // -----------------------------------------------------------------------------------
        // Surface <-> screen
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Pushes the surface-to-screen transform when the canvas is transposed and returns the
        /// number of pops owed. Zero pushes in the vertical orientation on purpose: not pushing an
        /// identity keeps the default path byte-identical to the one every existing pixel
        /// measurement was taken against.
        /// </summary>
        private int PushSurface(DrawingContext dc)
        {
            if (!_map.IsTransposed)
            {
                return 0;
            }
            dc.PushTransform(_map.ScreenFromSurface);
            return 1;
        }

        /// <summary>
        /// Pushes back to real screen space, on top of <see cref="PushSurface"/>. Text only: a
        /// glyph run drawn under the reflection comes out mirrored across the diagonal, so labels
        /// are positioned transposed - <see cref="TimelineSurfaceMap.ToScreen(Rect)"/> - and then
        /// drawn upright.
        /// </summary>
        private int PushScreen(DrawingContext dc)
        {
            if (!_map.IsTransposed)
            {
                return 0;
            }
            dc.PushTransform(_map.SurfaceFromScreen);
            return 1;
        }

        private static void Pop(DrawingContext dc, int pushes)
        {
            for (int i = 0; i < pushes; i++)
            {
                dc.Pop();
            }
        }

        // -----------------------------------------------------------------------------------
        // Layer 0: lane fields, grid, notes
        // -----------------------------------------------------------------------------------

        private void DrawBand()
        {
            using (DrawingContext dc = _band.RenderOpen())
            {
                // Push around the whole layer rather than per primitive, and pop outside the body
                // so an early return in it cannot leave the context unbalanced.
                int pushes = PushSurface(dc);
                DrawBandCore(dc);
                Pop(dc, pushes);
            }
        }

        private void DrawBandCore(DrawingContext dc)
        {
            VerticalTimelineFrame frame = _frame;
            VerticalCoordinateSystem coords = frame.Coordinates;
            double bodyTop = coords.RulerHeight;
            double bodyLeft = coords.HeaderWidth;
            double bodyWidth = Math.Max(0, frame.Width - bodyLeft);
            double bodyHeight = Math.Max(0, frame.Height - bodyTop);

            dc.DrawRectangle(_theme.Backdrop, null, new Rect(0, 0, frame.Width, frame.Height));

            if (bodyWidth <= 0 || bodyHeight <= 0)
            {
                return;
            }

            Rect body = new Rect(bodyLeft, bodyTop, bodyWidth, bodyHeight);
            dc.PushClip(new RectangleGeometry(body));

            DrawLaneFields(dc, frame, body);
            DrawGrid(dc, frame, body);
            DrawNotes(dc, frame);

            dc.Pop();
        }

        private void DrawLaneFields(DrawingContext dc, VerticalTimelineFrame frame, Rect body)
        {
            if (frame.FirstVisibleColumn < 0)
            {
                return;
            }

            VerticalCoordinateSystem coords = frame.Coordinates;
            System.Collections.Generic.IList<VerticalColumn> columns = frame.Layout.Columns;

            for (int i = frame.FirstVisibleColumn; i <= frame.LastVisibleColumn && i < columns.Count; i++)
            {
                VerticalColumn column = columns[i];
                double left = coords.NativeXToScreen(column.NativeLeft, frame.OriginNativeX);
                double width = column.Width * coords.ColumnScale;
                if (width <= 0)
                {
                    continue;
                }

                // Snap to whole pixels: a lane field on a half pixel leaves a 1px seam of
                // backdrop between lanes that reads as a phantom grid line.
                double x = Math.Round(left);
                double w = Math.Round(left + width) - x;
                dc.DrawRectangle(_theme.FieldFor(column), null, new Rect(x, body.Top, w, body.Height));
            }

            // Separators after the fields, so a lane's own fill never covers its neighbour's rule.
            StreamGeometry thin = new StreamGeometry();
            StreamGeometry heavy = new StreamGeometry();
            using (StreamGeometryContext thinCtx = thin.Open())
            using (StreamGeometryContext heavyCtx = heavy.Open())
            {
                for (int i = frame.FirstVisibleColumn; i <= frame.LastVisibleColumn && i < columns.Count; i++)
                {
                    VerticalColumn column = columns[i];
                    double x = Math.Round(coords.NativeXToScreen(column.NativeLeft, frame.OriginNativeX)) + 0.5;
                    // bold=3 in the .pst preset is the heavy rule that splits MR from the
                    // background lanes; bold=1 is the ordinary lane separator.
                    StreamGeometryContext target = column.Bold >= 3 ? heavyCtx : thinCtx;
                    target.BeginFigure(new Point(x, body.Top), false, false);
                    target.LineTo(new Point(x, body.Bottom), true, false);
                }
            }
            thin.Freeze();
            heavy.Freeze();
            dc.DrawGeometry(null, _theme.LaneEdge, thin);
            dc.DrawGeometry(null, _theme.LaneEdgeStrong, heavy);
        }

        private void DrawGrid(DrawingContext dc, VerticalTimelineFrame frame, Rect body)
        {
            VerticalCoordinateSystem coords = frame.Coordinates;
            int ticksPerMeasure = TicksPerMeasure();
            if (ticksPerMeasure <= 0)
            {
                return;
            }

            double startTick = Math.Min(frame.OriginTick, frame.LastVisibleTick);
            double endTick = Math.Max(frame.OriginTick, frame.LastVisibleTick);

            // Three geometries, three draw calls, regardless of how many lines there are. The
            // naive version issues one DrawLine per line, and each one is a separate entry in
            // the retained command list that the compositor has to walk every frame.
            StreamGeometry bars = new StreamGeometry();
            StreamGeometry beats = new StreamGeometry();
            StreamGeometry subs = new StreamGeometry();

            int beatDenominator = Beats != null && !Beats.IsOff ? Beats.Denominator : 4;
            double beatTicks = (double)ticksPerMeasure / beatDenominator;
            double subTicks = Grid != null ? Grid.StepTicks(ticksPerMeasure) : 0;

            using (StreamGeometryContext barCtx = bars.Open())
            using (StreamGeometryContext beatCtx = beats.Open())
            using (StreamGeometryContext subCtx = subs.Open())
            {
                // Sub-grid first and only when it is legible.
                if (subTicks > 0 && subTicks * coords.PixelsPerTick >= MinimumGridSpacing)
                {
                    AddHorizontalRules(subCtx, frame, body, startTick, endTick, subTicks, beatTicks, ticksPerMeasure);
                }
                if (beatTicks > 0 && beatTicks * coords.PixelsPerTick >= MinimumGridSpacing)
                {
                    AddHorizontalRules(beatCtx, frame, body, startTick, endTick, beatTicks, 0, ticksPerMeasure);
                }
                AddHorizontalRules(barCtx, frame, body, startTick, endTick, ticksPerMeasure, 0, 0);
            }

            bars.Freeze();
            beats.Freeze();
            subs.Freeze();

            dc.DrawGeometry(null, _theme.GridSub, subs);
            dc.DrawGeometry(null, _theme.GridBeat, beats);
            dc.DrawGeometry(null, _theme.GridBar, bars);
        }

        /// <summary>
        /// Adds one horizontal rule per multiple of <paramref name="step"/> in the tick window.
        /// <paramref name="skipMultiplesOf"/> and <paramref name="skipMeasure"/> suppress ticks a
        /// heavier line will cover, so we never stack two pens on the same pixel row - stacked
        /// hairlines are what makes a grid look muddy at low zoom.
        /// </summary>
        private void AddHorizontalRules(
            StreamGeometryContext ctx,
            VerticalTimelineFrame frame,
            Rect body,
            double startTick,
            double endTick,
            double step,
            double skipMultiplesOf,
            int skipMeasure)
        {
            if (step <= 0)
            {
                return;
            }

            VerticalCoordinateSystem coords = frame.Coordinates;
            long first = (long)Math.Floor(Math.Max(0, startTick) / step);
            long last = (long)Math.Ceiling(endTick / step);

            // A pathological zoom-out could ask for a million lines; the legibility guard above
            // normally prevents it, but bar lines have no guard so clamp here as a backstop.
            if (last - first > 8192)
            {
                return;
            }

            for (long index = first; index <= last; index++)
            {
                double tick = index * step;
                if (tick < startTick || tick > endTick)
                {
                    continue;
                }
                if (skipMultiplesOf > 0 && IsMultiple(tick, skipMultiplesOf))
                {
                    continue;
                }
                if (skipMeasure > 0 && IsMultiple(tick, skipMeasure))
                {
                    continue;
                }

                double y = Math.Round(coords.TickToY(tick, frame.OriginTick)) + 0.5;
                if (y < body.Top || y > body.Bottom)
                {
                    continue;
                }
                ctx.BeginFigure(new Point(body.Left, y), false, false);
                ctx.LineTo(new Point(body.Right, y), true, false);
            }
        }

        private static bool IsMultiple(double value, double step)
        {
            if (step <= 0)
            {
                return false;
            }
            double ratio = value / step;
            return Math.Abs(ratio - Math.Round(ratio)) < 0.0001;
        }

        private void DrawNotes(DrawingContext dc, VerticalTimelineFrame frame)
        {
            System.Collections.ObjectModel.ReadOnlyCollection<VerticalPlacedItem> items = frame.Items;
            bool labels = ShowNoteLabels;
            TimelineNoteSprites art = ArtFor(frame);
            double dir = frame.Coordinates.TimeDirection == VerticalTimeDirection.Upward ? -1.0 : 1.0;

            for (int i = 0; i < items.Count; i++)
            {
                VerticalPlacedItem placed = items[i];
                bool selected = _viewModel.IsSelected(placed.Item.SourceEvent);
                NoteBrushes brushes = _theme.NoteFor(placed.Item, placed.Column, selected);

                // Inset by half the pen so the outline lands inside the note's own cell rather
                // than straddling the lane separator. Without this the outline the playtest
                // asked for would itself look like a misaligned grid line.
                double x = Math.Round(placed.Left) + 0.5;
                double y = Math.Round(placed.Top) + 0.5;
                double w = Math.Max(1, Math.Round(placed.Width) - 1);
                double h = Math.Max(1, Math.Round(placed.Height) - 1);
                Rect rect = new Rect(x, y, w, h);

                if (!DrawNoteArt(dc, art, placed, dir))
                {
                    dc.DrawRectangle(brushes.Fill, brushes.Edge, rect);
                }
                else if (selected)
                {
                    // The art carries no selection colour of its own, so selection is an outline
                    // around the note's cell. Outline rather than a fill: a filled box over a glyph
                    // hides the thing the user selected.
                    dc.DrawRectangle(null, _theme.SelectionEdge, rect);
                }

                if (!labels)
                {
                    continue;
                }

                // The label's room is measured on screen, not on the surface: transposed, a long
                // note is wide rather than tall, and a legibility test taken on the surface would
                // reject exactly the notes that have the most room for a name.
                Rect screenRect = _map.ToScreen(rect);
                if (screenRect.Width < MinimumLabelWidth || screenRect.Height < MinimumLabelHeight)
                {
                    continue;
                }

                string label = LabelFor(placed.Item.SourceEvent);
                FormattedText text = _noteText.Get(label);
                if (text == null)
                {
                    continue;
                }

                // Clip to the note. This is round-3 item 6: without the clip, a keysound name
                // wider than its lane spills over the neighbouring lanes and, when scrolled,
                // over the tick ruler as well.
                int pushes = PushScreen(dc);
                dc.PushClip(new RectangleGeometry(screenRect));
                dc.DrawText(text, new Point(
                    screenRect.Left + 2,
                    screenRect.Top + Math.Max(0, (screenRect.Height - text.Height) / 2)));
                dc.Pop();
                Pop(dc, pushes);
            }
        }

        private static string LabelFor(EventData source)
        {
            if (source == null)
            {
                return null;
            }
            if (source.Instrument != null && !string.IsNullOrEmpty(source.Instrument.Name))
            {
                return source.Instrument.Name;
            }
            return null;
        }

        /// <summary>
        /// The note art to draw this frame with, or null to draw rectangles.
        ///
        /// <para>
        /// Gated on the layout being TECHNIKA's, not merely on the toggle. The art is indexed by
        /// <c>EventData.Attribute</c> through <see cref="TechnikaNoteClassifier"/>, and attribute 12
        /// is a hold in a TECHNIKA chart and something else entirely in a Portable one - so a 4B
        /// chart drawn through this table would be confidently mislabelled rather than merely plain.
        /// The lane colours have the same exposure and the same excuse; the difference is that a
        /// wrong colour is a wrong hint and a wrong glyph is a wrong fact.
        /// </para>
        /// </summary>
        private TimelineNoteSprites ArtFor(VerticalTimelineFrame frame)
        {
            if (!ShowNoteAssets || !VerticalTrackLayout.IsTechnikaMode(frame.Layout.Mode))
            {
                return null;
            }
            return TimelineNoteSprites.Default;
        }

        /// <summary>
        /// Draws one note as arcade art, and reports whether it did - false is the caller's signal
        /// to fall back to the rectangle.
        ///
        /// <para>
        /// Everything is drawn through one local frame whose +X is increasing time and whose +Y runs
        /// across the lane, which is the frame the art is authored in: the arcade scrolls
        /// horizontally, so a cap 25 px wide and 90 tall is narrow along travel and fills the lane
        /// across it. That single matrix is what makes one drawing serve four cases. The surface's
        /// own reflection - see <see cref="TimelineSurfaceMap"/> - turns the local frame back into
        /// the authored orientation when the canvas is transposed and into a 90-degree turn of it
        /// when it is not, and <paramref name="dir"/> flips it for upward time. No branch here knows
        /// which of the four it is in.
        /// </para>
        /// </summary>
        private bool DrawNoteArt(
            DrawingContext dc, TimelineNoteSprites sprites, VerticalPlacedItem placed, double dir)
        {
            if (sprites == null || !IsPlayable(placed.Column) ||
                placed.Width < MinimumNoteArtThickness)
            {
                return false;
            }

            TimelineNoteArt art = sprites.For(
                TechnikaNoteClassifier.Classify(placed.Item.SourceEvent));
            if (art == null)
            {
                return false;
            }

            // Scaled against the lane rather than fitted to it, so the arcade's own proportions
            // survive: longnote is authored at 116 in a 90 frame and is meant to overhang.
            double unit = placed.Width / TimelineNoteSprites.ReferenceFrameSize;

            // The onset edge. Upward time puts later ticks higher, so a note's head is at the
            // bottom of its cell there and at the top of it the other way round.
            double onset = dir < 0 ? placed.Bottom : placed.Top;
            dc.PushTransform(new MatrixTransform(new Matrix(
                0, dir, 1, 0, placed.Left + (placed.Width / 2.0), onset)));

            if (art.HasTrail)
            {
                double length = placed.Height;
                double capWidth = Math.Min(length, art.Cap.FrameWidth * unit);
                double body = length - capWidth;
                if (body > 0)
                {
                    // The body sheet where the family has one, a cut through the cap where it does
                    // not - the drag curve is authored as a cap and nothing else.
                    ImageSource run = art.Body == null
                        ? art.Cap.Stem(StillPhase)
                        : art.Body.Frame(StillPhase);
                    dc.DrawImage(run, Centred(0, body, art.Cap.FrameSize * unit));
                }
                dc.DrawImage(art.Cap.Frame(StillPhase),
                    Centred(body, capWidth, art.Cap.FrameSize * unit));
            }

            // Head last: it is authored to sit over the near end of its own run, and the legacy
            // renderer draws it in that order for the same reason.
            double headWidth = art.Head.FrameWidth * unit;
            dc.DrawImage(art.Head.Frame(StillPhase),
                Centred(-headWidth / 2.0, headWidth, art.Head.FrameSize * unit));

            dc.Pop();
            return true;
        }

        /// <summary>
        /// A rectangle in the note's local frame: <paramref name="along"/> and
        /// <paramref name="length"/> along time, <paramref name="across"/> centred on the lane.
        /// </summary>
        private static Rect Centred(double along, double length, double across)
        {
            return new Rect(along, -across / 2.0, length, across);
        }

        /// <summary>
        /// Whether a column is a lane a player reads. Note art is for those only - the same rule
        /// the theme applies to colour, and for a sharper reason: a keysound track carries long
        /// events whose duration is a sample length, and drawing one as a drag curve would claim
        /// the chart asks the player to hold something it does not.
        /// </summary>
        private static bool IsPlayable(VerticalColumn column)
        {
            if (column == null)
            {
                return false;
            }
            switch (column.Kind)
            {
                case VerticalColumnKind.Button:
                case VerticalColumnKind.SideLeft:
                case VerticalColumnKind.SideRight:
                case VerticalColumnKind.ShoulderLeft:
                case VerticalColumnKind.ShoulderRight:
                    return true;
                default:
                    return false;
            }
        }

        // -----------------------------------------------------------------------------------
        // Layer 1: lane-name strip + tick ruler + corner
        // -----------------------------------------------------------------------------------

        private void DrawChrome()
        {
            using (DrawingContext dc = _chrome.RenderOpen())
            {
                int pushes = PushSurface(dc);
                DrawChromeCore(dc);
                Pop(dc, pushes);
            }
        }

        private void DrawChromeCore(DrawingContext dc)
        {
            VerticalTimelineFrame frame = _frame;
            VerticalCoordinateSystem coords = frame.Coordinates;
            double rulerHeight = coords.RulerHeight;
            double headerWidth = coords.HeaderWidth;

            // Four quadrants, exactly the split VOCALOID 6 uses (RulerHeaderView corner,
            // RulerView on one axis, HeaderView on the other, TrackView in the middle):
            //   corner  |  lane-name strip   (scrolls with X only)
            //   ruler   |  canvas            (ruler scrolls with Y only)
            // Transposed, the same three rectangles read as corner / track column / top ruler.
            Rect corner = new Rect(0, 0, headerWidth, rulerHeight);
            Rect laneStrip = new Rect(headerWidth, 0, Math.Max(0, frame.Width - headerWidth), rulerHeight);
            Rect ruler = new Rect(0, rulerHeight, headerWidth, Math.Max(0, frame.Height - rulerHeight));

            dc.DrawRectangle(_theme.Chrome, null, corner);
            dc.DrawRectangle(_theme.Chrome, null, laneStrip);
            dc.DrawRectangle(_theme.Chrome, null, ruler);

            DrawLaneNames(dc, frame, laneStrip);
            DrawTickRuler(dc, frame, ruler);

            // Seams last, so no fill covers them.
            dc.DrawLine(_theme.ChromeEdgePen,
                new Point(0, Math.Round(rulerHeight) + 0.5),
                new Point(frame.Width, Math.Round(rulerHeight) + 0.5));
            dc.DrawLine(_theme.ChromeEdgePen,
                new Point(Math.Round(headerWidth) + 0.5, 0),
                new Point(Math.Round(headerWidth) + 0.5, frame.Height));
        }

        private void DrawLaneNames(DrawingContext dc, VerticalTimelineFrame frame, Rect strip)
        {
            if (frame.FirstVisibleColumn < 0 || strip.Width <= 0)
            {
                return;
            }

            VerticalCoordinateSystem coords = frame.Coordinates;
            System.Collections.Generic.IList<VerticalColumn> columns = frame.Layout.Columns;

            // Clip the whole strip once: this is what stops a lane name from bleeding left over
            // the corner cell when the canvas is scrolled horizontally.
            dc.PushClip(new RectangleGeometry(strip));

            for (int i = frame.FirstVisibleColumn; i <= frame.LastVisibleColumn && i < columns.Count; i++)
            {
                VerticalColumn column = columns[i];
                double left = coords.NativeXToScreen(column.NativeLeft, frame.OriginNativeX);
                double width = column.Width * coords.ColumnScale;
                if (width <= 1)
                {
                    continue;
                }

                Rect cell = new Rect(Math.Round(left), strip.Top, Math.Round(width), strip.Height);

                // A 3px colour chip along the bottom of the header instead of tinting the whole
                // cell. V6 does the same thing with its track colours: enough to identify the
                // lane, not enough to fight the canvas for attention. Transposed, "bottom" is the
                // edge facing the chart body either way, which is where it belongs.
                dc.DrawRectangle(_theme.ChipFor(column.Index), null,
                    new Rect(cell.Left, cell.Bottom - 3, cell.Width, 3));

                string name = column.ShortName;
                if (string.IsNullOrEmpty(name))
                {
                    name = column.Name;
                }

                // The label's box is the cell minus the chip, mapped to screen: one expression that
                // centres the name in the 22px-tall header cell when vertical and in the 72px-wide
                // track cell when transposed, with no second set of alignment rules to keep in step.
                Rect labelBox = _map.ToScreen(
                    new Rect(cell.Left, cell.Top, cell.Width, Math.Max(0, cell.Height - 3)));
                FormattedText text = _laneText.Get(name);
                if (text == null || text.Width > labelBox.Width - 2)
                {
                    continue;
                }

                int pushes = PushScreen(dc);
                dc.PushClip(new RectangleGeometry(labelBox));
                dc.DrawText(text, new Point(
                    labelBox.Left + Math.Max(1, (labelBox.Width - text.Width) / 2),
                    labelBox.Top + Math.Max(0, (labelBox.Height - text.Height) / 2)));
                dc.Pop();
                Pop(dc, pushes);
            }

            dc.Pop();
        }

        private void DrawTickRuler(DrawingContext dc, VerticalTimelineFrame frame, Rect ruler)
        {
            if (ruler.Height <= 0 || ruler.Width <= 0)
            {
                return;
            }

            VerticalCoordinateSystem coords = frame.Coordinates;
            int ticksPerMeasure = TicksPerMeasure();
            int beatsPerMeasure = BeatsPerMeasure();

            double startTick = Math.Max(0, Math.Min(frame.OriginTick, frame.LastVisibleTick));
            double endTick = Math.Max(frame.OriginTick, frame.LastVisibleTick);

            System.Collections.Generic.IReadOnlyList<TimelineRulerMark> marks =
                TimelineRulerCalculator.Build(
                    new TimelineTimeRange(startTick, endTick),
                    ticksPerMeasure,
                    beatsPerMeasure,
                    coords.PixelsPerTick);

            dc.PushClip(new RectangleGeometry(ruler));

            Rect rulerScreen = _map.ToScreen(ruler);
            bool transposed = _map.IsTransposed;

            for (int i = 0; i < marks.Count; i++)
            {
                TimelineRulerMark mark = marks[i];
                double y = Math.Round(coords.TickToY(mark.Tick, frame.OriginTick)) + 0.5;
                if (y < ruler.Top - 8 || y > ruler.Bottom + 8)
                {
                    continue;
                }

                bool measure = mark.Kind != TimelineRulerMarkKind.Beat;
                double tickLength = measure ? ruler.Width : 6;
                dc.DrawLine(measure ? _theme.GridBar : _theme.GridBeat,
                    new Point(ruler.Right - tickLength, y),
                    new Point(ruler.Right, y));

                if (string.IsNullOrEmpty(mark.Label))
                {
                    continue;
                }

                FormattedText text = _rulerText.Get(mark.Label);
                if (text == null)
                {
                    continue;
                }

                // Two placements in one pair of expressions. Along the time axis: upward (and its
                // transposed reading, leftward) means the label belongs *before* its rule, or it
                // sits on top of the bar it does not label. Across the axis: pushed against the
                // gutter's body-facing edge - its right when vertical, its bottom when transposed -
                // so the numbers sit next to the chart rather than out at the window edge.
                Point rule = _map.ToScreen(new Point(ruler.Right, y));
                bool before = coords.TimeDirection == VerticalTimeDirection.Upward;
                double along = before
                    ? (transposed ? rule.X - text.Width : rule.Y - text.Height) - 1
                    : (transposed ? rule.X : rule.Y) + 1;
                double across = transposed
                    ? Math.Max(2, rulerScreen.Bottom - text.Height - 3)
                    : Math.Max(2, rulerScreen.Right - text.Width - 3);

                int pushes = PushScreen(dc);
                dc.DrawText(text, transposed ? new Point(along, across) : new Point(across, along));
                Pop(dc, pushes);
            }

            dc.Pop();
        }

        // -----------------------------------------------------------------------------------
        // Layer 2: playhead, marquee, hover
        // -----------------------------------------------------------------------------------

        private void DrawOverlay()
        {
            VerticalTimelineFrame frame = _frame;
            if (frame == null)
            {
                using (_overlay.RenderOpen())
                {
                }
                return;
            }

            using (DrawingContext dc = _overlay.RenderOpen())
            {
                int pushes = PushSurface(dc);
                DrawOverlayCore(dc, frame);
                Pop(dc, pushes);
            }
        }

        private void DrawOverlayCore(DrawingContext dc, VerticalTimelineFrame frame)
        {
            VerticalCoordinateSystem coords = frame.Coordinates;
            double bodyTop = coords.RulerHeight;
            double bodyLeft = coords.HeaderWidth;
            Rect body = new Rect(bodyLeft, bodyTop,
                Math.Max(0, frame.Width - bodyLeft), Math.Max(0, frame.Height - bodyTop));

            if (body.Width <= 0 || body.Height <= 0)
            {
                return;
            }

            double playheadY = Math.Round(frame.PlayheadY) + 0.5;
            if (playheadY >= body.Top - 1 && playheadY <= body.Bottom + 1)
            {
                // A soft band under the line so the playhead stays findable over a dense
                // patch of notes without a hard 3px rule covering them.
                dc.DrawRectangle(_theme.PlayheadGlow, null,
                    new Rect(body.Left, playheadY - 3, body.Width, 6));
                dc.DrawLine(_theme.Playhead,
                    new Point(0, playheadY),
                    new Point(body.Right, playheadY));
            }

            if (_marquee.HasValue)
            {
                Rect m = _marquee.Value;
                dc.DrawRectangle(_theme.MarqueeFill, _theme.MarqueeEdge, m);
            }
            else if (_hoverPoint.HasValue && Tool == ToolMode.Addition)
            {
                // Ghost cell under the pointer in Addition mode, snapped to the grid, so the
                // user can see exactly where a click will land before committing to it.
                Rect? ghost = GhostCell(_hoverPoint.Value);
                if (ghost.HasValue)
                {
                    dc.PushClip(new RectangleGeometry(body));
                    dc.DrawRectangle(_theme.HoverFill, _theme.MarqueeEdge, ghost.Value);
                    dc.Pop();
                }
            }
        }

        private Rect? GhostCell(Point point)
        {
            VerticalTimelineFrame frame = _frame;
            if (frame == null)
            {
                return null;
            }

            VerticalHitResult hit = frame.HitTest(point.X, point.Y);
            if (!hit.HasColumn)
            {
                return null;
            }

            VerticalCoordinateSystem coords = frame.Coordinates;
            int snapped = SnapTick(hit.Tick);
            double y = coords.TickToY(snapped, frame.OriginTick);
            double left = coords.NativeXToScreen(hit.Column.NativeLeft, frame.OriginNativeX);
            double width = hit.Column.Width * coords.ColumnScale;
            double height = Math.Max(VerticalTimelineFrame.MinimumItemHeight, GridStepPixels());

            double top = coords.TimeDirection == VerticalTimeDirection.Upward ? y - height : y;
            return new Rect(Math.Round(left) + 0.5, Math.Round(top) + 0.5,
                Math.Max(1, Math.Round(width) - 1), Math.Max(1, Math.Round(height) - 1));
        }

        private double GridStepPixels()
        {
            if (_frame == null || Grid == null || Grid.IsFree)
            {
                return VerticalTimelineFrame.MinimumItemHeight;
            }
            return Grid.StepTicks(TicksPerMeasure()) * _frame.Coordinates.PixelsPerTick;
        }

        private int SnapTick(int tick)
        {
            if (Grid == null)
            {
                return tick;
            }
            return Grid.Snap(tick, TicksPerMeasure());
        }

        private int TicksPerMeasure()
        {
            if (_viewModel == null || _viewModel.Document == null || _viewModel.Document.Model == null)
            {
                return 192 * EventData.VirtualTickSize;
            }
            // TickPerMinute is badly named: it is the measure resolution, not a rate. Multiplying
            // by VirtualTickSize lifts it into the authoritative virtual-tick space the whole
            // editor positions against.
            int resolution = _viewModel.Document.Model.TickPerMinute;
            if (resolution <= 0)
            {
                resolution = 192;
            }
            return resolution * EventData.VirtualTickSize;
        }

        private int BeatsPerMeasure()
        {
            return Beats != null && !Beats.IsOff ? Beats.Denominator : 4;
        }

        // -----------------------------------------------------------------------------------
        // Input
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// The pointer position in surface space. Every handler converts once, on entry, and
        /// everything downstream - hit testing, the marquee, seek, zoom, the ghost cell - then
        /// works in the one space the frame was built in. This is the half of the orientation that
        /// cannot be allowed to drift from the drawing: it is the same matrix, inverted.
        /// </summary>
        private Point Surface(MouseEventArgs e)
        {
            return _map.ToSurface(e.GetPosition(this));
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();

            if (_frame == null)
            {
                return;
            }

            Point point = Surface(e);

            // The tick ruler is a seek strip. Round-3 item 3 asked for exactly this: click the
            // ruler to jump the playhead.
            if (point.X < _frame.Coordinates.HeaderWidth)
            {
                if (_viewModel.SeekAt(point.Y))
                {
                    InvalidateOverlay();
                }
                e.Handled = true;
                return;
            }

            if (point.Y < _frame.Coordinates.RulerHeight)
            {
                return;
            }

            _dragOrigin = point;
            _dragging = true;
            CaptureMouse();

            bool additive = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
            HandleToolPress(point, additive);
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_frame == null)
            {
                return;
            }

            Point point = Surface(e);

            if (_panning)
            {
                // Middle-drag pans both axes. Content follows the pointer, which is the
                // convention every DAW and ptSequencer itself use. Both ends of the drag are in
                // surface space, so the transpose swaps the two deltas for free.
                double dy = point.Y - _panOrigin.Y;
                double dx = point.X - _panOrigin.X;
                _viewModel.OriginTick = _panOriginTick +
                    (_frame.Coordinates.TimeDirection == VerticalTimeDirection.Upward
                        ? dy / _frame.Coordinates.PixelsPerTick
                        : -dy / _frame.Coordinates.PixelsPerTick);
                _viewModel.OriginNativeX = _panOriginNativeX - (dx / _frame.Coordinates.ColumnScale);
                return;
            }

            _hoverPoint = point;

            if (_dragging && Tool == ToolMode.Select)
            {
                _marquee = new Rect(_dragOrigin, point);
                DrawOverlay();
                return;
            }

            if (Tool == ToolMode.Addition)
            {
                DrawOverlay();
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }

            if (_marquee.HasValue)
            {
                CommitMarquee(_marquee.Value);
                _marquee = null;
            }

            _dragging = false;
            DrawOverlay();
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.ChangedButton == MouseButton.Middle && _frame != null)
            {
                _panning = true;
                _panOrigin = Surface(e);
                _panOriginTick = _viewModel.OriginTick;
                _panOriginNativeX = _viewModel.OriginNativeX;
                CaptureMouse();
                e.Handled = true;
            }
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.ChangedButton == MouseButton.Middle && _panning)
            {
                _panning = false;
                if (IsMouseCaptured)
                {
                    ReleaseMouseCapture();
                }
                e.Handled = true;
            }
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonUp(e);
            if (_frame == null)
            {
                return;
            }

            // ptSequencer: "If you right click on a Track, you can see the name of the track."
            // We carry the whole hit result so the shell can show the lane name and, when a note
            // is under the pointer, its keysound too.
            EventHandler<VerticalHitResult> handler = ContextRequested;
            if (handler != null)
            {
                Point point = Surface(e);
                handler(this, _frame.HitTest(point.X, point.Y));
                e.Handled = true;
            }
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverPoint = null;
            DrawOverlay();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_viewModel == null || !_viewModel.HasLayout)
            {
                return;
            }

            Point point = Surface(e);

            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                _viewModel.ZoomAt(point.Y, e.Delta > 0 ? 1.15 : 1.0 / 1.15);
            }
            else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                _viewModel.ScrollByNativeX(e.Delta > 0 ? -60 : 60);
            }
            else
            {
                _viewModel.ScrollByScreenDelta(-e.Delta);
            }
            e.Handled = true;
        }

        private void HandleToolPress(Point point, bool additive)
        {
            switch (Tool)
            {
                case ToolMode.Select:
                    _viewModel.SelectAt(point.X, point.Y, additive);
                    RaiseInteractionCompleted();
                    break;

                case ToolMode.Addition:
                    AddNoteAt(point);
                    break;

                case ToolMode.Delete:
                    DeleteAt(point);
                    break;

                case ToolMode.Edit:
                    // Selecting is the first half of an edit drag; the resize itself happens on
                    // drag in OnMouseMove once a note is under the pointer.
                    _viewModel.SelectAt(point.X, point.Y, additive);
                    RaiseInteractionCompleted();
                    break;
            }
        }

        private void AddNoteAt(Point point)
        {
            VerticalHitResult hit = _frame.HitTest(point.X, point.Y);
            if (!hit.HasColumn || hit.Column.SourceTrackId < 0)
            {
                return;
            }

            EditorDocumentContext document = _viewModel.Document;
            if (document == null || document.Edits == null)
            {
                return;
            }

            EventData template = new EventData();
            template.EventType = EventType.Note;
            template.Volume = 127;
            template.Vel = 127;
            template.Pan = 64;

            EventData created = document.Edits.CreateEvent(
                template, (uint)hit.Column.SourceTrackId, SnapTick(hit.Tick));
            if (created != null)
            {
                _viewModel.Rebuild();
                InvalidateBand();
                RaiseInteractionCompleted();
            }
        }

        private void DeleteAt(Point point)
        {
            VerticalHitResult hit = _frame.HitTest(point.X, point.Y);
            if (!hit.HasItem)
            {
                return;
            }

            EditorDocumentContext document = _viewModel.Document;
            if (document == null || document.Edits == null || document.Selection == null)
            {
                return;
            }

            document.Selection.Replace(new EventData[] { hit.Item.Item.SourceEvent });
            if (document.Edits.DeleteSelection())
            {
                _viewModel.Rebuild();
                InvalidateBand();
                RaiseInteractionCompleted();
            }
        }

        private void CommitMarquee(Rect marquee)
        {
            VerticalTimelineFrame frame = _frame;
            if (frame == null || _viewModel.Document == null || _viewModel.Document.Selection == null)
            {
                return;
            }

            // A drag shorter than a few pixels is a click, and a click already selected through
            // SelectAt. Re-running it as a marquee would clear that selection.
            if (marquee.Width < 3 && marquee.Height < 3)
            {
                return;
            }

            List<EventData> caught = new List<EventData>();
            foreach (VerticalPlacedItem placed in frame.Items)
            {
                if (placed.Right < marquee.Left || placed.Left > marquee.Right ||
                    placed.Bottom < marquee.Top || placed.Top > marquee.Bottom)
                {
                    continue;
                }
                caught.Add(placed.Item.SourceEvent);
            }

            _viewModel.Document.Selection.Replace(caught);
            InvalidateBand();
            RaiseInteractionCompleted();
        }

        private void RaiseInteractionCompleted()
        {
            InvalidateBand();
            EventHandler handler = InteractionCompleted;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}
