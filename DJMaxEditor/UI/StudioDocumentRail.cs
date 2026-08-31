using System;
using System.Drawing;
using System.Windows.Forms;

namespace DJMaxEditor.UI
{
    /// <summary>
    /// The top document rail: what is open, what it is, and the handful of controls that change
    /// the whole workspace.
    /// <para>
    /// It used to end in five chips and buttons crammed against the right edge — a 112px
    /// capability chip plus two 38px segments whose "V1"/"V2" labels clipped to a bare "V". The
    /// playtest read that as three broken boxes. Now there is one surface button that names the
    /// surface it will switch to, capability lives in the bottom status rail (which already
    /// printed it), and the reclaimed room carries the vertical-strip toggle.
    /// </para>
    /// </summary>
    public sealed class StudioDocumentRail : UserControl
    {
        private readonly Label _documentLabel;
        private readonly Label _formatChip;
        private readonly Button _surfaceToggle;
        private readonly Button _verticalToggle;
        private readonly Button _preview;
        private readonly Button _workspace;
        private readonly Button _palette;
        private readonly ContextMenuStrip _workspaceMenu;
        private bool _timelineV2Active;
        private bool _verticalEnabled = true;

        public StudioDocumentRail()
        {
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = StudioDesignSystem.Deck;
            Dock = DockStyle.Top;
            Height = 44;
            MinimumSize = new Size(640, 44);
            Padding = new Padding(12, 5, 10, 5);

            // A product name, not a slogan. "DJMAX // CHART STUDIO" in accent cyan was the
            // loudest thing in the window and said nothing the title bar did not.
            var brand = CreateLabel("DJMax Chart Studio", 150, StudioDesignSystem.Frost);
            brand.Font = StudioDesignSystem.DisplayFont(10f);

            _documentLabel = CreateLabel("No document", 230, StudioDesignSystem.Frost);
            _documentLabel.AutoEllipsis = true;
            _documentLabel.Font = StudioDesignSystem.BodyFont(9f, FontStyle.Bold);
            // Fill, not a fixed 230px: the chart name is the one thing here whose length is not
            // known in advance, so it gets whatever the brand and the button cluster leave and
            // ellipsizes instead of being overlapped by them.
            _documentLabel.Dock = DockStyle.Fill;

            _formatChip = CreateChip("No source", StudioDesignSystem.Muted);

            _surfaceToggle = CreateRailButton("TIMELINE V1", 106);
            _verticalToggle = CreateRailButton("Vertical", 80);
            _preview = CreateRailButton("Preview", 76);
            _workspace = CreateRailButton("Workspace  ▾", 104);
            _palette = CreateRailButton("Commands  Ctrl+K", 130);
            _workspaceMenu = BuildWorkspaceMenu();

            _surfaceToggle.Click += delegate { ToggleSurface(); };
            _verticalToggle.Click += delegate { ToggleVerticalTimeline(); };
            _preview.Click += delegate { if (PreviewRequested != null) PreviewRequested(this, EventArgs.Empty); };
            _workspace.Click += delegate
            {
                _workspaceMenu.Show(_workspace, new Point(0, _workspace.Height));
            };
            _palette.Click += delegate { if (CommandPaletteRequested != null) CommandPaletteRequested(this, EventArgs.Empty); };

            // Auto-sized rather than a hard 640px. The fixed width was wider than the buttons in
            // it, and it docked Right over the top of the document name on any window narrower
            // than about 1060px - which is how the rail ended up looking crammed.
            var right = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = StudioDesignSystem.Deck,
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.LeftToRight,
                Height = 34,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                WrapContents = false
            };
            right.Controls.Add(_formatChip);
            right.Controls.Add(_surfaceToggle);
            right.Controls.Add(_verticalToggle);
            right.Controls.Add(_preview);
            right.Controls.Add(_workspace);
            right.Controls.Add(_palette);

            Controls.Add(_documentLabel);
            Controls.Add(right);
            Controls.Add(brand);
            right.BringToFront();
            _documentLabel.BringToFront();

            ShowEmpty();
            SetActiveSurface(false);
            SetVerticalTimelineEnabled(true);
        }

        public event EventHandler TimelineV1Requested;
        public event EventHandler TimelineV2Requested;
        public event EventHandler PreviewRequested;
        public event EventHandler<StudioWorkspaceRequestedEventArgs> WorkspaceRequested;
        public event EventHandler CommandPaletteRequested;

        /// <summary>
        /// Raised when the rail's VERTICAL button is pressed. The argument is the state the user
        /// is asking for, so the shell can refuse (no layout for the chart) without the rail
        /// having to know why.
        /// </summary>
        public event EventHandler<StudioToggleRequestedEventArgs> VerticalTimelineRequested;

        public StudioWorkspacePreset[] WorkspacePresets
        {
            get
            {
                return new[]
                {
                    StudioWorkspacePreset.Editing,
                    StudioWorkspacePreset.Preview,
                    StudioWorkspacePreset.Audio,
                    StudioWorkspacePreset.Compact
                };
            }
        }

        public string DocumentName { get; private set; }
        public string SurfaceName { get; private set; }
        public string CapabilityText { get; private set; }
        public bool IsLocked { get; private set; }

        /// <summary>True while the rail is showing the vertical strip as switched on.</summary>
        public bool IsVerticalTimelineEnabled
        {
            get { return _verticalEnabled; }
        }

        /// <summary>The label the surface button currently shows.</summary>
        public string SurfaceButtonText
        {
            get { return _surfaceToggle.Text; }
        }

        public void ShowEmpty()
        {
            ShowDocument("No document", "TIMELINE V1", "No source", "Open a chart", true);
        }

        public void ShowDocument(
            string documentName,
            string surfaceName,
            string formatText,
            string capabilityText,
            bool isLocked)
        {
            DocumentName = string.IsNullOrWhiteSpace(documentName) ? "Untitled" : documentName;
            SurfaceName = string.IsNullOrWhiteSpace(surfaceName) ? "TIMELINE V1" : surfaceName;
            CapabilityText = string.IsNullOrWhiteSpace(capabilityText) ? "Unknown" : capabilityText;
            IsLocked = isLocked;

            _documentLabel.Text = DocumentName;
            _formatChip.Text = formatText ?? "Unknown";
            // The format chip carries the lock cue now that the capability chip is gone: amber
            // means "you are looking, not editing".
            _formatChip.ForeColor = isLocked
                ? StudioDesignSystem.SignalAmber
                : StudioDesignSystem.Muted;
            SetActiveSurface(SurfaceName.IndexOf("V2", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public void SetActiveSurface(bool timelineV2)
        {
            _timelineV2Active = timelineV2;
            SurfaceName = timelineV2 ? "TIMELINE V2" : "TIMELINE V1";
            _surfaceToggle.Text = SurfaceName;
            // The button always names the surface you are on, and pressing it moves you to the
            // other one; the tooltip is what says where you are going.
            _surfaceToggle.AccessibleName = SurfaceName;
            _surfaceToggle.AccessibleDescription =
                "Switch to " + (timelineV2 ? "Timeline V1" : "Timeline V2");
            StyleSegment(_surfaceToggle, true);
        }

        /// <summary>Reflects the shell's vertical-strip state on the button.</summary>
        public void SetVerticalTimelineEnabled(bool enabled)
        {
            _verticalEnabled = enabled;
            _verticalToggle.AccessibleName = "Vertical timeline";
            _verticalToggle.AccessibleDescription = enabled
                ? "Hide the vertical timeline"
                : "Show the vertical timeline";
            StyleSegment(_verticalToggle, enabled);
        }

        private void ToggleSurface()
        {
            if (_timelineV2Active)
            {
                if (TimelineV1Requested != null) TimelineV1Requested(this, EventArgs.Empty);
            }
            else if (TimelineV2Requested != null)
            {
                TimelineV2Requested(this, EventArgs.Empty);
            }
        }

        private void ToggleVerticalTimeline()
        {
            EventHandler<StudioToggleRequestedEventArgs> handler = VerticalTimelineRequested;
            if (handler != null)
            {
                handler(this, new StudioToggleRequestedEventArgs(!_verticalEnabled));
            }
        }

        public void RequestWorkspace(StudioWorkspacePreset preset)
        {
            EventHandler<StudioWorkspaceRequestedEventArgs> handler = WorkspaceRequested;
            if (handler != null)
            {
                handler(this, new StudioWorkspaceRequestedEventArgs(preset));
            }
        }

        private ContextMenuStrip BuildWorkspaceMenu()
        {
            var menu = new ContextMenuStrip
            {
                BackColor = StudioDesignSystem.Deck,
                Font = StudioDesignSystem.BodyFont(9f),
                ForeColor = StudioDesignSystem.Frost,
                ShowImageMargin = false
            };
            AddWorkspaceItem(menu, "Editing", StudioWorkspacePreset.Editing);
            AddWorkspaceItem(menu, "Preview", StudioWorkspacePreset.Preview);
            AddWorkspaceItem(menu, "Audio", StudioWorkspacePreset.Audio);
            AddWorkspaceItem(menu, "Compact", StudioWorkspacePreset.Compact);
            return menu;
        }

        private void AddWorkspaceItem(
            ContextMenuStrip menu,
            string label,
            StudioWorkspacePreset preset)
        {
            var item = new ToolStripMenuItem(label)
            {
                BackColor = StudioDesignSystem.Deck,
                ForeColor = StudioDesignSystem.Frost
            };
            item.Click += delegate { RequestWorkspace(preset); };
            menu.Items.Add(item);
        }

        private static Label CreateLabel(string text, int width, Color foreground)
        {
            return new Label
            {
                BackColor = StudioDesignSystem.Deck,
                Dock = DockStyle.Left,
                ForeColor = foreground,
                Height = 34,
                Margin = Padding.Empty,
                Padding = new Padding(0, 8, 8, 0),
                Text = text,
                Width = width
            };
        }

        private static Label CreateChip(string text, Color foreground)
        {
            return new Label
            {
                AutoEllipsis = true,
                BackColor = StudioDesignSystem.Lift,
                BorderStyle = BorderStyle.FixedSingle,
                Font = StudioDesignSystem.UtilityFont(7.5f),
                ForeColor = foreground,
                Height = 27,
                Margin = new Padding(3, 3, 3, 3),
                Padding = new Padding(8, 5, 8, 0),
                Text = text,
                TextAlign = ContentAlignment.TopCenter,
                Width = 112
            };
        }

        /// <summary>
        /// A rail button. The default is wide enough for a two-word label: the old 38px default
        /// clipped "V1" down to "V", which is how the playtest ended up looking at two unlabelled
        /// boxes.
        /// </summary>
        private static Button CreateRailButton(string text, int width = 96)
        {
            Button button = StudioDesignSystem.CreateDeckButton(text);
            button.Height = 28;
            button.Margin = new Padding(2, 3, 2, 3);
            button.Width = width;
            return button;
        }

        private static void StyleSegment(Button button, bool active)
        {
            button.BackColor = active ? StudioDesignSystem.Selected : StudioDesignSystem.Lift;
            button.ForeColor = active ? StudioDesignSystem.PulseCyan : StudioDesignSystem.Muted;
            button.FlatAppearance.BorderColor = active
                ? StudioDesignSystem.PulseCyan
                : StudioDesignSystem.Border;
        }
    }
}
