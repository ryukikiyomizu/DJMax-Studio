using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;

namespace DJMaxEditor.Studio.Tracks
{
    /// <summary>
    /// The set of track presets the editor offers, loaded from a ptSequencer install
    /// directory (the folder that holds <c>ptSequencer.exe</c>, <c>trackset.ini</c>,
    /// <c>common.ini</c> and the <c>DPC_*.pst</c> files).
    ///
    /// Nothing here throws on bad data. A directory that is missing, empty or full of
    /// malformed files yields a library with fewer presets and an explanation in
    /// <see cref="Warnings"/>; the caller falls back to <see cref="CreateFallback"/>, which
    /// synthesises the four shipped layouts in code so the app runs with no ptSequencer
    /// install at all.
    /// </summary>
    public sealed class TrackPresetLibrary
    {
        /// <summary>Index file that lists which presets to offer, and in what order.</summary>
        public const string TracksetFileName = "trackset.ini";

        /// <summary>Shared ptSequencer settings file; we read its pan presets.</summary>
        public const string CommonFileName = "common.ini";

        /// <summary>Search pattern for preset files when <c>trackset.ini</c> is absent.</summary>
        public const string PresetSearchPattern = "*" + TrackPreset.FileExtension;

        /// <summary>Centre pan. The MIDI-style range is 0..127, so 64 is dead centre.</summary>
        public const int CenterPan = 64;

        /// <summary>Lowest legal pan value.</summary>
        public const int MinimumPan = 0;

        /// <summary>Highest legal pan value.</summary>
        public const int MaximumPan = 127;

        /// <summary>How many pan slots <c>common.ini</c> ships.</summary>
        public const int DefaultPanPresetCount = 10;

        /// <summary>Key counts of the four shipped DPC presets, in trackset order.</summary>
        public static readonly int[] ShippedKeyCounts = { 4, 5, 6, 8 };

        private const string TracksetSection = "trackset";
        private const string TracksetTotalKey = "total";
        private const string TracksetNamePrefix = "name";
        private const string PanSection = "pan preset";
        private const string PanTotalKey = "total";
        private const string PanPresetPrefix = "preset";

        private TrackPresetLibrary(
            string sourceDirectory,
            bool isFallback,
            IList<TrackPreset> presets,
            IList<int> panPresets,
            IList<string> warnings)
        {
            SourceDirectory = sourceDirectory;
            IsFallback = isFallback;
            Presets = new ReadOnlyCollection<TrackPreset>(presets);
            PanPresets = new ReadOnlyCollection<int>(panPresets);
            Warnings = new ReadOnlyCollection<string>(warnings);
        }

        /// <summary>
        /// Directory the presets were read from, or null for
        /// <see cref="CreateFallback"/>.
        /// </summary>
        public string SourceDirectory { get; private set; }

        /// <summary>
        /// True when these presets were synthesised in code rather than read from a
        /// ptSequencer install.
        /// </summary>
        public bool IsFallback { get; private set; }

        /// <summary>
        /// Presets in the order <c>trackset.ini</c> lists them, which is the order the
        /// picker should show.
        /// </summary>
        public IReadOnlyList<TrackPreset> Presets { get; private set; }

        /// <summary>
        /// Pan slots from <c>[pan preset]</c> in <c>common.ini</c>, 0..127 with
        /// <see cref="CenterPan"/> in the middle. Ten centred values when the file is
        /// absent - which is exactly what ships.
        /// </summary>
        public IReadOnlyList<int> PanPresets { get; private set; }

        /// <summary>
        /// Human-readable notes about anything that was skipped or repaired during
        /// <see cref="Load"/>. Empty on a clean load.
        /// </summary>
        public IReadOnlyList<string> Warnings { get; private set; }

        /// <summary>
        /// Reads every preset a ptSequencer directory offers.
        ///
        /// <c>trackset.ini</c> drives both the membership and the order. When it is
        /// missing, every <c>.pst</c> in the directory is used instead, sorted by name. A
        /// preset that fails to parse is skipped and the reason recorded in
        /// <see cref="Warnings"/>.
        /// </summary>
        /// <param name="directory">The folder that holds <c>ptSequencer.exe</c>.</param>
        /// <exception cref="ArgumentException"><paramref name="directory"/> is null or blank.</exception>
        public static TrackPresetLibrary Load(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new ArgumentException("Preset directory is empty.", "directory");
            }

            List<string> warnings = new List<string>();
            List<TrackPreset> presets = new List<TrackPreset>();

            if (!Directory.Exists(directory))
            {
                warnings.Add("Preset directory not found: " + directory);
                return new TrackPresetLibrary(
                    directory, false, presets, DefaultPanPresets(), warnings);
            }

            foreach (string path in ResolvePresetPaths(directory, warnings))
            {
                TrackPreset preset;
                string diagnostic;
                if (TrackPreset.TryLoad(path, out preset, out diagnostic))
                {
                    // A successful load can still carry a note, e.g. a total= that
                    // disagreed with the sections present.
                    if (!string.IsNullOrEmpty(diagnostic))
                    {
                        warnings.Add(diagnostic);
                    }
                    presets.Add(preset);
                }
                else
                {
                    warnings.Add("Skipped " + diagnostic);
                }
            }

            if (presets.Count == 0)
            {
                warnings.Add(
                    "No usable track preset was found in " + directory +
                    "; call TrackPresetLibrary.CreateFallback() instead.");
            }

            return new TrackPresetLibrary(
                directory, false, presets, LoadPanPresets(directory, warnings), warnings);
        }

        /// <summary>
        /// Builds the four shipped layouts (4B, 5B, 6B, 8B) in code, field-for-field
        /// identical to <c>DPC_4B.pst</c> .. <c>DPC_8B.pst</c>, so the editor still opens
        /// charts on a machine with no ptSequencer install. <see cref="PanPresets"/> is the
        /// shipped ten centred values.
        /// </summary>
        public static TrackPresetLibrary CreateFallback()
        {
            List<TrackPreset> presets = new List<TrackPreset>(ShippedKeyCounts.Length);
            foreach (int keyCount in ShippedKeyCounts)
            {
                presets.Add(CreateBuiltInPreset(keyCount));
            }

            return new TrackPresetLibrary(
                null, true, presets, DefaultPanPresets(), new List<string>());
        }

        /// <summary>
        /// Finds the preset for a button count: 4, 5, 6 or 8.
        /// </summary>
        /// <param name="keyCount">Button count, matching <see cref="TrackPreset.KeyCount"/>.</param>
        /// <param name="preset">The preset, or null when none matches.</param>
        /// <returns>True when a preset was found.</returns>
        public bool TryFindByKeyCount(int keyCount, out TrackPreset preset)
        {
            for (int i = 0; i < Presets.Count; i++)
            {
                if (Presets[i].KeyCount == keyCount)
                {
                    preset = Presets[i];
                    return true;
                }
            }

            preset = null;
            return false;
        }

        /// <summary>
        /// Finds a preset by its short label (<c>8B</c>), case-insensitively.
        /// </summary>
        public bool TryFindByDisplayName(string displayName, out TrackPreset preset)
        {
            if (!string.IsNullOrEmpty(displayName))
            {
                for (int i = 0; i < Presets.Count; i++)
                {
                    if (string.Equals(
                            Presets[i].DisplayName,
                            displayName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        preset = Presets[i];
                        return true;
                    }
                }
            }

            preset = null;
            return false;
        }

        /// <summary>
        /// Builds one shipped layout from scratch. The numbers are the shipped presets, not
        /// an approximation of them: song track 0 for the spacer, 2 / 9 for SIDE L / R,
        /// 3..8 for the buttons, 10 / 11 for L1 / R1, 1 for BGA SYNC, 22 for MR, and
        /// 23..40 for BG 1..18. Widths are 18 / 60 / 30, and only the first lane of each
        /// visual group carries a separator rule - except MR, which carries the single
        /// heavy rule that splits the play area from the background area.
        /// </summary>
        private static TrackPreset CreateBuiltInPreset(int keyCount)
        {
            // 8B keeps six buttons and adds the L1/R1 lanes on the outside, so its button
            // count is 6, not 8.
            int buttonCount = keyCount == 8 ? 6 : keyCount;
            int[] buttonTypes = ButtonRawTypes(keyCount);

            List<TrackPresetEntry> entries = new List<TrackPresetEntry>();

            Add(entries, TrackLaneKinds.RawBackground, "nothing1", 0,
                TrackPresetEntry.SpacerWidth, TrackPresetEntry.SeparatorThin);
            Add(entries, TrackLaneKinds.RawSide, "SIDE L", 2,
                TrackPresetEntry.PlayableWidth, TrackPresetEntry.SeparatorThin);

            if (keyCount == 8)
            {
                Add(entries, TrackLaneKinds.RawExtra, "L1", 10,
                    TrackPresetEntry.PlayableWidth, TrackPresetEntry.SeparatorThin);
            }

            for (int i = 0; i < buttonCount; i++)
            {
                Add(
                    entries,
                    buttonTypes[i],
                    "button" + (i + 1).ToString(CultureInfo.InvariantCulture),
                    3 + i,
                    TrackPresetEntry.PlayableWidth,
                    i == 0 ? TrackPresetEntry.SeparatorThin : TrackPresetEntry.SeparatorNone);
            }

            if (keyCount == 8)
            {
                Add(entries, TrackLaneKinds.RawExtra, "R1", 11,
                    TrackPresetEntry.PlayableWidth, TrackPresetEntry.SeparatorThin);
            }

            Add(entries, TrackLaneKinds.RawSide, "SIDE R", 9,
                TrackPresetEntry.PlayableWidth, TrackPresetEntry.SeparatorThin);
            Add(entries, TrackLaneKinds.RawBackground, TrackPresetEntry.BgaSyncLaneName, 1,
                TrackPresetEntry.BackgroundWidth, TrackPresetEntry.SeparatorThin);
            Add(entries, TrackLaneKinds.RawSide, TrackPresetEntry.MasterRecordLaneName, 22,
                TrackPresetEntry.BackgroundWidth, TrackPresetEntry.SeparatorHeavy);

            for (int i = 0; i < 18; i++)
            {
                Add(
                    entries,
                    TrackLaneKinds.RawBackground,
                    "BG " + (i + 1).ToString(CultureInfo.InvariantCulture),
                    23 + i,
                    TrackPresetEntry.BackgroundWidth,
                    i == 0 ? TrackPresetEntry.SeparatorThin : TrackPresetEntry.SeparatorNone);
            }

            return new TrackPreset(
                keyCount.ToString(CultureInfo.InvariantCulture) + "B", null, entries, null);
        }

        /// <summary>
        /// The white/black key striping of the button lanes, per mode. 6B and 8B share the
        /// same six-button pattern.
        /// </summary>
        private static int[] ButtonRawTypes(int keyCount)
        {
            int primary = TrackLaneKinds.RawButton;
            int alternate = TrackLaneKinds.RawButtonAlternate;

            switch (keyCount)
            {
                case 4:
                    return new[] { primary, alternate, alternate, primary };
                case 5:
                    return new[] { primary, alternate, primary, alternate, primary };
                default:
                    return new[] { primary, alternate, primary, primary, alternate, primary };
            }
        }

        private static void Add(
            IList<TrackPresetEntry> entries,
            int rawType,
            string name,
            int songTrack,
            int width,
            int separatorWeight)
        {
            entries.Add(new TrackPresetEntry(
                entries.Count, rawType, name, songTrack, width, separatorWeight));
        }

        /// <summary>
        /// Works out which files to load, in which order. Matches ptSequencer: only the
        /// files <c>trackset.ini</c> names are offered, so an unlisted <c>.pst</c> sitting
        /// in the folder stays hidden - unless there is no index file at all.
        /// </summary>
        private static List<string> ResolvePresetPaths(string directory, IList<string> warnings)
        {
            List<string> paths = new List<string>();
            string tracksetPath = Path.Combine(directory, TracksetFileName);

            if (!File.Exists(tracksetPath))
            {
                warnings.Add(
                    TracksetFileName + " not found; using every " + PresetSearchPattern +
                    " in the directory, sorted by name.");
                return GlobPresetPaths(directory, warnings);
            }

            PresetIniDocument ini;
            try
            {
                ini = PresetIniDocument.Parse(File.ReadAllText(tracksetPath, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                warnings.Add(TracksetFileName + " could not be read (" + ex.Message +
                    "); using every " + PresetSearchPattern + " in the directory.");
                return GlobPresetPaths(directory, warnings);
            }

            // total= is advisory here too: keep reading nameN until one is missing, so a
            // stale total does not silently truncate the list.
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; ; i++)
            {
                string key = TracksetNamePrefix + i.ToString(CultureInfo.InvariantCulture);
                string fileName;
                if (!ini.TryGetString(TracksetSection, key, out fileName) ||
                    string.IsNullOrWhiteSpace(fileName))
                {
                    break;
                }

                fileName = fileName.Trim();

                // The shipped index writes the extension; tolerate one that does not.
                if (string.IsNullOrEmpty(Path.GetExtension(fileName)))
                {
                    fileName += TrackPreset.FileExtension;
                }

                string path = Path.Combine(directory, fileName);
                if (!seen.Add(path))
                {
                    warnings.Add(TracksetFileName + " lists " + fileName + " more than once; ignoring the repeat.");
                    continue;
                }

                if (!File.Exists(path))
                {
                    warnings.Add(TracksetFileName + " lists " + fileName + ", which does not exist.");
                    continue;
                }

                paths.Add(path);
            }

            if (paths.Count == 0)
            {
                warnings.Add(
                    TracksetFileName + " named no readable preset; using every " +
                    PresetSearchPattern + " in the directory instead.");
                return GlobPresetPaths(directory, warnings);
            }

            return paths;
        }

        private static List<string> GlobPresetPaths(string directory, IList<string> warnings)
        {
            List<string> paths = new List<string>();
            try
            {
                paths.AddRange(Directory.GetFiles(directory, PresetSearchPattern));
            }
            catch (Exception ex)
            {
                warnings.Add("Could not list " + directory + " (" + ex.Message + ").");
                return paths;
            }

            // Ordinal-ignore-case keeps DPC_4B before DPC_5B on every locale.
            paths.Sort(StringComparer.OrdinalIgnoreCase);
            return paths;
        }

        private static List<int> LoadPanPresets(string directory, IList<string> warnings)
        {
            string path = Path.Combine(directory, CommonFileName);
            if (!File.Exists(path))
            {
                warnings.Add(
                    CommonFileName + " not found; using " + DefaultPanPresetCount +
                    " centred pan presets.");
                return DefaultPanPresets();
            }

            PresetIniDocument ini;
            try
            {
                ini = PresetIniDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                warnings.Add(CommonFileName + " could not be read (" + ex.Message +
                    "); using centred pan presets.");
                return DefaultPanPresets();
            }

            int total;
            if (!ini.TryGetInt(PanSection, PanTotalKey, out total) || total <= 0)
            {
                total = DefaultPanPresetCount;
            }

            List<int> values = new List<int>(total);
            for (int i = 0; i < total; i++)
            {
                string key = PanPresetPrefix + i.ToString(CultureInfo.InvariantCulture);
                int value;
                if (!ini.TryGetInt(PanSection, key, out value))
                {
                    // A missing slot is centre, which is what every shipped slot holds.
                    values.Add(CenterPan);
                    continue;
                }

                if (value < MinimumPan || value > MaximumPan)
                {
                    warnings.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}: [{1}] {2}={3} is outside {4}..{5}; clamped.",
                        CommonFileName, PanSection, key, value, MinimumPan, MaximumPan));
                    value = Math.Min(MaximumPan, Math.Max(MinimumPan, value));
                }

                values.Add(value);
            }

            return values;
        }

        private static List<int> DefaultPanPresets()
        {
            List<int> values = new List<int>(DefaultPanPresetCount);
            for (int i = 0; i < DefaultPanPresetCount; i++)
            {
                values.Add(CenterPan);
            }
            return values;
        }
    }

    /// <summary>
    /// Minimal INI reader for ptSequencer's <c>.pst</c> / <c>.ini</c> files.
    ///
    /// Deliberately hand-rolled: <c>GetPrivateProfileString</c> is a P/Invoke into a
    /// 16-bit-era Win32 API that cannot be unit tested off Windows and mangles anything
    /// non-ANSI, and a NuGet INI package is far more dependency than four rules deserve.
    ///
    /// The rules: <c>[section]</c> opens a section; <c>key=value</c> adds to it; a line
    /// whose first non-space character is <c>;</c> or <c>#</c> is a comment; section names,
    /// keys and values are trimmed; keys and sections match case-insensitively; a repeated
    /// key wins over the earlier one.
    ///
    /// Note that an inline <c>;</c> does <b>not</b> start a comment - lane names are
    /// free-form text, so everything after <c>=</c> is taken literally.
    /// </summary>
    internal sealed class PresetIniDocument
    {
        private readonly Dictionary<string, Dictionary<string, string>> _sections;

        private PresetIniDocument()
        {
            _sections = new Dictionary<string, Dictionary<string, string>>(
                StringComparer.OrdinalIgnoreCase);
            HeaderComment = null;
        }

        /// <summary>
        /// First comment line in the file, before any section, with its leading <c>;</c>
        /// intact. ptSequencer writes <c>; DJMAX_RESPECT_8B_PRESET</c> there and we hand it
        /// straight back on save.
        /// </summary>
        public string HeaderComment { get; private set; }

        /// <summary>Parses whole-file text. Never throws on malformed input.</summary>
        public static PresetIniDocument Parse(string text)
        {
            PresetIniDocument document = new PresetIniDocument();
            if (string.IsNullOrEmpty(text))
            {
                return document;
            }

            Dictionary<string, string> current = null;

            // Split on both endings so a file that has been through a Unix tool still reads.
            string[] lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (line[0] == ';' || line[0] == '#')
                {
                    if (document.HeaderComment == null && current == null)
                    {
                        document.HeaderComment = line;
                    }
                    continue;
                }

                if (line[0] == '[')
                {
                    int close = line.IndexOf(']');
                    if (close <= 1)
                    {
                        // "[", "[]" or a header with no closing bracket: nothing to name.
                        continue;
                    }

                    string sectionName = line.Substring(1, close - 1).Trim();
                    if (!document._sections.TryGetValue(sectionName, out current))
                    {
                        current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        document._sections[sectionName] = current;
                    }
                    continue;
                }

                int separator = line.IndexOf('=');
                if (separator <= 0 || current == null)
                {
                    // A value before any section, or a line with no '=', has nowhere to go.
                    continue;
                }

                string key = line.Substring(0, separator).Trim();
                if (key.Length == 0)
                {
                    continue;
                }

                // Last writer wins, matching how Win32 profile files behave.
                current[key] = line.Substring(separator + 1).Trim();
            }

            return document;
        }

        /// <summary>True when the file contains a section with this name.</summary>
        public bool HasSection(string section)
        {
            return section != null && _sections.ContainsKey(section);
        }

        /// <summary>Reads a raw string value.</summary>
        public bool TryGetString(string section, string key, out string value)
        {
            value = null;
            if (section == null || key == null)
            {
                return false;
            }

            Dictionary<string, string> keys;
            return _sections.TryGetValue(section, out keys) && keys.TryGetValue(key, out value);
        }

        /// <summary>Reads a string value, or <paramref name="fallback"/> when absent.</summary>
        public string GetString(string section, string key, string fallback)
        {
            string value;
            return TryGetString(section, key, out value) ? value : fallback;
        }

        /// <summary>
        /// Reads an integer value. Culture-invariant and sign-aware; returns false when the
        /// key is absent or the value is not a plain integer.
        /// </summary>
        public bool TryGetInt(string section, string key, out int value)
        {
            value = 0;
            string text;
            if (!TryGetString(section, key, out text) || string.IsNullOrEmpty(text))
            {
                return false;
            }

            return int.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value);
        }
    }
}
