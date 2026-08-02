// #define ENABLE_EVENT_FORM

using System;
using System.Linq;
using System.Windows.Forms;
using System.IO;
using WeifenLuo.WinFormsUI.Docking;
using System.Threading;
using DJMaxEditor.Undo.Action;
using DJMaxEditor.PropertyLayer;
using DJMaxEditor.Controls.Editor.Renderers.Events;
using DJMaxEditor.DJMax;
using System.Collections.Generic;
using DJMaxEditor.Panels;
using DJMaxEditor.Files;
using DJMaxEditor.Controls.Editor.Renderers.Zones;
using DJMaxEditor.Files.pt;
using DJMaxEditor.Files.bytes;
using DJMaxEditor.Files.bms;
using DJMaxEditor.Files.Cyclon;
using DJMaxEditor.Editor;
using DJMaxEditor.Editor.Commands;
using DJMaxEditor.Preview;
using DJMaxEditor.UI;

namespace DJMaxEditor
{
    public partial class MainForm : Form
    {
        #region public defs

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!IsTextEntryFocused())
            {
                string disabledExplanation;
                if (m_commands.TryExecuteShortcut(
                    keyData, GetActiveCommandContext(), out disabledExplanation))
                {
                    return true;
                }
                if (!string.IsNullOrEmpty(disabledExplanation))
                {
                    SetStudioStatus(disabledExplanation);
                    return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private StudioCommandContext GetActiveCommandContext()
        {
            StudioCommandContext context = StudioCommandContext.Global |
                StudioCommandContext.Timeline |
                StudioCommandContext.Playback;
            if (_documentContext != null)
            {
                context |= StudioCommandContext.Document;
                if (_documentContext.Selection.Count > 0)
                {
                    context |= StudioCommandContext.Selection;
                }
            }
            return context;
        }

        private bool IsTextEntryFocused()
        {
            Control focused = this;
            while (focused != null)
            {
                Control child = focused.Controls
                    .Cast<Control>()
                    .FirstOrDefault(control => control.ContainsFocus);
                if (child == null) break;
                focused = child;
            }

            return focused is TextBoxBase ||
                focused is ComboBox ||
                focused is NumericUpDown ||
                focused is DataGridView;
        }

        private void UpdateAttributesBinding(EventData eventData)
        {
            if (eventData == null)
            {
                m_propertiesForm.PropertyObject = null;
                return;
            }

            switch (eventData.EventType)
            {
                case EventType.Note:
                    {
                        m_propertiesForm.PropertyObject = new NoteEventPropertiesLayer(eventData);
                    }
                    break;
                case EventType.Volume:
                    {
                        m_propertiesForm.PropertyObject = new VolumeEventPropertiesLayer(eventData);
                    }
                    break;
                case EventType.Tempo:
                    {
                        m_propertiesForm.PropertyObject = new TempoEventPropertiesLayer(eventData);
                    }
                    break;
                case EventType.Beat:
                    {
                        m_propertiesForm.PropertyObject = new BeatEventPropertiesLayer(eventData);
                    }
                    break;
                default:
                    m_propertiesForm.PropertyObject = null;
                    break;
            }

        }

        public void Editor_OnSelectItem(object sender, EventData[] selectedItems)
        {
            if (null == selectedItems)
            {
                m_selectedEvent = null;
                m_propertiesForm.PropertyObject = null;
                m_audioList.List.ClearSelection();
                return;
            }

            if (selectedItems.Count() != 1)
            {
                m_selectedEvent = null;
                m_propertiesForm.PropertyObject = null;
                m_audioList.List.ClearSelection();
                return;
            }

            if (selectedItems.Count() < 1)
            {
                m_selectedEvent = null;
                m_propertiesForm.PropertyObject = null;
                m_audioList.List.ClearSelection();
                return;
            }

            var firstEvent = m_selectedEvent = selectedItems[0];
            UpdateAttributesBinding(firstEvent);

            // Check the selected note instrument
            InstrumentData instrumentData = firstEvent.Instrument;

            if (instrumentData != null)
            {
                var row = m_audioList.List.Rows.Cast<DataGridViewRow>().SingleOrDefault(r => r.DataBoundItem == instrumentData);
                // If we got something, select and move to this track
                if (row != null)
                {
                    row.Selected = true;
                    m_audioList.List.FirstDisplayedScrollingRowIndex = row.Index;
                }
            }
            else
            {
                m_audioList.List.ClearSelection();
            }
            
        }

        public void NoteSelect_OnSelectData(EventData eventData)
        {
            if (!CanMutateThroughActiveSurface())
            {
                return;
            }

            m_editorForm.Editor.TemplateEvent = eventData;

            // if the event is a note, set it's instrument to the current selected one on the musics list
            if (eventData != null && eventData.EventType == EventType.Note)
            {

                DataGridViewSelectedRowCollection rows = m_audioList.List.SelectedRows;

                if (rows.Count > 0)
                {
                    DataGridViewRow selectedRow = rows[0];

                    if (selectedRow != null)
                    {
                        eventData.Instrument = selectedRow.DataBoundItem as InstrumentData;
                    }
                }

            }

        }

        public void Editor_OnRequestEvent(byte index)
        {
            m_notes.SelectEvent(index);
        }

        public void AudioList_onInstrumentChanged(object sender, InstrumentData instrumentData, string filename)
        {
            if (!CanMutateThroughActiveSurface())
            {
                Logs.Write("Instrument replacement is unavailable on the active read-only editor surface.");
                return;
            }

            bool res = m_audioPlayer.LoadSound(instrumentData.InsNum, filename, 0);
            if (!res)
            {
                Logs.Write("Failed to load sound {0} - {1}", instrumentData.InsNum, instrumentData.Name);
            }
            else
            {
                m_editorForm
                    .Editor.UndoManager
                    .ExecAction(
                        new RenameInstrumentEventAction(instrumentData, Path.GetFileName(filename))
                    );
                
                //instrumentData.Name = Path.GetFileName(filename);
            }
        }

        public MainForm()
        {
            InitializeComponent();

            m_timelineV2MenuItem = new ToolStripMenuItem(
                "Use Timeline V2 (editing rollout)")
            {
                Checked = FeatureFlags.UseTimelineV2,
                CheckOnClick = false
            };
            m_timelineV2MenuItem.Click += TimelineV2MenuItem_Click;
            optionsToolStripMenuItem.DropDownItems.Insert(0, m_timelineV2MenuItem);

            _saveHandler = new SaveHandler();
            _saveHandler.Register(new PTSaveFile());
            _saveHandler.Register(new TQSaveFile());
            _saveHandler.Register(new BMESaveFile());
            _saveHandler.Register(new BmsonSaveFile());

            _loadHandler = new LoadHandler();
            _loadHandler.Register(new PTOpenFile());
            _loadHandler.Register(new TQOpenFile());
            _loadHandler.Register(new CyclonXmlOpenFile());
            _loadHandler.Register(new BmsOpenFile());

            var editor = m_editorForm.Editor;

            var audioList = this.m_audioList = new AudioListForm();
            audioList.OnPlayPressed += this.AudioList_onPlayPressed;
            audioList.OnStopPressed += this.AudioList_onStopPressed;
            audioList.OnInstrumentChanged += this.AudioList_onInstrumentChanged;

            Logs.OnLogWrite += this.Logs_OnWrite;

            m_deserializeDockContent = new DeserializeDockContent(GetContentFromPersistString);
            
            m_notes = new NoteSelectForm(editor.EventsRenderer);

            editor.OnSelectItem += Editor_OnSelectItem;

            m_notes.OnSelectDataEvent += NoteSelect_OnSelectData;

            editor.OnRequestEvent += Editor_OnRequestEvent;

            m_audioList.List.SelectionChanged += AudioList_selectionChanged;

            editor.OnUndoRedo += UndoManager_OnUndoRedo;

            var eventsThemeList = editor.EventsThemeList;
            var currentTheme = editor.CurrentEventsTheme;
            foreach (var theme in eventsThemeList)
            {
                ToolStripMenuItem toolStripMenuItem = new ToolStripMenuItem(theme.GetName());
                ThemeDropDownButton.DropDownItems.Add(toolStripMenuItem);
                if (null != toolStripMenuItem && currentTheme == theme)
                {
                    toolStripMenuItem.Checked = true;
                    ApplyEventsTheme(theme);
                }
            }

            var zonesThemeList = editor.ZonesThemeList;
            var currentZoneTheme = editor.CurrentZonesTheme;
            foreach (var theme in zonesThemeList)
            {
                ToolStripMenuItem toolStripMenuItem = new ToolStripMenuItem(theme.GetName());
                zoneRendererToolStripDropDownButton.DropDownItems.Add(toolStripMenuItem);

                if (currentZoneTheme != theme)
                {
                    continue;
                }

                toolStripMenuItem.Checked = true;
                ApplyZonesTheme(theme);
            }

            var eventDisplayModes = GetAll<EventDisplayMode>();

            var currentEventDisplayMode = editor.EventDisplayMode;
            foreach (var eventDisplayMode in eventDisplayModes)
            {
                var toolStripMenuItem = new ToolStripMenuItem(eventDisplayMode.Value);
                eventDisplayModeToolStripDropDownButton.DropDownItems.Add(toolStripMenuItem);

                if (eventDisplayMode.Key != (int) currentEventDisplayMode)
                {
                    continue;
                }
                toolStripMenuItem.Checked = true;
                eventDisplayModeToolStripDropDownButton.Text = eventDisplayMode.Value;
            }

            ApplyStudioShell();
            RegisterStudioCommands();
            m_editorForm.OpenRequested += delegate
            {
                openToolStripMenuItem_Click(this, EventArgs.Empty);
            };
            m_editorForm.ActiveSurfaceChanged += delegate
            {
                UpdateStudioRails();
            };
            AllowDrop = true;
        }

        private void ApplyStudioShell()
        {
            StudioTheme.ApplyMainShell(this, menuStrip1, toolStrip1, dockPanel);

            m_documentRail = new StudioDocumentRail();
            m_statusRail = new StudioStatusRail();
            m_documentRail.TimelineV1Requested += delegate { SetTimelineSurface(false); };
            m_documentRail.TimelineV2Requested += delegate { SetTimelineSurface(true); };
            m_documentRail.PreviewRequested += delegate { ShowGameplayPreview(); };
            m_documentRail.WorkspaceRequested += delegate(object sender, StudioWorkspaceRequestedEventArgs e)
            {
                ApplyWorkspacePreset(e.Preset);
            };
            m_documentRail.CommandPaletteRequested += delegate { ShowCommandPalette(); };

            m_studioTopHost = new TableLayoutPanel
            {
                BackColor = StudioDesignSystem.Void,
                ColumnCount = 1,
                Dock = DockStyle.Top,
                Height = 120,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                RowCount = 3
            };
            m_studioTopHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            m_studioTopHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
            m_studioTopHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 44f));
            m_studioTopHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 46f));

            menuStrip1.Dock = DockStyle.Fill;
            menuStrip1.Margin = Padding.Empty;
            toolStrip1.Dock = DockStyle.Fill;
            toolStrip1.Margin = Padding.Empty;
            m_documentRail.Dock = DockStyle.Fill;
            m_studioTopHost.Controls.Add(menuStrip1, 0, 0);
            m_studioTopHost.Controls.Add(m_documentRail, 0, 1);
            m_studioTopHost.Controls.Add(toolStrip1, 0, 2);
            Controls.Add(m_statusRail);
            Controls.Add(m_studioTopHost);

            toolStripButton1.AutoSize = false;
            toolStripButton1.Size = new System.Drawing.Size(38, 32);
            toolStripButton1.Margin = new Padding(0, 0, 3, 0);
            toolStripButton1.Image = StudioTheme.CreatePlayIcon(StudioTheme.TimingCyan);
            toolStripButton1.Text = "Play";

            toolStripButton2.AutoSize = false;
            toolStripButton2.Size = new System.Drawing.Size(38, 32);
            toolStripButton2.Margin = new Padding(0, 0, 8, 0);
            toolStripButton2.Image = StudioTheme.CreateStopIcon(StudioTheme.MutedText);
            toolStripButton2.Text = "Stop";

            currentProgress.AutoSize = false;
            currentProgress.Width = 164;
            currentProgress.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            currentProgress.Font = StudioTheme.MonoFont(9f);
            currentProgress.ForeColor = StudioTheme.PrimaryText;
            currentProgress.Text = "00:00:00  |  TICK 0";

            ConfigureWorkflowControl(toolStripDropDownButton1, 122);
            ConfigureWorkflowControl(ThemeDropDownButton, 178);
            ConfigureWorkflowControl(zoneRendererToolStripDropDownButton, 176);
            ConfigureWorkflowControl(eventDisplayModeToolStripDropDownButton, 132);

            toolStripDropDownButton1.Text = "Quantize  1/8";
            AddToolModeDeck();
            m_debugOutput.TabText = "Output";
            m_fmod.TabText = "Audio Engine";

            StudioTheme.ApplyToForm(m_editorForm);
            StudioTheme.ApplyToForm(m_debugOutput);
            StudioTheme.ApplyToForm(m_audioList);
            StudioTheme.ApplyToForm(m_propertiesForm);
            StudioTheme.ApplyToForm(m_fmod);
            StudioTheme.ApplyToForm(m_notes);
            StudioTheme.ApplyToForm(m_preview);
            UpdateStudioRails();
        }

        private void AddToolModeDeck()
        {
            toolStrip1.Items.Add(new ToolStripSeparator());
            var toolsLabel = new ToolStripLabel("TOOLS")
            {
                Font = StudioTheme.MonoFont(7.5f),
                ForeColor = StudioTheme.MutedText,
                Margin = new Padding(8, 0, 5, 0)
            };
            toolStrip1.Items.Add(toolsLabel);

            AddToolButton(TimelineTool.Select, "V  SELECT");
            AddToolButton(TimelineTool.Draw, "B  DRAW");
            AddToolButton(TimelineTool.Erase, "E  ERASE");
            AddToolButton(TimelineTool.Resize, "R  RESIZE");
            AddToolButton(TimelineTool.Pan, "H  PAN");
            SetActiveTool(TimelineTool.Select);
        }

        private void AddToolButton(TimelineTool tool, string text)
        {
            var button = new ToolStripButton(text)
            {
                AutoSize = false,
                CheckOnClick = false,
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                Font = StudioTheme.StrongFont(7.75f),
                ForeColor = StudioTheme.MutedText,
                Height = 28,
                Margin = new Padding(2, 1, 2, 1),
                Width = text.Length > 8 ? 76 : 68
            };
            button.Click += delegate { SetActiveTool(tool); };
            m_toolButtons.Add(tool, button);
            toolStrip1.Items.Add(button);
        }

        private void RegisterStudioCommands()
        {
            m_commands.Register(new StudioCommand(
                "application.command-palette", "Open command palette", "Application",
                Keys.Control | Keys.K, StudioCommandContext.Global,
                () => true, () => string.Empty, ShowCommandPalette));
            m_commands.Register(new StudioCommand(
                "document.open", "Open chart", "Document", Keys.Control | Keys.O,
                StudioCommandContext.Global, () => true, () => string.Empty,
                () => openToolStripMenuItem_Click(this, EventArgs.Empty)));
            m_commands.Register(new StudioCommand(
                "document.save-as", "Save chart as", "Document", Keys.Control | Keys.Shift | Keys.S,
                StudioCommandContext.Document, () => _documentContext != null,
                () => "Open a chart before saving.", () => saveAsToolStripMenuItem_Click(this, EventArgs.Empty)));
            m_commands.Register(new StudioCommand(
                "transport.play-pause", "Play or pause", "Playback", Keys.Space,
                StudioCommandContext.Global, () => true, () => string.Empty,
                () => toolStripButton1_Click(this, EventArgs.Empty)));
            m_commands.Register(new StudioCommand(
                "panel.gameplay-preview", "Show gameplay preview", "Panels", Keys.None,
                StudioCommandContext.Global, () => true, () => string.Empty,
                ShowGameplayPreview));
            m_commands.Register(new StudioCommand(
                "edit.undo", "Undo", "Edit", Keys.Control | Keys.Z,
                StudioCommandContext.Document, () => _documentContext != null && m_undoManager.CanUndo,
                () => "There is nothing to undo.", () => undoToolStripMenuItem_Click(this, EventArgs.Empty)));
            m_commands.Register(new StudioCommand(
                "edit.redo", "Redo", "Edit", Keys.Control | Keys.Y,
                StudioCommandContext.Document, () => _documentContext != null && m_undoManager.CanRedo,
                () => "There is nothing to redo.", () => redoToolStripMenuItem_Click(this, EventArgs.Empty)));
            RegisterToolCommand("tool.select", "Select tool", Keys.V, TimelineTool.Select);
            RegisterToolCommand("tool.draw", "Draw tool", Keys.B, TimelineTool.Draw);
            RegisterToolCommand("tool.erase", "Erase tool", Keys.E, TimelineTool.Erase);
            RegisterToolCommand("tool.resize", "Resize tool", Keys.R, TimelineTool.Resize);
            RegisterToolCommand("tool.pan", "Pan tool", Keys.H, TimelineTool.Pan);
            RegisterWorkspaceCommand(
                "workspace.editing", "Use Editing workspace", StudioWorkspacePreset.Editing);
            RegisterWorkspaceCommand(
                "workspace.preview", "Use Preview workspace", StudioWorkspacePreset.Preview);
            RegisterWorkspaceCommand(
                "workspace.audio", "Use Audio workspace", StudioWorkspacePreset.Audio);
            RegisterWorkspaceCommand(
                "workspace.compact", "Use Compact workspace", StudioWorkspacePreset.Compact);
        }

        private void RegisterToolCommand(string id, string name, Keys shortcut, TimelineTool tool)
        {
            m_commands.Register(new StudioCommand(
                id, name, "Tools", shortcut, StudioCommandContext.Timeline,
                CanUseEditingTools,
                () => _documentContext == null
                    ? "Open a chart before choosing an editing tool."
                    : "Editing is unavailable for this document or timeline.",
                () => SetActiveTool(tool)));
        }

        private void RegisterWorkspaceCommand(
            string id,
            string name,
            StudioWorkspacePreset preset)
        {
            m_commands.Register(new StudioCommand(
                id, name, "Workspaces", Keys.None, StudioCommandContext.Global,
                () => true, () => string.Empty,
                () => ApplyWorkspacePreset(preset)));
        }

        private void ShowCommandPalette()
        {
            if (m_commandPalette == null || m_commandPalette.IsDisposed)
            {
                m_commandPalette = new StudioCommandPaletteForm(
                    m_commands, GetActiveCommandContext);
                m_commandPalette.FormClosed += delegate { m_commandPalette = null; };
                m_commandPalette.Show(this);
            }
            else
            {
                m_commandPalette.Activate();
            }
        }

        private void ShowGameplayPreview()
        {
            if (m_preview == null || m_preview.IsDisposed)
            {
                m_preview = new GameplayPreviewForm();
                StudioTheme.ApplyToForm(m_preview);
                m_preview.Bind(_documentContext);
                ApplyPreviewProfileFromEventTheme();
            }

            m_preview.Show(dockPanel);
            m_preview.Activate();
            m_preview.RefreshPlaybackImmediately();
            SetStudioStatus("GAMEPLAY PREVIEW  •  VIEW ONLY");
        }

        private void ApplyPreviewProfileFromEventTheme()
        {
            if (m_preview == null || m_preview.IsDisposed || _documentContext == null)
            {
                return;
            }

            IEventRenderer theme = m_editorForm.Editor.CurrentEventsTheme;
            if (theme != null &&
                string.Equals(theme.GetName(), "Technika", StringComparison.OrdinalIgnoreCase))
            {
                m_preview.ConfirmTechnikaProfile();
            }
            else
            {
                m_preview.UseGenericProfile();
            }
        }

        private void ApplyWorkspacePreset(StudioWorkspacePreset preset)
        {
            dockPanel.SuspendLayout(true);
            try
            {
                HideStudioToolWindows();
                m_editorForm.Show(dockPanel, DockState.Document);

                switch (preset)
                {
                    case StudioWorkspacePreset.Editing:
                        dockPanel.DockLeftPortion = 0.22;
                        dockPanel.DockRightPortion = 0.26;
                        dockPanel.DockBottomPortion = 0.20;
                        m_notes.Show(dockPanel, DockState.DockLeft);
                        m_audioList.Show(dockPanel, DockState.DockLeft);
                        m_propertiesForm.Show(dockPanel, DockState.DockRight);
                        m_debugOutput.Show(dockPanel, DockState.DockBottom);
                        break;

                    case StudioWorkspacePreset.Preview:
                        dockPanel.DockRightPortion = 0.40;
                        ShowGameplayPreview();
                        break;

                    case StudioWorkspacePreset.Audio:
                        dockPanel.DockRightPortion = 0.36;
                        dockPanel.DockBottomPortion = 0.22;
                        m_audioList.Show(dockPanel, DockState.DockRight);
                        EnsureAudioEngineForm();
                        m_fmod.Show(dockPanel, DockState.DockRight);
                        m_debugOutput.Show(dockPanel, DockState.DockBottom);
                        break;

                    case StudioWorkspacePreset.Compact:
                        break;
                }
            }
            finally
            {
                dockPanel.ResumeLayout(true, true);
            }

            SetStudioStatus(
                "WORKSPACE  " + preset.ToString().ToUpperInvariant() +
                "  •  SESSION PRESET");
        }

        private void HideStudioToolWindows()
        {
            HideDockContent(m_notes);
            HideDockContent(m_audioList);
            HideDockContent(m_propertiesForm);
            HideDockContent(m_debugOutput);
            HideDockContent(m_fmod);
            HideDockContent(m_preview);
        }

        private static void HideDockContent(DockContent content)
        {
            if (content != null && !content.IsDisposed)
            {
                content.Hide();
            }
        }

        private void EnsureAudioEngineForm()
        {
            if (m_fmod != null && !m_fmod.IsDisposed)
            {
                return;
            }
            m_fmod = new FModForm();
            StudioTheme.ApplyToForm(m_fmod);
        }

        private bool CanUseEditingTools()
        {
            return _documentContext != null &&
                _documentContext.Capabilities.CanEdit &&
                m_editorForm.ActiveSurface.SupportsEditing;
        }

        private void SetActiveTool(TimelineTool tool)
        {
            if (_documentContext != null)
            {
                _documentContext.Interaction.Tool = tool;
            }
            SyncToolButtons(tool);
            SetStudioStatus("TOOL  " + tool.ToString().ToUpperInvariant());
        }

        private void SyncToolButtons(TimelineTool tool)
        {
            foreach (var pair in m_toolButtons)
            {
                pair.Value.Checked = pair.Key == tool;
                pair.Value.ForeColor = pair.Key == tool
                    ? StudioDesignSystem.PulseCyan
                    : StudioDesignSystem.Muted;
            }
        }

        private void SetTimelineSurface(bool useTimelineV2)
        {
            FeatureFlags.SetUseTimelineV2(useTimelineV2);
            m_timelineV2MenuItem.Checked = useTimelineV2;
            m_editorForm.SwitchSurface(useTimelineV2);
            UpdateStudioRails();
        }

        private void UpdateStudioRails()
        {
            if (m_documentRail == null || m_statusRail == null)
            {
                return;
            }

            if (_documentContext == null)
            {
                m_documentRail.ShowEmpty();
                m_statusRail.SetStatus("READY", "SNAP 1/8", "NO DOCUMENT");
                return;
            }

            DocumentCapabilities capabilities = _documentContext.Capabilities;
            string format = GetFormatChipText(capabilities);
            string surface = m_editorForm.IsLegacySurfaceActive
                ? "TIMELINE V1"
                : "TIMELINE V2";
            string capabilityChip = capabilities.CanEdit
                ? (capabilities.IsRespectV ? "EDITABLE  •  EXPORT" : "EDITABLE")
                : "READ ONLY";
            m_documentRail.ShowDocument(
                Path.GetFileName(_documentContext.SourcePath),
                surface,
                format,
                capabilityChip,
                !capabilities.CanEdit);

            int selected = _documentContext.Selection.Count;
            m_statusRail.SetStatus(
                selected == 0 ? "READY  •  SELECT TOOL" : selected + " EVENT" + (selected == 1 ? string.Empty : "S") + " SELECTED",
                toolStripDropDownButton1.Text.ToUpperInvariant(),
                surface + "  •  " + capabilities.StatusLabel);
        }

        private static string GetFormatChipText(DocumentCapabilities capabilities)
        {
            if (!capabilities.SourceFormat.HasValue)
            {
                return "UNKNOWN";
            }

            switch (capabilities.SourceFormat.Value)
            {
                case DJMaxEditor.Files.FormatDetection.ChartFormat.PtffDecrypted:
                    return "PTFF";
                case DJMaxEditor.Files.FormatDetection.ChartFormat.PtffEncryptedTechnika:
                    return "TECHNIKA PTFF";
                case DJMaxEditor.Files.FormatDetection.ChartFormat.TrailerRespectV:
                    return "RESPECT V";
                case DJMaxEditor.Files.FormatDetection.ChartFormat.CyclonXml:
                    return "CYCLON XML";
                case DJMaxEditor.Files.FormatDetection.ChartFormat.BmsClassic:
                    return "BMS FAMILY";
                default:
                    return capabilities.SourceFormat.Value.ToString().ToUpperInvariant();
            }
        }

        private void SetStudioStatus(string message)
        {
            if (m_statusRail == null)
            {
                return;
            }
            string right = _documentContext == null
                ? "NO DOCUMENT"
                : _documentContext.Capabilities.StatusLabel;
            m_statusRail.SetStatus(message, toolStripDropDownButton1.Text.ToUpperInvariant(), right);
        }

        private static void ConfigureWorkflowControl(ToolStripDropDownButton button, int width)
        {
            button.AutoSize = false;
            button.Width = width;
            button.Height = 32;
            button.Margin = new Padding(2, 0, 2, 0);
            button.Padding = new Padding(8, 0, 4, 0);
            button.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            button.Font = StudioTheme.StrongFont(8.5f);
            button.ForeColor = StudioTheme.PrimaryText;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            StudioTheme.TryApplyDarkTitleBar(this);
        }

        #endregion // public defs

        #region private defs

        private DeserializeDockContent m_deserializeDockContent;
        private PropertiesForm m_propertiesForm = new PropertiesForm();
        private EditorForm m_editorForm = new EditorForm();
        private DebugOutput m_debugOutput = new DebugOutput();
        private AudioListForm m_audioList;
        //private PerformancesForm m_performances = new PerformancesForm();
        private FModForm m_fmod = new FModForm();
        private NoteSelectForm m_notes;
        private SaveHandler _saveHandler;
        private LoadHandler _loadHandler;
        private ToolStripMenuItem m_timelineV2MenuItem;
        private readonly StudioCommandRegistry m_commands = new StudioCommandRegistry();
        private readonly Dictionary<TimelineTool, ToolStripButton> m_toolButtons =
            new Dictionary<TimelineTool, ToolStripButton>();
        private StudioDocumentRail m_documentRail;
        private StudioStatusRail m_statusRail;
        private StudioCommandPaletteForm m_commandPalette;
        private GameplayPreviewForm m_preview = new GameplayPreviewForm();
        private TableLayoutPanel m_studioTopHost;
        private EditorDocumentContext _documentContext;

        private UndoManager m_undoManager = UndoManager.GetInstance();

#if ENABLE_EVENT_FORM
        private EventsListForm _eventsForm = new  EventsListForm();
#endif

        private bool m_isPaused = true;

        private LoadingForm m_loadingForm = new LoadingForm();

        private const string APP_NAME = "DJMax Editor";

        private const int ReservedChannel = 99;

        // the PT player
        private Player m_player = new Player();

        // the audio player
        private IAudioPlayer m_audioPlayer = new AudioPlayerFmodEx();

        private PlayerData m_playerData = new PlayerData();

        private EventData m_selectedEvent = null;

        private bool m_isFullScreen = false;

        private FormWindowState _oldState = FormWindowState.Maximized;

        private bool CanMutateThroughActiveSurface()
        {
            string reason;
            return DocumentMutationGuard.CanMutate(
                m_playerData,
                m_editorForm.ActiveSurface.SupportsEditing,
                out reason);
        }

        private void TimelineV2MenuItem_Click(object sender, EventArgs e)
        {
            bool enabled = !m_timelineV2MenuItem.Checked;
            SetTimelineSurface(enabled);
        }

        public static IDictionary<int, string> GetAll<TEnum>() where TEnum : struct
        {
            var enumerationType = typeof(TEnum);

            if (!enumerationType.IsEnum)
                throw new ArgumentException("Enumeration type is expected.");

            var dictionary = new Dictionary<int, string>();

            foreach (int value in Enum.GetValues(enumerationType))
            {
                var name = Enum.GetName(enumerationType, value);
                dictionary.Add(value, name);
            }

            return dictionary;
        }

        private void AudioList_selectionChanged(object sender, EventArgs e)
        {
            this.m_audioPlayer.StopSound(ReservedChannel);

            if (!CanMutateThroughActiveSurface())
            {
                return;
            }

            DataGridView audioList = sender as DataGridView;

            if (audioList == null) { return; }

            DataGridViewSelectedRowCollection rows = audioList.SelectedRows;

            if (rows == null || rows.Count == 0) { return; }

            DataGridViewRow selectedRow = rows[0];

            if (selectedRow == null) { return; }

            EventData eventData = m_editorForm.Editor.TemplateEvent;

            if (eventData == null) { return; }

            InstrumentData instrumentData = selectedRow.DataBoundItem as InstrumentData;

            // then try to update the current instrument
            eventData.Instrument = instrumentData;

            // if a note is selected, update it's instrument
            if (m_selectedEvent != null && m_selectedEvent.Instrument != instrumentData)
            {
                m_undoManager.ExecAction(new SetSoundAction(m_selectedEvent, instrumentData));
            }
        }

        private void Logs_OnWrite(string data)
        {

            if (InvokeRequired)
            {
                BeginInvoke(new Action(() =>
                {
                    m_debugOutput.Log(data);
                }));
            }
            else
            {
                m_debugOutput.Log(data);
            }

        }

        private void Form1_Load(object sender, EventArgs e)
        {
            //this.TopMost = true;
            //this.FormBorderStyle = FormBorderStyle.None;
            this.WindowState = FormWindowState.Maximized;

            string configFile = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "layout.config");

            Logs.Write("configFile : {0}", configFile);
            if (File.Exists(configFile))
            {
                Logs.Write("loading config from xml");
                dockPanel.LoadFromXml(configFile, m_deserializeDockContent);
            }
            else
            {
                Logs.Write("not found. Loading default");
                m_editorForm.Show(dockPanel);

                m_debugOutput.Show(dockPanel);
                m_audioList.Show(dockPanel);

#if ENABLE_EVENT_FORM
                _eventsForm.Show(dockPanel);
#else
                eventsToolStripMenuItem.Visible = false;
#endif

                m_propertiesForm.Show(dockPanel);
                m_fmod.Show(dockPanel);
                m_notes.Show(dockPanel);
            }

            m_player.OnEvent += Player_OnEvent;
            m_player.OnStatusChange += Player_OnStatusChange;            
        }

        private void Player_OnStatusChange(object sender) 
        {
            m_editorForm.Editor.IsPlayerPlaying = m_player.IsPlaying;

            m_editorForm.ActiveSurface.InvalidateView();
            CheckAndUpdatePlayPauseIcons();
        }

        private void Player_OnEvent(EventData eventData, uint trackIndex, EventType eventType, byte pan)
        {
            if (m_playerData == null)
            {
                return;
            }

            ushort soundIndex = (ushort)((eventData.Instrument != null) ? eventData.Instrument.InsNum : 0);

            TrackData track = m_playerData.Tracks.GetTrackAtIndex(trackIndex);
            if (track == null)
            {
                return;
            }

            switch (eventType)
            {
                case EventType.Note:

                    if (soundIndex > 0)
                    {
                        m_audioPlayer.SetVolume(trackIndex, eventData.Vel);

                        var evTick = eventData.Tick;
                        var currentTick = m_player.GetCurrentTick();
                        if (evTick < currentTick - 50)
                        {
                            // TODO: seek to the current position somehow
                            //var tps = m_playerData.TickPerMinute;
                            //int offset = (currentTick - evTick) * 1000 / tps;
                            //if (!m_audioPlayer.PlaySound(trackIndex, soundIndex, track.Volume, pan, (uint)offset))
                            //{
                            //    Logs.Write("Failed to play sound on track : {0}, soundIndex : {1}", trackIndex, soundIndex);
                            //};
                        } else
                        {
                            if (!m_audioPlayer.PlaySound(trackIndex, soundIndex, track.Volume, pan))
                            {
                                Logs.Write("Failed to play sound on track : {0}, soundIndex : {1}", trackIndex, soundIndex);
                            };
                        }

                    }

                    break;
                case EventType.Volume:

                    ushort volume = eventData.Volume;

                    track.Volume = ((float)volume / (float)sbyte.MaxValue);
                    m_audioPlayer.SetVolume(trackIndex, track.Volume);

                    break;
            }

        }

        private void OpenFileComplete(PlayerData playerData, string filename, bool success)
        {
            m_loadingForm.Close();
            if (false == success) 
            {
                MessageBox.Show("Failed to load the file", "Load file error", MessageBoxButtons.OK, MessageBoxIcon.Error); 
                return;
            }

            bool readOnly = playerData != null && playerData.IsReadOnly;
            this.Text = String.Format("{0} - {1}{2}", APP_NAME, filename, readOnly ? "   [READ-ONLY]" : "");

            m_editorForm.Title = readOnly ? filename + "  (read-only)" : filename;

            if (readOnly)
            {
                DJMaxEditor.Diagnostics.DiagnosticLog.Write("open.readonly",
                    filename + " opened read-only (source=" + playerData.SourceFormat + ")");
            }

            m_playerData = playerData;

            this.m_audioPlayer.StopAllSounds();
            m_player.LoadPlayerData(playerData);

            m_audioList.List.DataSource = playerData.Instruments;

#if ENABLE_EVENT_FORM
            _eventsForm.SetPlayerData(playerData);
#endif

            for (int i = 0, l = playerData.Instruments.Count; i < l; i++)
            {
                InstrumentData instrument = playerData.Instruments[i];
            }

            if (_documentContext != null)
            {
                _documentContext.Selection.SelectionChanged -= DocumentSelectionChanged;
                _documentContext.Interaction.ToolChanged -= DocumentToolChanged;
            }
            _documentContext = new EditorDocumentContext(m_playerData, filename);
            _documentContext.Selection.SelectionChanged += DocumentSelectionChanged;
            _documentContext.Interaction.ToolChanged += DocumentToolChanged;
            m_editorForm.Bind(_documentContext);
            m_propertiesForm.Bind(_documentContext);
            m_preview.Bind(_documentContext);
            ApplyPreviewProfileFromEventTheme();
            UpdateStudioRails();
        }

        private void DocumentSelectionChanged(object sender, EventArgs e)
        {
            Editor_OnSelectItem(this, _documentContext.Selection.Items.ToArray());
            UpdateStudioRails();
        }

        private void DocumentToolChanged(object sender, EventArgs e)
        {
            if (_documentContext == null)
            {
                return;
            }
            SyncToolButtons(_documentContext.Interaction.Tool);
            SetStudioStatus(
                "TOOL  " +
                _documentContext.Interaction.Tool.ToString().ToUpperInvariant());
        }

        private void SaveFile(string filename, int filterIndex = 0)
        {
            var extension = Path.GetExtension(filename).ToLower();

            var handler = filterIndex > 0 ?
                _saveHandler.GetHandlerForFilterIndex(filterIndex) :
                _saveHandler.GetHandlerForExtension(extension);
            if (handler == null)
            {
                return;
            }

            if (handler is BMESaveFile &&
                BmsonChartSerializer.ShouldUseForClassicBmsOverflow(m_playerData))
            {
                MessageBox.Show(
                    "This chart needs " + BmsChartSerializer.CountRequiredKeysounds(m_playerData) +
                    " unique keysounds, but classic BMS only has 1,295 usable IDs.\n\n" +
                    "DJMax Editor will save it as BMSON instead. BMSON keeps every keysound and " +
                    "is supported by Pulsus and BmsONE.",
                    "Using BMSON for this chart", MessageBoxButtons.OK, MessageBoxIcon.Information);

                using (var bmsonDialog = new SaveFileDialog())
                {
                    bmsonDialog.Filter = "Be-Music JSON (*.bmson)|*.bmson";
                    bmsonDialog.DefaultExt = "bmson";
                    bmsonDialog.AddExtension = true;
                    bmsonDialog.OverwritePrompt = true;
                    bmsonDialog.InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(filename));
                    bmsonDialog.FileName = Path.GetFileNameWithoutExtension(filename) + ".bmson";
                    if (bmsonDialog.ShowDialog(this) != DialogResult.OK) return;
                    filename = bmsonDialog.FileName;
                }
                handler = new BmsonSaveFile();
            }

            if (m_playerData != null && m_playerData.IsReadOnly)
            {
                MessageBox.Show(
                    "This chart was opened read-only" +
                    (m_playerData.SourceFormat.HasValue ? " (" + m_playerData.SourceFormat + ")" : "") +
                    ".\n\nSaving it back is disabled because lossless round-trip and in-game compatibility " +
                    "have not been verified for this format.",
                    "Read-only chart", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Respect V charts are editable in memory, but the legacy Respect/TQ writer is not
            // lossless. Allow the supported conversion path without risking the source container.
            if (m_playerData != null &&
                m_playerData.SourceFormat == DJMaxEditor.Files.FormatDetection.ChartFormat.TrailerRespectV &&
                !(handler is BMESaveFile) &&
                !(handler is BmsonSaveFile))
            {
                MessageBox.Show(
                    "Respect V charts are editable, but saving back to the original PT/bytes container " +
                    "is not verified.\n\nChoose Be-Music Script (*.bms) or Be-Music JSON (*.bmson) " +
                    "in Save As to export your edits.",
                    "Export Respect V chart", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var settingsForm = handler.GetSettingsForm();
            if (settingsForm != null)
            {
                settingsForm.Icon = this.Icon;
                settingsForm.StartPosition = FormStartPosition.CenterParent;
                settingsForm.FormBorderStyle = FormBorderStyle.FixedToolWindow;
                settingsForm.Text = "Save as...";
                var closeDialogSuccess =  settingsForm.ShowDialog(this) == DialogResult.OK;
                if (!closeDialogSuccess)
                {
                    return;
                }
            }

            Thread loadDataThread = new Thread(delegate ()
            {
                bool ok;
                try
                {
                    ok = handler.Save(filename, m_playerData);
                }
                catch (Exception ex)
                {
                    DJMaxEditor.Diagnostics.DiagnosticLog.Exception("save.error", ex);
                    ok = false;
                }

                ShowOnUi(() =>
                {
                    m_loadingForm.Close();
                    if (!ok)
                    {
                        MessageBox.Show(
                            "The file could not be saved. The target may be unchanged or incomplete; " +
                            "see the local diagnostics log.\n\nLog: " + DJMaxEditor.Diagnostics.DiagnosticLog.LogPath,
                            "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                });
            });
            loadDataThread.Start();
            m_loadingForm.DisplayedMessage = "Saving pattern...";
            m_loadingForm.ShowDialog(this);
        }

        private IOpenFile GetHandlerForFormat(DJMaxEditor.Files.FormatDetection.ChartFormat format)
        {
            switch (format)
            {
                case DJMaxEditor.Files.FormatDetection.ChartFormat.PtffDecrypted:
                    return _loadHandler.GetHandlerForExtension(".pt");
                case DJMaxEditor.Files.FormatDetection.ChartFormat.TrailerRespectV:
                    return _loadHandler.GetHandlerForExtension(".bytes");
                case DJMaxEditor.Files.FormatDetection.ChartFormat.CyclonXml:
                    return _loadHandler.GetHandlerForExtension(".xml");
                case DJMaxEditor.Files.FormatDetection.ChartFormat.BmsClassic:
                    return _loadHandler.GetHandlerForExtension(".bms");
                default:
                    return null;
            }
        }

        private void ShowOnUi(Action action)
        {
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }

        private bool OpenFileAsync(IOpenFile file, string filename, bool readOnly,
            DJMaxEditor.Files.FormatDetection.ChartFormat sourceFormat)
        {
            Logs.Write(String.Format("Openning file {0}", filename));

            PlayerData playerData = null;
            FileInfo fi = new FileInfo(filename);

            try
            {
                if (!file.Open(filename, out playerData) || playerData == null)
                {
                    Logs.Write("Failed to open ({0})", filename);
                    ShowOnUi(() => OpenFileComplete(null, fi.Name, false));
                    return false;
                }
            }
            catch (DJMaxEditor.Files.ChartLoadException ex)
            {
                DJMaxEditor.Diagnostics.DiagnosticLog.Write("open.error", fi.Name + ": " + ex);
                ShowOnUi(() => ShowChartLoadError(fi.Name, ex));
                return false;
            }
            catch (Exception ex)
            {
                DJMaxEditor.Diagnostics.DiagnosticLog.Exception("open.unexpected", ex);
                ShowOnUi(() => ShowUnexpectedLoadError(fi.Name, ex));
                return false;
            }

            // Preserve read-only / source-format decisions from detection.
            playerData.IsReadOnly = playerData.IsReadOnly || readOnly;
            if (playerData.SourceFormat == null) playerData.SourceFormat = sourceFormat;

            for (int i = 0, l = playerData.Instruments.Count; i < l; i++)
            {
                InstrumentData instrument = playerData.Instruments[i];

                if (instrument == null || instrument.InsNum == 0)
                {
                    continue;
                }

                bool res = m_audioPlayer.LoadSound(instrument.InsNum, fi.Directory + "\\" + instrument.Name, i == 0 ? 1 : 0);
                if (!res)
                {
                    Logs.Write("Failed to load sound {0} - {1}", instrument.InsNum, instrument.Name);
                }
            }

            ShowOnUi(() => OpenFileComplete(playerData, fi.Name, true));
            return true;
        }

        private void OpenFile(string filename, int filterIndex = 0)
        {
            if (false == File.Exists(filename))
            {
                return;
            }

            byte[] data;
            try
            {
                data = File.ReadAllBytes(filename);
            }
            catch (Exception ex)
            {
                DJMaxEditor.Diagnostics.DiagnosticLog.Exception("open.read", ex);
                MessageBox.Show("The file could not be read.\n\n" + ex.Message,
                    "Load file error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Content-based detection is authoritative; the extension is only a hint (a .pt file that is
            // really a Respect V trailer chart must NOT be force-routed to the PTFF parser, and a .bytes
            // file is still validated). Detection never decrypts and never touches the network.
            var extension = Path.GetExtension(filename).ToLower();
            var detection = DJMaxEditor.Files.FormatDetection.ChartFormatDetector.Detect(data, extension);
            DJMaxEditor.Diagnostics.DiagnosticLog.Write("open.detect", Path.GetFileName(filename) + ": " + detection);

            // Positively-identified encrypted Technika/Trilogy charts are decrypted OFFLINE (no network)
            // via the local PtCodec, on explicit user confirmation. This only runs after a positive
            // encrypted identification — never as a fallback for a generic parse failure.
            if (detection.Format == DJMaxEditor.Files.FormatDetection.ChartFormat.PtffEncryptedTechnika)
            {
                OpenEncryptedPt(filename, data, detection);
                return;
            }

            if (!detection.IsOpenable)
            {
                ShowLoadDiagnostic(Path.GetFileName(filename), detection);
                return;
            }

            var handler = GetHandlerForFormat(detection.Format);
            if (handler == null)
            {
                ShowLoadDiagnostic(Path.GetFileName(filename), detection);
                return;
            }

            var settingsForm = handler.GetSettingsForm();
            if (settingsForm != null)
            {
                settingsForm.Icon = this.Icon;
                settingsForm.StartPosition = FormStartPosition.CenterParent;
                settingsForm.FormBorderStyle = FormBorderStyle.FixedToolWindow;
                settingsForm.Text = "Loading file...";
                var closeDialogSuccess = settingsForm.ShowDialog(this) == DialogResult.OK;
                if (!closeDialogSuccess)
                {
                    return;
                }
            }

            var det = detection;
            Thread loadDataThread = new Thread(delegate () { OpenFileAsync(handler, filename, det.IsReadOnly, det.Format); });
            loadDataThread.Start();
            m_loadingForm.DisplayedMessage = "Loading pattern...";
            m_loadingForm.ShowDialog(this);
        }

        // Offline decrypt-to-open for encrypted Technika/Trilogy charts. No network, no external process,
        // no temp files: PtCodec runs in-process on the bytes already in memory. The decrypted output is
        // re-validated as a real PTFF chart before it is allowed near the parser.
        private void OpenEncryptedPt(string filename, byte[] data,
            DJMaxEditor.Files.FormatDetection.FormatDetectionResult detection)
        {
            string name = Path.GetFileName(filename);

            var choice = MessageBox.Show(
                "This is an encrypted Technika/Trilogy chart.\n\n" +
                "Decrypt it offline and open it? Decryption runs entirely on this machine " +
                "(no upload, no network request). The original file is not modified.\n\n" +
                "File: " + name +
                (string.IsNullOrEmpty(detection.Evidence) ? "" : "\nEvidence: " + detection.Evidence),
                "Decrypt encrypted chart?", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (choice != DialogResult.Yes)
            {
                DJMaxEditor.Diagnostics.DiagnosticLog.Write("open.decrypt", name + ": declined by user");
                return;
            }

            byte[] decrypted;
            try
            {
                decrypted = DJMaxEditor.Files.pt.PtCodec.Decrypt(data);
            }
            catch (Exception ex)
            {
                DJMaxEditor.Diagnostics.DiagnosticLog.Exception("open.decrypt", ex);
                MessageBox.Show(
                    "The chart could not be decrypted.\n\n" + ex.Message +
                    "\n\nFile: " + name + "\nThe file was not modified.",
                    "Decryption failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Re-validate: the decrypted bytes must present as a genuine decrypted PTFF chart (PTFF + EZTR),
            // otherwise the wrong key/format produced garbage and we must not feed it to the parser.
            var reDetect = DJMaxEditor.Files.FormatDetection.ChartFormatDetector.Detect(decrypted, ".pt");
            DJMaxEditor.Diagnostics.DiagnosticLog.Write("open.decrypt", name + " -> " + reDetect);

            if (reDetect.Format != DJMaxEditor.Files.FormatDetection.ChartFormat.PtffDecrypted)
            {
                MessageBox.Show(
                    "Decryption did not produce a valid chart (no PTFF/EZTR structure was found). " +
                    "This file may use a different key or format.\n\n" +
                    "File: " + name +
                    (string.IsNullOrEmpty(reDetect.Evidence) ? "" : "\nDecrypted evidence: " + reDetect.Evidence),
                    "Decryption produced invalid data", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var handler = GetHandlerForFormat(DJMaxEditor.Files.FormatDetection.ChartFormat.PtffDecrypted)
                as PTOpenFile;
            if (handler == null)
            {
                ShowLoadDiagnostic(name, detection);
                return;
            }

            handler.SourceOverride = decrypted;
            handler.FromEncryptedSource = true;

            Thread loadDataThread = new Thread(delegate ()
            {
                OpenFileAsync(handler, filename, false,
                    DJMaxEditor.Files.FormatDetection.ChartFormat.PtffEncryptedTechnika);
            });
            loadDataThread.Start();
            m_loadingForm.DisplayedMessage = "Decrypting and loading pattern...";
            m_loadingForm.ShowDialog(this);
        }

        private void ShowLoadDiagnostic(string filename, DJMaxEditor.Files.FormatDetection.FormatDetectionResult detection)
        {
            m_loadingForm.Close();

            string title;
            string message;
            switch (detection.Format)
            {
                case DJMaxEditor.Files.FormatDetection.ChartFormat.PtffEncryptedTechnika:
                    title = "Encrypted DJMax chart";
                    message =
                        "This file appears to be an encrypted Technika or Trilogy chart.\n\n" +
                        "Offline decryption is available (no network is used), but it could not be " +
                        "started for this file. No data was uploaded and the file was not modified.";
                    break;
                case DJMaxEditor.Files.FormatDetection.ChartFormat.Malformed:
                    title = "Chart file is malformed";
                    message =
                        "The chart contains an invalid offset, count, or truncated data block. " +
                        "The file was not modified.";
                    break;
                default:
                    title = "Unsupported chart format";
                    message =
                        "The file does not match PTFF, encrypted Technika, Respect V trailer format, or a supported XML format. " +
                        "No decryption or network request was attempted.";
                    break;
            }

            message += "\n\nFile: " + filename;
            if (!string.IsNullOrEmpty(detection.Evidence)) message += "\nEvidence: " + detection.Evidence;
            if (detection.FailureReason != null) message += "\nReason: " + detection.FailureReason;
            if (detection.Offset.HasValue) message += "\nOffset: 0x" + detection.Offset.Value.ToString("X");

            MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void ShowChartLoadError(string filename, DJMaxEditor.Files.ChartLoadException ex)
        {
            m_loadingForm.Close();
            string offset = ex.Offset.HasValue ? "\nOffset: 0x" + ex.Offset.Value.ToString("X") : "";
            MessageBox.Show(
                "The chart could not be read: " + ex.Message +
                "\n\nFile: " + filename + "\nError type: " + ex.Kind + offset +
                "\n\nThe file was not modified.",
                "Chart could not be read", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void ShowUnexpectedLoadError(string filename, Exception ex)
        {
            m_loadingForm.Close();
            MessageBox.Show(
                "An unexpected error occurred while loading the chart. It was logged locally.\n\n" +
                ex.Message + "\n\nFile: " + filename + "\nLog: " + DJMaxEditor.Diagnostics.DiagnosticLog.LogPath,
                "Load file error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void TryOpenFromCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            if (!(args.Length > 1))
            {
                return;
            }

            var fileToLoad = args[1];
            if (String.IsNullOrEmpty(fileToLoad))
            {
                return;
            }

            if (!File.Exists(fileToLoad))
            {
                return;
            }

            OpenFile(fileToLoad);
        }

        private void Form1_Shown(object sender, EventArgs e)
        {
            StudioTheme.ApplyNativeDarkMode(this);
            TryOpenFromCommandLine();
        }

        private void LongIntervaleTimer_Tick(object sender, EventArgs e)
        {

            if (m_fmod != null)
            {
                m_fmod.UpdateDebugInfo(m_audioPlayer.GetDebugInfo());
            }
        }

        private void UpdateTimer_Tick(object sender, EventArgs e)
        {
            m_player.Update();
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            if (m_player.IsReady && m_player.IsPlaying)
            {
                m_editorForm.ActiveSurface.InvalidateView();
            }
        }

        private void CheckAndUpdatePlayPauseIcons()
        {
            bool isPaused = m_player.IsPaused;
            bool isStopped = m_player.IsStopped;

            if (isPaused != m_isPaused || isStopped)
            {
                m_isPaused = isPaused;

                if (m_isPaused || isStopped)
                {
                    m_isPaused = true;
                    toolStripButton1.Image = StudioTheme.CreatePlayIcon(StudioTheme.TimingCyan);
                    playPauseToolStripMenuItem.Image = Resources.icon_play;
                }
                else
                {
                    toolStripButton1.Image = StudioTheme.CreatePauseIcon(StudioTheme.TimingCyan);
                    playPauseToolStripMenuItem.Image = Resources.icon_pause;
                }
            }
        }

        private void toolStripButton1_Click(object sender, EventArgs e)
        {
            if (m_player.IsStopped)
            {
                // enable the timer
                //RenderTimer.Enabled = true;
                //UpdateTimer.Enabled = true;
                // then play on tick 0
                m_player.Play(0);
            }
            else
            {

                if (m_isPaused)
                {
                    m_audioPlayer.PauseAllSounds();
                    m_player.Resume();
                }
                else
                {
                    m_player.Pause();
                    m_audioPlayer.PauseAllSounds();
                }
            }
        }

        private void toolStripButton2_Click(object sender, EventArgs e)
        {
            m_audioPlayer.StopAllSounds();
            //RenderTimer.Enabled = false;
            m_player.Reset();
        }

        private void zoomMenu_DropDownItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {
            /*
            foreach (ToolStripMenuItem it in zoomMenu.DropDown.Items) {
                it.Checked = false;
            }
                
            (e.ClickedItem as ToolStripMenuItem).Checked = true;

            String s = e.ClickedItem.Text;

            int ind = s.IndexOf(" %");
            s = s.Remove(ind);

            float z = Int32.Parse(s);
            _editorForm.Editor.SetZoom(z / 100);
            */
        }

        private void openToolStripMenuItem_Click(object sender, EventArgs e)
        {
            openFileDialog1.Filter = _loadHandler.GetFilter();
            DialogResult result = openFileDialog1.ShowDialog();
            if (result == DialogResult.OK)
            {
                OpenFile(openFileDialog1.FileName, openFileDialog1.FilterIndex);
            }
        }

        private void follwTrackPrgressToolStripMenuItem_DropDownItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {

        }

        private void follwTrackPrgressToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ToolStripMenuItem item = sender as ToolStripMenuItem;
            if (item != null)
            {
                item.Checked = !item.Checked;
                m_editorForm.Editor.FollowTracksProgressWhilePlaying = item.Checked;
            }
        }

        private void propertiesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            m_propertiesForm.Show(dockPanel);
        }

        private void debugOutputToolStripMenuItem_Click(object sender, EventArgs e)
        {
            m_debugOutput.Show(dockPanel);
        }

        private void soundsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            m_audioList.Show(dockPanel);
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {

            string configFile = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "layout.config");
            //if (m_bSaveLayout)
            dockPanel.SaveAsXml(configFile);
            //else if (File.Exists(configFile))
            //    File.Delete(configFile);

        }

        private IDockContent GetContentFromPersistString(string persistString)
        {
            if (persistString == typeof(PropertiesForm).ToString())
                return m_propertiesForm;

            else if (persistString == typeof(EditorForm).ToString())
                return m_editorForm;

            else if (persistString == typeof(DebugOutput).ToString())
                return m_debugOutput;

            else if (persistString == typeof(AudioListForm).ToString())
                return m_audioList;

            else if (persistString == typeof(Panels.FModForm).ToString())
                return m_fmod;

            else if (persistString == typeof(Panels.NoteSelectForm).ToString())
                return m_notes;


#if ENABLE_EVENT_FORM
            else if (persistString == typeof(Panels.EventsListForm).ToString())
                return _eventsForm;
#endif

            else
            {
                return null;
            }
        }

        private void resetZoomToolStripMenuItem_Click(object sender, EventArgs e)
        {
            m_editorForm.ActiveSurface.TrySetTimeZoom(0.5f);
        }

        private void saveAsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            saveFileDialog1.Filter = _saveHandler.GetFilter();
            saveFileDialog1.DefaultExt = _saveHandler.GetDefaultExtension();

            if (saveFileDialog1.ShowDialog() != DialogResult.OK)
            {
                return;
            }
            SaveFile(saveFileDialog1.FileName, saveFileDialog1.FilterIndex);
        }

        private void fMODInformationsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (m_fmod == null || m_fmod.IsDisposed)
            {
                m_fmod = new Panels.FModForm();
                StudioTheme.ApplyToForm(m_fmod);
            }
            if (!m_fmod.Visible)
            {
                m_fmod.Show(dockPanel);
            }
        }

        private void toolStripComboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            Console.WriteLine("ok");
        }

        private void toolStripDropDownButton1_DropDownItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {
            foreach (ToolStripMenuItem it in toolStripDropDownButton1.DropDown.Items)
            {
                it.Checked = false;
            }
            (e.ClickedItem as ToolStripMenuItem).Checked = true;

            String s = e.ClickedItem.Text;
            toolStripDropDownButton1.Text = "Quantize  " + s;
            string stringValue = s.Replace("1/", "");

            int value = 1;
            if (Int32.TryParse(stringValue, out value))
            {
                var editor = m_editorForm.Editor;
                editor.NoteValue = value;
                editor.Redraw();
            }
        }

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            AboutForm form = new AboutForm();
            form.ShowDialog(this);
        }

        private void notesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            m_notes.Show(dockPanel);
        }

        private void fullScreenToolStripMenuItem_Click(object sender, EventArgs e)
        {

            m_isFullScreen = !m_isFullScreen;

            fullScreenToolStripMenuItem.Checked = m_isFullScreen;

            if (m_isFullScreen)
            {
                _oldState = this.WindowState;

                //this.WindowState = FormWindowState.Normal;

                //DJMaxEditor.libs.WinApi.SetWinFullScreen(this.Handle);
                this.TopMost = true;
                this.WindowState = FormWindowState.Maximized;
                this.FormBorderStyle = FormBorderStyle.None;


            }
            else
            {
                this.TopMost = false;
                this.FormBorderStyle = FormBorderStyle.Sizable;
                this.WindowState = _oldState;
            }
            //this.TopMost = true;
            //
        }

        private void dockPanel_DragEnter(object sender, DragEventArgs e) { }

        private void dockPanel_DragDrop(object sender, DragEventArgs e) { }

        private void MainForm_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        private void MainForm_DragDrop(object sender, DragEventArgs e)
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            foreach (string file in files)
            {
                if (File.Exists(file))
                {
                    OpenFile(file);
                    break;
                }
            }

        }

        private void eventsToolStripMenuItem_Click(object sender, EventArgs e)
        {
#if ENABLE_EVENT_FORM
            _eventsForm.Show(dockPanel);
#endif
        }

        private void undoToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (CanMutateThroughActiveSurface())
            {
                m_editorForm.Editor.UndoManager.Undo();
            }
        }

        private void redoToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (CanMutateThroughActiveSurface())
            {
                m_editorForm.Editor.UndoManager.Redo();
            }
        }

        private void UndoManager_OnUndoRedo(object sender, EventArgs e)
        {

            UndoManager manager = sender as UndoManager;

            if (manager == null)
            {
                return;
            }

            redoToolStripMenuItem.Enabled = manager.CanRedo;
            undoToolStripMenuItem.Enabled = manager.CanUndo;
            m_editorForm.ActiveSurface.InvalidateView();
        }

        private void AudioList_onPlayPressed(object sender)
        {
            AudioListForm audioListForm = sender as AudioListForm;
            if (null == audioListForm) 
            {
                return;
            }

            var selection = audioListForm.GetCurrentSelection();
            if (null == selection)
            {
                return;
            }

            this.m_audioPlayer.StopSound(ReservedChannel);
            this.m_audioPlayer.PlaySound(ReservedChannel, selection.InsNum, 1, 64);
        }

        private void AudioList_onStopPressed(object sender)
        {
            AudioListForm audioListForm = sender as AudioListForm;
            if (null == audioListForm)
            {
                return;
            }

            var selection = audioListForm.GetCurrentSelection();
            if (null == selection)
            {
                return;
            }

            this.m_audioPlayer.StopSound(ReservedChannel);
        }

#endregion // private defs

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.Close();
            Application.Exit();
        }

        private void trackLengthTextBox_TextChanged(object sender, EventArgs e)
        {
            if (!m_editorForm.IsLegacySurfaceActive)
            {
                return;
            }

            this.m_editorForm.Editor.UpdateDrawableZone();
            this.m_editorForm.Editor.UpdateScrollbars();

        }

        private void ThemeDropDownButton_DropDownItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {
            if (!(sender is ToolStripDropDownButton toolStripDropDownButton))
            {
                return;
            }

            if (!(e.ClickedItem is ToolStripMenuItem toolStripMenuItem))
            {
                return;
            }

            var editor = m_editorForm.Editor;
            var themes = editor.EventsThemeList;
            foreach (var theme in themes)
            {
                if (toolStripMenuItem.Text != theme.GetName())
                {
                    continue;
                }

                foreach (ToolStripMenuItem it in toolStripDropDownButton.DropDown.Items)
                {
                    it.Checked = false;
                }

                toolStripMenuItem.Checked = true;
                ApplyEventsTheme(theme);
                break;
            }
        }

        private void ApplyEventsTheme(IEventRenderer theme)
        {
            if (null == theme)
            {
                return;
            }

            m_editorForm.Editor.CurrentEventsTheme = theme;
            ThemeDropDownButton.Text = "Events theme  " + theme.GetName();
            m_notes.ApplyTheme(theme);
            ApplyPreviewProfileFromEventTheme();
        }

        private void ApplyZonesTheme(IZoneRenderer theme)
        {
            if (null == theme)
            {
                return;
            }

            m_editorForm.Editor.CurrentZonesTheme = theme;
            zoneRendererToolStripDropDownButton.Text = "Zones theme  " + theme.GetName();
        }


        private void eventDisplayModeToolStripDropDownButton_DropDownItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {
            if (!(sender is ToolStripDropDownButton toolStripDropDownButton))
            {
                return;
            }

            if (!(e.ClickedItem is ToolStripMenuItem toolStripMenuItem))
            {
                return;
            }

            var eventDisplayModes = GetAll<EventDisplayMode>();
            foreach (var eventDisplayMode in eventDisplayModes)
            {
                if (eventDisplayMode.Value != toolStripMenuItem.Text)
                {
                    continue;
                }

                foreach (ToolStripMenuItem it in toolStripDropDownButton.DropDown.Items)
                {
                    it.Checked = false;
                }

                m_editorForm.Editor.EventDisplayMode = (EventDisplayMode)eventDisplayMode.Key;
                toolStripMenuItem.Checked = true;
                toolStripDropDownButton.Text = eventDisplayMode.Value;
            }
        }

        private void allToolStripMenuItem_Click(object sender, EventArgs e)
        {
            m_editorForm.SelectAll();
        }

        private void deselectToolStripMenuItem_Click(object sender, EventArgs e)
        {
            m_editorForm.Deselect();
        }

        private void inverseToolStripMenuItem_Click(object sender, EventArgs e)
        {
            m_editorForm.InverseSelection();
        }

        private void zoneRendererToolStripDropDownButton_DropDownItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {
            if (!(sender is ToolStripDropDownButton toolStripDropDownButton))
            {
                return;
            }

            if (!(e.ClickedItem is ToolStripMenuItem toolStripMenuItem))
            {
                return;
            }

            var editor = m_editorForm.Editor;
            var themes = editor.ZonesThemeList;
            foreach (var theme in themes)
            {
                if (toolStripMenuItem.Text != theme.GetName())
                {
                    continue;
                }

                foreach (ToolStripMenuItem it in toolStripDropDownButton.DropDown.Items)
                {
                    it.Checked = false;
                }
                toolStripMenuItem.Checked = true;
                ApplyZonesTheme(theme);
                break;
            }
        }

        private void PlayerTimer_Tick(object sender, EventArgs e)
        {
            var tm = TimeSpan.FromMilliseconds(m_player.GetCurrentMsTime());
            var tick = m_player.GetCurrentTick();
            var date = new DateTime(tm.Ticks);

            currentProgress.Text = date.ToString("HH:mm:ss") + "  |  TICK " + tick;

            if (m_player.IsReady)
            {
                m_playerData.CurrentTick = tick;
                m_editorForm.ActiveSurface.PlayheadVirtualTick = m_playerData.VirtualCurrentTick;
                m_preview.RefreshPlayback();
            }
        }

        private void currentProgress_Click(object sender, EventArgs e)
        {
            PlayTickDialog t = new PlayTickDialog(m_player.GetCurrentTick());
            var closeDialogSuccess = t.ShowDialog(this) == DialogResult.OK;
            if (!closeDialogSuccess)
            {
                return;
            }
            int target = int.Parse(t.SetTick);

            m_audioPlayer.StopAllSounds();
            m_player.Reset();
            m_player.Play(target);
        }
    }
}

/*
richtextbox1.Select(richtextbox1.TextLength, 0) 
richtextbox1.SelectionColor = Color.Green 
richtextbox1.AppendText("Append Text might be good idea since select text puts text where the carret is")
 */
