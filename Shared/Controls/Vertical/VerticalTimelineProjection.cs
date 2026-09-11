using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using DJMaxEditor.Controls.TimelineV2;
using DJMaxEditor.DJMax;
using DJMaxEditor.Files.bms;
using DJMaxEditor.Files.FormatDetection;

namespace DJMaxEditor.Controls.Vertical
{
    /// <summary>
    /// Projects a chart into the ptSequencer-style vertical column layout.
    /// </summary>
    /// <remarks>
    /// This deliberately reuses the V2 <see cref="TimelineItem"/> and
    /// <see cref="TimelineEventIndex"/> instead of cloning a second interval index.
    /// The index is partitioned on <see cref="TimelineItem.RowIndex"/>, which the
    /// vertical surface uses as the <em>column</em> index — so one tested
    /// prefix-maximum interval index serves both the horizontal DAW (rows) and the
    /// vertical timeline (columns), and long events starting before the visible
    /// window stay discoverable on both.
    ///
    /// Ticks are authoritative <see cref="EventData.VirtualTick"/> values, matching
    /// <c>RawTrackProjection</c> and <c>EditorViewState.PlayheadVirtualTick</c>, so the
    /// vertical and horizontal surfaces share one time base.
    /// </remarks>
    public sealed class VerticalTimelineProjection
    {
        private readonly ReadOnlyCollection<TimelineItem> _items;

        private VerticalTimelineProjection(
            VerticalTrackLayout layout,
            IList<TimelineItem> items,
            int skippedEventCount)
        {
            Layout = layout;
            _items = new ReadOnlyCollection<TimelineItem>(items);
            Index = new TimelineEventIndex(items, 0, layout.Columns.Count);
            SkippedEventCount = skippedEventCount;
        }

        public VerticalTrackLayout Layout { get; private set; }

        /// <summary>
        /// Button count (4/5/6/8), or <see cref="VerticalTrackLayout.TechnikaMode"/> for a
        /// TECHNIKA chart, or <see cref="VerticalTrackLayout.BmsMode"/> for a BMS one. Use
        /// <c>Layout.DisplayName</c> for anything a user reads.
        /// </summary>
        public int Mode { get { return Layout.Mode; } }

        /// <summary>Every projected event. <c>RowIndex</c> is the vertical column index.</summary>
        public ReadOnlyCollection<TimelineItem> Items { get { return _items; } }

        /// <summary>Column-partitioned interval index over <see cref="Items"/>.</summary>
        public TimelineEventIndex Index { get; private set; }

        /// <summary>
        /// Events that ended up with no column at all. The layout now grows an overflow column
        /// for every occupied source track the preset does not name, so this is 0 for any chart
        /// whose tracks are addressable - it stays as the guard for a genuinely impossible id
        /// rather than as the routine outcome it used to be.
        /// </summary>
        public int SkippedEventCount { get; private set; }

        /// <summary>
        /// How many source tracks were drawn on appended overflow columns rather than on a
        /// preset one. A UI can report it; 0 means the chart fits the ptSequencer schema.
        /// </summary>
        public int OverflowColumnCount { get { return Layout.OverflowColumnCount; } }

        /// <summary>
        /// Derives the layout from the chart: <see cref="VerticalTrackLayout.BmsMode"/> for a chart
        /// read out of a .bms, <see cref="VerticalTrackLayout.TechnikaMode"/> for a TECHNIKA-shaped
        /// one, otherwise the button mode using the same rule as the Respect BMS channel inference —
        /// 8B when the shoulder tracks (10/11) carry notes, otherwise the highest used gameplay track
        /// clamped to 4B-6B. Returns 0 when the chart has no gameplay notes at all.
        /// </summary>
        public static int DetectMode(PlayerData model)
        {
            if (model == null) throw new ArgumentNullException("model");

            // BMS first, and on format evidence rather than note placement: a .bms brings its own
            // channel schema, and a 7K+SC chart lands on tracks 0-8 with nothing on 9-11 - which is
            // precisely the TECHNIKA signature below, so the structural rule would answer
            // confidently and wrongly.
            if (IsBmsShaped(model)) return VerticalTrackLayout.BmsMode;

            // TECHNIKA next: its lanes live on tracks a button preset reads as the spacer and
            // BGA SYNC, so the button rule would answer confidently and wrongly.
            if (IsTechnikaShaped(model)) return VerticalTrackLayout.TechnikaMode;

            bool shoulder = false;
            int highestGameplayTrack = 0;
            foreach (TrackData track in model.Tracks)
            {
                if (!HasNote(track)) continue;

                if (track.Idx == 10 || track.Idx == 11)
                {
                    shoulder = true;
                }
                else if (track.Idx >= 3 && track.Idx <= 8 && (int)track.Idx > highestGameplayTrack)
                {
                    highestGameplayTrack = (int)track.Idx;
                }
            }

            if (shoulder) return 8;
            if (highestGameplayTrack == 0) return 0;
            return Math.Max(4, Math.Min(6, highestGameplayTrack - 2));
        }

        /// <summary>
        /// Whether the chart came out of a classic BMS file and still carries the channel map the
        /// reader built, which is what the BMS layout is drawn from.
        /// </summary>
        /// <remarks>
        /// Format evidence, not note placement, and deliberately so: BMS lane ids are channels, and
        /// no arrangement of notes on tracks 0-8 can tell a 7K+SC chart apart from a TECHNIKA one
        /// (both put notes on track 0 and none on 9-11). <c>ChartFormat.BmsClassic</c> is only set by
        /// the reader, and <c>BmsMetadata.TrackChannels</c> is only written there too - the BMS
        /// *writer* reads that map and never fills it - so exporting a .pt to BMS cannot make a .pt
        /// look like a BMS chart on reopen.
        /// </remarks>
        public static bool IsBmsShaped(PlayerData model)
        {
            if (model == null) throw new ArgumentNullException("model");
            return model.SourceFormat == ChartFormat.BmsClassic
                && model.BmsMetadata != null
                && model.BmsMetadata.TrackChannels.Count > 0;
        }

        /// <summary>
        /// Whether the chart is authored against the TECHNIKA schema — four touch lanes on source
        /// tracks 0-3 with end-of-scan markers on 4-7 — rather than against a ptSequencer button
        /// preset.
        /// </summary>
        /// <remarks>
        /// The discriminator is a note on source track 0. Every DPC preset spends track 0 on the
        /// unused spacer and track 1 on the BGA sync marker, so neither carries notes in a Portable
        /// or Trilogy chart, while those two ids are TECHNIKA's first two lanes. Tracks 9-11 are the
        /// other half of the test: SIDE R and the two shoulders are Portable-only, so a chart that
        /// uses them is a button chart even if something odd sits on track 0.
        ///
        /// Structural rather than <c>SourceFormat</c>-based on purpose. PTFF carries both TECHNIKA
        /// and Trilogy data - which is why the gameplay preview has to ask the user which profile it
        /// is - and a Trilogy chart is a button chart, so the format cannot answer this. Note
        /// placement is the thing that actually differs, and it is also what the harness can build.
        /// </remarks>
        public static bool IsTechnikaShaped(PlayerData model)
        {
            if (model == null) throw new ArgumentNullException("model");

            // A TECHMANIA track.tech is unambiguously the TECHNIKA schema - format evidence
            // beats note placement here, because its invisible keysound lanes compact onto
            // overflow tracks 9+, which the PT-era structural test below reads as Portable
            // side/shoulder tracks and would answer 8B on.
            if (model.SourceFormat == ChartFormat.TechmaniaTrack)
            {
                return true;
            }

            bool firstLane = false;
            foreach (TrackData track in model.Tracks)
            {
                if (!HasNote(track)) continue;
                if (track.Idx >= 9 && track.Idx <= 11) return false;
                if (track.Idx == 0) firstLane = true;
            }
            return firstLane;
        }

        /// <summary>
        /// Projects using the mode detected from the chart. Returns null when the
        /// chart carries no gameplay notes, so callers can fall back to the
        /// horizontal-only layout instead of guessing a mode.
        /// </summary>
        public static VerticalTimelineProjection Project(PlayerData model)
        {
            int mode = DetectMode(model);
            return mode == 0 ? null : Project(model, mode);
        }

        public static VerticalTimelineProjection Project(PlayerData model, int mode)
        {
            if (model == null) throw new ArgumentNullException("model");

            // The layout decides which tracks are lanes; the chart decides whether anything else
            // needs a column. Both charts named in the report author on tracks the Portable presets
            // never mention, and before this the projection counted those events into
            // SkippedEventCount and drew nothing - two dense keysound tracks of sonoflong simply
            // were not on screen. A TECHNIKA chart goes further: the preset would have mislabelled
            // its four lanes as the spacer, BGA SYNC, SIDE L and button1, so it gets its own layout
            // and every track from 8 up is appended in track order. BMS goes further still: its lane
            // ids are channels, so the layout is built from the chart's own channel map and only the
            // tracks with no channel at all are appended.
            VerticalTrackLayout layout = VerticalTrackLayout.IsBmsMode(mode)
                ? VerticalTrackLayout.ForBms(BmsTrackChannels(model), UnchanneledOccupiedTracks(model))
                : VerticalTrackLayout.ForMode(mode, UnmappedOccupiedTracks(model, mode));
            var items = new List<TimelineItem>();
            int skipped = 0;

            foreach (TrackData track in model.Tracks)
            {
                VerticalColumn column = layout.ColumnForSourceTrack((int)track.Idx);
                foreach (EventData sourceEvent in track.Events)
                {
                    if (column == null)
                    {
                        skipped++;
                        continue;
                    }

                    int startTick = sourceEvent.VirtualTick;
                    int endTick = startTick + sourceEvent.VirtualDuration;
                    items.Add(new TimelineItem(column.Index, startTick, endTick, sourceEvent));
                }
            }

            return new VerticalTimelineProjection(layout, items, skipped);
        }

        /// <summary>
        /// Queries one visible tick window across a column span. Long events that
        /// begin above the window are included because the shared index keeps a
        /// prefix maximum end tick per column.
        /// </summary>
        public IReadOnlyList<TimelineItem> Query(
            double startTick,
            double endTick,
            int firstColumn,
            int lastColumn)
        {
            return Index.Query(
                new TimelineTimeRange(startTick, endTick),
                new TimelineRowRange(firstColumn, lastColumn));
        }

        public IReadOnlyList<TimelineItem> Query(double startTick, double endTick)
        {
            return Query(startTick, endTick, 0, Layout.Columns.Count - 1);
        }

        public VerticalColumn ColumnOf(TimelineItem item)
        {
            if (item == null) throw new ArgumentNullException("item");
            return item.RowIndex >= 0 && item.RowIndex < Layout.Columns.Count
                ? Layout.Columns[item.RowIndex]
                : null;
        }

        private static bool HasNote(TrackData track)
        {
            foreach (EventData sourceEvent in track.Events)
            {
                if (sourceEvent.EventType == EventType.Note)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The chart's track-to-BMS-channel pairs in track order, for tracks that actually exist. A
        /// metadata entry left behind by a deleted track is ignored, so the layout can never claim a
        /// column for a track the model no longer has.
        /// </summary>
        private static List<KeyValuePair<int, string>> BmsTrackChannels(PlayerData model)
        {
            var pairs = new List<KeyValuePair<int, string>>();
            BmsMetadata metadata = model.BmsMetadata;
            if (metadata == null)
            {
                return pairs;
            }

            foreach (TrackData track in model.Tracks)
            {
                string channel;
                if (metadata.TrackChannels.TryGetValue(track.Idx, out channel) &&
                    !string.IsNullOrEmpty(channel))
                {
                    pairs.Add(new KeyValuePair<int, string>((int)track.Idx, channel));
                }
            }
            return pairs;
        }

        /// <summary>
        /// Occupied tracks with no BMS channel, which the BMS layout appends as overflow columns.
        /// Picking the BMS layout for a chart that is not BMS lands everything here - honest, and
        /// still nothing hidden.
        /// </summary>
        private static List<int> UnchanneledOccupiedTracks(PlayerData model)
        {
            BmsMetadata metadata = model.BmsMetadata;
            var extras = new List<int>();
            foreach (TrackData track in model.Tracks)
            {
                string channel;
                if (metadata != null &&
                    metadata.TrackChannels.TryGetValue(track.Idx, out channel) &&
                    !string.IsNullOrEmpty(channel))
                {
                    continue;
                }
                foreach (EventData sourceEvent in track.Events)
                {
                    if (sourceEvent != null)
                    {
                        extras.Add((int)track.Idx);
                        break;
                    }
                }
            }
            return extras;
        }

        /// <summary>
        /// Source tracks that carry at least one event and that the bare preset for
        /// <paramref name="mode"/> has no column for.
        /// </summary>
        /// <remarks>
        /// Any event counts, not just notes: a lone Volume or Tempo event on track 19 is content
        /// the author put there, and hiding it is the same defect as hiding a note. Empty
        /// unmapped tracks get nothing, which is why a Portable chart's layout is unchanged - a
        /// .pt allocates 64 tracks whether or not it uses them.
        /// </remarks>
        private static List<int> UnmappedOccupiedTracks(PlayerData model, int mode)
        {
            VerticalTrackLayout preset = VerticalTrackLayout.ForMode(mode);
            var extras = new List<int>();
            foreach (TrackData track in model.Tracks)
            {
                if (preset.ColumnForSourceTrack((int)track.Idx) != null)
                {
                    continue;
                }
                foreach (EventData sourceEvent in track.Events)
                {
                    if (sourceEvent != null)
                    {
                        extras.Add((int)track.Idx);
                        break;
                    }
                }
            }
            return extras;
        }
    }
}
