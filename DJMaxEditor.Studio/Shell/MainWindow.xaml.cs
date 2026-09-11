using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DJMaxEditor.Controls.Vertical;
using DJMaxEditor.Diagnostics;
using DJMaxEditor.DJMax;
using DJMaxEditor.Editor;
using DJMaxEditor.Files.FormatDetection;
using DJMaxEditor.Files.Tech;
using DJMaxEditor.Preview;
using DJMaxEditor.Studio.Audio;
using DJMaxEditor.Studio.Design;
using DJMaxEditor.Studio.Documents;
using DJMaxEditor.Studio.Editing;
using DJMaxEditor.Studio.Preview;
using DJMaxEditor.Studio.Settings;
using DJMaxEditor.Studio.Timeline;
using DJMaxEditor.Studio.Tracks;
using DJMaxEditor.Studio.Video;

namespace DJMaxEditor.Studio.Shell
{
    /// <summary>
    /// The studio shell.
    ///
    /// Everything below the UI is shared with the legacy editor by source, not by copy: the chart
    /// loaders, the sequencer, the tick geometry and the four Core editing services are the same
    /// files compiled into both exes. This class is the part that is genuinely new - a WPF
    /// retained-mode canvas, a managed audio backend, and one playback pump driven by the
    /// compositor instead of three WinForms timers all repainting the world at idle.
    /// </summary>
    public partial class MainWindow : StudioWindow
    {
        /// <summary>
        /// How thick the note-volume lane is across its value axis, in DIPs: its width docked
        /// beside the canvas, its height docked under it. One number for both, so rotating the
        /// timeline does not quietly change how much of a value the lane can show.
        /// </summary>
        private const double VolumeLaneThickness = 92;

        /// <summary>
        /// How many voices one track may be given back by a single seek, when overlapping retrigger is
        /// on and a track can therefore owe more than one.
        ///
        /// <para>
        /// A budget rather than a limit anyone should hit: measured over the corpus, a keysound track
        /// carries about a hundred notes across a two-minute chart and its samples are shorter than the
        /// gaps between them, so one or two voices is the normal answer and the MR's track holds a
        /// single note. Four is the point past which a chart would have to be pathological - and if one
        /// is, this is what stops a seek from pushing hundreds of inputs into the mixer at once.
        /// </para>
        /// </summary>
        private const int MaxRestoredVoicesPerTrack = 4;

        private readonly ChartFileService _files = new ChartFileService();
        private readonly VerticalTimelineViewModel _viewModel = new VerticalTimelineViewModel();
        private readonly StudioVerticalCanvas _canvas = new StudioVerticalCanvas();
        private readonly StudioVolumeLane _volumeLane = new StudioVolumeLane();
        private readonly BgaPreview _bga = new BgaPreview();
        private readonly BgaClockMap _bgaClock = new BgaClockMap();
        private readonly TechnikaPlayfieldView _playfield = new TechnikaPlayfieldView();
        private readonly RespectPlayfieldView _respectPlayfield = new RespectPlayfieldView();

        /// <summary>
        /// The playfield view the render pump is driving right now - TECHNIKA for PT charts,
        /// RESPECT V for its own container, and null for everything else, where "no gameplay
        /// preview" is the honest answer. Swapped by <see cref="BindPlayfield"/>.
        /// </summary>
        private IGameplayPlayfieldView _activePlayfield;

        private NAudioKeysoundPlayer _audio;
        private Player _player;
        private EditorDocumentContext _document;
        private TrackPresetLibrary _presets;
        private TimelineColumnScroll _columnScroll;

        /// <summary>
        /// Where the studio's own preferences live, and the object every apply site reads.
        ///
        /// <para>
        /// Loaded first thing in the constructor because two of the values in it - the output latency
        /// and the keysound cache budget - are arguments to the audio backend's constructor rather
        /// than properties on it, so <see cref="InitialiseAudio"/> has to already know them. The rest
        /// are pushed into the controls by <see cref="ApplySettings"/> once the XAML has been parsed.
        /// </para>
        /// </summary>
        private readonly StudioSettingsStore _settingsStore = new StudioSettingsStore();
        private StudioSettings _settings;

        /// <summary>
        /// The open preferences window, if there is one. Non-modal and single-instance: it edits the
        /// same <see cref="_settings"/> object this window holds, so a second copy would be two
        /// views of one model racing each other, and modal would stop you seeing your own change.
        /// </summary>
        private PreferencesWindow _preferences;

        /// <summary>
        /// The open theme picker, if there is one. Non-modal and single-instance for the same reason
        /// the preferences window is: it is a live view of a shell setting, so a second copy would
        /// be two views of one model racing each other, and modal would put a dialog between you and
        /// the chart you are judging the palette on.
        /// </summary>
        private ThemePickerWindow _themePicker;

        /// <summary>Set by the BGA file picker; the decoder attaches to it in <see cref="AttachBgaAsync"/>.</summary>
        private string _bgaPath;

        /// <summary>
        /// True once the user has clicked the BGA toggle themselves. Until then a discovered video
        /// is allowed to open the panel; afterwards their choice stands. See <see cref="DiscoverBga"/>.
        /// </summary>
        private bool _bgaPanelChosen;

        /// <summary>
        /// Same rule as <see cref="_bgaPanelChosen"/>: opening a TECHNIKA chart may open the
        /// playfield panel once, but only until the user has expressed a preference by clicking the
        /// toggle themselves.
        /// </summary>
        private bool _playfieldPanelChosen;

        /// <summary>
        /// Same rule as the panel flags above: adopting a TECHNIKA chart may switch the timeline
        /// to the DAW reading once (item: it is the layout the game and its editors are read in),
        /// but only until the user has picked an orientation themselves this session.
        /// </summary>
        private bool _orientationChosen;

        /// <summary>
        /// The orientation the settings file held at load. SaveSettings prefers this over the
        /// toggle while <see cref="_orientationChosen"/> is false, so an adopt-time default is
        /// not written back as if it were a preference.
        /// </summary>
        private bool _persistedOrientation;

        /// <summary>Same rule again, for the chart theme a newly adopted file suggests.</summary>
        private bool _themeChosen;

        /// <summary>The theme id the settings file held at load; see <see cref="_persistedOrientation"/>.</summary>
        private string _persistedThemeId;

        /// <summary>
        /// The open chart's detected container format, captured at adopt time. Every per-format
        /// behaviour below drives off this one answer: which toolbar toggles exist, which default
        /// orientation and theme the chart suggests, and whether the note palette has meaning.
        /// </summary>
        private ChartFormat? _chartFormat;

        /// <summary>
        /// The default-layout preference last pushed into the toolbar picker and the view model.
        ///
        /// <para>
        /// The toolbar's layout picker is a live override, while the preference is the default it
        /// starts from - and <see cref="ApplySettings"/> runs on <em>every</em> preferences edit,
        /// not just a layout change. Without this, forcing BMS on the toolbar and then picking the
        /// IIDX theme (or dragging any preferences slider) silently snapped the layout back to
        /// Auto, because pushing the unchanged default stomped the override. Only a preference
        /// value that actually changed since the last push is pushed again.
        /// </para>
        /// </summary>
        private int _appliedDefaultLayout = int.MinValue;

        private bool _pumpAttached;
        private int _lastPumpVirtualTick = -1;
        private bool _suppressComboEvents;

        /// <summary>
        /// True while the NoteSpeed slider is being written from the view model rather than by
        /// the user. Setting <c>Slider.Value</c> in code raises <c>ValueChanged</c> like a drag
        /// does, so without this the two-way zoom sync would answer its own echo.
        /// </summary>
        private bool _syncingNoteSpeed;

        private double _lastFrameMilliseconds;

        /// <summary>
        /// False until the constructor has finished.
        ///
        /// Slider and ComboBox raise ValueChanged/SelectionChanged *during* XAML parsing - setting
        /// Minimum coerces Value, which fires the handler while the elements declared further down
        /// the file are still null. One flag is the honest guard; per-field null checks only cover
        /// whichever field you happened to think of.
        /// </summary>
        private bool _ready;

        /// <summary>Notes in the open chart, counted once at adopt time. See <see cref="CountNotes"/>.</summary>
        private int _noteTotal;

        /// <summary>
        /// Cancels the in-flight background keysound load. Non-null only while one is running; see
        /// <see cref="LoadKeysounds"/>.
        /// </summary>
        private CancellationTokenSource _keysoundLoad;

        public MainWindow()
        {
            // Before the XAML, let alone before the audio device: the latency and cache budget in
            // here are constructor arguments further down, and a load that failed still hands back
            // a fully normalised object, so nothing after this line has to consider a null.
            _settings = _settingsStore.Load();

            InitializeComponent();

            LoadPaletteIcons();

            _canvas.ViewModel = _viewModel;
            _volumeLane.ViewModel = _viewModel;
            CanvasHost.Child = _canvas;
            VolumeLaneHost.Child = _volumeLane;
            BgaHost.Child = _bga;
            PlayfieldHost.Child = _playfield;

            _canvas.SeekRequested += OnCanvasSeekRequested;
            _canvas.InteractionCompleted += OnCanvasInteractionCompleted;
            _canvas.ContextRequested += OnCanvasContextRequested;
            _canvas.NoteClicked += OnCanvasNoteClicked;
            _canvas.GridCycleRequested += OnCanvasGridCycle;
            _volumeLane.VolumeEdited += OnCanvasInteractionCompleted;

            // The other half of the zoom sync: toolbar zoom and Alt+wheel change the model's zoom
            // without touching the slider, so the slider follows the model here. The slider's own
            // handler is the half going the other way.
            _viewModel.TimeZoomChanged += OnTimeZoomChanged;

            // Owns the bar from here on: it subscribes to the canvas's FrameBuilt itself, so the
            // shell only has to say which edge the bar sits on.
            _columnScroll = new TimelineColumnScroll(_canvas, ColumnScroll);
            PlaceColumnScroll();

            // WPF already scales the whole visual tree, so the geometry must be told the scale is
            // 1.0 or every measurement gets the monitor DPI applied twice. Text is the exception -
            // TextCache is handed the real PixelsPerDip so glyphs are hinted for the right grid.
            _viewModel.DpiScale = 1.0f;
            _viewModel.AutoFitColumns = true;
            _viewModel.FollowPlayback = true;

            _canvas.Tool = ToolMode.Select;
            _canvas.Grid = GridDivision.Default;
            _canvas.Beats = BeatDisplay.Default;
            _canvas.ShowNoteLabels = false;
            _canvas.ShowNoteAssets = AssetsToggle.IsChecked == true;

            // Derived rather than assigned, from the two toggles' declared states: vertical and
            // "gameplay order" is Upward. One code path decides it, so the buttons cannot start out
            // meaning something different from what they mean after the first click.
            ApplyTimeDirection();

            InitialiseCombos();
            InitialiseAudio();

            // Controls exist and the mixer is up, so the saved preferences can go into both. Run
            // before _ready on purpose: ApplySettings suppresses the handlers itself and pushes
            // straight through to the canvas and the view model, so nothing is applied twice and no
            // handler sees a half-built window.
            ApplySettings(_settings);

            _ready = true;

            // The sliders now carry the saved values (or their declared defaults on a first launch),
            // so applying them once here is what makes the two agree from the first frame.
            _viewModel.ColumnScale = TrackWidthSlider.Value;
            _viewModel.TrySetTimeZoom(
                (float)(NoteSpeedSlider.Value / VerticalTimelineViewModel.BasePixelsPerTick));
            _viewModel.NoteThickness = NoteHeightSlider.Value;
            RefreshPlayGlyph();
            RefreshStatus();

            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        // ===================================================================================
        // Startup / shutdown
        // ===================================================================================

        private void InitialiseCombos()
        {
            _suppressComboEvents = true;

            GridCombo.ItemsSource = GridDivision.All;
            GridCombo.SelectedItem = GridDivision.Default;

            BeatCombo.ItemsSource = BeatDisplay.All;
            BeatCombo.SelectedItem = BeatDisplay.Default;

            // The lane layout is a property of the chart's key count, so "Auto" is the honest
            // default: it reads the mode out of the chart instead of asking the user to know it.
            // The explicit entries exist because a chart can be ambiguous, and because you
            // sometimes want to see an 8B layout for a 6B chart while editing.
            _presets = LoadPresets();
            List<PresetChoice> choices = new List<PresetChoice>();
            choices.Add(new PresetChoice(0, "Auto (detect)"));
            foreach (int keyCount in TrackPresetLibrary.ShippedKeyCounts)
            {
                TrackPreset preset;
                string label = _presets != null && _presets.TryFindByKeyCount(keyCount, out preset)
                    ? preset.DisplayName + " (" + keyCount + " keys)"
                    : keyCount + "B";
                choices.Add(new PresetChoice(keyCount, label));
            }
            // TECHNIKA is not a key count, so it is not in ShippedKeyCounts and had no way into
            // this list. Without it the only layouts on offer were 4B-8B, and a TECHNIKA chart
            // that Auto misread could not be corrected by hand.
            choices.Add(new PresetChoice(
                VerticalTrackLayout.TechnikaMode, "TECHNIKA (4 lanes + scans)"));
            // BMS is the same story one step further out: it is a channel schema, not a key count,
            // and its columns come from the chart's own #mmmCC channels rather than from a preset -
            // which is why the label says "channels" and not a lane count. Auto picks it from the
            // source format, so this entry is for forcing it (and for saying out loud that the
            // timeline can draw one).
            choices.Add(new PresetChoice(
                VerticalTrackLayout.BmsMode, "BMS (channels)"));
            PresetCombo.ItemsSource = choices;
            PresetCombo.SelectedIndex = 0;

            _suppressComboEvents = false;
        }

        /// <summary>
        /// Track presets come from a <c>presets</c> folder beside the exe if one is there, and from
        /// the built-in copies of DPC_4B/5B/6B/8B otherwise. The fallback is not a stub - it is a
        /// faithful copy of the four shipped layouts, so the editor opens every chart with no
        /// external files at all.
        /// </summary>
        private static TrackPresetLibrary LoadPresets()
        {
            try
            {
                string directory = Path.Combine(
                    Path.GetDirectoryName(typeof(MainWindow).Assembly.Location) ?? ".", "presets");
                if (Directory.Exists(directory))
                {
                    return TrackPresetLibrary.Load(directory);
                }
            }
            catch (Exception ex)
            {
                DJMaxEditor.Diagnostics.DiagnosticLog.Exception("presets.load", ex);
            }
            return TrackPresetLibrary.CreateFallback();
        }

        /// <summary>
        /// Brings up the mixer and the sequencer.
        ///
        /// <para>
        /// The output latency and the keysound cache budget are read from the settings here and
        /// nowhere else, because both are constructor arguments rather than properties: a device is
        /// opened with a buffer size and a cache is created with a ceiling, and neither can be
        /// re-negotiated afterwards without tearing down every loaded sample. That is why the
        /// preferences window says those two take effect on restart instead of pretending otherwise.
        /// </para>
        /// </summary>
        private void InitialiseAudio()
        {
            AudioSettings audio = _settings.Audio;
            try
            {
                long cacheBytes = (long)audio.KeysoundCacheBudgetMb * 1024L * 1024L;
                _audio = new NAudioKeysoundPlayer(
                    new NAudioDeviceOutput(audio.OutputLatencyMs), true, cacheBytes);
                _audio.Log = message => Logs.Write(message);
                _audio.AllowOverlappingRetrigger = audio.AllowOverlappingRetrigger;
            }
            catch (Exception ex)
            {
                // The device chain already degrades to a null output rather than throwing, so
                // getting here means something worse. A silent editor still edits.
                DJMaxEditor.Diagnostics.DiagnosticLog.Exception("audio.init", ex);
                NullAudioOutput output;
                _audio = NAudioKeysoundPlayer.CreateWithoutDevice(out output);
                StatusHint.Text = "Audio device unavailable - editing only";
            }

            _player = new Player();
            _player.OnEvent += OnPlayerEvent;
            _player.OnStatusChange += OnPlayerStatusChanged;
        }

        // ===================================================================================
        // Preferences
        // ===================================================================================

        /// <summary>
        /// Pushes every saved preference into the controls and the objects behind them.
        ///
        /// <para>
        /// One apply path, called from the constructor and again on every edit the preferences window
        /// makes, which is what stops "the setting works at startup but not when you change it" and
        /// its mirror image from being possible. It suppresses the shell's own handlers for the
        /// duration and writes through to the canvas and the view model itself: a slider assignment
        /// that reached <see cref="OnTrackWidthChanged"/> would clear <c>AutoFitColumns</c> as a side
        /// effect and undo the very setting two lines further down.
        /// </para>
        /// <para>
        /// The toggle-backed settings only invoke their handler when the value actually changes. That
        /// is not an optimisation - <see cref="OnToggleOrientation"/> re-docks the volume lane and
        /// re-derives the time direction, and running it for a value that already matches would do
        /// that work for nothing on every launch.
        /// </para>
        /// <para>
        /// Two things deliberately do not happen here. The output latency and cache budget are
        /// constructor arguments (see <see cref="InitialiseAudio"/>), and the master and audition
        /// volumes are read at the gain sites rather than pushed, because a stored gain has to apply
        /// to notes that have not been played yet.
        /// </para>
        /// </summary>
        private void ApplySettings(StudioSettings settings)
        {
            if (settings == null)
            {
                return;
            }
            settings.Normalise();

            bool wasReady = _ready;
            _ready = false;
            _suppressComboEvents = true;
            try
            {
                ApplyAudioSettings(settings.Audio);
                ApplyTimelineSettings(settings.Timeline);
                ApplyBgaSettings(settings.Bga);
                ApplyFormatSettings(settings.Format);
                ApplyWorkspaceSettings(settings.Workspace);
                ApplyAppearanceSettings(settings.Appearance);
            }
            finally
            {
                _suppressComboEvents = false;
                _ready = wasReady;
            }

            _canvas.InvalidateAll();
            _volumeLane.InvalidateVisual();
            RefreshStatus();
        }

        private void ApplyAudioSettings(AudioSettings audio)
        {
            if (audio == null || _audio == null)
            {
                return;
            }
            // The only audio setting that can change under a running mixer: it decides whether a
            // retriggered channel layers or cuts, which is a decision the graph makes per note.
            _audio.AllowOverlappingRetrigger = audio.AllowOverlappingRetrigger;
        }

        private void ApplyTimelineSettings(TimelineSettings timeline)
        {
            if (timeline == null)
            {
                return;
            }

            TrackWidthSlider.Value = timeline.TrackWidthScale;
            TrackWidthReadout.Text =
                timeline.TrackWidthScale.ToString("0.00", CultureInfo.InvariantCulture) + "x";
            _viewModel.ColumnScale = timeline.TrackWidthScale;

            NoteSpeedSlider.Value = timeline.NoteSpeed;
            NoteSpeedReadout.Text = timeline.NoteSpeed.ToString("0.00", CultureInfo.InvariantCulture);
            _viewModel.TrySetTimeZoom(
                (float)(timeline.NoteSpeed / VerticalTimelineViewModel.BasePixelsPerTick));

            NoteHeightSlider.Value = timeline.NoteThickness;
            NoteHeightReadout.Text = timeline.NoteThickness.ToString("0.00", CultureInfo.InvariantCulture);
            _viewModel.NoteThickness = timeline.NoteThickness;

            // After the width, never before: assigning ColumnScale is what the auto-fit flag is
            // about, so the flag has to be the last word on it.
            _viewModel.AutoFitColumns = timeline.AutoFitColumns;
            _viewModel.FollowPlayback = timeline.FollowPlayback;

            LabelsToggle.IsChecked = timeline.ShowNoteLabels;
            _canvas.ShowNoteLabels = timeline.ShowNoteLabels;
            AssetsToggle.IsChecked = timeline.ShowNoteArt;
            _canvas.ShowNoteAssets = timeline.ShowNoteArt;
            _canvas.InverseScrolling = timeline.InverseScrolling;

            GridDivision division = GridDivision.FromDenominator(timeline.GridDenominator);
            if (division != null)
            {
                GridCombo.SelectedItem = division;
                _canvas.Grid = division;
            }

            BeatDisplay beats = BeatDisplay.FromDenominator(timeline.BeatDenominator);
            if (beats != null)
            {
                BeatCombo.SelectedItem = beats;
                _canvas.Beats = beats;
            }

            // The value as loaded, kept for SaveSettings: an adopt-time default (ApplyDocumentShape)
            // may move the toggle without the user ever choosing an orientation, and a default that
            // silently persisted itself would stop being a default next session.
            _persistedOrientation = timeline.HorizontalOrientation;
            if ((OrientationToggle.IsChecked == true) != timeline.HorizontalOrientation)
            {
                OrientationToggle.IsChecked = timeline.HorizontalOrientation;
                OnToggleOrientation(this, null);
            }

            // Last of the two, because OnToggleOrientation re-derives the direction from both.
            if ((DirectionToggle.IsChecked == true) != timeline.GameplayTimeDirection)
            {
                DirectionToggle.IsChecked = timeline.GameplayTimeDirection;
                ApplyTimeDirection();
            }
        }

        private void ApplyBgaSettings(BgaSettings bga)
        {
            if (bga == null)
            {
                return;
            }
            // The resolver memoises its probe, so this is also what clears a wrong path: assigning it
            // invalidates the cached answer and the next preview probes again.
            BgaSourceResolver.ExplicitFfmpegPath = bga.FfmpegPath;
        }

        private void ApplyFormatSettings(FormatSettings format)
        {
            if (format == null)
            {
                return;
            }

            // Only a changed preference pushes. ApplySettings runs on every preferences edit and
            // every theme pick, and the toolbar picker is a live override - pushing an unchanged
            // default here is what used to silently clear a forced layout.
            if (format.DefaultLayoutMode == _appliedDefaultLayout)
            {
                return;
            }
            _appliedDefaultLayout = format.DefaultLayoutMode;

            List<PresetChoice> choices = PresetCombo.ItemsSource as List<PresetChoice>;
            if (choices == null)
            {
                return;
            }

            // Matched on the mode rather than an index: the list gains entries as layouts are added
            // (TECHNIKA and BMS both arrived after this combo existed), so a stored index would
            // silently start meaning a different layout.
            foreach (PresetChoice choice in choices)
            {
                if (choice.KeyCount == format.DefaultLayoutMode)
                {
                    PresetCombo.SelectedItem = choice;
                    _viewModel.ModeOverride = choice.KeyCount;
                    return;
                }
            }

            PresetCombo.SelectedIndex = 0;
            _viewModel.ModeOverride = 0;
        }

        private void ApplyWorkspaceSettings(WorkspaceSettings workspace)
        {
            if (workspace == null)
            {
                return;
            }

            if ((LeftDockToggle.IsChecked == true) != workspace.ShowLeftDock)
            {
                LeftDockToggle.IsChecked = workspace.ShowLeftDock;
                OnToggleLeftDock(this, null);
            }
            if ((RightDockToggle.IsChecked == true) != workspace.ShowRightDock)
            {
                RightDockToggle.IsChecked = workspace.ShowRightDock;
                OnToggleRightDock(this, null);
            }
            if ((VolumeLaneToggle.IsChecked == true) != workspace.ShowVolumeLane)
            {
                VolumeLaneToggle.IsChecked = workspace.ShowVolumeLane;
                OnToggleVolumeLane(this, null);
            }

            PerfReadoutPanel.Visibility = workspace.ShowPerformanceReadout
                ? Visibility.Visible
                : Visibility.Collapsed;

            // The stored height is the whole panel (header + host + grip); the control sized is
            // the host. See OnBgaGripDragDelta for the same arithmetic in the other direction.
            BgaHost.Height = Math.Max(64.0, workspace.BgaPanelHeight - 31.0);
        }

        /// <summary>
        /// The BGA panel's bottom grip: trade BGA height against inspector (and playfield) room.
        /// The row above it is Auto-sized, so resizing <c>BgaHost.Height</c> is the whole layout
        /// pass - no splitter bookkeeping, no fighting the playfield's aspect-locked row.
        /// </summary>
        private void OnBgaGripDragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            double next = BgaHost.Height + e.VerticalChange;
            // 64..609 here equals the 96..640 the settings layer clamps the whole panel to
            // (host + 26 header + 5 grip), so a drag never lands somewhere a reload would move.
            BgaHost.Height = Math.Max(64.0, Math.Min(609.0, next));
        }

        /// <summary>
        /// Resolves the chart theme and pushes it into the two surfaces that draw with it.
        ///
        /// <para>
        /// Both surfaces, always together: they share one <see cref="StudioTimelineTheme"/> instance,
        /// and a volume lane left on the previous palette would sit next to a canvas on the new one,
        /// with the same notes in two colours. The playfield is deliberately not in this list - see
        /// <see cref="ThemePickerWindow"/> for why it keeps the arcade's own colours.
        /// </para>
        /// <para>
        /// Cheap enough to run on every preference change: <c>RefreshTheme</c> resolves through
        /// <see cref="StudioTimelineTheme.ForTheme"/>, which caches per theme id and returns the
        /// same frozen instance, and it is a no-op when the surface already holds that instance. So
        /// this costs a dictionary lookup and two reference comparisons unless the theme actually
        /// changed, and one invalidate when it did.
        /// </para>
        /// </summary>
        private void ApplyAppearanceSettings(AppearanceSettings appearance)
        {
            // Snapshot what the file said, the first time through. ApplyDocumentShape may later
            // point the live settings at a suggested theme, and SaveSettings restores this when
            // the user never picked one - the same rule _persistedOrientation keeps for the DAW
            // default.
            if (_persistedThemeId == null && appearance != null)
            {
                _persistedThemeId = appearance.ChartThemeId;
            }

            // A missing section is not a reason to draw nothing: the shipped palette is the answer
            // until the settings say otherwise, which is also the answer for an id this build does
            // not know (StudioChartTheme.Find is total).
            StudioChartTheme theme = StudioChartTheme.Find(
                appearance == null ? null : appearance.ChartThemeId);

            _canvas.RefreshTheme(theme);
            _volumeLane.RefreshTheme(theme);

            // The tooltip is the compact switcher's label: the toolbar is icons only, so this is the
            // one place the shell says out loud which palette is live without opening the picker.
            ThemesButton.ToolTip = "Chart themes... - " + theme.Name;
        }

        /// <summary>
        /// Opens the theme picker, or brings the open one forward.
        ///
        /// It is handed a delegate rather than a theme, so the rows' current-theme dots are re-read
        /// from the shell after every apply instead of being cached here - the picker mirrors the
        /// setting, it does not own it.
        /// </summary>
        private void OnOpenThemes(object sender, RoutedEventArgs e)
        {
            if (_themePicker != null)
            {
                _themePicker.Activate();
                return;
            }

            _themePicker = new ThemePickerWindow(
                () => StudioChartTheme.Find(
                    _settings == null || _settings.Appearance == null
                        ? null
                        : _settings.Appearance.ChartThemeId));
            _themePicker.Owner = this;
            _themePicker.ThemeApplied += OnThemeApplied;
            _themePicker.Closed += OnThemePickerClosed;
            _themePicker.Show();
        }

        private void OnThemeApplied(object sender, StudioChartTheme theme)
        {
            if (theme == null || _settings == null || _settings.Appearance == null)
            {
                return;
            }

            // A deliberate pick ends adopt-time theme suggestions: the chart the user is looking
            // at now wears what they asked for, whatever the next file would suggest.
            _themeChosen = true;

            // Written into the live settings object and applied through the one apply path, exactly
            // as a preferences edit is: no second route from a control to a surface. Persistence
            // follows at close, where SaveSettings already writes the whole object out.
            _settings.Appearance.ChartThemeId = theme.Id;
            ApplySettings(_settings);
        }

        private void OnThemePickerClosed(object sender, EventArgs e)
        {
            ThemePickerWindow window = sender as ThemePickerWindow;
            if (window != null)
            {
                window.ThemeApplied -= OnThemeApplied;
                window.Closed -= OnThemePickerClosed;
            }
            _themePicker = null;
        }

        /// <summary>
        /// Opens the preferences window, or brings the open one forward.
        ///
        /// <para>
        /// Non-modal and live: it edits the same settings object this window holds and raises
        /// <c>SettingsChanged</c> on every edit, so a latency you cannot hear change is the only thing
        /// in it that waits for a restart. Modal would have been easier and would also have meant
        /// dragging the note-height slider with the timeline hidden behind the dialog.
        /// </para>
        /// </summary>
        private void OnOpenPreferences(object sender, RoutedEventArgs e)
        {
            if (_preferences != null)
            {
                _preferences.Activate();
                return;
            }

            _preferences = new PreferencesWindow(_settings, _settingsStore);
            _preferences.Owner = this;
            _preferences.SettingsChanged += OnPreferencesChanged;
            _preferences.Closed += OnPreferencesClosed;
            _preferences.Show();
        }

        private void OnPreferencesChanged(object sender, EventArgs e)
        {
            ApplySettings(_settings);
        }

        private void OnPreferencesClosed(object sender, EventArgs e)
        {
            PreferencesWindow window = sender as PreferencesWindow;
            if (window != null)
            {
                window.SettingsChanged -= OnPreferencesChanged;
                window.Closed -= OnPreferencesClosed;
            }
            _preferences = null;
        }

        /// <summary>
        /// Writes the settings out, folding in the state the shell owns rather than the window.
        ///
        /// <para>
        /// The dock toggles and the view toggles are the shell's controls, and a user who hides the
        /// inspector by clicking the toolbar button means it just as much as one who unticks it in
        /// preferences. So the toolbar's state is harvested here at close time - otherwise the two
        /// ways of saying the same thing would disagree, and the one on the toolbar would be the one
        /// that never stuck.
        /// </para>
        /// </summary>
        private void SaveSettings()
        {
            if (_settings == null)
            {
                return;
            }

            try
            {
                TimelineSettings timeline = _settings.Timeline;
                timeline.TrackWidthScale = TrackWidthSlider.Value;
                timeline.NoteSpeed = NoteSpeedSlider.Value;
                timeline.NoteThickness = NoteHeightSlider.Value;
                timeline.AutoFitColumns = _viewModel.AutoFitColumns;
                timeline.ShowNoteLabels = LabelsToggle.IsChecked == true;
                timeline.ShowNoteArt = AssetsToggle.IsChecked == true;
                timeline.GameplayTimeDirection = DirectionToggle.IsChecked == true;
                // Only a real user choice earns persistence; the TECHNIKA chart that defaulted the
                // DAW view earlier in the session did not decide the default for everything else.
                timeline.HorizontalOrientation = _orientationChosen
                    ? OrientationToggle.IsChecked == true
                    : _persistedOrientation;

                // And the same rule for the theme a chart suggested: a suggestion the user never
                // confirmed is reverted to what the file held, otherwise every BMS chart danced
                // every other chart onto its palette after one session.
                if (!_themeChosen && _persistedThemeId != null &&
                    _settings.Appearance != null)
                {
                    _settings.Appearance.ChartThemeId = _persistedThemeId;
                }

                GridDivision division = GridCombo.SelectedItem as GridDivision;
                if (division != null)
                {
                    timeline.GridDenominator = division.Denominator;
                }
                BeatDisplay beats = BeatCombo.SelectedItem as BeatDisplay;
                if (beats != null)
                {
                    timeline.BeatDenominator = beats.Denominator;
                }

                WorkspaceSettings workspace = _settings.Workspace;
                workspace.ShowLeftDock = LeftDockToggle.IsChecked == true;
                workspace.ShowRightDock = RightDockToggle.IsChecked == true;
                workspace.ShowVolumeLane = VolumeLaneToggle.IsChecked == true;

                // The panel is a 26 px header and a 5 px grip on top of the host; the setting
                // stores the whole rather than the part, so a default and a dragged height are
                // the same unit.
                if (BgaPanel.Visibility == Visibility.Visible)
                {
                    workspace.BgaPanelHeight = BgaHost.Height + 31.0;
                }

                _settingsStore.Save(_settings);
            }
            catch (Exception ex)
            {
                // Closing down is the worst moment for a dialog, and an unsaved preference is not
                // worth one. The store logs its own failures; this catches the harvest above.
                DiagnosticLog.Exception("settings.harvest", ex);
            }
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            App app = Application.Current as App;
            string startup = app == null ? null : app.StartupChartPath;
            if (!string.IsNullOrEmpty(startup))
            {
                await OpenPathAsync(startup);
            }

            // After the chart, so the clock map is already built when the first frame is fetched.
            string video = app == null ? null : app.StartupBgaPath;
            if (!string.IsNullOrEmpty(video))
            {
                _bgaPath = video;
                BgaToggle.IsChecked = true;
                OnToggleBga(this, null);
                await AttachBgaAsync();
            }
        }

        private void OnClosed(object sender, EventArgs e)
        {
            // First, and before the harvest below: the preferences window writes on its own close,
            // and letting it do that after the shell has already saved would put a file on disk
            // without the toolbar state in it.
            if (_preferences != null)
            {
                _preferences.Close();
            }
            SaveSettings();

            StopPump();

            // Before the player goes: a worker mid-decode would otherwise finish into a disposed
            // cache and then post a status update to a window that is gone.
            CancelKeysoundLoad();

            if (_player != null)
            {
                _player.Reset();
            }
            if (_audio != null)
            {
                _audio.Dispose();
                _audio = null;
            }

            // BgaPreview only closes its source on Clear; the platform reference and the decoder
            // itself belong to whoever owns them, which is this window.
            IBgaSource source = _bga.Source;
            _bga.Source = null;
            if (source != null)
            {
                source.Dispose();
            }

            _viewModel.Dispose();
        }

        // ===================================================================================
        // Document
        // ===================================================================================

        /// <summary>
        /// The open-time chooser for a multi-difficulty track.tech. Returns the selected
        /// pattern slot, or null when the user cancels. Double-clicking a row opens it.
        /// </summary>
        private int? ChooseTechPattern(IList<TechPatternInfo> patterns, string fileName)
        {
            var dialog = new Window
            {
                Title = "Open difficulty",
                Width = 430,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Margin = new Thickness(0)
            };

            var root = new StackPanel { Margin = new Thickness(18) };
            root.Children.Add(new TextBlock
            {
                Text = fileName + " contains " + patterns.Count + " difficulties.",
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });
            root.Children.Add(new TextBlock
            {
                Text = "Choose the pattern to edit. Every other difficulty stays in the " +
                       "container and is written back unchanged when you save.",
                Foreground = SystemColors.GrayTextBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });

            var list = new ListBox
            {
                Height = Math.Min(220, 26 + patterns.Count * 24),
                SelectedIndex = 0
            };
            foreach (TechPatternInfo info in patterns)
            {
                list.Items.Add(string.Format(CultureInfo.CurrentCulture,
                    "{0,-14}  Level {1,-3}  {2} lanes",
                    string.IsNullOrEmpty(info.Name) ? "(unnamed)" : info.Name,
                    info.Level,
                    info.PlayableLanes));
            }
            list.MouseDoubleClick += (sender, args) =>
            {
                if (list.SelectedIndex >= 0)
                {
                    dialog.DialogResult = true;
                }
            };
            root.Children.Add(list);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 90,
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true
            };
            var openButton = new Button
            {
                Content = "Open",
                Width = 90,
                IsDefault = true
            };
            buttons.Children.Add(cancelButton);
            buttons.Children.Add(openButton);
            root.Children.Add(buttons);

            int? chosen = null;
            openButton.Click += (sender, args) =>
            {
                if (list.SelectedIndex >= 0)
                {
                    chosen = list.SelectedIndex;
                    dialog.DialogResult = true;
                }
            };
            dialog.Content = root;

            return dialog.ShowDialog() == true ? chosen : null;
        }

        private async System.Threading.Tasks.Task OpenPathAsync(string path)
        {
            string error;
            ChartProbe probe = _files.Probe(path, out error);
            if (probe == null)
            {
                Warn(error, "Load file error");
                return;
            }

            bool fromDecrypted = false;
            if (probe.IsEncryptedPt)
            {
                // Decryption is offline and opt-in, every time. Nothing is uploaded and the file on
                // disk is never modified.
                MessageBoxResult choice = System.Windows.MessageBox.Show(
                    this,
                    "This is an encrypted Technika/Trilogy chart.\n\n" +
                    "Decrypt it offline and open it? Decryption runs entirely on this machine " +
                    "(no upload, no network request). The original file is not modified.\n\n" +
                    "File: " + probe.FileName,
                    "Decrypt encrypted chart?", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (choice != MessageBoxResult.Yes)
                {
                    DJMaxEditor.Diagnostics.DiagnosticLog.Write(
                        "open.decrypt", probe.FileName + ": declined by user");
                    return;
                }

                ChartProbe decrypted = _files.Decrypt(probe, out error);
                if (decrypted == null)
                {
                    Warn(error, "Decryption failed");
                    return;
                }
                probe = decrypted;
                fromDecrypted = true;
            }

            // A track.tech packs one pattern per difficulty; ask which one to open when the
            // container holds several. The other slots are retained on save regardless.
            int patternIndex = 0;
            IList<TechPatternInfo> patterns = _files.EnumeratePatterns(probe);
            if (patterns != null && patterns.Count > 1)
            {
                int? chosen = ChooseTechPattern(patterns, probe.FileName);
                if (!chosen.HasValue)
                {
                    StatusHint.Text = string.Empty;
                    return;
                }
                patternIndex = chosen.Value;
            }

            StatusHint.Text = "Loading " + probe.FileName + "...";
            Cursor = Cursors.AppStarting;
            ChartOpenResult result;
            try
            {
                result = await _files.OpenAsync(probe, fromDecrypted, patternIndex);
            }
            finally
            {
                Cursor = null;
            }

            if (!result.Success)
            {
                StatusHint.Text = "Load failed";
                Warn(result.Error, "Load file error");
                return;
            }

            AdoptDocument(result.Model, result.Path);
        }

        private void AdoptDocument(PlayerData model, string path)
        {
            Stop();

            if (_document != null && _document.Selection != null)
            {
                _document.Selection.SelectionChanged -= OnSelectionChanged;
            }
            if (_document != null && _document.UndoManager != null)
            {
                _document.UndoManager.OnUndoRedo -= OnUndoRedo;
            }

            _document = new EditorDocumentContext(model, path, new UndoManager());
            _document.Selection.SelectionChanged += OnSelectionChanged;
            _document.UndoManager.OnUndoRedo += OnUndoRedo;

            // Every stage of an adopt is timed. A chart that takes too long to open does not throw -
            // it just stops pumping messages, and Windows reports that as an app hang with no stack
            // and no log. Timing each stage is what turns "it crashed" into a named phase.
            Stopwatch adopt = Stopwatch.StartNew();
            _noteTotal = CountNotes(model);
            long tCount = adopt.ElapsedMilliseconds;

            _viewModel.Bind(_document);
            long tBind = adopt.ElapsedMilliseconds;

            _player.LoadPlayerData(model);
            long tPlayer = adopt.ElapsedMilliseconds;

            LoadKeysounds(model, path);
            long tKeysounds = adopt.ElapsedMilliseconds;

            // The BGA's own clock is rebuilt from the new chart's tempo map. The video itself is
            // deliberately left attached: swapping charts in the same folder is the common case, and
            // re-picking the same file every time would be tedious.
            _bgaClock.Load(model);
            SyncBga();
            DiscoverBga(path);
            long tBga = adopt.ElapsedMilliseconds;

            BindPlayfield(model);
            ApplyDocumentShape(model);
            long tPlayfield = adopt.ElapsedMilliseconds;

            SampleList.ItemsSource = model.Instruments;
            SampleCount.Text = model.Instruments == null
                ? "0"
                : model.Instruments.Count.ToString(CultureInfo.InvariantCulture);

            DocumentLabel.Text = Path.GetFileName(path) + (model.IsReadOnly ? "  (read-only)" : "");
            InspectorChart.Text = DescribeChart(model);
            StatusHint.Text = model.IsReadOnly
                ? "Read-only: this format cannot be written back"
                : "Ready";
            long tPanels = adopt.ElapsedMilliseconds;

            _canvas.InvalidateAll();
            _volumeLane.InvalidateVisual();
            RefreshStatus();

            DiagnosticLog.Write(
                "open.adopt",
                Path.GetFileName(path) +
                ": count=" + tCount +
                "ms bind=" + (tBind - tCount) +
                "ms player=" + (tPlayer - tBind) +
                "ms ksdispatch=" + (tKeysounds - tPlayer) +
                "ms bga=" + (tBga - tKeysounds) +
                "ms playfield=" + (tPlayfield - tBga) +
                "ms panels=" + (tPanels - tPlayfield) +
                "ms invalidate=" + (adopt.ElapsedMilliseconds - tPanels) +
                "ms total=" + adopt.ElapsedMilliseconds + "ms");
        }

        /// <summary>
        /// Starts loading every keysound the chart names, from the chart's own folder, and returns
        /// immediately.
        /// <para>
        /// Decoding used to run inline here, on the dispatcher thread. That is what made opening a
        /// Technika chart look like a crash: 276 keysounds is ten seconds of Vorbis decoding even
        /// when nothing goes wrong, and the window cannot repaint or accept input for the whole of
        /// it. So the work goes to the thread pool and the chart is editable while samples arrive.
        /// </para>
        /// <para>
        /// Index 0 gets mode 1 exactly as the legacy editor does - that is the long background track
        /// (the "MR"). Instruments with InsNum 0 are placeholders and are skipped; a missing file is
        /// counted and ignored, because half a keysound set still lets you edit the chart.
        /// </para>
        /// </summary>
        private void LoadKeysounds(PlayerData model, string path)
        {
            // Whatever the previous chart was still loading is no longer wanted, and its samples
            // would land in the cache under this chart's instrument indices.
            CancelKeysoundLoad();

            if (model.Instruments == null || _audio == null)
            {
                return;
            }

            // A preference, not a performance knob: on a chart whose keysounds are missing or wrong
            // the decode is thousands of failed file opens, and someone editing note positions on a
            // borrowed chart has no use for the audio at all. Logged rather than put in the status
            // strip because the adopt sets that to "Ready" a few lines after this returns.
            if (_settings != null && !_settings.Audio.LoadKeysoundsOnOpen)
            {
                DiagnosticLog.Write("keysounds.skip", "LoadKeysoundsOnOpen is off");
                return;
            }

            string directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            // Snapshotted on the UI thread. The worker must not walk a list the editor can mutate,
            // and resolving the mode here keeps the ordering rule ("index 0 is the MR") in one place.
            List<KeysoundRequest> requests = new List<KeysoundRequest>(model.Instruments.Count);
            for (int i = 0; i < model.Instruments.Count; i++)
            {
                InstrumentData instrument = model.Instruments[i];
                if (instrument == null || instrument.InsNum == 0 || string.IsNullOrEmpty(instrument.Name))
                {
                    continue;
                }

                requests.Add(new KeysoundRequest(
                    instrument.InsNum, Path.Combine(directory, instrument.Name), i == 0 ? 1 : 0));
            }

            if (requests.Count == 0)
            {
                return;
            }

            _keysoundLoad = new CancellationTokenSource();
            StatusHint.Text = "Loading " + requests.Count.ToString(CultureInfo.InvariantCulture) + " keysounds...";

            // Deliberately not awaited: adopting a document must not block on audio. Faults are
            // handled inside, so nothing escapes to the unobserved-task handler.
            _ = LoadKeysoundsAsync(requests, _keysoundLoad.Token);
        }

        private async Task LoadKeysoundsAsync(List<KeysoundRequest> requests, CancellationToken token)
        {
            Stopwatch clock = Stopwatch.StartNew();
            int loaded = 0;
            int missing = 0;
            int done = 0;

            // Bounded rather than wide open: the decodes are independent and CPU-bound, but each
            // holds a multi-megabyte float buffer while it runs, so a full-width fan-out on a
            // 24-thread machine would spike hundreds of megabytes for no extra speed.
            int workers = Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1));
            int total = requests.Count;

            try
            {
                await Task.Run(
                    () => Parallel.ForEach(
                        requests,
                        new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = token },
                        request =>
                        {
                            if (_audio.LoadSound(request.Index, request.Path, request.Mode))
                            {
                                Interlocked.Increment(ref loaded);
                            }
                            else
                            {
                                Interlocked.Increment(ref missing);
                            }

                            // Every 32nd sample, not every one: the point is a moving readout, and a
                            // dispatcher post per keysound would be its own load on the UI thread.
                            int seen = Interlocked.Increment(ref done);
                            if (seen % 32 == 0)
                            {
                                ReportKeysoundProgress(seen, total, token);
                            }
                        }),
                    token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                DiagnosticLog.Write("open.keysounds", "cancelled after " + done + "/" + total);
                return;
            }
            catch (AggregateException ex)
            {
                // Parallel.ForEach wraps whatever a body threw. A keysound that cannot be decoded is
                // already handled inside LoadSound, so anything arriving here is worth recording.
                DiagnosticLog.Exception("open.keysounds", ex);
            }

            DiagnosticLog.Write(
                "open.keysounds",
                "requested=" + total +
                ", loaded=" + loaded +
                ", missing=" + missing +
                ", workers=" + workers +
                ", elapsed=" + clock.ElapsedMilliseconds + "ms");

            if (token.IsCancellationRequested)
            {
                return;
            }

            StatusHint.Text = missing > 0
                ? loaded + " keysounds loaded, " + missing + " missing"
                : loaded + " keysounds loaded";

            // Catch the audio up if the chart is already playing.
            //
            // Decoding a full TECHNIKA set takes seconds and nothing blocks on it, so a chart played
            // the instant it opens fires its first notes at slots that are still empty - the log shows
            // the transport starting eight seconds before "open.keysounds" finishes, and every one of
            // those notes is a play failure. The backing track is the one that matters and the one
            // that is worst affected, since it is struck once at the top of the chart and never again.
            // Now that the cache is warm, put back whatever should be sounding under the playhead: the
            // same call a seek makes, and it skips the tracks that are already sounding.
            if (loaded > 0 && _player != null && _player.IsPlaying)
            {
                RestoreSoundingVoices(_player.GetCurrentTick());
            }
        }

        private void ReportKeysoundProgress(int seen, int total, CancellationToken token)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                StatusHint.Text = "Loading keysounds " +
                    seen.ToString(CultureInfo.InvariantCulture) + "/" +
                    total.ToString(CultureInfo.InvariantCulture);
            }), DispatcherPriority.Background);
        }

        private void CancelKeysoundLoad()
        {
            CancellationTokenSource previous = _keysoundLoad;
            _keysoundLoad = null;
            if (previous == null)
            {
                return;
            }

            try
            {
                previous.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            previous.Dispose();
        }

        /// <summary>One keysound to decode, resolved off the model so a worker never touches it.</summary>
        private readonly struct KeysoundRequest
        {
            internal KeysoundRequest(uint index, string path, int mode)
            {
                Index = index;
                Path = path;
                Mode = mode;
            }

            internal uint Index { get; }

            internal string Path { get; }

            internal int Mode { get; }
        }

        private static string DescribeChart(PlayerData model)
        {
            if (model == null)
            {
                return string.Empty;
            }

            int notes = CountNotes(model);

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} tracks - {1} notes\n{2} BPM - {3} ticks/measure\n{4}",
                model.Tracks.Count, notes, model.Tempo.ToString("0.##", CultureInfo.InvariantCulture),
                model.TickPerMinute,
                model.SourceFormat.HasValue ? model.SourceFormat.Value.ToString() : "unknown format");
        }

        /// <summary>
        /// Notes in the whole chart, not the ones currently on screen.
        ///
        /// The status bar used to read this off the last drawn frame, which is why it said zero: the
        /// count is wanted the instant a chart is adopted, and at that point nothing has rendered
        /// yet. It is counted once per open and cached rather than recounted per repaint.
        /// </summary>
        private static int CountNotes(PlayerData model)
        {
            int notes = 0;
            foreach (TrackData track in model.Tracks)
            {
                foreach (EventData ev in track.Events)
                {
                    if (ev.EventType == EventType.Note)
                    {
                        notes++;
                    }
                }
            }
            return notes;
        }

        // ===================================================================================
        // Transport
        // ===================================================================================

        /// <summary>
        /// Starts playback at <paramref name="nativeTick"/>, from silence.
        /// <para>
        /// Also the resume path - see <see cref="OnPlayPause"/> - which is why
        /// <paramref name="edge"/> exists: the transport log has to say which of the two edges it
        /// was, and by this point the sequencer's own flags no longer remember.
        /// </para>
        /// </summary>
        private void Play(int nativeTick, string edge = "play")
        {
            if (_document == null || !_player.IsReady)
            {
                return;
            }

            // Clear the group pause before anything starts. A fresh play can follow a pause, and a
            // paused group would swallow the whole take silently.
            if (_audio != null)
            {
                _audio.StopAllSounds();
            }

            _player.Reset();
            _player.Play(nativeTick);
            RestoreSoundingVoices(nativeTick);
            _viewModel.IsPlaybackActive = true;
            _lastPumpVirtualTick = -1;
            StartPump();
            RefreshPlayGlyph();
            LogTransport(edge);
        }

        /// <summary>
        /// Re-strikes the keysound each track would still be sounding at
        /// <paramref name="nativeTick"/>, part-way in.
        /// <para>
        /// The sequencer no longer replays the notes behind a seek - see
        /// <c>Player.SeekEventsTo</c> - which is right for the notes and wrong for the audio: land in
        /// the middle of a four-beat pad and the arcade is holding that pad, and land anywhere at all
        /// and the arcade is still playing the MR underneath. Silence there is a hole in the mix.
        /// </para>
        /// <para>
        /// What counts as still sounding is <see cref="SoundingVoiceRule"/>'s answer - the keysound's
        /// own length, not the note's charted duration. That distinction is the fix: the MR is a
        /// single note with a six-tick duration carrying minutes of audio, so the old held-note test
        /// never restored it and the backing track dropped out on every seek and, since a pause is now
        /// a stop, on every resume. A note whose sample has already run out is still left alone, so a
        /// seek does not resurrect what the arcade would have finished with.
        /// </para>
        /// <para>
        /// A track can owe more than one voice. <see cref="NAudioKeysoundPlayer.AllowOverlappingRetrigger"/>
        /// is on, so during playback a retrigger leaves the previous keysound ringing out instead of
        /// hard-stopping it, and a resume that put back only the newest note would be audibly thinner
        /// than the playback it is resuming - which is the same complaint as a truncated tail, reached
        /// from the other side. The rule is asked for the whole set, oldest first, and they are struck
        /// in that order so the channel ends up owned by the newest exactly as it was before the stop.
        /// </para>
        /// </summary>
        private void RestoreSoundingVoices(int nativeTick)
        {
            if (nativeTick <= 0 || _audio == null || _document == null)
            {
                return;
            }

            PlayerData model = _document.Model;

            // Built per seek rather than cached: it closes over the chart's merged event stream, and
            // an edit can add a tempo event between two seeks.
            SoundingVoiceRule rule = new SoundingVoiceRule(
                model.Tracks.Events, model.Tempo, _audio.SampleLengthMilliseconds);

            int maxVoices = _audio.AllowOverlappingRetrigger ? MaxRestoredVoicesPerTrack : 1;
            var restorable = new List<SoundingVoiceRule.RestorableVoice>();

            foreach (TrackData track in model.Tracks)
            {
                // Never restart a channel that is already sounding. After a seek this never bites -
                // Play empties the graph through StopAllSounds before anything gets here - but the
                // keysound loader calls this too, and there the point is to fill the tracks that came
                // up empty without stealing the channel from one that is already playing correctly.
                if (_audio.IsChannelBusy(track.Idx))
                {
                    continue;
                }

                rule.RestorableVoices(track.OrderedEvents, nativeTick, maxVoices, restorable);

                for (int i = 0; i < restorable.Count; i++)
                {
                    EventData sounding = restorable[i].Note;
                    ushort soundIndex = sounding.Instrument.InsNum;
                    float gain = MasterGain * track.Volume * NoteVelocityGain(sounding.Vel);
                    if (!_audio.PlayNote(track.Idx, soundIndex, gain, sounding.Pan,
                            restorable[i].OffsetMilliseconds, 0.0))
                    {
                        // Nearly always "that keysound has not finished decoding yet" - loading runs on
                        // a background thread and a full TECHNIKA set takes seconds. Said out loud
                        // because a track that is silent after a seek is otherwise indistinguishable
                        // from a scheduling fault; the running total is in the transport log as
                        // playFailures.
                        Logs.Write("Failed to restore sounding voice on track : {0}, soundIndex : {1}",
                            track.Idx, soundIndex);
                    }
                }
            }
        }

        private void Stop()
        {
            if (_player == null)
            {
                return;
            }
            StopPump();
            _player.Reset();
            if (_audio != null)
            {
                _audio.StopAllSounds();
            }
            _viewModel.IsPlaybackActive = false;
            RefreshPlayGlyph();
            RefreshStatus();
            if (_activePlayfield != null)
            {
                _activePlayfield.Sync(_viewModel.PlayheadVirtualTick);
            }
            LogTransport("stop");
        }

        /// <summary>
        /// Records the state of both clocks at every transport edge.
        ///
        /// The sequencer's paused flag and the mixer group's paused flag have to agree, and when
        /// they do not the symptom is audible rather than visible - held voices carrying on under a
        /// stopped playhead. Writing both plus the live voice count on each edge makes that
        /// answerable from the log instead of by ear.
        ///
        /// <para>
        /// <c>playFailures</c> is here for the other half of that question. Keysounds decode on a
        /// background thread, so a chart played the instant it opens fires notes at slots that are
        /// still empty; every one of those is a silent track that looks like a scheduling bug. The
        /// counter is cumulative, so what matters in a log is whether it moved between two edges.
        /// </para>
        ///
        /// <para>
        /// <c>deviceStops</c> is the pause flush, printed as stops/starts. The two must never differ
        /// by more than one: a stop with no start after it is an editor that has gone silent for the
        /// rest of the session, which is a worse failure than the tail the flush removes and is not
        /// something to find out about by ear.
        /// </para>
        /// </summary>
        private void LogTransport(string edge)
        {
            if (_player == null)
            {
                return;
            }

            string audio = _audio == null
                ? "audio=none"
                : "groupPaused=" + (_audio.IsAllPaused ? "1" : "0") +
                  ", mixerInputs=" + _audio.SnapshotMixerInputCount() +
                  ", ringingOut=" + _audio.RingingOutVoiceCount +
                  ", buffers=" + _audio.BuffersRendered +
                  ", pausedBuffers=" + _audio.PausedBuffers +
                  ", fadeFrames=" + _audio.PauseFadeFrames +
                  ", fadeOuts=" + _audio.FadeOutsAnnounced +
                  ", teardowns=" + _audio.FadeTeardowns +
                  ", droppedOnFade=" + _audio.VoicesDroppedOnFade +
                  ", deviceStops=" + _audio.DeviceStops + "/" + _audio.DeviceStarts +
                  ", playFailures=" + _audio.PlayFailures;

            DiagnosticLog.Write(
                "transport." + edge,
                "tick=" + _player.GetCurrentTick() +
                ", seqPaused=" + (_player.IsPaused ? "1" : "0") +
                ", seqStopped=" + (_player.IsStopped ? "1" : "0") +
                ", pump=" + (_pumpAttached ? "on" : "off") +
                ", " + audio);
        }

        /// <summary>
        /// The playback pump.
        ///
        /// CompositionTarget.Rendering fires once per composition frame, which is the only clock in
        /// WPF that is actually in phase with what the screen shows - a DispatcherTimer ticks
        /// against a different beat and produces visible judder no matter how short the interval.
        /// It is attached only while playing: with a handler installed WPF composes every frame
        /// whether or not anything changed, and that idle cost is exactly the "everything lags once
        /// I open the preview" problem from the last playtest.
        /// </summary>
        private void StartPump()
        {
            if (_pumpAttached)
            {
                return;
            }
            _pumpAttached = true;
            CompositionTarget.Rendering += OnRendering;
        }

        private void StopPump()
        {
            if (!_pumpAttached)
            {
                return;
            }
            _pumpAttached = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        private void OnRendering(object sender, EventArgs e)
        {
            if (_player == null || !_player.IsReady)
            {
                StopPump();
                return;
            }

            _player.Update();

            // Round the playhead into virtual ticks - six per sequencer tick. Redrawing only when
            // GetCurrentTick moved was the real frame cap all along: the sequencer advances at
            // tempo * 48 ticks a second, so 48 redraws/s at 60 BPM and fewer below 75 BPM, no
            // matter that this handler itself fires every composition frame. The sub-tick
            // remainder from GetCurrentTickExact gives the same handler a fresh position six
            // times as often - 288 virtual ticks/s at 60 BPM, above any display.
            double exactTick = _player.GetCurrentTickExact();
            int virtualTick = (int)(exactTick * EventData.VirtualTickSize);
            if (virtualTick == _lastPumpVirtualTick)
            {
                return;
            }
            _lastPumpVirtualTick = virtualTick;

            // One assignment moves the playhead, scrolls the follow window and repaints the note
            // band; the overlay is nudged separately because the setter short-circuits when the
            // tick has not changed and the playhead line still has to move within a frame.
            _viewModel.PlayheadVirtualTick = virtualTick;
            _canvas.InvalidateOverlay();
            SyncBga();
            if (_activePlayfield != null)
            {
                _activePlayfield.Sync(_viewModel.PlayheadVirtualTick);
            }
            RenderingEventArgs rendering = e as RenderingEventArgs;
            if (rendering != null)
            {
                double now = rendering.RenderingTime.TotalMilliseconds;
                if (_lastFrameMilliseconds > 0)
                {
                    double delta = now - _lastFrameMilliseconds;
                    if (delta > 0)
                    {
                        PerfReadout.Text = string.Format(
                            CultureInfo.InvariantCulture, "{0:0.0} ms  {1:0} fps", delta, 1000.0 / delta);
                    }
                }
                _lastFrameMilliseconds = now;
            }

            UpdateTimeReadout();

            if (_player.IsStopped)
            {
                Stop();
            }
        }

        private void OnPlayerEvent(EventData eventData, uint trackIndex, EventType eventType, byte pan)
        {
            if (_document == null || _audio == null)
            {
                return;
            }

            TrackData track = _document.Model.Tracks.GetTrackAtIndex(trackIndex);
            if (track == null)
            {
                return;
            }

            switch (eventType)
            {
                case EventType.Note:
                    ushort soundIndex = eventData.Instrument != null ? eventData.Instrument.InsNum : (ushort)0;
                    if (soundIndex == 0)
                    {
                        break;
                    }

                    // Per-note volume is folded into the track gain rather than pushed through
                    // SetVolume: SetVolume takes a 0..1 float and PlaySound's own volume argument
                    // would overwrite it three lines later, which is why Vel round-tripped through
                    // every file format for years and was never audible. The master preference goes
                    // in the same product for the same reason.
                    float gain = MasterGain * track.Volume * NoteVelocityGain(eventData.Vel);

                    // No hold length, ever: every keysound rings out to the end of its own file.
                    //
                    // The shell used to cut a held note's sound when the note's body ended, which on
                    // TECHNIKA is wrong twice over. The stored duration *is* the length of the
                    // keysound, so the cut landed a hair inside the sample and shaved the tail off
                    // every pad - the owner heard it as the end of the sound going missing - and the
                    // period it was converted with was the one in force at the note's start, so a
                    // tempo change anywhere made the error grow. There is nothing for a cut to fix
                    // either: the arcade's keysound ends when its file ends. This is the rule
                    // KeysoundRenderProbe renders as its "after-ringout" pass.
                    if (!_audio.PlayNote(trackIndex, soundIndex, gain, pan, 0.0, 0.0))
                    {
                        Logs.Write("Failed to play sound on track : {0}, soundIndex : {1}",
                            trackIndex, soundIndex);
                    }
                    break;

                case EventType.Volume:
                    track.Volume = eventData.Volume / (float)sbyte.MaxValue;
                    _audio.SetVolume(trackIndex, track.Volume);
                    break;
            }
        }

        /// <summary>
        /// Turns a note's 0-127 velocity into a 0..1 multiplier. Clamped on the velocity side only:
        /// track volume can legitimately exceed unity in a .pt, and clamping the product would
        /// quietly make those charts play back softer than they used to.
        /// </summary>
        private static float NoteVelocityGain(byte velocity)
        {
            if (velocity >= ChartEditController.MaxNoteVolume)
            {
                return 1f;
            }
            return velocity / (float)ChartEditController.MaxNoteVolume;
        }

        /// <summary>
        /// The master volume preference, as a multiplier on every note the sequencer fires.
        ///
        /// <para>
        /// Read here rather than pushed into the mixer because it has to apply to notes that have not
        /// been played yet, and because the alternative - a device-level gain - would also scale the
        /// audition channel, which has a preference of its own precisely so it does not have to be
        /// the same loudness as playback.
        /// </para>
        /// </summary>
        private float MasterGain
        {
            get { return _settings == null ? 1f : (float)_settings.Audio.MasterVolume; }
        }

        private void OnPlayerStatusChanged(object sender)
        {
            if (!CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RefreshPlayGlyph));
                return;
            }
            RefreshPlayGlyph();
        }

        private void RefreshPlayGlyph()
        {
            bool playing = _player != null && _player.IsReady && _player.IsPlaying;
            PlayGlyph.Data = (Geometry)FindResource(playing ? "Icon.Pause" : "Icon.Play");
            PlayButton.ToolTip = playing ? "Pause (Space)" : "Play from the start (Space)";
        }

        // ===================================================================================
        // Canvas callbacks
        // ===================================================================================

        private void OnCanvasSeekRequested(object sender, VerticalSeekEventArgs e)
        {
            // A seek while stopped only moves the caret; the sequencer is restarted at the new tick
            // only if it was already running, so clicking the ruler cannot start playback by
            // surprise.
            if (_player != null && _player.IsReady && _player.IsPlaying)
            {
                Play(e.VirtualTick / EventData.VirtualTickSize);
            }
            UpdateTimeReadout();
            SyncBga();
            if (_activePlayfield != null)
            {
                _activePlayfield.Sync(_viewModel.PlayheadVirtualTick);
            }
        }

        private void OnCanvasInteractionCompleted(object sender, EventArgs e)
        {
            _volumeLane.InvalidateVisual();
            RefreshStatus();
            RefreshInspector();
        }

        /// <summary>
        /// Ctrl+wheel on the canvas: step the snap divisor, the osu! editor's one wheel modifier
        /// everyone reaching for it actually means. Routed through the combo's own selection path
        /// so the button, the status readout and the settings harvest all see the same value.
        /// Free sits at the end of the list and is skipped over in both directions: stepping the
        /// divisor and landing on "no divisor at all" is a trap, not a value.
        /// </summary>
        private void OnCanvasGridCycle(object sender, int direction)
        {
            int index = GridCombo.SelectedIndex;
            int count = GridCombo.Items.Count;
            // The last entry is Free: excluded from the cycle. Clamped rather than wrapped, so
            // holding the wheel against an end parks there instead of teleporting to the other.
            int next = index + direction;
            if (next < 0) next = 0;
            if (next > count - 2) next = count - 2;
            if (next != index)
            {
                GridCombo.SelectedIndex = next;
            }
        }

        /// <summary>
        /// "Play keysounds on click": a click landing on a note sounds that note's keysound once,
        /// through the same audition channel and gain a sample-list double-click uses - the top
        /// channel, which no shipped format addresses - so it never cuts a note that playback
        /// has sounding.
        /// </summary>
        private void OnCanvasNoteClicked(object sender, EventData note)
        {
            if (note == null || _audio == null || _settings == null ||
                !_settings.Audio.PlayKeysoundOnClick)
            {
                return;
            }

            InstrumentData instrument = note.Instrument;
            if (instrument == null || instrument.InsNum == 0)
            {
                return;
            }

            float gain = (float)_settings.Audio.AuditionVolume;
            _audio.PlaySound(AuditionChannel, instrument.InsNum, gain, 64);
        }

        /// <summary>
        /// Slices the resting still (strip frame 9 - the same frame the timeline draws with
        /// <c>StillPhase</c>) out of each arcade strip and puts it on its palette button. Done
        /// here rather than in XAML so a missing resource can fail one button quietly instead of
        /// throwing a BAML parse exception that stops the whole window loading.
        /// </summary>
        private void LoadPaletteIcons()
        {
            PaletteIconTap.Source = PaletteNoteFrame("Note_Basic", 90, 9);
            PaletteIconDrag.Source = PaletteNoteFrame("longnote", 116, 9);
            PaletteIconChain.Source = PaletteNoteFrame("notepressstart", 116, 9);
            PaletteIconChainNode.Source = PaletteNoteFrame("notepressnote", 76, 9);
            PaletteIconHold.Source = PaletteNoteFrame("longnotehold", 90, 9);
            PaletteIconRepeatHold.Source = PaletteNoteFrame("noterepeat", 90, 9);
            PaletteIconRepeat.Source = PaletteNoteFrame("noterepeat", 90, 9);
            PaletteIconRepeatRoll.Source = PaletteNoteFrame("repeattail", 90, 9);
        }

        private static ImageSource PaletteNoteFrame(string sheet, int frameSize, int frameIndex)
        {
            try
            {
                BitmapImage strip = new BitmapImage(new Uri(
                    "pack://application:,,,/DJMaxEditor.Studio;component/Timeline/Notes/" +
                    sheet + ".png",
                    UriKind.Absolute));
                CroppedBitmap frame = new CroppedBitmap(
                    strip, new Int32Rect(frameIndex * frameSize, 0, frameSize, frameSize));
                frame.Freeze();
                return frame;
            }
            catch (Exception ex)
            {
                // One bad or unshipped strip hides just that icon; the text label remains.
                DiagnosticLog.Exception("shell.palette", ex);
                return null;
            }
        }

        /// <summary>
        /// One of the TECHNIKA note-kind buttons. The pick lives on the canvas (attribute plus
        /// whether the note starts long), the buttons are exclusive, and picking one arms the
        /// Addition tool: a palette that changed art but not the tool would place whatever the
        /// previous pick was while looking like it changed its mind.
        /// </summary>
        private void OnPalettePick(object sender, RoutedEventArgs e)
        {
            System.Windows.Controls.Primitives.ToggleButton picked =
                sender as System.Windows.Controls.Primitives.ToggleButton;
            if (picked == null)
            {
                return;
            }

            // Clicking the latched button would uncheck everything; that is not a state the
            // palette has ("no kind"), so re-check it and take no other action.
            foreach (object child in NotePaletteGrid.Children)
            {
                System.Windows.Controls.Primitives.ToggleButton button =
                    child as System.Windows.Controls.Primitives.ToggleButton;
                if (button != null)
                {
                    button.IsChecked = ReferenceEquals(button, picked);
                }
            }

            string tag = picked.Tag as string ?? "0";
            bool isLong = tag.EndsWith("|long", StringComparison.Ordinal);
            if (isLong)
            {
                tag = tag.Substring(0, tag.Length - "|long".Length);
            }

            byte attribute;
            if (!byte.TryParse(tag, out attribute))
            {
                attribute = 0;
            }
            _canvas.NewNoteAttribute = attribute;
            _canvas.NewNoteIsLong = isLong;
            SetTool(ToolMode.Addition);
        }

        private void OnCanvasContextRequested(object sender, VerticalHitResult e)
        {
            if (e == null || !e.HasColumn)
            {
                InspectorLane.Text = string.Empty;
                return;
            }

            string text = e.Column.Name;
            if (e.HasItem && e.Item.Item != null && e.Item.Item.SourceEvent != null)
            {
                EventData source = e.Item.Item.SourceEvent;
                // Vel is the note's own gain - the .pt note byte, what the inspector edits and
                // what PlaySound scales by. EventData.Volume belongs to EventType.Volume track
                // events and on a note is only ever the constructor default.
                text += "\ntick " + source.VirtualTick + "  vol " + source.Vel;
                if (source.Instrument != null)
                {
                    text += "\n" + source.Instrument.Name;
                }
            }
            InspectorLane.Text = text;
        }

        private void OnSelectionChanged(object sender, EventArgs e)
        {
            RefreshStatus();
            RefreshInspector();
            _volumeLane.InvalidateVisual();
        }

        private void OnUndoRedo(object sender, UndoManager.Action action)
        {
            _viewModel.Rebuild();
            _canvas.InvalidateBand();
            _volumeLane.InvalidateVisual();
            RefreshStatus();
            RefreshInspector();
        }

        // ===================================================================================
        // Status / inspector
        // ===================================================================================

        private void RefreshStatus()
        {
            int selected = _viewModel.SelectedCount;
            StatusSelection.Text = "SEL " + selected.ToString(CultureInfo.InvariantCulture);
            StatusGrid.Text = "GRID " + (_canvas.Grid != null ? _canvas.Grid.Label : "-");
            StatusTool.Text = _canvas.Tool.ToString().ToUpperInvariant();
            StatusZoom.Text = "ZOOM " +
                Math.Round(_viewModel.ZoomFactor * 100).ToString(CultureInfo.InvariantCulture) + "%";

            VerticalTimelineFrame frame = _canvas.Frame;
            StatusTotal.Text = "NOTES " + _noteTotal.ToString(CultureInfo.InvariantCulture) +
                (frame == null ? string.Empty
                    : " (" + frame.Items.Count.ToString(CultureInfo.InvariantCulture) + " shown)");

            UpdateTimeReadout();
        }

        private void UpdateTimeReadout()
        {
            double current = _player != null && _player.IsReady ? _player.GetCurrentMsTime() : 0;
            double total = _document != null ? _document.Model.TrackDuration * 1000.0 : 0;
            StatusTime.Text = FormatTime(current) + " / " + FormatTime(total);
        }

        private static string FormatTime(double milliseconds)
        {
            if (milliseconds < 0 || double.IsNaN(milliseconds))
            {
                milliseconds = 0;
            }
            TimeSpan span = TimeSpan.FromMilliseconds(milliseconds);
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}.{2:0}",
                (int)span.TotalMinutes, span.Seconds, span.Milliseconds / 100);
        }

        private void RefreshInspector()
        {
            int selected = _viewModel.SelectedCount;
            if (selected == 0)
            {
                InspectorSelection.Text = "Nothing selected";
                VolumeSlider.IsEnabled = false;
                VolumeReadout.Text = "--";
                return;
            }

            InspectorSelection.Text = selected == 1
                ? "1 note selected"
                : selected + " notes selected";

            EventData first = FirstSelected();
            VolumeSlider.IsEnabled = _document != null && !_document.Model.IsReadOnly;
            if (first != null)
            {
                _suppressComboEvents = true;
                // Vel: the field SetSelectionVolume writes, so the slider shows back what it set
                // instead of a note's untouched EventData.Volume default. See
                // OnCanvasContextRequested for the two fields.
                VolumeSlider.Value = first.Vel;
                _suppressComboEvents = false;
                VolumeReadout.Text = first.Vel.ToString(CultureInfo.InvariantCulture);
            }
        }

        private EventData FirstSelected()
        {
            if (_document == null)
            {
                return null;
            }
            foreach (TrackData track in _document.Model.Tracks)
            {
                foreach (EventData ev in track.Events)
                {
                    if (_document.Selection.Contains(ev))
                    {
                        return ev;
                    }
                }
            }
            return null;
        }

        private void Warn(string message, string caption)
        {
            System.Windows.MessageBox.Show(this, message ?? "Unknown error", caption,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private bool RequireWritableDocument()
        {
            if (_document == null)
            {
                StatusHint.Text = "Open a chart first";
                return false;
            }
            if (_document.Model.IsReadOnly)
            {
                StatusHint.Text = "This chart is read-only";
                return false;
            }
            return true;
        }

        // ===================================================================================
        // Keyboard
        // ===================================================================================

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            if (e.Handled)
            {
                return;
            }

            bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (control)
            {
                switch (e.Key)
                {
                    case Key.Z: OnUndo(this, null); e.Handled = true; return;
                    case Key.Y: OnRedo(this, null); e.Handled = true; return;
                    case Key.X: OnCut(this, null); e.Handled = true; return;
                    case Key.C: OnCopy(this, null); e.Handled = true; return;
                    case Key.V: OnPaste(this, null); e.Handled = true; return;
                    case Key.N: OnNew(this, null); e.Handled = true; return;
                    case Key.O: OnOpen(this, null); e.Handled = true; return;
                    case Key.S: OnSave(this, null); e.Handled = true; return;

                    // Ctrl+, is the settings shortcut in VS Code, Chrome and every JetBrains IDE.
                    case Key.OemComma: OnOpenPreferences(this, null); e.Handled = true; return;
                }
                return;
            }

            switch (e.Key)
            {
                // ptSequencer's tool shortcuts, unchanged so muscle memory transfers.
                case Key.F5: SetTool(ToolMode.Select); e.Handled = true; break;
                case Key.F6: SetTool(ToolMode.Addition); e.Handled = true; break;
                case Key.F7: SetTool(ToolMode.Edit); e.Handled = true; break;
                case Key.F8: SetTool(ToolMode.Delete); e.Handled = true; break;

                case Key.Space: OnPlayPause(this, null); e.Handled = true; break;
                case Key.Enter: OnPlayFromSpot(this, null); e.Handled = true; break;
                case Key.Home: OnReturnToStart(this, null); e.Handled = true; break;

                case Key.Escape:
                    if (_player != null && _player.IsReady && _player.IsPlaying)
                    {
                        Stop();
                    }
                    else if (_document != null)
                    {
                        _document.Selection.Clear();
                        _canvas.InvalidateBand();
                    }
                    e.Handled = true;
                    break;

                case Key.Delete:
                    if (RequireWritableDocument() && _document.Edits.DeleteSelection())
                    {
                        _viewModel.Rebuild();
                        _canvas.InvalidateBand();
                        OnCanvasInteractionCompleted(this, EventArgs.Empty);
                    }
                    e.Handled = true;
                    break;

                case Key.OemPlus:
                case Key.Add:
                    OnZoomIn(this, null);
                    e.Handled = true;
                    break;

                case Key.OemMinus:
                case Key.Subtract:
                    OnZoomOut(this, null);
                    e.Handled = true;
                    break;

                // Drag-move by keyboard: arrows move the selection one lane and one grid step,
                // in screen directions regardless of orientation or inverse scroll. A slider,
                // combo or text box with focus keeps the keys for its own use.
                case Key.Left:
                case Key.Right:
                case Key.Up:
                case Key.Down:
                    if (!ArrowKeyWantedByFocusedControl())
                    {
                        int x = e.Key == Key.Left ? -1 : (e.Key == Key.Right ? 1 : 0);
                        int y = e.Key == Key.Up ? -1 : (e.Key == Key.Down ? 1 : 0);
                        e.Handled = _canvas.NudgeSelection(x, y);
                    }
                    break;
            }
        }

        /// <summary>
        /// True when the focused control consumes the arrow keys itself (a slider steps, a combo
        /// or list moves its selection, a text box moves the caret). Nudging notes must not steal
        /// the keys there.
        /// </summary>
        private static bool ArrowKeyWantedByFocusedControl()
        {
            if (Keyboard.FocusedElement is System.Windows.DependencyObject focused)
            {
                if (focused is System.Windows.Controls.TextBox ||
                    focused is System.Windows.Controls.Primitives.RangeBase ||
                    focused is System.Windows.Controls.Primitives.Selector)
                {
                    return true;
                }
            }
            return false;
        }

        private void SetTool(ToolMode tool)
        {
            _canvas.Tool = tool;
            _suppressComboEvents = true;
            ToolSelect.IsChecked = tool == ToolMode.Select;
            ToolAdd.IsChecked = tool == ToolMode.Addition;
            ToolEdit.IsChecked = tool == ToolMode.Edit;
            ToolDelete.IsChecked = tool == ToolMode.Delete;
            _suppressComboEvents = false;
            RefreshStatus();
        }

        // ===================================================================================
        // Title bar
        // ===================================================================================

        private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            BeginTitleBarDrag(e);
        }

        private void OnToggleMaximise(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        // ===================================================================================
        // File
        // ===================================================================================

        private void OnNew(object sender, RoutedEventArgs e)
        {
            PlayerData model = new PlayerData();
            model.TickPerMinute = 192;
            model.Tempo = 120f;
            AdoptDocument(model, "untitled.pt");
            StatusHint.Text = "New chart";
        }

        private async void OnOpen(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog();
            dialog.Filter = _files.OpenFilter;
            dialog.Title = "Open chart";
            ApplyLastFolder(dialog);
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
            RememberFolder(dialog.FileName);
            await OpenPathAsync(dialog.FileName);
        }

        /// <summary>
        /// Starts a file dialog in the folder the last one ended in.
        ///
        /// <para>
        /// Windows already does this per-process, which is exactly the problem: it forgets on exit,
        /// and a chart folder is somewhere like
        /// <c>...\Technika Projects\DM\extracted\&lt;song&gt;\</c> - deep enough that re-navigating to
        /// it is the slowest part of opening a chart.
        /// </para>
        /// </summary>
        private void ApplyLastFolder(Microsoft.Win32.FileDialog dialog)
        {
            if (_settings == null || !_settings.Format.RememberLastFolder)
            {
                return;
            }

            string folder = _settings.Format.LastFolder;
            try
            {
                // Checked rather than trusted: the folder may have been on a drive that is no longer
                // there, and a dialog handed a dead InitialDirectory silently ignores it anyway.
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                {
                    dialog.InitialDirectory = folder;
                }
            }
            catch (Exception)
            {
                // An unmappable stored path reads as "no preference".
            }
        }

        private void RememberFolder(string chosenFile)
        {
            if (_settings == null || !_settings.Format.RememberLastFolder)
            {
                return;
            }

            try
            {
                string folder = Path.GetDirectoryName(chosenFile);
                if (!string.IsNullOrEmpty(folder))
                {
                    _settings.Format.LastFolder = folder;
                }
            }
            catch (ArgumentException)
            {
                // Nothing to remember.
            }
        }

        private async void OnSave(object sender, RoutedEventArgs e)
        {
            if (_document == null)
            {
                StatusHint.Text = "Nothing to save";
                return;
            }

            Microsoft.Win32.SaveFileDialog dialog = new Microsoft.Win32.SaveFileDialog();
            dialog.Filter = _files.SaveFilter;
            dialog.Title = "Save chart as";
            dialog.FileName = Path.GetFileName(_document.SourcePath);
            ApplyLastFolder(dialog);
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
            RememberFolder(dialog.FileName);

            DJMaxEditor.Files.ISaveFile handler = _files.SaveHandlerFor(dialog.FileName);
            if (handler == null)
            {
                Warn("No writer is registered for that extension.", "Save error");
                return;
            }

            // The .pt writer still carries its WinForms options dialog. It is the one legacy window
            // left in this app; it is functional, and replacing it with a native panel is a
            // separate piece of work from getting the shell to save at all.
            System.Windows.Forms.Form settings = handler.GetSettingsForm();
            if (settings != null)
            {
                settings.Text = "Save options";
                settings.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
                if (settings.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                {
                    return;
                }
            }

            StatusHint.Text = "Saving...";
            string error = await _files.SaveAsync(dialog.FileName, _document.Model, handler);
            if (error != null)
            {
                StatusHint.Text = "Save failed";
                Warn(error, "Save error");
                return;
            }

            DocumentLabel.Text = Path.GetFileName(dialog.FileName);
            StatusHint.Text = "Saved " + Path.GetFileName(dialog.FileName);
        }

        // ===================================================================================
        // Edit
        // ===================================================================================

        private void OnUndo(object sender, RoutedEventArgs e)
        {
            if (_document != null)
            {
                _document.UndoManager.Undo();
            }
        }

        private void OnRedo(object sender, RoutedEventArgs e)
        {
            if (_document != null)
            {
                _document.UndoManager.Redo();
            }
        }

        private void OnCut(object sender, RoutedEventArgs e)
        {
            if (RequireWritableDocument() && _document.Clipboard.CutSelection())
            {
                _viewModel.Rebuild();
                OnCanvasInteractionCompleted(this, EventArgs.Empty);
            }
        }

        private void OnCopy(object sender, RoutedEventArgs e)
        {
            if (_document != null)
            {
                _document.Clipboard.CopySelection();
                RefreshStatus();
            }
        }

        private void OnPaste(object sender, RoutedEventArgs e)
        {
            if (!RequireWritableDocument())
            {
                return;
            }

            // PasteAt hands back the events it created so the caller can select them - which is
            // what you want after a paste, since the next thing you do is almost always move them.
            //
            // PasteAt writes its destination verbatim - it does not snap. The playhead moves in
            // virtual ticks now, so during playback it can sit between grid points, and a paste
            // there would put notes off the tick grid every editor surface edits on. Round back
            // to the grid line the playhead is inside of.
            int destinationTick = _viewModel.PlayheadVirtualTick /
                EventData.VirtualTickSize * EventData.VirtualTickSize;
            IList<EventData> pasted = _document.Clipboard.PasteAt(destinationTick);
            if (pasted == null || pasted.Count == 0)
            {
                return;
            }

            _document.Selection.Replace(pasted);
            _viewModel.Rebuild();
            OnCanvasInteractionCompleted(this, EventArgs.Empty);
        }

        // ===================================================================================
        // Tools
        // ===================================================================================

        private void OnToolSelect(object sender, RoutedEventArgs e) { SetTool(ToolMode.Select); }
        private void OnToolAdd(object sender, RoutedEventArgs e) { SetTool(ToolMode.Addition); }
        private void OnToolEdit(object sender, RoutedEventArgs e) { SetTool(ToolMode.Edit); }
        private void OnToolDelete(object sender, RoutedEventArgs e) { SetTool(ToolMode.Delete); }

        // ===================================================================================
        // Transport buttons
        // ===================================================================================

        private void OnReturnToStart(object sender, RoutedEventArgs e)
        {
            Stop();
            _viewModel.PlayheadVirtualTick = 0;
            _viewModel.ScrollToTick(0);
            _canvas.InvalidateBand();
            UpdateTimeReadout();
        }

        private void OnPlayPause(object sender, RoutedEventArgs e)
        {
            if (_player == null || !_player.IsReady)
            {
                StatusHint.Text = "Open a chart first";
                return;
            }

            if (_player.IsStopped)
            {
                Play(0);
                return;
            }

            if (_player.IsPaused)
            {
                // Resume is a play from the playhead, not an un-freeze.
                //
                // Un-pausing the group put every frozen voice back on the air from wherever in its
                // waveform it had stopped, and on a hold that is the sound the owner kept reporting
                // when the timeline stopped and started. Stop-then-play-from-here was their own
                // suggestion, and it needs nothing new: Play already empties the graph and
                // RestoreSoundingVoices re-strikes whatever each track would still be sounding here,
                // part-way in, which is exactly what the play-from-spot button has always done. It
                // also means a playhead dragged while paused is where playback continues from, rather
                // than silently resuming somewhere the playhead no longer is.
                Play(_viewModel.PlayheadVirtualTick / EventData.VirtualTickSize, "resume");
                RefreshStatus();
                return;
            }

            // Order matters: silence the group first, then stop the clock. The other way round
            // leaves the device pulling from a live mixer for a buffer or two with nothing
            // advancing it, which is audible as a stutter on the note that was sounding.
            //
            // FadeOutAndSilence rather than SetAllPaused(true): the mix still leaves over the 8 ms
            // ramp, and once it is out every voice is dropped instead of frozen, so a paused editor
            // is holding nothing that any later edge could put back on the air.
            //
            // Nothing else is needed here. The tail the owner heard for five rounds was never the
            // driver's queue: the paused graph was clearing a quarter of each buffer and handing the
            // endpoint the rest of the previous one, once per device period, for as long as the pause
            // lasted. That is fixed where it belonged, in the graph - see Audio.SampleBuffers. Measured
            // over a loopback capture of the endpoint's own mix, this edge now goes quiet 17.8 ms after
            // Escape's does, which is the anti-click ramp plus one buffer.
            if (_audio != null)
            {
                _audio.FadeOutAndSilence();
            }
            _player.Pause();
            _viewModel.IsPlaybackActive = false;
            StopPump();
            RefreshPlayGlyph();
            RefreshStatus();
            LogTransport("pause");
        }

        private void OnPlayFromSpot(object sender, RoutedEventArgs e)
        {
            Play(_viewModel.PlayheadVirtualTick / EventData.VirtualTickSize);
        }

        private void OnStop(object sender, RoutedEventArgs e)
        {
            Stop();
        }

        private void OnToggleFollow(object sender, RoutedEventArgs e)
        {
            _viewModel.FollowPlayback = FollowToggle.IsChecked == true;
        }

        // ===================================================================================
        // Grid / preset
        // ===================================================================================

        private void OnGridChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || _suppressComboEvents)
            {
                return;
            }
            GridDivision division = GridCombo.SelectedItem as GridDivision;
            if (division != null)
            {
                _canvas.Grid = division;
                _canvas.InvalidateAll();
                RefreshStatus();
            }
        }

        private void OnBeatChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || _suppressComboEvents)
            {
                return;
            }
            BeatDisplay display = BeatCombo.SelectedItem as BeatDisplay;
            if (display != null)
            {
                _canvas.Beats = display;
                _canvas.InvalidateAll();
            }
        }

        private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || _suppressComboEvents)
            {
                return;
            }
            PresetChoice choice = PresetCombo.SelectedItem as PresetChoice;
            if (choice != null)
            {
                _viewModel.ModeOverride = choice.KeyCount;
                _canvas.InvalidateAll();
                _volumeLane.InvalidateVisual();
            }
        }

        // ===================================================================================
        // View
        // ===================================================================================

        private void OnToggleLabels(object sender, RoutedEventArgs e)
        {
            _canvas.ShowNoteLabels = LabelsToggle.IsChecked == true;
            _canvas.InvalidateBand();
        }

        private void OnToggleAssets(object sender, RoutedEventArgs e)
        {
            _canvas.ShowNoteAssets = AssetsToggle.IsChecked == true;
            _canvas.InvalidateBand();
        }

        private void OnToggleDirection(object sender, RoutedEventArgs e)
        {
            // Upward is the gameplay reading: notes fall towards a judgement line at the bottom.
            // The first playtest called the other way round "so wrong", so this defaults to
            // Upward and the toggle is for people who think in score order.
            ApplyTimeDirection();
            _canvas.InvalidateAll();
            _volumeLane.InvalidateVisual();
        }

        /// <summary>
        /// Pushes the direction toggle into the view model for the orientation in force.
        ///
        /// The button means "the way the game reads it", not a screen axis. Vertical that is
        /// upward - notes falling towards a judgement line at the bottom. Transposed it is
        /// rightward, the way a TECHNIKA scan sweeps. Both are the surface's own
        /// <see cref="VerticalTimeDirection"/>, and the transpose maps surface Y onto screen X
        /// without reversing it, so the two readings are opposite enum values. Deriving them here,
        /// from the two states together, is what stops the toggle from meaning one thing until you
        /// rotate the canvas and the opposite afterwards.
        /// </summary>
        private void ApplyTimeDirection()
        {
            bool gameplay = DirectionToggle.IsChecked == true;
            bool horizontal = _canvas.Orientation == TimelineOrientation.Horizontal;

            _viewModel.TimeDirection = gameplay == horizontal
                ? VerticalTimeDirection.Downward
                : VerticalTimeDirection.Upward;

            DirectionToggle.ToolTip = horizontal
                ? (gameplay ? "Time runs rightward (gameplay order)" : "Time runs leftward (reverse)")
                : (gameplay ? "Time runs upward (gameplay order)" : "Time runs downward (score order)");
        }

        /// <summary>
        /// Reflects the timeline across its diagonal: time across the screen, lanes down it.
        ///
        /// Nothing is rebuilt and nothing is duplicated - the projection, the layout, the frame and
        /// the hit testing are the same objects, and only the matrix they are drawn and probed
        /// through changes. What the shell owes the new reading is the furniture: the volume lane
        /// docks under the canvas instead of beside it, and "the way the game scrolls" becomes
        /// rightward rather than upward.
        /// </summary>
        private void OnToggleOrientation(object sender, RoutedEventArgs e)
        {
            // A click is a decision, same convention as the BGA and playfield toggles: adopt-time
            // defaults pass this window as the sender so they do not count as one.
            if (sender != this)
            {
                _orientationChosen = true;
            }

            bool horizontal = OrientationToggle.IsChecked == true;

            _canvas.Orientation = horizontal
                ? TimelineOrientation.Horizontal
                : TimelineOrientation.Vertical;
            _volumeLane.Orientation = _canvas.Orientation;

            OrientationToggle.ToolTip = horizontal
                ? "Time runs across the screen (DAW layout)"
                : "Time runs down the screen (ptSequencer layout)";

            DockVolumeLane(horizontal);
            PlaceColumnScroll();
            ApplyTimeDirection();

            // No FitColumns here on purpose: the lanes now have to fit the other axis, and the
            // canvas has not been re-measured yet, so the honest width is the one BuildFrame reads
            // out of the next layout pass. AutoFitColumns makes it do exactly that.
            _canvas.InvalidateAll();
            _volumeLane.InvalidateVisual();
            RefreshStatus();
        }

        private void OnZoomIn(object sender, RoutedEventArgs e)
        {
            _viewModel.ZoomAt(_canvas.TimeAxisExtent / 2, ZoomStep);
            RefreshStatus();
        }

        private void OnZoomOut(object sender, RoutedEventArgs e)
        {
            _viewModel.ZoomAt(_canvas.TimeAxisExtent / 2, 1.0 / ZoomStep);
            RefreshStatus();
        }

        /// <summary>
        /// How much one zoom press multiplies the time scale by. A preference because the right
        /// answer depends on the chart: 1.25 is four presses to double, which is right for placing
        /// notes and far too slow for crossing a five-minute BGA-sync chart.
        /// </summary>
        private double ZoomStep
        {
            get { return _settings == null ? 1.25 : _settings.Timeline.ZoomStep; }
        }

        private void OnFitColumns(object sender, RoutedEventArgs e)
        {
            _viewModel.AutoFitColumns = true;
            _viewModel.FitColumns((int)_canvas.LaneAxisExtent);
            _canvas.InvalidateAll();
            _volumeLane.InvalidateVisual();
        }

        private void OnTrackWidthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready)
            {
                return;
            }
            TrackWidthReadout.Text = e.NewValue.ToString("0.00", CultureInfo.InvariantCulture) + "x";

            // Setting a width by hand means you no longer want the viewport deciding it for you,
            // otherwise the next resize would snap the thumb back and the control would feel broken.
            _viewModel.AutoFitColumns = false;
            _viewModel.ColumnScale = e.NewValue;
            _canvas.InvalidateAll();
            _volumeLane.InvalidateVisual();
        }

        /// <summary>
        /// "Note speed": the time-axis density. This is what the old Note height slider actually
        /// did - everything between two bar lines stretches or squeezes, which is the chart's
        /// pace on screen.
        /// </summary>
        private void OnNoteSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready || _syncingNoteSpeed)
            {
                return;
            }
            NoteSpeedReadout.Text = e.NewValue.ToString("0.00", CultureInfo.InvariantCulture);
            _viewModel.TrySetTimeZoom((float)(e.NewValue / VerticalTimelineViewModel.BasePixelsPerTick));
            _canvas.InvalidateAll();
            _volumeLane.InvalidateVisual();
            RefreshStatus();
        }

        /// <summary>
        /// The zoom the model is really at, reflected back onto the NoteSpeed slider. The slider
        /// is pixels-per-tick wearing a friendlier name, so this is a plain assignment - clamped
        /// by the slider itself when the model sits outside its range. The epsilon is what keeps
        /// a drag from fighting its own echo: the slider writes the model, the model answers here,
        /// and a value that came back unchanged must not write the slider again.
        /// </summary>
        private void OnTimeZoomChanged(object sender, EventArgs e)
        {
            if (!_ready)
            {
                return;
            }
            double zoom = _viewModel.PixelsPerTick;
            if (Math.Abs(zoom - NoteSpeedSlider.Value) < 1e-9)
            {
                return;
            }
            _syncingNoteSpeed = true;
            try
            {
                NoteSpeedSlider.Value = zoom;
                NoteSpeedReadout.Text =
                    NoteSpeedSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
            }
            finally
            {
                _syncingNoteSpeed = false;
            }
            RefreshStatus();
        }

        /// <summary>
        /// "Note height": how thick a note head draws, and nothing else. Kept off the time map on
        /// purpose - the chart's motion is the speed slider's job, not this one's.
        /// </summary>
        private void OnNoteHeightChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready)
            {
                return;
            }
            NoteHeightReadout.Text = e.NewValue.ToString("0.00", CultureInfo.InvariantCulture);
            _viewModel.NoteThickness = e.NewValue;
            _canvas.InvalidateAll();
            _volumeLane.InvalidateVisual();
        }

        // ===================================================================================
        // Docks
        // ===================================================================================

        private void OnToggleLeftDock(object sender, RoutedEventArgs e)
        {
            bool show = LeftDockToggle.IsChecked == true;
            LeftDock.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            LeftSplitter.Visibility = LeftDock.Visibility;
            LeftDockColumn.Width = show ? new GridLength(252) : new GridLength(0);
            LeftSplitterColumn.Width = show ? new GridLength(4) : new GridLength(0);
        }

        private void OnToggleRightDock(object sender, RoutedEventArgs e)
        {
            bool show = RightDockToggle.IsChecked == true;
            RightDock.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            RightSplitter.Visibility = RightDock.Visibility;
            RightDockColumn.Width = show ? new GridLength(288) : new GridLength(0);
            RightSplitterColumn.Width = show ? new GridLength(4) : new GridLength(0);
        }

        private void OnToggleVolumeLane(object sender, RoutedEventArgs e)
        {
            bool show = VolumeLaneToggle.IsChecked == true;
            VolumeLaneHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            VolumeSplitter.Visibility = VolumeLaneHost.Visibility;

            // Its thickness is on whichever axis it is docked against, so hiding it goes through
            // the same method that docks it. Two places that both set a GridLength to zero are two
            // places that can disagree about which axis is currently the open one.
            DockVolumeLane(_canvas.Orientation == TimelineOrientation.Horizontal);
        }

        /// <summary>
        /// Docks the note-volume lane on the axis it can actually be used on: a column to the right
        /// of the canvas while time runs down, a row underneath it once time runs across.
        ///
        /// It edits notes by where they sit along the time axis, so it is only usable while it
        /// shares that axis with the canvas - which is the same argument the XAML makes for putting
        /// it beside the canvas in the first place, just applied to the other orientation. The
        /// closed axis is pinned to zero and the canvas spans it, so the cell it does not occupy
        /// costs nothing.
        /// </summary>
        private void DockVolumeLane(bool horizontal)
        {
            bool show = VolumeLaneToggle.IsChecked == true;
            GridLength splitter = show ? new GridLength(4) : new GridLength(0);
            GridLength lane = show ? new GridLength(VolumeLaneThickness) : new GridLength(0);
            GridLength closed = new GridLength(0);

            if (horizontal)
            {
                VolumeSplitterColumn.Width = closed;
                VolumeLaneColumn.Width = closed;
                VolumeSplitterRow.Height = splitter;
                VolumeLaneRow.Height = lane;

                Grid.SetColumnSpan(CanvasCell, 3);

                Grid.SetRow(VolumeSplitter, 1);
                Grid.SetColumn(VolumeSplitter, 0);
                Grid.SetColumnSpan(VolumeSplitter, 3);
                VolumeSplitter.ResizeDirection = GridResizeDirection.Rows;

                Grid.SetRow(VolumeLaneHost, 2);
                Grid.SetColumn(VolumeLaneHost, 0);
                Grid.SetColumnSpan(VolumeLaneHost, 3);
            }
            else
            {
                VolumeSplitterRow.Height = closed;
                VolumeLaneRow.Height = closed;
                VolumeSplitterColumn.Width = splitter;
                VolumeLaneColumn.Width = lane;

                Grid.SetColumnSpan(CanvasCell, 1);

                Grid.SetRow(VolumeSplitter, 0);
                Grid.SetColumn(VolumeSplitter, 1);
                Grid.SetColumnSpan(VolumeSplitter, 1);
                VolumeSplitter.ResizeDirection = GridResizeDirection.Columns;

                Grid.SetRow(VolumeLaneHost, 0);
                Grid.SetColumn(VolumeLaneHost, 2);
                Grid.SetColumnSpan(VolumeLaneHost, 1);
            }
        }

        /// <summary>
        /// Puts the column scrollbar on the edge the lanes run along: under the canvas while time
        /// runs down the screen, beside it once time runs across.
        ///
        /// The bar always eats into the time axis, never the lane axis. That is what keeps it from
        /// fighting the fit: the space it takes cannot change how many lanes are on screen, so
        /// showing it can never be what makes it necessary or unnecessary.
        /// </summary>
        private void PlaceColumnScroll()
        {
            if (_columnScroll == null)
            {
                return;
            }

            bool horizontal = _canvas.Orientation == TimelineOrientation.Horizontal;
            _columnScroll.ApplyOrientation(_canvas.Orientation);
            Grid.SetRow(ColumnScroll, horizontal ? 0 : 1);
            Grid.SetColumn(ColumnScroll, horizontal ? 1 : 0);
        }

        private void OnToggleBga(object sender, RoutedEventArgs e)
        {
            // A click is a decision: once made, a discovered BGA never reopens the panel behind
            // the user's back. Startup wiring passes a null sender to stay out of that.
            if (sender != this)
            {
                _bgaPanelChosen = true;
            }

            BgaPanel.Visibility = BgaToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnTogglePlayfield(object sender, RoutedEventArgs e)
        {
            // Same convention as the BGA toggle: a real click settles the matter, and the
            // adopt-time auto-open passes a null sender so it does not count as one.
            if (sender != this)
            {
                _playfieldPanelChosen = true;
            }

            PlayfieldPanel.Visibility = PlayfieldToggle.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;

            // The view only redraws on a tick change, and while stopped there are none, so a panel
            // that was hidden when the chart was adopted needs one push to have something in it.
            if (PlayfieldPanel.Visibility == Visibility.Visible &&
                _activePlayfield != null)
            {
                _activePlayfield.Sync(_viewModel.PlayheadVirtualTick);
            }
        }

        /// <summary>
        /// The scroll-direction effector: it moves every note inside its scan, so honouring it
        /// means rebuilding the TECHNIKA projection rather than mirroring the finished frame.
        /// The view and its other effectors survive the rebind, since it is the same instance.
        /// </summary>
        private void OnTechnikaScrollChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _document == null || _chartFormat == null)
            {
                return;
            }
            if (_chartFormat != ChartFormat.PtffDecrypted &&
                _chartFormat != ChartFormat.PtffEncryptedTechnika &&
                _chartFormat != ChartFormat.TechmaniaTrack)
            {
                return;
            }
            BindPlayfield(_document.Model);
        }

        private void OnTechnikaFaderChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded)
            {
                return;
            }
            // The combo's item order matches the enum's declaration order: Off, FadeIn,
            // FadeIn2, FadeOut, FadeOut2.
            int mode = TechnikaFaderCombo.SelectedIndex;
            if (mode >= 0)
            {
                _playfield.NoteFader = (TechnikaNoteFader)mode;
            }
        }

        private void OnTechnikaLineChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded)
            {
                return;
            }
            // Item order matches the enum: On, Blink, Blink2, Blind.
            int mode = TechnikaLineCombo.SelectedIndex;
            if (mode >= 0)
            {
                _playfield.LineEffector = (TechnikaLineEffector)mode;
            }
        }

        private void OnTechnikaGuidesChanged(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded)
            {
                return;
            }
            _playfield.ShowMeasurementGuides = TechnikaGuidesCheck.IsChecked == true;
        }

        /// <summary>The scroll-direction effector selected in the playfield strip.</summary>
        private TechnikaScrollDirection SelectedTechnikaScroll()
        {
            switch (TechnikaScrollCombo.SelectedIndex)
            {
                case 1: return TechnikaScrollDirection.CounterClockwise;
                case 2: return TechnikaScrollDirection.AllLeft;
                case 3: return TechnikaScrollDirection.AllRight;
                default: return TechnikaScrollDirection.Clockwise;
            }
        }

        /// <summary>The arcade effector code shown in the panel status for the bound direction.</summary>
        private static string TechnikaScrollLabel(TechnikaScrollDirection direction)
        {
            switch (direction)
            {
                case TechnikaScrollDirection.CounterClockwise: return "CCW";
                case TechnikaScrollDirection.AllLeft: return "LL";
                case TechnikaScrollDirection.AllRight: return "RR";
                default: return "CW";
            }
        }

        /// <summary>
        /// Per-format shell shape, settled once per adopted document. Three rules, all from real
        /// feature requests:
        ///
        /// <para>
        /// <b>TECHNIKA-only tools only exist for TECHNIKA files.</b> The arcade-art toggle draws
        /// TECHNIKA attribute art by construction and the playfield panel projects a TECHNIKA
        /// profile, so on a BMS or RESPECT V chart both could only mislabel things. They are
        /// removed from the toolbar rather than disabled - a disabled button advertises a feature
        /// the format cannot have. The note palette (left dock) follows the same gate: attributes
        /// 5/6/10/11/12 mean something only under a TECHNIKA layout.
        /// </para>
        /// <para>
        /// <b>TECHNIKA charts default to the DAW reading.</b> TECHNIKA itself is a horizontal
        /// game - a scan sweeps sideways - and its editors read that way, so an adopted .pt
        /// lands transposed until the user picks an orientation themselves this session. The
        /// escape hatch is the same "once unless chosen" convention the BGA/playfield panels use.
        /// </para>
        /// <para>
        /// <b>The file suggests its theme.</b> A BMS chart opens on the beatoraja-style IIDX
        /// palette, a RESPECT V trailer on the RESPECT V one, a TECHNIKA .pt on the Studio one
        /// whose arcade art carries the look - again only until the user has picked a theme.
        /// </para>
        /// </summary>
        private void ApplyDocumentShape(PlayerData model)
        {
            ChartFormat? format = model == null ? null : model.SourceFormat;
            _chartFormat = format;

            bool technika = format == ChartFormat.PtffDecrypted ||
                format == ChartFormat.PtffEncryptedTechnika ||
                format == ChartFormat.TechmaniaTrack;
            bool respectV = format == ChartFormat.TrailerRespectV;
            bool bms = format == ChartFormat.BmsClassic || format == ChartFormat.Bmson;

            bool hasPlayfield = technika || respectV;
            PlayfieldToggle.Visibility = hasPlayfield ? Visibility.Visible : Visibility.Collapsed;
            AssetsToggle.Visibility = technika ? Visibility.Visible : Visibility.Collapsed;
            NotePalettePanel.Visibility = technika ? Visibility.Visible : Visibility.Collapsed;
            PlayfieldToggle.ToolTip = technika
                ? "Show the TECHNIKA gameplay playfield"
                : "Show the RESPECT V playfield";

            if (!hasPlayfield)
            {
                // A panel from the previous chart must not leak into a format that has nothing
                // to project; the user's choice flag is untouched, so the next TECHNIKA chart
                // behaves exactly as if this one had never come between.
                if (PlayfieldToggle.IsChecked == true)
                {
                    PlayfieldToggle.IsChecked = false;
                }
                PlayfieldPanel.Visibility = Visibility.Collapsed;
            }

            if (!_orientationChosen)
            {
                bool wantHorizontal = technika;
                if ((OrientationToggle.IsChecked == true) != wantHorizontal)
                {
                    OrientationToggle.IsChecked = wantHorizontal;
                    OnToggleOrientation(this, null);
                }
            }

            if (!_themeChosen && _settings != null && _settings.Appearance != null)
            {
                string themeId = bms
                    ? StudioChartTheme.IidxId
                    : respectV
                        ? StudioChartTheme.RespectVId
                        : technika
                            ? StudioChartTheme.StudioId
                            : null;
                if (themeId != null &&
                    !string.Equals(_settings.Appearance.ChartThemeId, themeId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _settings.Appearance.ChartThemeId = themeId;
                    ApplyAppearanceSettings(_settings.Appearance);
                }
            }
        }

        /// <summary>
        /// Projects the chart onto the TECHNIKA playfield and updates the panel around it.
        ///
        /// <para>
        /// The profile comes from <c>GameplayPreviewProfileResolver</c> rather than from a guess
        /// here, because the same PTFF container holds TECHNIKA and Trilogy charts and only the
        /// resolver knows how that is decided. When it reports the choice as unconfirmed, the
        /// explanation goes into the panel's tooltip: the preview still draws, but the reader is
        /// told the profile was inferred.
        /// </para>
        /// </summary>
        private void BindPlayfield(PlayerData model)
        {
            // The format question decides which gear the panel dresses as, so it is settled here
            // and kept: ApplyDocumentShape reads _chartFormat for toolbar visibility.
            _chartFormat = model == null ? (ChartFormat?)null : model.SourceFormat;

            if (model == null)
            {
                _playfield.Unbind();
                _respectPlayfield.Unbind();
                _activePlayfield = null;
                TechnikaEffectorStrip.Visibility = Visibility.Collapsed;
                PlayfieldStatus.Text = "no chart loaded";
                PlayfieldStatus.ToolTip = null;
                return;
            }

            GameplayPreviewProfileSuggestion suggestion;
            GameplayPreviewProjection projection;
            try
            {
                suggestion = GameplayPreviewProfileResolver.Suggest(model);
                // The scroll effector moves every note inside its scan, so it feeds the
                // projection rather than a renderer mirror; the Generic branch ignores it.
                TechnikaScrollDirection scroll = SelectedTechnikaScroll();
                projection = GameplayPreviewProjector.Project(
                    model, suggestion.Profile, scroll);
            }
            catch (Exception ex)
            {
                // A preview is not worth losing a chart over. The panel says so and the editor
                // carries on; the projector's own diagnostics are in the log.
                DiagnosticLog.Exception("technika.playfield", ex);
                _playfield.Unbind();
                _respectPlayfield.Unbind();
                _activePlayfield = null;
                TechnikaEffectorStrip.Visibility = Visibility.Collapsed;
                PlayfieldStatus.Text = "projection failed";
                PlayfieldStatus.ToolTip = ex.Message;
                return;
            }

            if (projection.Profile == GameplayPreviewProfile.Technika)
            {
                _respectPlayfield.Unbind();
                HostPlayfield(_playfield);
                _playfield.Bind(projection);
                _activePlayfield = _playfield;
                TechnikaEffectorStrip.Visibility = Visibility.Visible;

                PlayfieldStatus.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} lanes  {1} notes  {2}  scroll {3}",
                    projection.LaneCount,
                    projection.Notes.Count,
                    _playfield.SpriteSourceLabel,
                    TechnikaScrollLabel(projection.ScrollDirection));
                PlayfieldStatus.ToolTip = suggestion.RequiresConfirmation
                    ? projection.StatusLabel + "\n" + suggestion.Explanation
                    : projection.StatusLabel;
            }
            else if (_chartFormat == ChartFormat.TrailerRespectV)
            {
                // A RESPECT V chart: the Generic branch has already placed every note in the
                // game's own 502-wide lane geometry; the Respect view draws that, in the game's
                // gear where the extraction is on hand. Its gear has no TECHNIKA effectors.
                _playfield.Unbind();
                HostPlayfield(_respectPlayfield);
                _respectPlayfield.Bind(projection);
                _activePlayfield = _respectPlayfield;
                TechnikaEffectorStrip.Visibility = Visibility.Collapsed;

                PlayfieldStatus.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} lanes  {1} notes  {2}",
                    projection.LaneCount,
                    projection.Notes.Count,
                    _respectPlayfield.GearSourceLabel);
                PlayfieldStatus.ToolTip = projection.StatusLabel;
            }
            else
            {
                // Neither playfield speaks this chart's game. There is nothing to pretend with:
                // the panel reports it and hides, rather than showing the wrong game's gear.
                _playfield.Unbind();
                _respectPlayfield.Unbind();
                _activePlayfield = null;
                HostPlayfield(_playfield);
                TechnikaEffectorStrip.Visibility = Visibility.Collapsed;
                PlayfieldStatus.Text = "no gameplay preview for this format";
                PlayfieldStatus.ToolTip = suggestion.Explanation;
                return;
            }

            _activePlayfield.Sync(_viewModel.PlayheadVirtualTick);

            // First playable chart of the session opens the panel once, for the same reason a
            // discovered BGA does: the preview is useless if you have to know to go and find it.
            bool mayOpen = _settings == null || _settings.Bga.AutoOpenPlayfieldForTechnika;
            if (mayOpen && !_playfieldPanelChosen && PlayfieldToggle.IsChecked != true)
            {
                PlayfieldToggle.IsChecked = true;
                OnTogglePlayfield(this, null);
            }
        }

        /// <summary>Swaps which view the playfield panel hosts, if it is not there already.</summary>
        private void HostPlayfield(UIElement view)
        {
            if (!ReferenceEquals(PlayfieldHost.Child, view))
            {
                PlayfieldHost.Child = view;
            }
        }

        // ===================================================================================
        // Samples / inspector
        // ===================================================================================

        private void OnSampleDoubleClick(object sender, MouseButtonEventArgs e)
        {
            InstrumentData instrument = SampleList.SelectedItem as InstrumentData;
            if (instrument == null || _audio == null || instrument.InsNum == 0)
            {
                return;
            }
            // Auditions go out on the top channel, which no shipped format addresses, so
            // previewing a keysound during playback cannot cut off a note that is sounding. Its
            // own volume preference, separate from the master, because auditioning is a
            // comparison and playback is a mix.
            float gain = _settings == null ? 1f : (float)_settings.Audio.AuditionVolume;
            _audio.PlaySound(AuditionChannel, instrument.InsNum, gain, 64);
        }

        /// <summary>
        /// The channel auditions go out on. The top of the player's 100-channel table, not above
        /// it: channel indices wrap modulo <c>MAX_CHANNEL</c>, so the old 512 was really channel
        /// 12 - a track TECHNIKA charts and XB charts both address, and exactly the note an
        /// audition must not steal. Nothing addressable is perfectly isolated - a trailer chart
        /// can carry 256 tracks - but no shipped format puts gameplay on track 99, and with the
        /// player's overlapping retrigger on (the default) even that collision would ring out
        /// rather than cut. See <see cref="NAudioKeysoundPlayer.PlaySound"/> for why an audition
        /// also breaks a settled pause instead of freezing silently inside it.
        /// </summary>
        private const uint AuditionChannel = 99;

        private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready || _suppressComboEvents || _document == null)
            {
                return;
            }

            byte value = (byte)Math.Round(e.NewValue);
            VolumeReadout.Text = value.ToString(CultureInfo.InvariantCulture);
            if (!RequireWritableDocument())
            {
                return;
            }

            // One undo group for the whole slider drag: the key is the slider itself, so every
            // intermediate value collapses into a single step.
            if (_document.Edits.SetSelectionVolume(value, VolumeSlider))
            {
                _canvas.InvalidateBand();
                _volumeLane.InvalidateVisual();
            }
        }

        // ===================================================================================
        // BGA
        // ===================================================================================

        private void OnLoadBga(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog();

            // .bik is in the default filter because a Technika preview is the most likely BGA a
            // user of this editor has on disk, even though nothing on Windows plays one natively.
            // See BgaSourceResolver for how it becomes playable.
            dialog.Filter =
                "Video|*.mp4;*.wmv;*.avi;*.mkv;*.mov;*.bik|" +
                "Technika preview (Bink)|*.bik;*.bik2;*.bk2|" +
                "All files|*.*";
            dialog.Title = "Load BGA video";
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
            _bgaPath = dialog.FileName;
            _ = AttachBgaAsync();
        }

        /// <summary>
        /// Attaches the picked video to the preview surface. The decoder lives behind an interface
        /// in <c>Studio.Video</c> so the Media Foundation dependency stays swappable; this method is
        /// the single place the shell touches it.
        /// <para>
        /// Asynchronous because a Bink preview has to go through FFmpeg first, which is a child
        /// process and hundreds of milliseconds at best. Running that inline is the same mistake
        /// that made opening a Technika chart look like a crash - see <see cref="LoadKeysounds"/>.
        /// </para>
        /// </summary>
        private async Task AttachBgaAsync()
        {
            string path = _bgaPath;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            string playable = path;
            bool converted = false;

            if (BgaSourceResolver.RequiresTranscode(path))
            {
                BgaStatus.Text = "Converting " + Path.GetFileName(path) + "...";
                BgaStatus.ToolTip = path;

                string resolved = null;
                string failure = null;
                Stopwatch clock = Stopwatch.StartNew();
                await Task.Run(() =>
                {
                    if (!BgaSourceResolver.TryResolve(path, out resolved, out failure))
                    {
                        resolved = null;
                    }
                }).ConfigureAwait(true);

                // A second pick while the first was converting: the stale one must not steal the
                // panel from the video the user actually wants.
                if (!string.Equals(path, _bgaPath, StringComparison.Ordinal))
                {
                    DiagnosticLog.Write("bga.open", "superseded: " + path);
                    return;
                }

                if (resolved == null)
                {
                    BgaStatus.Text = failure;
                    BgaStatus.ToolTip = path;
                    DiagnosticLog.Write("bga.open", "transcode failed: " + failure + " for " + path);
                    return;
                }

                converted = !string.Equals(resolved, path, StringComparison.OrdinalIgnoreCase);
                DiagnosticLog.Write(
                    "bga.transcode",
                    Path.GetFileName(path) + " -> " + resolved +
                    " in " + clock.ElapsedMilliseconds + "ms");
                playable = resolved;
            }

            string error;
            if (!_bga.TryOpen(playable, out error))
            {
                // A missing codec or a Windows N edition with no Media Feature Pack is a normal
                // outcome, not a fault: the message goes on the panel and charting carries on.
                BgaStatus.Text = error;
                BgaStatus.ToolTip = path;
                DiagnosticLog.Write("bga.open", "failed: " + error + " for " + playable);
                return;
            }

            BgaStatus.Text = converted ? _bga.StatusText + " - converted" : _bga.StatusText;
            BgaStatus.ToolTip = path;
            DiagnosticLog.Write("bga.open", _bga.StatusText + " from " + playable);

            // Show the frame under the caret straight away, so loading a video is not a black
            // rectangle until you press play.
            SyncBga();
        }

        /// <summary>
        /// Attaches the video that sits beside a freshly opened chart, if there is one and the user
        /// has not already picked something themselves.
        /// <para>
        /// This is what makes a Technika song folder just work: extract <c>Preview.pak</c> into the
        /// pattern folders and every chart opens with its own BGA already on the panel. A manual pick
        /// always wins - once <see cref="_bgaPath"/> is set, only the file dialog changes it.
        /// </para>
        /// </summary>
        private void DiscoverBga(string chartPath)
        {
            if (!string.IsNullOrEmpty(_bgaPath))
            {
                return;
            }

            // Off is off: a folder full of previews is convenient right up until it is a folder you
            // did not want the editor reading, and the discovery is the part that touches the disk.
            if (_settings != null && !_settings.Bga.DiscoverBesideChart)
            {
                return;
            }

            string found = BgaSourceResolver.FindForChart(chartPath);
            if (found == null)
            {
                return;
            }

            _bgaPath = found;
            DiagnosticLog.Write("bga.discover", found + " for " + Path.GetFileName(chartPath));

            // Opening the panel is how the feature announces itself: extract a song's preview and
            // the next chart you open simply has it. Suppressed once the user has had their say -
            // by clicking the toggle, or in advance by unticking it in preferences.
            bool mayOpen = _settings == null || _settings.Bga.AutoOpenPanel;
            if (mayOpen && !_bgaPanelChosen && BgaToggle.IsChecked != true)
            {
                BgaToggle.IsChecked = true;
                OnToggleBga(this, null);
            }

            // Not awaited: a Bink conversion must not hold up the rest of the adopt.
            _ = AttachBgaAsync();
        }

        /// <summary>
        /// Puts the frame belonging to the current playhead tick on screen.
        ///
        /// Called from the playback pump and after every seek. Tick to time goes through
        /// <see cref="BgaClockMap"/> rather than the sequencer's own accumulated millisecond count,
        /// because that count only means anything while a take is running - scrubbing a stopped
        /// chart has to map the tick directly.
        /// </summary>
        private void SyncBga()
        {
            if (_bga.Source == null || !_bga.Source.IsOpen)
            {
                return;
            }
            _bga.Seek(_bgaClock.TimeForVirtualTick(_viewModel.PlayheadVirtualTick));
        }

        /// <summary>One entry in the lane-layout combo. 0 means "read the mode out of the chart".</summary>
        private sealed class PresetChoice
        {
            public PresetChoice(int keyCount, string label)
            {
                KeyCount = keyCount;
                Label = label;
            }

            public int KeyCount { get; private set; }
            public string Label { get; private set; }

            public override string ToString()
            {
                return Label;
            }
        }
    }
}
