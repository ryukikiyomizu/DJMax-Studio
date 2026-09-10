using System.Collections.Generic;

namespace DJMaxEditor.Studio.Design
{
    /// <summary>
    /// One chart-canvas palette, as data: hex strings and one behavioural flag, no brushes.
    ///
    /// <para>
    /// This is the seam <c>docs/bms-theme-research.md</c> asked for (its Phase 1/2). Until it
    /// existed, the canvas colours were <c>const string</c> members of <see cref="StudioPalette"/>
    /// read straight out of <c>StudioTimelineTheme</c>'s constructor, which is the "Generation 2 -
    /// hardcoded palettes per surface" problem the research doc describes: they could only change
    /// at compile time, and there was no single object a theme switcher could point at.
    /// </para>
    /// <para>
    /// Two rules follow from that document and are enforced by the shape of this class:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// <b>Data, not code, and not brushes.</b> A theme is a bag of strings so it can later come
    /// from a JSON file (the doc's Option A) without a second model, and so the frozen brush sets
    /// are built in exactly one place - <see cref="DJMaxEditor.Studio.Timeline.StudioTimelineTheme.ForTheme"/> - which
    /// is where the "build once, freeze, reuse" discipline lives. Nothing here allocates a WPF
    /// object, so a theme can be constructed, compared and listed without a Dispatcher.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <b>Fallback inheritance.</b> Every property's initialiser is the Studio value, so a theme
    /// lists only what it changes and everything else resolves to the built-in look - StepMania's
    /// <c>_fallback</c> mechanism, expressed as C# property defaults instead of a metrics file.
    /// That is also why switching to a theme that sets six values cannot leave a canvas half-drawn.
    /// </description>
    /// </item>
    /// </list>
    /// <para>
    /// Scope, stated because the boundary is deliberate: this themes the <em>chart canvas</em> -
    /// lane fields, grid, lane rules, notes and the playhead. It does not theme the shell chrome
    /// (<see cref="StudioPalette"/>'s surfaces and text stay put; the research doc keeps "editor
    /// chrome" a separate toggle from note art) and it does not theme the TECHNIKA gameplay
    /// playfield, whose <c>TechnikaPlayfieldTheme</c> colours are sampled from the arcade and are
    /// the point of that panel. A theme that recoloured the arcade's own note hues would make the
    /// preview lie about the game.
    /// </para>
    /// </summary>
    internal sealed class StudioChartTheme
    {
        /// <summary>Id of the built-in Studio palette. Also the settings default.</summary>
        public const string StudioId = "studio";

        /// <summary>Id of the built-in IIDX palette.</summary>
        public const string IidxId = "iidx";

        /// <summary>Id of the built-in RESPECT V palette.</summary>
        public const string RespectVId = "respectv";

        // ===================================================================================
        // Identity
        // ===================================================================================

        /// <summary>
        /// The stable identity, and the string that is persisted.
        ///
        /// Persisted by id rather than by <see cref="Name"/> for the same reason
        /// <c>TimelineSettings</c> stores a grid denominator instead of a list index: a display
        /// name is wording, and wording is allowed to improve without silently re-pointing every
        /// existing settings file at a different palette.
        /// </summary>
        public string Id { get; init; } = StudioId;

        /// <summary>What the picker shows on the row, in bold.</summary>
        public string Name { get; init; } = "Studio";

        /// <summary>
        /// The row's second line: what the theme is for, in a sentence. Names alone don't say that,
        /// which is the entire reason the picker exists next to the toolbar button.
        /// </summary>
        public string Description { get; init; } = string.Empty;

        // ===================================================================================
        // Lane fields
        // ===================================================================================

        /// <summary>Ordinary lane field. 90% of the canvas pixels; keep it flat.</summary>
        public string Field { get; init; } = StudioPalette.CanvasField;

        /// <summary>
        /// Field for a lane the layout marks <c>RegularAlternate</c> - ptSequencer's "striped
        /// gameplay-button shading", and exactly the white-key/blue-key stagger IIDX builds its
        /// whole reading out of (keys 1/3/5/7 white, 2/4/6 blue).
        ///
        /// Equal to <see cref="Field"/> by default, so themes that do not care draw the flat field
        /// they always drew.
        /// </summary>
        public string FieldAlternate { get; init; } = StudioPalette.CanvasField;

        /// <summary>
        /// Field for a turntable lane (<see cref="DJMaxEditor.Controls.Vertical.VerticalColumn.IsScratch"/>,
        /// and the SIDE L/SIDE R lanes of the DPC presets that stand in for one). IIDX's scratch
        /// lane is red; a dark red field is how that reads without a bright slab on the canvas.
        /// </summary>
        public string FieldScratch { get; init; } = StudioPalette.CanvasField;

        /// <summary>Field for a lane that cannot be played (spacer / disabled).</summary>
        public string FieldDead { get; init; } = StudioPalette.CanvasFieldDead;

        /// <summary>Field behind the background-screen lane group, one step off <see cref="Field"/>.</summary>
        public string FieldBackground { get; init; } = StudioPalette.CanvasFieldBackground;

        // ===================================================================================
        // Grid
        // ===================================================================================

        /// <summary>Sub-beat grid line.</summary>
        public string GridSub { get; init; } = StudioPalette.GridSub;

        /// <summary>Beat grid line.</summary>
        public string GridBeat { get; init; } = StudioPalette.GridBeat;

        /// <summary>Bar line - the one rule a chart author scans for.</summary>
        public string GridBar { get; init; } = StudioPalette.GridBar;

        // ===================================================================================
        // Lane rules and header chips
        // ===================================================================================

        /// <summary>Lane separator.</summary>
        public string LaneEdge { get; init; } = StudioPalette.LaneEdge;

        /// <summary>Heavy lane separator (bold=3 in a .pst preset, e.g. before MR).</summary>
        public string LaneEdgeStrong { get; init; } = StudioPalette.LaneEdgeStrong;

        /// <summary>
        /// Per-lane header chip colours, cycled by column index. Deliberately desaturated: ten
        /// coloured lanes still have to leave a calm canvas.
        /// </summary>
        public string[] LaneChips { get; init; } = StudioPalette.TrackChips;

        // ===================================================================================
        // Notes, coloured by attribute
        // ===================================================================================

        public string NotePlayable { get; init; } = StudioPalette.NotePlayable;
        public string NotePlayableEdge { get; init; } = StudioPalette.NotePlayableEdge;
        public string NoteAccent { get; init; } = StudioPalette.NoteAccent;
        public string NoteAccentEdge { get; init; } = StudioPalette.NoteAccentEdge;
        public string NoteLong { get; init; } = StudioPalette.NoteLong;
        public string NoteLongEdge { get; init; } = StudioPalette.NoteLongEdge;
        public string NoteBackground { get; init; } = StudioPalette.NoteBackground;
        public string NoteBackgroundEdge { get; init; } = StudioPalette.NoteBackgroundEdge;
        public string NoteBga { get; init; } = StudioPalette.NoteBga;
        public string NoteBgaEdge { get; init; } = StudioPalette.NoteBgaEdge;

        /// <summary>
        /// Selection fill and outline. Kept the same amber in every built-in on purpose: selection
        /// has to be readable as "the thing I grabbed" whatever the note colours are doing, and a
        /// theme that moved selection onto its own accent would make a selected note look like a
        /// different kind of note.
        /// </summary>
        public string NoteSelected { get; init; } = StudioPalette.NoteSelected;
        public string NoteSelectedEdge { get; init; } = StudioPalette.NoteSelectedEdge;

        /// <summary>Text drawn inside a note (keysound names). Has to contrast with the fills.</summary>
        public string TextOnNote { get; init; } = "#FFF2F5FA";

        // ===================================================================================
        // Notes, coloured by lane
        // ===================================================================================

        /// <summary>
        /// Whether playable lanes colour their notes by <em>lane role</em> instead of by note
        /// attribute.
        ///
        /// <para>
        /// Off by default, and the single most recognisable thing about the IIDX playfield when it
        /// is on: colour tracks the lane, not the note type, because that is what the piano-style
        /// stagger teaches a player to read. Non-playable lanes (BGA SYNC, MR, BG 1-18) keep their
        /// attribute colours under it - they are authoring lanes, and "which lane" is not the
        /// question anyone asks of them.
        /// </para>
        /// <para>
        /// One consequence, stated rather than discovered later: a long note keeps its lane's
        /// colour, so under a lane-coloured theme it is its <em>height</em> that says it is held.
        /// That is what an IIDX-style skin does anyway (the LN body is the lane colour drawn as a
        /// tail), but it is a real loss of the violet-at-a-glance cue the Studio palette gives.
        /// </para>
        /// </summary>
        public bool NotesColouredByLane { get; init; }

        /// <summary>Odd-numbered keys / primary-striped lanes. IIDX keys 1, 3, 5, 7.</summary>
        public string NoteWhiteKey { get; init; } = "#FFE6EDF7";
        public string NoteWhiteKeyEdge { get; init; } = "#FFFFFFFF";

        /// <summary>Even-numbered keys / alternate-striped lanes. IIDX keys 2, 4, 6.</summary>
        public string NoteBlueKey { get; init; } = "#FF2E7BD6";
        public string NoteBlueKeyEdge { get; init; } = "#FF9CCBFF";

        /// <summary>The turntable.</summary>
        public string NoteScratch { get; init; } = "#FFD8383C";
        public string NoteScratchEdge { get; init; } = "#FFFFA3A3";

        // ===================================================================================
        // Canvas accent
        // ===================================================================================

        /// <summary>
        /// The canvas accent: playhead, marquee and hover. Shell chrome keeps its own accent; this
        /// is only what is drawn inside the chart.
        ///
        /// For IIDX it is the judgement line's red, which is the closest thing a scrolling timeline
        /// has to the fixed red line notes fall onto.
        /// </summary>
        public string Accent { get; init; } = StudioPalette.Accent;

        // ===================================================================================
        // Built-ins
        // ===================================================================================

        /// <summary>
        /// The shipped look: ptSequencer's canvas under the studio's chrome.
        ///
        /// Every value here is the property default, so this instance is written out in full only
        /// for its identity - which is the point of fallback inheritance. A first launch with no
        /// settings file draws exactly the pixels the build before themes landed drew.
        /// </summary>
        public static readonly StudioChartTheme Studio = new StudioChartTheme
        {
            Id = StudioId,
            Name = "Studio (ptSequencer)",
            Description =
                "The shipped canvas: near-black lanes, olive bar rules, and notes coloured by " +
                "what they are - blue playable, red accented, violet long, grey keysound-only. " +
                "The look every .pt was authored against.",
        };

        /// <summary>
        /// A beatmania IIDX canvas.
        ///
        /// <para>
        /// Provenance, in the same terms <c>TechnikaPlayfieldTheme</c> uses: the lane roles and
        /// their colours are the game's documented reading - seven keys, white on 1/3/5/7 and blue
        /// on 2/4/6, a red turntable, a red judgement line - and the layout that carries them
        /// already exists here, because the BMS lane plan numbers its keys 1-7 and stripes them
        /// primary/alternate on exactly that parity (<c>VerticalTrackLayout.BmsColumnPlan</c>:
        /// odd key =&gt; <c>RegularPrimary</c>, even key =&gt; <c>RegularAlternate</c>, scratch
        /// channel =&gt; <c>IsScratch</c>). So this theme adds no new lane model; it colours the
        /// one that is already there. No Konami bitmap, font or sample is referenced, embedded or
        /// shipped - these are re-derived values, like the rest of the palettes in this file.
        /// </para>
        /// </summary>
        public static readonly StudioChartTheme Iidx = new StudioChartTheme
        {
            Id = IidxId,
            Name = "IIDX / beatoraja (white / blue / red)",
            Description =
                "The look beatoraja's default BMS playfield is built on, which is beatmania " +
                "IIDX's: white keys, blue keys, a red turntable and a red judge line. Colour " +
                "tracks the lane rather than the note type, so a long note is read by its " +
                "height. The natural pairing for a .bms chart, and the one BMS files open with.",

            // A field with a blue cast rather than a neutral one: on a black canvas the tint is
            // what separates "lane" from "gap", and it keeps the white keys reading as white.
            Field = "#FF0E1218",
            FieldAlternate = "#FF0A1C33",
            FieldScratch = "#FF2A0D12",
            FieldDead = "#FF070A0E",
            FieldBackground = "#FF12161C",

            GridSub = "#FF151B23",
            GridBeat = "#FF1F2C3B",
            // Cool rather than olive: the bar rule is the one warm-neutral thing in this palette
            // and it would fight the red playhead for the same job.
            GridBar = "#FF4C6A8C",

            LaneEdge = "#FF2B3644",
            LaneEdgeStrong = "#FF61829F",

            LaneChips = new[]
            {
                "#FF5C7A99", "#FF3F6FA8", "#FF7A8CA0", "#FF33506B", "#FF6E7F95",
                "#FF4A6B8A", "#FF3E4E60", "#FF66737F", "#FF2F4A63", "#FF8A5A62",
            },

            NotesColouredByLane = true,
            NoteWhiteKey = "#FFE6EDF7",
            NoteWhiteKeyEdge = "#FFFFFFFF",
            NoteBlueKey = "#FF2E7BD6",
            NoteBlueKeyEdge = "#FF9CCBFF",
            NoteScratch = "#FFD8383C",
            NoteScratchEdge = "#FFFFA3A3",

            // Still needed: the attribute colours are what non-playable lanes draw with, and what
            // a lane-coloured theme falls back to if a column has no role.
            NotePlayable = "#FF2E7BD6",
            NotePlayableEdge = "#FF9CCBFF",
            NoteAccent = "#FFD8383C",
            NoteAccentEdge = "#FFFFA3A3",
            NoteLong = "#FF3B6FB5",
            NoteLongEdge = "#FFA9CDF7",
            NoteBackground = "#FF59626F",
            NoteBackgroundEdge = "#FF98A2B0",

            // Near-black, because the loudest fill in this palette is a white note and a
            // near-white label on it would be invisible.
            TextOnNote = "#FF0C1218",

            Accent = "#FFE0343C",
        };

        /// <summary>
        /// A DJMAX RESPECT V canvas.
        ///
        /// <para>
        /// RESPECT V's reading is colder than IIDX's: the gear is a near-black navy glass and the
        /// notes are ice - white-cyan on the primary lanes, azure on the alternating ones, with
        /// the game's hot pink reserved for what demands attention (the analogue rails in
        /// gameplay, the accent notes here). Colour again tracks the lane, because that is what
        /// the gear's own lane stagger teaches, and because 4B/5B/6B/8B presets alternate
        /// primary/alternate lane roles exactly the way <see cref="NotesColouredByLane"/>
        /// consumes them. No asset from the game is embedded - the hues are re-derived, and the
        /// actual note and gear art lives in the owner's Shino-Tokuu folder next to the build
        /// (see <c>docs/arcade-assets.md</c>), which is where the preview takes it from.
        /// </para>
        /// </summary>
        public static readonly StudioChartTheme RespectV = new StudioChartTheme
        {
            Id = RespectVId,
            Name = "RESPECT V (ice on navy)",
            Description =
                "DJMAX RESPECT V's gear: navy glass, icy white-cyan keys, azure alternates and " +
                "the hot pink the game saves for what matters. Colour tracks the lane. RESPECT V " +
                "trailer charts open with this one; pair it with the Shino-Tokuu assets and the " +
                "playfield preview draws the real notes.",

            Field = "#FF080D16",
            FieldAlternate = "#FF0C1524",
            FieldScratch = "#FF1A0F1E",
            FieldDead = "#FF05080D",
            FieldBackground = "#FF0B101A",

            GridSub = "#FF121A28",
            GridBeat = "#FF1B2942",
            // A cool steel blue: the bar rule must not be mistaken for the cyan playhead or the
            // pink accents, the same discipline the IIDX palette keeps with its red.
            GridBar = "#FF46648C",

            LaneEdge = "#FF233348",
            LaneEdgeStrong = "#FF5A7DA8",

            LaneChips = new[]
            {
                "#FF4E7BA8", "#FF2E6E96", "#FF6E86A2", "#FF2B4A70", "#FF5F9CC7",
                "#FF3E5E85", "#FF36485E", "#FF7285A0", "#FF2C4666", "#FF8A5F8F",
            },

            NotesColouredByLane = true,
            NoteWhiteKey = "#FFE9F8FF",
            NoteWhiteKeyEdge = "#FFBDEFFF",
            NoteBlueKey = "#FF25B4E8",
            NoteBlueKeyEdge = "#FF8FE5FF",
            NoteScratch = "#FFFF4D8E",
            NoteScratchEdge = "#FFFFA8CC",

            NotePlayable = "#FF25B4E8",
            NotePlayableEdge = "#FF8FE5FF",
            NoteAccent = "#FFFF4D8E",
            NoteAccentEdge = "#FFFFA8CC",
            NoteLong = "#FF3E7FD6",
            NoteLongEdge = "#FFA8D4FF",
            NoteBackground = "#FF4E5A6C",
            NoteBackgroundEdge = "#FF8E9AB0",

            // Deep navy, unreadable on white: the same constraint the IIDX palette states.
            TextOnNote = "#FF04222E",

            Accent = "#FF38E0FF",
        };

        /// <summary>Every theme the picker offers, in the order it lists them.</summary>
        public static readonly StudioChartTheme[] All = { Studio, Iidx, RespectV };

        /// <summary>The theme a canvas uses until told otherwise, and the settings default.</summary>
        public static StudioChartTheme Default { get { return Studio; } }

        /// <summary>
        /// Resolves a persisted id, falling back to <see cref="Default"/>.
        ///
        /// Total on purpose, and silent: the id comes from a JSON file a user can edit, and an
        /// unknown one - a theme from a newer build, or a typo - has exactly one useful answer at
        /// the point it is read. <see cref="DJMaxEditor.Studio.Settings.AppearanceSettings.Clamp"/> normalises the
        /// stored value through this same method, so the settings file and the canvas can never
        /// disagree about which one they settled on.
        /// </summary>
        public static StudioChartTheme Find(string id)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                for (int i = 0; i < All.Length; i++)
                {
                    if (string.Equals(All[i].Id, id.Trim(), System.StringComparison.OrdinalIgnoreCase))
                    {
                        return All[i];
                    }
                }
            }
            return Default;
        }

        /// <summary>
        /// The colours the picker draws as a swatch strip: the ones that actually decide how this
        /// theme reads, not the first seven properties in the file.
        ///
        /// Which set that is differs by kind - a lane-coloured theme is its three lane roles and
        /// its striped fields, an attribute-coloured one is its four note families - so a user
        /// comparing two rows is comparing the right things.
        /// </summary>
        public IList<string> Swatches()
        {
            if (NotesColouredByLane)
            {
                return new[]
                {
                    Field, FieldAlternate, FieldScratch,
                    NoteWhiteKey, NoteBlueKey, NoteScratch, GridBar, Accent,
                };
            }
            return new[]
            {
                Field, GridBar, NotePlayable, NoteAccent, NoteLong, NoteBga, Accent,
            };
        }
    }
}
