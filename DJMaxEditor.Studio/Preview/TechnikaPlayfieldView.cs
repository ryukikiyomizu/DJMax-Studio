using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using DJMaxEditor.Diagnostics;
using DJMaxEditor.DJMax;
using DJMaxEditor.Preview;
using DJMaxEditor.Studio.Timeline;

namespace DJMaxEditor.Studio.Preview
{
    /// <summary>
    /// The TECHNIKA playfield, drawn at the arcade's own proportions.
    ///
    /// <para>
    /// TECHNIKA is not a vertical-scrolling game, so the studio's tick-axis canvas cannot show
    /// what a chart actually looks like. The screen is split into two halves; a scan sweeps the
    /// upper half left to right, the next sweeps the lower half right to left, and a note's
    /// horizontal position <em>is</em> its position in time within the current scan. This element
    /// draws that.
    /// </para>
    ///
    /// <para>
    /// It owns none of the geometry. <c>GameplayPreviewProjector</c> already resolves scan index,
    /// half, lane and note kind from the chart - that logic is shared with the legacy editor and
    /// is covered by the existing test suite - and <see cref="TechnikaPlayfieldMetrics"/> holds
    /// the measured arcade pixel layout. This class is only the paint pass over the two.
    /// </para>
    ///
    /// <para>
    /// Two retained-mode layers, for the reason the timeline has three: chrome changes when the
    /// element is resized or a different chart is loaded, notes and the scanline change on every
    /// pumped tick. Redrawing the chrome sixty times a second to move one scanline is the exact
    /// cost the WPF rewrite existed to remove.
    /// </para>
    /// </summary>
    internal sealed class TechnikaPlayfieldView : FrameworkElement
    {
        /// <summary>
        /// Pulses in one scan: <c>PulsesPerBeat * DefaultBeatsPerScan</c> from the projector.
        /// Duplicated rather than exposed because it is the projector's private calibration, and
        /// widening its API to share one constant would be the worse trade. If the projector ever
        /// gains a per-chart scan length, this becomes a property read off the projection.
        /// </summary>
        private const double PulsesPerScan = 240.0 * 4.0;

        /// <summary>
        /// Opacity of a note that belongs to the next scan and has not been handed over yet.
        ///
        /// <para>
        /// 0.6, which is what the reference implementation's <c>Visibility.Transparent</c> resolves to
        /// for a note in Prepare state. Taken from there rather than tuned by eye so the two halves of
        /// the playfield read the same distance apart here as they do in the game this is a preview of.
        /// </para>
        /// </summary>
        private const double PrepareOpacity = 0.6;

        /// <summary>
        /// Times the note sprites' shine loop runs per scan - one loop per beat, at the projector's
        /// default four beats to a scan.
        ///
        /// <para>
        /// Driven from the frame's own musical position rather than a wall clock, for two reasons.
        /// The arcade's loop is a property of the music, so a note pulsing off the beat is wrong in
        /// a way an author will notice; and a clock-driven loop would make the playfield animate
        /// while the transport is stopped, and would put a nondeterministic value into every
        /// rendered frame - which the pixel tests diff against fixed baselines.
        /// </para>
        /// </summary>
        private const double ShineLoopsPerScan = 4.0;

        /// <summary>
        /// Phase at which the next scan's notes go Active and its scanline appears. The system
        /// map's note lifecycle puts the handover at 87.5% of the current scan, and the projector
        /// already flips note state there, so the scanline has to agree or the two halves will
        /// disagree about which one is live.
        /// </summary>
        private const double HandoverPhase = 0.875;

        /// <summary>What <see cref="LinkFamily"/> answers: the two families the arcade joins, and neither.</summary>
        private const int NoFamily = 0;
        private const int ChainFamily = 1;
        private const int RepeatFamily = 2;

        /// <summary>
        /// The direction a run head's art is drawn pointing, in degrees clockwise from +x.
        ///
        /// <para>
        /// Measured off the sprite rather than chosen: the arcade's <c>notepressstart</c> is a
        /// left-pointing arrow, and left is where a chain goes on the lower half, where the sweep
        /// runs right to left. That one authored direction is correct for exactly one of the two
        /// halves and only for a chain that stays in its lane, so anything else has to turn it.
        /// </para>
        /// </summary>
        private const double HeadArtDegrees = 180.0;

        private readonly DrawingVisual _chrome = new DrawingVisual();
        private readonly DrawingVisual _field = new DrawingVisual();
        private readonly VisualCollection _layers;
        private readonly TechnikaPlayfieldTheme _theme = TechnikaPlayfieldTheme.Default;
        private readonly TechnikaNoteSprites _sprites = TechnikaNoteSprites.Load();

        /// <summary>
        /// Tiling brushes for the bars that join a run's notes, keyed by kind and by the height they
        /// were built to fill. Kept because a single frame can carry several runs of one kind, and
        /// keyed by height as well because the two halves of the playfield have different lane
        /// heights - a one-slot cache would rebuild on every note. Emptied when a resize or a
        /// different lane count makes every height in it stale.
        /// </summary>
        private readonly Dictionary<(GameplayPreviewNoteKind Kind, double Height), ImageBrush>
            _lineBrushes = new Dictionary<(GameplayPreviewNoteKind, double), ImageBrush>();

        /// <summary>
        /// Rotation in degrees for the note at the same index in the frame's note list: filled by
        /// the link pass, read by the note pass, zero for every note that is not a chain head.
        ///
        /// <para>
        /// It lives here rather than being worked out per note because the angle is a property of
        /// the run, not of the note - a head points at the member it is joined to, and only the walk
        /// that finds the run knows which that is. Reused between frames rather than reallocated:
        /// this is the pumped path.
        /// </para>
        /// </summary>
        private double[] _headAngles = new double[0];

        private bool _showGuides;

        private GameplayPreviewProjection _projection;
        private GameplayPreviewFrame _frame;
        private TechnikaPlayfieldFit _fit;
        private TextCache _labels;
        private double _labelDpi;
        private int _chromeLaneCount = -1;
        private Size _chromeSize;
        private int _tracedScan = int.MinValue;

        public TechnikaPlayfieldView()
        {
            _layers = new VisualCollection(this);
            _layers.Add(_chrome);
            _layers.Add(_field);

            // Chrome is axis-aligned rules and plates; aliasing them is what keeps a 1 px lane
            // separator a line instead of a grey smear. The field is notes and a gradient wash,
            // which need antialiasing, so it keeps the default.
            RenderOptions.SetEdgeMode(_chrome, EdgeMode.Aliased);

            ClipToBounds = true;
            Focusable = false;
            IsHitTestVisible = false;
        }

        /// <summary>The projection's own status line, for the panel header. Null when unbound.</summary>
        public string StatusLabel
        {
            get { return _projection == null ? null : _projection.StatusLabel; }
        }

        /// <summary>Whether note glyphs are coming from the owner's arcade sprites or the packaged set.</summary>
        public string SpriteSourceLabel
        {
            get { return _sprites.SourceLabel; }
        }

        /// <summary>
        /// Whether the parts of the chrome that exist to help someone read the layout are drawn:
        /// the two verticals at a scan's start and end, the lane centre rules, and the HUD cells
        /// and indicator lights of the header.
        ///
        /// <para>
        /// Off by default, because none of it is gameplay. The arcade's header carries a live
        /// groove gauge, a score and a combo; ours carries four empty plates and two words, which
        /// on screen reads as an editor inspecting a chart rather than as a machine playing one.
        /// The scan margins are the clearest case: they are a measurement drawn over the field in
        /// accent green, useful when checking that a note late in its scan is late rather than
        /// mislaned, and never present in front of a player.
        /// </para>
        ///
        /// <para>
        /// What stays either way is what the arcade itself draws: the backdrop, the two field
        /// halves, the side rails, the lane separators, the divider, and the header plate the
        /// arcade fills with its own readouts.
        /// </para>
        /// </summary>
        public bool ShowMeasurementGuides
        {
            get { return _showGuides; }
            set
            {
                if (_showGuides == value)
                {
                    return;
                }
                _showGuides = value;
                _chromeLaneCount = -1;
                InvalidateVisual();
            }
        }

        /// <summary>True once a TECHNIKA projection is bound and there is something to draw.</summary>
        public bool HasPlayfield
        {
            get
            {
                return _projection != null &&
                    _projection.Profile == GameplayPreviewProfile.Technika;
            }
        }

        /// <summary>
        /// Adopts a projection. A Generic projection is accepted and reported, but not drawn -
        /// that profile is the vertical-scrolling layout the studio's main canvas already shows,
        /// and duplicating it here would say nothing new.
        /// </summary>
        public void Bind(GameplayPreviewProjection projection)
        {
            _projection = projection;
            _frame = null;
            _chromeLaneCount = -1;
            _tracedScan = int.MinValue;
            LogProjection();
            InvalidateVisual();
        }

        public void Unbind()
        {
            _projection = null;
            _frame = null;
            _chromeLaneCount = -1;
            InvalidateVisual();
        }

        /// <summary>
        /// Moves the playfield to a playhead position, given in the editor's virtual tick space.
        ///
        /// <para>
        /// Called from the render pump once per changed tick, so it does the least possible work:
        /// it builds the projector's renderable window - this scan and the next, nothing else -
        /// and redraws only the note layer.
        /// </para>
        /// </summary>
        public void Sync(int playheadVirtualTick)
        {
            if (!HasPlayfield)
            {
                return;
            }

            int tick = playheadVirtualTick / EventData.VirtualTickSize;
            _frame = _projection.CreateRenderableFrame(tick);
            RedrawField();
        }

        // -----------------------------------------------------------------------------------
        // Element plumbing
        // -----------------------------------------------------------------------------------

        protected override int VisualChildrenCount
        {
            get { return _layers.Count; }
        }

        protected override Visual GetVisualChild(int index)
        {
            return _layers[index];
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            // The element declares the arcade's own aspect rather than filling whatever it is
            // given. Hosted in an auto-sized row it then comes out exactly as tall as it is wide
            // over 1280:768, so the panel never shows letterbox bars - and a host that constrains
            // both axes still gets a fit, because Fit() letterboxes whatever it is handed.
            const double Aspect =
                TechnikaPlayfieldMetrics.NativeHeight / TechnikaPlayfieldMetrics.NativeWidth;

            bool freeWidth = double.IsInfinity(availableSize.Width);
            bool freeHeight = double.IsInfinity(availableSize.Height);

            if (freeWidth && freeHeight)
            {
                return new Size(TechnikaPlayfieldMetrics.NativeWidth / 2.0,
                    TechnikaPlayfieldMetrics.NativeHeight / 2.0);
            }
            if (freeWidth)
            {
                return new Size(availableSize.Height / Aspect, availableSize.Height);
            }

            double height = availableSize.Width * Aspect;
            if (!freeHeight && height > availableSize.Height)
            {
                height = availableSize.Height;
            }
            return new Size(availableSize.Width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            InvalidateVisual();
            return finalSize;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            _fit = TechnikaPlayfieldMetrics.Fit(new Size(ActualWidth, ActualHeight));
            RedrawChrome();
            RedrawField();
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            _labels = null;
            InvalidateVisual();
        }

        // -----------------------------------------------------------------------------------
        // Chrome
        // -----------------------------------------------------------------------------------

        private void RedrawChrome()
        {
            int laneCount = _projection == null ? 0 : _projection.LaneCount;
            Size size = new Size(ActualWidth, ActualHeight);

            // The run-line brushes are built to fill one exact height. A resize or a chart with a
            // different lane count changes every one of those heights, so the brushes already held
            // will never be asked for again; this is the one place both inputs are known.
            if (size != _chromeSize || laneCount != _chromeLaneCount)
            {
                _lineBrushes.Clear();
            }

            _chromeSize = size;
            _chromeLaneCount = laneCount;

            using (DrawingContext dc = _chrome.RenderOpen())
            {
                if (size.Width <= 0 || size.Height <= 0)
                {
                    return;
                }

                dc.DrawRectangle(_theme.Backdrop, null, new Rect(size));

                if (!_fit.IsUsable)
                {
                    return;
                }

                if (!HasPlayfield)
                {
                    DrawPlaceholder(dc, size);
                    return;
                }

                DrawHalf(dc, true, laneCount);
                DrawHalf(dc, false, laneCount);
                DrawDivider(dc);
                DrawHeader(dc);
            }
        }

        private void DrawHalf(DrawingContext dc, bool isTopHalf, int laneCount)
        {
            Rect half = _fit.Half(isTopHalf);
            dc.DrawRectangle(_theme.FieldFor(isTopHalf), null, half);

            // Side rails. The arcade's are 40 px sprites (note\Common\sidebar.png) at each edge
            // of each half; they are the frame the touch strip sits in.
            double rail = _fit.Length(TechnikaPlayfieldMetrics.SidebarWidth);
            if (rail >= 1)
            {
                dc.DrawRectangle(_theme.SidebarFill, null,
                    new Rect(half.Left, half.Top, rail, half.Height));
                dc.DrawRectangle(_theme.SidebarFill, null,
                    new Rect(half.Right - rail, half.Top, rail, half.Height));
            }

            if (laneCount < 1)
            {
                return;
            }

            // Lane separators. The stack occupies the middle 90% of the half, which is the space
            // the projector's localY runs over.
            double inset = TechnikaPlayfieldMetrics.LaneInset;
            for (int lane = 0; lane <= laneCount; lane++)
            {
                double localY = inset + ((1.0 - (2.0 * inset)) * lane / laneCount);
                double y = Snap(half.Top + (localY * half.Height));
                dc.DrawLine(_theme.LaneRule, new Point(half.Left, y), new Point(half.Right, y));
            }

            if (!_showGuides)
            {
                return;
            }

            for (int lane = 0; lane < laneCount; lane++)
            {
                double localY = inset + ((1.0 - (2.0 * inset)) * (lane + 0.5) / laneCount);
                double y = Snap(half.Top + (localY * half.Height));
                dc.DrawLine(_theme.LaneCenterRule,
                    new Point(half.Left + rail, y), new Point(half.Right - rail, y));
            }

            // Where a scan starts and ends. Marking them is the fastest way to see that a note at
            // the far right of the upper half is late in its scan, not in a different lane.
            double startX = Snap(_fit.X(
                TechnikaPlayfieldMetrics.NoteMarginLeft * TechnikaPlayfieldMetrics.NativeWidth));
            double endX = Snap(_fit.X(
                TechnikaPlayfieldMetrics.NoteMarginRight * TechnikaPlayfieldMetrics.NativeWidth));
            dc.DrawLine(_theme.MarginRule, new Point(startX, half.Top), new Point(startX, half.Bottom));
            dc.DrawLine(_theme.MarginRule, new Point(endX, half.Top), new Point(endX, half.Bottom));
        }

        private void DrawDivider(DrawingContext dc)
        {
            // The sprite is a 1 px black rule, a 5 px core and another black rule; only the core
            // is coloured, and only the core is what the eye reads as "the divider".
            Rect core = _fit.Quad(
                0,
                TechnikaPlayfieldMetrics.DividerCoreTop,
                TechnikaPlayfieldMetrics.NativeWidth,
                TechnikaPlayfieldMetrics.DividerCoreHeight);
            dc.DrawRectangle(_theme.DividerCore, null, core);
            dc.DrawLine(_theme.DividerEdge,
                new Point(core.Left, Snap(core.Top)), new Point(core.Right, Snap(core.Top)));
            dc.DrawLine(_theme.DividerEdge,
                new Point(core.Left, Snap(core.Bottom)), new Point(core.Right, Snap(core.Bottom)));
        }

        private void DrawHeader(DrawingContext dc)
        {
            Rect header = _fit.Quad(
                0, 0, TechnikaPlayfieldMetrics.NativeWidth, TechnikaPlayfieldMetrics.HeaderHeight);
            dc.DrawRectangle(_theme.HeaderFill, null, header);
            dc.DrawLine(_theme.HeaderEdge,
                new Point(header.Left, Snap(header.Bottom)),
                new Point(header.Right, Snap(header.Bottom)));

            if (!_showGuides)
            {
                // The plate on its own. The arcade's readouts are live values this preview has
                // nothing to put in, and empty cells labelled GROOVE and SCORE are worse than a
                // clean band: they say the machine is broken rather than that it is not scoring.
                return;
            }

            EnsureLabels();

            // The four indicator lights. Their measured quads are 114x94 - taller than the 72 px
            // header - so they overhang it in the arcade too; clipping to the header is what the
            // runtime effectively does with the header plate drawn over them.
            dc.PushClip(new RectangleGeometry(header));
            for (int i = 0; i < TechnikaPlayfieldMetrics.HeaderLightLeft.Length; i++)
            {
                dc.DrawRectangle(null, _theme.HudEdge, _fit.Quad(
                    TechnikaPlayfieldMetrics.HeaderLightLeft[i],
                    0,
                    TechnikaPlayfieldMetrics.HeaderLightWidth,
                    TechnikaPlayfieldMetrics.HeaderLightHeight));
            }
            dc.Pop();

            DrawHudCell(dc, "GROOVE",
                TechnikaPlayfieldMetrics.GrooveLeft, TechnikaPlayfieldMetrics.GrooveTop,
                TechnikaPlayfieldMetrics.GrooveWidth, TechnikaPlayfieldMetrics.GrooveHeight);
            DrawHudCell(dc, "SCORE",
                TechnikaPlayfieldMetrics.ScoreDigitLeft, TechnikaPlayfieldMetrics.ScoreDigitTop,
                TechnikaPlayfieldMetrics.ScoreDigitWidth, TechnikaPlayfieldMetrics.ScoreDigitHeight);
            DrawHudCell(dc, null,
                TechnikaPlayfieldMetrics.ComboDigitLeft, TechnikaPlayfieldMetrics.ComboDigitTop,
                TechnikaPlayfieldMetrics.ComboDigitWidth, TechnikaPlayfieldMetrics.ComboDigitHeight);
            DrawHudCell(dc, null,
                TechnikaPlayfieldMetrics.GuideSlotLeft, TechnikaPlayfieldMetrics.GuideSlotTop,
                TechnikaPlayfieldMetrics.GuideSlotWidth, TechnikaPlayfieldMetrics.GuideSlotHeight);
        }

        private void DrawHudCell(
            DrawingContext dc, string label, double left, double top, double width, double height)
        {
            Rect cell = _fit.Quad(left, top, width, height);
            if (cell.Width < 1 || cell.Height < 1)
            {
                return;
            }
            dc.DrawRectangle(_theme.HudFill, _theme.HudEdge, cell);

            if (label == null)
            {
                return;
            }
            FormattedText text = _labels.Get(label);
            if (text == null || text.Width > cell.Width - 4 || text.Height > cell.Height)
            {
                return;
            }
            dc.DrawText(text, new Point(
                cell.Left + 3, cell.Top + ((cell.Height - text.Height) / 2)));
        }

        private void DrawPlaceholder(DrawingContext dc, Size size)
        {
            EnsureLabels();
            string message = _projection == null
                ? "NO CHART LOADED"
                : "NOT A TECHNIKA CHART";
            FormattedText text = _labels.Get(message);
            if (text == null)
            {
                return;
            }
            dc.DrawText(text, new Point(
                Math.Max(0, (size.Width - text.Width) / 2),
                Math.Max(0, (size.Height - text.Height) / 2)));
        }

        // -----------------------------------------------------------------------------------
        // Field
        // -----------------------------------------------------------------------------------

        private void RedrawField()
        {
            using (DrawingContext dc = _field.RenderOpen())
            {
                if (!_fit.IsUsable || !HasPlayfield || _frame == null)
                {
                    return;
                }

                // A resize or a chart change while playing: rebuild the chrome from the pump
                // rather than waiting for a layout pass, so the two layers never disagree.
                if (_chromeLaneCount != _projection.LaneCount ||
                    _chromeSize.Width != ActualWidth || _chromeSize.Height != ActualHeight)
                {
                    RedrawChrome();
                }

                DrawScanlines(dc);

                IReadOnlyList<ProjectedGameplayNote> notes = _frame.Notes;
                TraceFrame(notes);

                // Links first, and not only so the lines pass under the heads: the walk that finds
                // the runs is also what tells a chain head which way its arrow points, and it fills
                // _headAngles for the loop below.
                DrawGroupLinks(dc, notes);
                for (int i = 0; i < notes.Count; i++)
                {
                    DrawNote(dc, notes[i], _headAngles[i]);
                }
            }
        }

        /// <summary>
        /// One line per scan boundary describing what the projector handed over and where the
        /// first note of the window landed. Scan-gated rather than per-tick: at sixty frames a
        /// second an unthrottled trace is a write amplifier, not a diagnostic.
        /// </summary>
        private void TraceFrame(IReadOnlyList<ProjectedGameplayNote> notes)
        {
            if (_frame.CurrentIntScan == _tracedScan)
            {
                return;
            }

            _tracedScan = _frame.CurrentIntScan;
            string first = "-";
            if (notes.Count > 0)
            {
                ProjectedGameplayNote note = notes[0];
                Point center = _fit.Note(note.X, note.Y, note.IsTopHalf);
                first = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0} lane={1} scan={2} x={3:F3} y={4:F3} top={5} state={6} -> ({7:F1},{8:F1})",
                    note.Kind, note.Lane, note.ScanIndex, note.X, note.Y,
                    note.IsTopHalf, note.State, center.X, center.Y);
            }

            DiagnosticLog.Write("technika.frame", string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "tick={0} scan={1} phase={2:F3} window={3} fit={4:F3}@{5:F0},{6:F0} first={7}",
                _frame.CurrentTick, _frame.CurrentIntScan, _frame.CurrentPhase,
                notes.Count, _fit.Scale, _fit.OffsetX, _fit.OffsetY, first));
        }

        /// <summary>
        /// Summarises a freshly bound projection: how many notes, what scan and lane range they
        /// cover, and which kinds appear. Cheap - once per chart open - and it is the difference
        /// between "the playfield is empty" and "the chart has nothing in the first two scans".
        /// </summary>
        private void LogProjection()
        {
            if (!HasPlayfield)
            {
                return;
            }

            IReadOnlyList<ProjectedGameplayNote> notes = _projection.Notes;
            if (notes.Count == 0)
            {
                DiagnosticLog.Write("technika.projection", "lanes=" +
                    _projection.LaneCount + " notes=0");
                return;
            }

            int minScan = int.MaxValue, maxScan = int.MinValue;
            int minLane = int.MaxValue, maxLane = int.MinValue;
            int holds = 0;
            var kinds = new Dictionary<GameplayPreviewNoteKind, int>();
            for (int i = 0; i < notes.Count; i++)
            {
                ProjectedGameplayNote note = notes[i];
                if (note.ScanIndex < minScan) { minScan = note.ScanIndex; }
                if (note.ScanIndex > maxScan) { maxScan = note.ScanIndex; }
                if (note.Lane < minLane) { minLane = note.Lane; }
                if (note.Lane > maxLane) { maxLane = note.Lane; }
                if (note.DurationPulse > 0) { holds++; }
                int seen;
                kinds.TryGetValue(note.Kind, out seen);
                kinds[note.Kind] = seen + 1;
            }

            var summary = new System.Text.StringBuilder();
            summary.Append("lanes=").Append(_projection.LaneCount);
            summary.Append(" notes=").Append(notes.Count);
            summary.Append(" scans=").Append(minScan).Append("..").Append(maxScan);
            summary.Append(" laneRange=").Append(minLane).Append("..").Append(maxLane);
            summary.Append(" holds=").Append(holds).Append(" kinds=");
            foreach (KeyValuePair<GameplayPreviewNoteKind, int> pair in kinds)
            {
                summary.Append(pair.Key).Append(':').Append(pair.Value).Append(' ');
            }

            DiagnosticLog.Write("technika.projection", summary.ToString());
        }

        private void DrawScanlines(DrawingContext dc)
        {
            bool currentIsTop = (_frame.CurrentIntScan & 1) == 1;
            DrawScanline(dc, currentIsTop, _frame.CurrentPhase);

            // During the handover the arcade shows both: the outgoing scan finishing its sweep
            // and the incoming one already parked at its start. The projector flips note state at
            // the same phase, so this is the visual half of one rule, not a second one.
            if (_frame.CurrentPhase >= HandoverPhase)
            {
                DrawScanline(dc, !currentIsTop, 0.0);
            }
        }

        private void DrawScanline(DrawingContext dc, bool isTopHalf, double phase)
        {
            // The hit point, from the same margins the projector places notes with. Both halves
            // sweep the same rectangle - the lower one backwards - exactly as PlaceTechnikaNote
            // runs it backwards rather than reflecting it.
            double left = TechnikaPlayfieldMetrics.NoteMarginLeft;
            double right = TechnikaPlayfieldMetrics.NoteMarginRight;
            double travel = (right - left) * phase;
            double normalizedX = isTopHalf ? left + travel : right - travel;
            double edgeX = normalizedX * TechnikaPlayfieldMetrics.NativeWidth;

            // Place the quad so its bright edge lands on the hit point. The wash trails behind,
            // which is why the offset flips with the sweep direction.
            double quadLeft = isTopHalf
                ? edgeX - TechnikaPlayfieldMetrics.ScanlineLeadingEdgeOffset
                : edgeX - (TechnikaPlayfieldMetrics.ScanlineWidth -
                    TechnikaPlayfieldMetrics.ScanlineLeadingEdgeOffset);

            Rect quad = _fit.Quad(
                quadLeft,
                isTopHalf
                    ? TechnikaPlayfieldMetrics.ScanlineUpperTop
                    : TechnikaPlayfieldMetrics.ScanlineLowerTop,
                TechnikaPlayfieldMetrics.ScanlineWidth,
                TechnikaPlayfieldMetrics.ScanlineHeight);

            // The quad is taller than its half and deliberately overhangs; clip it so the upper
            // sweep does not bleed across the divider into the lower field.
            Rect half = _fit.Half(isTopHalf);
            dc.PushClip(new RectangleGeometry(half));
            dc.DrawRectangle(
                isTopHalf ? _theme.ScanlineForward : _theme.ScanlineReverse, null, quad);
            double edge = _fit.X(edgeX);
            dc.DrawLine(_theme.ScanlineCore,
                new Point(edge, half.Top), new Point(edge, half.Bottom));
            dc.Pop();
        }

        /// <summary>
        /// One note. <paramref name="angleDegrees"/> is how far its glyph has to turn, which is
        /// non-zero only for the head of a chain - see <see cref="HeadArtDegrees"/>.
        /// </summary>
        private void DrawNote(DrawingContext dc, ProjectedGameplayNote note, double angleDegrees)
        {
            if (note.State == GameplayPreviewNoteState.Inactive ||
                note.State == GameplayPreviewNoteState.Resolved)
            {
                return;
            }

            int laneCount = Math.Max(1, _projection.LaneCount);
            double nativeSize = TechnikaPlayfieldMetrics.NoteFrameSize(laneCount);
            double size = Math.Max(4.0, _fit.Length(nativeSize));
            Point center = _fit.Note(note.X, note.Y, note.IsTopHalf);
            TechnikaNoteBrushes brushes = _theme.NoteFor(note.Kind);

            // The lane box is what a tap fills; every other glyph is authored against that same tap
            // and is a different size on purpose. See TechnikaNoteSprites.ReferenceFrameSize - drawing
            // them all into the lane box is what made the notes look like a set of mismatched skins.
            double headSize = Math.Max(4.0, size * _sprites.ScaleFor(note.Kind));

            bool prepare = note.State == GameplayPreviewNoteState.Prepare;
            if (prepare)
            {
                dc.PushOpacity(PrepareOpacity);
            }

            DrawTrail(dc, note, center, size, brushes);

            TechnikaNoteSprite sprite = _sprites.For(note.Kind);

            if (note.ApproachVisible)
            {
                DrawApproach(dc, note, center, size);
            }

            Rect head = new Rect(
                center.X - (headSize / 2), center.Y - (headSize / 2), headSize, headSize);
            if (sprite == null)
            {
                // Vector chrome stands in for art that is missing; it does not layer under art that
                // is present. The arcade glyphs carry their own glow inside their frame, and a
                // second one behind them reads as a smudge around the note rather than as light
                // coming off it. A filled ellipse still says where the note is and what kind it is,
                // which is the whole job of the fallback.
                dc.DrawEllipse(brushes.Glow, null, center, size * 0.72, size * 0.72);
                dc.DrawEllipse(brushes.Fill, brushes.Edge, center, size / 2, size / 2);
            }
            else
            {
                // A long note wears the arcade's held glyph while the playhead is inside its span,
                // which is the swap the arcade itself makes; without one in the loaded set the head
                // simply carries on.
                TechnikaNoteSprite drawn = HeldNow(note)
                    ? (_sprites.Held ?? sprite)
                    : sprite;

                // A chain head is an arrow, so it is the one glyph whose orientation carries
                // meaning: it says where the chain goes next, and a chain crosses lanes. Turning
                // the image is the whole of that - the vector fallback below is a disc, which has
                // no direction to be wrong about.
                bool turned = Math.Abs(angleDegrees) > 0.01;
                if (turned)
                {
                    dc.PushTransform(new RotateTransform(angleDegrees, center.X, center.Y));
                }
                dc.DrawImage(drawn.Frame(_frame.CurrentScan * ShineLoopsPerScan), head);
                if (turned)
                {
                    dc.Pop();
                }
            }

            if (prepare)
            {
                dc.Pop();
            }
        }

        /// <summary>
        /// The arcade's approach glow: a fixed piece of light the sweep drags through the note.
        ///
        /// <para>
        /// <c>note_circle</c> was being played as a swell - frame zero to its brightest across the
        /// half scan before the note was due - on the belief that it opens on the spot and closes
        /// again. Measuring it says otherwise: its light travels from one edge of the frame to the
        /// other and is only centred in the middle frames, so a swell drew nothing but the entering
        /// crescents, each one parked a third of a lane to the left of a note still most of a second
        /// away. That is the field of stray arcs.
        /// </para>
        ///
        /// <para>
        /// So it is asked for by position instead. How far the sweep is from the note is known in
        /// scans; a scan travels a known width; the glow is a known number of frame widths across.
        /// Those three give the offset in the strip's own units, and the strip hands back the frame
        /// that has its light there - or nothing, which is the answer for most of the approach and is
        /// why the glow now reads as a pass rather than as clutter. Mirrored on the lower half, where
        /// the sweep runs the other way and the light has to arrive from the other side.
        /// </para>
        /// </summary>
        private void DrawApproach(
            DrawingContext dc,
            ProjectedGameplayNote note,
            Point center,
            double size)
        {
            TechnikaNoteSprite ring = _sprites.Ring;
            if (ring == null)
            {
                // No art in the loaded set: a converging circle says the same thing at lower
                // fidelity, and here the fade is worth keeping because a plain outline has no
                // brightness of its own to carry the arrival.
                dc.PushOpacity(0.30 + (0.70 * note.ApproachProgress));
                double radius = size * (0.5 + (0.45 * (1.0 - note.ApproachProgress)));
                dc.DrawEllipse(null, _theme.ApproachRing, center, radius, radius);
                dc.Pop();
                return;
            }

            double reference = _sprites.ReferenceFrameSize;
            double scale = reference > 0 && ring.FrameSize > 0 ? ring.FrameSize / reference : 1.0;
            double edge = size * scale;
            if (edge <= 0.0)
            {
                return;
            }

            // Native pixels rather than screen: the ratio is what matters and the arcade's own
            // numbers are the ones the sweep width was measured in.
            double scanTravel = (TechnikaPlayfieldMetrics.NoteMarginRight -
                TechnikaPlayfieldMetrics.NoteMarginLeft) * TechnikaPlayfieldMetrics.NativeWidth;
            double glowNative = TechnikaPlayfieldMetrics.NoteFrameSize(
                Math.Max(1, _projection.LaneCount)) * scale;
            if (glowNative <= 0.0 || scanTravel <= 0.0)
            {
                return;
            }

            double offset = note.ApproachScanDistance * scanTravel / glowNative;
            ImageSource frame = ring.Sweep(note.IsTopHalf ? offset : -offset);
            if (frame == null)
            {
                return;
            }

            bool mirrored = !note.IsTopHalf;
            if (mirrored)
            {
                dc.PushTransform(new ScaleTransform(-1.0, 1.0, center.X, center.Y));
            }
            dc.DrawImage(frame, new Rect(
                center.X - (edge / 2), center.Y - (edge / 2), edge, edge));
            if (mirrored)
            {
                dc.Pop();
            }
        }

        /// <summary>
        /// The bar the arcade runs through a chain or a repeat run.
        ///
        /// <para>
        /// A chain and a repeat run are several notes joined by one line, not one note carrying a
        /// duration - which is why <c>HasHoldTrail</c> excludes them, and why the line cannot be
        /// derived from any single note the way a hold's trail can. It is read off the run instead:
        /// consecutive notes of one family in one scan and one half, a repeat series within a lane
        /// and a chain across them, joined from each member to the next. Drawn before the heads, so
        /// the line passes under them as it does in the arcade.
        /// </para>
        /// </summary>
        private void DrawGroupLinks(DrawingContext dc, IReadOnlyList<ProjectedGameplayNote> notes)
        {
            PrepareHeadAngles(notes.Count);

            int start = 0;
            while (start < notes.Count)
            {
                int end = start;
                if (LinkFamily(notes[start].Kind) != NoFamily)
                {
                    while (end + 1 < notes.Count && ContinuesRun(notes[start], notes[end + 1]))
                    {
                        end++;
                    }
                    DrawGroupLink(dc, notes, start, end);
                }
                start = end + 1;
            }
        }

        /// <summary>Clears the frame's head angles, growing the buffer only when a frame is busier
        /// than any before it.</summary>
        private void PrepareHeadAngles(int count)
        {
            if (_headAngles.Length < count)
            {
                _headAngles = new double[Math.Max(count, 64)];
            }
            for (int i = 0; i < count; i++)
            {
                _headAngles[i] = 0.0;
            }
        }

        /// <summary>
        /// Joins the drawable members of one run, and tells its head which way to point. Skipping
        /// the members the renderer itself skips is what keeps a line from hanging off a head that
        /// has already been played while its tail notes are still on screen.
        /// </summary>
        private void DrawGroupLink(
            DrawingContext dc, IReadOnlyList<ProjectedGameplayNote> notes, int start, int end)
        {
            int first = -1;
            int last = -1;
            for (int i = start; i <= end; i++)
            {
                if (notes[i].State == GameplayPreviewNoteState.Inactive ||
                    notes[i].State == GameplayPreviewNoteState.Resolved)
                {
                    continue;
                }
                if (first < 0) { first = i; }
                last = i;
            }
            if (first < 0 || last == first)
            {
                return;
            }

            ProjectedGameplayNote from = notes[first];
            int laneCount = Math.Max(1, _projection.LaneCount);
            double size = Math.Max(4.0, _fit.Length(
                TechnikaPlayfieldMetrics.NoteFrameSize(laneCount)));

            bool prepare = from.State == GameplayPreviewNoteState.Prepare;
            if (prepare)
            {
                dc.PushOpacity(PrepareOpacity);
            }

            if (LinkFamily(from.Kind) == ChainFamily)
            {
                DrawChainLink(dc, notes, start, end, first, size);
            }
            else
            {
                // A repeat series lives in one lane, so its members are collinear and one bar from
                // the first to the last is the whole line - and drawing it in one piece is what
                // keeps the pitch of `noterepeatline`'s pips continuous across the run.
                Point a = Centre(from);
                Point b = Centre(notes[last]);
                DrawRunBar(dc, from.Kind, new Point(Math.Min(a.X, b.X), a.Y),
                    Math.Abs(b.X - a.X), 0.0, size);
            }

            if (prepare)
            {
                dc.Pop();
            }
        }

        /// <summary>
        /// A chain, joined member to member, and its head turned to face the second member.
        ///
        /// <para>
        /// Not one bar from the first member to the last, the way a repeat run is drawn: the
        /// projector's chain pass runs one open flag over the whole chart and absorbs whatever taps
        /// fall inside the span, whichever lane they are in, so a chain is a path across lanes and
        /// its members are collinear only by accident. A single bar between the ends would leave
        /// every note in between sitting off the line.
        /// </para>
        /// </summary>
        private void DrawChainLink(
            DrawingContext dc,
            IReadOnlyList<ProjectedGameplayNote> notes,
            int start,
            int end,
            int head,
            double size)
        {
            int previous = -1;
            for (int i = start; i <= end; i++)
            {
                ProjectedGameplayNote note = notes[i];
                if (note.State == GameplayPreviewNoteState.Inactive ||
                    note.State == GameplayPreviewNoteState.Resolved)
                {
                    continue;
                }

                if (previous >= 0)
                {
                    Point a = Centre(notes[previous]);
                    Point b = Centre(note);
                    DrawRunBar(dc, notes[previous].Kind, a, Distance(a, b), Bearing(a, b), size);

                    // The arrow answers "where does this chain go", so it is aimed at the member
                    // the head is actually joined to rather than along the sweep.
                    if (previous == head && IsRunHead(notes[head].Kind))
                    {
                        _headAngles[head] = Bearing(a, b) - HeadArtDegrees;
                    }
                }
                previous = i;
            }
        }

        /// <summary>
        /// One straight piece of a run's line: the art tiled from <paramref name="origin"/> along
        /// <paramref name="length"/>, turned by <paramref name="degrees"/> and centred on the line
        /// it joins. Both families draw through this - a repeat run passes its whole horizontal
        /// span, a chain passes each of its segments.
        /// </summary>
        private void DrawRunBar(
            DrawingContext dc,
            GameplayPreviewNoteKind kind,
            Point origin,
            double length,
            double degrees,
            double size)
        {
            if (length < 1)
            {
                return;
            }

            TechnikaNoteSprite line = _sprites.Line(kind);

            // The line is authored against the head it joins - 76 px of art around a 90 px note -
            // so it is scaled by the ratio the two sprites declare, exactly as the approach glow is.
            // Without art at all a themed bar joins the same two points at lower fidelity.
            double height = line == null
                ? size * 0.18
                : size * LineScale(_sprites.For(kind), line);

            // Rotate about the bar's own start rather than drawing a rotated rectangle: the tiling
            // brush below is axis-aligned by construction, so the art has to be laid out along +x
            // and then turned onto the segment. Composed in call order - centre the bar on the
            // line, turn it, then put it at the segment's start.
            Matrix placement = new Matrix();
            placement.Translate(0, -height / 2.0);
            placement.Rotate(degrees);
            placement.Translate(origin.X, origin.Y);

            dc.PushTransform(new MatrixTransform(placement));
            if (line == null)
            {
                dc.DrawRectangle(_theme.NoteFor(kind).Trail, null, new Rect(0, 0, length, height));
            }
            else
            {
                // Tiled, not stretched. `noterepeatline` is a row of ten pips rather than a solid
                // bar, so stretching it to the run's length changes the spacing between pips with
                // the length of the run - two repeat runs of different lengths would be drawn in
                // visibly different art. Tiling keeps the pitch the sprite was authored with, and
                // the tile phase starts at this bar's own origin, which is what lets the brush stay
                // frozen and shared.
                dc.DrawRectangle(LineBrush(kind, line, height), null, new Rect(0, 0, length, height));
            }
            dc.Pop();
        }

        /// <summary>How much bigger a run's line art is than the head it joins, per the sprites' own
        /// dimensions. A constant here would be wrong for any other note set.</summary>
        private static double LineScale(TechnikaNoteSprite head, TechnikaNoteSprite line)
        {
            return head != null && head.FrameSize > 0 && line != null && line.FrameSize > 0
                ? line.FrameSize / head.FrameSize
                : 1.0;
        }

        private Point Centre(ProjectedGameplayNote note)
        {
            return _fit.Note(note.X, note.Y, note.IsTopHalf);
        }

        /// <summary>Direction from one point to another, in degrees clockwise from +x - which is
        /// what a WPF rotation takes, in a coordinate system whose y grows downwards.</summary>
        private static double Bearing(Point from, Point to)
        {
            return Math.Atan2(to.Y - from.Y, to.X - from.X) * 180.0 / Math.PI;
        }

        private static double Distance(Point from, Point to)
        {
            double dx = to.X - from.X;
            double dy = to.Y - from.Y;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        /// <summary>
        /// A brush that repeats a run's line art along the run at the pitch it was authored with,
        /// filling a box <paramref name="height"/> tall.
        ///
        /// <para>
        /// The tile is one frame's own aspect taken at the height it is being drawn, so a pip stays
        /// as round as the artist drew it whatever the lane height works out to. The viewport is in
        /// absolute units and exactly as tall as the box it fills, which is what keeps the repeat
        /// horizontal - a viewport shorter than the box would tile downwards as well and stack a
        /// second row of pips inside the bar.
        /// </para>
        ///
        /// <para>
        /// An animated line is rebuilt every draw rather than cached, because a frozen brush holds
        /// the one frame it was built from. None of the arcade's line art is a strip, so that is a
        /// guard against a future note set rather than a cost paid now.
        /// </para>
        /// </summary>
        private ImageBrush LineBrush(
            GameplayPreviewNoteKind kind, TechnikaNoteSprite line, double height)
        {
            ImageSource frame = line.Frame(_frame.CurrentScan * ShineLoopsPerScan);
            double aspect = line.FrameSize > 0 ? line.FrameWidth / line.FrameSize : 1.0;
            double tile = Math.Max(1.0, height * aspect);

            if (line.FrameCount > 1)
            {
                return TiledBrush(frame, tile, height);
            }

            ImageBrush cached;
            if (_lineBrushes.TryGetValue((kind, height), out cached))
            {
                return cached;
            }

            ImageBrush brush = TiledBrush(frame, tile, height);
            _lineBrushes[(kind, height)] = brush;
            return brush;
        }

        private static ImageBrush TiledBrush(ImageSource frame, double tile, double height)
        {
            ImageBrush brush = new ImageBrush(frame);
            brush.TileMode = TileMode.Tile;
            brush.Stretch = Stretch.Fill;
            brush.ViewportUnits = BrushMappingMode.Absolute;
            brush.Viewport = new Rect(0, 0, tile, height);
            brush.Freeze();
            return brush;
        }

        /// <summary><see cref="NoFamily"/> for a kind the arcade does not join, otherwise which of
        /// the two families the kind belongs to.</summary>
        private static int LinkFamily(GameplayPreviewNoteKind kind)
        {
            switch (kind)
            {
                case GameplayPreviewNoteKind.ChainHead:
                case GameplayPreviewNoteKind.ChainNode:
                    return ChainFamily;
                case GameplayPreviewNoteKind.RepeatHead:
                case GameplayPreviewNoteKind.RepeatHeadHold:
                case GameplayPreviewNoteKind.Repeat:
                case GameplayPreviewNoteKind.RepeatHold:
                    return RepeatFamily;
                default:
                    return NoFamily;
            }
        }

        private static bool IsRunHead(GameplayPreviewNoteKind kind)
        {
            return kind == GameplayPreviewNoteKind.ChainHead ||
                kind == GameplayPreviewNoteKind.RepeatHead ||
                kind == GameplayPreviewNoteKind.RepeatHeadHold;
        }

        /// <summary>
        /// Whether a note belongs to the run another note started. A second head closes the run
        /// rather than extending it, so two runs back to back in one lane are two lines.
        /// </summary>
        private static bool ContinuesRun(ProjectedGameplayNote first, ProjectedGameplayNote next)
        {
            int family = LinkFamily(first.Kind);
            return LinkFamily(next.Kind) == family &&
                !IsRunHead(next.Kind) &&
                // A repeat series belongs to one lane - the projector tracks it per lane - but a
                // chain does not: its pass runs one open flag over the whole chart and turns
                // whatever taps fall inside the span into nodes, in whichever lane they sit.
                // Requiring one lane of a chain is what left every real chain unjoined, because a
                // chain that stays in its lane is the exception rather than the rule.
                (family == ChainFamily || next.Lane == first.Lane) &&
                next.IsTopHalf == first.IsTopHalf &&
                next.ScanIndex == first.ScanIndex;
        }

        /// <summary>
        /// True while the playhead is inside a long note's own span - the window the arcade swaps its
        /// head glyph for the held one over. Read from the frame's musical position, like everything
        /// else these notes animate from.
        /// </summary>
        private bool HeldNow(ProjectedGameplayNote note)
        {
            if (note.DurationPulse <= 0 ||
                !GameplayPreviewNoteKinds.HasHoldTrail(note.Kind))
            {
                return false;
            }

            double pulse = _frame.CurrentScan * PulsesPerScan;
            return pulse >= note.Pulse && pulse <= note.Pulse + note.DurationPulse;
        }

        private void DrawTrail(
            DrawingContext dc,
            ProjectedGameplayNote note,
            Point center,
            double size,
            TechnikaNoteBrushes brushes)
        {
            if (note.DurationPulse <= 0 ||
                !GameplayPreviewNoteKinds.HasHoldTrail(note.Kind))
            {
                return;
            }

            // A duration is a span of the scan, so it is a span of X - and it runs in the sweep
            // direction, which reverses between halves. Deriving it this way rather than from a
            // pixel constant is what keeps a hold the right length at any tempo or zoom.
            double span = note.DurationPulse / PulsesPerScan *
                (TechnikaPlayfieldMetrics.NoteMarginRight - TechnikaPlayfieldMetrics.NoteMarginLeft) *
                TechnikaPlayfieldMetrics.NativeWidth;
            double length = _fit.Length(span);
            if (length < 1)
            {
                return;
            }

            double thickness = size * 0.42;
            double left = note.IsTopHalf ? center.X : center.X - length;
            Rect trail = new Rect(left, center.Y - (thickness / 2), length, thickness);
            dc.DrawRectangle(brushes.Trail, null, trail);
        }

        private void EnsureLabels()
        {
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            if (_labels != null && Math.Abs(dpi - _labelDpi) < 0.001)
            {
                return;
            }
            _labelDpi = dpi;
            _labels = new TextCache(_theme.MonoTypeface, 9.0, _theme.HudLabel, dpi);
        }

        private static double Snap(double value)
        {
            return Math.Round(value) + 0.5;
        }
    }
}
