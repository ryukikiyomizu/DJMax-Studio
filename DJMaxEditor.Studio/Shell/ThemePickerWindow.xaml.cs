using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DJMaxEditor.Studio.Design;

namespace DJMaxEditor.Studio.Shell
{
    /// <summary>
    /// The chart-theme picker: every canvas palette listed with what it is for, applied on click.
    ///
    /// <para>
    /// This is the Studio-side counterpart of the legacy editor's <c>ThemePickerForm</c>, and it
    /// keeps that dialog's contract rather than reinventing one: the window is a <em>mirror</em> of
    /// the shell's state, not the owner of it. It is handed a delegate that answers "which theme is
    /// live right now", it raises <see cref="ThemeApplied"/> when the user picks a different one,
    /// and it then re-reads the delegate instead of assuming the click took. The shell owns the
    /// setting and the surfaces; a second owner would be a second thing to keep in step.
    /// </para>
    /// <para>
    /// Non-modal and single-instance, like <see cref="PreferencesWindow"/> and for the same reason:
    /// a theme is worth judging on the passage of the chart you are actually working on, and a modal
    /// dialog stops you scrolling to it.
    /// </para>
    /// <para>
    /// What it does not offer is a switch for the TECHNIKA gameplay playfield. That panel's colours
    /// are sampled from the arcade and are the point of it - recolouring them would make the
    /// preview lie about the game - so <c>TechnikaPlayfieldTheme</c> stays out of
    /// <see cref="StudioChartTheme"/> entirely, and the footer says so out loud rather than leaving
    /// a user to wonder why one panel ignored their choice.
    /// </para>
    /// </summary>
    internal sealed partial class ThemePickerWindow : StudioWindow
    {
        private readonly Func<StudioChartTheme> _getCurrentTheme;
        private List<ChartThemeRow> _rows;

        /// <summary>Designer entry point: the built-ins, nothing current, no persistence.</summary>
        public ThemePickerWindow()
            : this(null)
        {
        }

        /// <param name="getCurrentTheme">
        /// Answers which theme the shell is drawing with. Null is allowed and reads as "the shipped
        /// palette", so the window can be constructed before the settings have loaded.
        /// </param>
        public ThemePickerWindow(Func<StudioChartTheme> getCurrentTheme)
        {
            _getCurrentTheme = getCurrentTheme;

            InitializeComponent();

            _rows = new List<ChartThemeRow>();
            foreach (StudioChartTheme theme in StudioChartTheme.All)
            {
                if (theme != null)
                {
                    _rows.Add(new ChartThemeRow(theme));
                }
            }
            ThemeList.ItemsSource = _rows;

            // Markers first, selection second: assigning SelectedItem raises SelectionChanged, and
            // the handler's "is this already the current one" test is what stops opening the window
            // from re-applying and re-saving the theme it was opened with.
            RefreshCurrentMarkers();
            SelectCurrentRow();

            Loaded += OnLoaded;
        }

        /// <summary>
        /// Raised after the user picks a theme, carrying it. The shell applies it to the canvas and
        /// the volume lane and writes the choice into the settings; this window only reports.
        /// </summary>
        public event EventHandler<StudioChartTheme> ThemeApplied;

        /// <summary>How many themes the list offers.</summary>
        public int ThemeCount { get { return _rows == null ? 0 : _rows.Count; } }

        /// <summary>
        /// The theme the shell says is live, re-read on every access.
        ///
        /// A property rather than a field on purpose: after <see cref="ThemeApplied"/> the shell may
        /// have declined the change for a reason it owns, and a cached copy here would then show a
        /// dot beside a theme that is not being drawn.
        /// </summary>
        public StudioChartTheme CurrentTheme
        {
            get
            {
                if (_getCurrentTheme == null)
                {
                    return StudioChartTheme.Default;
                }
                return _getCurrentTheme() ?? StudioChartTheme.Default;
            }
        }

        /// <summary>Which row is selected, or null.</summary>
        public StudioChartTheme SelectedTheme
        {
            get
            {
                ChartThemeRow row = ThemeList.SelectedItem as ChartThemeRow;
                return row == null ? null : row.Theme;
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ThemeList.Focus();
        }

        private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ChartThemeRow row = ThemeList.SelectedItem as ChartThemeRow;
            if (row == null || row.Theme == null)
            {
                return;
            }

            if (IsSameTheme(row.Theme, CurrentTheme))
            {
                // Either the row the window opened on, or the theme that is already applied.
                return;
            }

            EventHandler<StudioChartTheme> handler = ThemeApplied;
            if (handler != null)
            {
                handler(this, row.Theme);
            }

            // Re-read rather than assume, exactly as the legacy ThemePickerForm does: the apply
            // callbacks own the editor state and this dialog only mirrors it.
            RefreshCurrentMarkers();
        }

        /// <summary>
        /// Moves the current-theme dot to wherever the shell actually is. The rows notify, so this
        /// changes one brush and rebuilds nothing.
        /// </summary>
        private void RefreshCurrentMarkers()
        {
            if (_rows == null)
            {
                return;
            }

            StudioChartTheme current = CurrentTheme;
            for (int i = 0; i < _rows.Count; i++)
            {
                _rows[i].IsCurrent = IsSameTheme(_rows[i].Theme, current);
            }
        }

        private void SelectCurrentRow()
        {
            if (_rows == null)
            {
                return;
            }

            StudioChartTheme current = CurrentTheme;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (IsSameTheme(_rows[i].Theme, current))
                {
                    ThemeList.SelectedItem = _rows[i];
                    ThemeList.ScrollIntoView(_rows[i]);
                    return;
                }
            }
        }

        /// <summary>
        /// Compared by id rather than by reference: the built-ins are singletons today so the two
        /// agree, but the whole point of <see cref="StudioChartTheme.Id"/> is that it is the
        /// identity that survives a theme being reloaded or re-declared.
        /// </summary>
        private static bool IsSameTheme(StudioChartTheme left, StudioChartTheme right)
        {
            if (left == null || right == null)
            {
                return left == right;
            }
            return string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase);
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        }

        /// <summary>
        /// The chromeless window has no OS caption to drag, so the title bar is the drag surface.
        /// Same one-line wiring as the other two windows; the actual work - including not throwing
        /// when the button is already up - is <see cref="StudioWindow.BeginTitleBarDrag"/>.
        /// </summary>
        private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            BeginTitleBarDrag(e);
        }
    }
}
