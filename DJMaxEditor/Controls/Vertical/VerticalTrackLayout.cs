using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DJMaxEditor.Controls.Vertical
{
    /// <summary>
    /// Functional role of a vertical editor column, mirroring the ptSequencer DPC
    /// presets (DPC_4B/5B/6B/8B.pst). This is the semantic identity used for theming
    /// and hit testing; <see cref="VerticalColumnStyle"/> carries the raw ptSequencer
    /// "type" shading value.
    /// </summary>
    public enum VerticalColumnKind
    {
        LeadingUnused,
        SideLeft,
        ShoulderLeft,
        Button,
        ShoulderRight,
        SideRight,
        BgaSync,
        Mr,
        Background,

        /// <summary>
        /// A source track the DPC preset has no column for, appended after BG 18 so that
        /// nothing authored on it is invisible. The presets name tracks 0-11, 22 and 23-40;
        /// TECHNIKA charts also author on 12-21, and dropping those was the whole of the
        /// "the other tracks don't show up" report. Only tracks that actually carry events
        /// get one, so a Portable chart's layout is the bare preset it always was.
        /// </summary>
        Overflow,

        /// <summary>
        /// A TECHNIKA end-of-scan marker track. The arcade keeps one marker track per playable
        /// lane - source tracks 4-7 pair with lanes 0-3 - and a note there means "the note on
        /// that lane at this tick closes its scan"; the TECHNIKA projector reads exactly those
        /// four tracks to set <c>EndOfScan</c>. They carry authored content, so they need
        /// columns, but they are not lanes a player touches, so they get utility-width columns
        /// beside the four lanes instead of 60px gameplay ones.
        /// </summary>
        ScanMarker
    }

    /// <summary>
    /// Raw ptSequencer column "type" value. Primary/alternate drive the striped
    /// gameplay-button shading; the remaining values identify non-button columns.
    /// </summary>
    public enum VerticalColumnStyle
    {
        RegularPrimary = 1,
        RegularAlternate = 2,
        SideOrMr = 4,
        Shoulder = 5,
        Utility = 6
    }

    /// <summary>
    /// One vertical editor column: a single source track drawn as a fixed-width
    /// vertical lane. Ordering, widths, and the source-track mapping are the literal
    /// ptSequencer DPC preset.
    /// </summary>
    public sealed class VerticalColumn
    {
        public VerticalColumn(
            int index,
            VerticalColumnKind kind,
            VerticalColumnStyle style,
            string name,
            int sourceTrackId,
            int width,
            int bold,
            int nativeLeft)
        {
            Index = index;
            Kind = kind;
            Style = style;
            Name = name;
            ShortName = DeriveShortName(kind, name);
            SourceTrackId = sourceTrackId;
            Width = width;
            Bold = bold;
            NativeLeft = nativeLeft;
        }

        /// <summary>Ordinal position, matching the ptSequencer TrackK index.</summary>
        public int Index { get; private set; }

        public VerticalColumnKind Kind { get; private set; }

        public VerticalColumnStyle Style { get; private set; }

        public string Name { get; private set; }

        /// <summary>
        /// Abbreviated label ("SL", "3", "R1", "BGA", "B12") for the column-name strip,
        /// which is only as wide as the column itself.
        /// </summary>
        public string ShortName { get; private set; }

        /// <summary>
        /// Source (song) track id this column draws: SIDE L = 2, buttons = 3-8,
        /// SIDE R = 9, L1 = 10, R1 = 11, BGA SYNC = 1, MR = 22, BG 1-18 = 23-40,
        /// leading unused = 0. On the TECHNIKA layout the lanes are 0-3 and the scan
        /// markers 4-7, and the spacer is -1 because it draws no track at all.
        /// </summary>
        public int SourceTrackId { get; private set; }

        /// <summary>
        /// ptSequencer native column width: 60 for gameplay/side/shoulder columns,
        /// 30 for utility/background/MR/BGA columns, 18 for the leading unused column.
        /// </summary>
        public int Width { get; private set; }

        /// <summary>ptSequencer "bold" attribute (0 none, 1 emphasized, 3 MR).</summary>
        public int Bold { get; private set; }

        /// <summary>Left edge in native ptSequencer width units (cumulative sum of prior widths).</summary>
        public int NativeLeft { get; private set; }

        /// <summary>Right edge in native ptSequencer width units.</summary>
        public int NativeRight { get { return NativeLeft + Width; } }

        private static string DeriveShortName(VerticalColumnKind kind, string name)
        {
            string text = name ?? string.Empty;
            switch (kind)
            {
                case VerticalColumnKind.LeadingUnused:
                    return string.Empty;
                case VerticalColumnKind.SideLeft:
                    return "SL";
                case VerticalColumnKind.SideRight:
                    return "SR";
                case VerticalColumnKind.ShoulderLeft:
                    return "L1";
                case VerticalColumnKind.ShoulderRight:
                    return "R1";
                case VerticalColumnKind.BgaSync:
                    return "BGA";
                case VerticalColumnKind.Mr:
                    return "MR";
                case VerticalColumnKind.Button:
                    if (text.StartsWith("button", StringComparison.Ordinal))
                        return text.Substring("button".Length);
                    // TECHNIKA lanes are touch lanes, not buttons, so they are named "lane1"..
                    // "lane4"; the header strip still wants the bare lane number.
                    return text.StartsWith("lane", StringComparison.Ordinal)
                        ? text.Substring("lane".Length)
                        : text;
                case VerticalColumnKind.ScanMarker:
                    return text.StartsWith("EOS ", StringComparison.Ordinal)
                        ? "E" + text.Substring("EOS ".Length)
                        : text;
                case VerticalColumnKind.Background:
                    return text.StartsWith("BG ", StringComparison.Ordinal)
                        ? "B" + text.Substring("BG ".Length)
                        : text;
                case VerticalColumnKind.Overflow:
                    return text.StartsWith("TRK ", StringComparison.Ordinal)
                        ? "T" + text.Substring("TRK ".Length)
                        : text;
                default:
                    return text;
            }
        }
    }

    /// <summary>
    /// Pure, WinForms-free description of the ptSequencer-style vertical editor
    /// columns for a 4B/5B/6B/8B chart, or of the TECHNIKA lane layout. Both the V1 editor
    /// and the V2 timeline drive their vertical surfaces from this single shared layout so
    /// their column order, widths, and source-track mapping cannot drift apart.
    /// </summary>
    public sealed class VerticalTrackLayout
    {
        // Native ptSequencer column widths.
        private const int GameplayWidth = 60;
        private const int UtilityWidth = 30;
        private const int LeadingWidth = 18;
        private const int BackgroundCount = 18;

        // Source (song) track ids, matching the DPC presets exactly.
        private const int TrackUnused = 0;
        private const int TrackBgaSync = 1;
        private const int TrackSideLeft = 2;
        private const int TrackFirstButton = 3;
        private const int TrackSideRight = 9;
        private const int TrackShoulderLeft = 10;
        private const int TrackShoulderRight = 11;
        private const int TrackMr = 22;
        private const int TrackFirstBackground = 23;

        // TECHNIKA source track ids. Its four playable lanes start at track 0 - the id every DPC
        // preset spends on the unused spacer - and its end-of-scan markers occupy 4-7, which the
        // presets hand to buttons. That overlap is why no button preset can describe a TECHNIKA
        // chart: laid out as 5B, lane 0 vanishes into an 18px spacer and lane 1 becomes BGA SYNC.
        private const int TrackFirstTechnikaLane = 0;
        private const int TechnikaLaneCount = 4;
        private const int TrackFirstScanMarker = 4;

        // A column that draws no source track at all (the TECHNIKA spacer).
        private const int TrackNone = -1;

        private readonly ReadOnlyCollection<VerticalColumn> _columns;
        private readonly Dictionary<int, VerticalColumn> _bySourceTrack;

        private VerticalTrackLayout(int mode, IList<VerticalColumn> columns)
        {
            Mode = mode;
            DisplayName = IsTechnikaMode(mode) ? "TECHNIKA" : mode + "B";
            _columns = new ReadOnlyCollection<VerticalColumn>(columns);
            _bySourceTrack = new Dictionary<int, VerticalColumn>();
            int total = 0;
            int gameplay = 0;
            int overflow = 0;
            foreach (VerticalColumn column in columns)
            {
                if (column.NativeRight > total)
                {
                    total = column.NativeRight;
                }
                if (IsGameplaySpan(column.Kind) && column.NativeRight > gameplay)
                {
                    gameplay = column.NativeRight;
                }
                if (column.Kind == VerticalColumnKind.Overflow)
                {
                    overflow++;
                }
                // Every column owns a distinct source track, so the first writer wins
                // and the guard only documents that invariant.
                if (!_bySourceTrack.ContainsKey(column.SourceTrackId))
                {
                    _bySourceTrack[column.SourceTrackId] = column;
                }
            }
            NativeWidth = total;
            GameplayNativeWidth = gameplay;
            OverflowColumnCount = overflow;
        }

        /// <summary>
        /// 4, 5, 6, or 8 — the button-count mode this layout describes, or
        /// <see cref="TechnikaMode"/> for the TECHNIKA lane layout.
        /// </summary>
        public int Mode { get; private set; }

        /// <summary>Layout name for UI and diagnostics: "4B".."8B", or "TECHNIKA".</summary>
        public string DisplayName { get; private set; }

        public ReadOnlyCollection<VerticalColumn> Columns { get { return _columns; } }

        /// <summary>Total native width (sum of every column width).</summary>
        public int NativeWidth { get; private set; }

        /// <summary>
        /// Native width of the leading gameplay span: the spacer, SIDE L, both
        /// shoulders, every button, and SIDE R. A narrow dock fits this span first and
        /// scrolls horizontally to reach the BGA SYNC / MR / BG columns.
        /// </summary>
        public int GameplayNativeWidth { get; private set; }

        /// <summary>
        /// How many <see cref="VerticalColumnKind.Overflow"/> columns were appended past the
        /// preset. 0 for any chart that stays inside the ptSequencer schema, so a caller can
        /// tell "this chart authors outside the presets" from "this is a stock 4B layout".
        /// </summary>
        public int OverflowColumnCount { get; private set; }

        /// <summary>
        /// Sentinel <see cref="Mode"/> for the TECHNIKA layout. Negative on purpose: it is not a
        /// button count, so it cannot collide with a detected 4B/5B/6B/8B mode, with the 0 that
        /// means "this chart has no vertical layout", or with a key count added later.
        /// </summary>
        public const int TechnikaMode = -1;

        /// <summary>
        /// True for the four ptSequencer button presets. TECHNIKA is deliberately not one of
        /// them: callers that mean "which DPC preset is this" (the Respect theme renderers, the
        /// key-count presets) must keep answering no for it.
        /// </summary>
        public static bool IsSupportedMode(int mode)
        {
            return mode == 4 || mode == 5 || mode == 6 || mode == 8;
        }

        public static bool IsTechnikaMode(int mode)
        {
            return mode == TechnikaMode;
        }

        /// <summary>
        /// True for every layout <see cref="ForMode(int)"/> can build: the four button presets
        /// plus TECHNIKA. This is the test for "can the vertical surface show this", which is a
        /// wider question than "is this a DPC preset".
        /// </summary>
        public static bool IsSupportedLayout(int mode)
        {
            return IsSupportedMode(mode) || IsTechnikaMode(mode);
        }

        /// <summary>The bare ptSequencer preset (or TECHNIKA layout) for <paramref name="mode"/>.</summary>
        public static VerticalTrackLayout ForMode(int mode)
        {
            return ForMode(mode, null);
        }

        /// <summary>
        /// The preset for <paramref name="mode"/> plus one appended
        /// <see cref="VerticalColumnKind.Overflow"/> column per id in
        /// <paramref name="extraSourceTracks"/> the preset does not already name, in ascending
        /// track order.
        /// </summary>
        /// <remarks>
        /// Appended rather than interleaved on purpose: a column's ordinal is what a projected
        /// <c>TimelineItem.RowIndex</c> carries and what the theme's per-lane chip colours are
        /// picked by, so inserting track 12 between SIDE R and BGA SYNC would silently renumber
        /// every utility column. Appending leaves every preset index exactly where it was, and
        /// the extra columns sit past BG 18 where the horizontal scroll already reaches.
        /// </remarks>
        public static VerticalTrackLayout ForMode(int mode, IEnumerable<int> extraSourceTracks)
        {
            if (IsTechnikaMode(mode))
            {
                return TechnikaLayout(extraSourceTracks);
            }

            if (!IsSupportedMode(mode))
            {
                throw new ArgumentOutOfRangeException(
                    "mode", mode, "Vertical layout supports only 4B/5B/6B/8B and TECHNIKA.");
            }

            int buttonCount = mode == 8 ? 6 : mode;
            VerticalColumnStyle[] buttonStyles = ButtonStyles(mode);

            var columns = new List<VerticalColumn>();
            int index = 0;
            int left = 0;

            // Leading unused spacer column ("nothing1").
            Add(columns, ref index, ref left, VerticalColumnKind.LeadingUnused,
                VerticalColumnStyle.Utility, "nothing1", TrackUnused, LeadingWidth, 1);

            // Left side track.
            Add(columns, ref index, ref left, VerticalColumnKind.SideLeft,
                VerticalColumnStyle.SideOrMr, "SIDE L", TrackSideLeft, GameplayWidth, 1);

            // Left shoulder (8B only).
            if (mode == 8)
            {
                Add(columns, ref index, ref left, VerticalColumnKind.ShoulderLeft,
                    VerticalColumnStyle.Shoulder, "L1", TrackShoulderLeft, GameplayWidth, 1);
            }

            // Gameplay buttons.
            for (int i = 0; i < buttonCount; i++)
            {
                Add(columns, ref index, ref left, VerticalColumnKind.Button,
                    buttonStyles[i], "button" + (i + 1), TrackFirstButton + i,
                    GameplayWidth, i == 0 ? 1 : 0);
            }

            // Right shoulder (8B only).
            if (mode == 8)
            {
                Add(columns, ref index, ref left, VerticalColumnKind.ShoulderRight,
                    VerticalColumnStyle.Shoulder, "R1", TrackShoulderRight, GameplayWidth, 1);
            }

            // Right side track.
            Add(columns, ref index, ref left, VerticalColumnKind.SideRight,
                VerticalColumnStyle.SideOrMr, "SIDE R", TrackSideRight, GameplayWidth, 1);

            // BGA sync marker column.
            Add(columns, ref index, ref left, VerticalColumnKind.BgaSync,
                VerticalColumnStyle.Utility, "BGA SYNC", TrackBgaSync, UtilityWidth, 1);

            // MR (master audio reference).
            Add(columns, ref index, ref left, VerticalColumnKind.Mr,
                VerticalColumnStyle.SideOrMr, "MR", TrackMr, UtilityWidth, 3);

            // Background layers BG 1..18.
            for (int i = 0; i < BackgroundCount; i++)
            {
                Add(columns, ref index, ref left, VerticalColumnKind.Background,
                    VerticalColumnStyle.Utility, "BG " + (i + 1), TrackFirstBackground + i,
                    UtilityWidth, 0);
            }

            // Anything the chart authors outside the preset.
            AddOverflowColumns(columns, ref index, ref left, extraSourceTracks);

            return new VerticalTrackLayout(mode, columns);
        }

        /// <summary>
        /// The TECHNIKA layout: an 18px spacer, the four playable lanes (source tracks 0-3), the
        /// four end-of-scan marker tracks (4-7), and one appended column per occupied track from 8
        /// up. Every keysound and accompaniment track therefore gets a column of its own.
        /// </summary>
        /// <remarks>
        /// This is not a ptSequencer preset, because the arcade never had one: TECHNIKA is a
        /// touch game with four lanes read left-to-right across a scan, so it has no side tracks,
        /// no shoulders, and no fixed MR/BG block. The Portable schema collides with it on both
        /// ends - track 0 is a lane here and the unused spacer there, tracks 4-7 are scan markers
        /// here and buttons there, and a TECHNIKA chart's accompaniment sits wherever the author
        /// put it rather than on 22-40 - so the honest layout names only what TECHNIKA defines and
        /// lets the chart's own occupied tracks decide the rest.
        /// </remarks>
        private static VerticalTrackLayout TechnikaLayout(IEnumerable<int> extraSourceTracks)
        {
            var columns = new List<VerticalColumn>();
            int index = 0;
            int left = 0;

            // Leading spacer. Unlike the DPC presets this claims no source track: track 0 is
            // TECHNIKA's first playable lane, and letting an 18px dead column swallow it is
            // exactly what hid a quarter of the notes.
            Add(columns, ref index, ref left, VerticalColumnKind.LeadingUnused,
                VerticalColumnStyle.Utility, "nothing1", TrackNone, LeadingWidth, 1);

            // The four touch lanes. TECHNIKA has no DPC striping to copy, so they simply
            // alternate, which keeps neighbouring lanes distinguishable at any column scale.
            for (int i = 0; i < TechnikaLaneCount; i++)
            {
                Add(columns, ref index, ref left, VerticalColumnKind.Button,
                    (i % 2) == 0 ? VerticalColumnStyle.RegularPrimary : VerticalColumnStyle.RegularAlternate,
                    "lane" + (i + 1), TrackFirstTechnikaLane + i, GameplayWidth, i == 0 ? 1 : 0);
            }

            // One end-of-scan marker track per lane, in lane order.
            for (int i = 0; i < TechnikaLaneCount; i++)
            {
                Add(columns, ref index, ref left, VerticalColumnKind.ScanMarker,
                    VerticalColumnStyle.Utility, "EOS " + (i + 1), TrackFirstScanMarker + i,
                    UtilityWidth, 0);
            }

            // Keysounds, accompaniment, and anything else the chart authors from track 8 up.
            AddOverflowColumns(columns, ref index, ref left, extraSourceTracks);

            return new VerticalTrackLayout(TechnikaMode, columns);
        }

        /// <summary>
        /// Appends one utility-width column per unmapped source track, ascending, ignoring
        /// duplicates, negatives, and ids the preset already covers.
        /// </summary>
        private static void AddOverflowColumns(
            IList<VerticalColumn> columns,
            ref int index,
            ref int left,
            IEnumerable<int> extraSourceTracks)
        {
            if (extraSourceTracks == null)
            {
                return;
            }

            var claimed = new HashSet<int>();
            foreach (VerticalColumn column in columns)
            {
                claimed.Add(column.SourceTrackId);
            }

            var extras = new List<int>();
            foreach (int sourceTrackId in extraSourceTracks)
            {
                if (sourceTrackId < 0 || claimed.Contains(sourceTrackId))
                {
                    continue;
                }
                claimed.Add(sourceTrackId);
                extras.Add(sourceTrackId);
            }
            extras.Sort();

            foreach (int sourceTrackId in extras)
            {
                Add(columns, ref index, ref left, VerticalColumnKind.Overflow,
                    VerticalColumnStyle.Utility, "TRK " + sourceTrackId, sourceTrackId,
                    UtilityWidth, 0);
            }
        }

        /// <summary>Maps a chart lane count to a supported mode, or 0 if unsupported.</summary>
        public static int NormalizeMode(int laneCount)
        {
            return IsSupportedMode(laneCount) ? laneCount : 0;
        }

        public VerticalColumn ColumnForSourceTrack(int sourceTrackId)
        {
            VerticalColumn column;
            return _bySourceTrack.TryGetValue(sourceTrackId, out column) ? column : null;
        }

        /// <summary>Returns the column whose native span contains <paramref name="nativeX"/>, or null.</summary>
        public VerticalColumn ColumnAtNativeX(double nativeX)
        {
            foreach (VerticalColumn column in _columns)
            {
                if (nativeX >= column.NativeLeft && nativeX < column.NativeRight)
                {
                    return column;
                }
            }
            return null;
        }

        /// <summary>
        /// True for the columns a player actually reads: the spacer plus every side,
        /// shoulder, and button lane, and on TECHNIKA the scan markers that belong to those
        /// lanes. BGA SYNC, MR, BG 1-18, and appended tracks are authoring aids.
        /// </summary>
        private static bool IsGameplaySpan(VerticalColumnKind kind)
        {
            return kind == VerticalColumnKind.LeadingUnused ||
                kind == VerticalColumnKind.SideLeft ||
                kind == VerticalColumnKind.ShoulderLeft ||
                kind == VerticalColumnKind.Button ||
                kind == VerticalColumnKind.ShoulderRight ||
                kind == VerticalColumnKind.SideRight ||
                kind == VerticalColumnKind.ScanMarker;
        }

        private static VerticalColumnStyle[] ButtonStyles(int mode)        {
            switch (mode)
            {
                case 4:
                    return new[]
                    {
                        VerticalColumnStyle.RegularPrimary,
                        VerticalColumnStyle.RegularAlternate,
                        VerticalColumnStyle.RegularAlternate,
                        VerticalColumnStyle.RegularPrimary
                    };
                case 5:
                    return new[]
                    {
                        VerticalColumnStyle.RegularPrimary,
                        VerticalColumnStyle.RegularAlternate,
                        VerticalColumnStyle.RegularPrimary,
                        VerticalColumnStyle.RegularAlternate,
                        VerticalColumnStyle.RegularPrimary
                    };
                default:
                    // 6B and 8B share the same six-button striping.
                    return new[]
                    {
                        VerticalColumnStyle.RegularPrimary,
                        VerticalColumnStyle.RegularAlternate,
                        VerticalColumnStyle.RegularPrimary,
                        VerticalColumnStyle.RegularPrimary,
                        VerticalColumnStyle.RegularAlternate,
                        VerticalColumnStyle.RegularPrimary
                    };
            }
        }

        private static void Add(
            IList<VerticalColumn> columns,
            ref int index,
            ref int left,
            VerticalColumnKind kind,
            VerticalColumnStyle style,
            string name,
            int sourceTrackId,
            int width,
            int bold)
        {
            columns.Add(new VerticalColumn(
                index, kind, style, name, sourceTrackId, width, bold, left));
            index++;
            left += width;
        }
    }
}
