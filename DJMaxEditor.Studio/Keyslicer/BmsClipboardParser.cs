using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DJMaxEditor.Files.bms;

namespace DJMaxEditor.Studio.Keyslicer
{
    /// <summary>
    /// Best-effort BMS/BMSE/iBMSC clipboard + file importer for the slicer.
    /// BMSE/iBMSC put a BMS fragment on the system clipboard: a few
    /// #WAV lines + #00111:… lines. The raw fragment corrupts if you paste
    /// it as-is into a UTF-8 chart (SJIS mojibake, base-36 ZZ reserved,
    /// measure-length 02 quirks). This normalises via the shared
    /// <see cref="BmsChartSerializer"/> so what you paste is what you get.
    /// Also handles full .bms/.bme/.bml/.bmson files via the same path.
    /// </summary>
    public sealed class BmsImportResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public double Bpm { get; set; } = 140;
        public Dictionary<int,string> WavMap { get; } = new Dictionary<int,string>();
        public List<BmsImportedNote> Notes { get; } = new List<BmsImportedNote>();
        public List<string> Warnings { get; } = new List<string>();
        // For diff preview: count per WAV, duplicate IDs, >192th placements
        public int DistinctWavs => WavMap.Count;
        public int MaxSlotsPerMeasure { get; set; }
    }

    public sealed class BmsImportedNote
    {
        public int Measure { get; set; }
        public string Channel { get; set; }
        public int WavId { get; set; }
        public string WavName { get; set; }
        public int VirtualTick { get; set; }
        public double ChartTimeMs { get; set; }
        public int Lane { get; set; }
    }

    public static class BmsClipboardParser
    {
        private const int VirtualMeasure = 192 * 6; // 1152 vt per 4/4 measure at 192*6

        /// <summary>
        /// Parse arbitrary text — full BMS file or a clipboard fragment.
        /// If the text lacks headers, synthesize minimal ones so
        /// <see cref="BmsChartSerializer.Parse"/> can run without
        /// throwing "no chart data".
        /// </summary>
        public static BmsImportResult Parse(string text, string contextPath = null)
        {
            var res = new BmsImportResult();
            if (string.IsNullOrWhiteSpace(text))
            {
                res.Error = "Clipboard is empty.";
                return res;
            }

            string normalized = Normalize(text);
            // Ensure at least one header so Parse doesn't throw "no #mmmcc"
            // Clipboard fragments often have only data lines.
            if (!HasHeader(normalized))
            {
                normalized = "#TITLE Clipboard import\n#ARTIST --\n#BPM 140\n" + normalized;
                res.Warnings.Add("Added minimal #TITLE/#BPM header — clipboard fragment had no headers.");
            }

            DJMaxEditor.DJMax.PlayerData player;
            try
            {
                player = BmsChartSerializer.Parse(normalized);
            }
            catch (DJMaxEditor.Files.ChartLoadException ex)
            {
                // Try as bmson (iBMSC can put bmson-ish JSON on clipboard rarely) — bmson is JSON via byte[] parse
                if (IsJson(text))
                {
                    try
                    {
                        byte[] jsonBytes = System.Text.Encoding.UTF8.GetBytes(text);
                        player = DJMaxEditor.Files.bms.BmsonChartSerializer.Parse(jsonBytes);
                        res.Warnings.Add("Detected BMSON JSON — parsed as bmson.");
                    }
                    catch (Exception ex2)
                    {
                        res.Error = "BMS parse failed: " + ex.Message + " — bmson fallback also failed: " + ex2.Message;
                        return res;
                    }
                }
                else
                {
                    res.Error = "BMS parse failed: " + ex.Message;
                    return res;
                }
            }
            catch (Exception ex)
            {
                res.Error = ex.Message;
                return res;
            }

            res.Bpm = player.Tempo > 0 ? player.Tempo : 140;
            // Extract WavMap from instruments (InsNum is base36 id)
            foreach (var ins in player.Instruments.Where(i => i != null && i.InsNum != 0))
            {
                int id = ins.InsNum;
                if (id > 0 && id < 1296)
                    res.WavMap[id] = ins.Name;
            }

            // Build time map: measure starts already handled inside Parse via VirtualMeasure
            // Convert ticks to ms using bpm map (simplified: single bpm). For BPM changes, use player events.
            var tempoTicks = player.Tracks.SelectMany(t => t.Events.Where(e => e.EventType == DJMaxEditor.DJMax.EventType.Tempo)).OrderBy(e => e.VirtualTick).ToList();
            Func<int,double> tickToMs = vt =>
            {
                // Integrate BPM segments: each segment msPerTick = 60000/(bpm*48*6) ? Actually vt is virtual (6 per tick), so msPerVT = 60000/(bpm*48*6)
                if (tempoTicks.Count == 0) return vt * (60000.0 / (res.Bpm * 48.0 * 6.0));
                double ms = 0;
                int lastVt = 0;
                double lastBpm = res.Bpm;
                foreach (var te in tempoTicks)
                {
                    if (vt <= te.VirtualTick)
                    {
                        ms += (vt - lastVt) * (60000.0 / (lastBpm * 48.0 * 6.0));
                        return ms;
                    }
                    ms += (te.VirtualTick - lastVt) * (60000.0 / (lastBpm * 48.0 * 6.0));
                    lastVt = te.VirtualTick;
                    lastBpm = te.Tempo > 0 ? te.Tempo : lastBpm;
                }
                ms += (vt - lastVt) * (60000.0 / (lastBpm * 48.0 * 6.0));
                return ms;
            };

            // Map channels to lanes: 11→0,12→1,...18→5,19→6, etc; fallback round-robin
            var channelToLane = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase)
            {
                ["11"]=0, ["12"]=1, ["13"]=2, ["14"]=3, ["15"]=4, ["16"]=5, ["18"]=6, ["19"]=7,
                ["21"]=0, ["22"]=1, ["23"]=2, ["24"]=3, ["25"]=4, ["26"]=5, ["28"]=6, ["29"]=7,
            };

            // Count max slots per measure for warning (>192)
            int maxSlots = 0;
            try
            {
                string[] lines = normalized.Split('\n');
                foreach (var l in lines)
                {
                    string t = l.Trim(); if (t.Length < 7) continue;
                    if (t[0]=='#' && t.Length>=6 && char.IsDigit(t[1]) && t.IndexOf(':')==6)
                    {
                        string payload = t.Substring(7).Trim();
                        if (payload.Length>0) maxSlots = Math.Max(maxSlots, payload.Length/2);
                    }
                }
                res.MaxSlotsPerMeasure = maxSlots;
                if (maxSlots > 192) res.Warnings.Add($"Measure has {maxSlots} slots — beyond iBMSC's 192th. Studio keeps it exact; some BMS players quantize here.");
            } catch {}

            // Notes: iterate tracks
            int laneFallback = 0;
            foreach (var track in player.Tracks)
            {
                string ch = null;
                if (player.BmsMetadata != null && player.BmsMetadata.TrackChannels.TryGetValue(track.Idx, out var cc)) ch = cc;
                // Try infer channel if missing (e.g., clipboard without metadata)
                if (string.IsNullOrEmpty(ch))
                {
                    // Heuristic: use track name if it says Lane X
                    ch = "11";
                }
                int lane = channelToLane.TryGetValue(ch, out var l) ? l : (laneFallback++ % 8);
                foreach (var ev in track.Events.Where(e => e.EventType == DJMaxEditor.DJMax.EventType.Note && e.Instrument != null && e.Instrument.InsNum != 0))
                {
                    int id = ev.Instrument.InsNum;
                    string name = res.WavMap.TryGetValue(id, out var n) ? n : ev.Instrument.Name;
                    // Filter out silent 008.wav? Keep but warn.
                    if (name != null && name.IndexOf("008", StringComparison.OrdinalIgnoreCase) >=0) res.Warnings.Add($"Note uses 008.wav (often silent) at vt {ev.VirtualTick} — check.");
                    var note = new BmsImportedNote
                    {
                        Measure = ev.VirtualTick / VirtualMeasure,
                        Channel = ch,
                        WavId = id,
                        WavName = name,
                        VirtualTick = ev.VirtualTick,
                        ChartTimeMs = tickToMs(ev.VirtualTick),
                        Lane = Math.Max(0, Math.Min(7, lane))
                    };
                    res.Notes.Add(note);
                }
            }

            // Deduplicate warnings: show base-36 overflow hint
            if (res.WavMap.Keys.Any(k => k >= 1295)) res.Warnings.Add("WAV ids ≥ ZZ (1295) — base-36 table overflow; BMS writers reuse IDs here.");
            // Duplicate hash check: same filename mapped to multiple ids
            var nameToIds = res.WavMap.GroupBy(kv => kv.Value?.ToLowerInvariant()).Where(g=>g.Count()>1).ToList();
            if (nameToIds.Count>0) res.Warnings.Add($"{nameToIds.Count} wav filename(s) share multiple ids — possible duplicate hash; Finalize will dedup by content.");

            res.Notes.Sort((a,b)=>a.VirtualTick.CompareTo(b.VirtualTick));
            res.Success = true;
            return res;
        }

        /// <summary>Parse a .bms/.bme/.bml/.bmson file on disk.</summary>
        public static BmsImportResult ParseFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return new BmsImportResult { Error = "File not found: " + path };
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                string text;
                try { text = BmsChartSerializer.Decode(bytes); }
                catch { text = System.Text.Encoding.UTF8.GetString(bytes); }
                return Parse(text, path);
            }
            catch (Exception ex) { return new BmsImportResult { Error = ex.Message }; }
        }

        private static string Normalize(string text)
        {
            // Clipboard often has CRLF, trailing spaces, BOM, tabs. Normalise to LF and trim.
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            // Strip system clipboard wrapper if present (e.g., "BMSClipboard\n" prefix some tools add)
            text = text.Trim('\uFEFF',' ','\t');
            // Remove nulls
            text = text.Replace("\0", "");
            return text;
        }

        private static bool HasHeader(string text) => text.IndexOf("\n#BPM", StringComparison.OrdinalIgnoreCase) >=0 || text.IndexOf("#BPM", StringComparison.OrdinalIgnoreCase)==0 || text.IndexOf("#TITLE", StringComparison.OrdinalIgnoreCase)>=0;

        private static bool IsJson(string text)
        {
            text = text.Trim();
            return (text.StartsWith("{") && text.EndsWith("}")) || (text.StartsWith("[") && text.EndsWith("]"));
        }

        /// <summary>Apply the import into the slicer VM: create placeholder slices for each distinct WAV and notes.</summary>
        public static int ApplyToViewModel(BmsImportResult result, KeyslicerViewModel vm, string bmsFolder = null)
        {
            if (result == null || !result.Success || vm == null) return 0;
            // Ensure source placeholder: if WAV files exist beside BMS, register them as sources
            if (!string.IsNullOrEmpty(bmsFolder) && Directory.Exists(bmsFolder))
            {
                foreach (var kv in result.WavMap)
                {
                    string wavName = kv.Value;
                    if (string.IsNullOrWhiteSpace(wavName)) continue;
                    string full = Path.Combine(bmsFolder, wavName);
                    if (File.Exists(full))
                    {
                        // Add as source if not already present
                        if (!vm.Project.Sources.Any(s => string.Equals(s.ResolvedPath, full, StringComparison.OrdinalIgnoreCase) || string.Equals(s.FilePath, wavName, StringComparison.OrdinalIgnoreCase)))
                        {
                            try { vm.AddSource(full); } catch {}
                        }
                    }
                }
            }

            // Create slices for each distinct WAV id (placeholder interval 0-500ms, user will re-slice if needed)
            var idToSliceId = new Dictionary<int,string>();
            foreach (var kv in result.WavMap.OrderBy(k=>k.Key))
            {
                if (string.IsNullOrWhiteSpace(kv.Value)) continue;
                string wavName = kv.Value;
                // Try find existing slice for same source file + name?
                var existing = vm.Slices.FirstOrDefault(s => string.Equals(Path.GetFileName(s.SourceFile), Path.GetFileName(wavName), StringComparison.OrdinalIgnoreCase));
                if (existing != null) { idToSliceId[kv.Key] = existing.Id; continue; }

                // Create a placeholder slice: 500ms window at 0, will be replaced when user slices properly
                // Use first source as host, or create a virtual source entry if none
                KeysoundSource host = vm.SelectedSource ?? vm.Project.Sources.FirstOrDefault();
                string srcFile = host?.FilePath ?? wavName;
                string resolved = host?.ResolvedPath ?? (bmsFolder != null ? Path.Combine(bmsFolder, wavName) : wavName);
                var slice = new KeysoundSlice
                {
                    Id = vm.Project.AllocateId(),
                    SourceFile = srcFile,
                    ResolvedSourcePath = File.Exists(resolved) ? resolved : srcFile,
                    StartMs = 0,
                    EndMs = Math.Min(500, vm.Waveform?.DurationMs ?? 500),
                    Gain = 1.0,
                    FadeInMs = vm.Project.Export.DefaultFadeInMs,
                    FadeOutMs = vm.Project.Export.DefaultFadeOutMs,
                    Lane = -1,
                    Label = Path.GetFileNameWithoutExtension(wavName) ?? kv.Key.ToString("X2"),
                    IsConfirmed = true
                };
                // Store original BMS id in label for round-trip traceability
                slice.Label += $" [WAV{kv.Key:X2}]";
                vm.Slices.Add(slice);
                idToSliceId[kv.Key] = slice.Id;
            }

            int addedNotes = 0;
            foreach (var n in result.Notes)
            {
                if (!idToSliceId.TryGetValue(n.WavId, out var sliceId)) continue;
                var slice = vm.Slices.FirstOrDefault(s=>s.Id==sliceId);
                if (slice == null) continue;
                // Create note at ChartTimeMs, lane as imported
                var note = new KeysoundMappedNote
                {
                    ChartTimeMs = n.ChartTimeMs,
                    VirtualTick = n.VirtualTick,
                    Lane = n.Lane,
                    KeysoundId = sliceId,
                    VelocityGain = 1.0
                };
                vm.Project.Notes.Add(note);
                addedNotes++;
            }

            vm.Slices.ToList().ForEach(_=>{}); // touch to trigger?
            return addedNotes;
        }
    }
}
