using System.Linq;
using DJMaxEditor.Controls.TimelineV2;

namespace DJMaxEditor.Tests
{
    internal static partial class Program
    {
        private static void RunTimelineRulerTests()
        {
            Test("TimelineRuler_ProducesMeasureAndBeatBoundaries", () =>
            {
                var marks = TimelineRulerCalculator.Build(
                    new TimelineTimeRange(0, 2400),
                    1152,
                    4,
                    0.5);

                AssertTrue(marks.Any(m => m.Tick == 0 && m.Kind == TimelineRulerMarkKind.Measure),
                    "measure zero is missing");
                AssertTrue(marks.Any(m => m.Tick == 1152 && m.Kind == TimelineRulerMarkKind.Measure),
                    "second measure is missing");
                AssertTrue(marks.Any(m => m.Tick == 288 && m.Kind == TimelineRulerMarkKind.Beat),
                    "beat boundary is missing");
                AssertTrue(marks.First(m => m.Tick == 1152).Label == "2",
                    "measure label is incorrect");
            });

            Test("TimelineRuler_RawFallback_DoesNotInventMeter", () =>
            {
                var marks = TimelineRulerCalculator.Build(
                    new TimelineTimeRange(100, 2100),
                    0,
                    0,
                    0.2);

                AssertTrue(marks.Count > 0, "raw fallback produced no navigation marks");
                AssertTrue(marks.All(m => m.Kind == TimelineRulerMarkKind.RawTick),
                    "raw fallback invented beat/measure semantics");
                AssertTrue(marks.All(m => m.Label.StartsWith("t")),
                    "raw fallback did not label authoritative ticks");
            });

            Test("TimelineRuler_LabelDensity_IsBoundedAtFarZoom", () =>
            {
                var marks = TimelineRulerCalculator.Build(
                    new TimelineTimeRange(0, 1152 * 200),
                    1152,
                    4,
                    0.005);
                int labels = marks.Count(m => !string.IsNullOrEmpty(m.Label));
                AssertTrue(labels < 40, "far zoom produced overlapping measure labels");
            });
        }
    }
}
