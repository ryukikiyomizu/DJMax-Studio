using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DJMaxEditor.Controls.Vertical;
using DJMaxEditor.DJMax;
using DJMaxEditor.Editor;
using DJMaxEditor.Studio.Design;

namespace DJMaxEditor.Studio.Timeline
{
    /// <summary>
    /// The note-volume lane: one horizontal bar per visible note, drawn on the same vertical tick
    /// axis as the chart canvas, editable by dragging across it.
    ///
    /// It reads the chart canvas's own <see cref="VerticalTimelineViewModel"/> rather than keeping
    /// a second projection, so the two can never disagree about where a tick is. That is also why
    /// it draws its own top strip instead of taking one from a Grid row: both elements start at the
    /// same Y and both offset their body by the model's RulerHeight, so the rows line up with no
    /// second header height for anyone to forget to update.
    ///
    /// <see cref="Orientation"/> follows the canvas through the same
    /// <see cref="TimelineSurfaceMap"/>, with one difference: the lane's map is the flipped one, so
    /// that transposed - where the value axis becomes vertical - a volume of zero is at the bottom
    /// of the lane and full scale at the top, the way every DAW draws a velocity lane. The shell
    /// docks it under the canvas instead of beside it in that orientation, which is where V6 keeps
    /// its VolumeLane, and for the same reason: a parameter lane that does not share the time axis
    /// with the notes it edits is decoration.
    /// </summary>
    internal sealed class StudioVolumeLane : FrameworkElement
    {
        /// <summary>
        /// Height of one value handle, in DIPs.
        ///
        /// A handle, not the note's full duration: volume applies at the note's onset, so a held
        /// note gets the same bar as a tap. Drawing the whole duration - which is what the first
        /// version did - turns every long note into a slab that buries the taps underneath it.
        /// </summary>
        private const double HandleHeight = 5.0;

        /// <summary>How far from a handle's centre a click still counts as grabbing it.</summary>
        private const double HandleGrab = 5.0;

        private readonly DrawingVisual _visual = new DrawingVisual();
        private readonly VisualCollection _layers;

        /// <summary>
        /// The frozen brush set this lane draws with, shared with the chart canvas so a bar here is
        /// the colour of the note it belongs to over there. Swapped by <see cref="RefreshTheme"/>;
        /// no draw path resolves a theme.
        /// </summary>
        private StudioTimelineTheme _theme = StudioTimelineTheme.Default;

        private VerticalTimelineViewModel _viewModel;
        private TextCache _labels;
        private double _textDpi;
        private TimelineOrientation _orientation = TimelineOrientation.Vertical;
        private TimelineSurfaceMap _map = TimelineSurfaceMap.VerticalIdentity;
        private double _mapWidth;
        private double _mapHeight;

        private bool _painting;
        private object _gestureKey;
        private VerticalTimelineFrame _gestureFrame;

        public StudioVolumeLane()
        {
            _layers = new VisualCollection(this);
            _layers.Add(_visual);

            // Axis-aligned bars only: aliased edges are what make a stack of thin bars legible
            // instead of a grey wash.
            RenderOptions.SetEdgeMode(_visual, EdgeMode.Aliased);

            // No brushes of its own. It used to build four here from StudioPalette directly, which
            // is why the lane ignored the chart theme; they are the theme's now (see
            // StudioTimelineTheme.VolumeBarFill for why it shares the note palette).

            ClipToBounds = true;
            SnapsToDevicePixels = true;
            Focusable = false;
        }

        /// <summary>Raised after a paint gesture changed one or more note volumes.</summary>
        public event EventHandler VolumeEdited;

        /// <summary>The brush set currently in use. Never null; the shipped palette until told.</summary>
        public StudioTimelineTheme Theme
        {
            get { return _theme; }
        }

        /// <summary>
        /// Re-resolves the frozen brush set for <paramref name="theme"/> and repaints.
        ///
        /// <para>
        /// The shell calls this on the lane and on the chart canvas together, from the same apply
        /// pass, because the two share one <see cref="StudioTimelineTheme"/> instance: a volume bar
        /// that stayed blue while its note turned white would be a second palette, silently.
        /// </para>
        /// <para>
        /// Zeroing <c>_textDpi</c> is what makes <see cref="EnsureLabels"/> rebuild its
        /// <see cref="TextCache"/> against the new theme instead of keeping FormattedText runs
        /// shaped with the old brushes - the same trick <c>InvalidateAll</c> plays on the canvas,
        /// and for the same reason: the cache is keyed by string, so it cannot notice a colour
        /// change by itself.
        /// </para>
        /// </summary>
        public void RefreshTheme(StudioChartTheme theme)
        {
            StudioTimelineTheme resolved = StudioTimelineTheme.ForTheme(theme);
            if (ReferenceEquals(_theme, resolved))
            {
                return;
            }

            _theme = resolved;
            _textDpi = 0;
            InvalidateVisual();
        }

        /// <summary>Kept in step with the chart canvas by the shell; see the class remarks.</summary>
        public TimelineOrientation Orientation
        {
            get { return _orientation; }
            set
            {
                if (_orientation == value)
                {
                    return;
                }
                _orientation = value;
                InvalidateVisual();
            }
        }

        public VerticalTimelineViewModel ViewModel
        {
            get { return _viewModel; }
            set
            {
                if (_viewModel == value)
                {
                    return;
                }
                if (_viewModel != null)
                {
                    _viewModel.RepaintRequested -= OnRepaintRequested;
                }
                _viewModel = value;
                if (_viewModel != null)
                {
                    _viewModel.RepaintRequested += OnRepaintRequested;
                }
                InvalidateVisual();
            }
        }

        protected override int VisualChildrenCount
        {
            get { return _layers.Count; }
        }

        protected override Visual GetVisualChild(int index)
        {
            return _layers[index];
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double width = double.IsInfinity(availableSize.Width) ? 92 : availableSize.Width;
            double height = double.IsInfinity(availableSize.Height) ? 480 : availableSize.Height;
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            InvalidateVisual();
            return finalSize;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            Redraw();
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            _labels = null;
            InvalidateVisual();
        }

        private void OnRepaintRequested(object sender, EventArgs e)
        {
            InvalidateVisual();
        }

        // -----------------------------------------------------------------------------------
        // Drawing
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Refreshes <see cref="_map"/> when the orientation or the element's size changed.
        ///
        /// The editing path needs the map as much as the draw pass does - a press can land before
        /// the first render of a freshly docked lane, and a resize between renders would otherwise
        /// have the pointer painting through a map built for the old size. Rebuilding is two matrix
        /// operations, so the guard is only here to keep the frozen transforms out of the
        /// mouse-move path.
        /// </summary>
        private void EnsureMap()
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (_map != null &&
                _map.Orientation == _orientation &&
                Math.Abs(_mapWidth - width) < 0.001 &&
                Math.Abs(_mapHeight - height) < 0.001)
            {
                return;
            }

            _mapWidth = width;
            _mapHeight = height;
            _map = TimelineSurfaceMap.CreateFlipped(_orientation, width, height);
        }

        private void Redraw()
        {
            EnsureMap();
            double width = _map.SurfaceWidth;
            double height = _map.SurfaceHeight;

            using (DrawingContext dc = _visual.RenderOpen())
            {
                if (width <= 0 || height <= 0)
                {
                    return;
                }

                // Pushed around the whole layer and popped outside the body, so the early returns
                // in it cannot leave the context unbalanced.
                int pushes = PushSurface(dc);
                RedrawCore(dc, width, height);
                Pop(dc, pushes);
            }
        }

        private int PushSurface(DrawingContext dc)
        {
            if (!_map.IsTransposed)
            {
                return 0;
            }
            dc.PushTransform(_map.ScreenFromSurface);
            return 1;
        }

        private int PushScreen(DrawingContext dc)
        {
            if (!_map.IsTransposed)
            {
                return 0;
            }
            dc.PushTransform(_map.SurfaceFromScreen);
            return 1;
        }

        private static void Pop(DrawingContext dc, int pushes)
        {
            for (int i = 0; i < pushes; i++)
            {
                dc.Pop();
            }
        }

        private void RedrawCore(DrawingContext dc, double width, double height)
        {
            dc.DrawRectangle(_theme.Backdrop, null, new Rect(0, 0, width, height));

            VerticalTimelineFrame frame = _viewModel == null ? null : _viewModel.LastFrame;
            double rulerHeight = frame != null ? frame.Coordinates.RulerHeight : 22;

            DrawHeader(dc, width, rulerHeight);

            if (frame == null)
            {
                return;
            }

            double bodyTop = rulerHeight;
            double bodyHeight = height - bodyTop;
            if (bodyHeight <= 0)
            {
                return;
            }

            dc.DrawRectangle(_theme.Field, null, new Rect(0, bodyTop, width, bodyHeight));

            // 1/4, 1/2 and 3/4 of full scale. Four rules is enough to read a value off the
            // lane by eye; a full ruler in 92 px is noise.
            for (int i = 1; i <= 3; i++)
            {
                double x = SnapX(width * i / 4.0);
                dc.DrawLine(_theme.VolumeScaleLine, new Point(x, bodyTop), new Point(x, height));
            }

            ChartSelectionService selection =
                _viewModel.Document == null ? null : _viewModel.Document.Selection;

            ReadOnlyCollection<VerticalPlacedItem> items = frame.Items;
            for (int i = 0; i < items.Count; i++)
            {
                VerticalPlacedItem placed = items[i];
                if (!IsEditableLane(placed.Column))
                {
                    continue;
                }

                EventData source = placed.Item == null ? null : placed.Item.SourceEvent;
                if (source == null || source.EventType != EventType.Note)
                {
                    continue;
                }

                // The onset, not the placed rect. TickToY is the same call the playhead uses,
                // so it is already direction-aware and cannot drift from the canvas.
                double onsetY = frame.Coordinates.TickToY(source.VirtualTick, frame.OriginTick);
                double top = Math.Round(onsetY - (HandleHeight / 2.0));
                double barHeight = HandleHeight;
                if (top + barHeight < bodyTop || top > height)
                {
                    continue;
                }
                if (top < bodyTop)
                {
                    barHeight -= bodyTop - top;
                    top = bodyTop;
                    if (barHeight <= 0)
                    {
                        continue;
                    }
                }

                // Vel, not Volume. EventData carries both, and only one of them is a note's
                // gain: Vel is the byte the .pt note record stores, the value the properties
                // panel edits, the one PlaySound scales the keysound by - and the one
                // ChartEditController.SetSelectionVolume writes. EventData.Volume is the payload
                // of a track-volume *event* (EventType.Volume) and on a note it is only ever the
                // constructor's 127, so drawing it would draw every bar full and never move.
                double fraction = source.Vel / (double)ChartEditController.MaxNoteVolume;
                if (fraction < 0)
                {
                    fraction = 0;
                }
                else if (fraction > 1)
                {
                    fraction = 1;
                }

                double barWidth = Math.Max(1.0, Math.Round(width * fraction));
                bool selected = selection != null && selection.Contains(source);

                dc.DrawRectangle(
                    selected ? _theme.VolumeBarSelected : _theme.VolumeBarFill,
                    barHeight >= 3 ? _theme.VolumeBarEdge : null,
                    new Rect(0, Math.Round(top), barWidth, Math.Round(barHeight)));
            }
        }

        private void DrawHeader(DrawingContext dc, double width, double rulerHeight)
        {
            dc.DrawRectangle(_theme.Chrome, null, new Rect(0, 0, width, rulerHeight));
            dc.DrawLine(_theme.ChromeEdgePen,
                new Point(0, SnapX(rulerHeight)), new Point(width, SnapX(rulerHeight)));

            EnsureLabels();
            FormattedText caption = _labels.Get("VOL");
            if (caption != null)
            {
                // Transposed the strip is the full-height gutter on the left, aligned with the
                // canvas's track-name column, so the caption centres across it and sits at the top
                // rather than staying pinned to a left edge that is now the lane's own bottom.
                Rect strip = _map.ToScreen(new Rect(0, 0, width, rulerHeight));
                Point at = _map.IsTransposed
                    ? new Point(strip.Left + Math.Max(1, (strip.Width - caption.Width) / 2), strip.Top + 4)
                    : new Point(6, Math.Max(0, (rulerHeight - caption.Height) / 2));

                int pushes = PushScreen(dc);
                dc.DrawText(caption, at);
                Pop(dc, pushes);
            }
        }

        private void EnsureLabels()
        {
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            if (_labels != null && Math.Abs(dpi - _textDpi) < 0.001)
            {
                return;
            }
            _textDpi = dpi;
            _labels = new TextCache(_theme.MonoTypeface, 9.5, _theme.TextSecondary, dpi);
        }

        // -----------------------------------------------------------------------------------
        // Editing
        // -----------------------------------------------------------------------------------

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (_viewModel == null || _viewModel.LastFrame == null)
            {
                return;
            }

            _painting = true;
            // One key for the whole drag, so the undo manager coalesces a hundred mouse-moves
            // into a single "set volume" step instead of a hundred.
            _gestureKey = new object();
            // The frame the gesture measures against, held for its whole duration. Each paint step
            // runs an edit through the undo manager, and the view model answers a mutation by
            // dropping LastFrame until the canvas rebuilds it - so a drag that read LastFrame every
            // step would be depending on a repaint landing between two mouse-moves to keep working.
            // It does today, because WPF drains Render priority ahead of Input, but nothing about
            // this lane should rest on that. The geometry cannot move mid-drag anyway: the mouse is
            // captured, so no scroll or zoom can reach the view.
            _gestureFrame = _viewModel.LastFrame;
            CaptureMouse();
            PaintAt(e.GetPosition(this));
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_painting)
            {
                PaintAt(e.GetPosition(this));
            }
        }

        /// <summary>
        /// Applies one paint step at a position in the lane's own screen space - what both mouse
        /// handlers hand it, and the one seam a headless test can drive: WPF reports the real
        /// cursor through <see cref="MouseEventArgs.GetPosition"/>, so a synthetic press cannot be
        /// raised through the handlers, and <i>where on the screen</i> a value lands is the whole
        /// of what the transposed lane changes.
        /// </summary>
        internal void PaintAt(Point screen)
        {
            if (_viewModel == null)
            {
                return;
            }
            EnsureMap();
            Paint(_map.ToSurface(screen));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!_painting)
            {
                return;
            }
            _painting = false;
            _gestureKey = null;
            _gestureFrame = null;
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }
            e.Handled = true;
        }

        /// <summary>
        /// Sets the volume of every note the pointer's row crosses from the pointer's position
        /// along the value axis.
        ///
        /// Takes a <i>surface</i> point - X is the value axis and Y the time axis whichever way the
        /// screen runs - because everything it consults (the frame's items, <c>TickToY</c>) is in
        /// surface space too. Converting once, in <see cref="PaintAt"/>, is what keeps the notes it
        /// edits the ones drawn under the pointer.
        ///
        /// The gesture retargets the selection to what it painted. That is deliberate: the
        /// volume edit runs through <see cref="ChartEditController"/>, which is selection-based
        /// so that undo, multi-note edits and the inspector all share one code path - and
        /// leaving the painted notes selected is also the feedback that tells you what you hit.
        /// </summary>
        private void Paint(Point point)
        {
            // The gesture's own frame while a drag is running; see OnMouseLeftButtonDown. Outside a
            // drag - a single programmatic step - the current frame is the only one there is.
            VerticalTimelineFrame frame = _painting && _gestureFrame != null
                ? _gestureFrame
                : _viewModel.LastFrame;
            EditorDocumentContext document = _viewModel.Document;
            if (frame == null || document == null || document.Edits == null || document.Selection == null)
            {
                return;
            }

            // The surface's own width, not ActualWidth: transposed, full scale is the lane's height.
            double width = _map.SurfaceWidth;
            if (width <= 0)
            {
                return;
            }

            double fraction = point.X / width;
            if (fraction < 0)
            {
                fraction = 0;
            }
            else if (fraction > 1)
            {
                fraction = 1;
            }
            byte value = (byte)Math.Round(fraction * ChartEditController.MaxNoteVolume);

            List<EventData> hit = null;
            ReadOnlyCollection<VerticalPlacedItem> items = frame.Items;
            for (int i = 0; i < items.Count; i++)
            {
                VerticalPlacedItem placed = items[i];
                if (!IsEditableLane(placed.Column))
                {
                    continue;
                }

                EventData source = placed.Item == null ? null : placed.Item.SourceEvent;
                if (source == null || source.EventType != EventType.Note)
                {
                    continue;
                }

                double onsetY = frame.Coordinates.TickToY(source.VirtualTick, frame.OriginTick);
                if (Math.Abs(point.Y - onsetY) > HandleGrab)
                {
                    continue;
                }

                if (hit == null)
                {
                    hit = new List<EventData>();
                }
                hit.Add(source);
            }

            if (hit == null)
            {
                return;
            }

            bool anyChanged = false;
            for (int i = 0; i < hit.Count; i++)
            {
                // Vel for the same reason the bar is drawn from it; see RedrawCore.
                if (hit[i].Vel != value)
                {
                    anyChanged = true;
                    break;
                }
            }
            if (!anyChanged)
            {
                return;
            }

            document.Selection.Replace(hit);
            if (!document.Edits.SetSelectionVolume(value, _gestureKey))
            {
                return;
            }

            InvalidateVisual();
            EventHandler handler = VolumeEdited;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Volume is only meaningful on lanes that carry keysounds the player mixes. The leading
        /// unused columns, the BGA sync lane, and TECHNIKA's end-of-scan markers have no gain of
        /// their own - a marker is a timing flag, so a volume handle on one would edit nothing.
        /// </summary>
        private static bool IsEditableLane(VerticalColumn column)
        {
            if (column == null)
            {
                return false;
            }
            return column.Kind != VerticalColumnKind.LeadingUnused &&
                   column.Kind != VerticalColumnKind.BgaSync &&
                   column.Kind != VerticalColumnKind.ScanMarker;
        }

        private static double SnapX(double value)
        {
            return Math.Round(value) + 0.5;
        }
    }
}
