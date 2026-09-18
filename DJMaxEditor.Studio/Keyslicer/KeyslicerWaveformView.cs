using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DJMaxEditor.Studio.Keyslicer
{
    /// <summary>
    /// Waveform surface for the keyslicer scene.
    /// Draws peak data as a filled waveform, overlays confirmed slices (lane-colored),
    /// suggestions, draft selection (yellow), and the playhead. Mouse interaction
    /// drives selection, slice moving/resizing, and playhead seeking.
    /// Unlike StudioVerticalCanvas, this is a horizontal, time-domain view of a
    /// single source recording - no tempo map, just milliseconds.
    /// </summary>
    public sealed class KeyslicerWaveformView : FrameworkElement
    {
        // Dependency: view model.
        private KeyslicerViewModel _viewModel;

        // Interaction state.
        private bool _draggingSelection;
        private Point _dragStartPoint;
        private double _dragStartMs;
        private KeysoundSlice _hoverSlice;
        private KeysoundSlice _dragSlice;
        private double _dragSliceOrigStart;
        private double _dragSliceOrigEnd;
        private bool _draggingSliceBody;
        private bool _draggingSliceEdge; // which edge: -1 = left, 1 = right
        private int _dragEdge;

        // Caches.
        private Typeface _typeface;
        private readonly Pen _zeroPen;
        private readonly Pen _gridPen;
        private readonly Pen _playheadPen;

        public KeyslicerViewModel ViewModel
        {
            get => _viewModel;
            set
            {
                if (_viewModel == value) return;
                if (_viewModel != null)
                {
                    _viewModel.WaveformUpdated -= OnWaveformUpdated;
                    _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                    _viewModel.SlicesChanged -= OnSlicesChanged;
                }
                _viewModel = value;
                if (_viewModel != null)
                {
                    _viewModel.WaveformUpdated += OnWaveformUpdated;
                    _viewModel.PropertyChanged += OnViewModelPropertyChanged;
                    _viewModel.SlicesChanged += OnSlicesChanged;
                }
                InvalidateVisual();
            }
        }

        public event EventHandler<double> PlayheadSeekRequested;
        public event EventHandler<KeysoundSlice> SliceClicked;
        public event EventHandler SelectionCompleted;

        public KeyslicerWaveformView()
        {
            ClipToBounds = true;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            Focusable = true;

            _typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            _zeroPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1);
            _zeroPen.Freeze();
            _gridPen = new Pen(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)), 1);
            _gridPen.DashStyle = DashStyles.Dash;
            _gridPen.Freeze();
            _playheadPen = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)), 1.5);
            _playheadPen.Freeze();

            // Mouse handling.
            MouseLeftButtonDown += OnMouseLeftDown;
            MouseLeftButtonUp += OnMouseLeftUp;
            MouseMove += OnMouseMove;
            MouseWheel += OnMouseWheel;
        }

        private void OnWaveformUpdated(object s, EventArgs e) => InvalidateVisual();
        private void OnViewModelPropertyChanged(object s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(KeyslicerViewModel.ScrollMs) ||
                e.PropertyName == nameof(KeyslicerViewModel.Zoom) ||
                e.PropertyName == nameof(KeyslicerViewModel.PlayheadMs) ||
                e.PropertyName == nameof(KeyslicerViewModel.DraftStartMs) ||
                e.PropertyName == nameof(KeyslicerViewModel.DraftEndMs) ||
                e.PropertyName == nameof(KeyslicerViewModel.SelectedSlice))
                InvalidateVisual();
        }
        private void OnSlicesChanged(object s, EventArgs e) => InvalidateVisual();

        // ----------------------------------------------------------------
        // Coordinate mapping
        // ----------------------------------------------------------------
        private double MsToX(double ms)
        {
            if (_viewModel == null || _viewModel.Waveform == null) return 0;
            double vis = _viewModel.VisibleDurationMs;
            if (vis <= 0) return 0;
            double scroll = _viewModel.ScrollMs;
            double frac = (ms - scroll) / vis;
            return frac * ActualWidth;
        }

        private double XToMs(double x)
        {
            if (_viewModel == null || _viewModel.Waveform == null) return 0;
            double vis = _viewModel.VisibleDurationMs;
            double scroll = _viewModel.ScrollMs;
            double frac = x / Math.Max(1, ActualWidth);
            return scroll + frac * vis;
        }

        private Rect SliceToRect(KeysoundSlice s, double y, double h)
        {
            double x0 = MsToX(s.StartMs);
            double x1 = MsToX(s.EndMs);
            if (x1 < x0) { double t = x0; x0 = x1; x1 = t; }
            x0 = Math.Max(0, Math.Min(ActualWidth, x0));
            x1 = Math.Max(0, Math.Min(ActualWidth, x1));
            return new Rect(x0, y, Math.Max(2, x1 - x0), h);
        }

        // ----------------------------------------------------------------
        // Rendering
        // ----------------------------------------------------------------
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            // Background.
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x12)), null, new Rect(0, 0, w, h));

            if (_viewModel == null || _viewModel.Waveform == null || _viewModel.Waveform.Peaks.Length == 0)
            {
                var hint = new FormattedText(
                    "Load a source recording to begin - Import audio, then drag on the waveform to create slices.",
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, 11,
                    new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x92)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(hint, new Point(14, h * 0.5 - 8));
                DrawRuler(dc, w, h); // still draw ruler so layout doesn't jump
                return;
            }

            double scroll = _viewModel.ScrollMs;
            double vis = _viewModel.VisibleDurationMs;
            double dur = _viewModel.Waveform.DurationMs;

            // Grid lines: every 1s / 5s depending on zoom.
            DrawGrid(dc, w, h, scroll, vis);

            // Waveform area: 60% of height centered vertically, with top/bottom padding for slices.
            double waveTop = 28;
            double waveH = h - 28 - 22; // top header + bottom ruler
            if (waveH < 40) waveH = h * 0.55;
            double waveMid = waveTop + waveH * 0.5;

            // Draw waveform peaks mapped to visible window.
            DrawWaveform(dc, w, waveTop, waveH, waveMid, scroll, vis);

            // Slices lane.
            double sliceH = 18;
            double sliceY = 4;
            DrawSlices(dc, w, sliceY, sliceH);

            // Draft selection (yellow) across the waveform band.
            DrawDraft(dc, w, waveTop, waveH);

            // Suggestions (ghost).
            DrawSuggestions(dc, w, waveTop, waveH);

            // Playhead.
            DrawPlayhead(dc, w, waveTop, waveH);

            // Ruler + time labels.
            DrawRuler(dc, w, h);

            // Slice labels inline on waveform where wide enough.
            DrawSliceLabels(dc, w, waveTop, waveH);

            // Info overlay for hover.
            if (_hoverSlice != null)
                DrawHoverTip(dc, _hoverSlice, w, h);
        }

        private void DrawGrid(DrawingContext dc, double w, double h, double scroll, double vis)
        {
            // Choose step so grid spacing is ~80-160 px.
            double pxPerMs = w / Math.Max(1, vis);
            double[] candidates = { 100, 250, 500, 1000, 2000, 5000, 10000, 30000, 60000 };
            double stepMs = 1000;
            foreach (double c in candidates)
            {
                if (c * pxPerMs >= 64) { stepMs = c; break; }
                stepMs = c;
            }
            // Minor ticks are step/4 when step >= 1000.
            double minorStep = stepMs >= 1000 ? stepMs / 4 : stepMs;

            double startMs = Math.Floor(scroll / stepMs) * stepMs;
            double endMs = scroll + vis;

            for (double ms = startMs; ms <= endMs + 0.01; ms += minorStep)
            {
                if (ms < scroll - 1) continue;
                if (ms > endMs + 1) break;
                double x = MsToX(ms);
                bool major = Math.Abs(ms % stepMs) < 0.5;
                if (major)
                    dc.DrawLine(_gridPen, new Point(x, 26), new Point(x, h - 22));
                else
                {
                    var pen = new Pen(new SolidColorBrush(Color.FromArgb(12, 255, 255, 255)), 1);
                    pen.Freeze();
                    dc.DrawLine(pen, new Point(x, 26), new Point(x, h - 22));
                }
            }
        }

        private void DrawWaveform(DrawingContext dc, double w, double top, double h, double mid, double scroll, double vis)
        {
            var data = _viewModel.Waveform;
            if (data == null) return;

            // Background for waveform band.
            var bg = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1E));
            bg.Freeze();
            dc.DrawRectangle(bg, null, new Rect(0, top, w, h));
            dc.DrawLine(_zeroPen, new Point(0, mid), new Point(w, mid));

            // Determine peak indices covering visible window.
            double dur = data.DurationMs;
            if (dur <= 0) return;
            double pxPerMs = data.Peaks.Length / dur;
            int i0 = (int)Math.Floor(scroll * pxPerMs);
            int i1 = (int)Math.Ceiling((scroll + vis) * pxPerMs);
            i0 = Math.Max(0, Math.Min(data.Peaks.Length - 1, i0));
            i1 = Math.Max(i0 + 1, Math.Min(data.Peaks.Length, i1));
            int visiblePeaks = Math.Max(1, i1 - i0);

            // Draw per-pixel line: map peaks to screen X. When zoomed in far, we have more
            // screen pixels than peaks in the window -> stretch. When zoomed out, average.
            // Simple O(w) fill.
            double halfH = h * 0.46;
            var waveformBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x9B, 0xFF));
            waveformBrush.Freeze();
            var fillBrush = new SolidColorBrush(Color.FromArgb(80, 0x6A, 0x9B, 0xFF));
            fillBrush.Freeze();
            var pen = new Pen(waveformBrush, 1);
            pen.Freeze();

            // For each screen column, sample peaks that map to it.
            double peaksPerColumn = (double)visiblePeaks / Math.Max(1, w);
            StreamGeometry geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                bool first = true;
                for (int px = 0; px < (int)w; px++)
                {
                    double ms = XToMs(px);
                    int idx = (int)(ms * pxPerMs);
                    if (idx < 0) idx = 0;
                    if (idx >= data.Peaks.Length) idx = data.Peaks.Length - 1;
                    // When one column covers multiple peaks, take min/max over them.
                    int span = Math.Max(1, (int)Math.Ceiling(peaksPerColumn));
                    float min = float.MaxValue, max = float.MinValue;
                    for (int k = 0; k < span && idx + k < data.Peaks.Length; k++)
                    {
                        var pk = data.Peaks[idx + k];
                        if (pk.Min < min) min = pk.Min;
                        if (pk.Max > max) max = pk.Max;
                    }
                    if (min == float.MaxValue) min = 0;
                    if (max == float.MinValue) max = 0;
                    double y0 = mid - max * halfH;
                    double y1 = mid - min * halfH;
                    // Build twin polylines: top goes via max, bottom via min, fill between.
                    // Simpler: draw vertical line per column.
                    double x = px + 0.5;
                    dc.DrawLine(pen, new Point(x, y0), new Point(x, y1));
                    // Also add to fill geom? For performance we just use lines.
                    if (first) { first = false; }
                }
            }

            // Border of waveform band.
            var borderPen = new Pen(new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), 1);
            borderPen.Freeze();
            dc.DrawRectangle(null, borderPen, new Rect(0, top, w, h));
        }

        private void DrawSlices(DrawingContext dc, double w, double y, double h)
        {
            if (_viewModel == null) return;
            var slices = _viewModel.Slices;
            if (slices == null || slices.Count == 0) return;

            foreach (var s in slices)
            {
                Rect r = SliceToRect(s, y, h);
                if (r.Width < 1) continue;
                // Color by lane, or fallback.
                Color col = LaneColor(s.Lane);
                bool isSelected = _viewModel.SelectedSlice == s;
                byte alpha = isSelected ? (byte)210 : (byte)150;
                var fill = new SolidColorBrush(Color.FromArgb(alpha, col.R, col.G, col.B));
                fill.Freeze();
                var stroke = new Pen(new SolidColorBrush(Color.FromArgb(230, col.R, col.G, col.B)), isSelected ? 1.5 : 1);
                stroke.Freeze();

                // Rounded rect.
                double radius = 3;
                var rect = new Rect(r.X + 0.5, r.Y + 0.5, Math.Max(0, r.Width - 1), r.Height - 1);
                dc.DrawRoundedRectangle(fill, stroke, rect, radius, radius);

                // Edge handles visual (thin).
                if (isSelected && r.Width > 22)
                {
                    var handleBrush = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255));
                    handleBrush.Freeze();
                    dc.DrawRectangle(handleBrush, null, new Rect(r.X + 1, r.Y + 3, 3, r.Height - 6));
                    dc.DrawRectangle(handleBrush, null, new Rect(r.Right - 4, r.Y + 3, 3, r.Height - 6));
                }

                // Label inside if wide enough.
                if (r.Width > 28)
                {
                    string label = string.IsNullOrEmpty(s.Label) ? s.Id : s.Label;
                    var tf = new FormattedText(
                        label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        _typeface, 9, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    tf.MaxTextWidth = r.Width - 8;
                    tf.Trimming = TextTrimming.CharacterEllipsis;
                    double tx = r.X + 4;
                    double ty = r.Y + (h - tf.Height) * 0.5;
                    dc.DrawText(tf, new Point(tx, ty));
                }
            }
        }

        private void DrawDraft(DrawingContext dc, double w, double top, double h)
        {
            if (_viewModel == null || !_viewModel.HasDraft) return;
            double x0 = MsToX(_viewModel.DraftStartMs);
            double x1 = MsToX(_viewModel.DraftEndMs);
            double l = Math.Min(x0, x1), r = Math.Max(x0, x1);
            l = Math.Max(0, Math.Min(w, l));
            r = Math.Max(0, Math.Min(w, r));
            if (r - l < 1) return;
            var fill = new SolidColorBrush(Color.FromArgb(110, 0xFF, 0xD0, 0x20));
            fill.Freeze();
            var stroke = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD0, 0x20)), 1) { DashStyle = DashStyles.Dash };
            stroke.Freeze();
            Rect rect = new Rect(l, top, r - l, h);
            dc.DrawRectangle(fill, stroke, rect);
            // Duration label.
            double dur = _viewModel.DraftEndMs - _viewModel.DraftStartMs;
            string txt = dur.ToString("0.#", CultureInfo.InvariantCulture) + " ms";
            var tf = new FormattedText(txt, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                _typeface, 10, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var bg = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
            bg.Freeze();
            Point p = new Point((l + r) * 0.5 - tf.Width * 0.5, top + 4);
            dc.DrawRoundedRectangle(bg, null, new Rect(p.X - 4, p.Y - 1, tf.Width + 8, tf.Height + 2), 2, 2);
            dc.DrawText(tf, p);
            // Handles.
            var handleBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD0, 0x20));
            handleBrush.Freeze();
            dc.DrawRectangle(handleBrush, null, new Rect(l - 1, top, 2, h));
            dc.DrawRectangle(handleBrush, null, new Rect(r - 1, top, 2, h));
        }

        private void DrawSuggestions(DrawingContext dc, double w, double top, double h)
        {
            if (_viewModel == null || _viewModel.Suggestions.Count == 0) return;
            foreach (var s in _viewModel.Suggestions)
            {
                double x0 = MsToX(s.StartMs), x1 = MsToX(s.EndMs);
                double l = Math.Min(x0, x1), r = Math.Max(x0, x1);
                if (r < 0 || l > w) continue;
                l = Math.Max(0, l); r = Math.Min(w, r);
                var fill = new SolidColorBrush(Color.FromArgb(38, 0x66, 0xFF, 0x99));
                fill.Freeze();
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(90, 0x66, 0xFF, 0x99)), 1) { DashStyle = DashStyles.Dot };
                pen.Freeze();
                dc.DrawRectangle(fill, pen, new Rect(l, top, Math.Max(1, r - l), h));
                // Confidence dot.
                double cx = (l + r) * 0.5;
                var dotBrush = new SolidColorBrush(Color.FromArgb((byte)(70 + s.Confidence * 140), 0x66, 0xFF, 0x99));
                dotBrush.Freeze();
                dc.DrawEllipse(dotBrush, null, new Point(cx, top + 6), 3, 3);
            }
            // Highlight current suggestion.
            if (_viewModel.SuggestionIndex >= 0 && _viewModel.SuggestionIndex < _viewModel.Suggestions.Count)
            {
                var cur = _viewModel.Suggestions[_viewModel.SuggestionIndex];
                double x0 = MsToX(cur.StartMs), x1 = MsToX(cur.EndMs);
                double l = Math.Min(x0, x1), r = Math.Max(x0, x1);
                var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x66, 0xFF, 0x99)), 2);
                pen.Freeze();
                dc.DrawRectangle(null, pen, new Rect(l, top, r - l, h));
            }
        }

        private void DrawPlayhead(DrawingContext dc, double w, double top, double h)
        {
            if (_viewModel == null) return;
            double x = MsToX(_viewModel.PlayheadMs);
            if (x < -2 || x > w + 2) return;
            dc.DrawLine(_playheadPen, new Point(x, top), new Point(x, top + h));
            // Triangle head.
            var brush = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30));
            brush.Freeze();
            Point[] pts = { new Point(x, top - 1), new Point(x - 5, top - 7), new Point(x + 5, top - 7) };
            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                ctx.BeginFigure(pts[0], true, true);
                ctx.PolyLineTo(new PointCollection(pts.Skip(1)), true, false);
            }
            geom.Freeze();
            dc.DrawGeometry(brush, null, geom);
        }

        private void DrawRuler(DrawingContext dc, double w, double h)
        {
            if (_viewModel == null || _viewModel.Waveform == null) return;
            double rulerY = h - 18;
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x22)), null, new Rect(0, rulerY, w, 18));
            var textBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x92));
            textBrush.Freeze();
            double scroll = _viewModel.ScrollMs;
            double vis = _viewModel.VisibleDurationMs;
            double dur = _viewModel.Waveform.DurationMs;
            double[] steps = { 100, 250, 500, 1000, 2000, 5000, 10000, 30000 };
            double stepMs = 1000;
            double pxPerMs = w / Math.Max(1, vis);
            foreach (double c in steps) if (c * pxPerMs >= 88) { stepMs = c; break; }
            double startMs = Math.Floor(scroll / stepMs) * stepMs;
            for (double ms = startMs; ms <= scroll + vis + 1; ms += stepMs)
            {
                double x = MsToX(ms);
                if (x < -20 || x > w + 20) continue;
                dc.DrawLine(new Pen(textBrush, 1), new Point(x, rulerY), new Point(x, rulerY + 6));
                string label = FormatTime(ms);
                var tf = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    _typeface, 9, textBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(tf, new Point(x + 3, rulerY + 2));
            }
            // Duration at right.
            string durLabel = FormatTime(dur) + " total";
            var durTf = new FormattedText(durLabel, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                _typeface, 9, textBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(durTf, new Point(w - durTf.Width - 6, rulerY + 2));
        }

        private void DrawSliceLabels(DrawingContext dc, double w, double top, double h)
        {
            // Already handled in DrawSlices for lane strip; optional overlay on waveform when zoomed far in
        }

        private void DrawHoverTip(DrawingContext dc, KeysoundSlice slice, double w, double h)
        {
            string txt = string.Format("{0}  {1:F1}-{2:F1} ms  ({3:F0} ms)  lane {4}", slice.Id, slice.StartMs, slice.EndMs, slice.DurationMs, slice.Lane);
            if (!string.IsNullOrEmpty(slice.Label)) txt += "  \"" + slice.Label + "\"";
            var tf = new FormattedText(txt, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                _typeface, 10, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double pad = 6;
            double tw = tf.Width + pad * 2;
            double th = tf.Height + 4;
            double x = Math.Max(4, Math.Min(w - tw - 4, MsToX((slice.StartMs + slice.EndMs) * 0.5) - tw * 0.5));
            double y = 28;
            var bg = new SolidColorBrush(Color.FromArgb(230, 0x1E, 0x1E, 0x26));
            bg.Freeze();
            var border = new Pen(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), 1);
            border.Freeze();
            dc.DrawRoundedRectangle(bg, border, new Rect(x, y, tw, th), 4, 4);
            dc.DrawText(tf, new Point(x + pad, y + 1));
        }

        private static Color LaneColor(int lane)
        {
            switch (lane)
            {
                case 0: return Color.FromRgb(0xFF, 0x69, 0x69);
                case 1: return Color.FromRgb(0xFF, 0xD6, 0x5A);
                case 2: return Color.FromRgb(0x66, 0xE0, 0x99);
                case 3: return Color.FromRgb(0x6A, 0x9B, 0xFF);
                default: return Color.FromRgb(0x9A, 0x9A, 0xAA);
            }
        }

        private static string FormatTime(double ms)
        {
            if (ms < 0) ms = 0;
            TimeSpan t = TimeSpan.FromMilliseconds(ms);
            if (t.TotalHours >= 1) return t.ToString(@"h\:mm\:ss\.fff", CultureInfo.InvariantCulture);
            return t.ToString(@"m\:ss\.fff", CultureInfo.InvariantCulture);
        }

        // ----------------------------------------------------------------
        // Hit testing
        // ----------------------------------------------------------------
        private KeysoundSlice HitTestSlice(Point p)
        {
            if (_viewModel == null) return null;
            double y0 = 4, h = 18;
            if (p.Y < y0 || p.Y > y0 + h) return null;
            foreach (var s in _viewModel.Slices)
            {
                Rect r = SliceToRect(s, y0, h);
                if (r.Contains(p)) return s;
            }
            return null;
        }

        private int HitTestSliceEdge(KeysoundSlice s, Point p)
        {
            Rect r = SliceToRect(s, 4, 18);
            const double edgeW = 6;
            if (Math.Abs(p.X - r.Left) <= edgeW) return -1;
            if (Math.Abs(p.X - r.Right) <= edgeW) return 1;
            return 0;
        }

        // ----------------------------------------------------------------
        // Mouse
        // ----------------------------------------------------------------
        private void OnMouseLeftDown(object sender, MouseButtonEventArgs e)
        {
            Focus();
            Point pt = e.GetPosition(this);
            if (_viewModel == null || _viewModel.Waveform == null) return;

            // Check slice hit first (selection / drag).
            var hit = HitTestSlice(pt);
            if (hit != null)
            {
                int edge = HitTestSliceEdge(hit, pt);
                if (edge != 0)
                {
                    _dragSlice = hit;
                    _draggingSliceEdge = true;
                    _dragEdge = edge;
                    _dragSliceOrigStart = hit.StartMs;
                    _dragSliceOrigEnd = hit.EndMs;
                    _dragStartPoint = pt;
                    _viewModel.SelectedSlice = hit;
                    CaptureMouse();
                    e.Handled = true;
                    return;
                }
                // Click on body: select and prepare body drag.
                _viewModel.SelectedSlice = hit;
                _dragSlice = hit;
                _draggingSliceBody = true;
                _dragSliceOrigStart = hit.StartMs;
                _dragSliceOrigEnd = hit.EndMs;
                _dragStartPoint = pt;
                SliceClicked?.Invoke(this, hit);
                CaptureMouse();
                e.Handled = true;
                return;
            }

            // Otherwise start draft selection in waveform band.
            _draggingSelection = true;
            _dragStartPoint = pt;
            _dragStartMs = XToMs(pt.X);
            _viewModel.SetDraft(_dragStartMs, _dragStartMs);
            CaptureMouse();
            e.Handled = true;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            Point pt = e.GetPosition(this);
            if (_draggingSliceEdge && _dragSlice != null)
            {
                double curMs = XToMs(pt.X);
                double startMs = _dragSliceOrigStart;
                double endMs = _dragSliceOrigEnd;
                if (_dragEdge == -1) startMs = curMs;
                else endMs = curMs;
                // Clamp and keep minimum duration.
                if (endMs - startMs < 10)
                {
                    if (_dragEdge == -1) startMs = endMs - 10;
                    else endMs = startMs + 10;
                }
                double dur = _viewModel.Waveform?.DurationMs ?? double.MaxValue;
                startMs = Math.Max(0, Math.Min(dur - 10, startMs));
                endMs = Math.Max(startMs + 10, Math.Min(dur, endMs));
                _viewModel.UpdateSlice(_dragSlice, startMs, endMs);
                e.Handled = true;
                return;
            }
            if (_draggingSliceBody && _dragSlice != null)
            {
                double deltaMs = XToMs(pt.X) - XToMs(_dragStartPoint.X);
                double newStart = _dragSliceOrigStart + deltaMs;
                double newEnd = _dragSliceOrigEnd + deltaMs;
                double dur = _viewModel.Waveform?.DurationMs ?? double.MaxValue;
                double len = newEnd - newStart;
                newStart = Math.Max(0, Math.Min(dur - len, newStart));
                newEnd = newStart + len;
                _viewModel.UpdateSlice(_dragSlice, newStart, newEnd);
                e.Handled = true;
                return;
            }
            if (_draggingSelection)
            {
                double curMs = XToMs(pt.X);
                _viewModel.SetDraft(_dragStartMs, curMs);
                // Auto-scroll when dragging near edges.
                if (pt.X < 20) _viewModel.ScrollMs -= 12;
                else if (pt.X > ActualWidth - 20) _viewModel.ScrollMs += 12;
                e.Handled = true;
                return;
            }

            // Hover.
            var hover = HitTestSlice(pt);
            if (hover != _hoverSlice)
            {
                _hoverSlice = hover;
                // Cursor feedback.
                if (hover != null)
                {
                    int edge = HitTestSliceEdge(hover, pt);
                    Cursor = edge != 0 ? Cursors.SizeWE : Cursors.Hand;
                }
                else Cursor = Cursors.IBeam;
                InvalidateVisual();
            }
            else if (hover != null)
            {
                int edge = HitTestSliceEdge(hover, pt);
                Cursor = edge != 0 ? Cursors.SizeWE : Cursors.Hand;
            }
            else Cursor = Cursors.Arrow;
        }

        private void OnMouseLeftUp(object sender, MouseButtonEventArgs e)
        {
            if (_draggingSelection)
            {
                _draggingSelection = false;
                ReleaseMouseCapture();
                if (_viewModel.HasDraft)
                    SelectionCompleted?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            if (_draggingSliceBody || _draggingSliceEdge)
            {
                _draggingSliceBody = false;
                _draggingSliceEdge = false;
                _dragSlice = null;
                ReleaseMouseCapture();
                e.Handled = true;
                return;
            }
            ReleaseMouseCapture();
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_viewModel == null) return;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
            if (ctrl || alt)
            {
                // Zoom around cursor.
                double centerMs = XToMs(e.GetPosition(this).X);
                double factor = e.Delta > 0 ? 1.2 : 1 / 1.2;
                double newZoom = _viewModel.Zoom * factor;
                // Keep centerMs stable: scroll = center - vis/2 ; vis = dur/zoom.
                double oldVis = _viewModel.VisibleDurationMs;
                _viewModel.Zoom = newZoom;
                double newVis = _viewModel.VisibleDurationMs;
                _viewModel.ScrollMs = centerMs - newVis * ((centerMs - _viewModel.ScrollMs) / oldVis);
                e.Handled = true;
            }
            else
            {
                // Scroll.
                double delta = e.Delta > 0 ? -80 : 80;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift) delta *= 4;
                _viewModel.ScrollMs += delta;
                e.Handled = true;
            }
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.ClickCount == 2 && e.ChangedButton == MouseButton.Left)
            {
                if (_viewModel == null) return;
                Point pt = e.GetPosition(this);
                var hit = HitTestSlice(pt);
                if (hit != null)
                {
                    PlayheadSeekRequested?.Invoke(this, (hit.StartMs + hit.EndMs) * 0.5);
                    _viewModel.PlayheadMs = (hit.StartMs + hit.EndMs) * 0.5;
                    SliceClicked?.Invoke(this, hit);
                    e.Handled = true;
                }
                else
                {
                    double ms = XToMs(pt.X);
                    _viewModel.PlayheadMs = ms;
                    PlayheadSeekRequested?.Invoke(this, ms);
                    e.Handled = true;
                }
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double w = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
            double h = double.IsInfinity(availableSize.Height) ? 260 : availableSize.Height;
            return new Size(w, Math.Max(140, h));
        }
    }
}
