using System;
using System.Drawing;
using System.Windows.Forms;

namespace DJMaxEditor.UI
{
    public sealed class EmptyWorkspaceControl : UserControl
    {
        public EmptyWorkspaceControl()
        {
            BackColor = StudioDesignSystem.Void;
            Dock = DockStyle.Fill;

            var card = new Panel
            {
                Anchor = AnchorStyles.None,
                BackColor = StudioDesignSystem.Deck,
                Size = new Size(560, 260)
            };
            card.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var border = new Pen(StudioDesignSystem.Border))
                using (var pulse = new Pen(StudioDesignSystem.PulseCyan, 2f))
                {
                    e.Graphics.DrawRectangle(border, 0, 0, card.Width - 1, card.Height - 1);
                    e.Graphics.DrawLine(pulse, 0, 0, card.Width, 0);
                }
            };

            var eyebrow = CreateLabel(
                "Authoring workspace",
                new Point(38, 32),
                new Size(480, 24),
                StudioDesignSystem.Muted,
                StudioDesignSystem.BodyFont(9f));
            var title = CreateLabel(
                "Open a chart to start editing.",
                new Point(38, 64),
                new Size(480, 42),
                StudioDesignSystem.Frost,
                StudioDesignSystem.DisplayFont(18f));
            var detail = CreateLabel(
                "Open PT, Respect V, XML, BMS, BME, BML, or PMS. Detection and safety rules stay active.",
                new Point(38, 112),
                new Size(474, 42),
                StudioDesignSystem.Muted,
                StudioDesignSystem.BodyFont(9f));

            Button open = StudioDesignSystem.CreateDeckButton("Open chart   Ctrl+O");
            open.Location = new Point(38, 178);
            open.Size = new Size(210, 38);
            open.FlatAppearance.BorderColor = StudioDesignSystem.PulseCyan;
            open.ForeColor = StudioDesignSystem.PulseCyan;
            open.Click += delegate { if (OpenRequested != null) OpenRequested(this, EventArgs.Empty); };

            Button import = StudioDesignSystem.CreateDeckButton("Import BMS");
            import.Location = new Point(258, 178);
            import.Size = new Size(130, 38);
            import.Click += delegate { if (OpenRequested != null) OpenRequested(this, EventArgs.Empty); };

            // A sentence about what the editor will and will not do to your files, not an amber
            // "OFFLINE • CAPABILITY-AWARE • NON-DESTRUCTIVE" badge strip. Amber is the lock cue
            // everywhere else in the shell, so spending it on a slogan on the start screen taught
            // the wrong colour association before the user had even opened anything.
            var safety = CreateLabel(
                "Works offline. Charts open read-only unless the format supports saving.",
                new Point(38, 226),
                new Size(474, 20),
                StudioDesignSystem.Muted,
                StudioDesignSystem.BodyFont(8.5f));

            card.Controls.Add(eyebrow);
            card.Controls.Add(title);
            card.Controls.Add(detail);
            card.Controls.Add(open);
            card.Controls.Add(import);
            card.Controls.Add(safety);
            Controls.Add(card);

            Resize += delegate
            {
                card.Left = System.Math.Max(16, (ClientSize.Width - card.Width) / 2);
                card.Top = System.Math.Max(16, (ClientSize.Height - card.Height) / 2);
            };
        }

        public event EventHandler OpenRequested;

        private static Label CreateLabel(
            string text,
            Point location,
            Size size,
            Color color,
            Font font)
        {
            return new Label
            {
                AutoEllipsis = true,
                BackColor = StudioDesignSystem.Deck,
                Font = font,
                ForeColor = color,
                Location = location,
                Size = size,
                Text = text
            };
        }
    }
}
