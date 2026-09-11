using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using DJMaxEditor.Controls.TimelineV2;
using DJMaxEditor.Diagnostics;
using DJMaxEditor.DJMax;

namespace DJMaxEditor.Files.Tech
{
    /// <summary>
    /// Writer half of the TECHMANIA track.tech round trip. See
    /// <see cref="TechmaniaChartSerializer"/> for the format and the grid conversion.
    ///
    /// <para>
    /// The shared model is one TECHNIKA-shaped chart, so the output is one v3 pattern on a
    /// four-lane Touch control scheme: tracks 0-3 become the note tables, tracks 4-7 are read
    /// purely for their attribute-100 end-of-scan flags, and tempo events from any track
    /// become <c>bpmEvents</c>. The note kinds map one-to-one onto the packed type names; a
    /// drag becomes a two-node straight Bézier curve whose length is its duration, because
    /// the model has nowhere to keep the original control points.
    /// </para>
    ///
    /// <para>
    /// Accompaniment tracks (8+ that are not tempo), time stops, first-beat offset and the AV
    /// metadata have no place a chart-only save can fill, so they are dropped (the dropped
    /// counts go to the diagnostics log). Volume/pan defaults are the model defaults (127 /
    /// center 64); anything else is written through and forces the extended packed form.
    /// </para>
    /// </summary>
    internal static partial class TechmaniaChartSerializer
    {
        // Reverse of the reader's grid: virtual tick (288/beat) back to pulse (240/beat).
        private const int DefaultBeatsPerScan = 4;

        // The model byte defaults, matching the EventData constructor.
        private const byte ModelDefaultVolume = 127;
        private const byte ModelDefaultPan = 64;
        // TECHMANIA's own packed-note defaults.
        private const int TechDefaultVolume = 100;
        private const int TechDefaultPan = 0;

        public static string Serialize(PlayerData player)
        {
            if (player == null)
            {
                throw new ArgumentNullException(nameof(player));
            }

            var packedNotes = new List<string>();
            var packedHolds = new List<string>();
            var packedDrags = new List<PackedDragDto>();

            // End-of-scan flags keyed (lane, pulse) from the marker tracks 4-7.
            var endOfScan = new HashSet<long>();
            CollectEndOfScan(player, endOfScan);

            var ordered = new List<EventData>();
            for (int lane = 0; lane < LaneCount; lane++)
            {
                TrackData track = player.Tracks.GetTrackAtIndex((uint)lane);
                if (track == null)
                {
                    continue;
                }
                foreach (EventData evt in track.Events)
                {
                    if (evt == null || evt.EventType != EventType.Note ||
                        evt.Attribute == 100)
                    {
                        continue;
                    }
                    ordered.Add(evt);
                }
            }
            // TECHMANIA keeps its tables in pulse/lane order; the game's own NoteComparer is
            // pulse then lane.
            ordered.Sort((a, b) =>
            {
                int pulse = ToPulse(a.VirtualTick).CompareTo(ToPulse(b.VirtualTick));
                if (pulse != 0)
                {
                    return pulse;
                }
                return a.TrackId.CompareTo(b.TrackId);
            });

            int dropped = 0;
            foreach (EventData evt in ordered)
            {
                int lane = (int)evt.TrackId;
                int pulse = ToPulse(evt.VirtualTick);
                int duration = ToPulse(evt.VirtualDuration);
                int volume = OutVolume(evt.Volume);
                int pan = OutPan(evt.Pan);
                bool eos = endOfScan.Contains(Key(lane, pulse));
                string sound = evt.Instrument != null && evt.Instrument.Name != "none"
                    ? evt.Instrument.Name ?? string.Empty
                    : string.Empty;

                TechnikaNoteKind kind = TechnikaNoteClassifier.Classify(evt);
                switch (kind)
                {
                    case TechnikaNoteKind.Basic:
                    case TechnikaNoteKind.ChainHead:
                    case TechnikaNoteKind.ChainNode:
                    case TechnikaNoteKind.RepeatHead:
                    case TechnikaNoteKind.Repeat:
                        packedNotes.Add(
                            PackNote(kind.ToString(), pulse, lane, volume, pan, eos, sound));
                        break;

                    case TechnikaNoteKind.Hold:
                    case TechnikaNoteKind.RepeatHeadHold:
                    case TechnikaNoteKind.RepeatHold:
                        packedHolds.Add(
                            PackHold(kind.ToString(), pulse, lane, duration, volume, pan, eos, sound));
                        break;

                    case TechnikaNoteKind.Drag:
                        packedDrags.Add(new PackedDragDto
                        {
                            packedNote = PackDrag(pulse, lane, volume, pan, sound),
                            packedNodes = StraightDragNodes(duration)
                        });
                        break;

                    default:
                        dropped++;
                        break;
                }
            }

            var pattern = new PatternDto
            {
                patternMetadata = new PatternMetadataDto
                {
                    guid = Guid.NewGuid().ToString(),
                    patternName = string.Empty,
                    level = 0,
                    controlScheme = 0,
                    playableLanes = LaneCount,
                    author = string.Empty,
                    backingTrack = string.Empty,
                    backImage = string.Empty,
                    bga = string.Empty,
                    bgaOffset = 0,
                    waitForEndOfBga = false,
                    playBgaOnLoop = false,
                    firstBeatOffset = 0,
                    initBpm = player.Tempo > 0 ? Math.Round(player.Tempo, 3) : 120.0,
                    bps = DefaultBeatsPerScan
                },
                bpmEvents = CollectBpmEvents(player),
                timeStops = new List<TimeStopDto>(),
                packedNotes = packedNotes,
                packedHoldNotes = packedHolds,
                packedDragNotes = packedDrags
            };

            var file = new TrackFileDto
            {
                version = SupportedVersion,
                trackMetadata = new TrackMetadataDto
                {
                    guid = Guid.NewGuid().ToString(),
                    title = string.Empty,
                    artist = string.Empty,
                    genre = string.Empty,
                    additionalCredits = string.Empty,
                    eyecatchImage = string.Empty,
                    previewTrack = string.Empty,
                    previewStartTime = 0,
                    previewEndTime = 0,
                    previewBga = string.Empty,
                    autoOrderPatterns = false
                },
                patterns = new List<PatternDto> { pattern }
            };

            if (dropped > 0)
            {
                DiagnosticLog.Write("tech.export",
                    "Dropped " + dropped + " note(s) with no TECHMANIA equivalent.");
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            string json = JsonSerializer.Serialize(file, options);

            // TECHMANIA's parser is a tolerant JSON reader but is case-sensitive about the
            // packed type names; nothing else here relies on key order.
            return json;
        }

        private static void CollectEndOfScan(PlayerData player, HashSet<long> flags)
        {
            int orphan = 0;
            for (int markerTrack = FirstMarkerTrack;
                 markerTrack < FirstMarkerTrack + LaneCount;
                 markerTrack++)
            {
                TrackData track = player.Tracks.GetTrackAtIndex((uint)markerTrack);
                if (track == null)
                {
                    continue;
                }
                int lane = markerTrack - FirstMarkerTrack;
                foreach (EventData marker in track.Events)
                {
                    if (marker == null || marker.EventType != EventType.Note ||
                        marker.Attribute != 100)
                    {
                        continue;
                    }
                    int pulse = ToPulse(marker.VirtualTick);
                    if (HasNoteAt(player, lane, pulse))
                    {
                        flags.Add(Key(lane, pulse));
                    }
                    else
                    {
                        orphan++;
                    }
                }
            }
            if (orphan > 0)
            {
                DiagnosticLog.Write("tech.export",
                    "Ignored " + orphan + " end-of-scan marker(s) with no matching note.");
            }
        }

        private static bool HasNoteAt(PlayerData player, int lane, int pulse)
        {
            TrackData track = player.Tracks.GetTrackAtIndex((uint)lane);
            if (track == null)
            {
                return false;
            }
            foreach (EventData evt in track.Events)
            {
                if (evt != null && evt.EventType == EventType.Note &&
                    evt.Attribute != 100 && ToPulse(evt.VirtualTick) == pulse)
                {
                    return true;
                }
            }
            return false;
        }

        private static List<BpmEventDto> CollectBpmEvents(PlayerData player)
        {
            var events = new List<BpmEventDto>();
            foreach (TrackData track in player.Tracks)
            {
                foreach (EventData evt in track.Events)
                {
                    if (evt == null || evt.EventType != EventType.Tempo || evt.Tempo <= 0)
                    {
                        continue;
                    }
                    int pulse = ToPulse(evt.VirtualTick);
                    if (pulse <= 0)
                    {
                        // The initial tempo lives in patternMetadata.initBpm; a change at 0
                        // written in both places is redundant.
                        continue;
                    }
                    events.Add(new BpmEventDto
                    {
                        pulse = pulse,
                        bpm = Math.Round(evt.Tempo, 3)
                    });
                }
            }
            events.Sort((a, b) => a.pulse.CompareTo(b.pulse));
            return events;
        }

        private static string PackNote(
            string type, int pulse, int lane, int volume, int pan, bool eos, string sound)
        {
            if (volume != TechDefaultVolume || pan != TechDefaultPan || eos)
            {
                return string.Join("|", "E", type,
                    pulse.ToString(CultureInfo.InvariantCulture),
                    lane.ToString(CultureInfo.InvariantCulture),
                    volume.ToString(CultureInfo.InvariantCulture),
                    pan.ToString(CultureInfo.InvariantCulture),
                    eos ? "1" : "0",
                    sound);
            }
            return string.Join("|", type,
                pulse.ToString(CultureInfo.InvariantCulture),
                lane.ToString(CultureInfo.InvariantCulture),
                sound);
        }

        private static string PackHold(
            string type, int pulse, int lane, int duration,
            int volume, int pan, bool eos, string sound)
        {
            if (volume != TechDefaultVolume || pan != TechDefaultPan || eos)
            {
                return string.Join("|", "E", type,
                    pulse.ToString(CultureInfo.InvariantCulture),
                    lane.ToString(CultureInfo.InvariantCulture),
                    duration.ToString(CultureInfo.InvariantCulture),
                    volume.ToString(CultureInfo.InvariantCulture),
                    pan.ToString(CultureInfo.InvariantCulture),
                    eos ? "1" : "0",
                    sound);
            }
            return string.Join("|", type,
                pulse.ToString(CultureInfo.InvariantCulture),
                lane.ToString(CultureInfo.InvariantCulture),
                duration.ToString(CultureInfo.InvariantCulture),
                sound);
        }

        private static string PackDrag(int pulse, int lane, int volume, int pan, string sound)
        {
            // Field 7 of an extended drag is the curve type, not end-of-scan (drags never
            // carry the flag); a flattened drag is always Bézier, so it stays 0.
            if (volume != TechDefaultVolume || pan != TechDefaultPan)
            {
                return string.Join("|", "E", "Drag",
                    pulse.ToString(CultureInfo.InvariantCulture),
                    lane.ToString(CultureInfo.InvariantCulture),
                    volume.ToString(CultureInfo.InvariantCulture),
                    pan.ToString(CultureInfo.InvariantCulture),
                    "0",
                    sound);
            }
            return string.Join("|", "Drag",
                pulse.ToString(CultureInfo.InvariantCulture),
                lane.ToString(CultureInfo.InvariantCulture),
                sound);
        }

        /// <summary>
        /// Two anchors with their control points pulled onto the straight line between them,
        /// so the Bézier interpolation the game performs stays on that line. The first node's
        /// left control and the last node's right control are ignored by the game but are
        /// still emitted as 0, as its editor writes them.
        /// </summary>
        private static List<string> StraightDragNodes(int durationPulses)
        {
            int duration = Math.Max(1, durationPulses);
            double half = duration / 2.0;
            return new List<string>
            {
                "0|0|0|0|" + Float(half) + "|0",
                Float(duration) + "|0|" + Float(-half) + "|0|0|0"
            };
        }

        private static int OutVolume(byte model)
        {
            if (model == ModelDefaultVolume)
            {
                return TechDefaultVolume;
            }
            return Math.Max(0, Math.Min(100, (int)model));
        }

        private static int OutPan(byte model)
        {
            if (model == ModelDefaultPan)
            {
                return TechDefaultPan;
            }
            // Reverse of the reader's 64 + pan * 0.64.
            int pan = (int)Math.Round((model - ModelDefaultPan) / 0.64,
                MidpointRounding.AwayFromZero);
            return Math.Max(-100, Math.Min(100, pan));
        }

        private static int ToPulse(int virtualTick)
        {
            // virtualTick * 240 / 288 = virtualTick * 5 / 6, rounded.
            long rounded = (long)Math.Round(virtualTick * 5L / 6.0,
                MidpointRounding.AwayFromZero);
            return (int)Math.Max(0, Math.Min(int.MaxValue, rounded));
        }

        private static long Key(int lane, int pulse)
        {
            return ((long)lane << 32) | (uint)pulse;
        }

        private static string Float(double value)
        {
            if (Math.Abs(value - Math.Round(value)) < 0.0001)
            {
                return ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
            }
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        // -------------------------------------------------------------------------------
        // Wire DTOs: property names are camelCased by the serializer to match track.tech.
        // -------------------------------------------------------------------------------

        private sealed class TrackFileDto
        {
            public string version { get; set; }
            public TrackMetadataDto trackMetadata { get; set; }
            public List<PatternDto> patterns { get; set; }
        }

        private sealed class TrackMetadataDto
        {
            public string guid { get; set; }
            public string title { get; set; }
            public string artist { get; set; }
            public string genre { get; set; }
            public string additionalCredits { get; set; }
            public string eyecatchImage { get; set; }
            public string previewTrack { get; set; }
            public double previewStartTime { get; set; }
            public double previewEndTime { get; set; }
            public string previewBga { get; set; }
            public bool autoOrderPatterns { get; set; }
        }

        private sealed class PatternDto
        {
            public PatternMetadataDto patternMetadata { get; set; }
            public List<BpmEventDto> bpmEvents { get; set; }
            public List<TimeStopDto> timeStops { get; set; }
            public List<string> packedNotes { get; set; }
            public List<string> packedHoldNotes { get; set; }
            public List<PackedDragDto> packedDragNotes { get; set; }
        }

        private sealed class PatternMetadataDto
        {
            public string guid { get; set; }
            public string patternName { get; set; }
            public int level { get; set; }
            public int controlScheme { get; set; }
            public int playableLanes { get; set; }
            public string author { get; set; }
            public string backingTrack { get; set; }
            public string backImage { get; set; }
            public string bga { get; set; }
            public double bgaOffset { get; set; }
            public bool waitForEndOfBga { get; set; }
            public bool playBgaOnLoop { get; set; }
            public double firstBeatOffset { get; set; }
            public double initBpm { get; set; }
            public int bps { get; set; }
        }

        private sealed class BpmEventDto
        {
            public int pulse { get; set; }
            public double bpm { get; set; }
        }

        private sealed class TimeStopDto
        {
            public int pulse { get; set; }
            public int duration { get; set; }
        }

        private sealed class PackedDragDto
        {
            public string packedNote { get; set; }
            public List<string> packedNodes { get; set; }
        }
    }
}
