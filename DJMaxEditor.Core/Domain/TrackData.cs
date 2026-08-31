using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DJMaxEditor.DJMax
{
    /// <summary>
    /// Tracks informations
    /// </summary>
    public class TrackData
    {
        /// <summary>
        /// The displayed track name
        /// </summary>
        public string DisplayedTrackName { get; set; }

        /// <summary>
        /// Actual volume on the track (not saved in the *.pt)
        /// </summary>
        public float Volume { get; set; }

        /// <summary>
        /// Events on this track, ordered by tick.
        /// <para>
        /// This used to hand back a deferred <c>OrderBy</c>, which meant every single
        /// enumeration re-sorted the whole track. The editor's paint loop enumerates this
        /// once per track per frame, so a dense chart re-sorted tens of thousands of
        /// events on every frame — the measured cause of the legacy timeline's lag. The
        /// ordering is now materialised once and cached until the track is mutated.
        /// </para>
        /// <para>
        /// Materialisation is lazy rather than eager because <see cref="AddEvent"/> is
        /// called in tight per-event parse loops; sorting on each add would turn chart
        /// loading into O(n² log n).
        /// </para>
        /// </summary>
        public IEnumerable<EventData> Events
        {
            get { return OrderedEvents; }
        }

        /// <summary>
        /// Same events as <see cref="Events"/>, indexable so a caller that only needs a
        /// tick window can binary-search instead of walking the whole track.
        /// </summary>
        public IReadOnlyList<EventData> OrderedEvents
        {
            get
            {
                if (m_orderedDirty)
                {
                    m_ordered = m_events.OrderBy(x => x.Tick).ToList();
                    m_orderedDirty = false;
                }
                return m_ordered;
            }
        }

        /// <summary>
        /// Track index
        /// </summary>
        public uint Idx { get; set; }

        public int MaxTick
        {
            get
            {
                if (m_maxTickDirty)
                {
                    _maxTick = m_events.Count == 0 ? 0 : m_events.Max(x => x.Tick);
                    m_maxTickDirty = false;
                }
                return _maxTick;
            }
        }

        /// <summary>
        /// Initialize trackData with an index
        /// </summary>
        /// <param name="idx"></param>
        public TrackData(uint idx)
        {
            // actually fixed limit for all tracks
            m_events = new List<EventData>();
            m_ordered = new List<EventData>();

            Idx = idx;

            Volume = 1;

            DisplayedTrackName = "Track " + idx;
        }

        /// <summary>
        /// Index of the first ordered event at or after <paramref name="tick"/>, or the
        /// event count when every event is earlier. Lets the renderers scan only the events
        /// a viewport can actually contain.
        /// </summary>
        public int FirstIndexAtOrAfterTick(int tick)
        {
            IReadOnlyList<EventData> ordered = OrderedEvents;
            int low = 0;
            int high = ordered.Count;
            while (low < high)
            {
                int middle = low + ((high - low) / 2);
                if (ordered[middle].Tick < tick)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            return low;
        }

        /// <summary>
        /// The track name
        /// </summary>
        public string TrackName
        {
            get
            {
                return m_trackName;
            }
            set
            {

                if (String.IsNullOrEmpty(value))
                {
                    DisplayedTrackName = String.Format("Track {0}", Idx);
                }
                else
                {
                    DisplayedTrackName = String.Format("Track {0} - {1}", Idx, value);
                }

                m_trackName = value;
            }
        }

        public event EventHandler EventAdded;

        public event EventHandler EventRemoved;

        /// <summary>
        /// Add an event to the TrackData
        /// </summary>
        /// <param name="eventData"></param>
        public void AddEvent(EventData eventData)
        {
            eventData.TrackId = Idx;
            m_events.Add(eventData);
            InvalidateDerivedState();
            TriggerEventAdded(eventData);
        }

        /// <summary>Add many events with one sort/notification. Used by large text chart imports.</summary>
        public void AddEvents(IEnumerable<EventData> eventData)
        {
            if (eventData == null) return;
            var additions = eventData.Where(x => x != null).ToList();
            if (additions.Count == 0) return;
            foreach (var item in additions) item.TrackId = Idx;
            m_events.AddRange(additions);
            InvalidateDerivedState();
            TriggerEventAdded(null);
        }

        /// <summary>
        /// Remove an event from the TrackData
        /// </summary>
        /// <param name="eventData"></param>
        public void RemoveEvent(EventData eventData)
        {
            m_events.Remove(eventData);
            InvalidateDerivedState();
            TriggerEventRemoved(eventData);
        }

        private string m_trackName = null;

        private List<EventData> m_events;

        private List<EventData> m_ordered;

        private bool m_orderedDirty = false;

        private bool m_maxTickDirty = false;

        private int _maxTick = 1;

        private void TriggerEventAdded(EventData eventData)
        {
            EventAdded?.Invoke(this, null);
        }

        private void TriggerEventRemoved(EventData eventData)
        {
            EventRemoved?.Invoke(this, null);
        }

        /// <summary>
        /// Marks the ordered view and the max tick stale. Both are recomputed on the next
        /// read, so a parse loop that adds thousands of events pays for one sort, not one
        /// sort per event.
        /// </summary>
        private void InvalidateDerivedState()
        {
            m_orderedDirty = true;
            m_maxTickDirty = true;
        }
    }
}
