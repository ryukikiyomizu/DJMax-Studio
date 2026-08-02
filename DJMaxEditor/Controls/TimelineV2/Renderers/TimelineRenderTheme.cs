using System.Drawing;
using DJMaxEditor.UI;

namespace DJMaxEditor.Controls.TimelineV2.Renderers
{
    public static class TimelineRenderTheme
    {
        public static readonly Color Canvas = StudioDesignSystem.Void;
        public static readonly Color CanvasAlternate = StudioDesignSystem.Deck;
        public static readonly Color Header = StudioDesignSystem.Lift;
        public static readonly Color HeaderBorder = StudioDesignSystem.Border;
        public static readonly Color Ruler = StudioDesignSystem.Void;
        public static readonly Color GridMinor =
            Color.FromArgb(112, StudioDesignSystem.Border);
        public static readonly Color GridMajor =
            Color.FromArgb(176, StudioDesignSystem.Muted);
        public static readonly Color Text = StudioDesignSystem.Frost;
        public static readonly Color MutedText = StudioDesignSystem.Muted;
        public static readonly Color Note = StudioDesignSystem.PulseCyan;
        public static readonly Color Automation = StudioDesignSystem.AutomationGreen;
        public static readonly Color Warning = StudioDesignSystem.SignalAmber;
        public static readonly Color Error = StudioDesignSystem.FaultRed;
        public static readonly Color Playhead = StudioDesignSystem.PulseCyan;
        public static readonly Color ReadOnly = StudioDesignSystem.Disabled;
        public static readonly Color Minimap = StudioDesignSystem.Void;
        public static readonly Color MinimapDensity = StudioDesignSystem.Selected;
        public static readonly Color MinimapViewport = StudioDesignSystem.Frost;
    }
}
