using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using System.Drawing.Drawing2D;
using DJMaxEditor.Undo.Action;
using DJMaxEditor.Controls;
using DJMaxEditor.Controls.Editor.Renderers;
using DJMaxEditor.DJMax;
using DJMaxEditor.Controls.Editor.Renderers.Events;
using DJMaxEditor.Controls.Editor.Renderers.Zones;
using DJMaxEditor.Controls.Editor;
using DJMaxEditor.Controls.Editor.Handlers;
using DJMaxEditor.Editor;

namespace DJMaxEditor 
{
    public sealed partial class EditorControl : UserControl
    {
        /// <summary>
        /// Where follow-while-playing pins the playhead, as a fraction of the visible tick window.
        /// A fifth in leaves four fifths of lead-in, which is the read the playtest asked for.
        /// </summary>
        private const double FollowBandAnchor = 0.20;

        /// <summary>
        /// Smallest scroll, in device pixels, worth acting on while following. Below this nothing
        /// on screen could move, but the scroll band's blit offset would still be invalidated.
        /// </summary>
        private const double FollowDeadZonePixels = 0.75;

        #region public defs

        public event EventHandler OnUndoRedo;

        public event EventRequestHandler OnRequestEvent;

        public event EventHandler ViewSettingsChanged;

        public UndoManager UndoManager = UndoManager.GetInstance();

        public const float MinZoom = 0.20f;

        public const float MaxZoom = 2f;

        // template event to add an event in PlayerData
        public EventData TemplateEvent = null;

        public bool FollowTracksProgressWhilePlaying
        {
            get { return _followTracksProgressWhilePlaying; }
            set
            {
                if (_followTracksProgressWhilePlaying == value) return;
                _followTracksProgressWhilePlaying = value;
                RaiseViewSettingsChanged();
            }
        }

        public bool IsPlayerPlaying
        {
            get { return _isPlayerPlaying; }
            set
            {
                if (_isPlayerPlaying == value) return;
                _isPlayerPlaying = value;
                RaiseViewSettingsChanged();
            }
        }

        public event EventDataHandler OnSelectItem;

        internal readonly TracksRenderer TracksRenderer;

        private readonly ZonesRenderer ZonesRenderer;

        public IEnumerable<IEventRenderer> EventsThemeList => EventsRenderer.Themes;

        public IEventRenderer CurrentEventsTheme
        {
            get => EventsRenderer.Theme;
            set
            {
                EventsRenderer.Theme = value;
                Redraw();
                RaiseViewSettingsChanged();
            }
        }

        internal IEnumerable<IZoneRenderer> ZonesThemeList => ZonesRenderer.Themes;

        internal IZoneRenderer CurrentZonesTheme
        {
            get => ZonesRenderer.Theme;
            set
            {
                ZonesRenderer.Theme = value;
                Redraw();
                RaiseViewSettingsChanged();
            }
        }

        internal readonly EventsRenderer EventsRenderer;

        public int NoteValue
        {
            get => _noteValue;

            set
            {
                _noteValue = value;
                UpdateBlockSize();
                RaiseViewSettingsChanged();
            }
        }

        private readonly TextBox _mTextBox;

        private TrackData _mSelectedTrack;

        public EditorControl() 
        {
            DoubleBuffered = true;
            ZonesRenderer = new ZonesRenderer();
            EventsRenderer = new EventsRenderer();
            TracksRenderer = new TracksRenderer(EventsRenderer, ZonesRenderer);
            ContextMenu contextMenu = new ContextMenu();
            contextMenu.Popup += MenuContextPopup;
            ContextMenu = contextMenu;

            _mTextBox = new TextBox();
            _mTextBox.Leave += TextBoxLeave;
            _mTextBox.Visible = false;
            _mTextBox.Parent = this;
            _mTextBox.MaxLength = 0x40;
            _mTextBox.TextChanged += TextBoxTextChanged;
            _mTextBox.LostFocus += TextBoxLostFocus;
            _mTextBox.KeyPress += TextBoxKeyPressed;

            InitializeComponent();

            // The ruler is a sibling above the 2x2 scroll table, not a row inside it: the table's
            // first row is the drawing area and the vertical scrollbar, and the ruler must not be
            // pushed right by the scrollbar column or it would be misaligned with the chart by
            // exactly the scrollbar's width.
            _timeRuler = new EditorTimeRuler();
            _timeRuler.SeekRequested += TimeRuler_SeekRequested;
            Controls.Add(_timeRuler);
            Controls.SetChildIndex(_timeRuler, 0);

            DrawingArea.BackColor = UI.StudioDesignSystem.Void;
            MouseWheel += DrawingArea_MouseWheel;
            DrawingArea.MouseWheel += DrawingArea_MouseWheel;

            SetStyle(ControlStyles.Selectable, true);
        }

        /// <summary>
        /// Raised when the time ruler above the chart is clicked or dragged. The shell owns the
        /// audio player, so moving the playhead has to go back out through the same path the
        /// vertical timeline and V2 use rather than being applied here.
        /// </summary>
        public event EventHandler<Controls.Vertical.VerticalSeekEventArgs> SeekRequested;

        /// <summary>Whether the clickable time ruler is shown above the chart.</summary>
        public bool ShowTimeRuler
        {
            get { return _timeRuler != null && _timeRuler.Visible; }
            set
            {
                if (_timeRuler == null || _timeRuler.Visible == value) return;
                _timeRuler.Visible = value;
                UpdateScrollbars();
                Repaint();
            }
        }

        private void TimeRuler_SeekRequested(
            object sender,
            Controls.Vertical.VerticalSeekEventArgs e)
        {
            if (SeekRequested != null)
            {
                SeekRequested(this, e);
            }
            else
            {
                // Unhosted (tests, or the legacy standalone path): still move the playhead, so the
                // ruler is not silently inert.
                SetPlayheadVirtualTick(e.VirtualTick);
            }
        }

        /// <summary>Pushes the current view and playhead into the ruler band.</summary>
        private void SyncTimeRuler()
        {
            if (_timeRuler == null || !_timeRuler.Visible || _playerData == null) return;

            // The band spans the whole control but only the part above the chart is seekable.
            _timeRuler.RightInset = vScrollBar.Visible ? vScrollBar.Width : 0;

            // PlayerData.TickPerMinute is the measure resolution (192 by default), so a measure is
            // TickPerMinute * VirtualTickSize virtual ticks - the same "beatSize" the track
            // renderer draws its major grid on. Four beats to the measure, matching the grid.
            const int BeatsPerMeasure = 4;
            int measureTicks = Math.Max(
                BeatsPerMeasure,
                (int)_playerData.TickPerMinute * EventData.VirtualTickSize);
            _timeRuler.SetView(
                _zoom,
                _viewablePixels.X,
                (int)_playerData.VirtualMaxTick,
                measureTicks,
                BeatsPerMeasure);
            _timeRuler.SetPlayhead(_playerData.VirtualCurrentTick);
        }

        public void SelectAll()
        {
            _selectMode.SelectAll();
        }

        public int SelectedEventCount
        {
            get { return _selectMode == null ? 0 : _selectMode.SelectedItems.Count; }
        }

        public IList<EventData> SelectedEvents
        {
            get
            {
                return _selectMode == null
                    ? new List<EventData>().AsReadOnly()
                    : _selectMode.SelectedItems;
            }
        }

        public void Deselect()
        {
            _selectMode.ClearSelection();
            Redraw();
        }

        public void InverseSelection()
        {
            _selectMode.InvertSelection();
            Redraw();
        }

        private void TextBoxKeyPressed(object sender, KeyPressEventArgs e)
        {
            if (!CanMutateDocument)
            {
                e.Handled = true;
                return;
            }

            if (e.KeyChar == Convert.ToChar(Keys.Enter))
            {
                ActiveControl = null;
                UndoManager.ExecAction(new RenameTrackAction(_mSelectedTrack, _mTextBox.Text));
                e.Handled = true;
            }
        }

        private void TextBoxLostFocus(object sender, EventArgs e)
        {
            if (!CanMutateDocument)
            {
                return;
            }

            UndoManager.ExecAction(new RenameTrackAction(_mSelectedTrack, _mTextBox.Text));
        }

        private void TextBoxTextChanged(object sender, EventArgs e)
        {
            if (!(sender is TextBox textBox))
            {
                return;
            }
        }

        private void MenuContextPopup(object sender, EventArgs e)
        {
            if (!(sender is ContextMenu contextMenu))
            {
                return;
            }

            if (null == _selectMode)
            {
                return;
            }

            contextMenu.MenuItems.Clear();

            if (!CanMutateDocument)
            {
                return;
            }

            var screenPoint = Cursor.Position;
            var pictureBoxPoint = contextMenu.SourceControl.PointToClient(screenPoint);


            var eventData = _selectMode.GetEventAtPos(
                (int)(pictureBoxPoint.X / _zoom) + _viewablePixels.X,
                (int)(pictureBoxPoint.Y / _zoom) + _viewablePixels.Y
            );

            if (eventData != null)
            {
                if (_documentContext != null)
                {
                    if (!_documentContext.Selection.Items.Contains(eventData))
                    {
                        _documentContext.Selection.Replace(new[] { eventData });
                    }
                    contextMenu.MenuItems.Add("Cu&t", delegate
                    {
                        _documentContext.Clipboard.CutSelection();
                    });
                    contextMenu.MenuItems.Add("&Copy", delegate
                    {
                        _documentContext.Clipboard.CopySelection();
                    });
                    contextMenu.MenuItems.Add("&Duplicate", delegate
                    {
                        _documentContext.Clipboard.DuplicateSelection(QuantizeVirtualStep);
                    });
                    contextMenu.MenuItems.Add("-");
                    contextMenu.MenuItems.Add("&Delete", delegate
                    {
                        _documentContext.Edits.DeleteSelection();
                    });
                    contextMenu.MenuItems.Add("Reveal in &Inspector", delegate
                    {
                        OnSelectItem?.Invoke(this, _documentContext.Selection.Items.ToArray());
                    });
                }
                return;
            }

            TrackData trackData = _selectMode.GetTrackAtPos(
                (int)(pictureBoxPoint.X / _zoom) + _viewablePixels.X,
                (int)(pictureBoxPoint.Y / _zoom) + _viewablePixels.Y
            );

            if (null == trackData)
            {
                return;
            }

            _mSelectedTrack = trackData;

            _mTextBox.Text = trackData.TrackName;
            _mTextBox.Left = pictureBoxPoint.X;
            _mTextBox.Top = pictureBoxPoint.Y;
            contextMenu.MenuItems.Add("&Rename track", new EventHandler(OpenTrackRename));
        }

        private void OpenTrackRename(object sender, EventArgs e)
        {
            _mTextBox.Visible = true;
            _mTextBox.Focus();
        }

        private void TextBoxLeave(object sender, EventArgs e)
        {
            _mTextBox.Visible = false;
        }

        public void UpdateDrawableZone()
        {
            if (null == _playerData)
            {
                return;
            }

            _drawableZone.Height = _playerData.Tracks.Count * EventsRenderer.VirtualTrackheight;
            _drawableZone.Width = (int)_playerData.VirtualMaxTick;
        }

        public void Bind(EditorDocumentContext document)
        {
            if (document == null) throw new ArgumentNullException("document");
            if (_documentContext != null)
            {
                _documentContext.Selection.SelectionChanged -= SharedSelectionChanged;
            }
            _documentContext = document;
            UndoManager = document.UndoManager;
            _documentContext.Selection.SelectionChanged += SharedSelectionChanged;
            InitializeCore(document.Model, document.Selection);
        }

        public void Initialize(PlayerData playerData)
        {
            if (_documentContext != null)
            {
                _documentContext.Selection.SelectionChanged -= SharedSelectionChanged;
            }
            _documentContext = null;
            InitializeCore(playerData, new ChartSelectionService());
        }

        private void InitializeCore(PlayerData playerData, ChartSelectionService selection)
        {
            hScrollBar.Visible = true;
            vScrollBar.Visible = true;

            hScrollBar.Value = 0;
            vScrollBar.Value = 0;

            DrawingArea.Width = Math.Max((int)playerData.MaxTick, _drawableZone.Width);

            _playerData = playerData;

            var tracks = playerData.Tracks;
            tracks.EventAdded += NoteAddedOrRemoved;
            tracks.EventRemoved += NoteAddedOrRemoved;

            UpdateDrawableZone();
            
            _selectMode?.Dispose();
            _selectMode = new EventSelectMode(this, _playerData, EventsRenderer, selection);

            _selectMode.OnSelectEvents += selectMode_OnSelect;

            _selectMode.OnChangeEventPosition += selectMode_OnChangePosition;
            _selectMode.OnDeleteEvent += SelectMode_OnDeleteEvent;

            UndoManager.OnUndoRedo += UndoManager_OnUndoRedo;

            _ready = true;

            SetZoom(_zoom);
        }

        private void SharedSelectionChanged(object sender, EventArgs e)
        {
            Redraw();
        }

        public void SetZoom(float nZoom) 
        {
            // Make the top corner stay the same between zooms
            hScrollBar.Value = Math.Max(hScrollBar.Minimum, Math.Min(hScrollBar.Maximum, (int)(hScrollBar.Value * (nZoom / _zoom))));
            vScrollBar.Value = Math.Max(vScrollBar.Minimum, Math.Min(vScrollBar.Maximum, (int)(vScrollBar.Value * (nZoom / _zoom))));

            this._zoom = nZoom;

            UpdateScrollbars();

            UpdateBlockSize();

            this.Redraw();

            // Zoom is a view setting like the others: announcing it keeps the V2 timeline
            // and the vertical ptSequencer surface at the same scale as this editor.
            RaiseViewSettingsChanged();
        }

        public float GetZoom()
        {
            return _zoom;
        }

        public void ScrollEditorPixel(int? x = null, int? y = null) 
        {
            if (x < 0) { x = 0; }
            if (y < 0) { y = 0; }

            if (x != null) {
                hScrollBar.Value = Math.Max(hScrollBar.Minimum, Math.Min(hScrollBar.Maximum, x ?? 0));
            }

            if (y != null) {
                vScrollBar.Value = Math.Max(vScrollBar.Minimum, Math.Min(vScrollBar.Maximum, y ?? 0));
            }
        }

        public void Repaint()
        {
            // Full repaint: the chart, the view or the theme changed, so the cached content
            // frame is stale. Bumping the revision is what forces it to be rebuilt.
            _contentRevision++;
            RepaintRequestCount++;
            DrawingArea.Invalidate();
        }

        /// <summary>
        /// Repaints the playhead and the selection overlay only, reusing the cached content
        /// frame. This is the playback path: it used to go through <see cref="Repaint"/>, so
        /// every 16ms tick re-rendered every visible note, label and grid line.
        /// </summary>
        public void RepaintPlayhead()
        {
            RepaintRequestCount++;
            DrawingArea.Invalidate();
        }

        /// <summary>
        /// Moves the playhead, doing nothing at all when the tick has not changed.
        /// </summary>
        /// <remarks>
        /// The change gate is the point. The shell drives this from a 16ms timer that runs
        /// whenever a chart is loaded - playing or not - so without it the editor repainted
        /// ~62 times a second while sitting completely idle.
        /// </remarks>
        /// <returns>True when the playhead actually moved.</returns>
        public bool SetPlayheadVirtualTick(int virtualTick)
        {
            if (_playerData == null) return false;

            int tick = virtualTick / EventData.VirtualTickSize;
            if (tick < 0) tick = 0;
            if (_playerData.CurrentTick == tick) return false;

            _playerData.CurrentTick = tick;
            // The ruler is its own control, so the caret can move without invalidating the chart.
            SyncTimeRuler();
            RepaintPlayhead();
            return true;
        }

        /// <summary>Times the cached content frame has been re-rendered; a test seam.</summary>
        public int ContentFrameRebuildCount { get; private set; }

        /// <summary>
        /// Leftmost virtual tick the view is scrolled to; a test seam. Virtual x is virtual tick
        /// 1:1 in this surface, so the viewable rectangle's left edge <em>is</em> the origin tick.
        /// </summary>
        /// <remarks>
        /// Exposed because a rebuild count alone cannot tell continuous follow-scroll from the
        /// paging follow this surface shipped for one release: both keep the count low, and paging
        /// keeps it low precisely <em>by</em> not moving the view. Reading the origin per frame is
        /// the only way to assert the view actually flows.
        /// </remarks>
        internal int ViewOriginVirtualTick
        {
            get { return _viewablePixels.X; }
        }

        /// <summary>Times a repaint has been requested at all; a test seam for the idle gate.</summary>
        public int RepaintRequestCount { get; private set; }

        public void ScrollTo(int x, int y) 
        {
            if (x > -1) {
                hScrollBar.Value = Math.Max(hScrollBar.Minimum, Math.Min(hScrollBar.Maximum, x));
            }

            if (y > -1) {
                vScrollBar.Value = Math.Max(vScrollBar.Minimum, Math.Min(vScrollBar.Maximum, y));
            }
        }

        public void Redraw() 
        {
            Repaint();
        }

        #endregion // public defs

        #region private defs

        private readonly EditorTimeRuler _timeRuler;

        private Rectangle _viewablePixels = new Rectangle();

        private Rectangle _drawableZone = new Rectangle();

        private PlayerData _playerData;

        private EditorDocumentContext _documentContext;

        private object _activeMoveUndoGroup;

        private object _activeResizeUndoGroup;

        private int _activeResizeLastX;

        private bool CanMutateDocument
        {
            get
            {
                string reason;
                return DocumentMutationGuard.CanMutate(_playerData, true, out reason);
            }
        }

        private bool _ignoreMouse = false;

        private bool _followTracksProgressWhilePlaying;

        private bool _isPlayerPlaying;

        private float _zoom = 0.5f;

        private bool _ready = false;

        private int _noteValue = 8;

        private readonly ProgressBar _progress = new ProgressBar();

        private readonly Drag _drag = new Drag();

        private EventSelectMode _selectMode = null;

        public EventDisplayMode EventDisplayMode
        {
            get => EventsRenderer.EventDisplayMode;

            set
            {
                EventsRenderer.EventDisplayMode = value;
                Redraw();
                RaiseViewSettingsChanged();
            }
        }

        private void RaiseViewSettingsChanged()
        {
            ViewSettingsChanged?.Invoke(this, EventArgs.Empty);
        }

        private void EditorControl_SizeChanged(object sender, EventArgs e) 
        {
            //DrawingArea.Invalidate();
            //updateTextBoxPosition();
        }

        private void UpdateBlockSize() 
        {
            if (!_ready) { return; }
            _selectMode.BlockSize.X = QuantizeVirtualStep;
            _selectMode.BlockSize.Y = EventsRenderer.VirtualTrackheight;
        }

        private int QuantizeVirtualStep
        {
            get
            {
                return Math.Max(
                    EventData.VirtualTickSize,
                    (int)((_playerData.TickPerMinute / Math.Max(1, _noteValue)) *
                        EventData.VirtualTickSize));
            }
        }

        private void selectMode_OnChangePosition(List<EventData> eventData, int trackDelta, int positionDelta) 
        {
            if (!CanMutateDocument)
            {
                return;
            }

            if (positionDelta != 0 || trackDelta != 0)
            {
                if (_documentContext != null)
                {
                    _documentContext.Edits.MoveSelection(
                        trackDelta,
                        positionDelta,
                        _activeMoveUndoGroup);
                }
                else
                {
                    UndoManager.ExecAction(new MoveEventAction(_playerData, eventData, trackDelta, positionDelta));
                }
            }
        }

        private void SelectMode_OnDeleteEvent(object sender, EventData[] events)
        {
            if (!CanMutateDocument)
            {
                return;
            }

            if (_documentContext != null)
            {
                _documentContext.Edits.DeleteSelection();
            }
            else
            {
                UndoManager.ExecAction(new RemoveEventAction(_playerData, events));
            }
        }

        private void selectMode_OnSelect(object sender, EventData[] events) 
        {
            if (_documentContext == null)
            {
                OnSelectItem?.Invoke(this, events);
            }
        }

        private void DrawingArea_MouseWheel(object sender, MouseEventArgs e)
        {
            switch (Control.ModifierKeys)
            {
                case Keys.Alt:
                    var oldZoom = _zoom;

                    if (e.Delta > 0) {
                        oldZoom = oldZoom + 0.1f;
                    } else if (e.Delta < 0) {
                        oldZoom = oldZoom - 0.1f;
                    }

                    oldZoom = Math.Min(oldZoom, MaxZoom);
                    oldZoom = Math.Max(oldZoom, MinZoom);

                    SetZoom(oldZoom);
                    break;
                case Keys.Control:
                    ScrollEditorPixel((int)(_viewablePixels.X * _zoom - e.Delta / 4), null);
                    break;
                default:
                    ScrollEditorPixel(null, (int)(_viewablePixels.Y * _zoom - e.Delta / 4));
                    break;
            }
        }

        private GraphicsWrapper m_gw = new GraphicsWrapper();

        private Bitmap _contentFrame;

        private string _contentKey;

        private int _contentRevision;

        /// <summary>
        /// Scroll band bookkeeping. <see cref="_contentFrame"/> is wider than the drawing area by
        /// <see cref="_bandMargin"/> device pixels on each side, and <see cref="_bandOriginVirtualX"/>
        /// is the virtual x its left edge was rendered at. Scrolling then moves the read window
        /// instead of re-rendering, which is what lets the playhead drag the view continuously
        /// without a full chart render per frame.
        /// </summary>
        private int _bandMargin;

        private int _bandOriginVirtualX;

        private bool IsFollowing => FollowTracksProgressWhilePlaying && IsPlayerPlaying && (_playerData.CurrentTick < _playerData.MaxTick);

        private void DrawToBuffer(Graphics g)
        {
            if (!_ready) { return; }
            if (_playerData == null) { return; }

            FollowPlayheadIntoView();

            // Two layers: the notes/grid/zones go into a cached bitmap keyed on everything
            // that can change them, and only the playhead and the selection rubber band are
            // redrawn live. During playback nothing in the first layer changes, so a playback
            // frame is a blit plus two thin overlays instead of a full chart render. Same
            // trick TimelineV2's static frame uses, for the same reason.
            int blitOffset;
            Bitmap content = EnsureContentFrame(out blitOffset);
            if (content == null)
            {
                ApplyViewTransform(g, _viewablePixels);
                RenderContent(g, _viewablePixels);
                RenderOverlay(g);
                return;
            }

            GraphicsState blitState = g.Save();
            try
            {
                g.ResetTransform();
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(
                    content,
                    new Rectangle(0, 0, DrawingArea.Width, DrawingArea.Height),
                    blitOffset,
                    0,
                    DrawingArea.Width,
                    DrawingArea.Height,
                    GraphicsUnit.Pixel);
            }
            finally
            {
                g.Restore(blitState);
            }

            ApplyViewTransform(g, _viewablePixels);
            RenderOverlay(g);
        }

        /// <summary>
        /// Scrolls so the playhead stays pinned at <see cref="FollowBandAnchor"/> while the chart
        /// flows past it.
        /// </summary>
        /// <remarks>
        /// This paged rather than scrolled for one release, because the content cache was keyed on
        /// the scroll position: moving the view a few pixels 60 times a second meant a full chart
        /// re-render per frame, so holding the grid still was the only affordable option. The
        /// playtest asked for continuous scroll on both surfaces, so the cache became a wider band
        /// that is blitted at an offset (see <see cref="EnsureContentFrame"/>) and a moving view is
        /// no longer expensive. The dead zone is in device pixels rather than ticks: a sub-pixel
        /// change cannot move anything on screen but would still invalidate the band's offset.
        /// </remarks>
        private void FollowPlayheadIntoView()
        {
            if (!IsFollowing) { return; }

            int width = _viewablePixels.Width;
            if (width <= 0) { return; }

            // Virtual x is virtual tick 1:1 in this surface, so the visible tick window is
            // just the viewable rectangle.
            int target = _playerData.VirtualCurrentTick - (int)(width * FollowBandAnchor);
            if (target < 0) { target = 0; }
            if (Math.Abs(target - _viewablePixels.X) * _zoom < FollowDeadZonePixels) { return; }

            ScrollTo((int)(target * _zoom), -1);
            UpdateScrollbars();
        }

        private void ApplyViewTransform(Graphics g, Rectangle viewable)
        {
            // Every primitive in this surface is an axis-aligned rectangle or a 1px grid
            // line, so antialiasing had nothing to smooth: it only cost a coverage pass per
            // edge and left the grid looking soft. None is both crisper and cheaper, which
            // is how ptSequencer's grid reads so sharp.
            g.SmoothingMode = SmoothingMode.None;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.ScaleTransform(_zoom, _zoom, MatrixOrder.Prepend);
            g.TranslateTransform(-viewable.X, -viewable.Y);
        }

        /// <summary>
        /// Rebuilds the cached content band when its key changed; null if it cannot. Reports the
        /// device-pixel offset into the band that the current scroll position reads from.
        /// </summary>
        private Bitmap EnsureContentFrame(out int blitOffset)
        {
            blitOffset = 0;
            int width = DrawingArea.Width;
            int height = DrawingArea.Height;
            if (width <= 0 || height <= 0) { return null; }

            // One definition of the band width, shared with Timeline V2, so the two surfaces
            // cannot drift into different scrolling behaviour.
            int margin = ScrollBand.MarginPixels(width);
            int bandWidth = width + (margin * 2);
            float zoom = Math.Max(0.0001f, _zoom);
            int marginVirtual = Math.Max(1, (int)Math.Ceiling(margin / zoom));

            // Re-anchor only when the view has walked out of the band. Everything else about the
            // band is part of the key below, so a changed zoom or row offset re-renders anyway.
            double offsetPixels = (_viewablePixels.X - _bandOriginVirtualX) * zoom;
            bool bandCoversView = _contentFrame != null &&
                _bandMargin == margin &&
                _contentFrame.Width == bandWidth &&
                _contentFrame.Height == height &&
                offsetPixels >= 0 &&
                offsetPixels <= margin * 2;
            if (!bandCoversView)
            {
                _bandMargin = margin;
                _bandOriginVirtualX = Math.Max(0, _viewablePixels.X - marginVirtual);
            }

            // Integer, because a blit source has to be: the residual is under half a device pixel
            // and shifts the whole frame together, so it cannot tear or shear the grid.
            blitOffset = (int)Math.Round((_viewablePixels.X - _bandOriginVirtualX) * (double)zoom);
            blitOffset = Math.Max(0, Math.Min(bandWidth - width, blitOffset));

            var bandViewable = new Rectangle(
                _bandOriginVirtualX,
                _viewablePixels.Y,
                (int)Math.Ceiling(bandWidth / zoom),
                _viewablePixels.Height);

            string key = string.Join(
                "|",
                bandWidth.ToString(),
                height.ToString(),
                _zoom.ToString("R"),
                _bandOriginVirtualX.ToString(),
                _viewablePixels.Y.ToString(),
                bandViewable.Width.ToString(),
                _viewablePixels.Height.ToString(),
                _noteValue.ToString(),
                ((int)EventsRenderer.EventDisplayMode).ToString(),
                _contentRevision.ToString());

            if (_contentFrame != null && _contentKey == key &&
                _contentFrame.Width == bandWidth && _contentFrame.Height == height)
            {
                return _contentFrame;
            }

            if (_contentFrame == null ||
                _contentFrame.Width != bandWidth || _contentFrame.Height != height)
            {
                ReleaseContentFrame();
                // PArgb: the blit is the hot path, and PArgb is the one format GDI+ copies
                // without a per-pixel alpha conversion.
                _contentFrame = new Bitmap(bandWidth, height, PixelFormat.Format32bppPArgb);
            }

            using (Graphics graphics = Graphics.FromImage(_contentFrame))
            {
                graphics.Clear(DrawingArea.BackColor);
                ApplyViewTransform(graphics, bandViewable);
                RenderContent(graphics, bandViewable);
            }

            _contentKey = key;
            ContentFrameRebuildCount++;
            return _contentFrame;
        }

        private void ReleaseContentFrame()
        {
            if (_contentFrame == null) { return; }
            _contentFrame.Dispose();
            _contentFrame = null;
            _contentKey = null;
        }

        /// <summary>The cacheable layer: tracks, events, zones and the grid.</summary>
        private void RenderContent(Graphics g, Rectangle viewable)
        {
            var gw = m_gw;

            // Bound after the quality modes, so the wrapper knows nearest-neighbour is the
            // mode note art wants back once a label has switched the surface to bilinear.
            gw.UpdateGraphics(g);

            // Renderers work in virtual pixels; this is what they end up scaled by, and it
            // is what decides whether a note label would still be legible on screen.
            gw.LabelScale = _zoom;

            var beatSize = EventData.VirtualTickSize * _playerData.TickPerMinute;
            var blockSize = beatSize / _noteValue;

            TracksRenderer.RenderTracskList(gw, _playerData.Tracks, viewable, beatSize, blockSize, _playerData.VirtualMaxTick, _drawableZone);
        }

        /// <summary>The live layer: playhead and selection rubber band, drawn every frame.</summary>
        private void RenderOverlay(Graphics g)
        {
            var gw = m_gw;
            gw.UpdateGraphics(g);
            gw.LabelScale = _zoom;

            _progress.Position = _playerData.VirtualCurrentTick;
            _progress.Render(gw, _viewablePixels);

            _selectMode?.Render(gw);
        }

        private void EditorControl_KeyPress(object sender, KeyPressEventArgs e) 
        {
            byte countFrom = (byte)'1';
            byte countTo = (byte)'9';
            byte kc = (byte)e.KeyChar;

            if ((kc >= countFrom) && (kc <= countTo)) {
                OnRequestEvent?.Invoke((byte)(kc - countFrom));
            }            
        }

        private void DrawingArea_DoubleClick(object sender, EventArgs e) 
        {
            if (!(e is MouseEventArgs mouseEvent)) { return; }

            _selectMode.MouseDoubleClick(
                (int)(mouseEvent.X / _zoom) + _viewablePixels.X,
                (int)(mouseEvent.Y / _zoom) + _viewablePixels.Y,                 
                mouseEvent.Button
            );
        }

        private void UndoManager_OnUndoRedo(object sender, UndoManager.Action action) 
        {
            if (
                action == UndoManager.Action.Undo ||
                action == UndoManager.Action.Redo
            ) {
                // _selectMode.ClearSelection();
            }

            OnUndoRedo?.Invoke(UndoManager, null);
        }

        private void DrawingArea_Paint(object sender, PaintEventArgs e) 
        {
            DrawToBuffer(e.Graphics);
        }

        public void UpdateScrollbars() 
        {
            hScrollBar.Maximum = (int)Math.Floor(_drawableZone.Width * _zoom);
            hScrollBar.LargeChange = DrawingArea.Width;
            hScrollBar.Value = Math.Min(hScrollBar.Value, Math.Max(hScrollBar.Minimum, hScrollBar.Maximum - hScrollBar.LargeChange));
            hScrollBar.Enabled = hScrollBar.Maximum > hScrollBar.LargeChange;

            vScrollBar.Maximum = (int)Math.Floor(_drawableZone.Height * _zoom);
            vScrollBar.LargeChange = DrawingArea.Height;
            vScrollBar.Value = Math.Min(vScrollBar.Value, Math.Max(vScrollBar.Minimum, vScrollBar.Maximum - vScrollBar.LargeChange));
            vScrollBar.Enabled = vScrollBar.Maximum > vScrollBar.LargeChange;

            _viewablePixels.X = (int)(hScrollBar.Value / _zoom);
            _viewablePixels.Y = (int)(vScrollBar.Value / _zoom);
            _viewablePixels.Width = (int)Math.Ceiling((float)DrawingArea.Width / _zoom);
            _viewablePixels.Height = (int)Math.Ceiling((float)DrawingArea.Height / _zoom);

            SyncTimeRuler();
        }

        private void vScrollBar_ValueChanged(object sender, EventArgs e) 
        {
            if (IsFollowing)
            {
                return;
            }
            UpdateScrollbars();
            DrawingArea.Invalidate();
        }

        private void hScrollBar_ValueChanged(object sender, EventArgs e) 
        {
            if (IsFollowing)
            {
                return;
            }

            UpdateScrollbars();
            DrawingArea.Invalidate();
        }

        private void DrawingArea_MouseDown(object sender, MouseEventArgs e) 
        {
            ActiveControl = null;

            if (null == _selectMode)
            {
                return;
            }

            if (!IsPlayerPlaying)
            {
                Redraw();
            }

            TimelineTool activeTool = _documentContext == null
                ? TimelineTool.Select
                : _documentContext.Interaction.Tool;

            // Alt + left, middle click, or the explicit Pan tool enters viewport movement.
            if (e.Button == MouseButtons.Middle ||
                e.Button == MouseButtons.Left &&
                    (Control.ModifierKeys == Keys.Alt || activeTool == TimelineTool.Pan))
            {
                _drag.Start(e.X, e.Y, 0, 0);
                return;
            }

            int virtualX = (int)(e.X / _zoom) + _viewablePixels.X;
            int virtualY = (int)(e.Y / _zoom) + _viewablePixels.Y;

            if (e.Button == MouseButtons.Left &&
                activeTool == TimelineTool.Draw &&
                _documentContext != null)
            {
                CreateEventAt(virtualX, virtualY);
                return;
            }

            if (e.Button == MouseButtons.Left &&
                activeTool == TimelineTool.Erase &&
                _documentContext != null)
            {
                EventData eraseTarget = _selectMode.GetEventAtPos(virtualX, virtualY);
                if (eraseTarget != null)
                {
                    _documentContext.Selection.Replace(new[] { eraseTarget });
                    _documentContext.Edits.DeleteSelection();
                }
                return;
            }

            if (e.Button == MouseButtons.Left &&
                activeTool == TimelineTool.Resize &&
                _documentContext != null)
            {
                EventData resizeTarget = _selectMode.GetEventAtPos(virtualX, virtualY);
                if (resizeTarget != null && resizeTarget.EventType == EventType.Note)
                {
                    if (!_documentContext.Selection.Items.Contains(resizeTarget))
                    {
                        _documentContext.Selection.Replace(new[] { resizeTarget });
                    }
                    _activeResizeUndoGroup = new object();
                    _activeResizeLastX = _selectMode.EvaluateBlock(virtualX, true);
                    _documentContext.Interaction.Begin(
                        TimelineInteractionKind.ResizingEnd,
                        new TimelineInteractionAnchor(
                            resizeTarget.VirtualTick + resizeTarget.VirtualDuration,
                            (int)resizeTarget.TrackId));
                    Cursor = Cursors.SizeWE;
                }
                return;
            }

            bool leftAndControlPressed =
                e.Button == MouseButtons.Left && Control.ModifierKeys == Keys.Control;
            bool additiveSelection =
                e.Button == MouseButtons.Left &&
                (Control.ModifierKeys == Keys.Shift || leftAndControlPressed);

            _activeMoveUndoGroup = new object();

            if (_selectMode != null)
            {
                bool res = _selectMode.MouseDown(
                    virtualX,
                    virtualY,
                    e.Button,
                    additiveSelection
                );

                if (res)
                {
                    return;
                }                
            }

            if (leftAndControlPressed) {

                if (!CanMutateDocument)
                {
                    return;
                }

                if (TemplateEvent == null)
                {
                    return;
                }

                CreateEventAt(virtualX, virtualY);
                return;
            }

            Focus();
        }

        private void CreateEventAt(int virtualX, int virtualY)
        {
            if (!CanMutateDocument || TemplateEvent == null)
            {
                return;
            }

            int trackIndex =
                _selectMode.EvaluateBlock(virtualY, false) /
                EventsRenderer.VirtualTrackheight;
            int virtualTick = _selectMode.EvaluateBlock(virtualX, true);

            if (_documentContext != null)
            {
                _documentContext.Edits.CreateEvent(
                    TemplateEvent,
                    (uint)Math.Max(0, trackIndex),
                    Math.Max(0, virtualTick));
            }
            else
            {
                var created = TemplateEvent.Clone() as EventData;
                created.VirtualTick = Math.Max(0, virtualTick);
                created.TrackId = (uint)Math.Max(0, trackIndex);
                UndoManager.ExecAction(new AddEventAction(
                    _playerData,
                    new List<EventData> { created }));
            }
            Redraw();
        }

        private void DrawingArea_SizeChanged(object sender, EventArgs e) 
        {
            _ignoreMouse = true;
            hScrollBar.LargeChange = DrawingArea.Width + 16;
            vScrollBar.LargeChange = DrawingArea.Height + 16;
            hScrollBar.Value = Math.Max(0, Math.Min(hScrollBar.Value, hScrollBar.Maximum - hScrollBar.LargeChange));
            vScrollBar.Value = Math.Max(0, Math.Min(vScrollBar.Value, vScrollBar.Maximum - vScrollBar.LargeChange));
        }

        private void DrawingArea_MouseMove(object sender, MouseEventArgs e) 
        {
            if (_ignoreMouse)
            {
                _ignoreMouse = false;
                return;
            }

            var xx = (int)(e.X / _zoom) + _viewablePixels.X;
            var yy = (int)(e.Y / _zoom) + _viewablePixels.Y;

            var shouldReDraw = false;

            if (_drag.Active)
            {
                var newX = e.X;
                var newY = e.Y;
                var xDelta = newX - _drag.X;
                var yDelta = newY - _drag.Y;
                ScrollEditorPixel(hScrollBar.Value - xDelta, vScrollBar.Value - yDelta);
                _drag.X = newX;
                _drag.Y = newY;
                shouldReDraw = true;
            }
            else if (_activeResizeUndoGroup != null &&
                e.Button == MouseButtons.Left &&
                _documentContext != null)
            {
                int snappedX = _selectMode.EvaluateBlock(xx, true);
                int delta = snappedX - _activeResizeLastX;
                if (delta != 0 &&
                    _documentContext.Edits.ResizeSelection(
                        delta,
                        _activeResizeUndoGroup))
                {
                    _activeResizeLastX = snappedX;
                }
                shouldReDraw = true;
            }
            else if ((e.Button == MouseButtons.Left || e.Button == MouseButtons.Right) && _selectMode != null)
            {
                if (_selectMode != null)
                {
                    _selectMode.MouseDrag(xx, yy);
                    shouldReDraw = true;
                }
            }
            else
            {
                if (_documentContext != null &&
                    _documentContext.Interaction.Tool == TimelineTool.Resize)
                {
                    EventData hover = _selectMode.GetEventAtPos(xx, yy);
                    Cursor = hover != null && hover.EventType == EventType.Note
                        ? Cursors.SizeWE
                        : Cursors.Default;
                }
                else
                {
                    _selectMode?.MouseMove(xx, yy);
                }
            }

            if (shouldReDraw && !IsPlayerPlaying)
            {
                Redraw();
            }
        }

        private void DrawingArea_MouseUp(object sender, MouseEventArgs e) 
        {
            _drag.Stop();

            _selectMode?.MouseUp();
            _activeMoveUndoGroup = null;
            _activeResizeUndoGroup = null;
            if (_documentContext != null)
            {
                _documentContext.Interaction.Complete();
            }

            if (!IsPlayerPlaying)
            {
                Redraw();
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) 
        {
            if (_documentContext == null)
            {
                if (keyData != Keys.Delete)
                {
                    return base.ProcessCmdKey(ref msg, keyData);
                }
                if (CanMutateDocument)
                {
                    _selectMode.DeleteSelection();
                }
                return true;
            }

            if (keyData == Keys.Escape)
            {
                _documentContext.Interaction.Cancel();
                _documentContext.Selection.Clear();
                Redraw();
                return true;
            }
            if (keyData == Keys.Delete || keyData == Keys.Back)
            {
                _documentContext.Edits.DeleteSelection();
                return true;
            }
            if (keyData == (Keys.Control | Keys.A))
            {
                SelectAll();
                return true;
            }
            if (keyData == (Keys.Control | Keys.C))
            {
                _documentContext.Clipboard.CopySelection();
                return true;
            }
            if (keyData == (Keys.Control | Keys.X))
            {
                _documentContext.Clipboard.CutSelection();
                return true;
            }
            if (keyData == (Keys.Control | Keys.V))
            {
                _documentContext.Clipboard.PasteAt(_documentContext.Model.VirtualCurrentTick);
                return true;
            }
            if (keyData == (Keys.Control | Keys.D))
            {
                _documentContext.Clipboard.DuplicateSelection(QuantizeVirtualStep);
                return true;
            }

            int multiplier = (keyData & Keys.Shift) == Keys.Shift ? 4 : 1;
            Keys keyCode = keyData & Keys.KeyCode;
            if (keyCode == Keys.Left || keyCode == Keys.Right)
            {
                int direction = keyCode == Keys.Left ? -1 : 1;
                _documentContext.Edits.MoveSelection(
                    0,
                    direction * QuantizeVirtualStep * multiplier);
                return true;
            }
            if (keyCode == Keys.Up || keyCode == Keys.Down)
            {
                int direction = keyCode == Keys.Up ? -1 : 1;
                _documentContext.Edits.MoveSelection(direction * multiplier, 0);
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        #endregion // private defs

        private void DrawingArea_Resize(object sender, EventArgs e)
        {
            UpdateScrollbars();
            DrawingArea.Invalidate();
        }

        private void NoteAddedOrRemoved(object sender, EventArgs e)
        {
            UpdateDrawableZone();
        }

        private void tableLayoutPanel1_Paint(object sender, PaintEventArgs e)
        {

        }
    }
}
