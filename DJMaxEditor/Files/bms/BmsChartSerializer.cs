using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DJMaxEditor.DJMax;
using DJMaxEditor.Files.FormatDetection;

namespace DJMaxEditor.Files.bms
{
    /// <summary>
    /// Reader/writer for the classic text BMS family (.bms/.bme/.bml/.pms).
    /// The editor clock is 48 ticks per quarter note, with six virtual sub-ticks.
    /// </summary>
    internal static class BmsChartSerializer
    {
        private const int VirtualMeasure = 192 * EventData.VirtualTickSize;
        private const string Base36 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        private static readonly string[] DefaultPlayableChannels =
            { "11", "12", "13", "14", "15", "16", "18", "19" };

        public static PlayerData Parse(string text)
        {
            if (text == null)
                throw new ChartLoadException(ChartLoadError.MalformedHeader, "BMS text is null.");

            var metadata = new BmsMetadata();
            var wav = new Dictionary<int, string>();
            var bpmDefinitions = new Dictionary<int, double>();
            var bgmSequences = new List<Sequence>();
            var mergedSequences = new Dictionary<string, Sequence>();
            double initialBpm = 120;
            int lnObj = -1;
            int maxMeasure = 0;
            bool sawData = false;

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int lineNumber = 0; lineNumber < lines.Length; lineNumber++)
            {
                string line = lines[lineNumber].Trim();
                if (line.Length == 0 || line[0] == '*') continue;
                if (line[0] != '#') continue;

                int colon = line.IndexOf(':');
                if (colon == 6 && IsDigits(line, 1, 3))
                {
                    int measure = int.Parse(line.Substring(1, 3), CultureInfo.InvariantCulture);
                    string channel = line.Substring(4, 2).ToUpperInvariant();
                    string content = line.Substring(7).Trim().ToUpperInvariant();
                    maxMeasure = Math.Max(maxMeasure, measure);
                    sawData = true;

                    if (channel == "02")
                    {
                        double ratio;
                        if (!double.TryParse(content, NumberStyles.Float, CultureInfo.InvariantCulture, out ratio) ||
                            ratio <= 0)
                            throw Malformed(lineNumber, "invalid #mmm02 measure length");
                        metadata.MeasureLengthRatios[measure] = ratio;
                        continue;
                    }

                    if ((content.Length & 1) != 0)
                        throw Malformed(lineNumber, "object data must contain two characters per slot");
                    if (content.Length == 0) continue;

                    if (channel != "01" && channel != "03" && channel != "08" &&
                        CanonicalPlayableChannel(channel) == null)
                    {
                        metadata.PreservedDataLines.Add("#" +
                            measure.ToString("000", CultureInfo.InvariantCulture) +
                            channel + ":" + content);
                        continue;
                    }

                    var sequence = new Sequence(measure, channel, ParseObjects(content, lineNumber));
                    if (channel == "01")
                    {
                        bgmSequences.Add(sequence);
                    }
                    else
                    {
                        string key = measure.ToString(CultureInfo.InvariantCulture) + ":" + channel;
                        Sequence existing;
                        mergedSequences[key] = mergedSequences.TryGetValue(key, out existing)
                            ? Merge(existing, sequence)
                            : sequence;
                    }
                    continue;
                }

                int delimiter = IndexOfWhitespace(line);
                string command = (delimiter < 0 ? line.Substring(1) : line.Substring(1, delimiter - 1))
                    .Trim().ToUpperInvariant();
                string value = delimiter < 0 ? "" : line.Substring(delimiter).Trim();
                if (command.Length == 0) continue;

                if (command == "TITLE") metadata.Title = value;
                else if (command == "ARTIST") metadata.Artist = value;
                else if (command == "GENRE") metadata.Genre = value;
                else if (command == "PLAYER") metadata.Player = ParseInt(value, metadata.Player);
                else if (command == "PLAYLEVEL") metadata.PlayLevel = ParseInt(value, metadata.PlayLevel);
                else if (command == "RANK") metadata.Rank = ParseInt(value, metadata.Rank);
                else if (command == "TOTAL") metadata.Total = ParseDouble(value, metadata.Total);
                else if (command == "VOLWAV") metadata.VolWav = ParseDouble(value, metadata.VolWav);
                else if (command == "BPM") initialBpm = ParsePositiveDouble(value, lineNumber, "#BPM");
                else if (command == "LNOBJ") lnObj = ParseBase36(value);
                else if (command.StartsWith("WAV", StringComparison.Ordinal) && command.Length == 5)
                {
                    int id = ParseBase36(command.Substring(3));
                    if (id >= 0) wav[id] = value;
                }
                else if (command.StartsWith("BPM", StringComparison.Ordinal) && command.Length == 5)
                {
                    int id = ParseBase36(command.Substring(3));
                    if (id >= 0) bpmDefinitions[id] = ParsePositiveDouble(value, lineNumber, command);
                }
                else
                {
                    metadata.AdditionalHeaderLines.Add(line);
                }
            }

            if (!sawData)
                throw new ChartLoadException(ChartLoadError.MalformedHeader,
                    "BMS contains no #mmmcc chart data.");

            var measureStarts = BuildMeasureStarts(metadata, maxMeasure + 2);
            var player = new PlayerData
            {
                TickPerMinute = 192,
                Tempo = (float)initialBpm,
                SourceFormat = ChartFormat.BmsClassic,
                IsReadOnly = false,
                BmsMetadata = metadata
            };

            var instruments = new Dictionary<int, InstrumentData>();
            AddInstrument(player, instruments, 0, "none");
            foreach (var pair in wav.OrderBy(x => x.Key))
                AddInstrument(player, instruments, pair.Key, pair.Value);

            Func<int, InstrumentData> instrumentFor = id =>
            {
                InstrumentData instrument;
                if (instruments.TryGetValue(id, out instrument)) return instrument;
                AddInstrument(player, instruments, id, "missing_" + ToBase36(id) + ".wav");
                return instruments[id];
            };

            if (bgmSequences.Count > 0)
            {
                var bgm = AddTrack(player, metadata, "BMS BGM", "01");
                bgm.AddEvents(bgmSequences
                    .SelectMany(sequence => EnumerateObjects(new[] { sequence }, measureStarts))
                    .Select(item => NewNote(item.VirtualTick, instrumentFor(item.ObjectId))));
            }

            var playableChannels = mergedSequences.Values
                .Select(x => CanonicalPlayableChannel(x.Channel))
                .Where(x => x != null)
                .Distinct()
                .OrderBy(x => ParseBase36(x))
                .ToArray();

            foreach (string channel in playableChannels)
            {
                var track = AddTrack(player, metadata, "BMS Lane " + channel, channel);
                AddNormalLane(track, channel, mergedSequences.Values, measureStarts, instrumentFor, lnObj);
                AddLongLane(track, channel, mergedSequences.Values, measureStarts, instrumentFor);
            }

            var tempoEvents = ReadTempoEvents(mergedSequences.Values, measureStarts, bpmDefinitions).ToArray();
            if (tempoEvents.Length > 0)
            {
                var tempoTrack = AddTrack(player, metadata, "BMS Tempo", "08");
                tempoTrack.AddEvents(tempoEvents);
            }

            // A valid BMS may contain timing only. Keep the generic model safe from empty-track Max().
            if (player.Tracks.Count == 0) AddTrack(player, metadata, "BMS BGM", "01");
            return player;
        }

        public static string Serialize(PlayerData player)
        {
            if (player == null) throw new ArgumentNullException(nameof(player));
            var metadata = player.BmsMetadata ?? new BmsMetadata();
            double initialBpm = player.Tempo > 0 ? player.Tempo : 120;

            var sb = new StringBuilder();
            sb.AppendLine("*---------------------- Exported by DJMax Editor");
            sb.AppendLine("#PLAYER " + Math.Max(1, metadata.Player));
            sb.AppendLine("#GENRE " + SafeHeader(metadata.Genre));
            sb.AppendLine("#TITLE " + SafeHeader(metadata.Title));
            sb.AppendLine("#ARTIST " + SafeHeader(metadata.Artist));
            sb.AppendLine("#BPM " + FormatNumber(initialBpm));
            sb.AppendLine("#PLAYLEVEL " + Math.Max(0, metadata.PlayLevel));
            sb.AppendLine("#RANK " + Math.Max(0, metadata.Rank));
            sb.AppendLine("#TOTAL " + FormatNumber(metadata.Total));
            sb.AppendLine("#VOLWAV " + FormatNumber(metadata.VolWav));
            foreach (string header in metadata.AdditionalHeaderLines)
                if (!string.IsNullOrWhiteSpace(header)) sb.AppendLine(header);
            sb.AppendLine();

            var instrumentIds = new Dictionary<InstrumentData, int>();
            var usedIds = new HashSet<int>();
            foreach (var instrument in player.Instruments.Where(x => x != null && x.InsNum > 0))
            {
                int id = instrument.InsNum;
                if (id <= 0 || id >= 1296 || usedIds.Contains(id))
                    id = FirstFreeId(usedIds);
                usedIds.Add(id);
                instrumentIds[instrument] = id;
                sb.AppendLine("#WAV" + ToBase36(id) + " " +
                    (string.IsNullOrWhiteSpace(instrument.Name) ? "missing_" + ToBase36(id) + ".wav" : instrument.Name));
            }

            var tempoIds = new Dictionary<double, int>();
            var entries = new List<OutputEntry>();
            var respectTrackChannels = InferRespectTrackChannels(player);
            int defaultLane = 0;
            int maxVirtual = 0;

            foreach (var track in player.Tracks)
            {
                string channel;
                if (!metadata.TrackChannels.TryGetValue(track.Idx, out channel))
                {
                    if (respectTrackChannels != null)
                    {
                        if (!respectTrackChannels.TryGetValue(track.Idx, out channel))
                            channel = "01";
                    }
                    else
                    {
                        channel = defaultLane < DefaultPlayableChannels.Length
                            ? DefaultPlayableChannels[defaultLane++]
                            : "01";
                    }
                }
                channel = channel.ToUpperInvariant();

                foreach (var ev in track.Events)
                {
                    maxVirtual = Math.Max(maxVirtual, ev.VirtualTick + ev.VirtualDuration);
                    if (ev.EventType == EventType.Tempo && ev.Tempo > 0)
                    {
                        double bpm = ev.Tempo;
                        int id;
                        if (!tempoIds.TryGetValue(bpm, out id))
                        {
                            id = tempoIds.Count + 1;
                            if (id >= 1296) throw new InvalidOperationException("BMS supports at most 1295 extended BPM definitions.");
                            tempoIds[bpm] = id;
                        }
                        entries.Add(new OutputEntry(ev.VirtualTick, "08", id));
                        continue;
                    }
                    if (ev.EventType != EventType.Note || ev.Instrument == null) continue;

                    int objectId;
                    if (!instrumentIds.TryGetValue(ev.Instrument, out objectId))
                    {
                        objectId = ev.Instrument.InsNum > 0 && ev.Instrument.InsNum < 1296
                            ? ev.Instrument.InsNum
                            : 0;
                    }
                    if (objectId == 0) continue;

                    bool isPlayable = CanonicalPlayableChannel(channel) != null;
                    bool isLong = isPlayable && ev.VirtualDuration > 6 * EventData.VirtualTickSize;
                    if (isLong)
                    {
                        string longChannel = LongChannelFor(channel);
                        entries.Add(new OutputEntry(ev.VirtualTick, longChannel, objectId));
                        entries.Add(new OutputEntry(ev.VirtualTick + ev.VirtualDuration, longChannel, objectId));
                    }
                    else
                    {
                        entries.Add(new OutputEntry(ev.VirtualTick, isPlayable ? channel : "01", objectId));
                    }
                }
            }

            if (tempoIds.Count > 0)
            {
                sb.AppendLine();
                foreach (var tempo in tempoIds.OrderBy(x => x.Value))
                    sb.AppendLine("#BPM" + ToBase36(tempo.Value) + " " + FormatNumber(tempo.Key));
            }

            var measures = BuildOutputMeasures(metadata, maxVirtual);
            sb.AppendLine();
            foreach (var ratio in metadata.MeasureLengthRatios.OrderBy(x => x.Key))
                sb.AppendLine("#" + ratio.Key.ToString("000", CultureInfo.InvariantCulture) +
                    "02:" + FormatNumber(ratio.Value));

            var located = entries.Select(x => Locate(x, measures)).ToArray();
            foreach (var group in located.Where(x => x.Channel != "01")
                .GroupBy(x => x.Measure.ToString("000", CultureInfo.InvariantCulture) + x.Channel)
                .OrderBy(x => x.Key))
            {
                var first = group.First();
                sb.AppendLine(EncodeLine(first.Measure, first.Channel, measures[first.Measure].Length, group));
            }

            // Channel 01 is additive in classic BMS. One line per object preserves simultaneous BGM sounds.
            foreach (var bgm in located.Where(x => x.Channel == "01")
                .OrderBy(x => x.Measure).ThenBy(x => x.Offset).ThenBy(x => x.ObjectId))
            {
                sb.AppendLine(EncodeLine(bgm.Measure, bgm.Channel, measures[bgm.Measure].Length,
                    new[] { bgm }));
            }
            foreach (string line in metadata.PreservedDataLines)
                if (!string.IsNullOrWhiteSpace(line)) sb.AppendLine(line);
            return sb.ToString();
        }

        public static int CountRequiredKeysounds(PlayerData player)
        {
            if (player == null) throw new ArgumentNullException(nameof(player));
            return player.Instruments.Count(x => x != null && x.InsNum > 0);
        }

        public static string Decode(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                return new UTF8Encoding(false, true).GetString(data, 3, data.Length - 3);
            try
            {
                return new UTF8Encoding(false, true).GetString(data);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.GetEncoding(932).GetString(data);
            }
        }

        private static void AddNormalLane(TrackData track, string channel, IEnumerable<Sequence> all,
            int[] starts, Func<int, InstrumentData> instrumentFor, int lnObj)
        {
            EventData pending = null;
            var events = new List<EventData>();
            foreach (var item in EnumerateObjects(all.Where(x => x.Channel == channel), starts))
            {
                if (lnObj > 0 && item.ObjectId == lnObj)
                {
                    if (pending != null && item.VirtualTick > pending.VirtualTick)
                        pending.VirtualDuration = ClampUShort(item.VirtualTick - pending.VirtualTick);
                    pending = null;
                    continue;
                }
                var note = NewNote(item.VirtualTick, instrumentFor(item.ObjectId));
                events.Add(note);
                pending = note;
            }
            track.AddEvents(events);
        }

        private static void AddLongLane(TrackData track, string channel, IEnumerable<Sequence> all,
            int[] starts, Func<int, InstrumentData> instrumentFor)
        {
            string longChannel = LongChannelFor(channel);
            EventData pending = null;
            var events = new List<EventData>();
            foreach (var item in EnumerateObjects(all.Where(x => x.Channel == longChannel), starts))
            {
                if (pending == null)
                {
                    pending = NewNote(item.VirtualTick, instrumentFor(item.ObjectId));
                    events.Add(pending);
                }
                else
                {
                    if (item.VirtualTick > pending.VirtualTick)
                        pending.VirtualDuration = ClampUShort(item.VirtualTick - pending.VirtualTick);
                    pending = null;
                }
            }
            track.AddEvents(events);
        }

        private static IEnumerable<EventData> ReadTempoEvents(IEnumerable<Sequence> sequences, int[] starts,
            IDictionary<int, double> definitions)
        {
            foreach (var item in EnumerateObjects(sequences.Where(x => x.Channel == "03"), starts))
            {
                int bpm;
                if (!int.TryParse(ToBase36(item.ObjectId), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out bpm) || bpm <= 0) continue;
                yield return NewTempo(item.VirtualTick, bpm);
            }
            foreach (var item in EnumerateObjects(sequences.Where(x => x.Channel == "08"), starts))
            {
                double bpm;
                if (definitions.TryGetValue(item.ObjectId, out bpm) && bpm > 0)
                    yield return NewTempo(item.VirtualTick, bpm);
            }
        }

        private static IEnumerable<ObjectAtTime> EnumerateObjects(IEnumerable<Sequence> sequences, int[] starts)
        {
            return sequences.SelectMany(sequence => sequence.Objects
                    .Select((id, index) => new { id, index })
                    .Where(x => x.id != 0)
                    .Select(x => new ObjectAtTime(
                        starts[sequence.Measure] +
                        (int)Math.Round((starts[sequence.Measure + 1] - starts[sequence.Measure]) *
                            (double)x.index / sequence.Objects.Length, MidpointRounding.AwayFromZero),
                        x.id)))
                .OrderBy(x => x.VirtualTick);
        }

        private static EventData NewNote(int virtualTick, InstrumentData instrument)
        {
            return new EventData
            {
                EventType = EventType.Note,
                VirtualTick = virtualTick,
                Instrument = instrument
            };
        }

        private static EventData NewTempo(int virtualTick, double bpm)
        {
            return new EventData
            {
                EventType = EventType.Tempo,
                VirtualTick = virtualTick,
                Tempo = (float)bpm
            };
        }

        private static TrackData AddTrack(PlayerData player, BmsMetadata metadata, string name, string channel)
        {
            var track = new TrackData(player.Tracks.Count) { TrackName = name };
            player.Tracks.AddTrack(track);
            metadata.TrackChannels[track.Idx] = channel;
            return track;
        }

        private static void AddInstrument(PlayerData player, IDictionary<int, InstrumentData> map,
            int id, string name)
        {
            if (id < 0 || id > ushort.MaxValue || map.ContainsKey(id)) return;
            var instrument = new InstrumentData { InsNum = (ushort)id, Name = name ?? "" };
            map[id] = instrument;
            player.Instruments.Add(instrument);
        }

        private static int[] BuildMeasureStarts(BmsMetadata metadata, int count)
        {
            var starts = new int[Math.Max(2, count + 1)];
            for (int measure = 0; measure < starts.Length - 1; measure++)
            {
                double ratio;
                if (!metadata.MeasureLengthRatios.TryGetValue(measure, out ratio)) ratio = 1;
                starts[measure + 1] = starts[measure] +
                    Math.Max(1, (int)Math.Round(VirtualMeasure * ratio, MidpointRounding.AwayFromZero));
            }
            return starts;
        }

        private static List<OutputMeasure> BuildOutputMeasures(BmsMetadata metadata, int maxVirtual)
        {
            var result = new List<OutputMeasure>();
            int start = 0;
            int measure = 0;
            do
            {
                double ratio;
                if (!metadata.MeasureLengthRatios.TryGetValue(measure, out ratio)) ratio = 1;
                int length = Math.Max(1,
                    (int)Math.Round(VirtualMeasure * ratio, MidpointRounding.AwayFromZero));
                result.Add(new OutputMeasure(start, length));
                start += length;
                measure++;
                if (measure > 999) throw new InvalidOperationException("Classic BMS supports measures 000 through 999.");
            } while (start <= maxVirtual || measure <= metadata.MeasureLengthRatios.Keys.DefaultIfEmpty(0).Max());
            result.Add(new OutputMeasure(start, VirtualMeasure));
            return result;
        }

        private static LocatedEntry Locate(OutputEntry entry, IList<OutputMeasure> measures)
        {
            int measure = 0;
            while (measure + 1 < measures.Count && entry.VirtualTick >= measures[measure + 1].Start)
                measure++;
            return new LocatedEntry(measure, entry.Channel,
                Math.Max(0, entry.VirtualTick - measures[measure].Start), entry.ObjectId);
        }

        private static string EncodeLine(int measure, string channel, int measureLength,
            IEnumerable<LocatedEntry> source)
        {
            var entries = source.ToArray();
            int gcd = measureLength;
            foreach (var entry in entries) gcd = Gcd(gcd, entry.Offset);
            int divisions = Math.Max(1, measureLength / Math.Max(1, gcd));
            var objects = Enumerable.Repeat("00", divisions).ToArray();
            foreach (var entry in entries)
            {
                int slot = (int)Math.Round(entry.Offset * (double)divisions / measureLength);
                if (slot >= 0 && slot < divisions) objects[slot] = ToBase36(entry.ObjectId);
            }
            return "#" + measure.ToString("000", CultureInfo.InvariantCulture) + channel + ":" +
                string.Concat(objects);
        }

        private static Sequence Merge(Sequence left, Sequence right)
        {
            int length = Lcm(left.Objects.Length, right.Objects.Length);
            if (length <= 0 || length > 65536)
                throw new ChartLoadException(ChartLoadError.InvalidCount,
                    "BMS channel resolution is too large.");
            var objects = new int[length];
            for (int i = 0; i < left.Objects.Length; i++)
                if (left.Objects[i] != 0) objects[i * (length / left.Objects.Length)] = left.Objects[i];
            for (int i = 0; i < right.Objects.Length; i++)
                if (right.Objects[i] != 0) objects[i * (length / right.Objects.Length)] = right.Objects[i];
            return new Sequence(left.Measure, left.Channel, objects);
        }

        private static int[] ParseObjects(string content, int line)
        {
            var result = new int[content.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                int value = ParseBase36(content.Substring(i * 2, 2));
                if (value < 0) throw Malformed(line, "invalid base-36 object id");
                result[i] = value;
            }
            return result;
        }

        internal static Dictionary<uint, string> InferRespectTrackChannels(PlayerData player)
        {
            if (player.SourceFormat != ChartFormat.TrailerRespectV)
                return null;

            var noteTracks = new HashSet<uint>(player.Tracks
                .Where(track => track.Events.Any(ev => ev.EventType == EventType.Note))
                .Select(track => track.Idx));

            // Respect's standard DJMAX layouts store gameplay on tracks 3..6 (4B), 3..7
            // (5B), 3..8 (6B), or 3..8 + 10..11 (8B). Setup, preview, and autoplay
            // keysounds live on other tracks and must remain channel 01 BGM.
            bool isEightButton = noteTracks.Contains(10) || noteTracks.Contains(11);
            int laneCount;
            uint[] sourceTracks;
            if (isEightButton)
            {
                laneCount = 8;
                sourceTracks = new uint[] { 3, 4, 5, 6, 7, 8, 10, 11 };
            }
            else
            {
                uint highestGameplayTrack = noteTracks
                    .Where(track => track >= 3 && track <= 8)
                    .Concat(new uint[] { 0 })
                    .Max();
                if (highestGameplayTrack == 0)
                    return new Dictionary<uint, string>();

                laneCount = Math.Max(4, Math.Min(6, (int)highestGameplayTrack - 2));
                sourceTracks = Enumerable.Range(3, laneCount).Select(x => (uint)x).ToArray();
            }

            var result = new Dictionary<uint, string>();
            for (int lane = 0; lane < laneCount; lane++)
                result[sourceTracks[lane]] = DefaultPlayableChannels[lane];
            return result;
        }

        private static string CanonicalPlayableChannel(string channel)
        {
            if (channel == null || channel.Length != 2) return null;
            char family = channel[0];
            if (family == '5') family = '1';
            else if (family == '6') family = '2';
            if (family != '1' && family != '2') return null;
            int lane = Base36.IndexOf(char.ToUpperInvariant(channel[1]));
            if (lane < 1) return null;
            return family + channel.Substring(1).ToUpperInvariant();
        }

        private static string LongChannelFor(string normal)
        {
            string canonical = CanonicalPlayableChannel(normal);
            if (canonical == null) throw new InvalidOperationException("Not a playable BMS channel: " + normal);
            return (canonical[0] == '1' ? "5" : "6") + canonical.Substring(1);
        }

        private static int ParseBase36(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Trim().Length != 2) return -1;
            string s = value.Trim().ToUpperInvariant();
            int high = Base36.IndexOf(s[0]);
            int low = Base36.IndexOf(s[1]);
            return high < 0 || low < 0 ? -1 : high * 36 + low;
        }

        private static string ToBase36(int value)
        {
            if (value < 0 || value >= 1296) throw new ArgumentOutOfRangeException(nameof(value));
            return new string(new[] { Base36[value / 36], Base36[value % 36] });
        }

        private static int FirstFreeId(ISet<int> used)
        {
            for (int i = 1; i < 1296; i++) if (!used.Contains(i)) return i;
            throw new InvalidOperationException("BMS supports at most 1295 keysound definitions.");
        }

        private static int IndexOfWhitespace(string line)
        {
            for (int i = 1; i < line.Length; i++)
                if (char.IsWhiteSpace(line[i])) return i;
            return -1;
        }

        private static bool IsDigits(string value, int start, int count)
        {
            if (value.Length < start + count) return false;
            for (int i = start; i < start + count; i++)
                if (value[i] < '0' || value[i] > '9') return false;
            return true;
        }

        private static int ParseInt(string value, int fallback)
        {
            int parsed;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                ? parsed : fallback;
        }

        private static double ParseDouble(string value, double fallback)
        {
            double parsed;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                ? parsed : fallback;
        }

        private static double ParsePositiveDouble(string value, int line, string command)
        {
            double parsed;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) || parsed <= 0)
                throw Malformed(line, command + " must be a positive number");
            return parsed;
        }

        private static string SafeHeader(string value)
        {
            return (value ?? "").Replace("\r", " ").Replace("\n", " ");
        }

        private static string FormatNumber(double value)
        {
            return value.ToString("0.##########", CultureInfo.InvariantCulture);
        }

        private static ushort ClampUShort(int value)
        {
            return (ushort)Math.Max(0, Math.Min(ushort.MaxValue, value));
        }

        private static ChartLoadException Malformed(int zeroBasedLine, string message)
        {
            return new ChartLoadException(ChartLoadError.MalformedHeader,
                "BMS line " + (zeroBasedLine + 1) + ": " + message + ".");
        }

        private static int Gcd(int a, int b)
        {
            a = Math.Abs(a);
            b = Math.Abs(b);
            while (b != 0)
            {
                int t = a % b;
                a = b;
                b = t;
            }
            return a;
        }

        private static int Lcm(int a, int b)
        {
            if (a == 0 || b == 0) return 0;
            long value = (long)a / Gcd(a, b) * b;
            return value > int.MaxValue ? -1 : (int)value;
        }

        private sealed class Sequence
        {
            public readonly int Measure;
            public readonly string Channel;
            public readonly int[] Objects;

            public Sequence(int measure, string channel, int[] objects)
            {
                Measure = measure;
                Channel = channel;
                Objects = objects;
            }
        }

        private sealed class ObjectAtTime
        {
            public readonly int VirtualTick;
            public readonly int ObjectId;

            public ObjectAtTime(int virtualTick, int objectId)
            {
                VirtualTick = virtualTick;
                ObjectId = objectId;
            }
        }

        private sealed class OutputEntry
        {
            public readonly int VirtualTick;
            public readonly string Channel;
            public readonly int ObjectId;

            public OutputEntry(int virtualTick, string channel, int objectId)
            {
                VirtualTick = virtualTick;
                Channel = channel;
                ObjectId = objectId;
            }
        }

        private sealed class LocatedEntry
        {
            public readonly int Measure;
            public readonly string Channel;
            public readonly int Offset;
            public readonly int ObjectId;

            public LocatedEntry(int measure, string channel, int offset, int objectId)
            {
                Measure = measure;
                Channel = channel;
                Offset = offset;
                ObjectId = objectId;
            }
        }

        private sealed class OutputMeasure
        {
            public readonly int Start;
            public readonly int Length;

            public OutputMeasure(int start, int length)
            {
                Start = start;
                Length = length;
            }
        }
    }
}
