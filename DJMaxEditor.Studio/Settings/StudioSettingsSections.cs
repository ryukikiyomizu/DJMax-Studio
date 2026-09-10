using System.Text;
using DJMaxEditor.Controls.Vertical;
using DJMaxEditor.Studio.Editing;

namespace DJMaxEditor.Studio.Settings
{
    /// <summary>
    /// Audio backend preferences.
    ///
    /// Two of these cannot be changed on a running graph and say so in the preferences window:
    /// <see cref="OutputLatencyMs"/> is the buffer size a driver was opened with, and
    /// <see cref="KeysoundCacheBudgetMb"/> is the decoded-PCM budget the mixer was constructed with.
    /// Reopening the device under a playing chart would drop every sounding voice, so both are read
    /// once, at startup, by the code that builds the player.
    /// </summary>
    public sealed class AudioSettings
    {
        /// <summary>
        /// Buffer size to ask the driver for, in ms. 60 is <c>NAudioDeviceOutput</c>'s own default
        /// and the reason for it is documented there: keysound triggers are already quantised to the
        /// sequencer tick, so a few ms of buffer are inaudible while a too-small one glitches as soon
        /// as a chart loads several hundred samples.
        /// </summary>
        public int OutputLatencyMs { get; set; } = 60;

        /// <summary>
        /// Multiplies every note and audition gain. Unity by default, so playback is exactly as loud
        /// as the chart asks for; this exists because a .pt's track volumes can exceed unity and
        /// there was no way to pull the whole mix down without editing the chart.
        /// </summary>
        public double MasterVolume { get; set; } = 1.0;

        /// <summary>Gain for a double-clicked sample in the list. Was a hard-coded 1.0.</summary>
        public double AuditionVolume { get; set; } = 1.0;

        /// <summary>
        /// Mirrors <c>NAudioKeysoundPlayer.AllowOverlappingRetrigger</c>, whose XML doc carries the
        /// measurements behind the default. True lets a retriggered keysound ring out; false gives
        /// one voice per channel back, at the cost of the truncation clicks counted there.
        /// </summary>
        public bool AllowOverlappingRetrigger { get; set; } = true;

        /// <summary>Total decoded-PCM budget, in MiB. 512 is the player's own default.</summary>
        public int KeysoundCacheBudgetMb { get; set; } = 512;

        /// <summary>
        /// Whether opening a chart also decodes the keysounds beside it. On by default; off is for
        /// working on a chart whose audio is missing, or on a slow disk where the load is noise.
        /// </summary>
        public bool LoadKeysoundsOnOpen { get; set; } = true;

        internal void Clamp()
        {
            // 10 ms is the floor NAudioDeviceOutput itself enforces; 250 is past the point where a
            // keysound audition feels detached from the double-click that asked for it.
            OutputLatencyMs = StudioSettings.Clamp(OutputLatencyMs, 10, 250);
            MasterVolume = StudioSettings.Clamp(MasterVolume, 0.0, 1.0, 1.0);
            AuditionVolume = StudioSettings.Clamp(AuditionVolume, 0.0, 1.0, 1.0);
            KeysoundCacheBudgetMb = StudioSettings.Clamp(KeysoundCacheBudgetMb, 32, 4096);
        }

        internal AudioSettings Clone()
        {
            return new AudioSettings
            {
                OutputLatencyMs = OutputLatencyMs,
                MasterVolume = MasterVolume,
                AuditionVolume = AuditionVolume,
                AllowOverlappingRetrigger = AllowOverlappingRetrigger,
                KeysoundCacheBudgetMb = KeysoundCacheBudgetMb,
                LoadKeysoundsOnOpen = LoadKeysoundsOnOpen,
            };
        }

        internal void Describe(StringBuilder text)
        {
            StudioSettings.Line(text, "audio.latencyMs", OutputLatencyMs);
            StudioSettings.Line(text, "audio.masterVolume", MasterVolume);
            StudioSettings.Line(text, "audio.auditionVolume", AuditionVolume);
            StudioSettings.Line(text, "audio.overlappingRetrigger", AllowOverlappingRetrigger);
            StudioSettings.Line(text, "audio.cacheBudgetMb", KeysoundCacheBudgetMb);
            StudioSettings.Line(text, "audio.loadKeysoundsOnOpen", LoadKeysoundsOnOpen);
        }
    }

    /// <summary>
    /// How the chart surface starts up.
    ///
    /// These are the states the toolbar's toggles and the left dock's two sliders were declared
    /// with in XAML. Persisting them is the whole reason this file exists: the shell already had
    /// the controls, it just forgot where you left them.
    /// </summary>
    public sealed class TimelineSettings
    {
        /// <summary>Lane width multiplier - the left dock's "Track width" slider.</summary>
        public double TrackWidthScale { get; set; } = 1.0;

        /// <summary>
        /// Pixels per tick - the left dock's "Note height" slider.
        /// <see cref="VerticalTimelineViewModel.BasePixelsPerTick"/> is this value, so the shipped
        /// default is a zoom factor of exactly 1.0.
        /// </summary>
        public double NoteHeight { get; set; } = 0.55;

        /// <summary>Let the viewport pick the lane width. Cleared by touching the width slider.</summary>
        public bool AutoFitColumns { get; set; } = true;

        /// <summary>Scroll to keep the playhead on screen during playback.</summary>
        public bool FollowPlayback { get; set; } = true;

        /// <summary>Draw keysound names inside notes. Off by default: it costs frame time.</summary>
        public bool ShowNoteLabels { get; set; } = false;

        /// <summary>Draw notes as arcade art rather than plain rectangles.</summary>
        public bool ShowNoteArt { get; set; } = true;

        /// <summary>
        /// True for the gameplay reading of the time axis - upward while time runs down the screen,
        /// rightward once the canvas is transposed. False is score order. The shell derives the
        /// surface's own direction enum from this and the orientation together; see
        /// <c>MainWindow.ApplyTimeDirection</c> for why it cannot be stored as the enum.
        /// </summary>
        public bool GameplayTimeDirection { get; set; } = true;

        /// <summary>True for the DAW reading: time across the screen, lanes down it.</summary>
        public bool HorizontalOrientation { get; set; } = false;

        /// <summary>
        /// Bar divisions notes snap to, 0 for Free. Stored as the denominator rather than as an
        /// index into <see cref="GridDivision.All"/> so inserting a division into that list cannot
        /// silently change what an existing settings file means.
        /// </summary>
        public int GridDenominator { get; set; } = 16;

        /// <summary>Which subdivision gets a drawn line, 0 for Off. Never snaps anything.</summary>
        public int BeatDenominator { get; set; } = 4;

        /// <summary>
        /// Multiplier for one zoom step - the toolbar buttons and Ctrl+wheel. 1.25 is what both
        /// were hard-coded to.
        /// </summary>
        public double ZoomStep { get; set; } = 1.25;

        internal void Clamp()
        {
            TrackWidthScale = StudioSettings.Clamp(
                TrackWidthScale,
                VerticalTimelineViewModel.MinColumnScale,
                VerticalTimelineViewModel.MaxColumnScale,
                1.0);
            // The same range the XAML slider declares. The floor is not zero on purpose: a zero
            // pixels-per-tick collapses every note onto one row and there is no gesture back.
            NoteHeight = StudioSettings.Clamp(NoteHeight, 0.05, 4.0, 0.55);
            ZoomStep = StudioSettings.Clamp(ZoomStep, 1.05, 2.5, 1.25);

            // A denominator no list entry carries would leave the combo with nothing selected, and
            // the shell would then read null out of it and keep whatever it had.
            if (GridDivision.FromDenominator(GridDenominator) == null)
            {
                GridDenominator = GridDivision.Default.Denominator;
            }
            if (BeatDisplay.FromDenominator(BeatDenominator) == null)
            {
                BeatDenominator = BeatDisplay.Default.Denominator;
            }
        }

        internal TimelineSettings Clone()
        {
            return new TimelineSettings
            {
                TrackWidthScale = TrackWidthScale,
                NoteHeight = NoteHeight,
                AutoFitColumns = AutoFitColumns,
                FollowPlayback = FollowPlayback,
                ShowNoteLabels = ShowNoteLabels,
                ShowNoteArt = ShowNoteArt,
                GameplayTimeDirection = GameplayTimeDirection,
                HorizontalOrientation = HorizontalOrientation,
                GridDenominator = GridDenominator,
                BeatDenominator = BeatDenominator,
                ZoomStep = ZoomStep,
            };
        }

        internal void Describe(StringBuilder text)
        {
            StudioSettings.Line(text, "timeline.trackWidth", TrackWidthScale);
            StudioSettings.Line(text, "timeline.noteHeight", NoteHeight);
            StudioSettings.Line(text, "timeline.autoFitColumns", AutoFitColumns);
            StudioSettings.Line(text, "timeline.followPlayback", FollowPlayback);
            StudioSettings.Line(text, "timeline.showNoteLabels", ShowNoteLabels);
            StudioSettings.Line(text, "timeline.showNoteArt", ShowNoteArt);
            StudioSettings.Line(text, "timeline.gameplayDirection", GameplayTimeDirection);
            StudioSettings.Line(text, "timeline.horizontal", HorizontalOrientation);
            StudioSettings.Line(text, "timeline.grid", GridDenominator);
            StudioSettings.Line(text, "timeline.beat", BeatDenominator);
            StudioSettings.Line(text, "timeline.zoomStep", ZoomStep);
        }
    }

    /// <summary>
    /// BGA preview preferences.
    ///
    /// The two discovery switches are here because the feature announces itself by opening a panel,
    /// and a tool that rearranges its own layout on file-open needs a way to be told not to. The
    /// FFmpeg path is here because the resolver probes for it and a user with a portable build has
    /// no way to say where it is.
    /// </summary>
    public sealed class BgaSettings
    {
        /// <summary>
        /// Look for a video beside an opened chart. This is what makes a Technika song folder work
        /// without a file dialog; see <c>BgaSourceResolver.FindForChart</c>.
        /// </summary>
        public bool DiscoverBesideChart { get; set; } = true;

        /// <summary>Open the BGA panel the first time a discovered video attaches.</summary>
        public bool AutoOpenPanel { get; set; } = true;

        /// <summary>Open the playfield panel the first time a TECHNIKA chart is adopted.</summary>
        public bool AutoOpenPlayfieldForTechnika { get; set; } = true;

        /// <summary>
        /// An explicit <c>ffmpeg.exe</c>, or empty to probe PATH and the usual install roots. Only
        /// Bink and Smacker previews need it - see <c>BgaSourceResolver</c> for why a Technika
        /// preview cannot be played any other way.
        /// </summary>
        public string FfmpegPath { get; set; } = string.Empty;

        internal void Clamp()
        {
            FfmpegPath = StudioSettings.CleanText(FfmpegPath);
        }

        internal BgaSettings Clone()
        {
            return new BgaSettings
            {
                DiscoverBesideChart = DiscoverBesideChart,
                AutoOpenPanel = AutoOpenPanel,
                AutoOpenPlayfieldForTechnika = AutoOpenPlayfieldForTechnika,
                FfmpegPath = FfmpegPath,
            };
        }

        internal void Describe(StringBuilder text)
        {
            StudioSettings.Line(text, "bga.discover", DiscoverBesideChart);
            StudioSettings.Line(text, "bga.autoOpenPanel", AutoOpenPanel);
            StudioSettings.Line(text, "bga.autoOpenPlayfield", AutoOpenPlayfieldForTechnika);
            StudioSettings.Line(text, "bga.ffmpegPath", FfmpegPath);
        }
    }

    /// <summary>
    /// Format and file-handling preferences.
    ///
    /// <para>
    /// What is deliberately <em>not</em> here: the confirmation before decrypting an encrypted
    /// Technika chart. That prompt is a consent gate, not a convenience - it is the point at which
    /// the user says yes to an offline decryption every single time - so it has no "don't ask again"
    /// switch and the preferences window states that in words rather than offering a disabled one.
    /// </para>
    /// </summary>
    public sealed class FormatSettings
    {
        /// <summary>
        /// The lane layout to select at startup: 0 for Auto, a ptSequencer key count (4/5/6/8),
        /// <see cref="VerticalTrackLayout.TechnikaMode"/> or <see cref="VerticalTrackLayout.BmsMode"/>.
        ///
        /// <para>
        /// Auto is the honest default and stays it: the layout is a property of the chart, and the
        /// detector reads the source format before it looks at the track shape - which is what stops
        /// a seven-key BMS chart, whose notes land on tracks 0-8 and nowhere else, from being drawn
        /// as TECHNIKA's four touch lanes plus scan markers. A user who pins a layout here is
        /// overriding that for every chart they open, which is occasionally what you want and never
        /// what you want by default.
        /// </para>
        /// </summary>
        public int DefaultLayoutMode { get; set; } = 0;

        /// <summary>Reopen the open/save dialogs where the last one was.</summary>
        public bool RememberLastFolder { get; set; } = true;

        /// <summary>Where that was. Maintained by the shell, not shown as an editable field.</summary>
        public string LastFolder { get; set; } = string.Empty;

        internal void Clamp()
        {
            // 0 is Auto and is not a layout, so it cannot go through IsSupportedLayout.
            if (DefaultLayoutMode != 0 &&
                !VerticalTrackLayout.IsSupportedLayout(DefaultLayoutMode))
            {
                DefaultLayoutMode = 0;
            }
            LastFolder = StudioSettings.CleanText(LastFolder);
        }

        internal FormatSettings Clone()
        {
            return new FormatSettings
            {
                DefaultLayoutMode = DefaultLayoutMode,
                RememberLastFolder = RememberLastFolder,
                LastFolder = LastFolder,
            };
        }

        internal void Describe(StringBuilder text)
        {
            StudioSettings.Line(text, "format.defaultLayout", DefaultLayoutMode);
            StudioSettings.Line(text, "format.rememberLastFolder", RememberLastFolder);
            StudioSettings.Line(text, "format.lastFolder", LastFolder);
        }
    }

    /// <summary>
    /// Which panels the shell opens with, and whether it shows its own frame time.
    /// </summary>
    public sealed class WorkspaceSettings
    {
        public bool ShowLeftDock { get; set; } = true;

        public bool ShowRightDock { get; set; } = true;

        public bool ShowVolumeLane { get; set; } = true;

        /// <summary>
        /// The frame-time readout under the inspector. On by default, and deliberately so: a tool
        /// whose entire complaint history is about frame time has no business hiding its own.
        /// </summary>
        public bool ShowPerformanceReadout { get; set; } = true;

        internal void Clamp()
        {
        }

        internal WorkspaceSettings Clone()
        {
            return new WorkspaceSettings
            {
                ShowLeftDock = ShowLeftDock,
                ShowRightDock = ShowRightDock,
                ShowVolumeLane = ShowVolumeLane,
                ShowPerformanceReadout = ShowPerformanceReadout,
            };
        }

        internal void Describe(StringBuilder text)
        {
            StudioSettings.Line(text, "workspace.leftDock", ShowLeftDock);
            StudioSettings.Line(text, "workspace.rightDock", ShowRightDock);
            StudioSettings.Line(text, "workspace.volumeLane", ShowVolumeLane);
            StudioSettings.Line(text, "workspace.perfReadout", ShowPerformanceReadout);
        }
    }
}
