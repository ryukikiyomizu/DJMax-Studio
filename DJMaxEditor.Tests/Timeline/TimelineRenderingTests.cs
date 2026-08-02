using System.Drawing;
using DJMaxEditor.Controls.TimelineV2;
using DJMaxEditor.Controls.TimelineV2.Renderers;
using DJMaxEditor.DJMax;

namespace DJMaxEditor.Tests
{
    internal static partial class Program
    {
        private static void RunTimelineRenderingTests()
        {
            Test("TimelineRenderer_DrawsPinnedFramePlayheadLockAndMinimap", () =>
            {
                var coordinates = new TimelineCoordinateSystem(0.5, 28, 160, 42, 1f);
                var viewport = new TimelineViewport(0, 4000, 800, coordinates.HeaderWidth)
                {
                    PixelsPerTick = coordinates.PixelsPerTick
                };
                var rows = new[]
                {
                    new TimelineRow(0, 0, "Track 0", new TrackData(0)),
                    new TimelineRow(1, 1, "Track 1", new TrackData(1))
                };
                var normal = new TimelineItem(
                    0, 200, 200,
                    new EventData { EventType = EventType.Note, VirtualTick = 200 });
                var unknown = new TimelineItem(
                    1, 500, 700,
                    new EventData { EventType = (EventType)99, VirtualTick = 500, VirtualDuration = 200 });
                var frame = new TimelineFrame(
                    800,
                    400,
                    coordinates,
                    viewport,
                    rows,
                    new[] { normal, unknown },
                    0,
                    600,
                    true,
                    "RESPECT V - READ ONLY",
                    "TrailerRespectV | RAW TRACKS",
                    1152,
                    4,
                    new[] { 0, 1, 3, 2, 0, 4, 1, 0 });

                using (var bitmap = new Bitmap(800, 400))
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    new TimelineRenderer().Render(graphics, frame);

                    AssertTrue(bitmap.GetPixel(10, 80).ToArgb() != Color.Empty.ToArgb(),
                        "row-header/background pass did not draw");
                    int playheadX = (int)viewport.ScreenXAtTick(600);
                    AssertTrue(bitmap.GetPixel(playheadX, 100).ToArgb() ==
                        TimelineRenderTheme.Playhead.ToArgb(), "playhead pass did not draw");
                    AssertTrue(bitmap.GetPixel(400, 390).ToArgb() != Color.Empty.ToArgb(),
                        "minimap pass did not draw");
                }
            });

            Test("TimelineRenderer_UnknownEvent_UsesFallbackGlyph", () =>
            {
                var coordinates = new TimelineCoordinateSystem(1, 30, 100, 40, 1f);
                var viewport = new TimelineViewport(0, 1000, 500, 100) { PixelsPerTick = 1 };
                var row = new TimelineRow(0, 0, "Unknown", new TrackData(0));
                var unknown = new TimelineItem(
                    0, 100, 120,
                    new EventData { EventType = (EventType)99, VirtualTick = 100, VirtualDuration = 20 });
                var frame = new TimelineFrame(
                    500, 240, coordinates, viewport, new[] { row }, new[] { unknown }, 0, 0,
                    false, string.Empty, "Unknown | RAW TRACKS", 0, 0, new[] { 0, 1, 0 });

                using (var bitmap = new Bitmap(500, 240))
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    new TimelineRenderer().Render(graphics, frame);
                    int x = (int)viewport.ScreenXAtTick(100);
                    int y = coordinates.RowToY(0, 0) + coordinates.RowHeight / 2;
                    AssertTrue(bitmap.GetPixel(x, y).ToArgb() == TimelineRenderTheme.Warning.ToArgb(),
                        "unknown event did not use the warning fallback glyph");
                }
            });

            Test("TimelineRenderer_CachesStaticPlaybackLayerAndInvalidatesChanges", () =>
            {
                var coordinates = new TimelineCoordinateSystem(1, 30, 100, 40, 1f);
                var viewport = new TimelineViewport(0, 2000, 500, 100)
                {
                    PixelsPerTick = 1
                };
                var row = new TimelineRow(0, 0, "Track 0", new TrackData(0));
                var source = new EventData
                {
                    EventType = EventType.Note,
                    VirtualTick = 200,
                    Attribute = 1
                };
                var item = new TimelineItem(0, 200, 200, source);
                var density = new[] { 0, 1, 0 };

                using (var renderer = new TimelineRenderer())
                using (var bitmap = new Bitmap(500, 240))
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    renderer.Render(
                        graphics,
                        new TimelineFrame(
                            500, 240, coordinates, viewport, new[] { row },
                            new[] { item }, 0, 100, false, string.Empty,
                            "PTFF | RAW TRACKS", 1152, 4, density));
                    AssertTrue(renderer.StaticFrameRebuildCount == 1,
                        "initial static layer was not built exactly once");

                    renderer.Render(
                        graphics,
                        new TimelineFrame(
                            500, 240, coordinates, viewport, new[] { row },
                            new[] { item }, 0, 120, false, string.Empty,
                            "PTFF | RAW TRACKS", 1152, 4, density));
                    AssertTrue(renderer.StaticFrameRebuildCount == 1,
                        "playhead-only frame rebuilt static timeline art");
                    int oldPlayheadX = (int)viewport.ScreenXAtTick(100);
                    int newPlayheadX = (int)viewport.ScreenXAtTick(120);
                    AssertTrue(
                        bitmap.GetPixel(oldPlayheadX, 100).ToArgb() !=
                            TimelineRenderTheme.Playhead.ToArgb(),
                        "cached frame retained the old playhead");
                    AssertTrue(
                        bitmap.GetPixel(newPlayheadX, 100).ToArgb() ==
                            TimelineRenderTheme.Playhead.ToArgb(),
                        "dynamic layer did not draw the new playhead");

                    viewport.OriginTick = 50;
                    renderer.Render(
                        graphics,
                        new TimelineFrame(
                            500, 240, coordinates, viewport, new[] { row },
                            new[] { item }, 0, 120, false, string.Empty,
                            "PTFF | RAW TRACKS", 1152, 4, density));
                    AssertTrue(renderer.StaticFrameRebuildCount == 2,
                        "viewport movement did not invalidate static art");

                    source.Attribute = 10;
                    renderer.Render(
                        graphics,
                        new TimelineFrame(
                            500, 240, coordinates, viewport, new[] { row },
                            new[] { item }, 0, 120, false, string.Empty,
                            "PTFF | RAW TRACKS", 1152, 4, density));
                    AssertTrue(renderer.StaticFrameRebuildCount == 3,
                        "event mutation did not invalidate static art");
                }
            });
        }
    }
}
