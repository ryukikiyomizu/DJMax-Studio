using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DJMaxEditor.Studio.Keyslicer
{
    public partial class KeyslicerFinalizeWindow : DJMaxEditor.Studio.Shell.StudioWindow
    {
        private readonly KeyslicerViewModel _vm;
        private string _chosenFolder;

        public KeyslicerFinalizeWindow(KeyslicerViewModel vm, string suggestedFolder = null)
        {
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));
            _chosenFolder = suggestedFolder;
            InitializeComponent();
            Refresh();
        }

        private void Refresh()
        {
            var st = _vm.BudgetState;
            BudgetLabel.Text = st.Label;
            BudgetBadge.Text = _vm.ModeLabel;
            double fillW = st.IsUnbounded ? 120 : Math.Max(0, Math.Min(1, st.Fill)) * 120;
            BudgetFill.Width = fillW;
            BudgetFill.Background = st.FillBrush;
            BudgetHint.Text = _vm.BudgetTooltip;
            if (st.IsOver)
            {
                BudgetHint.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
                BudgetHint.Text += "  You can still export, but a BMS player will silently reuse IDs past the limit — consider RESPECT or trimming the tail.";
                OverBudgetWarning.Visibility = Visibility.Visible;
                OverBudgetWarning.Text = $"⚠ {st.Count} slices exceeds {st.Max} for {_vm.ModeLabel}. In BMS this overflows the 36-base table (and iBMSC's base62 1.5GB past 46k notes). Use Finalize anyway, but prefer RESPECT/TECHNIKA if you need them all.";
            }
            else
            {
                BudgetHint.Foreground = TryFindResource("Brush.TextMuted") as Brush ?? Brushes.Gray;
                OverBudgetWarning.Visibility = Visibility.Collapsed;
            }

            int n = _vm.Slices.Count;
            int notes = _vm.Project.Notes.Count;
            SliceSummary.Text = $"{n} slice(s), {notes} placed note(s) — IDs stay stable (ks_0001 … ks_{n:D4}) even after re-slicing.";
            SlicePreview.ItemsSource = _vm.Slices.OrderBy(s => s.StartMs).Take(80).Select(s => $"{s.Id}  {s.StartMs,7:0}→{s.EndMs,7:0} ms  lane {s.Lane}  {(string.IsNullOrEmpty(s.Label)?"":s.Label)}").ToList();
            TechNote.Visibility = notes == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (string.IsNullOrEmpty(_chosenFolder))
            {
                // Default to beside the slicer project, or temp.
                try
                {
                    string baseDir = !string.IsNullOrEmpty(_vm.ProjectPath) ? Path.GetDirectoryName(_vm.ProjectPath) : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    _chosenFolder = Path.Combine(baseDir ?? ".", (_vm.Project.Title ?? "slices") + "_finalized");
                }
                catch { _chosenFolder = Path.Combine(Path.GetTempPath(), "slices_finalized"); }
            }
            FolderBox.Text = _chosenFolder;
        }

        private void OnTitleDrag(object sender, MouseButtonEventArgs e) => BeginTitleBarDrag(e);

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose where Finalize will write the rendered slices", ShowNewFolderButton = true, SelectedPath = _chosenFolder };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _chosenFolder = dlg.SelectedPath;
                FolderBox.Text = _chosenFolder;
            }
        }

        private void OnFinalize(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_chosenFolder))
            {
                MessageBox.Show(this, "Pick an output folder.", "Finalize", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (_vm.Slices.Count == 0)
            {
                MessageBox.Show(this, "No slices to export — make a draft on the waveform first.", "Finalize", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string folder = _chosenFolder;
            bool normalize = NormalizeCheck.IsChecked == true;
            bool onlyUsed = OnlyUsedCheck.IsChecked == true;
            bool writeTech = WriteTechCheck.IsChecked == true && _vm.Project.Notes.Count > 0;
            string fmtTag = (FormatCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "wav";
            var fmt = fmtTag == "ogg" ? SliceExporter.ExportFormat.Ogg : SliceExporter.ExportFormat.Wav;

            // Persist normalize/onlyUsed back to project for next time.
            _vm.Project.Export.Normalize = normalize;
            _vm.Project.Export.ExportOnlyUsed = onlyUsed;
            _vm.Project.Export.Format = fmtTag;

            try { Directory.CreateDirectory(folder); } catch (Exception ex) { MessageBox.Show(this, "Can't create folder:\n" + ex.Message, "Finalize", MessageBoxButton.OK, MessageBoxImage.Error); return; }

            // Export slices.
            var used = onlyUsed ? new System.Collections.Generic.HashSet<string>(_vm.Project.Notes.Select(n => n.KeysoundId)) : null;
            SliceExporter.ExportResult result;
            try
            {
                result = SliceExporter.Export(_vm.Slices, folder, fmt, onlyUsed, used, normalize);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Export failed:\n" + ex.Message, "Finalize", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Optionally write tech.
            string techPath = null;
            if (writeTech)
            {
                try
                {
                    techPath = Path.Combine(folder, "track.tech");
                    // Reuse the same BuildPlayerDataForExport path: we call into the window's VM? Instead, duplicate minimal writer via the existing KeyslicerWindow helper? For now, build here.
                    var player = BuildPlayerDataForExport(result, _vm);
                    string json = DJMaxEditor.Files.Tech.TechmaniaChartSerializer.Serialize(player);
                    File.WriteAllText(techPath, json);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Slices exported, but .tech failed:\n" + ex.Message + "\n\nThe slices are still in:\n" + folder, "Finalize", MessageBoxButton.OK, MessageBoxImage.Warning);
                    DialogResult = true; Close(); return;
                }
            }

            int exported = result.Files.Count(f => !string.IsNullOrEmpty(f.FullPath));
            string msg = $"Finalized {exported} slice(s) to:\n{folder}" + (techPath != null ? $"\n\nAlso wrote chart:\n{techPath}\n\nMove the whole folder as a Technika track, or import the .tech." : "\n\nMove the folder beside your chart's keysounds, or use Send to Editor.");
            var res = MessageBox.Show(this, msg + "\n\nOpen the folder now?", "Finalize — done", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (res == MessageBoxResult.Yes)
            {
                try { Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true }); } catch { }
            }
            DialogResult = true;
            Close();
        }

        private static DJMaxEditor.DJMax.PlayerData BuildPlayerDataForExport(SliceExporter.ExportResult exp, KeyslicerViewModel vm)
        {
            var player = new DJMaxEditor.DJMax.PlayerData();
            player.Tempo = (float)vm.Bpm;
            var lanes = new System.Collections.Generic.List<DJMaxEditor.DJMax.TrackData>();
            for (int i = 0; i < 8; i++)
            {
                var t = new DJMaxEditor.DJMax.TrackData((uint)i) { TrackName = i < 4 ? "Lane " + (i + 1) : "Overflow " + (i - 3) };
                lanes.Add(t); player.Tracks.AddTrack(t);
            }
            var tempoTrack = new DJMaxEditor.DJMax.TrackData(8) { TrackName = "Tempo" };
            tempoTrack.AddEvent(new DJMaxEditor.DJMax.EventData { EventType = DJMaxEditor.DJMax.EventType.Tempo, Tempo = (float)vm.Bpm, VirtualTick = 0 });
            player.Tracks.AddTrack(tempoTrack);
            var byId = new System.Collections.Generic.Dictionary<string,string>(StringComparer.Ordinal);
            foreach (var f in exp.Files) if (!string.IsNullOrEmpty(f.SliceId) && !string.IsNullOrEmpty(f.FileName)) byId[f.SliceId] = f.FileName;
            var instrBy = new System.Collections.Generic.Dictionary<string, DJMaxEditor.DJMax.InstrumentData>(StringComparer.OrdinalIgnoreCase);
            DJMaxEditor.DJMax.InstrumentData GetInstr(string name) { if (string.IsNullOrEmpty(name)) name="none"; if (instrBy.TryGetValue(name, out var x)) return x; ushort n=(ushort)(player.Instruments.Count+1); var ins=new DJMaxEditor.DJMax.InstrumentData{InsNum=n, Name=name}; player.Instruments.Add(ins); instrBy[name]=ins; return ins; }
            foreach (var n in vm.Project.Notes.OrderBy(x=>x.ChartTimeMs))
            {
                string fn = byId.TryGetValue(n.KeysoundId, out var f) ? f : n.KeysoundId + ".wav";
                var ins = GetInstr(fn);
                int vt = n.VirtualTick; if (vt<=0) { double bpm=Math.Max(30,vm.Bpm); double msPerTick=60000.0/(bpm*48.0); vt=(int)Math.Round((n.ChartTimeMs/msPerTick)*6.0); }
                int lane=Math.Max(0,Math.Min(7,n.Lane));
                var evt=new DJMaxEditor.DJMax.EventData{EventType=DJMaxEditor.DJMax.EventType.Note, Instrument=ins, Attribute=0, VirtualTick=Math.Max(0,vt), VirtualDuration=36, Volume=127, Pan=64};
                lanes[lane].AddEvent(evt);
            }
            player.TechMetadata=null;
            return player;
        }
    }
}
