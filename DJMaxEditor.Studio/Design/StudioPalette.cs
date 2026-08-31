using System.Windows.Media;

namespace DJMaxEditor.Studio.Design
{
    /// <summary>
    /// The studio design system, in one place, as numbers rather than as XAML literals so the
    /// tests can assert on them.
    ///
    /// Provenance. The dark chrome ramp is measured from VOCALOID 6 Editor, which is the design
    /// reference we were asked to follow: its UI assembly (VOCALOID6.dll) carries the ramp
    /// #FF171819 / #FF2D2D30 / #FF3C3C3C / #FF3F3F46 for surfaces and
    /// #FFEFEFF2 / #FFD7D7DE / #FFB9B9C0 / #FFA5A5AC / #FF696970 for text, which is the
    /// Visual-Studio-dark lineage every Yamaha/Steinberg-adjacent tool has converged on. We
    /// took the *structure* of that ramp - four surface steps, five text steps, one accent -
    /// and re-derived our own values from it so the shell reads as a peer of that class of
    /// tool without lifting its exact skin. No VOCALOID asset is copied, embedded or shipped.
    ///
    /// The canvas colours come from ptSequencer instead, because that is what the charts were
    /// authored against: a near-black lane field, olive bar rules, grey lane separators, and
    /// saturated note fills that read at a glance (blue = playable, red = accented, violet =
    /// long, grey = background/keysound-only).
    /// </summary>
    internal static class StudioPalette
    {
        // ---- surfaces (darkest to lightest) -------------------------------------------------
        /// <summary>Window backdrop behind every panel. Darker than any panel so the seams read.</summary>
        public const string Abyss = "#FF101113";
        /// <summary>Default panel body.</summary>
        public const string Surface = "#FF17181A";
        /// <summary>Raised panel: title bar, toolbars, headers.</summary>
        public const string Raised = "#FF1F2124";
        /// <summary>Hover / pressed / active-tab fill.</summary>
        public const string Hover = "#FF2A2D31";
        /// <summary>Control fill (text boxes, combo boxes, slider troughs).</summary>
        public const string Inset = "#FF121315";
        /// <summary>Hairline between panels. One device pixel, never a gradient.</summary>
        public const string Edge = "#FF34373C";
        /// <summary>Stronger divider for group boundaries.</summary>
        public const string EdgeStrong = "#FF4A4E55";

        // ---- text --------------------------------------------------------------------------
        public const string TextPrimary = "#FFEDEEF0";
        public const string TextSecondary = "#FFB6B9BF";
        public const string TextMuted = "#FF83878E";
        public const string TextDisabled = "#FF5A5E65";

        // ---- accent ------------------------------------------------------------------------
        /// <summary>
        /// One accent, used sparingly: active tool, focus ring, playhead, selection outline.
        /// V6 leans on a light cyan-blue (#FF29ABE2 / #FF65A8DF); ours is pulled slightly
        /// toward teal so it never collides with the blue note fill on the canvas.
        /// </summary>
        public const string Accent = "#FF2FBFA6";
        public const string AccentDim = "#FF1E7A6B";
        public const string AccentText = "#FF0C1412";
        /// <summary>Recording / destructive.</summary>
        public const string Danger = "#FFE05252";
        /// <summary>Warning, and the "unsaved" dot.</summary>
        public const string Warn = "#FFE0A23A";

        // ---- canvas (ptSequencer-derived) --------------------------------------------------
        /// <summary>Lane field. ptSequencer's is flat near-black; keep it flat, it is 90% of the pixels.</summary>
        public const string CanvasField = "#FF141414";
        /// <summary>Field for a lane that cannot be played (spacer / disabled).</summary>
        public const string CanvasFieldDead = "#FF0E0E0E";
        /// <summary>Field behind the background-screen lane group, one step lighter so the split reads.</summary>
        public const string CanvasFieldBackground = "#FF191919";
        /// <summary>Sub-beat grid line.</summary>
        public const string GridSub = "#FF232323";
        /// <summary>Beat grid line.</summary>
        public const string GridBeat = "#FF32332F";
        /// <summary>Bar line. ptSequencer draws these olive; that reads well and we keep it.</summary>
        public const string GridBar = "#FF6E6A2A";
        /// <summary>Lane separator.</summary>
        public const string LaneEdge = "#FF3A3A3A";
        /// <summary>Heavy lane separator (bold=3 in a .pst preset, e.g. before MR).</summary>
        public const string LaneEdgeStrong = "#FF75722F";

        // ---- notes -------------------------------------------------------------------------
        public const string NotePlayable = "#FF2E6BE0";
        public const string NotePlayableEdge = "#FF8FB4FF";
        public const string NoteAccent = "#FFD53B3B";
        public const string NoteAccentEdge = "#FFFF9A9A";
        public const string NoteLong = "#FF6C5AD8";
        public const string NoteLongEdge = "#FFB6AAFF";
        public const string NoteBackground = "#FF6A6A70";
        public const string NoteBackgroundEdge = "#FFA8A8B0";
        public const string NoteBga = "#FF2C8C6E";
        public const string NoteBgaEdge = "#FF7FE0C4";
        public const string NoteSelected = "#FFF2C14E";
        public const string NoteSelectedEdge = "#FFFFF3C4";

        /// <summary>
        /// Per-track accent chips. VOCALOID 6 ships a set of deliberately desaturated track
        /// colours (its DefaultColorSelectPopup drives values in the #5B302B / #4C6B87 /
        /// #573E61 / #3D422F family) so that ten coloured tracks still leave a calm canvas.
        /// Same idea, our values.
        /// </summary>
        public static readonly string[] TrackChips =
        {
            "#FF4C6B87", "#FF573E61", "#FF5B302B", "#FF3D422F",
            "#FF506C72", "#FF662424", "#FF915926", "#FF3E4960",
            "#FF5E544B", "#FF2A5C4E",
        };

        public static Color Parse(string hex)
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }

        public static SolidColorBrush Brush(string hex)
        {
            SolidColorBrush brush = new SolidColorBrush(Parse(hex));
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>
    /// Layout metrics. Every number here is a decision, so it lives next to the reason for it.
    /// </summary>
    internal static class StudioMetrics
    {
        /// <summary>Custom title bar height. V6 uses a slim flat title bar; 32 is the smallest
        /// height that still hits the 44x32 minimum for the caption buttons.</summary>
        public const double TitleBarHeight = 32;

        /// <summary>Main toolbar. ptSequencer's is a single 28px row of 22px buttons; we give the
        /// icons a little more air because they are vector, not 16px bitmaps.</summary>
        public const double ToolbarHeight = 38;
        public const double ToolButtonSize = 28;
        public const double IconSize = 16;

        /// <summary>Status bar, matching ptSequencer's SEL/GRID/TOT/time readout row.</summary>
        public const double StatusBarHeight = 24;

        /// <summary>Left dock: track preset, view options, sample list.</summary>
        public const double LeftDockWidth = 260;
        public const double LeftDockMinWidth = 200;

        /// <summary>Right dock: inspector.</summary>
        public const double RightDockWidth = 288;
        public const double RightDockMinWidth = 220;

        /// <summary>Bottom dock: the volume/parameter lane, V6's ParameterHeaderView split.</summary>
        public const double BottomDockHeight = 132;
        public const double BottomDockMinHeight = 72;

        /// <summary>
        /// The four-quadrant grid used by both V6 (RulerHeaderView + RulerView + HeaderView +
        /// TrackView) and ptSequencer: a fixed corner cell, a ruler that scrolls on one axis,
        /// a header strip that scrolls on the other, and a canvas that scrolls on both.
        /// </summary>
        public const double LaneHeaderHeight = 26;
        public const double RulerWidth = 44;

        /// <summary>Splitter grab width. 4 is the smallest that is comfortably hittable.</summary>
        public const double SplitterSize = 4;

        public const double PanelHeaderHeight = 26;
        public const double CornerRadius = 3;
    }
}
