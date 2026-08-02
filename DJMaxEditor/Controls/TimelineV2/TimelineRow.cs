using DJMaxEditor.DJMax;

namespace DJMaxEditor.Controls.TimelineV2
{
    public sealed class TimelineRow
    {
        public TimelineRow(int index, uint sourceTrackId, string name, TrackData sourceTrack)
        {
            Index = index;
            SourceTrackId = sourceTrackId;
            Name = string.IsNullOrWhiteSpace(name) ? "Track " + sourceTrackId : name;
            SourceTrack = sourceTrack;
        }

        public int Index { get; private set; }

        public uint SourceTrackId { get; private set; }

        public string Name { get; private set; }

        public TrackData SourceTrack { get; private set; }
    }
}
