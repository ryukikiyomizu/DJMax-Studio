using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DJMaxEditor.DJMax;
using DJMaxEditor.Files;
using DJMaxEditor.Files.FormatDetection;
using DJMaxEditor.Files.bms;
using DJMaxEditor.Files.bytes;
using DJMaxEditor.Files.Cyclon;
using DJMaxEditor.Files.pt;
using DJMaxEditor.Files.Tech;

namespace DJMaxEditor.Studio.Documents
{
    /// <summary>
    /// What a probe learned about a file before anything tried to parse it.
    /// </summary>
    internal sealed class ChartProbe
    {
        public ChartProbe(string path, byte[] data, FormatDetectionResult detection)
        {
            Path = path;
            Data = data;
            Detection = detection;
        }

        public string Path { get; private set; }
        public byte[] Data { get; private set; }
        public FormatDetectionResult Detection { get; private set; }

        public string FileName
        {
            get { return System.IO.Path.GetFileName(Path); }
        }

        public bool IsEncryptedPt
        {
            get { return Detection != null && Detection.Format == ChartFormat.PtffEncryptedTechnika; }
        }
    }

    internal sealed class ChartOpenResult
    {
        private ChartOpenResult(PlayerData model, string path, string error)
        {
            Model = model;
            Path = path;
            Error = error;
        }

        public PlayerData Model { get; private set; }
        public string Path { get; private set; }
        public string Error { get; private set; }
        public bool Success { get { return Model != null; } }

        public static ChartOpenResult Ok(PlayerData model, string path)
        {
            return new ChartOpenResult(model, path, null);
        }

        public static ChartOpenResult Fail(string error)
        {
            return new ChartOpenResult(null, null, error);
        }
    }

    /// <summary>
    /// Opening and saving charts for the studio shell.
    ///
    /// It drives the same <see cref="LoadHandler"/>/<see cref="SaveHandler"/> registry and the same
    /// content-based <see cref="ChartFormatDetector"/> the legacy editor uses, so every format the
    /// old exe can read the shell can read too - through one implementation, not a second
    /// half-finished loader set. What is different here is the shape, not the parsing: detection is
    /// split from parsing so the caller can put its own UI in between (the encrypted-chart consent
    /// prompt), and the parse itself is awaited off the UI thread instead of being run on a raw
    /// Thread behind a modal progress form.
    /// </summary>
    internal sealed class ChartFileService
    {
        private readonly LoadHandler _load = new LoadHandler();
        private readonly SaveHandler _save = new SaveHandler();

        public ChartFileService()
        {
            // Registration order sets the file-dialog filter order, so the format people open most
            // comes first.
            _load.Register(new PTOpenFile());
            _load.Register(new TQOpenFile());
            _load.Register(new CyclonXmlOpenFile());
            _load.Register(new BmsOpenFile());
            _load.Register(new BmsonOpenFile());
            _load.Register(new DJMaxEditor.Files.Tech.TechOpenFile());

            _save.Register(new PTSaveFile());
            _save.Register(new TQSaveFile());
            _save.Register(new BMESaveFile());
            _save.Register(new BmsonSaveFile());
            _save.Register(new DJMaxEditor.Files.Tech.TechSaveFile());
        }

        public string OpenFilter
        {
            get { return _load.GetFilter() + "|All files|*.*"; }
        }

        public string SaveFilter
        {
            get { return _save.GetFilter(); }
        }

        /// <summary>
        /// Reads the file and identifies it. Never parses, never decrypts, never touches the
        /// network. Returns null only when the bytes could not be read at all.
        /// </summary>
        public ChartProbe Probe(string path, out string error)
        {
            error = null;
            byte[] data;
            try
            {
                data = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                DJMaxEditor.Diagnostics.DiagnosticLog.Exception("open.read", ex);
                error = "The file could not be read.\n\n" + ex.Message;
                return null;
            }

            string extension = (System.IO.Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
            FormatDetectionResult detection = ChartFormatDetector.Detect(data, extension);
            DJMaxEditor.Diagnostics.DiagnosticLog.Write(
                "open.detect", System.IO.Path.GetFileName(path) + ": " + detection);
            return new ChartProbe(path, data, detection);
        }

        /// <summary>
        /// Decrypts a positively-identified encrypted Technika/Trilogy chart, entirely in this
        /// process, and re-identifies the result. Only ever called after the user has agreed to it.
        /// The decrypted bytes must present as a genuine PTFF chart before they go near the parser -
        /// a wrong key produces garbage that would otherwise be fed to the note reader.
        /// </summary>
        public ChartProbe Decrypt(ChartProbe probe, out string error)
        {
            error = null;
            byte[] decrypted;
            try
            {
                decrypted = PtCodec.Decrypt(probe.Data);
            }
            catch (Exception ex)
            {
                DJMaxEditor.Diagnostics.DiagnosticLog.Exception("open.decrypt", ex);
                error = "The chart could not be decrypted.\n\n" + ex.Message +
                        "\n\nFile: " + probe.FileName + "\nThe file was not modified.";
                return null;
            }

            FormatDetectionResult reDetect = ChartFormatDetector.Detect(decrypted, ".pt");
            DJMaxEditor.Diagnostics.DiagnosticLog.Write(
                "open.decrypt", probe.FileName + " -> " + reDetect);

            if (reDetect.Format != ChartFormat.PtffDecrypted)
            {
                error = "Decryption did not produce a valid chart (no PTFF/EZTR structure was " +
                        "found). This file may use a different key or format.\n\nFile: " +
                        probe.FileName;
                return null;
            }

            return new ChartProbe(probe.Path, decrypted, reDetect);
        }

        /// <summary>The loader for a probe, or null when nothing can open it.</summary>
        public IOpenFile HandlerFor(ChartProbe probe)
        {
            if (probe == null || probe.Detection == null || !probe.Detection.IsOpenable)
            {
                return null;
            }

            switch (probe.Detection.Format)
            {
                case ChartFormat.PtffDecrypted:
                    return _load.GetHandlerForExtension(".pt");
                case ChartFormat.TrailerRespectV:
                    return _load.GetHandlerForExtension(".bytes");
                case ChartFormat.CyclonXml:
                    return _load.GetHandlerForExtension(".xml");
                case ChartFormat.BmsClassic:
                    return _load.GetHandlerForExtension(".bms");
                case ChartFormat.Bmson:
                    return _load.GetHandlerForExtension(".bmson");
                case ChartFormat.TechmaniaTrack:
                    return _load.GetHandlerForExtension(".tech");
                default:
                    return null;
            }
        }

        /// <summary>
        /// Lists the difficulty patterns a track.tech container holds, without opening it -
        /// the data behind the open-time difficulty chooser. Null for any other format.
        /// </summary>
        public IList<DJMaxEditor.Files.Tech.TechPatternInfo> EnumeratePatterns(ChartProbe probe)
        {
            TechOpenFile tech = HandlerFor(probe) as TechOpenFile;
            return tech == null ? null : tech.EnumeratePatterns(probe.Path);
        }

        /// <summary>
        /// Parses a probed file off the UI thread. <paramref name="fromDecryptedSource"/> hands the
        /// in-memory decrypted bytes to the PT loader instead of letting it re-read the encrypted
        /// file from disk. <paramref name="patternIndex"/> chooses one difficulty in a multi-pattern
        /// track.tech container; every other pattern is still retained for save-back.
        /// </summary>
        public Task<ChartOpenResult> OpenAsync(
            ChartProbe probe, bool fromDecryptedSource, int patternIndex = 0)
        {
            IOpenFile handler = HandlerFor(probe);
            if (handler == null)
            {
                return Task.FromResult(ChartOpenResult.Fail(DescribeUnopenable(probe)));
            }

            PTOpenFile pt = handler as PTOpenFile;
            if (pt != null)
            {
                pt.SourceOverride = fromDecryptedSource ? probe.Data : null;
                pt.FromEncryptedSource = fromDecryptedSource;
            }

            TechOpenFile tech = handler as TechOpenFile;
            if (tech != null)
            {
                tech.SelectedPatternIndex = Math.Max(0, patternIndex);
            }

            string path = probe.Path;
            bool readOnly = probe.Detection.IsReadOnly;
            ChartFormat format = probe.Detection.Format;

            return Task.Run(() =>
            {
                Logs.Write(string.Format("Openning file {0}", path));
                PlayerData model = null;
                try
                {
                    if (!handler.Open(path, out model) || model == null)
                    {
                        return ChartOpenResult.Fail("The chart could not be parsed.\n\nFile: " +
                                                    System.IO.Path.GetFileName(path));
                    }
                }
                catch (ChartLoadException ex)
                {
                    DJMaxEditor.Diagnostics.DiagnosticLog.Exception("open.parse", ex);
                    return ChartOpenResult.Fail(ex.Message);
                }
                catch (Exception ex)
                {
                    DJMaxEditor.Diagnostics.DiagnosticLog.Exception("open.parse", ex);
                    return ChartOpenResult.Fail("The chart could not be parsed.\n\n" + ex.Message);
                }

                // Read-only-ness and provenance are properties of how the file was identified, so
                // they are stamped here rather than trusted from the parser. |= because a loader is
                // allowed to decide it produced a read-only document for its own reasons.
                model.IsReadOnly |= readOnly;
                if (model.SourceFormat == null)
                {
                    model.SourceFormat = format;
                }
                return ChartOpenResult.Ok(model, path);
            });
        }

        public ISaveFile SaveHandlerFor(string path)
        {
            return _save.GetHandlerForExtension(System.IO.Path.GetExtension(path));
        }

        public Task<string> SaveAsync(string path, PlayerData model, ISaveFile handler)
        {
            return Task.Run(() =>
            {
                try
                {
                    if (!handler.Save(path, model))
                    {
                        return "The chart could not be written.\n\nFile: " +
                               System.IO.Path.GetFileName(path);
                    }
                }
                catch (Exception ex)
                {
                    DJMaxEditor.Diagnostics.DiagnosticLog.Exception("save", ex);
                    return "The chart could not be written.\n\n" + ex.Message;
                }
                return null;
            });
        }

        private static string DescribeUnopenable(ChartProbe probe)
        {
            if (probe == null || probe.Detection == null)
            {
                return "This file is not a chart the editor recognises.";
            }

            string reason = probe.Detection.FailureReason;
            string evidence = probe.Detection.Evidence;

            string text = "This file cannot be opened as a chart.\n\nFile: " + probe.FileName +
                          "\nDetected: " + probe.Detection.Format;
            if (!string.IsNullOrEmpty(reason))
            {
                text += "\nReason: " + reason;
            }
            if (!string.IsNullOrEmpty(evidence))
            {
                text += "\nEvidence: " + evidence;
            }
            return text;
        }
    }
}
