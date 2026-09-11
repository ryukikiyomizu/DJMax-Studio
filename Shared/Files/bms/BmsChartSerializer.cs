using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DJMaxEditor.DJMax;
using DJMaxEditor.Diagnostics;
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
                        {
                            // A bad measure ratio used to abort the whole file. One malformed line
                            // loses one measure length; the chart around it survives.
                            DiagnosticLog.Write("open.bms",
                                "line " + (lineNumber + 1) + ": invalid #mmm02 measure length \"" +
                                content + "\" - line ignored.");
                            continue;
                        }
                        metadata.MeasureLengthRatios[measure] = ratio;
                        continue;
                    }

                    if ((content.Length & 1) != 0)
                    {
                        DiagnosticLog.Write("open.bms",
                            "line " + (lineNumber + 1) + ": object data must contain two characters " +
                            "per slot - line ignored.");
                        continue;
                    }
                    if (content.Length == 0) continue;

                    if (channel != "01" && channel != "03" && channel != "08" &&
                        CanonicalPlayableChannel(channel) == null)
                    {
                        metadata.PreservedDataLines.Add("#" +
                            measure.ToString("000", CultureInfo.InvariantCulture) +
                            channel + ":" + content);
                        continue;
                    }

                    try
                    {
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
                    }
                    catch (ChartLoadException ex)
                    {
                        // Unparsable object slots and unmergeable channel resolutions used to abort
                        // the whole file here. They are properties of one line (or of one channel on
                        // one measure), so the line is dropped and everything around it loads.
                        DiagnosticLog.Write("open.bms",
                            "line " + (lineNumber + 1) + ": " + ex.Message + " - line ignored.");
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

            // Accompaniment after the lanes. A busy chart has more BGM voices than it has keys, so
            // putting them first would bury the gameplay tracks under thirty keysound rows in every
            // surface that draws tracks in index order - the horizontal timeline, the track list, the
            // note picker. Column order on the vertical surface is set by the channel map instead, so
            // this only moves the *track* numbering, and it moves it to agree with the columns.
            AddBgmTracks(player, metadata, bgmSequences, measureStarts, instrumentFor);

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
                // Most real-world BMS is Shift-JIS. .NET 8 ships only UTF-8, UTF-16, UTF-32 and
                // ASCII, so code page 932 resolves only when the CodePages provider was registered
                // at startup. If the table is somehow still missing, ISO-8859-1 (and finally the
                // platform's Latin-1) is a total mapping - every byte decodes - so a chart is never
                // lost to an encoding gap; worst case the keysound names are mojibake.
                try
                {
                    return Encoding.GetEncoding(932).GetString(data);
                }
                catch (ArgumentException)
                {
                    return DecodeLatin1(data);
                }
                catch (NotSupportedException)
                {
                    return DecodeLatin1(data);
                }
            }
        }

        private static string DecodeLatin1(byte[] data)
        {
            try
            {
                return Encoding.GetEncoding(28591).GetString(data);
            }
            catch (ArgumentException)
            {
                return Encoding.Latin1.GetString(data);
            }
            catch (NotSupportedException)
            {
                return Encoding.Latin1.GetString(data);
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

        // NewNote/NewTempo/AddTrack/AddInstrument/ClampUShort are internal rather than private:
        // they are the one implementation of "assemble the shared model from a BMS-family chart",
        // and the bmson reader (the other partial of BmsonChartSerializer) builds its PlayerData
        // with them too.
        internal static EventData NewNote(int virtualTick, InstrumentData instrument)
        {
            return new EventData
            {
                EventType = EventType.Note,
                VirtualTick = virtualTick,
                Instrument = instrument
            };
        }

        internal static EventData NewTempo(int virtualTick, double bpm)
        {
            return new EventData
            {
                EventType = EventType.Tempo,
                VirtualTick = virtualTick,
                Tempo = (float)bpm
            };
        }

        /// <summary>
        /// Turns the chart's channel-01 lines into one track per simultaneous voice.
        ///
        /// <para>
        /// Channel 01 is additive: a measure that plays four keysounds at once writes four separate
        /// <c>#mmm01:</c> lines, and every BMS editor shows those as four side-by-side BGM columns.
        /// Flattening them into a single track - which is what this used to do - loses that shape:
        /// the timeline could only draw one BGM lane with every voice stacked inside it, so a chart
        /// with a dozen accompaniment layers looked like a chart with one.
        /// </para>
        /// <para>
        /// The slot a line lands in is its ordinal among that measure's own 01 lines, in file order,
        /// which is the same rule the editors use. Slots are independent per measure, so voice 3 of
        /// measure 7 has nothing to do with voice 3 of measure 8 - it is a column of the score, not
        /// an instrument. Export is unaffected either way: the writer emits one line per channel-01
        /// object regardless of which track holds it.
        /// </para>
        /// </summary>
        private static void AddBgmTracks(
            PlayerData player,
            BmsMetadata metadata,
            List<Sequence> bgmSequences,
            int[] measureStarts,
            Func<int, InstrumentData> instrumentFor)
        {
            if (bgmSequences.Count == 0) return;

            var slotsPerMeasure = new Dictionary<int, int>();
            var bySlot = new List<List<Sequence>>();
            foreach (Sequence sequence in bgmSequences)
            {
                int used;
                slotsPerMeasure.TryGetValue(sequence.Measure, out used);
                slotsPerMeasure[sequence.Measure] = used + 1;
                while (bySlot.Count <= used) bySlot.Add(new List<Sequence>());
                bySlot[used].Add(sequence);
            }

            bool numbered = bySlot.Count > 1;
            for (int slot = 0; slot < bySlot.Count; slot++)
            {
                string name = numbered
                    ? "BMS BGM " + (slot + 1).ToString(CultureInfo.InvariantCulture)
                    : "BMS BGM";
                var track = AddTrack(player, metadata, name, "01");
                track.AddEvents(EnumerateObjects(bySlot[slot], measureStarts)
                    .Select(item => NewNote(item.VirtualTick, instrumentFor(item.ObjectId))));
            }
        }

        internal static TrackData AddTrack(PlayerData player, BmsMetadata metadata, string name, string channel)
        {
            var track = new TrackData(player.Tracks.Count) { TrackName = name };
            player.Tracks.AddTrack(track);
            metadata.TrackChannels[track.Idx] = channel;
            return track;
        }

        internal static void AddInstrument(PlayerData player, IDictionary<int, InstrumentData> map,
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
            // (5B), or 3..8 (6B), with the L1/R1 shoulder inputs on 10..11 wherever the mode
            // has them: 8B is 6 mains plus shoulders, and the Clazziquai mission modes 4BFX /
            // 5BFX are 4 / 5 mains plus shoulders. Mains and shoulders are inferred separately
            // on purpose - reading "notes on 10/11" as 8B promoted a 4BFX chart onto the
            // 6-lane pitch with two ghost lanes. Setup, preview, and autoplay keysounds live
            // on other tracks and must remain channel 01 BGM. See
            // docs/respectv-playfield-research.md for the per-mode tables.
            bool hasShoulders = noteTracks.Contains(10) || noteTracks.Contains(11);
            uint highestGameplayTrack = noteTracks
                .Where(track => track >= 3 && track <= 8)
                .Concat(new uint[] { 0 })
                .Max();
            if (highestGameplayTrack == 0)
                return new Dictionary<uint, string>();

            int mains = Math.Max(4, Math.Min(6, (int)highestGameplayTrack - 2));
            var sourceTracks = new List<uint>(
                Enumerable.Range(3, mains).Select(x => (uint)x));
            if (hasShoulders)
            {
                // After the mains, so the channel order keeps them there too: the projector
                // numbers its regular lanes in track order and the shoulders must not take a
                // regular lane's number.
                sourceTracks.Add(10);
                sourceTracks.Add(11);
            }

            var result = new Dictionary<uint, string>();
            for (int lane = 0; lane < sourceTracks.Count; lane++)
                result[sourceTracks[lane]] = DefaultPlayableChannels[lane];
            return result;
        }

        /// <summary>
        /// Infers BMS channels for a chart that has none - a DJMax button chart drawn under a
        /// forced BMS layout - so it still draws keys and a turntable instead of a row of TRK
        /// columns. Positional off the DPC track schema: SIDE L (2) is the turntable outside key
        /// 1, the mains (3-8) are keys 1-6, SIDE R (9) is the key after the last main, the
        /// shoulders (10/11) are the extra-limb input, and BGA SYNC (1), MR (22) and BG 1-18
        /// (23-40) keep their timing/accompaniment roles as BPM and BGM.
        /// </summary>
        /// <remarks>
        /// The main count is read independently of the shoulders, the way
        /// <see cref="InferRespectTrackChannels"/> does: an 8B chart is 6 mains plus shoulders,
        /// a 4BFX-style chart 4 mains plus shoulders, and reading "notes on 10/11" as 8B would
        /// promote the latter onto two ghost keys. A 6B chart lands exactly on 7K+SC; 5B and 4B
        /// on 6K+SC and 5K+SC; 8B on 7K+SC with both shoulders on the pedal role, because one
        /// player's side has no ninth key. Two tracks sharing channel 17 draw two pedal columns -
        /// the DP layout already does that for 17/27 - so nothing is hidden and no lane is renamed.
        ///
        /// Transient: the layout uses this and never writes it to <c>BmsMetadata</c>, so the
        /// chart's own format is untouched and the writers keep seeing a chart with no channels.
        /// </remarks>
        internal static Dictionary<uint, string> InferButtonTrackChannels(PlayerData player)
        {
            var result = new Dictionary<uint, string>();
            if (player == null)
            {
                return result;
            }

            var noteTracks = new HashSet<uint>();
            foreach (TrackData track in player.Tracks)
            {
                foreach (EventData sourceEvent in track.Events)
                {
                    if (sourceEvent.EventType == EventType.Note)
                    {
                        noteTracks.Add(track.Idx);
                        break;
                    }
                }
            }

            bool shoulders = noteTracks.Contains(10) || noteTracks.Contains(11);
            uint highestMain = 0;
            foreach (uint track in noteTracks)
            {
                if (track >= 3 && track <= 8 && track > highestMain)
                {
                    highestMain = track;
                }
            }
            if (!shoulders && highestMain == 0)
            {
                return result;
            }

            // Keys 1-7 in channel ids: 11-15, then 18/19, because 16 is the turntable.
            string[] keys = { "11", "12", "13", "14", "15", "18", "19" };
            int mains = Math.Max(4, Math.Min(6, (int)highestMain - 2));
            result[2] = "16";
            for (int i = 0; i < mains; i++)
            {
                result[(uint)(3 + i)] = keys[i];
            }
            result[9] = keys[mains];
            if (shoulders)
            {
                result[10] = "17";
                result[11] = "17";
            }

            result[1] = "08";
            result[22] = "01";
            for (uint background = 23; background <= 40; background++)
            {
                result[background] = "01";
            }
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

        internal static ushort ClampUShort(int value)
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
