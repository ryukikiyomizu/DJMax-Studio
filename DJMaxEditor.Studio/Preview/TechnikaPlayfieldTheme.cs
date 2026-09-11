using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using DJMaxEditor.Preview;
using DJMaxEditor.Studio.Design;

namespace DJMaxEditor.Studio.Preview
{
    /// <summary>
    /// Every brush and pen the TECHNIKA playfield draws with, allocated once and frozen.
    ///
    /// <para>
    /// The note colours are the arcade's, sampled from the game's own sprites rather than
    /// invented: TECHNIKA keys colour to note <em>type</em>, not to lane, which is the opposite
    /// of every vertical-scrolling DJMAX title and the single most recognisable thing about the
    /// playfield. Taps are magenta, long heads green, holds blue, repeats purple. Reproducing
    /// those as vector fills gives the right read without shipping a byte of anyone's artwork.
    /// </para>
    ///
    /// <para>
    /// The chrome is deliberately <em>not</em> the arcade's. The real header is a light grey
    /// plate, which would be a bright slab across the top of a dark editor; instead the header is
    /// drawn in the studio's own palette with the arcade's cells - groove trough, score and combo
    /// digit fields, guide slot, four indicator lights - outlined at their measured coordinates.
    /// The structure is what a chart author needs to see. The skin is not.
    /// </para>
    /// </summary>
    internal sealed class TechnikaPlayfieldTheme
    {
        private static TechnikaPlayfieldTheme _default;

        private readonly Dictionary<GameplayPreviewNoteKind, TechnikaNoteBrushes> _notes =
            new Dictionary<GameplayPreviewNoteKind, TechnikaNoteBrushes>();

        private TechnikaPlayfieldTheme()
        {
            Backdrop = StudioPalette.Brush(StudioPalette.Abyss);

            // The arcade fills this area with the BGA video. With no video loaded it is near
            // black, and the two halves are given a single step of separation so the split is
            // legible even on a chart with no notes in view.
            FieldUpper = StudioPalette.Brush("#FF0B0C0E");
            FieldLower = StudioPalette.Brush("#FF0E1013");

            HeaderFill = StudioPalette.Brush(StudioPalette.Raised);
            HeaderEdge = FrozenPen(StudioPalette.Edge, 1.0);

            // The divider's opaque core is 5 px of flat grey at 100% in the non-fever state
            // (fever0_midgauge.png), with the 1 px black rules above and below that the sprite
            // carries. The fever states recolour it pink, pale pink then cyan.
            DividerCore = StudioPalette.Brush("#FF242424");
            DividerEdge = FrozenPen("#CC000000", 1.0);
            DividerFever = StudioPalette.Brush("#FF00FFF9");

            LaneRule = FrozenPen("#FF1E2124", 1.0);
            LaneCenterRule = FrozenPen("#FF262A2F", 1.0);
            MarginRule = FrozenPen("#66" + StudioPalette.Accent.Substring(3), 1.0);

            SidebarFill = StudioPalette.Brush("#FF16181B");

            HudFill = StudioPalette.Brush("#FF121315");
            HudEdge = FrozenPen("#FF2A2D31", 1.0);
            HudLabel = StudioPalette.Brush(StudioPalette.TextMuted);
            GrooveFill = StudioPalette.Brush("#FF2FBFA6");

            ScanlineForward = BuildScanline(false);
            ScanlineReverse = BuildScanline(true);

            // The sprite's own peak column, x181 of line_star.png. See BuildScanline.
            ScanlineCore = FrozenPen("#FFFCFDFD", 1.0);

            ApproachRing = FrozenPen("#7FBFE8FF", 1.5);

            TextPrimary = StudioPalette.Brush(StudioPalette.TextPrimary);
            TextMuted = StudioPalette.Brush(StudioPalette.TextMuted);
            UiTypeface = new Typeface(
                new FontFamily("Segoe UI Variable Text, Segoe UI, Tahoma"),
                FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            MonoTypeface = new Typeface(
                new FontFamily("Cascadia Mono, Consolas, Courier New"),
                FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            BuildNoteBrushes();
        }

        public static TechnikaPlayfieldTheme Default
        {
            get
            {
                if (_default == null)
                {
                    _default = new TechnikaPlayfieldTheme();
                }
                return _default;
            }
        }

        public Brush Backdrop { get; private set; }
        public Brush FieldUpper { get; private set; }
        public Brush FieldLower { get; private set; }

        public Brush HeaderFill { get; private set; }
        public Pen HeaderEdge { get; private set; }

        public Brush DividerCore { get; private set; }
        public Pen DividerEdge { get; private set; }
        public Brush DividerFever { get; private set; }

        public Pen LaneRule { get; private set; }
        public Pen LaneCenterRule { get; private set; }
        public Pen MarginRule { get; private set; }

        public Brush SidebarFill { get; private set; }

        public Brush HudFill { get; private set; }
        public Pen HudEdge { get; private set; }
        public Brush HudLabel { get; private set; }
        public Brush GrooveFill { get; private set; }

        /// <summary>Scanline wash for a left-to-right sweep (the upper half).</summary>
        public Brush ScanlineForward { get; private set; }

        /// <summary>Scanline wash for a right-to-left sweep (the lower half), mirrored.</summary>
        public Brush ScanlineReverse { get; private set; }

        /// <summary>The hard leading edge, drawn as a line on top of the wash.</summary>
        public Pen ScanlineCore { get; private set; }

        public Pen ApproachRing { get; private set; }

        public Brush TextPrimary { get; private set; }
        public Brush TextMuted { get; private set; }
        public Typeface UiTypeface { get; private set; }
        public Typeface MonoTypeface { get; private set; }

        public TechnikaNoteBrushes NoteFor(GameplayPreviewNoteKind kind)
        {
            TechnikaNoteBrushes brushes;
            if (_notes.TryGetValue(kind, out brushes))
            {
                return brushes;
            }
            return _notes[GameplayPreviewNoteKind.Basic];
        }

        /// <summary>Field colour for one half.</summary>
        public Brush FieldFor(bool isTopHalf)
        {
            return isTopHalf ? FieldUpper : FieldLower;
        }

        // -----------------------------------------------------------------------------------

        private void BuildNoteBrushes()
        {
            // Sampled from note\{star,pop}\0 in the user's own installed game. Colour tracks
            // type, and the family index (0..5) only shifts brightness within the hue, so one
            // family is enough to establish the palette.
            //
            // One adjustment, stated rather than hidden: family 0's hold note is #000E33, which
            // is near-black and would vanish on our field. #0B3E98 is family 2's hold - the same
            // hue at a luminance that reads - so the value is still a measured arcade colour.
            TechnikaNoteBrushes tap = new TechnikaNoteBrushes("#FFD3004F", "#FFFF6E9E");
            TechnikaNoteBrushes chain = new TechnikaNoteBrushes("#FF63A802", "#FFB6F06A");
            TechnikaNoteBrushes hold = new TechnikaNoteBrushes("#FF0B3E98", "#FF6FA8FF");
            TechnikaNoteBrushes repeat = new TechnikaNoteBrushes("#FFC203BE", "#FFF58CF2");

            _notes[GameplayPreviewNoteKind.Basic] = tap;
            _notes[GameplayPreviewNoteKind.Generic] = tap;
            // A drag is a tap with a duration - same magenta head, and the renderer draws the
            // trail that tells them apart.
            _notes[GameplayPreviewNoteKind.Drag] = tap;
            _notes[GameplayPreviewNoteKind.ChainHead] = chain;
            _notes[GameplayPreviewNoteKind.ChainNode] = chain;
            _notes[GameplayPreviewNoteKind.Hold] = hold;
            _notes[GameplayPreviewNoteKind.RepeatHead] = repeat;
            _notes[GameplayPreviewNoteKind.RepeatHeadHold] = repeat;
            _notes[GameplayPreviewNoteKind.Repeat] = repeat;
            _notes[GameplayPreviewNoteKind.RepeatHold] = repeat;

            // The connector fallback when the arcade's `notepressline` sheet is not on disk. The
            // green chain fill above colours the note head; the line that joins a chain's members
            // is yellow in the arcade (it matches the yellow `notepressnote` node rings), so the
            // fallback must not reuse the head's green. Sampled from notepressnote's ring.
            ChainRunLine = new SolidColorBrush(StudioPalette.Parse("#FFFFD300"));
            ChainRunLine.Freeze();
        }

        /// <summary>
        /// The yellow bar joining chain members when the packaged <c>notepressline</c> art is
        /// unavailable. Repeat runs fall back to their own purple trail brush instead.
        /// </summary>
        public Brush ChainRunLine { get; private set; }

        /// <summary>
        /// The scanline wash, as a gradient across the quad.
        ///
        /// <para>
        /// Stops are a transcription of the source sprite's own column profile, not a hand-tuned
        /// gradient. <c>panel\line_star.png</c> is 256x304 with ink from x11 to x199: a linear
        /// alpha ramp to a flat plateau of #001AA8 at alpha 128 across x108..152, then the hard
        /// edge - alpha reaches 255 at x169, luminance peaks at x181 on near-white #FCFDFD, and
        /// the far side collapses in eight px, alpha 143 by x193 and 2 by x199. Positions below
        /// are that x over 256, because the quad samples u 0..1.
        /// </para>
        ///
        /// <para>
        /// The peak at x181 - centre of the >=50% core at x169..191 - is what
        /// <see cref="TechnikaPlayfieldMetrics.ScanlineLeadingEdgeOffset"/> is derived from:
        /// 180/256 x 250 = 175.8, or 0.703 across the quad. The falloff is asymmetric, which is
        /// how an earlier reading centred the edge on x187 (0.730) by taking the last
        /// non-transparent column for the edge's far side. The body trails <em>behind</em> the
        /// edge, so the reversed brush is a genuine mirror rather than the same gradient shifted.
        /// </para>
        /// </summary>
        private static Brush BuildScanline(bool mirrored)
        {
            GradientStopCollection stops = new GradientStopCollection
            {
                new GradientStop(Argb(0x00, "001AA8"), 0.000),
                new GradientStop(Argb(0x00, "001AA8"), 0.043),
                new GradientStop(Argb(0x80, "001AA8"), 0.422),
                new GradientStop(Argb(0x80, "001AA8"), 0.594),
                new GradientStop(Argb(0xBD, "0045E0"), 0.645),
                new GradientStop(Argb(0xFF, "14B2FF"), 0.660),
                new GradientStop(Argb(0xFF, "C2EAFD"), 0.684),
                new GradientStop(Argb(0xFF, "FCFDFD"), 0.707),
                new GradientStop(Argb(0xFF, "35BEFE"), 0.746),
                new GradientStop(Argb(0x46, "0054FF"), 0.762),
                new GradientStop(Argb(0x00, "0054FF"), 0.777),
                new GradientStop(Argb(0x00, "0054FF"), 1.000),
            };

            LinearGradientBrush brush = new LinearGradientBrush(stops)
            {
                StartPoint = mirrored ? new Point(1, 0) : new Point(0, 0),
                EndPoint = mirrored ? new Point(0, 0) : new Point(1, 0),
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
            };
            brush.Freeze();
            return brush;
        }

        private static Color Argb(byte alpha, string rgb)
        {
            Color color = StudioPalette.Parse("#FF" + rgb);
            return Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        private static Pen FrozenPen(string hex, double thickness)
        {
            Pen pen = new Pen(StudioPalette.Brush(hex), thickness);
            pen.Freeze();
            return pen;
        }
    }

    /// <summary>A note's fill, its outline, and the translucent glow drawn under it. All frozen.</summary>
    internal sealed class TechnikaNoteBrushes
    {
        public TechnikaNoteBrushes(string fillHex, string edgeHex)
        {
            Fill = StudioPalette.Brush(fillHex);
            Edge = new Pen(StudioPalette.Brush(edgeHex), 1.0);
            Edge.Freeze();

            // The arcade draws note glows additively - blend (SRCALPHA, ONE) in the capture. WPF
            // has no additive mode on a DrawingContext, so the closest honest approximation is a
            // radial falloff in the edge colour: opaque enough at the note's rim to read as a
            // halo, gone by the outer edge. A flat disc at a fixed alpha was the first attempt and
            // it looked like a coloured plate, because an additive sprite's brightness is not
            // uniform - it decays, and the decay is most of what makes it read as light.
            Color edge = StudioPalette.Parse(edgeHex);
            RadialGradientBrush glow = new RadialGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0x59, edge.R, edge.G, edge.B), 0.00),
                    new GradientStop(Color.FromArgb(0x3D, edge.R, edge.G, edge.B), 0.55),
                    new GradientStop(Color.FromArgb(0x1C, edge.R, edge.G, edge.B), 0.80),
                    new GradientStop(Color.FromArgb(0x00, edge.R, edge.G, edge.B), 1.00),
                })
            {
                GradientOrigin = new Point(0.5, 0.5),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5,
            };
            glow.Freeze();
            Glow = glow;

            SolidColorBrush trail = new SolidColorBrush(StudioPalette.Parse(fillHex))
            {
                Opacity = 0.55,
            };
            trail.Freeze();
            Trail = trail;
        }

        public Brush Fill { get; private set; }
        public Pen Edge { get; private set; }
        public Brush Glow { get; private set; }
        public Brush Trail { get; private set; }
    }
}
