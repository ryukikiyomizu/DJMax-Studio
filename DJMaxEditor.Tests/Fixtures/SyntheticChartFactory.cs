using DJMaxEditor.DJMax;

namespace DJMaxEditor.Tests.Fixtures
{
    internal static class SyntheticChartFactory
    {
        public static PlayerData Create(int rowCount, int eventsPerRow, int tickSpacing)
        {
            var model = new PlayerData
            {
                TickPerMinute = 192,
                Tempo = 140
            };

            for (int row = 0; row < rowCount; row++)
            {
                var track = new TrackData((uint)row) { TrackName = "Synthetic " + row };
                for (int i = 0; i < eventsPerRow; i++)
                {
                    track.AddEvent(new EventData
                    {
                        EventType = EventType.Note,
                        VirtualTick = (i * tickSpacing) + row,
                        VirtualDuration = (ushort)(i % 7 == 0 ? tickSpacing * 2 : 0)
                    });
                }
                model.Tracks.AddTrack(track);
            }

            return model;
        }
    }
}
