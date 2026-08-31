using System;
using System.Collections.Generic;
using System.Linq;
using DJMaxEditor.DJMax;
using DJMaxEditor.Files.bms;
using DJMaxEditor.Files.FormatDetection;

namespace DJMaxEditor.Preview
{
    public enum GameplayPreviewProfile
    {
        Generic,
        Technika
    }

    public enum GameplayPreviewNoteKind
    {
        Basic,
        Drag,
        ChainHead,
        ChainNode,
        RepeatHead,
        RepeatHeadHold,
        Repeat,
        RepeatHold,
        Hold,
        Generic
    }

    public enum GameplayPreviewNoteState
    {
        Inactive,
        Prepare,
        Active,
        Resolved
    }

    /// <summary>
    /// Facts about <see cref="GameplayPreviewNoteKind"/> that every preview surface has to agree
    /// on. Lives beside the enum rather than inside a renderer so the WPF playfield and the
    /// WinForms preview cannot drift, and so the predicate is reachable from the test harness.
    /// </summary>
    public static class GameplayPreviewNoteKinds
    {
        /// <summary>
        /// Whether a kind is a hold gesture, and so draws a trail behind its head.
        ///
        /// <para>
        /// A TECHNIKA note's stored duration is the length of its <em>keysound</em>, not the length
        /// of a hold - <em>every</em> note in a chart carries one, so a chart of 131 notes reports
        /// 131 non-zero durations. Gating a trail on duration alone therefore puts a stub behind
        /// all of them. Whether a note is held is a property of its kind, which is what the
        /// projector already classifies from the note attribute.
        /// </para>
        /// </summary>
        public static bool HasHoldTrail(GameplayPreviewNoteKind kind)
        {
            return kind == GameplayPreviewNoteKind.Hold ||
                kind == GameplayPreviewNoteKind.RepeatHold ||
                kind == GameplayPreviewNoteKind.RepeatHeadHold;
        }
    }

    public enum GameplayPreviewLaneRole
    {
        Regular,
        SideTrackLeft,
        SideTrackRight,
        ExtraButtonLeft,
        ExtraButtonRight
    }

    public enum RespectGameplayNoteType
    {
        None,
        White,
        Blue,
        Analog,
        L1,
        L2,
        R1,
        R2
    }

    public sealed class GameplayPreviewProfileSuggestion
    {
        public GameplayPreviewProfileSuggestion(
            GameplayPreviewProfile profile,
            bool requiresConfirmation,
            string explanation)
        {
            Profile = profile;
            RequiresConfirmation = requiresConfirmation;
            Explanation = explanation ?? string.Empty;
        }

        public GameplayPreviewProfile Profile { get; private set; }

        public bool RequiresConfirmation { get; private set; }

        public string Explanation { get; private set; }
    }

    public static class GameplayPreviewProfileResolver
    {
        public static GameplayPreviewProfileSuggestion Suggest(PlayerData model)
        {
            ChartFormat? format = model == null ? null : model.SourceFormat;
            if (format == ChartFormat.PtffDecrypted ||
                format == ChartFormat.PtffEncryptedTechnika)
            {
                return new GameplayPreviewProfileSuggestion(
                    GameplayPreviewProfile.Technika,
                    true,
                    "PTFF can contain TECHNIKA or Trilogy data. Confirm the TECHNIKA profile.");
            }

            return new GameplayPreviewProfileSuggestion(
                GameplayPreviewProfile.Generic,
                false,
                "This format uses the vertical lane preview.");
        }
    }

    public sealed class ProjectedGameplayNote
    {
        internal ProjectedGameplayNote(EventData source)
        {
            Source = source;
        }

        public EventData Source { get; internal set; }

        public int Lane { get; internal set; }

        public GameplayPreviewLaneRole LaneRole { get; internal set; }

        public RespectGameplayNoteType RespectType { get; internal set; }

        public double NativeX { get; internal set; }

        public double NativeY { get; internal set; }

        public double NativeHeight { get; internal set; }

        public int Pulse { get; internal set; }

        public int DurationPulse { get; internal set; }

        public GameplayPreviewNoteKind Kind { get; internal set; }

        public int ScanIndex { get; internal set; }

        public double RelativeScan { get; internal set; }

        public double X { get; internal set; }

        public double Y { get; internal set; }

        public bool IsTopHalf { get; internal set; }

        public bool EndOfScan { get; internal set; }

        public bool IsImplicitChainNode { get; internal set; }

        public GameplayPreviewNoteState State { get; internal set; }

        public bool ApproachVisible { get; internal set; }

        public double ApproachProgress { get; internal set; }

        /// <summary>
        /// How far the sweep is from this note, in scans: negative before it, zero on it, positive
        /// past it.
        ///
        /// <para>
        /// <see cref="ApproachProgress"/> answers the same question normalised into a window the
        /// projector chose, which is the right shape for fading something in but loses the scale a
        /// renderer needs to place a sprite. The arcade's approach glow is a fixed piece of light the
        /// sweep drags across the note, so how long it is on screen is a property of how wide the art
        /// is against how far a scan travels - a renderer can work that out from this and its own
        /// geometry, and could not from a normalised progress without hard-coding the projector's
        /// window a second time.
        /// </para>
        /// </summary>
        public double ApproachScanDistance { get; internal set; }

        internal ProjectedGameplayNote Copy()
        {
            return (ProjectedGameplayNote)MemberwiseClone();
        }
    }

    public sealed class GameplayPreviewFrame
    {
        internal GameplayPreviewFrame(
            int currentTick,
            double currentScan,
            IReadOnlyList<ProjectedGameplayNote> notes)
        {
            CurrentTick = currentTick;
            CurrentScan = currentScan;
            CurrentIntScan = (int)Math.Floor(currentScan);
            CurrentPhase = currentScan - CurrentIntScan;
            Notes = notes;
        }

        public int CurrentTick { get; private set; }

        public double CurrentScan { get; private set; }

        public int CurrentIntScan { get; private set; }

        public double CurrentPhase { get; private set; }

        public IReadOnlyList<ProjectedGameplayNote> Notes { get; private set; }
    }

    public sealed class GameplayPreviewProjection
    {
        private readonly ushort _ticksPerMeasure;
        private readonly int _beatsPerScan;

        internal GameplayPreviewProjection(
            GameplayPreviewProfile profile,
            string statusLabel,
            int laneCount,
            ushort ticksPerMeasure,
            int beatsPerScan,
            IList<ProjectedGameplayNote> notes,
            IList<string> diagnostics)
        {
            Profile = profile;
            StatusLabel = statusLabel;
            LaneCount = laneCount;
            _ticksPerMeasure = ticksPerMeasure;
            _beatsPerScan = beatsPerScan;
            Notes = new List<ProjectedGameplayNote>(notes).AsReadOnly();
            Diagnostics = new List<string>(diagnostics).AsReadOnly();
        }

        public GameplayPreviewProfile Profile { get; private set; }

        public string StatusLabel { get; private set; }

        public int LaneCount { get; private set; }

        public IReadOnlyList<ProjectedGameplayNote> Notes { get; private set; }

        public IReadOnlyList<string> Diagnostics { get; private set; }

        public GameplayPreviewFrame CreateFrame(int currentTick)
        {
            return CreateFrame(currentTick, RespectGameplayLayout.DefaultNoteSpeed);
        }

        public GameplayPreviewFrame CreateFrame(int currentTick, float noteSpeed)
        {
            return CreateFrame(currentTick, false, noteSpeed);
        }

        /// <summary>
        /// Creates the small playback window used by the renderer. Unlike the
        /// diagnostic/full frame, it does not clone notes that cannot be drawn.
        /// </summary>
        public GameplayPreviewFrame CreateRenderableFrame(int currentTick)
        {
            return CreateRenderableFrame(
                currentTick, RespectGameplayLayout.DefaultNoteSpeed);
        }

        public GameplayPreviewFrame CreateRenderableFrame(
            int currentTick,
            float noteSpeed)
        {
            return CreateFrame(currentTick, true, noteSpeed);
        }

        private GameplayPreviewFrame CreateFrame(
            int currentTick,
            bool renderableOnly,
            float noteSpeed)
        {
            if (noteSpeed <= 0f) throw new ArgumentOutOfRangeException("noteSpeed");
            int ticks = Math.Max(1, (int)_ticksPerMeasure);
            double currentScan = Profile == GameplayPreviewProfile.Technika
                ? (4.0 * currentTick) / (ticks * Math.Max(1, _beatsPerScan))
                : currentTick / (double)ticks;
            int currentIntScan = (int)Math.Floor(currentScan);
            double currentPhase = currentScan - currentIntScan;
            var notes = new List<ProjectedGameplayNote>(Notes.Count);
            RespectGameplayLayout respectLayout =
                Profile == GameplayPreviewProfile.Generic &&
                RespectGameplayLayout.NormalizeLaneMode(LaneCount) != 0
                    ? RespectGameplayLayout.ForMode(LaneCount)
                    : null;

            foreach (ProjectedGameplayNote topology in Notes)
            {
                if (renderableOnly && !IsInRenderableWindow(
                    topology, currentTick, currentIntScan, ticks))
                {
                    continue;
                }

                ProjectedGameplayNote note = topology.Copy();
                if (Profile == GameplayPreviewProfile.Technika)
                {
                    double pulsesPerScan = 240.0 * Math.Max(1, _beatsPerScan);
                    double noteFloatScan = note.Pulse / pulsesPerScan;
                    double endFloatScan = (note.Pulse + note.DurationPulse) / pulsesPerScan;
                    double distance = currentScan - noteFloatScan;

                    if (note.ScanIndex < currentIntScan)
                    {
                        note.State = GameplayPreviewNoteState.Resolved;
                    }
                    else if (note.ScanIndex == currentIntScan)
                    {
                        // Resolved the moment the sweep is past it, not at the end of the scan it
                        // sits in. A scan is several seconds wide, so the end-of-scan test left every
                        // note the line had already crossed sitting on the field at full brightness
                        // until the handover - a bar's worth of notes stacked up behind the line, in a
                        // game whose whole read is "the line is where now is". Held notes answer for
                        // their tail rather than their head: the body is still being played.
                        note.State = currentScan > endFloatScan
                            ? GameplayPreviewNoteState.Resolved
                            : GameplayPreviewNoteState.Active;
                    }
                    else if (note.ScanIndex == currentIntScan + 1)
                    {
                        note.State = currentPhase >= 0.875
                            ? GameplayPreviewNoteState.Active
                            : GameplayPreviewNoteState.Prepare;
                    }
                    else
                    {
                        note.State = GameplayPreviewNoteState.Inactive;
                    }

                    note.ApproachVisible = distance >= -0.5 && distance <= 0;
                    note.ApproachProgress = note.ApproachVisible
                        ? Math.Max(0, Math.Min(1, (distance + 0.5) / 0.5))
                        : 0;
                    note.ApproachScanDistance = distance;
                }
                else
                {
                    int distance = note.Source.Tick - currentTick;
                    note.State = Math.Abs(distance) <= Math.Max(1, ticks / 16)
                        ? GameplayPreviewNoteState.Active
                        : distance < 0
                            ? GameplayPreviewNoteState.Resolved
                            : GameplayPreviewNoteState.Prepare;
                    note.X = Math.Max(0.05, Math.Min(0.95,
                        0.5 + (distance / (double)(ticks * 2))));

                    if (respectLayout != null &&
                        note.RespectType != RespectGameplayNoteType.None)
                    {
                        note.NativeY = respectLayout.GetNoteY(
                            note.Source.Tick, currentTick, noteSpeed);
                        note.NativeHeight = respectLayout.GetLongNoteHeight(
                            note.Source.Duration,
                            noteSpeed,
                            respectLayout.GetDefaultNoteHeight(note.RespectType));
                    }
                }
                notes.Add(note);
            }

            return new GameplayPreviewFrame(currentTick, currentScan, notes.AsReadOnly());
        }

        private bool IsInRenderableWindow(
            ProjectedGameplayNote note,
            int currentTick,
            int currentIntScan,
            int ticks)
        {
            if (Profile == GameplayPreviewProfile.Technika)
            {
                // The Technika renderer draws only this scan and the next scan;
                // older/further scans are Resolved or Inactive before painting.
                int scanDistance = note.ScanIndex - currentIntScan;
                return scanDistance >= 0 && scanDistance <= 1;
            }

            // Keep a long note while any part of its tick span intersects the
            // playback window. Testing only its head makes an active tail pop
            // out as soon as the start tick moves two measures behind playback.
            long windowStart = (long)currentTick - (ticks * 2L);
            long windowEnd = (long)currentTick + (ticks * 2L);
            long noteStart = note.Source.Tick;
            long noteEnd = noteStart + note.Source.Duration;
            return noteEnd >= windowStart && noteStart <= windowEnd;
        }
    }

    public static class GameplayPreviewProjector
    {
        private const int PulsesPerMeasure = 960;
        private const int PulsesPerBeat = 240;
        private const int DefaultBeatsPerScan = 4;

        /// <summary>
        /// Left edge of the TECHNIKA note field, as a fraction of the arcade's 1280 px width.
        ///
        /// <para>
        /// Measured from the client's own draw calls, not chosen. In the D3D9 capture the scanline
        /// sprite (<c>panel/line_star.png</c>, 250x355 quad) is drawn on both halves during a
        /// handover, and the lower half's quad carries mirrored UVs - u=1 at its left edge, u=0 at
        /// its right - so the two bright edges sit at <c>upperLeft + o</c> and
        /// <c>lowerLeft + (250 - o)</c> for whatever leading-edge offset <c>o</c> the sprite has.
        /// Their sum therefore drops <c>o</c> entirely, and across every frame that draws both
        /// halves the client holds <c>upperLeft + lowerLeft + 250</c> at exactly 313.0 or exactly
        /// 2233.0 with no spread at all. The sweep is continuous in x through a handover, so the
        /// two edges must coincide there: at x = 156.5 and x = 1116.5, both independent of the
        /// sprite measurement. Those two crossings are the field's edges.
        /// </para>
        /// <para>
        /// The consequence is that both halves share <em>one</em> screen rectangle rather than
        /// being mirror images of each other: the field's left margin is 156.5 px and its right
        /// margin 1280 - 1116.5 = 163.5 px, so it sits 7 px left of centre. Mirroring the upper
        /// half's window - <c>1 - x</c> - moves the lower half 7 px off the arcade's.
        /// </para>
        /// </summary>
        public const double ScanFieldLeft = 156.5 / 1280.0;

        /// <summary>
        /// Right edge of the TECHNIKA note field. See <see cref="ScanFieldLeft"/> for how both
        /// edges were measured. The span is exactly 960 px - 0.75 of the width - which the capture
        /// confirms twice over: the two invariant regimes above differ by exactly 1920.0, giving
        /// each half's line a 1920 px spatial period and one scan an advance of half that.
        /// </summary>
        public const double ScanFieldRight = 1116.5 / 1280.0;

        /// <summary>
        /// Highest source track the playfield draws as a lane.
        ///
        /// <para>
        /// This number used to be a bare <c>Idx &gt; 3</c> with nothing behind it, which made "the
        /// other tracks of technika pt doesn't showed up" impossible to answer: both timelines draw
        /// those events and only the playfield does not, so whether that is a defect depends on what
        /// the corpus puts up there and not on anything readable in this file. So it was counted.
        /// <c>--track-census</c> over all 445 TECHNIKA 2 charts (444 open; the 445th is the known
        /// bad <c>lovemode_star_1.pt</c>) gives notes per source track per chart:
        /// </para>
        ///
        /// <para>
        /// <code>
        /// track        solo (416 charts)   duo (28)     what is there
        ///  0-3         68 / 98 / 93 / 72   20/37/22/0   the lanes. DUO player 1 is 3-line.
        ///  4-7         1.2 .. 1.7          0.1          end-of-scan flags, consumed as a flag on
        ///                                               a lane note by ApplyEndOfScanMarkers
        ///  8-10        0                   18/37/23     a DUO chart's second player
        /// 11-15,18-19  under 0.05          under 0.05   nothing - stray authoring, single digits
        /// 16-17        1.9 / 1.1           1.9 / 1.0    every chart has both; track 17 carries the
        ///                                               one attribute-100 event per chart
        /// 20-31        48 .. 150 each      44 .. 159    keysound accompaniment, ~1200 per chart
        /// 32-37        ~0                  ~0           nothing
        /// </code>
        /// </para>
        ///
        /// <para>
        /// So the filter is right about the two bands it was really being accused of dropping. The
        /// 20-31 bank is 1202 events per chart against 336 on the lanes; drawn as gameplay it would
        /// be twelve notes a second sustained across four lanes, which is not a TECHNIKA chart. And
        /// it cannot be told from gameplay by attribute - attribute 0 is both a tap and the default
        /// a keysound-only event carries, so <see cref="Classify"/> happily returns
        /// <c>Basic</c> for 523,772 of them. The track index is this format's only discriminator,
        /// which is exactly why the census had to be per index.
        /// </para>
        ///
        /// <para>
        /// The one real gap is DUO. Zero notes sit on tracks 8-10 in any of the 416 solo charts,
        /// while all 28 <c>*_duo_1.pt</c> files put 18/37/23 notes per chart there - their own
        /// lanes 0-2 hold 20/37/22 - and leave track 3 and track 11 empty, i.e. three lanes each
        /// for two players. That is gameplay, and this projection draws one player's field, so it
        /// is reported as a projection warning rather than dropped in silence. How a second player
        /// should be presented is a design decision and not this method's to make.
        /// </para>
        /// </summary>
        private const int LastLaneTrack = 3;

        /// <summary>
        /// First and last source track of a DUO chart's second player. See
        /// <see cref="LastLaneTrack"/> for the census these came from.
        /// </summary>
        private const int SecondPlayerFirstTrack = 8;
        private const int SecondPlayerLastTrack = 10;

        public static GameplayPreviewProjection Project(
            PlayerData model,
            GameplayPreviewProfile profile)
        {
            if (model == null) throw new ArgumentNullException("model");
            return profile == GameplayPreviewProfile.Technika
                ? ProjectTechnika(model)
                : ProjectGeneric(model);
        }

        private static GameplayPreviewProjection ProjectTechnika(PlayerData model)
        {
            int ticksPerMeasure = Math.Max(1, (int)model.TickPerMinute);
            var diagnostics = new List<string>();
            var notes = new List<ProjectedGameplayNote>();
            int secondPlayerNotes = 0;

            foreach (TrackData track in model.Tracks)
            {
                if (track.Idx > LastLaneTrack)
                {
                    if (track.Idx >= SecondPlayerFirstTrack &&
                        track.Idx <= SecondPlayerLastTrack)
                    {
                        secondPlayerNotes += CountNotes(track);
                    }
                    continue;
                }
                foreach (EventData source in track.Events)
                {
                    GameplayPreviewNoteKind? kind = Classify(source);
                    if (!kind.HasValue)
                    {
                        if (source.EventType == EventType.Note && source.Attribute != 100)
                        {
                            diagnostics.Add(
                                "Unsupported attribute " + source.Attribute +
                                " on lane " + track.Idx + " at tick " + source.Tick + ".");
                        }
                        continue;
                    }

                    notes.Add(new ProjectedGameplayNote(source)
                    {
                        Lane = (int)track.Idx,
                        Pulse = TickToPulse(source.Tick, ticksPerMeasure),
                        DurationPulse = TickToPulse(source.Duration, ticksPerMeasure),
                        Kind = kind.Value
                    });
                }
            }

            notes = notes
                .OrderBy(note => note.Pulse)
                .ThenBy(note => note.Lane)
                .ToList();

            if (secondPlayerNotes > 0)
            {
                diagnostics.Add(
                    "DUO chart: " + secondPlayerNotes + " note(s) belong to a second player on " +
                    "tracks " + SecondPlayerFirstTrack + "-" + SecondPlayerLastTrack +
                    ", which this one-player field does not draw.");
            }

            ApplyChainFixups(notes, diagnostics);
            ApplyRepeatFixups(notes, diagnostics);
            ApplyEndOfScanMarkers(model, notes, ticksPerMeasure);

            int laneCount = DeriveLaneCount(notes);
            foreach (ProjectedGameplayNote note in notes)
            {
                PlaceTechnikaNote(note, laneCount);
            }

            return new GameplayPreviewProjection(
                GameplayPreviewProfile.Technika,
                "TECHNIKA PROFILE  |  CONFIRMED TWO-WAY PROJECTION",
                laneCount,
                model.TickPerMinute,
                DefaultBeatsPerScan,
                notes,
                diagnostics);
        }

        private static GameplayPreviewProjection ProjectGeneric(PlayerData model)
        {
            var diagnostics = new List<string>();
            var notes = new List<ProjectedGameplayNote>();
            List<TrackData> noteTracks = SelectGenericNoteTracks(model);
            int laneCount = Math.Max(1, noteTracks.Count);
            RespectGameplayLayout respectLayout =
                model.SourceFormat == ChartFormat.TrailerRespectV &&
                RespectGameplayLayout.NormalizeLaneMode(laneCount) != 0
                    ? RespectGameplayLayout.ForMode(laneCount)
                    : null;

            int regularLane = 0;
            for (int trackIndex = 0; trackIndex < noteTracks.Count; trackIndex++)
            {
                TrackData track = noteTracks[trackIndex];
                GameplayPreviewLaneRole role = GenericLaneRole(
                    model, track, laneCount);
                int lane = role == GameplayPreviewLaneRole.Regular
                    ? regularLane++
                    : role == GameplayPreviewLaneRole.ExtraButtonLeft ? 0 : 1;
                foreach (EventData source in track.Events)
                {
                    if (source.EventType != EventType.Note) continue;
                    notes.Add(new ProjectedGameplayNote(source)
                    {
                        Lane = lane,
                        LaneRole = role,
                        Kind = GameplayPreviewNoteKind.Generic,
                        Pulse = source.Tick,
                        DurationPulse = source.Duration,
                        ScanIndex = 0,
                        RelativeScan = 0.5,
                        X = 0.5,
                        Y = (lane + 0.5) / laneCount,
                        IsTopHalf = false
                    });
                    ProjectedGameplayNote projected = notes[notes.Count - 1];
                    if (respectLayout != null)
                    {
                        projected.RespectType = RespectTypeForTrack(track.Idx, laneCount);
                        projected.NativeX = respectLayout.GetTrackX((int)track.Idx);
                    }
                }
            }

            if (model.SourceFormat == ChartFormat.TrailerRespectV)
            {
                AddRespectSideTrack(
                    model, notes, respectLayout, 2,
                    GameplayPreviewLaneRole.SideTrackLeft,
                    RespectGameplayNoteType.Analog);
                AddRespectSideTrack(
                    model, notes, respectLayout, 9,
                    GameplayPreviewLaneRole.SideTrackRight,
                    RespectGameplayNoteType.Analog);
                if (laneCount == 8)
                {
                    AddRespectSideTrack(
                        model, notes, respectLayout, 12,
                        GameplayPreviewLaneRole.ExtraButtonLeft,
                        RespectGameplayNoteType.L2);
                    AddRespectSideTrack(
                        model, notes, respectLayout, 13,
                        GameplayPreviewLaneRole.ExtraButtonRight,
                        RespectGameplayNoteType.R2);
                }
            }

            return new GameplayPreviewProjection(
                GameplayPreviewProfile.Generic,
                model.SourceFormat == ChartFormat.TrailerRespectV
                    ? "RESPECT V  |  PACKAGE-DERIVED GAMEPLAY  |  4B / 5B / 6B / 8B"
                    : "VERTICAL LANE PREVIEW  |  BMS PLAYABLE CHANNELS",
                laneCount,
                model.TickPerMinute,
                DefaultBeatsPerScan,
                notes,
                diagnostics);
        }

        private static GameplayPreviewLaneRole GenericLaneRole(
            PlayerData model,
            TrackData track,
            int laneCount)
        {
            if (laneCount != 8)
            {
                return GameplayPreviewLaneRole.Regular;
            }

            if (model.SourceFormat == ChartFormat.TrailerRespectV)
            {
                if (track.Idx == 10) return GameplayPreviewLaneRole.ExtraButtonLeft;
                if (track.Idx == 11) return GameplayPreviewLaneRole.ExtraButtonRight;
                return GameplayPreviewLaneRole.Regular;
            }

            string channel;
            if (model.BmsMetadata != null &&
                model.BmsMetadata.TrackChannels.TryGetValue(track.Idx, out channel))
            {
                if (channel == "18" || channel == "28")
                    return GameplayPreviewLaneRole.ExtraButtonLeft;
                if (channel == "19" || channel == "29")
                    return GameplayPreviewLaneRole.ExtraButtonRight;
            }
            return GameplayPreviewLaneRole.Regular;
        }

        private static void AddRespectSideTrack(
            PlayerData model,
            IList<ProjectedGameplayNote> notes,
            RespectGameplayLayout layout,
            uint trackIndex,
            GameplayPreviewLaneRole role,
            RespectGameplayNoteType type)
        {
            TrackData track = model.Tracks.FirstOrDefault(item => item.Idx == trackIndex);
            if (track == null) return;

            foreach (EventData source in track.Events)
            {
                if (source.EventType != EventType.Note) continue;
                notes.Add(new ProjectedGameplayNote(source)
                {
                    Lane = role == GameplayPreviewLaneRole.SideTrackLeft ? 0 : 1,
                    LaneRole = role,
                    RespectType = type,
                    NativeX = layout == null ? 0 : layout.GetTrackX((int)trackIndex),
                    Kind = GameplayPreviewNoteKind.Generic,
                    Pulse = source.Tick,
                    DurationPulse = source.Duration,
                    ScanIndex = 0,
                    RelativeScan = 0.5,
                    X = 0.5,
                    Y = 0.5,
                    IsTopHalf = false
                });
            }
        }

        private static RespectGameplayNoteType RespectTypeForTrack(
            uint trackIndex,
            int laneCount)
        {
            switch (trackIndex)
            {
                case 2: return RespectGameplayNoteType.Analog;
                case 9: return RespectGameplayNoteType.Analog;
                case 10: return RespectGameplayNoteType.L1;
                case 11: return RespectGameplayNoteType.R1;
                case 12: return RespectGameplayNoteType.L2;
                case 13: return RespectGameplayNoteType.R2;
            }

            uint rightBlue = laneCount == 4 ? 5u : laneCount == 5 ? 6u : 7u;
            return trackIndex == 4 || trackIndex == rightBlue
                ? RespectGameplayNoteType.Blue
                : RespectGameplayNoteType.White;
        }

        private static List<TrackData> SelectGenericNoteTracks(PlayerData model)
        {
            Dictionary<uint, string> respectChannels =
                BmsChartSerializer.InferRespectTrackChannels(model);
            if (respectChannels != null && respectChannels.Count > 0)
            {
                return respectChannels
                    .OrderBy(pair => pair.Value, StringComparer.Ordinal)
                    .Select(pair => model.Tracks.FirstOrDefault(
                        track => track.Idx == pair.Key))
                    .Where(track => track != null)
                    .ToList();
            }

            if (model.BmsMetadata != null &&
                model.BmsMetadata.TrackChannels.Count > 0)
            {
                return model.BmsMetadata.TrackChannels
                    .Where(pair => IsBmsPlayableChannel(pair.Value))
                    .OrderBy(pair => pair.Value, StringComparer.Ordinal)
                    .Select(pair => model.Tracks.FirstOrDefault(
                        track => track.Idx == pair.Key))
                    .Where(track => track != null)
                    .ToList();
            }

            return model.Tracks
                .Where(track => track.Events.Any(
                    source => source.EventType == EventType.Note))
                .ToList();
        }

        private static bool IsBmsPlayableChannel(string channel)
        {
            if (string.IsNullOrEmpty(channel) || channel.Length != 2)
            {
                return false;
            }
            char family = channel[0];
            return family == '1' || family == '2' ||
                family == '5' || family == '6';
        }

        /// <summary>
        /// Which drawable kind an event is, or null when the playfield draws nothing for it.
        ///
        /// <para>
        /// Attribute 100 is the one that matters for "the other tracks do not show up": it is a
        /// keysound-only event, so a track full of them carries audio and no gameplay. Kept
        /// <c>internal</c> rather than private so <c>TrackCensusProbe</c> can count what the
        /// playfield would draw using this rule and not a second copy of it.
        /// </para>
        /// </summary>
        internal static GameplayPreviewNoteKind? Classify(EventData source)
        {
            if (source == null || source.EventType != EventType.Note ||
                source.Attribute == 100)
            {
                return null;
            }

            switch (source.Attribute)
            {
                case 0:
                    return source.Duration > 6
                        ? GameplayPreviewNoteKind.Drag
                        : GameplayPreviewNoteKind.Basic;
                case 5:
                    return GameplayPreviewNoteKind.ChainHead;
                case 6:
                    return GameplayPreviewNoteKind.ChainNode;
                case 10:
                    return source.Duration > 6
                        ? GameplayPreviewNoteKind.RepeatHeadHold
                        : GameplayPreviewNoteKind.RepeatHead;
                case 11:
                    return source.Duration > 6
                        ? GameplayPreviewNoteKind.RepeatHold
                        : GameplayPreviewNoteKind.Repeat;
                case 12:
                    return GameplayPreviewNoteKind.Hold;
                default:
                    return null;
            }
        }

        private static int TickToPulse(int tick, int ticksPerMeasure)
        {
            return (int)(((long)tick * PulsesPerMeasure) / Math.Max(1, ticksPerMeasure));
        }

        private static void ApplyChainFixups(
            IList<ProjectedGameplayNote> notes,
            IList<string> diagnostics)
        {
            bool open = false;
            int headPulse = -1;
            var implicitNodes = new List<ProjectedGameplayNote>();

            foreach (ProjectedGameplayNote note in notes)
            {
                if (note.Kind == GameplayPreviewNoteKind.ChainHead)
                {
                    open = true;
                    headPulse = note.Pulse;
                    implicitNodes.Clear();
                    continue;
                }

                if (note.Kind == GameplayPreviewNoteKind.ChainNode)
                {
                    if (!open)
                    {
                        diagnostics.Add("Orphan chain node at tick " + note.Source.Tick + ".");
                        continue;
                    }

                    foreach (ProjectedGameplayNote implicitNode in
                        implicitNodes.Where(node => node.Pulse == note.Pulse))
                    {
                        implicitNode.Kind = GameplayPreviewNoteKind.Basic;
                        implicitNode.IsImplicitChainNode = false;
                    }
                    open = false;
                    continue;
                }

                if (open && note.Kind == GameplayPreviewNoteKind.Basic &&
                    note.Pulse > headPulse)
                {
                    note.Kind = GameplayPreviewNoteKind.ChainNode;
                    note.IsImplicitChainNode = true;
                    implicitNodes.Add(note);
                }
            }

            if (open)
            {
                diagnostics.Add("Unclosed chain beginning at pulse " + headPulse + ".");
            }
        }

        private static void ApplyRepeatFixups(
            IList<ProjectedGameplayNote> notes,
            IList<string> diagnostics)
        {
            var openByLane = new bool[4];
            foreach (ProjectedGameplayNote note in notes)
            {
                if (note.Kind == GameplayPreviewNoteKind.RepeatHead ||
                    note.Kind == GameplayPreviewNoteKind.RepeatHeadHold)
                {
                    if (openByLane[note.Lane])
                    {
                        note.Kind = note.Kind == GameplayPreviewNoteKind.RepeatHeadHold
                            ? GameplayPreviewNoteKind.RepeatHold
                            : GameplayPreviewNoteKind.Repeat;
                    }
                    else
                    {
                        openByLane[note.Lane] = true;
                    }
                }
                else if (note.Kind == GameplayPreviewNoteKind.Repeat ||
                    note.Kind == GameplayPreviewNoteKind.RepeatHold)
                {
                    if (!openByLane[note.Lane])
                    {
                        diagnostics.Add(
                            "Orphan repeat node on lane " + note.Lane +
                            " at tick " + note.Source.Tick + ".");
                    }
                    openByLane[note.Lane] = false;
                }
            }

            for (int lane = 0; lane < openByLane.Length; lane++)
            {
                if (openByLane[lane])
                {
                    diagnostics.Add("Unclosed repeat series on lane " + lane + ".");
                }
            }
        }

        private static void ApplyEndOfScanMarkers(
            PlayerData model,
            IList<ProjectedGameplayNote> notes,
            int ticksPerMeasure)
        {
            foreach (TrackData track in model.Tracks)
            {
                if (track.Idx < 4 || track.Idx > 7) continue;
                int lane = (int)track.Idx - 4;
                foreach (EventData marker in track.Events)
                {
                    if (marker.EventType != EventType.Note) continue;
                    int pulse = TickToPulse(marker.Tick, ticksPerMeasure);
                    ProjectedGameplayNote match = notes.FirstOrDefault(
                        note => note.Lane == lane && note.Pulse == pulse);
                    if (match != null)
                    {
                        match.EndOfScan = true;
                    }
                }
            }
        }

        /// <summary>
        /// Note events on a source track, ignoring tempo and keysound rows. Used to size the DUO
        /// warning; see <see cref="LastLaneTrack"/>.
        /// </summary>
        private static int CountNotes(TrackData track)
        {
            int count = 0;
            foreach (EventData source in track.Events)
            {
                if (source.EventType == EventType.Note) count++;
            }
            return count;
        }

        private static int DeriveLaneCount(IEnumerable<ProjectedGameplayNote> notes)
        {
            bool lane2 = notes.Any(note => note.Lane == 2);
            bool lane3 = notes.Any(note => note.Lane == 3);
            if (!lane2 && !lane3) return 2;
            return lane3 ? 4 : 3;
        }

        private static void PlaceTechnikaNote(ProjectedGameplayNote note, int laneCount)
        {
            double floatScan = note.Pulse / (double)(PulsesPerBeat * DefaultBeatsPerScan);
            int intScan = (int)Math.Floor(floatScan);
            if (note.EndOfScan &&
                note.Kind != GameplayPreviewNoteKind.Drag &&
                note.Pulse > 0 &&
                note.Pulse % (PulsesPerBeat * DefaultBeatsPerScan) == 0)
            {
                intScan--;
            }

            double relative = floatScan - intScan;
            bool top = (intScan & 1) == 1;
            double travel = (ScanFieldRight - ScanFieldLeft) * relative;
            double laneHeight = (1.0 - 0.05 - 0.05) / laneCount;
            double localY = 0.05 + laneHeight * (note.Lane + 0.5);

            note.ScanIndex = intScan;
            note.RelativeScan = relative;
            note.IsTopHalf = top;

            // Both halves sweep the same rectangle - the upper one left to right, the lower one
            // right to left - so the lower half is the same window run backwards, not the upper
            // half's window reflected. See ScanFieldLeft: the field is 7 px left of centre, so
            // reflecting it (1 - x) would put every lower-half note 7 px off the arcade's.
            note.X = top ? ScanFieldLeft + travel : ScanFieldRight - travel;
            note.Y = top ? localY / 2.0 : 0.5 + localY / 2.0;
        }
    }
}
