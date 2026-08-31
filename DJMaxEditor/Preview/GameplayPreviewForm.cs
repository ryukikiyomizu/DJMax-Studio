using System;
using System.Drawing;
using System.Windows.Forms;
using DJMaxEditor.Editor;
using DJMaxEditor.UI;

namespace DJMaxEditor.Preview
{
    /// <summary>
    /// Dockable, read-only visualization of the active document. Profile choices
    /// are session-only and perform no settings or layout file I/O.
    /// </summary>
    public sealed class GameplayPreviewForm : ToolWindow
    {
        private readonly GameplayPreviewControl _preview;
        private readonly Label _status;
        private readonly Button _generic;
        private readonly Button _technika;
        private readonly TrackBar _zoom;
        private readonly TrackBar _speed;
        private readonly Label _speedValue;
        private readonly ComboBox _gearSkin;
        private readonly ComboBox _noteSkin;
        // Native gameplay animation rate (Hz). NOT a render cap: playback is
        // uncapped and draws every update the UI can consume. WinForms coalesces
        // rapid Invalidate() calls into a single WM_PAINT.
        public static int PlaybackFramesPerSecond { get { return 60; } }

        public GameplayPreviewForm()
        {
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = StudioDesignSystem.Void;
            ClientSize = new Size(720, 520);
            MinimumSize = new Size(360, 260);
            ShowHint = WeifenLuo.WinFormsUI.Docking.DockState.DockRight;
            TabText = "Gameplay Preview";
            Text = "Gameplay Preview";

            var header = new Panel
            {
                BackColor = StudioDesignSystem.Deck,
                Dock = DockStyle.Top,
                Height = 154,
                Padding = new Padding(12, 8, 12, 8)
            };
            var title = new Label
            {
                AutoSize = true,
                Font = StudioDesignSystem.DisplayFont(10f),
                ForeColor = StudioDesignSystem.Frost,
                Location = new Point(12, 8),
                Text = "Playback preview"
            };
            _status = new Label
            {
                AutoEllipsis = true,
                Font = StudioDesignSystem.UtilityFont(7.5f),
                ForeColor = StudioDesignSystem.Muted,
                Location = new Point(12, 30),
                Size = new Size(660, 24),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Text = "No document"
            };
            _generic = BuildProfileButton("Vertical", 12);
            _technika = BuildProfileButton("Technika", 106);
            _zoom = new TrackBar
            {
                AutoSize = false,
                BackColor = StudioDesignSystem.Deck,
                LargeChange = 2,
                Location = new Point(206, 76),
                Maximum = 250,
                Minimum = 75,
                SmallChange = 5,
                Size = new Size(142, 26),
                TickStyle = TickStyle.None,
                Value = 100
            };
            var zoomLabel = new Label
            {
                AutoSize = true,
                Font = StudioDesignSystem.UtilityFont(7f),
                ForeColor = StudioDesignSystem.Muted,
                Location = new Point(206, 59),
                Text = "Note size"
            };
            _speedValue = new Label
            {
                AutoSize = true,
                Font = StudioDesignSystem.UtilityFont(7f),
                ForeColor = StudioDesignSystem.Muted,
                Location = new Point(372, 59),
                Text = "Speed 4.5"
            };
            _speed = new TrackBar
            {
                AutoSize = false,
                BackColor = StudioDesignSystem.Deck,
                LargeChange = 10,
                Location = new Point(372, 76),
                Maximum = 200,
                Minimum = 10,
                SmallChange = 5,
                Size = new Size(142, 26),
                TickStyle = TickStyle.None,
                Value = 45
            };
            var gearLabel = BuildSkinLabel("Gear");
            var noteLabel = BuildSkinLabel("Notes");
            _gearSkin = BuildSkinSelector();
            _noteSkin = BuildSkinSelector();
            LayoutSkinSelectors(header, gearLabel, _gearSkin,
                noteLabel, _noteSkin);
            header.Resize += delegate
            {
                LayoutSkinSelectors(header, gearLabel, _gearSkin,
                    noteLabel, _noteSkin);
            };
            _generic.Click += delegate { SetProfile(GameplayPreviewProfile.Generic); };
            _technika.Click += delegate { SetProfile(GameplayPreviewProfile.Technika); };
            _zoom.ValueChanged += delegate
            {
                _preview.NoteZoom = _zoom.Value / 100f;
            };
            _speed.ValueChanged += delegate
            {
                _preview.NoteSpeed = _speed.Value / 10f;
                _speedValue.Text = "Speed " +
                    (_speed.Value / 10f).ToString("0.0");
            };
            _gearSkin.SelectedIndexChanged += delegate
            {
                _preview.SelectGearSkin(_gearSkin.SelectedIndex);
                UpdateStatus();
            };
            _noteSkin.SelectedIndexChanged += delegate
            {
                _preview.SelectNoteSkin(_noteSkin.SelectedIndex);
                UpdateStatus();
            };
            header.Controls.Add(title);
            header.Controls.Add(_status);
            header.Controls.Add(_generic);
            header.Controls.Add(_technika);
            header.Controls.Add(zoomLabel);
            header.Controls.Add(_zoom);
            header.Controls.Add(_speedValue);
            header.Controls.Add(_speed);
            header.Controls.Add(gearLabel);
            header.Controls.Add(_gearSkin);
            header.Controls.Add(noteLabel);
            header.Controls.Add(_noteSkin);

            _preview = new GameplayPreviewControl();
            PopulateSkinSelectors();
            Controls.Add(_preview);
            Controls.Add(header);
            SetProfile(GameplayPreviewProfile.Generic);
        }

        public EditorDocumentContext Document
        {
            get { return _preview.Document; }
        }

        public bool SupportsEditing
        {
            get { return false; }
        }

        public GameplayPreviewProfile Profile
        {
            get { return _preview.Profile; }
        }

        public void Bind(EditorDocumentContext document)
        {
            _preview.Bind(document);
            if (document == null)
            {
                SetProfile(GameplayPreviewProfile.Generic);
            }
            else
            {
                GameplayPreviewProfileSuggestion suggestion =
                    GameplayPreviewProfileResolver.Suggest(document.Model);
                SetProfile(suggestion.RequiresConfirmation
                    ? GameplayPreviewProfile.Generic
                    : suggestion.Profile);
                _status.Text = suggestion.RequiresConfirmation
                    ? "PTFF IS AMBIGUOUS  |  CHOOSE TECHNIKA TO CONFIRM"
                    : suggestion.Explanation.ToUpperInvariant();
            }
            UpdateStatus();
        }

        public void ConfirmTechnikaProfile()
        {
            SetProfile(GameplayPreviewProfile.Technika);
        }

        public void UseGenericProfile()
        {
            SetProfile(GameplayPreviewProfile.Generic);
        }

        public void RefreshPlayback()
        {
            if (!IsPlaybackVisible())
            {
                return;
            }

            // Uncapped: draw every playback update the UI can consume. WinForms
            // coalesces rapid Invalidate() calls into a single WM_PAINT, so there
            // is no artificial frame-rate gate here.
            //
            // What is gated is the work: the preview only rebuilds its frame when an input
            // changed, and the status line is only reassigned when its text changed. Both
            // used to run unconditionally off a 16ms timer that ticks whether or not playback
            // is running, and a Label.Text assignment is a full invalidate of its own.
            int before = _preview.PlaybackFrameRebuildCount;
            _preview.RefreshPlayback();
            if (_preview.PlaybackFrameRebuildCount != before)
            {
                UpdateStatus();
            }
        }

        public void RefreshPlaybackImmediately()
        {
            if (!IsPlaybackVisible())
            {
                return;
            }

            _preview.RefreshPlayback();
            UpdateStatus();
        }

        // Uncapped render policy: every playback frame the UI can consume is drawn.
        // Retained as a pure, testable seam; always true so no frame is dropped.
        public static bool ShouldRenderPlaybackFrame(
            double previousMilliseconds,
            double currentMilliseconds)
        {
            return true;
        }

        public void RefreshTopology()
        {
            _preview.RefreshTopology();
            UpdateStatus();
        }

        private void SetProfile(GameplayPreviewProfile profile)
        {
            if (profile == GameplayPreviewProfile.Technika &&
                _preview.Document != null &&
                GameplayPreviewProfileResolver.Suggest(_preview.Document.Model).Profile !=
                    GameplayPreviewProfile.Technika)
            {
                profile = GameplayPreviewProfile.Generic;
            }
            _preview.SetProfile(profile);
            StyleProfileButton(_generic, profile == GameplayPreviewProfile.Generic);
            StyleProfileButton(_technika, profile == GameplayPreviewProfile.Technika);
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            if (_preview.Document == null)
            {
                _status.Text = "No document";
                return;
            }
            _status.Text = _preview.ProjectionStatus +
                (_preview.Profile == GameplayPreviewProfile.Generic
                    ? "   Skin: " + _preview.RespectAssetStatus
                    : string.Empty) +
                (_preview.DiagnosticCount == 0
                    ? string.Empty
                    : "   " + _preview.DiagnosticCount + " warning" +
                        (_preview.DiagnosticCount == 1 ? string.Empty : "s"));
            _status.ForeColor = _preview.DiagnosticCount == 0
                ? StudioDesignSystem.Muted
                : StudioDesignSystem.SignalAmber;
        }

        private bool IsPlaybackVisible()
        {
            return !IsDisposed && Visible && _preview.Visible;
        }

        private static Button BuildProfileButton(string text, int left)
        {
            Button button = StudioDesignSystem.CreateDeckButton(text);
            button.Location = new Point(left, 72);
            button.Size = new Size(text == "Vertical" ? 88 : 82, 30);
            return button;
        }

        private static void StyleProfileButton(Button button, bool selected)
        {
            button.BackColor = selected
                ? StudioDesignSystem.Selected
                : StudioDesignSystem.Lift;
            button.ForeColor = selected
                ? StudioDesignSystem.PulseCyan
                : StudioDesignSystem.Muted;
            button.FlatAppearance.BorderColor = selected
                ? StudioDesignSystem.PulseCyan
                : StudioDesignSystem.Border;
        }

        private void PopulateSkinSelectors()
        {
            foreach (string name in _preview.GearSkinNames)
            {
                _gearSkin.Items.Add(name);
            }
            foreach (string name in _preview.NoteSkinNames)
            {
                _noteSkin.Items.Add(name);
            }
            if (_gearSkin.Items.Count > 0) _gearSkin.SelectedIndex = 0;
            if (_noteSkin.Items.Count > 0) _noteSkin.SelectedIndex = 0;
        }

        private static Label BuildSkinLabel(string text)
        {
            return new Label
            {
                AutoSize = false,
                Font = StudioDesignSystem.UtilityFont(7f),
                ForeColor = StudioDesignSystem.Muted,
                Size = new Size(44, 24),
                Text = text,
                TextAlign = ContentAlignment.MiddleLeft
            };
        }

        private static ComboBox BuildSkinSelector()
        {
            return new ComboBox
            {
                BackColor = StudioDesignSystem.Lift,
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                Font = StudioDesignSystem.UtilityFont(8f),
                ForeColor = StudioDesignSystem.Frost,
                IntegralHeight = false,
                MaxDropDownItems = 12,
                Size = new Size(220, 24)
            };
        }

        private static void LayoutSkinSelectors(
            Control header,
            Control gearLabel,
            Control gearSelector,
            Control noteLabel,
            Control noteSelector)
        {
            int gap = 12;
            int labelWidth = 44;
            int half = Math.Max(130, (header.ClientSize.Width - (gap * 3)) / 2);
            int selectorWidth = Math.Max(76, half - labelWidth);
            int y = 116;

            gearLabel.Location = new Point(gap, y);
            gearSelector.Location = new Point(gap + labelWidth, y);
            gearSelector.Width = selectorWidth;
            int second = gap + half + gap;
            noteLabel.Location = new Point(second, y);
            noteSelector.Location = new Point(second + labelWidth, y);
            noteSelector.Width = selectorWidth;
        }
    }
}
