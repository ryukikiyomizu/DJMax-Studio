using System.Collections.Generic;
using DJMaxEditor.DJMax;

namespace DJMaxEditor.Undo.Action
{
    /// <summary>
    /// Moves a prevalidated event group as one undo entry.
    /// </summary>
    public sealed class MoveEventsAction : UndoRedoAction
    {
        public sealed class EventMove
        {
            public EventMove(
                EventData item,
                uint sourceTrack,
                int sourceTick,
                uint destinationTrack,
                int destinationTick)
            {
                Item = item;
                SourceTrack = sourceTrack;
                SourceTick = sourceTick;
                DestinationTrack = destinationTrack;
                DestinationTick = destinationTick;
            }

            public EventData Item { get; private set; }
            public uint SourceTrack { get; private set; }
            public int SourceTick { get; private set; }
            public uint DestinationTrack { get; private set; }
            public int DestinationTick { get; private set; }

            internal void SetDestination(uint destinationTrack, int destinationTick)
            {
                DestinationTrack = destinationTrack;
                DestinationTick = destinationTick;
            }
        }

        private readonly PlayerData _model;
        private readonly List<EventMove> _moves;
        private readonly object _undoGroupKey;

        public MoveEventsAction(PlayerData model, IEnumerable<EventMove> moves)
            : this(model, moves, null)
        {
        }

        public MoveEventsAction(
            PlayerData model,
            IEnumerable<EventMove> moves,
            object undoGroupKey)
        {
            _model = model;
            _moves = moves == null
                ? new List<EventMove>()
                : new List<EventMove>(moves);
            _undoGroupKey = undoGroupKey;
            Cancel = _model == null || _moves.Count == 0;
        }

        public override bool CanMerge(UndoRedoAction action)
        {
            var next = action as MoveEventsAction;
            if (_undoGroupKey == null ||
                next == null ||
                !object.ReferenceEquals(_undoGroupKey, next._undoGroupKey) ||
                _moves.Count != next._moves.Count)
            {
                return false;
            }

            for (int i = 0; i < _moves.Count; i++)
            {
                if (!object.ReferenceEquals(_moves[i].Item, next._moves[i].Item) ||
                    _moves[i].DestinationTrack != next._moves[i].SourceTrack ||
                    _moves[i].DestinationTick != next._moves[i].SourceTick)
                {
                    return false;
                }
            }
            return true;
        }

        public override void Merge(UndoRedoAction action)
        {
            var next = action as MoveEventsAction;
            if (next == null)
            {
                return;
            }

            for (int i = 0; i < _moves.Count; i++)
            {
                _moves[i].SetDestination(
                    next._moves[i].DestinationTrack,
                    next._moves[i].DestinationTick);
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
            foreach (EventMove move in _moves)
            {
                TrackData current = _model.Tracks.GetTrackForEvent(move.Item);
                current?.RemoveEvent(move.Item);
            }

            foreach (EventMove move in _moves)
            {
                uint track = forward ? move.DestinationTrack : move.SourceTrack;
                int tick = forward ? move.DestinationTick : move.SourceTick;
                move.Item.VirtualTick = tick;
                _model.Tracks.GetTrackAtIndex(track).AddEvent(move.Item);
            }
        }
    }
}
