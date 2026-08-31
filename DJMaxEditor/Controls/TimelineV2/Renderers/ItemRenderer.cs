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
    public sealed class ItemRenderer : IDisposable
    {
        /// <summary>
        /// Number of distinct velocity brightness steps. Velocity is 0-127, but 8 steps is
        /// already past what the eye resolves on a 7px glyph and it keeps the brush cache to a
        /// couple of dozen entries instead of one per value per item kind.
        /// </summary>
        private const int VelocitySteps = 8;

        private const byte MaxVelocity = 127;

        private readonly FallbackArtRenderer _fallback = new FallbackArtRenderer();
        private readonly SolidBrush _noteBrush = new SolidBrush(TimelineRenderTheme.Note);
        private readonly SolidBrush _automationBrush =
            new SolidBrush(TimelineRenderTheme.Automation);
        private readonly Pen _noteOutline = new Pen(TimelineRenderTheme.NoteOutline, 1f);
        private readonly Pen _automationOutline =
            new Pen(TimelineRenderTheme.AutomationOutline, 1f);
        private readonly SolidBrush _selectionBrush =
            new SolidBrush(TimelineRenderTheme.Selection);
        private readonly Pen _selectionOutline =
            new Pen(TimelineRenderTheme.SelectionOutline, 1f);

        /// <summary>
        /// Velocity-shaded body brushes, keyed by colour. Same rationale as the vertical
        /// renderer's cache: the palette is a fixed small set, so it converges after one frame
        /// rather than allocating a brush per note per frame.
        /// </summary>
        private readonly Dictionary<int, SolidBrush> _velocityBrushes =
            new Dictionary<int, SolidBrush>();

        private readonly SolidBrush _labelBackground =
            new SolidBrush(Color.FromArgb(210, 17, 20, 25));
        private readonly SolidBrush _labelText =
            new SolidBrush(UI.StudioDesignSystem.Frost);
        private readonly Font _labelFont =
            UI.StudioDesignSystem.UtilityFont(6.5f);
        private readonly Dictionary<string, Bitmap> _labelImages =
            new Dictionary<string, Bitmap>();
        private readonly GraphicsWrapper _themeArtWrapper = new GraphicsWrapper();

        private static readonly Rectangle ThemeArtBounds =
            new Rectangle(-59, -22, 118, 45);

        /// <summary>
        /// Releases the cached brushes, font and pre-rendered label bitmaps. The label
        /// bitmaps are the reason this cache exists at all: measuring and drawing a string
        /// per visible note per frame was the original cost, so they are drawn once per
        /// distinct label and then blitted.
        /// </summary>
        public void Dispose()
        {
            _noteBrush.Dispose();
            _automationBrush.Dispose();
            _noteOutline.Dispose();
            _automationOutline.Dispose();
            _selectionBrush.Dispose();
            _selectionOutline.Dispose();
            foreach (SolidBrush brush in _velocityBrushes.Values)
            {
                brush.Dispose();
            }
            _velocityBrushes.Clear();
            _labelBackground.Dispose();
            _labelText.Dispose();
            _labelFont.Dispose();
            foreach (Bitmap image in _labelImages.Values)
            {
                image.Dispose();
            }
            _labelImages.Clear();
        }

        public void Render(Graphics graphics, TimelineFrame frame)
        {
            // Both of these are constant for the whole frame, but used to be recomputed per
            // visible note — including a GetName() string compare against "Technika".
            bool drawDetail = frame.EventTheme != null && frame.VisibleItems.Count <= 2500;
            bool technikaTheme = drawDetail && string.Equals(
                frame.EventTheme.GetName(), "Technika", StringComparison.OrdinalIgnoreCase);

            // The body clip, the theme-art scale and the wrapper are the same for every item
            // in the frame. They used to be set up inside the per-note theme-art call, so a
            // dense frame paid a region intersect plus a full GraphicsState save and restore
            // several hundred times over to draw art that never leaves the body anyway.
            GraphicsState state = graphics.Save();
            Matrix baseTransform = graphics.Transform;
            try
            {
                graphics.SetClip(new Rectangle(
                    frame.Coordinates.HeaderWidth,
                    frame.Coordinates.RulerHeight,
                    Math.Max(1, frame.Width - frame.Coordinates.HeaderWidth),
                    Math.Max(1, frame.CanvasBottom - frame.Coordinates.RulerHeight)));

                float themeArtScale = 0.2f * (frame.Coordinates.RowHeight / 28f);
                // One wrapper for the whole control's lifetime. This used to be a fresh
                // GraphicsWrapper per visible note per frame, i.e. hundreds of throwaway
                // objects a frame purely to hand the theme a Graphics it already had.
                _themeArtWrapper.UpdateGraphics(graphics);
                _themeArtWrapper.LabelScale = themeArtScale;

                RenderItems(
                    graphics, frame, drawDetail, technikaTheme, themeArtScale, baseTransform);
            }
            finally
            {
                graphics.Restore(state);
                baseTransform.Dispose();
            }
        }

        private void RenderItems(
            Graphics graphics,
            TimelineFrame frame,
            bool drawDetail,
            bool technikaTheme,
            float themeArtScale,
            Matrix baseTransform)
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

                // Read the ticks off the live event rather than the projection snapshot:
                // a sustain resized in place keeps its projected EndTick, so the bar used to
                // keep its old length until something else forced a re-projection.
                EventData source = item.SourceEvent;
                int startTick = TimelineItemGeometry.StartTickOf(item);
                int endTick = TimelineItemGeometry.EndTickOf(item);

                int startX = (int)frame.Viewport.ScreenXAtTick(startTick);
                int endX = (int)frame.Viewport.ScreenXAtTick(endTick);
                int centerY = y + (frame.Coordinates.RowHeight / 2);
                int glyphWidth = TimelineItemGeometry.GlyphWidth(frame);
                int glyphHeight = TimelineItemGeometry.GlyphHeight(frame);
                bool selected = frame.IsSelected(source);
                bool isNote = source != null && source.EventType == EventType.Note;
                Brush itemBrush = selected
                    ? _selectionBrush
                    : BodyBrush(isNote, source);
                Pen itemOutline = selected
                    ? _selectionOutline
                    : (isNote ? _noteOutline : _automationOutline);
                int durationWidth = Math.Max(0, endX - startX);
                if (durationWidth > 1)
                {
                    graphics.FillRectangle(
                        itemBrush,
                        startX,
                        centerY - TimelineItemGeometry.SustainHalfHeight,
                        durationWidth,
                        TimelineItemGeometry.SustainHalfHeight * 2);
                    // Outline the sustain bar too: back-to-back long notes on the same row are
                    // otherwise one continuous stripe with no visible join.
                    graphics.DrawRectangle(
                        itemOutline,
                        startX,
                        centerY - TimelineItemGeometry.SustainHalfHeight,
                        durationWidth,
                        TimelineItemGeometry.SustainHalfHeight * 2);
                }

                bool usedAuthenticTechnikaArt = !selected && technikaTheme &&
                    TechnikaNoteArt.TryDraw(
                        graphics,
                        source,
                        startX,
                        centerY,
                        frame.Coordinates.RowHeight);
                if (!usedAuthenticTechnikaArt)
                {
                    int glyphLeft = startX - (glyphWidth / 2);
                    int glyphTop = centerY - (glyphHeight / 2);
                    graphics.FillRectangle(
                        itemBrush,
                        glyphLeft,
                        glyphTop,
                        glyphWidth,
                        glyphHeight);
                    graphics.DrawRectangle(
                        itemOutline,
                        glyphLeft,
                        glyphTop,
                        glyphWidth - 1,
                        glyphHeight - 1);
                    if (drawDetail)
                    {
                        RenderThemeArt(
                            graphics, frame, source, startX, centerY,
                            themeArtScale, baseTransform);
                    }
                }

                if (!drawDetail)
                {
                    continue;
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

        /// <summary>
        /// Body brush for an unselected item, shaded by the note's velocity so per-note volume
        /// is legible on the chart. Full velocity is the plain theme colour, so an untouched
        /// chart looks exactly as it did before note volume existed.
        /// </summary>
        private Brush BodyBrush(bool isNote, EventData source)
        {
            if (source == null)
            {
                return _automationBrush;
            }
            if (!isNote)
            {
                // Velocity is not meaningful on tempo/beat/volume events, so they keep the flat
                // automation colour rather than being shaded by a byte nobody authored.
                return _automationBrush;
            }
            if (source.Vel >= MaxVelocity)
            {
                return _noteBrush;
            }

            int step = (source.Vel * VelocitySteps) / (MaxVelocity + 1);
            float amount = 1f - (step / (float)(VelocitySteps - 1));
            Color shaded = TimelineRenderTheme.Blend(
                TimelineRenderTheme.Note, TimelineRenderTheme.SilentNote, amount);

            int key = shaded.ToArgb();
            SolidBrush brush;
            if (!_velocityBrushes.TryGetValue(key, out brush))
            {
                brush = new SolidBrush(shaded);
                _velocityBrushes.Add(key, brush);
            }
            return brush;
        }

        private void RenderThemeArt(
            Graphics graphics,
            TimelineFrame frame,
            EventData source,
            int startX,
            int centerY,
            float scale,
            Matrix baseTransform)
        {
            if (source == null) return;

            graphics.TranslateTransform(startX, centerY);
            graphics.ScaleTransform(scale, scale);
            try
            {
                frame.EventTheme.RenderEventData(
                    _themeArtWrapper,
                    source,
                    ThemeArtBounds,
                    0,
                    0);
            }
            finally
            {
                // Assigning the frame's base matrix back is a single native call. Save and
                // Restore would clone and re-apply the clip region as well, which is the
                // part that actually costs, and the clip has not changed.
                graphics.Transform = baseTransform;
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
                    // EventDisplayMode.None, the default. This arm used to be the attribute
                    // label, so leaving it in place would have kept V2 stamping "A000" on
                    // every note while V1 drew nothing - the two surfaces silently
                    // disagreeing, and no speed win where it was most visible.
                    return;
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
