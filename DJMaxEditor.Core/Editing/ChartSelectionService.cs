using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using DJMaxEditor.DJMax;

namespace DJMaxEditor.Editor
{
    /// <summary>
    /// Authoritative UI selection shared by every editor surface and the Inspector.
    /// Events are retained by object identity; no chart data is copied.
    /// </summary>
    public sealed class ChartSelectionService
    {
        private readonly List<EventData> _items = new List<EventData>();
        private readonly ReadOnlyCollection<EventData> _readOnlyItems;

        public ChartSelectionService()
        {
            _readOnlyItems = _items.AsReadOnly();
        }

        public event EventHandler SelectionChanged;

        public IList<EventData> Items
        {
            get { return _readOnlyItems; }
        }

        public int Count
        {
            get { return _items.Count; }
        }

        public void Replace(IEnumerable<EventData> events)
        {
            var replacement = new List<EventData>();
            if (events != null)
            {
                foreach (EventData item in events)
                {
                    if (item != null && !replacement.Contains(item))
                    {
                        replacement.Add(item);
                    }
                }
            }

            if (SameItems(replacement))
            {
                return;
            }

            _items.Clear();
            _items.AddRange(replacement);
            OnSelectionChanged();
        }

        public void Clear()
        {
            if (_items.Count == 0)
            {
                return;
            }

            _items.Clear();
            OnSelectionChanged();
        }

        private bool SameItems(IList<EventData> other)
        {
            if (other == null || other.Count != _items.Count)
            {
                return false;
            }

            for (int i = 0; i < _items.Count; i++)
            {
                if (!object.ReferenceEquals(_items[i], other[i]))
                {
                    return false;
                }
            }
            return true;
        }

        private void OnSelectionChanged()
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
