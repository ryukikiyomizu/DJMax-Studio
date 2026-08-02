using System;
using System.Collections.Generic;
using DJMaxEditor.DJMax;
using DJMaxEditor.Editor;

namespace DJMaxEditor.Controls.TimelineV2.Projection
{
    /// <summary>
    /// Safe projection that mirrors source tracks exactly. It is valid for every loaded format and
    /// is the mandatory fallback whenever gameplay mapping confidence is insufficient.
    /// </summary>
    public sealed class RawTrackProjection : ITimelineProjection
    {
        public string Id
        {
            get { return "raw-tracks"; }
        }

        public bool IsVerifiedFor(EditorDocumentContext document)
        {
            return document != null && document.Model != null;
        }

        public TimelineProjectionResult Build(EditorDocumentContext document)
        {
            if (document == null) throw new ArgumentNullException("document");

            var rows = new List<TimelineRow>();
            var items = new List<TimelineItem>();
            int rowIndex = 0;

            foreach (TrackData track in document.Model.Tracks)
            {
                var row = new TimelineRow(
                    rowIndex,
                    track.Idx,
                    track.DisplayedTrackName,
                    track);
                rows.Add(row);

                foreach (EventData sourceEvent in track.Events)
                {
                    int startTick = sourceEvent.VirtualTick;
                    int endTick = startTick + sourceEvent.VirtualDuration;
                    items.Add(new TimelineItem(rowIndex, startTick, endTick, sourceEvent));
                }

                rowIndex++;
            }

            return new TimelineProjectionResult(rows, items);
        }
    }
}
