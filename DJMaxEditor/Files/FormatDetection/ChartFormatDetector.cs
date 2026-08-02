using System;

namespace DJMaxEditor.Files.FormatDetection
{
    /// <summary>
    /// Single authoritative component that classifies a chart file from its *content* (not its
    /// extension). It performs only in-memory inspection: it never decrypts, never touches the
    /// network, and never throws for malformed input — malformed/unknown data is reported as a typed
    /// <see cref="FormatDetectionResult"/>. Extension is used at most as a tie-break hint, never as the
    /// final authority.
    /// </summary>
    public static class ChartFormatDetector
    {
        // "PTFF" and "EZTR" as their on-disk little-endian byte sequences.
        private static readonly byte[] PtffMagic = { 0x50, 0x54, 0x46, 0x46 }; // 'P','T','F','F'
        private static readonly byte[] EztrMagic = { 0x45, 0x5A, 0x54, 0x52 }; // 'E','Z','T','R' == 0x52545A45 LE

        // Trailer layout: sign(4) num(4) pos(4) = 12-byte header; trailer at EOF is 26 bytes.
        private const int TrailerHeaderSize = 12;
        private const int TrailerSize = 26;

        // Sanity bounds to reject malformed files without huge allocations.
        private const int MaxPtffInstruments = 1000;   // PTOpenFile already rejects >1000
        private const int MaxTrailerInstruments = 8192;
        private const int MaxTrailerTracks = 256;       // parser stores track count in a byte
        private const int MaxTickPerMinute = 60000;      // tickPerMinute is a u16 (real values are ~192)

        public static FormatDetectionResult Detect(byte[] data, string extensionHint = null)
        {
            if (data == null || data.Length == 0)
            {
                return new FormatDetectionResult(ChartFormat.Malformed, DetectionConfidence.None,
                    false, false, "no data", "File is empty or unreadable.");
            }

            if (data.Length < 4)
            {
                return new FormatDetectionResult(ChartFormat.Malformed, DetectionConfidence.Low,
                    false, false, $"{data.Length} bytes", "File is too small to contain a chart header.");
            }

            // (2) XML — detected by content (leading '<' after optional BOM/whitespace).
            if (LooksLikeXml(data))
            {
                return new FormatDetectionResult(ChartFormat.CyclonXml, DetectionConfidence.High,
                    false, false, "leading '<' — XML document");
            }

            // (3) PTFF signature at offset 0.
            string bmsEvidence;
            if (LooksLikeClassicBms(data, out bmsEvidence))
            {
                return new FormatDetectionResult(ChartFormat.BmsClassic, DetectionConfidence.High,
                    false, false, bmsEvidence);
            }

            if (StartsWith(data, PtffMagic))
            {
                return ClassifyPtff(data);
            }

            // (5) Trailer format — leading 32-bit zero signature.
            if (data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 0)
            {
                return ClassifyTrailer(data);
            }

            // (6) Nothing matched.
            var hint = string.IsNullOrEmpty(extensionHint) ? "" : $"; extension '{extensionHint}'";
            return new FormatDetectionResult(ChartFormat.Unknown, DetectionConfidence.None,
                false, false, $"first bytes {Hex(data, 0, 8)}{hint}",
                "File does not match PTFF, encrypted Technika, Respect V trailer, classic BMS, or XML.");
        }

        private static bool LooksLikeClassicBms(byte[] data, out string evidence)
        {
            evidence = null;
            int limit = Math.Min(data.Length, 1024 * 1024);
            string text = System.Text.Encoding.ASCII.GetString(data, 0, limit)
                .Replace("\r\n", "\n").Replace('\r', '\n');
            bool header = false;
            bool channel = false;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length < 2 || line[0] != '#') continue;
                string upper = line.ToUpperInvariant();
                if (upper.StartsWith("#BPM ", StringComparison.Ordinal) ||
                    upper.StartsWith("#TITLE ", StringComparison.Ordinal) ||
                    upper.StartsWith("#PLAYER ", StringComparison.Ordinal) ||
                    (upper.StartsWith("#WAV", StringComparison.Ordinal) && upper.Length >= 6))
                    header = true;

                if (line.Length >= 8 && line[6] == ':' &&
                    IsAsciiDigit(line[1]) && IsAsciiDigit(line[2]) && IsAsciiDigit(line[3]) &&
                    IsBase36(line[4]) && IsBase36(line[5]))
                    channel = true;

                if (header && channel)
                {
                    evidence = "classic BMS header + #mmmcc: chart data";
                    return true;
                }
            }
            return false;
        }

        private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

        private static bool IsBase36(char c)
        {
            return IsAsciiDigit(c) || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
        }

        private static FormatDetectionResult ClassifyPtff(byte[] data)
        {
            int eztr = IndexOf(data, EztrMagic, 4);
            if (eztr >= 0)
            {
                // Readable EZTR track magic present => decrypted editor PTFF.
                return new FormatDetectionResult(ChartFormat.PtffDecrypted, DetectionConfidence.High,
                    false, false, $"PTFF@0x0 + EZTR@0x{eztr:X}", offset: eztr);
            }

            // No EZTR. To distinguish an encrypted body from a truncated/malformed file we need the
            // body flag at 0x18 (pt_tool's own heuristic) plus a length check.
            if (data.Length < 0x18 + 4)
            {
                return new FormatDetectionResult(ChartFormat.Malformed, DetectionConfidence.Probable,
                    false, false, $"PTFF@0x0, {data.Length} bytes, no EZTR",
                    "PTFF header present but file is too short to contain a body.", 0x18);
            }

            uint bodyFlag = ReadU32(data, 0x18);
            double bodyEntropy = Entropy(data, 0x18, data.Length - 0x18);

            if (bodyFlag > 10)
            {
                // Positive encrypted identification: pt_tool treats first body u32 > 10 as encrypted,
                // corroborated by the absence of EZTR and a high-entropy body (logged, not decisive).
                return new FormatDetectionResult(ChartFormat.PtffEncryptedTechnika, DetectionConfidence.High,
                    isEncrypted: true, isReadOnly: false,
                    evidence: $"PTFF@0x0, no EZTR, bodyFlag@0x18={bodyFlag} (>10), bodyEntropy={bodyEntropy:F2} bits/byte",
                    offset: 0x18);
            }

            // PTFF header, small body flag, no EZTR: structurally looks like a decrypted file with no
            // track blocks. Let the real PTFF parser attempt it (no network fallback happens now).
            return new FormatDetectionResult(ChartFormat.PtffDecrypted, DetectionConfidence.Probable,
                false, false,
                $"PTFF@0x0, no EZTR, bodyFlag@0x18={bodyFlag} (<=10), bodyEntropy={bodyEntropy:F2} bits/byte");
        }

        private static FormatDetectionResult ClassifyTrailer(byte[] data)
        {
            if (data.Length < TrailerHeaderSize)
            {
                return new FormatDetectionResult(ChartFormat.Malformed, DetectionConfidence.Low,
                    false, false, $"zero signature, {data.Length} bytes",
                    "Trailer header is truncated.");
            }

            uint num = ReadU32(data, 4); // offset of the EOF metadata trailer

            if (num < TrailerHeaderSize || (long)num + TrailerSize > data.Length)
            {
                return new FormatDetectionResult(ChartFormat.Malformed, DetectionConfidence.Probable,
                    false, false,
                    $"zero signature, trailerOffset={num}, fileLen={data.Length}",
                    "Trailer offset is out of range (file truncated or malformed).", num);
            }

            ushort insCnt = ReadU16(data, (int)num);
            ushort trackCntRaw = ReadU16(data, (int)num + 2);
            int trackCnt = trackCntRaw & 0xFF; // parser uses the low byte
            ushort tickPerMinute = ReadU16(data, (int)num + 4);

            if (insCnt > MaxTrailerInstruments || trackCnt > MaxTrailerTracks ||
                tickPerMinute == 0 || tickPerMinute > MaxTickPerMinute)
            {
                return new FormatDetectionResult(ChartFormat.Malformed, DetectionConfidence.Probable,
                    false, false,
                    $"zero signature, trailerOffset={num}, insCnt={insCnt}, trackCnt={trackCnt}, tpm={tickPerMinute}",
                    "Trailer contains implausible instrument/track/tempo counts.", num);
            }

            return new FormatDetectionResult(ChartFormat.TrailerRespectV, DetectionConfidence.High,
                isEncrypted: false, isReadOnly: false,
                evidence: $"zero signature, trailer@0x{num:X}, insCnt={insCnt}, trackCnt={trackCnt}, tpm={tickPerMinute}",
                offset: num);
        }

        // ---- helpers (pure, bounds-safe) ----

        private static bool LooksLikeXml(byte[] d)
        {
            int i = 0;
            // skip UTF-8 BOM
            if (d.Length >= 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF) i = 3;
            while (i < d.Length && (d[i] == 0x20 || d[i] == 0x09 || d[i] == 0x0D || d[i] == 0x0A)) i++;
            return i < d.Length && d[i] == (byte)'<';
        }

        private static bool StartsWith(byte[] d, byte[] prefix)
        {
            if (d.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++)
                if (d[i] != prefix[i]) return false;
            return true;
        }

        private static int IndexOf(byte[] haystack, byte[] needle, int start)
        {
            int last = haystack.Length - needle.Length;
            for (int i = Math.Max(0, start); i <= last; i++)
            {
                int j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        private static uint ReadU32(byte[] d, int off)
        {
            return (uint)(d[off] | (d[off + 1] << 8) | (d[off + 2] << 16) | (d[off + 3] << 24));
        }

        private static ushort ReadU16(byte[] d, int off)
        {
            return (ushort)(d[off] | (d[off + 1] << 8));
        }

        private static double Entropy(byte[] d, int off, int len)
        {
            if (len <= 0) return 0;
            var counts = new int[256];
            int end = off + len;
            for (int i = off; i < end; i++) counts[d[i]]++;
            double h = 0;
            for (int i = 0; i < 256; i++)
            {
                if (counts[i] == 0) continue;
                double p = (double)counts[i] / len;
                h -= p * Math.Log(p, 2);
            }
            return h;
        }

        private static string Hex(byte[] d, int off, int count)
        {
            int end = Math.Min(d.Length, off + count);
            var sb = new System.Text.StringBuilder();
            for (int i = off; i < end; i++)
            {
                if (i > off) sb.Append(' ');
                sb.Append(d[i].ToString("X2"));
            }
            return sb.ToString();
        }
    }
}
