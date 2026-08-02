using System;
using System.Drawing;
using System.Linq;

namespace DJMaxEditor.Controls.TimelineV2.Renderers
{
    public sealed class MinimapRenderer
    {
        public void Render(Graphics graphics, TimelineFrame frame)
        {
            int top = frame.Height - TimelineFrame.MinimapHeight;
            using (var background = new SolidBrush(TimelineRenderTheme.Minimap))
            {
                graphics.FillRectangle(background, 0, top, frame.Width, TimelineFrame.MinimapHeight);
            }

            if (frame.MinimapDensity.Count > 0)
            {
                int maximum = Math.Max(1, frame.MinimapDensity.Max());
                double bucketWidth = (double)frame.Width / frame.MinimapDensity.Count;
                using (var density = new SolidBrush(TimelineRenderTheme.MinimapDensity))
                {
                    for (int i = 0; i < frame.MinimapDensity.Count; i++)
                    {
                        int height = (int)Math.Round(
                            (TimelineFrame.MinimapHeight - 8) *
                            ((double)frame.MinimapDensity[i] / maximum));
                        graphics.FillRectangle(
                            density,
                            (int)Math.Floor(i * bucketWidth),
                            top + TimelineFrame.MinimapHeight - height - 2,
                            Math.Max(1, (int)Math.Ceiling(bucketWidth)),
                            height);
                    }
                }
            }

            double documentLength = Math.Max(
                1,
                frame.Viewport.DocumentEndTick - frame.Viewport.DocumentStartTick);
            int viewportX = (int)Math.Round(frame.Width *
                ((frame.Viewport.OriginTick - frame.Viewport.DocumentStartTick) / documentLength));
            int viewportWidth = Math.Max(3, (int)Math.Round(frame.Width *
                (frame.Viewport.VisibleTickCount / documentLength)));
            using (var pen = new Pen(TimelineRenderTheme.MinimapViewport))
            {
                graphics.DrawRectangle(
                    pen,
                    viewportX,
                    top + 1,
                    Math.Min(frame.Width - viewportX - 1, viewportWidth),
                    TimelineFrame.MinimapHeight - 3);
            }
        }
    }
}
