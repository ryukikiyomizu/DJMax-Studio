using System.Collections.Generic;
using DJMaxEditor.DJMax;

namespace DJMaxEditor.Undo.Action
{
    public sealed class ResizeEventsAction : UndoRedoAction
    {
        public sealed class EventResize
        {
            public EventResize(EventData item, ushort sourceDuration, ushort destinationDuration)
            {
                Item = item;
                SourceDuration = sourceDuration;
                DestinationDuration = destinationDuration;
            }

            public EventData Item { get; private set; }
            public ushort SourceDuration { get; private set; }
            public ushort DestinationDuration { get; private set; }

            internal void SetDestination(ushort destinationDuration)
            {
                DestinationDuration = destinationDuration;
            }
        }

        private readonly List<EventResize> _resizes;
        private readonly object _undoGroupKey;

        public ResizeEventsAction(IEnumerable<EventResize> resizes)
            : this(resizes, null)
        {
        }

        public ResizeEventsAction(
            IEnumerable<EventResize> resizes,
            object undoGroupKey)
        {
            _resizes = resizes == null
                ? new List<EventResize>()
                : new List<EventResize>(resizes);
            _undoGroupKey = undoGroupKey;
            Cancel = _resizes.Count == 0;
        }

        public override bool CanMerge(UndoRedoAction action)
        {
            var next = action as ResizeEventsAction;
            if (_undoGroupKey == null ||
                next == null ||
                !object.ReferenceEquals(_undoGroupKey, next._undoGroupKey) ||
                _resizes.Count != next._resizes.Count)
            {
                return false;
            }

            for (int i = 0; i < _resizes.Count; i++)
            {
                if (!object.ReferenceEquals(_resizes[i].Item, next._resizes[i].Item) ||
                    _resizes[i].DestinationDuration != next._resizes[i].SourceDuration)
                {
                    return false;
                }
            }
            return true;
        }

        public override void Merge(UndoRedoAction action)
        {
            var next = action as ResizeEventsAction;
            if (next == null)
            {
                return;
            }

            for (int i = 0; i < _resizes.Count; i++)
            {
                _resizes[i].SetDestination(next._resizes[i].DestinationDuration);
            }
        }

        public override void Undo()
        {
            Apply(false);
        }

        public override void Redo()
        {
            Apply(true);
        }

        private void Apply(bool forward)
        {
            foreach (EventResize resize in _resizes)
            {
                resize.Item.VirtualDuration = forward
                    ? resize.DestinationDuration
                    : resize.SourceDuration;
            }
        }
    }
}
