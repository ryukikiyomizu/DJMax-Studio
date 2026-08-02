using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DJMaxEditor.DJMax;
using DJMaxEditor.Files;
using DJMaxEditor.Files.bms;
using DJMaxEditor.Files.bytes;
using DJMaxEditor.Files.FormatDetection;

namespace DJMaxEditor.Tests
{
    internal static partial class Program
    {
        private const string BmsFixture =
            "#PLAYER 1\r\n" +
            "#TITLE Round Trip Test\r\n" +
            "#ARTIST Test Artist\r\n" +
            "#GENRE Test Genre\r\n" +
            "#BPM 120\r\n" +
            "#WAV01 kick.wav\r\n" +
            "#WAV02 backing.ogg\r\n" +
            "#BPM01 180\r\n" +
            "#00011:0100\r\n" +
            "#00001:0002\r\n" +
            "#00108:0100\r\n" +
            "#00151:0101\r\n";

        private static void RunBmsFormatTests(string fixtures, string bmsFixtures)
        {
            Test("Bms_DetectsClassicTextFromContent", () =>
            {
                var detected = ChartFormatDetector.Detect(Encoding.UTF8.GetBytes(BmsFixture), ".pt");
                AssertFormat(detected, ChartFormat.BmsClassic);
                AssertTrue(detected.IsOpenable, "classic BMS should be openable");
                AssertTrue(!detected.IsReadOnly, "classic BMS should be editable");
            });

            Test("Bms_ImportsKeysoundsTempoBgmAndLongNotes", () =>
            {
                var chart = BmsChartSerializer.Parse(BmsFixture);

                AssertTrue(chart.SourceFormat == ChartFormat.BmsClassic, "source format was not retained");
                AssertTrue(chart.TickPerMinute == 192, "BMS must use the editor's 48 PPQ / 192 ticks-per-measure clock");
                AssertTrue(Math.Abs(chart.Tempo - 120f) < 0.001f, "initial BPM was not imported");
                AssertTrue(chart.BmsMetadata != null && chart.BmsMetadata.Title == "Round Trip Test",
                    "BMS title metadata was not retained");
                AssertTrue(chart.Instruments.Any(i => i.InsNum == 1 && i.Name == "kick.wav"),
                    "#WAV01 was not imported");
                AssertTrue(chart.Tracks.Events != null && chart.Tracks.Events.Length > 0,
                    "import did not publish the flattened playback event list");

                var notes = chart.Tracks.SelectMany(t => t.Events)
                    .Where(e => e.EventType == EventType.Note).ToArray();
                AssertTrue(notes.Any(e => e.Tick == 0 && e.Instrument != null && e.Instrument.InsNum == 1),
                    "playable #00011 note was not imported");
                AssertTrue(notes.Any(e => e.Tick == 96 && e.Instrument != null && e.Instrument.InsNum == 2),
                    "half-measure BGM keysound was not imported");
                AssertTrue(notes.Any(e => e.Tick == 192 && e.Duration == 96),
                    "channel 51 long note did not become a 96-tick hold");

                var tempo = chart.Tracks.SelectMany(t => t.Events)
                    .Single(e => e.EventType == EventType.Tempo);
                AssertTrue(tempo.Tick == 192 && Math.Abs(tempo.Tempo - 180f) < 0.001f,
                    "extended BPM event was not imported");
            });

            Test("Bms_PlayerStartsAtHeaderBpmWithoutTempoEvents", () =>
            {
                const string source =
                    "#BPM 185\r\n" +
                    "#WAV01 tick.wav\r\n" +
                    "#00001:01\r\n" +
                    "#00101:01\r\n";
                var player = new Player();
                player.LoadPlayerData(BmsChartSerializer.Parse(source));

                var elapsed = Stopwatch.StartNew();
                player.Play(0);
                Thread.Sleep(120);
                player.Update();
                elapsed.Stop();

                int expectedTick = (int)(elapsed.Elapsed.TotalMilliseconds /
                    (60000.0 / (185.0 * 48.0)));
                int actualTick = player.GetCurrentTick();
                AssertTrue(Math.Abs(actualTick - expectedTick) <= 3,
                    "header-only #BPM 185 advanced to tick " + actualTick +
                    "; expected about " + expectedTick);
            });

            Test("Bms_ImportExportImportPreservesChartMeaning", () =>
            {
                var first = BmsChartSerializer.Parse(BmsFixture);
                var exported = BmsChartSerializer.Serialize(first);
                var second = BmsChartSerializer.Parse(exported);

                AssertTrue(exported.Contains("#TITLE Round Trip Test"), "export dropped title metadata");
                AssertTrue(exported.Contains("#WAV01 kick.wav"), "export dropped keysound definition");
                AssertTrue(exported.Contains("#00151:"), "export did not use a long-note channel");
                AssertTrue(BmsMeaning(first) == BmsMeaning(second),
                    "BMS import/export changed lanes, timing, duration, tempo, or keysounds");
            });

            Test("Bms_RejectsOddLengthObjectDataWithTypedError", () =>
            {
                AssertThrows(ChartLoadError.MalformedHeader,
                    () => BmsChartSerializer.Parse("#BPM 120\r\n#00011:001\r\n"));
            });

            Test("Bms_RoundTripPreservesUnsupportedBgaInvisibleAndExtraHeaders", () =>
            {
                const string source =
                    "#TITLE Preserve Me\r\n#BPM 120\r\n#SUBTITLE [HYPER]\r\n" +
                    "#BMP01 background.png\r\n#WAV01 key.wav\r\n" +
                    "#00004:01\r\n#00031:01\r\n#00011:01\r\n";
                string exported = BmsChartSerializer.Serialize(BmsChartSerializer.Parse(source));
                AssertTrue(exported.Contains("#SUBTITLE [HYPER]"), "extra header was dropped");
                AssertTrue(exported.Contains("#BMP01 background.png"), "BGA definition was dropped");
                AssertTrue(exported.Contains("#00004:01"), "BGA event channel was dropped");
                AssertTrue(exported.Contains("#00031:01"), "invisible-note channel was dropped");
            });

            Test("RespectV_EditableChartExportsAsBms", () =>
            {
                byte[] source = BuildTrailer(new byte[] { 1, 2, 3, 4 });
                TrailerReadResult stats;
                PlayerData respect = TrailerChartReader.Read(source, false, out stats);
                EventData editedNote = respect.Tracks.SelectMany(x => x.Events)
                    .First(x => x.EventType == EventType.Note);
                editedNote.Instrument = respect.Instruments.First(x => x.InsNum > 0);
                string output = Path.Combine(Path.GetTempPath(),
                    "djmax-editor-respect-export-" + Guid.NewGuid().ToString("N") + ".bms");
                try
                {
                    AssertTrue(!respect.IsReadOnly, "Respect source was not editable");
                    AssertTrue(new BMESaveFile().Save(output, respect), "Respect-to-BMS export failed");
                    byte[] exported = File.ReadAllBytes(output);
                    AssertFormat(ChartFormatDetector.Detect(exported, ".bms"), ChartFormat.BmsClassic);
                    PlayerData roundTrip = BmsChartSerializer.Parse(BmsChartSerializer.Decode(exported));
                    AssertTrue(roundTrip.Tracks.SelectMany(x => x.Events)
                        .Any(x => x.EventType == EventType.Note),
                        "Respect-to-BMS export lost every note");
                }
                finally
                {
                    if (File.Exists(output)) File.Delete(output);
                }
            });

            Test("RespectV_ExportMapsOnlyDJMaxGameplayTracksToContiguousBmsLanes", () =>
            {
                var respect = new PlayerData
                {
                    SourceFormat = ChartFormat.TrailerRespectV,
                    Tempo = 120,
                    TickPerMinute = 192
                };
                var instrument = new InstrumentData { InsNum = 1, Name = "note.wav" };
                respect.Instruments.Add(instrument);

                for (uint trackIndex = 0; trackIndex <= 22; trackIndex++)
                {
                    var track = new TrackData(trackIndex);
                    respect.Tracks.AddTrack(track);

                    // Respect's sparse setup/autoplay tracks must remain BGM. Its 4B gameplay lanes
                    // are source tracks 3..6 and must become contiguous BMS lanes 11..14.
                    if (trackIndex == 1 || trackIndex == 2 || trackIndex == 22 ||
                        (trackIndex >= 3 && trackIndex <= 6))
                    {
                        track.AddEvent(new EventData
                        {
                            EventType = EventType.Note,
                            Instrument = instrument,
                            Tick = (int)trackIndex * 48,
                            Duration = trackIndex == 2 ? (ushort)48 : (ushort)6
                        });
                    }
                }

                string exported = BmsChartSerializer.Serialize(respect);
                PlayerData roundTrip = BmsChartSerializer.Parse(exported);
                string lanes = string.Join(",", roundTrip.BmsMetadata.TrackChannels.Values
                    .Where(x => x.StartsWith("1", StringComparison.Ordinal))
                    .OrderBy(x => x));

                AssertTrue(lanes == "11,12,13,14",
                    "Respect 4B export produced non-contiguous Pulsus lanes: " + lanes);
                AssertTrue(roundTrip.Tracks.SelectMany(x => x.Events)
                    .Count(x => x.EventType == EventType.Note) == 7,
                    "Respect export dropped setup/autoplay notes while separating BGM from gameplay lanes");
            });

            RealFileTest("RespectV_PharmOfCharm4B_ExportsPulsusSafeLanes", fixtures,
                "DMRV_PLI03_LITE/pharmofcharm/pharmofcharm_4b_sc.bytes",
                data => AssertRespectPulsusLanes(data, "11,12,13,14"));
            RealFileTest("RespectV_PharmOfCharm5B_ExportsPulsusSafeLanes", fixtures,
                "DMRV_PLI03_LITE/pharmofcharm/pharmofcharm_5b_mx.pt",
                data => AssertRespectPulsusLanes(data, "11,12,13,14,15"));
            RealFileTest("RespectV_PharmOfCharm6B_ExportsPulsusSafeLanes", fixtures,
                "DMRV_PLI03_LITE/pharmofcharm/pharmofcharm_6b_mx.pt",
                data => AssertRespectPulsusLanes(data, "11,12,13,14,15,16"));
            RealFileTest("RespectV_PharmOfCharm8B_ExportsPulsusSafeLanes", fixtures,
                "DMRV_PLI03_LITE/pharmofcharm/pharmofcharm_8b_mx.pt",
                data => AssertRespectPulsusLanes(data, "11,12,13,14,15,16,18,19"));

            RealFileTest("RespectV_BlueArchiveKeysoundOverflowExportsAsBmson", fixtures,
                "DMRV_BLUE_ARCHIVE_FULL/bahifumidaisuki/SND/bahifumidaisuki_4b_mx.pt",
                data =>
                {
                    TrailerReadResult stats;
                    PlayerData respect = TrailerChartReader.Read(data, false, out stats);
                    int sourceNotes = respect.Tracks.SelectMany(x => x.Events)
                        .Count(x => x.EventType == EventType.Note && x.Instrument != null &&
                            x.Instrument.InsNum > 0);

                    AssertTrue(BmsChartSerializer.CountRequiredKeysounds(respect) > 1295,
                        "real BLUE ARCHIVE fixture no longer exercises the classic BMS limit");
                    AssertTrue(BmsonChartSerializer.ShouldUseForClassicBmsOverflow(respect),
                        "overflow chart was not routed to the lossless BMSON path");

                    string exported = BmsonChartSerializer.Serialize(respect);
                    AssertTrue(exported.Contains("\"version\":\"1.0.0\""),
                        "BMSON version header was not emitted");
                    AssertTrue(exported.Contains("\"mode_hint\":\"beat-5k\""),
                        "4B Respect chart did not receive a Pulsus-supported mode hint");
                    int exportedKeysounds = CountOccurrences(exported, "\"name\":");
                    AssertTrue(exportedKeysounds == 1474,
                        "BMSON retained " + exportedKeysounds + " of 1474 unique keysound files");
                    AssertTrue(CountOccurrences(exported, "\"x\":") == sourceNotes,
                        "BMSON did not retain every source note");

                    string pulsus = Path.Combine(Directory.GetParent(fixtures).FullName,
                        "ubmsc.pulsus", "ubmsc+pulsus", "Pulsus.exe");
                    if (File.Exists(pulsus))
                    {
                        string chartPath = Path.Combine(fixtures,
                            "DMRV_BLUE_ARCHIVE_FULL", "bahifumidaisuki", "SND",
                            "bahifumidaisuki_4b_mx.pt");
                        string output = Path.Combine(Path.GetDirectoryName(chartPath),
                            ".djmax-editor-pulsus-" + Guid.NewGuid().ToString("N") + ".bmson");
                        try
                        {
                            AssertTrue(new BmsonSaveFile().Save(output, respect),
                                "BMSON handler did not write the real overflow chart");
                            var pulsusAssembly = System.Reflection.Assembly.LoadFrom(pulsus);
                            Type parserType = pulsusAssembly.GetType("Pulsus.Gameplay.BMSONParser", true);
                            object parser = Activator.CreateInstance(parserType);
                            object parsed = parserType.GetMethod("Load").Invoke(parser, new object[] { output });
                            AssertTrue(parsed != null, "Pulsus rejected the generated BMSON");
                            parsed.GetType().GetMethod("GenerateEvents").Invoke(parsed, new object[0]);
                            int pulsusNotes = (int)parsed.GetType().GetProperty("noteCount").GetValue(parsed);
                            AssertTrue(pulsusNotes > 0, "Pulsus generated no playable notes from BMSON");
                        }
                        finally
                        {
                            if (File.Exists(output)) File.Delete(output);
                        }
                    }
                });

            if (string.IsNullOrEmpty(bmsFixtures) || !Directory.Exists(bmsFixtures))
            {
                _skip++;
                Console.WriteLine("[SKIP] Bms_RonriLonely_RealChartsRoundTrip (no --bms-fixtures)");
            }
            else
            {
                Test("Bms_RonriLonely_RealChartsRoundTrip", () =>
                {
                    int files = 0;
                    foreach (string path in Directory.GetFiles(bmsFixtures, "*.bms"))
                    {
                        files++;
                        byte[] bytes = File.ReadAllBytes(path);
                        AssertFormat(ChartFormatDetector.Detect(bytes, Path.GetExtension(path)),
                            ChartFormat.BmsClassic);
                        var imported = BmsChartSerializer.Parse(BmsChartSerializer.Decode(bytes));
                        int notes = imported.Tracks.SelectMany(x => x.Events)
                            .Count(x => x.EventType == EventType.Note);
                        AssertTrue(notes > 1000, Path.GetFileName(path) + " imported too few notes");
                        AssertTrue(imported.Instruments.Count > 100,
                            Path.GetFileName(path) + " imported too few #WAV definitions");
                        AssertTrue(!imported.BmsMetadata.Title.Contains("\uFFFD"),
                            Path.GetFileName(path) + " was decoded with replacement characters");

                        var roundTrip = BmsChartSerializer.Parse(BmsChartSerializer.Serialize(imported));
                        int afterNotes = roundTrip.Tracks.SelectMany(x => x.Events)
                            .Count(x => x.EventType == EventType.Note);
                        AssertTrue(afterNotes == notes,
                            Path.GetFileName(path) + " changed note count on export/re-import");
                        if (Path.GetFileName(path).Contains("14"))
                            AssertTrue(roundTrip.BmsMetadata.TrackChannels.Values.Contains("21"),
                                Path.GetFileName(path) + " lost player-two lanes");
                    }
                    AssertTrue(files == 11, "expected all 11 RonriLonely charts, got " + files);
                });
            }

            RunBmsAudioTests();
        }

        private static void AssertRespectPulsusLanes(byte[] source, string expected)
        {
            TrailerReadResult stats;
            PlayerData respect = TrailerChartReader.Read(source, false, out stats);
            string exported = BmsChartSerializer.Serialize(respect);
            PlayerData roundTrip = BmsChartSerializer.Parse(exported);
            string actual = string.Join(",", roundTrip.BmsMetadata.TrackChannels.Values
                .Where(x => x.StartsWith("1", StringComparison.Ordinal))
                .OrderBy(x => x));
            AssertTrue(actual == expected,
                "expected Pulsus-safe lanes " + expected + ", got " + actual);
        }

        private static string BmsMeaning(PlayerData chart)
        {
            var trackChannels = chart.BmsMetadata.TrackChannels;
            return string.Join("|", chart.Tracks
                .SelectMany(track => track.Events.Select(ev =>
                {
                    string channel;
                    if (!trackChannels.TryGetValue(track.Idx, out channel)) channel = "??";
                    var instrument = ev.Instrument == null ? 0 : ev.Instrument.InsNum;
                    return string.Join(",",
                        channel,
                        ((int)ev.EventType).ToString(),
                        ev.VirtualTick.ToString(),
                        ev.VirtualDuration.ToString(),
                        instrument.ToString(),
                        ev.Tempo.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                }))
                .OrderBy(x => x));
        }

        private static int CountOccurrences(string source, string value)
        {
            int count = 0;
            int offset = 0;
            while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
            {
                count++;
                offset += value.Length;
            }
            return count;
        }
    }
}
