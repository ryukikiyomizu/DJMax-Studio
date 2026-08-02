using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;
using System.Drawing.Drawing2D;
using DJMaxEditor.Undo.Action;
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

            DrawingArea.BackColor = UI.StudioDesignSystem.Void;
            MouseWheel += DrawingArea_MouseWheel;
            DrawingArea.MouseWheel += DrawingArea_MouseWheel;

            SetStyle(ControlStyles.Selectable, true);
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
            DrawingArea.Invalidate();
        }

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

        private bool IsFollowing => FollowTracksProgressWhilePlaying && IsPlayerPlaying && (_playerData.CurrentTick < _playerData.MaxTick);

        private void DrawToBuffer(Graphics g) 
        {
            if (!_ready) { return; }

            var gw = m_gw;
            gw.UpdateGraphics(g);

            // If checked, follow playing track progression
            if (IsFollowing) {

                const int spacing = 150;
                var pos = _playerData.VirtualCurrentTick > spacing ? _playerData.VirtualCurrentTick - spacing : _playerData.VirtualCurrentTick;

                ScrollTo((int)(pos * _zoom), -1);
                UpdateScrollbars();
            }

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.ScaleTransform(_zoom, _zoom, MatrixOrder.Prepend);

            g.TranslateTransform(-_viewablePixels.X, -_viewablePixels.Y);

            if (_playerData == null) {
                return;
            }

            var beatSize = EventData.VirtualTickSize * _playerData.TickPerMinute;
            var blockSize = beatSize / _noteValue;

            TracksRenderer.RenderTracskList(gw, _playerData.Tracks, _viewablePixels, beatSize, blockSize, _playerData.VirtualMaxTick, _drawableZone);
            
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
