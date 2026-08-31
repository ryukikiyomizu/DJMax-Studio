using System;
using System.Drawing;

namespace DJMaxEditor.Controls.TimelineV2.Renderers
{
    /// <summary>
    /// Bottom density strip plus the viewport indicator.
    /// <para>
    /// Owns its brushes and pen, and finds the busiest bucket with a plain loop instead of
    /// a LINQ <c>Max</c>, so a frame costs no allocations at all here.
    /// </para>
    /// </summary>
    public sealed class MinimapRenderer : IDisposable
    {
        private readonly SolidBrush _background = new SolidBrush(TimelineRenderTheme.Minimap);
        private readonly SolidBrush _density = new SolidBrush(TimelineRenderTheme.MinimapDensity);
        private readonly Pen _viewportPen = new Pen(TimelineRenderTheme.MinimapViewport);

        public void Render(Graphics graphics, TimelineFrame frame)
        {
            RenderDensity(graphics, frame);
            RenderViewportIndicator(graphics, frame);
        }

        /// <summary>
        /// Background and density bars — the part of the minimap that depends only on the document,
        /// so it survives in the renderer's chrome cache while the chart scrolls underneath.
        /// </summary>
        public void RenderDensity(Graphics graphics, TimelineFrame frame)
        {
            int top = frame.Height - TimelineFrame.MinimapHeight;
            graphics.FillRectangle(_background, 0, top, frame.Width, TimelineFrame.MinimapHeight);

            if (frame.MinimapDensity.Count > 0)
            {
                int maximum = 1;
                for (int i = 0; i < frame.MinimapDensity.Count; i++)
                {
                    if (frame.MinimapDensity[i] > maximum) maximum = frame.MinimapDensity[i];
                }

                double bucketWidth = (double)frame.Width / frame.MinimapDensity.Count;
                int barWidth = Math.Max(1, (int)Math.Ceiling(bucketWidth));
                for (int i = 0; i < frame.MinimapDensity.Count; i++)
                {
                    int height = (int)Math.Round(
                        (TimelineFrame.MinimapHeight - 8) *
                        ((double)frame.MinimapDensity[i] / maximum));
                    graphics.FillRectangle(
                        _density,
                        (int)Math.Floor(i * bucketWidth),
                        top + TimelineFrame.MinimapHeight - height - 2,
                        barWidth,
                        height);
                }
            }
        }

        /// <summary>
        /// The "you are here" box. Tracks the viewport origin, so unlike the density bars it has to
        /// be drawn live on every frame.
        /// </summary>
        public void RenderViewportIndicator(Graphics graphics, TimelineFrame frame)
        {
            int top = frame.Height - TimelineFrame.MinimapHeight;
            double documentLength = Math.Max(
                1,
                frame.Viewport.DocumentEndTick - frame.Viewport.DocumentStartTick);
            int viewportX = (int)Math.Round(frame.Width *
                ((frame.Viewport.OriginTick - frame.Viewport.DocumentStartTick) / documentLength));
            viewportX = Math.Max(0, Math.Min(frame.Width - 4, viewportX));
            int viewportWidth = Math.Max(3, (int)Math.Round(frame.Width *
                (frame.Viewport.VisibleTickCount / documentLength)));
            graphics.DrawRectangle(
                _viewportPen,
                viewportX,
                top + 1,
                Math.Min(frame.Width - viewportX - 1, viewportWidth),
                TimelineFrame.MinimapHeight - 3);
        }

        public void Dispose()
        {
            _background.Dispose();
            _density.Dispose();
            _viewportPen.Dispose();
        }
    }
}
