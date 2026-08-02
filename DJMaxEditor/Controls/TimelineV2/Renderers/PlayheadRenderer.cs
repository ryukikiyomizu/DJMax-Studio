using System.Drawing;

namespace DJMaxEditor.Controls.TimelineV2.Renderers
{
    public sealed class PlayheadRenderer
    {
        public void Render(Graphics graphics, TimelineFrame frame)
        {
            int x = (int)frame.Viewport.ScreenXAtTick(frame.PlayheadTick);
            using (var pen = new Pen(TimelineRenderTheme.Playhead))
            using (var brush = new SolidBrush(TimelineRenderTheme.Playhead))
            {
                graphics.DrawLine(pen, x, 0, x, frame.CanvasBottom);
                graphics.FillPolygon(brush, new[]
                {
                    new Point(x - 5, 0),
                    new Point(x + 5, 0),
                    new Point(x, 7)
                });
            }
        }
    }
}
