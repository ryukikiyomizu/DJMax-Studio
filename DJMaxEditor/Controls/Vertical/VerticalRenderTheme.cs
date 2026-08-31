using System.Drawing;
using DJMaxEditor.UI;

namespace DJMaxEditor.Controls.Vertical
{
    /// <summary>
    /// Palette for the vertical ptSequencer-style surface. Column tints follow the
    /// ptSequencer "type" values so a chart looks the same here as it does in DPC.
    /// </summary>
    public static class VerticalRenderTheme
    {
        public static readonly Color Canvas = StudioDesignSystem.Void;
        public static readonly Color Gutter = StudioDesignSystem.Lift;
        public static readonly Color GutterBorder = StudioDesignSystem.Border;
        public static readonly Color NameStrip = StudioDesignSystem.Lift;
        public static readonly Color Text = StudioDesignSystem.Frost;
        public static readonly Color MutedText = StudioDesignSystem.Muted;
        public static readonly Color ColumnBorder = Color.FromArgb(96, StudioDesignSystem.Border);
        public static readonly Color GridMinor = Color.FromArgb(70, StudioDesignSystem.Border);
        public static readonly Color GridMajor = Color.FromArgb(150, StudioDesignSystem.Muted);
        public static readonly Color Playhead = StudioDesignSystem.PulseCyan;
        public static readonly Color SelectionFill = StudioDesignSystem.Frost;
        public static readonly Color SelectionOutline = StudioDesignSystem.PulseCyan;

        // Column backgrounds by role. The plain button lanes are neutral so a dense chart reads
        // as notes on grey; only the lanes that mean something in ptOne/DPC keep a hue.
        public static readonly Color Unused = Color.FromArgb(0x09, 0x09, 0x0A);
        public static readonly Color ButtonPrimary = StudioDesignSystem.Deck;
        public static readonly Color ButtonAlternate = Color.FromArgb(0x1A, 0x1A, 0x1D);
        public static readonly Color Side = Color.FromArgb(0x19, 0x1C, 0x2E);
        public static readonly Color Shoulder = Color.FromArgb(0x20, 0x1A, 0x2E);
        public static readonly Color BgaSync = Color.FromArgb(0x1E, 0x1B, 0x14);
        public static readonly Color Mr = Color.FromArgb(0x10, 0x1F, 0x1A);
        public static readonly Color Background = Color.FromArgb(0x12, 0x12, 0x14);

        /// <summary>
        /// TECHNIKA end-of-scan marker columns. Darker and cooler than a lane so the four
        /// markers read as annotation beside the four lanes rather than as four more lanes -
        /// which is what they looked like while they fell through to the button fill.
        /// </summary>
        public static readonly Color ScanMarker = Color.FromArgb(0x14, 0x17, 0x22);

        // Note fills by event role.
        public static readonly Color Note = StudioDesignSystem.PulseCyan;
        public static readonly Color LongNote = Color.FromArgb(0x1F, 0xA8, 0xCC);
        public static readonly Color Sample = StudioDesignSystem.AutomationGreen;
        public static readonly Color Tempo = StudioDesignSystem.SignalAmber;
        public static readonly Color Unknown = StudioDesignSystem.FaultRed;

        /// <summary>
        /// Number of distinct velocity brightness steps, and the velocity that means "full".
        /// Matches Timeline V2's ramp exactly so the same note is the same shade on both
        /// surfaces - the strip is a preview of the chart, so a note that looks quiet here has
        /// to look quiet there.
        /// </summary>
        private const int VelocitySteps = 8;

        private const byte MaxVelocity = 127;

        /// <summary>
        /// Body colour of a note at zero velocity. Fills are interpolated between this and the
        /// role colour across the velocity range, so per-note volume is visible on the strip
        /// instead of only in the Inspector.
        /// </summary>
        public static Color Silent(Color roleColor)
        {
            return Blend(roleColor, StudioDesignSystem.Lift, 0.78f);
        }

        /// <summary>
        /// Shades a note's role colour by its velocity. Full velocity returns the role colour
        /// untouched, so a chart that never used note volume looks exactly as it did before.
        /// </summary>
        public static Color ShadeByVelocity(Color roleColor, byte velocity)
        {
            if (velocity >= MaxVelocity)
            {
                return roleColor;
            }

            int step = (velocity * VelocitySteps) / (MaxVelocity + 1);
            float amount = 1f - (step / (float)(VelocitySteps - 1));
            return Blend(roleColor, Silent(roleColor), amount);
        }

        internal static Color Blend(Color from, Color to, float amount)
        {
            if (amount <= 0f) return from;
            if (amount >= 1f) return to;
            return Color.FromArgb(
                from.A,
                (int)(from.R + ((to.R - from.R) * amount)),
                (int)(from.G + ((to.G - from.G) * amount)),
                (int)(from.B + ((to.B - from.B) * amount)));
        }

        public static Color ForColumn(VerticalColumn column)
        {
            switch (column.Kind)
            {
                case VerticalColumnKind.LeadingUnused:
                    return Unused;
                case VerticalColumnKind.SideLeft:
                case VerticalColumnKind.SideRight:
                    return Side;
                case VerticalColumnKind.ShoulderLeft:
                case VerticalColumnKind.ShoulderRight:
                    return Shoulder;
                case VerticalColumnKind.BgaSync:
                    return BgaSync;
                case VerticalColumnKind.Mr:
                    return Mr;
                case VerticalColumnKind.Background:
                    return Background;
                case VerticalColumnKind.ScanMarker:
                    return ScanMarker;
                default:
                    return column.Style == VerticalColumnStyle.RegularAlternate
                        ? ButtonAlternate
                        : ButtonPrimary;
            }
        }
    }
}
