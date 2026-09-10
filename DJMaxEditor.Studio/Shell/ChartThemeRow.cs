using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Media;
using DJMaxEditor.Studio.Design;

namespace DJMaxEditor.Studio.Shell
{
    /// <summary>
    /// One row of the chart-theme picker: what to call it, what it is for, and its own colours.
    ///
    /// <para>
    /// A view model, and a public one. The rest of this shell's types are internal, and this is the
    /// one place that would cost something to keep that way: <see cref="ThemePickerWindow"/>'s rows
    /// are drawn by an <c>ItemTemplate</c>, and WPF resolves those bindings by reflection against
    /// the item's type at runtime. A non-public type there is the shape of bug that produces empty
    /// rows rather than a compile error, so the row is public and the theme it wraps is not - the
    /// constructor and <see cref="Theme"/> stay internal, which is what keeps this from becoming a
    /// public API surface for the palette.
    /// </para>
    /// <para>
    /// It notifies on <see cref="IsCurrent"/> rather than letting the list be rebuilt: WPF has no
    /// owner-draw invalidate to call, which is what the legacy <c>ThemePickerForm</c> calls after an
    /// apply, and rebuilding the items to move one dot would throw away the selection and the scroll
    /// position with it.
    /// </para>
    /// </summary>
    public sealed class ChartThemeRow : INotifyPropertyChanged
    {
        private static readonly Brush CurrentMarker = Marker(StudioPalette.Accent);
        private static readonly Brush IdleMarker = Marker(StudioPalette.Edge);

        private bool _isCurrent;

        internal ChartThemeRow(StudioChartTheme theme)
        {
            if (theme == null)
            {
                throw new ArgumentNullException("theme");
            }

            Theme = theme;
            Name = theme.Name;
            Description = theme.Description;

            // Built once and frozen, like every other brush in the shell. Which colours these are is
            // the theme's own call (StudioChartTheme.Swatches): a lane-coloured palette shows its
            // three lane roles and its striped fields, an attribute-coloured one its note families -
            // so two rows can be compared on the thing that actually differs between them.
            IList<string> swatches = theme.Swatches();
            Brush[] brushes = new Brush[swatches.Count];
            for (int i = 0; i < swatches.Count; i++)
            {
                brushes[i] = StudioPalette.Brush(swatches[i]);
            }
            Swatches = brushes;
        }

        /// <summary>The palette this row applies. Internal: the palette is not a public model.</summary>
        internal StudioChartTheme Theme { get; private set; }

        public string Name { get; private set; }

        public string Description { get; private set; }

        /// <summary>The frozen swatch strip, bound item-by-item as each Border's background.</summary>
        public Brush[] Swatches { get; private set; }

        /// <summary>Whether the shell is drawing with this theme right now.</summary>
        public bool IsCurrent
        {
            get { return _isCurrent; }
            set
            {
                if (_isCurrent == value)
                {
                    return;
                }
                _isCurrent = value;
                OnPropertyChanged("IsCurrent");
                OnPropertyChanged("MarkerBrush");
            }
        }

        /// <summary>The dot beside the name. Lit for the theme in use, a seam colour otherwise.</summary>
        public Brush MarkerBrush
        {
            get { return _isCurrent ? CurrentMarker : IdleMarker; }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string name)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(name));
            }
        }

        private static Brush Marker(string hex)
        {
            return StudioPalette.Brush(hex);
        }
    }
}
