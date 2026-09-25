using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace DJMaxEditor.Studio.Keyslicer
{
    public partial class SlicerModeChooserWindow : DJMaxEditor.Studio.Shell.StudioWindow, INotifyPropertyChanged
    {
        private SlicerMode _selectedMode = SlicerMode.Bms;

        public SlicerModeChooserWindow(SlicerMode initial = SlicerMode.Bms)
        {
            InitializeComponent();
            SelectedMode = initial;
            DataContext = this;
            // sync radios
            ApplyRadio();
        }

        public SlicerMode SelectedMode
        {
            get => _selectedMode;
            set { _selectedMode = value; OnPropertyChanged(); ApplyRadio(); }
        }

        public bool SuppressFuture
        {
            get => DontShowCheck?.IsChecked == true;
            set { if (DontShowCheck != null) DontShowCheck.IsChecked = value; }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private void ApplyRadio()
        {
            if (BmsRadio == null) return;
            BmsRadio.IsChecked = _selectedMode == SlicerMode.Bms;
            RespectRadio.IsChecked = _selectedMode == SlicerMode.Respect;
            TechnikaRadio.IsChecked = _selectedMode == SlicerMode.Technika;
        }

        private void OnBmsChecked(object s, RoutedEventArgs e) => SelectedMode = SlicerMode.Bms;
        private void OnRespectChecked(object s, RoutedEventArgs e) => SelectedMode = SlicerMode.Respect;
        private void OnTechnikaChecked(object s, RoutedEventArgs e) => SelectedMode = SlicerMode.Technika;

        private void OnBmsPick(object s, MouseButtonEventArgs e) => SelectedMode = SlicerMode.Bms;
        private void OnRespectPick(object s, MouseButtonEventArgs e) => SelectedMode = SlicerMode.Respect;
        private void OnTechnikaPick(object s, MouseButtonEventArgs e) => SelectedMode = SlicerMode.Technika;

        private void OnTitleBarDrag(object sender, MouseButtonEventArgs e) => BeginTitleBarDrag(e);

        private void OnOk(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { DialogResult = false; Close(); e.Handled = true; }
            base.OnPreviewKeyDown(e);
        }

        /// <summary>Show modally with owner; returns true if user confirmed.</summary>
        public static bool TryPick(Window owner, ref SlicerMode mode, ref bool suppress)
        {
            var w = new SlicerModeChooserWindow(mode) { Owner = owner, SuppressFuture = suppress };
            bool? ok = w.ShowDialog();
            if (ok == true)
            {
                mode = w.SelectedMode;
                suppress = w.SuppressFuture;
                return true;
            }
            return false;
        }
    }
}
