using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using DJMaxEditor.Controls.Editor.Renderers.Events;
using DJMaxEditor.Controls.Editor.Renderers.Zones;
using DJMaxEditor.Controls.TimelineV2.Projection;
using DJMaxEditor.Controls.TimelineV2.Renderers;
using DJMaxEditor.DJMax;
using DJMaxEditor.Editor;

namespace DJMaxEditor.Controls.TimelineV2
{
    /// <summary>
    /// The second-generation horizontal timeline. It began life as a feature-flagged read-only
    /// prototype; it now carries the same editing gesture set as V1 (select, marquee, move, draw,
    /// erase, resize) routed through the shared <see cref="ChartEditController"/>, so undo/redo,
    /// capability gating and selection are identical on both surfaces.
    /// </summary>
    public sealed partial class TimelineV2Control : UserControl, IEditorSurface
    {
        private const int DefaultRowHeight = 28;
        private const int DefaultHeaderWidth = 180;

        /// <summary>
        /// Height of the top ruler. This was 42px to make room for a second line of status text
        /// drawn into the header corner; the playtest read that truncated line ("TIM...") as a
        /// glitch, and the shell status rail already says the same thing, so the ruler is back to
        /// one row of measure labels.
        /// </summary>
        private const int DefaultRulerHeight = 24;
        private const int MinimapBucketCount = 256;
        private const double MinNoteVisualScale = 0.75;
        private const double MaxNoteVisualScale = 2.0;

        /// <summary>
        /// Follow-while-playing dead zone, in pixels. Below this the origin is left alone, so a
        /// stationary playhead cannot jitter the view; above it the origin tracks the playhead
        /// continuously. See <see cref="FollowPlayhead"/>.
        /// </summary>
        private const double FollowDeadZonePixels = 0.75;

        /// <summary>
        /// Where the playhead sits once the view is following, as a fraction of the visible tick
        /// window: a third in, so most of what is on screen is the chart still to come.
        /// </summary>
        private const double FollowAnchor = 0.33;

        private readonly TimelineRenderer _renderer = new TimelineRenderer();
        private readonly RawTrackProjection _projection = new RawTrackProjection();
        // Uncapped playback: draw every playhead update the UI can consume. WinForms
        // coalesces rapid Invalidate() calls into a single WM_PAINT, so we no longer
        // throttle to a 30 Hz budget. The scheduler is now a pass-through seam
        // (see PlaybackFrameScheduler) kept for future adaptive tuning.
        private readonly PlaybackFrameScheduler _playbackFrames =
            new PlaybackFrameScheduler(33);
        private readonly Stopwatch _playbackClock = Stopwatch.StartNew();
        private TimelineProjectionResult _projectionResult;
        private TimelineEventIndex _index;
        private TimelineCoordinateSystem _coordinates;
        private TimelineViewport _viewport;
        private int[] _minimapDensity = new int[0];
        private int _firstVisibleRow;
        private int _playheadVirtualTick;
        private bool _isPanning;
        private bool _isRulerScrubbing;
        private bool _hHeld;
        private bool _isPlaybackActive;
        private double _noteVisualScale = 1.0;
        private Point _lastPointer;

        /// <summary>
        /// The document this control is currently subscribed to. Tracked separately from
        /// <see cref="Document"/> because rebinding has to detach the old handlers first; leaking
        /// them would keep a closed chart alive and repaint a surface that no longer shows it.
        /// </summary>
        private EditorDocumentContext _boundDocument;

        /// <summary>
        /// Set when the model changed underneath us. The projection and interval index are
        /// rebuilt on the next frame rather than inside the mutation, because a drag raises one
        /// mutation per pointer pixel and re-indexing the whole chart that often costs far more
        /// than the edit itself.
        /// </summary>
        private bool _projectionStale;

        /// <summary>
        /// Painting snapshot of <see cref="ChartSelectionService"/>, refreshed only when its
        /// version moves. The renderer asks "is this selected?" once per visible item, so it
        /// needs a set; rebuilding that set per frame would allocate for nothing.
        /// </summary>
        private readonly HashSet<EventData> _selectionSnapshot = new HashSet<EventData>();
        private int _selectionSnapshotVersion = -1;

        /// <summary>
        /// Bookkeeping for the renderer's scroll band. The control owns it rather than the renderer
        /// because the item list has to match: the renderer's cache key hashes the items it was
        /// handed, so a per-viewport query would change the key on every scrolled frame and the band
        /// would rebuild anyway. Both the pixels the band covers and the events inside it are
        /// therefore decided here, and held still until the viewport reaches the edge.
        /// </summary>
        private double _bandOriginTick;
        private int _bandMargin;
        private int _bandWidth;
        private int _bandFirstRow = -1;
        private int _bandLastRow = -1;
        private double _bandPixelsPerTick;
        private IReadOnlyList<TimelineItem> _bandItems;

        public TimelineV2Control()
        {
            DoubleBuffered = true;
            BackColor = TimelineRenderTheme.Canvas;
            TabStop = true;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable |
                ControlStyles.UserPaint,
                true);
            _coordinates = CreateCoordinates(1f, 0.25);
        }

        public Control View
        {
            get { return this; }
        }

        /// <summary>
        /// Raised when the ruler is used to move the playhead. Deliberately the same event type
        /// the vertical strip raises, so the shell routes both through one handler and a seek
        /// behaves identically wherever it came from.
        /// </summary>
        public event EventHandler<Vertical.VerticalSeekEventArgs> SeekRequested;

        /// <summary>
        /// V2 now supports the full editing gesture set. Whether a given document may actually be
        /// mutated is still decided by <see cref="DocumentCapabilities"/>, exactly as on V1 — this
        /// only says the surface knows how to ask.
        /// </summary>
        public bool SupportsEditing
        {
            get { return true; }
        }

        public EditorDocumentContext Document { get; private set; }

        public TimelinePerformanceMonitor Performance { get; } = new TimelinePerformanceMonitor();

        public int IndexedItemCount
        {
            get { return _index == null ? 0 : _index.ItemCount; }
        }

        public string StatusText { get; private set; } = "NO DOCUMENT | RAW TRACKS";

        public int QuantizeDivision { get; set; } = 8;

        public IEventRenderer EventTheme { get; set; }

        public IZoneRenderer ZoneTheme { get; set; }

        public EventDisplayMode EventDisplayMode { get; set; } = EventDisplayMode.None;

        public bool FollowPlayback { get; set; }

        public bool IsPlaybackActive
        {
            get { return _isPlaybackActive; }
            set { _isPlaybackActive = value; }
        }

        public int PlayheadVirtualTick
        {
            get { return _playheadVirtualTick; }
            set
            {
                int next = Math.Max(0, value);
                if (_playheadVirtualTick == next)
                {
                    return;
                }
                _playheadVirtualTick = next;
                FollowPlayhead();
                RequestRepaint();
            }
        }

        public void Bind(EditorDocumentContext document)
        {
            if (document == null) throw new ArgumentNullException("document");

            AttachDocument(document);
            Document = document;
            var stopwatch = Stopwatch.StartNew();
            _projectionResult = _projection.Build(document);
            int documentEnd = Math.Max(
                1,
                _projectionResult.Items.Count == 0
                    ? 1
                    : _projectionResult.Items.Max(item => item.EndTick));
            int ticksPerMeasure = TicksPerMeasure;
            _index = new TimelineEventIndex(
                _projectionResult.Items,
                Math.Max(96, ticksPerMeasure));
            _viewport = new TimelineViewport(
                0,
                documentEnd,
                Math.Max(1, ClientSize.Width),
                _coordinates.HeaderWidth);
            _viewport.PixelsPerTick = _coordinates.PixelsPerTick;
            _minimapDensity = BuildMinimapDensity(_projectionResult, documentEnd);
            _firstVisibleRow = 0;
            _playheadVirtualTick = document.Model.VirtualCurrentTick;
            StatusText = BuildStatusText(document);
            _projectionStale = false;
            _selectionSnapshotVersion = -1;
            InvalidateBand();
            stopwatch.Stop();

            Performance.LastIndexBuildMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            Performance.IndexedItemCount = _index.ItemCount;
            Performance.FullRebuildCount++;
            Invalidate();
        }

        private void AttachDocument(EditorDocumentContext document)
        {
            if (ReferenceEquals(_boundDocument, document))
            {
                return;
            }

            DetachDocument();
            _boundDocument = document;
            if (document == null)
            {
                return;
            }
            document.UndoManager.OnUndoRedo += DocumentMutated;
            document.Selection.SelectionChanged += DocumentSelectionChanged;
        }

        private void DetachDocument()
        {
            if (_boundDocument == null)
            {
                return;
            }

            _boundDocument.UndoManager.OnUndoRedo -= DocumentMutated;
            _boundDocument.Selection.SelectionChanged -= DocumentSelectionChanged;
            _boundDocument = null;
        }

        /// <summary>
        /// The one authoritative "the chart changed" signal: create, move, resize, delete, undo and
        /// redo all reach the model through <see cref="UndoManager"/>. Listening here is what makes
        /// V2 live-sync with V1, the vertical strip and the Inspector instead of only reflecting its
        /// own gestures.
        /// </summary>
        private void DocumentMutated(object sender, UndoManager.Action action)
        {
            _projectionStale = true;
            Invalidate();
        }

        /// <summary>Flags the projection for rebuild at the next frame.</summary>
        private void MarkProjectionStale()
        {
            _projectionStale = true;
        }

        private void DocumentSelectionChanged(object sender, EventArgs e)
        {
            Invalidate();
        }

        /// <summary>
        /// Re-projects if a mutation arrived since the last frame, preserving zoom, origin, row and
        /// playhead. Called from frame construction so a burst of edits costs one rebuild.
        /// </summary>
        internal void EnsureProjection()
        {
            if (!_projectionStale || Document == null)
            {
                return;
            }

            _projectionStale = false;
            InvalidateBand();
            EditorViewState state = CaptureViewState();
            var stopwatch = Stopwatch.StartNew();
            _projectionResult = _projection.Build(Document);
            int documentEnd = Math.Max(
                1,
                _projectionResult.Items.Count == 0
                    ? 1
                    : _projectionResult.Items.Max(item => item.EndTick));
            _index = new TimelineEventIndex(
                _projectionResult.Items,
                Math.Max(96, TicksPerMeasure));
            _minimapDensity = BuildMinimapDensity(_projectionResult, documentEnd);
            _viewport = new TimelineViewport(
                0,
                documentEnd,
                Math.Max(1, ClientSize.Width),
                _coordinates.HeaderWidth);
            stopwatch.Stop();

            Performance.LastIndexBuildMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            Performance.IndexedItemCount = _index.ItemCount;
            Performance.FullRebuildCount++;
            RestoreViewState(state);
        }

        /// <summary>
        /// Refreshes the painting snapshot of the shared selection when its version has moved.
        /// </summary>
        private HashSet<EventData> SelectionSnapshot()
        {
            if (Document == null)
            {
                return null;
            }
            if (_selectionSnapshotVersion == Document.Selection.Version)
            {
                return _selectionSnapshot;
            }

            _selectionSnapshot.Clear();
            IList<EventData> items = Document.Selection.Items;
            for (int i = 0; i < items.Count; i++)
            {
                _selectionSnapshot.Add(items[i]);
            }
            _selectionSnapshotVersion = Document.Selection.Version;
            return _selectionSnapshot;
        }

        public void InvalidateView()
        {
            RequestRepaint();
        }

        internal Bitmap RenderSnapshot(int width, int height)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException("width");
            if (height <= 0) throw new ArgumentOutOfRangeException("height");

            var bitmap = new Bitmap(width, height);
            var stopwatch = Stopwatch.StartNew();
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                TimelineFrame frame = CreateFrameForTesting(width, height);
                _renderer.Render(graphics, frame);
            }
            stopwatch.Stop();
            Performance.LastFrameMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            return bitmap;
        }

        /// <summary>
        /// Renders one frame straight onto a caller-owned <see cref="Graphics"/>. Snapshot
        /// tests need a bitmap back, but timing tests must not pay for allocating one per
        /// sample, so they reuse a single target and call this instead.
        /// </summary>
        internal void RenderForTesting(Graphics graphics, int width, int height)
        {
            if (graphics == null) throw new ArgumentNullException("graphics");
            if (width <= 0) throw new ArgumentOutOfRangeException("width");
            if (height <= 0) throw new ArgumentOutOfRangeException("height");

            TimelineFrame frame = CreateFrameForTesting(width, height);
            _renderer.Render(graphics, frame);
        }

        /// <summary>
        /// Scrolls the time axis the way the mouse wheel does, without needing a real
        /// message loop or a mouse.
        /// </summary>
        internal void ScrollByTicksForTesting(double ticks)
        {
            if (_viewport == null) return;
            _viewport.OriginTick = _viewport.OriginTick + ticks;
        }

        /// <summary>
        /// How many times the offscreen static frame has actually been re-rendered. A
        /// playhead-only repaint must not increment this.
        /// </summary>
        internal int StaticFrameRebuildCountForTesting
        {
            get { return _renderer.StaticFrameRebuildCount; }
        }

        public bool TrySetTimeZoom(float zoom)
        {
            if (_viewport == null || zoom <= 0)
            {
                return false;
            }

            _viewport.PixelsPerTick = zoom;
            _coordinates = CreateCoordinates(1f, _viewport.PixelsPerTick);
            Invalidate();
            return true;
        }

        public EditorViewState CaptureViewState()
        {
            return new EditorViewState
            {
                PixelsPerTick = _viewport == null
                    ? _coordinates.PixelsPerTick
                    : _viewport.PixelsPerTick,
                OriginTick = _viewport == null ? 0 : _viewport.OriginTick,
                FirstVisibleRow = _firstVisibleRow,
                PlayheadVirtualTick = PlayheadVirtualTick
            };
        }

        public void RestoreViewState(EditorViewState state)
        {
            if (state == null) return;

            if (_viewport != null)
            {
                _viewport.PixelsPerTick = state.PixelsPerTick;
                _viewport.OriginTick = state.OriginTick;
                _coordinates = CreateCoordinates(1f, _viewport.PixelsPerTick);
            }
            _firstVisibleRow = ClampFirstRow(state.FirstVisibleRow);
            _playheadVirtualTick = Math.Max(0, state.PlayheadVirtualTick);
            Invalidate();
        }

        internal TimelineFrame CreateFrameForTesting(int width, int height)
        {
            if (Document == null || _projectionResult == null || _index == null || _viewport == null)
            {
                throw new InvalidOperationException("Bind a document before creating a frame.");
            }

            EnsureProjection();
            _viewport.ViewportWidth = Math.Max(1, width);
            int visibleRowCount = Math.Max(
                1,
                (Math.Max(1, height - TimelineFrame.MinimapHeight - _coordinates.RulerHeight) /
                    _coordinates.RowHeight) + 1);
            int lastVisibleRow = Math.Max(
                _firstVisibleRow,
                Math.Min(_projectionResult.Rows.Count - 1, _firstVisibleRow + visibleRowCount - 1));

            int bandMargin = TimelineRenderer.ScrollBandMarginPixels(
                Math.Max(1, width - _coordinates.HeaderWidth));
            var queryStopwatch = Stopwatch.StartNew();
            IReadOnlyList<TimelineItem> visibleItems = EnsureBandItems(
                width, bandMargin, lastVisibleRow);
            queryStopwatch.Stop();

            Performance.LastQueryMilliseconds = queryStopwatch.Elapsed.TotalMilliseconds;
            Performance.LastVisibleItemCount = visibleItems.Count;
            Performance.LastQueryCandidateCount = _index.LastQueryCandidateCount;

            return new TimelineFrame(
                width,
                height,
                _coordinates,
                _viewport,
                _projectionResult.Rows,
                visibleItems,
                _firstVisibleRow,
                _playheadVirtualTick,
                Document.Capabilities.IsReadOnly || !SupportsEditing,
                Document.Capabilities.StatusLabel,
                StatusText,
                TicksPerMeasure,
                TicksPerMeasure > 0 ? 4 : 0,
                _minimapDensity,
                QuantizeDivision,
                EventTheme,
                ZoneTheme,
                EventDisplayMode,
                SelectionSnapshot(),
                ActiveMarquee.IsEmpty ? (Rectangle?)null : ActiveMarquee,
                _bandMargin,
                _bandOriginTick);
        }

        /// <summary>
        /// Returns the events the renderer should draw, re-querying only when the viewport has left
        /// the band that was cached for it.
        /// </summary>
        /// <remarks>
        /// The band deliberately starts <paramref name="bandMargin"/> pixels before the viewport, so
        /// scrolling either way is a blit until the viewport reaches one of the margins. Rows, zoom
        /// and width all change what the band contains, so any of them moving forces a fresh query;
        /// a stale projection does too, since the item objects themselves are replaced.
        /// </remarks>
        private IReadOnlyList<TimelineItem> EnsureBandItems(
            int width,
            int bandMargin,
            int lastVisibleRow)
        {
            double pixelsPerTick = Math.Max(1e-9, _viewport.PixelsPerTick);
            double offsetPixels = (_viewport.OriginTick - _bandOriginTick) * pixelsPerTick;
            bool reusable = _bandItems != null &&
                _bandMargin == bandMargin &&
                _bandWidth == width &&
                _bandFirstRow == _firstVisibleRow &&
                _bandLastRow == lastVisibleRow &&
                _bandPixelsPerTick == _viewport.PixelsPerTick &&
                offsetPixels >= 0 &&
                offsetPixels <= bandMargin * 2;
            if (reusable)
            {
                return _bandItems;
            }

            double bandSpan = (width + (bandMargin * 2)) / pixelsPerTick;
            _bandOriginTick = _viewport.OriginTick - (bandMargin / pixelsPerTick);
            _bandMargin = bandMargin;
            _bandWidth = width;
            _bandFirstRow = _firstVisibleRow;
            _bandLastRow = lastVisibleRow;
            _bandPixelsPerTick = _viewport.PixelsPerTick;
            _bandItems = _index.Query(
                new TimelineTimeRange(_bandOriginTick, _bandOriginTick + bandSpan),
                new TimelineRowRange(_firstVisibleRow, lastVisibleRow),
                Math.Max(96, TicksPerMeasure),
                1);
            return _bandItems;
        }

        /// <summary>
        /// Drops the cached band. Anything that changes what the band would contain but is not part
        /// of its identity — a re-projection above all — has to call this, or the renderer would keep
        /// blitting items that no longer exist in the document.
        /// </summary>
        private void InvalidateBand()
        {
            _bandItems = null;
            _bandFirstRow = -1;
            _bandLastRow = -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Document == null)
            {
                e.Graphics.Clear(TimelineRenderTheme.Canvas);
                using (var brush = new SolidBrush(TimelineRenderTheme.MutedText))
                using (var font = new Font("Segoe UI", 10f))
                {
                    e.Graphics.DrawString("Open a chart to use Timeline V2", font, brush, 24, 24);
                }
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            TimelineFrame frame = CreateFrameForTesting(ClientSize.Width, ClientSize.Height);
            _renderer.Render(e.Graphics, frame);
            if (Focused)
            {
                ControlPaint.DrawFocusRectangle(
                    e.Graphics,
                    new Rectangle(2, 2, Math.Max(1, Width - 5), Math.Max(1, Height - 5)),
                    TimelineRenderTheme.Text,
                    TimelineRenderTheme.Canvas);
            }
            stopwatch.Stop();
            Performance.LastFrameMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_viewport != null)
            {
                _viewport.ViewportWidth = Math.Max(1, ClientSize.Width);
            }
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_viewport == null) return;

            switch (TimelineInputBindings.ResolveWheelAction(ModifierKeys))
            {
                case TimelineWheelAction.Zoom:
                    if ((ModifierKeys & Keys.Control) == Keys.Control)
                    {
                        ApplyTouchpadZoom(e.X, e.Delta);
                        return;
                    }
                    _viewport.ZoomAt(e.X, WheelZoomFactor(e.Delta));
                    _coordinates = CreateCoordinates(1f, _viewport.PixelsPerTick);
                    break;
                case TimelineWheelAction.HorizontalScroll:
                    _viewport.ScrollByPixels(-e.Delta / 2.0);
                    break;
                default:
                    _firstVisibleRow = ClampFirstRow(
                        _firstVisibleRow - Math.Sign(e.Delta) * 3);
                    break;
            }
            Invalidate();
        }

        internal void ApplyTouchpadZoom(int screenX, int delta)
        {
            if (_viewport == null || delta == 0)
            {
                return;
            }

            double factor = WheelZoomFactor(delta);
            _viewport.ZoomAt(screenX, factor);
            _noteVisualScale = Math.Max(
                MinNoteVisualScale,
                Math.Min(MaxNoteVisualScale, _noteVisualScale * factor));
            _coordinates = CreateCoordinates(1f, _viewport.PixelsPerTick);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();

            if (_viewport != null && e.Y >= Height - TimelineFrame.MinimapHeight)
            {
                double ratio = Math.Max(0, Math.Min(1, (double)e.X / Math.Max(1, Width)));
                double target = _viewport.DocumentStartTick +
                    ((_viewport.DocumentEndTick - _viewport.DocumentStartTick) * ratio);
                _viewport.OriginTick = target - (_viewport.VisibleTickCount / 2);
                Invalidate();
                return;
            }

            // The ruler is a transport control, the way it is in every DAW: click or drag it to
            // move the playhead. The playtest asked for this because the minimap moves the *view*
            // and there was no pointer route to move the *time*.
            if (_viewport != null &&
                e.Button == MouseButtons.Left &&
                e.Y < _coordinates.RulerHeight &&
                e.X >= _coordinates.HeaderWidth)
            {
                _isRulerScrubbing = true;
                Capture = true;
                SeekToScreenX(e.X);
                return;
            }

            if (TimelineInputBindings.IsPanGesture(e.Button, ModifierKeys, _hHeld))
            {
                _isPanning = true;
                _lastPointer = e.Location;
                Capture = true;
                Cursor = Cursors.SizeAll;
                return;
            }

            BeginEditingGesture(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_isRulerScrubbing)
            {
                SeekToScreenX(e.X);
                return;
            }

            if (_isPanning && _viewport != null)
            {
                int deltaX = e.X - _lastPointer.X;
                int deltaY = e.Y - _lastPointer.Y;
                _viewport.PanByPixels(deltaX);
                if (Math.Abs(deltaY) >= _coordinates.RowHeight / 2)
                {
                    _firstVisibleRow = ClampFirstRow(
                        _firstVisibleRow - (deltaY / _coordinates.RowHeight));
                    _lastPointer.Y = e.Y;
                }
                _lastPointer.X = e.X;
                Invalidate();
                return;
            }

            ContinueEditingGesture(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            EndEditingGesture();
            _isRulerScrubbing = false;
            _isPanning = false;
            Capture = false;
            Cursor = Cursors.Default;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_viewport == null) return;

            if (e.KeyCode == Keys.H)
            {
                _hHeld = true;
                Cursor = Cursors.Hand;
            }
            else if (e.KeyCode == Keys.Add || e.KeyCode == Keys.Oemplus)
            {
                _viewport.ZoomAt(Width / 2.0, 1.2);
                _coordinates = CreateCoordinates(1f, _viewport.PixelsPerTick);
                Invalidate();
            }
            else if (e.KeyCode == Keys.Subtract || e.KeyCode == Keys.OemMinus)
            {
                _viewport.ZoomAt(Width / 2.0, 1.0 / 1.2);
                _coordinates = CreateCoordinates(1f, _viewport.PixelsPerTick);
                Invalidate();
            }
            else if (e.KeyCode == Keys.Home)
            {
                _viewport.OriginTick = _viewport.DocumentStartTick;
                Invalidate();
            }
            else if (e.KeyCode == Keys.End)
            {
                _viewport.OriginTick = _viewport.DocumentEndTick;
                Invalidate();
            }
            else if (e.KeyCode == Keys.Delete || e.KeyCode == Keys.Back)
            {
                // Erasing with the keyboard was the one editing verb V1 had and V2 did not, which
                // made "V2 is editable now" feel half-true: you could draw and move but not delete
                // without switching tools.
                if (Document != null && Document.Edits.DeleteSelection())
                {
                    e.Handled = true;
                }
            }
            else if (e.KeyCode == Keys.Escape)
            {
                CancelEditingGesture();
            }
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.H)
            {
                _hHeld = false;
                if (!_isPanning) Cursor = Cursors.Default;
            }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Home || key == Keys.End || key == Keys.Add ||
                key == Keys.Subtract || key == Keys.Oemplus || key == Keys.OemMinus ||
                key == Keys.H || key == Keys.Delete || key == Keys.Back ||
                key == Keys.Escape || base.IsInputKey(keyData);
        }

        private int TicksPerMeasure
        {
            get
            {
                if (Document == null || Document.Model.TickPerMinute == 0)
                {
                    return 0;
                }
                return Document.Model.TickPerMinute * DJMax.EventData.VirtualTickSize;
            }
        }

        /// <summary>
        /// Scrolls the view continuously so the playhead stays pinned at
        /// <see cref="FollowAnchor"/> while the chart flows past it.
        /// </summary>
        /// <remarks>
        /// This shipped as a paging band first, because the renderer's static-frame cache was
        /// keyed on the origin tick: a moving origin meant a full re-render of every visible note
        /// and label on every frame, 13-18ms against 2.5ms for a cache hit, so holding the origin
        /// still was the only way to keep playback smooth. The playtest asked for continuous
        /// scroll back, so the cache was changed instead of the motion — the renderer now caches
        /// an over-wide content band and blits it at an offset (see
        /// <c>TimelineRenderer.ScrollBandMarginPixels</c>), which makes a per-frame origin change
        /// a blit rather than a rebuild. Continuous scroll is therefore now affordable.
        /// </remarks>
        private void FollowPlayhead()
        {
            if (!FollowPlayback || !IsPlaybackActive || _viewport == null)
            {
                return;
            }

            double visible = _viewport.VisibleTickCount;
            if (visible <= 0)
            {
                return;
            }

            double followedOrigin = Math.Max(
                _viewport.DocumentStartTick,
                _playheadVirtualTick - (visible * FollowAnchor));

            // A dead zone in pixels, not ticks: sub-pixel origin churn cannot move anything on
            // screen but would still invalidate the scroll band's integer offset every frame.
            double deltaPixels =
                Math.Abs(followedOrigin - _viewport.OriginTick) * _viewport.PixelsPerTick;
            if (deltaPixels < FollowDeadZonePixels)
            {
                return;
            }

            _viewport.OriginTick = followedOrigin;
        }

        private int ClampFirstRow(int row)
        {
            int maximum = _projectionResult == null
                ? 0
                : Math.Max(0, _projectionResult.Rows.Count - 1);
            return Math.Max(0, Math.Min(maximum, row));
        }

        private TimelineCoordinateSystem CreateCoordinates(float dpiScale, double pixelsPerTick)
        {
            return new TimelineCoordinateSystem(
                pixelsPerTick,
                (int)Math.Round(DefaultRowHeight * _noteVisualScale),
                DefaultHeaderWidth,
                DefaultRulerHeight,
                dpiScale);
        }

        private static double WheelZoomFactor(int delta)
        {
            return Math.Pow(1.0015, delta);
        }

        private void RequestRepaint()
        {
            if (IsPlaybackActive &&
                !_playbackFrames.ShouldRenderAt(_playbackClock.ElapsedMilliseconds))
            {
                return;
            }
            Invalidate();
        }

        private static int[] BuildMinimapDensity(
            TimelineProjectionResult projection,
            int documentEnd)
        {
            var density = new int[MinimapBucketCount];
            foreach (TimelineItem item in projection.Items)
            {
                int bucket = Math.Min(
                    density.Length - 1,
                    Math.Max(0, (int)((long)item.StartTick * density.Length / Math.Max(1, documentEnd))));
                density[bucket]++;
            }
            return density;
        }

        private static string BuildStatusText(EditorDocumentContext document)
        {
            string format = document.Capabilities.SourceFormat.HasValue
                ? document.Capabilities.SourceFormat.Value.ToString()
                : "Unknown format";
            string encryption = document.Capabilities.IsEncrypted
                ? " | ENCRYPTED SOURCE / DECRYPTED IN MEMORY"
                : string.Empty;
            string lockState = document.Capabilities.IsReadOnly ||
                document.Capabilities.IsRespectV
                    ? " | " + document.Capabilities.StatusLabel
                    : string.Empty;
            return format + encryption + " | RAW TRACKS" + lockState;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DetachDocument();
                _renderer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
