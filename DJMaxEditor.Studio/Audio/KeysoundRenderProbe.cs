using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using DJMaxEditor.DJMax;
using DJMaxEditor.Preview;
using DJMaxEditor.Studio.Preview;

namespace DJMaxEditor.Studio.Audio
{
    /// <summary>
    /// Renders a chart's keysound mix to a WAV without a device, and measures what the mix does to
    /// the samples.
    ///
    /// <para>
    /// Written because "the sound is horrifying" cannot be chased through source reading. The two
    /// mechanisms that can make a correct-looking scheduler sound wrong are both invisible in a
    /// diff: a voice cut off part-way through its waveform, which is a step to zero and therefore a
    /// click, and a voice cut off part-way through its <em>note</em>, which is a keysound that stops
    /// before the ear expects it. Both are arithmetic on decoded PCM, so both can be counted
    /// exactly - and the WAV means a human can check the verdict by listening.
    /// </para>
    ///
    /// <para>
    /// The schedule is rebuilt here rather than driven through <c>Player</c> on purpose: the
    /// sequencer runs off a wall clock, so a headless render driven by it would be at the mercy of
    /// how fast this machine happens to be. Tick-to-millisecond conversion mirrors
    /// <c>Player.SeekEventsTo</c> exactly - accumulate <c>(tick - lastTick) * period</c> and reset
    /// the period at each Tempo event - and the per-note gain mirrors
    /// <c>MainWindow.OnPlayerEvent</c>.
    /// </para>
    /// </summary>
    internal static class KeysoundRenderProbe
    {
        /// <summary>Command-line switch that selects this mode.</summary>
        public const string Switch = "--audio-render";

        private const double DefaultSeconds = 30.0;

        /// <summary>
        /// Pump granularity. The graph is advanced in blocks and notes land on block boundaries, so
        /// this is also the worst-case timing error of the render: 64 frames is 1.33 ms at 48 kHz,
        /// which is below the point where a scheduling artefact and a mixing artefact could be
        /// confused for each other.
        /// </summary>
        private const int BlockFrames = 64;

        /// <summary>How a note's hold length is decided. The whole point of the probe is to compare these.</summary>
        private enum HoldRule
        {
            /// <summary>Ship's rule: any note with <c>Duration &gt; 6</c> is treated as held.</summary>
            Duration,

            /// <summary>Renderer's rule: only kinds that draw a trail are held.</summary>
            Kind,

            /// <summary>No note is ever cut; every keysound rings out to the end of its file.</summary>
            RingOut
        }

        /// <summary>One scheduled note, resolved down to what the mixer is actually asked to do.</summary>
        private sealed class Strike
        {
            public double StartMilliseconds;
            public uint Track;
            public ushort Sound;
            public float Gain;
            public byte Pan;
            public int DurationTicks;
            public double MillisecondsPerTick;
            public bool HasTrail;
            public string Instrument;

            public double HoldMilliseconds(HoldRule rule)
            {
                switch (rule)
                {
                    case HoldRule.Duration:
                        return DurationTicks <= 6 ? 0.0 : DurationTicks * MillisecondsPerTick;
                    case HoldRule.Kind:
                        return HasTrail ? DurationTicks * MillisecondsPerTick : 0.0;
                    default:
                        return 0.0;
                }
            }
        }

        /// <summary>
        /// Runs the probe. Exit code 0 when a WAV was written, non-zero when the chart could not be
        /// opened or has no keysounds to render.
        /// </summary>
        public static int Run(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine(Switch + " <chart> <output-directory> [seconds]");
                return 2;
            }

            string chartPath = args[1];
            string outputDirectory = args[2];
            double seconds = DefaultSeconds;
            if (args.Length > 3)
            {
                double parsed;
                if (double.TryParse(args[3], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out parsed) && parsed > 0.0)
                {
                    seconds = parsed;
                }
            }

            var report = new StringBuilder();
            report.Append("chart: ").AppendLine(chartPath);
            report.Append("seconds: ").Append(seconds.ToString("F1", CultureInfo.InvariantCulture)).AppendLine();

            PlayerData model = PlayfieldProbe.Open(chartPath, report);
            if (model == null)
            {
                Write(outputDirectory, report);
                return 3;
            }

            List<Strike> schedule = BuildSchedule(model, chartPath, report);
            if (schedule.Count == 0)
            {
                report.AppendLine("no audible notes - nothing to render");
                Write(outputDirectory, report);
                return 4;
            }

            Directory.CreateDirectory(outputDirectory);

            NullAudioOutput output;
            using (NAudioKeysoundPlayer player = NAudioKeysoundPlayer.CreateWithoutDevice(out output))
            {
                int loaded = LoadKeysounds(player, model, chartPath, report);
                if (loaded == 0)
                {
                    report.AppendLine("no keysound decoded - nothing to render");
                    Write(outputDirectory, report);
                    return 5;
                }

                // One player for all passes: the cache is the expensive part (276 Vorbis decodes) and
                // StopAllSounds leaves it intact while emptying the graph, so each pass still starts
                // from silence.
                //
                // The first pass is the graph as it shipped - no headroom, no limiter - so the report
                // carries its own before-and-after rather than asking anyone to take the fix on
                // trust. The rest hold the output stage fixed and vary the hold rule instead, which
                // is how the two candidate explanations for "horrifying" get separated.
                RenderPass(player, output, schedule, "before", HoldRule.Duration,
                    1f, false, true, seconds, outputDirectory, report);
                RenderPass(player, output, schedule, "after", HoldRule.Duration,
                    PlaybackGroupSampleProvider.DefaultMasterGain, true, true,
                    seconds, outputDirectory, report);
                RenderPass(player, output, schedule, "after-kindrule", HoldRule.Kind,
                    PlaybackGroupSampleProvider.DefaultMasterGain, true, true,
                    seconds, outputDirectory, report);

                // The last two differ in one flag, and it is the flag this probe was extended to
                // measure: whether a retrigger on a busy track leaves the previous keysound ringing
                // out (what ships) or hard-stops it mid-waveform (what shipped). Rendering both and
                // subtracting is the only way to say what the change is worth in audio rather than in
                // counts.
                float[] ringingOut = RenderPass(player, output, schedule, "after-ringout",
                    HoldRule.RingOut, PlaybackGroupSampleProvider.DefaultMasterGain, true, true,
                    seconds, outputDirectory, report);
                float[] hardStopped = RenderPass(player, output, schedule, "after-ringout-hardstop",
                    HoldRule.RingOut, PlaybackGroupSampleProvider.DefaultMasterGain, true, false,
                    seconds, outputDirectory, report);
                DescribeOverlapDifference(ringingOut, hardStopped,
                    player.WaveFormat.SampleRate, player.WaveFormat.Channels, report);

                DescribeGains(player, schedule, seconds, report);
            }

            Write(outputDirectory, report);
            return 0;
        }

        /// <summary>
        /// Loads the chart's keysounds the way the shell does: from the chart's own folder, indexed
        /// by <c>InsNum</c>, with mode 1 for the first instrument (the long background track).
        /// </summary>
        private static int LoadKeysounds(
            NAudioKeysoundPlayer player, PlayerData model, string chartPath, StringBuilder report)
        {
            string directory = Path.GetDirectoryName(chartPath);
            if (model.Instruments == null || string.IsNullOrEmpty(directory))
            {
                return 0;
            }

            int loaded = 0;
            int missing = 0;
            for (int i = 0; i < model.Instruments.Count; i++)
            {
                InstrumentData instrument = model.Instruments[i];
                if (instrument == null || instrument.InsNum == 0 || string.IsNullOrEmpty(instrument.Name))
                {
                    continue;
                }

                if (player.LoadSound(instrument.InsNum, Path.Combine(directory, instrument.Name), i == 0 ? 1 : 0))
                {
                    loaded++;
                }
                else
                {
                    missing++;
                }
            }

            report.Append("keysounds: loaded=").Append(loaded).Append(" missing=").Append(missing).AppendLine();
            return loaded;
        }

        /// <summary>
        /// Turns the model into the exact sequence of <c>PlayNote</c> calls the shell would make, in
        /// milliseconds.
        /// </summary>
        private static List<Strike> BuildSchedule(
            PlayerData model, string chartPath, StringBuilder report)
        {
            var strikes = new List<Strike>();
            EventData[] events = model.Tracks == null ? null : model.Tracks.Events;
            if (events == null)
            {
                return strikes;
            }

            Dictionary<EventData, GameplayPreviewNoteKind> kinds = ProjectKinds(model, report);

            // Track volume starts where the file left it and is moved by Volume events, exactly as
            // MainWindow.OnPlayerEvent does - but on a copy, because a probe must not edit the model.
            var volumes = new Dictionary<uint, float>();
            foreach (TrackData track in model.Tracks)
            {
                volumes[track.Idx] = track.Volume;
            }

            double period = model.Tempo > 0.0f ? 60000.0 / (model.Tempo * 48.0) : 1.0;
            double elapsed = 0.0;
            int lastTick = 0;
            int noTrail = 0;
            int unprojected = 0;

            for (int i = 0; i < events.Length; i++)
            {
                EventData ev = events[i];
                if (ev == null)
                {
                    continue;
                }

                elapsed += (ev.Tick - lastTick) * period;
                lastTick = ev.Tick;

                if (ev.EventType == EventType.Tempo)
                {
                    period = ev.Tempo > 0.0f ? 60000.0 / (ev.Tempo * 48.0) : period;
                    continue;
                }

                if (ev.EventType == EventType.Volume)
                {
                    volumes[ev.TrackId] = ev.Volume / (float)sbyte.MaxValue;
                    continue;
                }

                if (ev.EventType != EventType.Note)
                {
                    continue;
                }

                ushort sound = ev.Instrument == null ? (ushort)0 : ev.Instrument.InsNum;
                if (sound == 0)
                {
                    continue;
                }

                GameplayPreviewNoteKind kind;
                bool projected = kinds.TryGetValue(ev, out kind);
                if (!projected)
                {
                    unprojected++;
                }

                bool trail = projected && GameplayPreviewNoteKinds.HasHoldTrail(kind);
                if (!trail)
                {
                    noTrail++;
                }

                float volume;
                strikes.Add(new Strike
                {
                    StartMilliseconds = elapsed,
                    Track = ev.TrackId,
                    Sound = sound,
                    Gain = (volumes.TryGetValue(ev.TrackId, out volume) ? volume : 1f) * VelocityGain(ev.Vel),
                    Pan = ev.Pan,
                    DurationTicks = ev.Duration,
                    MillisecondsPerTick = period,
                    HasTrail = trail,
                    Instrument = ev.Instrument.Name
                });
            }

            report.AppendFormat(CultureInfo.InvariantCulture,
                "schedule: strikes={0} spanning {1:F1}s  bodied-by-duration={2}  held-by-kind={3}  " +
                "not-in-projection={4}",
                strikes.Count,
                strikes.Count == 0 ? 0.0 : strikes[strikes.Count - 1].StartMilliseconds / 1000.0,
                CountBodied(strikes), strikes.Count - noTrail, unprojected).AppendLine();
            return strikes;
        }

        private static int CountBodied(List<Strike> strikes)
        {
            int count = 0;
            for (int i = 0; i < strikes.Count; i++)
            {
                if (strikes[i].DurationTicks > 6)
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Maps every note the projector recognises to its kind, keyed by the very
        /// <see cref="EventData"/> instance the sequencer will dispatch - the projection keeps that
        /// reference in <c>GameplayPreviewNote.Source</c>, so no tick-and-lane matching is needed.
        /// </summary>
        private static Dictionary<EventData, GameplayPreviewNoteKind> ProjectKinds(
            PlayerData model, StringBuilder report)
        {
            var kinds = new Dictionary<EventData, GameplayPreviewNoteKind>();
            try
            {
                GameplayPreviewProfileSuggestion suggestion = GameplayPreviewProfileResolver.Suggest(model);
                GameplayPreviewProjection projection = GameplayPreviewProjector.Project(model, suggestion.Profile);
                report.Append("profile: ").Append(suggestion.Profile)
                    .Append("  lanes=").Append(projection.LaneCount)
                    .Append("  projected notes=").Append(projection.Notes.Count).AppendLine();

                foreach (ProjectedGameplayNote note in projection.Notes)
                {
                    if (note.Source != null)
                    {
                        kinds[note.Source] = note.Kind;
                    }
                }
            }
            catch (Exception ex)
            {
                report.Append("projection failed: ").AppendLine(ex.Message);
            }

            return kinds;
        }

        private static float VelocityGain(byte velocity)
        {
            const int max = 127;
            return velocity >= max ? 1f : velocity / (float)max;
        }

        private static void Write(string directory, StringBuilder report)
        {
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "audio-report.txt"), report.ToString());
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
        /// Renders <paramref name="seconds"/> of the mix under one hold rule, one output-stage setting
        /// and one retrigger rule, writes it as a WAV and appends both the mix statistics and the
        /// analytic voice-cut statistics to the report. Returns the rendered interleaved mix so two
        /// passes can be subtracted, or null when the request was too long to hold in memory.
        /// </summary>
        private static float[] RenderPass(
            NAudioKeysoundPlayer player, NullAudioOutput output, List<Strike> schedule,
            string label, HoldRule rule, float masterGain, bool limiter, bool overlap,
            double seconds, string outputDirectory, StringBuilder report)
        {
            player.StopAllSounds();
            player.MasterGain = masterGain;
            player.LimiterEnabled = limiter;
            player.AllowOverlappingRetrigger = overlap;
            player.ResetMeters();

            int sampleRate = player.WaveFormat.SampleRate;
            int channels = player.WaveFormat.Channels;
            int totalFrames = (int)(seconds * sampleRate);
            var mix = new float[(long)totalFrames * channels <= int.MaxValue
                ? totalFrames * channels
                : 0];
            if (mix.Length == 0)
            {
                report.AppendLine("requested render is too long to hold in memory");
                return null;
            }

            int next = 0;
            int fired = 0;
            for (int frame = 0; frame < totalFrames; )
            {
                double now = frame * 1000.0 / sampleRate;
                while (next < schedule.Count && schedule[next].StartMilliseconds <= now)
                {
                    Strike strike = schedule[next++];
                    player.PlayNote(strike.Track, strike.Sound, strike.Gain, strike.Pan,
                        0.0, strike.HoldMilliseconds(rule));
                    fired++;
                }

                int block = Math.Min(BlockFrames, totalFrames - frame);
                float[] buffer = output.Pump(block);
                Array.Copy(buffer, 0, mix, frame * channels, block * channels);
                frame += block;
            }

            string wav = Path.Combine(outputDirectory, "mix-" + label + ".wav");
            WriteWav(wav, mix, sampleRate, channels);

            report.AppendLine();
            report.Append("=== ").Append(label).Append(": hold rule ").Append(rule)
                .AppendFormat(CultureInfo.InvariantCulture, ", master gain {0:F2}", masterGain)
                .Append(", limiter ").Append(limiter ? "on" : "off")
                .Append(", retrigger ").Append(overlap ? "rings out" : "hard stops")
                .Append(" -> ").AppendLine(Path.GetFileName(wav));
            report.Append("  notes fired: ").Append(fired)
                .AppendFormat(CultureInfo.InvariantCulture,
                    "   limiter pulled down {0} frames ({1:P2} of the render)",
                    player.LimitedFrames, player.LimitedFrames / (double)Math.Max(1, totalFrames))
                .AppendLine();
            DescribeMix(mix, sampleRate, channels, report);
            DescribeCuts(player, schedule, rule, overlap, sampleRate, channels, seconds, report);
            return mix;
        }

        /// <summary>
        /// What letting a retriggered keysound ring out actually adds to the mix, by subtracting the
        /// hard-stopping render from the overlapping one.
        ///
        /// <para>
        /// The counts in <see cref="DescribeCuts"/> say how often a voice was truncated; this says how
        /// much audio that was. The difference signal <em>is</em> the recovered tails plus the clicks
        /// that are no longer being made, so its RMS against the mix's own RMS is the honest answer to
        /// "is this audible or is it bookkeeping". Both passes fire the identical schedule into the
        /// identical output stage, so nothing else can contribute to the difference.
        /// </para>
        /// </summary>
        private static void DescribeOverlapDifference(
            float[] ringingOut, float[] hardStopped, int sampleRate, int channels, StringBuilder report)
        {
            report.AppendLine();
            report.AppendLine("=== what ring-out added, sample by sample ===");

            if (ringingOut == null || hardStopped == null || ringingOut.Length != hardStopped.Length)
            {
                report.AppendLine("  the two passes are not comparable - nothing subtracted");
                return;
            }

            double square = 0.0;
            double referenceSquare = 0.0;
            double peak = 0.0;
            double peakAt = 0.0;
            int changed = 0;
            for (int i = 0; i < ringingOut.Length; i++)
            {
                double difference = ringingOut[i] - hardStopped[i];
                double magnitude = Math.Abs(difference);
                if (magnitude > 1e-6)
                {
                    changed++;
                }
                if (magnitude > peak)
                {
                    peak = magnitude;
                    peakAt = i / (double)channels / sampleRate;
                }
                square += difference * difference;
                referenceSquare += (double)hardStopped[i] * hardStopped[i];
            }

            double rms = Math.Sqrt(square / Math.Max(1, ringingOut.Length));
            double referenceRms = Math.Sqrt(referenceSquare / Math.Max(1, hardStopped.Length));
            report.AppendFormat(CultureInfo.InvariantCulture,
                "  recovered audio: rms={0:F4} ({1}), peak={2:F3} ({3}) at {4:F3}s",
                rms, Decibels(rms), peak, Decibels(peak), peakAt).AppendLine();
            report.AppendFormat(CultureInfo.InvariantCulture,
                "  samples changed: {0} of {1} ({2:P2}); recovered audio sits {3:F1} dB under the mix",
                changed, ringingOut.Length, changed / (double)Math.Max(1, ringingOut.Length),
                rms <= 0.0 || referenceRms <= 0.0 ? 0.0 : 20.0 * Math.Log10(referenceRms / rms))
                .AppendLine();
        }

        /// <summary>
        /// Level and continuity of the rendered mix.
        ///
        /// <para>
        /// The step histogram counts sample-to-sample jumps within one channel. A note onset is a
        /// legitimate large step, so the absolute counts are not a defect measure on their own -
        /// what makes them evidence is that the note onsets are identical under every rule, so any
        /// difference between the rules is a difference in how voices <em>end</em>.
        /// </para>
        /// </summary>
        private static void DescribeMix(
            float[] mix, int sampleRate, int channels, StringBuilder report)
        {
            double peak = 0.0;
            double square = 0.0;
            int clipped = 0;
            for (int i = 0; i < mix.Length; i++)
            {
                double value = mix[i];
                double magnitude = Math.Abs(value);
                if (magnitude > peak)
                {
                    peak = magnitude;
                }
                if (magnitude > 1.0)
                {
                    clipped++;
                }
                square += value * value;
            }

            double rms = Math.Sqrt(square / Math.Max(1, mix.Length));
            report.AppendFormat(CultureInfo.InvariantCulture,
                "  level: peak={0:F3} ({1}) rms={2:F4} ({3}) clipped={4} samples ({5:P3})",
                peak, Decibels(peak), rms, Decibels(rms), clipped,
                clipped / (double)Math.Max(1, mix.Length)).AppendLine();

            double[] bounds = { 0.05, 0.1, 0.2, 0.4, 0.8 };
            var counts = new int[bounds.Length];
            double biggest = 0.0;
            int biggestAt = 0;
            for (int i = channels; i < mix.Length; i++)
            {
                double step = Math.Abs(mix[i] - mix[i - channels]);
                if (step > biggest)
                {
                    biggest = step;
                    biggestAt = i;
                }
                for (int b = bounds.Length - 1; b >= 0; b--)
                {
                    if (step > bounds[b])
                    {
                        counts[b]++;
                        break;
                    }
                }
            }

            report.Append("  steps within a channel:");
            for (int b = 0; b < bounds.Length; b++)
            {
                report.AppendFormat(CultureInfo.InvariantCulture, "  >{0:F2}={1}", bounds[b], counts[b]);
            }
            report.AppendFormat(CultureInfo.InvariantCulture,
                "   largest={0:F3} at {1:F3}s", biggest,
                biggestAt / (double)channels / sampleRate).AppendLine();
        }

        private static string Decibels(double magnitude)
        {
            if (magnitude <= 0.0)
            {
                return "-inf dBFS";
            }
            return (20.0 * Math.Log10(magnitude)).ToString("F1", CultureInfo.InvariantCulture) + " dBFS";
        }

        /// <summary>A voice as the channel table sees it, for the analytic pass.</summary>
        private sealed class Voice
        {
            public KeysoundSample Sample;
            public int StartFrame;
            public int EndFrame;
            public int HoldFrames;
            public bool Limited;
            public float Gain;
            public string Instrument;
        }

        /// <summary>
        /// What the scheduler does to each voice, counted exactly rather than inferred from the mix.
        ///
        /// <para>
        /// Two things can end a voice early. A <b>retrigger</b> is another note arriving on the same
        /// track. With <c>AllowOverlappingRetrigger</c> off, <c>ReleaseChannelLocked</c> calls
        /// <c>KeysoundVoice.Stop</c>, whose next read returns zero samples, so the waveform is
        /// truncated wherever it happened to be - the amplitude at that instant <em>is</em> the size of
        /// the step, and a step is a click. With it on, which is what ships, the voice keeps its place
        /// in the mixer and plays out; the same arithmetic then measures two different things, so the
        /// report says which, and in the ring-out case reports the step it <em>avoided</em> and the tail
        /// audio it kept rather than pretending 106 clicks are still being made. A <b>limit</b> is the
        /// note's own hold length running out, which is ramped over 8 ms and therefore silent, but it
        /// discards the rest of the keysound and that is audible as a sound that stops too soon.
        /// Reporting them separately is the whole point: they need opposite fixes.
        /// </para>
        /// </summary>
        private static void DescribeCuts(
            NAudioKeysoundPlayer player, List<Strike> schedule, HoldRule rule, bool overlap,
            int sampleRate, int channels, double seconds, StringBuilder report)
        {
            double[] bounds = { -6.0, -12.0, -20.0, -30.0, -40.0, -60.0 };
            var histogram = new int[bounds.Length + 1];
            var live = new Dictionary<uint, Voice>();
            var trimmed = new List<double>();
            var tails = new List<double>();
            int steals = 0;
            int audible = 0;
            int limited = 0;
            double loudest = 0.0;
            double loudestAt = 0.0;
            string loudestInstrument = string.Empty;
            int rampFrames = (int)(8.0 * sampleRate / 1000.0);

            for (int i = 0; i < schedule.Count; i++)
            {
                Strike strike = schedule[i];
                if (strike.StartMilliseconds > seconds * 1000.0)
                {
                    break;
                }

                KeysoundSample sample = player.SampleAt(strike.Sound);
                if (sample == null)
                {
                    continue;
                }

                int startFrame = (int)(strike.StartMilliseconds * sampleRate / 1000.0);
                int sampleFrames = sample.Samples.Length / Math.Max(1, channels);
                double holdMilliseconds = strike.HoldMilliseconds(rule);
                int holdFrames = holdMilliseconds <= 0.0
                    ? 0
                    : (int)(holdMilliseconds * sampleRate / 1000.0);
                bool isLimited = holdFrames > 0 && (long)holdFrames * channels < sample.Samples.Length;
                if (isLimited)
                {
                    limited++;
                    trimmed.Add((sampleFrames - holdFrames) * 1000.0 / sampleRate);
                }

                Voice previous;
                if (live.TryGetValue(strike.Track, out previous) && startFrame < previous.EndFrame)
                {
                    steals++;
                    tails.Add((previous.EndFrame - startFrame) * 1000.0 / sampleRate);
                    double amplitude = CutAmplitude(previous, startFrame, channels, rampFrames);
                    double db = amplitude <= 0.0 ? -200.0 : 20.0 * Math.Log10(amplitude);
                    if (db > -40.0)
                    {
                        audible++;
                    }
                    if (amplitude > loudest)
                    {
                        loudest = amplitude;
                        loudestAt = strike.StartMilliseconds / 1000.0;
                        loudestInstrument = previous.Instrument;
                    }

                    int bucket = bounds.Length;
                    for (int b = 0; b < bounds.Length; b++)
                    {
                        if (db > bounds[b])
                        {
                            bucket = b;
                            break;
                        }
                    }
                    histogram[bucket]++;
                }

                live[strike.Track] = new Voice
                {
                    Sample = sample,
                    StartFrame = startFrame,
                    EndFrame = startFrame + (isLimited ? holdFrames : sampleFrames),
                    HoldFrames = holdFrames,
                    Limited = isLimited,
                    Gain = strike.Gain,
                    Instrument = strike.Instrument
                };
            }

            if (overlap)
            {
                tails.Sort();
                double totalTail = 0.0;
                for (int i = 0; i < tails.Count; i++)
                {
                    totalTail += tails[i];
                }

                report.AppendFormat(CultureInfo.InvariantCulture,
                    "  retriggers on a busy track: {0}, hard stops: 0 (the previous voice rings out)",
                    steals).AppendLine();
                report.AppendFormat(CultureInfo.InvariantCulture,
                    "    tail audio kept: {0:F1}s in total, median {1:F0} ms per retrigger",
                    totalTail / 1000.0,
                    tails.Count == 0 ? 0.0 : tails[tails.Count / 2]).AppendLine();
                report.AppendFormat(CultureInfo.InvariantCulture,
                    "    clicks avoided: {0} above -40 dBFS, by step size:", audible);
            }
            else
            {
                report.AppendFormat(CultureInfo.InvariantCulture,
                    "  hard stops (retrigger steals): {0}, of which above -40 dBFS: {1}", steals, audible)
                    .AppendLine();
                report.Append("    step size at the cut:");
            }

            for (int b = 0; b <= bounds.Length; b++)
            {
                string label = b == 0
                    ? "above " + bounds[0].ToString("F0", CultureInfo.InvariantCulture)
                    : b == bounds.Length
                        ? "below " + bounds[bounds.Length - 1].ToString("F0", CultureInfo.InvariantCulture)
                        : bounds[b - 1].ToString("F0", CultureInfo.InvariantCulture) + ".." +
                          bounds[b].ToString("F0", CultureInfo.InvariantCulture);
                report.Append("  ").Append(label).Append('=').Append(histogram[b]);
            }
            report.AppendLine();

            if (loudest > 0.0)
            {
                report.AppendFormat(CultureInfo.InvariantCulture,
                    overlap
                        ? "    loudest step avoided: {0} at {1:F3}s, on \"{2}\""
                        : "    loudest cut: {0} at {1:F3}s, cutting \"{2}\"",
                    Decibels(loudest), loudestAt, loudestInstrument).AppendLine();
            }

            trimmed.Sort();
            double totalTrimmed = 0.0;
            for (int i = 0; i < trimmed.Count; i++)
            {
                totalTrimmed += trimmed[i];
            }

            report.AppendFormat(CultureInfo.InvariantCulture,
                "  ramped limits (note ended before its keysound): {0}, discarding {1:F1}s of audio" +
                " in total, median {2:F0} ms per note",
                limited, totalTrimmed / 1000.0,
                trimmed.Count == 0 ? 0.0 : trimmed[trimmed.Count / 2]).AppendLine();
        }

        /// <summary>
        /// Loudest channel of the frame a stolen voice was truncated at, scaled by its gain and by
        /// the release ramp if it had already started fading.
        /// </summary>
        private static double CutAmplitude(Voice voice, int atFrame, int channels, int rampFrames)
        {
            int cursor = atFrame - voice.StartFrame;
            if (cursor < 0)
            {
                cursor = 0;
            }

            int index = cursor * channels;
            float[] samples = voice.Sample.Samples;
            if (index >= samples.Length)
            {
                return 0.0;
            }

            double peak = 0.0;
            for (int c = 0; c < channels && index + c < samples.Length; c++)
            {
                double magnitude = Math.Abs(samples[index + c]);
                if (magnitude > peak)
                {
                    peak = magnitude;
                }
            }

            peak *= voice.Gain;

            if (voice.Limited && rampFrames > 0)
            {
                int fadeStart = voice.HoldFrames - rampFrames;
                if (cursor > fadeStart)
                {
                    double scale = (voice.HoldFrames - cursor) / (double)rampFrames;
                    peak *= scale > 0.0 ? scale : 0.0;
                }
            }

            return peak;
        }
        /// <summary>
        /// Where the mix's level comes from: the loudest any single voice can be on its own, against
        /// the loudest the sum reaches.
        ///
        /// <para>
        /// The distinction decides the fix. If one keysound alone already exceeds full scale then the
        /// gains are wrong; if every voice is inside full scale and only the sum overflows, the mixer
        /// is missing headroom, and no amount of per-note correctness will stop it distorting.
        /// </para>
        /// </summary>
        private static void DescribeGains(
            NAudioKeysoundPlayer player, List<Strike> schedule, double seconds, StringBuilder report)
        {
            double loudestVoice = 0.0;
            string loudestName = string.Empty;
            double loudestGain = 0.0;
            double maxGain = 0.0;
            var seen = new Dictionary<ushort, double>();

            for (int i = 0; i < schedule.Count; i++)
            {
                Strike strike = schedule[i];
                if (strike.StartMilliseconds > seconds * 1000.0)
                {
                    break;
                }

                if (strike.Gain > maxGain)
                {
                    maxGain = strike.Gain;
                }

                double peak;
                if (!seen.TryGetValue(strike.Sound, out peak))
                {
                    KeysoundSample sample = player.SampleAt(strike.Sound);
                    peak = 0.0;
                    if (sample != null)
                    {
                        float[] samples = sample.Samples;
                        for (int s = 0; s < samples.Length; s++)
                        {
                            double magnitude = Math.Abs(samples[s]);
                            if (magnitude > peak)
                            {
                                peak = magnitude;
                            }
                        }
                    }
                    seen[strike.Sound] = peak;
                }

                double voice = peak * strike.Gain;
                if (voice > loudestVoice)
                {
                    loudestVoice = voice;
                    loudestName = strike.Instrument;
                    loudestGain = strike.Gain;
                }
            }

            report.AppendLine();
            report.AppendFormat(CultureInfo.InvariantCulture,
                "gains: highest note gain={0:F3}  loudest single voice={1:F3} ({2}) from \"{3}\" at gain {4:F3}",
                maxGain, loudestVoice, Decibels(loudestVoice), loudestName, loudestGain).AppendLine();
        }

        /// <summary>
        /// Writes the mix as 16-bit PCM so anything can play it, with hard clipping at full scale.
        ///
        /// <para>
        /// Clipping here rather than normalising deliberately: the level the mix comes out at is part
        /// of what is being measured, and a normalised file would hide a mix that overflows.
        /// </para>
        /// </summary>
        private static void WriteWav(string path, float[] mix, int sampleRate, int channels)
        {
            const int bitsPerSample = 16;
            int dataBytes = mix.Length * (bitsPerSample / 8);

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + dataBytes);
                writer.Write(new[] { 'W', 'A', 'V', 'E' });
                writer.Write(new[] { 'f', 'm', 't', ' ' });
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)channels);
                writer.Write(sampleRate);
                writer.Write(sampleRate * channels * (bitsPerSample / 8));
                writer.Write((short)(channels * (bitsPerSample / 8)));
                writer.Write((short)bitsPerSample);
                writer.Write(new[] { 'd', 'a', 't', 'a' });
                writer.Write(dataBytes);

                for (int i = 0; i < mix.Length; i++)
                {
                    float value = mix[i];
                    if (value > 1f)
                    {
                        value = 1f;
                    }
                    else if (value < -1f)
                    {
                        value = -1f;
                    }
                    writer.Write((short)(value * short.MaxValue));
                }
            }
        }
    }
}
