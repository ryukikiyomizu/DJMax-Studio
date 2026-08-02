using System.Collections.Generic;
using System.Collections.ObjectModel;
using DJMaxEditor.DJMax;

namespace DJMaxEditor.Editor
{
    /// <summary>
    /// In-memory chart clipboard. It never serializes events or touches the system clipboard.
    /// </summary>
    public sealed class ChartClipboardService
    {
        private readonly EditorDocumentContext _document;
        private readonly List<EventData> _templates = new List<EventData>();
        private int _baseTick;

        internal ChartClipboardService(EditorDocumentContext document)
        {
            _document = document;
        }

        public bool HasEvents
        {
            get { return _templates.Count > 0; }
        }

        public bool CopySelection()
        {
            if (_document.Selection.Count == 0)
            {
                return false;
            }

            _templates.Clear();
            _baseTick = int.MaxValue;
            foreach (EventData selected in _document.Selection.Items)
            {
                var clone = (EventData)selected.Clone();
                _templates.Add(clone);
                if (clone.VirtualTick < _baseTick)
                {
                    _baseTick = clone.VirtualTick;
                }
            }
            return true;
        }

        public bool CutSelection()
        {
            return CopySelection() && _document.Edits.DeleteSelection();
        }

        public IList<EventData> DuplicateSelection(int virtualTickOffset)
        {
            if (_document.Selection.Count == 0 || virtualTickOffset <= 0)
            {
                return new ReadOnlyCollection<EventData>(new List<EventData>());
            }

            int earliestTick = int.MaxValue;
            foreach (EventData selected in _document.Selection.Items)
            {
                earliestTick = System.Math.Min(earliestTick, selected.VirtualTick);
            }

            if (!CopySelection())
            {
                return new ReadOnlyCollection<EventData>(new List<EventData>());
            }
            return PasteAt(earliestTick + virtualTickOffset);
        }

        public IList<EventData> PasteAt(int destinationVirtualTick)
        {
            if (!HasEvents || destinationVirtualTick < 0)
            {
                return new ReadOnlyCollection<EventData>(new List<EventData>());
            }

            var events = new List<EventData>();
            foreach (EventData template in _templates)
            {
                var clone = (EventData)template.Clone();
                long tick = (long)destinationVirtualTick +
                    (template.VirtualTick - _baseTick);
                if (tick < 0 || tick > int.MaxValue)
                {
                    return new ReadOnlyCollection<EventData>(new List<EventData>());
                }
                clone.VirtualTick = (int)tick;
                events.Add(clone);
            }

            if (!_document.Edits.AddEvents(events))
            {
                return new ReadOnlyCollection<EventData>(new List<EventData>());
            }
            return new ReadOnlyCollection<EventData>(events);
        }
    }
}
