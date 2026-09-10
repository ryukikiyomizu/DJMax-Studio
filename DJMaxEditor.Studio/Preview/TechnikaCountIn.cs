using System;

namespace DJMaxEditor.Studio.Preview
{
    /// <summary>
    /// One moment of the count-in before a chart's first note: which number is showing, and how
    /// brightly.
    ///
    /// <para>
    /// Every number here is a policy choice. There is no countdown art in the extracted set and no
    /// timing for one anywhere in the client - the arcade's own count-in is part of a stage intro
    /// this preview does not model - so nothing about this is measured, and it is marked as policy
    /// where each constant is declared rather than dressed up as provenance.
    /// </para>
    ///
    /// <para>
    /// Separate from the renderer for the reason <see cref="TechnikaHitFlash"/> is: it is arithmetic
    /// over a beat position and nothing else, so a test can walk the whole count-in and check that
    /// the digits land on the beats without a render target, and the draw path is left with no
    /// numbers of its own to get wrong.
    /// </para>
    /// </summary>
    internal struct TechnikaCountIn
    {
        /// <summary>
        /// Beats of count-in drawn before the first note: 3, 2, 1 and then play.
        ///
        /// <para>
        /// Policy. Three is what a rhythm game's count-in conventionally is. One beat per number, in
        /// the musical beats a scan is four of, so the count sits on the music rather than on a wall
        /// clock - which is also what makes scrubbing the transport back into the count-in show it
        /// again instead of replaying a timer.
        /// </para>
        /// </summary>
        public const int Beats = 3;

        /// <summary>
        /// How much of its own beat a digit spends at full strength before it starts to fade. Policy:
        /// the digit lands solid on the beat and thins towards the next one, so the count reads as a
        /// pulse rather than as three numbers cross-dissolving.
        /// </summary>
        public const double FadeShare = 0.55;

        private TechnikaCountIn(int number, double opacity)
        {
            Number = number;
            Opacity = opacity;
        }

        /// <summary>The number showing, or 0 when the count-in is not running.</summary>
        public int Number { get; }

        /// <summary>How brightly to draw it, 1 on the beat and falling to 0 by the next one.</summary>
        public double Opacity { get; }

        /// <summary>Whether there is anything to draw at all.</summary>
        public bool IsVisible
        {
            get { return Number > 0 && Opacity > 0.0; }
        }

        /// <summary>
        /// The count-in <paramref name="beatsAway"/> beats before the first note.
        ///
        /// <para>
        /// Nothing at or past the note itself, and nothing more than <see cref="Beats"/> beats ahead
        /// of it: outside that window the answer is an invisible count rather than a clamp, for the
        /// reason <see cref="TechnikaHitFlash.At"/> gives - a caller that draws what it is handed then
        /// needs no window test of its own, and cannot leave a "1" parked over a chart that is
        /// already playing. An unreal position is treated the same way, so a chart with no clock
        /// counts nothing in rather than counting from some invented place.
        /// </para>
        /// </summary>
        public static TechnikaCountIn At(double beatsAway)
        {
            if (double.IsNaN(beatsAway) || double.IsInfinity(beatsAway) ||
                beatsAway <= 0.0 || beatsAway > Beats)
            {
                return new TechnikaCountIn(0, 0.0);
            }

            // Ceiling, so the whole beat before the first note is "1" and the beat before that is
            // "2": the number showing is how many beats are still to come, counting the one running.
            int number = (int)Math.Ceiling(beatsAway);
            double intoBeat = number - beatsAway;
            double opacity = intoBeat <= FadeShare
                ? 1.0
                : 1.0 - ((intoBeat - FadeShare) / (1.0 - FadeShare));
            return new TechnikaCountIn(number, opacity);
        }
    }
}
