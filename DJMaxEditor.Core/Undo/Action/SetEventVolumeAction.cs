using System.Collections.Generic;
using DJMaxEditor.DJMax;

namespace DJMaxEditor.Undo.Action
{
    /// <summary>
    /// Changes per-note volume (<see cref="EventData.Vel"/>) on a set of events.
    /// <para>
    /// The field has always round-tripped through the .pt writer, it was simply never editable:
    /// every note was written at its loaded velocity and new notes at the template's. This is the
    /// undo half of making it a first-class property, and it is deliberately a batch action so
    /// setting the volume of a 300-note selection is one undo step, not three hundred.
    /// </para>
    /// <para>
    /// Like <see cref="ResizeEventsAction"/> it can merge with later actions sharing the same
    /// group key, so dragging a volume slider or holding a nudge shortcut collapses into a single
    /// undo entry instead of one per pixel or keypress.
    /// </para>
    /// </summary>
    public sealed class SetEventVolumeAction : UndoRedoAction
    {
        public sealed class EventVolumeChange
        {
            public EventVolumeChange(EventData item, byte previous, byte next)
            {
                Item = item;
                Previous = previous;
                Next = next;
            }

            public EventData Item { get; private set; }
            public byte Previous { get; private set; }
            public byte Next { get; private set; }

            internal void SetNext(byte next)
            {
                Next = next;
            }
        }

        private readonly List<EventVolumeChange> _changes;
        private readonly object _undoGroupKey;

        public SetEventVolumeAction(IEnumerable<EventVolumeChange> changes)
            : this(changes, null)
        {
        }

        public SetEventVolumeAction(
            IEnumerable<EventVolumeChange> changes,
            object undoGroupKey)
        {
            _changes = changes == null
                ? new List<EventVolumeChange>()
                : new List<EventVolumeChange>(changes);
            _undoGroupKey = undoGroupKey;
            Cancel = _changes.Count == 0;
        }

        public override bool CanMerge(UndoRedoAction action)
        {
            var next = action as SetEventVolumeAction;
            if (_undoGroupKey == null ||
                next == null ||
                !object.ReferenceEquals(_undoGroupKey, next._undoGroupKey) ||
                _changes.Count != next._changes.Count)
            {
                return false;
            }

            for (int i = 0; i < _changes.Count; i++)
            {
                if (!object.ReferenceEquals(_changes[i].Item, next._changes[i].Item) ||
                    _changes[i].Next != next._changes[i].Previous)
                {
                    return false;
                }
            }
            return true;
        }

        public override void Merge(UndoRedoAction action)
        {
            var next = action as SetEventVolumeAction;
            if (next == null)
            {
                return;
            }

            for (int i = 0; i < _changes.Count; i++)
            {
                _changes[i].SetNext(next._changes[i].Next);
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
            foreach (EventVolumeChange change in _changes)
            {
                change.Item.Vel = forward ? change.Next : change.Previous;
            }
        }
    }
}
