using System;

namespace DJMaxEditor.Editor
{
    public enum TimelineTool
    {
        Select,
        Draw,
        Erase,
        Resize,
        Pan,
        Audition
    }

    public enum TimelineInteractionKind
    {
        Idle,
        Hovering,
        MarqueeSelecting,
        Creating,
        MovingSelection,
        ResizingStart,
        ResizingEnd,
        Erasing,
        Panning,
        Auditioning,
        AutoScrolling,
        Cancelled
    }

    public sealed class TimelineInteractionAnchor
    {
        public TimelineInteractionAnchor(int virtualTick, int trackIndex)
        {
            VirtualTick = virtualTick;
            TrackIndex = trackIndex;
        }

        public int VirtualTick { get; private set; }
        public int TrackIndex { get; private set; }
    }

    /// <summary>
    /// Shared explicit gesture state. Rendering may preview this state, but model mutations
    /// are committed separately through ChartEditController.
    /// </summary>
    public sealed class TimelineInteractionState
    {
        private TimelineTool _tool = TimelineTool.Select;

        public event EventHandler ToolChanged;

        public TimelineTool Tool
        {
            get { return _tool; }
            set
            {
                if (_tool == value)
                {
                    return;
                }
                _tool = value;
                if (ToolChanged != null)
                {
                    ToolChanged(this, EventArgs.Empty);
                }
            }
        }

        public TimelineInteractionKind Kind { get; private set; } =
            TimelineInteractionKind.Idle;

        public TimelineInteractionAnchor Anchor { get; private set; }

        public bool HasPendingMutation
        {
            get
            {
                return Kind == TimelineInteractionKind.Creating ||
                    Kind == TimelineInteractionKind.MovingSelection ||
                    Kind == TimelineInteractionKind.ResizingStart ||
                    Kind == TimelineInteractionKind.ResizingEnd ||
                    Kind == TimelineInteractionKind.Erasing;
            }
        }

        public bool Begin(TimelineInteractionKind kind, TimelineInteractionAnchor anchor)
        {
            if (Kind != TimelineInteractionKind.Idle || kind == TimelineInteractionKind.Idle)
            {
                return false;
            }

            Kind = kind;
            Anchor = anchor;
            return true;
        }

        public bool Cancel()
        {
            if (Kind == TimelineInteractionKind.Idle)
            {
                Tool = TimelineTool.Select;
                Anchor = null;
                return false;
            }

            Kind = TimelineInteractionKind.Cancelled;
            Anchor = null;
            Tool = TimelineTool.Select;
            Kind = TimelineInteractionKind.Idle;
            return true;
        }

        public bool Complete()
        {
            if (Kind == TimelineInteractionKind.Idle)
            {
                return false;
            }

            Kind = TimelineInteractionKind.Idle;
            Anchor = null;
            return true;
        }
    }
}
