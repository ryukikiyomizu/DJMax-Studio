using DJMaxEditor.DJMax;
using System;
using System.Drawing;

namespace DJMaxEditor.Controls.Editor.Renderers
{
    internal class TracksRenderer
    {
        public const int VirtualTrackheight = 120;

        public TracksRenderer(EventsRenderer eventsRenderer, ZonesRenderer zonesRenderer)
        {
            m_eventsRenderer = eventsRenderer;
            m_zonesRenderer = zonesRenderer;
            m_trackRectangle = new Rectangle();
            m_oddTrackBrush = new SolidBrush(ColorScheme.oddTrackColor);
            m_evenTrackBrush = new SolidBrush(ColorScheme.evenTrackColor);
            m_gridColor = new Pen(Color.FromArgb(
                112,
                UI.StudioDesignSystem.Border));
            m_gridColorBeat = new Pen(Color.FromArgb(
                176,
                UI.StudioDesignSystem.PulseCyan));
            m_infoFont = UI.StudioDesignSystem.DisplayFont(16f);
            m_infoBrush = new SolidBrush(UI.StudioDesignSystem.Frost);
            m_infoFontHeight = m_infoFont.GetHeight();
        }

        public void RenderTracskList(GraphicsWrapper g, TracksList tracksList, Rectangle bounds, int beatSize, int blockSize, int virtualMaxTick, Rectangle drawableZone)
        {
            // Default color used for even tracks
            g.FillRectangle(m_evenTrackBrush, bounds);

            var trackRectangle = m_trackRectangle;
            var virtualTrackheight = VirtualTrackheight;

            foreach (var track in tracksList)
            {
                int trackIndex = (int)track.Idx;
                int trackX = trackRectangle.X = drawableZone.X;
                int trackY = trackRectangle.Y = trackIndex * virtualTrackheight;
                int trackWidth = trackRectangle.Width = drawableZone.Width;
                int trackHeight = trackRectangle.Height = virtualTrackheight;

                var viewableTrackRectangle = Rectangle.Intersect(trackRectangle, bounds);
                if (viewableTrackRectangle.IsEmpty)
                {
                    continue;
                }

                var isOddTrack = (trackIndex & 1) == 1;
                if (isOddTrack)
                {
                    g.FillRectangle(
                        m_oddTrackBrush,
                        viewableTrackRectangle
                    );
                }
            }

            int boundsX = bounds.X;
            var boundsY = bounds.Y;
            var boundsWidth = bounds.Width;
            var boundsHeight = bounds.Height;

            var blocksCount = boundsWidth / blockSize;
            var blockFrom = ((boundsX / blockSize) + 1) * blockSize;
            var blockTo = blockFrom + boundsWidth;
            var blockColor = m_gridColor;
            int gridBottom = boundsY + boundsHeight;
            // DrawLine, not DrawRectangle: a 1px-wide rectangle is four edges, so the grid
            // used to issue twice the primitives it needed for the same single pixel column.
            for (int i = blockFrom; i < blockTo; i += blockSize)
            {
                g.DrawLine(blockColor, i, boundsY, i, gridBottom);
            }

            var beatsCount = boundsWidth / beatSize;
            var beatFrom = ((boundsX / beatSize) + 1) * beatSize;
            var beatTo = beatFrom + boundsWidth;
            var beatColor = m_gridColorBeat;
            for (int i = beatFrom; i < beatTo; i += beatSize)
            {
                g.DrawLine(beatColor, i, boundsY, i, gridBottom);
            }

            foreach (var track in tracksList)
            {
                int trackIndex = (int)track.Idx;
                int trackX = trackRectangle.X = drawableZone.X;
                int trackY = trackRectangle.Y = trackIndex * virtualTrackheight;
                int trackWidth = trackRectangle.Width = drawableZone.Width;
                int trackHeight = trackRectangle.Height = virtualTrackheight;

                var viewableTrackRectangle = Rectangle.Intersect(trackRectangle, bounds);
                if (viewableTrackRectangle.IsEmpty)
                {
                    continue;
                }

                // Everything at or past this tick starts to the right of the viewport, and a
                // note body only ever extends rightwards, so the whole tail of the chart can
                // be skipped without a per-event rectangle test. The left edge deliberately
                // is *not* windowed: a long note whose head is off-screen to the left can
                // still have its body on screen, and sustains are edited in place (see
                // ResizeEventsAction) so no cached "longest sustain" could be trusted here.
                var ordered = track.OrderedEvents;
                int scanEnd = track.FirstIndexAtOrAfterTick(
                    FirstTickPastRightEdge(viewableTrackRectangle.Right));

                for (int i = 0; i < scanEnd; i++)
                {
                    m_eventsRenderer.RenderEventData(g, ordered[i], viewableTrackRectangle, trackY);
                }

                int trackNamePosX = viewableTrackRectangle.X;
                int trackNamePosY = trackY;

                if (trackNamePosX < boundsX)
                {
                    trackNamePosX = boundsX;
                }

                if (trackNamePosY < boundsY)
                {
                    trackNamePosY = boundsY;
                }

                DrawTrackName(
                    g,
                    track.DisplayedTrackName,
                    trackNamePosX + 10,
                    trackNamePosY + 3);


                m_zonesRenderer.DrawZones(g, trackIndex, trackX, trackY, trackWidth, trackHeight, viewableTrackRectangle);
            }
        }

        /// <summary>
        /// Draws a track name from the shared rasterised-text cache. A visible track name
        /// per frame is a handful of <c>DrawString</c> calls, but each one costs a flattened
        /// glyph path per character because the editor paints under a scale transform.
        /// </summary>
        private void DrawTrackName(GraphicsWrapper g, string name, int x, int y)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (!TextImageCache.IsLegible(m_infoFontHeight, g.LabelScale)) return;

            TextImage label = TextImageCache.Get(m_infoFont, name, m_infoColor);
            if (label != null)
            {
                g.DrawLabel(label.Image, label.StartingAt(x, y));
                return;
            }

            g.DrawString(name, m_infoFont, m_infoBrush, x, y);
        }

        private readonly ZonesRenderer m_zonesRenderer;
        private readonly EventsRenderer m_eventsRenderer;

        private readonly Brush m_oddTrackBrush;

        private readonly Brush m_evenTrackBrush;

        private readonly Pen m_gridColor;

        private readonly Pen m_gridColorBeat;

        private readonly Font m_infoFont;

        private readonly Brush m_infoBrush;

        private readonly Color m_infoColor = UI.StudioDesignSystem.Frost;

        private readonly float m_infoFontHeight;

        private Rectangle m_trackRectangle;

        /// <summary>
        /// Lowest tick whose note head is guaranteed to start at or past
        /// <paramref name="rightEdge"/>, in the same virtual space
        /// <see cref="EventsRenderer.GetEventRectangle"/> works in: a note is drawn from
        /// <c>VirtualTick - HalfNoteWidth</c>, and <c>VirtualTick == Tick * VirtualTickSize</c>.
        /// </summary>
        private static int FirstTickPastRightEdge(int rightEdge)
        {
            long firstVirtualTick = (long)rightEdge + HalfVirtualNoteWidth;
            if (firstVirtualTick < 0) return 0;

            // Ceiling divide so a note sitting exactly on the edge is still included.
            long tick = (firstVirtualTick + EventData.VirtualTickSize - 1)
                / EventData.VirtualTickSize;
            return tick > int.MaxValue ? int.MaxValue : (int)tick + 1;
        }

        /// <summary>Half of <c>EventsRenderer</c>'s virtual note width, in virtual ticks.</summary>
        private const int HalfVirtualNoteWidth = 60;

        private int RoundUp(int num, int factor)
        {
            return num + factor - 1 - (num - 1) % factor;
        }
    }
}
