using System;
using System.Collections.Generic;
using System.Linq;
using DJMaxEditor.DJMax;
using DJMaxEditor.Files.bms;
using DJMaxEditor.Files.FormatDetection;
using DJMaxEditor.Files.Tech;

namespace DJMaxEditor.Preview
{
    public enum GameplayPreviewProfile
    {
        Generic,
        Technika
    }

    /// <summary>
    /// The arcade's scroll-direction effector for a TECHNIKA field.
    ///
    /// <para>
    /// The timeline sweeps the two halves clockwise by default - over the top half it travels
    /// left to right, over the bottom half right to left - and the pre-song effector screen
    /// offers three alternatives, present since TECHNIKA 1's mission courses and in both
    /// sequels (the Korean manuals name them CW / ACW / LL / RR):
    /// </para>
    /// <list type="bullet">
    /// <item><description><see cref="Clockwise"/> (CW): top left-to-right, bottom right-to-left.
    /// The default and the only direction charts are authored against.</description></item>
    /// <item><description><see cref="CounterClockwise"/> (ACW): the inverse reading - top
    /// right-to-left, bottom left-to-right.</description></item>
    /// <item><description><see cref="AllLeft"/> (LL): both halves sweep toward the left.</description></item>
    /// <item><description><see cref="AllRight"/> (RR): both halves sweep toward the right.</description></item>
    /// </list>
    /// <para>
    /// The scan a note belongs to and which half that scan fills are properties of the clock
    /// and never change; only the direction of travel inside the field does, which is why this
    /// is one enum feeding note placement, the scanline, hold bodies and the approach glow
    /// rather than a second projection. It is the one effector the manuals note can help a
    /// player's score, so it gets modelled even though the preview cannot be played.
    /// </para>
    /// </summary>
    public enum TechnikaScrollDirection
    {
        Clockwise,
        CounterClockwise,
        AllLeft,
        AllRight
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
        /// 131 non-zero durations. Gating a trail on a duration merely being non-zero therefore
        /// puts a stub behind all of them. Whether a note is held is a property of its kind, which
        /// is what the projector already classifies from the note attribute.
        /// </para>
        ///
        /// <para>
        /// <see cref="GameplayPreviewNoteKind.Drag"/> belongs here even so, and used not to. It is
        /// attribute 0 having already passed the same <c>Duration &gt; 6</c> gate the legacy editor
        /// uses everywhere it distinguishes a long note from a tap, so it is not the keysound
        /// artefact the paragraph above is about: across the 445-chart TECHNIKA 2 corpus only 1505
        /// of 95400 attribute-0 notes clear the gate, and the legacy TECHNIKA renderer draws every
        /// one of them as <c>longnote</c> over a <c>longnoteline</c> body. Excluding it was what
        /// left the green-yellow slide note drawn as a plain magenta tap.
        /// </para>
        /// </summary>
        public static bool HasHoldTrail(GameplayPreviewNoteKind kind)
        {
            return kind == GameplayPreviewNoteKind.Hold ||
                kind == GameplayPreviewNoteKind.Drag ||
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

            if (format == ChartFormat.TechmaniaTrack)
            {
                // A TECHMANIA track.tech is the fan TECHNIKA format by construction, so its
                // profile is not merely inferred - no confirmation prompt is warranted.
                return new GameplayPreviewProfileSuggestion(
                    GameplayPreviewProfile.Technika,
                    true,
                    "TECHMANIA track file; the TECHNIKA profile is certain.");
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
            double tempo,
            IList<ProjectedGameplayNote> notes,
            IList<string> diagnostics,
            TechnikaScrollDirection scrollDirection = TechnikaScrollDirection.Clockwise)
        {
            Profile = profile;
            StatusLabel = statusLabel;
            LaneCount = laneCount;
            _ticksPerMeasure = ticksPerMeasure;
            _beatsPerScan = beatsPerScan;
            ScrollDirection = scrollDirection;
            ScanSeconds = tempo > 0.0 ? (beatsPerScan * 60.0) / tempo : 0.0;
            Notes = new List<ProjectedGameplayNote>(notes).AsReadOnly();
            Diagnostics = new List<string>(diagnostics).AsReadOnly();
        }

        public GameplayPreviewProfile Profile { get; private set; }

        public string StatusLabel { get; private set; }

        public int LaneCount { get; private set; }

        /// <summary>
        /// Beats per scan the notes were placed with: four on real TECHNIKA .pt charts, and
        /// the value a TECHMANIA .tech declares (2, 4, 8, 12 ...). Renderers that lay trails
        /// or count beats per scan must read this rather than assume four.
        /// </summary>
        public int BeatsPerScan
        {
            get { return _beatsPerScan; }
        }

        /// <summary>
        /// The TECHNIKA scroll-direction effector this projection was placed under;
        /// <see cref="TechnikaScrollDirection.Clockwise"/> for the arcade default and for every
        /// non-TECHNIKA profile. Renderers read it to sweep the scanline, lay hold bodies and
        /// orient the approach glow the same way the notes were placed.
        /// </summary>
        public TechnikaScrollDirection ScrollDirection { get; private set; }

        /// <summary>
        /// How long one scan lasts at the chart's header tempo, in seconds, or 0 when the chart
        /// carries no usable tempo.
        ///
        /// <para>
        /// Nominal, and deliberately so: the scan grid itself is counted in ticks, so a chart that
        /// changes tempo mid-way has scans of different real lengths and no single number can be
        /// right for all of them. This is here for the one thing a renderer cannot do without it -
        /// playing an animation the arcade authored in seconds, such as the hit burst's 0.583 s, at
        /// close to its authored speed - and every such use is an approximation that the tick grid
        /// was already making.
        /// </para>
        /// </summary>
        public double ScanSeconds { get; private set; }

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
                    // Only a hold answers for its tail: every note carries a keysound-length
                    // duration, so reading the raw tail for a tap would keep it lit behind the
                    // sweep until its sample "ends". A tap is Resolved the instant the sweep
                    // clears its head, which is also what the hit-effect pass assumes.
                    double endFloatScan = GameplayPreviewNoteKinds.HasHoldTrail(note.Kind)
                        ? (note.Pulse + note.DurationPulse) / pulsesPerScan
                        : noteFloatScan;
                    double distance = currentScan - noteFloatScan;

                    if (currentScan > endFloatScan)
                    {
                        // Resolved the moment the sweep is past it, not at the end of the scan it
                        // sits in. A scan is several seconds wide, so the end-of-scan test left every
                        // note the line had already crossed sitting on the field at full brightness
                        // until the handover - a bar's worth of notes stacked up behind the line, in a
                        // game whose whole read is "the line is where now is".
                        note.State = GameplayPreviewNoteState.Resolved;
                    }
                    else if (note.ScanIndex < currentIntScan)
                    {
                        // Head behind, tail still ahead: a hold spanning into this scan or the
                        // next. It is still being played, so Active - the renderer draws the
                        // visible scans' worth of its body and skips the head it has passed.
                        note.State = GameplayPreviewNoteState.Active;
                    }
                    else if (note.ScanIndex == currentIntScan)
                    {
                        note.State = GameplayPreviewNoteState.Active;
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
                    // A held note is still being played while its tail is ahead, even though its
                    // head is behind - answering Resolved for the head would drop the tail the
                    // renderable window deliberately kept. A tap answers for its head alone, which
                    // is why the tail only counts past the codebase's long gate (Duration > 6, the
                    // same one Classify and the BMS codec use) rather than for any non-zero
                    // keysound-length duration.
                    bool held = note.Source.Duration > 6 && distance < 0 &&
                        distance + note.Source.Duration >= 0;
                    note.State = distance < 0 && !held
                        ? GameplayPreviewNoteState.Resolved
                        : Math.Abs(distance) <= Math.Max(1, ticks / 16) || held
                            ? GameplayPreviewNoteState.Active
                            : GameplayPreviewNoteState.Prepare;
                    note.X = Math.Max(0.05, Math.Min(0.95,
                        0.5 + (distance / (double)(ticks * 2))));

                    if (respectLayout != null &&
                        note.RespectType != RespectGameplayNoteType.None)
                    {
                        note.NativeY = respectLayout.GetNoteY(
                            note.Source.Tick, currentTick, noteSpeed);
                        // Taps carry a keysound-length duration, not a tail: duration 6 is the
                        // format's "not a long note" sentinel, yet 6 x speed still clears the
                        // default head height - which grew every tap a phantom dim body plus a
                        // bright tail cap, the "two notes" look. The same Duration > 6 long gate
                        // the state computation above uses; real holds keep their bodies.
                        int tailDuration = note.Source.Duration > 6
                            ? note.Source.Duration
                            : 0;
                        note.NativeHeight = respectLayout.GetLongNoteHeight(
                            tailDuration,
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
                // The Technika renderer draws this scan plus the one already waiting on the
                // other half - two float scans on stage. A hold belongs to the window while any
                // part of its span intersects them: its head may be scans behind while its tail
                // is still ahead, and testing the head alone is what clipped a long hold at the
                // scan past it. The far edge sits at the start of the scan AFTER the waiting
                // one (float scan current+2); an edge at current+1 admitted only notes exactly
                // on the handover boundary, so the waiting half showed just its first note and
                // swallowed every other one until the sweep reached it. Taps answer for their
                // head alone (see CreateFrame for why the raw duration is not a tail).
                double pulsesPerScan = 240.0 * Math.Max(1, _beatsPerScan);
                double headFloatScan = note.Pulse / pulsesPerScan;
                double tailFloatScan = GameplayPreviewNoteKinds.HasHoldTrail(note.Kind)
                    ? (note.Pulse + note.DurationPulse) / pulsesPerScan
                    : headFloatScan;
                return tailFloatScan >= currentIntScan &&
                    headFloatScan < currentIntScan + 2;
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
        /// Beats per scan for the projection. Real TECHNIKA .pt charts are always four; a
        /// TECHMANIA .tech declares its own (2, 4, 8, 12 ...) in pattern metadata, and the
        /// scan boundary is what places every note, so a fixed four would pack a 2-bps chart
        /// into half-length scans. Anything that declares none keeps the four-beat default.
        /// </summary>
        private static int BeatsPerScanFor(PlayerData model)
        {
            int bps = model != null && model.TechMetadata != null
                ? model.TechMetadata.Bps
                : DefaultBeatsPerScan;
            return bps > 0 ? bps : DefaultBeatsPerScan;
        }

        /// <summary>
        /// Whether the timeline sweeps left-to-right (true) or right-to-left (false) over the
        /// named half under a scroll-direction effector. See
        /// <see cref="TechnikaScrollDirection"/> for the four arcade readings; this is the one
        /// rule both the projector's note placement and every renderer-side sweep (scanline,
        /// hold body, approach glow) answer to, so they cannot disagree about which way a scan
        /// runs.
        /// </summary>
        public static bool TechnikaSweepRightward(
            bool isTopHalf,
            TechnikaScrollDirection direction)
        {
            switch (direction)
            {
                case TechnikaScrollDirection.CounterClockwise:
                    // The inverse of the clockwise default: top runs right-to-left, bottom
                    // left-to-right.
                    return !isTopHalf;
                case TechnikaScrollDirection.AllLeft:
                    // Both halves travel toward the left edge.
                    return false;
                case TechnikaScrollDirection.AllRight:
                    // Both halves travel toward the right edge.
                    return true;
                default:
                    // Clockwise: top left-to-right, bottom right-to-left.
                    return isTopHalf;
            }
        }

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
            return Project(model, profile, TechnikaScrollDirection.Clockwise);
        }

        /// <summary>
        /// Projects a chart, applying a scroll-direction effector to the TECHNIKA field. The
        /// direction changes where every note sits inside its scan, so it is applied here at
        /// placement time rather than as a renderer-side mirror; a Generic projection ignores
        /// it, because the vertical gear has no such effector.
        /// </summary>
        public static GameplayPreviewProjection Project(
            PlayerData model,
            GameplayPreviewProfile profile,
            TechnikaScrollDirection scrollDirection)
        {
            if (model == null) throw new ArgumentNullException("model");
            return profile == GameplayPreviewProfile.Technika
                ? ProjectTechnika(model, scrollDirection)
                : ProjectGeneric(model);
        }

        private static GameplayPreviewProjection ProjectTechnika(
            PlayerData model,
            TechnikaScrollDirection scrollDirection)
        {
            int ticksPerMeasure = Math.Max(1, (int)model.TickPerMinute);
            var diagnostics = new List<string>();
            var notes = new List<ProjectedGameplayNote>();
            int secondPlayerNotes = 0;

            // A .tech declares how many lanes are playable. Notes on later lanes (including
            // the compacted overflow tracks 9+, which are already skipped below) are the
            // format's invisible/autoplay keysound lanes: they still trigger audio in game
            // but are never drawn, and letting one widen the field would misdraw every note.
            int playableLanes = model.TechMetadata != null &&
                                model.TechMetadata.PlayableLanes >= 2
                ? Math.Min(4, model.TechMetadata.PlayableLanes)
                : 0;

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
                    if (playableLanes > 0 && (int)track.Idx >= playableLanes)
                    {
                        continue;
                    }
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

            // A .tech's declared playable lanes win; anything else (legacy .pt charts)
            // keeps deriving the count from the lanes the notes actually use.
            int laneCount = playableLanes > 0
                ? playableLanes
                : DeriveLaneCount(notes);
            int beatsPerScan = BeatsPerScanFor(model);
            foreach (ProjectedGameplayNote note in notes)
            {
                PlaceTechnikaNote(note, laneCount, beatsPerScan, scrollDirection);
            }

            return new GameplayPreviewProjection(
                GameplayPreviewProfile.Technika,
                "TECHNIKA PROFILE  |  CONFIRMED TWO-WAY PROJECTION",
                laneCount,
                model.TickPerMinute,
                BeatsPerScanFor(model),
                model.Tempo,
                notes,
                diagnostics,
                scrollDirection);
        }

        private static GameplayPreviewProjection ProjectGeneric(PlayerData model)
        {
            var diagnostics = new List<string>();
            var notes = new List<ProjectedGameplayNote>();
            List<TrackData> noteTracks = SelectGenericNoteTracks(model);
            int laneCount = Math.Max(1, noteTracks.Count);
            bool hasShoulders = false;
            if (model.SourceFormat == ChartFormat.TrailerRespectV && noteTracks.Count > 0)
            {
                // The gear mode comes from the mains alone: 8B is 6 mains with shoulders, and
                // the 4BFX / 5BFX mission modes are 4 / 5 mains with shoulders, so counting the
                // shoulder tracks as lanes puts a 4BFX chart on the 6-lane pitch. The shoulders
                // are overlay bars on the mains' gear, not lanes of their own - see
                // docs/respectv-playfield-research.md.
                int mains = Math.Max(4, Math.Min(6, noteTracks.Count(
                    track => track.Idx >= 3 && track.Idx <= 8)));
                hasShoulders = noteTracks.Any(
                    track => track.Idx == 10 || track.Idx == 11);
                laneCount = hasShoulders && mains == 6 ? 8 : mains;
            }
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
                // L2/R2 exist only on XB, which is 8B with more shoulders - so they are gated
                // on the shoulders being there, not on the lane count reading 8.
                if (hasShoulders)
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
                model.Tempo,
                notes,
                diagnostics);
        }

        private static GameplayPreviewLaneRole GenericLaneRole(
            PlayerData model,
            TrackData track,
            int laneCount)
        {
            // Trailer track ids are authoritative: 10/11 are the shoulder bars in every mode
            // that has them, including 4BFX/5BFX, so this is not gated on the lane count. (It
            // was, and the gate is what drew 4BFX shoulders as overlapping white lane notes.)
            if (model.SourceFormat == ChartFormat.TrailerRespectV)
            {
                if (track.Idx == 10) return GameplayPreviewLaneRole.ExtraButtonLeft;
                if (track.Idx == 11) return GameplayPreviewLaneRole.ExtraButtonRight;
                return GameplayPreviewLaneRole.Regular;
            }

            if (laneCount != 8)
            {
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
            // Pass 1 - spans, delimited purely from the explicit typing. A .tech chains one
            // ChainHead through every ChainNode that follows (the waypoints cross lanes), and
            // the next head starts the next span; there is no single closing node. The legacy
            // dialect's span ends at its one node. Streaming "first node closes" chopped a real
            // dozen-node chain into a pair plus eleven orphans - and closing on any other-family
            // note killed a chain that shares a tick with, say, a repeat head in another lane.
            var heads = new List<ProjectedGameplayNote>();
            var spanEnd = new List<int>();
            var spanNodePulses = new List<HashSet<int>>();
            int openSpan = -1;
            foreach (ProjectedGameplayNote note in notes)
            {
                if (note.Kind == GameplayPreviewNoteKind.ChainHead)
                {
                    heads.Add(note);
                    spanEnd.Add(note.Pulse);
                    spanNodePulses.Add(new HashSet<int>());
                    openSpan = heads.Count - 1;
                }
                else if (note.Kind == GameplayPreviewNoteKind.ChainNode)
                {
                    if (openSpan < 0)
                    {
                        diagnostics.Add("Orphan chain node at tick " + note.Source.Tick + ".");
                        continue;
                    }
                    spanEnd[openSpan] = note.Pulse;
                    spanNodePulses[openSpan].Add(note.Pulse);
                }
            }

            // Pass 2 - the legacy dialect traces its path through ordinary taps that the file
            // never re-tagged: absorb the basics strictly inside a span, except taps on a
            // node's own pulse, which are real taps in another lane sharing the waypoint's tick.
            openSpan = -1;
            int noteIndex = 0;
            foreach (ProjectedGameplayNote note in notes)
            {
                if (note.Kind == GameplayPreviewNoteKind.ChainHead)
                {
                    // The heads list follows the same walk order, so a pointer is enough.
                    while (noteIndex < heads.Count && heads[noteIndex] != note)
                    {
                        noteIndex++;
                    }
                    openSpan = noteIndex < heads.Count ? noteIndex : -1;
                    noteIndex++;
                    continue;
                }

                if (note.Kind == GameplayPreviewNoteKind.ChainNode)
                {
                    // Explicit waypoint: nothing to absorb or hand back here.
                    continue;
                }

                if (openSpan >= 0 && note.Kind == GameplayPreviewNoteKind.Basic &&
                    note.Pulse > heads[openSpan].Pulse &&
                    note.Pulse <= spanEnd[openSpan] &&
                    !spanNodePulses[openSpan].Contains(note.Pulse))
                {
                    note.Kind = GameplayPreviewNoteKind.ChainNode;
                    note.IsImplicitChainNode = true;
                }
            }

            for (int i = 0; i < heads.Count; i++)
            {
                if (spanEnd[i] == heads[i].Pulse)
                {
                    diagnostics.Add(
                        "Unclosed chain beginning at pulse " + heads[i].Pulse + ".");
                }
            }
        }

        private static void ApplyRepeatFixups(
            IList<ProjectedGameplayNote> notes,
            IList<string> diagnostics)
        {
            var openByLane = new bool[4];
            // The legacy dialect tags the single closing tick with Repeat/RepeatHold, while a
            // .tech series names every post-head marker Repeat (a held RepeatHold may sit among
            // them). An end marker therefore joins the series but does not close it; only a fresh
            // head or a non-repeat note on the same lane does.
            var endSeenByLane = new bool[4];
            foreach (ProjectedGameplayNote note in notes)
            {
                if (note.Kind == GameplayPreviewNoteKind.RepeatHead ||
                    note.Kind == GameplayPreviewNoteKind.RepeatHeadHold)
                {
                    if (openByLane[note.Lane] && !endSeenByLane[note.Lane])
                    {
                        // Legacy intermediate ticks keep the head attribute until the end marker.
                        note.Kind = note.Kind == GameplayPreviewNoteKind.RepeatHeadHold
                            ? GameplayPreviewNoteKind.RepeatHold
                            : GameplayPreviewNoteKind.Repeat;
                    }
                    else
                    {
                        openByLane[note.Lane] = true;
                        endSeenByLane[note.Lane] = false;
                    }
                }
                else if (note.Kind == GameplayPreviewNoteKind.Repeat ||
                    note.Kind == GameplayPreviewNoteKind.RepeatHold)
                {
                    if (openByLane[note.Lane])
                    {
                        endSeenByLane[note.Lane] = true;
                    }
                    else
                    {
                        diagnostics.Add(
                            "Orphan repeat node on lane " + note.Lane +
                            " at tick " + note.Source.Tick + ".");
                    }
                }
                else if (note.Lane >= 0 && note.Lane < openByLane.Length &&
                    openByLane[note.Lane])
                {
                    // A repeat never leaves its lane, so only a same-lane note closes the series;
                    // anything happening in other lanes is irrelevant.
                    openByLane[note.Lane] = false;
                    endSeenByLane[note.Lane] = false;
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

        private static void PlaceTechnikaNote(
            ProjectedGameplayNote note,
            int laneCount,
            int beatsPerScan,
            TechnikaScrollDirection scrollDirection)
        {
            double pulsesPerScan = PulsesPerBeat * Math.Max(1, beatsPerScan);
            double floatScan = note.Pulse / pulsesPerScan;
            int intScan = (int)Math.Floor(floatScan);
            if (note.EndOfScan &&
                note.Kind != GameplayPreviewNoteKind.Drag &&
                note.Pulse > 0 &&
                note.Pulse % pulsesPerScan == 0)
            {
                intScan--;
            }

            double relative = floatScan - intScan;
            bool top = (intScan & 1) == 1;
            bool rightward = TechnikaSweepRightward(top, scrollDirection);
            double travel = (ScanFieldRight - ScanFieldLeft) * relative;
            double laneHeight = (1.0 - 0.05 - 0.05) / laneCount;
            double localY = 0.05 + laneHeight * (note.Lane + 0.5);

            note.ScanIndex = intScan;
            note.RelativeScan = relative;
            note.IsTopHalf = top;

            // Both halves sweep the same rectangle; under the clockwise default the upper one
            // runs left to right and the lower one right to left, so the lower half is the same
            // window run backwards, not the upper half's window reflected. See ScanFieldLeft:
            // the field is 7 px left of centre, so reflecting it (1 - x) would put every
            // leftward-sweeping note 7 px off the arcade's. The scroll effector only changes
            // which way that run goes, which TechnikaSweepRightward answers for all four modes.
            note.X = rightward ? ScanFieldLeft + travel : ScanFieldRight - travel;
            note.Y = top ? localY / 2.0 : 0.5 + localY / 2.0;
        }
    }
}
