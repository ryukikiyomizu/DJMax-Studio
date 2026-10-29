using System;
using System.IO;
using System.Text;
using DJMaxEditor;
using DJMaxEditor.DJMax;
using DJMaxEditor.Files;
using DJMaxEditor.Files.bms;
using DJMaxEditor.Files.FormatDetection;
using DJMaxEditor.Files.pt;
using DJMaxEditor.Files.Tech;

namespace DJMaxEditor.Linux
{
    internal sealed class LinuxChartFileService
    {
        public PlayerData Open(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A chart path is required.", nameof(path));
            }

            byte[] data = File.ReadAllBytes(path);
            string extension = Path.GetExtension(path);
            FormatDetectionResult detection = ChartFormatDetector.Detect(data, extension);
            ChartFormat format = detection.Format;
            if (format == ChartFormat.Unknown || format == ChartFormat.Malformed)
            {
                ChartLoadError error = format == ChartFormat.Malformed
                    ? ChartLoadError.MalformedHeader
                    : ChartLoadError.UnsupportedFormat;
                throw new ChartLoadException(error,
                    detection.FailureReason ?? "The file is not a supported chart format.");
            }

            if (format == ChartFormat.PtffEncryptedTechnika)
            {
                data = PtCodec.Decrypt(data);
                FormatDetectionResult decrypted = ChartFormatDetector.Detect(data, ".pt");
                if (decrypted.Format != ChartFormat.PtffDecrypted)
                {
                    throw new ChartLoadException(ChartLoadError.MalformedHeader,
                        "The encrypted PT did not decrypt to a valid PTFF chart.");
                }
                format = ChartFormat.PtffDecrypted;
            }

            PlayerData model;
            switch (format)
            {
                case ChartFormat.PtffDecrypted:
                    model = OpenPt(path, data, detection.IsEncrypted);
                    break;
                case ChartFormat.BmsClassic:
                    model = BmsChartSerializer.Parse(BmsChartSerializer.Decode(data));
                    break;
                case ChartFormat.Bmson:
                    model = BmsonChartSerializer.Parse(data);
                    break;
                case ChartFormat.TechmaniaTrack:
                    // track.tech containers can hold several difficulties; this first Linux
                    // frontend opens the first pattern and leaves the container round-trip data
                    // intact in TechMetadata.
                    model = TechmaniaChartSerializer.Parse(data, 0);
                    break;
                default:
                    throw new ChartLoadException(ChartLoadError.UnsupportedFormat,
                        "This Linux build currently opens PT, BMS, bmson, and TECHMANIA .tech charts. " +
                        "Detected: " + format + ".");
            }

            model.SourceFormat = format;
            model.IsReadOnly = detection.IsReadOnly;
            return model;
        }

        public void Save(string path, PlayerData model)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A chart path is required.", nameof(path));
            }

            string extension = Path.GetExtension(path).ToLowerInvariant();
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory,
                "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                ChartFormat outputFormat;
                switch (extension)
                {
                    case ".pt":
                        if (!new PTSaveFile().Save(temporary, model))
                        {
                            throw new IOException("The PT chart could not be written.");
                        }
                        outputFormat = ChartFormat.PtffDecrypted;
                        break;
                    case ".bms":
                    case ".bme":
                    case ".bml":
                    case ".pms":
                        WriteUtf8(temporary, BmsChartSerializer.Serialize(model));
                        outputFormat = ChartFormat.BmsClassic;
                        break;
                    case ".bmson":
                        WriteUtf8(temporary, BmsonChartSerializer.Serialize(model));
                        outputFormat = ChartFormat.Bmson;
                        break;
                    case ".tech":
                        WriteUtf8(temporary, TechmaniaChartSerializer.Serialize(model));
                        outputFormat = ChartFormat.TechmaniaTrack;
                        break;
                    default:
                        throw new ChartLoadException(ChartLoadError.UnsupportedFormat,
                            "Choose a .pt, .bms, .bme, .bml, .pms, .bmson, or .tech destination.");
                }

                File.Move(temporary, path, true);
                model.SourceFormat = outputFormat;
                model.Encrypted = false;
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
                catch
                {
                    // Leave cleanup failures secondary to the actual save result.
                }
            }
        }

        private static PlayerData OpenPt(string path, byte[] data, bool wasEncrypted)
        {
            var opener = new PTOpenFile(false)
            {
                SourceOverride = data,
                FromEncryptedSource = wasEncrypted
            };
            if (!opener.Open(path, out PlayerData model) || model == null)
            {
                throw new ChartLoadException(ChartLoadError.MalformedHeader,
                    "The PT chart could not be parsed.");
            }
            return model;
        }

        private static void WriteUtf8(string path, string content)
        {
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
    }
}
