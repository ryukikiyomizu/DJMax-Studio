using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DJMaxEditor.Controls.TimelineV2;
using DJMaxEditor.Controls.Vertical;
using DJMaxEditor.DJMax;
using DJMaxEditor.Studio.Design;

namespace DJMaxEditor.Studio.Timeline
{
    /// <summary>
    /// Every brush, pen and typeface the timeline draws with, allocated once and frozen.
    ///
    /// <para>
    /// This exists because of a measured problem, not for tidiness. The legacy GDI+ renderer
    /// created its Pen and Brush objects inside the paint loop, so a 3000-note frame allocated
    /// and finalised ~6000 GDI handles per repaint - that is the single biggest reason the V1
    /// and V2 timelines stuttered during playback. A frozen WPF brush is immutable, thread-safe
    /// and never re-realised by the compositor, so the draw loop below does no allocation at all
    /// beyond the DrawingContext's own command list.
    /// </para>
    /// <para>
    /// One instance per <see cref="StudioChartTheme"/>, resolved through <see cref="ForTheme"/> and
    /// never rebuilt: the theme is the data, this is the frozen render side of it, and the split is
    /// what lets a theme switch cost one dictionary lookup plus an invalidate rather than a
    /// per-frame re-parse of hex strings. A surface holds its instance in a field and re-reads it
    /// only from <c>RefreshTheme</c>, so no draw path ever resolves a theme.
    /// </para>
    /// <para>
    /// Two families of colour live here on purpose. Canvas colours come from the
    /// <see cref="Definition"/>; chrome and text come from <see cref="StudioPalette"/> directly,
    /// because a chart theme recolours the chart and not the window around it (see the
    /// <see cref="StudioChartTheme"/> remarks for why that boundary is where it is).
    /// </para>
    /// </summary>
    internal sealed class StudioTimelineTheme
    {
        /// <summary>
        /// Pen widths are in device-independent units and deliberately 1.0: WPF snaps a 1.0 pen
        /// to a single device pixel when the geometry is aligned, which is what makes a grid look
        /// crisp instead of grey-and-fuzzy. Anything thinner antialiases into a smear.
        /// </summary>
        private const double HairlineWidth = 1.0;

        private static readonly object CacheLock = new object();

        /// <summary>
        /// One frozen brush set per theme id.
        ///
        /// Keyed by id rather than by definition instance so a theme loaded from disk and an
        /// equal one built in memory resolve to the same brushes, and so the lookup cannot keep a
        /// definition alive by reference. The set is bounded by how many themes exist - two
        /// built-ins today - and is only ever written on the UI thread during a switch.
        /// </summary>
        private static readonly Dictionary<string, StudioTimelineTheme> Cache =
            new Dictionary<string, StudioTimelineTheme>();

        private readonly Dictionary<TechnikaNoteKind, NoteBrushes> _noteBrushes =
            new Dictionary<TechnikaNoteKind, NoteBrushes>();

        private StudioTimelineTheme(StudioChartTheme definition)
        {
            Definition = definition;

            Field = StudioPalette.Brush(definition.Field);
            FieldAlternate = StudioPalette.Brush(definition.FieldAlternate);
            FieldScratch = StudioPalette.Brush(definition.FieldScratch);
            FieldDead = StudioPalette.Brush(definition.FieldDead);
            FieldBackground = StudioPalette.Brush(definition.FieldBackground);

            // Chrome is the shell's, not the theme's: see the class remarks.
            Chrome = StudioPalette.Brush(StudioPalette.Raised);
            ChromeEdge = StudioPalette.Brush(StudioPalette.Edge);
            Backdrop = StudioPalette.Brush(StudioPalette.Abyss);

            GridSub = FrozenPen(definition.GridSub, HairlineWidth);
            GridBeat = FrozenPen(definition.GridBeat, HairlineWidth);
            GridBar = FrozenPen(definition.GridBar, HairlineWidth);
            LaneEdge = FrozenPen(definition.LaneEdge, HairlineWidth);
            // bold=3 in a .pst preset. ptSequencer draws this as a visibly heavier rule and it
            // is the only cue that separates MR from the eighteen background lanes.
            LaneEdgeStrong = FrozenPen(definition.LaneEdgeStrong, 2.0);
            ChromeEdgePen = FrozenPen(StudioPalette.Edge, HairlineWidth);

            Playhead = FrozenPen(definition.Accent, 1.5);
            PlayheadGlow = Translucent(definition.Accent, 0.16);

            MarqueeFill = Translucent(definition.Accent, 0.14);
            MarqueeEdge = FrozenPen(definition.Accent, HairlineWidth);

            HoverFill = Translucent(StudioPalette.TextPrimary, 0.06);
            SelectionEdge = FrozenPen(definition.NoteSelectedEdge, 1.5);

            TextPrimary = StudioPalette.Brush(StudioPalette.TextPrimary);
            TextSecondary = StudioPalette.Brush(StudioPalette.TextSecondary);
            TextMuted = StudioPalette.Brush(StudioPalette.TextMuted);
            TextOnNote = StudioPalette.Brush(definition.TextOnNote);

            // The volume lane shares the note palette rather than inventing one: a bar in it is a
            // note's volume, so it should be the colour that note is drawn in on the canvas.
            VolumeBarFill = StudioPalette.Brush(definition.NotePlayable);
            VolumeBarSelected = StudioPalette.Brush(definition.NoteSelected);
            VolumeBarEdge = FrozenPen(definition.NotePlayableEdge, HairlineWidth);
            VolumeScaleLine = FrozenPen(definition.GridBeat, HairlineWidth);

            // Two faces only. The ruler and lane labels are numeric, so they get the mono face -
            // proportional digits make a column of bar numbers visibly ragged.
            UiTypeface = new Typeface(
                new FontFamily("Segoe UI Variable Text, Segoe UI, Tahoma"),
                FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            MonoTypeface = new Typeface(
                new FontFamily("Cascadia Mono, Consolas, Courier New"),
                FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            BuildNoteBrushes();
        }

        /// <summary>The palette this brush set was built from. Never null.</summary>
        public StudioChartTheme Definition { get; private set; }

        /// <summary>
        /// The frozen brush set for a theme, built on first use and cached afterwards.
        ///
        /// <para>
        /// This is the one place a theme's hex strings are parsed, which is the whole reason the
        /// lookup is cached rather than done per surface: building the set means a few dozen
        /// <see cref="ColorConverter"/> round trips and the same number of freezes, and doing that
        /// on a canvas invalidate would put allocation back into the path this class exists to
        /// keep clear of it.
        /// </para>
        /// <para>
        /// A null or unknown definition resolves to <see cref="StudioChartTheme.Default"/> rather
        /// than throwing: a surface always has to draw something, and "the shipped palette" is the
        /// only defensible answer when the one asked for cannot be had.
        /// </para>
        /// </summary>
        public static StudioTimelineTheme ForTheme(StudioChartTheme definition)
        {
            if (definition == null)
            {
                definition = StudioChartTheme.Default;
            }

            lock (CacheLock)
            {
                StudioTimelineTheme existing;
                if (Cache.TryGetValue(definition.Id, out existing))
                {
                    return existing;
                }

                StudioTimelineTheme built = new StudioTimelineTheme(definition);
                Cache[definition.Id] = built;
                return built;
            }
        }

        /// <summary>The built-in Studio palette, frozen. What a canvas starts with.</summary>
        public static StudioTimelineTheme Default
        {
            get { return ForTheme(StudioChartTheme.Default); }
        }

        /// <summary>
        /// Drops every cached brush set.
        ///
        /// Not used by the shell - a switch resolves a new entry and leaves the old one, which is
        /// correct because a surface may still be drawing with it. It is here for the case the
        /// research doc's Phase 3 introduces: a theme folder reloaded from disk, where the id is
        /// the same and the contents are not.
        /// </summary>
        public static void ClearCache()
        {
            lock (CacheLock)
            {
                Cache.Clear();
            }
        }

        public Brush Field { get; private set; }

        /// <summary>Field for a lane the layout stripes as alternate. See <see cref="FieldFor"/>.</summary>
        public Brush FieldAlternate { get; private set; }

        /// <summary>Field for a turntable lane.</summary>
        public Brush FieldScratch { get; private set; }

        public Brush FieldDead { get; private set; }
        public Brush FieldBackground { get; private set; }

        public Brush Chrome { get; private set; }
        public Brush ChromeEdge { get; private set; }
        public Brush Backdrop { get; private set; }

        public Pen GridSub { get; private set; }
        public Pen GridBeat { get; private set; }
        public Pen GridBar { get; private set; }
        public Pen LaneEdge { get; private set; }
        public Pen LaneEdgeStrong { get; private set; }
        public Pen ChromeEdgePen { get; private set; }

        public Pen Playhead { get; private set; }
        public Brush PlayheadGlow { get; private set; }

        public Brush MarqueeFill { get; private set; }
        public Pen MarqueeEdge { get; private set; }

        public Brush HoverFill { get; private set; }
        public Pen SelectionEdge { get; private set; }

        /// <summary>Volume-lane bar fill, and the same bar for a selected note.</summary>
        public Brush VolumeBarFill { get; private set; }
        public Brush VolumeBarSelected { get; private set; }
        public Pen VolumeBarEdge { get; private set; }

        /// <summary>Volume-lane scale rule.</summary>
        public Pen VolumeScaleLine { get; private set; }

        public Brush TextPrimary { get; private set; }
        public Brush TextSecondary { get; private set; }
        public Brush TextMuted { get; private set; }
        public Brush TextOnNote { get; private set; }

        public Typeface UiTypeface { get; private set; }
        public Typeface MonoTypeface { get; private set; }

        /// <summary>Fill and outline for a note. The outline is what the playtest asked for: without
        /// it, adjacent notes in one lane merge into a single block.</summary>
        public NoteBrushes NoteFor(TimelineItem item, VerticalColumn column, bool selected)
        {
            if (selected)
            {
                return _noteBrushes[TechnikaNoteKind.Unknown].Selected;
            }

            EventData source = item == null ? null : item.SourceEvent;

            // A lane's role beats the note's attribute for colour, because that is the thing the
            // user is scanning for: is this a key I press, or a keysound that just plays?
            if (column != null)
            {
                if (column.Kind == VerticalColumnKind.BgaSync)
                {
                    return BgaBrushes;
                }
                if (column.Kind == VerticalColumnKind.Background || column.Kind == VerticalColumnKind.Mr)
                {
                    return BackgroundBrushes;
                }
                // ...and under a lane-coloured theme, *which* key it is beats both. Checked after
                // the two above so BGA SYNC and the background lanes keep telling you what they
                // are: they are not lanes a player reads, so "which lane" is not the question.
                if (Definition.NotesColouredByLane && IsPlayLane(column))
                {
                    return LaneRoleBrushes(column);
                }
            }

            TechnikaNoteKind kind = TechnikaNoteClassifier.Classify(source);
            NoteBrushes brushes;
            if (_noteBrushes.TryGetValue(kind, out brushes))
            {
                return brushes;
            }
            return _noteBrushes[TechnikaNoteKind.Basic];
        }

        public NoteBrushes BgaBrushes { get; private set; }
        public NoteBrushes BackgroundBrushes { get; private set; }

        /// <summary>
        /// The three lane roles a lane-coloured theme distinguishes: white key, blue key,
        /// turntable. Meaningless when <see cref="StudioChartTheme.NotesColouredByLane"/> is off,
        /// but built either way so <see cref="NoteFor"/> has nothing to null-check.
        /// </summary>
        public NoteBrushes WhiteKeyBrushes { get; private set; }
        public NoteBrushes BlueKeyBrushes { get; private set; }
        public NoteBrushes ScratchBrushes { get; private set; }

        public Brush FieldFor(VerticalColumn column)
        {
            if (column == null)
            {
                return Field;
            }
            switch (column.Kind)
            {
                case VerticalColumnKind.LeadingUnused:
                    return FieldDead;
                case VerticalColumnKind.BgaSync:
                case VerticalColumnKind.Mr:
                case VerticalColumnKind.Background:
                // Appended tracks are authoring lanes, not lanes a player reads, so they get the
                // same recessed field as MR and BG. Their notes keep their own attribute colours.
                case VerticalColumnKind.Overflow:
                // Same for TECHNIKA's end-of-scan markers: a marker says "the note on lane N at
                // this tick closes its scan", so it is annotation on the four lanes beside it and
                // must not read as a fifth through eighth playable lane.
                case VerticalColumnKind.ScanMarker:
                    return FieldBackground;
                default:
                    break;
            }

            // Playable lanes, then: the stripe the layout already carries. Under the Studio theme
            // all three of these are the same brush, which is why the flat ptSequencer field is
            // unchanged; under IIDX they are the white-key, blue-key and turntable fields.
            if (column.IsScratch)
            {
                return FieldScratch;
            }
            if (column.Style == VerticalColumnStyle.RegularAlternate)
            {
                return FieldAlternate;
            }
            return Field;
        }

        /// <summary>Per-lane chip colour, used by the lane header strip.</summary>
        public Brush ChipFor(int columnIndex)
        {
            if (columnIndex < 0)
            {
                columnIndex = 0;
            }
            return _chips[columnIndex % _chips.Length];
        }

        private Brush[] _chips;

        /// <summary>
        /// The lanes a player reads. Mirrors <c>VerticalTrackLayout.IsGameplaySpan</c> minus the
        /// two that are not lanes at all (the leading spacer and TECHNIKA's scan markers): those
        /// are annotation, and giving them a key colour would make them read as playable.
        /// </summary>
        private static bool IsPlayLane(VerticalColumn column)
        {
            switch (column.Kind)
            {
                case VerticalColumnKind.SideLeft:
                case VerticalColumnKind.SideRight:
                case VerticalColumnKind.ShoulderLeft:
                case VerticalColumnKind.ShoulderRight:
                case VerticalColumnKind.Button:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// White key, blue key or turntable, from the same parity the layout uses to stripe the
        /// field: the BMS plan numbers its keys 1-7 and marks odd ones <c>RegularPrimary</c> and
        /// even ones <c>RegularAlternate</c>, which is IIDX's own 1/3/5/7 white, 2/4/6 blue.
        /// </summary>
        private NoteBrushes LaneRoleBrushes(VerticalColumn column)
        {
            if (column.IsScratch)
            {
                return ScratchBrushes;
            }
            if (column.Style == VerticalColumnStyle.RegularAlternate)
            {
                return BlueKeyBrushes;
            }
            return WhiteKeyBrushes;
        }

        private void BuildNoteBrushes()
        {
            StudioChartTheme definition = Definition;

            NoteBrushes selected = new NoteBrushes(
                definition.NoteSelected, definition.NoteSelectedEdge);
            NoteBrushes basic = new NoteBrushes(
                definition.NotePlayable, definition.NotePlayableEdge);
            NoteBrushes accent = new NoteBrushes(
                definition.NoteAccent, definition.NoteAccentEdge);
            NoteBrushes hold = new NoteBrushes(
                definition.NoteLong, definition.NoteLongEdge);

            basic.Selected = selected;
            accent.Selected = selected;
            hold.Selected = selected;

            // Technika attributes map onto three readable families rather than nine colours.
            // Nine hues on one canvas is noise; the shape of a long note already tells you it is
            // long, so hue only needs to answer "plain / special / held".
            _noteBrushes[TechnikaNoteKind.Unknown] = basic;
            _noteBrushes[TechnikaNoteKind.Basic] = basic;
            _noteBrushes[TechnikaNoteKind.Drag] = hold;
            _noteBrushes[TechnikaNoteKind.ChainHead] = accent;
            _noteBrushes[TechnikaNoteKind.ChainNode] = accent;
            _noteBrushes[TechnikaNoteKind.RepeatHead] = accent;
            _noteBrushes[TechnikaNoteKind.RepeatHeadHold] = hold;
            _noteBrushes[TechnikaNoteKind.Repeat] = accent;
            _noteBrushes[TechnikaNoteKind.RepeatHold] = hold;
            _noteBrushes[TechnikaNoteKind.Hold] = hold;

            BgaBrushes = new NoteBrushes(definition.NoteBga, definition.NoteBgaEdge);
            BgaBrushes.Selected = selected;
            BackgroundBrushes = new NoteBrushes(
                definition.NoteBackground, definition.NoteBackgroundEdge);
            BackgroundBrushes.Selected = selected;

            WhiteKeyBrushes = new NoteBrushes(definition.NoteWhiteKey, definition.NoteWhiteKeyEdge);
            BlueKeyBrushes = new NoteBrushes(definition.NoteBlueKey, definition.NoteBlueKeyEdge);
            ScratchBrushes = new NoteBrushes(definition.NoteScratch, definition.NoteScratchEdge);
            WhiteKeyBrushes.Selected = selected;
            BlueKeyBrushes.Selected = selected;
            ScratchBrushes.Selected = selected;

            // A theme with an empty chip list is a malformed theme, not a reason to divide by zero
            // in the header strip. Fall back to the shipped ramp and say nothing: the strip is a
            // decoration, and the canvas still has to draw.
            string[] chips = definition.LaneChips;
            if (chips == null || chips.Length == 0)
            {
                chips = StudioPalette.TrackChips;
            }
            _chips = new Brush[chips.Length];
            for (int i = 0; i < chips.Length; i++)
            {
                _chips[i] = StudioPalette.Brush(chips[i]);
            }
        }

        private static Pen FrozenPen(string hex, double thickness)
        {
            Pen pen = new Pen(StudioPalette.Brush(hex), thickness);
            pen.Freeze();
            return pen;
        }

        private static Brush Translucent(string hex, double opacity)
        {
            SolidColorBrush brush = new SolidColorBrush(StudioPalette.Parse(hex));
            brush.Opacity = opacity;
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>A note's fill plus its outline. Both frozen.</summary>
    internal sealed class NoteBrushes
    {
        public NoteBrushes(string fillHex, string edgeHex)
        {
            Fill = StudioPalette.Brush(fillHex);
            Edge = new Pen(StudioPalette.Brush(edgeHex), 1.0);
            Edge.Freeze();
            Selected = this;
        }

        public Brush Fill { get; private set; }
        public Pen Edge { get; private set; }

        /// <summary>The selected variant. Self-referential by default so callers never null-check.</summary>
        public NoteBrushes Selected { get; set; }
    }

    /// <summary>
    /// A FormattedText cache keyed by string.
    ///
    /// Building a FormattedText is expensive - it shapes the run, resolves fallback fonts and
    /// allocates a GlyphRun. The ruler asks for the same forty bar numbers on every single frame
    /// while scrolling, so building them fresh each time showed up as the second-largest cost
    /// after brush allocation. Bar numbers and lane names are a tiny bounded set, so caching them
    /// outright is both correct and cheap.
    /// </summary>
    internal sealed class TextCache
    {
        private readonly Dictionary<string, FormattedText> _cache =
            new Dictionary<string, FormattedText>();

        private readonly Typeface _typeface;
        private readonly double _size;
        private readonly Brush _brush;
        private readonly double _pixelsPerDip;

        public TextCache(Typeface typeface, double size, Brush brush, double pixelsPerDip)
        {
            _typeface = typeface;
            _size = size;
            _brush = brush;
            // A wrong pixelsPerDip is the classic cause of blurry WPF text on a scaled monitor:
            // the glyphs get hinted for the wrong grid. Always pass the real value from
            // VisualTreeHelper.GetDpi.
            _pixelsPerDip = pixelsPerDip > 0 ? pixelsPerDip : 1.0;
        }

        public FormattedText Get(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            FormattedText formatted;
            if (_cache.TryGetValue(text, out formatted))
            {
                return formatted;
            }

            formatted = new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                _typeface,
                _size,
                _brush,
                _pixelsPerDip);

            // Bar numbers and lane names are a bounded set in practice, but a pathological chart
            // with thousands of distinct keysound names must not grow this without bound.
            if (_cache.Count > 4096)
            {
                _cache.Clear();
            }
            _cache[text] = formatted;
            return formatted;
        }

        public void Clear()
        {
            _cache.Clear();
        }
    }
}
