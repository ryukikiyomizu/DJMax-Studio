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
    /// (difficulties) in one file while the model holds one chart, so the first pattern opens;
    /// every other pattern is retained verbatim as raw JSON in <see cref="TechMetadata"/> and
    /// re-spliced into its original slot on save, which keeps a multi-difficulty container
    /// intact even though only one chart is being edited. Touch, Keys and KM control schemes
    /// share one lane coordinate system (lanes 0..playableLanes-1, playableLanes being 2-4),
    /// so no scheme-specific mapping is needed.
    /// </para>
    ///
    /// <para>
    /// Not represented in the model but preserved as container metadata rather than dropped:
    /// time stops, BGA/preview file references, first-beat offset and the track/pattern
    /// identity fields (see <see cref="TechMetadata"/>). The media files themselves are not
    /// part of a .tech - the format references them by filename and they live beside the
    /// chart in the track folder, so only the references are this code's business. Drag
    /// control points are the one authored detail the model has no room for. A drag the user
    /// moves, resizes, re-voices or newly draws is therefore re-emitted as a straight run; a
    /// drag the edit did not touch (head pulse/lane, computed length, volume, pan and
    /// keysound all unchanged) keeps its entire original object verbatim, including its
    /// B-spline control points and lane-crossing anchors.
    /// </para>
    /// </summary>
    internal static partial class TechmaniaChartSerializer
    {
        /// <summary>The format version this reader understands (TECHMANIA 1.0 onward).</summary>
        public const string SupportedVersion = "3";

        // Pulse (240/beat) -> this model's virtual tick (288/beat), i.e. pulse * 6 / 5.
        private const int PulsesPerBeat = 240;
        private const int VirtualTicksPerBeat = 288;

        // Source tracks the TECHNIKA layout already reserves: lanes 0-3, end-of-scan marker
        // tracks 4-7, the tempo slot at 8; occupied invisible/keysound format lanes are
        // compacted onto overflow tracks 9..50.
        private const int LaneCount = 4;
        private const int FirstMarkerTrack = 4;
        private const int TempoTrack = 8;
        private const int FirstOverflowTrack = 9;
        private const int MaxModelTrackIndex = 50;
        // Highest format lane an overflow track can hold: lanes 0-3 are fixed and each of
        // the 42 model tracks 9..50 takes one occupied format lane from 4 upward.
        private const int MaxExtraFormatLane = LaneCount + (MaxModelTrackIndex - FirstOverflowTrack + 1) - 1;
        // A save cannot name a lane the import could not have read.
        private const int MaxWritableFormatLane = MaxExtraFormatLane;

        /// <summary>Beats per scan when a pattern declares none (normal TECHNIKA charts).</summary>
        private const int DefaultBeatsPerScan = 4;

        /// <summary>
        /// Parses a track.tech document's bytes into the shared model.
        /// </summary>
        /// <exception cref="ChartLoadException">For a document that is not version-3 .tech,
        /// is not valid JSON, or contains no importable pattern/notes.</exception>
        public static PlayerData Parse(byte[] data)
        {
            return Parse(data, 0);
        }

        /// <summary>
        /// Parses a track.tech document, opening the pattern (difficulty) at
        /// <paramref name="patternIndex"/> in the container's patterns array. Every other
        /// pattern is retained verbatim for save-back regardless of which slot opens; an
        /// out-of-range index is clamped to the first or last available pattern. This is
        /// the seam a future difficulty chooser calls - today the opener always passes 0.
        /// </summary>
        public static PlayerData Parse(byte[] data, int patternIndex)
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
                return ParseDocument(document.RootElement, patternIndex);
            }
        }

        /// <summary>
        /// Lists the difficulty patterns in a track.tech container without importing one,
        /// for a pattern chooser. Order matches the container's patterns array; each entry's
        /// index can be passed to <see cref="Parse(byte[], int)"/>.
        /// </summary>
        public static IList<TechPatternInfo> ListPatterns(byte[] data)
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
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new ChartLoadException(ChartLoadError.MalformedHeader,
                        "A TECHMANIA track file must contain a JSON object at its root.");
                }
                JsonElement patterns;
                if (!root.TryGetProperty("patterns", out patterns) ||
                    patterns.ValueKind != JsonValueKind.Array)
                {
                    throw new ChartLoadException(ChartLoadError.MalformedHeader,
                        "The TECHMANIA track contains no patterns.");
                }

                var result = new List<TechPatternInfo>();
                int index = 0;
                foreach (JsonElement pattern in patterns.EnumerateArray())
                {
                    JsonElement meta = ElementProperty(pattern, "patternMetadata");
                    int lanes = Math.Max(2, IntProperty(meta, "playableLanes", LaneCount));
                    result.Add(new TechPatternInfo(
                        index,
                        StringProperty(meta, "patternName") ?? ("Pattern " + (index + 1)),
                        IntProperty(meta, "level", 0),
                        lanes));
                    index++;
                }
                return result;
            }
        }

        private static PlayerData ParseDocument(JsonElement root, int requestedPattern = 0)
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

            var metadata = new TechMetadata
            {
                TrackGuid = TrackString(root, "guid"),
                Title = TrackString(root, "title"),
                Artist = TrackString(root, "artist"),
                Genre = TrackString(root, "genre"),
                AdditionalCredits = TrackString(root, "additionalCredits"),
                EyecatchImage = TrackString(root, "eyecatchImage"),
                PreviewTrack = TrackString(root, "previewTrack"),
                PreviewStartTime = TrackDouble(root, "previewStartTime"),
                PreviewEndTime = TrackDouble(root, "previewEndTime"),
                PreviewBga = TrackString(root, "previewBga"),
                AutoOrderPatterns = TrackBool(root, "autoOrderPatterns")
            };

            // One model is one chart. A container normally holds one pattern per
            // difficulty; the requested slot opens (today the opener always asks for the
            // first) and every other slot is retained verbatim, tagged with its original
            // index, so save-back splices all difficulties back in their original order.
            // The opened pattern keeps its raw JSON too: the exporter patches it in place,
            // carrying fields this code does not model (legacy overrides, fingerprint block,
            // null styles) instead of rebuilding the object from a fixed DTO.
            int patternCount = patterns.GetArrayLength();
            int activeIndex = requestedPattern <= 0
                ? 0
                : Math.Min(requestedPattern, patternCount - 1);
            metadata.ActivePatternIndex = activeIndex;
            metadata.ActivePatternJson = patterns[activeIndex].GetRawText();
            PlayerData model = ParsePattern(patterns[activeIndex], metadata);
            for (int i = 0; i < patternCount; i++)
            {
                if (i != activeIndex)
                {
                    metadata.SiblingPatterns.Add(
                        new TechSiblingPattern(i, patterns[i].GetRawText()));
                }
            }

            return model;
        }

        private static PlayerData ParsePattern(JsonElement pattern, TechMetadata metadata)
        {
            JsonElement meta = ElementProperty(pattern, "patternMetadata");
            double initBpm = DoubleProperty(meta, "initBpm", 0);
            if (initBpm <= 0)
            {
                initBpm = 120;
            }

            metadata.PatternGuid = StringProperty(meta, "guid") ?? string.Empty;
            metadata.PatternName = StringProperty(meta, "patternName") ?? string.Empty;
            metadata.Level = IntProperty(meta, "level", 0);
            metadata.ControlScheme = IntProperty(meta, "controlScheme", 0);
            metadata.PlayableLanes = Clamp(IntProperty(meta, "playableLanes", 4), 2, 4);
            metadata.Author = StringProperty(meta, "author") ?? string.Empty;
            metadata.BackingTrack = StringProperty(meta, "backingTrack") ?? string.Empty;
            metadata.BackImage = StringProperty(meta, "backImage") ?? string.Empty;
            metadata.Bga = StringProperty(meta, "bga") ?? string.Empty;
            metadata.BgaOffset = DoubleProperty(meta, "bgaOffset", 0);
            metadata.WaitForEndOfBga = BoolProperty(meta, "waitForEndOfBga");
            metadata.PlayBgaOnLoop = BoolProperty(meta, "playBgaOnLoop");
            metadata.FirstBeatOffset = DoubleProperty(meta, "firstBeatOffset", 0);
            int bps = IntProperty(meta, "bps", DefaultBeatsPerScan);
            metadata.Bps = bps > 0 ? bps : DefaultBeatsPerScan;

            JsonElement timeStops;
            if (pattern.TryGetProperty("timeStops", out timeStops) &&
                timeStops.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in timeStops.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    metadata.TimeStops.Add(new TechTimeStop(
                        IntProperty(entry, "pulse", 0),
                        Math.Max(0, IntProperty(entry, "duration", 0))));
                }
            }

            var player = new PlayerData
            {
                TickPerMinute = 192,
                Tempo = (float)initBpm,
                SourceFormat = ChartFormat.TechmaniaTrack,
                IsReadOnly = false,
                TechMetadata = metadata
            };

            // Added in index order: TracksList.GetTrackAtIndex is list-position based, so the
            // list position must equal Idx. Lanes 0-3 first, marker tracks 4-7, the tempo
            // slot at 8, then occupied invisible/keysound lanes compacted onto tracks 9+,
            // exactly the "one column per track that has notes" behaviour the other importers
            // use. The .tech format allows notes on lanes far past playableLanes - they are
            // the autoplay/preview keysound lanes - and the format's own ceiling is 63; we
            // keep as many as fit through model track 50.
            var laneTracks = new TrackData[LaneCount];
            var markerTracks = new TrackData[LaneCount];
            for (int lane = 0; lane < LaneCount; lane++)
            {
                laneTracks[lane] = AddTrack(player, (uint)lane, "lane " + (lane + 1));
            }
            for (int lane = 0; lane < LaneCount; lane++)
            {
                markerTracks[lane] =
                    AddTrack(player, (uint)(FirstMarkerTrack + lane), "EOS " + (lane + 1));
            }

            // Always hold slot 8 so the overflow tracks keep list position equal to Idx.
            AddTrack(player, TempoTrack, "Tempo");

            // Format lane -> events. Lanes 0-3 feed the fixed lane tracks; any occupied lane
            // at 4 or beyond is compacted onto an overflow track after import.
            var laneEvents = new List<EventData>[LaneCount];
            var extraLaneEvents = new SortedDictionary<int, List<EventData>>();
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
                    if (!TryUnpack(entry.GetString(), false, false, out note))
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
                    if (!BucketNote(note.Lane, evt, laneEvents, extraLaneEvents))
                    {
                        skipped++;
                        continue;
                    }
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
                    if (!TryUnpack(entry.GetString(), true, false, out note))
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
                    if (!BucketNote(note.Lane, evt, laneEvents, extraLaneEvents))
                    {
                        skipped++;
                        continue;
                    }
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
                    if (packedHead == null || !TryUnpack(packedHead, false, true, out note))
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
                    if (!BucketNote(note.Lane, evt, laneEvents, extraLaneEvents))
                    {
                        skipped++;
                        continue;
                    }
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

            // Compact every occupied format lane at 4+ onto the next free model track
            // from 9 up, remembering its format lane for save-back. Empty lanes get no
            // track and therefore no column, exactly like the RESPECT/BMS importers.
            int overflowTrack = FirstOverflowTrack;
            foreach (KeyValuePair<int, List<EventData>> extra in extraLaneEvents)
            {
                if (extra.Value.Count == 0)
                {
                    continue;
                }
                if (overflowTrack > MaxModelTrackIndex)
                {
                    DiagnosticLog.Write("tech.import",
                        "Ignored " + extra.Value.Count + " note(s) on lane " + extra.Key +
                        ": only tracks through " + MaxModelTrackIndex + " are supported.");
                    skipped += extra.Value.Count;
                    continue;
                }
                TrackData track = AddTrack(player, (uint)overflowTrack,
                    "lane " + (extra.Key + 1));
                track.AddEvents(extra.Value);
                metadata.FormatLaneByTrack[overflowTrack] = extra.Key;
                overflowTrack++;
            }

            // Soundtrack-only charts: every tap is silent and the full song lives in
            // patternMetadata.backingTrack. The editor transport only plays keysounds on
            // notes, so synthesize one tick-0 trigger on its own overflow track with the
            // backing file as its instrument. The track is editor-only scaffolding: the
            // writer skips it and keeps the filename in the pattern metadata. (Charts in the
            // other style trigger the song themselves from a note on a hidden lane, which
            // the code above already imports.)
            if (!string.IsNullOrEmpty(metadata.BackingTrack))
            {
                if (overflowTrack > MaxModelTrackIndex)
                {
                    DiagnosticLog.Write("tech.import",
                        "No free track for the backing track \"" + metadata.BackingTrack +
                        "\"; it is kept in metadata but cannot be previewed.");
                }
                else
                {
                    InstrumentData backing;
                    if (!instruments.TryGetValue(metadata.BackingTrack, out backing))
                    {
                        backing = AddInstrument(player, instruments,
                            Math.Min(ushort.MaxValue, instruments.Count),
                            metadata.BackingTrack);
                    }
                    TrackData backingTrackData =
                        AddTrack(player, (uint)overflowTrack, "backing");
                    backingTrackData.AddEvent(new EventData
                    {
                        EventType = EventType.Note,
                        Attribute = 0,
                        VirtualTick = 0,
                        VirtualDuration = 0,
                        Instrument = backing
                    });
                    metadata.BackingTrackModelTrack = overflowTrack;
                    overflowTrack++;
                    notes++;
                }
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
                // Slot 8 was created empty up front so the overflow tracks keep their
                // position-equals-Idx invariant; just fill it when tempo events exist.
                player.Tracks.GetTrackAtIndex(TempoTrack).AddEvents(tempoEvents);
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

        /// <summary>
        /// Buckets one imported note by its .tech format lane. Lanes 0-3 are the fixed
        /// playable lanes; lanes 4..<see cref="MaxExtraFormatLane"/> are the format's
        /// invisible/autoplay keysound lanes and are collected in lane order for the
        /// overflow tracks. A negative or out-of-range lane is the caller's malformed note.
        /// </summary>
        private static bool BucketNote(
            int formatLane,
            EventData evt,
            List<EventData>[] laneEvents,
            IDictionary<int, List<EventData>> extraLaneEvents)
        {
            if (formatLane < 0)
            {
                return false;
            }
            if (formatLane < LaneCount)
            {
                laneEvents[formatLane].Add(evt);
                return true;
            }
            if (formatLane > MaxExtraFormatLane)
            {
                return false;
            }

            List<EventData> bucket;
            if (!extraLaneEvents.TryGetValue(formatLane, out bucket))
            {
                bucket = new List<EventData>();
                extraLaneEvents[formatLane] = bucket;
            }
            bucket.Add(evt);
            return true;
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
        /// Builds the model event for one unpacked note. Returns false only when the note type
        /// is unknown or its format lane is out of the supported 0..<see cref="MaxExtraFormatLane"/>.
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
            // Lanes 0-3 are playable; 4..MaxExtraFormatLane are the format's invisible/
            // autoplay keysound lanes and compact onto overflow tracks after this. Only a
            // genuinely out-of-range format lane (the format ceiling is 63) is malformed.
            if (note.Lane < 0 || note.Lane > MaxExtraFormatLane)
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
        private static bool TryUnpack(string packed, bool hold, bool drag, out PackedNote note)
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
                // A drag's 7th extended field is the curve type (0 Bezier, 1 B-spline), not
                // an end-of-scan flag - TECHMANIA forces endOfScan false on every drag.
                endOfScan = !drag && parts[6 + offset] == "1";
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

        private static string TrackString(JsonElement root, string name)
        {
            JsonElement track;
            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("trackMetadata", out track)
                ? StringProperty(track, name) ?? string.Empty
                : string.Empty;
        }

        private static double TrackDouble(JsonElement root, string name)
        {
            JsonElement track;
            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("trackMetadata", out track)
                ? DoubleProperty(track, name, 0)
                : 0;
        }

        private static bool TrackBool(JsonElement root, string name)
        {
            JsonElement track;
            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("trackMetadata", out track) &&
                BoolProperty(track, name);
        }

        private static bool BoolProperty(JsonElement element, string name)
        {
            JsonElement value;
            return element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(name, out value) &&
                value.ValueKind == JsonValueKind.True;
        }

        private static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
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
