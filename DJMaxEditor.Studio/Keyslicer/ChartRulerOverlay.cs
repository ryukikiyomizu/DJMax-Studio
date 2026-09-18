using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DJMaxEditor.Studio.Keyslicer
{
    /// <summary>
    /// Dual ruler drawn on top of the waveform: source time (top) + chart bars (bottom).
    /// Both share the same horizontal mapping as the waveform view (ScrollMs / Zoom).
    /// Uses the chart BPM/Offset to place bar/beat ticks so a slice made at source ms X
    /// lands on the same chart grid that the main timeline shows — the core of the
    /// "slices automatically become notes" workflow.
    /// </summary>
    public sealed class ChartRulerOverlay : FrameworkElement
    {
        private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x22)) { Opacity = 0.96 };
        private static readonly Brush Bg2 = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x2A));
        private static readonly Pen ThinEdge = new Pen(new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x3A)), 1);
        private static readonly Pen BeatPen = new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x4E)), 1);
        private static readonly Pen BarPen = new Pen(new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x82)), 1) { Thickness = 1.2 };
        private static readonly Pen PlayheadPen = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x3A)), 1.3);
        private static readonly Brush PlayheadFill = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x3A));
        private static readonly Typeface Typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        public static readonly DependencyProperty BpmProperty = DependencyProperty.Register(nameof(Bpm), typeof(double), typeof(ChartRulerOverlay), new FrameworkPropertyMetadata(140.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty OffsetMsProperty = DependencyProperty.Register(nameof(OffsetMs), typeof(double), typeof(ChartRulerOverlay), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty SnapDenominatorProperty = DependencyProperty.Register(nameof(SnapDenominator), typeof(int), typeof(ChartRulerOverlay), new FrameworkPropertyMetadata(16, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(nameof(Zoom), typeof(double), typeof(ChartRulerOverlay), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty ScrollMsProperty = DependencyProperty.Register(nameof(ScrollMs), typeof(double), typeof(ChartRulerOverlay), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty DurationMsProperty = DependencyProperty.Register(nameof(DurationMs), typeof(double), typeof(ChartRulerOverlay), new FrameworkPropertyMetadata(10000.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty ChartPlayheadMsProperty = DependencyProperty.Register(nameof(ChartPlayheadMs), typeof(double), typeof(ChartRulerOverlay), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Bpm { get => (double)GetValue(BpmProperty); set => SetValue(BpmProperty, value); }
        public double OffsetMs { get => (double)GetValue(OffsetMsProperty); set => SetValue(OffsetMsProperty, value); }
        public int SnapDenominator { get => (int)GetValue(SnapDenominatorProperty); set => SetValue(SnapDenominatorProperty, value); }
        public double Zoom { get => (double)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
        public double ScrollMs { get => (double)GetValue(ScrollMsProperty); set => SetValue(ScrollMsProperty, value); }
        public double DurationMs { get => (double)GetValue(DurationMsProperty); set => SetValue(DurationMsProperty, value); }
        public double ChartPlayheadMs { get => (double)GetValue(ChartPlayheadMsProperty); set => SetValue(ChartPlayheadMsProperty, value); }

        public ChartRulerOverlay()
        {
            Height = 44;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double w = ActualWidth;
            double h = ActualHeight;
            if (w < 4 || h < 4) return;

            // Background split
            dc.DrawRectangle(Bg, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(Bg2, null, new Rect(0, 20, w, h - 20));
            // thin separators
            dc.DrawLine(ThinEdge, new Point(0, 20), new Point(w, 20));
            dc.DrawLine(ThinEdge, new Point(0, h - 0.5), new Point(w, h - 0.5));

            double dur = DurationMs > 0 ? DurationMs : 60000;
            double zoom = Math.Max(0.25, Math.Min(32, Zoom <= 0 ? 1 : Zoom));
            double visible = dur / zoom;
            if (visible < 1) visible = dur;
            double scroll = Math.Max(0, ScrollMs);
            Func<double, double> msToX = ms => (ms - scroll) / visible * w;

            // ---- Source ruler (top 0-20): time labels every adaptive interval ----
            double visSec = visible / 1000.0;
            double stepMs = ChooseTimeStepMs(visible);
            double startMs = Math.Floor(scroll / stepMs) * stepMs;
            for (double ms = startMs; ms < scroll + visible + stepMs; ms += stepMs)
            {
                double x = msToX(ms);
                if (x < -20 || x > w + 20) continue;
                bool isLong = Math.Abs(ms % (stepMs * 5)) < 0.5 || stepMs > 5000;
                double tickH = isLong ? 8 : 5;
                dc.DrawLine(ThinEdge, new Point(x, 20 - tickH), new Point(x, 20));
                if (isLong || stepMs <= 1000)
                {
                    string label = FormatMs(ms);
                    var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface, 10, Brushes.LightGray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    dc.DrawText(ft, new Point(x + 3, 2));
                }
            }

            // ---- Chart ruler (20 - h): bar numbers + beat ticks ----
            double bpm = Math.Max(30, Bpm);
            double beatMs = 60000.0 / bpm;
            double barMs = beatMs * 4.0;
            double off = OffsetMs;
            // Find first bar in view.
            double firstBar = Math.Floor((scroll - off) / barMs);
            double endMs = scroll + visible;
            for (double bar = firstBar; bar * barMs + off < endMs + barMs; bar++)
            {
                double barStart = off + bar * barMs;
                double x = msToX(barStart);
                bool inView = x >= -30 && x <= w + 30;
                
                if (inView)
                {
                    // Bar tick taller
                    dc.DrawLine(BarPen, new Point(x, 20), new Point(x, h));
                    // Ghost extension faint into waveform area? Already drawn as full height.
                    int barNo = (int)bar + 1;
                    if (barNo >= 1 && barNo < 9999)
                    {
                        string lab = barNo.ToString(CultureInfo.InvariantCulture);
                        var ft = new FormattedText(lab, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface, 10, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                        ft.SetFontWeight(FontWeights.SemiBold);
                        dc.DrawText(ft, new Point(x + 3, 24));
                    }
                }
                // Beats within this bar (1..3) + sub-beat ghost if snap shows
                for (int b = 1; b < 4; b++)
                {
                    double beatMsPos = barStart + b * beatMs;
                    double bx = msToX(beatMsPos);
                    if (bx < -10 || bx > w + 10) continue;
                    dc.DrawLine(BeatPen, new Point(bx, 26), new Point(bx, h));
                }
                // Subdivisions for snap (ghost) — only if zoom shows them without clutter.
                if (SnapDenominator > 0 && SnapDenominator != 4 && visible < 30000)
                {
                    double stepBeat = barMs / SnapDenominator; // e.g. 16 -> quarter of beat
                    // Don't draw beat positions again.
                    for (double m = barStart + stepBeat; m < barStart + barMs - 0.5; m += stepBeat)
                    {
                        double modBeat = (m - barStart) % beatMs;
                        if (Math.Abs(modBeat) < 0.5 || Math.Abs(modBeat - beatMs) < 0.5) continue;
                        double sx = msToX(m);
                        if (sx < -5 || sx > w + 5) continue;
                        var p = new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x4A)) { Opacity = 0.55 }, 1) { DashStyle = DashStyles.Dot };
                        p.Freeze();
                        dc.DrawLine(p, new Point(sx, 30), new Point(sx, h));
                    }
                }
            }

            // ---- Playhead ----
            double px = msToX(ChartPlayheadMs);
            bool playheadInView = px >= -12 && px <= w + 12;
            if (playheadInView && DurationMs > 0)
            {
                dc.DrawLine(PlayheadPen, new Point(px, 0), new Point(px, h));
                // Triangle cap at top.
                var geom = new StreamGeometry();
                using (var ctx = geom.Open())
                {
                    ctx.BeginFigure(new Point(px - 6, 0), true, true);
                    ctx.LineTo(new Point(px + 6, 0), true, false);
                    ctx.LineTo(new Point(px, 9), true, false);
                }
                geom.Freeze();
                dc.DrawGeometry(PlayheadFill, null, geom);
                // Lower marker
                var geom2 = new StreamGeometry();
                using (var ctx =geom2.Open())
                {
                    ctx.BeginFigure(new Point(px - 5, h), true, true);
                    ctx.LineTo(new Point(px + 5, h), true, false);
                    ctx.LineTo(new Point(px, h - 7), true, false);
                }
                geom2.Freeze();
                dc.DrawGeometry(PlayheadFill, null, geom2);
            }

            // Edge vignette left/right to hint scroll
            if (scroll > 1 && w > 40)
            {
                var grad = new LinearGradientBrush();
                grad.StartPoint = new Point(0, 0.5);
                grad.EndPoint = new Point(1, 0.5);
                grad.GradientStops.Add(new GradientStop(Color.FromArgb(60, 0, 0, 0), 0));
                grad.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
                grad.Freeze();
                dc.DrawRectangle(grad, null, new Rect(0, 0, 16, h));
            }
            if (scroll + visible < dur - 1 && w > 40)
            {
                var grad = new LinearGradientBrush();
                grad.StartPoint = new Point(0, 0.5);
                grad.EndPoint = new Point(1, 0.5);
                grad.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
                grad.GradientStops.Add(new GradientStop(Color.FromArgb(60, 0, 0, 0), 1));
                grad.Freeze();
                dc.DrawRectangle(grad, null, new Rect(w - 16, 0, 16, h));
            }
        }

        private static double ChooseTimeStepMs(double visibleMs)
        {
            if (visibleMs < 800) return 100;
            if (visibleMs < 2000) return 200;
            if (visibleMs < 5000) return 500;
            if (visibleMs < 12000) return 1000;
            if (visibleMs < 30000) return 2000;
            if (visibleMs < 80000) return 5000;
            if (visibleMs < 180000) return 10000;
            return 30000;
        }

        private static string FormatMs(double ms)
        {
            if (ms < 0) ms = 0;
            double sec = ms / 1000.0;
            int m = (int)(sec / 60);
            double s = sec % 60;
            if (m > 0) return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00.0}", m, s);
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0}s", s);
        }

        protected override Size MeasureOverride(Size constraint)
        {
            return new Size(constraint.Width, 44);
        }
    }
}
