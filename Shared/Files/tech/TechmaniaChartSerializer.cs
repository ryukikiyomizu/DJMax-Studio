using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using DJMaxEditor.Diagnostics;
using DJMaxEditor.DJMax;
using DJMaxEditor.Files.FormatDetection;

namespace DJMaxEditor.Files.Tech
{
    /// <summary>
    /// Reads TECHMANIA's native <c>track.tech</c> format (format version "3", the current
    /// one since TECHMANIA 1.0) into the shared chart model.
    ///
    /// <para>
    /// The format is documented at
    /// https://techmania-team.github.io/techmania-docs/English/track.tech_specification.html :
    /// a JSON container of track metadata and one or more patterns, and each pattern carries
    /// its notes as pipe-packed strings - <c>packedNotes</c> for Basic / ChainHead / ChainNode /
    /// RepeatHead / Repeat, <c>packedHoldNotes</c> for Hold / RepeatHeadHold / RepeatHold, and
    /// <c>packedDragNotes</c> for Drag notes with their Bézier/B-spline nodes. The note-type
    /// vocabulary is the one the TECHNIKA preview already speaks, so the mapping is a direct
    /// attribute assignment with no run inference: the file tags every chain joint and repeat
    /// tick explicitly.
    /// </para>
    ///
    /// <para>
    /// Grid conversion. TECHMANIA counts 240 pulses per beat; this model counts 192 ticks per
    /// 4-beat measure with 6 virtual ticks per tick, i.e. 288 virtual ticks per beat, so a pulse
    /// is 6/5 of a virtual tick and positions are rounded. A track.tech packs several patterns
    /// (difficulties) in one file while the model holds one chart, so the first pattern is
    /// imported - same choice an importer with no pattern chooser makes elsewhere. Touch, Keys
    /// and KM control schemes share one lane coordinate system (lanes 0..playableLanes-1,
    /// playableLanes being 2-4), so no scheme-specific mapping is needed.
    /// </para>
    ///
    /// <para>
    /// Not represented in the model and deliberately dropped: drag control points (the editor's
    /// drag is a straight run; only the head and its duration survive), time stops, BGA and
    /// preview metadata, first-beat offset, and per-note volume/pan curves outside the stored
    /// byte fields. Volume/pan and the end-of-scan flag do survive.
    /// </para>
    /// </summary>
    internal static class TechmaniaChartSerializer
    {
        /// <summary>The format version this reader understands (TECHMANIA 1.0 onward).</summary>
        public const string SupportedVersion = "3";

        // Pulse (240/beat) -> this model's virtual tick (288/beat), i.e. pulse * 6 / 5.
        private const int PulsesPerBeat = 240;
        private const int VirtualTicksPerBeat = 288;

        // Source tracks the TECHNIKA layout already reserves: lanes 0-3, end-of-scan marker
        // tracks 4-7; tempo and other accompaniment follow from track 8.
        private const int LaneCount = 4;
        private const int FirstMarkerTrack = 4;
        private const int TempoTrack = 8;

        /// <summary>
        /// Parses a track.tech document's bytes into the shared model.
        /// </summary>
        /// <exception cref="ChartLoadException">For a document that is not version-3 .tech,
        /// is not valid JSON, or contains no importable pattern/notes.</exception>
        public static PlayerData Parse(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                throw new ChartLoadException(ChartLoadError.MalformedHeader,
                    "The TECHMANIA track file is empty.");
            }

            JsonDocument document;
            try
            {
                int start = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
                document = JsonDocument.Parse(data.AsMemory(start, data.Length - start));
            }
            catch (JsonException ex)
            {
                throw new ChartLoadException(ChartLoadError.MalformedHeader,
                    "The TECHMANIA track file is not valid JSON: " + ex.Message);
            }

            using (document)
            {
                return ParseDocument(document.RootElement);
            }
        }

        private static PlayerData ParseDocument(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ChartLoadException(ChartLoadError.MalformedHeader,
                    "A TECHMANIA track file must contain a JSON object at its root.");
            }

            string version = StringProperty(root, "version");
            if (version != SupportedVersion)
            {
                throw new ChartLoadException(ChartLoadError.UnsupportedFormat,
                    "TECHMANIA track format version \"" + (version ?? "(none)") +
                    "\" is not supported; only version \"" + SupportedVersion +
                    "\" (TECHMANIA 1.0 and later) can be imported. Re-save the track in a " +
                    "current TECHMANIA build and open it again.");
            }

            JsonElement patterns;
            if (!root.TryGetProperty("patterns", out patterns) ||
                patterns.ValueKind != JsonValueKind.Array ||
                patterns.GetArrayLength() == 0)
            {
                throw new ChartLoadException(ChartLoadError.MalformedHeader,
                    "The TECHMANIA track contains no patterns.");
            }

            // One model is one chart; take the first pattern deterministically.
            return ParsePattern(patterns[0]);
        }

        private static PlayerData ParsePattern(JsonElement pattern)
        {
            JsonElement meta = ElementProperty(pattern, "patternMetadata");
            double initBpm = DoubleProperty(meta, "initBpm", 0);
            if (initBpm <= 0)
            {
                initBpm = 120;
            }

            var player = new PlayerData
            {
                TickPerMinute = 192,
                Tempo = (float)initBpm,
                SourceFormat = ChartFormat.TechmaniaTrack,
                IsReadOnly = false
            };

            var laneTracks = new TrackData[LaneCount];
            var markerTracks = new TrackData[LaneCount];
            for (int lane = 0; lane < LaneCount; lane++)
            {
                laneTracks[lane] = AddTrack(player, (uint)lane, "lane " + (lane + 1));
                markerTracks[lane] =
                    AddTrack(player, (uint)(FirstMarkerTrack + lane), "EOS " + (lane + 1));
            }

            var laneEvents = new List<EventData>[LaneCount];
            var markerEvents = new List<EventData>[LaneCount];
            for (int lane = 0; lane < LaneCount; lane++)
            {
                laneEvents[lane] = new List<EventData>();
                markerEvents[lane] = new List<EventData>();
            }

            var instruments = new Dictionary<string, InstrumentData>(StringComparer.OrdinalIgnoreCase);
            InstrumentData silent = AddInstrument(player, instruments, 0, "none");

            int notes = 0;
            int skipped = 0;

            // Notes without a duration: Basic, ChainHead, ChainNode, RepeatHead, Repeat.
            JsonElement packedNotes;
            if (pattern.TryGetProperty("packedNotes", out packedNotes) &&
                packedNotes.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in packedNotes.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.String)
                    {
                        skipped++;
                        continue;
                    }
                    PackedNote note;
                    if (!TryUnpack(entry.GetString(), false, out note))
                    {
                        skipped++;
                        continue;
                    }
                    EventData evt;
                    if (!TryBuildNote(note, 0, instruments, player, silent, out evt))
                    {
                        skipped++;
                        continue;
                    }
                    laneEvents[note.Lane].Add(evt);
                    AddMarker(markerEvents, note);
                    notes++;
                }
            }

            // Notes with a duration: Hold, RepeatHeadHold, RepeatHold.
            JsonElement packedHoldNotes;
            if (pattern.TryGetProperty("packedHoldNotes", out packedHoldNotes) &&
                packedHoldNotes.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in packedHoldNotes.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.String)
                    {
                        skipped++;
                        continue;
                    }
                    PackedNote note;
                    if (!TryUnpack(entry.GetString(), true, out note))
                    {
                        skipped++;
                        continue;
                    }
                    EventData evt;
                    if (!TryBuildNote(note, ToVirtual(note.DurationPulse), instruments, player, silent, out evt))
                    {
                        skipped++;
                        continue;
                    }
                    laneEvents[note.Lane].Add(evt);
                    AddMarker(markerEvents, note);
                    notes++;
                }
            }

            // Drags: head note plus a node list that defines the curve and length.
            JsonElement packedDragNotes;
            if (pattern.TryGetProperty("packedDragNotes", out packedDragNotes) &&
                packedDragNotes.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in packedDragNotes.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        skipped++;
                        continue;
                    }
                    string packedHead = StringProperty(entry, "packedNote");
                    PackedNote note;
                    if (packedHead == null || !TryUnpack(packedHead, false, out note))
                    {
                        skipped++;
                        continue;
                    }
                    int dragDuration = DragDurationPulses(entry);
                    EventData evt;
                    if (!TryBuildNote(note, ToVirtual(dragDuration), instruments, player, silent, out evt))
                    {
                        skipped++;
                        continue;
                    }
                    // The drag head is attribute 0 with a duration - the classifier's Drag.
                    evt.Attribute = 0;
                    laneEvents[note.Lane].Add(evt);
                    // Drags never carry an end-of-scan flag (TECHMANIA forces it false on
                    // unpack), so no marker is added.
                    notes++;
                }
            }

            for (int lane = 0; lane < LaneCount; lane++)
            {
                laneTracks[lane].AddEvents(laneEvents[lane]);
                markerTracks[lane].AddEvents(markerEvents[lane]);
            }

            // Tempo events on track 8, the first overflow track, exactly as the BMS family does.
            var tempoEvents = new List<EventData>();
            JsonElement bpmEvents;
            if (pattern.TryGetProperty("bpmEvents", out bpmEvents) &&
                bpmEvents.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in bpmEvents.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    double bpm = DoubleProperty(entry, "bpm", 0);
                    int pulse = IntProperty(entry, "pulse", 0);
                    if (bpm <= 0)
                    {
                        continue;
                    }
                    tempoEvents.Add(new EventData
                    {
                        EventType = EventType.Tempo,
                        VirtualTick = ToVirtual(pulse),
                        Tempo = (float)bpm
                    });
                }
            }
            if (tempoEvents.Count > 0)
            {
                AddTrack(player, TempoTrack, "Tempo").AddEvents(tempoEvents);
            }

            if (notes == 0)
            {
                throw new ChartLoadException(ChartLoadError.MalformedHeader,
                    "The TECHMANIA pattern contains no notes that could be imported.");
            }
            if (skipped > 0)
            {
                DiagnosticLog.Write("tech.import",
                    "Skipped " + skipped + " unrecognised or malformed note(s) while importing.");
            }
            return player;
        }

        private static TrackData AddTrack(PlayerData player, uint idx, string name)
        {
            var track = new TrackData(idx) { TrackName = name };
            player.Tracks.AddTrack(track);
            return track;
        }

        private static InstrumentData AddInstrument(
            PlayerData player, IDictionary<string, InstrumentData> map, int number, string name)
        {
            var instrument = new InstrumentData { InsNum = (ushort)number, Name = name ?? string.Empty };
            map[name ?? string.Empty] = instrument;
            player.Instruments.Add(instrument);
            return instrument;
        }

        /// <summary>
        /// Builds the model event for one unpacked note. Returns false when the lane is one the
        /// four-track TECHNIKA field cannot place.
        /// </summary>
        private static bool TryBuildNote(
            PackedNote note,
            int virtualDuration,
            IDictionary<string, InstrumentData> instruments,
            PlayerData player,
            InstrumentData silent,
            out EventData evt)
        {
            evt = null;
            byte attribute;
            switch (note.Type)
            {
                case "Basic": attribute = 0; break;
                case "ChainHead": attribute = 5; break;
                case "ChainNode": attribute = 6; break;
                case "Hold": attribute = 12; break;
                case "Drag": attribute = 0; break;
                case "RepeatHead": attribute = 10; break;
                case "RepeatHeadHold": attribute = 10; break;
                case "Repeat": attribute = 11; break;
                case "RepeatHold": attribute = 11; break;
                default:
                    return false;
            }
            if (note.Lane < 0 || note.Lane >= LaneCount)
            {
                return false;
            }

            InstrumentData instrument = silent;
            string keysound = note.Keysound ?? string.Empty;
            if (keysound.Length > 0)
            {
                InstrumentData existing;
                if (!instruments.TryGetValue(keysound, out existing))
                {
                    // InsNum 0 is the silent slot; keysounds number from 1 in file order.
                    int number = Math.Min(ushort.MaxValue, instruments.Count);
                    existing = AddInstrument(player, instruments, number, keysound);
                }
                instrument = existing;
            }

            evt = new EventData
            {
                EventType = EventType.Note,
                Attribute = attribute,
                VirtualTick = ToVirtual(note.Pulse),
                VirtualDuration = (ushort)Math.Max(0, Math.Min(ushort.MaxValue, virtualDuration)),
                Instrument = instrument,
                Volume = (byte)Math.Max(0, Math.Min(100, note.Volume)),
                Pan = (byte)Math.Max(0, Math.Min(127, (int)Math.Round(64 + (note.Pan * 0.64),
                    MidpointRounding.AwayFromZero)))
            };
            return true;
        }

        /// <summary>
        /// Adds the end-of-scan marker a flagged note carries: an attribute-100 event on the
        /// marker track for its lane, at the same tick. The projector reads those tracks (4-7)
        /// purely as flags and never classifies them as notes.
        /// </summary>
        private static void AddMarker(List<EventData>[] markerEvents, PackedNote note)
        {
            if (!note.EndOfScan || note.Lane < 0 || note.Lane >= LaneCount)
            {
                return;
            }
            markerEvents[note.Lane].Add(new EventData
            {
                EventType = EventType.Note,
                Attribute = 100,
                VirtualTick = ToVirtual(note.Pulse)
            });
        }

        /// <summary>
        /// The drag's length in pulses, exactly as TECHMANIA computes it: the last anchor for a
        /// Bézier curve (or a two-node B-spline), and a 1:5 interpolation of the last two
        /// anchors for a longer B-spline whose final segment is removed.
        /// </summary>
        private static int DragDurationPulses(JsonElement drag)
        {
            JsonElement nodes;
            if (!drag.TryGetProperty("packedNodes", out nodes) ||
                nodes.ValueKind != JsonValueKind.Array ||
                nodes.GetArrayLength() < 2)
            {
                return 0;
            }

            var anchors = new List<double>();
            foreach (JsonElement packedNode in nodes.EnumerateArray())
            {
                if (packedNode.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                string[] parts = packedNode.GetString().Split('|');
                double anchorPulse;
                if (parts.Length >= 1 &&
                    double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture,
                        out anchorPulse))
                {
                    anchors.Add(anchorPulse);
                }
            }
            if (anchors.Count < 2)
            {
                return 0;
            }

            // The extended head string's 7th field is the curve type for drags.
            int curveType = 0;
            string head = StringProperty(drag, "packedNote") ?? string.Empty;
            string[] headParts = head.Split('|');
            if (headParts.Length >= 8 && headParts[0] == "E")
            {
                int parsed;
                if (int.TryParse(headParts[6], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out parsed))
                {
                    curveType = parsed;
                }
            }

            double durationPulses;
            if (curveType == 1 && anchors.Count > 2)
            {
                durationPulses = (anchors[anchors.Count - 2] + (anchors[anchors.Count - 1] * 5f)) / 6f;
            }
            else
            {
                durationPulses = anchors[anchors.Count - 1];
            }
            return Math.Max(0, (int)Math.Round(durationPulses, MidpointRounding.AwayFromZero));
        }

        /// <summary>
        /// Unpacks one pipe-packed note string, mirroring TECHMANIA's own <c>Note.Unpack</c> /
        /// <c>HoldNote.Unpack</c>. The keysound portion may itself contain '|', which is why the
        /// split count is limited.
        /// </summary>
        private static bool TryUnpack(string packed, bool hold, out PackedNote note)
        {
            note = null;
            if (string.IsNullOrEmpty(packed))
            {
                return false;
            }

            char[] delim = { '|' };
            string[] parts = packed.Split(delim, 2);
            bool extended = parts.Length > 0 && parts[0] == "E";

            int pulse;
            int lane;
            string type;
            int duration = 0;
            int volume = 100;
            int pan = 0;
            bool endOfScan = false;
            string keysound;

            if (extended)
            {
                parts = packed.Split(delim, hold ? 9 : 8);
                int minimum = hold ? 9 : 8;
                if (parts.Length < minimum)
                {
                    return false;
                }
                int offset = hold ? 1 : 0;
                type = parts[1];
                if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out pulse) ||
                    !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out lane))
                {
                    return false;
                }
                if (hold &&
                    !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out duration))
                {
                    return false;
                }
                string volumeText = parts[4 + offset];
                string panText = parts[5 + offset];
                if (!int.TryParse(volumeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out volume) ||
                    !int.TryParse(panText, NumberStyles.Integer, CultureInfo.InvariantCulture, out pan))
                {
                    return false;
                }
                endOfScan = parts[6 + offset] == "1";
                keysound = parts[7 + offset];
            }
            else
            {
                parts = packed.Split(delim, hold ? 5 : 4);
                int minimum = hold ? 5 : 4;
                if (parts.Length < minimum)
                {
                    return false;
                }
                if (hold)
                {
                    type = parts[0];
                    if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out pulse) ||
                        !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out lane) ||
                        !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out duration))
                    {
                        return false;
                    }
                    keysound = parts[4];
                }
                else
                {
                    type = parts[0];
                    if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out pulse) ||
                        !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out lane))
                    {
                        return false;
                    }
                    keysound = parts[3];
                }
            }

            note = new PackedNote
            {
                Type = type,
                Pulse = pulse,
                Lane = lane,
                DurationPulse = duration,
                Volume = volume,
                Pan = pan,
                EndOfScan = endOfScan,
                Keysound = keysound ?? string.Empty
            };
            return true;
        }

        private static int ToVirtual(int pulse)
        {
            // pulse * 288 / 240, rounded away from zero; notes are never at negative pulses.
            long scaled = (long)pulse * VirtualTicksPerBeat;
            long rounded = (scaled + (PulsesPerBeat / 2)) / PulsesPerBeat;
            return (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, rounded));
        }

        private static string StringProperty(JsonElement element, string name)
        {
            JsonElement value;
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(name, out value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
            return null;
        }

        private static JsonElement ElementProperty(JsonElement element, string name)
        {
            JsonElement value;
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(name, out value) &&
                value.ValueKind == JsonValueKind.Object)
            {
                return value;
            }
            return default;
        }

        private static int IntProperty(JsonElement element, string name, int fallback)
        {
            JsonElement value;
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(name, out value) &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out int parsed))
            {
                return parsed;
            }
            return fallback;
        }

        private static double DoubleProperty(JsonElement element, string name, double fallback)
        {
            JsonElement value;
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(name, out value) &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetDouble(out double parsed))
            {
                return parsed;
            }
            return fallback;
        }

        private sealed class PackedNote
        {
            public string Type;
            public int Pulse;
            public int Lane;
            public int DurationPulse;
            public int Volume;
            public int Pan;
            public bool EndOfScan;
            public string Keysound;
        }
    }
}
