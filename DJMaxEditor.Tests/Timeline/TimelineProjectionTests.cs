using System.Linq;
using DJMaxEditor.Controls.TimelineV2.Projection;
using DJMaxEditor.DJMax;
using DJMaxEditor.Editor;
using DJMaxEditor.Files.FormatDetection;
using DJMaxEditor.Tests.Fixtures;

namespace DJMaxEditor.Tests
{
    internal static partial class Program
    {
        private static void RunTimelineProjectionTests()
        {
            Test("RawProjection_PreservesRowsAndEventIdentity", () =>
            {
                var model = SyntheticChartFactory.Create(3, 4, 100);
                model.SourceFormat = ChartFormat.PtffDecrypted;
                var original = model.Tracks.GetTrackAtIndex(1).Events.ElementAt(2);
                var projection = new RawTrackProjection();
                var result = projection.Build(new EditorDocumentContext(model, "synthetic.pt"));

                AssertTrue(result.Rows.Count == 3, "raw projection lost tracks");
                AssertTrue(result.Rows[1].SourceTrackId == 1, "source track identity changed");
                var projected = result.Items.Single(i => object.ReferenceEquals(i.SourceEvent, original));
                AssertTrue(projected.RowIndex == 1, "event moved to a different raw row");
                AssertTrue(object.ReferenceEquals(projected.SourceEvent, original),
                    "projection copied/replaced EventData");
            });

            Test("RawProjection_PreservesUnknownEvents", () =>
            {
                var model = SyntheticChartFactory.Create(1, 0, 100);
                var unknown = new EventData { EventType = (EventType)99, VirtualTick = 500 };
                model.Tracks.GetTrackAtIndex(0).AddEvent(unknown);

                var result = new RawTrackProjection().Build(new EditorDocumentContext(model, "unknown.pt"));
                AssertTrue(result.Items.Count == 1, "unknown event was hidden");
                AssertTrue(result.Items[0].IsUnknown, "unknown event lost its fallback state");
                AssertTrue(object.ReferenceEquals(result.Items[0].SourceEvent, unknown),
                    "unknown event identity changed");
            });

            Test("RawProjection_EmptyChart_IsSafe", () =>
            {
                var result = new RawTrackProjection().Build(
                    new EditorDocumentContext(new PlayerData(), "empty.pt"));
                AssertTrue(result.Rows.Count == 0, "empty chart invented rows");
                AssertTrue(result.Items.Count == 0, "empty chart invented events");
            });
        }
    }
}
