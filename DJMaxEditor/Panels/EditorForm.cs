using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using DJMaxEditor.Controls.TimelineV2;
using DJMaxEditor.Editor;
using DJMaxEditor.Panels;
using DJMaxEditor.UI;
using WeifenLuo.WinFormsUI.Docking;

namespace DJMaxEditor 
{
    public partial class EditorForm : DockContent 
    {
        private readonly Panel _surfaceHost;
        private readonly Label _documentStatus;
        private readonly EmptyWorkspaceControl _emptyWorkspace;
        private readonly LegacyEditorSurfaceAdapter _legacySurface;
        private readonly TimelineV2Control _timelineV2Surface;
        private EditorDocumentContext _document;

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
            _documentStatus = new Label
            {
                AutoEllipsis = true,
                BackColor = StudioDesignSystem.Deck,
                Dock = DockStyle.Bottom,
                Font = StudioDesignSystem.UtilityFont(8f),
                ForeColor = StudioDesignSystem.Frost,
                Height = 24,
                Padding = new Padding(8, 5, 8, 0),
                Text = "NO DOCUMENT"
            };
            _emptyWorkspace = new EmptyWorkspaceControl();
            _emptyWorkspace.OpenRequested += delegate
            {
                if (OpenRequested != null) OpenRequested(this, EventArgs.Empty);
            };

            _legacySurface = new LegacyEditorSurfaceAdapter(editorControl1);
            _timelineV2Surface = new TimelineV2Control();
            editorControl1.ViewSettingsChanged += EditorControl_ViewSettingsChanged;
            SyncTimelineV2Settings();
            PrepareSurface(_legacySurface);
            PrepareSurface(_timelineV2Surface);

            Controls.Add(_surfaceHost);
            Controls.Add(_documentStatus);
            Controls.Add(_emptyWorkspace);
            _emptyWorkspace.BringToFront();
            _documentStatus.BringToFront();

            ActiveSurface = EditorSurfaceSelection.Resolve(useTimelineV2) ==
                EditorSurfaceKind.TimelineV2
                    ? (IEditorSurface)_timelineV2Surface
                    : _legacySurface;
            ShowActiveSurface();
        }

        public IEditorSurface ActiveSurface { get; private set; }

        public EditorDocumentContext Document
        {
            get { return _document; }
        }

        public event EventHandler OpenRequested;
        public event EventHandler ActiveSurfaceChanged;

        public bool IsLegacySurfaceActive
        {
            get { return object.ReferenceEquals(ActiveSurface, _legacySurface); }
        }

        public string DocumentStatusText
        {
            get { return _documentStatus.Text; }
        }

        public EditorControl Editor 
        {
            get
            {
                return editorControl1;
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
            UpdateDocumentStatus();
        }

        public void SwitchSurface(bool useTimelineV2)
        {
            IEditorSurface requested = EditorSurfaceSelection.Resolve(useTimelineV2) ==
                EditorSurfaceKind.TimelineV2
                    ? (IEditorSurface)_timelineV2Surface
                    : _legacySurface;
            if (object.ReferenceEquals(ActiveSurface, requested))
            {
                return;
            }

            EditorViewState state = ActiveSurface.CaptureViewState();
            ActiveSurface = requested;
            if (_document != null)
            {
                ActiveSurface.Bind(_document);
                ActiveSurface.RestoreViewState(state);
            }
            ShowActiveSurface();
            UpdateDocumentStatus();
            if (ActiveSurfaceChanged != null) ActiveSurfaceChanged(this, EventArgs.Empty);
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
            SyncTimelineV2Settings();
        }

        private void SyncTimelineV2Settings()
        {
            _timelineV2Surface.QuantizeDivision = editorControl1.NoteValue;
            _timelineV2Surface.EventTheme = editorControl1.CurrentEventsTheme;
            _timelineV2Surface.ZoneTheme = editorControl1.CurrentZonesTheme;
            _timelineV2Surface.EventDisplayMode = editorControl1.EventDisplayMode;
            _timelineV2Surface.FollowPlayback =
                editorControl1.FollowTracksProgressWhilePlaying;
            _timelineV2Surface.IsPlaybackActive = editorControl1.IsPlayerPlaying;
            _timelineV2Surface.InvalidateView();
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
                _documentStatus.Text = "NO DOCUMENT";
                return;
            }

            string source = _document.Capabilities.SourceFormat.HasValue
                ? _document.Capabilities.SourceFormat.Value.ToString()
                : "Unknown format";
            string encryption = _document.Capabilities.IsEncrypted
                ? " | encrypted source, decrypted in memory"
                : string.Empty;
            string surface = IsLegacySurfaceActive
                ? "TIMELINE V1"
                : (ActiveSurface.SupportsEditing ? "TIMELINE V2" : "TIMELINE V2 - EDITING ROLLOUT");
            _documentStatus.Text = source + encryption + " | " + surface + " | " +
                _document.Capabilities.StatusLabel;

            bool locked = _document.Capabilities.IsReadOnly || !ActiveSurface.SupportsEditing;
            _documentStatus.BackColor = locked
                ? StudioDesignSystem.Lift
                : StudioDesignSystem.Deck;
        }
    }
}
