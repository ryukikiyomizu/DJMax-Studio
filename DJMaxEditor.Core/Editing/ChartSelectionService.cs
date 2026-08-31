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

        /// <summary>
        /// Identity index over <see cref="_items"/>. Two jobs: it makes <see cref="Contains"/>
        /// O(1) for renderers that ask once per visible note, and it de-duplicates
        /// <see cref="Replace"/> in linear time. The list used to dedup with
        /// <c>List.Contains</c>, which is quadratic — a marquee over a few thousand notes spent
        /// most of its time there.
        /// </summary>
        private readonly HashSet<EventData> _lookup =
            new HashSet<EventData>(EventIdentityComparer.Instance);

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

        /// <summary>
        /// Increments on every change. Surfaces cache a snapshot of the selection for painting
        /// and compare versions instead of contents, so a frame costs no allocation while the
        /// selection is unchanged.
        /// </summary>
        public int Version { get; private set; }

        /// <summary>True when this exact event instance is selected.</summary>
        public bool Contains(EventData item)
        {
            return item != null && _lookup.Contains(item);
        }

        public void Replace(IEnumerable<EventData> events)
        {
            var replacement = new List<EventData>();
            if (events != null)
            {
                var seen = new HashSet<EventData>(EventIdentityComparer.Instance);
                foreach (EventData item in events)
                {
                    if (item != null && seen.Add(item))
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
            _lookup.Clear();
            for (int i = 0; i < _items.Count; i++)
            {
                _lookup.Add(_items[i]);
            }
            OnSelectionChanged();
        }

        public void Clear()
        {
            if (_items.Count == 0)
            {
                return;
            }

            _items.Clear();
            _lookup.Clear();
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
            Version++;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Reference identity for chart events. <see cref="EventData"/> deliberately does not
        /// override equality — two notes can be value-identical and still be different notes —
        /// so the hash set has to be told to compare by instance rather than fall back to
        /// whatever a future <c>Equals</c> override might do.
        /// </summary>
        private sealed class EventIdentityComparer : IEqualityComparer<EventData>
        {
            internal static readonly EventIdentityComparer Instance =
                new EventIdentityComparer();

            public bool Equals(EventData x, EventData y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(EventData obj)
            {
                return obj == null
                    ? 0
                    : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
