using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using DJMaxEditor.Controls.TimelineV2.Renderers;
using DJMaxEditor.Controls.Vertical;
using DJMaxEditor.DJMax;
using DJMaxEditor.UI;

namespace DJMaxEditor.Controls.Editor
{
    /// <summary>
    /// Clickable time ruler for the legacy horizontal surface.
    /// <para>
    /// V1 never had one: the playhead could only be moved from the transport or by playing, so
    /// "jump to bar 40 and listen" meant scrubbing the scrollbar and guessing. Timeline V2's whole
    /// ruler band is a seek target, and the playtest asked for the same thing above the timeline
    /// here. Click or drag anywhere in the band to move the playhead.
    /// </para>
    /// <para>
    /// This is a plain <see cref="Control"/> rather than part of the parent's paint, so scrubbing
    /// the ruler does not force the chart bitmap to re-render and the band can repaint on its own
    /// when only the playhead moved. Every GDI object it needs is owned for the life of the strip
    /// for the same reason <see cref="RulerRenderer"/> owns its own: a zoomed-out viewport has
    /// hundreds of marks per frame.
    /// </para>
    /// </summary>
    internal sealed class EditorTimeRuler : Control
    {
        internal const int DefaultHeight = 22;

        /// <summary>Never label closer than this, or the strings overlap into mush.</summary>
        private const int MinimumLabelSpacingPixels = 52;

        /// <summary>Below this a beat mark is noise, so only measures get drawn.</summary>
        private const float MinimumBeatSpacingPixels = 5f;

        private readonly SolidBrush _band = new SolidBrush(TimelineRenderTheme.Ruler);
        private readonly SolidBrush _text = new SolidBrush(TimelineRenderTheme.Text);
        private readonly SolidBrush _muted = new SolidBrush(TimelineRenderTheme.MutedText);
        private readonly SolidBrush _playheadFill = new SolidBrush(TimelineRenderTheme.Playhead);
        private readonly Pen _major = new Pen(TimelineRenderTheme.GridMajor);
        private readonly Pen _minor = new Pen(TimelineRenderTheme.GridMinor);
        private readonly Pen _baseline = new Pen(TimelineRenderTheme.HeaderBorder);
        private readonly Pen _playhead = new Pen(TimelineRenderTheme.Playhead);
        private readonly Pen _hover = new Pen(Color.FromArgb(120, StudioDesignSystem.Frost));
        private readonly Font _font = StudioDesignSystem.UtilityFont(8f);

        private float _zoom = 1f;
        private int _viewOriginVirtualTick;
        private int _virtualMaxTick;
        private int _ticksPerMeasure = EventData.VirtualTickSize * 192;
        private int _beatsPerMeasure = 4;
        private int _playheadVirtualTick;
        private int _hoverX = -1;
        private bool _scrubbing;
        private int _rightInset;

        /// <summary>
        /// Pixels at the right edge that are not above the chart. The band is a sibling of the
        /// scroll table rather than a row in it, so without this it would run out over the vertical
        /// scrollbar and the last measure marks would point at nothing.
        /// </summary>
        public int RightInset
        {
            get { return _rightInset; }
            set
            {
                value = Math.Max(0, value);
                if (_rightInset == value) return;
                _rightInset = value;
                Invalidate();
            }
        }

        /// <summary>Width of the part of the band that is actually above the chart.</summary>
        private int BandWidth
        {
            get { return Math.Max(1, Width - _rightInset); }
        }

        public EditorTimeRuler()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint |
                ControlStyles.ResizeRedraw,
                true);
            Dock = DockStyle.Top;
            Height = DefaultHeight;
            BackColor = TimelineRenderTheme.Ruler;
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        /// <summary>Raised while the band is clicked or dragged.</summary>
        public event EventHandler<VerticalSeekEventArgs> SeekRequested;

        /// <summary>
        /// Mirrors the parent's view state. Set as one call so a scroll never paints the band with
        /// a stale zoom, and repaint only when something actually moved - the shell drives the
        /// surface from a 16ms timer whether or not anything changed.
        /// </summary>
        /// <remarks>
        /// The meter is given as ticks per <em>measure</em>, not per beat, because that is the unit
        /// the chart formats carry and the unit Timeline V2 numbers its bars in. Taking a beat here
        /// and multiplying up numbered V1's bars four times faster than V2's for the same chart.
        /// </remarks>
        public void SetView(
            float zoom,
            int viewOriginVirtualTick,
            int virtualMaxTick,
            int ticksPerMeasure,
            int beatsPerMeasure)
        {
            if (zoom > 0f) zoom = Math.Max(0.0001f, zoom);
            if (ticksPerMeasure < 1) ticksPerMeasure = 1;
            if (beatsPerMeasure < 1) beatsPerMeasure = 1;
            if (_zoom == zoom &&
                _viewOriginVirtualTick == viewOriginVirtualTick &&
                _virtualMaxTick == virtualMaxTick &&
                _ticksPerMeasure == ticksPerMeasure &&
                _beatsPerMeasure == beatsPerMeasure)
            {
                return;
            }

            _zoom = zoom;
            _viewOriginVirtualTick = viewOriginVirtualTick;
            _virtualMaxTick = virtualMaxTick;
            _ticksPerMeasure = ticksPerMeasure;
            _beatsPerMeasure = beatsPerMeasure;
            Invalidate();
        }

        /// <summary>Moves the caret. No-ops when the tick is unchanged, as above.</summary>
        public void SetPlayhead(int virtualTick)
        {
            if (_playheadVirtualTick == virtualTick) return;
            _playheadVirtualTick = virtualTick;
            Invalidate();
        }

        /// <summary>The virtual tick a device-pixel x in this band points at; a test seam.</summary>
        internal int VirtualTickAt(int x)
        {
            if (x > BandWidth) x = BandWidth;
            int tick = _viewOriginVirtualTick + (int)Math.Round(x / _zoom);
            if (tick < 0) tick = 0;
            if (_virtualMaxTick > 0 && tick > _virtualMaxTick) tick = _virtualMaxTick;
            return tick;
        }

        /// <summary>Ticks this band currently treats as one measure; a test seam.</summary>
        internal int TicksPerMeasure
        {
            get { return _ticksPerMeasure; }
        }

        /// <summary>
        /// The 1-based bar number this band would print at a tick - the same expression
        /// <see cref="OnPaint"/> labels with, exposed so the numbering can be compared against
        /// Timeline V2's without rendering and reading pixels.
        /// </summary>
        /// <remarks>
        /// Worth a seam of its own because the two surfaces did silently disagree: this band was
        /// fed ticks-per-beat while V2 numbers by ticks-per-measure, so the same chart was labelled
        /// "bar 40" here and "bar 10" there and neither looked obviously wrong on its own.
        /// </remarks>
        internal int BarNumberAt(int virtualTick)
        {
            if (_ticksPerMeasure < 1) return 1;
            return (int)Math.Floor(virtualTick / (double)_ticksPerMeasure) + 1;
        }

        internal bool IsScrubbing
        {
            get { return _scrubbing; }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _scrubbing = true;
            Capture = true;
            RaiseSeek(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _hoverX = e.X;
            if (_scrubbing)
            {
                RaiseSeek(e.X);
            }
            else
            {
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            _scrubbing = false;
            Capture = false;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverX = -1;
            Invalidate();
        }

        private void RaiseSeek(int x)
        {
            if (SeekRequested == null) return;
            SeekRequested(this, new VerticalSeekEventArgs(VirtualTickAt(x)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;

            int width = BandWidth;
            int height = Height;
            g.FillRectangle(_band, 0, 0, Width, height);

            int measureTicks = _ticksPerMeasure;
            // Beats are a fraction of the measure rather than a whole tick count, matching
            // TimelineRulerCalculator, so a meter that does not divide evenly cannot drift the
            // downbeat away from where V2 draws it.
            double beatTicks = (double)measureTicks / _beatsPerMeasure;
            float beatPixels = (float)(beatTicks * _zoom);

            // Label every Nth measure rather than every one, so zooming out thins the labels
            // instead of overprinting them.
            int labelStride = 1;
            float measurePixels = measureTicks * _zoom;
            if (measurePixels > 0f)
            {
                while (measurePixels * labelStride < MinimumLabelSpacingPixels &&
                    labelStride < 1024)
                {
                    labelStride *= 2;
                }
            }

            bool drawBeats = beatPixels >= MinimumBeatSpacingPixels;
            int firstMeasure = _viewOriginVirtualTick / measureTicks;
            int measureHeight = Math.Max(6, height / 2);
            int beatHeight = Math.Max(4, height / 3);

            for (int measure = firstMeasure; ; measure++)
            {
                int measureTick = measure * measureTicks;
                int measureX = (int)Math.Round((measureTick - _viewOriginVirtualTick) * _zoom);
                if (measureX > width) break;

                if (measureX >= 0)
                {
                    g.DrawLine(_major, measureX, height - measureHeight, measureX, height);
                    if (measure % labelStride == 0)
                    {
                        g.DrawString(
                            (measure + 1).ToString(),
                            _font,
                            _text,
                            measureX + 3,
                            1);
                    }
                }

                if (!drawBeats) continue;
                for (int beat = 1; beat < _beatsPerMeasure; beat++)
                {
                    int beatX = (int)Math.Round(
                        ((measureTick + (beat * beatTicks)) - _viewOriginVirtualTick) * _zoom);
                    if (beatX < 0 || beatX > width) continue;
                    g.DrawLine(_minor, beatX, height - beatHeight, beatX, height);
                }
            }

            // Where the mouse would land, so a scrub target is visible before committing to it.
            if (_hoverX >= 0 && !_scrubbing)
            {
                g.DrawLine(_hover, _hoverX, 0, _hoverX, height);
            }

            int playheadX = (int)Math.Round(
                (_playheadVirtualTick - _viewOriginVirtualTick) * _zoom);
            if (playheadX >= 0 && playheadX <= width)
            {
                g.DrawLine(_playhead, playheadX, 0, playheadX, height);
                // A small flag so the playhead reads as a handle rather than another beat line.
                g.FillPolygon(_playheadFill, new[]
                {
                    new Point(playheadX - 4, 0),
                    new Point(playheadX + 4, 0),
                    new Point(playheadX, 6)
                });
            }

            g.DrawLine(_baseline, 0, height - 1, Width, height - 1);

            if (_virtualMaxTick > 0)
            {
                int endX = (int)Math.Round((_virtualMaxTick - _viewOriginVirtualTick) * _zoom);
                if (endX >= 0 && endX < width)
                {
                    // Past the end of the chart is dead space, not seekable, and saying so beats
                    // letting the ruler imply there is more song.
                    g.FillRectangle(
                        _muted, endX, height - 2, width - endX, 2);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _band.Dispose();
                _text.Dispose();
                _muted.Dispose();
                _playheadFill.Dispose();
                _major.Dispose();
                _minor.Dispose();
                _baseline.Dispose();
                _playhead.Dispose();
                _hover.Dispose();
                _font.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
