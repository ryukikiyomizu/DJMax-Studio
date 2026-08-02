using System;
using System.Collections.Generic;
using System.Linq;
using DJMaxEditor.DJMax;
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
                "This format uses the generic lane preview.");
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
            return CreateFrame(currentTick, false);
        }

        /// <summary>
        /// Creates the small playback window used by the renderer. Unlike the
        /// diagnostic/full frame, it does not clone notes that cannot be drawn.
        /// </summary>
        public GameplayPreviewFrame CreateRenderableFrame(int currentTick)
        {
            return CreateFrame(currentTick, true);
        }

        private GameplayPreviewFrame CreateFrame(int currentTick, bool renderableOnly)
        {
            int ticks = Math.Max(1, (int)_ticksPerMeasure);
            double currentScan = Profile == GameplayPreviewProfile.Technika
                ? (4.0 * currentTick) / (ticks * Math.Max(1, _beatsPerScan))
                : currentTick / (double)ticks;
            int currentIntScan = (int)Math.Floor(currentScan);
            double currentPhase = currentScan - currentIntScan;
            var notes = new List<ProjectedGameplayNote>(Notes.Count);

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
                    if (note.ScanIndex < currentIntScan)
                    {
                        note.State = GameplayPreviewNoteState.Resolved;
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

                    double noteFloatScan =
                        note.Pulse / (240.0 * Math.Max(1, _beatsPerScan));
                    double distance = currentScan - noteFloatScan;
                    note.ApproachVisible = distance >= -0.5 && distance <= 0;
                    note.ApproachProgress = note.ApproachVisible
                        ? Math.Max(0, Math.Min(1, (distance + 0.5) / 0.5))
                        : 0;
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

            // Generic rendering maps two measures around the playhead into the
            // viewport, so notes outside that range cannot contribute pixels.
            return Math.Abs(note.Source.Tick - currentTick) <= ticks * 2;
        }
    }

    public static class GameplayPreviewProjector
    {
        private const int PulsesPerMeasure = 960;
        private const int PulsesPerBeat = 240;
        private const int DefaultBeatsPerScan = 4;

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

            foreach (TrackData track in model.Tracks)
            {
                if (track.Idx > 3) continue;
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
            List<TrackData> noteTracks = model.Tracks
                .Where(track => track.Events.Any(source => source.EventType == EventType.Note))
                .ToList();
            int laneCount = Math.Max(1, noteTracks.Count);

            for (int lane = 0; lane < noteTracks.Count; lane++)
            {
                foreach (EventData source in noteTracks[lane].Events)
                {
                    if (source.EventType != EventType.Note) continue;
                    notes.Add(new ProjectedGameplayNote(source)
                    {
                        Lane = lane,
                        Kind = GameplayPreviewNoteKind.Generic,
                        Pulse = source.Tick,
                        DurationPulse = source.Duration,
                        ScanIndex = 0,
                        RelativeScan = 0.5,
                        X = 0.5,
                        Y = (lane + 0.5) / laneCount,
                        IsTopHalf = false
                    });
                }
            }

            return new GameplayPreviewProjection(
                GameplayPreviewProfile.Generic,
                "GENERIC LANE PREVIEW  |  APPROXIMATION",
                laneCount,
                model.TickPerMinute,
                DefaultBeatsPerScan,
                notes,
                diagnostics);
        }

        private static GameplayPreviewNoteKind? Classify(EventData source)
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
            double baseX = 0.15 + ((1.0 - 0.10) - 0.15) * relative;
            double laneHeight = (1.0 - 0.05 - 0.05) / laneCount;
            double localY = 0.05 + laneHeight * (note.Lane + 0.5);

            note.ScanIndex = intScan;
            note.RelativeScan = relative;
            note.IsTopHalf = top;
            note.X = top ? baseX : 1.0 - baseX;
            note.Y = top ? localY / 2.0 : 0.5 + localY / 2.0;
        }
    }
}
