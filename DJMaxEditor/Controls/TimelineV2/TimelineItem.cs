using System;
using DJMaxEditor.DJMax;

namespace DJMaxEditor.Controls.TimelineV2
{
    public sealed class TimelineItem
    {
        public TimelineItem(int rowIndex, int firstTick, int secondTick, EventData sourceEvent)
        {
            if (sourceEvent == null) throw new ArgumentNullException("sourceEvent");

            RowIndex = rowIndex;
            StartTick = Math.Min(firstTick, secondTick);
            EndTick = Math.Max(firstTick, secondTick);
            SourceEvent = sourceEvent;
        }

        public int RowIndex { get; private set; }

        public int StartTick { get; private set; }

        public int EndTick { get; private set; }

        public EventData SourceEvent { get; private set; }

        public bool IsUnknown
        {
            get
            {
                return SourceEvent.EventType == EventType.None ||
                    !Enum.IsDefined(typeof(EventType), SourceEvent.EventType);
            }
        }

        public bool Intersects(double startTick, double endTick)
        {
            return StartTick <= endTick && EndTick >= startTick;
        }
    }
}
