using System;
using System.Collections.Generic;
using System.Linq;

namespace DJMaxEditor.Controls.TimelineV2
{
    /// <summary>
    /// Row-partitioned interval index shared by paint and pointer queries. Each row stores a
    /// prefix-maximum end tick so long events that begin before the viewport remain discoverable.
    /// </summary>
    public sealed class TimelineEventIndex
    {
        private readonly Dictionary<int, RowBucket> _rows;

        public TimelineEventIndex(
            IEnumerable<TimelineItem> items,
            double maxOverscanTicks,
            int maxOverscanRows = 4)
        {
            MaxOverscanTicks = Math.Max(0, maxOverscanTicks);
            MaxOverscanRows = Math.Max(0, maxOverscanRows);
            _rows = (items ?? Enumerable.Empty<TimelineItem>())
                .GroupBy(item => item.RowIndex)
                .ToDictionary(group => group.Key, group => new RowBucket(group));
        }

        public double MaxOverscanTicks { get; private set; }

        public int MaxOverscanRows { get; private set; }

        public int ItemCount
        {
            get { return _rows.Values.Sum(row => row.Items.Length); }
        }

        public int LastQueryCandidateCount { get; private set; }

        public int LastQueryRowCount { get; private set; }

        public IReadOnlyList<TimelineItem> Query(
            TimelineTimeRange timeRange,
            TimelineRowRange rowRange,
            double overscanTicks = 0,
            int overscanRows = 0)
        {
            double boundedTickOverscan = Math.Min(Math.Abs(overscanTicks), MaxOverscanTicks);
            int boundedRowOverscan = Math.Min(Math.Abs(overscanRows), MaxOverscanRows);
            double startTick = timeRange.StartTick - boundedTickOverscan;
            double endTick = timeRange.EndTick + boundedTickOverscan;
            int firstRow = rowRange.FirstRow - boundedRowOverscan;
            int lastRow = rowRange.LastRow + boundedRowOverscan;

            var result = new List<TimelineItem>();
            LastQueryCandidateCount = 0;
            LastQueryRowCount = 0;

            for (int rowIndex = firstRow; rowIndex <= lastRow; rowIndex++)
            {
                RowBucket row;
                if (!_rows.TryGetValue(rowIndex, out row))
                {
                    continue;
                }

                LastQueryRowCount++;
                int firstCandidate = row.FindFirstPotentialOverlap(startTick);
                for (int i = firstCandidate; i < row.Items.Length; i++)
                {
                    TimelineItem item = row.Items[i];
                    if (item.StartTick > endTick)
                    {
                        break;
                    }

                    LastQueryCandidateCount++;
                    if (item.EndTick >= startTick)
                    {
                        result.Add(item);
                    }
                }
            }

            return result;
        }

        private sealed class RowBucket
        {
            public RowBucket(IEnumerable<TimelineItem> items)
            {
                Items = items
                    .OrderBy(item => item.StartTick)
                    .ThenBy(item => item.EndTick)
                    .ToArray();
                PrefixMaximumEnd = new int[Items.Length];

                int maximum = Int32.MinValue;
                for (int i = 0; i < Items.Length; i++)
                {
                    maximum = Math.Max(maximum, Items[i].EndTick);
                    PrefixMaximumEnd[i] = maximum;
                }
            }

            public TimelineItem[] Items { get; private set; }

            private int[] PrefixMaximumEnd { get; set; }

            public int FindFirstPotentialOverlap(double startTick)
            {
                int low = 0;
                int high = PrefixMaximumEnd.Length;
                while (low < high)
                {
                    int middle = low + ((high - low) / 2);
                    if (PrefixMaximumEnd[middle] >= startTick)
                    {
                        high = middle;
                    }
                    else
                    {
                        low = middle + 1;
                    }
                }
                return low;
            }
        }
    }
}
