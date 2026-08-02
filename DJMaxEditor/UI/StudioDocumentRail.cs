using System;
using System.Drawing;
using System.Windows.Forms;

namespace DJMaxEditor.UI
{
    public sealed class StudioDocumentRail : UserControl
    {
        private readonly Label _documentLabel;
        private readonly Label _formatChip;
        private readonly Label _capabilityChip;
        private readonly Button _timelineV1;
        private readonly Button _timelineV2;
        private readonly Button _preview;
        private readonly Button _workspace;
        private readonly Button _palette;
        private readonly ContextMenuStrip _workspaceMenu;

        public StudioDocumentRail()
        {
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = StudioDesignSystem.Deck;
            Dock = DockStyle.Top;
            Height = 44;
            MinimumSize = new Size(760, 44);
            Padding = new Padding(12, 5, 10, 5);

            var brand = CreateLabel("DJMAX  //  CHART STUDIO", 188, StudioDesignSystem.PulseCyan);
            brand.Font = StudioDesignSystem.DisplayFont(10f);

            _documentLabel = CreateLabel("NO DOCUMENT", 230, StudioDesignSystem.Frost);
            _documentLabel.AutoEllipsis = true;
            _documentLabel.Font = StudioDesignSystem.BodyFont(9f, FontStyle.Bold);

            _formatChip = CreateChip("NO SOURCE", StudioDesignSystem.Muted);
            _capabilityChip = CreateChip("OPEN A CHART", StudioDesignSystem.SignalAmber);

            _timelineV1 = CreateRailButton("V1");
            _timelineV2 = CreateRailButton("V2");
            _preview = CreateRailButton("PREVIEW", 76);
            _workspace = CreateRailButton("WORKSPACE  ▾", 104);
            _palette = CreateRailButton("COMMANDS  Ctrl+K", 132);
            _workspaceMenu = BuildWorkspaceMenu();

            _timelineV1.Click += delegate { if (TimelineV1Requested != null) TimelineV1Requested(this, EventArgs.Empty); };
            _timelineV2.Click += delegate { if (TimelineV2Requested != null) TimelineV2Requested(this, EventArgs.Empty); };
            _preview.Click += delegate { if (PreviewRequested != null) PreviewRequested(this, EventArgs.Empty); };
            _workspace.Click += delegate
            {
                _workspaceMenu.Show(_workspace, new Point(0, _workspace.Height));
            };
            _palette.Click += delegate { if (CommandPaletteRequested != null) CommandPaletteRequested(this, EventArgs.Empty); };

            var right = new FlowLayoutPanel
            {
                AutoSize = false,
                BackColor = StudioDesignSystem.Deck,
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.LeftToRight,
                Height = 34,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                Width = 690,
                WrapContents = false
            };
            right.Controls.Add(_formatChip);
            right.Controls.Add(_capabilityChip);
            right.Controls.Add(_timelineV1);
            right.Controls.Add(_timelineV2);
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
        }

        public event EventHandler TimelineV1Requested;
        public event EventHandler TimelineV2Requested;
        public event EventHandler PreviewRequested;
        public event EventHandler<StudioWorkspaceRequestedEventArgs> WorkspaceRequested;
        public event EventHandler CommandPaletteRequested;

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

        public void ShowEmpty()
        {
            ShowDocument("NO DOCUMENT", "TIMELINE V1", "NO SOURCE", "OPEN A CHART", true);
        }

        public void ShowDocument(
            string documentName,
            string surfaceName,
            string formatText,
            string capabilityText,
            bool isLocked)
        {
            DocumentName = string.IsNullOrWhiteSpace(documentName) ? "UNTITLED" : documentName;
            SurfaceName = string.IsNullOrWhiteSpace(surfaceName) ? "TIMELINE V1" : surfaceName;
            CapabilityText = string.IsNullOrWhiteSpace(capabilityText) ? "UNKNOWN" : capabilityText;
            IsLocked = isLocked;

            _documentLabel.Text = DocumentName;
            _formatChip.Text = formatText ?? "UNKNOWN";
            _capabilityChip.Text = CapabilityText;
            _capabilityChip.ForeColor = isLocked
                ? StudioDesignSystem.SignalAmber
                : StudioDesignSystem.PulseCyan;
            SetActiveSurface(SurfaceName.IndexOf("V2", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public void SetActiveSurface(bool timelineV2)
        {
            SurfaceName = timelineV2 ? "TIMELINE V2" : "TIMELINE V1";
            StyleSegment(_timelineV1, !timelineV2);
            StyleSegment(_timelineV2, timelineV2);
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
            AddWorkspaceItem(menu, "EDITING", StudioWorkspacePreset.Editing);
            AddWorkspaceItem(menu, "PREVIEW", StudioWorkspacePreset.Preview);
            AddWorkspaceItem(menu, "AUDIO", StudioWorkspacePreset.Audio);
            AddWorkspaceItem(menu, "COMPACT", StudioWorkspacePreset.Compact);
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

        private static Button CreateRailButton(string text, int width = 38)
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
