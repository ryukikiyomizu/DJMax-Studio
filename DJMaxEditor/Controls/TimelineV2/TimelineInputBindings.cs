using System.Windows.Forms;

namespace DJMaxEditor.Controls.TimelineV2
{
    internal enum TimelineWheelAction
    {
        VerticalScroll,
        HorizontalScroll,
        Zoom
    }

    internal static class TimelineInputBindings
    {
        internal static TimelineWheelAction ResolveWheelAction(Keys modifiers)
        {
            if ((modifiers & Keys.Alt) == Keys.Alt)
            {
                return TimelineWheelAction.Zoom;
            }
            if ((modifiers & Keys.Control) == Keys.Control)
            {
                return TimelineWheelAction.Zoom;
            }
            if ((modifiers & Keys.Shift) == Keys.Shift)
            {
                return TimelineWheelAction.HorizontalScroll;
            }
            return TimelineWheelAction.VerticalScroll;
        }

        internal static bool IsPanGesture(
            MouseButtons button,
            Keys modifiers,
            bool hHeld)
        {
            return button == MouseButtons.Middle ||
                (button == MouseButtons.Left &&
                    (hHeld || (modifiers & Keys.Alt) == Keys.Alt));
        }
    }
}
