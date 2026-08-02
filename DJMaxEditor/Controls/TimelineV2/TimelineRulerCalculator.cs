using System;
using System.Collections.Generic;

namespace DJMaxEditor.Controls.TimelineV2
{
    public enum TimelineRulerMarkKind
    {
        RawTick,
        Beat,
        Measure
    }

    public sealed class TimelineRulerMark
    {
        public TimelineRulerMark(int tick, TimelineRulerMarkKind kind, string label)
        {
            Tick = tick;
            Kind = kind;
            Label = label ?? string.Empty;
        }

        public int Tick { get; private set; }

        public TimelineRulerMarkKind Kind { get; private set; }

        public string Label { get; private set; }
    }

    public static class TimelineRulerCalculator
    {
        private const double MinimumLabelSpacing = 80;

        public static IReadOnlyList<TimelineRulerMark> Build(
            TimelineTimeRange range,
            int ticksPerMeasure,
            int beatsPerMeasure,
            double pixelsPerTick)
        {
            if (pixelsPerTick <= 0) throw new ArgumentOutOfRangeException("pixelsPerTick");

            return ticksPerMeasure > 0 && beatsPerMeasure > 0
                ? BuildMetered(range, ticksPerMeasure, beatsPerMeasure, pixelsPerTick)
                : BuildRaw(range, pixelsPerTick);
        }

        private static IReadOnlyList<TimelineRulerMark> BuildMetered(
            TimelineTimeRange range,
            int ticksPerMeasure,
            int beatsPerMeasure,
            double pixelsPerTick)
        {
            var marks = new List<TimelineRulerMark>();
            double beatTicks = (double)ticksPerMeasure / beatsPerMeasure;
            int firstBeat = (int)Math.Floor(range.StartTick / beatTicks);
            int lastBeat = (int)Math.Ceiling(range.EndTick / beatTicks);
            int labelEveryMeasures = Math.Max(
                1,
                (int)Math.Ceiling(MinimumLabelSpacing / (ticksPerMeasure * pixelsPerTick)));

            for (int beatIndex = firstBeat; beatIndex <= lastBeat; beatIndex++)
            {
                int tick = (int)Math.Round(beatIndex * beatTicks, MidpointRounding.AwayFromZero);
                if (tick < range.StartTick || tick > range.EndTick)
                {
                    continue;
                }

                bool isMeasure = Mod(beatIndex, beatsPerMeasure) == 0;
                int measureIndex = FloorDiv(beatIndex, beatsPerMeasure);
                string label = isMeasure && Mod(measureIndex, labelEveryMeasures) == 0
                    ? (measureIndex + 1).ToString()
                    : string.Empty;
                marks.Add(new TimelineRulerMark(
                    tick,
                    isMeasure ? TimelineRulerMarkKind.Measure : TimelineRulerMarkKind.Beat,
                    label));
            }

            return marks;
        }

        private static IReadOnlyList<TimelineRulerMark> BuildRaw(
            TimelineTimeRange range,
            double pixelsPerTick)
        {
            var marks = new List<TimelineRulerMark>();
            double desiredTicks = MinimumLabelSpacing / pixelsPerTick;
            int step = NiceStep(desiredTicks);
            int first = (int)Math.Floor(range.StartTick / step) * step;

            for (int tick = first; tick <= range.EndTick; tick += step)
            {
                if (tick < range.StartTick)
                {
                    continue;
                }
                marks.Add(new TimelineRulerMark(tick, TimelineRulerMarkKind.RawTick, "t" + tick));
            }

            return marks;
        }

        private static int NiceStep(double value)
        {
            if (value <= 1) return 1;
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
            double normalized = value / magnitude;
            double nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
            return Math.Max(1, (int)Math.Ceiling(nice * magnitude));
        }

        private static int Mod(int value, int divisor)
        {
            int result = value % divisor;
            return result < 0 ? result + divisor : result;
        }

        private static int FloorDiv(int value, int divisor)
        {
            int quotient = value / divisor;
            int remainder = value % divisor;
            return remainder < 0 ? quotient - 1 : quotient;
        }
    }
}
