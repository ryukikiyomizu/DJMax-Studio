using System;

namespace DJMaxEditor.Controls.TimelineV2
{
    public struct TimelineRowRange
    {
        public TimelineRowRange(int firstRow, int lastRow)
        {
            FirstRow = Math.Min(firstRow, lastRow);
            LastRow = Math.Max(firstRow, lastRow);
        }

        public int FirstRow { get; private set; }

        public int LastRow { get; private set; }

        public bool Contains(int row)
        {
            return row >= FirstRow && row <= LastRow;
        }
    }
}
