using System;

namespace DJMaxEditor.Studio.Tracks
{
    /// <summary>
    /// Lane class, as intent rather than as the raw ptSequencer number.
    ///
    /// ptSequencer stores this as the <c>type=</c> key of a <c>[TrackN]</c> section in a
    /// <c>.pst</c> preset. Only five raw values appear in the four shipped DPC presets
    /// (1, 2, 4, 5, 6); the enum keeps the raw number on
    /// <see cref="TrackPresetEntry.RawType"/> so an unrecognised future value survives a
    /// load/save round-trip untouched instead of being normalised away.
    /// </summary>
    public enum TrackLaneKind
    {
        /// <summary>
        /// The leading <c>nothing1</c> gutter. Not a lane at all: 18px wide, holds no note,
        /// and belongs to neither the Play Screen nor the Background Screen.
        ///
        /// This kind has no raw type of its own - the shipped presets give the spacer the
        /// same <c>type=6</c> as the BG lanes - so only its position ahead of the play
        /// lanes distinguishes it. See <see cref="TrackPresetEntry.Section"/>.
        /// </summary>
        Spacer,

        /// <summary>Playable button lane, "white key" shading (raw <c>type=1</c>).</summary>
        Button,

        /// <summary>Playable button lane, "black key" shading (raw <c>type=2</c>).</summary>
        ButtonAlternate,

        /// <summary>SIDE L, SIDE R, and MR (raw <c>type=4</c>).</summary>
        Side,

        /// <summary>The 8B-only extra lanes L1 and R1 (raw <c>type=5</c>).</summary>
        Extra,

        /// <summary>
        /// Non-playable lane (raw <c>type=6</c>): BGA SYNC and BG 1..BG 18. These only
        /// sound; they are never drawn in game. Also the degrade target for any raw type
        /// this build does not recognise.
        /// </summary>
        Background
    }

    /// <summary>
    /// Which half of the ptSequencer canvas a lane sits in.
    ///
    /// The ptSequencer manual splits the canvas into a <b>Play Screen</b> (the lanes that
    /// are actually played) and a <b>Background Screen</b> (lanes that only sound). The
    /// split is positional, not type-driven: everything from the <c>BGA SYNC</c> lane
    /// onward is Background Screen - which is why MR lands there despite carrying the same
    /// <c>type=4</c> as SIDE L and SIDE R.
    /// </summary>
    public enum TrackScreenSection
    {
        /// <summary>The leading <c>nothing1</c> gutter: neither screen.</summary>
        Spacer,

        /// <summary>SIDE L through SIDE R - the lanes the player reads.</summary>
        PlayScreen,

        /// <summary>BGA SYNC, MR, and BG 1..BG 18 - sound and video only.</summary>
        BackgroundScreen
    }

    /// <summary>
    /// Maps the raw ptSequencer <c>type=</c> number to a <see cref="TrackLaneKind"/> and
    /// back.
    /// </summary>
    public static class TrackLaneKinds
    {
        /// <summary>Raw <c>type</c> of a playable button lane, "white key" shading.</summary>
        public const int RawButton = 1;

        /// <summary>Raw <c>type</c> of a playable button lane, "black key" shading.</summary>
        public const int RawButtonAlternate = 2;

        /// <summary>Raw <c>type</c> of SIDE L / SIDE R / MR.</summary>
        public const int RawSide = 4;

        /// <summary>Raw <c>type</c> of the 8B extra lanes L1 / R1.</summary>
        public const int RawExtra = 5;

        /// <summary>Raw <c>type</c> of every non-playable lane, spacer included.</summary>
        public const int RawBackground = 6;

        /// <summary>
        /// Classifies a raw ptSequencer <c>type</c> value. Never throws: an unknown value
        /// degrades to <see cref="TrackLaneKind.Background"/>, which is the safe reading
        /// because the raw number is retained on
        /// <see cref="TrackPresetEntry.RawType"/> and written back verbatim.
        /// </summary>
        public static TrackLaneKind FromRawType(int rawType)
        {
            switch (rawType)
            {
                case RawButton:
                    return TrackLaneKind.Button;
                case RawButtonAlternate:
                    return TrackLaneKind.ButtonAlternate;
                case RawSide:
                    return TrackLaneKind.Side;
                case RawExtra:
                    return TrackLaneKind.Extra;
                default:
                    // 6, plus 0/3 and anything a later ptSequencer build might add.
                    return TrackLaneKind.Background;
            }
        }

        /// <summary>
        /// The raw <c>type</c> a synthesised lane of this kind should be written with.
        /// <see cref="TrackLaneKind.Spacer"/> maps to <see cref="RawBackground"/> because
        /// the shipped presets give the spacer <c>type=6</c> as well.
        /// </summary>
        /// <remarks>
        /// This is only for lanes built in code. When a lane came from a file, write
        /// <see cref="TrackPresetEntry.RawType"/> instead so an unrecognised value is not
        /// flattened to 6.
        /// </remarks>
        public static int ToRawType(TrackLaneKind kind)
        {
            switch (kind)
            {
                case TrackLaneKind.Button:
                    return RawButton;
                case TrackLaneKind.ButtonAlternate:
                    return RawButtonAlternate;
                case TrackLaneKind.Side:
                    return RawSide;
                case TrackLaneKind.Extra:
                    return RawExtra;
                default:
                    return RawBackground;
            }
        }

        /// <summary>True for the kinds that carry gameplay input.</summary>
        public static bool IsPlayableKind(TrackLaneKind kind)
        {
            return kind == TrackLaneKind.Button ||
                kind == TrackLaneKind.ButtonAlternate ||
                kind == TrackLaneKind.Side ||
                kind == TrackLaneKind.Extra;
        }
    }

    /// <summary>
    /// One <c>[TrackN]</c> section of a ptSequencer <c>.pst</c> preset: a single chart
    /// track drawn as one fixed-width vertical lane.
    ///
    /// Mutable on purpose - the preset editor edits these in place - so
    /// <see cref="TrackPreset.Reclassify"/> must be called after any change that could
    /// move the Play/Background boundary (a rename to or from <c>BGA SYNC</c>, a reorder,
    /// an insert, a <see cref="RawType"/> change).
    /// </summary>
    public sealed class TrackPresetEntry
    {
        /// <summary>No separator rule on this lane's left edge (<c>bold=</c> absent).</summary>
        public const int SeparatorNone = 0;

        /// <summary>Thin separator rule (<c>bold=1</c>).</summary>
        public const int SeparatorThin = 1;

        /// <summary>
        /// Heavy separator rule (<c>bold=3</c>). MR is the only lane that carries this in
        /// the shipped presets: it is how ptSequencer visually splits the play area from
        /// the background area.
        /// </summary>
        public const int SeparatorHeavy = 3;

        /// <summary>Name of the lane that carries the BGA video sync events.</summary>
        public const string BgaSyncLaneName = "BGA SYNC";

        /// <summary>Name of the master-record (backing track) lane.</summary>
        public const string MasterRecordLaneName = "MR";

        /// <summary>Lane width of a playable lane, in device-independent px at 100% zoom.</summary>
        public const int PlayableWidth = 60;

        /// <summary>Lane width of a BGA SYNC / MR / BG lane.</summary>
        public const int BackgroundWidth = 30;

        /// <summary>Lane width of the leading spacer.</summary>
        public const int SpacerWidth = 18;

        /// <summary>Creates an empty lane. Used by the parser, which fills the fields in.</summary>
        public TrackPresetEntry()
        {
            Name = string.Empty;
            RawType = TrackLaneKinds.RawBackground;
            Section = TrackScreenSection.BackgroundScreen;
        }

        /// <summary>Creates a fully specified lane.</summary>
        /// <param name="index">Visual position, matching the <c>[TrackN]</c> ordinal.</param>
        /// <param name="rawType">Raw ptSequencer <c>type</c> value.</param>
        /// <param name="name">Display name, shown by ptSequencer on right-click.</param>
        /// <param name="songTrack">Index into the chart's track array.</param>
        /// <param name="width">Lane width in device-independent px at 100% zoom.</param>
        /// <param name="separatorWeight">
        /// <see cref="SeparatorNone"/>, <see cref="SeparatorThin"/> or
        /// <see cref="SeparatorHeavy"/>.
        /// </param>
        public TrackPresetEntry(
            int index,
            int rawType,
            string name,
            int songTrack,
            int width,
            int separatorWeight)
        {
            Index = index;
            RawType = rawType;
            Name = name ?? string.Empty;
            SongTrack = songTrack;
            Width = width;
            SeparatorWeight = separatorWeight;

            // Best guess until an owning TrackPreset reclassifies us: a playable type is
            // Play Screen, anything else is Background Screen. A standalone spacer
            // therefore reports Background until it is placed in a preset.
            Section = TrackLaneKinds.IsPlayableKind(TrackLaneKinds.FromRawType(rawType))
                ? TrackScreenSection.PlayScreen
                : TrackScreenSection.BackgroundScreen;
        }

        /// <summary>
        /// Visual position of this lane, left to right, matching the <c>[TrackN]</c>
        /// ordinal it is written as. Not the chart track id - that is
        /// <see cref="SongTrack"/>.
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// Raw ptSequencer <c>type=</c> value, preserved exactly as read so it round-trips
        /// even when this build does not recognise it.
        /// </summary>
        public int RawType { get; set; }

        /// <summary>
        /// <see cref="RawType"/> read as intent. Resolves to
        /// <see cref="TrackLaneKind.Spacer"/> for a non-playable lane that sits ahead of
        /// the Play Screen, which is the only way to tell the <c>nothing1</c> gutter apart
        /// from a BG lane - both are <c>type=6</c>.
        /// </summary>
        public TrackLaneKind Kind
        {
            get
            {
                TrackLaneKind kind = TrackLaneKinds.FromRawType(RawType);
                if (kind == TrackLaneKind.Background && Section == TrackScreenSection.Spacer)
                {
                    return TrackLaneKind.Spacer;
                }
                return kind;
            }
        }

        /// <summary>Display name. ptSequencer shows it on right-click.</summary>
        public string Name { get; set; }

        /// <summary>
        /// Index into the chart's track array (<c>PlayerData.Tracks</c>). This is the
        /// chart-side identity and is deliberately unrelated to <see cref="Index"/>: the
        /// 8B preset draws song tracks 2, 10, 3, 4, 5, 6, 7, 8, 11, 9 in that visual
        /// order.
        /// </summary>
        public int SongTrack { get; set; }

        /// <summary>
        /// Lane width in device-independent px at 100% zoom: 60 playable, 30 for
        /// BGA/MR/BG, 18 for the spacer.
        /// </summary>
        public int Width { get; set; }

        /// <summary>
        /// Weight of the separator rule drawn on this lane's <b>left</b> edge:
        /// <see cref="SeparatorNone"/>, <see cref="SeparatorThin"/> or
        /// <see cref="SeparatorHeavy"/>. Written as <c>bold=</c>, and omitted entirely
        /// when zero.
        /// </summary>
        public int SeparatorWeight { get; set; }

        /// <summary>
        /// Which half of the canvas this lane belongs to. Assigned by the owning
        /// <see cref="TrackPreset"/>; see <see cref="TrackPreset.Reclassify"/>.
        /// </summary>
        public TrackScreenSection Section { get; internal set; }

        /// <summary>
        /// True when <see cref="Kind"/> carries gameplay input (Button, ButtonAlternate,
        /// Side, Extra).
        /// </summary>
        /// <remarks>
        /// This is a statement about the lane's <i>type</i>, not about where it is drawn.
        /// MR is <c>type=4</c>, so it is playable by kind while also being
        /// <see cref="IsBackgroundScreen"/>. Use <see cref="IsPlayScreen"/> when the
        /// question is "does the player see this lane".
        /// </remarks>
        public bool IsPlayable
        {
            get { return TrackLaneKinds.IsPlayableKind(Kind); }
        }

        /// <summary>
        /// True for the leading <c>nothing1</c> gutter, which belongs to neither screen.
        /// </summary>
        public bool IsSpacer
        {
            get { return Section == TrackScreenSection.Spacer; }
        }

        /// <summary>
        /// True for the lanes the player actually reads: SIDE L through SIDE R.
        /// </summary>
        public bool IsPlayScreen
        {
            get { return Section == TrackScreenSection.PlayScreen; }
        }

        /// <summary>
        /// True from the <c>BGA SYNC</c> lane onward - the Background Screen half, which
        /// includes MR and BG 1..BG 18. These lanes only sound; they never appear in game.
        /// </summary>
        public bool IsBackgroundScreen
        {
            get { return Section == TrackScreenSection.BackgroundScreen; }
        }

        /// <summary>
        /// True when this is the BGA video sync lane, found by name so the BGA feature can
        /// locate it by role instead of by a hard-coded index.
        /// </summary>
        public bool IsBgaSync
        {
            get { return NameEquals(BgaSyncLaneName); }
        }

        /// <summary>
        /// True when this is the MR (master record / backing track) lane, found by name so
        /// the backing-track feature can locate it by role instead of by index.
        /// </summary>
        public bool IsMasterRecord
        {
            get { return NameEquals(MasterRecordLaneName); }
        }

        /// <summary>Case-insensitive name test, tolerating a null <see cref="Name"/>.</summary>
        public bool NameEquals(string other)
        {
            return string.Equals(Name, other, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Deep copy, including the assigned <see cref="Section"/>.</summary>
        public TrackPresetEntry Clone()
        {
            TrackPresetEntry copy = new TrackPresetEntry(
                Index, RawType, Name, SongTrack, Width, SeparatorWeight);
            copy.Section = Section;
            return copy;
        }

        /// <summary>
        /// Compares every persisted field plus the derived section. Used by the round-trip
        /// tests and by the editor's dirty check; <see cref="Kind"/>,
        /// <see cref="IsPlayable"/> and friends are all derived from these, so this is a
        /// full comparison.
        /// </summary>
        public bool HasSameFields(TrackPresetEntry other)
        {
            if (other == null)
            {
                return false;
            }

            return Index == other.Index &&
                RawType == other.RawType &&
                SongTrack == other.SongTrack &&
                Width == other.Width &&
                SeparatorWeight == other.SeparatorWeight &&
                Section == other.Section &&
                string.Equals(Name, other.Name, StringComparison.Ordinal);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return string.Format(
                "[Track{0}] {1} type={2} songtrack={3} width={4} bold={5}",
                Index, Name, RawType, SongTrack, Width, SeparatorWeight);
        }
    }
}
