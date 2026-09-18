using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace DJMaxEditor.Studio.Keyslicer
{
    /// <summary>
    /// A large source recording from which virtual keysounds are sliced non-destructively.
    /// The project keeps a reference to the original file rather than duplicating it;
    /// slices are intervals [StartMs, EndMs) into this source.
    /// </summary>
    public sealed class KeysoundSource
    {
        /// <summary>Filename as stored in the project (relative when portable).</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>Absolute resolved path on disk (not serialized; resolved at load).</summary>
        [JsonIgnore]
        public string ResolvedPath { get; set; } = string.Empty;

        /// <summary>Original file name without directory.</summary>
        [JsonIgnore]
        public string FileName => string.IsNullOrEmpty(ResolvedPath)
            ? Path.GetFileName(FilePath)
            : Path.GetFileName(ResolvedPath);

        /// <summary>Duration in milliseconds (filled after probing).</summary>
        public double DurationMs { get; set; }

        /// <summary>Sample rate of the source file.</summary>
        public int SampleRate { get; set; }

        /// <summary>Channel count.</summary>
        public int Channels { get; set; }

        /// <summary>File size in bytes (for change detection).</summary>
        public long FileSize { get; set; }

        /// <summary>SHA-256 hash hex (first 16 chars) for identity after moves.</summary>
        public string Hash { get; set; } = string.Empty;

        /// <summary>When this source was added to the project.</summary>
        public DateTime AddedAt { get; set; } = DateTime.UtcNow;

        /// <summary>True if the source file was missing at last probe.</summary>
        [JsonIgnore]
        public bool IsMissing { get; set; }

        public static KeysoundSource FromFile(string absolutePath, string storedPath = null)
        {
            var src = new KeysoundSource
            {
                FilePath = storedPath ?? absolutePath,
                ResolvedPath = absolutePath,
                AddedAt = DateTime.UtcNow
            };
            if (File.Exists(absolutePath))
            {
                try
                {
                    var info = new FileInfo(absolutePath);
                    src.FileSize = info.Length;
                    src.Hash = ComputeHashPrefix(absolutePath);
                }
                catch { }
            }
            else
            {
                src.IsMissing = true;
            }
            return src;
        }

        private static string ComputeHashPrefix(string path)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(path))
                {
                    // Hash only first 2 MiB + file length to avoid hashing hour-long sessions.
                    byte[] buffer = new byte[Math.Min(stream.Length, 2 * 1024 * 1024)];
                    int read = stream.Read(buffer, 0, buffer.Length);
                    byte[] lenBytes = BitConverter.GetBytes(stream.Length);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    sha.TransformFinalBlock(lenBytes, 0, lenBytes.Length);
                    string hex = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                    return hex.Substring(0, 16);
                }
            }
            catch { return string.Empty; }
        }
    }
}
