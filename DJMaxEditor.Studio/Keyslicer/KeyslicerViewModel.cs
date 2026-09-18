using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DJMaxEditor.Studio.Keyslicer
{
    internal interface IUndoAction { void Undo(KeyslicerViewModel vm); void Redo(KeyslicerViewModel vm); string Label { get; } }
    /// <summary>
    /// View model for the Keyslicer scene. Holds the project, waveform, detection,
    /// draft selection, and the chart playhead. All heavy work (peak building,
    /// detection) runs off the UI thread and marshals back via the dispatcher.
    /// </summary>
    public sealed class KeyslicerViewModel : INotifyPropertyChanged, IDisposable
    {
        private KeyslicerProject _project;
        private string _projectPath;
        private WaveformData _waveform;
        private CancellationTokenSource _waveformCts;
        private TransientDetectorSettings _detectorSettings = new TransientDetectorSettings();
        private List<SuggestedSlice> _suggestions = new List<SuggestedSlice>();
        private int _suggestionIndex = -1;

        // View state.
        private double _zoom = 1.0; // 1.0 = fit, >1 = zoom in
        private double _scrollMs = 0;
        private double _playheadMs = 0; // source playhead
        private double _chartPlayheadMs = 0; // chart playhead
        private double _draftStartMs = double.NaN;
        private double _draftEndMs = double.NaN;
        private KeysoundSlice _selectedSlice;
        private KeysoundSource _selectedSource;
        private bool _isPlayingSource;
        private string _status = "Ready";

        // Polishing state: undo + workflow prefs mirrored from Project.
        private readonly Stack<IUndoAction> _undoStack = new Stack<IUndoAction>();
        private readonly Stack<IUndoAction> _redoStack = new Stack<IUndoAction>();

        public KeyslicerViewModel()
        {
            Project = KeyslicerProject.CreateEmpty();
            Slices = new ObservableCollection<KeysoundSlice>();
            Suggestions = new ObservableCollection<SuggestedSlice>();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler WaveformUpdated;
        public event EventHandler SlicesChanged;

        public KeyslicerProject Project
        {
            get => _project;
            set
            {
                _project = value ?? KeyslicerProject.CreateEmpty();
                SyncCollectionsFromProject();
                OnPropertyChanged();
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Bpm));
                OnPropertyChanged(nameof(SongFile));
            }
        }

        public string ProjectPath
        {
            get => _projectPath;
            set { _projectPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(Title)); }
        }

        public string Title => string.IsNullOrEmpty(ProjectPath)
            ? (Project?.Title ?? "Untitled")
            : Path.GetFileNameWithoutExtension(ProjectPath) + (IsDirty ? " *": string.Empty);

        public bool IsDirty { get; private set; }

        public ObservableCollection<KeysoundSlice> Slices { get; }
        public ObservableCollection<SuggestedSlice> Suggestions { get; }

        public WaveformData Waveform
        {
            get => _waveform;
            private set { _waveform = value; OnPropertyChanged(); WaveformUpdated?.Invoke(this, EventArgs.Empty); }
        }

        public KeysoundSource SelectedSource
        {
            get => _selectedSource;
            set
            {
                if (_selectedSource == value) return;
                _selectedSource = value;
                OnPropertyChanged();
                _ = LoadWaveformAsync();
            }
        }

        public KeysoundSlice SelectedSlice
        {
            get => _selectedSlice;
            set { _selectedSlice = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasSelection)); }
        }

        public bool HasSelection => SelectedSlice != null;

        public double Zoom
        {
            get => _zoom;
            set
            {
                double v = Math.Max(0.25, Math.Min(32.0, value));
                if (Math.Abs(_zoom - v) < 0.001) return;
                _zoom = v;
                OnPropertyChanged();
            }
        }

        public double ScrollMs
        {
            get => _scrollMs;
            set
            {
                double max = Math.Max(0, (Waveform?.DurationMs ?? 0) - VisibleDurationMs);
                double v = Math.Max(0, Math.Min(max, value));
                if (Math.Abs(_scrollMs - v) < 0.1) return;
                _scrollMs = v;
                OnPropertyChanged();
            }
        }

        public double VisibleDurationMs
        {
            get
            {
                if (Waveform == null || Waveform.DurationMs <= 0) return 10000;
                // Zoom 1 = fit; zoom 4 = quarter visible.
                return Waveform.DurationMs / Zoom;
            }
        }

        public double PlayheadMs
        {
            get => _playheadMs;
            set { _playheadMs = Math.Max(0, value); OnPropertyChanged(); }
        }

        public double ChartPlayheadMs
        {
            get => _chartPlayheadMs;
            set { _chartPlayheadMs = Math.Max(0, value); OnPropertyChanged(); }
        }

        public double DraftStartMs
        {
            get => _draftStartMs;
            set { _draftStartMs = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasDraft)); }
        }

        public double DraftEndMs
        {
            get => _draftEndMs;
            set { _draftEndMs = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasDraft)); }
        }

        public bool HasDraft => !double.IsNaN(DraftStartMs) && !double.IsNaN(DraftEndMs) && DraftEndMs > DraftStartMs;

        public bool IsPlayingSource
        {
            get => _isPlayingSource;
            set { _isPlayingSource = value; OnPropertyChanged(); }
        }

        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        public double Bpm
        {
            get => Project.Bpm;
            set { Project.Bpm = Math.Max(30, Math.Min(300, value)); MarkDirty(); OnPropertyChanged(); }
        }

        public string SongFile
        {
            get => Project.SongFile;
            set { Project.SongFile = value ?? string.Empty; MarkDirty(); OnPropertyChanged(); }
        }

        public int SnapDenominator
        {
            get => Project.SnapDenominator;
            set
            {
                int v = value;
                // 0=Free, else 4,8,16,32,64,192. Clamp to known set; 0 stays 0.
                if (v != 0 && v != 4 && v != 8 && v != 16 && v != 32 && v != 64 && v != 192) v = 16;
                Project.SnapDenominator = v;
                MarkDirty();
                OnPropertyChanged();
                OnPropertyChanged(nameof(SnapLabel));
            }
        }

        public string SnapLabel => SnapDenominator == 0 ? "Free" : "1/" + SnapDenominator;

        public string AutoAdvanceMode
        {
            get => Project.AutoAdvanceMode ?? "grid";
            set
            {
                string v = string.IsNullOrEmpty(value) ? "grid" : value;
                if (v != "grid" && v != "beat" && v != "gap" && v != "off") v = "grid";
                Project.AutoAdvanceMode = v;
                MarkDirty();
                OnPropertyChanged();
            }
        }

        public bool SnapToZeroCrossing
        {
            get => Project.SnapToZeroCrossing;
            set { Project.SnapToZeroCrossing = value; MarkDirty(); OnPropertyChanged(); }
        }

        /// <summary>Called when the open chart changes: sync BPM/offset into the slicer project
        /// without marking it dirty unless the chart's tempo is meaningfully different. Best
        /// default is to follow the chart, because the chart is the source of truth for where
        /// a note at Tick X lands in milliseconds.</summary>
        public void SyncFromChart(double chartBpm, double chartOffsetMs = 0)
        {
            bool changed = false;
            if (chartBpm > 0 && Math.Abs(Project.Bpm - chartBpm) > 0.01)
            {
                Project.Bpm = Math.Max(30, Math.Min(300, chartBpm));
                changed = true;
                OnPropertyChanged(nameof(Bpm));
            }
            if (Math.Abs(Project.OffsetMs - chartOffsetMs) > 0.5)
            {
                Project.OffsetMs = chartOffsetMs;
                changed = true;
                OnPropertyChanged(nameof(OffsetMsSafe));
            }
            if (changed) { OnPropertyChanged(nameof(Title)); /* keep IsDirty false for auto-sync */ IsDirty = false; OnPropertyChanged(nameof(Title)); }
        }

        public double OffsetMsSafe => Project.OffsetMs;

        public TransientDetectorSettings DetectorSettings
        {
            get => _detectorSettings;
            set { _detectorSettings = value ?? new TransientDetectorSettings(); OnPropertyChanged(); }
        }

        public int SuggestionIndex
        {
            get => _suggestionIndex;
            set
            {
                int v = value;
                if (v < -1) v = -1;
                if (v >= Suggestions.Count) v = Suggestions.Count - 1;
                _suggestionIndex = v;
                OnPropertyChanged();
                if (_suggestionIndex >= 0 && _suggestionIndex < Suggestions.Count)
                {
                    var s = Suggestions[_suggestionIndex];
                    DraftStartMs = s.StartMs;
                    DraftEndMs = s.EndMs;
                    // Scroll to make suggestion visible.
                    EnsureMsVisible((s.StartMs + s.EndMs) * 0.5);
                }
            }
        }

        // ----------------------------------------------------------------
        // Project / source management
        // ----------------------------------------------------------------
        public void NewProject(string title = null)
        {
            Project = KeyslicerProject.CreateEmpty(title);
            ProjectPath = null;
            IsDirty = false;
            ClearWaveform();
            Suggestions.Clear();
            DraftStartMs = double.NaN;
            DraftEndMs = double.NaN;
            Status = "New project";
        }

        public bool LoadProject(string path)
        {
            try
            {
                var p = KeyslicerProject.Load(path);
                Project = p;
                ProjectPath = path;
                IsDirty = false;
                if (Project.Sources.Count > 0)
                    SelectedSource = Project.Sources[0];
                SyncCollectionsFromProject();
                Suggestions.Clear();
                DraftStartMs = double.NaN;
                DraftEndMs = double.NaN;
                Status = "Opened " + Path.GetFileName(path);
                return true;
            }
            catch (Exception ex)
            {
                Status = "Load failed: " + ex.Message;
                return false;
            }
        }

        public bool SaveProject(string path = null, bool portable = false)
        {
            string target = path ?? ProjectPath;
            if (string.IsNullOrEmpty(target)) return false;
            try
            {
                SyncProjectFromCollections();
                Project.Save(target, portable, Path.GetDirectoryName(target));
                ProjectPath = target;
                IsDirty = false;
                OnPropertyChanged(nameof(Title));
                Status = "Saved " + Path.GetFileName(target) + (portable ? " (portable)" : string.Empty);
                return true;
            }
            catch (Exception ex)
            {
                Status = "Save failed: " + ex.Message;
                return false;
            }
        }

        public void AddSource(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath)) return;
            var src = KeysoundSource.FromFile(absolutePath);
            // Probe duration.
            try
            {
                var data = WaveformPeakProvider.Build(absolutePath, 128, default);
                src.DurationMs = data.DurationMs;
                src.SampleRate = data.SampleRate;
                src.Channels = data.Channels;
            }
            catch { }
            Project.Sources.Add(src);
            MarkDirty();
            SelectedSource = src;
            Status = "Added source " + src.FileName;
        }

        public void RemoveSource(KeysoundSource src)
        {
            if (src == null) return;
            // Remove slices that depend on it (or keep but mark missing? For MVP, remove).
            var toRemove = Slices.Where(s => string.Equals(s.ResolvedSourcePath, src.ResolvedPath, StringComparison.OrdinalIgnoreCase)
                                          || string.Equals(s.SourceFile, src.FilePath, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var sl in toRemove) Slices.Remove(sl);
            Project.Sources.Remove(src);
            if (SelectedSource == src)
                SelectedSource = Project.Sources.FirstOrDefault();
            MarkDirty();
            Status = "Removed source " + src.FileName;
        }

        // ----------------------------------------------------------------
        // Slice management
        // ----------------------------------------------------------------
        public KeysoundSlice CreateSliceFromDraft(int lane = -1, string label = null)
        {
            if (!HasDraft) return null;
            if (SelectedSource == null) return null;
            double s = Math.Min(DraftStartMs, DraftEndMs);
            double e = Math.Max(DraftStartMs, DraftEndMs);
            if (e - s < 10) return null; // too short
            if (SnapToZeroCrossing) { s = FindNearestZeroCrossing(s); e = FindNearestZeroCrossing(e); if (e <= s) e = s + 10; }
            // Keep within source duration
            if (Waveform != null) { s = Math.Max(0, Math.Min(s, Waveform.DurationMs - 10)); e = Math.Max(s + 10, Math.Min(e, Waveform.DurationMs)); }

            var slice = new KeysoundSlice
            {
                Id = Project.AllocateId(),
                SourceFile = SelectedSource.FilePath,
                ResolvedSourcePath = SelectedSource.ResolvedPath,
                StartMs = s,
                EndMs = e,
                Gain = 1.0,
                FadeInMs = Project.Export.DefaultFadeInMs,
                FadeOutMs = Project.Export.DefaultFadeOutMs,
                Lane = lane,
                Label = label ?? string.Empty,
                IsConfirmed = true
            };
            Slices.Add(slice);
            PushUndo(new AddSliceUndo(slice));
            MarkDirty();
            SlicesChanged?.Invoke(this, EventArgs.Empty);
            SelectedSlice = slice;
            ClearDraft();
            Status = "Created " + slice.Id + " " + (e - s).ToString("0.#") + " ms";
            return slice;
        }

        public KeysoundSlice CreateSlice(double startMs, double endMs, int lane = -1)
        {
            if (SelectedSource == null) return null;
            var slice = new KeysoundSlice
            {
                Id = Project.AllocateId(),
                SourceFile = SelectedSource.FilePath,
                ResolvedSourcePath = SelectedSource.ResolvedPath,
                StartMs = Math.Min(startMs, endMs),
                EndMs = Math.Max(startMs, endMs),
                Gain = 1.0,
                FadeInMs = Project.Export.DefaultFadeInMs,
                FadeOutMs = Project.Export.DefaultFadeOutMs,
                Lane = lane,
                IsConfirmed = true
            };
            Slices.Add(slice);
            MarkDirty();
            SlicesChanged?.Invoke(this, EventArgs.Empty);
            return slice;
        }

        public void DeleteSlice(KeysoundSlice slice)
        {
            if (slice == null) return;
            var relatedNotes = Project.Notes.Where(n => string.Equals(n.KeysoundId, slice.Id, StringComparison.Ordinal)).ToList();
            Project.Notes.RemoveAll(n => string.Equals(n.KeysoundId, slice.Id, StringComparison.Ordinal));
            Slices.Remove(slice);
            if (SelectedSlice == slice) SelectedSlice = null;
            PushUndo(new RemoveSliceUndo(slice, relatedNotes));
            MarkDirty();
            SlicesChanged?.Invoke(this, EventArgs.Empty);
            Status = "Deleted " + slice.Id;
        }

        public void UpdateSlice(KeysoundSlice slice, double newStartMs, double newEndMs)
        {
            if (slice == null) return;
            slice.StartMs = Math.Max(0, Math.Min(newStartMs, newEndMs));
            slice.EndMs = Math.Max(slice.StartMs + 10, Math.Max(newStartMs, newEndMs));
            if (SelectedSource != null && Waveform != null)
                slice.EndMs = Math.Min(slice.EndMs, Waveform.DurationMs);
            MarkDirty();
            SlicesChanged?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged(nameof(Slices));
        }

        public void ClearDraft()
        {
            DraftStartMs = double.NaN;
            DraftEndMs = double.NaN;
        }

        public void SetDraft(double startMs, double endMs)
        {
            DraftStartMs = Math.Min(startMs, endMs);
            DraftEndMs = Math.Max(startMs, endMs);
        }

        public void EnsureMsVisible(double ms)
        {
            double vis = VisibleDurationMs;
            double lo = ScrollMs;
            double hi = ScrollMs + vis;
            if (ms < lo + vis * 0.12) ScrollMs = Math.Max(0, ms - vis * 0.45);
            else if (ms > hi - vis * 0.12) ScrollMs = ms - vis * 0.55;
        }

        // ----------------------------------------------------------------
        // Detection
        // ----------------------------------------------------------------
        public async Task DetectAsync()
        {
            if (Waveform == null) { Status = "No waveform loaded"; return; }
            Status = "Detecting onsets...";
            try
            {
                var settings = DetectorSettings;
                var data = Waveform;
                var found = await Task.Run(() => TransientDetector.Detect(data, settings));
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    Suggestions.Clear();
                    foreach (var s in found) Suggestions.Add(s);
                    _suggestions = found;
                    SuggestionIndex = found.Count > 0 ? 0 : -1;
                    Status = found.Count == 0 ? "No onsets detected (try lowering sensitivity)" : "Found " + found.Count + " candidates - review with [ / ] then Enter to accept";
                });
            }
            catch (Exception ex)
            {
                Status = "Detection failed: " + ex.Message;
            }
        }

        public void AcceptSuggestion()
        {
            if (SuggestionIndex < 0 || SuggestionIndex >= Suggestions.Count) return;
            var s = Suggestions[SuggestionIndex];
            // Use draft that was already set by SuggestionIndex setter.
            CreateSliceFromDraft();
            // Remove suggestion and advance.
            int idx = SuggestionIndex;
            Suggestions.RemoveAt(idx);
            if (Suggestions.Count == 0) { SuggestionIndex = -1; ClearDraft(); }
            else SuggestionIndex = Math.Min(idx, Suggestions.Count - 1);
        }

        public void RejectSuggestion()
        {
            if (SuggestionIndex < 0 || SuggestionIndex >= Suggestions.Count) return;
            int idx = SuggestionIndex;
            Suggestions.RemoveAt(idx);
            if (Suggestions.Count == 0) { SuggestionIndex = -1; ClearDraft(); }
            else SuggestionIndex = Math.Min(idx, Suggestions.Count - 1);
            Status = Suggestions.Count == 0 ? "No more suggestions" : (Suggestions.Count - SuggestionIndex) + " remaining";
        }

        public void AcceptAllSuggestions(int lane = -1)
        {
            var copy = Suggestions.ToList();
            int accepted = 0;
            foreach (var _ in copy)
            {
                SuggestionIndex = 0;
                CreateSliceFromDraft(lane);
                Suggestions.RemoveAt(0);
                accepted++;
            }
            SuggestionIndex = -1;
            ClearDraft();
            Status = "Accepted " + accepted + " slices";
        }

        // ----------------------------------------------------------------
        // Notes (chart placement)
        // ----------------------------------------------------------------
        public KeysoundMappedNote PlaceNoteForSlice(KeysoundSlice slice, double chartTimeMs)
        {
            if (slice == null) return null;
            if (SnapDenominator != 0) chartTimeMs = QuantizeMs(chartTimeMs);
            var note = new KeysoundMappedNote
            {
                ChartTimeMs = chartTimeMs,
                VirtualTick = MsToVirtualTick(chartTimeMs),
                Lane = slice.Lane >= 0 ? slice.Lane : 0,
                KeysoundId = slice.Id,
                VelocityGain = 1.0
            };
            Project.Notes.Add(note);
            MarkDirty();
            return note;
        }

        public KeysoundMappedNote PlaceDraftAsNote(double chartTimeMs, int lane)
        {
            var slice = CreateSliceFromDraft(lane);
            if (slice == null) return null;
            return PlaceNoteForSlice(slice, chartTimeMs);
        }

        private int MsToVirtualTick(double ms)
        {
            double bpm = Math.Max(30, Project.Bpm);
            double msPerTick = 60000.0 / (bpm * 48.0);
            // The model uses virtual ticks (6 per native tick).
            // PlayerData historically: period = 60000/(bpm*48). tick = curTick (native).
            // VirtualTick = tick * 6.
            // So ms -> native tick = ms / period, then *6.
            double native = ms / msPerTick;
            return (int)Math.Round(native * 6.0);
        }

        // ----------------------------------------------------------------
        // Waveform
        // ----------------------------------------------------------------
        private async Task LoadWaveformAsync()
        {
            _waveformCts?.Cancel();
            _waveformCts = new CancellationTokenSource();
            var token = _waveformCts.Token;
            var src = SelectedSource;
            if (src == null || string.IsNullOrEmpty(src.ResolvedPath) || !File.Exists(src.ResolvedPath))
            {
                Waveform = null;
                return;
            }

            Status = "Loading waveform...";
            try
            {
                // Width heuristic: use 1200 if zoom 1, more if zoomed? For now fixed.
                int width = 1600;
                var data = await WaveformPeakProvider.BuildAsync(src.ResolvedPath, width, token);
                if (token.IsCancellationRequested) return;
                // Marshal to UI thread.
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    if (token.IsCancellationRequested) return;
                    Waveform = data;
                    ScrollMs = 0;
                    Zoom = 1.0;
                    Status = string.Format("Waveform loaded: {0} ms, {1} kHz x{2}", (int)data.DurationMs, data.SampleRate, data.Channels);
                });
                if (Application.Current == null)
                    Waveform = data;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Status = "Waveform failed: " + ex.Message;
                Waveform = null;
            }
        }

        private void ClearWaveform()
        {
            Waveform = null;
            ScrollMs = 0;
            Zoom = 1.0;
        }

        // ----------------------------------------------------------------
        // Best-practice helpers: quantize, zero-cross, auto-advance, undo
        // ----------------------------------------------------------------
        public double QuantizeMs(double ms)
        {
            if (SnapDenominator == 0) return ms;
            double bpm = Math.Max(30, Project.Bpm);
            double beatMs = 60000.0 / bpm;
            // 1/denom per measure; 4 beats per measure
            double stepMs = beatMs * 4.0 / SnapDenominator;
            if (stepMs < 1) return ms;
            // Quantize relative to OffsetMs so bar lines stay stable.
            double off = Project.OffsetMs;
            return off + Math.Round((ms - off) / stepMs) * stepMs;
        }

        public double FindNearestZeroCrossing(double ms)
        {
            if (!SnapToZeroCrossing || Waveform == null || Waveform.ZoomSamples == null || Waveform.ZoomSamples.Length < 16)
                return ms;
            // Search in zoomSamples (mono) within +/- 2.5 ms window.
            double sr = Waveform.SampleRate > 0 ? Waveform.SampleRate : 44100;
            int win = (int)(sr * 0.0025); // 2.5 ms each side
            // Approximate sample index for ms: zoomSamples is downsampled but roughly proportional to duration
            double frac = ms / Math.Max(1, Waveform.DurationMs);
            int center = (int)(frac * Waveform.ZoomSamples.Length);
            center = Math.Max(0, Math.Min(Waveform.ZoomSamples.Length - 1, center));
            int bestIdx = center;
            double bestDist = double.MaxValue;
            int lo = Math.Max(1, center - win);
            int hi = Math.Min(Waveform.ZoomSamples.Length - 1, center + win);
            for (int i = lo; i < hi; i++)
            {
                float a = Waveform.ZoomSamples[i - 1];
                float b = Waveform.ZoomSamples[i];
                if ((a <= 0 && b >= 0) || (a >= 0 && b <= 0))
                {
                    double estMs = (i / (double)Waveform.ZoomSamples.Length) * Waveform.DurationMs;
                    double dist = Math.Abs(estMs - ms);
                    if (dist < bestDist) { bestDist = dist; bestIdx = i; }
                }
            }
            if (bestDist < 2.6) // found within window
                return (bestIdx / (double)Waveform.ZoomSamples.Length) * Waveform.DurationMs;
            return ms;
        }

        public double ComputeAutoAdvanceMs(KeysoundSlice justMade = null)
        {
            string mode = AutoAdvanceMode ?? "grid";
            if (mode == "off") return 0;
            double bpm = Math.Max(30, Project.Bpm);
            double beatMs = 60000.0 / bpm;
            if (mode == "beat") return beatMs;
            if (mode == "gap" && justMade != null)
            {
                // If suggestions remain, gap to next suggestion start; else grid.
                if (Suggestions.Count > 0 && SuggestionIndex >= 0 && SuggestionIndex < Suggestions.Count)
                {
                    double nextStart = Suggestions[SuggestionIndex].StartMs;
                    double gap = nextStart - justMade.EndMs;
                    if (gap > 20 && gap < 4000) return ChartQuantizedGap(gap, beatMs);
                }
                // Otherwise distance to next placed slice start (if working left-to-right in source)
                // fallback to grid
            }
            // grid (default best): one snap step
            if (SnapDenominator == 0) return beatMs; // free => one beat
            return beatMs * 4.0 / SnapDenominator;
        }

        private static double ChartQuantizedGap(double gap, double beatMs)
        {
            // Round gap to nearest grid-like musical value so the chart doesn't drift off-grid
            // even when the source performance was human-timed.
            // We snap the gap itself to the nearest 1/16 (at that beat).
            double step = beatMs / 4.0;
            return Math.Round(gap / step) * step;
        }

        // ---- Undo -----------------------------------------------------------------
        private void PushUndo(IUndoAction action)
        {
            _undoStack.Push(action);
            _redoStack.Clear();
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        }

        public bool CanUndo => _undoStack.Count > 0;
        public bool CanRedo => _redoStack.Count > 0;

        public void Undo()
        {
            if (!CanUndo) return;
            var a = _undoStack.Pop();
            a.Undo(this);
            _redoStack.Push(a);
            Status = "Undo " + a.Label;
            SlicesChanged?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged(nameof(CanUndo)); OnPropertyChanged(nameof(CanRedo));
            MarkDirty();
        }

        public void Redo()
        {
            if (!CanRedo) return;
            var a = _redoStack.Pop();
            a.Redo(this);
            _undoStack.Push(a);
            Status = "Redo " + a.Label;
            SlicesChanged?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged(nameof(CanUndo)); OnPropertyChanged(nameof(CanRedo));
            MarkDirty();
        }

        private sealed class AddSliceUndo : IUndoAction
        {
            private readonly KeysoundSlice _slice;
            public AddSliceUndo(KeysoundSlice s) { _slice = s; }
            public string Label => "add " + _slice.Id;
            public void Undo(KeyslicerViewModel vm) { vm.Slices.Remove(_slice); if (vm.SelectedSlice == _slice) vm.SelectedSlice = null; }
            public void Redo(KeyslicerViewModel vm) { vm.Slices.Add(_slice); vm.SelectedSlice = _slice; }
        }
        private sealed class RemoveSliceUndo : IUndoAction
        {
            private readonly KeysoundSlice _slice; private readonly List<KeysoundMappedNote> _notes;
            public RemoveSliceUndo(KeysoundSlice s, List<KeysoundMappedNote> notes) { _slice = s; _notes = notes; }
            public string Label => "remove " + _slice.Id;
            public void Undo(KeyslicerViewModel vm) { vm.Slices.Add(_slice); foreach (var n in _notes) vm.Project.Notes.Add(n); }
            public void Redo(KeyslicerViewModel vm) { vm.Slices.Remove(_slice); foreach (var n in _notes) vm.Project.Notes.Remove(n); }
        }

        public string UndoLabel => CanUndo ? _undoStack.Peek().Label : string.Empty;
        public string RedoLabel => CanRedo ? _redoStack.Peek().Label : string.Empty;

        private void SyncCollectionsFromProject()
        {
            Slices.Clear();
            if (Project.Slices != null)
                foreach (var s in Project.Slices) Slices.Add(s);
            // Sources are accessed directly via Project.Sources for ComboBox binding?
            // Ensure selected source is valid.
            if (SelectedSource != null && !Project.Sources.Contains(SelectedSource))
                SelectedSource = Project.Sources.FirstOrDefault();
            else if (SelectedSource == null && Project.Sources.Count > 0)
                SelectedSource = Project.Sources[0];
        }

        private void SyncProjectFromCollections()
        {
            Project.Slices.Clear();
            foreach (var s in Slices) Project.Slices.Add(s);
        }

        private void MarkDirty()
        {
            IsDirty = true;
            OnPropertyChanged(nameof(Title));
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public void Dispose()
        {
            _waveformCts?.Cancel();
            _waveformCts?.Dispose();
        }
    }
}
