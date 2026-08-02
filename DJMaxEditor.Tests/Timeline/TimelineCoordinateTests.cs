using System;
using DJMaxEditor.Controls.TimelineV2;

namespace DJMaxEditor.Tests
{
    internal static partial class Program
    {
        private static void RunTimelineCoordinateTests()
        {
            Test("TimelineCoordinates_TickX_RoundTripsAcrossDpi", () =>
            {
                foreach (float dpi in new[] { 1f, 1.25f, 1.5f, 2f })
                {
                    var coordinates = new TimelineCoordinateSystem(0.35, 28, 180, 42, dpi);
                    foreach (int tick in new[] { 0, 1, 17, 960, 12345 })
                    {
                        double x = coordinates.TickToX(tick, 111);
                        int roundTrip = coordinates.XToTick(x, 111);
                        AssertTrue(Math.Abs(roundTrip - tick) <= 1,
                            "tick/X round trip drifted at DPI " + dpi);
                    }
                }
            });

            Test("TimelineCoordinates_RowY_UsesPinnedHeaderAndDpi", () =>
            {
                var coordinates = new TimelineCoordinateSystem(0.25, 30, 160, 40, 1.5f);
                AssertTrue(coordinates.HeaderWidth == 240, "row header was not DPI scaled");
                AssertTrue(coordinates.RulerHeight == 60, "ruler was not DPI scaled");
                AssertTrue(coordinates.RowHeight == 45, "row height was not DPI scaled");
                AssertTrue(coordinates.RowToY(8, 5) == 195, "row Y is incorrect");
                AssertTrue(coordinates.YToRow(196, 5) == 8, "Y to row is incorrect");
                AssertTrue(coordinates.YToRow(10, 5) == -1, "ruler area must not resolve to a row");
            });

            Test("TimelineViewport_CursorZoom_PreservesTick", () =>
            {
                var viewport = new TimelineViewport(0, 100000, 1000, 180)
                {
                    OriginTick = 1000,
                    PixelsPerTick = 0.25
                };

                const double cursorX = 620;
                double before = viewport.TickAtScreenX(cursorX);
                viewport.ZoomAt(cursorX, 1.6);
                double after = viewport.TickAtScreenX(cursorX);

                AssertTrue(Math.Abs(after - before) * viewport.PixelsPerTick <= 1.0,
                    "cursor-centered zoom moved the anchored tick by more than one pixel");
            });

            Test("TimelineViewport_ClampsZoomAndDocumentBounds", () =>
            {
                var viewport = new TimelineViewport(0, 1000, 900, 100);
                viewport.PixelsPerTick = 999;
                AssertNear(viewport.MaxPixelsPerTick, viewport.PixelsPerTick, 0.000001,
                    "maximum zoom was not clamped");

                viewport.PixelsPerTick = 0;
                AssertNear(viewport.MinPixelsPerTick, viewport.PixelsPerTick, 0.000001,
                    "minimum zoom was not clamped");

                viewport.OriginTick = -500;
                AssertNear(0, viewport.OriginTick, 0.000001, "origin escaped chart start");

                viewport.OriginTick = 5000;
                AssertTrue(viewport.VisibleTimeRange.EndTick <= 1000.000001,
                    "viewport escaped chart end");
            });

            Test("TimelineViewport_PanIsPixelSmooth", () =>
            {
                var viewport = new TimelineViewport(0, 100000, 1000, 180)
                {
                    OriginTick = 5000,
                    PixelsPerTick = 0.5
                };

                viewport.PanByPixels(1.5);
                AssertNear(4997, viewport.OriginTick, 0.000001,
                    "fractional-pixel pan was quantized or reversed");
            });

            Test("TimelineRanges_NormalizeAndIntersect", () =>
            {
                var range = new TimelineTimeRange(100, 20);
                AssertNear(20, range.StartTick, 0.000001, "time range did not normalize");
                AssertNear(100, range.EndTick, 0.000001, "time range did not normalize");
                AssertTrue(range.Intersects(90, 120), "overlap at range end was missed");
                AssertTrue(!range.Intersects(101, 120), "non-overlap was included");

                var rows = new TimelineRowRange(7, 3);
                AssertTrue(rows.FirstRow == 3 && rows.LastRow == 7, "row range did not normalize");
                AssertTrue(rows.Contains(5) && !rows.Contains(8), "row containment is incorrect");
            });
        }

        private static void AssertNear(double expected, double actual, double tolerance, string message)
        {
            if (Math.Abs(expected - actual) > tolerance)
            {
                throw new Exception(message + ": expected " + expected + ", got " + actual);
            }
        }
    }
}
