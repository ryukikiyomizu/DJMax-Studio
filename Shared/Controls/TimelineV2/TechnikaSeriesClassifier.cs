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

        /// <summary>Head first, tail last, joints/ticks in between - chart order.</summary>
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
    /// pt model only lets a single event say so much: legacy packs tag every tick of a repeat with
    /// the head's own attribute (10), and the joints of a chain path traced through ordinary taps
    /// are attribute 0. An isolated classifier therefore draws every repeat tick as a round repeat
    /// head and such chain joints as pink taps - the exact notes the arcade paints as a magenta
    /// repeat tick and a yellow chain dot.
    /// </para>
    ///
    /// <para>
    /// A TECHMANIA .tech, by contrast, tags every member explicitly: one ChainHead followed by
    /// <em>all</em> ChainNode waypoints (zig-zagging across lanes - real charts carry a dozen nodes
    /// per head), and one RepeatHead followed by every Repeat marker (a mid-series RepeatHold
    /// included); there is no single closing note. The passes below therefore speak both dialects:
    /// an explicit node or repeat marker extends the open series, a plain tap between chain members
    /// is absorbed the legacy way, and only a note of some other family (or a new head) ends one.
    /// This mirrors the gameplay preview projector's fixup passes, so editing a note and playing
    /// it back show one chart - and the same grouping feeds the timeline's yellow chain line and
    /// purple repeat line instead of leaving a series looking like a scatter of separate notes.
    /// </para>
    /// </summary>
    internal static class TechnikaSeriesClassifier
    {
        /// <summary>The playable lanes, in the projector's own order.</summary>
        private const int LaneCount = 4;

        /// <summary>
        /// Builds the kind every playable-lane note paints as, keyed by the model event itself so
        /// a frame's <see cref="TimelineItem.SourceEvent"/> is an exact reference hit, plus the
        /// chain/repeat runs that join them. Non-lane events are deliberately absent: callers fall
        /// through to the single-event classifier.
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
        /// A chain head opens a path that runs through every following ChainNode (the .tech way,
        /// crossing lanes), plus any ordinary taps strictly after the head that no node shares a
        /// tick with (the legacy way: those taps are joints the file never re-tagged). A note of
        /// another family ends the path; another head replaces it. A tap on a node's own tick is a
        /// real tap in another lane, not a joint.
        /// </summary>
        private static void ApplyChainFixups(
            TechnikaSeriesMap map, List<SeriesNote> notes)
        {
            Dictionary<EventData, TechnikaNoteKind> kinds = map.Kinds;

            // Pass 1 - spans. A chain can only be delimited from the explicit typing: each
            // ChainHead opens a span that runs through every following ChainNode, and ends at
            // the last such node (a .tech tags all waypoints explicitly) or at the next head.
            // Streaming "first node closes" was the legacy single-close dialect and chopped a
            // real dozen-node chain into one pair plus eleven orphans.
            var spans = new List<ChainSpan>();
            ChainSpan currentSpan = null;
            foreach (SeriesNote note in notes)
            {
                if (note.Kind == TechnikaNoteKind.ChainHead)
                {
                    currentSpan = new ChainSpan(note.Event);
                    spans.Add(currentSpan);
                }
                else if (note.Kind == TechnikaNoteKind.ChainNode && currentSpan != null)
                {
                    currentSpan.EndTick = note.Event.Tick;
                    currentSpan.NodeTicks.Add(note.Event.Tick);
                }
            }

            // Pass 2 - painted kinds and run membership. Ordinary taps strictly inside a span
            // are the legacy dialect's untagged joints; a tap on a node's own tick is a real
            // tap in another lane and gets handed back.
            TechnikaSeriesRun run = null;
            ChainSpan span = null;
            var absorbed = new List<SeriesNote>();

            foreach (SeriesNote note in notes)
            {
                TechnikaNoteKind kind = note.Kind;

                if (kind == TechnikaNoteKind.ChainHead)
                {
                    span = FindSpan(spans, note.Event);
                    run = new TechnikaSeriesRun(TechnikaNoteKind.ChainNode);
                    run.Members.Add(note.Event);
                    map.Runs.Add(run);
                    absorbed.Clear();
                    kinds[note.Event] = kind;
                    continue;
                }

                if (kind == TechnikaNoteKind.ChainNode)
                {
                    if (span == null || run == null)
                    {
                        // Orphan node: paint as a node anyway, but it joins nothing.
                        kinds[note.Event] = kind;
                        continue;
                    }

                    int nodeTick = note.Event.Tick;
                    for (int i = absorbed.Count - 1; i >= 0; i--)
                    {
                        if (absorbed[i].Event.Tick == nodeTick)
                        {
                            kinds[absorbed[i].Event] = TechnikaNoteKind.Basic;
                            run.Members.Remove(absorbed[i].Event);
                        }
                    }
                    run.Members.Add(note.Event);
                    kinds[note.Event] = kind;
                    continue;
                }

                if (kind == TechnikaNoteKind.Basic && span != null)
                {
                    int tick = note.Event.Tick;
                    if (tick > span.HeadTick && tick <= span.EndTick &&
                        !span.NodeTicks.Contains(tick))
                    {
                        absorbed.Add(note);
                        run.Members.Add(note.Event);
                        kinds[note.Event] = TechnikaNoteKind.ChainNode;
                        continue;
                    }
                }

                kinds[note.Event] = kind;
            }
        }

        private static ChainSpan FindSpan(List<ChainSpan> spans, EventData head)
        {
            foreach (ChainSpan span in spans)
            {
                if (span.Head == head)
                {
                    return span;
                }
            }
            return null;
        }

        private sealed class ChainSpan
        {
            public ChainSpan(EventData head)
            {
                Head = head;
                EndTick = head.Tick;
            }

            public EventData Head { get; private set; }
            public int HeadTick { get { return Head.Tick; } }
            public int EndTick { get; set; }
            public HashSet<int> NodeTicks { get; private set; }
                = new HashSet<int>();
        }

        /// <summary>
        /// Per lane, a repeat head opens a series; every following Repeat/RepeatHold marker is a
        /// member (the .tech way - there can be several, a held segment among them), and a legacy
        /// run's extra head markers before the first end marker are downgraded to ticks. A note of
        /// any other kind on the same lane closes the series; notes on other lanes never do.
        /// </summary>
        private static void ApplyRepeatFixups(
            TechnikaSeriesMap map, List<SeriesNote> notes)
        {
            Dictionary<EventData, TechnikaNoteKind> kinds = map.Kinds;
            bool[] openByLane = new bool[LaneCount];
            bool[] endSeenByLane = new bool[LaneCount];
            TechnikaSeriesRun[] runs = new TechnikaSeriesRun[LaneCount];

            foreach (SeriesNote note in notes)
            {
                TechnikaNoteKind kind;
                if (!kinds.TryGetValue(note.Event, out kind))
                {
                    kind = note.Kind;
                }

                int lane = note.Lane;
                bool laneInRange = lane < LaneCount;

                if (kind == TechnikaNoteKind.RepeatHead ||
                    kind == TechnikaNoteKind.RepeatHeadHold)
                {
                    bool held = kind == TechnikaNoteKind.RepeatHeadHold;
                    if (laneInRange && openByLane[lane] && !endSeenByLane[lane])
                    {
                        // Legacy dialect: the intermediate ticks keep the head attribute until the
                        // single end marker, so a head before that end is another tick.
                        kind = held ? TechnikaNoteKind.RepeatHold : TechnikaNoteKind.Repeat;
                        if (runs[lane] != null)
                        {
                            runs[lane].Members.Add(note.Event);
                        }
                    }
                    else
                    {
                        // A fresh head: opens a new series (replacing any that ran its course).
                        if (laneInRange)
                        {
                            openByLane[lane] = true;
                            endSeenByLane[lane] = false;
                            runs[lane] = new TechnikaSeriesRun(TechnikaNoteKind.Repeat);
                            runs[lane].Members.Add(note.Event);
                            map.Runs.Add(runs[lane]);
                        }
                    }
                }
                else if (kind == TechnikaNoteKind.Repeat ||
                         kind == TechnikaNoteKind.RepeatHold)
                {
                    if (laneInRange)
                    {
                        if (openByLane[lane])
                        {
                            // An end marker joins the run, but does not close it: a .tech series
                            // carries several Repeat markers, a held one possibly among them.
                            endSeenByLane[lane] = true;
                            runs[lane].Members.Add(note.Event);
                        }
                        // An orphan end marker still paints as one; it just joins nothing.
                    }
                }
                else if (laneInRange && openByLane[lane])
                {
                    // A non-repeat note on the series' own lane closes it. Other lanes are
                    // irrelevant to a repeat run, which never leaves its lane.
                    openByLane[lane] = false;
                    endSeenByLane[lane] = false;
                    runs[lane] = null;
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
