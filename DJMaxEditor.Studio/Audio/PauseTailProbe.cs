using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using DJMaxEditor.DJMax;
using DJMaxEditor.Studio.Preview;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DJMaxEditor.Studio.Audio
{
    /// <summary>
    /// Measures how long a sound keeps coming out of the speakers after a transport edge, on the
    /// real endpoint, by capturing the endpoint's own mix through WASAPI loopback.
    ///
    /// <para>
    /// Five rounds of "the hold sound is still there when I pause" were answered with headless
    /// numbers, and headless numbers structurally could not see this one. <see cref="NullAudioOutput"/>
    /// hands the graph a genuine <c>float[]</c>; a real device does not. NAudio's
    /// <c>SampleToWaveProvider</c> re-types the driver's <c>byte[]</c> as a <c>float[]</c> through
    /// <c>WaveBuffer</c>'s union, so anything that asks the runtime about the array - a range length,
    /// a <c>Length</c>, an <c>Array.Clear</c> - is off by the four bytes in a sample. The paused graph
    /// was clearing a quarter of every buffer and handing the endpoint the rest of the last unpaused
    /// one. Every existing pause case stayed green while the editor squealed, which is precisely why
    /// this mode exists: it measures where the lie lives.
    /// </para>
    ///
    /// <para>
    /// Loopback capture taps the engine's mixed output, which is exactly the audio the DAC receives,
    /// so "the last frame above the threshold" is the last frame the ear could have heard. What it
    /// does not give is a shared clock: the capture is delivered some unknown delay <c>D</c> behind
    /// the engine, so a tail measured from the keypress reads <c>queue + D</c> rather than
    /// <c>queue</c>. The probe calibrates that out by measuring the <i>onset</i> of the same trial -
    /// silence to sound, which reads <c>queue + D</c> as well - and by running all five edges in one
    /// capture so their differences are exact whatever <c>D</c> is.
    /// </para>
    ///
    /// <para>
    /// The edges are the two the owner actually uses and three they do not, because the report is a
    /// comparison: Escape (a hard <c>StopAllSounds</c>) was described as working and Space
    /// (<c>FadeOutAndSilence</c>) was not. They measured 103 ms against 503 ms, which is what made the
    /// complaint a number; they now measure 112 ms against 130 ms, and the 18 ms is the anti-click
    /// ramp plus one buffer.
    /// </para>
    ///
    /// <para>
    /// Every trial now strikes its holds <em>twice</em>, the second time half way through, because
    /// <see cref="NAudioKeysoundPlayer.AllowOverlappingRetrigger"/> is on by default: a retrigger no
    /// longer stops the voice it displaces, it detaches it and leaves it ringing out on no channel at
    /// all. That is a voice no per-channel call can reach, so a pause has to silence it through
    /// <c>SilenceVoicesLocked</c>'s sweep of the detached set or the tail this probe measures grows by
    /// the whole remainder of a keysound. Struck once per channel, as this probe used to do, no
    /// detached voice ever existed and the measurement could not see that. The <c>detached</c> column
    /// says how many were on the air when each edge was taken, so a tail of 0 ms is now a claim about
    /// them too.
    /// </para>
    /// </summary>
    internal static class PauseTailProbe
    {
        /// <summary>Command-line switch that selects this mode.</summary>
        public const string Switch = "--pause-tail";

        /// <summary>How long the holds are left sounding before the edge.</summary>
        private const double DefaultHoldSeconds = 1.2;

        /// <summary>How long the capture keeps running after the edge, looking for the tail.</summary>
        private const double TailWindowSeconds = 0.5;

        /// <summary>Settling time either side of a trial, so one trial's tail cannot leak into the next.</summary>
        private const double SettleSeconds = 0.35;

        /// <summary>
        /// Audible threshold, linear. -60 dBFS: below the noise floor of anything a chart plays and
        /// well above the arithmetic floor of a 32-bit float engine mix, so a frame over it is sound
        /// rather than dither.
        /// </summary>
        private const float AudibleThreshold = 0.001f;

        /// <summary>How many long keysounds are struck at once, standing in for a dense bar of holds.</summary>
        private const int HoldVoices = 6;

        /// <summary>
        /// Where in the hold the same channels are struck a second time, as a fraction of it. Half
        /// way: late enough that the first voice is unmistakably still sounding when it loses its
        /// channel, early enough that the voice which replaces it is still sounding at the edge, so
        /// both an attached and a detached voice are on the air when the transport is asked to stop.
        /// </summary>
        private const double RetriggerAtFraction = 0.5;

        /// <summary>The transport edges under test, in the order they are run.</summary>
        private enum Edge
        {
            /// <summary>Escape: <c>StopAllSounds</c>, a hard cut with no ramp. The owner's workaround.</summary>
            Stop,

            /// <summary>Space as it ships: <c>FadeOutAndSilence</c>, 8 ms ramp then teardown.</summary>
            Pause,

            /// <summary>
            /// Space, then <c>FlushDeviceTail</c>: the control that discards the driver queue outright.
            /// Round five's attempted fix, kept as a baseline - if a plain pause and a pause with the
            /// queue thrown away measure the same, what is left after a pause is not queued audio.
            /// </summary>
            PauseFlush,

            /// <summary>The FMOD-parity freeze, for reference - not on any key.</summary>
            Freeze,

            /// <summary>
            /// The control: the same pause, with nothing struck. The stream is open and feeding zeros
            /// throughout, exactly as it is after any other edge once the ramp has finished.
            /// <para>
            /// Without this the tail cannot be attributed. A loopback capture takes the whole
            /// endpoint's mix, so anything else on the machine lands in it, and a capture that returns
            /// stale bytes instead of honouring the silence flag would look identical to a real leak.
            /// If this trial is silent while <see cref="Pause"/> is not, the difference was made by the
            /// voices that were sounding and the leak is ours.
            /// </para>
            /// </summary>
            PauseIdle
        }

        private sealed class Trial
        {
            public Edge Edge;
            public int StrikeMark;
            public int EdgeMark;
            public int EndMark;
            public long DeviceStops;
            public long DeviceStarts;
            public long FadeOuts;
            public long Teardowns;

            /// <summary>
            /// Voices that had lost their channel to the mid-hold retrigger and were still sounding
            /// when the edge was taken. Zero here means the trial never tested the detached path and
            /// its tail says nothing about it - see
            /// <see cref="NAudioKeysoundPlayer.RingingOutVoiceCount"/>.
            /// </summary>
            public int RingingOutAtEdge;

            /// <summary>
            /// Loudest sample that left the mixer graph from 100 ms after the edge to the end of the
            /// window - the one number that says whether a tail is ours.
            /// <para>
            /// The graph is silent by construction after a pause: the group clears its buffer and
            /// never reads the mixer. If this is at the floor while the endpoint is still sounding,
            /// then the audio the ear gets is not being produced here and no change to the graph can
            /// remove it, which is a completely different bug from the one four rounds have been
            /// chasing. If it is loud, the graph is lying and the fault is upstream of the driver.
            /// </para>
            /// </summary>
            public float GraphPeakLate;

            /// <summary>Buffers the device pulled during that same window. 0 means it was not pulling at all.</summary>
            public long BuffersLate;

            /// <summary>How many of those were cleared because the group was paused.</summary>
            public long PausedLate;

            /// <summary>Loudest byte handed to NAudio in the same window - the last thing we can see.</summary>
            public float HandoffPeakLate;

            /// <summary>Reads the device took in the window, and how many came back short of full length.</summary>
            public long HandoffReads;

            public long HandoffShortReads;

            /// <summary>How many of those reads were pure digital silence.</summary>
            public long HandoffSilentReads;

            /// <summary>See <see cref="DeviceHandoffMeter.FirstSoundingRead"/>.</summary>
            public string HandoffDetail;

            /// <summary>The same four numbers one stage earlier, in floats, straight off the group.</summary>
            public float TapPeakLate;

            public long TapReads;

            public long TapShortReads;

            public long TapSilentReads;

            public string TapDetail;

            /// <summary>The group's own account of the last buffer of the window. See PlaybackGroupSampleProvider.Trace.</summary>
            public string GroupDetail;

            /// <summary>The ten buffers before it, in order. See PlaybackGroupSampleProvider.RecentReads.</summary>
            public string GroupRecent;
        }

        /// <summary>
        /// Runs the probe. 0 when every trial was measured; non-zero when the chart, the keysounds,
        /// the device or the loopback capture made the measurement impossible - each with its own
        /// code, because "no number" and "a bad number" have to be told apart from a script.
        /// </summary>
        public static int Run(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine(
                    Switch + " <chart> <output-directory> [hold-seconds] [ringout|hardstop]");
                return 2;
            }

            string chartPath = args[1];
            string outputDirectory = args[2];
            double holdSeconds = DefaultHoldSeconds;
            if (args.Length > 3)
            {
                double parsed;
                if (double.TryParse(args[3], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out parsed) && parsed > 0.1)
                {
                    holdSeconds = parsed;
                }
            }

            // The retrigger rule, forceable, so the same session shape can be measured both ways and
            // the pause tail with detached voices on the air can be compared against the pause tail
            // without them instead of against a number from an earlier run on an earlier build. The
            // absolute tails move by tens of milliseconds between runs - they carry the loopback
            // capture's own delay - so only a comparison inside one build says anything.
            bool overlap = true;
            if (args.Length > 4 &&
                string.Equals(args[4], "hardstop", StringComparison.OrdinalIgnoreCase))
            {
                overlap = false;
            }

            var report = new StringBuilder();
            report.Append("chart: ").AppendLine(chartPath);
            report.Append("hold seconds: ")
                .Append(holdSeconds.ToString("F2", CultureInfo.InvariantCulture)).AppendLine();
            report.Append("retrigger: ")
                .AppendLine(overlap ? "rings out (shipped default)" : "hard stops");
            report.Append("audible threshold: ").Append(Decibels(AudibleThreshold)).AppendLine();

            PlayerData model = PlayfieldProbe.Open(chartPath, report);
            if (model == null)
            {
                Write(outputDirectory, report);
                return 3;
            }

            return Measure(model, chartPath, outputDirectory, holdSeconds, overlap, report);
        }

        private static int Measure(PlayerData model, string chartPath, string outputDirectory,
            double holdSeconds, bool overlap, StringBuilder report)
        {
            Directory.CreateDirectory(outputDirectory);

            Edge[] edges = { Edge.Stop, Edge.Pause, Edge.PauseFlush, Edge.Freeze, Edge.PauseIdle };
            double trialSeconds = SettleSeconds * 2.0 + holdSeconds + TailWindowSeconds;
            double captureSeconds = trialSeconds * edges.Length + 3.0;

            using (var player = new NAudioKeysoundPlayer())
            {
                player.Log = message => report.Append("player: ").AppendLine(message);
                player.GraphTracing = true;
                player.AllowOverlappingRetrigger = overlap;

                IAudioOutput output = player.Output;
                report.Append("output: ").Append(output == null ? "(none)" : output.Name)
                    .Append(", requested latency ")
                    .Append(output == null ? 0 : output.LatencyMilliseconds).AppendLine(" ms");

                // Worth stating, because a graph rate that differs from the endpoint's means the
                // shared-mode path inserts a resampler between the two, and a resampler is another
                // place a tail can live that no counter in the graph can see.
                report.Append("graph format: ").Append(player.WaveFormat.SampleRate)
                    .Append(" Hz, ").Append(player.WaveFormat.Channels).AppendLine(" ch");

                if (output == null || !output.IsRealDevice)
                {
                    // The whole point is the driver queue. Without a driver there is nothing to
                    // measure and a zero here would be a lie rather than a pass.
                    report.AppendLine("no real device - this probe measures the driver queue and cannot run headless");
                    Write(outputDirectory, report);
                    return 6;
                }

                List<uint> holds = LoadHolds(player, model, chartPath, report);
                if (holds.Count == 0)
                {
                    report.AppendLine("no keysound decoded - nothing to hold");
                    Write(outputDirectory, report);
                    return 5;
                }

                using (var recorder = new LoopbackRecorder(captureSeconds))
                {
                    if (!recorder.Start(report))
                    {
                        Write(outputDirectory, report);
                        return 7;
                    }

                    // The endpoint's engine has to be running before the first mark, or the first
                    // trial's onset is measured against a clock that had not started.
                    Thread.Sleep(500);

                    var trials = new List<Trial>();
                    DeviceHandoffMeter handoff = (output as NAudioDeviceOutput)?.Handoff;
                    GraphTapSampleProvider tap = (output as NAudioDeviceOutput)?.Tap;
                    report.Append("handoff chain: ")
                        .AppendLine(handoff == null ? "(not metered)" : handoff.SourceName);
                    foreach (Edge edge in edges)
                    {
                        trials.Add(RunTrial(player, recorder, holds, edge, holdSeconds, handoff, tap));
                    }

                    recorder.Stop();
                    Report(recorder, trials, report);
                    recorder.WriteWav(Path.Combine(outputDirectory, "pause-tail-loopback.wav"), report);
                }
            }

            Write(outputDirectory, report);
            return 0;
        }

        /// <summary>
        /// Strikes the holds, waits, takes the edge and waits again, marking the capture at each
        /// step. Every trial starts and ends at silence with the device running, so a trial can only
        /// measure its own tail.
        /// </summary>
        private static Trial RunTrial(NAudioKeysoundPlayer player, LoopbackRecorder recorder,
            List<uint> holds, Edge edge, double holdSeconds, DeviceHandoffMeter handoff,
            GraphTapSampleProvider tap)
        {
            // Also the un-pause and the device restart, for whatever the previous trial left behind.
            player.StopAllSounds();
            Sleep(SettleSeconds);

            var trial = new Trial { Edge = edge, StrikeMark = recorder.Mark() };

            if (edge != Edge.PauseIdle)
            {
                StrikeHolds(player, holds);
            }

            // Half way through, the same channels again. With overlapping retrigger on - the shipped
            // default - that leaves the first six voices detached and ringing out while six fresh ones
            // hold the channels, which is the state the pause has to cope with and the state this
            // probe could not previously produce.
            double toRetrigger = holdSeconds * RetriggerAtFraction;
            Sleep(toRetrigger);
            if (edge != Edge.PauseIdle)
            {
                StrikeHolds(player, holds);
            }

            Sleep(holdSeconds - toRetrigger);
            trial.RingingOutAtEdge = player.RingingOutVoiceCount;
            trial.EdgeMark = recorder.Mark();

            switch (edge)
            {
                case Edge.Stop:
                    player.StopAllSounds();
                    break;
                case Edge.Pause:
                case Edge.PauseIdle:
                    player.FadeOutAndSilence();
                    break;
                case Edge.PauseFlush:
                    player.FadeOutAndSilence();
                    player.FlushDeviceTail();
                    break;
                default:
                    player.SetAllPaused(true);
                    break;
            }

            // The graph's own view of the same window, and the reason this round can name a
            // mechanism instead of another candidate. Meters are reset twice: once at the edge, then
            // again after 100 ms, so what is finally read covers only [edge + 100 ms, edge + 500 ms].
            // The 8 ms ramp is long gone by then, so a paused graph must read the floor here. If the
            // endpoint is still sounding while this is at the floor, the tail is downstream of us.
            player.ResetMeters();
            Sleep(0.1);
            player.ResetMeters();
            handoff?.Reset();
            tap?.Reset();
            long buffersAtSettle = player.BuffersRendered;
            long pausedAtSettle = player.PausedBuffers;

            Sleep(TailWindowSeconds - 0.1);
            trial.GraphPeakLate = player.PeakOut;
            trial.BuffersLate = player.BuffersRendered - buffersAtSettle;
            trial.PausedLate = player.PausedBuffers - pausedAtSettle;
            if (handoff != null)
            {
                trial.HandoffPeakLate = handoff.Peak;
                trial.HandoffReads = handoff.Reads;
                trial.HandoffShortReads = handoff.ShortReads;
                trial.HandoffSilentReads = handoff.SilentReads;
                trial.HandoffDetail = handoff.FirstSoundingRead;
            }

            if (tap != null)
            {
                trial.TapPeakLate = tap.Peak;
                trial.TapReads = tap.Reads;
                trial.TapShortReads = tap.ShortReads;
                trial.TapSilentReads = tap.SilentReads;
                trial.TapDetail = tap.FirstSoundingRead;
            }

            trial.GroupDetail = player.LastGraphRead;
            trial.GroupRecent = player.RecentGraphReads;
            trial.EndMark = recorder.Mark();
            trial.DeviceStops = player.DeviceStops;
            trial.DeviceStarts = player.DeviceStarts;
            trial.FadeOuts = player.FadeOutsAnnounced;
            trial.Teardowns = player.FadeTeardowns;
            return trial;
        }

        private static void Sleep(double seconds)
        {
            Thread.Sleep((int)Math.Round(seconds * 1000.0));
        }

        /// <summary>
        /// Strikes every hold voice, one per channel and in channel order, so that calling this twice
        /// retriggers each of them rather than adding voices beside them.
        /// </summary>
        private static void StrikeHolds(NAudioKeysoundPlayer player, List<uint> holds)
        {
            for (int i = 0; i < holds.Count; i++)
            {
                player.PlaySound((uint)i, holds[i], 0.7f, 64);
            }
        }

        /// <summary>
        /// Decodes the chart's keysounds and returns the slots of the longest ones.
        /// <para>
        /// Longest rather than first, because the report is about <i>holds</i>: a tap that has already
        /// decayed by the time the key goes down leaves no tail to measure whatever the driver does,
        /// so a probe built on taps would pass no matter how deep the queue is.
        /// </para>
        /// </summary>
        private static List<uint> LoadHolds(NAudioKeysoundPlayer player, PlayerData model,
            string chartPath, StringBuilder report)
        {
            var holds = new List<uint>();
            string directory = Path.GetDirectoryName(chartPath);
            if (model.Instruments == null || string.IsNullOrEmpty(directory))
            {
                return holds;
            }

            var lengths = new List<KeyValuePair<uint, double>>();
            int loaded = 0;
            for (int i = 0; i < model.Instruments.Count; i++)
            {
                InstrumentData instrument = model.Instruments[i];
                if (instrument == null || instrument.InsNum == 0 || string.IsNullOrEmpty(instrument.Name))
                {
                    continue;
                }

                if (!player.LoadSound(instrument.InsNum, Path.Combine(directory, instrument.Name), 0))
                {
                    continue;
                }

                loaded++;
                lengths.Add(new KeyValuePair<uint, double>(
                    instrument.InsNum, player.SampleLengthMilliseconds(instrument.InsNum)));
            }

            lengths.Sort((a, b) => b.Value.CompareTo(a.Value));
            report.Append("keysounds: loaded=").Append(loaded).AppendLine();

            for (int i = 0; i < lengths.Count && holds.Count < HoldVoices; i++)
            {
                holds.Add(lengths[i].Key);
                report.Append("  hold voice ").Append(holds.Count).Append(": slot ")
                    .Append(lengths[i].Key).Append(", ")
                    .Append(lengths[i].Value.ToString("F0", CultureInfo.InvariantCulture))
                    .AppendLine(" ms");
            }

            return holds;
        }

        /// <summary>
        /// Turns the marks into the four numbers that matter per edge, then states the comparison the
        /// round is actually about.
        /// </summary>
        private static void Report(LoopbackRecorder recorder, List<Trial> trials, StringBuilder report)
        {
            int rate = recorder.SampleRate;
            report.AppendLine();
            report.Append("capture: ").Append(recorder.FormatText)
                .Append(", ").Append(recorder.Count).Append(" frames (")
                .Append(Milliseconds(recorder.Count, rate)).AppendLine(")");
            if (recorder.Gaps > 0)
            {
                report.Append("capture gaps: ").Append(recorder.Gaps)
                    .AppendLine(" - the endpoint idled mid-capture; tails after a gap are lower bounds");
            }
            report.AppendLine();
            report.AppendLine("edge          onset    tail   audible-after   peak-after   pre-edge   detached   stops/starts  fadeOuts/teardowns");

            var tails = new Dictionary<Edge, double>();
            foreach (Trial trial in trials)
            {
                double onset = recorder.FirstAudibleMilliseconds(trial.StrikeMark, trial.EdgeMark);
                double tail = recorder.LastAudibleMilliseconds(trial.EdgeMark, trial.EndMark);
                double audible = recorder.AudibleMilliseconds(trial.EdgeMark, trial.EndMark);
                float peakAfter = recorder.Peak(trial.EdgeMark, trial.EndMark);
                float preEdge = recorder.Peak(Math.Max(trial.StrikeMark, trial.EdgeMark - rate / 20), trial.EdgeMark);
                tails[trial.Edge] = tail;

                report.Append(trial.Edge.ToString().PadRight(13))
                    .Append(Pad(onset, 7)).Append(Pad(tail, 8))
                    .Append(Pad(audible, 16)).Append(' ')
                    .Append(Decibels(peakAfter).PadLeft(11))
                    .Append(' ').Append(Decibels(preEdge).PadLeft(10))
                    .Append(trial.RingingOutAtEdge.ToString(CultureInfo.InvariantCulture).PadLeft(11))
                    .Append("   ").Append(trial.DeviceStops).Append('/').Append(trial.DeviceStarts)
                    .Append("          ").Append(trial.FadeOuts).Append('/').Append(trial.Teardowns)
                    .AppendLine();
            }

            report.AppendLine();
            report.AppendLine("onset and tail are both measured from a mark taken on this thread, so both");
            report.AppendLine("carry the same unknown capture delay; their difference does not.");
            Compare(report, tails, Edge.Pause, Edge.PauseFlush, "queued mix the flush discarded");
            Compare(report, tails, Edge.Stop, Edge.Pause, "Escape's cut against Space's ramp");
            Compare(report, tails, Edge.Stop, Edge.PauseFlush, "Escape's cut against the flushed pause");

            // The graph's side of the same window. Read this table against the one above: a tail that
            // shows here is ours to fix in the mixer, a tail that shows only above is not.
            report.AppendLine();
            report.AppendLine("what left the graph from edge+100 ms to edge+500 ms (the ramp is 8 ms, so a");
            report.AppendLine("paused graph must read the floor here):");
            report.AppendLine();
            report.AppendLine("edge          graph-peak   buffers   of which cleared");
            foreach (Trial trial in trials)
            {
                report.Append(trial.Edge.ToString().PadRight(13))
                    .Append(Decibels(trial.GraphPeakLate).PadLeft(10))
                    .Append(trial.BuffersLate.ToString(CultureInfo.InvariantCulture).PadLeft(10))
                    .Append(trial.PausedLate.ToString(CultureInfo.InvariantCulture).PadLeft(18))
                    .AppendLine();
            }

            // One stage further out: the bytes NAudio was actually given. Everything past this is
            // inside NAudio or inside Windows, so this is where the search for a mechanism has to end
            // one way or the other. See DeviceHandoffMeter.
            report.AppendLine();
            report.AppendLine("what was handed to NAudio in the same window:");
            report.AppendLine();
            report.AppendLine("edge          peak         reads   short   pure-silence");
            foreach (Trial trial in trials)
            {
                report.Append(trial.Edge.ToString().PadRight(13))
                    .Append(Decibels(trial.HandoffPeakLate).PadLeft(10))
                    .Append(trial.HandoffReads.ToString(CultureInfo.InvariantCulture).PadLeft(8))
                    .Append(trial.HandoffShortReads.ToString(CultureInfo.InvariantCulture).PadLeft(8))
                    .Append(trial.HandoffSilentReads.ToString(CultureInfo.InvariantCulture).PadLeft(15))
                    .AppendLine();
                if (trial.HandoffDetail != null)
                {
                    report.Append("              ").AppendLine(trial.HandoffDetail);
                }
            }

            // And the same at the group's own output, with no NAudio between. See
            // GraphTapSampleProvider: whichever of the two tables is dirty says whose bug it is.
            report.AppendLine();
            report.AppendLine("what the group itself returned in the same window (floats):");
            report.AppendLine();
            report.AppendLine("edge          peak         reads   short   pure-silence");
            foreach (Trial trial in trials)
            {
                report.Append(trial.Edge.ToString().PadRight(13))
                    .Append(Decibels(trial.TapPeakLate).PadLeft(10))
                    .Append(trial.TapReads.ToString(CultureInfo.InvariantCulture).PadLeft(8))
                    .Append(trial.TapShortReads.ToString(CultureInfo.InvariantCulture).PadLeft(8))
                    .Append(trial.TapSilentReads.ToString(CultureInfo.InvariantCulture).PadLeft(15))
                    .AppendLine();
                if (trial.TapDetail != null)
                {
                    report.Append("              ").AppendLine(trial.TapDetail);
                }
            }

            report.AppendLine();
            report.AppendLine("the group's own account of the last buffer of each window:");
            foreach (Trial trial in trials)
            {
                report.Append("  ").Append(trial.Edge).Append(": ")
                    .AppendLine(trial.GroupDetail ?? "(not traced)");
            }

            report.AppendLine();
            report.AppendLine("and the ten buffers before that one (branch, count, wanted, non-zero, first, array, thread):");
            foreach (Trial trial in trials)
            {
                report.Append("  ").Append(trial.Edge).Append(": ")
                    .AppendLine(string.IsNullOrEmpty(trial.GroupRecent) ? "(not traced)" : trial.GroupRecent);
            }

            // And the shape of each tail, because "how long" has never been the question that was
            // stuck: a draining queue and something still being produced measure the same in
            // milliseconds and look nothing alike here.
            report.AppendLine();
            report.AppendLine("tail envelope, 25 ms buckets in dBFS from the edge ('--' is digital silence):");
            foreach (Trial trial in trials)
            {
                double lag;
                double loop = recorder.LoopCorrelation(trial.EdgeMark, trial.EndMark, 5.0, 150.0, out lag);

                report.AppendLine();
                report.Append("  ").Append(trial.Edge).Append(": ")
                    .AppendLine(recorder.EnvelopeText(trial.EdgeMark, trial.EndMark, 25.0));
                report.Append("    self-similarity ")
                    .Append(loop.ToString("F3", CultureInfo.InvariantCulture))
                    .Append(" at ").Append(lag.ToString("F1", CultureInfo.InvariantCulture))
                    .AppendLine(" ms lag");
            }
        }

        private static void Compare(StringBuilder report, Dictionary<Edge, double> tails,
            Edge left, Edge right, string what)
        {
            double a, b;
            if (!tails.TryGetValue(left, out a) || !tails.TryGetValue(right, out b))
            {
                return;
            }

            report.Append(what).Append(": ").Append(left).Append(' ')
                .Append(a.ToString("F1", CultureInfo.InvariantCulture)).Append(" ms - ")
                .Append(right).Append(' ')
                .Append(b.ToString("F1", CultureInfo.InvariantCulture)).Append(" ms = ")
                .Append((a - b).ToString("F1", CultureInfo.InvariantCulture)).AppendLine(" ms");
        }

        private static string Pad(double milliseconds, int width)
        {
            return (milliseconds.ToString("F1", CultureInfo.InvariantCulture) + " ms").PadLeft(width);
        }

        private static string Milliseconds(int frames, int rate)
        {
            double ms = rate > 0 ? frames * 1000.0 / rate : 0.0;
            return ms.ToString("F1", CultureInfo.InvariantCulture) + " ms";
        }

        private static string Decibels(float linear)
        {
            if (linear <= 0f)
            {
                return "-inf dBFS";
            }

            double db = 20.0 * Math.Log10(linear);
            return db.ToString("F1", CultureInfo.InvariantCulture) + " dBFS";
        }

        private static void Write(string directory, StringBuilder report)
        {
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "pause-tail-report.txt"), report.ToString());
            }
            catch (IOException)
            {
                Console.Error.WriteLine(report.ToString());
            }
            catch (UnauthorizedAccessException)
            {
                Console.Error.WriteLine(report.ToString());
            }

            Console.Out.WriteLine(report.ToString());
        }

        /// <summary>
        /// Records the endpoint's own mix through WASAPI loopback, keeping a mono mixdown to listen to
        /// and a per-frame peak envelope to measure.
        /// <para>
        /// The envelope is the maximum magnitude across channels rather than the average: a mix whose
        /// channels are out of phase averages to nothing, and a tail that cancels in the average is
        /// still a tail in the speakers.
        /// </para>
        /// </summary>
        private sealed class LoopbackRecorder : IDisposable
        {
            private readonly double _seconds;
            private readonly object _sync = new object();
            private readonly System.Diagnostics.Stopwatch _clock = new System.Diagnostics.Stopwatch();

            private WasapiLoopbackCapture _capture;
            private float[] _mono;
            private float[] _peak;
            private int _written;
            private int _channels;
            private int _rate;
            private bool _float;
            private int _gaps;
            private double _drift;

            public LoopbackRecorder(double seconds)
            {
                _seconds = seconds;
            }

            public int SampleRate => _rate;

            public int Count => Volatile.Read(ref _written);

            public int Gaps => Volatile.Read(ref _gaps);

            public string FormatText { get; private set; } = "(not started)";

            /// <summary>Frames delivered so far - the probe's clock, read on the calling thread.</summary>
            public int Mark()
            {
                return Volatile.Read(ref _written);
            }

            public bool Start(StringBuilder report)
            {
                try
                {
                    _capture = new WasapiLoopbackCapture();
                }
                catch (Exception ex)
                {
                    report.Append("loopback capture unavailable: ").AppendLine(ex.Message);
                    return false;
                }

                WaveFormat format = _capture.WaveFormat;
                _channels = Math.Max(1, format.Channels);
                _rate = format.SampleRate;
                _float = format.BitsPerSample == 32;
                FormatText = format.SampleRate + " Hz, " + _channels + " ch, " +
                    format.BitsPerSample + "-bit " + format.Encoding;

                if (_rate <= 0 || (format.BitsPerSample != 32 && format.BitsPerSample != 16))
                {
                    report.Append("loopback format not understood: ").AppendLine(FormatText);
                    return false;
                }

                int capacity = (int)(_seconds * _rate) + _rate;
                _mono = new float[capacity];
                _peak = new float[capacity];
                _capture.DataAvailable += OnDataAvailable;

                try
                {
                    _capture.StartRecording();
                }
                catch (Exception ex)
                {
                    report.Append("loopback capture would not start: ").AppendLine(ex.Message);
                    return false;
                }

                _clock.Start();
                return true;
            }

            private void OnDataAvailable(object sender, WaveInEventArgs e)
            {
                int bytesPerSample = _float ? 4 : 2;
                int stride = bytesPerSample * _channels;
                int frames = e.BytesRecorded / Math.Max(1, stride);

                lock (_sync)
                {
                    int room = _peak.Length - _written;
                    if (frames > room)
                    {
                        frames = room;
                    }

                    for (int frame = 0; frame < frames; frame++)
                    {
                        float sum = 0f;
                        float loudest = 0f;
                        int baseOffset = frame * stride;
                        for (int c = 0; c < _channels; c++)
                        {
                            int at = baseOffset + c * bytesPerSample;
                            float sample = _float
                                ? BitConverter.ToSingle(e.Buffer, at)
                                : BitConverter.ToInt16(e.Buffer, at) / 32768f;
                            sum += sample;
                            float magnitude = sample >= 0f ? sample : -sample;
                            if (magnitude > loudest)
                            {
                                loudest = magnitude;
                            }
                        }

                        _mono[_written + frame] = sum / _channels;
                        _peak[_written + frame] = loudest;
                    }

                    // A loopback endpoint delivers nothing at all while no stream is active, so the
                    // frame count is only a clock as long as delivery is continuous. Drift that jumps
                    // means it was not, and a tail measured across the jump is a lower bound rather
                    // than a measurement - said out loud instead of quietly reported as a number.
                    double expected = (_written + frames) * 1000.0 / _rate;
                    double drift = _clock.Elapsed.TotalMilliseconds - expected;
                    if (_written > 0 && drift - _drift > 30.0)
                    {
                        _gaps++;
                    }
                    _drift = drift;

                    Volatile.Write(ref _written, _written + frames);
                }
            }

            public void Stop()
            {
                if (_capture == null)
                {
                    return;
                }

                try
                {
                    _capture.StopRecording();
                }
                catch (Exception)
                {
                    // Nothing useful to do; whatever was captured is still measurable.
                }

                // StopRecording is asynchronous in NAudio: the capture thread may still be inside a
                // DataAvailable when it returns, and reading the arrays while it writes them would
                // make the last trial's numbers depend on the scheduler.
                Thread.Sleep(200);
            }

            /// <summary>Milliseconds from <paramref name="from"/> to the first audible frame, or the whole window.</summary>
            public double FirstAudibleMilliseconds(int from, int to)
            {
                int limit = Math.Min(to, Count);
                for (int i = Math.Max(0, from); i < limit; i++)
                {
                    if (_peak[i] > AudibleThreshold)
                    {
                        return (i - from) * 1000.0 / _rate;
                    }
                }

                return (limit - from) * 1000.0 / _rate;
            }

            /// <summary>Milliseconds from <paramref name="from"/> to the last audible frame, 0 when silent throughout.</summary>
            public double LastAudibleMilliseconds(int from, int to)
            {
                int limit = Math.Min(to, Count);
                for (int i = limit - 1; i >= Math.Max(0, from); i--)
                {
                    if (_peak[i] > AudibleThreshold)
                    {
                        return (i - from + 1) * 1000.0 / _rate;
                    }
                }

                return 0.0;
            }

            /// <summary>
            /// How much of the window was audible at all, which is not the same as when it stopped: a
            /// single stray frame at the end would give a long tail and a near-zero total here.
            /// </summary>
            public double AudibleMilliseconds(int from, int to)
            {
                int limit = Math.Min(to, Count);
                int audible = 0;
                for (int i = Math.Max(0, from); i < limit; i++)
                {
                    if (_peak[i] > AudibleThreshold)
                    {
                        audible++;
                    }
                }

                return audible * 1000.0 / _rate;
            }

            public float Peak(int from, int to)
            {
                int limit = Math.Min(to, Count);
                float peak = 0f;
                for (int i = Math.Max(0, from); i < limit; i++)
                {
                    if (_peak[i] > peak)
                    {
                        peak = _peak[i];
                    }
                }

                return peak;
            }

            /// <summary>
            /// The window's loudness bucket by bucket, in dBFS, as one line of text.
            /// <para>
            /// A single number for a tail cannot tell a decay from a sustain, and the difference is the
            /// whole diagnosis: a queue draining falls away monotonically, while something still being
            /// produced holds its level. Written out rather than reduced to a slope so the shape is
            /// checkable by eye against the wav.
            /// </para>
            /// </summary>
            public string EnvelopeText(int from, int to, double bucketMilliseconds)
            {
                int limit = Math.Min(to, Count);
                int start = Math.Max(0, from);
                int bucket = Math.Max(1, (int)Math.Round(bucketMilliseconds * _rate / 1000.0));
                var text = new StringBuilder();

                for (int at = start; at < limit; at += bucket)
                {
                    float peak = 0f;
                    int end = Math.Min(at + bucket, limit);
                    for (int i = at; i < end; i++)
                    {
                        if (_peak[i] > peak)
                        {
                            peak = _peak[i];
                        }
                    }

                    if (text.Length > 0)
                    {
                        text.Append(' ');
                    }

                    text.Append(peak <= 0f
                        ? "--"
                        : Math.Round(20.0 * Math.Log10(peak)).ToString("F0", CultureInfo.InvariantCulture));
                }

                return text.ToString();
            }

            /// <summary>
            /// The strongest self-similarity in the window over lags from <paramref name="minMilliseconds"/>
            /// to <paramref name="maxMilliseconds"/>, as a correlation in [0, 1] with the lag that gave it.
            /// <para>
            /// This is the test for a repeated buffer. When a driver releases a buffer it was only
            /// partly given, the engine plays whatever the previous cycle left in it, again and again -
            /// which is heard as a squeal rather than a tail, and which correlates with itself almost
            /// perfectly at the device's own period. Music does not: even a held note drifts in phase
            /// enough over 400 ms to fall well short of 1.
            /// </para>
            /// </summary>
            public double LoopCorrelation(int from, int to, double minMilliseconds, double maxMilliseconds,
                out double lagMilliseconds)
            {
                lagMilliseconds = 0.0;
                int limit = Math.Min(to, Count);
                int start = Math.Max(0, from);
                int minLag = Math.Max(1, (int)Math.Round(minMilliseconds * _rate / 1000.0));
                int maxLag = (int)Math.Round(maxMilliseconds * _rate / 1000.0);
                double best = 0.0;

                for (int lag = minLag; lag <= maxLag; lag++)
                {
                    int overlap = limit - start - lag;
                    if (overlap < minLag)
                    {
                        break;
                    }

                    double dot = 0.0, left = 0.0, right = 0.0;
                    for (int i = 0; i < overlap; i++)
                    {
                        double a = _mono[start + i];
                        double b = _mono[start + i + lag];
                        dot += a * b;
                        left += a * a;
                        right += b * b;
                    }

                    double energy = Math.Sqrt(left * right);
                    if (energy <= 0.0)
                    {
                        continue;
                    }

                    double correlation = dot / energy;
                    if (correlation > best)
                    {
                        best = correlation;
                        lagMilliseconds = lag * 1000.0 / _rate;
                    }
                }

                return best;
            }

            /// <summary>
            /// Writes the mono mixdown so the tail can also be heard and looked at in an editor. The
            /// numbers above are the verdict; this is what makes them checkable by someone else.
            /// </summary>
            public void WriteWav(string path, StringBuilder report)
            {
                int frames = Count;
                if (frames <= 0 || _rate <= 0)
                {
                    return;
                }

                try
                {
                    using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
                    using (var writer = new BinaryWriter(stream))
                    {
                        int dataBytes = frames * 2;
                        writer.Write(new[] { 'R', 'I', 'F', 'F' });
                        writer.Write(36 + dataBytes);
                        writer.Write(new[] { 'W', 'A', 'V', 'E' });
                        writer.Write(new[] { 'f', 'm', 't', ' ' });
                        writer.Write(16);
                        writer.Write((short)1);
                        writer.Write((short)1);
                        writer.Write(_rate);
                        writer.Write(_rate * 2);
                        writer.Write((short)2);
                        writer.Write((short)16);
                        writer.Write(new[] { 'd', 'a', 't', 'a' });
                        writer.Write(dataBytes);

                        for (int i = 0; i < frames; i++)
                        {
                            float sample = _mono[i];
                            if (sample > 1f) { sample = 1f; }
                            if (sample < -1f) { sample = -1f; }
                            writer.Write((short)(sample * short.MaxValue));
                        }
                    }

                    report.Append("loopback wav: ").AppendLine(path);
                }
                catch (IOException ex)
                {
                    report.Append("loopback wav not written: ").AppendLine(ex.Message);
                }
            }

            public void Dispose()
            {
                if (_capture == null)
                {
                    return;
                }

                _capture.DataAvailable -= OnDataAvailable;
                try
                {
                    _capture.Dispose();
                }
                catch (Exception)
                {
                    // A reclaimed endpoint throws on Dispose; the report is already written.
                }

                _capture = null;
            }
        }
    }
}
