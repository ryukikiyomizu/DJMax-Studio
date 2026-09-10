using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DJMaxEditor.Controls.Vertical;
using DJMaxEditor.Diagnostics;
using DJMaxEditor.Studio.Editing;
using DJMaxEditor.Studio.Settings;
using DJMaxEditor.Studio.Tracks;

namespace DJMaxEditor.Studio.Shell
{
    /// <summary>
    /// The preferences popup: one page per section of <see cref="StudioSettings"/>.
    ///
    /// <para>
    /// It edits the shell's live settings object rather than a copy, and every control writes
    /// through on change - so there is no OK button and no Cancel. That is a deliberate choice and
    /// not a shortcut: all of these are cheap and all of them are visible on the surface behind the
    /// window, so watching one happen is better feedback than a dialog that promises it. The two
    /// that cannot apply live (the driver buffer size and the decoded-audio budget, both
    /// constructor arguments to the audio graph) say so on the page instead of pretending.
    /// </para>
    /// <para>
    /// <see cref="PullFromSettings"/> and <see cref="CommitFromControls"/> are the whole contract,
    /// and they are public because they are also the test seam: pull a non-default object in, commit
    /// straight back out, and compare <c>Describe()</c> before and after. That catches the classic
    /// preferences bug - a control wired to load but not to save, or the reverse - which no amount
    /// of clicking through the window reliably finds.
    /// </para>
    /// </summary>
    public partial class PreferencesWindow : StudioWindow
    {
        private readonly StudioSettings _settings;
        private readonly StudioSettingsStore _store;
        private readonly DispatcherTimer _saveTimer;
        private UIElement[] _pages;

        /// <summary>
        /// False until the controls hold the settings. Slider and ComboBox both raise their change
        /// events while XAML is still being parsed, which is before any of this is wired up.
        /// </summary>
        private bool _ready;

        /// <summary>Defaults, no persistence. For the designer and for tests.</summary>
        public PreferencesWindow()
            : this(new StudioSettings(), null)
        {
        }

        /// <param name="settings">
        /// The shell's live object. Edited in place - the shell sees changes through
        /// <see cref="SettingsChanged"/> and re-reads the same instance.
        /// </param>
        /// <param name="store">Where to persist to, or null not to.</param>
        public PreferencesWindow(StudioSettings settings, StudioSettingsStore store)
        {
            _settings = settings ?? new StudioSettings();
            _settings.Normalise();
            _store = store;

            // Created before InitializeComponent so a change event raised during parsing cannot
            // reach a null timer.
            _saveTimer = new DispatcherTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(700);
            _saveTimer.Tick += OnSaveTick;

            InitializeComponent();

            _pages = new UIElement[]
            {
                AudioPage, TimelinePage, BgaPage, FormatPage, WorkspacePage, AboutPage,
            };

            InitialiseCombos();
            PullFromSettings();
            ShowEnvironment();
            SelectSection(0);

            Closed += OnWindowClosed;
        }

        /// <summary>Raised after every change, once the settings object already holds it.</summary>
        public event EventHandler SettingsChanged;

        /// <summary>The live object this window edits.</summary>
        public StudioSettings Settings { get { return _settings; } }

        /// <summary>How many pages the navigator has.</summary>
        public int SectionCount { get { return _pages == null ? 0 : _pages.Length; } }

        // ===================================================================================
        // Sections
        // ===================================================================================

        /// <summary>Shows one page and selects its navigator row.</summary>
        public void SelectSection(int index)
        {
            if (_pages == null || _pages.Length == 0)
            {
                return;
            }
            if (index < 0) { index = 0; }
            if (index >= _pages.Length) { index = _pages.Length - 1; }

            if (NavList.SelectedIndex != index)
            {
                NavList.SelectedIndex = index;
            }
            ShowSection(index);
        }

        /// <summary>Which page is visible, or -1 while the window is still being built.</summary>
        public int VisibleSection
        {
            get
            {
                if (_pages == null)
                {
                    return -1;
                }
                for (int i = 0; i < _pages.Length; i++)
                {
                    if (_pages[i].Visibility == Visibility.Visible)
                    {
                        return i;
                    }
                }
                return -1;
            }
        }

        private void ShowSection(int index)
        {
            for (int i = 0; i < _pages.Length; i++)
            {
                _pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void OnSectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || _pages == null)
            {
                return;
            }
            ShowSection(NavList.SelectedIndex);
        }

        // ===================================================================================
        // Combo contents
        // ===================================================================================

        private void InitialiseCombos()
        {
            GridCombo.ItemsSource = GridDivision.All;
            BeatCombo.ItemsSource = BeatDisplay.All;

            // Same list the toolbar's picker offers, and for the same reasons - Auto reads the mode
            // out of the chart, the key counts are there for a chart Auto gets wrong, and TECHNIKA
            // and BMS are there because they are channel schemas rather than key counts and would
            // otherwise have no way into the list at all.
            List<LayoutChoice> choices = new List<LayoutChoice>();
            choices.Add(new LayoutChoice(0, "Auto (detect)"));
            foreach (int keyCount in TrackPresetLibrary.ShippedKeyCounts)
            {
                choices.Add(new LayoutChoice(keyCount, keyCount + "B (" + keyCount + " keys)"));
            }
            choices.Add(new LayoutChoice(
                VerticalTrackLayout.TechnikaMode, "TECHNIKA (4 lanes + scans)"));
            choices.Add(new LayoutChoice(VerticalTrackLayout.BmsMode, "BMS (channels)"));
            LayoutCombo.ItemsSource = choices;
        }

        // ===================================================================================
        // Model -> controls
        // ===================================================================================

        /// <summary>
        /// Loads every control from the settings object. Change events are suppressed while this
        /// runs, so it cannot write back what it just read.
        /// </summary>
        public void PullFromSettings()
        {
            bool was = _ready;
            _ready = false;
            try
            {
                _settings.Normalise();

                AudioSettings audio = _settings.Audio;
                LatencySlider.Value = audio.OutputLatencyMs;
                MasterVolumeSlider.Value = audio.MasterVolume;
                AuditionVolumeSlider.Value = audio.AuditionVolume;
                OverlapCheck.IsChecked = audio.AllowOverlappingRetrigger;
                CacheBudgetSlider.Value = audio.KeysoundCacheBudgetMb;
                LoadKeysoundsCheck.IsChecked = audio.LoadKeysoundsOnOpen;

                TimelineSettings timeline = _settings.Timeline;
                TrackWidthSlider.Value = timeline.TrackWidthScale;
                NoteHeightSlider.Value = timeline.NoteHeight;
                ZoomStepSlider.Value = timeline.ZoomStep;
                AutoFitCheck.IsChecked = timeline.AutoFitColumns;
                FollowPlaybackCheck.IsChecked = timeline.FollowPlayback;
                NoteLabelsCheck.IsChecked = timeline.ShowNoteLabels;
                NoteArtCheck.IsChecked = timeline.ShowNoteArt;
                GameplayDirectionCheck.IsChecked = timeline.GameplayTimeDirection;
                HorizontalCheck.IsChecked = timeline.HorizontalOrientation;
                // Normalise has already guaranteed both denominators name a real entry, so the
                // fallbacks below are belt and braces rather than a live path.
                GridCombo.SelectedItem =
                    GridDivision.FromDenominator(timeline.GridDenominator) ?? GridDivision.Default;
                BeatCombo.SelectedItem =
                    BeatDisplay.FromDenominator(timeline.BeatDenominator) ?? BeatDisplay.Default;

                BgaSettings bga = _settings.Bga;
                DiscoverCheck.IsChecked = bga.DiscoverBesideChart;
                AutoOpenPanelCheck.IsChecked = bga.AutoOpenPanel;
                AutoOpenPlayfieldCheck.IsChecked = bga.AutoOpenPlayfieldForTechnika;
                FfmpegPathBox.Text = bga.FfmpegPath ?? string.Empty;

                FormatSettings format = _settings.Format;
                SelectLayout(format.DefaultLayoutMode);
                RememberFolderCheck.IsChecked = format.RememberLastFolder;

                WorkspaceSettings workspace = _settings.Workspace;
                LeftDockCheck.IsChecked = workspace.ShowLeftDock;
                RightDockCheck.IsChecked = workspace.ShowRightDock;
                VolumeLaneCheck.IsChecked = workspace.ShowVolumeLane;
                PerfReadoutCheck.IsChecked = workspace.ShowPerformanceReadout;
            }
            finally
            {
                _ready = true;
            }

            UpdateReadouts();
            if (!was)
            {
                // First pull: nothing has changed yet, so no save is owed.
                _saveTimer.Stop();
            }
        }

        private void SelectLayout(int mode)
        {
            List<LayoutChoice> choices = LayoutCombo.ItemsSource as List<LayoutChoice>;
            if (choices == null)
            {
                return;
            }
            foreach (LayoutChoice choice in choices)
            {
                if (choice.Mode == mode)
                {
                    LayoutCombo.SelectedItem = choice;
                    return;
                }
            }
            LayoutCombo.SelectedIndex = 0;
        }

        // ===================================================================================
        // Controls -> model
        // ===================================================================================

        /// <summary>
        /// Writes every control back into the settings object, normalises it, refreshes the
        /// readouts, schedules a save and raises <see cref="SettingsChanged"/>.
        /// </summary>
        public void CommitFromControls()
        {
            AudioSettings audio = _settings.Audio;
            audio.OutputLatencyMs = (int)Math.Round(LatencySlider.Value);
            audio.MasterVolume = MasterVolumeSlider.Value;
            audio.AuditionVolume = AuditionVolumeSlider.Value;
            audio.AllowOverlappingRetrigger = OverlapCheck.IsChecked == true;
            audio.KeysoundCacheBudgetMb = (int)Math.Round(CacheBudgetSlider.Value);
            audio.LoadKeysoundsOnOpen = LoadKeysoundsCheck.IsChecked == true;

            TimelineSettings timeline = _settings.Timeline;
            timeline.TrackWidthScale = TrackWidthSlider.Value;
            timeline.NoteHeight = NoteHeightSlider.Value;
            timeline.ZoomStep = ZoomStepSlider.Value;
            timeline.AutoFitColumns = AutoFitCheck.IsChecked == true;
            timeline.FollowPlayback = FollowPlaybackCheck.IsChecked == true;
            timeline.ShowNoteLabels = NoteLabelsCheck.IsChecked == true;
            timeline.ShowNoteArt = NoteArtCheck.IsChecked == true;
            timeline.GameplayTimeDirection = GameplayDirectionCheck.IsChecked == true;
            timeline.HorizontalOrientation = HorizontalCheck.IsChecked == true;

            // A ComboBox with nothing selected must leave the stored value alone rather than
            // writing a zero: 0 is a real value for both of these (Free, and beat lines off).
            GridDivision division = GridCombo.SelectedItem as GridDivision;
            if (division != null)
            {
                timeline.GridDenominator = division.Denominator;
            }
            BeatDisplay beat = BeatCombo.SelectedItem as BeatDisplay;
            if (beat != null)
            {
                timeline.BeatDenominator = beat.Denominator;
            }

            BgaSettings bga = _settings.Bga;
            bga.DiscoverBesideChart = DiscoverCheck.IsChecked == true;
            bga.AutoOpenPanel = AutoOpenPanelCheck.IsChecked == true;
            bga.AutoOpenPlayfieldForTechnika = AutoOpenPlayfieldCheck.IsChecked == true;
            bga.FfmpegPath = FfmpegPathBox.Text;

            FormatSettings format = _settings.Format;
            LayoutChoice layout = LayoutCombo.SelectedItem as LayoutChoice;
            if (layout != null)
            {
                format.DefaultLayoutMode = layout.Mode;
            }
            format.RememberLastFolder = RememberFolderCheck.IsChecked == true;

            WorkspaceSettings workspace = _settings.Workspace;
            workspace.ShowLeftDock = LeftDockCheck.IsChecked == true;
            workspace.ShowRightDock = RightDockCheck.IsChecked == true;
            workspace.ShowVolumeLane = VolumeLaneCheck.IsChecked == true;
            workspace.ShowPerformanceReadout = PerfReadoutCheck.IsChecked == true;

            _settings.Normalise();
            UpdateReadouts();
            ScheduleSave();

            EventHandler handler = SettingsChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        // ===================================================================================
        // Change events
        // ===================================================================================

        private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_ready)
            {
                CommitFromControls();
            }
        }

        private void OnToggleChanged(object sender, RoutedEventArgs e)
        {
            if (_ready)
            {
                CommitFromControls();
            }
        }

        private void OnComboChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_ready)
            {
                CommitFromControls();
            }
        }

        private void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_ready)
            {
                CommitFromControls();
            }
        }

        // ===================================================================================
        // Readouts
        // ===================================================================================

        private void UpdateReadouts()
        {
            LatencyReadout.Text = ((int)Math.Round(LatencySlider.Value))
                .ToString(CultureInfo.InvariantCulture) + " ms";
            MasterVolumeReadout.Text = Percent(MasterVolumeSlider.Value);
            AuditionVolumeReadout.Text = Percent(AuditionVolumeSlider.Value);
            CacheBudgetReadout.Text = ((int)Math.Round(CacheBudgetSlider.Value))
                .ToString(CultureInfo.InvariantCulture) + " MiB";

            TrackWidthReadout.Text =
                TrackWidthSlider.Value.ToString("0.00", CultureInfo.InvariantCulture) + "x";
            NoteHeightReadout.Text =
                NoteHeightSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
            ZoomStepReadout.Text =
                ZoomStepSlider.Value.ToString("0.00", CultureInfo.InvariantCulture) + "x";

            FfmpegStateText.Text = DescribeFfmpeg(FfmpegPathBox.Text);
            LastFolderText.Text = string.IsNullOrWhiteSpace(_settings.Format.LastFolder)
                ? "Nothing remembered yet - the next dialog opens wherever Windows puts it."
                : _settings.Format.LastFolder;
        }

        private static string Percent(double value)
        {
            return ((int)Math.Round(value * 100.0)).ToString(CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>
        /// Says whether the configured ffmpeg is actually there. A path that was right on the
        /// machine the settings file came from is the normal way this goes wrong, and silence about
        /// it turns into "why does this Bink file not preview".
        /// </summary>
        private static string DescribeFfmpeg(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "Not set - PATH and the usual install roots are searched instead.";
            }
            try
            {
                return File.Exists(path)
                    ? "Found."
                    : "Nothing at that path. Bink and Smacker previews will not play.";
            }
            catch (Exception)
            {
                // A malformed path (a bare drive letter, an illegal character) throws rather than
                // returning false, and this is a label.
                return "That path cannot be read.";
            }
        }

        /// <summary>
        /// The Diagnostics page's three read-only facts. Set once - none of them can change while
        /// the window is open.
        /// </summary>
        private void ShowEnvironment()
        {
            string settingsPath = _store != null ? _store.Path_ : StudioSettingsStore.DefaultPath;
            SettingsPathBox.Text = settingsPath;
            FooterStatus.Text = settingsPath;

            if (_store == null)
            {
                SettingsStateText.Text =
                    "This window is not saving: it was opened without a store, which only happens " +
                    "in a test.";
            }
            else if (_store.LoadedFromDisk)
            {
                SettingsStateText.Text = "Read from this file at startup.";
            }
            else
            {
                SettingsStateText.Text =
                    "No usable file at startup, so these are the shipped defaults. That is also " +
                    "what you see if the file was there but could not be read - the log says which.";
            }

            try
            {
                LogPathBox.Text = DiagnosticLog.LogPath;
            }
            catch (Exception)
            {
                LogPathBox.Text = "(unavailable)";
            }

            Version version = typeof(PreferencesWindow).Assembly.GetName().Version;
            VersionText.Text = "DJMax Studio " +
                (version == null ? "(unknown)" : version.ToString()) +
                "   settings v" + StudioSettings.CurrentVersion.ToString(CultureInfo.InvariantCulture);
        }

        // ===================================================================================
        // Buttons
        // ===================================================================================

        private void OnRestoreDefaults(object sender, RoutedEventArgs e)
        {
            RestoreDefaults();
        }

        /// <summary>
        /// Puts every page back to the shipped value, in place, then re-reads the controls from it.
        ///
        /// The remembered folder survives: it is bookkeeping the shell maintains rather than a
        /// preference anyone set, and forgetting it would be a side effect nobody asked this button
        /// for.
        /// </summary>
        public void RestoreDefaults()
        {
            StudioSettings fresh = new StudioSettings();
            fresh.Format.LastFolder = _settings.Format.LastFolder;
            _settings.CopyFrom(fresh);
            PullFromSettings();
            CommitFromControls();
        }

        private void OnBrowseFfmpeg(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog();
            dialog.Title = "Locate ffmpeg.exe";
            dialog.Filter = "ffmpeg|ffmpeg.exe|Programs|*.exe|All files|*.*";
            dialog.CheckFileExists = true;
            try
            {
                string current = FfmpegPathBox.Text;
                if (!string.IsNullOrWhiteSpace(current))
                {
                    string directory = Path.GetDirectoryName(current);
                    if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                    {
                        dialog.InitialDirectory = directory;
                    }
                }
            }
            catch (Exception)
            {
                // A malformed current value is not a reason not to open the dialog.
            }

            if (dialog.ShowDialog(this) == true)
            {
                FfmpegPathBox.Text = dialog.FileName;
            }
        }

        private void OnClearFfmpeg(object sender, RoutedEventArgs e)
        {
            FfmpegPathBox.Text = string.Empty;
        }

        // ===================================================================================
        // Persistence
        // ===================================================================================

        private void ScheduleSave()
        {
            if (_store == null)
            {
                return;
            }
            // Dragging a slider raises hundreds of these. Restarting the timer on each one means
            // the file is written once, when the hand comes off.
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void OnSaveTick(object sender, EventArgs e)
        {
            _saveTimer.Stop();
            SaveNow();
        }

        /// <summary>
        /// Writes the settings out immediately, cancelling any pending debounced save. Returns
        /// false if there is no store or the write failed - the failure is logged, not shown.
        /// </summary>
        public bool SaveNow()
        {
            _saveTimer.Stop();
            return _store != null && _store.Save(_settings);
        }

        private void OnWindowClosed(object sender, EventArgs e)
        {
            _saveTimer.Tick -= OnSaveTick;
            SaveNow();
        }

        // ===================================================================================
        // Window chrome
        // ===================================================================================

        private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            BeginTitleBarDrag(e);
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        }

        /// <summary>One row of the layout picker. Mirrors the toolbar's own private choice type.</summary>
        private sealed class LayoutChoice
        {
            public LayoutChoice(int mode, string label)
            {
                Mode = mode;
                Label = label;
            }

            public int Mode { get; private set; }

            public string Label { get; private set; }

            public override string ToString()
            {
                return Label;
            }
        }
    }
}
