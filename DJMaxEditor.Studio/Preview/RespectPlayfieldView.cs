using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DJMaxEditor.Diagnostics;
using DJMaxEditor.DJMax;
using DJMaxEditor.Preview;

namespace DJMaxEditor.Studio.Preview
{
    /// <summary>
    /// The RESPECT V gameplay playfield: one vertical gear, notes falling onto the judgement
    /// deck, driven by the projector's <see cref="GameplayPreviewProfile.Generic"/> branch and
    /// <see cref="RespectGameplayLayout"/>'s native 502-unit lane geometry.
    ///
    /// <para>
    /// Why this exists: the Generic profile's RESPECT branch already answers where every note is
    /// in the game's own coordinate space (lane X from the package's mode tables, Y against the
    /// fixed judgement line), but nothing WPF-side drew it - the playfield panel reported "not a
    /// TECHNIKA chart" for the format half the editor's library is written in.
    /// </para>
    /// <para>
    /// The drawing is two-lane art rather than a reskin of the TECHNIKA view because the game is:
    /// notes are the flat bars the Steam/RESPECT note skin authors (silver-white on the primary
    /// lanes, azure on the alternating ones, hot-pink shoulder bars, teal side-track bars),
    /// sampled from the owner's Shino-Tokuu set so the extraction is the authority rather than
    /// a guess. The per-mode geometry - which lanes exist, which are blue, and the wide bars
    /// the shoulders and sides are - is researched in
    /// <c>docs/respectv-playfield-research.md</c>, which is the authority this file defers to.
    /// The gear itself - the smoky playfield glass, the navy header plate, the thin metal edge
    /// rails and the steel bottom deck the judgement line rides on - is drawn from the same
    /// extraction when <c>LocalAssets\RespectV\Gear</c> is populated (see
    /// <c>docs/arcade-assets.md</c>; gitignored, like the TECHNIKA folder the pattern came from),
    /// and procedurally re-derived in the same hues when it is not, so a fresh clone still gets a
    /// readable frame.
    /// </para>
    /// <para>
    /// Layering mirrors the game: glass and lanes underneath, then the wide side-track bars,
    /// then the pink shoulder bars, then the main notes, with the bottom deck, edge rails and
    /// the judgement line over them - approaching notes drop behind the deck exactly as they do
    /// on the real gear. The bars sit under the mains because the game draws them there, which
    /// it can afford because charts never put same-half lane notes under a side bar. Three
    /// retained visuals, one per invalidation frequency, the same discipline the TECHNIKA view
    /// and the chart canvas keep.
    /// </para>
    /// </summary>
    internal sealed class RespectPlayfieldView : FrameworkElement, IGameplayPlayfieldView
    {
        /// <summary>Environment override for the owner's gear folder, for keeping it elsewhere.</summary>
        private const string PathVariable = "DJMAX_EDITOR_RESPECT_ASSETS";

        /// <summary>
        /// Note speed the frame is built with. Pushed by the shell from the timeline's Note
        /// Speed slider, so the gear scrolls with the chart's on-screen pace; RESPECT's own
        /// 4.5 is what it rests at. Assigning while bound re-syncs the current playhead, so a
        /// slider drag re-flows the field live instead of waiting for the next pump tick.
        /// Non-positive values fall back to the default - the layout throws on those.
        /// </summary>
        public float NoteSpeed
        {
            get { return _noteSpeed; }
            set
            {
                float speed = value <= 0 || float.IsNaN(value) || float.IsInfinity(value)
                    ? RespectGameplayLayout.DefaultNoteSpeed
                    : value;
                if (speed == _noteSpeed)
                {
                    return;
                }
                _noteSpeed = speed;
                if (HasPlayfield)
                {
                    Sync(_lastSyncTick);
                }
            }
        }

        private float _noteSpeed = RespectGameplayLayout.DefaultNoteSpeed;

        /// <summary>
        /// Height of the bottom deck as a fraction of the playfield's 1080-native height. 244 is
        /// the number <see cref="RespectGameplayLayout"/> bakes its camera offset against ("At
        /// -217 the judge line meets the 244-high bottom deck"), so the deck art and the note
        /// plane agree about where the floor is by construction.
        /// </summary>
        private const double DeckHeightFraction = 244.0 / 1080.0;

        private readonly VisualCollection _layers;
        private readonly DrawingVisual _glass = new DrawingVisual();
        private readonly DrawingVisual _field = new DrawingVisual();
        private readonly DrawingVisual _deck = new DrawingVisual();

        private GameplayPreviewProjection _projection;
        private GameplayPreviewFrame _frame;
        private int _lastSyncTick;
        private RespectGearArt _gear;
        private RespectLanePlan[] _lanes = new RespectLanePlan[0];
        private int _glassLaneCount = -1;
        private Size _glassSize;

        /// <summary>
        /// Frozen once per use. The white/blue mains are the sampled Shino-Tokuu values; the
        /// hot-pink shoulders and the teal sides are research-derived (see the research doc) -
        /// the extraction this was sampled from carries no art for either bar.
        /// </summary>
        private static readonly Brush NoteWhite = Frozen("#FFDEE7F2");
        private static readonly Brush NoteWhiteEdge = Frozen("#FFFFFFFF");
        private static readonly Brush NoteBlue = Frozen("#FF509DDE");
        private static readonly Brush NoteBlueEdge = Frozen("#FF9CD2FF");
        private static readonly Brush NotePink = Frozen("#FFF2447F");
        private static readonly Brush NotePinkEdge = Frozen("#FFFFB3D1");
        private static readonly Brush NoteSide = Frozen("#CC3DC6BE");
        private static readonly Brush NoteSideEdge = Frozen("#E08FF2EA");
        private static readonly Brush FieldBrush = Frozen("#D0080D15");
        private static readonly Brush FieldAltBrush = Frozen("#D00B1420");
        private static readonly Pen EdgePen = FrozenPen("#33223548", 1.0);
        private static readonly Brush JudgeGlow = Frozen("#6638E0FF");
        private static readonly Pen JudgeLine = FrozenPen("#FFA9F2FF", 2.0);
        private static readonly Pen LaneRulePen = FrozenPen("#FF1E2E44", 1.0);

        public RespectPlayfieldView()
        {
            _layers = new VisualCollection(this);
            _layers.Add(_glass);
            _layers.Add(_field);
            _layers.Add(_deck);

            RenderOptions.SetEdgeMode(_glass, EdgeMode.Aliased);
            // The gear art is authored far larger than the dock it lands in; downscaling wants
            // supersampling, the same choice the timeline makes for note glyphs.
            RenderOptions.SetBitmapScalingMode(_glass, BitmapScalingMode.HighQuality);
            RenderOptions.SetBitmapScalingMode(_deck, BitmapScalingMode.HighQuality);

            ClipToBounds = true;
            Focusable = false;
            IsHitTestVisible = false;
        }

        /// <summary>True once a Generic RESPECT projection is bound and there is something to draw.</summary>
        public bool HasPlayfield
        {
            get
            {
                return _projection != null &&
                    _projection.Profile == GameplayPreviewProfile.Generic &&
                    RespectGameplayLayout.NormalizeLaneMode(_projection.LaneCount) != 0;
            }
        }

        /// <summary>Whether the gear is drawn from the owner's extraction or re-derived. Panel text.</summary>
        public string GearSourceLabel
        {
            get { return _gear != null && _gear.HasAnyArt ? "SHINO-TOKUU GEAR" : "RE-DERIVED GEAR"; }
        }

        public void Bind(GameplayPreviewProjection projection)
        {
            _projection = projection;
            _frame = null;
            // The live speed re-sync answers against this; a fresh bind starts at zero, not
            // at whatever tick the previous document was parked on.
            _lastSyncTick = 0;
            _lanes = BuildLanePlan(projection);
            _glassLaneCount = -1;
            // Eager, not at first paint: the panel header asks GearSourceLabel immediately after
            // this call, and five small reads up front cost less than a wrong label.
            _gear = RespectGearArt.Load();
            InvalidateVisual();
        }

        public void Unbind()
        {
            _projection = null;
            _frame = null;
            _lanes = new RespectLanePlan[0];
            _glassLaneCount = -1;
            InvalidateVisual();
        }

        /// <summary>
        /// Moves the gear to a playhead position, in the editor's virtual tick space - the same
        /// contract <see cref="TechnikaPlayfieldView.Sync"/> keeps, so the pump drives either
        /// view without knowing which is up.
        /// </summary>
        public void Sync(int playheadVirtualTick)
        {
            if (!HasPlayfield)
            {
                return;
            }

            _lastSyncTick = playheadVirtualTick;
            int tick = playheadVirtualTick / EventData.VirtualTickSize;
            _frame = _projection.CreateRenderableFrame(tick, _noteSpeed);
            using (DrawingContext dc = _field.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Transparent, null,
                    new Rect(0, 0, ActualWidth, ActualHeight));
                DrawNotes(dc);
            }
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
            // Aspect-declaring, like the TECHNIKA view: hosted in an auto-sized row the panel is
            // exactly as tall as a 684:1080 scene is wide, so it never shows letterbox bars.
            RespectGameplayLayout layout = RespectGameplayLayout.ForMode(
                HasPlayfield ? _projection.LaneCount : 4);
            double aspect = layout.SceneWidth / layout.PlayfieldHeight;
            if (double.IsInfinity(availableSize.Width) || availableSize.Width <= 0)
            {
                availableSize.Width = 240;
            }
            double height = availableSize.Width / aspect;
            if (!double.IsInfinity(availableSize.Height) && availableSize.Height > 0 &&
                height > availableSize.Height)
            {
                height = availableSize.Height;
            }
            return new Size(availableSize.Width, height);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            if (!HasPlayfield)
            {
                using (DrawingContext dc = _glass.RenderOpen())
                {
                    dc.DrawRectangle(FieldBrush, null,
                        new Rect(0, 0, ActualWidth, ActualHeight));
                }
                using (_field.RenderOpen()) { }
                using (_deck.RenderOpen()) { }
                return;
            }

            Size size = new Size(ActualWidth, ActualHeight);
            if (_glassLaneCount != _projection.LaneCount || _glassSize != size)
            {
                _glassLaneCount = _projection.LaneCount;
                _glassSize = size;
                DrawGlass();
            }
            if (_frame == null)
            {
                // A rebind or a resize happened while stopped; without one sync the panel is an
                // empty gear until playback starts.
                Sync(0);
            }
        }

        // -----------------------------------------------------------------------------------
        // Geometry
        // -----------------------------------------------------------------------------------

        /// <summary>One lane's plan, read once at bind time out of the notes the projector placed.</summary>
        private struct RespectLanePlan
        {
            public double NativeX;
            public double LaneWidth;
            public RespectGameplayNoteType Type;
            public GameplayPreviewLaneRole Role;
        }

        /// <summary>
        /// The distinct lanes the projection actually placed notes on, in X order. Read from the
        /// notes rather than from a mode table on purpose: the projector already decided which
        /// tracks are playable and where they sit in native units - a second lane table here
        /// would only be able to disagree with it.
        /// </summary>
        private static RespectLanePlan[] BuildLanePlan(GameplayPreviewProjection projection)
        {
            var lanes = new List<RespectLanePlan>();
            if (projection == null)
            {
                return lanes.ToArray();
            }

            for (int i = 0; i < projection.Notes.Count; i++)
            {
                ProjectedGameplayNote note = projection.Notes[i];
                bool known = false;
                for (int j = 0; j < lanes.Count; j++)
                {
                    if (Math.Abs(lanes[j].NativeX - note.NativeX) < 0.5 &&
                        lanes[j].Role == note.LaneRole)
                    {
                        known = true;
                        break;
                    }
                }
                if (!known)
                {
                    lanes.Add(new RespectLanePlan
                    {
                        NativeX = note.NativeX,
                        Type = note.RespectType,
                        Role = note.LaneRole,
                        LaneWidth = 0,
                    });
                }
            }
            lanes.Sort((a, b) => a.NativeX.CompareTo(b.NativeX));

            // Regular lane width from spacing: adjacent lanes share a pitch (120/96/80 by mode),
            // and the edge lanes mirror it. Measured over the regular lanes only, so an overlay
            // bar sitting at a half-centre cannot donate its X to a neighbour's pitch.
            for (int i = 0; i < lanes.Count; i++)
            {
                RespectLanePlan lane = lanes[i];
                if (lane.Role != GameplayPreviewLaneRole.Regular)
                {
                    continue;
                }
                double pitch = 80;
                bool measured = false;
                for (int j = i + 1; j < lanes.Count; j++)
                {
                    if (lanes[j].Role == GameplayPreviewLaneRole.Regular)
                    {
                        pitch = lanes[j].NativeX - lane.NativeX;
                        measured = true;
                        break;
                    }
                }
                if (!measured)
                {
                    for (int j = i - 1; j >= 0; j--)
                    {
                        if (lanes[j].Role == GameplayPreviewLaneRole.Regular)
                        {
                            pitch = lane.NativeX - lanes[j].NativeX;
                            break;
                        }
                    }
                }
                lane.LaneWidth = Math.Max(40, pitch - 4);
                lanes[i] = lane;
            }

            // Overlay bars from the regular span: each shoulder and each side bar runs from its
            // half's outer lane edge to the centre, stopping 2 units short so the two halves
            // never touch. On every shipped pitch the outer edge lands at +/-238, so the bars
            // come out 236 wide around the +/-120 half-centres the projector places them at.
            double outer = 0;
            for (int i = 0; i < lanes.Count; i++)
            {
                if (lanes[i].Role == GameplayPreviewLaneRole.Regular)
                {
                    double edge = Math.Abs(lanes[i].NativeX) + (lanes[i].LaneWidth / 2.0);
                    if (edge > outer)
                    {
                        outer = edge;
                    }
                }
            }
            if (outer <= 0)
            {
                outer = 238;
            }
            for (int i = 0; i < lanes.Count; i++)
            {
                if (lanes[i].Role != GameplayPreviewLaneRole.Regular)
                {
                    RespectLanePlan bar = lanes[i];
                    bar.LaneWidth = Math.Max(40, outer - 2);
                    lanes[i] = bar;
                }
            }
            return lanes.ToArray();
        }

        // -----------------------------------------------------------------------------------
        // Layer 0: playfield glass + lane fields
        // -----------------------------------------------------------------------------------

        private void DrawGlass()
        {
            RespectGameplayLayout layout = RespectGameplayLayout.ForMode(_projection.LaneCount);
            Rect scene = FitScene(new Rect(0, 0, ActualWidth, ActualHeight), layout);
            Rect field = GetField(scene, layout);

            if (_gear == null)
            {
                _gear = RespectGearArt.Load();
            }

            using (DrawingContext dc = _glass.RenderOpen())
            {
                // Scene surround: the cabinet area around the playfield's own glass.
                dc.DrawRectangle(Frozen("#FF04060B"), null, new Rect(0, 0, ActualWidth, ActualHeight));

                if (_gear.Backdrop != null)
                {
                    dc.DrawImage(_gear.Backdrop, field);
                }
                else
                {
                    dc.DrawRectangle(FieldBrush, null, field);
                }

                DrawLaneFields(dc, field);
            }

            using (DrawingContext dc = _deck.RenderOpen())
            {
                DrawDeck(dc, field, scene);
            }
        }

        private void DrawLaneFields(DrawingContext dc, Rect field)
        {
            var separator = new StreamGeometry();
            using (StreamGeometryContext ctx = separator.Open())
            {
                // Regular lanes only: shoulders and sides are overlay bars, not lanes, and the
                // game draws no lane field or rule under them. The alternation counts regular
                // lanes, not plan entries, so the overlay entries sharing their X do not flip it.
                int regular = 0;
                for (int i = 0; i < _lanes.Length; i++)
                {
                    RespectLanePlan lane = _lanes[i];
                    if (lane.Role != GameplayPreviewLaneRole.Regular)
                    {
                        continue;
                    }

                    double x = NativeXToScreen(lane.NativeX, field);
                    double half = NativeWidthToScreen(lane.LaneWidth, field) / 2.0;
                    Brush shade = (regular % 2 == 0) ? FieldBrush : FieldAltBrush;
                    regular++;
                    if (_gear.Backdrop == null)
                    {
                        dc.DrawRectangle(shade, null,
                            new Rect(x - half, field.Top, half * 2, field.Height));
                    }

                    double ruleX = Math.Round(x - half) + 0.5;
                    ctx.BeginFigure(new Point(ruleX, field.Top), false, false);
                    ctx.LineTo(new Point(ruleX, field.Bottom), true, false);
                }
            }
            separator.Freeze();
            dc.DrawGeometry(null, LaneRulePen, separator);
        }

        /// <summary>
        /// The deck, the edge rails and the judgement line - everything that sits in front of the
        /// notes. With art: the owner's bottom deck stretched across the lane core, the metal
        /// rails at the edges. Without: the same shapes as flat plates in the same hues.
        /// </summary>
        private void DrawDeck(DrawingContext dc, Rect field, Rect scene)
        {
            // Metal edge rails first, under the deck art like the package arranges them.
            double railLeft = NativeWidthToScreen(14, field);
            if (_gear.FrameLeft != null)
            {
                double w = field.Width * (_gear.FrameLeft.Width / 502.0);
                dc.DrawImage(_gear.FrameLeft, new Rect(field.Left, field.Top, w, field.Height));
            }
            else
            {
                dc.DrawRectangle(Frozen("#FF9AA6B4"), null, new Rect(field.Left, field.Top, railLeft, field.Height));
            }
            if (_gear.FrameRight != null)
            {
                double w = field.Width * (_gear.FrameRight.Width / 502.0);
                dc.DrawImage(_gear.FrameRight, new Rect(field.Right - w, field.Top, w, field.Height));
            }
            else
            {
                dc.DrawRectangle(Frozen("#FF9AA6B4"), null, new Rect(field.Right - railLeft, field.Top, railLeft, field.Height));
            }

            // The bottom deck the judgement line rides on. The image keeps its own aspect, never
            // squeezed: anchored at the field's bottom edge and as wide as the lane core.
            double deckHeight = Math.Max(60, field.Height * DeckHeightFraction);
            Rect deck = new Rect(field.Left, field.Bottom - deckHeight, field.Width, deckHeight);
            if (_gear.BottomDeck != null)
            {
                double artHeight = field.Width * (_gear.BottomDeck.Height / Math.Max(1.0, _gear.BottomDeck.Width));
                Rect art = new Rect(
                    field.Left,
                    field.Bottom - Math.Max(deck.Height, artHeight),
                    field.Width,
                    Math.Max(deck.Height, artHeight));
                dc.DrawImage(_gear.BottomDeck, art);
            }
            else
            {
                dc.DrawRectangle(Frozen("#FF101C30"), null, deck);
                dc.DrawRectangle(Frozen("#FF1D3350"), null,
                    new Rect(deck.Left, deck.Top, deck.Width, 3));
            }

            // The header plate across the top, which the package draws as one 635-wide sprite
            // overlapping the frames - so scene-wide, not field-wide.
            if (_gear.Header != null)
            {
                double artHeight = scene.Width * (_gear.Header.Height / Math.Max(1.0, _gear.Header.Width));
                dc.DrawImage(_gear.Header, new Rect(scene.Left, scene.Top, scene.Width, artHeight));
            }

            // Judgement line: a soft wash under a bright hairline, riding the deck's top.
            double y = NativeYToScreen(RespectGameplayLayout.ForMode(_projection.LaneCount).JudgmentLineY, field);
            dc.DrawRectangle(JudgeGlow, null, new Rect(field.Left, y - 4, field.Width, 8));
            dc.DrawLine(JudgeLine, new Point(field.Left, y), new Point(field.Right, y));
        }

        // -----------------------------------------------------------------------------------
        // Layer 1: notes
        // -----------------------------------------------------------------------------------

        private void DrawNotes(DrawingContext dc)
        {
            if (_frame == null)
            {
                return;
            }

            RespectGameplayLayout layout = RespectGameplayLayout.ForMode(_projection.LaneCount);
            Rect scene = FitScene(new Rect(0, 0, ActualWidth, ActualHeight), layout);
            Rect field = GetField(scene, layout);
            // Clip notes to the lane core: the deck and the header are drawn over the edges, so a
            // note never floats over the cabinet.
            dc.PushClip(new RectangleGeometry(field));

            // Three passes, bottom to top, because the game layers them: the wide side-track bars
            // first, then the pink shoulder bars, then the main notes over both. One loop with a
            // sort would do it in a single walk, but the frame is already in pulse order and the
            // three walks are cheaper than reordering it.
            for (int i = 0; i < _frame.Notes.Count; i++)
            {
                ProjectedGameplayNote note = _frame.Notes[i];
                if (note.LaneRole == GameplayPreviewLaneRole.SideTrackLeft ||
                    note.LaneRole == GameplayPreviewLaneRole.SideTrackRight)
                {
                    DrawNote(dc, note, field, layout);
                }
            }
            for (int i = 0; i < _frame.Notes.Count; i++)
            {
                ProjectedGameplayNote note = _frame.Notes[i];
                if (note.LaneRole == GameplayPreviewLaneRole.ExtraButtonLeft ||
                    note.LaneRole == GameplayPreviewLaneRole.ExtraButtonRight)
                {
                    DrawNote(dc, note, field, layout);
                }
            }
            for (int i = 0; i < _frame.Notes.Count; i++)
            {
                ProjectedGameplayNote note = _frame.Notes[i];
                if (note.LaneRole == GameplayPreviewLaneRole.Regular)
                {
                    DrawNote(dc, note, field, layout);
                }
            }

            dc.Pop();
        }

        private void DrawNote(
            DrawingContext dc,
            ProjectedGameplayNote note,
            Rect field,
            RespectGameplayLayout layout)
        {
            if (note.State == GameplayPreviewNoteState.Inactive ||
                note.State == GameplayPreviewNoteState.Resolved)
            {
                return;
            }

            RespectLanePlan lane = LaneFor(note);
            double x = NativeXToScreen(note.NativeX, field);
            double width = NativeWidthToScreen(lane.LaneWidth, field);

            // note.NativeY is the *head's* position along the native axis; the head sits
            // astride the judgement line at the moment it is due, and a held note's body
            // trails upward behind it (screen-negative: further ahead in time is further up).
            double yHead = NativeYToScreen(note.NativeY, field);
            double headHeight = Math.Max(
                NativeHeightToScreen(layout.GetDefaultNoteHeight(note.RespectType), field), 4.0);
            double bodyHeight = Math.Max(
                NativeHeightToScreen(note.NativeHeight, field), headHeight);

            Brush fill;
            Brush edge;
            switch (note.RespectType)
            {
                case RespectGameplayNoteType.Blue:
                    fill = NoteBlue; edge = NoteBlueEdge; break;
                case RespectGameplayNoteType.Analog:
                    // Both sides the same teal: the game never coloured them apart, and the old
                    // red-left/purple-right split was invented.
                    fill = NoteSide; edge = NoteSideEdge; break;
                case RespectGameplayNoteType.L1:
                case RespectGameplayNoteType.L2:
                case RespectGameplayNoteType.R1:
                case RespectGameplayNoteType.R2:
                    fill = NotePink; edge = NotePinkEdge; break;
                default:
                    fill = NoteWhite; edge = NoteWhiteEdge; break;
            }

            if (bodyHeight > headHeight + 1.0)
            {
                // The held body: same hue at half light, so it reads as one note stretched
                // rather than two stacked. It ends one head-height past the head, whose own
                // cap is drawn after.
                Brush body = HalfLit(fill);
                dc.DrawRectangle(body, null,
                    new Rect(x - (width / 2.0) + 2, yHead - bodyHeight, width - 4,
                        bodyHeight - headHeight / 2.0));

                // The tail cap: one full-bright bar across the body's far end, so a hold reads
                // as finished up there rather than clipped by the window. Same width as the
                // head, so a wide bar gets a wide cap.
                double tailHeight = Math.Max(2.0, headHeight / 2.0);
                dc.DrawRectangle(fill, null,
                    new Rect(x - (width / 2.0) + 2, yHead - bodyHeight, width - 4, tailHeight));
            }

            double headTop = yHead - headHeight;
            Rect head = new Rect(
                Math.Round(x - (width / 2.0)) + 0.5,
                Math.Round(headTop) + 0.5,
                Math.Max(2, Math.Round(width) - 1),
                Math.Max(2, Math.Round(headHeight) - 1));
            bool analog = note.RespectType == RespectGameplayNoteType.Analog;
            dc.DrawRectangle(fill, analog ? null : EdgePen, head);
            if (!analog)
            {
                // Bright top edge: the note art's own reading, one lighter line across the
                // top of the bar. Frozen once per hue, or this allocates a pen per note.
                Pen edgePen = EdgePenFor(edge);
                dc.DrawLine(edgePen,
                    new Point(head.Left + 1, head.Top + 1), new Point(head.Right - 1, head.Top + 1));
            }
        }

        private RespectLanePlan LaneFor(ProjectedGameplayNote note)
        {
            for (int i = 0; i < _lanes.Length; i++)
            {
                if (Math.Abs(_lanes[i].NativeX - note.NativeX) < 0.5 &&
                    _lanes[i].Role == note.LaneRole)
                {
                    return _lanes[i];
                }
            }
            return new RespectLanePlan { NativeX = note.NativeX, LaneWidth = 60, Type = note.RespectType, Role = note.LaneRole };
        }

        // -----------------------------------------------------------------------------------
        // Native -> screen
        // -----------------------------------------------------------------------------------

        private static Rect FitScene(Rect viewport, RespectGameplayLayout layout)
        {
            if (viewport.Width <= 0 || viewport.Height <= 0)
            {
                return viewport;
            }
            double scale = Math.Min(
                viewport.Width / layout.SceneWidth,
                viewport.Height / layout.PlayfieldHeight);
            double width = layout.SceneWidth * scale;
            double height = layout.PlayfieldHeight * scale;
            return new Rect(
                viewport.Left + ((viewport.Width - width) / 2.0),
                viewport.Top + ((viewport.Height - height) / 2.0),
                width, height);
        }

        private static Rect GetField(Rect scene, RespectGameplayLayout layout)
        {
            if (scene.Width <= 0)
            {
                return scene;
            }
            double scale = scene.Width / layout.SceneWidth;
            return new Rect(
                scene.Left + (layout.LeftFrameWidth * scale),
                scene.Top,
                layout.PlayfieldWidth * scale,
                scene.Height);
        }

        private static double NativeXToScreen(double nativeX, Rect field)
        {
            return field.Left + ((nativeX + 251.0) * field.Width / 502.0);
        }

        private static double NativeYToScreen(double nativeY, Rect field)
        {
            // Matches RespectGameplayLayout.ToScreenY: the camera is offset inside the 1080
            // frame; at -217 the judge line meets the bottom deck.
            const double nativeTop = 619.0;
            return field.Top + ((nativeTop - nativeY) * field.Height / 1080.0);
        }

        private static double NativeWidthToScreen(double nativeWidth, Rect field)
        {
            return nativeWidth * field.Width / 502.0;
        }

        private static double NativeHeightToScreen(double nativeHeight, Rect field)
        {
            return nativeHeight * field.Height / 1080.0;
        }

        // -----------------------------------------------------------------------------------
        // Brushes
        // -----------------------------------------------------------------------------------

        private static readonly Dictionary<Brush, Brush> _halves = new Dictionary<Brush, Brush>();
        private static readonly Dictionary<Brush, Pen> _edgePens = new Dictionary<Brush, Pen>();

        /// <summary>Half-brightness copy of a note fill, cached: one per hue per session.</summary>
        private static Brush HalfLit(Brush source)
        {
            Brush cached;
            if (_halves.TryGetValue(source, out cached))
            {
                return cached;
            }
            SolidColorBrush solid = source as SolidColorBrush;
            Color c = solid == null ? Colors.Gray : solid.Color;
            Brush dim = Frozen(Color.FromArgb(
                (byte)(solid == null ? 0xCC : solid.Color.A),
                (byte)(c.R / 2 + 40), (byte)(c.G / 2 + 40), (byte)(c.B / 2 + 44)));
            _halves[source] = dim;
            return dim;
        }

        /// <summary>The 1.5 px top-edge pen for a note hue, frozen once per hue.</summary>
        private static Pen EdgePenFor(Brush source)
        {
            Pen cached;
            if (_edgePens.TryGetValue(source, out cached))
            {
                return cached;
            }
            Pen pen = new Pen(source, 1.5);
            pen.Freeze();
            _edgePens[source] = pen;
            return pen;
        }

        private static Brush Frozen(string hex)
        {
            Brush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        private static Brush Frozen(Color color)
        {
            Brush brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static Pen FrozenPen(string hex, double thickness)
        {
            Pen pen = new Pen(Frozen(hex), thickness);
            pen.Freeze();
            return pen;
        }

        // -----------------------------------------------------------------------------------
        // The owner's gear art
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// The RESPECT V gear sprites, loaded from <c>LocalAssets\RespectV\Gear</c> when present:
        /// the smoky playfield glass, the navy header, the steel bottom deck and the two metal
        /// edge rails. All five pinned by texture in <c>scripts/fetch-arcade-assets.ps1</c>;
        /// every one is optional, and each missing piece falls back to the re-derived plate.
        /// </summary>
        private sealed class RespectGearArt
        {
            public BitmapSource Backdrop;
            public BitmapSource Header;
            public BitmapSource BottomDeck;
            public BitmapSource FrameLeft;
            public BitmapSource FrameRight;

            public bool HasAnyArt
            {
                get
                {
                    return Backdrop != null || Header != null || BottomDeck != null ||
                        FrameLeft != null || FrameRight != null;
                }
            }

            public static RespectGearArt Load()
            {
                var art = new RespectGearArt();
                string root = FindGearRoot();
                if (root == null)
                {
                    return art;
                }
                art.Backdrop = LoadFrozenImage(Path.Combine(root, "gear_bg.png"));
                art.Header = LoadFrozenImage(Path.Combine(root, "gear_back.png"));
                art.BottomDeck = LoadFrozenImage(Path.Combine(root, "gear_bottom.png"));
                art.FrameLeft = LoadFrozenImage(Path.Combine(root, "gear_frame_left.png"));
                art.FrameRight = LoadFrozenImage(Path.Combine(root, "gear_frame_right.png"));
                if (art.HasAnyArt)
                {
                    DiagnosticLog.Write("respect.gear", "local gear art from " + root);
                }
                return art;
            }

            private static string FindGearRoot()
            {
                string env = Environment.GetEnvironmentVariable(PathVariable);
                if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
                {
                    return env;
                }

                string directory = Path.GetDirectoryName(
                    typeof(RespectPlayfieldView).Assembly.Location);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    directory = AppDomain.CurrentDomain.BaseDirectory;
                }
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    string candidate = Path.Combine(directory, "LocalAssets", "RespectV", "Gear");
                    if (Directory.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                return null;
            }

            private static BitmapSource LoadFrozenImage(string path)
            {
                if (!File.Exists(path))
                {
                    return null;
                }
                try
                {
                    BitmapImage image = new BitmapImage();
                    image.BeginInit();
                    // OnLoad plus a stream: one read, no held handle - the owner's extraction
                    // stays replaceable while the editor runs. Same rule the TECHNIKA loader
                    // documents.
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    using (FileStream stream = File.OpenRead(path))
                    {
                        image.StreamSource = stream;
                        image.EndInit();
                    }
                    image.Freeze();
                    return image;
                }
                catch (IOException ex) { DiagnosticLog.Exception("respect.gear", ex); }
                catch (UnauthorizedAccessException ex) { DiagnosticLog.Exception("respect.gear", ex); }
                catch (NotSupportedException ex) { DiagnosticLog.Exception("respect.gear", ex); }
                return null;
            }
        }
    }
}
