using System;
using System.Drawing;
using System.Windows.Forms;

namespace DJMaxEditor.UI
{
    /// <summary>
    /// Centralized production UI tokens.  Controls consume semantic tokens from
    /// here so the shell, docked panels, and both timelines share one language.
    /// </summary>
    /// <remarks>
    /// The surface ramp (<see cref="Void"/> through <see cref="Border"/>) is deliberately
    /// <em>neutral</em> grey. It used to be navy-tinted, which made the alternating row fills on
    /// both timelines read as a blue wash behind the notes and gave the whole shell the
    /// backlit-dashboard look. Colour is now reserved for signals — timing, selection,
    /// automation, warning, fault — so a coloured pixel always means something.
    /// </remarks>
    public static class StudioDesignSystem
    {
        public static readonly Color Void = Color.FromArgb(0x0F, 0x0F, 0x10);
        public static readonly Color Deck = Color.FromArgb(0x15, 0x15, 0x17);
        public static readonly Color Lift = Color.FromArgb(0x1E, 0x1E, 0x21);
        public static readonly Color Hover = Color.FromArgb(0x2A, 0x2A, 0x2E);
        public static readonly Color Border = Color.FromArgb(0x3A, 0x3A, 0x3F);
        public static readonly Color PulseCyan = Color.FromArgb(0x36, 0xD5, 0xFF);
        public static readonly Color BeatViolet = Color.FromArgb(0xA7, 0x7B, 0xFF);
        public static readonly Color AutomationGreen = Color.FromArgb(0x53, 0xD7, 0xA0);
        public static readonly Color SignalAmber = Color.FromArgb(0xFF, 0xCB, 0x5C);
        public static readonly Color FaultRed = Color.FromArgb(0xFF, 0x5F, 0x73);
        public static readonly Color Frost = Color.FromArgb(0xE9, 0xEA, 0xEC);
        public static readonly Color Muted = Color.FromArgb(0x99, 0x9A, 0x9F);

        /// <summary>
        /// Selection stays tinted on purpose: it is a signal, not chrome, so it has to read as
        /// distinct from every neutral surface behind it. Derived from <see cref="PulseCyan"/>.
        /// </summary>
        public static readonly Color Selected = Color.FromArgb(0x1C, 0x3C, 0x49);
        public static readonly Color Disabled = Color.FromArgb(0x6A, 0x6A, 0x70);

        public const int BaseDpi = 96;

        public static int Scale(int logicalPixels, int dpi)
        {
            if (logicalPixels <= 0)
            {
                return logicalPixels;
            }

            int effectiveDpi = dpi <= 0 ? BaseDpi : dpi;
            return Math.Max(1, (logicalPixels * effectiveDpi + (BaseDpi / 2)) / BaseDpi);
        }

        public static Padding Scale(Padding logicalPadding, int dpi)
        {
            return new Padding(
                Scale(logicalPadding.Left, dpi),
                Scale(logicalPadding.Top, dpi),
                Scale(logicalPadding.Right, dpi),
                Scale(logicalPadding.Bottom, dpi));
        }

        public static Font DisplayFont(float size, FontStyle style = FontStyle.Bold)
        {
            return CreateFallbackFont(
                new[] { "Bahnschrift SemiCondensed", "Bahnschrift", "Segoe UI Semibold" },
                size,
                style);
        }

        public static Font BodyFont(float size = 9f, FontStyle style = FontStyle.Regular)
        {
            return CreateFallbackFont(
                new[] { "Segoe UI Variable Text", "Segoe UI" },
                size,
                style);
        }

        public static Font UtilityFont(float size = 9f)
        {
            return CreateFallbackFont(new[] { "Consolas", "Courier New" }, size, FontStyle.Regular);
        }

        public static Button CreateDeckButton(string text)
        {
            var button = new Button
            {
                AutoSize = false,
                BackColor = Lift,
                FlatStyle = FlatStyle.Flat,
                Font = BodyFont(8.5f, FontStyle.Bold),
                ForeColor = Frost,
                Height = 28,
                Margin = new Padding(3, 4, 3, 4),
                Padding = new Padding(8, 0, 8, 0),
                Text = text,
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderColor = Border;
            button.FlatAppearance.MouseOverBackColor = Hover;
            button.FlatAppearance.MouseDownBackColor = Selected;
            return button;
        }

        private static Font CreateFallbackFont(
            string[] familyNames,
            float size,
            FontStyle style)
        {
            foreach (string familyName in familyNames)
            {
                try
                {
                    using (var family = new FontFamily(familyName))
                    {
                        if (family.IsStyleAvailable(style))
                        {
                            return new Font(familyName, size, style, GraphicsUnit.Point);
                        }
                    }
                }
                catch (ArgumentException)
                {
                }
            }

            return new Font(SystemFonts.MessageBoxFont.FontFamily, size, style, GraphicsUnit.Point);
        }
    }
}
