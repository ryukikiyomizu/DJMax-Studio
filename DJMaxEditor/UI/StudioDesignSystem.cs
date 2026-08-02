using System;
using System.Drawing;
using System.Windows.Forms;

namespace DJMaxEditor.UI
{
    /// <summary>
    /// Centralized production UI tokens.  Controls consume semantic tokens from
    /// here so the shell, docked panels, and both timelines share one language.
    /// </summary>
    public static class StudioDesignSystem
    {
        public static readonly Color Void = Color.FromArgb(0x0B, 0x0F, 0x17);
        public static readonly Color Deck = Color.FromArgb(0x12, 0x1A, 0x27);
        public static readonly Color Lift = Color.FromArgb(0x1C, 0x29, 0x3A);
        public static readonly Color Hover = Color.FromArgb(0x26, 0x37, 0x4D);
        public static readonly Color Border = Color.FromArgb(0x31, 0x45, 0x5E);
        public static readonly Color PulseCyan = Color.FromArgb(0x36, 0xD5, 0xFF);
        public static readonly Color BeatViolet = Color.FromArgb(0xA7, 0x7B, 0xFF);
        public static readonly Color AutomationGreen = Color.FromArgb(0x53, 0xD7, 0xA0);
        public static readonly Color SignalAmber = Color.FromArgb(0xFF, 0xCB, 0x5C);
        public static readonly Color FaultRed = Color.FromArgb(0xFF, 0x5F, 0x73);
        public static readonly Color Frost = Color.FromArgb(0xEA, 0xF2, 0xFF);
        public static readonly Color Muted = Color.FromArgb(0x91, 0xA2, 0xBA);
        public static readonly Color Selected = Color.FromArgb(0x25, 0x50, 0x73);
        public static readonly Color Disabled = Color.FromArgb(0x5E, 0x6B, 0x7D);

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
