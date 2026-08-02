using System.Collections.Generic;
using DJMaxEditor.Controls.TimelineV2;
using DJMaxEditor.DJMax;

namespace DJMaxEditor.Tests
{
    internal static partial class Program
    {
        private static void RunTimelineEventIndexTests()
        {
            Test("TimelineIndex_EmptyQuery_IsSafe", () =>
            {
                var index = new TimelineEventIndex(new TimelineItem[0], 96);
                var result = index.Query(
                    new TimelineTimeRange(0, 100),
                    new TimelineRowRange(0, 5),
                    20,
                    1);
                AssertTrue(result.Count == 0, "empty index returned items");
            });

            Test("TimelineIndex_IncludesBoundariesAndDurationOverlap", () =>
            {
                var a = Item(0, 10, 10);
                var hold = Item(0, 20, 100);
                var b = Item(0, 120, 120);
                var index = new TimelineEventIndex(new[] { a, hold, b }, 96);

                var atBoundary = index.Query(
                    new TimelineTimeRange(10, 10),
                    new TimelineRowRange(0, 0));
                AssertTrue(atBoundary.Count == 1 && object.ReferenceEquals(a, atBoundary[0]),
                    "event at exact boundary was missed");

                var overlap = index.Query(
                    new TimelineTimeRange(80, 90),
                    new TimelineRowRange(0, 0));
                AssertTrue(overlap.Count == 1 && object.ReferenceEquals(hold, overlap[0]),
                    "hold beginning before the viewport was missed");
            });

            Test("TimelineIndex_FiltersRowsAndOrdersDeterministically", () =>
            {
                var items = new[]
                {
                    Item(2, 50, 50),
                    Item(1, 70, 70),
                    Item(1, 20, 20),
                    Item(0, 10, 10)
                };
                var index = new TimelineEventIndex(items, 96);
                var result = index.Query(
                    new TimelineTimeRange(0, 100),
                    new TimelineRowRange(1, 2));

                AssertTrue(result.Count == 3, "row filter returned the wrong count");
                AssertTrue(result[0].RowIndex == 1 && result[0].StartTick == 20,
                    "first item order is unstable");
                AssertTrue(result[1].RowIndex == 1 && result[1].StartTick == 70,
                    "within-row order is unstable");
                AssertTrue(result[2].RowIndex == 2, "row order is unstable");
            });

            Test("TimelineIndex_ClampsOverscan", () =>
            {
                var index = new TimelineEventIndex(
                    new[] { Item(0, -97, -97), Item(0, -96, -96), Item(0, 196, 196), Item(0, 197, 197) },
                    96);
                var result = index.Query(
                    new TimelineTimeRange(0, 100),
                    new TimelineRowRange(0, 0),
                    100000,
                    100000);

                AssertTrue(result.Count == 2, "overscan escaped its configured bound");
            });

            Test("TimelineIndex_NarrowQuery_DoesNotScanTenThousandItems", () =>
            {
                var items = new List<TimelineItem>();
                for (int i = 0; i < 10000; i++)
                {
                    items.Add(Item(0, i * 10, i * 10));
                }

                var index = new TimelineEventIndex(items, 100);
                var result = index.Query(
                    new TimelineTimeRange(50000, 50100),
                    new TimelineRowRange(0, 0),
                    20,
                    0);

                AssertTrue(result.Count < 20, "narrow query returned too much data");
                AssertTrue(index.LastQueryCandidateCount < 100,
                    "narrow query scanned the full or a large fraction of the chart");
            });

            Test("TimelineIndex_FiftyThousandItems_RemainsBounded", () =>
            {
                var items = new List<TimelineItem>();
                for (int row = 0; row < 50; row++)
                {
                    for (int i = 0; i < 1000; i++)
                    {
                        items.Add(Item(row, i * 24, i * 24));
                    }
                }

                var index = new TimelineEventIndex(items, 192);
                var result = index.Query(
                    new TimelineTimeRange(10000, 10200),
                    new TimelineRowRange(20, 24),
                    48,
                    1);

                AssertTrue(result.Count < 100, "50k query was not visibly bounded");
                AssertTrue(index.LastQueryRowCount == 7, "row overscan was not bounded");
                AssertTrue(index.LastQueryCandidateCount < 250,
                    "50k query scanned too many candidates");
            });
        }

        private static TimelineItem Item(int row, int start, int end)
        {
            return new TimelineItem(
                row,
                start,
                end,
                new EventData { VirtualTick = start, EventType = EventType.Note });
        }
    }
}
