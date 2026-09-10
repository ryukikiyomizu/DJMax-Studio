using System;

namespace DJMaxEditor.Controls
{
    /// <summary>
    /// Sizing policy for the editor surfaces' scroll bands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both V1 (<c>EditorControl</c>) and V2 (<c>TimelineV2Control</c>) cache their chart layer into
    /// a bitmap wider than the visible area and blit it at an offset, so a scrolling view costs a
    /// copy rather than a re-render. That is what makes continuous follow-while-playing affordable:
    /// the caches used to be keyed on the scroll position, so a view that moved a few pixels sixty
    /// times a second re-rendered every visible note and label sixty times a second.
    /// </para>
    /// <para>
    /// The number lives here rather than in either surface because the two must not drift into
    /// different scrolling behaviour — the playtest reads them side by side.
    /// </para>
    /// </remarks>
    public static class ScrollBand
    {
        /// <summary>
        /// Lower bound on the margin. Below roughly this much lookahead the rebuilds come often
        /// enough that the blit stops paying for the extra pixels rendered.
        /// </summary>
        public const int MinMarginPixels = 128;

        /// <summary>
        /// Upper bound on the margin. The band is a bitmap and a render pass, so both its memory and
        /// its rebuild cost grow with it; past half a screen either side the lookahead buys little.
        /// </summary>
        public const int MaxMarginPixels = 512;

        /// <summary>
        /// How many pixels of chart to cache either side of a viewport <paramref name="contentWidth"/>
        /// pixels wide. Half a screen is the balance point: the band is then about twice the
        /// viewport, so a rebuild costs roughly two frames and buys half a screen of scrolling.
        /// </summary>
        public static int MarginPixels(int contentWidth)
        {
            if (contentWidth <= 0)
            {
                return 0;
            }
            return Math.Max(MinMarginPixels, Math.Min(MaxMarginPixels, contentWidth / 2));
        }
    }
}
