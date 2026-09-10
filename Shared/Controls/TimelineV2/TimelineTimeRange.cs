using System;

namespace DJMaxEditor.Controls.TimelineV2
{
    public struct TimelineTimeRange
    {
        public TimelineTimeRange(double firstTick, double secondTick)
        {
            StartTick = Math.Min(firstTick, secondTick);
            EndTick = Math.Max(firstTick, secondTick);
        }

        public double StartTick { get; private set; }

        public double EndTick { get; private set; }

        public double Length
        {
            get { return EndTick - StartTick; }
        }

        public bool Contains(double tick)
        {
            return tick >= StartTick && tick <= EndTick;
        }

        public bool Intersects(double firstTick, double secondTick)
        {
            double start = Math.Min(firstTick, secondTick);
            double end = Math.Max(firstTick, secondTick);
            return start <= EndTick && end >= StartTick;
        }
    }
}
