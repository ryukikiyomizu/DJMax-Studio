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

        private int _videoStartVirtualTick;
        private double _videoStartMs;

        public BgaClockMap()
        {
            Clear();
        }

        /// <summary>
        /// Nudges the mapping on top of whatever the chart itself says, for a video whose first frame
        /// does not land on its own start marker. Positive values delay the video.
        /// </summary>
        public TimeSpan Offset { get; set; }

        /// <summary>
        /// The virtual tick the video's first frame belongs to: the chart's own
        /// <see cref="EventAttribute.VideoStart"/> marker, or 0 when it carries none.
        ///
        /// <para>
        /// A TECHNIKA chart does not start its video at the top of the timeline. It authors one
        /// attribute-100 marker - <c>EventAttribute.VideoStart</c>, commented in the enum as the
        /// "T2/T3 video track start" - and the video is cued there, so the frame belonging to a tick
        /// before it is no frame at all. Reading the playhead's absolute time and handing it to the
        /// decoder, which is what this did, plays the video early by exactly the marker's position:
        /// the whole BGA runs ahead of the chart for as long as the chart lasts.
        /// </para>
        ///
        /// <para>
        /// The marker is found by attribute rather than by track, and the earliest one wins. Across
        /// the 444-chart TECHNIKA 2 corpus every attribute-100 event is authored on track 17, one per
        /// chart - but the track band that carries it is not part of any preset, and a rule keyed to
        /// the attribute is the one the enum actually documents. A chart with no marker leaves this at
        /// 0, which is the behaviour every caller had before.
        /// </para>
        /// </summary>
        public int VideoStartVirtualTick
        {
            get { return _videoStartVirtualTick; }
        }

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

            // After the segments, because the marker's wall time is read through them.
            _videoStartVirtualTick = EarliestVideoStart(events);
            _videoStartMs = MsForVirtualTick(_videoStartVirtualTick);
        }

        /// <summary>
        /// The earliest <see cref="EventAttribute.VideoStart"/> marker's virtual tick, or 0 when the
        /// chart has none. See <see cref="VideoStartVirtualTick"/> for why the attribute and not the
        /// track is the discriminator.
        /// </summary>
        private static int EarliestVideoStart(EventData[] events)
        {
            int earliest = -1;
            for (int i = 0; i < events.Length; i++)
            {
                EventData candidate = events[i];
                if (candidate == null || candidate.EventType != EventType.Note ||
                    candidate.Attribute != (byte)EventAttribute.VideoStart)
                {
                    continue;
                }

                int tick = Math.Max(0, candidate.VirtualTick);
                if (earliest < 0 || tick < earliest)
                {
                    earliest = tick;
                }
            }
            return earliest < 0 ? 0 : earliest;
        }

        public void Clear()
        {
            _segments.Clear();
            _segments.Add(new Segment(0, MsPerVirtualTick(DefaultTempo), 0.0));
            _ticksPerMeasure = 192 * EventData.VirtualTickSize;
            _videoStartVirtualTick = 0;
            _videoStartMs = 0.0;
        }

        /// <summary>
        /// Wall time of <paramref name="virtualTick"/> in the video's own clock: measured from
        /// <see cref="VideoStartVirtualTick"/>, and including <see cref="Offset"/>. Negative before
        /// the chart's start marker, or when a negative offset is set; the source clamps.
        /// </summary>
        public TimeSpan TimeForVirtualTick(int virtualTick)
        {
            if (virtualTick < 0)
            {
                virtualTick = 0;
            }

            return TimeSpan.FromMilliseconds(MsForVirtualTick(virtualTick) - _videoStartMs) + Offset;
        }

        /// <summary>Chart time of a virtual tick, from the top of the timeline and before any
        /// offset - the raw tempo-map reading the video clock is measured against.</summary>
        private double MsForVirtualTick(int virtualTick)
        {
            Segment segment = _segments[IndexForTick(virtualTick)];
            return segment.StartMs +
                ((virtualTick - segment.StartVirtualTick) * segment.MsPerVirtualTick);
        }

        /// <summary>Same, for a native (non-virtual) tick as <see cref="Player"/> counts them.</summary>
        public TimeSpan TimeForTick(int tick)
        {
            return TimeForVirtualTick(tick * EventData.VirtualTickSize);
        }

        /// <summary>
        /// The inverse, for nudging <see cref="Offset"/> against a frame the user picked out of the
        /// video rather than out of the chart. Takes a position in the video's own clock, so a time of
        /// zero comes back as <see cref="VideoStartVirtualTick"/> rather than as tick 0.
        /// </summary>
        public int VirtualTickForTime(TimeSpan time)
        {
            double ms = (time - Offset).TotalMilliseconds + _videoStartMs;
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
