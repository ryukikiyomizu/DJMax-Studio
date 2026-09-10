using System;

namespace DJMaxEditor.Controls.TimelineV2
{
    /// <summary>
    /// Pure conversion between authoritative virtual ticks/row indexes and device pixels.
    /// </summary>
    public sealed class TimelineCoordinateSystem
    {
        public TimelineCoordinateSystem(
            double pixelsPerTick,
            int rowHeight,
            int headerWidth,
            int rulerHeight,
            float dpiScale)
        {
            if (pixelsPerTick <= 0) throw new ArgumentOutOfRangeException("pixelsPerTick");
            if (rowHeight <= 0) throw new ArgumentOutOfRangeException("rowHeight");
            if (headerWidth < 0) throw new ArgumentOutOfRangeException("headerWidth");
            if (rulerHeight < 0) throw new ArgumentOutOfRangeException("rulerHeight");
            if (dpiScale <= 0) throw new ArgumentOutOfRangeException("dpiScale");

            DpiScale = dpiScale;
            PixelsPerTick = pixelsPerTick * dpiScale;
            RowHeight = Scale(rowHeight, dpiScale);
            HeaderWidth = Scale(headerWidth, dpiScale);
            RulerHeight = Scale(rulerHeight, dpiScale);
        }

        public double PixelsPerTick { get; private set; }

        public int RowHeight { get; private set; }

        public int HeaderWidth { get; private set; }

        public int RulerHeight { get; private set; }

        public float DpiScale { get; private set; }

        public double TickToX(double tick, double originTick)
        {
            return HeaderWidth + ((tick - originTick) * PixelsPerTick);
        }

        public int XToTick(double x, double originTick)
        {
            return (int)Math.Round(
                originTick + ((x - HeaderWidth) / PixelsPerTick),
                MidpointRounding.AwayFromZero);
        }

        public int RowToY(int rowIndex, int firstVisibleRow)
        {
            return RulerHeight + ((rowIndex - firstVisibleRow) * RowHeight);
        }

        public int YToRow(double y, int firstVisibleRow)
        {
            if (y < RulerHeight)
            {
                return -1;
            }

            return firstVisibleRow + (int)Math.Floor((y - RulerHeight) / RowHeight);
        }

        private static int Scale(int value, float dpiScale)
        {
            return (int)Math.Round(value * dpiScale, MidpointRounding.AwayFromZero);
        }
    }
}
