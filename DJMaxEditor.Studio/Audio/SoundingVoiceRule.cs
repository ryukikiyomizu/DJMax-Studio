using System;
using System.Collections.Generic;
using DJMaxEditor.DJMax;

namespace DJMaxEditor.Studio.Audio
{
    /// <summary>
    /// Which note a seek has to put back on the air, per track, and how far into its keysound.
    ///
    /// <para>
    /// This replaces the old "restore the hold that spans this tick" rule, which asked the chart a
    /// question only the decoded audio can answer. Now that nothing truncates a voice - see
    /// <see cref="KeysoundVoice"/> and the ring-out decision in <see cref="HeldNoteRule"/>'s remarks -
    /// a note is still sounding at the seek point when its <em>sample</em> has not run out, whatever
    /// its charted duration says. The difference is not academic: the MR on a TECHNIKA chart is a
    /// single note with a duration of six ticks carrying minutes of audio, so the duration test left
    /// the backing track silent after every seek and, with the stop-flavoured pause, after every
    /// resume.
    /// </para>
    ///
    /// <para>
    /// How many candidates per track depends on the one thing that decides whether an older voice is
    /// still alive: <see cref="NAudioKeysoundPlayer.AllowOverlappingRetrigger"/>. With it off a track
    /// owns exactly one mixer channel, so the newest note at or before the tick is holding it and
    /// everything older was hard-stopped the moment that note started - one candidate, and looking
    /// further back would add a voice the transport is not playing. With it on, which is now the
    /// default, a retrigger leaves the previous voice ringing out, so <em>every</em> note on the track
    /// whose sample still has audio left is sounding at the seek point and all of them have to come
    /// back or a resume is thinner than the playback it is resuming. <see cref="RestorableVoices"/>
    /// takes the per-track voice budget as a parameter for exactly that reason and the shell passes
    /// the player's own setting through; <see cref="Restorable"/> is the one-voice case.
    /// </para>
    ///
    /// <para>
    /// It is an instance rather than a static call because it carries the chart's tempo map: the
    /// offset into a keysound is wall-clock, and a note that started minutes ago cannot be placed by
    /// multiplying ticks by the period in force <em>now</em>. Live beside the audio backend, free of
    /// WPF, so the harness can pin it without standing a window up.
    /// </para>
    /// </summary>
    internal sealed class SoundingVoiceRule
    {
        /// <summary>
        /// Ticks per beat, as the sequencer counts them: <c>Player.SetTempo</c> is
        /// <c>60000 / (tempo * 48)</c>, so this constant is what makes the two clocks agree.
        /// </summary>
        internal const int TicksPerBeat = 48;

        /// <summary>Used only when a chart carries no usable header tempo, matching the sequencer.</summary>
        private const float FallbackTempo = 120.0f;

        private readonly double _initialPeriod;
        private readonly Func<uint, double> _sampleLengthMilliseconds;

        /// <summary>
        /// The chart's tempo map, lifted out of the merged timeline once at construction: tick and the
        /// period it opens, in stream order.
        ///
        /// <para>
        /// Not an optimisation for its own sake. <see cref="ElapsedMilliseconds"/> used to scan the
        /// merged event stream - two thousand events on a TECHNIKA chart, nearly all of them notes -
        /// and that was affordable only while the rule asked exactly one question per track. Now that
        /// a seek walks back over a track's notes it asks tens of them, so the cost of one call has to
        /// come off the size of the chart and onto the size of the tempo map, which is usually one
        /// entry. The arithmetic is unchanged, including the two rules a tempo event obeys: it closes
        /// the segment before it, and one at or before <c>fromTick</c> sets the starting period without
        /// contributing any time.
        /// </para>
        /// </summary>
        private readonly int[] _tempoTicks;
        private readonly double[] _tempoPeriods;

        /// <param name="timeline">
        /// The chart's merged, tick-ordered event stream - <c>TracksList.Events</c>, the same array
        /// the sequencer seeks over. Only its Tempo events are read; null is tolerated and means "one
        /// tempo for the whole chart".
        /// </param>
        /// <param name="headerTempo">
        /// The chart's header tempo, which is where the sequencer's period starts before any Tempo
        /// event has been seen. See <c>Player.Reset</c>.
        /// </param>
        /// <param name="sampleLengthMilliseconds">
        /// How long a loaded keysound runs, by cache slot -
        /// <see cref="NAudioKeysoundPlayer.SampleLengthMilliseconds"/>, whose <c>uint</c> slot number
        /// this signature deliberately matches so the shell can hand the method over as it stands. A
        /// slot that has not finished decoding answers 0, and a note whose sound is not loaded yet is
        /// correctly left alone.
        /// </param>
        internal SoundingVoiceRule(
            IReadOnlyList<EventData> timeline,
            float headerTempo,
            Func<uint, double> sampleLengthMilliseconds)
        {
            if (sampleLengthMilliseconds == null)
            {
                throw new ArgumentNullException(nameof(sampleLengthMilliseconds));
            }

            _sampleLengthMilliseconds = sampleLengthMilliseconds;

            double period = PeriodFor(headerTempo);
            _initialPeriod = period > 0.0 ? period : PeriodFor(FallbackTempo);

            BuildTempoMap(timeline, out _tempoTicks, out _tempoPeriods);
        }

        /// <summary>
        /// Collects the timeline's usable Tempo events. A tempo of zero or less is dropped rather than
        /// kept as a segment boundary, which is exactly what the old inline scan did by leaving the
        /// period alone: a boundary that does not change the period cannot change the integral.
        /// </summary>
        private static void BuildTempoMap(
            IReadOnlyList<EventData> timeline, out int[] ticks, out double[] periods)
        {
            if (timeline == null)
            {
                ticks = new int[0];
                periods = new double[0];
                return;
            }

            var collected = new List<EventData>();
            for (int i = 0; i < timeline.Count; i++)
            {
                EventData ev = timeline[i];
                if (ev != null && ev.EventType == EventType.Tempo && PeriodFor(ev.Tempo) > 0.0)
                {
                    collected.Add(ev);
                }
            }

            ticks = new int[collected.Count];
            periods = new double[collected.Count];
            for (int i = 0; i < collected.Count; i++)
            {
                ticks[i] = collected[i].Tick;
                periods[i] = PeriodFor(collected[i].Tempo);
            }
        }

        /// <summary>Milliseconds per tick at a given tempo, in the sequencer's own units.</summary>
        internal static double PeriodFor(float tempo)
        {
            return tempo > 0.0f ? 60000.0 / (tempo * TicksPerBeat) : 0.0;
        }

        /// <summary>
        /// Wall-clock milliseconds between two ticks, integrated over the tempo map.
        ///
        /// <para>
        /// Segment by segment, exactly as <c>Player.SeekEventsTo</c> does it - a Tempo event closes
        /// the segment before it and opens the next - so this and the sequencer's own clock cannot
        /// disagree about where in the song a tick sits. Tempo events at or before
        /// <paramref name="fromTick"/> set the starting period without contributing time, which is
        /// what lets the answer be a difference rather than two absolute positions.
        /// </para>
        /// </summary>
        internal double ElapsedMilliseconds(int fromTick, int toTick)
        {
            if (toTick <= fromTick)
            {
                return 0.0;
            }

            double period = _initialPeriod;
            double elapsed = 0.0;
            int last = fromTick;

            for (int i = 0; i < _tempoTicks.Length; i++)
            {
                int tick = _tempoTicks[i];
                if (tick > toTick)
                {
                    break;
                }

                if (tick > last)
                {
                    elapsed += (tick - last) * period;
                    last = tick;
                }

                period = _tempoPeriods[i];
            }

            return elapsed + (toTick - last) * period;
        }

        /// <summary>One note a seek has to start again, and how far into its keysound it already is.</summary>
        internal struct RestorableVoice
        {
            internal RestorableVoice(EventData note, double offsetMilliseconds)
            {
                Note = note;
                OffsetMilliseconds = offsetMilliseconds;
            }

            /// <summary>The note whose keysound is still sounding.</summary>
            internal readonly EventData Note;

            /// <summary>
            /// Where in that keysound the seek point falls, ready to hand to
            /// <see cref="NAudioKeysoundPlayer.PlayNote"/> as the start offset.
            /// </summary>
            internal readonly double OffsetMilliseconds;
        }

        /// <summary>
        /// Every voice this track should have on the air at <paramref name="nativeTick"/>, appended to
        /// <paramref name="into"/> <b>oldest first</b>.
        ///
        /// <para>
        /// Oldest first is not cosmetic: the caller starts them in this order, so the retrigger path
        /// runs in the same sequence it ran during playback and the newest note ends up the one holding
        /// the channel, with the older ones detached and ringing out. Start them newest first and the
        /// channel would be left owned by the oldest voice, which is the one <c>StopSound</c> would
        /// then address.
        /// </para>
        ///
        /// <para>
        /// <paramref name="maxVoices"/> is the per-track budget and 1 is a distinct rule rather than a
        /// smaller number: with overlapping retrigger off the newest note at or before the tick
        /// <em>decides the track</em>, and if its own keysound has run out the track is silent - the
        /// notes behind it were stopped by it and are not waiting to be found. Above 1 the walk keeps
        /// going and collects everything that still has audio left, newest first, until the budget is
        /// spent; the budget is what stops a pathological chart from putting hundreds of voices into
        /// the mixer on a single seek.
        /// </para>
        ///
        /// <para>
        /// A note exactly on the seek tick is included on purpose: the sequencer swallows everything at
        /// or before the tick it seeks to, so if this rule skipped it nobody would play it.
        /// </para>
        /// </summary>
        internal void RestorableVoices(
            IReadOnlyList<EventData> trackEvents, int nativeTick, int maxVoices,
            List<RestorableVoice> into)
        {
            if (into == null)
            {
                throw new ArgumentNullException(nameof(into));
            }

            into.Clear();
            if (trackEvents == null || nativeTick <= 0 || maxVoices <= 0)
            {
                return;
            }

            // Backwards, because the list is tick-ordered and the notes nearest the seek are the ones
            // most likely to still be sounding.
            for (int i = trackEvents.Count - 1; i >= 0; i--)
            {
                EventData ev = trackEvents[i];
                if (ev == null || ev.EventType != EventType.Note || ev.Tick > nativeTick)
                {
                    continue;
                }

                double offset;
                if (StillSounding(ev, nativeTick, out offset))
                {
                    into.Add(new RestorableVoice(ev, offset));
                    if (into.Count >= maxVoices)
                    {
                        break;
                    }
                }
                else if (maxVoices == 1)
                {
                    // One voice per track means this note stole the channel from everything older, so
                    // its silence is the track's silence.
                    break;
                }
            }

            // Collected newest first; the caller needs oldest first.
            into.Reverse();
        }

        /// <summary>
        /// Whether this note's keysound still has audio left at <paramref name="nativeTick"/>, and if
        /// so how far into it the seek point falls. Slot 0 is "no sound" and a slot that has not
        /// finished decoding answers 0, so both are left alone rather than asked to play.
        /// </summary>
        private bool StillSounding(EventData note, int nativeTick, out double offsetMilliseconds)
        {
            offsetMilliseconds = 0.0;

            ushort sound = note.Instrument != null ? note.Instrument.InsNum : (ushort)0;
            if (sound == 0)
            {
                return false;
            }

            double length = _sampleLengthMilliseconds(sound);
            if (length <= 0.0)
            {
                return false;
            }

            double offset = ElapsedMilliseconds(note.Tick, nativeTick);
            if (offset >= length)
            {
                return false;
            }

            offsetMilliseconds = offset;
            return true;
        }

        /// <summary>
        /// The single newest note that should be sounding at <paramref name="nativeTick"/>, or null -
        /// the one-voice-per-track answer, kept as its own entry point because that is the whole rule
        /// when overlapping retrigger is off.
        /// </summary>
        internal EventData Restorable(
            IReadOnlyList<EventData> trackEvents, int nativeTick, out double offsetMilliseconds)
        {
            var one = new List<RestorableVoice>(1);
            RestorableVoices(trackEvents, nativeTick, 1, one);

            if (one.Count == 0)
            {
                offsetMilliseconds = 0.0;
                return null;
            }

            offsetMilliseconds = one[0].OffsetMilliseconds;
            return one[0].Note;
        }
    }
}
