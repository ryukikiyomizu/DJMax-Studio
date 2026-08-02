using System.Drawing;

namespace DJMaxEditor.Controls.TimelineV2.Renderers
{
    public sealed class FallbackArtRenderer
    {
        public void Render(Graphics graphics, TimelineFrame frame, TimelineItem item)
        {
            int x = (int)frame.Viewport.ScreenXAtTick(item.StartTick);
            int y = frame.Coordinates.RowToY(item.RowIndex, frame.FirstVisibleRow);
            int centerY = y + (frame.Coordinates.RowHeight / 2);
            using (var pen = new Pen(TimelineRenderTheme.Warning, 2))
            {
                graphics.DrawLine(pen, x, y + 5, x, y + frame.Coordinates.RowHeight - 5);
                graphics.DrawLine(pen, x - 4, centerY, x + 4, centerY);
            }
        }
    }
}
