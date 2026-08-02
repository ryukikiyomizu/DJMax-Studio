using System;
using System.Diagnostics;
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
        private const long PlaybackFrameIntervalMilliseconds = 33;
        private readonly Stopwatch _playbackClock = Stopwatch.StartNew();
        private long _lastPlaybackFrameMilliseconds = -PlaybackFrameIntervalMilliseconds;

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
                Height = 104,
                Padding = new Padding(12, 8, 12, 8)
            };
            var title = new Label
            {
                AutoSize = true,
                Font = StudioDesignSystem.DisplayFont(10f),
                ForeColor = StudioDesignSystem.Frost,
                Location = new Point(12, 8),
                Text = "PLAYBACK VISUALIZER"
            };
            _status = new Label
            {
                AutoEllipsis = true,
                Font = StudioDesignSystem.UtilityFont(7.5f),
                ForeColor = StudioDesignSystem.Muted,
                Location = new Point(12, 30),
                Size = new Size(660, 24),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Text = "NO DOCUMENT"
            };
            _generic = BuildProfileButton("GENERIC", 12);
            _technika = BuildProfileButton("TECHNIKA", 100);
            _zoom = new TrackBar
            {
                AutoSize = false,
                BackColor = StudioDesignSystem.Deck,
                LargeChange = 2,
                Location = new Point(194, 62),
                Maximum = 250,
                Minimum = 75,
                SmallChange = 5,
                Size = new Size(160, 30),
                TickStyle = TickStyle.None,
                Value = 135
            };
            _generic.Click += delegate { SetProfile(GameplayPreviewProfile.Generic); };
            _technika.Click += delegate { SetProfile(GameplayPreviewProfile.Technika); };
            _zoom.ValueChanged += delegate
            {
                _preview.NoteZoom = _zoom.Value / 100f;
            };
            header.Controls.Add(title);
            header.Controls.Add(_status);
            header.Controls.Add(_generic);
            header.Controls.Add(_technika);
            header.Controls.Add(_zoom);

            _preview = new GameplayPreviewControl();
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

            long elapsed = _playbackClock.ElapsedMilliseconds;
            if (elapsed - _lastPlaybackFrameMilliseconds <
                PlaybackFrameIntervalMilliseconds)
            {
                return;
            }

            _lastPlaybackFrameMilliseconds = elapsed;
            _preview.RefreshPlayback();
            UpdateStatus();
        }

        public void RefreshPlaybackImmediately()
        {
            if (!IsPlaybackVisible())
            {
                return;
            }

            _lastPlaybackFrameMilliseconds = _playbackClock.ElapsedMilliseconds;
            _preview.RefreshPlayback();
            UpdateStatus();
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
                _status.Text = "NO DOCUMENT";
                return;
            }
            _status.Text = _preview.ProjectionStatus +
                (_preview.DiagnosticCount == 0
                    ? string.Empty
                    : "  |  " + _preview.DiagnosticCount + " WARNING(S)");
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
            button.Location = new Point(left, 62);
            button.Size = new Size(82, 30);
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
    }
}
