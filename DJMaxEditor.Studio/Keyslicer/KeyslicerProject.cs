using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DJMaxEditor.Studio.Keyslicer
{
    public enum SlicerMode
    {
        Bms,
        Respect,
        Technika
    }

    /// <summary>
    /// Project model for the keysound slicer scene.
    /// Stores references to source audio files and non-destructive slice intervals,
    /// plus the chart metadata needed to place notes (BPM, offset, song). This is
    /// the portable project the slicer saves; export renders real wav/ogg files
    /// and a Techmania .tech.
    /// </summary>
    public sealed class KeyslicerProject
    {
        public string Version { get; set; } = "1.0";

        /// <summary>Project name (also the .tech title fallback).</summary>
        public string Title { get; set; } = "Untitled";

        public string Artist { get; set; } = string.Empty;

        /// <summary>BGM / song file path (optional, for chart sync).</summary>
        public string SongFile { get; set; } = string.Empty;

        /// <summary>BPM of the chart (for note timing).</summary>
        public double Bpm { get; set; } = 140;

        /// <summary>Global offset in ms (chart time 0 -> song time).</summary>
        public double OffsetMs { get; set; } = 0;

        /// <summary>Snap grid denominator for quantize and auto-advance (0=Free, 4,8,16,32).</summary>
        public int SnapDenominator { get; set; } = 16;

        /// <summary>Target platform for budget + polyphony simulation.</summary>
        public SlicerMode SlicerMode { get; set; } = SlicerMode.Bms;

        /// <summary>How the chart playhead auto-advances after placing a note.</summary>
        public string AutoAdvanceMode { get; set; } = "grid"; // grid | beat | gap | off

        /// <summary>Nudge slice edges to nearest zero-crossing within ~2 ms.</summary>
        public bool SnapToZeroCrossing { get; set; } = true;

        public static int MaxSlicesForMode(SlicerMode mode) => mode switch
        {
            SlicerMode.Respect => 2047,
            SlicerMode.Technika => int.MaxValue,
            _ => 1295,
        };

        public int MaxSlices => MaxSlicesForMode(SlicerMode);

        public List<KeysoundSource> Sources { get; set; } = new List<KeysoundSource>();

        public List<KeysoundSlice> Slices { get; set; } = new List<KeysoundSlice>();

        public List<KeysoundMappedNote> Notes { get; set; } = new List<KeysoundMappedNote>();

        /// <summary>Export settings remembered per project.</summary>
        public ExportSettings Export { get; set; } = new ExportSettings();

        /// <summary>Editor view state (zoom, playhead, selection).</summary>
        public ViewState View { get; set; } = new ViewState();

        public sealed class ExportSettings
        {
            public string Format { get; set; } = "ogg";
            public bool Normalize { get; set; } = false;
            public double DefaultFadeInMs { get; set; } = 2;
            public double DefaultFadeOutMs { get; set; } = 5;
            public bool ExportOnlyUsed { get; set; } = true;
        }

        public sealed class ViewState
        {
            public double WaveformZoom { get; set; } = 1.0;
            public double WaveformScrollMs { get; set; } = 0;
            public double ChartPlayheadMs { get; set; } = 0;
            public string SelectedSliceId { get; set; } = string.Empty;
        }

        /// <summary>Next keysound id counter (ks_0001 ...). Increment on create.</summary>
        public int NextIdCounter { get; set; } = 1;

        public string AllocateId()
        {
            string id = string.Format("ks_{0:D4}", NextIdCounter);
            NextIdCounter++;
            return id;
        }

        public static KeyslicerProject CreateEmpty(string title = null)
        {
            return new KeyslicerProject
            {
                Title = string.IsNullOrEmpty(title) ? "Untitled" : title,
                Bpm = 140,
                Sources = new List<KeysoundSource>(),
                Slices = new List<KeysoundSlice>(),
                Notes = new List<KeysoundMappedNote>()
            };
        }

        // --------------------------------------------------------------------
        // Persistence
        // --------------------------------------------------------------------
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        public void Save(string path, bool portable = false, string portableRoot = null)
        {
            // If portable, rewrite FilePath to be relative to project dir and copy sources into project/sources/.
            string dir = Path.GetDirectoryName(path) ?? ".";
            if (portable && !string.IsNullOrEmpty(portableRoot))
                dir = portableRoot;

            var clone = CloneForSave(portable, dir);
            string json = JsonSerializer.Serialize(clone, JsonOptions);
            // Atomic via temp file.
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);

            if (portable)
            {
                string srcDir = Path.Combine(dir, "sources");
                Directory.CreateDirectory(srcDir);
                foreach (var s in Sources)
                {
                    if (string.IsNullOrEmpty(s.ResolvedPath) || !File.Exists(s.ResolvedPath)) continue;
                    string dest = Path.Combine(srcDir, Path.GetFileName(s.ResolvedPath));
                    if (!File.Exists(dest))
                    {
                        try { File.Copy(s.ResolvedPath, dest); } catch { }
                    }
                }
                if (!string.IsNullOrEmpty(SongFile) && File.Exists(SongFile))
                {
                    string songDest = Path.Combine(dir, Path.GetFileName(SongFile));
                    if (!File.Exists(songDest))
                        try { File.Copy(SongFile, songDest); } catch { }
                }
            }
        }

        private KeyslicerProject CloneForSave(bool portable, string projectDir)
        {
            var clone = new KeyslicerProject
            {
                Version = Version,
                Title = Title,
                Artist = Artist,
                SongFile = SongFile,
                Bpm = Bpm,
                OffsetMs = OffsetMs,
                SnapDenominator = SnapDenominator,
                SlicerMode = SlicerMode,
                AutoAdvanceMode = AutoAdvanceMode,
                SnapToZeroCrossing = SnapToZeroCrossing,
                NextIdCounter = NextIdCounter,
                Sources = Sources.Select(s =>
                {
                    string stored = s.FilePath;
                    if (portable && !string.IsNullOrEmpty(s.ResolvedPath))
                    {
                        string rel = "sources/" + Path.GetFileName(s.ResolvedPath);
                        stored = rel.Replace('\\', '/');
                    }
                    else if (!portable && !string.IsNullOrEmpty(s.ResolvedPath))
                    {
                        try
                        {
                            stored = MakeRelative(projectDir, s.ResolvedPath);
                        }
                        catch { stored = s.FilePath; }
                    }
                    return new KeysoundSource
                    {
                        FilePath = stored,
                        DurationMs = s.DurationMs,
                        SampleRate = s.SampleRate,
                        Channels = s.Channels,
                        FileSize = s.FileSize,
                        Hash = s.Hash,
                        AddedAt = s.AddedAt
                    };
                }).ToList(),
                Slices = Slices.Select(s => s.Clone()).ToList(),
                Notes = Notes.Select(n => new KeysoundMappedNote
                {
                    ChartTimeMs = n.ChartTimeMs,
                    VirtualTick = n.VirtualTick,
                    Lane = n.Lane,
                    KeysoundId = n.KeysoundId,
                    VelocityGain = n.VelocityGain,
                    DurationMs = n.DurationMs
                }).ToList(),
                Export = new ExportSettings
                {
                    Format = Export.Format,
                    Normalize = Export.Normalize,
                    DefaultFadeInMs = Export.DefaultFadeInMs,
                    DefaultFadeOutMs = Export.DefaultFadeOutMs,
                    ExportOnlyUsed = Export.ExportOnlyUsed
                },
                View = new ViewState
                {
                    WaveformZoom = View.WaveformZoom,
                    WaveformScrollMs = View.WaveformScrollMs,
                    ChartPlayheadMs = View.ChartPlayheadMs,
                    SelectedSliceId = View.SelectedSliceId
                }
            };
            // Rewrite slice SourceFile to be portable-relative too.
            if (portable)
            {
                var map = Sources.ToDictionary(k => k.ResolvedPath ?? k.FilePath, v => "sources/" + Path.GetFileName(v.ResolvedPath ?? v.FilePath), StringComparer.OrdinalIgnoreCase);
                foreach (var sl in clone.Slices)
                {
                    if (map.TryGetValue(sl.SourceFile, out string rel)) sl.SourceFile = rel;
                    else if (!string.IsNullOrEmpty(sl.ResolvedSourcePath) && map.TryGetValue(sl.ResolvedSourcePath, out rel)) sl.SourceFile = rel;
                }
            }
            if (!string.IsNullOrEmpty(clone.SongFile) && portable)
                clone.SongFile = Path.GetFileName(clone.SongFile);
            else if (!string.IsNullOrEmpty(clone.SongFile) && !portable && File.Exists(clone.SongFile))
                try { clone.SongFile = MakeRelative(projectDir, clone.SongFile); } catch { }

            return clone;
        }

        public static KeyslicerProject Load(string path)
        {
            string json = File.ReadAllText(path);
            var proj = JsonSerializer.Deserialize<KeyslicerProject>(json, JsonOptions) ?? CreateEmpty();
            string dir = Path.GetDirectoryName(path) ?? ".";
            // Resolve sources.
            foreach (var s in proj.Sources)
            {
                string resolved = ResolvePath(dir, s.FilePath);
                s.ResolvedPath = resolved;
                s.IsMissing = string.IsNullOrEmpty(resolved) || !File.Exists(resolved);
                if (!s.IsMissing)
                {
                    // Refresh hash/size if zero.
                    if (s.FileSize == 0 || string.IsNullOrEmpty(s.Hash))
                    {
                        try
                        {
                            var info = new FileInfo(resolved);
                            s.FileSize = info.Length;
                            // lazy hash: recomputed on demand
                        }
                        catch { }
                    }
                    // Duration probe.
                    if (s.DurationMs <= 0)
                    {
                        try
                        {
                            var data = WaveformPeakProvider.Build(resolved, 64, default);
                            s.DurationMs = data.DurationMs;
                            s.SampleRate = data.SampleRate;
                            s.Channels = data.Channels;
                        }
                        catch { }
                    }
                }
                // Also resolve slices that point at this source.
                foreach (var sl in proj.Slices.Where(sl => string.Equals(sl.SourceFile, s.FilePath, StringComparison.OrdinalIgnoreCase)))
                    sl.ResolvedSourcePath = resolved;
            }
            // Fallback for slices whose SourceFile wasn't in Sources (legacy).
            foreach (var sl in proj.Slices)
            {
                if (string.IsNullOrEmpty(sl.ResolvedSourcePath))
                {
                    string r = ResolvePath(dir, sl.SourceFile);
                    if (!string.IsNullOrEmpty(r) && File.Exists(r))
                        sl.ResolvedSourcePath = r;
                    else
                        sl.ResolvedSourcePath = sl.SourceFile;
                }
            }
            if (!string.IsNullOrEmpty(proj.SongFile))
            {
                string r = ResolvePath(dir, proj.SongFile);
                if (!string.IsNullOrEmpty(r) && File.Exists(r))
                    proj.SongFile = r;
            }
            return proj;
        }

        private static string ResolvePath(string baseDir, string stored)
        {
            if (string.IsNullOrEmpty(stored)) return stored;
            if (Path.IsPathRooted(stored)) return stored;
            try
            {
                string combined = Path.Combine(baseDir, stored.Replace('/', Path.DirectorySeparatorChar));
                return Path.GetFullPath(combined);
            }
            catch { return stored; }
        }

        private static string MakeRelative(string fromDir, string toPath)
        {
            if (string.IsNullOrEmpty(fromDir) || string.IsNullOrEmpty(toPath)) return toPath;
            try
            {
                Uri fromUri = new Uri(fromDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar);
                Uri toUri = new Uri(Path.GetFullPath(toPath));
                Uri rel = fromUri.MakeRelativeUri(toUri);
                string s = Uri.UnescapeDataString(rel.ToString()).Replace('/', Path.DirectorySeparatorChar);
                return s;
            }
            catch { return toPath; }
        }

        private KeyslicerProject Clone()
        {
            return CloneForSave(false, ".");
        }
    }
}
