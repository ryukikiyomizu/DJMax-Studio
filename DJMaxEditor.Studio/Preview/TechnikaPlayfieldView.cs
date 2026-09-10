using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DJMaxEditor.Diagnostics;
using DJMaxEditor.DJMax;
using DJMaxEditor.Preview;
using DJMaxEditor.Studio.Timeline;

namespace DJMaxEditor.Studio.Preview
{
    /// <summary>
    /// The arcade's note-series effector: whether notes fade in as the sweep approaches or fade
    /// out before it reaches them. Fade In hides notes far ahead of the line, Fade Out hides
    /// them right where the line reads, and the 2 variants do it over roughly half the distance.
    /// The manuals name them FI / FI2 / FO / FO2; they are position-based, not speed-based, so
    /// the renderer derives opacity from each note's distance in scans, the same number the
    /// approach glow uses.
    /// </summary>
    internal enum TechnikaNoteFader
    {
        Off,
        FadeIn,
        FadeIn2,
        FadeOut,
        FadeOut2
    }

    /// <summary>
    /// The arcade's timeline-series effector. Blink flashes the sweep on for about half (Blink)
    /// or a quarter (Blink2) of the time; Blind removes it altogether, leaving memory play.
    /// The duty is taken off the musical clock rather than a wall clock, the same contract the
    /// note shine loop keeps, so a stopped transport holds a still, deterministic frame.
    /// </summary>
    internal enum TechnikaLineEffector
    {
        On,
        Blink,
        Blink2,
        Blind
    }

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
    internal sealed class TechnikaPlayfieldView : FrameworkElement, IGameplayPlayfieldView
    {
        /// <summary>
        /// Beats in one scan: the projector's <c>DefaultBeatsPerScan</c>, and the unit the countdown
        /// counts in. See <see cref="PulsesPerScan"/> for why it is duplicated here.
        /// </summary>
        private const double BeatsPerScan = 4.0;

        /// <summary>
        /// Pulses in one scan: <c>PulsesPerBeat * DefaultBeatsPerScan</c> from the projector.
        /// Duplicated rather than exposed because it is the projector's private calibration, and
        /// widening its API to share one constant would be the worse trade. If the projector ever
        /// gains a per-chart scan length, this becomes a property read off the projection.
        /// </summary>
        private const double PulsesPerScan = 240.0 * BeatsPerScan;

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

        /// <summary>
        /// Height of a countdown digit as a share of one half of the playfield. Policy, chosen large
        /// enough to read at a glance over the field behind it without covering a whole half. Lives
        /// here rather than in <see cref="TechnikaCountIn"/> because it is a fact about how this
        /// renderer places the digit, not about the count.
        /// </summary>
        private const double CountdownHeightShare = 0.42;

        /// <summary>What <see cref="LinkFamily"/> answers: the two families the arcade joins, and neither.</summary>
        private const int NoFamily = 0;
        private const int ChainFamily = 1;
        private const int RepeatFamily = 2;

        /// <summary>What <see cref="_runOf"/> holds for a note that belongs to no run.</summary>
        private const int NoRun = -1;

        /// <summary>
        /// The lane a chain's run is keyed on. A chain crosses lanes - the projector's pass absorbs
        /// whatever taps fall inside its span, in whichever lane they sit - so its identity cannot
        /// include one, while a repeat series belongs to a single lane and must.
        /// </summary>
        private const int AnyLane = -1;

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

        /// <summary>
        /// Which run the note at the same index in the frame's note list belongs to, or
        /// <see cref="NoRun"/> for one the arcade joins to nothing. Filled by
        /// <see cref="GroupRuns"/>, read by both link passes.
        ///
        /// <para>
        /// Gathering a run's members by identity rather than by position is the whole reason this
        /// exists. <c>GameplayPreviewProjection</c> orders its notes by pulse and then by lane, so
        /// the entry after a run's head is whatever else the chart plays at that moment - the other
        /// lane's repeat, a chain passing through - and hardly ever the next member. Walking
        /// neighbours instead, which is what this pass did, found a run of one on any chart where two
        /// lanes are busy at once, and a run of one has no line: the bar disappeared exactly when the
        /// chart was dense enough to need it. Single-lane fixtures cannot catch that, because in one
        /// lane list order and run order are the same order.
        /// </para>
        /// </summary>
        private int[] _runOf = new int[0];

        /// <summary>
        /// The runs <see cref="GroupRuns"/> still has open, and how much of the buffer is live. One
        /// per family, lane, half and scan in flight at once - a handful at most - so it is searched
        /// linearly rather than hashed.
        /// </summary>
        private OpenRun[] _openRuns = new OpenRun[8];
        private int _openRunCount;

        /// <summary>How many runs <see cref="GroupRuns"/> found in the frame being drawn.</summary>
        private int _runCount;

        private bool _showGuides;

        private TechnikaNoteFader _noteFader = TechnikaNoteFader.Off;
        private TechnikaLineEffector _lineEffector = TechnikaLineEffector.On;

        private GameplayPreviewProjection _projection;
        private GameplayPreviewFrame _frame;
        private TechnikaPlayfieldFit _fit;
        private TextCache _labels;
        private double _labelDpi;
        private TextCache _countdown;
        private double _countdownEm = -1.0;
        private double _countdownDpi;
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

        /// <summary>
        /// The note-series effector (fade in / fade out). Renderer state only: the arcade
        /// computes visibility from where the sweep is, which this view knows per note, so this
        /// never rebuilds the projection the way the scroll direction does.
        /// </summary>
        public TechnikaNoteFader NoteFader
        {
            get { return _noteFader; }
            set
            {
                if (_noteFader == value)
                {
                    return;
                }
                _noteFader = value;
                RedrawFieldIfReady();
            }
        }

        /// <summary>
        /// The timeline-series effector (blink / blind). Flashing is driven from the frame's
        /// phase, so changing this only repaints the field layer.
        /// </summary>
        public TechnikaLineEffector LineEffector
        {
            get { return _lineEffector; }
            set
            {
                if (_lineEffector == value)
                {
                    return;
                }
                _lineEffector = value;
                RedrawFieldIfReady();
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
            _countdown = null;
            InvalidateVisual();
        }

        // -----------------------------------------------------------------------------------
        // Chrome
        // -----------------------------------------------------------------------------------

        private void RedrawChrome()
        {
            int laneCount = _projection == null ? 0 : _projection.LaneCount;
            Size size = new Size(ActualWidth, ActualHeight);

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

                // Last, so a burst reads as light in front of the field rather than something
                // buried in it - and after the heads because the note it belongs to is already
                // gone by the time it is at its brightest.
                DrawHitEffects(dc, notes);

                // Over everything, because a count-in is the one thing on screen that is not part of
                // the chart.
                DrawCountdown(dc);
            }
        }

        /// <summary>
        /// The count-in before the chart starts: 3, 2, 1, one to a beat, centred on the boundary
        /// between the halves.
        ///
        /// <para>
        /// Which number and how bright belong to <see cref="TechnikaCountIn"/>, where they can be
        /// walked without a render target; what is here is only where it goes and how large. Drawn as
        /// text because there is no countdown art in the extracted set - the arcade's own count-in is
        /// part of a stage intro this preview does not model.
        /// </para>
        ///
        /// <para>
        /// Counted in beats off the musical clock rather than in seconds off a wall clock, so
        /// scrubbing the transport back into the count-in shows the count-in again, and so the digits
        /// land on the beats an author hears. It measures from the first note in the whole chart, not
        /// the first note of the current window: the point is to say when play begins, and that is a
        /// fact about the chart. Silent from that note onwards, and silent for a chart with no notes.
        /// </para>
        /// </summary>
        private void DrawCountdown(DrawingContext dc)
        {
            double firstScan;
            if (!FirstNoteScan(out firstScan))
            {
                return;
            }

            TechnikaCountIn count =
                TechnikaCountIn.At((firstScan - _frame.CurrentScan) * BeatsPerScan);
            if (!count.IsVisible)
            {
                return;
            }

            double em = _fit.Length(
                TechnikaPlayfieldMetrics.MeanHalfHeight * CountdownHeightShare);
            EnsureCountdown(em);
            FormattedText text =
                _countdown.Get(count.Number.ToString(CultureInfo.InvariantCulture));
            if (text == null)
            {
                return;
            }

            Point centre = new Point(
                _fit.X(TechnikaPlayfieldMetrics.NativeWidth / 2.0),
                _fit.Y(TechnikaPlayfieldMetrics.HalfBoundary));
            dc.PushOpacity(count.Opacity);
            dc.DrawText(text, new Point(
                centre.X - (text.Width / 2.0), centre.Y - (text.Height / 2.0)));
            dc.Pop();
        }

        /// <summary>
        /// Scan position of the earliest note in the chart, or false when the chart has none. The
        /// projector hands its notes over in pulse order, so this is the first one; taken as a double
        /// scan because the countdown has to know where inside a beat the note falls.
        /// </summary>
        private bool FirstNoteScan(out double scan)
        {
            scan = 0.0;
            IReadOnlyList<ProjectedGameplayNote> all = _projection.Notes;
            if (all == null || all.Count == 0)
            {
                return false;
            }
            scan = all[0].Pulse / PulsesPerScan;
            return true;
        }

        private void EnsureCountdown(double em)
        {
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            if (_countdown != null &&
                Math.Abs(dpi - _countdownDpi) < 0.001 &&
                Math.Abs(em - _countdownEm) < 0.5)
            {
                return;
            }
            _countdownDpi = dpi;
            _countdownEm = em;
            _countdown = new TextCache(
                _theme.UiTypeface, Math.Max(1.0, em), _theme.TextPrimary, dpi);
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
            // Blind draws no sweep at all; Blink gates it on the musical phase. Both lines in a
            // handover share that phase, so they flash together rather than independently.
            if (_lineEffector == TechnikaLineEffector.Blind)
            {
                return;
            }
            bool visible = LineVisibleAt(_frame.CurrentPhase);
            if (!visible && _frame.CurrentPhase < HandoverPhase)
            {
                return;
            }

            bool currentIsTop = (_frame.CurrentIntScan & 1) == 1;
            if (visible)
            {
                DrawScanline(dc, currentIsTop, _frame.CurrentPhase);
            }

            // During the handover the arcade shows both: the outgoing scan finishing its sweep
            // and the incoming one already parked at its start. The projector flips note state at
            // the same phase, so this is the visual half of one rule, not a second one.
            if (_frame.CurrentPhase >= HandoverPhase &&
                LineVisibleAt(0.0))
            {
                DrawScanline(dc, !currentIsTop, 0.0);
            }
        }

        private void DrawScanline(DrawingContext dc, bool isTopHalf, double phase)
        {
            // The hit point, from the same margins the projector places notes with. Every half
            // sweeps the same rectangle, its direction answered by the scroll effector - under
            // the clockwise default that is left to right on top and right to left below.
            bool rightward = SweepRightward(isTopHalf);
            double left = TechnikaPlayfieldMetrics.NoteMarginLeft;
            double right = TechnikaPlayfieldMetrics.NoteMarginRight;
            double travel = (right - left) * phase;
            double normalizedX = rightward ? left + travel : right - travel;
            double edgeX = normalizedX * TechnikaPlayfieldMetrics.NativeWidth;

            // Place the quad so its bright edge lands on the hit point. The wash trails behind,
            // which is why the offset flips with the sweep direction.
            double quadLeft = rightward
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
                rightward ? _theme.ScanlineForward : _theme.ScanlineReverse, null, quad);
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

            // The lane box is what a whole note frame fills, at either mode's pitch; a glyph authored
            // on a smaller canvas than that is smaller on purpose. See TechnikaNoteSprites.ScaleOf -
            // squeezing every canvas into the lane box made the set look like mismatched skins, and
            // reading a 3-line canvas in a 4-line set as "authored larger" drew a chain head 1.29x
            // the note it leads.
            double headSize = Math.Max(4.0, size * _sprites.ScaleFor(note.Kind));

            bool prepare = note.State == GameplayPreviewNoteState.Prepare;
            double opacity = (prepare ? PrepareOpacity : 1.0) * FaderOpacityFor(note);
            if (opacity <= 0.01)
            {
                // Fully faded by the note effector: no head, trail or approach - the arcade's
                // Fade Out reads as empty field at the line, not as a stack of zero-alpha art.
                return;
            }
            if (opacity < 0.999)
            {
                dc.PushOpacity(opacity);
            }

            DrawTrail(dc, note, size, brushes);

            // The head draws only while its own scan is on screen. A hold whose head the sweep
            // has passed but whose tail is still ahead stays Active for its body (see the
            // projector), and drawing its head at a stale scan position would park a struck
            // note on the field behind the line. The trail above is the whole of it then.
            bool headVisible = note.ScanIndex == _frame.CurrentIntScan ||
                note.ScanIndex == _frame.CurrentIntScan + 1;
            if (headVisible)
            {
                TechnikaNoteSprite sprite = _sprites.For(note.Kind);

                if (note.ApproachVisible)
                {
                    DrawApproach(dc, note, center, size);
                }

                Rect head = new Rect(
                    center.X - (headSize / 2), center.Y - (headSize / 2), headSize, headSize);
                if (sprite == null)
                {
                    // Vector chrome stands in for art that is missing; it does not layer under art
                    // that is present. The arcade glyphs carry their own glow inside their frame,
                    // and a second one behind them reads as a smudge around the note rather than
                    // as light coming off it. A filled ellipse still says where the note is and
                    // what kind it is, which is the whole job of the fallback.
                    dc.DrawEllipse(brushes.Glow, null, center, size * 0.72, size * 0.72);
                    dc.DrawEllipse(brushes.Fill, brushes.Edge, center, size / 2, size / 2);
                }
                else
                {
                    // A chain head is an arrow, so it is the one glyph whose orientation carries
                    // meaning: it says where the chain goes next, and a chain crosses lanes.
                    // Turning the image is the whole of that - the vector fallback below is a
                    // disc, which has no direction to be wrong about.
                    bool turned = Math.Abs(angleDegrees) > 0.01;
                    if (turned)
                    {
                        dc.PushTransform(new RotateTransform(angleDegrees, center.X, center.Y));
                    }
                    dc.DrawImage(sprite.Frame(_frame.CurrentScan * ShineLoopsPerScan), head);
                    if (turned)
                    {
                        dc.Pop();
                    }
                }
            }

            if (opacity < 0.999)
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

            double scale = _sprites.ScaleOf(ring);
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
            // The light enters from the edge the sweep comes from, so its sign and the mirror
            // follow the sweep's direction rather than the half - an ACW/LL/RR field otherwise
            // drags the glow in from the wrong side.
            bool rightward = SweepRightward(note.IsTopHalf);
            ImageSource frame = ring.Sweep(rightward ? offset : -offset);
            if (frame == null)
            {
                return;
            }

            bool mirrored = !rightward;
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
        /// The burst the sweep leaves behind it, on every note it has just passed.
        ///
        /// <para>
        /// A pass of its own rather than a branch inside <see cref="DrawNote"/>, because the burst
        /// outlives the note: a tap is <c>Resolved</c> the instant the sweep clears it and
        /// <see cref="DrawNote"/> rightly stops drawing it, while the flash it was struck with has
        /// most of half a second still to run. Nothing here reads the note's state at all - the
        /// clock decides, and the clock is <see cref="ProjectedGameplayNote.ApproachScanDistance"/>,
        /// which is how long ago the sweep crossed the note measured in scans.
        /// </para>
        ///
        /// <para>
        /// Silent when the chart has no tempo, because then no number of scans is honestly
        /// <see cref="TechnikaPlayfieldMetrics.CoolBombSeconds"/> long. A burst late in a scan is
        /// also cut short by the projector's one-scan render window, which drops the note that owns
        /// it at the scan flip; widening that window is a change to what every other pass measures,
        /// so the truncation stands for now.
        /// </para>
        ///
        /// <para>
        /// Each kind is struck with its own burst, through <see cref="TechnikaHitEffectProfile"/>.
        /// The arcade has a sequence per gesture - <c>CoolBomb\*\onePoint</c> and
        /// <c>CoolBomb\*\hold</c> beside <c>cool</c> - and only <c>cool</c> is in the set on disk, so
        /// what varies here is the shape of the one sequence and not the pictures in it. Read that
        /// file for which of the numbers is measured and which is chosen.
        /// </para>
        /// </summary>
        private void DrawHitEffects(DrawingContext dc, IReadOnlyList<ProjectedGameplayNote> notes)
        {
            double effectScans = TechnikaHitFlash.ScansFor(_projection.ScanSeconds);
            double previewScale = TechnikaHitEffectProfile.PreviewScale(
                Math.Max(1, _projection.LaneCount));
            if (effectScans <= 0.0 || previewScale <= 0.0)
            {
                return;
            }

            TechnikaNoteSprite bomb = _sprites.CoolBomb;
            for (int i = 0; i < notes.Count; i++)
            {
                DrawHitEffect(dc, notes[i], bomb, effectScans, previewScale);
            }
        }

        private void DrawHitEffect(
            DrawingContext dc,
            ProjectedGameplayNote note,
            TechnikaNoteSprite bomb,
            double effectScans,
            double previewScale)
        {
            // The profile's Life shortens the whole burst rather than cutting it off part-way, so a
            // quicker flash still runs its own envelope end to end and still leaves the field clean.
            TechnikaHitEffectProfile profile = TechnikaHitEffectProfile.For(note.Kind);
            double life = effectScans * profile.Life;
            if (life <= 0.0)
            {
                return;
            }

            TechnikaHitFlash flash = TechnikaHitFlash.At(note.ApproachScanDistance / life)
                .Scaled(previewScale * profile.Scale, profile.Opacity);
            if (!flash.IsVisible)
            {
                return;
            }

            double width = _fit.Length(flash.Width);
            double height = _fit.Length(flash.Height);
            if (width <= 0.0 || height <= 0.0)
            {
                return;
            }

            Point center = _fit.Note(note.X, note.Y, note.IsTopHalf);
            Rect quad = new Rect(
                center.X - (width / 2), center.Y - (height / 2), width, height);

            // Kept to its own half. At the arcade's authored size the burst is taller than the half it
            // fires in - 385 px against 348 - and this stopped a hit on the top row blooming onto the
            // bottom one; at the preview's size it rarely reaches the boundary, but a lane at the
            // edge of a half still can, and clipping costs nothing.
            dc.PushClip(new RectangleGeometry(_fit.Half(note.IsTopHalf)));
            dc.PushOpacity(flash.Opacity);
            ImageSource frame = bomb == null ? null : bomb.Shot(flash.FrameProgress);
            if (frame == null)
            {
                // No arcade art: a soft disc in the note's own colour, sized and faded by the same
                // envelope. It says "struck here, just now" without pretending to be the art.
                dc.DrawEllipse(_theme.NoteFor(note.Kind).Glow, null,
                    center, width / 2, height / 2);
            }
            else
            {
                dc.DrawImage(frame, quad);
            }
            dc.Pop();
            dc.Pop();
        }

        /// <summary>
        /// The bar the arcade runs through a chain or a repeat run.
        ///
        /// <para>
        /// A chain and a repeat run are several notes joined by one line, not one note carrying a
        /// duration - which is why <c>HasHoldTrail</c> excludes them, and why the line cannot be
        /// derived from any single note the way a hold's trail can. It is read off the run instead:
        /// notes of one family in one scan and one half, a repeat series within a lane and a chain
        /// across them, joined from each member to the next. Drawn before the heads, so the line
        /// passes under them as it does in the arcade.
        /// </para>
        /// </summary>
        private void DrawGroupLinks(DrawingContext dc, IReadOnlyList<ProjectedGameplayNote> notes)
        {
            PrepareHeadAngles(notes.Count);
            GroupRuns(notes);

            for (int run = 0; run < _runCount; run++)
            {
                DrawGroupLink(dc, notes, run);
            }
        }

        /// <summary>
        /// Puts every note of the frame into the run it belongs to: one family, one half, one scan,
        /// and one lane as well for a repeat series. A second head of the same identity starts a line
        /// of its own rather than extending the one before it, so two runs back to back in a lane are
        /// two lines.
        ///
        /// <para>
        /// Position in the note list is not part of the answer - see <see cref="_runOf"/> for what
        /// that cost. A member arriving with no head open keeps its own run rather than being
        /// discarded: on a chart the head may simply be behind the window, and half a run still has a
        /// line through it.
        /// </para>
        /// </summary>
        private void GroupRuns(IReadOnlyList<ProjectedGameplayNote> notes)
        {
            if (_runOf.Length < notes.Count)
            {
                _runOf = new int[Math.Max(notes.Count, 64)];
            }
            _openRunCount = 0;
            _runCount = 0;

            for (int i = 0; i < notes.Count; i++)
            {
                _runOf[i] = NoRun;

                ProjectedGameplayNote note = notes[i];
                int family = LinkFamily(note.Kind);
                if (family == NoFamily)
                {
                    continue;
                }

                int lane = family == RepeatFamily ? note.Lane : AnyLane;
                int slot = IndexOfOpenRun(family, lane, note.IsTopHalf, note.ScanIndex);
                if (slot < 0 || IsRunHead(note.Kind))
                {
                    slot = StartRun(family, lane, note.IsTopHalf, note.ScanIndex);
                }

                _runOf[i] = _openRuns[slot].Run;
            }
        }

        /// <summary>Starts a run for an identity, replacing whatever was open for it, and answers its
        /// slot in <see cref="_openRuns"/>.</summary>
        private int StartRun(int family, int lane, bool isTopHalf, int scanIndex)
        {
            int slot = IndexOfOpenRun(family, lane, isTopHalf, scanIndex);
            if (slot < 0)
            {
                if (_openRunCount == _openRuns.Length)
                {
                    Array.Resize(ref _openRuns, _openRuns.Length * 2);
                }
                slot = _openRunCount++;
            }

            _openRuns[slot] = new OpenRun(family, lane, isTopHalf, scanIndex, _runCount++);
            return slot;
        }

        private int IndexOfOpenRun(int family, int lane, bool isTopHalf, int scanIndex)
        {
            for (int i = 0; i < _openRunCount; i++)
            {
                if (_openRuns[i].Matches(family, lane, isTopHalf, scanIndex))
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// The identity of a run being gathered - family, lane (<see cref="AnyLane"/> for a chain),
        /// half and scan - and the run number its members are given.
        /// </summary>
        private struct OpenRun
        {
            public OpenRun(int family, int lane, bool isTopHalf, int scanIndex, int run)
            {
                _family = family;
                _lane = lane;
                _isTopHalf = isTopHalf;
                _scanIndex = scanIndex;
                Run = run;
            }

            private readonly int _family;
            private readonly int _lane;
            private readonly bool _isTopHalf;
            private readonly int _scanIndex;

            public readonly int Run;

            public bool Matches(int family, int lane, bool isTopHalf, int scanIndex)
            {
                return _family == family && _lane == lane &&
                    _isTopHalf == isTopHalf && _scanIndex == scanIndex;
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
            DrawingContext dc, IReadOnlyList<ProjectedGameplayNote> notes, int run)
        {
            int first = -1;
            int last = -1;
            for (int i = 0; i < notes.Count; i++)
            {
                if (_runOf[i] != run || !IsDrawn(notes[i]))
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
            double opacity = (prepare ? PrepareOpacity : 1.0) * FaderOpacityFor(from);
            if (opacity <= 0.01)
            {
                return;
            }
            if (opacity < 0.999)
            {
                dc.PushOpacity(opacity);
            }

            if (LinkFamily(from.Kind) == ChainFamily)
            {
                DrawChainLink(dc, notes, run, first, size);
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

            if (opacity < 0.999)
            {
                dc.Pop();
            }
        }

        /// <summary>
        /// Whether a run line may join to this note: drawn, and with its head's scan on screen.
        /// A line joined to a note the pass skips would hang off a head that has already been
        /// played - and since a spanning hold stays Active for its body while its head is
        /// behind the window, state alone no longer implies the head is there to join to. The
        /// hold's continuation trail still draws through the note pass; it just has no head
        /// in this window for a line to touch.
        /// </summary>
        private bool IsDrawn(ProjectedGameplayNote note)
        {
            if (note.State == GameplayPreviewNoteState.Inactive ||
                note.State == GameplayPreviewNoteState.Resolved)
            {
                return false;
            }
            return note.ScanIndex == _frame.CurrentIntScan ||
                note.ScanIndex == _frame.CurrentIntScan + 1;
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
            int run,
            int head,
            double size)
        {
            int previous = -1;
            for (int i = 0; i < notes.Count; i++)
            {
                if (_runOf[i] != run || !IsDrawn(notes[i]))
                {
                    continue;
                }

                ProjectedGameplayNote note = notes[i];
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
        /// One straight piece of a run's line: the art stretched from <paramref name="origin"/> along
        /// <paramref name="length"/>, turned by <paramref name="degrees"/> and centred on the line it
        /// joins. Both families draw through this - a repeat run passes its whole horizontal span, a
        /// chain passes each of its segments.
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

            // The line is authored against the tap the whole set is measured from - 76 px of art for
            // a 90 px note - so it is scaled the same way a head and a hold's body are. Against the
            // head it joins instead, which is what this did, a chain's line came out 22% short: its
            // head is a 116 px frame, so 76/116 rather than 76/90. Without art at all a themed bar
            // joins the same two points at lower fidelity.
            double height = line == null ? size * 0.18 : size * _sprites.ScaleOf(line);

            // Rotate about the bar's own start rather than drawing a rotated rectangle: the art is
            // authored along +x, so it is laid out there and then turned onto the segment. Composed
            // in call order - centre the bar on the line, turn it, then put it at the segment's start.
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
                // One column of the art stretched down the whole run, which is how a hold's body is
                // drawn too - see DrawTrail. Both line strips are built as a cap and not as a tile:
                // per column of `noterepeatline`'s 30 px frame the alpha runs 4780, 4824, 4786 and
                // then tapers monotonically to nothing by x11, with x12..x29 empty, which is the same
                // construction as `longnoteline`'s known cap. Tiling that frame drew a run as a row
                // of fading blobs - the "not a straight line" a playtest reported for the repeat - so
                // the flat near end is the cross-section and the soft far end is where the art stops.
                // Stem's cut is 2 of those 30 columns and lands inside the flat core; on the chain's
                // 1x76 columns it is the whole frame and this is a plain stretch.
                dc.DrawImage(line.Stem(_frame.CurrentScan * ShineLoopsPerScan),
                    new Rect(0, 0, length, height));
            }
            dc.Pop();
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

        /// <summary>
        /// The heads the arcade turns: a run's first note, which its line is measured from and which
        /// <see cref="DrawChainLink"/> may turn towards the member it joins. A second one of these
        /// closes the run before it rather than extending it.
        /// </summary>
        private static bool IsRunHead(GameplayPreviewNoteKind kind)
        {
            return kind == GameplayPreviewNoteKind.ChainHead ||
                kind == GameplayPreviewNoteKind.RepeatHead ||
                kind == GameplayPreviewNoteKind.RepeatHeadHold;
        }

        /// <summary>
        /// The body a held note stretches behind its head, closed at the far end by the cap the
        /// arcade animates there.
        ///
        /// <para>
        /// This was a flat themed rectangle, which is what "the lines are wrong" was about. The
        /// arcade authors each hold family as a cap plus a one-pixel-wide body sheet of ten frames -
        /// <c>longholdgauge</c> into <c>line_nor_end</c> for attribute 12, <c>long_note_line</c> into
        /// <c>long_note_end</c> for a repeat carrying a duration - and the two are one animation:
        /// column 9 of the body and frame 9 of the cap share a cross-section. The drag curve is the
        /// exception, authored as a cap alone, so its body is still the cut through it the legacy
        /// renderer takes.
        /// </para>
        ///
        /// <para>
        /// While the sweep is inside the note the arcade swaps in the lit "in" variant of both pieces.
        /// A hold under the line is the one thing on this field that is being played rather than
        /// waiting, and that is the whole of what the preview can honestly say about it.
        /// </para>
        ///
        /// <para>
        /// Laid out along +x from the head and mirrored on the lower half, where the sweep runs the
        /// other way: the cap has a flat side and a round one, so drawing it unmirrored down there
        /// would point it back into the note.
        /// </para>
        /// <para>
        /// Drawn per visible scan, not as one run from the head: a hold longer than a scan crosses
        /// the divider into the other half, where the sweep runs the other way, so one straight
        /// body cannot cover it. Each of the two drawn scans gets the intersection of the hold's
        /// span with that scan, and only the segment holding the tail gets the cap. A hold whose
        /// head is behind the window still draws its continuation segments - that is the whole of
        /// it then, since <see cref="DrawNote"/> skips the head.
        /// </para>
        /// </summary>
        private void DrawTrail(
            DrawingContext dc,
            ProjectedGameplayNote note,
            double size,
            TechnikaNoteBrushes brushes)
        {
            if (note.DurationPulse <= 0 ||
                !GameplayPreviewNoteKinds.HasHoldTrail(note.Kind))
            {
                return;
            }

            double headFloatScan = note.Pulse / PulsesPerScan;
            double tailFloatScan = (note.Pulse + note.DurationPulse) / PulsesPerScan;
            double phase = _frame.CurrentScan * ShineLoopsPerScan;

            for (int scan = _frame.CurrentIntScan; scan <= _frame.CurrentIntScan + 1; scan++)
            {
                // The hold's span intersected with this scan, in scan units: a continuation
                // segment starts at the scan's edge rather than at the head.
                double start = Math.Max(headFloatScan, scan);
                double end = Math.Min(tailFloatScan, scan + 1);
                if (start >= end)
                {
                    continue;
                }
                DrawTrailSegment(dc, note, size, brushes, phase, scan, start, end,
                    tailFloatScan <= scan + 1);
            }
        }

        /// <summary>
        /// One scan's worth of a hold's body: the span [<paramref name="start"/>,
        /// <paramref name="end"/>] in scan units, drawn in <paramref name="scan"/>'s half and lane.
        /// <para>
        /// The geometry is the projector's <c>PlaceTechnikaNote</c> run over a span instead of a
        /// point - the same margins, the same lane stack, the lower half run backwards rather
        /// than reflected - so a segment starts and ends exactly where notes at those instants
        /// would. <paramref name="withCap"/> is true only for the segment holding the tail; a
        /// continuation segment runs body to the scan's edge and stops, because the cap belongs
        /// to the hold's end, not to the divider it crosses.
        /// </para>
        /// <para>
        /// The lit "in" art follows the sweep per segment rather than per note: while the line
        /// is inside this segment's span the player is holding this part of the body, and the
        /// segment in the other half waits its turn at rest.
        /// </para>
        /// </summary>
        private void DrawTrailSegment(
            DrawingContext dc,
            ProjectedGameplayNote note,
            double size,
            TechnikaNoteBrushes brushes,
            double phase,
            int scan,
            double start,
            double end,
            bool withCap)
        {
            bool top = (scan & 1) == 1;
            // Placement is PlaceTechnikaNote run over a span, so it answers direction from the
            // same effector rule the note heads do rather than assuming the clockwise default.
            bool rightward = SweepRightward(top);
            double left = TechnikaPlayfieldMetrics.NoteMarginLeft;
            double right = TechnikaPlayfieldMetrics.NoteMarginRight;
            double fromFraction = start - scan;
            double toFraction = end - scan;
            double fromX = rightward
                ? left + ((right - left) * fromFraction)
                : right - ((right - left) * fromFraction);
            double toX = rightward
                ? left + ((right - left) * toFraction)
                : right - ((right - left) * toFraction);

            int laneCount = Math.Max(1, _projection.LaneCount);
            double inset = TechnikaPlayfieldMetrics.LaneInset;
            double localY = inset + ((1.0 - (2.0 * inset)) * (note.Lane + 0.5) / laneCount);
            double normalizedY = top ? localY / 2.0 : 0.5 + (localY / 2.0);

            Point from = _fit.Note(fromX, normalizedY, top);
            Point to = _fit.Note(toX, normalizedY, top);
            double length = Math.Abs(to.X - from.X);
            if (length < 1)
            {
                return;
            }

            bool ongoing = note.State == GameplayPreviewNoteState.Active &&
                _frame.CurrentScan >= start && _frame.CurrentScan <= end;

            TechnikaNoteSprite cap = _sprites.TrailCap(note.Kind, ongoing);
            if (cap == null)
            {
                // No cap in the local extraction or the packaged sheets - only the actively-held
                // trail variants ship nowhere - so a themed bar spans the segment at lower
                // fidelity, which is what the trail drew for every kind before.
                double flat = size * 0.42;
                double x = Math.Min(from.X, to.X);
                dc.DrawRectangle(
                    brushes.Trail, null, new Rect(x, from.Y - (flat / 2), length, flat));
                return;
            }

            // Height against the tap the whole set is authored around, exactly as a head is: the
            // arcade draws attribute 0's body 76 px tall inside a 90 px lane box and attribute 12's
            // at the full 90, and that difference is visible.
            double height = size * _sprites.ScaleOf(cap);
            double aspect = cap.FrameSize > 0 ? cap.FrameWidth / cap.FrameSize : 1.0;
            double capWidth = withCap ? Math.Min(length, height * aspect) : 0.0;
            double body = length - capWidth;

            // Laid out along +x from the segment's start and mirrored when the sweep runs the
            // other way, exactly as the single-span trail was: the cap has a flat side and a
            // round one, so drawing it unmirrored on the lower half would point it back into
            // the note.
            Matrix placement = new Matrix();
            placement.Translate(0, -height / 2.0);
            if (to.X < from.X)
            {
                placement.Scale(-1.0, 1.0);
            }
            placement.Translate(from.X, from.Y);

            dc.PushTransform(new MatrixTransform(placement));
            if (body > 0)
            {
                // Stretched, not tiled: a body frame is one column of pixels, so every tile of it
                // would be the same and the pitch has nothing to say.
                TechnikaNoteSprite sheet = _sprites.TrailBody(note.Kind, ongoing);
                ImageSource run = sheet == null ? cap.Stem(phase) : sheet.Frame(phase);
                dc.DrawImage(run, new Rect(0, 0, body, height));
            }
            if (withCap)
            {
                dc.DrawImage(cap.Frame(phase), new Rect(body, 0, capWidth, height));
            }
            dc.Pop();
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

        /// <summary>
        /// Repaints the note layer when an effector changes outside the tick pump - while the
        /// transport is stopped <c>Sync</c> is not being called, and without this the new
        /// effector would not be visible until playback moved.
        /// </summary>
        private void RedrawFieldIfReady()
        {
            // _fit is a struct whose default scale is zero, so IsUsable is the ready test.
            if (_fit.IsUsable && HasPlayfield && _frame != null)
            {
                RedrawField();
            }
        }

        /// <summary>Which way the sweep travels over the named half under the bound effector.</summary>
        private bool SweepRightward(bool isTopHalf)
        {
            TechnikaScrollDirection direction = _projection == null
                ? TechnikaScrollDirection.Clockwise
                : _projection.ScrollDirection;
            return GameplayPreviewProjector.TechnikaSweepRightward(isTopHalf, direction);
        }

        /// <summary>
        /// The fader's opacity for one note, read off how many scans ahead of the sweep its head
        /// sits (<see cref="ProjectedGameplayNote.ApproachScanDistance"/> negated).
        ///
        /// <para>
        /// Fade In is invisible far ahead and solid at the line; Fade Out is the inverse. The
        /// level-1 variants complete the fade across most of a scan (0.9), level 2 across about
        /// half one (0.45) - the arcade calls 2 the "stronger" effector purely because the note
        /// changes over a shorter distance. Notes already behind the sweep (a hold's played
        /// body) are held solid under Fade In and gone under Fade Out, which is what applying
        /// the same ramp past its ends naturally answers.
        /// </para>
        /// </summary>
        private double FaderOpacityFor(ProjectedGameplayNote note)
        {
            if (_noteFader == TechnikaNoteFader.Off)
            {
                return 1.0;
            }

            double ahead = -note.ApproachScanDistance;
            double window = _noteFader == TechnikaNoteFader.FadeIn2 ||
                _noteFader == TechnikaNoteFader.FadeOut2
                ? 0.45
                : 0.9;

            double alpha;
            if (_noteFader == TechnikaNoteFader.FadeIn ||
                _noteFader == TechnikaNoteFader.FadeIn2)
            {
                alpha = (window - ahead) / window;
            }
            else
            {
                alpha = ahead / window;
            }
            return Math.Max(0.0, Math.Min(1.0, alpha));
        }

        /// <summary>
        /// Whether the sweep is visible at a scan phase under Blink / Blind. A half-scan blink
        /// period gives Blink a 50% duty and Blink2 a 25% duty, matching the arcade timings; the
        /// incoming sweep during a handover shares the phase, so the two lines flash together.
        /// </summary>
        private bool LineVisibleAt(double phase)
        {
            if (_lineEffector == TechnikaLineEffector.Blind)
            {
                return false;
            }
            if (_lineEffector == TechnikaLineEffector.On)
            {
                return true;
            }

            const double Period = 0.5;
            double duty = _lineEffector == TechnikaLineEffector.Blink2 ? 0.125 : 0.25;
            double within = phase - Math.Floor(phase / Period) * Period;
            return within < duty;
        }
    }
}
