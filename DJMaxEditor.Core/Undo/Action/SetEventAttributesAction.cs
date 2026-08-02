using System.Collections.Generic;
using DJMaxEditor.DJMax;

namespace DJMaxEditor.Undo.Action
{
    public sealed class SetEventAttributesAction : UndoRedoAction
    {
        public sealed class EventAttributeChange
        {
            public EventAttributeChange(EventData item, byte previous, byte next)
            {
                Item = item;
                Previous = previous;
                Next = next;
            }

            public EventData Item { get; private set; }
            public byte Previous { get; private set; }
            public byte Next { get; private set; }
        }

        private readonly List<EventAttributeChange> _changes;

        public SetEventAttributesAction(IEnumerable<EventAttributeChange> changes)
        {
            _changes = changes == null
                ? new List<EventAttributeChange>()
                : new List<EventAttributeChange>(changes);
            Cancel = _changes.Count == 0;
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
            foreach (EventAttributeChange change in _changes)
            {
                change.Item.Attribute = forward ? change.Next : change.Previous;
            }
        }
    }
}
