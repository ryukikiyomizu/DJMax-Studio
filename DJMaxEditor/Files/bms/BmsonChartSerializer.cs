using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DJMaxEditor.DJMax;

namespace DJMaxEditor.Files.bms
{
    /// <summary>
    /// Lossless keysound-capacity export for charts which exceed classic BMS's 01..ZZ object IDs.
    /// BMSON identifies audio by filename instead of a two-character object ID.
    /// </summary>
    internal static class BmsonChartSerializer
    {
        private const int Resolution = 1440;
        private const int PulsePerVirtualTick = 5;
        private static readonly string[] DefaultChannels =
            { "11", "12", "13", "14", "15", "16", "18", "19" };

        public static bool ShouldUseForClassicBmsOverflow(PlayerData player)
        {
            return player != null && BmsChartSerializer.CountRequiredKeysounds(player) > 1295;
        }

        public static string Serialize(PlayerData player)
        {
            if (player == null) throw new ArgumentNullException(nameof(player));

            BmsMetadata metadata = player.BmsMetadata ?? new BmsMetadata();
            Dictionary<uint, string> channels = ResolveTrackChannels(player);
            int playableLaneCount = channels.Values.Select(ChannelToLane)
                .Where(x => x > 0).DefaultIfEmpty(0).Max();
            string modeHint = playableLaneCount <= 5 ? "beat-5k" : "beat-7k";

            var notesByName = new Dictionary<string, List<BmsonNote>>(StringComparer.OrdinalIgnoreCase);
            var orderedNames = new List<string>();
            var tempoEvents = new List<EventData>();
            int maxPulse = 0;

            foreach (TrackData track in player.Tracks)
            {
                string channel;
                if (!channels.TryGetValue(track.Idx, out channel)) channel = "01";
                int lane = ChannelToLane(channel);

                foreach (EventData ev in track.Events)
                {
                    int pulse = CheckedPulse(ev.VirtualTick);
                    maxPulse = Math.Max(maxPulse, CheckedPulse(ev.VirtualTick + ev.VirtualDuration));
                    if (ev.EventType == EventType.Tempo && ev.Tempo > 0)
                    {
                        tempoEvents.Add(ev);
                        continue;
                    }
                    if (ev.EventType != EventType.Note || ev.Instrument == null ||
                        ev.Instrument.InsNum == 0) continue;

                    string name = string.IsNullOrWhiteSpace(ev.Instrument.Name)
                        ? "missing_" + ev.Instrument.InsNum.ToString(CultureInfo.InvariantCulture) + ".wav"
                        : ev.Instrument.Name;
                    List<BmsonNote> notes;
                    if (!notesByName.TryGetValue(name, out notes))
                    {
                        notes = new List<BmsonNote>();
                        notesByName[name] = notes;
                        orderedNames.Add(name);
                    }

                    int length = lane > 0 && ev.VirtualDuration > 6 * EventData.VirtualTickSize
                        ? CheckedPulse(ev.VirtualDuration)
                        : 0;
                    notes.Add(new BmsonNote(lane, pulse, length));
                }
            }

            var sb = new StringBuilder(Math.Max(4096, notesByName.Count * 96));
            sb.Append("{\"version\":\"1.0.0\",\"info\":{");
            AppendProperty(sb, "title", metadata.Title ?? "Untitled");
            sb.Append(',');
            AppendProperty(sb, "artist", metadata.Artist ?? "");
            sb.Append(',');
            AppendProperty(sb, "genre", metadata.Genre ?? "");
            sb.Append(",\"mode_hint\":\"").Append(modeHint).Append('"');
            sb.Append(",\"chart_name\":\"\"");
            sb.Append(",\"level\":").Append(Math.Max(0, metadata.PlayLevel));
            sb.Append(",\"init_bpm\":").Append(Number(player.Tempo > 0 ? player.Tempo : 120));
            sb.Append(",\"judge_rank\":").Append(Number(metadata.Rank));
            sb.Append(",\"total\":").Append(Number(metadata.Total));
            sb.Append(",\"resolution\":").Append(Resolution);
            sb.Append("},\"lines\":[");

            int measureLength = Resolution * 4;
            int lastMeasure = Math.Max(1, (maxPulse + measureLength - 1) / measureLength);
            for (int measure = 0; measure <= lastMeasure; measure++)
            {
                if (measure > 0) sb.Append(',');
                sb.Append("{\"y\":").Append(measure * measureLength).Append(",\"k\":0}");
            }

            sb.Append("],\"bpm_events\":[");
            EventData[] orderedTempo = tempoEvents.OrderBy(x => x.VirtualTick).ToArray();
            for (int i = 0; i < orderedTempo.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"y\":").Append(CheckedPulse(orderedTempo[i].VirtualTick))
                    .Append(",\"bpm\":").Append(Number(orderedTempo[i].Tempo)).Append('}');
            }

            sb.Append("],\"stop_events\":[],\"sound_channels\":[");
            for (int i = 0; i < orderedNames.Count; i++)
            {
                if (i > 0) sb.Append(',');
                string name = orderedNames[i];
                sb.Append('{');
                AppendProperty(sb, "name", name);
                sb.Append(",\"notes\":[");
                BmsonNote[] notes = notesByName[name].OrderBy(x => x.Y).ThenBy(x => x.X).ToArray();
                for (int j = 0; j < notes.Length; j++)
                {
                    if (j > 0) sb.Append(',');
                    BmsonNote note = notes[j];
                    sb.Append("{\"x\":").Append(note.X)
                        .Append(",\"y\":").Append(note.Y)
                        .Append(",\"l\":").Append(note.Length)
                        .Append(",\"c\":false}");
                }
                sb.Append("]}");
            }
            sb.Append("],\"bga\":{\"bga_header\":[],\"bga_events\":[],\"layer_events\":[],\"poor_events\":[]}}");
            return sb.ToString();
        }

        private static Dictionary<uint, string> ResolveTrackChannels(PlayerData player)
        {
            Dictionary<uint, string> respect = BmsChartSerializer.InferRespectTrackChannels(player);
            if (respect != null) return respect;

            var result = new Dictionary<uint, string>();
            int next = 0;
            foreach (TrackData track in player.Tracks)
            {
                string channel;
                if (player.BmsMetadata != null &&
                    player.BmsMetadata.TrackChannels.TryGetValue(track.Idx, out channel))
                    result[track.Idx] = channel;
                else
                    result[track.Idx] = next < DefaultChannels.Length ? DefaultChannels[next++] : "01";
            }
            return result;
        }

        private static int ChannelToLane(string channel)
        {
            if (string.IsNullOrWhiteSpace(channel)) return 0;
            string normalized = channel.Trim().ToUpperInvariant();
            if (normalized.Length != 2) return 0;
            if (normalized[0] == '5') normalized = "1" + normalized.Substring(1);
            if (normalized[0] != '1') return 0;
            for (int i = 0; i < DefaultChannels.Length; i++)
                if (DefaultChannels[i] == normalized) return i + 1;
            return 0;
        }

        private static int CheckedPulse(int virtualTick)
        {
            return checked(virtualTick * PulsePerVirtualTick);
        }

        private static string Number(double value)
        {
            return value.ToString("0.################", CultureInfo.InvariantCulture);
        }

        private static void AppendProperty(StringBuilder sb, string name, string value)
        {
            sb.Append('"').Append(name).Append("\":\"").Append(JsonEscape(value)).Append('"');
        }

        private static string JsonEscape(string value)
        {
            var sb = new StringBuilder(value == null ? 0 : value.Length + 8);
            foreach (char c in value ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private sealed class BmsonNote
        {
            public BmsonNote(int x, int y, int length)
            {
                X = x;
                Y = y;
                Length = length;
            }

            public int X { get; }
            public int Y { get; }
            public int Length { get; }
        }
    }
}
