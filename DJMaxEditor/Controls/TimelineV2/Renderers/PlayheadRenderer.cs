using System;
using System.Drawing;

namespace DJMaxEditor.Controls.TimelineV2.Renderers
{
    /// <summary>
    /// The playhead line and its cap triangle.
    /// <para>
    /// This is the one thing redrawn on literally every playback frame, so it owns its pen,
    /// its brush and even the triangle's point array: allocating three objects per frame at
    /// an uncapped frame rate is pure garbage-collector pressure for no visual gain.
    /// </para>
    /// </summary>
    public sealed class PlayheadRenderer : IDisposable
    {
        private readonly Pen _pen = new Pen(TimelineRenderTheme.Playhead);
        private readonly SolidBrush _brush = new SolidBrush(TimelineRenderTheme.Playhead);
        private readonly Point[] _cap = new Point[3];

        public void Render(Graphics graphics, TimelineFrame frame)
        {
            int x = (int)frame.Viewport.ScreenXAtTick(frame.PlayheadTick);
            graphics.DrawLine(_pen, x, 0, x, frame.CanvasBottom);

            _cap[0] = new Point(x - 5, 0);
            _cap[1] = new Point(x + 5, 0);
            _cap[2] = new Point(x, 7);
            graphics.FillPolygon(_brush, _cap);
        }

        public void Dispose()
        {
            _pen.Dispose();
            _brush.Dispose();
        }
    }
}
