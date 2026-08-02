using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using DJMaxEditor.Controls.Editor.Renderers.Events;
using DJMaxEditor.Controls.TimelineV2;
using DJMaxEditor.DJMax;
using DJMaxEditor.Editor;
using DJMaxEditor.Files.FormatDetection;
using DJMaxEditor.Files.bytes;
using DJMaxEditor.Files.pt;

namespace DJMaxEditor.Tests
{
    internal static partial class Program
    {
        private static void ExportTimelineSnapshots(string fixtures, string outputDirectory)
        {
            if (string.IsNullOrEmpty(fixtures))
            {
                Console.WriteLine("[SKIP] Timeline snapshots (no --fixtures)");
                return;
            }

            Directory.CreateDirectory(outputDirectory);
            ExportPtffSnapshot(
                Path.Combine(fixtures, "TECHNIKA 2 Patterns", "tutorial", "tutorial_pop_1.pt"),
                Path.Combine(outputDirectory, "timeline-v2-decrypted-1080p.png"),
                false);
            ExportTechnikaSpecialNoteSnapshot(
                Path.Combine(fixtures, "TECHNIKA 2 Patterns", "sonof", "sonof_pop_3.pt"),
                Path.Combine(
                    outputDirectory,
                    "timeline-v2-sonof-technika-special-notes-1080p.png"));
            ExportPtffSnapshot(
                Path.Combine(fixtures, "TECHNIKA 2 Patterns", "@baramege", "@baramege_star_1.pt"),
                Path.Combine(outputDirectory, "timeline-v2-encrypted-decrypted-1080p.png"),
                true);
            ExportTrailerSnapshot(
                Path.Combine(fixtures, "_analysis_scratch", "charts", "wakeup", "wakeup_4b_nm.pt"),
                Path.Combine(outputDirectory, "timeline-v2-respect-readonly-1080p.png"));
        }

        private static void ExportTechnikaSpecialNoteSnapshot(
            string sourcePath,
            string outputPath)
        {
            if (!File.Exists(sourcePath)) return;
            PlayerData model = LoadPtff(
                PtCodec.Decrypt(File.ReadAllBytes(sourcePath)),
                true);
            EventData firstRepeat = model.Tracks
                .SelectMany(track => track.Events)
                .First(note => note.Attribute == 10);

            using (var surface = new TimelineV2Control())
            {
                surface.EventTheme = new TechnikaThemeRenderer();
                surface.Bind(new EditorDocumentContext(model, sourcePath));
                surface.RestoreViewState(new EditorViewState
                {
                    OriginTick = Math.Max(0, firstRepeat.VirtualTick - 1200),
                    PixelsPerTick = 0.09,
                    FirstVisibleRow = Math.Max(0, (int)firstRepeat.TrackId - 4),
                    PlayheadVirtualTick = firstRepeat.VirtualTick
                });
                using (Bitmap bitmap = surface.RenderSnapshot(1920, 1080))
                {
                    bitmap.Save(outputPath, ImageFormat.Png);
                }
                Console.WriteLine("[ARTIFACT] " + outputPath);

                surface.ApplyTouchpadZoom(1000, 240);
                string zoomedOutputPath = Path.Combine(
                    Path.GetDirectoryName(outputPath),
                    Path.GetFileNameWithoutExtension(outputPath) +
                    "-touchpad-zoom.png");
                using (Bitmap bitmap = surface.RenderSnapshot(1920, 1080))
                {
                    bitmap.Save(zoomedOutputPath, ImageFormat.Png);
                }
                Console.WriteLine("[ARTIFACT] " + zoomedOutputPath);
            }
        }

        private static void ExportPtffSnapshot(
            string sourcePath,
            string outputPath,
            bool encrypted)
        {
            if (!File.Exists(sourcePath)) return;
            byte[] source = File.ReadAllBytes(sourcePath);
            PlayerData model = LoadPtff(encrypted ? PtCodec.Decrypt(source) : source, encrypted);
            model.SourceFormat = encrypted
                ? ChartFormat.PtffEncryptedTechnika
                : ChartFormat.PtffDecrypted;
            ExportSnapshot(model, sourcePath, outputPath);
        }

        private static void ExportTrailerSnapshot(string sourcePath, string outputPath)
        {
            if (!File.Exists(sourcePath)) return;
            TrailerReadResult result;
            PlayerData model = TrailerChartReader.Read(
                File.ReadAllBytes(sourcePath),
                false,
                out result);
            ExportSnapshot(model, sourcePath, outputPath);
        }

        private static void ExportSnapshot(
            PlayerData model,
            string sourcePath,
            string outputPath)
        {
            using (var surface = new TimelineV2Control())
            {
                surface.Bind(new EditorDocumentContext(model, sourcePath));
                using (Bitmap bitmap = surface.RenderSnapshot(1920, 1080))
                {
                    bitmap.Save(outputPath, ImageFormat.Png);
                }
                Console.WriteLine("[ARTIFACT] " + outputPath);
            }
        }
    }
}
