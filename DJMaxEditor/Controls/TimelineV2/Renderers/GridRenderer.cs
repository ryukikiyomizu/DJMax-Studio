using System;
using System.Collections.Generic;
using System.Drawing;

namespace DJMaxEditor.Controls.TimelineV2.Renderers
{
    /// <summary>
    /// Row banding, quantize lines and measure/beat grid.
    /// <para>
    /// Every pen and brush is created once and owned for the life of the renderer. It used
    /// to allocate a <see cref="SolidBrush"/> and a <see cref="Pen"/> per visible row and
    /// another <see cref="Pen"/> per ruler mark, which on a dense viewport is hundreds of
    /// native GDI+ handles per frame — the bulk of the measured V2 stutter.
    /// </para>
    /// </summary>
    public sealed class GridRenderer : IDisposable
    {
        private readonly SolidBrush _rowBrush = new SolidBrush(TimelineRenderTheme.Canvas);
        private readonly SolidBrush _rowAlternateBrush =
            new SolidBrush(TimelineRenderTheme.CanvasAlternate);
        private readonly Pen _minorPen = new Pen(TimelineRenderTheme.GridMinor);
        private readonly Pen _majorPen = new Pen(TimelineRenderTheme.GridMajor);
        private readonly Pen _quantizePen =
            new Pen(Color.FromArgb(42, TimelineRenderTheme.GridMinor));

        public void Render(Graphics graphics, TimelineFrame frame, IReadOnlyList<TimelineRulerMark> marks)
        {
            for (int rowIndex = frame.FirstVisibleRow; rowIndex < frame.Rows.Count; rowIndex++)
            {
                int y = frame.Coordinates.RowToY(rowIndex, frame.FirstVisibleRow);
                if (y >= frame.CanvasBottom) break;

                graphics.FillRectangle(
                    rowIndex % 2 == 0 ? _rowBrush : _rowAlternateBrush,
                    frame.Coordinates.HeaderWidth,
                    y,
                    frame.Width - frame.Coordinates.HeaderWidth,
                    frame.Coordinates.RowHeight);
                graphics.DrawLine(_minorPen, 0, y + frame.Coordinates.RowHeight - 1,
                    frame.Width, y + frame.Coordinates.RowHeight - 1);
            }

            RenderQuantizeLines(graphics, frame);

            for (int i = 0; i < marks.Count; i++)
            {
                TimelineRulerMark mark = marks[i];
                int x = (int)frame.Viewport.ScreenXAtTick(mark.Tick);
                graphics.DrawLine(
                    mark.Kind == TimelineRulerMarkKind.Measure ? _majorPen : _minorPen,
                    x,
                    frame.Coordinates.RulerHeight,
                    x,
                    frame.CanvasBottom);
            }
        }

        public void Dispose()
        {
            _rowBrush.Dispose();
            _rowAlternateBrush.Dispose();
            _minorPen.Dispose();
            _majorPen.Dispose();
            _quantizePen.Dispose();
        }

        private void RenderQuantizeLines(Graphics graphics, TimelineFrame frame)
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
            for (double tick = firstTick;
                tick <= frame.Viewport.VisibleTimeRange.EndTick;
                tick += interval)
            {
                int x = (int)frame.Viewport.ScreenXAtTick(tick);
                graphics.DrawLine(
                    _quantizePen,
                    x,
                    frame.Coordinates.RulerHeight,
                    x,
                    frame.CanvasBottom);
            }
        }
    }
}
