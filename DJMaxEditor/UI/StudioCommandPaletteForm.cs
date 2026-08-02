using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using DJMaxEditor.Editor.Commands;

namespace DJMaxEditor.UI
{
    public sealed class StudioCommandPaletteEntry
    {
        public string Id { get; internal set; }
        public string DisplayName { get; internal set; }
        public string Category { get; internal set; }
        public Keys Shortcut { get; internal set; }
        public bool IsAvailable { get; internal set; }
        public string DisabledExplanation { get; internal set; }
        public bool HasShortcutConflict { get; internal set; }
        public string ConflictExplanation { get; internal set; }
    }

    /// <summary>
    /// Searchable command and shortcut center. Shortcut changes live only in the
    /// supplied registry, so closing the editor restores every registered default.
    /// </summary>
    public sealed class StudioCommandPaletteForm : Form
    {
        private readonly StudioCommandRegistry _registry;
        private readonly Func<StudioCommandContext> _activeContext;
        private readonly TextBox _search;
        private readonly DataGridView _commandList;
        private readonly Label _summary;
        private readonly Label _detail;
        private readonly Label _captureHint;
        private readonly Button _run;
        private readonly Button _reassign;
        private readonly Button _reset;
        private readonly Button _resetAll;
        private StudioCommandPaletteEntry[] _visibleEntries =
            new StudioCommandPaletteEntry[0];
        private bool _capturingShortcut;

        public StudioCommandPaletteForm(
            StudioCommandRegistry registry,
            Func<StudioCommandContext> activeContext)
        {
            if (registry == null) throw new ArgumentNullException("registry");
            if (activeContext == null) throw new ArgumentNullException("activeContext");

            _registry = registry;
            _activeContext = activeContext;

            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = StudioDesignSystem.Void;
            ClientSize = new Size(820, 570);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            KeyPreview = true;
            MaximizeBox = false;
            MinimizeBox = false;
            MinimumSize = new Size(700, 500);
            Padding = new Padding(1);
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Text = "Command Palette / Shortcut Center";

            var header = BuildHeader();
            var searchHost = BuildSearchHost(out _search);
            _commandList = BuildCommandList();
            var detailHost = BuildDetailHost(
                out _summary,
                out _detail,
                out _captureHint,
                out _run,
                out _reassign,
                out _reset,
                out _resetAll);

            Controls.Add(_commandList);
            Controls.Add(detailHost);
            Controls.Add(searchHost);
            Controls.Add(header);

            _search.TextChanged += delegate { RefreshEntries(true); };
            _commandList.SelectionChanged += delegate { RefreshSelectionDetail(); };
            _commandList.CellDoubleClick += delegate { ExecuteAndClose(); };
            _run.Click += delegate { ExecuteAndClose(); };
            _reassign.Click += delegate { BeginShortcutCapture(); };
            _reset.Click += delegate
            {
                ResetSelectedShortcut();
                _captureHint.Text = "Default shortcut restored for this session.";
            };
            _resetAll.Click += delegate
            {
                _registry.ResetShortcutsToDefaults();
                RefreshEntries(false);
                _captureHint.Text = "All shortcuts restored to registered defaults.";
            };

            Activated += delegate
            {
                RefreshEntries(false);
                _search.Focus();
            };

            RefreshEntries(true);
        }

        public string SearchText
        {
            get { return _search.Text; }
            set { _search.Text = value ?? string.Empty; }
        }

        public IEnumerable<StudioCommandPaletteEntry> VisibleEntries
        {
            get { return _visibleEntries; }
        }

        public string SelectedCommandId
        {
            get
            {
                StudioCommandPaletteEntry selected = SelectedEntry;
                return selected == null ? null : selected.Id;
            }
            set
            {
                foreach (DataGridViewRow row in _commandList.Rows)
                {
                    var entry = row.Tag as StudioCommandPaletteEntry;
                    if (entry != null &&
                        string.Equals(entry.Id, value, StringComparison.OrdinalIgnoreCase))
                    {
                        row.Selected = true;
                        _commandList.CurrentCell = row.Cells[0];
                        RefreshSelectionDetail();
                        return;
                    }
                }
            }
        }

        public bool ExecuteSelected(out string feedback)
        {
            StudioCommandPaletteEntry selected = SelectedEntry;
            if (selected == null)
            {
                feedback = "Choose a command first.";
                return false;
            }

            StudioCommand command = _registry.Get(selected.Id);
            if (command == null)
            {
                feedback = "The selected command is no longer registered.";
                RefreshEntries(true);
                return false;
            }

            if (!selected.IsAvailable)
            {
                feedback = string.IsNullOrWhiteSpace(selected.DisabledExplanation)
                    ? "This command is currently unavailable."
                    : selected.DisabledExplanation;
                _captureHint.Text = feedback;
                return false;
            }

            bool executed = command.TryExecute(out feedback);
            if (!executed)
            {
                _captureHint.Text = string.IsNullOrWhiteSpace(feedback)
                    ? "This command is currently unavailable."
                    : feedback;
            }
            return executed;
        }

        public bool ReassignSelectedShortcut(Keys shortcut, out string feedback)
        {
            StudioCommandPaletteEntry selected = SelectedEntry;
            if (selected == null)
            {
                feedback = "Choose a command first.";
                return false;
            }

            if (!_registry.ReassignShortcut(selected.Id, shortcut))
            {
                feedback = "The selected command is no longer registered.";
                return false;
            }

            string selectedId = selected.Id;
            RefreshEntries(false);
            SelectedCommandId = selectedId;
            StudioCommandPaletteEntry refreshed = SelectedEntry;
            feedback = refreshed != null && refreshed.HasShortcutConflict
                ? refreshed.ConflictExplanation
                : "Shortcut changed for this session.";
            return true;
        }

        public void ResetSelectedShortcut()
        {
            StudioCommandPaletteEntry selected = SelectedEntry;
            if (selected == null)
            {
                return;
            }

            string selectedId = selected.Id;
            _registry.ResetShortcut(selectedId);
            RefreshEntries(false);
            SelectedCommandId = selectedId;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RefreshEntries(false);
            _search.SelectAll();
            _search.Focus();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (_capturingShortcut)
            {
                CaptureShortcut(e);
                return;
            }

            if (e.KeyCode == Keys.Escape)
            {
                Close();
                e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode == Keys.Enter)
            {
                ExecuteAndClose();
                e.SuppressKeyPress = true;
                return;
            }

            if (e.Control && e.KeyCode == Keys.K)
            {
                _search.SelectAll();
                _search.Focus();
                e.SuppressKeyPress = true;
                return;
            }

            base.OnKeyDown(e);
        }

        private Panel BuildHeader()
        {
            var header = new Panel
            {
                BackColor = StudioDesignSystem.Deck,
                Dock = DockStyle.Top,
                Height = 76,
                Padding = new Padding(22, 14, 22, 8)
            };
            var eyebrow = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Font = StudioDesignSystem.UtilityFont(7.5f),
                ForeColor = StudioDesignSystem.PulseCyan,
                Height = 18,
                Text = "DJMAX  //  WORKFLOW CONTROL"
            };
            var title = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                Font = StudioDesignSystem.DisplayFont(17f),
                ForeColor = StudioDesignSystem.Frost,
                Text = "COMMAND PALETTE  /  SHORTCUT CENTER"
            };
            header.Controls.Add(title);
            header.Controls.Add(eyebrow);
            return header;
        }

        private static Panel BuildSearchHost(out TextBox search)
        {
            var host = new Panel
            {
                BackColor = StudioDesignSystem.Deck,
                Dock = DockStyle.Top,
                Height = 68,
                Padding = new Padding(22, 8, 22, 12)
            };
            var label = new Label
            {
                Dock = DockStyle.Left,
                Font = StudioDesignSystem.DisplayFont(13f),
                ForeColor = StudioDesignSystem.BeatViolet,
                Text = ">",
                TextAlign = ContentAlignment.MiddleCenter,
                Width = 32
            };
            search = new TextBox
            {
                BackColor = StudioDesignSystem.Lift,
                BorderStyle = BorderStyle.FixedSingle,
                Dock = DockStyle.Fill,
                Font = StudioDesignSystem.BodyFont(13f),
                ForeColor = StudioDesignSystem.Frost,
                Margin = Padding.Empty
            };
            host.Controls.Add(search);
            host.Controls.Add(label);
            return host;
        }

        private static DataGridView BuildCommandList()
        {
            var list = new DataGridView
            {
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = StudioDesignSystem.Void,
                BorderStyle = BorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
                ColumnHeadersHeight = 32,
                Dock = DockStyle.Fill,
                EnableHeadersVisualStyles = false,
                GridColor = StudioDesignSystem.Border,
                MultiSelect = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                RowTemplate = { Height = 38 },
                SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            list.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = StudioDesignSystem.Deck,
                Font = StudioDesignSystem.UtilityFont(8f),
                ForeColor = StudioDesignSystem.Muted,
                Padding = new Padding(8, 0, 8, 0),
                SelectionBackColor = StudioDesignSystem.Deck
            };
            list.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = StudioDesignSystem.Void,
                Font = StudioDesignSystem.BodyFont(9.5f),
                ForeColor = StudioDesignSystem.Frost,
                Padding = new Padding(8, 0, 8, 0),
                SelectionBackColor = StudioDesignSystem.Selected,
                SelectionForeColor = StudioDesignSystem.Frost
            };

            list.Columns.Add(new DataGridViewTextBoxColumn
            {
                FillWeight = 43,
                HeaderText = "COMMAND",
                Name = "Command"
            });
            list.Columns.Add(new DataGridViewTextBoxColumn
            {
                FillWeight = 19,
                HeaderText = "CATEGORY",
                Name = "Category"
            });
            list.Columns.Add(new DataGridViewTextBoxColumn
            {
                FillWeight = 20,
                HeaderText = "SHORTCUT",
                Name = "Shortcut"
            });
            list.Columns.Add(new DataGridViewTextBoxColumn
            {
                FillWeight = 18,
                HeaderText = "STATUS",
                Name = "Status"
            });
            return list;
        }

        private static Panel BuildDetailHost(
            out Label summary,
            out Label detail,
            out Label captureHint,
            out Button run,
            out Button reassign,
            out Button reset,
            out Button resetAll)
        {
            var host = new Panel
            {
                BackColor = StudioDesignSystem.Deck,
                Dock = DockStyle.Bottom,
                Height = 142,
                Padding = new Padding(22, 12, 22, 12)
            };

            summary = new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Top,
                Font = StudioDesignSystem.UtilityFont(8f),
                ForeColor = StudioDesignSystem.PulseCyan,
                Height = 20
            };
            detail = new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Top,
                Font = StudioDesignSystem.BodyFont(9f),
                ForeColor = StudioDesignSystem.Muted,
                Height = 24
            };
            captureHint = new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Top,
                Font = StudioDesignSystem.UtilityFont(7.5f),
                ForeColor = StudioDesignSystem.SignalAmber,
                Height = 22,
                Text = "ENTER runs  //  ESC closes  //  shortcut edits last for this session only"
            };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 40,
                Padding = Padding.Empty,
                WrapContents = false
            };
            run = CreateActionButton("RUN  Enter", 112, StudioDesignSystem.PulseCyan);
            reassign = CreateActionButton("REASSIGN", 108, StudioDesignSystem.BeatViolet);
            reset = CreateActionButton("RESET SELECTED", 128, StudioDesignSystem.Muted);
            resetAll = CreateActionButton("RESET ALL", 96, StudioDesignSystem.Muted);
            buttons.Controls.Add(run);
            buttons.Controls.Add(reassign);
            buttons.Controls.Add(reset);
            buttons.Controls.Add(resetAll);

            host.Controls.Add(buttons);
            host.Controls.Add(captureHint);
            host.Controls.Add(detail);
            host.Controls.Add(summary);
            return host;
        }

        private static Button CreateActionButton(string text, int width, Color foreground)
        {
            Button button = StudioDesignSystem.CreateDeckButton(text);
            button.ForeColor = foreground;
            button.Height = 32;
            button.Margin = new Padding(6, 4, 0, 4);
            button.Width = width;
            return button;
        }

        private StudioCommandPaletteEntry SelectedEntry
        {
            get
            {
                if (_commandList.SelectedRows.Count == 0)
                {
                    return null;
                }
                return _commandList.SelectedRows[0].Tag as StudioCommandPaletteEntry;
            }
        }

        private void RefreshEntries(bool selectFirst)
        {
            string previousId = selectFirst ? null : SelectedCommandId;
            Dictionary<string, StudioShortcutConflict> conflicts =
                _registry.FindShortcutConflicts()
                    .SelectMany(conflict => conflict.CommandIds.Select(
                        id => new { Id = id, Conflict = conflict }))
                    .ToDictionary(pair => pair.Id, pair => pair.Conflict,
                        StringComparer.OrdinalIgnoreCase);

            _visibleEntries = _registry.Search(_search.Text)
                .Select(command => CreateEntry(command, conflicts))
                .ToArray();

            _commandList.Rows.Clear();
            foreach (StudioCommandPaletteEntry entry in _visibleEntries)
            {
                int index = _commandList.Rows.Add(
                    entry.DisplayName,
                    entry.Category.ToUpperInvariant(),
                    FormatShortcut(entry.Shortcut),
                    entry.HasShortcutConflict
                        ? "CONFLICT"
                        : (entry.IsAvailable ? "READY" : "UNAVAILABLE"));
                DataGridViewRow row = _commandList.Rows[index];
                row.Tag = entry;
                row.Cells[2].Style.Font = StudioDesignSystem.UtilityFont(8.5f);
                row.Cells[3].Style.Font = StudioDesignSystem.UtilityFont(7.5f);
                row.Cells[3].Style.ForeColor = entry.HasShortcutConflict
                    ? StudioDesignSystem.FaultRed
                    : (entry.IsAvailable
                        ? StudioDesignSystem.PulseCyan
                        : StudioDesignSystem.Disabled);
                if (!entry.IsAvailable)
                {
                    row.DefaultCellStyle.ForeColor = StudioDesignSystem.Disabled;
                }
            }

            if (!string.IsNullOrEmpty(previousId))
            {
                SelectedCommandId = previousId;
            }
            if (_commandList.SelectedRows.Count == 0 && _commandList.Rows.Count > 0)
            {
                _commandList.Rows[0].Selected = true;
                _commandList.CurrentCell = _commandList.Rows[0].Cells[0];
            }

            RefreshSelectionDetail();
        }

        private StudioCommandPaletteEntry CreateEntry(
            StudioCommand command,
            IDictionary<string, StudioShortcutConflict> conflicts)
        {
            bool contextIsActive =
                command.ActiveContext == StudioCommandContext.Global ||
                (command.ActiveContext & _activeContext()) != 0;
            bool commandIsAvailable = command.IsAvailable;
            bool isAvailable = contextIsActive && commandIsAvailable;
            StudioShortcutConflict conflict;
            bool hasConflict = conflicts.TryGetValue(command.Id, out conflict);
            string[] otherIds = hasConflict
                ? conflict.CommandIds
                    .Where(id => !string.Equals(id, command.Id,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray()
                : new string[0];

            return new StudioCommandPaletteEntry
            {
                Id = command.Id,
                DisplayName = command.DisplayName,
                Category = command.Category,
                Shortcut = _registry.GetCurrentShortcut(command.Id),
                IsAvailable = isAvailable,
                DisabledExplanation = isAvailable
                    ? string.Empty
                    : (!commandIsAvailable
                        ? command.DisabledExplanation
                        : "Unavailable in the current editor context."),
                HasShortcutConflict = hasConflict,
                ConflictExplanation = hasConflict
                    ? "Shortcut conflict with " + string.Join(", ", otherIds) + "."
                    : string.Empty
            };
        }

        private void RefreshSelectionDetail()
        {
            StudioCommandPaletteEntry selected = SelectedEntry;
            bool hasSelection = selected != null;
            _run.Enabled = hasSelection && selected.IsAvailable;
            _reassign.Enabled = hasSelection;
            _reset.Enabled = hasSelection;

            if (!hasSelection)
            {
                _summary.Text = "NO MATCHING COMMANDS";
                _detail.Text = "Try a command name, stable ID, or category.";
                return;
            }

            _summary.Text = selected.Id + "  //  " +
                FormatShortcut(selected.Shortcut);
            if (selected.HasShortcutConflict)
            {
                _detail.ForeColor = StudioDesignSystem.FaultRed;
                _detail.Text = selected.ConflictExplanation;
            }
            else if (!selected.IsAvailable)
            {
                _detail.ForeColor = StudioDesignSystem.SignalAmber;
                _detail.Text = string.IsNullOrWhiteSpace(selected.DisabledExplanation)
                    ? "This command is unavailable in the current editor state."
                    : selected.DisabledExplanation;
            }
            else
            {
                _detail.ForeColor = StudioDesignSystem.Muted;
                _detail.Text = selected.Category +
                    " command is available in the current editor state.";
            }
        }

        private void BeginShortcutCapture()
        {
            if (SelectedEntry == null)
            {
                return;
            }
            _capturingShortcut = true;
            _captureHint.Text =
                "PRESS A SHORTCUT  //  Backspace clears  //  Escape cancels";
            _captureHint.ForeColor = StudioDesignSystem.PulseCyan;
        }

        private void CaptureShortcut(KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;

            if (e.KeyCode == Keys.Escape)
            {
                EndShortcutCapture("Shortcut change cancelled.");
                return;
            }

            if (e.KeyCode == Keys.ControlKey ||
                e.KeyCode == Keys.ShiftKey ||
                e.KeyCode == Keys.Menu)
            {
                return;
            }

            Keys shortcut = e.KeyCode == Keys.Back
                ? Keys.None
                : e.KeyData;
            string feedback;
            ReassignSelectedShortcut(shortcut, out feedback);
            EndShortcutCapture(feedback);
        }

        private void EndShortcutCapture(string feedback)
        {
            _capturingShortcut = false;
            _captureHint.ForeColor = StudioDesignSystem.SignalAmber;
            _captureHint.Text = feedback;
        }

        private void ExecuteAndClose()
        {
            string feedback;
            if (ExecuteSelected(out feedback))
            {
                Close();
            }
        }

        private static string FormatShortcut(Keys shortcut)
        {
            if (shortcut == Keys.None)
            {
                return "UNASSIGNED";
            }

            var parts = new List<string>();
            if ((shortcut & Keys.Control) == Keys.Control) parts.Add("Ctrl");
            if ((shortcut & Keys.Shift) == Keys.Shift) parts.Add("Shift");
            if ((shortcut & Keys.Alt) == Keys.Alt) parts.Add("Alt");

            Keys keyCode = shortcut & Keys.KeyCode;
            if (keyCode != Keys.None)
            {
                string keyName = keyCode == Keys.Space
                    ? "Space"
                    : keyCode.ToString();
                parts.Add(keyName);
            }
            return string.Join("+", parts);
        }
    }
}
