using DJMaxEditor.DJMax;
using System;
using System.Collections.Generic;
using System.Drawing;

namespace DJMaxEditor.Controls.Editor.Renderers.Events
{
    internal class NullThemeRenderer : EventRenderer
    {
        public NullThemeRenderer()
        {
            _stringFormat.Alignment = StringAlignment.Center;
            _stringFormat.LineAlignment = StringAlignment.Center;
            _fontHeight = m_textFont.GetHeight();
        }

        public override void DrawZones(GraphicsWrapper g, int trackIndex, int trackX, int trackY, int width, int height, Rectangle bounds)
        {
        }

        public override string GetName()
        {
            return "Default";
        }

        public override string GetDescription()
        {
            return "Plain boxes with labels. Reads on every chart; nothing game-specific.";
        }

        public override IEnumerable<KeyValuePair<string, EventData>> GetTemplates()
        {
            var list = new List<KeyValuePair<string, EventData>>();

            list.Add(this.CreateNoteFromEventData(
                "Basic note",
                new EventData()
                {
                    EventType = EventType.Note,
                    Attribute = (byte)EventAttribute.BasicNote,
                }
            ));

            list.Add(this.CreateNoteFromEventData(
                "Tempo",
                new EventData()
                {
                    EventType = EventType.Tempo,
                }
            ));

            list.Add(this.CreateNoteFromEventData(
                "Volume",
                new EventData()
                {
                    EventType = EventType.Volume,
                }
            ));

            list.Add(this.CreateNoteFromEventData(
                "Beat",
                new EventData()
                {
                    EventType = EventType.Beat,
                }
            ));

            return list;
        }

        public override void RenderEventData(GraphicsWrapper g, EventData eventData, Rectangle eventRectangle, int centerX, int centerY)
        {
            //g.DrawRectangle(Pens.Red, eventRectangle);

            switch (eventData.EventType)
            {
                case EventType.Note:
                    {
                        RenderNote(g, eventData, eventRectangle, centerX, centerY);
                    }
                    break;
                case EventType.Tempo:
                    {
                        DrawImage(g, DJMRessources.metronome, centerX - (90 / 2), centerY - (90 / 2), 90, 90);
                    }
                    break;
                case EventType.Volume:
                    {
                        DrawImage(g, DJMRessources.volume, centerX - (64 / 2), centerY - (64 / 2), 64, 64);
                    }
                    break;
                case EventType.Beat:
                    {
                        DrawImage(g, DJMRessources.beat, centerX - (90 / 2), centerY - (90 / 2), 90, 90);
                    }
                    break;
                default:
                    {
                        DrawImage(g, DJMRessources.unknowNote, eventRectangle.X, eventRectangle.Y, 90, 90);
                    }
                    break;
            }
        }

        public override void RenderNote(GraphicsWrapper g, EventData eventData, Rectangle eventRectangle, int centerX, int centerY)
        {
            int rectangleX = centerX - (RECTANGLE_WIDTH / 2);
            int rectangleY = centerY - (RECTANGLE_HEIGHT / 2);
            int rectangleWidth = RECTANGLE_WIDTH + (eventData.Duration > 6 ? eventData.VirtualDuration : 0);
            int rectangleHeight = RECTANGLE_HEIGHT;

            g.FillRectangle(CustomBrushes.NoteBackground, rectangleX, rectangleY, rectangleWidth, rectangleHeight);
            g.DrawRectangle(Pens.Black, rectangleX, rectangleY, rectangleWidth, rectangleHeight);

            // Labels come from a precomputed table now. This used to string.Format once per
            // visible note per frame, which is what made Attr mode the slowest V1 view.
            // An unrecognised display mode yields no label, and drawing nothing must cost
            // nothing rather than two GDI+ text calls on an empty string.
            string text = EventLabelCache.For(EventDisplayMode, eventData);
            if (text.Length == 0) return;

            // Both horizontal surfaces draw notes through a scale transform, and the V2
            // timeline's theme art runs at 0.2x where this 20pt font lands under 6px tall.
            // Nobody can read that, and it is the single most expensive call in the frame.
            if (!TextImageCache.IsLegible(_fontHeight, g.LabelScale)) return;

            // Rasterised once per distinct label, then blitted: GDI+ flattens glyphs to
            // filled paths under a world transform, so the text calls below cost roughly a
            // hundred times what the blit does.
            TextImage label = TextImageCache.Get(
                m_textFont, text, Color.White, Color.Black, SHADOW_DISTANCE);
            if (label != null)
            {
                g.DrawLabel(label.Image, label.CenteredOn(centerX, centerY));
                return;
            }

            g.DrawString(text, this.m_textFont, Brushes.Black, centerX + SHADOW_DISTANCE, centerY + SHADOW_DISTANCE, _stringFormat);
            g.DrawString(text, this.m_textFont, Brushes.White, centerX, centerY, _stringFormat);
        }

        private Font m_textFont = new Font("Tahoma", 20, FontStyle.Bold);

        /// <summary>Cached because <see cref="Font.GetHeight()"/> is a native call.</summary>
        private readonly float _fontHeight;

        private StringFormat _stringFormat = new StringFormat();

        private const int RECTANGLE_WIDTH = 118;

        private const int RECTANGLE_HEIGHT = 45;

        private const int SHADOW_DISTANCE = 3;
    }
}
