using System.Collections.Generic;
using System.Drawing;

namespace DJMaxEditor.Controls.TimelineV2.Renderers
{
    public sealed class RulerRenderer
    {
        public void Render(Graphics graphics, TimelineFrame frame, IReadOnlyList<TimelineRulerMark> marks)
        {
            using (var brush = new SolidBrush(TimelineRenderTheme.Ruler))
            {
                graphics.FillRectangle(brush, 0, 0, frame.Width, frame.Coordinates.RulerHeight);
            }

            using (var textBrush = new SolidBrush(TimelineRenderTheme.Text))
            using (var mutedBrush = new SolidBrush(TimelineRenderTheme.MutedText))
            using (var font = UI.StudioDesignSystem.UtilityFont(8f))
            using (var statusFormat = new StringFormat
            {
                FormatFlags = StringFormatFlags.NoWrap,
                Trimming = StringTrimming.EllipsisCharacter
            })
            {
                foreach (TimelineRulerMark mark in marks)
                {
                    int x = (int)frame.Viewport.ScreenXAtTick(mark.Tick);
                    int height = mark.Kind == TimelineRulerMarkKind.Measure ? 14 : 8;
                    using (var pen = new Pen(
                        mark.Kind == TimelineRulerMarkKind.Measure
                            ? TimelineRenderTheme.GridMajor
                            : TimelineRenderTheme.GridMinor))
                    {
                        graphics.DrawLine(pen, x, frame.Coordinates.RulerHeight - height,
                            x, frame.Coordinates.RulerHeight);
                    }

                    if (!string.IsNullOrEmpty(mark.Label))
                    {
                        graphics.DrawString(mark.Label, font,
                            mark.Kind == TimelineRulerMarkKind.RawTick ? mutedBrush : textBrush,
                            x + 3, 4);
                    }
                }

                graphics.DrawString(
                    frame.StatusText,
                    font,
                    mutedBrush,
                    new RectangleF(8, 22, frame.Coordinates.HeaderWidth - 16, 16),
                    statusFormat);
            }
        }
    }
}
