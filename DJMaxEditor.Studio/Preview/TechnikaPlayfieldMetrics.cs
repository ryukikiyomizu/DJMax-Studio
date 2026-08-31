using System;
using System.Windows;
using DJMaxEditor.Preview;

namespace DJMaxEditor.Studio.Preview
{
    /// <summary>
    /// The DJMAX TECHNIKA 2 arcade playfield, in its own pixels.
    ///
    /// <para>
    /// Provenance. Every number here was measured, from one of two sources, and each constant
    /// says which. The runtime geometry comes from a Direct3D 9 draw-call capture of the user's
    /// own <c>CLIENT.EXE</c> in the maingame scene, read against
    /// <c>DMT2\ReverseEngineering\live_maingame_capture_20260815\MAIN_GAME_SYSTEM_MAP.md</c> -
    /// the capture logs each draw's destination quad, so those are the runtime's actual numbers
    /// rather than anyone's reading of the artwork. The sprite geometry comes from measuring the
    /// game's own PNGs (opaque bounding boxes and confirmed cell grids). Half-pixel values are
    /// not typos: D3D9 places a screen-space quad on half-integer boundaries so texel centres
    /// land on pixel centres, and the capture records them verbatim.
    /// </para>
    ///
    /// <para>
    /// The cabinet is fixed at 1280x768 with a presentation interval of 1 and no responsive
    /// layout at all, which is exactly why a constant table works: there is one authored size
    /// and everything else is a letterbox fit of it. <see cref="Fit"/> does that fit.
    /// </para>
    ///
    /// <para>
    /// What the capture cannot say. Its maingame window is 6.82 s - frames 22674 to 23082 - and
    /// it contains no note draws at all: the sampled draw lists are header, HUD, divider,
    /// scanline and glow only, and the recording's own contact sheet shows the field empty. So
    /// nothing below about the lane stack is measured off the runtime. The sweep is, because the
    /// scanline is drawn, and everything derived from the scanline says so explicitly. Note sizes
    /// are too, but from the client's data rather than its draw calls: the note art and the VCE
    /// animations that sit on it carry screen-space quads, and <see cref="NoteFrameThreeLine"/>
    /// and <see cref="MeasuredHitEffectSize"/> are read off those. Anything that does not cite a
    /// draw call, a PNG or a VCE quad is a policy choice; treat it as one.
    /// </para>
    ///
    /// <para>
    /// No artwork is copied here, and none is shipped. These are measurements of the user's own
    /// installed files, which is what lets our own vector chrome sit at the arcade's coordinates
    /// without redistributing anything.
    /// </para>
    ///
    /// <para>
    /// This type is also the "HUD parity contract" the system map asks for under its recommended
    /// implementation boundary - one table a test can assert against, so a later refactor cannot
    /// quietly drift the layout away from the arcade.
    /// </para>
    /// </summary>
    internal static class TechnikaPlayfieldMetrics
    {
        // ---- the frame -----------------------------------------------------------------------

        /// <summary>Backbuffer and viewport width. Fixed; the arcade is exclusive fullscreen.</summary>
        public const double NativeWidth = 1280.0;

        /// <summary>Backbuffer and viewport height.</summary>
        public const double NativeHeight = 768.0;

        /// <summary>
        /// Header band height, from the capture: a 1024x72 quad at (-0.5,-0.5) plus a 256x72
        /// quad at (1023.5,-0.5) - two textures side by side because 1280 is not a power of two.
        /// The header artwork agrees: band 0 of <c>ingamebar_cut.png</c> is y0..71.
        /// </summary>
        public const double HeaderHeight = 72.0;

        /// <summary>Top of the playable area: immediately below the header.</summary>
        public const double PlayfieldTop = HeaderHeight;

        /// <summary>Bottom of the playable area: the bottom of the screen.</summary>
        public const double PlayfieldBottom = NativeHeight;

        // ---- the two halves ------------------------------------------------------------------

        /// <summary>
        /// Where the upper half ends and the lower half begins.
        ///
        /// <para>
        /// The halves are <em>not</em> equal, which is easy to get wrong because the system map
        /// describes them as "two 50% halves". The divider sprite settles it:
        /// <c>panel\Fever\fever0_midgauge.png</c> is a 512x32 image whose only opaque content is
        /// a 5 px solid core at y13..19, and it is stretched to a 1280x32 quad at y 399.5. That
        /// puts the core at y 413.5..418.5 - centred on 416, not on the arithmetic midpoint 420.
        /// So the upper half is 344 px and the lower is 352 px.
        /// </para>
        ///
        /// <para>
        /// A second asset agrees, and it is worth having because it shares nothing with the first.
        /// <c>Perpect\perfect_eff.vce</c> - the flash across the divider on a perfect - draws a
        /// 1278x4 quad at (1,414), so its own 4 px band is 414..418, centred on 416 exactly. The
        /// fever and miss overlays in the same set are 1280x350 quads drawn once per half, which is
        /// the same 344/352 pair rounded to one number an artist can author twice.
        /// </para>
        /// </summary>
        public const double HalfBoundary = 416.0;

        /// <summary>Top of the upper half.</summary>
        public const double UpperFieldTop = PlayfieldTop;

        /// <summary>Bottom of the upper half.</summary>
        public const double UpperFieldBottom = HalfBoundary;

        /// <summary>Top of the lower half.</summary>
        public const double LowerFieldTop = HalfBoundary;

        /// <summary>Bottom of the lower half.</summary>
        public const double LowerFieldBottom = PlayfieldBottom;

        /// <summary>Height of the upper half: 344 px.</summary>
        public const double UpperFieldHeight = UpperFieldBottom - UpperFieldTop;

        /// <summary>Height of the lower half: 352 px.</summary>
        public const double LowerFieldHeight = LowerFieldBottom - LowerFieldTop;

        // ---- centre divider ------------------------------------------------------------------

        /// <summary>Top of the 1280x32 divider quad, stretched from a 512x32 source.</summary>
        public const double DividerQuadTop = 399.5;

        /// <summary>Height of the divider quad.</summary>
        public const double DividerQuadHeight = 32.0;

        /// <summary>Top of the divider's opaque 5 px core, once the source is stretched.</summary>
        public const double DividerCoreTop = 413.5;

        /// <summary>Height of the divider's opaque core.</summary>
        public const double DividerCoreHeight = 5.0;

        // ---- scanline ------------------------------------------------------------------------

        /// <summary>Width of the scanline quad, from the capture.</summary>
        public const double ScanlineWidth = 250.0;

        /// <summary>Height of the scanline quad - taller than either half, so it bleeds past both.</summary>
        public const double ScanlineHeight = 355.0;

        /// <summary>Top of the upper-half scanline quad (y 63.5 to 418.5).</summary>
        public const double ScanlineUpperTop = 63.5;

        /// <summary>Top of the lower-half scanline quad (y 417.5 to 772.5).</summary>
        public const double ScanlineLowerTop = 417.5;

        /// <summary>Width of the scanline source texture, <c>panel\line_star.png</c>.</summary>
        public const double ScanlineSourceWidth = 256.0;

        /// <summary>
        /// Where the bright leading edge sits inside the scanline quad, measured from the quad's
        /// leading side.
        ///
        /// <para>
        /// This matters more than it looks. The scanline is not a line - it is a 250 px wash with
        /// a soft trailing gradient and one hard cyan-white edge, and only that edge is the hit
        /// point. Measured off the client's own texture: <c>panel\line_star.png</c> is 256x304
        /// RGBA - the same dimensions the capture reports for the bound texture, so the extracted
        /// file is the runtime one - it has ink from x 11 to x 199, and the bright core, the
        /// columns at or above half the peak luminance, is x 169..191 with its centre at 180.0
        /// and its peak at 181. The quad samples u 0..1, so 180 x 250/256 = 175.8 px from the
        /// quad's leading side.
        /// </para>
        ///
        /// <para>
        /// This constant positions the drawn wash around the hit point; it does not decide where
        /// the hit point is. <see cref="GameplayPreviewProjector.ScanFieldLeft"/> is derived from
        /// a capture invariant that cancels this offset out, so an error of a few px here slides
        /// the sprite, not the notes. That independence is deliberate - the earlier value of
        /// 182.6 came from reading x 199 (the last non-transparent column, deep in the trailing
        /// falloff) as the edge, and it left the notes untouched precisely because of it.
        /// </para>
        /// </summary>
        public const double ScanlineLeadingEdgeOffset = 175.8;

        // ---- sweep, as measured --------------------------------------------------------------

        /// <summary>
        /// Quad translation rate measured off the capture's timestamps: 26.0 px per 100 ms.
        ///
        /// <para>
        /// Fitted against <c>elapsed_ms</c> rather than against frame index - the capture has
        /// hitches, one of them 189 ms long, which is enough to make a per-frame fit drift 23 px
        /// over 210 frames. Against timestamps it is exactly 260.0 px/s with no residual.
        /// </para>
        /// <para>
        /// Recorded, not used. In the arcade this rate is a consequence of the chart's tempo, and
        /// in the editor the phase comes from <c>GameplayPreviewFrame.CurrentPhase</c>, which is
        /// derived from the chart being edited. Driving the renderer off a hardcoded px/s would
        /// make the preview disagree with the chart the moment the tempo was not the captured
        /// one.
        /// </para>
        /// </summary>
        public const double MeasuredSweepPixelsPerSecond = 260.0;

        /// <summary>
        /// How long one scan takes in the captured sequence: the 960 px field at 260 px/s, so
        /// 3.6923 s. Recorded for the same reason as
        /// <see cref="MeasuredSweepPixelsPerSecond"/> - the editor takes its phase from the chart.
        ///
        /// <para>
        /// Not to be confused with two nearby intervals that the capture also shows. A given
        /// half's line reappears every <see cref="MeasuredLineSpatialPeriod"/> px, twice a scan's
        /// travel, so 7.3846 s; and one line quad stays on screen about 4.8 s, because the wash
        /// enters the viewport before its bright edge reaches the left margin and keeps going
        /// after the edge passes the right one. The constant here previously held 4.98 and was
        /// documented as the re-entry interval, which is neither of those.
        /// </para>
        /// </summary>
        public const double MeasuredScanSeconds = 960.0 / MeasuredSweepPixelsPerSecond;

        /// <summary>
        /// Spatial period of one half's scanline, in px, and the capture's most exact number.
        ///
        /// <para>
        /// The both-halves invariant behind <see cref="GameplayPreviewProjector.ScanFieldLeft"/>
        /// holds at exactly 313.0 in one regime and exactly 2233.0 in the other. The regimes
        /// differ by exactly 1920.0, so a half's line repeats every 1920 px and one scan advances
        /// the handover point by half of that: 960 px, which is 0.75 x
        /// <see cref="NativeWidth"/> to the last bit.
        /// </para>
        /// </summary>
        public const double MeasuredLineSpatialPeriod = 1920.0;

        /// <summary>
        /// X of the near handover - where the upper half's incoming edge and the lower half's
        /// outgoing edge coincide. The left margin of the note field, in arcade px.
        /// See <see cref="GameplayPreviewProjector.ScanFieldLeft"/>.
        /// </summary>
        public const double MeasuredHandoverNearX = 156.5;

        /// <summary>
        /// X of the far handover, 960 px along from <see cref="MeasuredHandoverNearX"/>. The right
        /// margin of the note field, in arcade px - note that it leaves 163.5 px to the right
        /// edge, not 156.5, so the field is 7 px left of centre.
        /// </summary>
        public const double MeasuredHandoverFarX = 1116.5;

        /// <summary>
        /// The <c>bar_light.jpg</c> glow, 265x350, which corroborates the handovers independently
        /// of the sweep fit.
        ///
        /// <para>
        /// The client lights the incoming half's starting corner: upper-left at x -0.5..264.5,
        /// y 63.5..413.5, then lower-right at x 1014.5..1279.5, y 417.5..767.5, exact mirrors of
        /// each other, 114 captured frames each, alternating on a 222-frame cycle. At 60 fps that
        /// cycle is 3.70 s, which is <see cref="MeasuredScanSeconds"/>; and the switch happens
        /// about 17 frames before the handover it precedes, which lands where the sweep fit says
        /// the handovers are (frames 22880 and 23102). Two unrelated draw streams agreeing on the
        /// same cadence is why the field is treated as measured rather than fitted.
        /// </para>
        /// </summary>
        public const double MeasuredScanStartGlowWidth = 265.0;

        /// <summary>Height of the <c>bar_light</c> glow. See <see cref="MeasuredScanStartGlowWidth"/>.</summary>
        public const double MeasuredScanStartGlowHeight = 350.0;

        // ---- note field ----------------------------------------------------------------------

        /// <summary>
        /// Left margin of the note field as a fraction of the width - where an upper-half scan
        /// begins and a lower-half one ends.
        ///
        /// <para>
        /// Taken from the projector rather than restated here, because the scanline has to arrive
        /// at a note's X exactly when the note is due: the sweep and the placement have to be one
        /// number, and a second copy of it in this file is a second thing to drift.
        /// <see cref="GameplayPreviewProjector.ScanFieldLeft"/> carries the measurement and the
        /// argument for it.
        /// </para>
        /// </summary>
        public const double NoteMarginLeft = GameplayPreviewProjector.ScanFieldLeft;

        /// <summary>Right margin of the note field. See <see cref="NoteMarginLeft"/>.</summary>
        public const double NoteMarginRight = GameplayPreviewProjector.ScanFieldRight;

        /// <summary>
        /// Vertical inset of the lane stack inside a half, as a fraction of the half's height.
        /// The projector's <c>localY</c> runs 0.05 to 0.95, so the lanes occupy the middle 90%.
        /// </summary>
        public const double LaneInset = 0.05;

        /// <summary>
        /// Side rail width. <c>note\Common\sidebar.png</c> is a 400x350 strip of ten 40x350
        /// frames, one rail per half per side.
        /// </summary>
        public const double SidebarWidth = 40.0;

        /// <summary>
        /// Note frame size for a 3-line chart - Star Mixing - in arcade pixels.
        ///
        /// <para>
        /// Measured, and the measurement needs its own paragraph because the capture cannot make it.
        /// Almost every piece of note art is one geometry shared by both mixing modes:
        /// <c>note\star\0</c> and <c>note\pop\0</c> agree to the byte on <c>Note_Basic.png</c>
        /// 900x90, <c>longnote.png</c> 1160x116, <c>note_circle.png</c> 2420x121 and
        /// <c>notepressnote.png</c> 760x76. Exactly one asset is authored twice, at two different
        /// sizes: the repeat counter. <c>repeat_number_3line.png</c> is 1160x116, ten 116 px cells;
        /// <c>repeat_number_4line.png</c> is 900x90, ten 90 px cells - and the ink inside the cell
        /// scales with it, 50x46 against 40x36.
        /// </para>
        ///
        /// <para>
        /// That is the one asset that had to be re-authored, because it is the one asset that is
        /// text: a round glowing blob upscales invisibly and a digit does not. So the pair is the
        /// client telling us what it scales its notes to in each mode, and their own VCEs confirm
        /// it at the quad: <c>note\star\*\Repeat_Num_3line.vce</c> opens on a 116x116 note-local
        /// quad and <c>note\pop\*\Repeat_Num_4line.vce</c> on a 90x90 one, both then running the
        /// same 10-frame pop to x1.60. A quad equal to its own crop is drawn 1:1.
        /// </para>
        ///
        /// <para>
        /// Both sizes are one lane pitch of the mean half - 348/3 is 116.0 exactly, 348/4 is 87
        /// against an authored 90 - measured on the <em>uninset</em> half. That is why a measured
        /// note is still larger than <see cref="LaneHeight"/>: the 90% inset is ours, and the
        /// arcade's note fills the whole pitch.
        /// </para>
        /// </summary>
        public const double NoteFrameThreeLine = 116.0;

        /// <summary>
        /// Note frame size for a 4-line chart - Pop Mixing. See <see cref="NoteFrameThreeLine"/>
        /// for the measurement; this is the 90 px half of it.
        /// </summary>
        public const double NoteFrameFourLine = 90.0;

        /// <summary>
        /// The mean of the two halves, which is the height one lane pitch is measured on: the two
        /// authored note sizes are one mode's pitch of it, and it is the only height that is not a
        /// choice between 344 and 352 when the art has to serve both halves.
        /// </summary>
        public const double MeanHalfHeight = (UpperFieldHeight + LowerFieldHeight) / 2.0;

        /// <summary>
        /// Diameter of the hold hit effect, in arcade pixels.
        /// <c>CoolBomb\*\hold\note\note.vce</c> draws it note-local and centred at -175..175, and
        /// all six skin sets agree. It is a fixed size, not a multiple of the note or the lane:
        /// 350 px is a half's own height, so the effect covers the half it fires in whichever mode
        /// is playing. The cool flash beside it is 394x385 from <c>CoolBomb\*\cool\cool.vce</c>.
        /// </summary>
        public const double MeasuredHitEffectSize = 350.0;

        // ---- HUD ------------------------------------------------------------------------------

        /// <summary>Groove gauge fill: a 434x20 quad at (422.5,24.5) from a 512x256 sheet.</summary>
        public const double GrooveLeft = 422.5;

        /// <summary>Top of the groove gauge.</summary>
        public const double GrooveTop = 24.5;

        /// <summary>Full width of the groove gauge quad.</summary>
        public const double GrooveWidth = 434.0;

        /// <summary>Height of the groove gauge quad.</summary>
        public const double GrooveHeight = 20.0;

        /// <summary>
        /// Width the runtime clips the groove fill to at 100%. Twelve pixels narrower than the
        /// quad, because the source fill (<c>panel\energy.png</c>, 440x31 with opaque content
        /// only at x2..438 y7..26) has soft edges that are meant to be cropped.
        /// </summary>
        public const double GrooveClipWidth = 422.0;

        /// <summary>Header indicator lights: four 114x94 quads cut from one 1140x94 strip.</summary>
        public const double HeaderLightWidth = 114.0;

        /// <summary>Height of a header indicator light.</summary>
        public const double HeaderLightHeight = 94.0;

        /// <summary>Left edge of each header indicator light.</summary>
        public static readonly double[] HeaderLightLeft = { 430.5, 530.5, 630.5, 730.5 };

        /// <summary>
        /// Score digit cell: a 16x20 quad at (1062.5,15.5). The source
        /// (<c>panel\star_ingame_info\score_num.png</c>, 256x32) holds eleven glyphs on a 16 px
        /// pitch - digits 0-9 then a dash.
        /// </summary>
        public const double ScoreDigitLeft = 1062.5;

        /// <summary>Top of the score digit cell.</summary>
        public const double ScoreDigitTop = 15.5;

        /// <summary>Width of one score digit quad.</summary>
        public const double ScoreDigitWidth = 16.0;

        /// <summary>Height of one score digit quad.</summary>
        public const double ScoreDigitHeight = 20.0;

        /// <summary>
        /// Advance between score digits on screen. The artist's own coordinate note
        /// (<c>panel\new</c>, in Korean) records that the digits are 16 px apart in the image but
        /// step 10 px on screen, so the glyphs deliberately overlap.
        /// </summary>
        public const double ScoreDigitAdvance = 10.0;

        /// <summary>Combo digit cell: 13x17 at (1065.5,37.5), a smaller face than the score.</summary>
        public const double ComboDigitLeft = 1065.5;

        /// <summary>Top of the combo digit cell.</summary>
        public const double ComboDigitTop = 37.5;

        /// <summary>Width of one combo digit quad.</summary>
        public const double ComboDigitWidth = 13.0;

        /// <summary>Height of one combo digit quad.</summary>
        public const double ComboDigitHeight = 17.0;

        /// <summary>Guide / fever slot: 164x40 at (1098.5,14.5) from a 256x64 texture.</summary>
        public const double GuideSlotLeft = 1098.5;

        /// <summary>Top of the guide / fever slot.</summary>
        public const double GuideSlotTop = 14.5;

        /// <summary>Width of the guide / fever slot.</summary>
        public const double GuideSlotWidth = 164.0;

        /// <summary>Height of the guide / fever slot.</summary>
        public const double GuideSlotHeight = 40.0;

        // ---- fitting --------------------------------------------------------------------------

        /// <summary>Top of one half, in arcade pixels.</summary>
        public static double HalfTop(bool isTopHalf)
        {
            return isTopHalf ? UpperFieldTop : LowerFieldTop;
        }

        /// <summary>Height of one half, in arcade pixels. 344 above, 352 below.</summary>
        public static double HalfHeight(bool isTopHalf)
        {
            return isTopHalf ? UpperFieldHeight : LowerFieldHeight;
        }

        /// <summary>
        /// Height of one lane inside a half, in arcade pixels. The lane stack occupies the middle
        /// 90% of the half, so a 4-lane upper half gives 77.4 px lanes and the lower 79.2 px.
        /// </summary>
        public static double LaneHeight(bool isTopHalf, int laneCount)
        {
            if (laneCount < 1)
            {
                laneCount = 1;
            }
            return HalfHeight(isTopHalf) * (1.0 - (2.0 * LaneInset)) / laneCount;
        }

        /// <summary>
        /// The size the arcade draws a note frame at, in arcade pixels, for a chart with
        /// <paramref name="laneCount"/> lines.
        ///
        /// <para>
        /// 3 and 4 are measured - see <see cref="NoteFrameThreeLine"/> - and are the only two the
        /// client has art for, because they are the only two mixing modes. Anything else is an
        /// extrapolation of the rule the two measurements share, one lane pitch of
        /// <see cref="MeanHalfHeight"/>, and is marked as one here rather than hidden behind a
        /// number that looks measured.
        /// </para>
        /// </summary>
        public static double NoteFrameSize(int laneCount)
        {
            if (laneCount == 3)
            {
                return NoteFrameThreeLine;
            }
            if (laneCount == 4)
            {
                return NoteFrameFourLine;
            }
            return MeanHalfHeight / (laneCount < 1 ? 1 : laneCount);
        }

        /// <summary>
        /// Recovers the projector's within-half lane coordinate from its whole-playfield
        /// normalised Y.
        ///
        /// <para>
        /// The projector emits <c>Y = localY / 2</c> for the upper half and
        /// <c>Y = 0.5 + localY / 2</c> for the lower, i.e. it assumes equal halves. Undoing that
        /// and re-placing <c>localY</c> into the measured band is what lets the renderer honour
        /// the real 344/352 split without the projector having to know about it.
        /// </para>
        /// </summary>
        public static double ToHalfLocalY(double normalizedY, bool isTopHalf)
        {
            return isTopHalf ? normalizedY * 2.0 : (normalizedY - 0.5) * 2.0;
        }

        /// <summary>
        /// Letterboxes the 1280x768 arcade frame into <paramref name="viewport"/>, centred,
        /// preserving aspect. Returns an unusable fit when there is no room to draw.
        /// </summary>
        public static TechnikaPlayfieldFit Fit(Size viewport)
        {
            if (viewport.Width <= 0 || viewport.Height <= 0 ||
                double.IsNaN(viewport.Width) || double.IsNaN(viewport.Height))
            {
                return default(TechnikaPlayfieldFit);
            }

            double scale = Math.Min(viewport.Width / NativeWidth, viewport.Height / NativeHeight);
            if (scale <= 0 || double.IsInfinity(scale))
            {
                return default(TechnikaPlayfieldFit);
            }

            return new TechnikaPlayfieldFit(
                scale,
                (viewport.Width - (NativeWidth * scale)) / 2.0,
                (viewport.Height - (NativeHeight * scale)) / 2.0);
        }
    }

    /// <summary>
    /// A uniform scale plus a centring offset: the one transform between arcade pixels and the
    /// element's own coordinates. A struct, so a per-frame draw pass costs no allocation.
    /// </summary>
    internal struct TechnikaPlayfieldFit
    {
        public TechnikaPlayfieldFit(double scale, double offsetX, double offsetY)
        {
            Scale = scale;
            OffsetX = offsetX;
            OffsetY = offsetY;
        }

        /// <summary>Screen units per arcade pixel. Zero when the viewport is unusable.</summary>
        public double Scale { get; private set; }

        /// <summary>Left edge of the letterboxed frame.</summary>
        public double OffsetX { get; private set; }

        /// <summary>Top edge of the letterboxed frame.</summary>
        public double OffsetY { get; private set; }

        /// <summary>False when there is not enough room to draw anything.</summary>
        public bool IsUsable
        {
            get { return Scale > 0; }
        }

        /// <summary>The whole arcade frame, in screen units.</summary>
        public Rect Frame
        {
            get
            {
                return new Rect(
                    OffsetX,
                    OffsetY,
                    TechnikaPlayfieldMetrics.NativeWidth * Scale,
                    TechnikaPlayfieldMetrics.NativeHeight * Scale);
            }
        }

        public double X(double nativeX)
        {
            return OffsetX + (nativeX * Scale);
        }

        public double Y(double nativeY)
        {
            return OffsetY + (nativeY * Scale);
        }

        public double Length(double nativeLength)
        {
            return nativeLength * Scale;
        }

        public Rect Quad(double nativeLeft, double nativeTop, double nativeWidth, double nativeHeight)
        {
            return new Rect(X(nativeLeft), Y(nativeTop), nativeWidth * Scale, nativeHeight * Scale);
        }

        /// <summary>One half of the playfield, in screen units.</summary>
        public Rect Half(bool isTopHalf)
        {
            return Quad(
                0,
                TechnikaPlayfieldMetrics.HalfTop(isTopHalf),
                TechnikaPlayfieldMetrics.NativeWidth,
                TechnikaPlayfieldMetrics.HalfHeight(isTopHalf));
        }

        /// <summary>
        /// Places a projected note. X is the projector's normalised X across the full width; Y is
        /// resolved through the measured 344/352 half split rather than by halving, so a note
        /// lands on its real arcade lane centre.
        /// </summary>
        public Point Note(double normalizedX, double normalizedY, bool isTopHalf)
        {
            double localY = TechnikaPlayfieldMetrics.ToHalfLocalY(normalizedY, isTopHalf);
            return new Point(
                X(normalizedX * TechnikaPlayfieldMetrics.NativeWidth),
                Y(TechnikaPlayfieldMetrics.HalfTop(isTopHalf) +
                    (localY * TechnikaPlayfieldMetrics.HalfHeight(isTopHalf))));
        }
    }
}
