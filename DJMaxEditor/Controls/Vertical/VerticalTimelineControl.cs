using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using DJMaxEditor.DJMax;
using DJMaxEditor.Editor;

namespace DJMaxEditor.Controls.Vertical
{
    /// <summary>
    /// ptSequencer-style vertical timeline surface. It is a thin renderer/input adapter
    /// over <see cref="VerticalTimelineViewModel"/>: every synchronization rule lives in
    /// that pure model, and every on-screen rectangle comes from
    /// <see cref="VerticalTimelineFrame"/>, so what is drawn and what is hit-tested cannot
    /// disagree.
    /// </summary>
    /// <remarks>
    /// It implements <see cref="IEditorSurface"/> so the shell can drive it through the
    /// same seam it already uses for V1 and V2 (see <see cref="SynchronizedEditorSurface"/>).
    /// Editing gestures stay in the V1 editor: this surface only moves the playhead and
    /// changes the shared selection, both of which are non-destructive.
    /// </remarks>
    public sealed class VerticalTimelineControl : UserControl, IEditorSurface
    {
        private readonly VerticalTimelineViewModel _model = new VerticalTimelineViewModel();
        private readonly VerticalTimelineRenderer _renderer = new VerticalTimelineRenderer();
        private readonly VerticalRenderOptions _options = new VerticalRenderOptions();
        private readonly HScrollBar _columnScroll;
        private bool _syncingColumnScroll;
        private bool _isPanning;
        private Point _lastPointer;

        public VerticalTimelineControl()
        {
            DoubleBuffered = true;
            BackColor = VerticalRenderTheme.Canvas;
            TabStop = true;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable |
                ControlStyles.UserPaint,
                true);
            _options.IsSelected = _model.IsSelected;

            // A real scrollbar, because the columns that do not fit are not decoration: an
            // 8B layout is 31 columns wide and only the first ten fit a comfortable panel,
            // so without this the SIDE R / L1 / R1 / MR / BG columns were simply unreachable.
            // Shift+wheel already panned horizontally but nothing on screen said so.
            _columnScroll = new HScrollBar
            {
                Dock = DockStyle.Bottom,
                Visible = false,
                TabStop = false,
                Minimum = 0,
                Maximum = 0,
                LargeChange = 1,
                SmallChange = 1
            };
            _columnScroll.ValueChanged += ColumnScrollValueChanged;
            Controls.Add(_columnScroll);

            _model.RepaintRequested += ModelRepaintRequested;
            _model.SeekRequested += ModelSeekRequested;
            _model.LayoutAvailabilityChanged += ModelLayoutAvailabilityChanged;
        }

        /// <summary>Raised when the user clicks the ruler to move the playhead.</summary>
        public event EventHandler<VerticalSeekEventArgs> SeekRequested;

        /// <summary>Raised when an edit made the vertical layout appear or disappear.</summary>
        public event EventHandler LayoutAvailabilityChanged;

        /// <summary>
        /// Panel width at which every gameplay column of <paramref name="mode"/> is visible
        /// at the minimum column scale, including the tick gutter.
        /// </summary>
        /// <remarks>
        /// The shell used to hard-code 320px, which is 64px short of the 8B gameplay span
        /// (618 native columns * 0.55 minimum scale + 44 gutter = 384). The columns past the
        /// right edge were silently clipped, which is exactly the "cut off on the right"
        /// report. Derived rather than hard-coded so a layout change cannot re-break it.
        /// Returns 0 for unsupported modes so callers can fall back to their own default.
        /// </remarks>
        public static int PreferredWidthForMode(int mode)
        {
            if (!VerticalTrackLayout.IsSupportedLayout(mode)) return 0;

            VerticalTrackLayout layout = VerticalTrackLayout.ForMode(mode);
            if (layout == null) return 0;

            return (int)Math.Ceiling(layout.GameplayNativeWidth * VerticalTimelineViewModel.MinColumnScale)
                + VerticalTimelineViewModel.DefaultHeaderWidth;
        }

        /// <summary>
        /// True when the layout is wider than the panel, i.e. the column scrollbar is showing
        /// and the off-screen columns are reachable.
        /// </summary>
        public bool IsColumnScrollBarVisible
        {
            get { return _columnScroll.Visible; }
        }

        internal VerticalTimelineViewModel Model
        {
            get { return _model; }
        }

        public Control View
        {
            get { return this; }
        }

        /// <summary>
        /// False by design: this surface registers no mutation, undo action, file, or audio
        /// operation. Chart edits are made on the horizontal editor and mirrored here.
        /// </summary>
        public bool SupportsEditing
        {
            get { return false; }
        }

        public EditorDocumentContext Document { get; private set; }

        public bool HasLayout
        {
            get { return _model.HasLayout; }
        }

        public int Mode
        {
            get { return _model.Mode; }
        }

        public bool FollowPlayback
        {
            get { return _model.FollowPlayback; }
            set { _model.FollowPlayback = value; }
        }

        public bool IsPlaybackActive
        {
            get { return _model.IsPlaybackActive; }
            set { _model.IsPlaybackActive = value; }
        }

        /// <summary>
        /// Which way time runs down the strip. Upward (the default) puts tick 0 at the
        /// bottom so notes travel toward the judgement line the way the game and
        /// ptSequencer draw them; Downward is the spreadsheet reading for authors who
        /// prefer it. Setting this rebuilds the coordinate system and repaints.
        /// </summary>
        public VerticalTimeDirection TimeDirection
        {
            get { return _model.TimeDirection; }
            set { _model.TimeDirection = value; }
        }

        public double LastFrameMilliseconds { get; private set; }

        public int PlayheadVirtualTick
        {
            get { return _model.PlayheadVirtualTick; }
            set { _model.PlayheadVirtualTick = value; }
        }

        public void Bind(EditorDocumentContext document)
        {
            if (document == null) throw new ArgumentNullException("document");

            Document = document;
            _options.IsReadOnly = document.Capabilities.IsReadOnly;
            _options.TicksPerMeasure = TicksPerMeasure(document);
            _model.Bind(document);
            Invalidate();
        }

        public void InvalidateView()
        {
            // Uncapped: WinForms coalesces rapid Invalidate() calls into one WM_PAINT, so
            // every playback update is offered to the surface without an artificial gate.
            Invalidate();
        }

        public bool TrySetTimeZoom(float zoom)
        {
            return _model.TrySetTimeZoom(zoom);
        }

        public EditorViewState CaptureViewState()
        {
            return new EditorViewState
            {
                PixelsPerTick = _model.ZoomFactor,
                OriginTick = _model.OriginTick,
                FirstVisibleRow = 0,
                PlayheadVirtualTick = _model.PlayheadVirtualTick
            };
        }

        public void RestoreViewState(EditorViewState state)
        {
            if (state == null) return;

            TrySetTimeZoom((float)state.PixelsPerTick);
            _model.PlayheadVirtualTick = state.PlayheadVirtualTick;
            Invalidate();
        }

        /// <summary>Renders straight to a bitmap so tests can inspect real painted pixels.</summary>
        internal Bitmap RenderSnapshot(int width, int height)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException("width");
            if (height <= 0) throw new ArgumentOutOfRangeException("height");

            var bitmap = new Bitmap(width, height);
            var stopwatch = Stopwatch.StartNew();
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                VerticalTimelineFrame frame = _model.BuildFrame(width, height);
                if (frame == null)
                {
                    graphics.Clear(VerticalRenderTheme.Canvas);
                }
                else
                {
                    _renderer.Render(graphics, frame, _options);
                }
            }
            stopwatch.Stop();
            SyncColumnScrollBar();
            LastFrameMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            return bitmap;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            var stopwatch = Stopwatch.StartNew();
            // The scrollbar is a real child window docked along the bottom, so the frame must
            // be built for the space that is left. Building it for the full client height
            // would put the last ticks and the playhead under the scrollbar.
            VerticalTimelineFrame frame = _model.BuildFrame(ClientSize.Width, CanvasHeight);
            if (frame == null)
            {
                e.Graphics.Clear(VerticalRenderTheme.Canvas);
                using (var brush = new SolidBrush(VerticalRenderTheme.MutedText))
                using (var font = new Font("Segoe UI", 8f))
                {
                    e.Graphics.DrawString(
                        Document == null
                            ? "Open a chart"
                            : "No 4B/5B/6B/8B notes",
                        font,
                        brush,
                        6,
                        6);
                }
                stopwatch.Stop();
                SyncColumnScrollBar();
                LastFrameMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                return;
            }

            _renderer.Render(e.Graphics, frame, _options);
            stopwatch.Stop();
            SyncColumnScrollBar();
            LastFrameMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        }

        /// <summary>Client height available to the timeline once the column scrollbar is subtracted.</summary>
        private int CanvasHeight
        {
            get
            {
                int height = ClientSize.Height;
                if (_columnScroll.Visible) height -= _columnScroll.Height;
                return Math.Max(1, height);
            }
        }

        /// <summary>
        /// Mirrors the model's horizontal offset onto the scrollbar after a frame is built,
        /// which is the first moment the visible native width is known.
        /// </summary>
        private void SyncColumnScrollBar()
        {
            if (IsDisposed) return;

            VerticalTrackLayout layout = _model.Layout;
            double maximum = _model.MaxOriginNativeX;
            bool needed = _model.HasLayout && layout != null && maximum > 0.5;

            _syncingColumnScroll = true;
            try
            {
                if (!needed)
                {
                    if (_columnScroll.Visible) _columnScroll.Visible = false;
                    return;
                }

                // LargeChange is the visible span, so the thumb size tells the truth about how
                // much of the layout fits, and Maximum is offset by it because WinForms caps
                // Value at Maximum - LargeChange + 1.
                int page = Math.Max(1, (int)Math.Round(layout.NativeWidth - maximum));
                int top = (int)Math.Ceiling(maximum);
                _columnScroll.LargeChange = page;
                _columnScroll.SmallChange = Math.Max(1, page / 8);
                _columnScroll.Maximum = top + page - 1;

                int value = (int)Math.Round(_model.OriginNativeX);
                if (value < 0) value = 0;
                if (value > top) value = top;
                if (_columnScroll.Value != value) _columnScroll.Value = value;
                if (!_columnScroll.Visible) _columnScroll.Visible = true;
            }
            finally
            {
                _syncingColumnScroll = false;
            }
        }

        private void ColumnScrollValueChanged(object sender, EventArgs e)
        {
            if (_syncingColumnScroll) return;
            _model.OriginNativeX = _columnScroll.Value;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();

            if (!_model.HasLayout) return;

            if (e.Button == MouseButtons.Middle)
            {
                _isPanning = true;
                _lastPointer = e.Location;
                Capture = true;
                Cursor = Cursors.SizeAll;
                return;
            }

            if (e.Button != MouseButtons.Left) return;

            if (e.X < _model.Coordinates.HeaderWidth)
            {
                // The left gutter carries the measure numbers, so it is the seek ruler
                // for a vertical timeline (time runs downward, not rightward).
                if (e.Y >= _model.Coordinates.RulerHeight)
                {
                    _model.SeekAt(e.Y);
                }
                return;
            }

            if (e.Y < _model.Coordinates.RulerHeight)
            {
                // Column-name strip: no gesture.
                return;
            }

            bool additive = (ModifierKeys & (Keys.Control | Keys.Shift)) != Keys.None;
            _model.SelectAt(e.X, e.Y, additive);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_isPanning) return;

            int deltaY = e.Y - _lastPointer.Y;
            int deltaX = e.X - _lastPointer.X;
            if (deltaY != 0) _model.ScrollByScreenDelta(deltaY);
            if (deltaX != 0) _model.ScrollByNativeX(-deltaX / Math.Max(0.01, _model.Coordinates.ColumnScale));
            _lastPointer = e.Location;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _isPanning = false;
            Capture = false;
            Cursor = Cursors.Default;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!_model.HasLayout) return;

            if ((ModifierKeys & Keys.Control) == Keys.Control)
            {
                _model.ZoomAt(e.Y, Math.Pow(1.0015, e.Delta));
            }
            else if ((ModifierKeys & Keys.Shift) == Keys.Shift)
            {
                _model.ScrollByNativeX(-e.Delta / 4.0);
            }
            else
            {
                _model.ScrollByScreenDelta(e.Delta / 2.0);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!_model.HasLayout) return;

            switch (e.KeyCode)
            {
                case Keys.Home:
                    _model.OriginTick = 0;
                    break;
                case Keys.End:
                    _model.OriginTick = _model.MaxOriginTick;
                    break;
                case Keys.Add:
                case Keys.Oemplus:
                    // Anchor on the time origin, which is the ruler edge going down and the
                    // bottom edge going up, so keyboard zoom holds the same tick either way.
                    _model.ZoomAt(_model.Coordinates.OriginY, 1.2);
                    break;
                case Keys.Subtract:
                case Keys.OemMinus:
                    _model.ZoomAt(_model.Coordinates.OriginY, 1.0 / 1.2);
                    break;
            }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Home || key == Keys.End || key == Keys.Add ||
                key == Keys.Subtract || key == Keys.Oemplus || key == Keys.OemMinus ||
                base.IsInputKey(keyData);
        }

        private static int TicksPerMeasure(EditorDocumentContext document)
        {
            if (document == null || document.Model.TickPerMinute == 0) return 0;
            return document.Model.TickPerMinute * EventData.VirtualTickSize;
        }

        private void ModelRepaintRequested(object sender, EventArgs e)
        {
            if (IsHandleCreated && !IsDisposed)
            {
                Invalidate();
            }
        }

        private void ModelSeekRequested(object sender, VerticalSeekEventArgs e)
        {
            if (SeekRequested != null)
            {
                SeekRequested(this, e);
            }
        }

        private void ModelLayoutAvailabilityChanged(object sender, EventArgs e)
        {
            if (LayoutAvailabilityChanged != null)
            {
                LayoutAvailabilityChanged(this, e);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _columnScroll.ValueChanged -= ColumnScrollValueChanged;
                _model.RepaintRequested -= ModelRepaintRequested;
                _model.SeekRequested -= ModelSeekRequested;
                _model.LayoutAvailabilityChanged -= ModelLayoutAvailabilityChanged;
                _model.Dispose();
                _renderer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
