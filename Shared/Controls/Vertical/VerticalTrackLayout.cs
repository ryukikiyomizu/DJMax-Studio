using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

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
            : this(index, kind, style, name, null, sourceTrackId, width, bold, nativeLeft)
        {
        }

        /// <summary>
        /// As the other constructor, but with an explicit <paramref name="shortName"/> for a column
        /// whose header label cannot be derived from its kind. The BMS layout needs it: its
        /// turntable is a <see cref="VerticalColumnKind.SideLeft"/> column that has to read "SC",
        /// and its timing column is a <see cref="VerticalColumnKind.BgaSync"/> one that reads "BPM".
        /// </summary>
        public VerticalColumn(
            int index,
            VerticalColumnKind kind,
            VerticalColumnStyle style,
            string name,
            string shortName,
            int sourceTrackId,
            int width,
            int bold,
            int nativeLeft)
            : this(index, kind, style, name, shortName, sourceTrackId, width, bold, nativeLeft, false)
        {
        }

        /// <summary>
        /// As above, and marks the column as a turntable. Only the BMS layout sets it, and only for
        /// its scratch lanes; see <see cref="IsScratch"/> for what reads it.
        /// </summary>
        public VerticalColumn(
            int index,
            VerticalColumnKind kind,
            VerticalColumnStyle style,
            string name,
            string shortName,
            int sourceTrackId,
            int width,
            int bold,
            int nativeLeft,
            bool isScratch)
        {
            Index = index;
            Kind = kind;
            Style = style;
            Name = name;
            ShortName = string.IsNullOrEmpty(shortName) ? DeriveShortName(kind, name) : shortName;
            SourceTrackId = sourceTrackId;
            Width = width;
            Bold = bold;
            NativeLeft = nativeLeft;
            IsScratch = isScratch;
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
        /// Whether this column is a BMS turntable.
        ///
        /// <para>
        /// A turntable reuses <see cref="VerticalColumnKind.SideLeft"/>/<see cref="VerticalColumnKind.SideRight"/>
        /// so the playable-lane rules already cover it, which means the kind cannot tell it apart from
        /// a DJMax SIDE lane. <c>DisplayName</c> reads this to put the "+SC" in "BMS 7K+SC". What makes
        /// a scratch look like one is the column's own <see cref="Width"/> - the same branch that sets
        /// this flag draws it wider than a key - rather than anything the frame does to an item: a
        /// scratch is a flick of the whole hand and reads wrong drawn as the same lane as a keypress,
        /// but one that really is a long note still has to draw its own length, so the difference lives
        /// on the axis that is not time.
        /// </para>
        /// </summary>
        public bool IsScratch { get; private set; }

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
        /// A BMS turntable is the one column that is none of those: 78, a key plus a spacer.
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
    /// columns for a 4B/5B/6B/8B chart, of the TECHNIKA lane layout, or of a classic BMS chart's own
    /// channels. Both the V1 editor
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

        /// <summary>
        /// A BMS turntable is drawn half again as wide as a key. On a cabinet it is a platter under
        /// the whole hand rather than a key under one finger, and the strip has to say which lane
        /// that is without being read - beatoraja's own playfields give the scratch column that
        /// same 1.5x over a key lane, which is the reference anyone opening a .bms knows. Width
        /// and not height: a scratch that really is a long note has to keep drawing its own
        /// duration, so the difference has to live on the axis that is not time.
        /// </summary>
        private const int BmsScratchWidth = (GameplayWidth * 3) / 2;

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

        // Classic BMS channels the layout places by name. Lane digits inside the two playable
        // families are read by BmsKeyNumber; 6 is the turntable and 7 the (rare) foot pedal.
        private const string BmsBgmChannel = "01";
        private const string BmsTempoChannel = "08";
        private const string BmsTempoRawChannel = "03";
        private const int BmsScratchLane = 6;
        private const int BmsPedalLane = 7;

        // Sort keys for BMS playing order: first player's side, second player's side, then the
        // non-playable columns. Only the relative order matters.
        private const int BmsFirstSideOrder = 100;
        private const int BmsSecondSideOrder = 200;
        private const int BmsSecondScratchOrder = 290;
        private const int BmsBgmOrder = 900;
        private const int BmsTempoOrder = 910;
        private const int BmsUnknownOrder = 950;

        private readonly ReadOnlyCollection<VerticalColumn> _columns;
        private readonly Dictionary<int, VerticalColumn> _bySourceTrack;

        private VerticalTrackLayout(int mode, IList<VerticalColumn> columns)
            : this(mode, columns, null)
        {
        }

        /// <summary>
        /// <paramref name="displayName"/> overrides the derived name for a layout whose shape is not
        /// a button count - the BMS layout reads its key/scratch columns back out to say "BMS 7K+SC".
        /// </summary>
        private VerticalTrackLayout(int mode, IList<VerticalColumn> columns, string displayName)
        {
            Mode = mode;
            DisplayName = displayName ?? (IsTechnikaMode(mode) ? "TECHNIKA" : mode + "B");
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
        /// <see cref="TechnikaMode"/> for the TECHNIKA lane layout, or <see cref="BmsMode"/> for a
        /// BMS one.
        /// </summary>
        public int Mode { get; private set; }

        /// <summary>
        /// Layout name for UI and diagnostics: "4B".."8B", "TECHNIKA", or the BMS shape the chart's
        /// channels describe ("BMS 7K+SC", "BMS 9K", "BMS DP 14K").
        /// </summary>
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
        /// Sentinel <see cref="Mode"/> for the classic-BMS layout: keys and turntable read off the
        /// chart's own <c>#mmmCC</c> channels instead of a fixed preset.
        /// </summary>
        /// <remarks>
        /// A second negative sentinel rather than a key count, for the same reason TECHNIKA got one -
        /// "BMS" is a channel schema, not a button count, and a .bms can be 5K, 7K+SC, 9-button PMS
        /// or 14K DP without changing format. The layout itself is built per chart by
        /// <see cref="ForBms"/>, so the sentinel only says "read the channels", never how many.
        /// </remarks>
        public const int BmsMode = -2;

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

        public static bool IsBmsMode(int mode)
        {
            return mode == BmsMode;
        }

        /// <summary>
        /// True for every layout <see cref="ForMode(int)"/> can build: the four button presets
        /// plus TECHNIKA and BMS. This is the test for "can the vertical surface show this", which
        /// is a wider question than "is this a DPC preset".
        /// </summary>
        public static bool IsSupportedLayout(int mode)
        {
            return IsSupportedMode(mode) || IsTechnikaMode(mode) || IsBmsMode(mode);
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
            return ForMode(mode, extraSourceTracks, null);
        }

        /// <summary>As the two-argument overload, with custom labels for overflow columns,
        /// keyed by source track id. A .tech overflow column names its in-game format lane
        /// rather than its compacted model track ("lane 6", not "TRK 9").</summary>
        public static VerticalTrackLayout ForMode(
            int mode,
            IEnumerable<int> extraSourceTracks,
            IDictionary<int, string> overflowLabels)
        {
            if (IsTechnikaMode(mode))
            {
                return TechnikaLayout(extraSourceTracks, overflowLabels);
            }

            // BMS columns come from the chart's channels, which a bare mode does not carry. Callers
            // that only have a mode - PreferredWidthForMode sizing the dock, for one - still need an
            // answer, so they get the shape of a stock 7K+SC chart as read by BmsChartSerializer
            // (BGM on track 0, lanes 11-19 on 1-8, tempo last). VerticalTimelineProjection never
            // takes this path: it always builds from the opened chart's own TrackChannels.
            if (IsBmsMode(mode))
            {
                return ForBms(DefaultBmsTrackChannels(), extraSourceTracks);
            }

            if (!IsSupportedMode(mode))
            {
                throw new ArgumentOutOfRangeException(
                    "mode", mode, "Vertical layout supports only 4B/5B/6B/8B, TECHNIKA and BMS.");
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
            AddOverflowColumns(columns, ref index, ref left, extraSourceTracks, overflowLabels);

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
        private static VerticalTrackLayout TechnikaLayout(
            IEnumerable<int> extraSourceTracks,
            IDictionary<int, string> overflowLabels)
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
            AddOverflowColumns(columns, ref index, ref left, extraSourceTracks, overflowLabels);

            return new VerticalTrackLayout(TechnikaMode, columns);
        }

        /// <summary>
        /// The layout for a classic BMS chart: an 18px spacer, one gameplay column per playable
        /// channel in playing order (turntable, keys 1..n, the second player's side if the chart has
        /// one), then the BGM and timing columns, then anything authored on a track with no channel.
        /// </summary>
        /// <remarks>
        /// Channel-driven rather than preset-driven because BMS has no single preset: 11-15 with
        /// 18/19 is seven keys, 16 is the turntable, 11-15 with 22-25 is a nine-button PMS chart, and
        /// 21-29 is a second player's side. Reading the chart's own channels is also the only way to
        /// place the turntable: <c>BmsChartSerializer</c> orders its lane tracks by base36 channel
        /// value, which drops scratch (16) between keys 5 and 6, while the game and every BMS editor
        /// draw it outside key 1.
        ///
        /// The reused kinds are deliberate. A turntable is a <see cref="VerticalColumnKind.SideLeft"/>
        /// column - a 60px non-button gameplay lane on the outside, with its own shading, which the
        /// note-art and volume-lane rules already count as playable - and the second player's is the
        /// <see cref="VerticalColumnKind.SideRight"/> mirror. Keysound BGM is
        /// <see cref="VerticalColumnKind.Background"/>, and timing is
        /// <see cref="VerticalColumnKind.BgaSync"/>, the utility marker kind, which is the behaviour
        /// BPM changes want: no note art and no volume lane. Explicit short names keep the header
        /// strip honest ("SC", "BPM") where the derived ones would read "SL" and "BGA".
        /// </remarks>
        public static VerticalTrackLayout ForBms(
            IEnumerable<KeyValuePair<int, string>> trackChannels,
            IEnumerable<int> extraSourceTracks)
        {
            bool pms;
            List<BmsColumnPlan> plans = PlanBmsColumns(trackChannels, out pms);

            var columns = new List<VerticalColumn>();
            int index = 0;
            int left = 0;

            // Leading spacer, claiming no source track. BMS track ids start at 0 and mean whatever
            // the channel map says, so a spacer that swallowed track 0 would hide a lane.
            Add(columns, ref index, ref left, VerticalColumnKind.LeadingUnused,
                VerticalColumnStyle.Utility, "nothing1", TrackNone, LeadingWidth, 1);

            foreach (BmsColumnPlan plan in plans)
            {
                columns.Add(new VerticalColumn(index, plan.Kind, plan.Style, plan.Name,
                    plan.ShortName, plan.SourceTrackId, plan.Width, plan.Bold, left,
                    plan.IsScratch));
                index++;
                left += plan.Width;
            }

            AddOverflowColumns(columns, ref index, ref left, extraSourceTracks, null);

            return new VerticalTrackLayout(BmsMode, columns, BmsDisplayName(plans, pms));
        }

        /// <summary>
        /// Turns a track-to-channel map into planned columns in playing order, dropping entries with
        /// a negative track id, an unreadable channel, or a track already planned.
        /// </summary>
        private static List<BmsColumnPlan> PlanBmsColumns(
            IEnumerable<KeyValuePair<int, string>> trackChannels, out bool pms)
        {
            pms = false;
            var plans = new List<BmsColumnPlan>();
            if (trackChannels == null)
            {
                return plans;
            }

            var pairs = new List<KeyValuePair<int, string>>();
            var channels = new List<string>();
            var seen = new HashSet<int>();
            foreach (KeyValuePair<int, string> pair in trackChannels)
            {
                string channel = NormalizeBmsChannel(pair.Value);
                if (pair.Key < 0 || channel == null || !seen.Add(pair.Key))
                {
                    continue;
                }
                pairs.Add(new KeyValuePair<int, string>(pair.Key, channel));
                channels.Add(channel);
            }

            pms = IsPmsShaped(channels);
            foreach (KeyValuePair<int, string> pair in pairs)
            {
                plans.Add(PlanBmsColumn(pair.Key, pair.Value, pms));
            }

            // List.Sort is unstable, so ties carry the chart's own track order explicitly rather
            // than whatever the sort happens to do with them.
            for (int i = 0; i < plans.Count; i++)
            {
                plans[i].Sequence = i;
            }
            plans.Sort(delegate(BmsColumnPlan a, BmsColumnPlan b)
            {
                return a.Order != b.Order ? a.Order - b.Order : a.Sequence - b.Sequence;
            });
            NumberBmsBgmColumns(plans);
            return plans;
        }

        /// <summary>
        /// Numbers the BGM columns "BGM 1".."BGM n" once there is more than one of them.
        /// </summary>
        /// <remarks>
        /// Applied after planning rather than inside it because the label depends on how many there
        /// are: a chart with one accompaniment voice reads "BGM", the same as it always did, and only
        /// a chart with several needs telling apart. The short names follow the "B12" convention the
        /// DJMax background columns already use, so a header strip too narrow for "BGM 3" still says
        /// which voice it is.
        /// </remarks>
        private static void NumberBmsBgmColumns(List<BmsColumnPlan> plans)
        {
            int total = 0;
            for (int i = 0; i < plans.Count; i++)
            {
                if (plans[i].Order == BmsBgmOrder) total++;
            }
            if (total < 2) return;

            int slot = 0;
            for (int i = 0; i < plans.Count; i++)
            {
                if (plans[i].Order != BmsBgmOrder) continue;
                slot++;
                string ordinal = slot.ToString(CultureInfo.InvariantCulture);
                plans[i].Name = "BGM " + ordinal;
                plans[i].ShortName = "B" + ordinal;
            }
        }

        /// <summary>Plans one column from its BMS channel.</summary>
        private static BmsColumnPlan PlanBmsColumn(int sourceTrackId, string channel, bool pms)
        {
            var plan = new BmsColumnPlan();
            plan.SourceTrackId = sourceTrackId;
            plan.Width = UtilityWidth;
            plan.Style = VerticalColumnStyle.Utility;
            plan.Name = "CH " + channel;
            plan.ShortName = channel;

            if (channel == BmsBgmChannel)
            {
                plan.Order = BmsBgmOrder;
                plan.Kind = VerticalColumnKind.Background;
                plan.Name = "BGM";
                plan.ShortName = "BGM";
                plan.Bold = 1;
                return plan;
            }

            if (channel == BmsTempoChannel || channel == BmsTempoRawChannel)
            {
                plan.Order = BmsTempoOrder;
                plan.Kind = VerticalColumnKind.BgaSync;
                plan.Name = "BPM";
                plan.ShortName = "BPM";
                plan.Bold = 1;
                return plan;
            }

            bool second = channel[0] == '2';
            int lane = Base36Value(channel[1]);
            if ((channel[0] != '1' && !second) || lane < 1)
            {
                // Not a playable channel at all. The reader keeps those as PreservedDataLines rather
                // than tracks, so this only fires for a hand-built map - it still gets a column.
                plan.Order = BmsUnknownOrder + (Base36Value(channel[0]) * 36) + Math.Max(0, lane);
                plan.Kind = VerticalColumnKind.Overflow;
                return plan;
            }

            plan.Width = GameplayWidth;
            plan.SecondSide = second && !pms;
            int side = second ? BmsSecondSideOrder : BmsFirstSideOrder;
            // In PMS the second channel family is still the same player's right hand, so it must not
            // be labelled as a second player.
            string prefix = plan.SecondSide ? "2P " : string.Empty;

            int key = BmsKeyNumber(lane, pms, second);
            if (key > 0)
            {
                plan.Order = side + 10 + key;
                plan.Kind = VerticalColumnKind.Button;
                plan.Style = (key % 2) == 1
                    ? VerticalColumnStyle.RegularPrimary
                    : VerticalColumnStyle.RegularAlternate;
                plan.Name = prefix + "KEY " + key.ToString(CultureInfo.InvariantCulture);
                plan.ShortName = key.ToString(CultureInfo.InvariantCulture);
                plan.Bold = key == 1 ? 1 : 0;
                plan.IsKey = true;
                return plan;
            }

            if (!pms && lane == BmsScratchLane)
            {
                // Outside key 1 on the first player's side and outside key 7 on the second's, which
                // is where the two turntables sit on a DP cabinet.
                plan.Order = second ? BmsSecondScratchOrder : BmsFirstSideOrder;
                plan.Kind = second ? VerticalColumnKind.SideRight : VerticalColumnKind.SideLeft;
                plan.Style = VerticalColumnStyle.SideOrMr;
                plan.Name = prefix + "SCRATCH";
                plan.ShortName = "SC";
                plan.Bold = 1;
                plan.Width = BmsScratchWidth;
                plan.IsScratch = true;
                return plan;
            }

            if (!pms && lane == BmsPedalLane)
            {
                plan.Order = side + 50;
                plan.Kind = second ? VerticalColumnKind.ShoulderRight : VerticalColumnKind.ShoulderLeft;
                plan.Style = VerticalColumnStyle.Shoulder;
                plan.Name = prefix + "PEDAL";
                plan.ShortName = "FP";
                plan.Bold = 1;
                return plan;
            }

            // A playable channel outside the schemas above - the 1A/2B extension lanes. It is a lane,
            // so it gets a lane, labelled by the channel that authored it.
            plan.Order = side + 60 + lane;
            plan.Kind = VerticalColumnKind.Button;
            plan.Style = VerticalColumnStyle.RegularAlternate;
            return plan;
        }

        /// <summary>
        /// The key number a lane digit carries, or 0 when the lane is not a key (turntable, pedal,
        /// or an extension channel).
        /// </summary>
        private static int BmsKeyNumber(int lane, bool pms, bool secondFamily)
        {
            if (pms)
            {
                // Nine-button PMS: keys 1-5 on 11-15, keys 6-9 on 22-25.
                if (!secondFamily)
                {
                    return lane >= 1 && lane <= 5 ? lane : 0;
                }
                return lane >= 2 && lane <= 5 ? lane + 4 : 0;
            }

            if (lane >= 1 && lane <= 5) return lane;
            if (lane == 8) return 6;
            if (lane == 9) return 7;
            return 0;
        }

        /// <summary>
        /// Whether a channel set is a nine-button PMS chart rather than a chart with a second player's
        /// side: the second channel family carries only lanes 2-5, and the first has neither a
        /// turntable nor the 18/19 keys a seven-key chart would use.
        /// </summary>
        private static bool IsPmsShaped(IList<string> channels)
        {
            bool secondFamilyKey = false;
            foreach (string channel in channels)
            {
                if (channel[0] == '2')
                {
                    int lane = Base36Value(channel[1]);
                    if (lane < 2 || lane > 5) return false;
                    secondFamilyKey = true;
                }
                else if (channel == "16" || channel == "17" || channel == "18" || channel == "19")
                {
                    return false;
                }
            }
            return secondFamilyKey;
        }

        /// <summary>Layout name from what the plan actually contains: "BMS 7K+SC", "BMS 9K", "BMS DP 14K".</summary>
        private static string BmsDisplayName(IList<BmsColumnPlan> plans, bool pms)
        {
            int keys = 0;
            bool scratch = false;
            bool secondSide = false;
            foreach (BmsColumnPlan plan in plans)
            {
                if (plan.IsKey) keys++;
                if (plan.IsScratch) scratch = true;
                if (plan.SecondSide) secondSide = true;
            }

            if (keys == 0) return "BMS";
            string count = keys.ToString(CultureInfo.InvariantCulture) + "K";
            if (pms) return "BMS " + count;
            if (secondSide) return "BMS DP " + count;
            return "BMS " + count + (scratch ? "+SC" : string.Empty);
        }

        /// <summary>
        /// Two characters, upper case, with the long-note families folded onto the lanes they extend.
        /// Returns null for anything that is not a channel id.
        /// </summary>
        private static string NormalizeBmsChannel(string channel)
        {
            if (channel == null) return null;
            string text = channel.Trim().ToUpperInvariant();
            if (text.Length != 2) return null;

            // BmsChartSerializer already folds 5x/6x into 1x/2x when it names a track, because both
            // families address the same lane; folding again keeps a hand-built map honest.
            if (text[0] == '5') return "1" + text[1];
            if (text[0] == '6') return "2" + text[1];
            return text;
        }

        private static int Base36Value(char value)
        {
            if (value >= '0' && value <= '9') return value - '0';
            char upper = char.ToUpperInvariant(value);
            return upper >= 'A' && upper <= 'Z' ? 10 + (upper - 'A') : -1;
        }

        /// <summary>
        /// The map <c>BmsChartSerializer</c> produces for a stock 7K+SC chart, used only when a caller
        /// asks for the BMS layout by mode and so has no chart to read channels from.
        /// </summary>
        private static List<KeyValuePair<int, string>> DefaultBmsTrackChannels()
        {
            string[] lanes = { "11", "12", "13", "14", "15", "16", "18", "19" };
            var map = new List<KeyValuePair<int, string>>();
            map.Add(new KeyValuePair<int, string>(0, BmsBgmChannel));
            for (int i = 0; i < lanes.Length; i++)
            {
                map.Add(new KeyValuePair<int, string>(i + 1, lanes[i]));
            }
            map.Add(new KeyValuePair<int, string>(lanes.Length + 1, BmsTempoChannel));
            return map;
        }

        /// <summary>One planned BMS column: what to draw, and where it sorts in playing order.</summary>
        private sealed class BmsColumnPlan
        {
            public int SourceTrackId;
            public int Order;
            public int Sequence;
            public VerticalColumnKind Kind;
            public VerticalColumnStyle Style;
            public string Name;
            public string ShortName;
            public int Width;
            public int Bold;
            public bool IsKey;
            public bool IsScratch;
            public bool SecondSide;
        }


        /// <summary>
        /// Appends one utility-width column per unmapped source track, ascending, ignoring
        /// duplicates, negatives, and ids the preset already covers.
        /// </summary>
        private static void AddOverflowColumns(
            IList<VerticalColumn> columns,
            ref int index,
            ref int left,
            IEnumerable<int> extraSourceTracks,
            IDictionary<int, string> overflowLabels)
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
                string label;
                if (overflowLabels == null ||
                    !overflowLabels.TryGetValue(sourceTrackId, out label) ||
                    string.IsNullOrEmpty(label))
                {
                    label = "TRK " + sourceTrackId;
                }
                Add(columns, ref index, ref left, VerticalColumnKind.Overflow,
                    VerticalColumnStyle.Utility, label, sourceTrackId,
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
