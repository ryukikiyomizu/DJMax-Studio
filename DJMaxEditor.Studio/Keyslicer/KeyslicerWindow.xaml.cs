using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using DJMaxEditor.DJMax;
using DJMaxEditor.Editor;
using DJMaxEditor.Studio.Audio;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DJMaxEditor.Studio.Keyslicer
{
    /// <summary>
    /// The slicer scene window. This is the "new scene" the spec calls for:
    /// a dedicated, non-destructive keysound authoring space that sits beside
    /// the chart timeline rather than inside it.
    ///
    /// Two timelines are kept strictly separate:
    ///   - Source timeline: milliseconds inside the raw recording (waveform view).
    ///   - Chart timeline: milliseconds inside the song (playhead + note list).
    /// A slice bridges them as { sourceFile, startMs, endMs } -> note { chartTimeMs, lane }.
    /// </summary>
    public partial class KeyslicerWindow : Window
    {
        private readonly KeyslicerViewModel _vm = new KeyslicerViewModel();
        private EditorDocumentContext _chartContext; // optional: the open chart to send notes to
        private NAudioKeysoundPlayer _auditionPlayer; // lightweight player for preview
        private IAudioOutput _auditionOutput;
        private bool _ready;
        private bool _suppressInspectorEvents;

        public KeyslicerWindow()
        {
            InitializeComponent();
            DataContext = _vm;

            WaveformView.ViewModel = _vm;
            WaveformView.SelectionCompleted += OnWaveformSelectionCompleted;
            WaveformView.PlayheadSeekRequested += OnWaveformSeek;
            WaveformView.SliceClicked += OnSliceClickedViaWaveform;

            // Wire up view model events.
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.WaveformUpdated += (s, e) => UpdateSourceInfo();
            _vm.SlicesChanged += (s, e) => RefreshLists();

            // Lists.
            SliceList.ItemsSource = _vm.Slices;
            // SourceList will be bound manually to Project.Sources (Observable vs List - use refresh).

            // Inspector lane picker.
            InspectorLane.Items.Add(new ComboBoxItem { Content = "— unassigned", Tag = -1 });
            for (int i = 1; i <= 8; i++)
                InspectorLane.Items.Add(new ComboBoxItem { Content = "Lane " + i, Tag = i - 1 });
            InspectorLane.SelectedIndex = 0;

            // Sens label.
            SensLabel.Text = _vm.DetectorSettings.Sensitivity.ToString("0.00", CultureInfo.InvariantCulture);
            SensSlider.Value = _vm.DetectorSettings.Sensitivity;

            // Snap + advance: best defaults without nagging the user.
            SnapCombo.ItemsSource = new[] { "Free", "1/4", "1/8", "1/16", "1/32" };
            SnapCombo.SelectedIndex = SnapIndexFromDenom(_vm.SnapDenominator);
            foreach (ComboBoxItem it in AutoAdvanceCombo.Items)
                if ((string)it.Tag == (_vm.AutoAdvanceMode ?? "grid")) { AutoAdvanceCombo.SelectedItem = it; break; }
            ZeroCrossCheck.IsChecked = _vm.SnapToZeroCrossing;
            NormalizeCheck.IsChecked = _vm.Project.Export.Normalize;

            Loaded += OnLoaded;
            Closing += OnClosing;
            PreviewKeyDown += OnPreviewKeyDown;
        }

        public void AttachChartContext(EditorDocumentContext context)
        {
            _chartContext = context;
            // Best default: slicer follows the open chart's tempo/offset so a note placed
            // at the slicer playhead lands on time in the editor. No dialog needed.
            try
            {
                if (context != null)
                {
                    double bpm = 0;
                    double off = 0;
                    try
                    {
                        var player = context.Model;
                        if (player != null)
                        {
                            // First tempo event wins; offset from techMetadata if present
                            if (Math.Abs(player.Tempo) > 0.1) bpm = player.Tempo;
                            foreach (var tr in player.Tracks)
                                foreach (var ev in tr.Events)
                                    if (ev.EventType == EventType.Tempo && ev.Tempo > 10) { bpm = ev.Tempo; break; }
                            // PlayerData doesn't carry a global offset in this fork; keep 0 and let OffsetBox drive it.
                            // If the main editor exposes an offset later, wire it here.
                        }
                    }
                    catch { }
                    if (bpm > 0)
                    {
                        _vm.SyncFromChart(bpm, off);
                        // Reflect in UI if loaded; otherwise UpdateAll will pick it up.
                        BpmBox.Text = _vm.Bpm.ToString("0.###", CultureInfo.InvariantCulture);
                        SnapCombo.SelectedIndex = SnapIndexFromDenom(_vm.SnapDenominator);
                    }
                }
            }
            catch { }
        }

        private static int SnapIndexFromDenom(int denom)
        {
            switch (denom)
            {
                case 0: return 0;
                case 4: return 1;
                case 8: return 2;
                case 16: return 3;
                case 32: return 4;
                default: return 3;
            }
        }
        private static int SnapDenomFromIndex(int idx)
        {
            switch (idx)
            {
                case 0: return 0;
                case 1: return 4;
                case 2: return 8;
                case 3: return 16;
                case 4: return 32;
                default: return 16;
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _ready = true;
            // Lightweight audition device.
            try
            {
                var settingsStore = new Studio.Settings.StudioSettingsStore();
                var settings = settingsStore.Load();
                int latency = settings?.Audio?.OutputLatencyMs ?? 60;
                _auditionOutput = new NAudioDeviceOutput(latency);
                _auditionPlayer = new NAudioKeysoundPlayer(_auditionOutput, 512 * 1024 * 1024L);
            }
            catch
            {
                _auditionPlayer = null;
            }

            UpdateAll();
            WaveformView.Focus();
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            // Don't destroy project on close - window is just hidden. But check dirty.
            if (_vm.IsDirty)
            {
                var res = MessageBox.Show(this,
                    "The slicer project has unsaved changes. Save before closing?",
                    "Keysound Slicer", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (res == MessageBoxResult.Cancel) { e.Cancel = true; return; }
                if (res == MessageBoxResult.Yes)
                {
                    string path = _vm.ProjectPath;
                    if (string.IsNullOrEmpty(path))
                        path = PromptSavePath();
                    if (!string.IsNullOrEmpty(path))
                        _vm.SaveProject(path, PortableCheck.IsChecked == true);
                    else
                        e.Cancel = true;
                }
            }
            _vm.Dispose();
            try { _auditionPlayer?.Dispose(); } catch { }
            try { _auditionOutput?.Dispose(); } catch { }
        }

        // ----------------------------------------------------------------
        // View model -> UI
        // ----------------------------------------------------------------
        private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!_ready) return;
            switch (e.PropertyName)
            {
                case nameof(KeyslicerViewModel.Title):
                    Title = "Keysound Slicer - " + _vm.Title;
                    ProjectLabel.Text = _vm.Title;
                    break;
                case nameof(KeyslicerViewModel.Status):
                    StatusLabel.Text = _vm.Status;
                    StatusBarText.Text = _vm.Status;
                    break;
                case nameof(KeyslicerViewModel.Zoom):
                    ZoomLabel.Text = "ZOOM " + _vm.Zoom.ToString("0.0", CultureInfo.InvariantCulture) + "x";
                    if (Math.Abs(ZoomSlider.Value - _vm.Zoom) > 0.01)
                        ZoomSlider.Value = _vm.Zoom;
                    break;
                case nameof(KeyslicerViewModel.PlayheadMs):
                    // no op
                    break;
                case nameof(KeyslicerViewModel.DraftStartMs):
                case nameof(KeyslicerViewModel.DraftEndMs):
                    UpdateDraftInfo();
                    break;
                case nameof(KeyslicerViewModel.SelectedSlice):
                    UpdateInspector();
                    break;
            }
            if (e.PropertyName == nameof(KeyslicerViewModel.Zoom) ||
                e.PropertyName == nameof(KeyslicerViewModel.ScrollMs))
                ZoomLabel.Text = "ZOOM " + _vm.Zoom.ToString("0.0", CultureInfo.InvariantCulture) + "x";

            // Suggestion label.
            if (e.PropertyName == nameof(KeyslicerViewModel.SuggestionIndex) ||
                e.PropertyName == "Suggestions")
            {
                int idx = _vm.SuggestionIndex;
                int n = _vm.Suggestions.Count;
                SuggestionLabel.Text = n == 0 ? "No suggestions" : string.Format("Suggestion {0}/{1}  ({2:F1}-{3:F1} ms, {4:P0})",
                    idx + 1, n, _vm.Suggestions[idx].StartMs, _vm.Suggestions[idx].EndMs, _vm.Suggestions[idx].Confidence);
            }

            // Slice count + project summary.
            if (e.PropertyName == nameof(KeyslicerViewModel.Status) ||
                e.PropertyName == "Slices")
            {
                SliceCountLabel.Text = _vm.Slices.Count.ToString(CultureInfo.InvariantCulture);
                ProjectSummary.Text = string.Format("{0} source(s), {1} slice(s), {2} note(s)",
                    _vm.Project.Sources.Count, _vm.Slices.Count, _vm.Project.Notes.Count);
                NoteCountLabel.Text = _vm.Project.Notes.Count + " notes";
            }
        }

        private void UpdateAll()
        {
            Title = "Keysound Slicer - " + _vm.Title;
            ProjectLabel.Text = _vm.Title;
            BpmBox.Text = _vm.Bpm.ToString("0.###", CultureInfo.InvariantCulture);
            OffsetBox.Text = _vm.Project.OffsetMs.ToString("0.#", CultureInfo.InvariantCulture);
            ChartPlayheadBox.Text = _vm.ChartPlayheadMs.ToString("0.#", CultureInfo.InvariantCulture);
            FadeInBox.Text = _vm.Project.Export.DefaultFadeInMs.ToString("0.#", CultureInfo.InvariantCulture);
            FadeOutBox.Text = _vm.Project.Export.DefaultFadeOutMs.ToString("0.#", CultureInfo.InvariantCulture);
            try { SnapCombo.SelectedIndex = SnapIndexFromDenom(_vm.SnapDenominator); } catch { }
            try
            {
                foreach (ComboBoxItem it in AutoAdvanceCombo.Items)
                    if ((string)it.Tag == (_vm.AutoAdvanceMode ?? "grid")) { AutoAdvanceCombo.SelectedItem = it; break; }
            }
            catch { }
            try { ZeroCrossCheck.IsChecked = _vm.SnapToZeroCrossing; } catch { }
            try { NormalizeCheck.IsChecked = _vm.Project.Export.Normalize; } catch { }
            RefreshSources();
            RefreshLists();
            UpdateInspector();
            UpdateDraftInfo();
            UpdateSourceInfo();
            StatusLabel.Text = _vm.Status;
            StatusBarText.Text = _vm.Status;
            ZoomLabel.Text = "ZOOM " + _vm.Zoom.ToString("0.0", CultureInfo.InvariantCulture) + "x";
        }

        private void RefreshSources()
        {
            SourceList.ItemsSource = null;
            SourceList.ItemsSource = _vm.Project.Sources;
            if (_vm.SelectedSource != null)
                SourceList.SelectedItem = _vm.SelectedSource;
            SourceInfoLabel.Text = _vm.SelectedSource == null
                ? "no source loaded"
                : string.Format("{0}  {1:F0} ms  {2} kHz", _vm.SelectedSource.FileName, _vm.SelectedSource.DurationMs, _vm.SelectedSource.SampleRate);
            ProjectSummary.Text = string.Format("{0} source(s), {1} slice(s), {2} note(s)",
                _vm.Project.Sources.Count, _vm.Slices.Count, _vm.Project.Notes.Count);
        }

        private void RefreshLists()
        {
            // Force slice list refresh (ObservableCollection already notifies, but count label etc).
            SliceCountLabel.Text = _vm.Slices.Count.ToString(CultureInfo.InvariantCulture);
            NoteList.ItemsSource = null;
            NoteList.ItemsSource = _vm.Project.Notes.OrderBy(n => n.ChartTimeMs).ToList();
            ProjectSummary.Text = string.Format("{0} source(s), {1} slice(s), {2} note(s)",
                _vm.Project.Sources.Count, _vm.Slices.Count, _vm.Project.Notes.Count);
            NoteCountLabel.Text = _vm.Project.Notes.Count + " notes";
            StatusLabel.Text = _vm.Status;
            StatusBarText.Text = _vm.Status;
        }

        private void UpdateDraftInfo()
        {
            if (_vm.HasDraft)
            {
                double s = _vm.DraftStartMs, e = _vm.DraftEndMs;
                DraftInfo.Text = string.Format("{0:F1} - {1:F1} ms  ({2:F0} ms)", Math.Min(s, e), Math.Max(s, e), Math.Abs(e - s));
                DraftLabel.Text = ((Math.Abs(e - s))).ToString("0.#", CultureInfo.InvariantCulture) + " ms draft";
            }
            else
            {
                DraftInfo.Text = "— drag on the waveform to create a draft";
                DraftLabel.Text = "";
            }
        }

        private void UpdateSourceInfo()
        {
            if (_vm.SelectedSource != null && _vm.Waveform != null)
                SourceInfoLabel.Text = string.Format("{0}  {1:F0} ms  {2} kHz x{3}  zoom {4:F1}x",
                    _vm.SelectedSource.FileName, _vm.Waveform.DurationMs, _vm.Waveform.SampleRate, _vm.Waveform.Channels, _vm.Zoom);
        }

        private void UpdateInspector()
        {
            _suppressInspectorEvents = true;
            var s = _vm.SelectedSlice;
            if (s == null)
            {
                InspectorNoneLabel.Visibility = Visibility.Visible;
                InspectorForm.Visibility = Visibility.Collapsed;
                InspectorHint.Text = "";
            }
            else
            {
                InspectorNoneLabel.Visibility = Visibility.Collapsed;
                InspectorForm.Visibility = Visibility.Visible;
                FieldId.Text = s.Id;
                FieldLabel.Text = s.Label ?? string.Empty;
                FieldStart.Text = s.StartMs.ToString("0.###", CultureInfo.InvariantCulture);
                FieldEnd.Text = s.EndMs.ToString("0.###", CultureInfo.InvariantCulture);
                FieldDuration.Text = s.DurationMs.ToString("0.#", CultureInfo.InvariantCulture) + " ms";
                GainSlider.Value = Math.Max(0.1, Math.Min(2.0, s.Gain));
                GainLabel.Text = s.Gain.ToString("0.00", CultureInfo.InvariantCulture) + "x";
                FieldSource.Text = s.ResolvedSourcePath ?? s.SourceFile;
                ExportPreviewLabel.Text = string.Format("{0}.ogg  ({1:F0} ms, {2:F2}x)", s.Id, s.DurationMs, s.Gain);

                // Lane picker.
                int idx = 0;
                for (int i = 0; i < InspectorLane.Items.Count; i++)
                {
                    var item = InspectorLane.Items[i] as ComboBoxItem;
                    if (item != null && (int)item.Tag == s.Lane) { idx = i; break; }
                }
                InspectorLane.SelectedIndex = idx;
                InspectorHint.Text = s.IsConfirmed ? "confirmed" : "draft";
            }
            _suppressInspectorEvents = false;
        }

        // ----------------------------------------------------------------
        // Toolbar / project file
        // ----------------------------------------------------------------
        private void OnNew(object sender, RoutedEventArgs e)
        {
            if (_vm.IsDirty)
            {
                var res = MessageBox.Show(this, "Discard unsaved slicer project?", "Keysound Slicer", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res != MessageBoxResult.Yes) return;
            }
            _vm.NewProject();
            UpdateAll();
        }

        private void OnOpenProject(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Keyslicer project|*.ksp;*.json|All files|*.*",
                Title = "Open slicer project"
            };
            if (dlg.ShowDialog(this) != true) return;
            if (_vm.LoadProject(dlg.FileName))
                UpdateAll();
            else
                MessageBox.Show(this, _vm.Status, "Open failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void OnSaveProject(object sender, RoutedEventArgs e)
        {
            string path = _vm.ProjectPath;
            if (string.IsNullOrEmpty(path))
                path = PromptSavePath();
            if (string.IsNullOrEmpty(path)) return;
            bool portable = PortableCheck.IsChecked == true;
            if (_vm.SaveProject(path, portable))
                UpdateAll();
            else
                MessageBox.Show(this, _vm.Status, "Save failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void OnSaveProjectAs(object sender, RoutedEventArgs e)
        {
            string path = PromptSavePath();
            if (string.IsNullOrEmpty(path)) return;
            bool portable = PortableCheck.IsChecked == true;
            if (_vm.SaveProject(path, portable))
                UpdateAll();
            else
                MessageBox.Show(this, _vm.Status, "Save failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private string PromptSavePath()
        {
            var dlg = new SaveFileDialog
            {
                Filter = "Keyslicer project|*.ksp|JSON|*.json|All files|*.*",
                DefaultExt = ".ksp",
                FileName = (_vm.Project.Title ?? "Untitled") + ".ksp",
                Title = "Save slicer project",
                OverwritePrompt = true
            };
            if (dlg.ShowDialog(this) != true) return null;
            return dlg.FileName;
        }

        private void OnImportSource(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Audio files|*.wav;*.ogg;*.mp3;*.flac;*.aiff|All files|*.*",
                Title = "Import source recording (long take)",
                Multiselect = false
            };
            if (dlg.ShowDialog(this) != true) return;
            _vm.AddSource(dlg.FileName);
            RefreshSources();
            StatusLabel.Text = _vm.Status;
            StatusBarText.Text = _vm.Status;
        }

        private void OnRemoveSource(object sender, RoutedEventArgs e)
        {
            var src = SourceList.SelectedItem as KeysoundSource ?? _vm.SelectedSource;
            if (src == null) return;
            var res = MessageBox.Show(this, "Remove source \"" + src.FileName + "\" and its slices?", "Remove source", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;
            _vm.RemoveSource(src);
            RefreshSources();
            RefreshLists();
            UpdateAll();
        }

        private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
        {
            var src = SourceList.SelectedItem as KeysoundSource;
            if (src != null) _vm.SelectedSource = src;
            UpdateSourceInfo();
        }

        // ----------------------------------------------------------------
        // Detection
        // ----------------------------------------------------------------
        private async void OnDetect(object sender, RoutedEventArgs e)
        {
            // Commit sensitivity from slider.
            _vm.DetectorSettings.Sensitivity = SensSlider.Value;
            await _vm.DetectAsync();
            SuggestionLabel.Text = _vm.Suggestions.Count == 0 ? "No suggestions" : string.Format("{0} suggestion(s)", _vm.Suggestions.Count);
            StatusLabel.Text = _vm.Status;
            StatusBarText.Text = _vm.Status;
        }

        private void OnSensChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            SensLabel.Text = e.NewValue.ToString("0.00", CultureInfo.InvariantCulture);
            _vm.DetectorSettings.Sensitivity = e.NewValue;
        }

        private void OnAccept(object sender, RoutedEventArgs e)
        {
            _vm.AcceptSuggestion();
            RefreshLists();
            SuggestionLabel.Text = _vm.Suggestions.Count == 0 ? "No suggestions" : string.Format("{0} remaining", _vm.Suggestions.Count);
        }

        private void OnReject(object sender, RoutedEventArgs e)
        {
            _vm.RejectSuggestion();
            SuggestionLabel.Text = _vm.Suggestions.Count == 0 ? "No suggestions" : string.Format("{0} remaining", _vm.Suggestions.Count);
        }

        private void OnAcceptAll(object sender, RoutedEventArgs e)
        {
            _vm.AcceptAllSuggestions();
            RefreshLists();
            SuggestionLabel.Text = "No suggestions";
        }

        // ----------------------------------------------------------------
        // Slices
        // ----------------------------------------------------------------
        private void OnSliceSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var slice = SliceList.SelectedItem as KeysoundSlice;
            if (slice != null)
            {
                _vm.SelectedSlice = slice;
                // Scroll waveform to show it.
                _vm.EnsureMsVisible((slice.StartMs + slice.EndMs) * 0.5);
            }
            UpdateInspector();
        }

        private void OnSliceClickedViaWaveform(object sender, KeysoundSlice slice)
        {
            SliceList.SelectedItem = slice;
            _vm.SelectedSlice = slice;
            UpdateInspector();
        }

        private void OnDeleteSlice(object sender, RoutedEventArgs e)
        {
            var slice = SliceList.SelectedItem as KeysoundSlice ?? _vm.SelectedSlice;
            if (slice == null) return;
            _vm.DeleteSlice(slice);
            RefreshLists();
            UpdateInspector();
        }

        private void OnDuplicateSlice(object sender, RoutedEventArgs e)
        {
            var slice = SliceList.SelectedItem as KeysoundSlice ?? _vm.SelectedSlice;
            if (slice == null) return;
            var dup = slice.Clone();
            dup.Id = _vm.Project.AllocateId();
            dup.CreatedAt = DateTime.UtcNow;
            _vm.Slices.Add(dup);
            _vm.SelectedSlice = dup;
            SliceList.SelectedItem = dup;
            RefreshLists();
            UpdateInspector();
            StatusLabel.Text = "Duplicated " + slice.Id + " -> " + dup.Id;
            StatusBarText.Text = StatusLabel.Text;
        }

        private void OnMergeSlices(object sender, RoutedEventArgs e)
        {
            var selected = SliceList.SelectedItems.Cast<KeysoundSlice>().ToList();
            if (selected.Count != 2)
            {
                MessageBox.Show(this, "Select exactly 2 slices to merge.", "Merge", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            selected = selected.OrderBy(s => s.StartMs).ToList();
            var a = selected[0];
            var b = selected[1];
            if (!string.Equals(a.ResolvedSourcePath ?? a.SourceFile, b.ResolvedSourcePath ?? b.SourceFile, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "Slices must come from the same source file to merge.", "Merge", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var merged = new KeysoundSlice
            {
                Id = _vm.Project.AllocateId(),
                SourceFile = a.SourceFile,
                ResolvedSourcePath = a.ResolvedSourcePath,
                StartMs = Math.Min(a.StartMs, b.StartMs),
                EndMs = Math.Max(a.EndMs, b.EndMs),
                Gain = (a.Gain + b.Gain) * 0.5,
                FadeInMs = a.FadeInMs,
                FadeOutMs = b.FadeOutMs,
                Lane = a.Lane,
                Label = a.Label + "+" + b.Label,
                IsConfirmed = true
            };
            _vm.Slices.Add(merged);
            _vm.DeleteSlice(a);
            _vm.DeleteSlice(b);
            _vm.SelectedSlice = merged;
            SliceList.SelectedItem = merged;
            RefreshLists();
            UpdateInspector();
        }

        private void OnLaneAssigned(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || _suppressInspectorEvents) return;
            var slice = SliceList.SelectedItem as KeysoundSlice ?? _vm.SelectedSlice;
            if (slice == null) return;
            var item = LaneCombo.SelectedItem as ComboBoxItem;
            if (item == null) return;
            slice.Lane = (int)item.Tag;
            RefreshLists();
            UpdateInspector();
        }

        // ----------------------------------------------------------------
        // Draft -> slice
        // ----------------------------------------------------------------
        private void OnWaveformSelectionCompleted(object sender, EventArgs e)
        {
            UpdateDraftInfo();
        }

        private void OnWaveformSeek(object sender, double ms)
        {
            _vm.PlayheadMs = ms;
            // Audition on seek if desired.
            _ = AuditionAtAsync(ms, 300);
        }

        private void OnCreateSlice(object sender, RoutedEventArgs e)
        {
            CreateSliceFromDraft(-1);
        }

        private void OnClearDraft(object sender, RoutedEventArgs e)
        {
            _vm.ClearDraft();
            UpdateDraftInfo();
        }

        private void CreateSliceFromDraft(int lane)
        {
            if (!_vm.HasDraft)
            {
                StatusLabel.Text = "No draft - drag on the waveform first";
                StatusBarText.Text = StatusLabel.Text;
                return;
            }
            // Lane from inspector or param.
            if (lane == -1)
            {
                var s = _vm.SelectedSlice;
                // Use lane from lane combo? Keep as unassigned if none.
                // Use currently selected lane combo choice as hint.
                var item = LaneCombo.SelectedItem as ComboBoxItem;
                if (item != null) lane = (int)item.Tag;
            }
            // Gain / fades from UI.
            double fadeIn = _vm.Project.Export.DefaultFadeInMs;
            double fadeOut = _vm.Project.Export.DefaultFadeOutMs;
            double.TryParse(FadeInBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out fadeIn);
            double.TryParse(FadeOutBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out fadeOut);
            _vm.Project.Export.DefaultFadeInMs = fadeIn;
            _vm.Project.Export.DefaultFadeOutMs = fadeOut;

            var slice = _vm.CreateSliceFromDraft(lane);
            if (slice != null)
            {
                slice.FadeInMs = fadeIn;
                slice.FadeOutMs = fadeOut;
                // Auto-place note if enabled.
                if (AutoPlaceCheck.IsChecked == true)
                {
                    double chartMs = _vm.ChartPlayheadMs;
                    var note = _vm.PlaceNoteForSlice(slice, chartMs);
                    double adv = _vm.ComputeAutoAdvanceMs(slice);
                    double nextMs = adv > 0 ? _vm.QuantizeMs(chartMs + adv) : _vm.ChartPlayheadMs;
                    // When autoAdvance is grid, Quantize prevents drift; when off, keep playhead.
                    if (adv > 0) { _vm.ChartPlayheadMs = nextMs; ChartPlayheadBox.Text = _vm.ChartPlayheadMs.ToString("0.#", CultureInfo.InvariantCulture); }
                    // Subtle status hint so user sees grid vs beat.
                    if (adv > 0 && note != null) StatusLabel.Text = "Placed " + slice.Id + " -> " + note.ChartTimeMs.ToString("0.#", CultureInfo.InvariantCulture) + " ms  next " + nextMs.ToString("0.#", CultureInfo.InvariantCulture) + " ms  (" + _vm.AutoAdvanceMode + "/" + _vm.SnapLabel + ")";
                }
                RefreshLists();
                UpdateInspector();
                _ = AuditionSliceAsync(slice);
            }
            UpdateDraftInfo();
        }

        private void OnLaneD(object sender, RoutedEventArgs e) => CreateSliceFromDraft(0);
        private void OnLaneF(object sender, RoutedEventArgs e) => CreateSliceFromDraft(1);
        private void OnLaneJ(object sender, RoutedEventArgs e) => CreateSliceFromDraft(2);
        private void OnLaneK(object sender, RoutedEventArgs e) => CreateSliceFromDraft(3);

        // ----------------------------------------------------------------
        // Transport / audition
        // ----------------------------------------------------------------
        private void OnPlaySource(object sender, RoutedEventArgs e)
        {
            _ = AuditionAtAsync(_vm.PlayheadMs, 2000);
        }

        private void OnPlayDraft(object sender, RoutedEventArgs e)
        {
            if (_vm.HasDraft)
            {
                var slice = new KeysoundSlice
                {
                    SourceFile = _vm.SelectedSource?.FilePath,
                    ResolvedSourcePath = _vm.SelectedSource?.ResolvedPath,
                    StartMs = Math.Min(_vm.DraftStartMs, _vm.DraftEndMs),
                    EndMs = Math.Max(_vm.DraftStartMs, _vm.DraftEndMs),
                    Gain = 1.0,
                    FadeInMs = 2,
                    FadeOutMs = 5
                };
                _ = AuditionSliceAsync(slice);
            }
            else if (_vm.SelectedSlice != null)
                _ = AuditionSliceAsync(_vm.SelectedSlice);
        }

        private void OnStopSource(object sender, RoutedEventArgs e)
        {
            try { _auditionPlayer?.StopAllSounds(); } catch { }
        }

        private async Task AuditionAtAsync(double startMs, double previewMs)
        {
            if (_vm.SelectedSource == null) return;
            // Lightweight preview: render the interval to a temp wav and play via a throwaway
            // WaveOutEvent that lives for the preview duration. This is editor-only convenience;
            // the chart export does not depend on it, so a failure here never blocks slicing.
            try
            {
                string path = _vm.SelectedSource.ResolvedPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                var slice = new KeysoundSlice
                {
                    SourceFile = path,
                    ResolvedSourcePath = path,
                    StartMs = startMs,
                    EndMs = startMs + previewMs,
                    Gain = 1.0,
                    FadeInMs = 2,
                    FadeOutMs = 10
                };
                string tmpDir = Path.Combine(Path.GetTempPath(), "djmax_slicer_audition_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tmpDir);
                await Task.Run(() =>
                {
                    try
                    {
                        var exp = SliceExporter.Export(new[] { slice }, tmpDir, SliceExporter.ExportFormat.Wav, false, null, false);
                        string wav = exp.Files.FirstOrDefault()?.FullPath;
                        if (!string.IsNullOrEmpty(wav) && File.Exists(wav))
                        {
                            Application.Current?.Dispatcher?.Invoke(() =>
                            {
                                try
                                {
                                    var reader = new AudioFileReader(wav);
                                    var wo = new WaveOutEvent();
                                    wo.Init(reader);
                                    wo.PlaybackStopped += (s, e) => { try { wo.Dispose(); reader.Dispose(); } catch { } try { Directory.Delete(tmpDir, true); } catch { } };
                                    wo.Play();
                                    // Schedule cleanup after previewMs + tail.
                                    Task.Delay((int)Math.Min(5000, previewMs + 400)).ContinueWith(_ =>
                                    {
                                        try { wo.Stop(); } catch { }
                                    });
                                }
                                catch { try { Directory.Delete(tmpDir, true); } catch { } }
                            });
                        }
                        else
                        {
                            try { Directory.Delete(tmpDir, true); } catch { }
                        }
                    }
                    catch { try { Directory.Delete(tmpDir, true); } catch { } }
                });
            }
            catch { }
        }

        private async Task AuditionSliceAsync(KeysoundSlice slice)
        {
            if (slice == null || _vm.SelectedSource == null) return;
            await AuditionAtAsync(slice.StartMs, slice.DurationMs + 120);
        }

        // ----------------------------------------------------------------
        // Chart playhead / notes
        // ----------------------------------------------------------------
        private void OnChartPlayheadChanged(object sender, TextChangedEventArgs e)
        {
            if (!_ready) return;
            if (double.TryParse(ChartPlayheadBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                _vm.ChartPlayheadMs = Math.Max(0, v);
        }

        private void OnClearNotes(object sender, RoutedEventArgs e)
        {
            var res = MessageBox.Show(this, "Clear all " + _vm.Project.Notes.Count + " placed notes?", "Clear notes", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;
            _vm.Project.Notes.Clear();
            RefreshLists();
        }

        private void OnQuantizeNotes(object sender, RoutedEventArgs e)
        {
            foreach (var n in _vm.Project.Notes)
                n.ChartTimeMs = _vm.QuantizeMs(n.ChartTimeMs);
            RefreshLists();
            StatusLabel.Text = "Quantized " + _vm.Project.Notes.Count + " notes to " + _vm.SnapLabel + " at " + _vm.Bpm.ToString("0.#", CultureInfo.InvariantCulture) + " BPM";
            StatusBarText.Text = StatusLabel.Text;
        }

        // ----------------------------------------------------------------
        // Inspector
        // ----------------------------------------------------------------
        private void OnInspectorLabelChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressInspectorEvents || _vm.SelectedSlice == null) return;
            _vm.SelectedSlice.Label = FieldLabel.Text ?? string.Empty;
            RefreshLists();
        }

        private void OnInspectorBoundsChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressInspectorEvents || _vm.SelectedSlice == null) return;
            if (double.TryParse(FieldStart.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double s) &&
                double.TryParse(FieldEnd.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double en))
            {
                _vm.UpdateSlice(_vm.SelectedSlice, s, en);
                FieldDuration.Text = _vm.SelectedSlice.DurationMs.ToString("0.#", CultureInfo.InvariantCulture) + " ms";
                RefreshLists();
            }
        }

        private void OnInspectorLaneChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressInspectorEvents || _vm.SelectedSlice == null) return;
            var item = InspectorLane.SelectedItem as ComboBoxItem;
            if (item == null) return;
            _vm.SelectedSlice.Lane = (int)item.Tag;
            RefreshLists();
        }

        private void OnGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressInspectorEvents || _vm.SelectedSlice == null) return;
            _vm.SelectedSlice.Gain = e.NewValue;
            GainLabel.Text = e.NewValue.ToString("0.00", CultureInfo.InvariantCulture) + "x";
            ExportPreviewLabel.Text = string.Format("{0}.ogg  ({1:F0} ms, {2:F2}x)", _vm.SelectedSlice.Id, _vm.SelectedSlice.DurationMs, _vm.SelectedSlice.Gain);
        }

        private void OnInspectorPlay(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedSlice != null) _ = AuditionSliceAsync(_vm.SelectedSlice);
        }

        private void OnInspectorPlaceNote(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedSlice == null) return;
            double before = _vm.ChartPlayheadMs;
            var note = _vm.PlaceNoteForSlice(_vm.SelectedSlice, before);
            double adv = _vm.ComputeAutoAdvanceMs(_vm.SelectedSlice);
            if (adv > 0) { _vm.ChartPlayheadMs = _vm.QuantizeMs(before + adv); ChartPlayheadBox.Text = _vm.ChartPlayheadMs.ToString("0.#", CultureInfo.InvariantCulture); }
            RefreshLists();
            StatusLabel.Text = "Placed note for " + _vm.SelectedSlice.Id + " at " + note.ChartTimeMs.ToString("0.#", CultureInfo.InvariantCulture) + " ms -> " + _vm.ChartPlayheadMs.ToString("0.#", CultureInfo.InvariantCulture) + " ms";
            StatusBarText.Text = StatusLabel.Text;
        }

        // ----------------------------------------------------------------
        // Export + send to editor
        // ----------------------------------------------------------------
        private void OnExportSlices(object sender, RoutedEventArgs e)
        {
            if (_vm.Slices.Count == 0)
            {
                MessageBox.Show(this, "No slices to export.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Choose output folder for exported slices",
                ShowNewFolderButton = true
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            bool normalize = _vm.Project.Export.Normalize;
            var result = SliceExporter.Export(_vm.Slices, dlg.SelectedPath, SliceExporter.ExportFormat.Wav, false, null, normalize);
            MessageBox.Show(this,
                string.Format("Exported {0} slice(s) to:\n{1}{2}", result.Files.Count(f => f.FullPath != null), dlg.SelectedPath, normalize ? "\n(normalized)" : string.Empty),
                "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusLabel.Text = "Exported " + result.Files.Count(f => f.FullPath != null) + " file(s)";
            StatusBarText.Text = StatusLabel.Text;
        }

        private void OnExportTech(object sender, RoutedEventArgs e)
        {
            if (_vm.Project.Notes.Count == 0)
            {
                MessageBox.Show(this, "No placed notes - use D/F/J/K or Place note to put virtual keysounds on the chart timeline before exporting.", "Export .tech", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // Ask where to write the track.
            var dlg = new SaveFileDialog
            {
                Filter = "TECHMANIA track|track.tech|All files|*.*",
                FileName = "track.tech",
                Title = "Export Techmania chart",
                OverwritePrompt = true
            };
            if (dlg.ShowDialog(this) != true) return;

            string dir = Path.GetDirectoryName(dlg.FileName) ?? ".";
            string audioOut = Path.Combine(dir, "keysounds");
            try { Directory.CreateDirectory(audioOut); } catch { }

            // Export audio first. Normalize is the best default for loudness consistency.
            var usedIds = new HashSet<string>(_vm.Project.Notes.Select(n => n.KeysoundId));
            var exp = SliceExporter.Export(_vm.Slices, audioOut, SliceExporter.ExportFormat.Wav, true, usedIds, _vm.Project.Export.Normalize);

            // Build a PlayerData -> Techmania serialization via the existing writer.
            try
            {
                var player = BuildPlayerDataForExport(exp);
                var techJson = DJMaxEditor.Files.Tech.TechmaniaChartSerializer.Serialize(player);
                File.WriteAllText(dlg.FileName, techJson);
                MessageBox.Show(this,
                    string.Format("Exported {0} keysounds + a {1}-note Techmania chart to:\n{2}\n\nMove the whole folder as a Techmania track.", exp.Files.Count(f => f.FullPath != null), player.Tracks.Events.Length, dir),
                    "Export .tech", MessageBoxButton.OK, MessageBoxImage.Information);
                StatusLabel.Text = "Exported .tech to " + dlg.FileName;
                StatusBarText.Text = StatusLabel.Text;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Tech export failed:\n\n" + ex.Message, "Export .tech", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private PlayerData BuildPlayerDataForExport(SliceExporter.ExportResult exp)
        {
            var player = new PlayerData();
            player.Tempo = (float)_vm.Bpm;
            // Create tracks.
            var laneTracks = new List<TrackData>();
            for (int i = 0; i < 8; i++)
            {
                var t = new TrackData((uint)i) { TrackName = i < 4 ? "Lane " + (i + 1) : "Overflow " + (i - 3) };
                laneTracks.Add(t);
                player.Tracks.AddTrack(t);
            }
            // Tempo track.
            var tempoTrack = new TrackData(8) { TrackName = "Tempo" };
            tempoTrack.AddEvent(new EventData { EventType = EventType.Tempo, Tempo = (float)_vm.Bpm, VirtualTick = 0 });
            player.Tracks.AddTrack(tempoTrack);

            // Map exported file names by slice id.
            var fileById = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in exp.Files)
                if (!string.IsNullOrEmpty(f.FileName) && !string.IsNullOrEmpty(f.SliceId))
                    fileById[f.SliceId] = "keysounds/" + f.FileName;

            // Ensure instruments.
            var instrByName = new Dictionary<string, InstrumentData>(StringComparer.OrdinalIgnoreCase);
            InstrumentData GetOrAddInstrument(string name)
            {
                if (string.IsNullOrEmpty(name)) name = "none";
                if (instrByName.TryGetValue(name, out var existing)) return existing;
                ushort num = (ushort)(player.Instruments.Count + 1);
                var ins = new InstrumentData { InsNum = num, Name = name };
                player.Instruments.Add(ins);
                instrByName[name] = ins;
                return ins;
            }

            // One event per placed note.
            var notes = _vm.Project.Notes.OrderBy(n => n.ChartTimeMs).ToList();
            foreach (var n in notes)
            {
                string keysound = fileById.TryGetValue(n.KeysoundId, out string fn) ? fn : n.KeysoundId + ".wav";
                var instr = GetOrAddInstrument(keysound);
                int vt = n.VirtualTick;
                // Fallback recomputed if stale.
                if (vt <= 0)
                {
                    double bpm = Math.Max(30, _vm.Bpm);
                    double msPerTick = 60000.0 / (bpm * 48.0);
                    double native = n.ChartTimeMs / msPerTick;
                    vt = (int)Math.Round(native * 6.0);
                }
                int lane = Math.Max(0, Math.Min(7, n.Lane));
                var evt = new EventData
                {
                    EventType = EventType.Note,
                    Instrument = instr,
                    Attribute = 0,
                    VirtualTick = Math.Max(0, vt),
                    VirtualDuration = 36, // default tap
                    Volume = 127,
                    Pan = 64
                };
                laneTracks[lane].AddEvent(evt);
            }

            // TechMetadata is optional; leaving it null lets the serializer create a clean container
            // from the 8 playable lanes + tempo above. No need to stash slicer metadata there.
            player.TechMetadata = null;

            return player;
        }

        private void OnSendToEditor(object sender, RoutedEventArgs e)
        {
            if (_chartContext == null)
            {
                MessageBox.Show(this, "No chart is open in the main editor.\n\nOpen a chart first, or export a .tech and open that instead.", "Send to Editor", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (_vm.Project.Notes.Count == 0 && _vm.Slices.Count == 0)
            {
                MessageBox.Show(this, "No slices/notes to send.", "Send to Editor", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // For this session, "send" copies notes into the live PlayerData by inserting
            // Events that reference newly added Instruments. Keysounds are still named ks_*.wav
            // and will need the exported files beside the chart to actually play.
            int added = 0;
            var player = _chartContext.Model;
            // Ensure enough tracks.
            while (player.Tracks.Count < 9)
                player.Tracks.AddTrack(new TrackData((uint)player.Tracks.Count));

            var usedIds = new HashSet<string>(_vm.Project.Notes.Select(n => n.KeysoundId));
            var mapFile = new Dictionary<string, string>();
            // Instruments are just names; the chart will reference ks_*.wav - export must have been done.
            // We still create placeholder instruments so the events have an Instrument.
            var instrByKs = new Dictionary<string, InstrumentData>();
            foreach (var slice in _vm.Slices.Where(s => usedIds.Contains(s.Id)))
            {
                string fn = slice.Id + ".wav";
                if (instrByKs.ContainsKey(fn)) continue;
                ushort num = (ushort)(player.Instruments.Count + 1);
                var ins = new InstrumentData { InsNum = num, Name = fn };
                player.Instruments.Add(ins);
                instrByKs[fn] = ins;
            }

            // Create undo group.
            var backupNotes = _vm.Project.Notes.OrderBy(n => n.ChartTimeMs).ToList();
            foreach (var n in backupNotes)
            {
                string fn = n.KeysoundId + ".wav";
                if (!instrByKs.TryGetValue(fn, out var instr)) continue;
                int lane = Math.Max(0, Math.Min((int)player.Tracks.Count - 1, n.Lane));
                var track = player.Tracks.GetTrackAtIndex((uint)lane);
                if (track == null) continue;
                var evt = new EventData
                {
                    EventType = EventType.Note,
                    Instrument = instr,
                    Attribute = 0,
                    VirtualTick = n.VirtualTick,
                    VirtualDuration = 36,
                    Volume = 127
                };
                track.AddEvent(evt);
                added++;
            }
            _chartContext.UndoManager.Clear(); // simplest: clear undo since we added directly; proper Undo action would be better.
            MessageBox.Show(this,
                string.Format("Sent {0} note(s) into the open chart.\n\nRemember to Export WAVs into the same folder as the chart so the keysounds play.", added),
                "Send to Editor", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusLabel.Text = "Sent " + added + " note(s) to editor";
            StatusBarText.Text = StatusLabel.Text;
        }

        private void OnCloseWindow(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // ----------------------------------------------------------------
        // Inspector field handlers
        // ----------------------------------------------------------------
        private void OnBpmChanged(object sender, TextChangedEventArgs e)
        {
            if (!_ready) return;
            if (double.TryParse(BpmBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                _vm.Bpm = v;
        }

        private void OnOffsetChanged(object sender, TextChangedEventArgs e)
        {
            if (!_ready) return;
            if (double.TryParse(OffsetBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                _vm.Project.OffsetMs = v;
        }

        private void OnFadeChanged(object sender, TextChangedEventArgs e)
        {
            if (!_ready) return;
            double.TryParse(FadeInBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double fin);
            double.TryParse(FadeOutBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double fout);
            _vm.Project.Export.DefaultFadeInMs = Math.Max(0, Math.Min(50, fin));
            _vm.Project.Export.DefaultFadeOutMs = Math.Max(0, Math.Min(200, fout));
            if (_vm.SelectedSlice != null)
            {
                _vm.SelectedSlice.FadeInMs = _vm.Project.Export.DefaultFadeInMs;
                _vm.SelectedSlice.FadeOutMs = _vm.Project.Export.DefaultFadeOutMs;
            }
        }

        private void OnZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            _vm.Zoom = e.NewValue;
        }

        private void OnSnapChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            var cb = sender as ComboBox;
            if (cb == null || cb.SelectedIndex < 0) return;
            _vm.SnapDenominator = SnapDenomFromIndex(cb.SelectedIndex);
            StatusLabel.Text = "Snap " + _vm.SnapLabel;
            StatusBarText.Text = StatusLabel.Text;
        }

        private void OnAutoAdvanceChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            var cb = sender as ComboBox;
            var item = cb?.SelectedItem as ComboBoxItem;
            if (item == null) return;
            string tag = item.Tag as string;
            if (!string.IsNullOrEmpty(tag)) _vm.AutoAdvanceMode = tag;
        }

        private void OnZeroCrossChanged(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            _vm.SnapToZeroCrossing = ZeroCrossCheck.IsChecked == true;
        }

        private void OnNormalizeChanged(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            _vm.Project.Export.Normalize = NormalizeCheck.IsChecked == true;
        }

        private void OnUndo(object sender, RoutedEventArgs e)
        {
            if (_vm.CanUndo) { _vm.Undo(); RefreshLists(); UpdateInspector(); }
        }

        private void OnRedo(object sender, RoutedEventArgs e)
        {
            if (_vm.CanRedo) { _vm.Redo(); RefreshLists(); UpdateInspector(); }
        }

        // ----------------------------------------------------------------
        // Keyboard
        // ----------------------------------------------------------------
        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Undo/redo always wins regardless of draft state.
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (e.Key == Key.Z) { if (_vm.CanUndo) { _vm.Undo(); RefreshLists(); UpdateInspector(); } e.Handled = true; return; }
                if (e.Key == Key.Y || (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift))
                { if (_vm.CanRedo) { _vm.Redo(); RefreshLists(); UpdateInspector(); } e.Handled = true; return; }
            }
            // Lane hotkeys: D/F/J/K -> lane + make slice.
            if (e.Key == Key.D) { CreateSliceFromDraft(0); e.Handled = true; }
            else if (e.Key == Key.F) { CreateSliceFromDraft(1); e.Handled = true; }
            else if (e.Key == Key.J) { CreateSliceFromDraft(2); e.Handled = true; }
            else if (e.Key == Key.K) { CreateSliceFromDraft(3); e.Handled = true; }
            else if (e.Key == Key.Enter)
            {
                if (_vm.Suggestions.Count > 0 && _vm.SuggestionIndex >= 0)
                    OnAccept(null, null);
                else
                    CreateSliceFromDraft(-1);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (_vm.HasDraft) { _vm.ClearDraft(); UpdateDraftInfo(); }
                else try { _auditionPlayer?.StopAllSounds(); } catch { }
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                var slice = SliceList.SelectedItem as KeysoundSlice ?? _vm.SelectedSlice;
                if (slice != null) { _vm.DeleteSlice(slice); RefreshLists(); UpdateInspector(); }
                e.Handled = true;
            }
            else if (e.Key == Key.OemOpenBrackets) // [
            {
                if (_vm.Suggestions.Count > 0)
                {
                    _vm.SuggestionIndex = Math.Max(0, _vm.SuggestionIndex - 1);
                    SuggestionLabel.Text = string.Format("Suggestion {0}/{1}", _vm.SuggestionIndex + 1, _vm.Suggestions.Count);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.OemCloseBrackets) // ]
            {
                if (_vm.Suggestions.Count > 0)
                {
                    _vm.SuggestionIndex = Math.Min(_vm.Suggestions.Count - 1, _vm.SuggestionIndex + 1);
                    SuggestionLabel.Text = string.Format("Suggestion {0}/{1}", _vm.SuggestionIndex + 1, _vm.Suggestions.Count);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Space)
            {
                if (_vm.HasDraft) _ = AuditionAtAsync(Math.Min(_vm.DraftStartMs, _vm.DraftEndMs), Math.Abs(_vm.DraftEndMs - _vm.DraftStartMs));
                else if (_vm.SelectedSlice != null) _ = AuditionSliceAsync(_vm.SelectedSlice);
                else _ = AuditionAtAsync(_vm.PlayheadMs, 800);
                e.Handled = true;
            }
            else if (e.Key == Key.P)
            {
                OnPlayDraft(null, null);
                e.Handled = true;
            }
        }
    }

}
