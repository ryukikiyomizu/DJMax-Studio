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
        private readonly NumericUpDown _volume;
        private EditorDocumentContext _document;
        private bool _suppressCommits;
        private object _volumeGesture;

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
                Height = 76,
                Padding = new Padding(12, 9, 12, 8)
            };
            // A plain panel title, not a shouted "INSPECTOR // SHARED SELECTION" chip. The band
            // above every panel used to be an all-caps monospace slogan with a slash separator,
            // which read as decoration rather than information - the panel already has a tab that
            // says "Inspector", so the header's job is only to name what is selected.
            var eyebrow = new Label
            {
                Dock = DockStyle.Top,
                Font = StudioDesignSystem.BodyFont(8f),
                ForeColor = StudioDesignSystem.Muted,
                Height = 18,
                Text = "Selection"
            };
            _summary = new Label
            {
                Dock = DockStyle.Top,
                Font = StudioDesignSystem.DisplayFont(12f),
                ForeColor = StudioDesignSystem.Frost,
                Height = 28,
                Text = "Nothing selected"
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
                RowCount = 12
            };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92f));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            _eventType = CreateReadOnlyText();
            _timing = CreateNumber(0, int.MaxValue);
            _duration = CreateNumber(0, ushort.MaxValue);
            _attribute = CreateNumber(0, byte.MaxValue);
            _track = CreateNumber(0, 4095);
            _volume = CreateNumber(0, ChartEditController.MaxNoteVolume);
            _sound = CreateReadOnlyText();
            _source = CreateReadOnlyText();

            AddSection(fields, "Event", 0);
            AddField(fields, "Type", _eventType, 1);
            AddSection(fields, "Timing", 2);
            AddField(fields, "Virtual tick", _timing, 3);
            AddField(fields, "Duration", _duration, 4);
            AddSection(fields, "Semantics", 5);
            AddField(fields, "Attribute", _attribute, 6);
            AddField(fields, "Track", _track, 7);
            AddField(fields, "Sound", _sound, 8);
            AddField(fields, "Volume", _volume, 9);
            AddSection(fields, "Document", 10);
            AddField(fields, "Source", _source, 11);

            _timing.Validated += delegate { CommitTiming(); };
            _duration.Validated += delegate { CommitDuration(); };
            _attribute.Validated += delegate { CommitAttribute(); };
            _track.Validated += delegate { CommitTrack(); };
            // Volume is the one field that commits to the whole selection: setting 300 notes to
            // the same loudness is the normal way to use it, and it is one undo step.
            _volume.Validated += delegate { CommitVolume(); };
            _volume.ValueChanged += delegate { CommitVolume(); };
            // One focus session is one undo entry, so spinning the arrows ten times does not cost
            // ten Ctrl+Z presses. Leaving the field ends the group.
            _volume.Enter += delegate { _volumeGesture = new object(); };
            _volume.Leave += delegate { _volumeGesture = null; };

            var advancedHeader = new Label
            {
                BackColor = StudioDesignSystem.Deck,
                Dock = DockStyle.Bottom,
                Font = StudioDesignSystem.BodyFont(8f),
                ForeColor = StudioDesignSystem.Muted,
                Height = 24,
                Padding = new Padding(10, 6, 0, 0),
                Text = "Advanced"
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
                    ? "Nothing selected"
                    : (count == 1 ? "1 event" : count + " events, mixed values");
                _capability.Text = _document == null
                    ? "Open a chart to inspect its editing capabilities."
                    : (_document.Capabilities.StatusLabel +
                        (editable ? string.Empty : " - " + _document.Capabilities.EditBlockReason));
                _capability.ForeColor = editable
                    ? StudioDesignSystem.PulseCyan
                    : StudioDesignSystem.SignalAmber;
                _source.Text = _document == null
                    ? "No document"
                    : Path.GetFileName(_document.SourcePath);

                EventData item = single ? _document.Selection.Items[0] : null;
                _eventType.Text = item == null ? (count > 1 ? "Mixed" : "-") : item.EventType.ToString();
                _sound.Text = item == null
                    ? (count > 1 ? "Mixed" : "-")
                    : (item.Instrument == null ? "None" : item.Instrument.Name);

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

                // Volume reads and writes the whole selection, so it stays live for a marquee of
                // notes. A mixed selection shows the loudest note rather than a lie about being
                // uniform; typing a value flattens them all to it, which is what the field says.
                byte volume;
                bool anyNotes = TryGetSelectionVolume(out volume);
                if (anyNotes)
                {
                    _volume.Value = Clamp(volume, _volume.Minimum, _volume.Maximum);
                }
                _volume.Enabled = editable && anyNotes;
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

        /// <summary>
        /// Flattens every selected note to the field's value. Unlike the other commits this one
        /// deliberately accepts a multi-note selection - per-note volume is most useful applied to
        /// a phrase - and groups the whole focus session into one undo entry.
        /// </summary>
        private void CommitVolume()
        {
            if (_suppressCommits ||
                _document == null ||
                !_document.Capabilities.CanEdit ||
                _document.Selection.Count == 0)
            {
                return;
            }
            if (_document.Edits.SetSelectionVolume(
                (byte)Decimal.ToInt32(_volume.Value),
                _volumeGesture))
            {
                ShowSelection();
            }
        }

        /// <summary>
        /// The volume to show for the current selection: the loudest selected note, so a mixed
        /// phrase reports a value that exists in it. False when nothing selected is a note, which
        /// is also what greys the field out.
        /// </summary>
        private bool TryGetSelectionVolume(out byte volume)
        {
            volume = 0;
            if (_document == null)
            {
                return false;
            }

            bool found = false;
            foreach (EventData item in _document.Selection.Items)
            {
                if (item == null || item.EventType != EventType.Note)
                {
                    continue;
                }
                if (!found || item.Vel > volume)
                {
                    volume = item.Vel;
                }
                found = true;
            }
            return found;
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

        /// <summary>
        /// A field-group divider. Grey rather than accent-coloured on purpose: three accent hues
        /// competing for attention in one panel is what made the shell read as a dashboard mock-up.
        /// Colour is reserved for things that change - selection, timing, faults.
        /// </summary>
        private static void AddSection(TableLayoutPanel table, string text, int row)
        {
            var label = new Label
            {
                Dock = DockStyle.Fill,
                Font = StudioDesignSystem.BodyFont(8.5f, FontStyle.Bold),
                ForeColor = StudioDesignSystem.Muted,
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
