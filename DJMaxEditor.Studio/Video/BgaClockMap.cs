using System;
using System.Collections.Generic;
using System.Linq;
using DJMaxEditor.DJMax;

namespace DJMaxEditor.Studio.Video
{
    /// <summary>
    /// Chart tick to wall time, so the shell can hand the playhead's position straight to
    /// <see cref="BgaPreview.Seek"/>.
    /// <para>
    /// This is <em>not</em> a wrapper over <see cref="Player"/>, and that is worth stating because it
    /// looks like it should be. <c>Player.GetCurrentMsTime</c> returns <c>m_curMsTime</c>, which
    /// <c>UpdateTimer</c> builds by accumulating wall-clock intervals from <c>CustomTimer</c> as
    /// playback runs - it answers "how long has this take been going", not "what time is tick N",
    /// and it is meaningless while stopped or scrubbing. There is no tick-to-time function on
    /// <see cref="Player"/> to reuse.
    /// </para>
    /// <para>
    /// What <em>is</em> reused is the one number that matters: <c>Player.SetTempo</c> computes
    /// <c>m_period = 60000 / (tempo * 48)</c> milliseconds per native tick, so the sequencer treats
    /// a beat as 48 native ticks regardless of <see cref="PlayerData.TickPerMinute"/>. The same
    /// divisor is used below rather than deriving one from <c>TickPerMinute</c>, because a BGA that
    /// disagreed with the sequencer's clock by even a fraction of a percent would visibly drift
    /// against the audio over a three minute chart. If <c>Player</c>'s period ever changes, this
    /// changes with it.
    /// </para>
    /// </summary>
    public sealed class BgaClockMap
    {
        /// <summary>
        /// <c>Player.SetTempo</c>'s hard-coded native ticks per beat. Not <c>TickPerMinute / 4</c>:
        /// the sequencer does not consult <c>TickPerMinute</c> at all when timing, so neither does
        /// this.
        /// </summary>
        private const double PlayerTicksPerBeat = 48.0;

        private const double DefaultTempo = 120.0;

        private readonly List<Segment> _segments = new List<Segment>();

        private int _ticksPerMeasure;

        public BgaClockMap()
        {
            Clear();
        }

        /// <summary>
        /// Shifts the whole mapping, for a BGA whose first frame does not land on bar 1. Positive
        /// values delay the video.
        /// </summary>
        public TimeSpan Offset { get; set; }

        /// <summary>
        /// Virtual ticks in one measure. <see cref="PlayerData.TickPerMinute"/> is badly named - it
        /// is the measure resolution, not a rate - so this is that value lifted into the
        /// authoritative virtual-tick space by <see cref="EventData.VirtualTickSize"/>.
        /// </summary>
        public int TicksPerMeasure
        {
            get { return _ticksPerMeasure; }
        }

        /// <summary>Number of tempo segments; 1 when the chart has a single constant tempo.</summary>
        public int SegmentCount
        {
            get { return _segments.Count; }
        }

        public void Load(PlayerData playerData)
        {
            Clear();

            if (playerData == null)
            {
                return;
            }

            _ticksPerMeasure = playerData.TickPerMinute * EventData.VirtualTickSize;

            double tempo = playerData.Tempo > 0.0f ? playerData.Tempo : DefaultTempo;
            _segments[0] = new Segment(0, MsPerVirtualTick(tempo), 0.0);

            EventData[] events = playerData.Tracks == null ? null : playerData.Tracks.Events;
            if (events == null)
            {
                return;
            }

            // TracksList.Events is ordered by native Tick, which is VirtualTick / 6 - so two tempo
            // changes inside one native tick can arrive in the wrong virtual order. There are only
            // ever a handful of tempo events, so sorting them properly costs nothing.
            IEnumerable<EventData> tempoEvents = events
                .Where(e => e != null && e.EventType == EventType.Tempo)
                .OrderBy(e => e.VirtualTick);

            foreach (EventData tempoEvent in tempoEvents)
            {
                int tick = Math.Max(0, tempoEvent.VirtualTick);
                double next = tempoEvent.Tempo > 0.0f ? tempoEvent.Tempo : tempo;
                tempo = next;

                Segment last = _segments[_segments.Count - 1];
                if (tick <= last.StartVirtualTick)
                {
                    // A tempo event on the very first tick replaces the header tempo, which is what
                    // Player does: it applies any event whose tick has been reached, and every PT
                    // chart carries a tempo event at tick 0.
                    _segments[_segments.Count - 1] =
                        new Segment(last.StartVirtualTick, MsPerVirtualTick(tempo), last.StartMs);
                    continue;
                }

                double startMs = last.StartMs + ((tick - last.StartVirtualTick) * last.MsPerVirtualTick);
                _segments.Add(new Segment(tick, MsPerVirtualTick(tempo), startMs));
            }
        }

        public void Clear()
        {
            _segments.Clear();
            _segments.Add(new Segment(0, MsPerVirtualTick(DefaultTempo), 0.0));
            _ticksPerMeasure = 192 * EventData.VirtualTickSize;
        }

        /// <summary>
        /// Wall time of <paramref name="virtualTick"/>, including <see cref="Offset"/>. May be
        /// negative when a negative offset is set; the source clamps.
        /// </summary>
        public TimeSpan TimeForVirtualTick(int virtualTick)
        {
            if (virtualTick < 0)
            {
                virtualTick = 0;
            }

            Segment segment = _segments[IndexForTick(virtualTick)];
            double ms = segment.StartMs +
                ((virtualTick - segment.StartVirtualTick) * segment.MsPerVirtualTick);
            return TimeSpan.FromMilliseconds(ms) + Offset;
        }

        /// <summary>Same, for a native (non-virtual) tick as <see cref="Player"/> counts them.</summary>
        public TimeSpan TimeForTick(int tick)
        {
            return TimeForVirtualTick(tick * EventData.VirtualTickSize);
        }

        /// <summary>
        /// The inverse, for nudging <see cref="Offset"/> against a frame the user picked out of the
        /// video rather than out of the chart.
        /// </summary>
        public int VirtualTickForTime(TimeSpan time)
        {
            double ms = (time - Offset).TotalMilliseconds;
            if (ms <= 0.0)
            {
                return 0;
            }

            int index = IndexForMs(ms);
            Segment segment = _segments[index];
            if (segment.MsPerVirtualTick <= 0.0)
            {
                return segment.StartVirtualTick;
            }

            double ticks = segment.StartVirtualTick + ((ms - segment.StartMs) / segment.MsPerVirtualTick);
            if (ticks >= int.MaxValue)
            {
                return int.MaxValue;
            }
            return (int)Math.Round(ticks);
        }

        private int IndexForTick(int virtualTick)
        {
            int low = 0;
            int high = _segments.Count - 1;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (_segments[mid].StartVirtualTick <= virtualTick)
                {
                    low = mid;
                }
                else
                {
                    high = mid - 1;
                }
            }
            return low;
        }

        private int IndexForMs(double ms)
        {
            int low = 0;
            int high = _segments.Count - 1;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (_segments[mid].StartMs <= ms)
                {
                    low = mid;
                }
                else
                {
                    high = mid - 1;
                }
            }
            return low;
        }

        private static double MsPerVirtualTick(double tempo)
        {
            if (tempo <= 0.0)
            {
                tempo = DefaultTempo;
            }
            return 60000.0 / (tempo * PlayerTicksPerBeat * EventData.VirtualTickSize);
        }

        private struct Segment
        {
            public Segment(int startVirtualTick, double msPerVirtualTick, double startMs)
            {
                StartVirtualTick = startVirtualTick;
                MsPerVirtualTick = msPerVirtualTick;
                StartMs = startMs;
            }

            public readonly int StartVirtualTick;
            public readonly double MsPerVirtualTick;
            public readonly double StartMs;
        }
    }
}
