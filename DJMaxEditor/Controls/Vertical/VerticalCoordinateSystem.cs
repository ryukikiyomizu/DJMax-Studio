using System;

namespace DJMaxEditor.Controls.Vertical
{
    /// <summary>Which way the vertical time axis runs on screen.</summary>
    public enum VerticalTimeDirection
    {
        /// <summary>
        /// Later ticks sit lower. Convenient for a spreadsheet-like read, but it is not how
        /// any DJMax client presents a chart.
        /// </summary>
        Downward = 0,

        /// <summary>
        /// Later ticks sit higher, so notes travel downward toward a judgement line at the
        /// bottom — the way the game itself scrolls, and the way ptSequencer shows a chart.
        /// </summary>
        Upward = 1
    }

    /// <summary>
    /// Pure conversion for the ptSequencer-style vertical timeline: authoritative
    /// ticks map to the vertical (Y) axis and native ptSequencer column offsets map
    /// to the horizontal (X) axis. This is the axis-swapped mirror of
    /// <see cref="DJMaxEditor.Controls.TimelineV2.TimelineCoordinateSystem"/>: columns
    /// advance rightward from a left header, and time advances along Y in whichever
    /// direction <see cref="TimeDirection"/> selects, so the vertical surface stays
    /// synchronized with the horizontal DAW either way.
    /// </summary>
    /// <remarks>
    /// This class is the single place the flip is implemented. Everything that positions
    /// anything on the vertical surface — items, the measure grid, the gutter labels, the
    /// playhead, hit testing — goes through <see cref="TickToY"/> and
    /// <see cref="YToTick"/>, so no caller has to know which way time runs and on-screen
    /// geometry cannot drift from hit testing.
    /// </remarks>
    public sealed class VerticalCoordinateSystem
    {
        /// <summary>
        /// Downward-time constructor, kept so existing callers and coordinate tests read
        /// unchanged.
        /// </summary>
        public VerticalCoordinateSystem(
            double pixelsPerTick,
            double columnScale,
            int headerWidth,
            int rulerHeight,
            float dpiScale)
            : this(
                pixelsPerTick,
                columnScale,
                headerWidth,
                rulerHeight,
                dpiScale,
                VerticalTimeDirection.Downward,
                0)
        {
        }

        /// <param name="bodyHeight">
        /// Height in <em>device</em> pixels of the canvas below the ruler. Upward time is
        /// measured from the bottom of that body, so unlike the header and ruler this value
        /// is a viewport measurement and is deliberately not DPI-scaled again.
        /// </param>
        public VerticalCoordinateSystem(
            double pixelsPerTick,
            double columnScale,
            int headerWidth,
            int rulerHeight,
            float dpiScale,
            VerticalTimeDirection timeDirection,
            int bodyHeight)
        {
            if (pixelsPerTick <= 0) throw new ArgumentOutOfRangeException("pixelsPerTick");
            if (columnScale <= 0) throw new ArgumentOutOfRangeException("columnScale");
            if (headerWidth < 0) throw new ArgumentOutOfRangeException("headerWidth");
            if (rulerHeight < 0) throw new ArgumentOutOfRangeException("rulerHeight");
            if (dpiScale <= 0) throw new ArgumentOutOfRangeException("dpiScale");
            if (bodyHeight < 0) throw new ArgumentOutOfRangeException("bodyHeight");

            DpiScale = dpiScale;
            PixelsPerTick = pixelsPerTick * dpiScale;
            ColumnScale = columnScale * dpiScale;
            HeaderWidth = ScaleToDevice(headerWidth, dpiScale);
            RulerHeight = ScaleToDevice(rulerHeight, dpiScale);
            TimeDirection = timeDirection;
            BodyHeight = bodyHeight;
        }

        /// <summary>Device pixels advanced per tick along the vertical (time) axis.</summary>
        public double PixelsPerTick { get; private set; }

        /// <summary>Device pixels per native ptSequencer column-width unit.</summary>
        public double ColumnScale { get; private set; }

        /// <summary>Left gutter (device px) reserved for column labels; offsets the X axis.</summary>
        public int HeaderWidth { get; private set; }

        /// <summary>Top gutter (device px) reserved for the time ruler; offsets the Y axis.</summary>
        public int RulerHeight { get; private set; }

        /// <summary>Which way later ticks go.</summary>
        public VerticalTimeDirection TimeDirection { get; private set; }

        /// <summary>Device-pixel height of the canvas below the ruler.</summary>
        public int BodyHeight { get; private set; }

        /// <summary>Y of the time origin: the ruler edge downward, the bottom edge upward.</summary>
        public int OriginY
        {
            get
            {
                return TimeDirection == VerticalTimeDirection.Upward
                    ? RulerHeight + BodyHeight
                    : RulerHeight;
            }
        }

        public float DpiScale { get; private set; }

        public double TickToY(double tick, double originTick)
        {
            double offset = (tick - originTick) * PixelsPerTick;
            return TimeDirection == VerticalTimeDirection.Upward
                ? OriginY - offset
                : OriginY + offset;
        }

        public int YToTick(double y, double originTick)
        {
            double offset = TimeDirection == VerticalTimeDirection.Upward
                ? OriginY - y
                : y - OriginY;
            return (int)Math.Round(
                originTick + (offset / PixelsPerTick),
                MidpointRounding.AwayFromZero);
        }

        public double NativeXToScreen(double nativeX, double originNativeX)
        {
            return HeaderWidth + ((nativeX - originNativeX) * ColumnScale);
        }

        public double ScreenXToNative(double x, double originNativeX)
        {
            return originNativeX + ((x - HeaderWidth) / ColumnScale);
        }

        public int ColumnWidthToScreen(int nativeWidth)
        {
            if (nativeWidth < 0) throw new ArgumentOutOfRangeException("nativeWidth");
            return (int)Math.Round(nativeWidth * ColumnScale, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Rounds a design-unit measurement to device pixels the same way this class does,
        /// so callers computing a body height can subtract the scaled ruler consistently.
        /// </summary>
        public static int ScaleToDevice(int value, float dpiScale)
        {
            return (int)Math.Round(value * dpiScale, MidpointRounding.AwayFromZero);
        }
    }
}
