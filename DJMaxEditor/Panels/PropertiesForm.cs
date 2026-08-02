using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using DJMaxEditor.DJMax;
using DJMaxEditor.Editor;
using DJMaxEditor.UI;

namespace DJMaxEditor
{
    public partial class PropertiesForm : ToolWindow
    {
        private readonly Label _summary;
        private readonly Label _capability;
        private readonly TextBox _eventType;
        private readonly TextBox _sound;
        private readonly TextBox _source;
        private readonly NumericUpDown _timing;
        private readonly NumericUpDown _duration;
        private readonly NumericUpDown _attribute;
        private readonly NumericUpDown _track;
        private EditorDocumentContext _document;
        private bool _suppressCommits;

        public PropertiesForm()
        {
            InitializeComponent();
            TabText = "Inspector";
            Text = "Inspector";
            propertyGrid1.Visible = true;
            propertyGrid1.Dock = DockStyle.Bottom;
            propertyGrid1.Height = 164;
            propertyGrid1.BackColor = StudioDesignSystem.Deck;
            propertyGrid1.CategoryForeColor = StudioDesignSystem.PulseCyan;
            propertyGrid1.CommandsBackColor = StudioDesignSystem.Deck;
            propertyGrid1.CommandsForeColor = StudioDesignSystem.Frost;
            propertyGrid1.HelpBackColor = StudioDesignSystem.Deck;
            propertyGrid1.HelpForeColor = StudioDesignSystem.Muted;
            propertyGrid1.LineColor = StudioDesignSystem.Border;
            propertyGrid1.ViewBackColor = StudioDesignSystem.Void;
            propertyGrid1.ViewForeColor = StudioDesignSystem.Frost;

            var header = new Panel
            {
                BackColor = StudioDesignSystem.Deck,
                Dock = DockStyle.Top,
                Height = 82,
                Padding = new Padding(12, 10, 12, 8)
            };
            var eyebrow = new Label
            {
                Dock = DockStyle.Top,
                Font = StudioDesignSystem.UtilityFont(7.5f),
                ForeColor = StudioDesignSystem.PulseCyan,
                Height = 20,
                Text = "INSPECTOR  //  SHARED SELECTION"
            };
            _summary = new Label
            {
                Dock = DockStyle.Top,
                Font = StudioDesignSystem.DisplayFont(12f),
                ForeColor = StudioDesignSystem.Frost,
                Height = 28,
                Text = "NO SELECTION"
            };
            _capability = new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                Font = StudioDesignSystem.BodyFont(8f),
                ForeColor = StudioDesignSystem.Muted,
                Text = "Open a chart to inspect its editing capabilities."
            };
            header.Controls.Add(_capability);
            header.Controls.Add(_summary);
            header.Controls.Add(eyebrow);

            var fields = new TableLayoutPanel
            {
                AutoScroll = true,
                BackColor = StudioDesignSystem.Void,
                ColumnCount = 2,
                Dock = DockStyle.Fill,
                Padding = new Padding(10, 8, 10, 10),
                RowCount = 11
            };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92f));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            _eventType = CreateReadOnlyText();
            _timing = CreateNumber(0, int.MaxValue);
            _duration = CreateNumber(0, ushort.MaxValue);
            _attribute = CreateNumber(0, byte.MaxValue);
            _track = CreateNumber(0, 4095);
            _sound = CreateReadOnlyText();
            _source = CreateReadOnlyText();

            AddSection(fields, "EVENT", 0);
            AddField(fields, "Type", _eventType, 1);
            AddSection(fields, "TIMING", 2);
            AddField(fields, "Virtual tick", _timing, 3);
            AddField(fields, "Duration", _duration, 4);
            AddSection(fields, "SEMANTICS", 5);
            AddField(fields, "Attribute", _attribute, 6);
            AddField(fields, "Track", _track, 7);
            AddField(fields, "Sound", _sound, 8);
            AddSection(fields, "DOCUMENT", 9);
            AddField(fields, "Source", _source, 10);

            _timing.Validated += delegate { CommitTiming(); };
            _duration.Validated += delegate { CommitDuration(); };
            _attribute.Validated += delegate { CommitAttribute(); };
            _track.Validated += delegate { CommitTrack(); };

            var advancedHeader = new Label
            {
                BackColor = StudioDesignSystem.Deck,
                Dock = DockStyle.Bottom,
                Font = StudioDesignSystem.UtilityFont(7.5f),
                ForeColor = StudioDesignSystem.BeatViolet,
                Height = 26,
                Padding = new Padding(10, 7, 0, 0),
                Text = "ADVANCED  //  FORMAT-SPECIFIC PROPERTIES"
            };

            Controls.Add(fields);
            Controls.Add(advancedHeader);
            Controls.Add(header);
            propertyGrid1.BringToFront();
            advancedHeader.BringToFront();
            header.BringToFront();
            ShowSelection();
        }

        public object PropertyObject
        {
            get { return propertyGrid1.SelectedObject; }
            set { propertyGrid1.SelectedObject = value; }
        }

        public string SelectionSummary
        {
            get { return _summary.Text; }
        }

        public void Bind(EditorDocumentContext document)
        {
            if (_document != null)
            {
                _document.Selection.SelectionChanged -= SelectionChanged;
                _document.UndoManager.OnUndoRedo -= DocumentUndoRedo;
            }
            _document = document;
            if (_document != null)
            {
                _document.Selection.SelectionChanged += SelectionChanged;
                _document.UndoManager.OnUndoRedo += DocumentUndoRedo;
            }
            ShowSelection();
        }

        private void SelectionChanged(object sender, EventArgs e)
        {
            ShowSelection();
        }

        private void DocumentUndoRedo(object sender, UndoManager.Action action)
        {
            ShowSelection();
        }

        private void ShowSelection()
        {
            _suppressCommits = true;
            try
            {
                int count = _document == null ? 0 : _document.Selection.Count;
                bool editable = _document != null && _document.Capabilities.CanEdit;
                bool single = count == 1;

                _summary.Text = count == 0
                    ? "NO SELECTION"
                    : (count == 1 ? "1 EVENT SELECTED" : count + " EVENTS  •  MIXED VALUES");
                _capability.Text = _document == null
                    ? "Open a chart to inspect its editing capabilities."
                    : (_document.Capabilities.StatusLabel +
                        (editable ? string.Empty : "  •  " + _document.Capabilities.EditBlockReason));
                _capability.ForeColor = editable
                    ? StudioDesignSystem.PulseCyan
                    : StudioDesignSystem.SignalAmber;
                _source.Text = _document == null
                    ? "NO DOCUMENT"
                    : Path.GetFileName(_document.SourcePath);

                EventData item = single ? _document.Selection.Items[0] : null;
                _eventType.Text = item == null ? (count > 1 ? "MIXED" : "—") : item.EventType.ToString();
                _sound.Text = item == null
                    ? (count > 1 ? "MIXED" : "—")
                    : (item.Instrument == null ? "NONE" : item.Instrument.Name);

                if (item != null)
                {
                    _timing.Value = Clamp(item.VirtualTick, _timing.Minimum, _timing.Maximum);
                    _duration.Value = Clamp(item.VirtualDuration, _duration.Minimum, _duration.Maximum);
                    _attribute.Value = Clamp(item.Attribute, _attribute.Minimum, _attribute.Maximum);
                    _track.Value = Clamp(item.TrackId, _track.Minimum, _track.Maximum);
                }

                _timing.Enabled = single && editable;
                _duration.Enabled = single && editable && item.EventType == EventType.Note;
                _attribute.Enabled = single && editable;
                _track.Enabled = single && editable;
            }
            finally
            {
                _suppressCommits = false;
            }
        }

        private void CommitTiming()
        {
            EventData item;
            if (!TryGetSingle(out item)) return;
            if (_document.Edits.MoveSelection(
                0,
                Decimal.ToInt32(_timing.Value) - item.VirtualTick))
            {
                ShowSelection();
            }
        }

        private void CommitDuration()
        {
            EventData item;
            if (!TryGetSingle(out item)) return;
            if (_document.Edits.ResizeSelection(
                Decimal.ToInt32(_duration.Value) - item.VirtualDuration))
            {
                ShowSelection();
            }
        }

        private void CommitAttribute()
        {
            EventData item;
            if (!TryGetSingle(out item)) return;
            if (_document.Edits.SetSelectionAttribute((byte)_attribute.Value))
            {
                ShowSelection();
            }
        }

        private void CommitTrack()
        {
            EventData item;
            if (!TryGetSingle(out item)) return;
            if (_document.Edits.MoveSelection(
                Decimal.ToInt32(_track.Value) - (int)item.TrackId,
                0))
            {
                ShowSelection();
            }
        }

        private bool TryGetSingle(out EventData item)
        {
            item = null;
            if (_suppressCommits ||
                _document == null ||
                !_document.Capabilities.CanEdit ||
                _document.Selection.Count != 1)
            {
                return false;
            }
            item = _document.Selection.Items[0];
            return true;
        }

        private static TextBox CreateReadOnlyText()
        {
            return new TextBox
            {
                BackColor = StudioDesignSystem.Deck,
                BorderStyle = BorderStyle.FixedSingle,
                Dock = DockStyle.Fill,
                Font = StudioDesignSystem.BodyFont(8.5f),
                ForeColor = StudioDesignSystem.Frost,
                ReadOnly = true
            };
        }

        private static NumericUpDown CreateNumber(decimal minimum, decimal maximum)
        {
            return new NumericUpDown
            {
                BackColor = StudioDesignSystem.Deck,
                BorderStyle = BorderStyle.FixedSingle,
                Dock = DockStyle.Fill,
                Font = StudioDesignSystem.UtilityFont(8.5f),
                ForeColor = StudioDesignSystem.Frost,
                Maximum = maximum,
                Minimum = minimum,
                ThousandsSeparator = true
            };
        }

        private static void AddSection(TableLayoutPanel table, string text, int row)
        {
            var label = new Label
            {
                Dock = DockStyle.Fill,
                Font = StudioDesignSystem.UtilityFont(7.5f),
                ForeColor = StudioDesignSystem.BeatViolet,
                Padding = new Padding(0, 8, 0, 0),
                Text = text
            };
            table.Controls.Add(label, 0, row);
            table.SetColumnSpan(label, 2);
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
        }

        private static void AddField(TableLayoutPanel table, string name, Control value, int row)
        {
            var label = new Label
            {
                Dock = DockStyle.Fill,
                Font = StudioDesignSystem.BodyFont(8f),
                ForeColor = StudioDesignSystem.Muted,
                Padding = new Padding(0, 7, 4, 0),
                Text = name
            };
            value.Margin = new Padding(0, 3, 0, 3);
            table.Controls.Add(label, 0, row);
            table.Controls.Add(value, 1, row);
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
        }

        private static decimal Clamp(decimal value, decimal minimum, decimal maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
