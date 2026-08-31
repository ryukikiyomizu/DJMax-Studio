using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using DJMaxEditor.DJMax;
using DJMaxEditor.Preview;

namespace DJMaxEditor.Studio.Preview
{
    /// <summary>
    /// Counts, over a whole corpus, what a TECHNIKA chart puts on the tracks the playfield does not
    /// draw - and whether any of it is gameplay.
    ///
    /// <para>
    /// This exists to settle one open question with numbers instead of an argument.
    /// <c>GameplayPreviewProjector.ProjectTechnika</c> drops every source track above 3, and the
    /// owner's report was that "the other tracks of technika pt doesn't showed up". Both timelines
    /// draw those events; only the playfield does not. Whether that is right depends on what is
    /// actually on those tracks, which no amount of reading the projector can say.
    /// </para>
    ///
    /// <para>
    /// So this walks every chart, opens it through the same service the shell uses, and reports per
    /// track band: tracks 0-3 (the lanes), 4-7 (end-of-scan flags, consumed as flags on lane notes
    /// rather than drawn), and 8 and up. For the last band it counts how many events
    /// <see cref="GameplayPreviewProjector.Classify"/> - the playfield's own rule, not a copy of it -
    /// would draw, and histograms the note attributes. Metadata only: track indices, event counts and
    /// attribute numbers. No chart bytes are written anywhere.
    /// </para>
    /// </summary>
    internal static class TrackCensusProbe
    {
        /// <summary>Command-line switch that selects this mode.</summary>
        public const string Switch = "--track-census";

        /// <summary>
        /// Runs the census. Returns 0 when at least one chart was read, 2 on a usage error and 3
        /// when nothing could be opened at all.
        /// </summary>
        public static int Run(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine(Switch + " <chart-or-directory> <output-file>");
                return 2;
            }

            string target = args[1];
            string outputPath = args[2];
            List<string> charts = Collect(target);
            if (charts.Count == 0)
            {
                Console.Error.WriteLine("no .pt charts under " + target);
                return 2;
            }

            var report = new StringBuilder();
            report.Append("track census over ").Append(charts.Count)
                .Append(" chart(s) under ").AppendLine(target);
            report.AppendLine(
                "columns: lanes(0-3) eos(4-7) above7=notes/drawable  highest  chart");
            report.AppendLine();

            var totals = new Census();
            var attributes = new SortedDictionary<int, int>();
            var drawableAbove = new SortedDictionary<string, int>();
            var perTrack = new int[TrackSlots];
            var perTrackDuo = new int[TrackSlots];
            var perTrackCharts = new int[TrackSlots];
            var perTrackAttributes = new SortedDictionary<int, int>[TrackSlots];
            int opened = 0;
            int duoCharts = 0;

            foreach (string chart in charts)
            {
                var scratch = new StringBuilder();
                PlayerData model;
                try
                {
                    model = PlayfieldProbe.Open(chart, scratch);
                }
                catch (Exception ex)
                {
                    report.Append("  FAILED ").Append(Name(chart)).Append("  ")
                        .AppendLine(ex.GetType().Name + ": " + ex.Message);
                    continue;
                }

                if (model == null || model.Tracks == null)
                {
                    report.Append("  FAILED ").AppendLine(Name(chart));
                    continue;
                }

                opened++;
                bool duo = Name(chart).IndexOf("_duo_", StringComparison.OrdinalIgnoreCase) >= 0;
                if (duo)
                {
                    duoCharts++;
                }

                Census one = Measure(model, attributes, drawableAbove,
                    duo ? perTrackDuo : perTrack, perTrackCharts, perTrackAttributes);
                totals.Add(one);
                report.AppendFormat(CultureInfo.InvariantCulture,
                    "  {0,6} {1,5} {2,6}/{3,-6} {4,3}  {5}",
                    one.LaneNotes, one.EndOfScanNotes, one.AboveNotes, one.AboveDrawable,
                    one.HighestTrack, Name(chart)).AppendLine();
            }

            Summarise(report, totals, attributes, drawableAbove, opened, charts.Count);
            PerTrack(report, perTrack, perTrackDuo, perTrackCharts, opened - duoCharts, duoCharts);
            PerTrackAttributes(report, perTrackAttributes);
            File.WriteAllText(outputPath, report.ToString());
            Console.Out.Write(Tail(report.ToString()));
            return opened > 0 ? 0 : 3;
        }

        /// <summary>How many track indices to histogram. The corpus's highest is 37.</summary>
        private const int TrackSlots = 64;

        private sealed class Census
        {
            public int Charts;
            public int LaneNotes;
            public int EndOfScanNotes;
            public int AboveNotes;
            public int AboveDrawable;
            public int ChartsWithAbove;
            public int ChartsWithDrawableAbove;
            public int HighestTrack = -1;

            public void Add(Census other)
            {
                Charts += 1;
                LaneNotes += other.LaneNotes;
                EndOfScanNotes += other.EndOfScanNotes;
                AboveNotes += other.AboveNotes;
                AboveDrawable += other.AboveDrawable;
                if (other.AboveNotes > 0) ChartsWithAbove++;
                if (other.AboveDrawable > 0) ChartsWithDrawableAbove++;
                if (other.HighestTrack > HighestTrack) HighestTrack = other.HighestTrack;
            }
        }

        /// <summary>
        /// One chart's counts. <paramref name="attributes"/> and <paramref name="drawableAbove"/>
        /// accumulate across the corpus: the first is every note attribute seen above track 7, the
        /// second is the kinds the playfield would have drawn there, which is the list that decides
        /// whether the filter is dropping gameplay.
        /// </summary>
        private static Census Measure(
            PlayerData model,
            IDictionary<int, int> attributes,
            IDictionary<string, int> drawableAbove,
            int[] perTrack,
            int[] perTrackCharts,
            SortedDictionary<int, int>[] perTrackAttributes)
        {
            var census = new Census();
            foreach (TrackData track in model.Tracks)
            {
                IReadOnlyList<EventData> events = track.OrderedEvents;
                if (events.Count == 0)
                {
                    continue;
                }

                int idx = (int)track.Idx;
                if (idx > census.HighestTrack)
                {
                    census.HighestTrack = idx;
                }
                if (idx >= 0 && idx < TrackSlots)
                {
                    perTrackCharts[idx]++;
                }

                for (int i = 0; i < events.Count; i++)
                {
                    EventData note = events[i];
                    if (note == null || note.EventType != EventType.Note)
                    {
                        continue;
                    }

                    if (idx >= 0 && idx < TrackSlots)
                    {
                        perTrack[idx]++;
                        if (perTrackAttributes[idx] == null)
                        {
                            perTrackAttributes[idx] = new SortedDictionary<int, int>();
                        }
                        Bump(perTrackAttributes[idx], note.Attribute);
                    }

                    if (idx <= 3)
                    {
                        census.LaneNotes++;
                        continue;
                    }
                    if (idx <= 7)
                    {
                        census.EndOfScanNotes++;
                        continue;
                    }

                    census.AboveNotes++;
                    Bump(attributes, note.Attribute);
                    GameplayPreviewNoteKind? kind = GameplayPreviewProjector.Classify(note);
                    if (kind.HasValue)
                    {
                        census.AboveDrawable++;
                        Bump(drawableAbove,
                            kind.Value + " (attr " +
                            note.Attribute.ToString(CultureInfo.InvariantCulture) + ")");
                    }
                }
            }

            return census;
        }

        private static void Summarise(
            StringBuilder report, Census totals,
            SortedDictionary<int, int> attributes,
            SortedDictionary<string, int> drawableAbove,
            int opened, int found)
        {
            report.AppendLine();
            report.AppendLine("== TOTALS ==");
            report.AppendFormat(CultureInfo.InvariantCulture,
                "charts found {0}, opened {1}", found, opened).AppendLine();
            report.AppendFormat(CultureInfo.InvariantCulture,
                "notes on tracks 0-3 (drawn as lanes): {0}", totals.LaneNotes).AppendLine();
            report.AppendFormat(CultureInfo.InvariantCulture,
                "notes on tracks 4-7 (end-of-scan flags): {0}", totals.EndOfScanNotes).AppendLine();
            report.AppendFormat(CultureInfo.InvariantCulture,
                "notes on tracks 8+ : {0} in {1} chart(s), highest track index {2}",
                totals.AboveNotes, totals.ChartsWithAbove, totals.HighestTrack).AppendLine();
            report.AppendFormat(CultureInfo.InvariantCulture,
                "  of those, the playfield's own rule would draw: {0} in {1} chart(s)",
                totals.AboveDrawable, totals.ChartsWithDrawableAbove).AppendLine();

            report.AppendLine("attributes seen above track 7 (attribute: count):");
            foreach (KeyValuePair<int, int> pair in attributes)
            {
                report.AppendFormat(CultureInfo.InvariantCulture,
                    "  {0,4}: {1}", pair.Key, pair.Value).AppendLine();
            }

            if (drawableAbove.Count == 0)
            {
                report.AppendLine(
                    "nothing above track 7 classifies as a drawable note, so the projector's " +
                    "track filter is dropping keysound-only events and no gameplay");
                return;
            }

            report.AppendLine("DRAWABLE kinds above track 7 - these are being dropped:");
            foreach (KeyValuePair<string, int> pair in drawableAbove)
            {
                report.AppendFormat(CultureInfo.InvariantCulture,
                    "  {0}: {1}", pair.Key, pair.Value).AppendLine();
            }
        }

        /// <summary>
        /// Notes per track index, DUO charts kept separate.
        ///
        /// <para>
        /// This is the shape that answers the question. A lane track and an accompaniment track are
        /// not told apart by their note attributes - attribute 0 is both a tap and the default a
        /// keysound event carries - so the only discriminator this format has is the index, and the
        /// only way to check the index is right is to look at what sits on each one. DUO is split out
        /// because its second player is authored on tracks 8-10, so a DUO file legitimately has lane
        /// density up there and a solo file should not.
        /// </para>
        /// </summary>
        private static void PerTrack(
            StringBuilder report, int[] solo, int[] duo, int[] charts, int soloCharts, int duoCharts)
        {
            report.AppendLine();
            report.AppendFormat(CultureInfo.InvariantCulture,
                "== NOTES PER TRACK INDEX ==  solo charts {0}, duo charts {1}",
                soloCharts, duoCharts).AppendLine();
            report.AppendLine("  idx  solo notes  per solo chart  duo notes  per duo chart  charts using");
            for (int i = 0; i < TrackSlots; i++)
            {
                if (solo[i] == 0 && duo[i] == 0 && charts[i] == 0)
                {
                    continue;
                }

                report.AppendFormat(CultureInfo.InvariantCulture,
                    "  {0,3}  {1,10}  {2,14:0.0}  {3,9}  {4,13:0.0}  {5,12}",
                    i, solo[i], soloCharts > 0 ? solo[i] / (double)soloCharts : 0.0,
                    duo[i], duoCharts > 0 ? duo[i] / (double)duoCharts : 0.0,
                    charts[i]).AppendLine();
            }
        }

        /// <summary>
        /// Attributes per track index above the lane band.
        ///
        /// <para>
        /// The per-index counts say which tracks are populated; this says what is on them, which is
        /// what tells a spare authoring slot from a band with a job. Tracks 16 and 17 are the reason
        /// it exists: they carry only a couple of events each yet appear in every chart in the
        /// corpus, so their attributes are the only way to see whether that is structure or noise.
        /// </para>
        /// </summary>
        private static void PerTrackAttributes(
            StringBuilder report, SortedDictionary<int, int>[] perTrackAttributes)
        {
            report.AppendLine();
            report.AppendLine("== ATTRIBUTES PER TRACK INDEX, TRACKS 8+ ==  (attribute:count)");
            for (int i = 8; i < TrackSlots; i++)
            {
                SortedDictionary<int, int> found = perTrackAttributes[i];
                if (found == null || found.Count == 0)
                {
                    continue;
                }

                report.AppendFormat(CultureInfo.InvariantCulture, "  {0,3}  ", i);
                foreach (KeyValuePair<int, int> pair in found)
                {
                    report.AppendFormat(CultureInfo.InvariantCulture,
                        "{0}:{1}  ", pair.Key, pair.Value);
                }
                report.AppendLine();
            }
        }

        private static void Bump<T>(IDictionary<T, int> counts, T key)
        {
            int existing;
            counts[key] = counts.TryGetValue(key, out existing) ? existing + 1 : 1;
        }

        private static List<string> Collect(string target)
        {
            var charts = new List<string>();
            if (File.Exists(target))
            {
                charts.Add(target);
                return charts;
            }

            if (!Directory.Exists(target))
            {
                return charts;
            }

            charts.AddRange(Directory.GetFiles(target, "*.pt", SearchOption.AllDirectories));
            charts.Sort(StringComparer.OrdinalIgnoreCase);
            return charts;
        }

        private static string Name(string path)
        {
            return Path.GetFileName(path);
        }

        /// <summary>The totals block, so a console caller sees the answer without opening the file.</summary>
        private static string Tail(string report)
        {
            int marker = report.IndexOf("== TOTALS ==", StringComparison.Ordinal);
            return marker < 0 ? report : report.Substring(marker);
        }
    }
}
