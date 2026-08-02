using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using DJMaxEditor.Controls.TimelineV2;
using DJMaxEditor.DJMax;
using DJMaxEditor.Editor;
using DJMaxEditor.Files.Cyclon;
using DJMaxEditor.Files.FormatDetection;
using DJMaxEditor.Files.bytes;
using DJMaxEditor.Files.pt;
using DJMaxEditor.Tests.Fixtures;

namespace DJMaxEditor.Tests
{
    internal static partial class Program
    {
        private static void RunTimelineSurfaceTests(string fixtures)
        {
            Test("EditorSurfaceSelection_FlagOffIsLegacy_FlagOnIsV2", () =>
            {
                AssertTrue(EditorSurfaceSelection.Resolve(false) == EditorSurfaceKind.Legacy,
                    "flag off did not select legacy");
                AssertTrue(EditorSurfaceSelection.Resolve(true) == EditorSurfaceKind.TimelineV2,
                    "flag on did not select Timeline V2");
            });

            Test("TimelineV2Surface_BindsSameModel_ReadOnlyPrototype", () =>
            {
                var model = SyntheticChartFactory.Create(2, 10, 100);
                model.SourceFormat = ChartFormat.PtffDecrypted;
                var context = new EditorDocumentContext(model, "test.pt");

                using (var surface = new TimelineV2Control())
                {
                    surface.Bind(context);
                    AssertTrue(!surface.SupportsEditing, "prototype unexpectedly exposed editing");
                    AssertTrue(object.ReferenceEquals(context, surface.Document),
                        "surface copied/replaced the document context");
                    AssertTrue(surface.StatusText.Contains("PtffDecrypted"),
                        "format status is not persistent");
                    AssertTrue(surface.IndexedItemCount == 20, "surface index lost events");
                }
            });

            Test("TimelineV2Surface_RespectV_ShowsEditableSource", () =>
            {
                var model = SyntheticChartFactory.Create(1, 1, 100);
                model.SourceFormat = ChartFormat.TrailerRespectV;
                model.IsReadOnly = false;

                using (var surface = new TimelineV2Control())
                {
                    surface.Bind(new EditorDocumentContext(model, "respect.pt"));
                    AssertTrue(surface.StatusText.Contains("RESPECT V"), "Respect V label is missing");
                    AssertTrue(surface.StatusText.Contains("EDITABLE"), "editable label is missing");
                }
            });

            Test("TimelineV2Surface_FrameUsesVisibleRangeQuery", () =>
            {
                var model = SyntheticChartFactory.Create(10, 1000, 24);
                var context = new EditorDocumentContext(model, "large.pt");

                using (var surface = new TimelineV2Control())
                {
                    surface.Bind(context);
                    var frame = surface.CreateFrameForTesting(1000, 600);
                    AssertTrue(surface.IndexedItemCount == 10000, "large index count is incorrect");
                    AssertTrue(frame.VisibleItems.Count < surface.IndexedItemCount,
                        "frame requested the full chart");
                    AssertTrue(surface.Performance.LastQueryCandidateCount <
                        surface.IndexedItemCount / 4, "visible query scanned too much data");
                }
            });

            Test("TimelineV2Surface_PlayheadAndViewStateRoundTrip", () =>
            {
                var model = SyntheticChartFactory.Create(1, 100, 100);
                using (var surface = new TimelineV2Control())
                {
                    surface.Bind(new EditorDocumentContext(model, "state.pt"));
                    surface.PlayheadVirtualTick = 900;
                    surface.TrySetTimeZoom(0.75f);
                    var state = surface.CaptureViewState();

                    surface.PlayheadVirtualTick = 1200;
                    surface.TrySetTimeZoom(0.25f);
                    surface.RestoreViewState(state);

                    AssertTrue(surface.PlayheadVirtualTick == 900, "playhead was not restored");
                    AssertNear(0.75, surface.CaptureViewState().PixelsPerTick, 0.000001,
                        "zoom was not restored");
                }
            });

            Test("EditorForm_FeatureFlagHostsEitherSurfaceWithLegacyFallback", () =>
            {
                using (var legacyForm = new EditorForm(false))
                using (var timelineForm = new EditorForm(true))
                {
                    AssertTrue(legacyForm.IsLegacySurfaceActive,
                        "flag off did not keep the legacy control active");
                    AssertTrue(object.ReferenceEquals(legacyForm.Editor, legacyForm.ActiveSurface.View),
                        "legacy adapter does not expose the unchanged EditorControl");
                    AssertTrue(!timelineForm.IsLegacySurfaceActive,
                        "flag on did not activate Timeline V2");
                    AssertTrue(timelineForm.ActiveSurface is TimelineV2Control,
                        "flag on selected the wrong surface type");
                }
            });

            Test("EditorForm_BindUpdatesPersistentDocumentStatus", () =>
            {
                var model = SyntheticChartFactory.Create(1, 1, 100);
                model.SourceFormat = ChartFormat.TrailerRespectV;
                model.IsReadOnly = false;

                using (var form = new EditorForm(true))
                {
                    form.Bind(new EditorDocumentContext(model, "respect.pt"));
                    AssertTrue(form.DocumentStatusText.Contains("RESPECT V"),
                        "host status did not show Respect V");
                    AssertTrue(form.DocumentStatusText.Contains("EDITABLE"),
                        "host status did not show editable state");
                }
            });

            Test("EditorForm_RuntimeSwitchPreservesDocumentAndLegacyFallback", () =>
            {
                var model = SyntheticChartFactory.Create(2, 5, 100);
                model.SourceFormat = ChartFormat.PtffDecrypted;
                var context = new EditorDocumentContext(model, "switch.pt");

                using (var form = new EditorForm(false))
                {
                    form.Bind(context);
                    form.SwitchSurface(true);
                    var timeline = form.ActiveSurface as TimelineV2Control;
                    AssertTrue(timeline != null, "runtime switch did not select Timeline V2");
                    AssertTrue(object.ReferenceEquals(timeline.Document.Model, model),
                        "runtime switch copied or replaced the model");

                    form.SwitchSurface(false);
                    AssertTrue(form.IsLegacySurfaceActive,
                        "runtime switch did not restore the legacy surface");
                    AssertTrue(form.DocumentStatusText.Contains("TIMELINE V1"),
                        "runtime switch did not update persistent status");
                }
            });

            Test("LegacySurface_BindsAndRendersSharedSelectionIdentity", () =>
            {
                var model = SyntheticChartFactory.Create(1, 2, 100);
                var context = new EditorDocumentContext(model, "shared-selection.pt", new UndoManager());
                EventData selected = model.Tracks.Events.First();

                using (var editor = new EditorControl())
                {
                    var surface = new LegacyEditorSurfaceAdapter(editor);
                    surface.Bind(context);
                    context.Selection.Replace(new[] { selected });

                    AssertTrue(editor.SelectedEventCount == 1,
                        "legacy editor did not observe the shared selection service");
                    AssertTrue(object.ReferenceEquals(editor.SelectedEvents.Single(), selected),
                        "legacy editor copied or reinterpreted the selected event");
                }
            });

            Test("LegacySurface_ResizeToolChangesDurationWithoutMovingEvent", () =>
            {
                var model = SyntheticChartFactory.Create(1, 1, 100);
                model.TickPerMinute = 192;
                var context = new EditorDocumentContext(model, "resize.pt", new UndoManager());
                EventData selected = model.Tracks.Events.First();
                selected.VirtualTick = 24;
                selected.VirtualDuration = 24;

                using (var editor = new EditorControl())
                {
                    editor.Size = new Size(900, 420);
                    editor.CreateControl();
                    editor.Bind(context);
                    context.Selection.Replace(new[] { selected });
                    context.Interaction.Tool = TimelineTool.Resize;

                    Rectangle eventBounds = editor.EventsRenderer.GetEventRectangle(selected, 0);
                    int originTick = selected.VirtualTick;
                    int originDuration = selected.VirtualDuration;
                    int handleX = (int)Math.Round(eventBounds.Right * editor.GetZoom());
                    int centerY = (int)Math.Round(
                        (eventBounds.Top + (eventBounds.Height / 2)) *
                        editor.GetZoom());

                    InvokeEditorMouse(
                        editor,
                        "DrawingArea_MouseDown",
                        new MouseEventArgs(MouseButtons.Left, 1, handleX, centerY, 0));
                    InvokeEditorMouse(
                        editor,
                        "DrawingArea_MouseMove",
                        new MouseEventArgs(MouseButtons.Left, 0, handleX + 160, centerY, 0));
                    InvokeEditorMouse(
                        editor,
                        "DrawingArea_MouseMove",
                        new MouseEventArgs(MouseButtons.Left, 0, handleX + 160, centerY, 0));
                    InvokeEditorMouse(
                        editor,
                        "DrawingArea_MouseUp",
                        new MouseEventArgs(MouseButtons.Left, 1, handleX + 160, centerY, 0));

                    AssertTrue(selected.VirtualTick == originTick,
                        "Resize tool moved the note instead of keeping its start fixed");
                    AssertTrue(selected.VirtualDuration > originDuration,
                        "Resize tool did not extend the selected note");
                }
            });

            RealFileTest("TimelineV2_Render_Real_DecryptedPtff", fixtures,
                "TECHNIKA 2 Patterns/tutorial/tutorial_pop_1.pt", data =>
                {
                    string before = Sha256(data);
                    PlayerData model = LoadPtff(data, false);
                    model.SourceFormat = ChartFormat.PtffDecrypted;
                    RenderAndAssert(model, "tutorial_pop_1.pt", false);
                    AssertTrue(before == Sha256(data), "rendering changed decrypted source bytes");
                });

            RealFileTest("TimelineV2_Render_Real_EncryptedThenDecryptedPtff", fixtures,
                "TECHNIKA 2 Patterns/@baramege/@baramege_star_1.pt", data =>
                {
                    string before = Sha256(data);
                    PlayerData model = LoadPtff(PtCodec.Decrypt(data), true);
                    model.SourceFormat = ChartFormat.PtffEncryptedTechnika;
                    RenderAndAssert(model, "@baramege_star_1.pt", false);
                    AssertTrue(before == Sha256(data), "rendering changed encrypted source bytes");
                });

            RealFileTest("TimelineV2_Render_Real_RespectV_EditableSource", fixtures,
                "_analysis_scratch/charts/wakeup/wakeup_4b_nm.pt", data =>
                {
                    string before = Sha256(data);
                    TrailerReadResult result;
                    PlayerData model = TrailerChartReader.Read((byte[])data.Clone(), false, out result);
                    RenderAndAssert(model, "wakeup_4b_nm.pt", false);
                    AssertTrue(before == Sha256(data), "rendering changed Respect V source bytes");
                });

            RealFileTest("TimelineV2_Render_Real_Bytes_EditableSource", fixtures,
                "DMRV_PLI03_LITE/pharmofcharm/pharmofcharm_4b_sc.bytes", data =>
                {
                    string before = Sha256(data);
                    TrailerReadResult result;
                    PlayerData model = TrailerChartReader.Read((byte[])data.Clone(), false, out result);
                    RenderAndAssert(model, "pharmofcharm_4b_sc.bytes", false);
                    AssertTrue(before == Sha256(data), "rendering changed .bytes source bytes");
                });

            Test("TimelineV2_Render_CyclonXml", () =>
            {
                string path = Path.Combine(Path.GetTempPath(), "djme_timeline_cyclon.xml");
                File.WriteAllText(path, SyntheticCyclonXml());
                try
                {
                    var loader = new CyclonXmlOpenFile();
                    PlayerData model;
                    AssertTrue(loader.Open(path, out model), "Cyclon XML loader failed");
                    model.SourceFormat = ChartFormat.CyclonXml;
                    RenderAndAssert(model, path, false);
                }
                finally
                {
                    try { File.Delete(path); } catch { }
                }
            });

            Test("TimelineV2_Performance_10kAnd50k_AreMeasuredAndVisibleRangeBounded", () =>
            {
                MeasureTimeline("10k", 10, 1000, 24);
                MeasureTimeline("50k", 20, 2500, 12);
            });
        }

        private static void InvokeEditorMouse(
            EditorControl editor,
            string methodName,
            MouseEventArgs args)
        {
            MethodInfo method = typeof(EditorControl).GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            AssertTrue(method != null, "missing editor gesture method " + methodName);
            method.Invoke(editor, new object[] { editor, args });
        }

        private static PlayerData LoadPtff(byte[] data, bool encryptedSource)
        {
            var loader = new PTOpenFile
            {
                SourceOverride = data,
                FromEncryptedSource = encryptedSource
            };
            PlayerData model;
            AssertTrue(loader.Open("timeline_fixture.pt", out model), "PTFF loader failed");
            return model;
        }

        private static void RenderAndAssert(
            PlayerData model,
            string sourcePath,
            bool expectReadOnly)
        {
            using (var surface = new TimelineV2Control())
            {
                surface.Bind(new EditorDocumentContext(model, sourcePath));
                var frame = surface.CreateFrameForTesting(1920, 1080);
                AssertTrue(frame.Rows.Count == model.Tracks.Count(),
                    "raw projection changed the track count");
                AssertTrue(surface.IndexedItemCount > 0, "loaded chart rendered no events");
                AssertTrue(frame.IsReadOnly, "Timeline V2 prototype must be visibly view-only");
                if (expectReadOnly)
                {
                    AssertTrue(surface.StatusText.Contains("READ ONLY"),
                        "read-only document lacks a persistent lock label");
                }
                using (Bitmap bitmap = surface.RenderSnapshot(1920, 1080))
                {
                    AssertTrue(bitmap.Width == 1920 && bitmap.Height == 1080,
                        "1080p render dimensions changed");
                }
                Console.WriteLine(
                    "[METRIC] TimelineV2 real {0}: events={1}, index={2:F2}ms, query={3:F2}ms, " +
                    "frame={4:F2}ms, visible={5}, candidates={6}",
                    Path.GetFileName(sourcePath),
                    surface.IndexedItemCount,
                    surface.Performance.LastIndexBuildMilliseconds,
                    surface.Performance.LastQueryMilliseconds,
                    surface.Performance.LastFrameMilliseconds,
                    surface.Performance.LastVisibleItemCount,
                    surface.Performance.LastQueryCandidateCount);
            }
        }

        private static void MeasureTimeline(
            string label,
            int rowCount,
            int eventsPerRow,
            int tickSpacing)
        {
            PlayerData model = SyntheticChartFactory.Create(rowCount, eventsPerRow, tickSpacing);
            using (var surface = new TimelineV2Control())
            {
                var bind = Stopwatch.StartNew();
                surface.Bind(new EditorDocumentContext(model, label + ".pt"));
                bind.Stop();
                using (surface.RenderSnapshot(1920, 1080))
                {
                }

                Console.WriteLine(
                    "[METRIC] TimelineV2 {0}: events={1}, bind={2:F2}ms, query={3:F2}ms, " +
                    "frame={4:F2}ms, visible={5}, candidates={6}",
                    label,
                    surface.IndexedItemCount,
                    bind.Elapsed.TotalMilliseconds,
                    surface.Performance.LastQueryMilliseconds,
                    surface.Performance.LastFrameMilliseconds,
                    surface.Performance.LastVisibleItemCount,
                    surface.Performance.LastQueryCandidateCount);

                AssertTrue(surface.Performance.LastVisibleItemCount < surface.IndexedItemCount,
                    label + " render requested the whole chart");
                AssertTrue(surface.Performance.LastQueryCandidateCount < surface.IndexedItemCount,
                    label + " query inspected the whole chart");
            }
        }

        private static string SyntheticCyclonXml()
        {
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<root><header><version date=\"2026-07-26\"/>" +
                "<songinfo tempo=\"140\" tpm=\"192\" start_tick=\"0\" end_tick=\"768\" " +
                "tick=\"0\" ms=\"4000\" track_cnt=\"2\" tps=\"1\"/></header>" +
                "<instrument><ins idx=\"1\" name=\"kick.wav\"/></instrument>" +
                "<tempo><tempo tick=\"0\" tempo=\"140\" tps=\"1\"/></tempo>" +
                "<note_list><track idx=\"0\"><note tick=\"0\" ins=\"1\" attr=\"0\" dur=\"0\"/>" +
                "</track><track idx=\"1\"><note tick=\"192\" ins=\"1\" attr=\"0\" dur=\"96\"/>" +
                "</track></note_list></root>";
        }
    }
}
