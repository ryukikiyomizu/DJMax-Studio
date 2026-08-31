using System.Collections.Generic;
using DJMaxEditor.DJMax;
using DJMaxEditor.Preview;

namespace DJMaxEditor.Studio.Audio
{
    /// <summary>
    /// Which notes hold their keysound, and so have it cut when the note ends.
    ///
    /// <para>
    /// The whole point of this type is that the answer is <em>not</em> "the note has a duration". A
    /// TECHNIKA note's stored duration is the length of its <em>keysound</em>, and every note in the
    /// chart carries one, so a duration test marks taps as holds - the same mistake that once put a
    /// stub trail behind every note on the playfield, which is why the renderer gates on
    /// <see cref="GameplayPreviewNoteKinds.HasHoldTrail"/> instead. Measured on
    /// <c>sonoflong_pop_3</c>: 115 of 2828 notes pass the duration test and only 61 are really held.
    /// </para>
    ///
    /// <para>
    /// It lives beside the audio backend rather than inside the shell so that what the mixer cuts and
    /// what the renderer draws are two calls to one rule, and so the rule is reachable from the test
    /// harness without standing a WPF window up.
    /// </para>
    ///
    /// <para>
    /// <strong>No longer on any code path.</strong> The transport stopped cutting anything: every
    /// keysound rings out to the end of its file, because the duration this rule measures against is
    /// the keysound's own length on TECHNIKA, so a cut could only ever arrive early and eat the tail.
    /// What a seek puts back is <see cref="SoundingVoiceRule"/>'s business now, and it asks the decoded
    /// sample instead. The trail renderer calls
    /// <see cref="GameplayPreviewNoteKinds.HasHoldTrail"/> directly and
    /// <c>KeysoundRenderProbe</c> re-derives both rules inline for its comparison passes, so nothing
    /// but the four tests in <c>OutputStageTests</c> reaches this type. It is kept, rather than
    /// deleted, as the written-down form of the measurement above - the evidence for why the renderer
    /// gates on kind and not on duration - and it is the one place that number is recorded.
    /// </para>
    /// </summary>
    internal static class HeldNoteRule
    {
        /// <summary>
        /// Longest duration that still counts as a tap, in native ticks. Used only by the fallback
        /// below; the tick size is 6, so this is "one tick of note".
        /// </summary>
        internal const ushort TapDurationTicks = 6;

        /// <summary>
        /// The held notes of a TECHNIKA projection, by reference, or null for any other profile.
        ///
        /// <para>
        /// Reference identity is what makes the lookup exact: the projection keeps the very
        /// <see cref="EventData"/> instance the sequencer later dispatches in
        /// <see cref="ProjectedGameplayNote.Source"/>, so no matching by tick and track is needed. If
        /// that ever changed to a copy, every lookup would miss and every hold would ring out - which
        /// is why the test harness pins it.
        /// </para>
        ///
        /// <para>
        /// Null rather than empty for the non-TECHNIKA case, on purpose: an empty set is
        /// indistinguishable from "nothing is held" and would silently stop cutting anything in a
        /// format whose durations really are hold lengths.
        /// </para>
        /// </summary>
        internal static HashSet<EventData> Collect(GameplayPreviewProjection projection)
        {
            if (projection == null || projection.Profile != GameplayPreviewProfile.Technika)
            {
                return null;
            }

            HashSet<EventData> held = new HashSet<EventData>();
            foreach (ProjectedGameplayNote note in projection.Notes)
            {
                if (note.Source != null && GameplayPreviewNoteKinds.HasHoldTrail(note.Kind))
                {
                    held.Add(note.Source);
                }
            }

            return held;
        }

        /// <summary>
        /// Whether this note's keysound should be stopped when the note ends.
        ///
        /// <para>
        /// With a set from <see cref="Collect"/> the answer is the projection's. Without one - every
        /// non-TECHNIKA format, where there is no gameplay projection to ask and the duration is a
        /// hold length - it falls back to the duration test.
        /// </para>
        /// </summary>
        internal static bool IsHeld(HashSet<EventData> held, EventData eventData)
        {
            if (eventData == null)
            {
                return false;
            }

            if (held != null)
            {
                return held.Contains(eventData);
            }

            return eventData.Duration > TapDurationTicks;
        }
    }
}
