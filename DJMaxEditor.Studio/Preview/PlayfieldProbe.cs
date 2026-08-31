using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DJMaxEditor.Controls.TimelineV2;
using DJMaxEditor.Controls.Vertical;
using DJMaxEditor.DJMax;
using DJMaxEditor.Preview;
using DJMaxEditor.Studio.Documents;

namespace DJMaxEditor.Studio.Preview
{
    /// <summary>
    /// Renders the TECHNIKA playfield to PNG without showing a window.
    ///
    /// <para>
    /// Verifying a renderer by screen-scraping the running shell needs the window in the
    /// foreground, which Windows will not grant to a background script while someone is using the
    /// machine - and the synthetic keystrokes that drive it land in whatever app actually has
    /// focus. This runs the real projector, the real metrics and the real
    /// <see cref="TechnikaPlayfieldView"/> against a <see cref="RenderTargetBitmap"/> instead, so
    /// the frames are exactly what the shell would paint and nothing depends on what else is on
    /// screen.
    /// </para>
    ///
    /// <para>
    /// It also writes a report of which ticks actually have notes in the renderer's window, because
    /// "the playfield looks empty" and "this chart has nothing in the first two scans" are
    /// different bugs and a screenshot cannot tell them apart.
    /// </para>
    /// </summary>
    internal static class PlayfieldProbe
    {
        /// <summary>Command-line switch that selects this mode.</summary>
        public const string Switch = "--playfield-shot";

        private const int DefaultWidth = 960;
        private const int FrameCount = 6;

        /// <summary>
        /// Runs the probe. Returns the process exit code: 0 when frames were written, non-zero
        /// when the chart could not be opened or is not a TECHNIKA chart.
        /// </summary>
        public static int Run(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine(
                    Switch + " <chart> <output-directory> [width]");
                return 2;
            }

            string chartPath = args[1];
            string outputDirectory = args[2];
            int width = DefaultWidth;
            if (args.Length > 3)
            {
                int parsed;
                if (int.TryParse(args[3], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out parsed) && parsed >= 160)
                {
                    width = parsed;
                }
            }

            var report = new StringBuilder();
            report.Append("chart: ").AppendLine(chartPath);
            report.Append("width: ").Append(width).AppendLine();

            PlayerData model = Open(chartPath, report);
            if (model == null)
            {
                Write(outputDirectory, report);
                return 3;
            }

            DescribeKeysounds(model, chartPath, report);
            DescribeTrackOccupancy(model, report);
            DescribeVerticalTimeline(model, report);

            GameplayPreviewProfileSuggestion suggestion =
                GameplayPreviewProfileResolver.Suggest(model);
            GameplayPreviewProjection projection =
                GameplayPreviewProjector.Project(model, suggestion.Profile);

            report.Append("profile: ").Append(suggestion.Profile)
                .Append("  confirm=").Append(suggestion.RequiresConfirmation).AppendLine();
            report.Append("status: ").AppendLine(projection.StatusLabel);
            report.Append("lanes: ").Append(projection.LaneCount)
                .Append("  notes: ").Append(projection.Notes.Count).AppendLine();
            DescribeDiagnostics(projection, report);

            if (projection.Profile != GameplayPreviewProfile.Technika)
            {
                report.AppendLine("not a TECHNIKA projection - nothing to draw");
                Write(outputDirectory, report);
                return 4;
            }

            DescribeTopology(projection, report);
            int[] ticks = ChooseTicks(projection, report);
            RenderFrames(projection, ticks, width, outputDirectory, report);
            Write(outputDirectory, report);
            return 0;
        }

        /// <summary>
        /// The projection's own warnings, which are otherwise only ever a count in the overlay.
        /// The DUO notice is the reason this is here: on a <c>*_duo_1.pt</c> chart the second
        /// player's lanes sit on source tracks 8-10 and cannot be drawn on a one-player field, and
        /// a headless run is the only place that claim can be checked against a real file.
        /// </summary>
        private static void DescribeDiagnostics(
            GameplayPreviewProjection projection, StringBuilder report)
        {
            IReadOnlyList<string> warnings = projection.Diagnostics;
            report.Append("warnings: ").Append(warnings.Count).AppendLine();
            for (int i = 0; i < warnings.Count && i < 8; i++)
            {
                report.Append("  ").AppendLine(warnings[i]);
            }
            if (warnings.Count > 8)
            {
                report.Append("  ... and ").Append(warnings.Count - 8).AppendLine(" more");
            }
        }

        /// <summary>
        /// Opens the chart through the shell's own service, including the encrypted-Technika
        /// decrypt step. Consent is implicit: the caller named the file on the command line.
        /// </summary>
        internal static PlayerData Open(string chartPath, StringBuilder report)
        {
            var service = new ChartFileService();
            string error;
            ChartProbe probe = service.Probe(chartPath, out error);
            if (probe == null)
            {
                report.Append("probe failed: ").AppendLine(error);
                return null;
            }

            report.Append("detected: ").Append(probe.Detection).AppendLine();
            if (probe.IsEncryptedPt)
            {
                ChartProbe decrypted = service.Decrypt(probe, out error);
                if (decrypted == null)
                {
                    report.Append("decrypt failed: ").AppendLine(error);
                    return null;
                }
                ChartOpenResult fromCipher =
                    service.OpenAsync(decrypted, true).GetAwaiter().GetResult();
                if (!fromCipher.Success)
                {
                    report.Append("parse failed: ").AppendLine(fromCipher.Error);
                    return null;
                }
                return fromCipher.Model;
            }

            ChartOpenResult result = service.OpenAsync(probe, false).GetAwaiter().GetResult();
            if (!result.Success)
            {
                report.Append("parse failed: ").AppendLine(result.Error);
                return null;
            }
            return result.Model;
        }

        /// <summary>
        /// The audio side of the chart, read straight off the model rather than off the projection.
        ///
        /// <para>
        /// The projection deliberately drops everything that is not a playable lane note, so it
        /// cannot answer the question the keysound scheduler needs answered: which notes carry a
        /// <em>body</em> (<c>Duration &gt; 6</c>, the same threshold the projector classifies
        /// Drag/Hold with), and what sample each one plays. That matters because silencing a bodied
        /// note at its end is right for a held pad and catastrophic for the MR - the hundred-second
        /// background track, which is instrument 0 and is fired by an ordinary note event like any
        /// other. So the MR's own notes are listed with their durations, and the histogram says how
        /// many notes in the chart are bodied at all.
        /// </para>
        /// </summary>
        private static void DescribeKeysounds(
            PlayerData model, string chartPath, StringBuilder report)
        {
            report.AppendFormat(CultureInfo.InvariantCulture,
                "model: tracks={0} instruments={1} tempo={2} ticksPerMeasure={3} duration={4:F2}s",
                model.Tracks == null ? 0 : model.Tracks.Count,
                model.Instruments == null ? 0 : model.Instruments.Count,
                model.Tempo, model.TickPerMinute, model.TrackDuration).AppendLine();

            EventData[] events = model.Tracks == null ? null : model.Tracks.Events;
            if (events == null)
            {
                return;
            }

            int[] bounds = { 6, 24, 96, 384, 1536, int.MaxValue };
            int[] counts = new int[bounds.Length];
            var bodied = new List<EventData>();
            var notesPerSound = new Dictionary<ushort, int>();
            var longestPerSound = new Dictionary<ushort, int>();
            int silent = 0;
            for (int i = 0; i < events.Length; i++)
            {
                EventData ev = events[i];
                if (ev == null || ev.EventType != EventType.Note)
                {
                    continue;
                }

                for (int b = 0; b < bounds.Length; b++)
                {
                    if (ev.Duration <= bounds[b])
                    {
                        counts[b]++;
                        break;
                    }
                }

                if (ev.Duration > 6)
                {
                    bodied.Add(ev);
                }

                ushort sound = ev.Instrument == null ? (ushort)0 : ev.Instrument.InsNum;
                if (sound == 0)
                {
                    // The shell skips InsNum 0 when it loads keysounds, so these notes are silent.
                    silent++;
                    continue;
                }

                int seen;
                notesPerSound[sound] = notesPerSound.TryGetValue(sound, out seen) ? seen + 1 : 1;
                int longest;
                longestPerSound[sound] =
                    longestPerSound.TryGetValue(sound, out longest) && longest >= ev.Duration
                        ? longest : ev.Duration;
            }

            report.Append("  note durations (native ticks):");
            for (int b = 0; b < bounds.Length; b++)
            {
                report.Append(' ')
                    .Append(b == 0 ? "<=6" : (bounds[b - 1] + 1) + "-" +
                        (bounds[b] == int.MaxValue ? "inf" : bounds[b].ToString(CultureInfo.InvariantCulture)))
                    .Append('=').Append(counts[b]);
            }
            report.AppendLine();

            report.Append("  notes with no keysound (InsNum 0): ").Append(silent).AppendLine();
            report.Append("  bodied notes (Duration > 6): ").Append(bodied.Count).AppendLine();
            bodied.Sort((a, b) => b.Duration.CompareTo(a.Duration));
            for (int i = 0; i < bodied.Count && i < 8; i++)
            {
                EventData ev = bodied[i];
                report.AppendFormat(CultureInfo.InvariantCulture,
                    "    longest: tick={0,-7} track={1,-3} attr={2,-3} dur={3,-5} ins=\"{4}\"",
                    ev.Tick, ev.TrackId, ev.Attribute, ev.Duration,
                    ev.Instrument == null ? "(none)" : ev.Instrument.Name).AppendLine();
            }

            DescribeLargestSamples(model, chartPath, notesPerSound, longestPerSound, report);
        }

        /// <summary>
        /// Which of the chart's tracks actually carry events, and how far each one reaches.
        ///
        /// <para>
        /// A .pt always declares its full complement of tracks whether or not a chart uses them, so
        /// "the other tracks do not show up" is two different bugs wearing one sentence: either the
        /// tracks are empty in the file, or they hold events the editor is not drawing. This section
        /// separates them - it reads the model, not any view - and the tail is what a viewport bug
        /// looks like: occupied tracks clustered above whatever index the editor stops at.
        /// </para>
        /// </summary>
        private static void DescribeTrackOccupancy(PlayerData model, StringBuilder report)
        {
            if (model.Tracks == null)
            {
                return;
            }

            int occupied = 0;
            int highest = -1;
            var line = new StringBuilder();
            foreach (TrackData track in model.Tracks)
            {
                IReadOnlyList<EventData> events = track.OrderedEvents;
                int notes = 0;
                for (int i = 0; i < events.Count; i++)
                {
                    if (events[i] != null && events[i].EventType == EventType.Note)
                    {
                        notes++;
                    }
                }

                if (events.Count == 0)
                {
                    continue;
                }

                occupied++;
                highest = (int)track.Idx;
                line.AppendFormat(CultureInfo.InvariantCulture, " {0}:{1}", track.Idx,
                    notes == events.Count ? notes.ToString(CultureInfo.InvariantCulture)
                        : notes + "n+" + (events.Count - notes) + "e");
            }

            report.AppendFormat(CultureInfo.InvariantCulture,
                "  tracks with events: {0} of {1}, highest used index {2}",
                occupied, model.Tracks.Count, highest).AppendLine();
            report.Append("  per track (idx:notes[+other events]):").Append(line).AppendLine();
        }

        /// <summary>
        /// What the vertical (ptSequencer-style) timeline makes of the chart.
        ///
        /// <para>
        /// The occupancy section above says what the file holds; this says what that surface draws
        /// from it. The two together are the whole of the "other tracks do not show up" report: the
        /// preset column map is the DJMAX Portable one, so a TECHNIKA chart authoring on tracks it
        /// never names had those events counted into <c>SkippedEventCount</c> and drawn nowhere.
        /// <c>drawn + skipped</c> must equal the model's own event total, and <c>skipped</c> must be
        /// 0 - anything else is that bug, in numbers, on a real file.
        /// </para>
        /// </summary>
        private static void DescribeVerticalTimeline(PlayerData model, StringBuilder report)
        {
            if (model.Tracks == null)
            {
                return;
            }

            int total = 0;
            foreach (TrackData track in model.Tracks)
            {
                total += track.OrderedEvents.Count;
            }

            int mode = VerticalTimelineProjection.DetectMode(model);
            if (mode == 0)
            {
                report.AppendFormat(CultureInfo.InvariantCulture,
                    "  vertical timeline: no gameplay notes, so no layout ({0} events in the model)",
                    total).AppendLine();
                return;
            }

            VerticalTimelineProjection projection = VerticalTimelineProjection.Project(model, mode);
            report.AppendFormat(CultureInfo.InvariantCulture,
                "  vertical timeline: detected {0}, {1} columns ({2} appended for unmapped tracks)",
                projection.Layout.DisplayName, projection.Layout.Columns.Count,
                projection.Layout.OverflowColumnCount).AppendLine();
            report.AppendFormat(CultureInfo.InvariantCulture,
                "  events drawn {0} of {1}, skipped {2}",
                projection.Items.Count, total, projection.SkippedEventCount).AppendLine();

            var appended = new StringBuilder();
            foreach (VerticalColumn column in projection.Layout.Columns)
            {
                if (column.Kind != VerticalColumnKind.Overflow)
                {
                    continue;
                }
                appended.AppendFormat(CultureInfo.InvariantCulture, " {0}:{1}",
                    column.ShortName, CountOnColumn(projection, column));
            }
            if (appended.Length > 0)
            {
                report.Append("  appended columns (label:events):").Append(appended).AppendLine();
            }

            // On TECHNIKA the four lanes are the whole point of the report: laid out as a button
            // preset they were mislabelled rather than dropped, so a count of 2901 of 2901 looked
            // healthy while lane 0 was drawn inside an 18px spacer. Print them by source track.
            if (VerticalTrackLayout.IsTechnikaMode(projection.Mode))
            {
                var lanes = new StringBuilder();
                foreach (VerticalColumn column in projection.Layout.Columns)
                {
                    if (column.Kind != VerticalColumnKind.Button &&
                        column.Kind != VerticalColumnKind.ScanMarker)
                    {
                        continue;
                    }
                    lanes.AppendFormat(CultureInfo.InvariantCulture, " {0}(T{1}):{2}",
                        column.ShortName, column.SourceTrackId, CountOnColumn(projection, column));
                }
                report.Append("  technika lanes (label(track):events):").Append(lanes).AppendLine();
            }
        }

        private static int CountOnColumn(VerticalTimelineProjection projection, VerticalColumn column)
        {
            int drawn = 0;
            foreach (TimelineItem item in projection.Items)
            {
                if (item.RowIndex == column.Index)
                {
                    drawn++;
                }
            }
            return drawn;
        }

        /// <summary>
        /// The chart's biggest keysound files against the longest note that fires each one.
        ///
        /// <para>
        /// File size stands in for sample length: the samples are all Vorbis at one quality, so the
        /// biggest file is the longest sound, which in a TECHNIKA chart is the hundred-second MR.
        /// The only column that matters is <c>longestDur</c> - if the MR is fired by a note with no
        /// body then cutting bodied notes at their release cannot touch it, and if it is ever marked
        /// BODIED then that cut would kill the backing track and needs a carve-out.
        /// </para>
        /// </summary>
        private static void DescribeLargestSamples(
            PlayerData model, string chartPath,
            Dictionary<ushort, int> notesPerSound, Dictionary<ushort, int> longestPerSound,
            StringBuilder report)
        {
            string directory = Path.GetDirectoryName(chartPath);
            if (model.Instruments == null || string.IsNullOrEmpty(directory))
            {
                return;
            }

            var sized = new List<KeyValuePair<long, InstrumentData>>();
            foreach (InstrumentData instrument in model.Instruments)
            {
                if (instrument == null || instrument.InsNum == 0 ||
                    string.IsNullOrEmpty(instrument.Name))
                {
                    continue;
                }

                var file = new FileInfo(Path.Combine(directory, instrument.Name));
                if (file.Exists)
                {
                    sized.Add(new KeyValuePair<long, InstrumentData>(file.Length, instrument));
                }
            }
            sized.Sort((a, b) => b.Key.CompareTo(a.Key));
            report.Append("  largest keysound files (of ").Append(sized.Count)
                .AppendLine(" present):");
            for (int i = 0; i < sized.Count && i < 6; i++)
            {
                InstrumentData instrument = sized[i].Value;
                int notes, longest;
                notesPerSound.TryGetValue(instrument.InsNum, out notes);
                longestPerSound.TryGetValue(instrument.InsNum, out longest);
                report.AppendFormat(CultureInfo.InvariantCulture,
                    "    {0,7} KB  notes={1,-4} longestDur={2,-5} {3}{4}",
                    sized[i].Key / 1024, notes, longest, instrument.Name,
                    longest > 6 ? "   <-- BODIED" : string.Empty).AppendLine();
            }
        }

        /// <summary>Scan, lane and kind coverage of the whole projection.</summary>
        private static void DescribeTopology(
            GameplayPreviewProjection projection, StringBuilder report)
        {
            IReadOnlyList<ProjectedGameplayNote> notes = projection.Notes;
            if (notes.Count == 0)
            {
                report.AppendLine("topology: empty");
                return;
            }

            int minScan = int.MaxValue, maxScan = int.MinValue;
            int minTick = int.MaxValue, maxTick = int.MinValue;
            int holds = 0;
            var lanes = new SortedDictionary<int, int>();
            var kinds = new SortedDictionary<string, int>();
            for (int i = 0; i < notes.Count; i++)
            {
                ProjectedGameplayNote note = notes[i];
                if (note.ScanIndex < minScan) { minScan = note.ScanIndex; }
                if (note.ScanIndex > maxScan) { maxScan = note.ScanIndex; }
                if (note.Source.Tick < minTick) { minTick = note.Source.Tick; }
                if (note.Source.Tick > maxTick) { maxTick = note.Source.Tick; }
                if (note.DurationPulse > 0) { holds++; }

                int seen;
                lanes.TryGetValue(note.Lane, out seen);
                lanes[note.Lane] = seen + 1;
                string kind = note.Kind.ToString();
                kinds.TryGetValue(kind, out seen);
                kinds[kind] = seen + 1;
            }

            report.Append("scans: ").Append(minScan).Append("..").Append(maxScan)
                .Append("   ticks: ").Append(minTick).Append("..").Append(maxTick)
                .Append("   holds: ").Append(holds).AppendLine();
            report.Append("lanes:");
            foreach (KeyValuePair<int, int> pair in lanes)
            {
                report.Append(' ').Append(pair.Key).Append('=').Append(pair.Value);
            }
            report.AppendLine();
            report.Append("kinds:");
            foreach (KeyValuePair<string, int> pair in kinds)
            {
                report.Append(' ').Append(pair.Key).Append('=').Append(pair.Value);
            }
            report.AppendLine();

            report.AppendLine("first 12 notes (tick, pulse, lane, scan, x, y, top, kind):");
            for (int i = 0; i < notes.Count && i < 12; i++)
            {
                ProjectedGameplayNote note = notes[i];
                report.AppendFormat(CultureInfo.InvariantCulture,
                    "  t={0,-6} p={1,-6} lane={2} scan={3,-3} x={4:F4} y={5:F4} top={6,-5} {7}",
                    note.Source.Tick, note.Pulse, note.Lane, note.ScanIndex,
                    note.X, note.Y, note.IsTopHalf, note.Kind).AppendLine();
            }
        }

        /// <summary>
        /// Picks the ticks worth rendering: the fullest renderer windows in the chart, spread out
        /// so the frames are not six views of the same bar. Sampling every scan boundary is enough
        /// because the window is defined in whole scans.
        /// </summary>
        private static int[] ChooseTicks(
            GameplayPreviewProjection projection, StringBuilder report)
        {
            // Recover ticks-per-scan from the projection itself rather than assuming a value: a
            // note's tick and its scan index are both known, so their ratio is the calibration.
            int ticksPerScan = 0;
            IReadOnlyList<ProjectedGameplayNote> notes = projection.Notes;
            for (int i = 0; i < notes.Count; i++)
            {
                if (notes[i].ScanIndex > 0 && notes[i].Source.Tick > 0)
                {
                    ticksPerScan = notes[i].Source.Tick / notes[i].ScanIndex;
                    break;
                }
            }
            if (ticksPerScan <= 0)
            {
                ticksPerScan = 192;
            }
            report.Append("ticksPerScan (derived): ").Append(ticksPerScan).AppendLine();

            int maxScan = 0;
            for (int i = 0; i < notes.Count; i++)
            {
                if (notes[i].ScanIndex > maxScan) { maxScan = notes[i].ScanIndex; }
            }

            var occupancy = new List<KeyValuePair<int, int>>();
            var kindsAtTick = new Dictionary<int, HashSet<GameplayPreviewNoteKind>>();
            report.AppendLine("window occupancy by scan (scan: notes at mid-scan tick):");
            for (int scan = 0; scan <= maxScan; scan++)
            {
                int tick = (scan * ticksPerScan) + (ticksPerScan / 2);
                GameplayPreviewFrame frame = projection.CreateRenderableFrame(tick);
                occupancy.Add(new KeyValuePair<int, int>(tick, frame.Notes.Count));

                var present = new HashSet<GameplayPreviewNoteKind>();
                for (int n = 0; n < frame.Notes.Count; n++)
                {
                    present.Add(frame.Notes[n].Kind);
                }
                kindsAtTick[tick] = present;

                if (scan < 24)
                {
                    report.Append("  ").Append(scan).Append(": ")
                        .Append(frame.Notes.Count).AppendLine();
                }
            }

            occupancy.Sort((a, b) => b.Value.CompareTo(a.Value));
            var chosen = new List<int>();
            chosen.Add(0);
            for (int i = 0; i < occupancy.Count && chosen.Count < FrameCount; i++)
            {
                if (occupancy[i].Value == 0)
                {
                    break;
                }
                // Keep frames at least a scan apart so they show different patterns.
                bool tooClose = false;
                for (int j = 0; j < chosen.Count; j++)
                {
                    if (Math.Abs(chosen[j] - occupancy[i].Key) < ticksPerScan)
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (!tooClose)
                {
                    chosen.Add(occupancy[i].Key);
                }
            }

            chosen.Sort();
            AddKindCoverage(projection, chosen, kindsAtTick, ticksPerScan, report);

            chosen.Sort();
            report.Append("chosen ticks:");
            for (int i = 0; i < chosen.Count; i++)
            {
                report.Append(' ').Append(chosen[i]);
            }
            report.AppendLine();
            return chosen.ToArray();
        }

        /// <summary>
        /// Tops the frame set up with one frame per note kind the chart contains but no chosen
        /// frame shows.
        ///
        /// <para>
        /// Choosing frames purely by how full they are systematically misses the kinds worth
        /// verifying most: a chart's handful of <c>Hold</c> notes are exactly what a busy frame
        /// crowds out, and the hold trail is the part of the renderer most recently wrong. Six
        /// frames of this chart's densest scans contain no <c>Hold</c> at all despite the chart
        /// having eight, so "trails draw for holds and only for holds" was not checkable from the
        /// frames the probe picked.
        /// </para>
        /// </summary>
        private static void AddKindCoverage(
            GameplayPreviewProjection projection,
            List<int> chosen,
            Dictionary<int, HashSet<GameplayPreviewNoteKind>> kindsAtTick,
            int ticksPerScan,
            StringBuilder report)
        {
            var covered = new HashSet<GameplayPreviewNoteKind>();
            for (int i = 0; i < chosen.Count; i++)
            {
                HashSet<GameplayPreviewNoteKind> present;
                if (kindsAtTick.TryGetValue(chosen[i], out present))
                {
                    foreach (GameplayPreviewNoteKind kind in present)
                    {
                        covered.Add(kind);
                    }
                }
            }

            // Sorted by kind so two runs of the same chart produce comparable reports; the tick is
            // the first note of that kind, at its own scan's mid-point, where the renderable window
            // is guaranteed to hold it.
            var wanted = new SortedDictionary<GameplayPreviewNoteKind, int>();
            IReadOnlyList<ProjectedGameplayNote> notes = projection.Notes;
            for (int i = 0; i < notes.Count; i++)
            {
                ProjectedGameplayNote note = notes[i];
                if (covered.Contains(note.Kind) || wanted.ContainsKey(note.Kind))
                {
                    continue;
                }
                wanted[note.Kind] = (note.ScanIndex * ticksPerScan) + (ticksPerScan / 2);
            }

            if (wanted.Count == 0)
            {
                report.AppendLine(
                    "kind coverage: every kind in the chart already appears in a chosen frame");
                return;
            }

            report.Append("kind coverage: adding frames for");
            foreach (KeyValuePair<GameplayPreviewNoteKind, int> pair in wanted)
            {
                // A frame added for one kind usually carries others with it, so re-check rather
                // than adding a frame per kind.
                if (covered.Contains(pair.Key))
                {
                    continue;
                }

                report.Append(' ').Append(pair.Key).Append('@').Append(pair.Value);
                if (!chosen.Contains(pair.Value))
                {
                    chosen.Add(pair.Value);
                }

                HashSet<GameplayPreviewNoteKind> present;
                if (kindsAtTick.TryGetValue(pair.Value, out present))
                {
                    foreach (GameplayPreviewNoteKind kind in present)
                    {
                        covered.Add(kind);
                    }
                }
                else
                {
                    covered.Add(pair.Key);
                }
            }
            report.AppendLine();
        }

        private static void RenderFrames(
            GameplayPreviewProjection projection,
            int[] ticks,
            int width,
            string outputDirectory,
            StringBuilder report)
        {
            Directory.CreateDirectory(outputDirectory);

            var view = new TechnikaPlayfieldView();
            view.Bind(projection);
            view.Measure(new Size(width, double.PositiveInfinity));
            Size size = view.DesiredSize;
            view.Arrange(new Rect(new Point(0, 0), size));
            view.UpdateLayout();
            report.AppendFormat(CultureInfo.InvariantCulture,
                "element: measured {0:F1}x{1:F1}", size.Width, size.Height).AppendLine();

            int pixelWidth = Math.Max(1, (int)Math.Round(size.Width));
            int pixelHeight = Math.Max(1, (int)Math.Round(size.Height));

            for (int i = 0; i < ticks.Length; i++)
            {
                int tick = ticks[i];
                GameplayPreviewFrame frame = projection.CreateRenderableFrame(tick);
                view.Sync(tick * EventData.VirtualTickSize);

                var target = new RenderTargetBitmap(
                    pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
                target.Render(view);

                string file = Path.Combine(outputDirectory, string.Format(
                    CultureInfo.InvariantCulture, "playfield-{0:00}-tick{1}.png", i, tick));
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(target));
                using (FileStream stream = File.Create(file))
                {
                    encoder.Save(stream);
                }

                report.AppendFormat(CultureInfo.InvariantCulture,
                    "frame {0}: tick={1} scan={2} phase={3:F3} notes={4} -> {5}",
                    i, tick, frame.CurrentIntScan, frame.CurrentPhase, frame.Notes.Count,
                    Path.GetFileName(file)).AppendLine();

                for (int n = 0; n < frame.Notes.Count; n++)
                {
                    ProjectedGameplayNote note = frame.Notes[n];

                    // The trail verdict is printed rather than left to the eye: every TECHNIKA note
                    // carries a keysound duration, so "does this note draw a trail" is the one
                    // question a screenshot of a dense frame cannot answer, and it is exactly the
                    // thing that was wrong.
                    bool trail = note.DurationPulse > 0 &&
                        GameplayPreviewNoteKinds.HasHoldTrail(note.Kind);

                    report.AppendFormat(CultureInfo.InvariantCulture,
                        "    {0,-14} lane={1} scan={2,-3} x={3:F4} top={4,-5} {5,-7} " +
                        "approach={6:F2} durPulse={7,-4} trail={8}",
                        note.Kind, note.Lane, note.ScanIndex, note.X, note.IsTopHalf,
                        note.State, note.ApproachProgress, note.DurationPulse,
                        trail ? "yes" : "no").AppendLine();
                }
            }
        }

        private static void Write(string outputDirectory, StringBuilder report)
        {
            try
            {
                Directory.CreateDirectory(outputDirectory);
                File.WriteAllText(
                    Path.Combine(outputDirectory, "playfield-report.txt"), report.ToString());
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
