using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace DJMaxEditor.Studio.Keyslicer
{
    public partial class BmsImportPreviewWindow : DJMaxEditor.Studio.Shell.StudioWindow
    {
        private readonly BmsImportResult _result;
        private readonly string _folder;

        public BmsImportPreviewWindow(BmsImportResult result, string folder = null)
        {
            _result = result;
            _folder = folder;
            InitializeComponent();
            Refresh();
        }

        public bool DidImport { get; private set; }

        private void Refresh()
        {
            if (_result == null || !_result.Success)
            {
                SummaryLine.Text = "Import failed: " + (_result?.Error ?? "unknown");
                return;
            }
            SummaryLine.Text = $"Parsed BMS — {(_folder != null ? System.IO.Path.GetFileName(_folder) : "clipboard")} — {(_result.Warnings.Count>0 ? "with warnings" : "clean")}";
            BpmLine.Text = $"BPM { _result.Bpm:0.###}  •  Distinct WAVs { _result.DistinctWavs}  •  Notes { _result.Notes.Count}";
            SlotsLine.Text = _result.MaxSlotsPerMeasure>0 ? $"Max slots/measure { _result.MaxSlotsPerMeasure} ({(_result.MaxSlotsPerMeasure>192?"beyond iBMSC 192th":"within 192")})" : "";
            WavCount.Text = _result.DistinctWavs + " wavs";
            NoteCount.Text = _result.Notes.Count + " notes";

            if (_result.Warnings.Count>0)
            {
                WarningsBlock.Visibility = Visibility.Visible;
                WarningsBlock.Text = "⚠ " + string.Join("\n⚠ ", _result.Warnings.Take(6));
            }
            else WarningsBlock.Visibility = Visibility.Collapsed;

            WavList.ItemsSource = _result.WavMap.OrderBy(k=>k.Key).Select(kv => $"{kv.Key:D3} ({ToBase36(kv.Key)}) → {kv.Value}").ToList();
            NoteList.ItemsSource = _result.Notes.Take(200).Select(n => $"{n.VirtualTick,6} vt  {n.ChartTimeMs,7:0} ms  ch{n.Channel}  lane{n.Lane}  WAV{n.WavId:D2} {System.IO.Path.GetFileName(n.WavName)}").ToList();
        }

        private static string ToBase36(int v)
        {
            const string chars="0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            return new string(new[]{ chars[v/36], chars[v%36]});
        }

        private void OnTitleDrag(object s, MouseButtonEventArgs e) => BeginTitleBarDrag(e);

        private void OnImport(object s, RoutedEventArgs e)
        {
            DidImport = true;
            DialogResult = true;
            Close();
        }
    }
}
