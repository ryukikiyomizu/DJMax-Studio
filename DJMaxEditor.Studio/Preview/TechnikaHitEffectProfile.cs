using DJMaxEditor.Preview;

namespace DJMaxEditor.Studio.Preview
{
    /// <summary>
    /// How one kind of note's hit burst differs from the tap's: how large, how long, how bright.
    ///
    /// <para>
    /// The arcade does not fire one effect for everything. <c>CoolBomb\*\</c> carries a sequence per
    /// gesture - <c>cool</c> beside <c>onePoint</c> beside <c>hold</c> - and the renderer drew
    /// <c>cool</c> for every kind because <c>cool</c> is the only one in the extracted set on disk.
    /// The missing sequences cannot be invented, so this stands in for them the only way a preview
    /// honestly can: the one sequence, shaped per kind. A chain node's tick is a smaller, quicker
    /// flash than a tap's; a hold's is the wider one the arcade sizes off the half itself.
    /// </para>
    ///
    /// <para>
    /// Provenance, in the same spirit as <see cref="TechnikaPlayfieldMetrics"/>: <see cref="Scale"/>
    /// for the hold family is measured - <see cref="TechnikaPlayfieldMetrics.MeasuredHitEffectSize"/>
    /// against <see cref="TechnikaPlayfieldMetrics.CoolBombWidth"/> is the arcade's own hold quad
    /// against its own cool quad - and every other number here is a policy choice, chosen so the
    /// families read apart at a glance and marked as one where it is set.
    /// </para>
    ///
    /// <para>
    /// Separate from the renderer for the reason <see cref="TechnikaHitFlash"/> is: it is a table and
    /// arithmetic, so a test can walk every kind without a render target.
    /// </para>
    /// </summary>
    internal struct TechnikaHitEffectProfile
    {
        /// <summary>
        /// The hold burst against the cool burst, both from their own VCE quads: 350 against 394.
        /// The one number here that is measured rather than chosen.
        /// </summary>
        public const double HoldScale =
            TechnikaPlayfieldMetrics.MeasuredHitEffectSize / TechnikaPlayfieldMetrics.CoolBombWidth;

        /// <summary>
        /// How many note frames wide the tap's burst is drawn in the preview.
        ///
        /// <para>
        /// Policy, and the whole of the "effects are too scaled big" fix. The arcade's own burst is
        /// 394 px growing to 602 against a 90 px note - four and a half notes wide, and taller than
        /// the 344 px half it fires in. That is right on an arcade cabinet, where one hit is the thing
        /// being looked at; in an editor's preview, where the point is to read the chart, it hides
        /// several lanes at once. Two note frames keeps the burst clearly a burst and leaves the
        /// pattern behind it legible.
        /// </para>
        ///
        /// <para>
        /// Expressed in note frames rather than as a flat factor so it follows the lane count: a
        /// 3-line chart's notes are 116 px against a 4-line chart's 90, and an effect that did not
        /// follow would read as two different sizes between modes.
        /// </para>
        /// </summary>
        public const double PreviewNoteFrames = 2.0;

        private TechnikaHitEffectProfile(double scale, double life, double opacity)
        {
            Scale = scale;
            Life = life;
            Opacity = opacity;
        }

        /// <summary>Size of this kind's burst against the tap's.</summary>
        public double Scale { get; }

        /// <summary>How long it lasts against the tap's, so a quick tick can be a quick tick.</summary>
        public double Life { get; }

        /// <summary>Brightness against the tap's.</summary>
        public double Opacity { get; }

        /// <summary>
        /// The profile for <paramref name="kind"/>. Every kind has one, so there is no fallback to
        /// get wrong: an unrecognised kind gets the tap's, which is the sequence as authored.
        /// </summary>
        public static TechnikaHitEffectProfile For(GameplayPreviewNoteKind kind)
        {
            switch (kind)
            {
                // A chain's members are ticks along a gesture rather than hits, and the arcade has a
                // sequence of its own for them - `onePoint`, absent from the set on disk. Half size,
                // half length and dimmer: policy, standing in for art that is not here, and chosen
                // because a chain can put a dozen of these on the field inside one scan.
                case GameplayPreviewNoteKind.ChainHead:
                case GameplayPreviewNoteKind.ChainNode:
                    return new TechnikaHitEffectProfile(0.5, 0.5, 0.75);

                // The hold family fires the arcade's `hold` sequence, also absent. Its size is the
                // one thing about it that is known - see HoldScale - and it runs the full length,
                // because a hold's burst is what marks a note that is still being held.
                case GameplayPreviewNoteKind.Hold:
                case GameplayPreviewNoteKind.Drag:
                case GameplayPreviewNoteKind.RepeatHeadHold:
                case GameplayPreviewNoteKind.RepeatHold:
                    return new TechnikaHitEffectProfile(HoldScale, 1.0, 1.0);

                // A repeat series fires once per member a sixteenth apart, so at the tap's size and
                // length every burst in a run overlaps the next and the series reads as one glare
                // with no notes visible inside it. Smaller, shorter and dimmer so the members stay
                // countable: policy, and the numbers are chosen for that and nothing else.
                case GameplayPreviewNoteKind.RepeatHead:
                case GameplayPreviewNoteKind.Repeat:
                    return new TechnikaHitEffectProfile(0.55, 0.7, 0.7);

                default:
                    return new TechnikaHitEffectProfile(1.0, 1.0, 1.0);
            }
        }

        /// <summary>
        /// The factor that takes the burst from the size the arcade authored it at to the size the
        /// preview draws it at, on a chart of <paramref name="laneCount"/> lines. Zero when there is
        /// no measurement to scale against, which a caller reads as "draw nothing".
        /// </summary>
        /// <remarks>
        /// Derived rather than a constant of its own, so the arcade's measured quad stays the
        /// arcade's measured quad: this is <see cref="PreviewNoteFrames"/> note frames expressed as a
        /// share of <see cref="TechnikaPlayfieldMetrics.CoolBombWidth"/>. On a 4-line chart that is
        /// 180 of the authored 394 px.
        /// </remarks>
        public static double PreviewScale(int laneCount)
        {
            double note = TechnikaPlayfieldMetrics.NoteFrameSize(laneCount);
            double authored = TechnikaPlayfieldMetrics.CoolBombWidth;
            if (note <= 0.0 || authored <= 0.0)
            {
                return 0.0;
            }
            return note * PreviewNoteFrames / authored;
        }
    }
}
