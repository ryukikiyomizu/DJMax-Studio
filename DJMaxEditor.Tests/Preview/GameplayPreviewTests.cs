using System;
using System.Drawing;
using System.Linq;
using DJMaxEditor.DJMax;
using DJMaxEditor.Editor;
using DJMaxEditor.Files.FormatDetection;
using DJMaxEditor.Preview;

namespace DJMaxEditor.Tests
{
    internal static partial class Program
    {
        private static void RunGameplayPreviewTests()
        {
            Test("GameplayPreview_ProfileResolverNeverClaimsAmbiguousPtffIsCertain", () =>
            {
                var ptff = PreviewModel(ChartFormat.PtffDecrypted, 4);
                GameplayPreviewProfileSuggestion suggestion =
                    GameplayPreviewProfileResolver.Suggest(ptff);

                AssertTrue(suggestion.Profile == GameplayPreviewProfile.Technika,
                    "PTFF should offer the TECHNIKA projection");
                AssertTrue(suggestion.RequiresConfirmation,
                    "ambiguous Technika/Trilogy PTFF must require explicit confirmation");

                var respect = PreviewModel(ChartFormat.TrailerRespectV, 4);
                suggestion = GameplayPreviewProfileResolver.Suggest(respect);
                AssertTrue(suggestion.Profile == GameplayPreviewProfile.Generic,
                    "Respect must use the generic preview");
                AssertTrue(!suggestion.RequiresConfirmation,
                    "generic Respect projection should not request TECHNIKA confirmation");
            });

            Test("GameplayPreview_UsesExactTwoWayTechnikaGeometry", () =>
            {
                PlayerData model = PreviewModel(ChartFormat.PtffDecrypted, 4);
                AddPreviewNote(model, 0, 0, 0, 6);
                AddPreviewNote(model, 1, 192, 0, 6);
                AddPreviewNote(model, 3, 96, 0, 6);

                GameplayPreviewProjection chart =
                    GameplayPreviewProjector.Project(model, GameplayPreviewProfile.Technika);
                ProjectedGameplayNote bottom = chart.Notes.Single(n => n.Source.Tick == 0);
                ProjectedGameplayNote top = chart.Notes.Single(n => n.Source.Tick == 192);

                AssertPreviewNear(bottom.X, 0.85, 0.0001, "scan 0 must begin bottom-right");
                AssertPreviewNear(top.X, 0.15, 0.0001, "scan 1 must begin top-left");
                AssertTrue(!bottom.IsTopHalf && top.IsTopHalf,
                    "even/odd scans did not alternate bottom/top");
                AssertPreviewNear(chart.Notes.Single(n => n.Source.TrackId == 0).Y,
                    0.58125, 0.0001, "four-lane bottom center changed");
            });

            Test("GameplayPreview_EndOfScanMarkerKeepsDividerNoteOnPreviousHalf", () =>
            {
                PlayerData model = PreviewModel(ChartFormat.PtffDecrypted, 8);
                EventData visible = AddPreviewNote(model, 0, 192, 0, 6);
                AddPreviewNote(model, 4, 192, 0, 6);

                GameplayPreviewProjection chart =
                    GameplayPreviewProjector.Project(model, GameplayPreviewProfile.Technika);
                ProjectedGameplayNote note = chart.Notes.Single(n => n.Source == visible);

                AssertTrue(note.EndOfScan, "matching special-track marker was ignored");
                AssertTrue(note.ScanIndex == 0 && !note.IsTopHalf,
                    "divider note moved to the next scan");
                AssertPreviewNear(note.RelativeScan, 1.0, 0.0001,
                    "divider note should land at the outgoing edge");
                AssertPreviewNear(note.X, 0.10, 0.0001,
                    "bottom RTL outgoing edge is incorrect");
            });

            Test("GameplayPreview_AppliesChartWideChainAndRepeatFixups", () =>
            {
                PlayerData model = PreviewModel(ChartFormat.PtffDecrypted, 4);
                AddPreviewNote(model, 0, 12, 5, 6);
                EventData promoted = AddPreviewNote(model, 1, 24, 0, 6);
                EventData reverted = AddPreviewNote(model, 2, 36, 0, 6);
                AddPreviewNote(model, 3, 36, 6, 6);
                AddPreviewNote(model, 0, 48, 10, 6);
                EventData repeatMember = AddPreviewNote(model, 0, 60, 10, 12);
                AddPreviewNote(model, 0, 72, 11, 6);

                GameplayPreviewProjection chart =
                    GameplayPreviewProjector.Project(model, GameplayPreviewProfile.Technika);

                AssertTrue(chart.Notes.Single(n => n.Source == promoted).Kind ==
                    GameplayPreviewNoteKind.ChainNode,
                    "basic note inside the global chain was not promoted");
                AssertTrue(chart.Notes.Single(n => n.Source == reverted).Kind ==
                    GameplayPreviewNoteKind.Basic,
                    "simultaneous implicit node was not reverted at chain close");
                AssertTrue(chart.Notes.Single(n => n.Source == repeatMember).Kind ==
                    GameplayPreviewNoteKind.RepeatHold,
                    "second per-lane repeat head was not converted to a repeat member");
            });

            Test("GameplayPreview_FrameUsesTheSharedModelTickAndActivationBoundary", () =>
            {
                PlayerData model = PreviewModel(ChartFormat.PtffDecrypted, 4);
                EventData next = AddPreviewNote(model, 0, 192, 0, 6);
                GameplayPreviewProjection chart =
                    GameplayPreviewProjector.Project(model, GameplayPreviewProfile.Technika);

                model.CurrentTick = 167;
                GameplayPreviewFrame before = chart.CreateFrame(model.CurrentTick);
                model.CurrentTick = 168;
                GameplayPreviewFrame boundary = chart.CreateFrame(model.CurrentTick);

                AssertTrue(before.Notes.Single(n => n.Source == next).State ==
                    GameplayPreviewNoteState.Prepare,
                    "next scan activated before the 0.875 boundary");
                AssertTrue(boundary.Notes.Single(n => n.Source == next).State ==
                    GameplayPreviewNoteState.Active,
                    "next scan did not activate at the 0.875 boundary");
                AssertTrue(boundary.CurrentTick == model.CurrentTick,
                    "preview frame did not consume the shared model tick");
            });

            Test("GameplayPreview_RenderableFrameOnlyClonesTheVisiblePlaybackWindow", () =>
            {
                PlayerData model = PreviewModel(ChartFormat.PtffDecrypted, 4);
                EventData current = AddPreviewNote(model, 0, 0, 0, 6);
                EventData next = AddPreviewNote(model, 1, 192, 0, 6);
                AddPreviewNote(model, 2, 384, 0, 6);
                GameplayPreviewProjection chart =
                    GameplayPreviewProjector.Project(model, GameplayPreviewProfile.Technika);

                GameplayPreviewFrame frame = chart.CreateRenderableFrame(24);

                AssertTrue(frame.Notes.Any(note => note.Source == current),
                    "current Technika scan was omitted from the renderable frame");
                AssertTrue(frame.Notes.Any(note => note.Source == next),
                    "next Technika scan was omitted from the renderable frame");
                AssertTrue(frame.Notes.Count < chart.Notes.Count,
                    "renderable frame cloned notes outside the visible playback window");
            });

            Test("GameplayPreview_GenericModeDoesNotApplyTechnikaSpecialTracks", () =>
            {
                PlayerData model = PreviewModel(ChartFormat.TrailerRespectV, 8);
                EventData visible = AddPreviewNote(model, 0, 192, 0, 6);
                AddPreviewNote(model, 4, 192, 0, 6);

                GameplayPreviewProjection chart =
                    GameplayPreviewProjector.Project(model, GameplayPreviewProfile.Generic);
                ProjectedGameplayNote note = chart.Notes.Single(n => n.Source == visible);

                AssertTrue(!note.EndOfScan,
                    "generic preview applied TECHNIKA special-track semantics");
                AssertTrue(chart.StatusLabel.IndexOf("APPROX", StringComparison.OrdinalIgnoreCase) >= 0,
                    "generic preview is not visibly labelled as an approximation");
            });

            Test("GameplayPreviewDock_BindsTheSameDocumentAndNeverEdits", () =>
            {
                var document = new EditorDocumentContext(
                    PreviewModel(ChartFormat.PtffDecrypted, 4),
                    "preview.pt");
                using (var dock = new GameplayPreviewForm())
                {
                    dock.Bind(document);
                    AssertTrue(object.ReferenceEquals(dock.Document, document),
                        "preview cloned or replaced the editor document context");
                    AssertTrue(!dock.SupportsEditing,
                        "preview must remain a visualization-only surface");
                }
            });

            Test("GameplayPreviewDock_RejectsTechnikaProfileForRespect", () =>
            {
                var document = new EditorDocumentContext(
                    PreviewModel(ChartFormat.TrailerRespectV, 4),
                    "respect.pt");
                using (var dock = new GameplayPreviewForm())
                {
                    dock.Bind(document);
                    dock.ConfirmTechnikaProfile();
                    AssertTrue(dock.Profile == GameplayPreviewProfile.Generic,
                        "Respect was allowed to use PT special-track gameplay semantics");
                }
            });

            Test("GameplayPreviewControl_RendersAResizableReadOnlyFrame", () =>
            {
                PlayerData model = PreviewModel(ChartFormat.PtffDecrypted, 4);
                AddPreviewNote(model, 0, 24, 0, 6);
                AddPreviewNote(model, 3, 96, 12, 48);
                model.CurrentTick = 30;
                using (var control = new GameplayPreviewControl())
                using (var bitmap = new Bitmap(960, 540))
                {
                    control.Size = bitmap.Size;
                    control.Bind(new EditorDocumentContext(model, "render.pt"));
                    control.SetProfile(GameplayPreviewProfile.Technika);
                    control.NoteZoom = 1.8f;
                    control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));

                    AssertTrue(bitmap.GetPixel(480, 270) != Color.Empty,
                        "preview produced no drawable frame");
                    AssertTrue(control.NoteZoom == 1.8f,
                        "preview note-size zoom was not retained");
                }
            });
        }

        private static PlayerData PreviewModel(ChartFormat format, int tracks)
        {
            var model = new PlayerData
            {
                SourceFormat = format,
                TickPerMinute = 192,
                Tempo = 120
            };
            for (uint i = 0; i < tracks; i++)
            {
                model.Tracks.AddTrack(new TrackData(i));
            }
            return model;
        }

        private static EventData AddPreviewNote(
            PlayerData model,
            uint track,
            int tick,
            byte attribute,
            ushort duration)
        {
            var note = new EventData
            {
                EventType = EventType.Note,
                Tick = tick,
                Attribute = attribute,
                Duration = duration
            };
            model.Tracks.GetTrackAtIndex(track).AddEvent(note);
            return note;
        }

        private static void AssertPreviewNear(
            double actual,
            double expected,
            double tolerance,
            string message)
        {
            AssertTrue(Math.Abs(actual - expected) <= tolerance,
                message + " (expected " + expected + ", got " + actual + ")");
        }
    }
}
