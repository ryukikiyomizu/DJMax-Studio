using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using DJMaxEditor.Controls.Editor;
using DJMaxEditor.Controls.Editor.Renderers.Events;
using DJMaxEditor.DJMax;

namespace DJMaxEditor.Controls.TimelineV2.Renderers
{
    public sealed class ItemRenderer
    {
        private readonly FallbackArtRenderer _fallback = new FallbackArtRenderer();
        private readonly SolidBrush _noteBrush = new SolidBrush(TimelineRenderTheme.Note);
        private readonly SolidBrush _automationBrush =
            new SolidBrush(TimelineRenderTheme.Automation);
        private readonly SolidBrush _labelBackground =
            new SolidBrush(Color.FromArgb(210, 17, 20, 25));
        private readonly SolidBrush _labelText =
            new SolidBrush(UI.StudioDesignSystem.Frost);
        private readonly Font _labelFont =
            UI.StudioDesignSystem.UtilityFont(6.5f);
        private readonly Dictionary<string, Bitmap> _labelImages =
            new Dictionary<string, Bitmap>();

        public void Render(Graphics graphics, TimelineFrame frame)
        {
            foreach (TimelineItem item in frame.VisibleItems)
            {
                if (item.RowIndex < frame.FirstVisibleRow) continue;
                int y = frame.Coordinates.RowToY(item.RowIndex, frame.FirstVisibleRow);
                if (y >= frame.CanvasBottom) continue;

                if (item.IsUnknown)
                {
                    _fallback.Render(graphics, frame, item);
                    continue;
                }

                int startX = (int)frame.Viewport.ScreenXAtTick(item.StartTick);
                int endX = (int)frame.Viewport.ScreenXAtTick(item.EndTick);
                int centerY = y + (frame.Coordinates.RowHeight / 2);
                float noteScale = frame.Coordinates.RowHeight / 28f;
                int glyphWidth = Math.Max(7, (int)Math.Round(7 * noteScale));
                int glyphHeight = Math.Max(12, (int)Math.Round(12 * noteScale));
                SolidBrush itemBrush = item.SourceEvent.EventType == EventType.Note
                    ? _noteBrush
                    : _automationBrush;
                int durationWidth = Math.Max(0, endX - startX);
                if (durationWidth > 1)
                {
                    graphics.FillRectangle(
                        itemBrush,
                        startX,
                        centerY - 2,
                        durationWidth,
                        4);
                }

                bool usedAuthenticTechnikaArt = RenderAuthenticTechnikaArt(
                    graphics,
                    frame,
                    item,
                    startX,
                    centerY);
                if (!usedAuthenticTechnikaArt)
                {
                    graphics.FillRectangle(
                        itemBrush,
                        startX - (glyphWidth / 2),
                        centerY - (glyphHeight / 2),
                        glyphWidth,
                        glyphHeight);
                    RenderThemeArt(graphics, frame, item, startX, centerY);
                }

                int authenticSize = Math.Min(
                    48,
                    Math.Max(14, frame.Coordinates.RowHeight - 4));
                RenderCompactLabel(
                    graphics,
                    frame,
                    item,
                    startX,
                    centerY,
                    usedAuthenticTechnikaArt
                        ? (authenticSize / 2) + 2
                        : (glyphWidth / 2) + 2);
            }
        }

        private static bool RenderAuthenticTechnikaArt(
            Graphics graphics,
            TimelineFrame frame,
            TimelineItem item,
            int startX,
            int centerY)
        {
            if (frame.EventTheme == null ||
                frame.VisibleItems.Count > 2500 ||
                !string.Equals(
                    frame.EventTheme.GetName(),
                    "Technika",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return TechnikaNoteArt.TryDraw(
                graphics,
                item.SourceEvent,
                startX,
                centerY,
                frame.Coordinates.RowHeight);
        }

        private static void RenderThemeArt(
            Graphics graphics,
            TimelineFrame frame,
            TimelineItem item,
            int startX,
            int centerY)
        {
            if (frame.EventTheme == null || frame.VisibleItems.Count > 2500)
            {
                return;
            }

            GraphicsState state = graphics.Save();
            try
            {
                graphics.SetClip(new Rectangle(
                    frame.Coordinates.HeaderWidth,
                    frame.Coordinates.RulerHeight,
                    Math.Max(1, frame.Width - frame.Coordinates.HeaderWidth),
                    Math.Max(1, frame.CanvasBottom - frame.Coordinates.RulerHeight)));
                graphics.TranslateTransform(startX, centerY);
                float scale = 0.2f * (frame.Coordinates.RowHeight / 28f);
                graphics.ScaleTransform(scale, scale);
                var wrapper = new GraphicsWrapper();
                wrapper.UpdateGraphics(graphics);
                frame.EventTheme.RenderEventData(
                    wrapper,
                    item.SourceEvent,
                    new Rectangle(-59, -22, 118, 45),
                    0,
                    0);
            }
            finally
            {
                graphics.Restore(state);
            }
        }

        private void RenderCompactLabel(
            Graphics graphics,
            TimelineFrame frame,
            TimelineItem item,
            int startX,
            int centerY,
            int labelOffset)
        {
            if (frame.VisibleItems.Count > 2500)
            {
                return;
            }

            EventData source = item.SourceEvent;
            string label;
            switch (frame.EventDisplayMode)
            {
                case EventDisplayMode.Instrument:
                    label = "I" + (source.Instrument == null
                        ? "000"
                        : source.Instrument.InsNum.ToString("000"));
                    break;
                case EventDisplayMode.Duration:
                    label = "D" + source.Duration.ToString("000");
                    break;
                case EventDisplayMode.Pan:
                    label = "P" + source.Pan.ToString("000");
                    break;
                case EventDisplayMode.Velocity:
                    label = "V" + source.Vel.ToString("000");
                    break;
                default:
                    label = "A" + source.Attribute.ToString("000");
                    break;
            }

            Bitmap labelImage = GetLabelImage(label);
            graphics.DrawImageUnscaled(
                labelImage,
                startX + labelOffset,
                centerY - 7);
        }

        private Bitmap GetLabelImage(string label)
        {
            Bitmap image;
            if (_labelImages.TryGetValue(label, out image))
            {
                return image;
            }

            int width;
            using (var measure = new Bitmap(1, 1))
            using (Graphics graphics = Graphics.FromImage(measure))
            {
                width = (int)Math.Ceiling(
                    graphics.MeasureString(label, _labelFont).Width) + 4;
            }

            image = new Bitmap(width, 14, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(image))
            {
                graphics.FillRectangle(_labelBackground, 0, 0, width, 14);
                graphics.DrawString(label, _labelFont, _labelText, 2, 0);
            }
            _labelImages.Add(label, image);
            return image;
        }
    }
}
