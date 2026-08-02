using System.Collections.Generic;
using DJMaxEditor.DJMax;
using DJMaxEditor.Undo.Action;

namespace DJMaxEditor.Editor
{
    /// <summary>
    /// Shared UI mutation boundary. It validates an entire logical operation before
    /// handing one undoable action to the existing UndoManager.
    /// </summary>
    public sealed class ChartEditController
    {
        private readonly EditorDocumentContext _document;
        private readonly UndoManager _undo;

        internal ChartEditController(EditorDocumentContext document, UndoManager undo)
        {
            _document = document;
            _undo = undo;
        }

        public bool MoveSelection(int trackDelta, int virtualTickDelta)
        {
            return MoveSelection(trackDelta, virtualTickDelta, null);
        }

        public bool MoveSelection(int trackDelta, int virtualTickDelta, object undoGroupKey)
        {
            if ((trackDelta == 0 && virtualTickDelta == 0) ||
                !CanMutateSelection())
            {
                return false;
            }

            var moves = new List<MoveEventsAction.EventMove>();
            foreach (EventData item in _document.Selection.Items)
            {
                long destinationTrack = (long)item.TrackId + trackDelta;
                long destinationTick = (long)item.VirtualTick + virtualTickDelta;
                if (destinationTrack < 0 ||
                    destinationTrack >= _document.Model.Tracks.Count ||
                    destinationTick < 0 ||
                    destinationTick > int.MaxValue)
                {
                    return false;
                }

                moves.Add(new MoveEventsAction.EventMove(
                    item,
                    item.TrackId,
                    item.VirtualTick,
                    (uint)destinationTrack,
                    (int)destinationTick));
            }

            _undo.ExecAction(new MoveEventsAction(_document.Model, moves, undoGroupKey));
            return true;
        }

        public bool DeleteSelection()
        {
            if (!CanMutateSelection())
            {
                return false;
            }

            var selected = new EventData[_document.Selection.Count];
            _document.Selection.Items.CopyTo(selected, 0);
            _undo.ExecAction(new RemoveEventAction(_document.Model, selected));
            _document.Selection.Clear();
            return true;
        }

        public EventData CreateEvent(EventData template, uint trackIndex, int virtualTick)
        {
            if (!_document.Capabilities.CanEdit ||
                template == null ||
                virtualTick < 0 ||
                trackIndex >= _document.Model.Tracks.Count)
            {
                return null;
            }

            var created = (EventData)template.Clone();
            created.TrackId = trackIndex;
            created.VirtualTick = virtualTick;
            if (!AddEvents(new[] { created }))
            {
                return null;
            }
            return created;
        }

        public bool ResizeSelection(int virtualDurationDelta)
        {
            return ResizeSelection(virtualDurationDelta, null);
        }

        public bool ResizeSelection(int virtualDurationDelta, object undoGroupKey)
        {
            if (virtualDurationDelta == 0 || !CanMutateSelection())
            {
                return false;
            }

            var resizes = new List<ResizeEventsAction.EventResize>();
            foreach (EventData item in _document.Selection.Items)
            {
                if (item.EventType != EventType.Note)
                {
                    return false;
                }

                long duration = (long)item.VirtualDuration + virtualDurationDelta;
                if (duration <= 0 || duration > ushort.MaxValue)
                {
                    return false;
                }
                resizes.Add(new ResizeEventsAction.EventResize(
                    item,
                    item.VirtualDuration,
                    (ushort)duration));
            }

            _undo.ExecAction(new ResizeEventsAction(resizes, undoGroupKey));
            return true;
        }

        public bool SetSelectionAttribute(byte attribute)
        {
            if (!CanMutateSelection())
            {
                return false;
            }

            var changes = new List<SetEventAttributesAction.EventAttributeChange>();
            foreach (EventData item in _document.Selection.Items)
            {
                if (item.Attribute == attribute)
                {
                    continue;
                }
                changes.Add(new SetEventAttributesAction.EventAttributeChange(
                    item,
                    item.Attribute,
                    attribute));
            }
            if (changes.Count == 0)
            {
                return false;
            }
            _undo.ExecAction(new SetEventAttributesAction(changes));
            return true;
        }

        internal bool AddEvents(IEnumerable<EventData> events)
        {
            if (!_document.Capabilities.CanEdit || events == null)
            {
                return false;
            }

            var additions = new List<EventData>();
            foreach (EventData item in events)
            {
                if (item == null ||
                    item.VirtualTick < 0 ||
                    item.TrackId >= _document.Model.Tracks.Count)
                {
                    return false;
                }
                additions.Add(item);
            }
            if (additions.Count == 0)
            {
                return false;
            }

            _undo.ExecAction(new AddEventAction(_document.Model, additions));
            _document.Selection.Replace(additions);
            return true;
        }

        private bool CanMutateSelection()
        {
            return _document.Capabilities.CanEdit &&
                _document.Selection.Count > 0;
        }
    }
}
