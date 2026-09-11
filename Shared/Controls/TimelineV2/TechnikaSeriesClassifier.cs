using System.Collections.Generic;
using DJMaxEditor.DJMax;

namespace DJMaxEditor.Controls.TimelineV2
{
    /// <summary>
    /// One joined group: a chain path or a repeat series, in chart order, with the kind its
    /// connector line must paint in.
    /// </summary>
    internal sealed class TechnikaSeriesRun
    {
        public TechnikaSeriesRun(TechnikaNoteKind lineKind)
        {
            LineKind = lineKind;
            Members = new List<EventData>();
        }

        /// <summary>
        /// The kind naming the run's family: <see cref="TechnikaNoteKind.ChainNode"/> for a chain
        /// (the line is the chain's yellow connector), <see cref="TechnikaNoteKind.Repeat"/> for a
        /// repeat series (the purple connector).
        /// </summary>
        public TechnikaNoteKind LineKind { get; private set; }

        /// <summary>Head first, close last, joints/ticks in between - chart order.</summary>
        public List<EventData> Members { get; private set; }

        public bool IsChain
        {
            get { return LineKind == TechnikaNoteKind.ChainNode; }
        }
    }

    /// <summary>
    /// The runs-aware view of one model: the painted kind of every playable-lane event, plus the
    /// chain and repeat runs that join them. Built together because the same passes derive both.
    /// </summary>
    internal sealed class TechnikaSeriesMap
    {
        public TechnikaSeriesMap()
        {
            Kinds = new Dictionary<EventData, TechnikaNoteKind>();
            Runs = new List<TechnikaSeriesRun>();
        }

        public Dictionary<EventData, TechnikaNoteKind> Kinds { get; private set; }

        public List<TechnikaSeriesRun> Runs { get; private set; }
    }

    /// <summary>
    /// Runs-aware classification for the chart's four playable lanes.
    ///
    /// <para>
    /// <see cref="TechnikaNoteClassifier.Classify"/> answers from one event in isolation, and the
    /// pt format only lets a single event say so much: every member of a repeat run carries the
    /// head's own attribute (10), and the joints a chain path runs through are ordinary taps
    /// (attribute 0) the format never re-tagged. An isolated classifier therefore draws every
    /// repeat member as a round repeat head and the absorbed chain joints as pink taps - the exact
    /// notes the arcade paints as a magenta repeat tick and a yellow chain dot.
    /// </para>
    ///
    /// <para>
    /// This mirrors the run passes the gameplay preview projector performs in
    /// <c>ApplyChainFixups</c> / <c>ApplyRepeatFixups</c>: a chain head opens a run that absorbs
    /// the taps up to its explicit closing node, and the first attribute-10 note of a repeat
    /// series is its head while every following one is a member until the attribute-11 close.
    /// The two surfaces must agree about what an event is, or editing a note and playing it back
    /// show two different charts. The same passes also record the runs themselves, which is what
    /// lets the timeline paint the arcade's yellow chain line and purple repeat line between the
    /// members instead of leaving a chain or repeat looking like a scatter of separate notes.
    /// </para>
    /// </summary>
    internal static class TechnikaSeriesClassifier
    {
        /// <summary>The playable lanes, in the projector's own order.</summary>
        private const int LaneCount = 4;

        /// <summary>
        /// Builds the kind every playable-lane note paints as, keyed by the model event itself so
        /// a frame's <see cref="TimelineItem.SourceEvent"/> is an exact reference hit. Non-lane
        /// events are deliberately absent: callers fall through to the single-event classifier.
        /// </summary>
        public static TechnikaSeriesMap Build(PlayerData model)
        {
            var map = new TechnikaSeriesMap();
            if (model == null || model.Tracks == null)
            {
                return map;
            }

            // Tick then lane, the same ordering the projector walks notes in.
            var notes = new List<SeriesNote>();
            for (int lane = 0; lane < LaneCount && lane < model.Tracks.Count; lane++)
            {
                TrackData track = model.Tracks.GetTrackAtIndex((uint)lane);
                if (track == null)
                {
                    continue;
                }

                foreach (EventData evt in track.Events)
                {
                    if (evt == null || evt.EventType != EventType.Note)
                    {
                        continue;
                    }
                    notes.Add(new SeriesNote(evt, lane, TechnikaNoteClassifier.Classify(evt)));
                }
            }
            notes.Sort(Compare);

            ApplyChainFixups(map, notes);
            ApplyRepeatFixups(map, notes);
            return map;
        }

        private static int Compare(SeriesNote a, SeriesNote b)
        {
            int tick = a.Event.Tick.CompareTo(b.Event.Tick);
            if (tick != 0)
            {
                return tick;
            }
            return a.Lane.CompareTo(b.Lane);
        }

        /// <summary>
        /// The chain head's marker opens the chain; ordinary taps strictly after it on the way to
        /// the explicit closing node are the joints the chain path scrapes through. Taps that sit
        /// on the closing node's own tick are a real tap in another lane, not joints.
        /// </summary>
        private static void ApplyChainFixups(
            TechnikaSeriesMap map, List<SeriesNote> notes)
        {
            Dictionary<EventData, TechnikaNoteKind> kinds = map.Kinds;
            bool open = false;
            EventData head = null;
            TechnikaSeriesRun run = null;
            List<SeriesNote> absorbed = new List<SeriesNote>();

            // Absorbed joints are tracked as their own list rather than by a flag on the note:
            // the same tick can hold a joint on one lane and a real tap on another, and only the
            // ones this very run absorbed may revert when it closes.

            foreach (SeriesNote note in notes)
            {
                TechnikaNoteKind kind = note.Kind;
                if (kind == TechnikaNoteKind.ChainHead)
                {
                    open = true;
                    head = note.Event;
                    absorbed.Clear();
                    run = new TechnikaSeriesRun(TechnikaNoteKind.ChainNode);
                    run.Members.Add(note.Event);
                    map.Runs.Add(run);
                    kinds[note.Event] = kind;
                    continue;
                }

                if (kind == TechnikaNoteKind.ChainNode)
                {
                    if (open)
                    {
                        // A tap sharing the closing node's tick is the node's own neighbour, not a
                        // joint - the projector hands those back before closing the run.
                        int closeTick = note.Event.Tick;
                        for (int i = absorbed.Count - 1; i >= 0; i--)
                        {
                            if (absorbed[i].Event.Tick == closeTick)
                            {
                                kinds[absorbed[i].Event] = TechnikaNoteKind.Basic;
                                if (run != null)
                                {
                                    run.Members.Remove(absorbed[i].Event);
                                }
                            }
                        }
                        run.Members.Add(note.Event);
                        open = false;
                        head = null;
                        run = null;
                        absorbed.Clear();
                    }
                    kinds[note.Event] = kind;
                    continue;
                }

                if (open && kind == TechnikaNoteKind.Basic && note.Event.Tick > head.Tick)
                {
                    absorbed.Add(note);
                    run.Members.Add(note.Event);
                    kinds[note.Event] = TechnikaNoteKind.ChainNode;
                    continue;
                }

                kinds[note.Event] = kind;
            }
        }

        /// <summary>
        /// Per lane, the first repeat-head marker opens the series; every later head marker while
        /// the series is open is a repeat tick, and the closing marker ends it. The held variants
        /// keep their held tail through the downgrade.
        /// </summary>
        private static void ApplyRepeatFixups(
            TechnikaSeriesMap map, List<SeriesNote> notes)
        {
            Dictionary<EventData, TechnikaNoteKind> kinds = map.Kinds;
            bool[] openByLane = new bool[LaneCount];
            TechnikaSeriesRun[] runs = new TechnikaSeriesRun[LaneCount];

            foreach (SeriesNote note in notes)
            {
                TechnikaNoteKind kind;
                if (!kinds.TryGetValue(note.Event, out kind))
                {
                    kind = note.Kind;
                }

                if (kind == TechnikaNoteKind.RepeatHead ||
                    kind == TechnikaNoteKind.RepeatHeadHold)
                {
                    bool held = kind == TechnikaNoteKind.RepeatHeadHold;
                    if (note.Lane < LaneCount && openByLane[note.Lane])
                    {
                        kind = held ? TechnikaNoteKind.RepeatHold : TechnikaNoteKind.Repeat;
                        if (runs[note.Lane] != null)
                        {
                            runs[note.Lane].Members.Add(note.Event);
                        }
                    }
                    else if (note.Lane < LaneCount)
                    {
                        openByLane[note.Lane] = true;
                        runs[note.Lane] = new TechnikaSeriesRun(TechnikaNoteKind.Repeat);
                        runs[note.Lane].Members.Add(note.Event);
                        map.Runs.Add(runs[note.Lane]);
                    }
                }
                else if (kind == TechnikaNoteKind.Repeat ||
                         kind == TechnikaNoteKind.RepeatHold)
                {
                    if (note.Lane < LaneCount)
                    {
                        openByLane[note.Lane] = false;
                        // An explicit close is only part of the line when a head opened one. An
                        // orphan close is still painted as a close; it just joins nothing.
                        if (runs[note.Lane] != null)
                        {
                            runs[note.Lane].Members.Add(note.Event);
                            runs[note.Lane] = null;
                        }
                    }
                }

                kinds[note.Event] = kind;
            }
        }

        private sealed class SeriesNote
        {
            public SeriesNote(EventData evt, int lane, TechnikaNoteKind kind)
            {
                Event = evt;
                Lane = lane;
                Kind = kind;
            }

            public EventData Event { get; private set; }
            public int Lane { get; private set; }
            public TechnikaNoteKind Kind { get; private set; }
        }
    }
}
