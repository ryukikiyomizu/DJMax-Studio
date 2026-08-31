using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DJMaxEditor.Studio.Video
{
    /// <summary>
    /// Turns a video file the platform decoder cannot read into one it can.
    /// <para>
    /// The case that forces this is DJMAX Technika 2. Its BGA previews are Bink Video - the client
    /// ships <c>binkw32.dll</c> and every preview inside <c>Preview.pak</c> is a <c>BIKi</c> stream,
    /// 320x240 at 29.97 fps. Media Foundation has never had a Bink decoder and never will: Bink is a
    /// proprietary RAD Game Tools format, so there is no codec to install and no Media Feature Pack
    /// that adds one. Bink is also 32-bit only in the shipped DLL, which rules out P/Invoking the
    /// game's own runtime from a 64-bit editor.
    /// </para>
    /// <para>
    /// What is left is FFmpeg, which has carried a native Bink video decoder for years. Rather than
    /// bind it in-process - a second decoding stack, native binaries to ship, and a licence to
    /// reason about - this shells out once per file and caches the result as H.264 in MP4, which the
    /// existing, already-proven Media Foundation path then plays with exact frame seeking. An 8
    /// second 320x240 preview converts in about a third of a second, so the cost lands once on first
    /// open and never again.
    /// </para>
    /// <para>
    /// FFmpeg is optional. When it is absent the resolver says so plainly and the editor carries on
    /// without a BGA, exactly as it does for a missing codec.
    /// </para>
    /// </summary>
    internal static class BgaSourceResolver
    {
        /// <summary>
        /// Containers Media Foundation cannot open at all, so there is no point trying it first.
        /// Anything not listed here goes to Media Foundation and only falls back on failure.
        /// </summary>
        private static readonly HashSet<string> AlwaysTranscode =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".bik", ".bik2", ".bk2", ".smk" };

        /// <summary>Where converted videos are kept between sessions.</summary>
        private static readonly string CacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DJMaxEditor", "bga-cache");

        /// <summary>
        /// Generous on purpose: this is a guard against a wedged child process, not a performance
        /// budget. A full-length BGA on a slow machine can legitimately take a while.
        /// </summary>
        private static readonly TimeSpan TranscodeTimeout = TimeSpan.FromMinutes(5);

        private static string _ffmpegProbed;
        private static bool _ffmpegProbeDone;

        /// <summary>
        /// Whether <paramref name="path"/> has to go through FFmpeg before anything can play it.
        /// </summary>
        internal static bool RequiresTranscode(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            try
            {
                return AlwaysTranscode.Contains(Path.GetExtension(path));
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>
        /// Extensions worth auto-attaching, best first. MP4 leads because a preview that has already
        /// been converted once should be picked up directly rather than re-derived from the Bink.
        /// </summary>
        private static readonly string[] DiscoveryExtensions =
            { ".mp4", ".bik", ".mkv", ".wmv", ".avi", ".mov" };

        /// <summary>
        /// Looks for the BGA that belongs to a chart, in the chart's own folder.
        /// <para>
        /// Technika song folders are named after the song and hold <c>&lt;song&gt;_&lt;mode&gt;_&lt;n&gt;.pt</c>,
        /// so the preview extracted out of <c>Preview.pak</c> lands beside the charts as
        /// <c>&lt;song&gt;_pre.bik</c>. Probing the folder name before the chart stem is what makes
        /// opening any of a song's four patterns find the one preview they share.
        /// </para>
        /// <para>Returns null when there is nothing to attach, which is the ordinary case.</para>
        /// </summary>
        internal static string FindForChart(string chartPath)
        {
            if (string.IsNullOrEmpty(chartPath))
            {
                return null;
            }

            string folder;
            string chartStem;
            string songKey;
            try
            {
                folder = Path.GetDirectoryName(chartPath);
                chartStem = Path.GetFileNameWithoutExtension(chartPath);
                songKey = string.IsNullOrEmpty(folder) ? null : Path.GetFileName(folder);
            }
            catch (ArgumentException)
            {
                return null;
            }

            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return null;
            }

            List<string> stems = new List<string>(5);
            if (!string.IsNullOrEmpty(songKey))
            {
                stems.Add(songKey + "_pre");
                stems.Add(songKey);
            }
            if (!string.IsNullOrEmpty(chartStem))
            {
                stems.Add(chartStem + "_pre");
                stems.Add(chartStem);
            }
            stems.Add("bga");

            foreach (string stem in stems)
            {
                foreach (string extension in DiscoveryExtensions)
                {
                    string candidate = Path.Combine(folder, stem + extension);
                    try
                    {
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                    catch (Exception)
                    {
                        // An unmappable name reads as "not there".
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Returns a path the platform decoder can open, converting and caching if it has to.
        /// <para>
        /// Blocking, and can take seconds: call it off the UI thread. When it returns false the
        /// caller has a message fit to put in front of the user.
        /// </para>
        /// </summary>
        internal static bool TryResolve(string path, out string playablePath, out string error)
        {
            playablePath = path;
            error = null;

            if (!RequiresTranscode(path))
            {
                return true;
            }

            if (!File.Exists(path))
            {
                error = "file not found";
                return false;
            }

            string ffmpeg = FindFfmpeg();
            if (ffmpeg == null)
            {
                error = Path.GetExtension(path).TrimStart('.').ToUpperInvariant() +
                    " video needs FFmpeg, which was not found on PATH";
                return false;
            }

            string cached;
            try
            {
                cached = CachePathFor(path);
            }
            catch (Exception ex)
            {
                error = "cache path failed: " + ex.Message;
                return false;
            }

            // A cached conversion is only trusted if it is non-empty; a previous run killed
            // mid-write would otherwise poison this file forever.
            if (File.Exists(cached) && new FileInfo(cached).Length > 0L)
            {
                playablePath = cached;
                return true;
            }

            return Transcode(ffmpeg, path, cached, out playablePath, out error);
        }

        private static bool Transcode(string ffmpeg, string source, string destination,
            out string playablePath, out string error)
        {
            playablePath = source;
            error = null;

            string partial = destination + ".part";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination));

                var startInfo = new ProcessStartInfo(ffmpeg)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };

                // -an: the editor's audio is the chart's keysounds, never the video's track.
                // -pix_fmt yuv420p: Bink decodes to a planar format H.264 in MP4 cannot carry.
                // -movflags +faststart: puts the index first, so the reader does not seek to the
                // end of the file before it can decode the first frame.
                // -f mp4: the output is written to a .part name first, and FFmpeg picks its muxer
                // from the extension, so without this it refuses the file it was told to write.
                foreach (string argument in new[]
                {
                    "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                    "-i", source,
                    "-an",
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
                    "-pix_fmt", "yuv420p",
                    "-movflags", "+faststart",
                    "-f", "mp4",
                    partial,
                })
                {
                    startInfo.ArgumentList.Add(argument);
                }

                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        error = "FFmpeg did not start";
                        return false;
                    }

                    // Read both pipes before waiting: FFmpeg is chatty on stderr and a full pipe
                    // buffer would deadlock the wait.
                    string stderr = process.StandardError.ReadToEnd();
                    process.StandardOutput.ReadToEnd();

                    if (!process.WaitForExit((int)TranscodeTimeout.TotalMilliseconds))
                    {
                        TryKill(process);
                        error = "FFmpeg timed out after " +
                            TranscodeTimeout.TotalMinutes.ToString(CultureInfo.InvariantCulture) + " minutes";
                        return false;
                    }

                    if (process.ExitCode != 0)
                    {
                        error = "FFmpeg failed: " + FirstLine(stderr);
                        return false;
                    }
                }

                if (!File.Exists(partial) || new FileInfo(partial).Length == 0L)
                {
                    error = "FFmpeg produced no output";
                    return false;
                }

                // Renamed only once it is complete, so the cache never holds a half-written file.
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }
                File.Move(partial, destination);

                playablePath = destination;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally
            {
                TryDelete(partial);
            }
        }

        /// <summary>
        /// A stable cache name for one source file: full path, length and write time, hashed. Any
        /// edit to the source produces a different name, so a re-exported BGA is never served stale.
        /// </summary>
        private static string CachePathFor(string path)
        {
            FileInfo info = new FileInfo(path);
            string identity = info.FullName.ToUpperInvariant() + "|" +
                info.Length.ToString(CultureInfo.InvariantCulture) + "|" +
                info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);

            byte[] hash;
            using (SHA256 sha = SHA256.Create())
            {
                hash = sha.ComputeHash(Encoding.UTF8.GetBytes(identity));
            }

            StringBuilder name = new StringBuilder(24);
            for (int i = 0; i < 10; i++)
            {
                name.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
            }

            // The stem is kept for a human reading the cache folder; the hash is what makes it
            // unique, so an unusable stem is simply dropped.
            string stem = SanitiseStem(Path.GetFileNameWithoutExtension(path));
            string leaf = stem.Length == 0 ? name.ToString() : stem + "-" + name;
            return Path.Combine(CacheRoot, leaf + ".mp4");
        }

        private static string SanitiseStem(string stem)
        {
            if (string.IsNullOrEmpty(stem))
            {
                return string.Empty;
            }

            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder text = new StringBuilder(stem.Length);
            foreach (char character in stem)
            {
                text.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
            }

            return text.ToString().Trim().TrimEnd('.');
        }

        /// <summary>
        /// Locates an ffmpeg executable, probed once per session.
        /// <para>
        /// PATH first, then the two locations the common Windows builds land in, then beside our own
        /// executable so a portable copy can be dropped in.
        /// </para>
        /// </summary>
        internal static string FindFfmpeg()
        {
            if (_ffmpegProbeDone)
            {
                return _ffmpegProbed;
            }

            _ffmpegProbeDone = true;
            _ffmpegProbed = ProbeFfmpeg();
            return _ffmpegProbed;
        }

        private static string ProbeFfmpeg()
        {
            List<string> candidates = new List<string>();

            string pathVariable = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathVariable))
            {
                foreach (string directory in pathVariable.Split(Path.PathSeparator))
                {
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        candidates.Add(Path.Combine(directory.Trim(), "ffmpeg.exe"));
                    }
                }
            }

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles))
            {
                candidates.Add(Path.Combine(programFiles, "ffmpeg", "bin", "ffmpeg.exe"));
            }
            candidates.Add(@"C:\ffmpeg\bin\ffmpeg.exe");

            string ourFolder = Path.GetDirectoryName(typeof(BgaSourceResolver).Assembly.Location);
            if (!string.IsNullOrEmpty(ourFolder))
            {
                candidates.Add(Path.Combine(ourFolder, "ffmpeg.exe"));
            }

            foreach (string candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception)
                {
                    // A malformed PATH entry is not a reason to give up on the rest of it.
                }
            }

            return null;
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "no diagnostic output";
            }

            string[] lines = text.Split('\n');
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed.Length > 200 ? trimmed.Substring(0, 200) : trimmed;
                }
            }

            return "no diagnostic output";
        }

        private static void TryKill(Process process)
        {
            try
            {
                process.Kill(true);
            }
            catch (Exception)
            {
                // Already gone, or not ours to kill.
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // A leftover .part is harmless; the next run overwrites it.
            }
        }
    }
}
