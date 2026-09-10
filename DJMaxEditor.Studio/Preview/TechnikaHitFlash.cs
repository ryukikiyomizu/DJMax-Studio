using System;

namespace DJMaxEditor.Studio.Preview
{
    /// <summary>
    /// One moment of the arcade's tap hit burst: how big it is, how bright, and which of its frames
    /// is showing.
    ///
    /// <para>
    /// Separate from the renderer because it is arithmetic over a measurement and nothing else - the
    /// three keys of <c>cool.vce</c>, quoted in <see cref="TechnikaPlayfieldMetrics.CoolBombWidth"/>
    /// and its neighbours - so a test can walk the whole effect and check its shape without a render
    /// target, and the draw path is left with no numbers of its own to get wrong.
    /// </para>
    /// </summary>
    internal struct TechnikaHitFlash
    {
        private TechnikaHitFlash(double width, double height, double opacity, double frameProgress)
        {
            Width = width;
            Height = height;
            Opacity = opacity;
            FrameProgress = frameProgress;
        }

        /// <summary>Width of the quad to draw, in arcade pixels.</summary>
        public double Width { get; }

        /// <summary>Height of the quad to draw, in arcade pixels.</summary>
        public double Height { get; }

        /// <summary>Opacity of the quad, 1 at the strike and 0 when the burst is spent.</summary>
        public double Opacity { get; }

        /// <summary>
        /// How far through its own frames the sequence is, 0 to 1, for
        /// <see cref="TechnikaNoteSprite.Shot"/>. Separate from <see cref="Opacity"/> because the
        /// frames advance at their own steady rate while the quad's size and alpha move between
        /// keys - the animation carries both, and collapsing them would tie the picture to the fade.
        /// </summary>
        public double FrameProgress { get; }

        /// <summary>Whether there is anything to draw at all.</summary>
        public bool IsVisible
        {
            get { return Opacity > 0.0 && Width > 0.0 && Height > 0.0; }
        }

        /// <summary>
        /// The burst <paramref name="progress"/> of the way through, where 0 is the frame the note
        /// was struck on and 1 is the end of <see cref="TechnikaPlayfieldMetrics.CoolBombSeconds"/>.
        /// Outside 0..1 the answer is an invisible flash rather than a clamp: a caller that draws
        /// what it is handed then needs no window test of its own, and cannot leave a spent burst
        /// parked on the field at its last size.
        /// </summary>
        public static TechnikaHitFlash At(double progress)
        {
            if (double.IsNaN(progress) || progress < 0.0 || progress > 1.0)
            {
                return new TechnikaHitFlash(0.0, 0.0, 0.0, 0.0);
            }

            double pinch = TechnikaPlayfieldMetrics.CoolBombPinchProgress;
            if (progress <= pinch)
            {
                // Into the pinch: full scale and opaque, closing to the middle key.
                double t = pinch <= 0.0 ? 1.0 : progress / pinch;
                return new TechnikaHitFlash(
                    Lerp(TechnikaPlayfieldMetrics.CoolBombWidth,
                        TechnikaPlayfieldMetrics.CoolBombPinchWidth, t),
                    Lerp(TechnikaPlayfieldMetrics.CoolBombHeight,
                        TechnikaPlayfieldMetrics.CoolBombPinchHeight, t),
                    Lerp(1.0, TechnikaPlayfieldMetrics.CoolBombPinchAlpha, t),
                    progress);
            }

            // Out of the pinch: expanding past its authored width and fading to nothing.
            double u = pinch >= 1.0 ? 1.0 : (progress - pinch) / (1.0 - pinch);
            return new TechnikaHitFlash(
                Lerp(TechnikaPlayfieldMetrics.CoolBombPinchWidth,
                    TechnikaPlayfieldMetrics.CoolBombEndWidth, u),
                Lerp(TechnikaPlayfieldMetrics.CoolBombPinchHeight,
                    TechnikaPlayfieldMetrics.CoolBombEndHeight, u),
                Lerp(TechnikaPlayfieldMetrics.CoolBombPinchAlpha, 0.0, u),
                progress);
        }

        /// <summary>
        /// The same moment of the burst taken <paramref name="scale"/> as large and
        /// <paramref name="opacity"/> as bright, for a caller that has a reason to draw it at other
        /// than its authored size - a per-kind profile, or the preview's own smaller field. The frame
        /// showing does not move: which picture is on screen is the sequence's business and only the
        /// quad it is drawn on belongs to the caller.
        ///
        /// <para>
        /// A non-positive or unreal factor gives an invisible flash rather than a clamp, for the same
        /// reason <see cref="At"/> does: a caller that draws what it is handed needs no test of its
        /// own, and nothing can end up parked on the field at a size nobody chose.
        /// </para>
        /// </summary>
        public TechnikaHitFlash Scaled(double scale, double opacity)
        {
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0.0 ||
                double.IsNaN(opacity) || double.IsInfinity(opacity) || opacity <= 0.0)
            {
                return new TechnikaHitFlash(0.0, 0.0, 0.0, FrameProgress);
            }
            return new TechnikaHitFlash(
                Width * scale, Height * scale, Opacity * opacity, FrameProgress);
        }

        /// <summary>
        /// How much of a scan the burst lasts on a chart whose scans are
        /// <paramref name="scanSeconds"/> long, or 0 when that is not known.
        ///
        /// <para>
        /// The renderer knows where the sweep is in scans and nothing else, so this is the one place
        /// the effect's authored seconds meet the musical clock the playfield is driven by. Zero is a
        /// real answer - a chart with no tempo - and a caller reads it as "no burst", which is better
        /// than a burst of some invented length on a chart that cannot say.
        /// </para>
        /// </summary>
        public static double ScansFor(double scanSeconds)
        {
            if (double.IsNaN(scanSeconds) || double.IsInfinity(scanSeconds) || scanSeconds <= 0.0)
            {
                return 0.0;
            }
            return TechnikaPlayfieldMetrics.CoolBombSeconds / scanSeconds;
        }

        private static double Lerp(double from, double to, double t)
        {
            return from + ((to - from) * t);
        }
    }
}
