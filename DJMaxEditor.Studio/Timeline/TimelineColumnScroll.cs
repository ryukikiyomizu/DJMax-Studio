using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DJMaxEditor.Controls.Vertical;

namespace DJMaxEditor.Studio.Timeline
{
    /// <summary>
    /// Drives a <see cref="ScrollBar"/> from the timeline's column axis, so the track columns that do
    /// not fit the viewport can be reached without a middle-drag.
    ///
    /// <para>
    /// Every layout is wider than the screen it is shown on. A DJMax 8B chart is eight buttons plus
    /// SIDE L/R, the two shoulders, BGA SYNC, MR and eighteen BG columns; TECHNIKA adds its keysound
    /// overflow on top of four lanes; and a BMS chart is its keys, its turntable and one BGM column
    /// per simultaneous accompaniment voice. Column fitting only ever promised to fit the *gameplay*
    /// columns (<see cref="VerticalTrackLayout.GameplayNativeWidth"/>), which is the right default -
    /// notes are what you edit - but it left everything past them off the right edge with nothing on
    /// screen to say they were there.
    /// </para>
    /// <para>
    /// The offset, its clamp and the scrollable extent all belong to the view model
    /// (<see cref="VerticalTimelineViewModel.OriginNativeX"/> and
    /// <see cref="VerticalTimelineViewModel.MaxOriginNativeX"/>), which the middle-drag pan and the
    /// shift-wheel already move; this only mirrors them onto a control and back. Sharing the numbers
    /// rather than keeping its own is what stops the thumb from disagreeing with the canvas - a
    /// scrollbar with its own maximum would leave dead travel at one end or hide the last columns at
    /// the other.
    /// </para>
    /// </summary>
    internal sealed class TimelineColumnScroll
    {
        /// <summary>One gameplay lane, so an arrow press moves by a whole column.</summary>
        private const double ColumnStep = 60.0;

        private readonly StudioVerticalCanvas _canvas;
        private readonly ScrollBar _bar;
        private bool _syncing;

        public TimelineColumnScroll(StudioVerticalCanvas canvas, ScrollBar bar)
        {
            if (canvas == null) throw new ArgumentNullException("canvas");
            if (bar == null) throw new ArgumentNullException("bar");

            _canvas = canvas;
            _bar = bar;

            _bar.Minimum = 0;
            _bar.SmallChange = ColumnStep;
            _bar.Focusable = false;
            _bar.Visibility = Visibility.Collapsed;
            _bar.Scroll += OnBarScroll;
            _bar.ValueChanged += OnBarValueChanged;
            _canvas.FrameBuilt += OnFrameBuilt;
            ApplyOrientation(_canvas.Orientation);
        }

        /// <summary>The scrollbar being driven, for the shell to place in its grid.</summary>
        public ScrollBar Bar
        {
            get { return _bar; }
        }

        /// <summary>Whether the layout currently overflows its viewport, i.e. the bar is showing.</summary>
        public bool IsNeeded
        {
            get { return _bar.Visibility == Visibility.Visible; }
        }

        /// <summary>
        /// Puts the bar on the screen axis the columns run along: across the bottom for the
        /// ptSequencer reading, down the side for the transposed one. The surface is the same either
        /// way - the canvas reflects it across its diagonal - so what changes here is only which way
        /// the control is drawn, not what it scrolls.
        /// </summary>
        public void ApplyOrientation(TimelineOrientation orientation)
        {
            _bar.Orientation = orientation == TimelineOrientation.Horizontal
                ? System.Windows.Controls.Orientation.Vertical
                : System.Windows.Controls.Orientation.Horizontal;
        }

        /// <summary>
        /// Mirrors the model's column offset onto the bar. Called after every frame, which is the
        /// first moment the fitted scale and the visible span are both known.
        /// </summary>
        /// <remarks>
        /// Showing or hiding the bar here re-runs layout, and that is safe rather than a cycle: the
        /// bar always eats into the *time* axis (the bottom edge when lanes run across, the side when
        /// they run down), and the scrollable extent is measured along the lane axis, so a resize
        /// caused by this cannot change the answer that caused it.
        /// </remarks>
        public void Sync()
        {
            VerticalTimelineViewModel model = _canvas.ViewModel;
            double maximum = model == null ? 0 : model.MaxOriginNativeX;
            if (model == null || !model.HasLayout || maximum <= 0.5)
            {
                if (_bar.Visibility != Visibility.Collapsed)
                {
                    _bar.Visibility = Visibility.Collapsed;
                }
                return;
            }

            _syncing = true;
            try
            {
                // ViewportSize is the span that fits, so the thumb's length is the fraction of the
                // layout on screen rather than a fixed grip that says nothing about how much is left.
                double visible = Math.Max(0, model.Layout.NativeWidth - maximum);
                _bar.Maximum = maximum;
                _bar.ViewportSize = visible;
                _bar.LargeChange = Math.Max(ColumnStep, visible);
                _bar.Value = Math.Max(0, Math.Min(maximum, model.OriginNativeX));
                if (_bar.Visibility != Visibility.Visible)
                {
                    _bar.Visibility = Visibility.Visible;
                }
            }
            finally
            {
                _syncing = false;
            }
        }

        private void OnFrameBuilt(object sender, EventArgs e)
        {
            Sync();
        }

        private void OnBarValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            Push();
        }

        private void OnBarScroll(object sender, ScrollEventArgs e)
        {
            Push();
        }

        private void Push()
        {
            if (_syncing || _canvas.ViewModel == null)
            {
                return;
            }
            _canvas.ViewModel.OriginNativeX = _bar.Value;
        }
    }
}
