using System;
using System.Drawing;
using DJMaxEditor.DJMax;

namespace DJMaxEditor.Controls.TimelineV2
{
    /// <summary>
    /// The single definition of where a timeline item lands in device pixels.
    /// </summary>
    /// <remarks>
    /// Both <see cref="Renderers.ItemRenderer"/> and <see cref="TimelineV2Control"/>'s hit test
    /// read this, which is the point: V2 became editable, and a click that resolves against a
    /// second, independently written copy of the layout maths is a click that eventually lands on
    /// nothing. Ticks are read off the live <see cref="EventData"/> rather than the projection
    /// snapshot for the same reason the renderer does it — a note resized in place keeps its
    /// projected <see cref="TimelineItem.EndTick"/> until something forces a re-projection.
    /// </remarks>
    public static class TimelineItemGeometry
    {
        /// <summary>Row height the base glyph size was authored against.</summary>
        public const int ReferenceRowHeight = 28;

        public const int BaseGlyphWidth = 7;
        public const int BaseGlyphHeight = 12;

        /// <summary>Half-height of a sustain bar, in pixels.</summary>
        public const int SustainHalfHeight = 2;

        /// <summary>
        /// Extra pixels added around a body when resolving a pointer. A 7px glyph is a hard
        /// target with a mouse, and an unhittable note reads as a broken editor.
        /// </summary>
        public const int GrabTolerance = 3;

        /// <summary>Width of the drag handle at the end of a sustain.</summary>
        public const int SustainGripWidth = 7;

        public static float NoteScale(TimelineFrame frame)
        {
            if (frame == null) throw new ArgumentNullException("frame");
            return frame.Coordinates.RowHeight / (float)ReferenceRowHeight;
        }

        public static int GlyphWidth(TimelineFrame frame)
        {
            return Math.Max(
                BaseGlyphWidth,
                (int)Math.Round(BaseGlyphWidth * NoteScale(frame)));
        }

        public static int GlyphHeight(TimelineFrame frame)
        {
            return Math.Max(
                BaseGlyphHeight,
                (int)Math.Round(BaseGlyphHeight * NoteScale(frame)));
        }

        public static int StartTickOf(TimelineItem item)
        {
            if (item == null) throw new ArgumentNullException("item");
            EventData source = item.SourceEvent;
            return source == null ? item.StartTick : source.VirtualTick;
        }

        public static int EndTickOf(TimelineItem item)
        {
            if (item == null) throw new ArgumentNullException("item");
            EventData source = item.SourceEvent;
            return source == null
                ? item.EndTick
                : source.VirtualTick + source.VirtualDuration;
        }

        /// <summary>Vertical centre of the row the item sits on.</summary>
        public static int CenterY(TimelineFrame frame, TimelineItem item)
        {
            if (frame == null) throw new ArgumentNullException("frame");
            if (item == null) throw new ArgumentNullException("item");

            return frame.Coordinates.RowToY(item.RowIndex, frame.FirstVisibleRow) +
                (frame.Coordinates.RowHeight / 2);
        }

        /// <summary>
        /// The glyph box: what the renderer fills at the item's start tick when the active
        /// events theme does not supply its own artwork.
        /// </summary>
        public static Rectangle GlyphRect(TimelineFrame frame, TimelineItem item)
        {
            int startX = (int)frame.Viewport.ScreenXAtTick(StartTickOf(item));
            int width = GlyphWidth(frame);
            int height = GlyphHeight(frame);
            int centerY = CenterY(frame, item);
            return new Rectangle(
                startX - (width / 2),
                centerY - (height / 2),
                width,
                height);
        }

        /// <summary>
        /// Glyph plus sustain bar. Empty width for a tap, so callers should union rather than
        /// test this alone.
        /// </summary>
        public static Rectangle BodyRect(TimelineFrame frame, TimelineItem item)
        {
            Rectangle glyph = GlyphRect(frame, item);
            int endX = (int)frame.Viewport.ScreenXAtTick(EndTickOf(item));
            if (endX <= glyph.Right)
            {
                return glyph;
            }
            return Rectangle.Union(
                glyph,
                new Rectangle(
                    glyph.Left + (glyph.Width / 2),
                    CenterY(frame, item) - SustainHalfHeight,
                    endX - (glyph.Left + (glyph.Width / 2)),
                    SustainHalfHeight * 2));
        }

        /// <summary>The pointer target for selecting, moving, or erasing an item.</summary>
        public static Rectangle GrabRect(TimelineFrame frame, TimelineItem item)
        {
            Rectangle body = BodyRect(frame, item);
            body.Inflate(GrabTolerance, GrabTolerance);
            return body;
        }

        /// <summary>
        /// The pointer target for dragging a sustain's tail. Sits at the end tick so a long
        /// note can be lengthened without first having to hit its 7px head.
        /// </summary>
        public static Rectangle SustainGripRect(TimelineFrame frame, TimelineItem item)
        {
            int endX = (int)frame.Viewport.ScreenXAtTick(EndTickOf(item));
            int centerY = CenterY(frame, item);
            int halfHeight = Math.Max(GlyphHeight(frame) / 2, SustainHalfHeight + GrabTolerance);
            return new Rectangle(
                endX - (SustainGripWidth / 2),
                centerY - halfHeight,
                SustainGripWidth,
                halfHeight * 2);
        }
    }
}
