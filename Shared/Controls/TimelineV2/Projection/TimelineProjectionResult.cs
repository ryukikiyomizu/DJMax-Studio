using System.Collections.Generic;

namespace DJMaxEditor.Controls.TimelineV2.Projection
{
    public sealed class TimelineProjectionResult
    {
        public TimelineProjectionResult(
            IReadOnlyList<TimelineRow> rows,
            IReadOnlyList<TimelineItem> items)
        {
            Rows = rows;
            Items = items;
        }

        public IReadOnlyList<TimelineRow> Rows { get; private set; }

        public IReadOnlyList<TimelineItem> Items { get; private set; }
    }
}
