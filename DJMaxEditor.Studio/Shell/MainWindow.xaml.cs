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
using System.Windows.Threading;
using DJMaxEditor.Controls.Vertical;
using DJMaxEditor.Diagnostics;
using DJMaxEditor.DJMax;
using DJMaxEditor.Editor;
using DJMaxEditor.Preview;
using DJMaxEditor.Studio.Audio;
using DJMaxEditor.Studio.Documents;
using DJMaxEditor.Studio.Editing;
using DJMaxEditor.Studio.Preview;
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

        private NAudioKeysoundPlayer _audio;
        private Player _player;
        private EditorDocumentContext _document;
        private TrackPresetLibrary _presets;

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

        private bool _pumpAttached;
        private int _lastPumpTick = -1;
        private bool _suppressComboEvents;
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
            InitializeComponent();

            _canvas.ViewModel = _viewModel;
            _volumeLane.ViewModel = _viewModel;
            CanvasHost.Child = _canvas;
            VolumeLaneHost.Child = _volumeLane;
            BgaHost.Child = _bga;
            PlayfieldHost.Child = _playfield;

            _canvas.SeekRequested += OnCanvasSeekRequested;
            _canvas.InteractionCompleted += OnCanvasInteractionCompleted;
            _canvas.ContextRequested += OnCanvasContextRequested;
            _volumeLane.VolumeEdited += OnCanvasInteractionCompleted;

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

            // Derived rather than assigned, from the two toggles' declared states: vertical and
            // "gameplay order" is Upward. One code path decides it, so the buttons cannot start out
            // meaning something different from what they mean after the first click.
            ApplyTimeDirection();

            InitialiseCombos();
            InitialiseAudio();

            _ready = true;

            // The sliders' declared values are the defaults, so apply them once now that the
            // handlers are allowed to run.
            _viewModel.ColumnScale = TrackWidthSlider.Value;
            _viewModel.TrySetTimeZoom(
                (float)(NoteHeightSlider.Value / VerticalTimelineViewModel.BasePixelsPerTick));
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

        private void InitialiseAudio()
        {
            try
            {
                _audio = new NAudioKeysoundPlayer();
                _audio.Log = message => Logs.Write(message);
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

            StatusHint.Text = "Loading " + probe.FileName + "...";
            Cursor = Cursors.AppStarting;
            ChartOpenResult result;
            try
            {
                result = await _files.OpenAsync(probe, fromDecrypted);
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
            _lastPumpTick = -1;
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
                    float gain = track.Volume * NoteVelocityGain(sounding.Vel);
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
            _playfield.Sync(_viewModel.PlayheadVirtualTick);
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

            int tick = _player.GetCurrentTick();
            if (tick == _lastPumpTick)
            {
                return;
            }
            _lastPumpTick = tick;

            // One assignment moves the playhead, scrolls the follow window and repaints the note
            // band; the overlay is nudged separately because the setter short-circuits when the
            // tick has not changed and the playhead line still has to move within a frame.
            _viewModel.PlayheadVirtualTick = tick * EventData.VirtualTickSize;
            _canvas.InvalidateOverlay();
            SyncBga();
            _playfield.Sync(_viewModel.PlayheadVirtualTick);

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
                    // every file format for years and was never audible.
                    float gain = track.Volume * NoteVelocityGain(eventData.Vel);

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
            _playfield.Sync(_viewModel.PlayheadVirtualTick);
        }

        private void OnCanvasInteractionCompleted(object sender, EventArgs e)
        {
            _volumeLane.InvalidateVisual();
            RefreshStatus();
            RefreshInspector();
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
            }
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
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
            await OpenPathAsync(dialog.FileName);
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
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

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
            IList<EventData> pasted = _document.Clipboard.PasteAt(_viewModel.PlayheadVirtualTick);
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
            bool horizontal = OrientationToggle.IsChecked == true;

            _canvas.Orientation = horizontal
                ? TimelineOrientation.Horizontal
                : TimelineOrientation.Vertical;
            _volumeLane.Orientation = _canvas.Orientation;

            OrientationToggle.ToolTip = horizontal
                ? "Time runs across the screen (DAW layout)"
                : "Time runs down the screen (ptSequencer layout)";

            DockVolumeLane(horizontal);
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
            _viewModel.ZoomAt(_canvas.TimeAxisExtent / 2, 1.25);
            RefreshStatus();
        }

        private void OnZoomOut(object sender, RoutedEventArgs e)
        {
            _viewModel.ZoomAt(_canvas.TimeAxisExtent / 2, 1.0 / 1.25);
            RefreshStatus();
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

        private void OnNoteHeightChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready)
            {
                return;
            }
            NoteHeightReadout.Text = e.NewValue.ToString("0.00", CultureInfo.InvariantCulture);
            _viewModel.TrySetTimeZoom((float)(e.NewValue / VerticalTimelineViewModel.BasePixelsPerTick));
            _canvas.InvalidateAll();
            _volumeLane.InvalidateVisual();
            RefreshStatus();
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

                Grid.SetColumnSpan(CanvasHost, 3);

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

                Grid.SetColumnSpan(CanvasHost, 1);

                Grid.SetRow(VolumeSplitter, 0);
                Grid.SetColumn(VolumeSplitter, 1);
                Grid.SetColumnSpan(VolumeSplitter, 1);
                VolumeSplitter.ResizeDirection = GridResizeDirection.Columns;

                Grid.SetRow(VolumeLaneHost, 0);
                Grid.SetColumn(VolumeLaneHost, 2);
                Grid.SetColumnSpan(VolumeLaneHost, 1);
            }
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
            if (PlayfieldPanel.Visibility == Visibility.Visible)
            {
                _playfield.Sync(_viewModel.PlayheadVirtualTick);
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
            if (model == null)
            {
                _playfield.Unbind();
                PlayfieldStatus.Text = "no chart loaded";
                PlayfieldStatus.ToolTip = null;
                return;
            }

            GameplayPreviewProfileSuggestion suggestion;
            GameplayPreviewProjection projection;
            try
            {
                suggestion = GameplayPreviewProfileResolver.Suggest(model);
                projection = GameplayPreviewProjector.Project(model, suggestion.Profile);
            }
            catch (Exception ex)
            {
                // A preview is not worth losing a chart over. The panel says so and the editor
                // carries on; the projector's own diagnostics are in the log.
                DiagnosticLog.Exception("technika.playfield", ex);
                _playfield.Unbind();
                PlayfieldStatus.Text = "projection failed";
                PlayfieldStatus.ToolTip = ex.Message;
                return;
            }

            _playfield.Bind(projection);

            if (projection.Profile != GameplayPreviewProfile.Technika)
            {
                PlayfieldStatus.Text = "not a TECHNIKA chart";
                PlayfieldStatus.ToolTip = suggestion.Explanation;
                return;
            }

            PlayfieldStatus.Text = string.Format(
                CultureInfo.InvariantCulture,
                "{0} lanes  {1} notes  {2}",
                projection.LaneCount,
                projection.Notes.Count,
                _playfield.SpriteSourceLabel);
            PlayfieldStatus.ToolTip = suggestion.RequiresConfirmation
                ? projection.StatusLabel + "\n" + suggestion.Explanation
                : projection.StatusLabel;

            _playfield.Sync(_viewModel.PlayheadVirtualTick);

            // First TECHNIKA chart of the session opens the panel once, for the same reason a
            // discovered BGA does: the preview is useless if you have to know to go and find it.
            if (!_playfieldPanelChosen && PlayfieldToggle.IsChecked != true)
            {
                PlayfieldToggle.IsChecked = true;
                OnTogglePlayfield(this, null);
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
            // Auditions go out on a channel the sequencer never uses, so previewing a keysound
            // during playback cannot cut off a note that is sounding.
            _audio.PlaySound(AuditionChannel, instrument.InsNum, 1f, 64);
        }

        /// <summary>A channel index above any track a chart can address, reserved for auditions.</summary>
        private const uint AuditionChannel = 512;

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

            string found = BgaSourceResolver.FindForChart(chartPath);
            if (found == null)
            {
                return;
            }

            _bgaPath = found;
            DiagnosticLog.Write("bga.discover", found + " for " + Path.GetFileName(chartPath));

            // Opening the panel is how the feature announces itself: extract a song's preview and
            // the next chart you open simply has it. Suppressed once the user has had their say.
            if (!_bgaPanelChosen && BgaToggle.IsChecked != true)
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
