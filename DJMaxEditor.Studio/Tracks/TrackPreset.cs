using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;

namespace DJMaxEditor.Studio.Tracks
{
    /// <summary>
    /// One ptSequencer track preset - the lane layout of a <c>.pst</c> file
    /// (<c>DPC_4B.pst</c>, <c>DPC_5B.pst</c>, <c>DPC_6B.pst</c>, <c>DPC_8B.pst</c>).
    ///
    /// The file is INI-shaped: a <c>;</c> banner comment, a <c>[general]</c> section with
    /// <c>total=N</c>, then N <c>[TrackN]</c> sections in visual order, left to right. The
    /// order in the file <b>is</b> the on-screen order; the chart-side track id lives in
    /// each section's <c>songtrack</c> key.
    ///
    /// <see cref="Save"/> writes the format back byte-for-byte (CRLF, blank line between
    /// sections, <c>bold=</c> omitted when zero) because ptSequencer itself must still be
    /// able to open what we write.
    /// </summary>
    public sealed class TrackPreset
    {
        /// <summary>Extension of a ptSequencer track preset.</summary>
        public const string FileExtension = ".pst";

        /// <summary>File-name prefix the four shipped DPC presets use.</summary>
        public const string ShippedFileNamePrefix = "DPC_";

        // Section and key names, exactly as ptSequencer writes them. Lookups are
        // case-insensitive, so casing here only affects what we write back out.
        private const string GeneralSection = "general";
        private const string TotalKey = "total";
        private const string TrackSectionPrefix = "Track";
        private const string TypeKey = "type";
        private const string NameKey = "name";
        private const string SongTrackKey = "songtrack";
        private const string WidthKey = "width";
        private const string BoldKey = "bold";

        // ptSequencer is an ANSI-era tool: the shipped presets are plain ASCII with no
        // byte-order mark. Writing UTF-8 *without* a BOM keeps ASCII content byte-identical
        // and keeps a non-ASCII lane name at least readable rather than mojibake.
        private static readonly UTF8Encoding PresetEncoding = new UTF8Encoding(false);

        private readonly List<TrackPresetEntry> _entries;
        private readonly ReadOnlyCollection<TrackPresetEntry> _entriesView;
        private readonly Dictionary<int, TrackPresetEntry> _bySongTrack;
        private List<TrackPresetEntry> _playEntries;
        private List<TrackPresetEntry> _backgroundEntries;

        /// <summary>Builds a preset from lanes in visual order.</summary>
        /// <param name="displayName">Short label, e.g. <c>8B</c>.</param>
        /// <param name="sourcePath">File the lanes were read from, or null when synthesised.</param>
        /// <param name="entries">Lanes, left to right. Copied; the list is not aliased.</param>
        /// <param name="headerComment">
        /// Banner comment line to write back, <c>;</c> included. Null synthesises
        /// <c>; DJMAX_RESPECT_&lt;displayName&gt;_PRESET</c>, which is what the shipped
        /// files carry.
        /// </param>
        public TrackPreset(
            string displayName,
            string sourcePath,
            IEnumerable<TrackPresetEntry> entries,
            string headerComment)
        {
            if (entries == null)
            {
                throw new ArgumentNullException("entries");
            }

            DisplayName = string.IsNullOrEmpty(displayName) ? string.Empty : displayName;
            SourcePath = sourcePath;
            KeyCount = ParseKeyCount(DisplayName);
            HeaderComment = NormalizeHeaderComment(headerComment, DisplayName);

            _entries = new List<TrackPresetEntry>(entries);
            _entriesView = new ReadOnlyCollection<TrackPresetEntry>(_entries);
            _bySongTrack = new Dictionary<int, TrackPresetEntry>();
            Reclassify();
        }

        /// <summary>Builds a preset with the default banner comment.</summary>
        public TrackPreset(
            string displayName,
            string sourcePath,
            IEnumerable<TrackPresetEntry> entries)
            : this(displayName, sourcePath, entries, null)
        {
        }

        /// <summary>
        /// Short label derived from the file name: <c>DPC_8B.pst</c> becomes <c>8B</c>.
        /// </summary>
        public string DisplayName { get; private set; }

        /// <summary>
        /// Full path this preset was read from, or null when it came from
        /// <see cref="TrackPresetLibrary.CreateFallback"/>.
        /// </summary>
        public string SourcePath { get; private set; }

        /// <summary>
        /// Banner comment line written above <c>[general]</c>, leading <c>;</c> included.
        /// Preserved verbatim on load so a load/save round-trip is byte-identical.
        /// </summary>
        public string HeaderComment { get; private set; }

        /// <summary>
        /// Button count parsed out of <see cref="DisplayName"/> - 4, 5, 6 or 8 for the
        /// shipped presets - or 0 when the name carries no <c>&lt;digits&gt;B</c> token.
        /// </summary>
        public int KeyCount { get; private set; }

        /// <summary>Every lane, in visual order, left to right.</summary>
        public IReadOnlyList<TrackPresetEntry> Entries
        {
            get { return _entriesView; }
        }

        /// <summary>
        /// Sum of every lane width, i.e. the canvas width in device-independent px at 100%
        /// zoom. 978 for 4B, 1038 for 5B, 1098 for 6B, 1218 for 8B.
        /// </summary>
        public int TotalWidth { get; private set; }

        /// <summary>
        /// Play Screen lanes only - SIDE L through SIDE R, the lanes the player reads. The
        /// leading spacer and everything from BGA SYNC onward are excluded, so for 8B this
        /// is song tracks 2, 10, 3, 4, 5, 6, 7, 8, 11, 9 in that order.
        /// </summary>
        public IReadOnlyList<TrackPresetEntry> PlayEntries { get; private set; }

        /// <summary>
        /// Background Screen lanes only - BGA SYNC, MR and BG 1..BG 18. These sound but
        /// never appear in game.
        /// </summary>
        public IReadOnlyList<TrackPresetEntry> BackgroundEntries { get; private set; }

        /// <summary>The leading <c>nothing1</c> gutter, or null if the preset has none.</summary>
        public TrackPresetEntry SpacerEntry { get; private set; }

        /// <summary>The <c>BGA SYNC</c> lane, or null if the preset has none.</summary>
        public TrackPresetEntry BgaSyncEntry { get; private set; }

        /// <summary>The <c>MR</c> lane, or null if the preset has none.</summary>
        public TrackPresetEntry MasterRecordEntry { get; private set; }

        /// <summary>
        /// Finds the lane that draws a given chart track.
        /// </summary>
        /// <param name="songTrack">Index into the chart's track array.</param>
        /// <param name="entry">The lane, or null when no lane draws that track.</param>
        /// <returns>True when a lane was found.</returns>
        public bool TryGetBySongTrack(int songTrack, out TrackPresetEntry entry)
        {
            return _bySongTrack.TryGetValue(songTrack, out entry);
        }

        /// <summary>
        /// Renumbers <see cref="TrackPresetEntry.Index"/> in visual order and recomputes
        /// everything derived from lane order: the Play/Background split,
        /// <see cref="TotalWidth"/>, the song-track lookup, and the role shortcuts.
        ///
        /// Call this after inserting, removing, reordering or renaming lanes. The
        /// constructor already does.
        /// </summary>
        public void Reclassify()
        {
            // Where the Background Screen starts. The manual's split is positional, so the
            // BGA SYNC lane is the boundary marker rather than any type value - MR is
            // type=4 like SIDE L/R yet still belongs to the background half.
            int backgroundStart = FindBackgroundScreenStart();

            // Where the Play Screen starts. Only the *leading* run of non-playable lanes is
            // gutter; a non-playable lane sitting between two play lanes stays in the play
            // half, because that is where it is drawn.
            int playStart = FindPlayScreenStart(backgroundStart);

            _playEntries = new List<TrackPresetEntry>();
            _backgroundEntries = new List<TrackPresetEntry>();
            _bySongTrack.Clear();
            SpacerEntry = null;
            BgaSyncEntry = null;
            MasterRecordEntry = null;

            int totalWidth = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                TrackPresetEntry entry = _entries[i];

                // Index is the [TrackN] ordinal, and total=N + contiguous TrackN is what
                // ptSequencer expects, so visual order is the single source of truth.
                entry.Index = i;

                if (i >= backgroundStart)
                {
                    entry.Section = TrackScreenSection.BackgroundScreen;
                    _backgroundEntries.Add(entry);
                }
                else if (i < playStart)
                {
                    // A non-playable lane ahead of every play lane is a gutter, not a
                    // background lane: this is the "nothing1" spacer.
                    entry.Section = TrackScreenSection.Spacer;
                    if (SpacerEntry == null)
                    {
                        SpacerEntry = entry;
                    }
                }
                else
                {
                    entry.Section = TrackScreenSection.PlayScreen;
                    _playEntries.Add(entry);
                }

                if (BgaSyncEntry == null && entry.IsBgaSync)
                {
                    BgaSyncEntry = entry;
                }
                if (MasterRecordEntry == null && entry.IsMasterRecord)
                {
                    MasterRecordEntry = entry;
                }

                // Every lane owns a distinct song track in the shipped presets, so first
                // writer wins and the guard just documents that invariant.
                if (!_bySongTrack.ContainsKey(entry.SongTrack))
                {
                    _bySongTrack[entry.SongTrack] = entry;
                }

                totalWidth += entry.Width;
            }

            TotalWidth = totalWidth;
            PlayEntries = new ReadOnlyCollection<TrackPresetEntry>(_playEntries);
            BackgroundEntries = new ReadOnlyCollection<TrackPresetEntry>(_backgroundEntries);
        }

        /// <summary>
        /// Reads a preset from disk.
        /// </summary>
        /// <exception cref="ArgumentException">The path is null or blank.</exception>
        /// <exception cref="IOException">The file could not be read.</exception>
        /// <exception cref="FormatException">The file is not a usable preset.</exception>
        public static TrackPreset Load(string path)
        {
            TrackPreset preset;
            string diagnostic;
            if (!TryLoad(path, out preset, out diagnostic))
            {
                throw new FormatException(diagnostic);
            }
            return preset;
        }

        /// <summary>
        /// Reads a preset from disk without throwing on a bad file.
        /// </summary>
        /// <param name="path">Full path to a <c>.pst</c> file.</param>
        /// <param name="preset">The preset, or null on failure.</param>
        /// <param name="diagnostic">
        /// Null when the file parsed cleanly. On a false return this is the reason it was
        /// rejected; on a true return it is a non-fatal note (for example a
        /// <c>total=</c> that disagrees with the number of sections present).
        /// </param>
        /// <returns>True when <paramref name="preset"/> was produced.</returns>
        public static bool TryLoad(string path, out TrackPreset preset, out string diagnostic)
        {
            preset = null;
            diagnostic = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                diagnostic = "Preset path is empty.";
                return false;
            }

            string text;
            try
            {
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                diagnostic = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: could not be read ({1})",
                    Path.GetFileName(path),
                    ex.Message);
                return false;
            }

            return TryParse(text, path, out preset, out diagnostic);
        }

        /// <summary>
        /// Parses preset text that has already been read into memory.
        /// </summary>
        /// <param name="text">Whole file contents.</param>
        /// <param name="sourcePath">
        /// Path the text came from. Used for <see cref="DisplayName"/>,
        /// <see cref="KeyCount"/> and diagnostics; may be null.
        /// </param>
        /// <param name="preset">The preset, or null on failure.</param>
        /// <param name="diagnostic">See <see cref="TryLoad"/>.</param>
        /// <returns>True when <paramref name="preset"/> was produced.</returns>
        public static bool TryParse(
            string text,
            string sourcePath,
            out TrackPreset preset,
            out string diagnostic)
        {
            preset = null;
            diagnostic = null;

            string label = string.IsNullOrEmpty(sourcePath)
                ? "preset"
                : Path.GetFileName(sourcePath);

            if (text == null)
            {
                diagnostic = label + ": no content.";
                return false;
            }

            PresetIniDocument ini = PresetIniDocument.Parse(text);

            // total= is advisory: we trust the sections that are actually present, and only
            // report the discrepancy. A preset that is one section short is still usable.
            int declaredTotal;
            bool hasDeclaredTotal = ini.TryGetInt(GeneralSection, TotalKey, out declaredTotal);

            List<TrackPresetEntry> entries = new List<TrackPresetEntry>();
            for (int index = 0; ; index++)
            {
                string section = TrackSectionPrefix + index.ToString(CultureInfo.InvariantCulture);
                if (!ini.HasSection(section))
                {
                    break;
                }

                TrackPresetEntry entry;
                string entryError;
                if (!TryParseEntry(ini, section, index, out entry, out entryError))
                {
                    diagnostic = label + ": " + entryError;
                    return false;
                }

                entries.Add(entry);
            }

            if (entries.Count == 0)
            {
                diagnostic = label + ": no [Track0] section, so this is not a track preset.";
                return false;
            }

            if (hasDeclaredTotal && declaredTotal != entries.Count)
            {
                diagnostic = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: [general] total={1} but {2} contiguous [Track] sections were found; using {2}.",
                    label,
                    declaredTotal,
                    entries.Count);
            }
            else if (!hasDeclaredTotal)
            {
                diagnostic = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: [general] total is missing; using the {1} sections found.",
                    label,
                    entries.Count);
            }

            preset = new TrackPreset(
                DeriveDisplayName(sourcePath),
                sourcePath,
                entries,
                ini.HeaderComment);
            return true;
        }

        /// <summary>
        /// Writes this preset in ptSequencer's exact format: CRLF endings, the banner
        /// comment, <c>[general] total=</c>, one blank line before each <c>[TrackN]</c>
        /// section, and <c>bold=</c> omitted when the weight is zero. Round-tripping a
        /// shipped file reproduces it byte for byte.
        /// </summary>
        /// <param name="path">Destination path. Any existing file is overwritten.</param>
        public void Save(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Preset path is empty.", "path");
            }

            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, ToPresetText(), PresetEncoding);
        }

        /// <summary>
        /// Renders this preset exactly as <see cref="Save"/> would write it. Split out so
        /// tests can compare against a shipped file without touching disk.
        /// </summary>
        public string ToPresetText()
        {
            StringBuilder text = new StringBuilder();

            // ptSequencer is a Windows tool and reads its own files line-wise; CRLF is not
            // cosmetic here, so it is written explicitly rather than via AppendLine.
            const string Crlf = "\r\n";

            text.Append(HeaderComment).Append(Crlf);
            text.Append(Crlf);
            text.Append('[').Append(GeneralSection).Append(']').Append(Crlf);
            text.Append(TotalKey).Append('=')
                .Append(_entries.Count.ToString(CultureInfo.InvariantCulture)).Append(Crlf);

            for (int i = 0; i < _entries.Count; i++)
            {
                TrackPresetEntry entry = _entries[i];

                // The blank line goes *before* each section, which is what leaves the
                // shipped files ending in a single newline after the last width=.
                text.Append(Crlf);
                text.Append('[').Append(TrackSectionPrefix)
                    .Append(i.ToString(CultureInfo.InvariantCulture)).Append(']').Append(Crlf);
                text.Append(TypeKey).Append('=')
                    .Append(entry.RawType.ToString(CultureInfo.InvariantCulture)).Append(Crlf);
                text.Append(NameKey).Append('=').Append(entry.Name ?? string.Empty).Append(Crlf);
                text.Append(SongTrackKey).Append('=')
                    .Append(entry.SongTrack.ToString(CultureInfo.InvariantCulture)).Append(Crlf);
                text.Append(WidthKey).Append('=')
                    .Append(entry.Width.ToString(CultureInfo.InvariantCulture)).Append(Crlf);

                // Absent means "no rule", so a zero weight is written by omission.
                if (entry.SeparatorWeight != TrackPresetEntry.SeparatorNone)
                {
                    text.Append(BoldKey).Append('=')
                        .Append(entry.SeparatorWeight.ToString(CultureInfo.InvariantCulture))
                        .Append(Crlf);
                }
            }

            return text.ToString();
        }

        /// <summary>Independent copy, safe to edit without disturbing this instance.</summary>
        public TrackPreset Clone()
        {
            List<TrackPresetEntry> copies = new List<TrackPresetEntry>(_entries.Count);
            foreach (TrackPresetEntry entry in _entries)
            {
                copies.Add(entry.Clone());
            }
            return new TrackPreset(DisplayName, SourcePath, copies, HeaderComment);
        }

        /// <summary>
        /// Field-for-field lane comparison, ignoring <see cref="SourcePath"/> and
        /// <see cref="HeaderComment"/>. True when both presets would draw the same canvas.
        /// </summary>
        public bool HasSameLayout(TrackPreset other)
        {
            if (other == null || other._entries.Count != _entries.Count)
            {
                return false;
            }

            for (int i = 0; i < _entries.Count; i++)
            {
                if (!_entries[i].HasSameFields(other._entries[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Turns a preset path into a short label: <c>DPC_8B.pst</c> becomes <c>8B</c>. The
        /// shipped <c>DPC_</c> prefix is dropped; any other stem is kept whole so a
        /// user-authored preset keeps its own name.
        /// </summary>
        public static string DeriveDisplayName(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string stem = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(stem))
            {
                return string.Empty;
            }

            if (stem.Length > ShippedFileNamePrefix.Length &&
                stem.StartsWith(ShippedFileNamePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return stem.Substring(ShippedFileNamePrefix.Length);
            }

            return stem;
        }

        /// <summary>
        /// Pulls the button count out of a preset label: the digits immediately before a
        /// <c>B</c>, so <c>8B</c> gives 8. Returns 0 when there is no such token. The
        /// shipped presets are 4, 5, 6 and 8; a wider value is returned as-is rather than
        /// rejected, so a future mode does not need a code change here.
        /// </summary>
        public static int ParseKeyCount(string displayName)
        {
            if (string.IsNullOrEmpty(displayName))
            {
                return 0;
            }

            for (int i = 0; i < displayName.Length; i++)
            {
                if (displayName[i] != 'B' && displayName[i] != 'b')
                {
                    continue;
                }

                // Walk back over the run of digits that ends at this 'B'.
                int end = i;
                int start = i;
                while (start > 0 && char.IsDigit(displayName[start - 1]))
                {
                    start--;
                }

                if (start == end)
                {
                    continue;
                }

                int value;
                if (int.TryParse(
                        displayName.Substring(start, end - start),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out value))
                {
                    return value;
                }
            }

            return 0;
        }

        private static bool TryParseEntry(
            PresetIniDocument ini,
            string section,
            int index,
            out TrackPresetEntry entry,
            out string error)
        {
            entry = null;
            error = null;

            int rawType;
            if (!ini.TryGetInt(section, TypeKey, out rawType))
            {
                error = "[" + section + "] has no readable type=.";
                return false;
            }

            int songTrack;
            if (!ini.TryGetInt(section, SongTrackKey, out songTrack))
            {
                error = "[" + section + "] has no readable songtrack=.";
                return false;
            }

            int width;
            if (!ini.TryGetInt(section, WidthKey, out width))
            {
                error = "[" + section + "] has no readable width=.";
                return false;
            }

            // name= is cosmetic, so a missing one is not worth rejecting the file over.
            string name = ini.GetString(section, NameKey, string.Empty);

            // bold= absent means "no separator rule", which is how every unemphasised lane
            // in the shipped presets is written.
            int separatorWeight;
            if (!ini.TryGetInt(section, BoldKey, out separatorWeight))
            {
                separatorWeight = TrackPresetEntry.SeparatorNone;
            }

            entry = new TrackPresetEntry(index, rawType, name, songTrack, width, separatorWeight);
            return true;
        }

        private static string NormalizeHeaderComment(string headerComment, string displayName)
        {
            if (!string.IsNullOrWhiteSpace(headerComment))
            {
                string trimmed = headerComment.Trim();
                return trimmed.StartsWith(";", StringComparison.Ordinal) ? trimmed : "; " + trimmed;
            }

            // What the shipped files carry, e.g. "; DJMAX_RESPECT_8B_PRESET".
            return "; DJMAX_RESPECT_" + displayName + "_PRESET";
        }

        private int FindBackgroundScreenStart()
        {
            // Primary rule, straight out of the manual: the BGA SYNC lane opens the
            // Background Screen.
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].IsBgaSync)
                {
                    return i;
                }
            }

            // Fallback for a preset that renamed or dropped BGA SYNC: the first
            // non-playable lane that comes after at least one playable lane.
            bool seenPlayable = false;
            for (int i = 0; i < _entries.Count; i++)
            {
                bool playable = TrackLaneKinds.IsPlayableKind(
                    TrackLaneKinds.FromRawType(_entries[i].RawType));
                if (playable)
                {
                    seenPlayable = true;
                }
                else if (seenPlayable)
                {
                    return i;
                }
            }

            // No background half at all.
            return _entries.Count;
        }

        /// <summary>
        /// Index of the first Play Screen lane: the first lane whose type is playable. Every
        /// lane before it is the leading gutter. Capped at
        /// <paramref name="backgroundStart"/> so a preset with no playable lane at all
        /// reports an empty play half rather than swallowing the background half.
        /// </summary>
        private int FindPlayScreenStart(int backgroundStart)
        {
            for (int i = 0; i < backgroundStart && i < _entries.Count; i++)
            {
                if (TrackLaneKinds.IsPlayableKind(TrackLaneKinds.FromRawType(_entries[i].RawType)))
                {
                    return i;
                }
            }

            return backgroundStart;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} ({1} lanes, {2}px)",
                string.IsNullOrEmpty(DisplayName) ? "preset" : DisplayName,
                _entries.Count,
                TotalWidth);
        }
    }
}
