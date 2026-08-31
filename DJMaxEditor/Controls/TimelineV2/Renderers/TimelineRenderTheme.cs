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

        /// <summary>
        /// Border drawn around every note body so a dense run reads as separate notes instead
        /// of one solid bar. Darkened rather than a fixed grey so it keeps working if the note
        /// colours change, and opaque so it survives on top of the alternating row fills.
        /// </summary>
        public static readonly Color NoteOutline = Darken(Note);
        public static readonly Color AutomationOutline = Darken(Automation);

        /// <summary>
        /// Selected note body and its border. V2 had no selection colour at all while it was
        /// read-only; now that it edits, "which notes will this drag move?" has to be answerable
        /// at a glance.
        /// </summary>
        public static readonly Color Selection = StudioDesignSystem.Frost;
        public static readonly Color SelectionOutline = StudioDesignSystem.BeatViolet;

        /// <summary>Marquee band drawn while a rubber-band selection is in progress.</summary>
        public static readonly Color MarqueeFill = Color.FromArgb(48, StudioDesignSystem.PulseCyan);
        public static readonly Color MarqueeBorder = StudioDesignSystem.PulseCyan;

        /// <summary>
        /// Body colour of a note at zero velocity. Note fills are interpolated between this and
        /// <see cref="Note"/> across the velocity range so per-note volume is visible on the
        /// chart instead of only in the Inspector.
        /// </summary>
        public static readonly Color SilentNote = Blend(Note, StudioDesignSystem.Lift, 0.78f);
        public static readonly Color SilentAutomation =
            Blend(Automation, StudioDesignSystem.Lift, 0.78f);

        public static readonly Color Warning = StudioDesignSystem.SignalAmber;
        public static readonly Color Error = StudioDesignSystem.FaultRed;
        public static readonly Color Playhead = StudioDesignSystem.PulseCyan;
        public static readonly Color ReadOnly = StudioDesignSystem.Disabled;
        public static readonly Color Minimap = StudioDesignSystem.Void;
        public static readonly Color MinimapDensity = StudioDesignSystem.Selected;
        public static readonly Color MinimapViewport = StudioDesignSystem.Frost;

        private static Color Darken(Color color)
        {
            return Color.FromArgb(
                255,
                (int)(color.R * 0.42f),
                (int)(color.G * 0.42f),
                (int)(color.B * 0.42f));
        }

        /// <summary>Linear mix of two opaque colours; <paramref name="amount"/> 0 keeps <paramref name="from"/>.</summary>
        internal static Color Blend(Color from, Color to, float amount)
        {
            float clamped = amount < 0f ? 0f : (amount > 1f ? 1f : amount);
            return Color.FromArgb(
                255,
                (int)(from.R + ((to.R - from.R) * clamped)),
                (int)(from.G + ((to.G - from.G) * clamped)),
                (int)(from.B + ((to.B - from.B) * clamped)));
        }
    }
}
