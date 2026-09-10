using System;
using System.Globalization;
using System.Text;

namespace DJMaxEditor.Studio.Settings
{
    /// <summary>
    /// Everything the studio remembers between sessions.
    ///
    /// <para>
    /// Until this existed the shell remembered nothing: every default was a literal in
    /// <c>MainWindow</c>'s constructor or in a XAML attribute, so a user who wanted a 1/32 grid, no
    /// note art and a 20 ms buffer set all three again on every launch. The point of a settings
    /// object rather than a pile of static properties is that it can be validated, serialised,
    /// cloned and diffed as one thing - which is what makes "restore defaults" and the round-trip
    /// test possible.
    /// </para>
    /// <para>
    /// Every default below is the value the shell already used, taken from the code it replaces
    /// rather than chosen afresh: a first launch with no settings file must behave exactly like the
    /// build before this class landed. <c>StudioSettingsTests</c> asserts each one.
    /// </para>
    /// <para>
    /// Plain settable properties, no INotifyPropertyChanged: the preferences window reads and writes
    /// the whole object at once (see <c>PreferencesWindow.CommitFromControls</c>) and the shell
    /// applies it in one pass, so per-property change notification would buy nothing and would put
    /// a second, subtler apply path next to the explicit one.
    /// </para>
    /// </summary>
    public sealed class StudioSettings
    {
        /// <summary>
        /// Bumped only for a change no reader can survive. <see cref="Normalise"/> already repairs
        /// missing sections and out-of-range values, so adding a property does not need a bump.
        /// </summary>
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;

        public AudioSettings Audio { get; set; } = new AudioSettings();

        public TimelineSettings Timeline { get; set; } = new TimelineSettings();

        public BgaSettings Bga { get; set; } = new BgaSettings();

        public FormatSettings Format { get; set; } = new FormatSettings();

        public WorkspaceSettings Workspace { get; set; } = new WorkspaceSettings();

        public AppearanceSettings Appearance { get; set; } = new AppearanceSettings();

        /// <summary>
        /// Replaces missing sections and pulls every value back into range.
        ///
        /// Called after every load and before every save, because a settings file is a text file a
        /// user can edit: <c>"Audio": null</c>, a latency of 0, a NaN volume and a grid denominator
        /// of 7 are all things this has to survive without the shell seeing them. Nothing here
        /// throws - a bad value is replaced, never reported, because there is nobody to report it to
        /// at the point it is read.
        /// </summary>
        public void Normalise()
        {
            if (Version <= 0)
            {
                Version = CurrentVersion;
            }

            if (Audio == null) { Audio = new AudioSettings(); }
            if (Timeline == null) { Timeline = new TimelineSettings(); }
            if (Bga == null) { Bga = new BgaSettings(); }
            if (Format == null) { Format = new FormatSettings(); }
            if (Workspace == null) { Workspace = new WorkspaceSettings(); }
            if (Appearance == null) { Appearance = new AppearanceSettings(); }

            Audio.Clamp();
            Timeline.Clamp();
            Bga.Clamp();
            Format.Clamp();
            Workspace.Clamp();
            Appearance.Clamp();
        }

        /// <summary>
        /// A deep copy. The preferences window edits the shell's live object in place, so the shell
        /// keeps one of these from before the window opened - that is what "restore defaults" and a
        /// future cancel button both need.
        /// </summary>
        public StudioSettings Clone()
        {
            return new StudioSettings
            {
                Version = Version,
                Audio = Audio == null ? new AudioSettings() : Audio.Clone(),
                Timeline = Timeline == null ? new TimelineSettings() : Timeline.Clone(),
                Bga = Bga == null ? new BgaSettings() : Bga.Clone(),
                Format = Format == null ? new FormatSettings() : Format.Clone(),
                Workspace = Workspace == null ? new WorkspaceSettings() : Workspace.Clone(),
                Appearance = Appearance == null ? new AppearanceSettings() : Appearance.Clone(),
            };
        }

        /// <summary>
        /// Overwrites every section with <paramref name="other"/>'s, in place.
        ///
        /// The shell and the preferences window both hold the same root object, so "restore
        /// defaults" cannot simply hand back a new instance - it has to change the one everyone is
        /// already pointing at. Sections are replaced with clones rather than shared, so the source
        /// object can be a throwaway <c>new StudioSettings()</c> without the two staying linked.
        /// </summary>
        public void CopyFrom(StudioSettings other)
        {
            if (other == null)
            {
                return;
            }

            Version = other.Version;
            Audio = other.Audio == null ? new AudioSettings() : other.Audio.Clone();
            Timeline = other.Timeline == null ? new TimelineSettings() : other.Timeline.Clone();
            Bga = other.Bga == null ? new BgaSettings() : other.Bga.Clone();
            Format = other.Format == null ? new FormatSettings() : other.Format.Clone();
            Workspace = other.Workspace == null ? new WorkspaceSettings() : other.Workspace.Clone();
            Appearance = other.Appearance == null
                ? new AppearanceSettings()
                : other.Appearance.Clone();
            Normalise();
        }

        /// <summary>
        /// Every value as one canonical <c>section.key=value</c> block.
        ///
        /// This is the comparison used by the tests - a JSON round-trip, and the preferences
        /// window's two directions, are both asserted by describing before and after and comparing
        /// the text, so a property that is serialised but not shown (or shown but not saved) fails
        /// loudly with the offending line in the message instead of passing quietly.
        /// </summary>
        public string Describe()
        {
            StudioSettings copy = Clone();
            copy.Normalise();

            StringBuilder text = new StringBuilder();
            copy.Audio.Describe(text);
            copy.Timeline.Describe(text);
            copy.Bga.Describe(text);
            copy.Format.Describe(text);
            copy.Workspace.Describe(text);
            copy.Appearance.Describe(text);
            return text.ToString();
        }

        // ===================================================================================
        // Shared clamping helpers
        // ===================================================================================

        /// <summary>
        /// Clamps to a range, mapping NaN and the infinities onto <paramref name="fallback"/>.
        ///
        /// The NaN case is not theoretical: <c>Math.Min</c> and <c>Math.Max</c> both propagate NaN,
        /// so a hand-edited file could put one into a Slider's Value and WPF throws on that during
        /// layout - a corrupted settings file would take the window down instead of being repaired.
        /// </summary>
        internal static double Clamp(double value, double min, double max, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return fallback;
            }
            if (value < min) { return min; }
            if (value > max) { return max; }
            return value;
        }

        internal static int Clamp(int value, int min, int max)
        {
            if (value < min) { return min; }
            if (value > max) { return max; }
            return value;
        }

        /// <summary>Trims a path or name, mapping null and whitespace onto the empty string.</summary>
        internal static string CleanText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        internal static void Line(StringBuilder text, string key, bool value)
        {
            text.Append(key).Append('=').Append(value ? "true" : "false").Append('\n');
        }

        internal static void Line(StringBuilder text, string key, int value)
        {
            text.Append(key).Append('=')
                .Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        internal static void Line(StringBuilder text, string key, double value)
        {
            // Fixed precision, invariant culture: the description is compared as text, so a value
            // that differs only in the last float bit must still describe identically.
            text.Append(key).Append('=')
                .Append(value.ToString("0.####", CultureInfo.InvariantCulture)).Append('\n');
        }

        internal static void Line(StringBuilder text, string key, string value)
        {
            text.Append(key).Append('=').Append(value ?? string.Empty).Append('\n');
        }
    }
}
