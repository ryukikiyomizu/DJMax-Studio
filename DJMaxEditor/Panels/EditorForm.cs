using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using DJMaxEditor.Controls.TimelineV2;
using DJMaxEditor.Controls.Vertical;
using DJMaxEditor.Editor;
using DJMaxEditor.Panels;
using DJMaxEditor.UI;
using WeifenLuo.WinFormsUI.Docking;

namespace DJMaxEditor
{
    public partial class EditorForm : DockContent
    {
        /// <summary>
        /// Default width of the vertical ptSequencer strip, in pixels: wide enough that every
        /// gameplay column of the widest supported mode (8B) is visible at the minimum column
        /// scale. Derived instead of hard-coded, because the previous fixed 320px silently
        /// clipped the right-hand columns of an 8B chart.
        /// </summary>
        private static readonly int DefaultVerticalWidth =
            Math.Max(320, VerticalTimelineControl.PreferredWidthForMode(8));

        private readonly Panel _surfaceHost;
        private readonly EmptyWorkspaceControl _emptyWorkspace;
        private readonly LegacyEditorSurfaceAdapter _legacySurface;
        private readonly TimelineV2Control _timelineV2Surface;
        private readonly VerticalTimelineControl _verticalSurface;
        private readonly Splitter _verticalSplitter;
        private IEditorSurface _activeHorizontal;
        private EditorDocumentContext _document;
        private bool _verticalEnabled = true;

        /// <summary>
        /// Backing store for <see cref="DocumentStatusText"/>. This used to be the text of a
        /// 24px strip pinned to the bottom of the editor panel, which the playtest asked to be
        /// removed: it duplicated the shell status rail and stole a row of chart from every
        /// document. The string is still computed because callers and diagnostics read it.
        /// </summary>
        private string _documentStatusText = NoDocumentStatus;

        private const string NoDocumentStatus = "NO DOCUMENT";

        public string Title {
            get {
                return this.Text;
            }
            set {
                this.Text = value;
            }
        }

        public EditorForm()
            : this(FeatureFlags.UseTimelineV2)
        {
        }

        public EditorForm(bool useTimelineV2)
        {
            InitializeComponent();

            Controls.Remove(editorControl1);
            _surfaceHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = StudioDesignSystem.Void
            };
            _emptyWorkspace = new EmptyWorkspaceControl();
            _emptyWorkspace.OpenRequested += delegate
            {
                if (OpenRequested != null) OpenRequested(this, EventArgs.Empty);
            };

            _legacySurface = new LegacyEditorSurfaceAdapter(editorControl1);
            _timelineV2Surface = new TimelineV2Control();
            _verticalSurface = new VerticalTimelineControl
            {
                Dock = DockStyle.Left,
                Width = DefaultVerticalWidth,
                Visible = false
            };
            _verticalSurface.SeekRequested += Surface_SeekRequested;
            _verticalSurface.LayoutAvailabilityChanged += VerticalSurface_LayoutAvailabilityChanged;
            // V2's ruler raises the same event type, so both surfaces share one seek path.
            _timelineV2Surface.SeekRequested += Surface_SeekRequested;
            // V1 has a ruler of its own now, and it seeks through the same path so the two
            // horizontal surfaces cannot disagree about where the playhead is.
            editorControl1.SeekRequested += Surface_SeekRequested;
            _verticalSplitter = new Splitter
            {
                Dock = DockStyle.Left,
                BackColor = StudioDesignSystem.Border,
                MinExtra = 160,
                MinSize = 96,
                Width = 4,
                Visible = false
            };
            editorControl1.ViewSettingsChanged += EditorControl_ViewSettingsChanged;
            SyncSecondarySurfaceSettings();
            PrepareSurface(_legacySurface);
            PrepareSurface(_timelineV2Surface);

            Controls.Add(_surfaceHost);
            Controls.Add(_emptyWorkspace);
            Controls.Add(_verticalSurface);
            Controls.Add(_verticalSplitter);

            // Dock order is front-to-back, so set it explicitly: the vertical timeline and its
            // splitter claim the left edge, and the horizontal surface host fills whatever is
            // left. The empty workspace overlays the fill area while no document is open.
            Controls.SetChildIndex(_verticalSurface, 0);
            Controls.SetChildIndex(_verticalSplitter, 1);
            Controls.SetChildIndex(_emptyWorkspace, 2);
            Controls.SetChildIndex(_surfaceHost, 3);

            SetActiveHorizontal(
                EditorSurfaceSelection.Resolve(useTimelineV2) == EditorSurfaceKind.TimelineV2
                    ? (IEditorSurface)_timelineV2Surface
                    : _legacySurface);
            ShowActiveSurface();
        }

        /// <summary>Raised when the vertical timeline's ruler is used to move the playhead.</summary>
        public event EventHandler<VerticalSeekEventArgs> PlayheadSeekRequested;

        /// <summary>
        /// The surface the shell drives. It fans every command out to the active horizontal
        /// editor and to the vertical ptSequencer timeline, so both editor paths stay
        /// synchronized without any caller needing to know the vertical surface exists.
        /// </summary>
        public IEditorSurface ActiveSurface { get; private set; }

        /// <summary>The active horizontal editor on its own (V1 or V2).</summary>
        public IEditorSurface ActiveHorizontalSurface
        {
            get { return _activeHorizontal; }
        }

        public VerticalTimelineControl VerticalTimeline
        {
            get { return _verticalSurface; }
        }

        /// <summary>True when the vertical strip is enabled and has a layout to draw.</summary>
        public bool IsVerticalTimelineVisible
        {
            get { return _verticalSurface.Visible; }
        }

        public bool VerticalTimelineEnabled
        {
            get { return _verticalEnabled; }
            set
            {
                if (_verticalEnabled == value) return;
                _verticalEnabled = value;
                UpdateVerticalVisibility();
            }
        }

        public EditorDocumentContext Document
        {
            get { return _document; }
        }

        public event EventHandler OpenRequested;
        public event EventHandler ActiveSurfaceChanged;

        public bool IsLegacySurfaceActive
        {
            get { return object.ReferenceEquals(_activeHorizontal, _legacySurface); }
        }

        /// <summary>
        /// The document/surface/capability summary. No longer painted inside the editor panel —
        /// the shell status rail owns that real estate — but still computed so tests, logs, and
        /// the shell can read one canonical description of what is loaded.
        /// </summary>
        public string DocumentStatusText
        {
            get { return _documentStatusText; }
        }

        public EditorControl Editor
        {
            get
            {
                return editorControl1;
            }
        }

        /// <summary>
        /// The prototype the Draw tool clones, fanned out to every editable surface. The Notes
        /// palette used to poke <c>Editor.TemplateEvent</c> directly, which meant drawing worked
        /// on V1 and silently did nothing on V2; setting it here keeps both in step.
        /// </summary>
        public DJMax.EventData TemplateEvent
        {
            get { return editorControl1.TemplateEvent; }
            set
            {
                editorControl1.TemplateEvent = value;
                _timelineV2Surface.TemplateEvent = value;
            }
        }

        public string[] EventThemeNames
        {
            get
            {
                return editorControl1.EventsThemeList
                    .Select(theme => theme.GetName())
                    .ToArray();
            }
        }

        public string[] ZoneThemeNames
        {
            get
            {
                return editorControl1.ZonesThemeList
                    .Select(theme => theme.GetName())
                    .ToArray();
            }
        }

        public string ActiveEventThemeName
        {
            get
            {
                return editorControl1.CurrentEventsTheme == null
                    ? string.Empty
                    : editorControl1.CurrentEventsTheme.GetName();
            }
        }

        public string ActiveZoneThemeName
        {
            get
            {
                return editorControl1.CurrentZonesTheme == null
                    ? string.Empty
                    : editorControl1.CurrentZonesTheme.GetName();
            }
        }

        public int QuantizeDivision
        {
            get { return editorControl1.NoteValue; }
        }

        public bool SetEventTheme(string name)
        {
            var theme = editorControl1.EventsThemeList.FirstOrDefault(
                candidate => string.Equals(
                    candidate.GetName(),
                    name,
                    StringComparison.OrdinalIgnoreCase));
            if (theme == null)
                return false;
            editorControl1.CurrentEventsTheme = theme;
            return true;
        }

        public bool SetZoneTheme(string name)
        {
            var theme = editorControl1.ZonesThemeList.FirstOrDefault(
                candidate => string.Equals(
                    candidate.GetName(),
                    name,
                    StringComparison.OrdinalIgnoreCase));
            if (theme == null)
                return false;
            editorControl1.CurrentZonesTheme = theme;
            return true;
        }

        public void SetQuantizeDivision(int division)
        {
            editorControl1.NoteValue = Math.Max(1, division);
        }

        public void Bind(EditorDocumentContext document)
        {
            if (document == null) throw new ArgumentNullException("document");
            _document = document;
            ActiveSurface.Bind(document);
            _emptyWorkspace.Visible = false;
            UpdateVerticalVisibility();
            UpdateDocumentStatus();
        }

        public void SwitchSurface(bool useTimelineV2)
        {
            IEditorSurface requested = EditorSurfaceSelection.Resolve(useTimelineV2) ==
                EditorSurfaceKind.TimelineV2
                    ? (IEditorSurface)_timelineV2Surface
                    : _legacySurface;
            if (object.ReferenceEquals(_activeHorizontal, requested))
            {
                return;
            }

            EditorViewState state = ActiveSurface.CaptureViewState();
            SetActiveHorizontal(requested);
            if (_document != null)
            {
                ActiveSurface.Bind(_document);
                ActiveSurface.RestoreViewState(state);
            }
            ShowActiveSurface();
            UpdateVerticalVisibility();
            UpdateDocumentStatus();
            if (ActiveSurfaceChanged != null) ActiveSurfaceChanged(this, EventArgs.Empty);
        }

        private void SetActiveHorizontal(IEditorSurface horizontal)
        {
            _activeHorizontal = horizontal;
            ActiveSurface = new SynchronizedEditorSurface(horizontal, _verticalSurface);
        }

        /// <summary>
        /// The vertical strip only takes space when it is enabled, a document is open, and
        /// the chart actually resolves to a 4B/5B/6B/8B layout.
        /// </summary>
        private void UpdateVerticalVisibility()
        {
            bool visible = _verticalEnabled && _document != null && _verticalSurface.HasLayout;
            _verticalSurface.Visible = visible;
            _verticalSplitter.Visible = visible;
            if (visible)
            {
                _verticalSurface.InvalidateView();
            }
            if (_activeHorizontal != null)
            {
                _activeHorizontal.InvalidateView();
            }
        }

        /// <summary>
        /// A click on the vertical gutter or on either horizontal ruler is a real seek: it moves
        /// the document tick, every other surface, and (through
        /// <see cref="PlayheadSeekRequested"/>) the audio player, so no surface is left showing a
        /// stale playhead. One handler for all three so a seek behaves identically wherever the
        /// user clicked.
        /// </summary>
        private void Surface_SeekRequested(object sender, VerticalSeekEventArgs e)
        {
            if (_document != null)
            {
                _document.Model.CurrentTick = e.VirtualTick / DJMax.EventData.VirtualTickSize;
            }
            if (_activeHorizontal != null &&
                !object.ReferenceEquals(sender, _activeHorizontal))
            {
                _activeHorizontal.PlayheadVirtualTick = e.VirtualTick;
            }
            if (!object.ReferenceEquals(sender, _verticalSurface))
            {
                _verticalSurface.PlayheadVirtualTick = e.VirtualTick;
            }
            if (PlayheadSeekRequested != null)
            {
                PlayheadSeekRequested(this, e);
            }
        }

        private void VerticalSurface_LayoutAvailabilityChanged(object sender, EventArgs e)
        {
            UpdateVerticalVisibility();
        }

        private void EditorForm_Resize(object sender, EventArgs e) 
        {
            if (ActiveSurface != null)
            {
                ActiveSurface.InvalidateView();
            }
        }

        private void EditorControl_ViewSettingsChanged(object sender, EventArgs e)
        {
            SyncSecondarySurfaceSettings();
        }

        /// <summary>
        /// Pushes the V1 editor's view settings onto every follower surface. Zoom is
        /// included so the V2 timeline and the vertical ptSequencer strip stay at the same
        /// scale as the legacy editor instead of drifting after a zoom change.
        /// </summary>
        private void SyncSecondarySurfaceSettings()
        {
            _timelineV2Surface.QuantizeDivision = editorControl1.NoteValue;
            _timelineV2Surface.EventTheme = editorControl1.CurrentEventsTheme;
            _timelineV2Surface.ZoneTheme = editorControl1.CurrentZonesTheme;
            _timelineV2Surface.EventDisplayMode = editorControl1.EventDisplayMode;
            _timelineV2Surface.FollowPlayback =
                editorControl1.FollowTracksProgressWhilePlaying;
            _timelineV2Surface.IsPlaybackActive = editorControl1.IsPlayerPlaying;
            _timelineV2Surface.InvalidateView();

            _verticalSurface.FollowPlayback = editorControl1.FollowTracksProgressWhilePlaying;
            _verticalSurface.IsPlaybackActive = editorControl1.IsPlayerPlaying;
            _verticalSurface.TrySetTimeZoom(editorControl1.GetZoom());
            _verticalSurface.InvalidateView();
        }

        public void SelectAll()
        {
            if (IsLegacySurfaceActive)
            {
                editorControl1.SelectAll();
            }
        }

        public void Deselect()
        {
            if (IsLegacySurfaceActive)
            {
                editorControl1.Deselect();
            }
        }

        public void InverseSelection()
        {
            if (IsLegacySurfaceActive)
            {
                editorControl1.InverseSelection();
            }
        }

        private void PrepareSurface(IEditorSurface surface)
        {
            surface.View.Dock = DockStyle.Fill;
            surface.View.Visible = false;
            _surfaceHost.Controls.Add(surface.View);
        }

        private void ShowActiveSurface()
        {
            _legacySurface.View.Visible = IsLegacySurfaceActive;
            _timelineV2Surface.View.Visible = !IsLegacySurfaceActive;
            ActiveSurface.View.BringToFront();
        }

        private void UpdateDocumentStatus()
        {
            if (_document == null)
            {
                _documentStatusText = NoDocumentStatus;
                return;
            }

            string source = _document.Capabilities.SourceFormat.HasValue
                ? _document.Capabilities.SourceFormat.Value.ToString()
                : "Unknown format";
            string encryption = _document.Capabilities.IsEncrypted
                ? " | encrypted source, decrypted in memory"
                : string.Empty;
            string surface = IsLegacySurfaceActive ? "TIMELINE V1" : "TIMELINE V2";
            if (!ActiveSurface.SupportsEditing)
            {
                surface += " - VIEW ONLY";
            }

            _documentStatusText = source + encryption + " | " + surface + " | " +
                _document.Capabilities.StatusLabel;
        }
    }
}
