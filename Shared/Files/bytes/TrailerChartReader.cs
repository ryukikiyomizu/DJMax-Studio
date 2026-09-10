using System;
using System.Linq;
using DJMaxEditor.DJMax;
using DJMaxEditor.Files;
using DJMaxEditor.Files.FormatDetection;

namespace DJMaxEditor.Files.bytes
{
    /// <summary>Outcome of a trailer-format read: the chart plus resolution statistics for diagnostics/UX.</summary>
    public sealed class TrailerReadResult
    {
        public PlayerData Player;
        public int InstrumentsRead;
        public int TracksRead;
        public int EventsRead;
        public int AudioRemappedWavToOgg;
    }

    /// <summary>
    /// UI-free, bounds-checked reader for the DJMax Respect V / "Technika Q" trailer format.
    /// Every read is validated against the buffer length; any structural problem raises a typed
    /// <see cref="ChartLoadException"/> (with an offset) instead of throwing an OOB exception or
    /// silently producing garbage. It performs no IO, no network, and no decryption, and it never
    /// mutates the input buffer. The chart it returns is editable in memory; save-back to the
    /// original container remains blocked while BMS export is permitted.
    /// </summary>
    public static class TrailerChartReader
    {
        private const int TrailerSize = 26;
        private const int EventStride = 13;      // tick(4) + code(1) + payload(8)
        private const int InstrumentStride = 3 + 0x40;
        private const int MaxInstruments = 8192;
        private const int MaxTracks = 256;

        public static PlayerData Read(byte[] data, bool renameWavToOgg, out TrailerReadResult stats)
        {
            stats = new TrailerReadResult();
            if (data == null || data.Length < 12)
                throw new ChartLoadException(ChartLoadError.MalformedHeader, "Trailer header is missing or truncated.", 0);

            var r = new SafeReader(data);
            var playerData = new PlayerData();
            var instruments = playerData.Instruments;
            var tracks = playerData.Tracks;

            uint sign = r.U32();          // 0
            uint num = r.U32();           // trailer offset
            uint pos = r.U32();           // unused here

            if (num < 12 || (long)num + TrailerSize > data.Length)
                throw new ChartLoadException(ChartLoadError.InvalidTrailerOffset,
                    $"Trailer offset {num} is outside the file (length {data.Length}).", num);

            r.Seek(num);
            ushort insCnt = r.U16();
            int trackCnt = r.U16() & 0xFF;
            ushort tickPerMinute = r.U16();
            float tempo = r.F32();
            uint tick1 = r.U32();
            float playTime = r.F32();
            uint endTick = r.U32();
            r.U32();

            if (insCnt > MaxInstruments)
                throw new ChartLoadException(ChartLoadError.InvalidCount, $"Instrument count {insCnt} is implausible.", num);
            if (trackCnt > MaxTracks)
                throw new ChartLoadException(ChartLoadError.InvalidCount, $"Track count {trackCnt} is implausible.", num + 2);

            playerData.Encrypted = false; // trailer charts are plaintext
            playerData.Version = 1;
            playerData.TrackDuration = playTime;
            playerData.TickPerMinute = tickPerMinute;
            playerData.Tempo = tempo;
            playerData.HeaderEndTick = endTick;

            // Instrument table begins at offset 8.
            r.Seek(8);
            r.Require((long)insCnt * InstrumentStride, ChartLoadError.TruncatedInstrumentTable, "instrument table");

            instruments.Add(new InstrumentData { InsNum = 0, Name = "none" });
            for (int i = 0; i < insCnt; i++)
            {
                ushort insNo = r.U16();
                r.Skip(1);
                string oggName = r.Str(0x40);

                if (renameWavToOgg && oggName.IndexOf(".wav", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    oggName = oggName.Replace(".wav", ".ogg").Replace(".WAV", ".ogg");
                    stats.AudioRemappedWavToOgg++;
                }

                instruments.Add(new InstrumentData { InsNum = insNo, Name = oggName });
                stats.InstrumentsRead++;
            }

            for (byte trackIdx = 0; trackIdx < trackCnt; trackIdx++)
            {
                r.Skip(2);
                string trackName = r.Str(0x40);
                r.Skip(4);

                var track = new TrackData(trackIdx) { TrackName = trackName };
                tracks.AddTrack(track);
                stats.TracksRead++;

                uint eventsCount = r.U32();
                // Guard against runaway counts from malformed data before the per-read checks.
                if ((long)eventsCount * EventStride > r.Available)
                    throw new ChartLoadException(ChartLoadError.TruncatedTrack,
                        $"Track '{trackName}' declares {eventsCount} events but the file is too short.", r.Position);

                for (uint index = 0; index < eventsCount; index++)
                {
                    uint tick = r.U32();
                    byte eventCode = r.U8();

                    switch (eventCode)
                    {
                        case 1: // Note
                        {
                            ushort insNo = r.U16();
                            byte vel = r.U8();
                            byte pan = r.U8();
                            byte attribute = r.U8();
                            ushort duration = r.U16();
                            r.Skip(1);

                            InstrumentData inst = playerData.Instruments.SingleOrDefault(ins => ins.InsNum == insNo);
                            track.AddEvent(new EventData
                            {
                                TrackId = trackIdx,
                                Tick = (int)tick,
                                Attribute = attribute,
                                Duration = duration,
                                EventType = EventType.Note,
                                Instrument = inst,
                                Vel = vel,
                                Pan = pan
                            });
                            break;
                        }
                        case 2: // Volume
                        {
                            byte volume = r.U8();
                            r.Skip(7);
                            track.AddEvent(new EventData
                            {
                                TrackId = trackIdx,
                                Tick = (int)tick,
                                EventType = EventType.Volume,
                                Volume = volume
                            });
                            break;
                        }
                        case 3: // Tempo
                        {
                            float tempo2 = r.F32();
                            r.Skip(4);
                            track.AddEvent(new EventData
                            {
                                TrackId = trackIdx,
                                Tick = (int)tick,
                                EventType = EventType.Tempo,
                                Tempo = tempo2
                            });
                            break;
                        }
                        case 4: // skip
                            r.Skip(8);
                            break;
                        default:
                            throw new ChartLoadException(ChartLoadError.UnsupportedOpcode,
                                $"Unsupported event code {eventCode} in track '{trackName}'.", r.Position - 1);
                    }
                    stats.EventsRead++;
                }
            }

            // Respect/trailer charts are editable in the shared in-memory model. Save-back to the
            // original container remains blocked; users can export the edited chart as BMS.
            playerData.IsReadOnly = false;
            playerData.SourceFormat = ChartFormat.TrailerRespectV;
            stats.Player = playerData;
            return playerData;
        }

        /// <summary>Bounds-checked forward/seek reader over a byte buffer. Never reads past the end.</summary>
        private sealed class SafeReader
        {
            private readonly byte[] _d;
            private int _p;

            public SafeReader(byte[] d) { _d = d; _p = 0; }

            public long Position => _p;
            public long Available => _d.Length - _p;

            public void Seek(long pos)
            {
                if (pos < 0 || pos > _d.Length)
                    throw new ChartLoadException(ChartLoadError.MalformedHeader, $"Seek to {pos} is out of range.", pos);
                _p = (int)pos;
            }

            public void Skip(int n) => EnsureAndAdvance(n);

            public void Require(long n, ChartLoadError kind, string what)
            {
                if (n < 0 || n > Available)
                    throw new ChartLoadException(kind, $"File too short for {what} ({n} bytes needed, {Available} left).", _p);
            }

            public byte U8() { int at = _p; EnsureAndAdvance(1); return _d[at]; }

            public ushort U16()
            {
                int at = _p; EnsureAndAdvance(2);
                return (ushort)(_d[at] | (_d[at + 1] << 8));
            }

            public uint U32()
            {
                int at = _p; EnsureAndAdvance(4);
                return (uint)(_d[at] | (_d[at + 1] << 8) | (_d[at + 2] << 16) | (_d[at + 3] << 24));
            }

            public float F32()
            {
                int at = _p; EnsureAndAdvance(4);
                return BitConverter.ToSingle(_d, at);
            }

            public string Str(int len)
            {
                int at = _p; EnsureAndAdvance(len);
                int end = at;
                while (end < at + len && _d[end] != 0) end++;
                return System.Text.Encoding.ASCII.GetString(_d, at, end - at).Trim();
            }

            private void EnsureAndAdvance(int n)
            {
                if (n < 0 || _p + n > _d.Length)
                    throw new ChartLoadException(ChartLoadError.TruncatedTrack,
                        $"Attempted to read {n} bytes past end of file.", _p);
                _p += n;
            }
        }
    }
}
