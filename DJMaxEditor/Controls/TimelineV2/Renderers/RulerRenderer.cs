using System;
using System.Collections.Generic;
using System.Drawing;

namespace DJMaxEditor.Controls.TimelineV2.Renderers
{
    /// <summary>
    /// Top ruler: measure/beat ticks and their labels.
    /// <para>
    /// It used to also print the document status line into the header corner. The ruler is now
    /// one row tall, the string never fit ("TIMELINE V2 - EDITING R..."), and the shell status
    /// rail says the same thing with room to spare — so the band is chrome only, and the whole
    /// strip is a seek target instead.
    /// </para>
    /// <para>
    /// Resources are owned rather than created per call. This used to allocate a
    /// <see cref="Pen"/> per ruler mark and a <see cref="Font"/> per frame; a zoomed-out
    /// viewport has hundreds of marks, so that was hundreds of GDI+ handles created and
    /// finalised on every single frame.
    /// </para>
    /// </summary>
    public sealed class RulerRenderer : System.IDisposable
    {
        private readonly SolidBrush _rulerBrush = new SolidBrush(TimelineRenderTheme.Ruler);
        private readonly SolidBrush _textBrush = new SolidBrush(TimelineRenderTheme.Text);
        private readonly SolidBrush _mutedBrush = new SolidBrush(TimelineRenderTheme.MutedText);
        private readonly Pen _majorPen = new Pen(TimelineRenderTheme.GridMajor);
        private readonly Pen _minorPen = new Pen(TimelineRenderTheme.GridMinor);
        private readonly Pen _baselinePen = new Pen(TimelineRenderTheme.HeaderBorder);
        private readonly Font _font = UI.StudioDesignSystem.UtilityFont(8f);

        public void Render(Graphics graphics, TimelineFrame frame, IReadOnlyList<TimelineRulerMark> marks)
        {
            int rulerHeight = frame.Coordinates.RulerHeight;
            graphics.FillRectangle(_rulerBrush, 0, 0, frame.Width, rulerHeight);

            for (int i = 0; i < marks.Count; i++)
            {
                TimelineRulerMark mark = marks[i];
                bool isMeasure = mark.Kind == TimelineRulerMarkKind.Measure;
                int x = (int)frame.Viewport.ScreenXAtTick(mark.Tick);

                // Ticks are proportional to the band so they keep their proportions if the
                // ruler height ever changes again: a measure line runs half the band, a beat
                // line a third of it.
                int height = isMeasure
                    ? Math.Max(6, rulerHeight / 2)
                    : Math.Max(4, rulerHeight / 3);

                graphics.DrawLine(
                    isMeasure ? _majorPen : _minorPen,
                    x,
                    rulerHeight - height,
                    x,
                    rulerHeight);

                if (!string.IsNullOrEmpty(mark.Label))
                {
                    graphics.DrawString(mark.Label, _font,
                        mark.Kind == TimelineRulerMarkKind.RawTick ? _mutedBrush : _textBrush,
                        x + 3, 1);
                }
            }

            // Hairline under the band so the labels never look like they are floating in the
            // first row of chart.
            graphics.DrawLine(_baselinePen, 0, rulerHeight - 1, frame.Width, rulerHeight - 1);
        }

        public void Dispose()
        {
            _rulerBrush.Dispose();
            _textBrush.Dispose();
            _mutedBrush.Dispose();
            _majorPen.Dispose();
            _minorPen.Dispose();
            _baselinePen.Dispose();
            _font.Dispose();
        }
    }
}
