using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using DJMaxEditor.DJMax;
using DJMaxEditor.Diagnostics;
using DJMaxEditor.Files.FormatDetection;

namespace DJMaxEditor.Files.bms
{
    // Reader half of BmsonChartSerializer. The writer half lives in BmsonChartSerializer.cs;
    // this is the second partial of the same class, so the writer's resolution constants and
    // lane table are shared rather than restated.
    //
    // bmson (https://bmson-spec.readthedocs.io/) is a JSON chart: notes live in `sound_channels`,
    // each channel named after the audio file that plays it, positioned on a pulse grid
    // (`resolution` pulses per quarter note) with an x lane and an optional l length. Identifying
    // sound by filename instead of a two-character object id is the whole point of the format -
    // it is how a chart carries more than the 1296 keysounds classic BMS can address - so an
    // instrument here is a sound_channels entry, not a #WAV line.
    //
    // Lanes follow the spec's own tables (see BmsonLaneMap): on beat-* layouts x = 1..7 are the
    // keys and x = 8 is the turntable, with player 2 mirroring that on x = 9..16, and lane 0 is
    // accompaniment. The model has no lane ids of its own, so playable lanes fold onto the
    // classic channels (11-15 keys, 16 turntable, 18/19 keys 6/7, 2x the second side), which is
    // also what the BMS layout draws them from.
    internal static partial class BmsonChartSerializer
    {
        /// <summary>
        /// Parses bmson bytes into the shared chart model.
        /// </summary>
        /// <exception cref="ChartLoadException">When the document is not bmson at all (bad JSON,
        /// no sound channels, no notes). Per-note problems are logged to the diagnostics file and
        /// skip only their own note, on the same terms as the classic BMS reader.</exception>
        public static PlayerData Parse(byte[] data)
        {
            if (data == null)
                throw new ChartLoadException(ChartLoadError.MalformedHeader, "bmson data is null.");

            JsonDocument document;
            try
            {
                int start = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
                document = JsonDocument.Parse(data.AsMemory(start, data.Length - start));
            }
            catch (JsonException ex)
            {
                throw new ChartLoadException(ChartLoadError.MalformedHeader,
                    "bmson is not valid JSON: " + ex.Message);
            }

            using (document)
            {
                return ParseDocument(document.RootElement);
            }
        }

        private static PlayerData ParseDocument(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
                throw new ChartLoadException(ChartLoadError.MalformedHeader,
                    "bmson root is not a JSON object.");

            string version = StringOr(root, "version") ?? "(none)";

            JsonElement info;
            root.TryGetProperty("info", out info);
            if (info.ValueKind != JsonValueKind.Object)
                info = root; // pre-v1 files carry the header fields at the top level; tolerate that.

            var metadata = new BmsMetadata
            {
                Title = StringOr(info, "title") ?? "Untitled",
                Artist = StringOr(info, "artist") ?? "",
                Genre = StringOr(info, "genre") ?? "",
                PlayLevel = (int)NumberOr(info, "level", 0),
                Rank = (int)NumberOr(info, "judge_rank", 0),
                Total = NumberOr(info, "total", 0)
            };

            double resolution = NumberOr(info, "resolution", Resolution);
            if (resolution <= 0 || !IsFinite(resolution))
            {
                DiagnosticLog.Write("open.bmson",
                    "info.resolution " + resolution + " is not usable; assuming " + Resolution + ".");
                resolution = Resolution;
            }

            double initialBpm = NumberOr(info, "init_bpm", 120);
            if (initialBpm <= 0 || !IsFinite(initialBpm))
            {
                DiagnosticLog.Write("open.bmson", "info.init_bpm " + initialBpm + " is not usable; assuming 120.");
                initialBpm = 120;
            }

            // Which lane table the x values are read against. beat-* is the spec default and
            // popn-9k the other layout with its own table; anything else is read as beat and
            // said out loud, because a lane 8 that is really a ninth key would otherwise
            // silently become a turntable.
            string modeHint = StringOr(info, "mode_hint");
            if (!string.IsNullOrWhiteSpace(modeHint) &&
                !BmsonLaneMap.IsPopnHint(modeHint) &&
                !modeHint.Trim().StartsWith("beat", StringComparison.OrdinalIgnoreCase))
            {
                DiagnosticLog.Write("open.bmson",
                    "info.mode_hint \"" + modeHint + "\" is not a beat/popn layout; " +
                    "reading its lanes as beat.");
            }

            // The editor clock is 48 native ticks per quarter note with six virtual sub-ticks
            // each, so a pulse is (192 * VirtualTickSize / 4 / resolution) virtual ticks. At the
            // format's usual 1440 that is exactly the writer's PulsePerVirtualTick = 5.
            double ticksPerPulse = (192.0 * EventData.VirtualTickSize) / 4.0 / resolution;

            RecordMeasureRatios(root, metadata, resolution);
            int stopEvents = CountStopEvents(root);
            if (stopEvents > 0)
            {
                DiagnosticLog.Write("open.bmson",
                    stopEvents + " stop event(s) ignored - the editor model has no stops.");
            }

            var player = new PlayerData
            {
                TickPerMinute = 192,
                Tempo = (float)initialBpm,
                SourceFormat = ChartFormat.Bmson,
                IsReadOnly = false,
                BmsMetadata = metadata
            };

            // ---- pass 1: read every channel into (lane, tick, duration) notes -----------------
            JsonElement channels;
            bool haveChannels = root.TryGetProperty("sound_channels", out channels) &&
                channels.ValueKind == JsonValueKind.Array;

            var instruments = new Dictionary<int, InstrumentData>();
            var channelNotes = new List<BmsonChannelNotes>();
            int instrumentId = 1; // 0 is the classic reader's silence sentinel; bmson has no such id.
            bool instrumentOverflowLogged = false;
            int totalNotes = 0;

            if (haveChannels)
            {
                foreach (JsonElement channel in channels.EnumerateArray())
                {
                    if (channel.ValueKind != JsonValueKind.Object)
                    {
                        DiagnosticLog.Write("open.bmson", "a sound_channels entry is not an object - ignored.");
                        continue;
                    }

                    int ordinal = channelNotes.Count + 1;
                    string name = StringOr(channel, "name");
                    if (string.IsNullOrWhiteSpace(name)) name = "unnamed_" + ordinal + ".wav";

                    JsonElement notes;
                    if (!channel.TryGetProperty("notes", out notes) ||
                        notes.ValueKind != JsonValueKind.Array)
                    {
                        DiagnosticLog.Write("open.bmson", "sound channel \"" + name + "\" has no notes array.");
                        continue;
                    }

                    int id = instrumentId++;
                    if (id > ushort.MaxValue)
                    {
                        if (!instrumentOverflowLogged)
                        {
                            DiagnosticLog.Write("open.bmson",
                                "more than " + ushort.MaxValue + " sound channels; the rest are skipped.");
                            instrumentOverflowLogged = true;
                        }
                        continue;
                    }
                    BmsChartSerializer.AddInstrument(player, instruments, id, name);

                    var parsed = new BmsonChannelNotes(id, name);
                    foreach (JsonElement note in notes.EnumerateArray())
                    {
                        if (note.ValueKind != JsonValueKind.Object) continue;

                        double pulse = NumberOr(note, "y", double.NaN);
                        if (!IsFinite(pulse) || pulse < 0)
                        {
                            DiagnosticLog.Write("open.bmson",
                                "note in \"" + name + "\" has no usable y pulse - note ignored.");
                            continue;
                        }

                        double tick = pulse * ticksPerPulse;
                        if (!IsFinite(tick) || tick > int.MaxValue - 1024)
                        {
                            DiagnosticLog.Write("open.bmson",
                                "note at pulse " + pulse + " in \"" + name +
                                "\" is beyond the editor's tick range - note ignored.");
                            continue;
                        }

                        int lane = (int)NumberOr(note, "x", 0);
                        if (lane < 0)
                        {
                            DiagnosticLog.Write("open.bmson",
                                "note at pulse " + pulse + " in \"" + name + "\" has a negative lane - note ignored.");
                            continue;
                        }

                        double length = NumberOr(note, "l", 0);
                        ushort duration = length > 0 && IsFinite(length)
                            ? BmsChartSerializer.ClampUShort((int)Math.Round(length * ticksPerPulse, MidpointRounding.AwayFromZero))
                            : (ushort)0;

                        parsed.Notes.Add(new ParsedNote(lane,
                            (int)Math.Round(tick, MidpointRounding.AwayFromZero), duration));
                        totalNotes++;
                    }
                    channelNotes.Add(parsed);
                }
            }

            if (totalNotes == 0)
                throw new ChartLoadException(ChartLoadError.MalformedHeader,
                    "bmson contains no sound_channels notes" +
                    (haveChannels ? "." : " (no sound_channels array was found)."));

            DiagnosticLog.Write("open.bmson",
                "version " + version + ", resolution " + resolution.ToString("0", CultureInfo.InvariantCulture) +
                ", " + channelNotes.Count + " sound channel(s), " + totalNotes + " note(s).");

            // ---- pass 2: assemble the model ---------------------------------------------------
            // Playable lanes first, one track per playable x - the same lanes-first order the
            // classic reader uses, so the horizontal timeline and track list put keys under the
            // hand before the accompaniment rows.
            var laneNotes = new Dictionary<int, List<EventData>>();
            foreach (BmsonChannelNotes channel in channelNotes)
            {
                InstrumentData instrument = instruments[channel.InstrumentId];
                foreach (ParsedNote note in channel.Notes)
                {
                    if (BmsonLaneMap.ChannelForLane(modeHint, note.Lane) == null) continue;
                    List<EventData> events;
                    if (!laneNotes.TryGetValue(note.Lane, out events))
                    {
                        events = new List<EventData>();
                        laneNotes[note.Lane] = events;
                    }
                    var noteEvent = BmsChartSerializer.NewNote(note.Tick, instrument);
                    if (note.Duration > 0)
                    {
                        noteEvent.VirtualDuration = note.Duration;
                    }
                    events.Add(noteEvent);
                }
            }

            foreach (KeyValuePair<int, List<EventData>> lane in laneNotes.OrderBy(x => x.Key))
            {
                TrackData track = BmsChartSerializer.AddTrack(player, metadata,
                    "bmson Lane " + lane.Key, BmsonLaneMap.ChannelForLane(modeHint, lane.Key));
                track.AddEvents(lane.Value);
            }

            // Anything without a playable lane - lane 0, the bmson backing/keysound lane, and
            // anything past the layout's last lane - becomes accompaniment. Grouped into
            // simultaneity voice slots rather than one track per sound channel: a bmson carries
            // its keysounds as hundreds of channels, and a track per channel buried the playable
            // lanes under hundreds of BGM columns. A slot is a column of the score, not an
            // instrument - the same rule the classic reader's per-measure BGM slots follow - and
            // the writer regroups by filename on export, so nothing is lost by the merge.
            var bgmEvents = new List<EventData>();
            foreach (BmsonChannelNotes channel in channelNotes)
            {
                InstrumentData instrument = instruments[channel.InstrumentId];
                foreach (ParsedNote note in channel.Notes)
                {
                    if (BmsonLaneMap.ChannelForLane(modeHint, note.Lane) != null) continue;
                    if (note.Lane > 0)
                    {
                        DiagnosticLog.Write("open.bmson",
                            "lane " + note.Lane + " has no playable column in the editor model; " +
                            "its notes are routed to an accompaniment track.");
                    }
                    var noteEvent = BmsChartSerializer.NewNote(note.Tick, instrument);
                    if (note.Duration > 0)
                    {
                        noteEvent.VirtualDuration = note.Duration;
                    }
                    bgmEvents.Add(noteEvent);
                }
            }

            List<List<EventData>> bgmSlots = SplitBgmVoices(bgmEvents);
            bool numberedBgm = bgmSlots.Count > 1;
            for (int slot = 0; slot < bgmSlots.Count; slot++)
            {
                string name = numberedBgm
                    ? "bmson BGM " + (slot + 1).ToString(CultureInfo.InvariantCulture)
                    : "bmson BGM";
                TrackData track = BmsChartSerializer.AddTrack(player, metadata, name, "01");
                track.AddEvents(bgmSlots[slot]);
            }
            if (bgmSlots.Count > 0)
            {
                DiagnosticLog.Write("open.bmson",
                    bgmEvents.Count.ToString(CultureInfo.InvariantCulture) +
                    " accompaniment note(s) in " +
                    bgmSlots.Count.ToString(CultureInfo.InvariantCulture) + " voice slot(s).");
            }

            // Tempo events last, matching the classic reader's track order.
            JsonElement bpmEvents;
            if (root.TryGetProperty("bpm_events", out bpmEvents) &&
                bpmEvents.ValueKind == JsonValueKind.Array)
            {
                var tempoEvents = new List<EventData>();
                foreach (JsonElement bpmEvent in bpmEvents.EnumerateArray())
                {
                    if (bpmEvent.ValueKind != JsonValueKind.Object) continue;
                    double pulse = NumberOr(bpmEvent, "y", double.NaN);
                    double bpm = NumberOr(bpmEvent, "bpm", double.NaN);
                    if (!IsFinite(pulse) || pulse < 0 || !IsFinite(bpm) || bpm <= 0)
                    {
                        DiagnosticLog.Write("open.bmson", "a bpm_event has no usable y/bpm - ignored.");
                        continue;
                    }
                    double tick = pulse * ticksPerPulse;
                    if (!IsFinite(tick) || tick > int.MaxValue - 1024) continue;
                    tempoEvents.Add(BmsChartSerializer.NewTempo(
                        (int)Math.Round(tick, MidpointRounding.AwayFromZero), bpm));
                }

                if (tempoEvents.Count > 0)
                {
                    TrackData tempoTrack = BmsChartSerializer.AddTrack(player, metadata, "bmson Tempo", "08");
                    tempoTrack.AddEvents(tempoEvents.OrderBy(x => x.VirtualTick).ToList());
                }
            }

            return player;
        }

        /// <summary>
        /// Colours accompaniment notes into voice slots: notes that sound together go on
        /// different tracks so the timeline can draw them side by side, and notes that never
        /// overlap share one. Greedy interval colouring on (start, end) with instantaneous
        /// notes splitting same-tick ties - a bmson's accompaniment is nearly all
        /// instantaneous, and two of those on one tick are two voices, not one.
        /// </summary>
        private static List<List<EventData>> SplitBgmVoices(List<EventData> events)
        {
            var slots = new List<List<EventData>>();
            if (events == null || events.Count == 0)
            {
                return slots;
            }

            // CompareTo rather than subtraction: ticks near the top of the int range would
            // overflow a difference and sort backwards.
            events.Sort(delegate(EventData a, EventData b)
            {
                int byStart = a.VirtualTick.CompareTo(b.VirtualTick);
                if (byStart != 0)
                {
                    return byStart;
                }
                return EndTick(b).CompareTo(EndTick(a));
            });

            var slotEnds = new List<int>();
            foreach (EventData note in events)
            {
                int start = note.VirtualTick;
                int end = EndTick(note);
                int slot = 0;
                while (slot < slotEnds.Count && slotEnds[slot] >= start)
                {
                    slot++;
                }
                if (slot == slots.Count)
                {
                    slots.Add(new List<EventData>());
                    slotEnds.Add(end);
                }
                else
                {
                    slotEnds[slot] = end;
                }
                slots[slot].Add(note);
            }
            return slots;
        }

        /// <summary>End tick of a note, clamped rather than overflowing near int.MaxValue.</summary>
        private static int EndTick(EventData note)
        {
            return note.VirtualTick > int.MaxValue - note.VirtualDuration
                ? int.MaxValue
                : note.VirtualTick + note.VirtualDuration;
        }

        /// <summary>
        /// Feeds non-4/4 bar lines into <see cref="BmsMetadata.MeasureLengthRatios"/>, so a bmson
        /// with changing bar lengths survives a trip through the classic BMS exporter the same way
        /// a #mmm02 ratio does. Bar lines that do not tile contiguously are reported and the
        /// ratios after them are left at the default, because a ratio at the wrong ordinal would
        /// move every later measure.
        /// </summary>
        private static void RecordMeasureRatios(JsonElement root, BmsMetadata metadata, double resolution)
        {
            JsonElement lines;
            if (!root.TryGetProperty("lines", out lines) || lines.ValueKind != JsonValueKind.Array)
                return;

            var bars = new List<KeyValuePair<double, double>>(); // (y pulse, length pulses)
            foreach (JsonElement line in lines.EnumerateArray())
            {
                if (line.ValueKind != JsonValueKind.Object) continue;
                double y = NumberOr(line, "y", double.NaN);
                double k = NumberOr(line, "k", 0);
                if (!IsFinite(y) || y < 0) continue;
                // k = 0 means "default 4/4 bar" in bmson, exactly like the writer emits.
                double length = k > 0 && IsFinite(k) ? k : 4 * resolution;
                bars.Add(new KeyValuePair<double, double>(y, length));
            }

            double expected = 0;
            foreach (KeyValuePair<double, double> bar in bars.OrderBy(x => x.Key))
            {
                if (Math.Abs(bar.Key - expected) > 0.5)
                {
                    DiagnosticLog.Write("open.bmson",
                        "bar lines are not contiguous at pulse " + bar.Key +
                        "; measure ratios beyond it are assumed 4/4.");
                    return;
                }
                double ratio = bar.Value / (4 * resolution);
                int measure = (int)Math.Round(bar.Key / (4 * resolution), MidpointRounding.AwayFromZero);
                if (measure >= 0 && measure <= 999 && Math.Abs(ratio - 1.0) > 1e-9)
                    metadata.MeasureLengthRatios[measure] = ratio;
                expected = bar.Key + bar.Value;
            }
        }

        private static int CountStopEvents(JsonElement root)
        {
            JsonElement stops;
            if (!root.TryGetProperty("stop_events", out stops) || stops.ValueKind != JsonValueKind.Array)
                return 0;
            return stops.EnumerateArray().Count();
        }

        // ---- JSON helpers ---------------------------------------------------------------------

        /// <summary>Reads a numeric member, tolerating numbers written as strings.</summary>
        private static double NumberOr(JsonElement parent, string name, double fallback)
        {
            JsonElement value;
            if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out value))
                return fallback;
            switch (value.ValueKind)
            {
                case JsonValueKind.Number:
                    return value.GetDouble();
                case JsonValueKind.String:
                    double parsed;
                    return double.TryParse(value.GetString(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
                default:
                    return fallback;
            }
        }

        private static string StringOr(JsonElement parent, string name)
        {
            JsonElement value;
            return parent.ValueKind == JsonValueKind.Object &&
                parent.TryGetProperty(name, out value) &&
                value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private sealed class BmsonChannelNotes
        {
            public BmsonChannelNotes(int instrumentId, string name)
            {
                InstrumentId = instrumentId;
                Name = name ?? "";
            }

            public int InstrumentId { get; }
            public string Name { get; }
            public List<ParsedNote> Notes { get; } = new List<ParsedNote>();
        }

        private readonly struct ParsedNote
        {
            public ParsedNote(int lane, int tick, ushort duration)
            {
                Lane = lane;
                Tick = tick;
                Duration = duration;
            }

            public int Lane { get; }
            public int Tick { get; }
            public ushort Duration { get; }
        }
    }
}
