using System;
using System.Collections.Generic;
using System.Drawing;

namespace DJMaxEditor.Controls.TimelineV2.Renderers
{
    public sealed class GridRenderer
    {
        public void Render(Graphics graphics, TimelineFrame frame, IReadOnlyList<TimelineRulerMark> marks)
        {
            for (int rowIndex = frame.FirstVisibleRow; rowIndex < frame.Rows.Count; rowIndex++)
            {
                int y = frame.Coordinates.RowToY(rowIndex, frame.FirstVisibleRow);
                if (y >= frame.CanvasBottom) break;

                using (var brush = new SolidBrush(
                    rowIndex % 2 == 0 ? TimelineRenderTheme.Canvas : TimelineRenderTheme.CanvasAlternate))
                {
                    graphics.FillRectangle(
                        brush,
                        frame.Coordinates.HeaderWidth,
                        y,
                        frame.Width - frame.Coordinates.HeaderWidth,
                        frame.Coordinates.RowHeight);
                }
                using (var line = new Pen(TimelineRenderTheme.GridMinor))
                {
                    graphics.DrawLine(line, 0, y + frame.Coordinates.RowHeight - 1,
                        frame.Width, y + frame.Coordinates.RowHeight - 1);
                }
            }

            RenderQuantizeLines(graphics, frame);

            foreach (TimelineRulerMark mark in marks)
            {
                int x = (int)frame.Viewport.ScreenXAtTick(mark.Tick);
                Color color = mark.Kind == TimelineRulerMarkKind.Measure
                    ? TimelineRenderTheme.GridMajor
                    : TimelineRenderTheme.GridMinor;
                using (var pen = new Pen(color))
                {
                    graphics.DrawLine(pen, x, frame.Coordinates.RulerHeight, x, frame.CanvasBottom);
                }
            }
        }

        private static void RenderQuantizeLines(Graphics graphics, TimelineFrame frame)
        {
            if (frame.TicksPerMeasure <= 0 || frame.QuantizeDivision <= 0)
            {
                return;
            }

            double interval = (double)frame.TicksPerMeasure / frame.QuantizeDivision;
            if (interval <= 0 || interval * frame.Viewport.PixelsPerTick < 4)
            {
                return;
            }

            double firstTick = Math.Floor(
                frame.Viewport.VisibleTimeRange.StartTick / interval) * interval;
            using (var pen = new Pen(Color.FromArgb(42, TimelineRenderTheme.GridMinor)))
            {
                for (double tick = firstTick;
                    tick <= frame.Viewport.VisibleTimeRange.EndTick;
                    tick += interval)
                {
                    int x = (int)frame.Viewport.ScreenXAtTick(tick);
                    graphics.DrawLine(
                        pen,
                        x,
                        frame.Coordinates.RulerHeight,
                        x,
                        frame.CanvasBottom);
                }
            }
        }
    }
}
