using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DJMaxEditor.Diagnostics;
using DJMaxEditor.Preview;

namespace DJMaxEditor.Studio.Preview
{
    /// <summary>
    /// One note glyph: a single image, or the horizontal strip of frames the arcade animates it
    /// from.
    ///
    /// <para>
    /// TECHNIKA's notes are not still pictures. Every head is a ten frame loop - the tap's inner
    /// bar fills and drains, the long note's arrow sweeps through it - and the ring around an
    /// approaching note is twenty frames of a glow that opens and settles. Taking frame zero and
    /// discarding the rest leaves a flat disc, which is precisely what makes a playfield read as a
    /// generic vertical-scroller skin rather than as TECHNIKA. Frames are cropped once, frozen,
    /// and handed out by position in the loop.
    /// </para>
    ///
    /// <para>
    /// Every file in the set is the same ten frames; what differs is how wide one frame is. A head
    /// is square - ninety, a hundred and sixteen - a cap is narrower than it is tall, and a body is
    /// a single column of pixels, the cross-section the run is drawn out of. See
    /// <see cref="FrameWidthOf"/>: the frame count is the constant and the width is derived from
    /// it, which is the way round the TechMania skin manifest for this set states it too
    /// (<c>"columns": 10</c> on all fifteen entries, whatever the file's dimensions).
    /// </para>
    /// </summary>
    internal sealed class TechnikaNoteSprite
    {
        /// <summary>
        /// Frames in an arcade note strip. Every animated piece in the TECHNIKA note set is a ten
        /// frame loop - heads, caps, run lines and bodies alike - except <c>note_circle</c>, which is
        /// twenty and is square-framed, so it is read by its frame height instead.
        /// </summary>
        private const int StripFrames = 10;

        /// <summary>
        /// How much of a cap frame's width is the body cross-section, as a fraction. Two pixels of a
        /// thirty pixel frame, which is what the legacy renderer takes from <c>longnoteline</c>.
        ///
        /// <para>
        /// Only the drag curve needs this. The other two hold families are authored with a body
        /// sheet of their own - see <see cref="TechnikaNoteSprites.TrailBody"/> - and take their
        /// cross-section from that instead of cutting one out of the cap.
        /// </para>
        /// </summary>
        private const double StemFraction = 0.08;

        private readonly ImageSource[] _frames;

        private int _peakFrame = -1;

        private double[] _sweepOffsets;

        private ImageSource[] _stems;

        private TechnikaNoteSprite(ImageSource[] frames, double frameSize)
        {
            _frames = frames;
            FrameSize = frameSize;
            FrameWidth = frameSize;
        }

        private TechnikaNoteSprite(ImageSource[] frames, double frameSize, double frameWidth)
        {
            _frames = frames;
            FrameSize = frameSize;
            FrameWidth = frameWidth;
        }

        /// <summary>Frames in the loop. One for a still image.</summary>
        public int FrameCount
        {
            get { return _frames.Length; }
        }

        /// <summary>
        /// Edge of one frame in the sprite's own pixels. Exposed so one sprite can be scaled
        /// against another - the arcade authors its ring larger than the head it surrounds - rather
        /// than against a constant that would be wrong for a different note set.
        /// </summary>
        public double FrameSize { get; private set; }

        /// <summary>
        /// One frame's width in the sprite's own pixels - the same as <see cref="FrameSize"/> for a
        /// strip of square frames, and the whole image for art that is not one. Exposed so a piece
        /// meant to be repeated along a run, like the dotted bar between repeat notes, can be tiled
        /// at its authored aspect instead of stretched to whatever length the run happens to be.
        /// </summary>
        public double FrameWidth { get; private set; }


        public bool IsAnimated
        {
            get { return _frames.Length > 1; }
        }

        /// <summary>
        /// The frame that puts the most light on screen, measured from the pixels the first time it
        /// is asked for.
        ///
        /// <para>
        /// The arcade's approach glow does not build to its end. <c>note_circle</c> opens from a
        /// sliver to a full ring across its first half and closes back to a sliver across its second,
        /// so the frame a one-shot approach has to finish on is the peak in the middle - finish on
        /// the last frame and the glow is at its faintest at the exact moment the note is due, which
        /// is the moment it exists to shout about. Measured rather than assumed to be the midpoint,
        /// because where a strip peaks is a property of whichever note set is loaded.
        /// </para>
        /// </summary>
        public int PeakFrame
        {
            get
            {
                if (_peakFrame < 0)
                {
                    _peakFrame = FindPeakFrame();
                }
                return _peakFrame;
            }
        }

        /// <summary>
        /// The frame at a position in a repeating loop. <paramref name="phase"/> is wrapped, so a
        /// caller can hand over a musical position that keeps counting up and never has to do the
        /// modulo itself.
        /// </summary>
        public ImageSource Frame(double phase)
        {
            if (_frames.Length == 1 || double.IsNaN(phase) || double.IsInfinity(phase))
            {
                return _frames[0];
            }

            double wrapped = phase - Math.Floor(phase);
            int index = (int)(wrapped * _frames.Length);
            if (index < 0) { index = 0; }
            if (index >= _frames.Length) { index = _frames.Length - 1; }
            return _frames[index];
        }

        /// <summary>
        /// The frame at a one-shot progress across the whole sequence: 0 gives the first frame and 1
        /// the last. Clamped, not wrapped - an effect played once must not restart on its final pixel
        /// - and distinct from <see cref="Swell"/>, which stops at the brightest frame because the
        /// thing it plays is an approach that has to peak on arrival rather than run to its end.
        /// </summary>
        public ImageSource Shot(double progress)
        {
            if (_frames.Length == 1 || double.IsNaN(progress) || progress <= 0.0)
            {
                return _frames[0];
            }
            if (progress >= 1.0)
            {
                return _frames[_frames.Length - 1];
            }

            int index = (int)(progress * _frames.Length);
            if (index < 0) { index = 0; }
            if (index >= _frames.Length) { index = _frames.Length - 1; }
            return _frames[index];
        }

        /// <summary>
        /// The leftmost columns of the frame at <paramref name="phase"/>, to be stretched along a
        /// hold's body while the frame itself closes the far end.
        ///
        /// <para>
        /// This is the legacy TECHNIKA renderer's own construction - it draws <c>longnoteline</c>
        /// twice, once whole as the cap and once from a two pixel source rectangle stretched over the
        /// duration. It is the right answer for the drag curve, which the arcade authors as a cap and
        /// nothing else, and it is the fallback for a set that is missing a body sheet. Where a body
        /// sheet does exist it is the better source: measured, <c>long_note_line</c> column 9 and
        /// <c>long_note_end</c> frame 9 carry the same 16..74 cross-section, so the two are one
        /// animation authored in two files, and the body carries a per-frame colour ramp no cut
        /// through the cap can reproduce.
        /// </para>
        ///
        /// <para>
        /// The left edge is the cut through the tube: the flat side that abuts the note, as against
        /// the rounded outer edge on the right. Stretching from the other end would smear the outline
        /// down the whole body.
        /// </para>
        /// </summary>
        public ImageSource Stem(double phase)
        {
            if (_stems == null)
            {
                _stems = new ImageSource[_frames.Length];
            }

            int index = 0;
            if (_frames.Length > 1 && !double.IsNaN(phase) && !double.IsInfinity(phase))
            {
                double wrapped = phase - Math.Floor(phase);
                index = (int)(wrapped * _frames.Length);
                if (index < 0) { index = 0; }
                if (index >= _frames.Length) { index = _frames.Length - 1; }
            }

            if (_stems[index] != null)
            {
                return _stems[index];
            }

            ImageSource stem = _frames[index];
            BitmapSource bitmap = stem as BitmapSource;
            if (bitmap != null && bitmap.PixelWidth > 1)
            {
                int columns = (int)Math.Round(bitmap.PixelWidth * StemFraction);
                if (columns < 1) { columns = 1; }
                if (columns > bitmap.PixelWidth) { columns = bitmap.PixelWidth; }
                try
                {
                    CroppedBitmap cropped = new CroppedBitmap(
                        bitmap, new Int32Rect(0, 0, columns, bitmap.PixelHeight));
                    cropped.Freeze();
                    stem = cropped;
                }
                catch (ArgumentException)
                {
                    // A frame that will not crop is not worth losing the body over - stretching the
                    // whole frame is wrong but visible, and the cap still draws.
                }
            }

            _stems[index] = stem;
            return stem;
        }

        /// <summary>
        /// The frame at a one-shot progress that finishes on <see cref="PeakFrame"/>: 0 gives the
        /// first frame and 1 the brightest. Clamped rather than wrapped, so a glow played once over
        /// an approach cannot snap back to a sliver on the last pixel of it.
        /// </summary>
        public ImageSource Swell(double progress)
        {
            if (_frames.Length == 1 || double.IsNaN(progress) || progress <= 0.0)
            {
                return _frames[0];
            }

            int peak = PeakFrame;
            if (progress >= 1.0)
            {
                return _frames[peak];
            }

            int index = (int)Math.Round(progress * peak);
            if (index < 0) { index = 0; }
            if (index > peak) { index = peak; }
            return _frames[index];
        }

        /// <summary>
        /// The frame whose light sits <paramref name="offsetInFrames"/> of a frame width from the
        /// note's centre, or null when the strip's light never reaches that far - so a caller draws
        /// nothing rather than the nearest thing it has.
        ///
        /// <para>
        /// <c>note_circle</c> is not a ring that opens on the spot. Measured across its twenty frames
        /// the light starts at 12% of the frame width and finishes at 87%, always vertically centred:
        /// it is a crescent the sweep <em>drags through</em> the note, left to right, fattening as it
        /// crosses. Played as a swell it is only ever drawn in its first frames, which puts a thin
        /// crescent a third of a lane to the left of a note that is still most of a second away - the
        /// stray arcs the playfield was covered in. Asking for it by where the sweep actually is
        /// instead makes it the piece of light it was drawn as, and makes it appear only while the
        /// sweep is genuinely over the note.
        /// </para>
        ///
        /// <para>
        /// Offsets are measured off the pixels, once, because where a strip's light sits is a property
        /// of whichever note set is loaded and not something a renderer can be told.
        /// </para>
        /// </summary>
        public ImageSource Sweep(double offsetInFrames)
        {
            if (_frames.Length == 1)
            {
                return _frames[0];
            }

            double[] offsets = SweepOffsets();
            int best = -1;
            double nearest = 0.0;
            for (int i = 0; i < offsets.Length; i++)
            {
                double gap = Math.Abs(offsets[i] - offsetInFrames);
                if (best < 0 || gap < nearest)
                {
                    best = i;
                    nearest = gap;
                }
            }

            // Outside the strip's own travel by more than the step between two of its frames, the
            // honest answer is that this strip has no picture of the sweep being there.
            double step = Math.Abs(offsets[offsets.Length - 1] - offsets[0]) /
                Math.Max(1, offsets.Length - 1);
            if (best < 0 || nearest > Math.Max(step, 1e-6))
            {
                return null;
            }
            return _frames[best];
        }

        /// <summary>
        /// Where each frame's light sits, as a signed fraction of the frame width from its centre.
        /// Alpha-weighted, so a wide faint arc and a narrow bright one are placed by where the light
        /// actually is rather than by the extent of the pixels that are not quite transparent.
        /// </summary>
        private double[] SweepOffsets()
        {
            if (_sweepOffsets == null)
            {
                var offsets = new double[_frames.Length];
                for (int i = 0; i < _frames.Length; i++)
                {
                    offsets[i] = Centroid(_frames[i]);
                }
                _sweepOffsets = offsets;
            }
            return _sweepOffsets;
        }

        /// <summary>
        /// The alpha-weighted horizontal centre of a frame's light, as a signed fraction of its
        /// width from the middle. Zero when nothing can be measured, which places an unreadable
        /// frame on the note rather than off the side of it.
        /// </summary>
        private static double Centroid(ImageSource frame)
        {
            BitmapSource bitmap = frame as BitmapSource;
            if (bitmap == null || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
            {
                return 0.0;
            }

            try
            {
                FormatConvertedBitmap converted =
                    new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                converted.Freeze();

                int width = converted.PixelWidth;
                int stride = width * 4;
                byte[] pixels = new byte[stride * converted.PixelHeight];
                converted.CopyPixels(pixels, stride, 0);

                double weight = 0.0;
                double moment = 0.0;
                for (int y = 0; y < converted.PixelHeight; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < width; x++)
                    {
                        double alpha = pixels[row + (x * 4) + 3];
                        weight += alpha;
                        moment += alpha * x;
                    }
                }
                if (weight <= 0.0)
                {
                    return 0.0;
                }
                return ((moment / weight) - ((width - 1) / 2.0)) / width;
            }
            catch (NotSupportedException)
            {
                return 0.0;
            }
            catch (InvalidOperationException)
            {
                return 0.0;
            }
        }

        /// <summary>
        /// A sprite whose frames were authored as separate numbered files rather than as one strip -
        /// which is how the arcade keeps every effect under <c>CoolBomb</c>, twenty-three files for
        /// the tap burst alone. The frames are taken in the order given; the first one's dimensions
        /// are the frame size, since a sequence's files are one canvas throughout.
        /// </summary>
        internal static TechnikaNoteSprite Sequence(IList<ImageSource> frames)
        {
            if (frames == null || frames.Count == 0)
            {
                return null;
            }

            var copy = new ImageSource[frames.Count];
            for (int i = 0; i < frames.Count; i++)
            {
                copy[i] = frames[i];
            }

            var first = copy[0] as BitmapSource;
            double height = first == null ? 0.0 : first.PixelHeight;
            double width = first == null ? 0.0 : first.PixelWidth;
            return new TechnikaNoteSprite(copy, height, width);
        }

        /// <summary>
        /// Slices a horizontal strip into its frames, or keeps the image whole when it is not a
        /// strip. <see cref="FrameWidthOf"/> decides which it is; a single glyph comes back as a
        /// strip of one, so a strip and a still image drop in through the same path.
        /// </summary>
        internal static TechnikaNoteSprite Slice(BitmapSource source)
        {
            int width = source.PixelWidth;
            int height = source.PixelHeight;
            if (width <= 0 || height <= 0)
            {
                return new TechnikaNoteSprite(new ImageSource[] { source }, height, width);
            }

            int frameWidth = FrameWidthOf(width, height);
            if (frameWidth <= 0)
            {
                return new TechnikaNoteSprite(new ImageSource[] { source }, height, width);
            }

            int count = width / frameWidth;
            ImageSource[] frames = new ImageSource[count];
            for (int i = 0; i < count; i++)
            {
                CroppedBitmap frame = new CroppedBitmap(
                    source, new Int32Rect(i * frameWidth, 0, frameWidth, height));
                frame.Freeze();
                frames[i] = frame;
            }
            return new TechnikaNoteSprite(frames, height, frameWidth);
        }

        /// <summary>
        /// One frame's width in a horizontal strip, or 0 for art that is a single image.
        ///
        /// <para>
        /// Ten frames first, because that is what the whole note set is: <c>Note_Basic</c> is ten
        /// 90x90 in a 900x90 sheet, <c>longnoteline</c> ten 30x76, <c>long_note_end</c> ten 25x90,
        /// and <c>long_note_line</c> ten <em>1x90</em> in a sheet ten pixels wide. Squareness is not
        /// the test - it never was. It is a coincidence that holds for the heads.
        /// </para>
        ///
        /// <para>
        /// Reading those ten pixel sheets as one image instead is what the playfield's worst artefact
        /// was. They are the bodies a hold or a run is drawn out of, and each column is a different
        /// frame of the same pulse: <c>long_note_line</c> runs 111,0,98 at column 0 down to 49,0,51
        /// in the middle and back up to 95,0,85 at column 9, brightest and tallest on the last. Kept
        /// whole and stretched along a note's duration, all ten columns paint at once - a banded
        /// ten-shade ramp down the length of every hold, frozen there - instead of one shade
        /// animating. Column 9's 16..74 cross-section matching <c>long_note_end</c> frame 9's is the
        /// confirmation: body and cap are one animation, indexed the same way.
        /// </para>
        ///
        /// <para>
        /// A frame wider than the sheet is tall means those ten were not frames, which is how
        /// <c>note_circle</c> - twenty 121px frames in 2420x121 - reaches the square rule below. A
        /// sheet that is square is one glyph and nothing else: 90x90 divides by ten as readily as
        /// 900x90 does, and reading a lone <c>videoStart</c> as ten nine-pixel frames is the same
        /// mistake in the other direction.
        /// </para>
        /// </summary>
        private static int FrameWidthOf(int width, int height)
        {
            if (width == height)
            {
                return 0;
            }
            if (width % StripFrames == 0 && width / StripFrames <= height)
            {
                return width / StripFrames;
            }
            if (width % height == 0)
            {
                return height;
            }
            return 0;
        }

        /// <summary>
        /// The brightest frame's index, or the midpoint when the measurement cannot single one out.
        /// Never frame 0 for a strip: a peak at the very start would collapse <see cref="Swell"/>
        /// onto one image, and a strip whose frames all measure the same is one this cannot speak
        /// about - the midpoint is where every arcade strip peaks anyway.
        /// </summary>
        private int FindPeakFrame()
        {
            int best = 0;
            long brightest = -1;
            for (int i = 0; i < _frames.Length; i++)
            {
                long light = Light(_frames[i]);
                if (light > brightest)
                {
                    brightest = light;
                    best = i;
                }
            }
            return best > 0 ? best : _frames.Length / 2;
        }

        /// <summary>
        /// Alpha weighted by colour: how much light a frame actually puts on screen. Transparency
        /// alone would not separate a wide faint ring from a narrow bright one, and colour alone
        /// would count pixels that are not drawn at all.
        /// </summary>
        private static long Light(ImageSource frame)
        {
            BitmapSource bitmap = frame as BitmapSource;
            if (bitmap == null)
            {
                return 0;
            }

            try
            {
                FormatConvertedBitmap converted =
                    new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                converted.Freeze();

                int stride = converted.PixelWidth * 4;
                byte[] pixels = new byte[stride * converted.PixelHeight];
                converted.CopyPixels(pixels, stride, 0);

                long total = 0;
                for (int i = 0; i + 3 < pixels.Length; i += 4)
                {
                    total += pixels[i + 3] * (pixels[i] + pixels[i + 1] + pixels[i + 2]);
                }
                return total;
            }
            catch (NotSupportedException)
            {
                // A frame in a format WPF will not convert. It simply loses the comparison, and if
                // every frame does, FindPeakFrame falls back to the midpoint.
                return 0;
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// Note glyphs for the TECHNIKA playfield, resolved once per kind and frozen.
    ///
    /// <para>
    /// Two sources, in order. First an optional local folder the owner can drop their own
    /// extracted arcade sprites into; if that yields nothing, the arcade sheets already in
    /// this repository for the editor timeline (<c>Shared\Resources</c>, linked into this
    /// project under <c>Timeline/Notes</c> as WPF resources) are sliced the same way. The
    /// fallback is not a placeholder - it is the arcade's own animation strips, heads and
    /// trails included - so a fresh clone gets real note art with no setup at all. Only the
    /// run lines, actively-held trail variants, approach ring and hit burst are absent and
    /// keep their vector fallbacks.
    /// </para>
    ///
    /// <para>
    /// The local folder exists because the arcade's own note art is much more specific than any
    /// generic glyph. TECHNIKA colours by note type rather than by lane - magenta taps, green long
    /// heads, blue holds, purple repeats - the shapes are authored for a two-way playfield, and
    /// every one of them is an animation strip rather than a still. Owners who have extracted
    /// their own copy get that exactly. Nobody else has to, and nothing is redistributed: the
    /// folder is gitignored and empty in a fresh clone, exactly like the RESPECT skin folder that
    /// established the pattern.
    /// </para>
    ///
    /// <para>
    /// The arcade ships six complete note sets side by side in numbered folders, so a folder whose
    /// sprites sit one level down is accepted too - copying the tree wholesale is enough, with no
    /// flattening or renaming.
    /// </para>
    /// </summary>
    internal sealed class TechnikaNoteSprites
    {
        /// <summary>Environment override for the local sprite folder, for owners who keep it elsewhere.</summary>
        private const string PathVariable = "DJMAX_EDITOR_TECHNIKA_ASSETS";

        /// <summary>
        /// The glow that opens around an approaching note. Arcade only - the packaged set has no
        /// equivalent, and the renderer tweens a vector ring when it is absent.
        /// </summary>
        private const string ArcadeRing = "note_circle";

        /// <summary>
        /// The arcade's folder of hit effects, and the one this draws: <c>CoolBomb\&lt;set&gt;\cool</c>,
        /// twenty-three files named <c>cool_0000.png</c> upward.
        ///
        /// <para>
        /// The tap burst only. The set also carries <c>good</c> and <c>max</c> for the other
        /// judgements, and separate animations for a hold's start, body and release - a preview has
        /// no judgement to report and no player to release a hold early, so the burst a note is
        /// struck with is the whole of what can honestly be drawn here.
        /// </para>
        /// </summary>
        private const string ArcadeEffectFolder = "CoolBomb";
        private const string ArcadeHitEffect = "cool";

        /// <summary>
        /// How many folders to try when looking for the effect tree, counting the note folder
        /// itself. Five means four levels above it, which reaches <c>MainGame</c> from
        /// <c>MainGame\note\pop\0</c> - the furthest the arcade's own layout puts between them.
        /// Stopping there keeps a mistaken environment variable from walking a whole drive.
        /// </summary>
        private const int EffectSearchDepth = 5;


        /// <summary>
        /// Packaged glyph per note kind. These are the same arcade sheets the timeline draws,
        /// linked under <c>Timeline/Notes</c> and reached by the arcade's own file names - so the
        /// fallback is the real art rather than a stand-in glyph. That matters most for
        /// <see cref="GameplayPreviewNoteKind.Drag"/>: the old six-glyph TechMania set had no
        /// slide note at all and handed it the blue hold head, which made the preview report
        /// every drag as a hold. The strips are sliced exactly as a local extraction would be.
        /// </summary>
        private static readonly Dictionary<GameplayPreviewNoteKind, string> PackagedGlyphs =
            new Dictionary<GameplayPreviewNoteKind, string>
            {
                { GameplayPreviewNoteKind.Basic, "Note_Basic" },
                { GameplayPreviewNoteKind.Drag, "longnote" },
                { GameplayPreviewNoteKind.Generic, "Note_Basic" },
                { GameplayPreviewNoteKind.ChainHead, "notepressstart" },
                { GameplayPreviewNoteKind.ChainNode, "notepressnote" },
                { GameplayPreviewNoteKind.Hold, "longnotehold" },
                { GameplayPreviewNoteKind.RepeatHead, "noterepeat" },
                { GameplayPreviewNoteKind.RepeatHeadHold, "noterepeat" },
                { GameplayPreviewNoteKind.Repeat, "repeattail" },
                { GameplayPreviewNoteKind.RepeatHold, "repeattail" },
            };

        /// <summary>
        /// The arcade's own file name per note kind, for the local folder. Names are the game's,
        /// so an owner can copy files across without renaming them.
        ///
        /// <para>
        /// Taken from the legacy editor's own TECHNIKA renderer, which is the authority on this
        /// grammar: <c>TechnikaEventRenderer.RenderNote</c> keys art off the note attribute, and the
        /// two long families are separate notes rather than two states of one. Attribute 0 carrying
        /// a duration over six - the projector's <c>Drag</c> - is the green-yellow <c>longnote</c>,
        /// and attribute 12 - the projector's <c>Hold</c> - is the blue <c>longnotehold</c>. Both
        /// were wrong here: <c>Drag</c> shared the magenta tap, so the slide note never appeared at
        /// all, and <c>Hold</c> wore the green-yellow head that belongs to the other one.
        /// </para>
        /// </summary>
        private static readonly Dictionary<GameplayPreviewNoteKind, string> ArcadeGlyphs =
            new Dictionary<GameplayPreviewNoteKind, string>
            {
                { GameplayPreviewNoteKind.Basic, "Note_Basic" },
                { GameplayPreviewNoteKind.Drag, "longnote" },
                { GameplayPreviewNoteKind.Generic, "Note_Basic" },
                { GameplayPreviewNoteKind.ChainHead, "notepressstart" },
                { GameplayPreviewNoteKind.ChainNode, "notepressnote" },
                { GameplayPreviewNoteKind.Hold, "longnotehold" },
                { GameplayPreviewNoteKind.RepeatHead, "noterepeat" },
                { GameplayPreviewNoteKind.RepeatHeadHold, "noterepeat" },
                { GameplayPreviewNoteKind.Repeat, "repeattail" },
                { GameplayPreviewNoteKind.RepeatHold, "repeattail" },
            };

        /// <summary>
        /// The cap that closes a held note's body at the far end, per kind. A separate axis from
        /// <see cref="ArcadeLines"/>: a run's line joins several notes, whereas this is one note's
        /// own duration.
        ///
        /// <para>
        /// One file per long family, from the legacy renderer and confirmed by the TechMania skin
        /// manifest for this set: attribute 0's drag curve is <c>longnoteline</c>
        /// (<c>dragCurve</c>), attribute 12's hold closes with <c>line_nor_end</c>
        /// (<c>holdTrailEnd</c>), and a repeat carrying a duration closes with <c>long_note_end</c>
        /// (<c>repeatHoldTrailEnd</c>).
        /// </para>
        ///
        /// <para>
        /// <c>line_nor_end</c> rather than the <c>line_in_end</c> this used to draw: "nor" and "in"
        /// are the resting and actively-held variants of the same cap, and a preview shows a hold at
        /// rest for all but the moment the sweep is inside it. See
        /// <see cref="ArcadeHeldTrailCaps"/>.
        /// </para>
        /// </summary>
        private static readonly Dictionary<GameplayPreviewNoteKind, string> ArcadeTrailCaps =
            new Dictionary<GameplayPreviewNoteKind, string>
            {
                { GameplayPreviewNoteKind.Drag, "longnoteline" },
                { GameplayPreviewNoteKind.Hold, "line_nor_end" },
                { GameplayPreviewNoteKind.RepeatHeadHold, "long_note_end" },
                { GameplayPreviewNoteKind.RepeatHold, "long_note_end" },
            };

        /// <summary>
        /// The body a held note runs between its head and its cap, per kind: the arcade's own
        /// cross-section sheet, ten frames one pixel wide.
        ///
        /// <para>
        /// The manifest names these <c>holdTrail</c> and <c>repeatHoldTrail</c>, and the pixels agree
        /// - <c>long_note_line</c> column 9 and <c>long_note_end</c> frame 9 share a cross-section, so
        /// body and cap are one animation split across two files. Cutting the body out of the cap
        /// instead, which is what <see cref="TechnikaNoteSprite.Stem"/> does, loses the ramp the body
        /// sheet carries along its columns.
        /// </para>
        ///
        /// <para>
        /// No entry for the drag curve: the arcade authors that one as a cap alone, so its body stays
        /// a cut through it.
        /// </para>
        /// </summary>
        private static readonly Dictionary<GameplayPreviewNoteKind, string> ArcadeTrailBodies =
            new Dictionary<GameplayPreviewNoteKind, string>
            {
                { GameplayPreviewNoteKind.Hold, "longholdgauge" },
                { GameplayPreviewNoteKind.RepeatHeadHold, "long_note_line" },
                { GameplayPreviewNoteKind.RepeatHold, "long_note_line" },
            };

        /// <summary>
        /// What a hold's body becomes while it is actually being held - <c>holdOngoingTrail</c> in the
        /// manifest. Measured, <c>longholdgaugein</c> is the same shape lit: 0,57,111 against
        /// <c>longholdgauge</c>'s 0,2,111 at the same column. Kinds absent from this table keep their
        /// resting body throughout, which is all the arcade gives them.
        /// </summary>
        private static readonly Dictionary<GameplayPreviewNoteKind, string> ArcadeHeldTrailBodies =
            new Dictionary<GameplayPreviewNoteKind, string>
            {
                { GameplayPreviewNoteKind.Hold, "longholdgaugein" },
            };

        /// <summary>
        /// The cap that goes with <see cref="ArcadeHeldTrailBodies"/>. The manifest has no slot for
        /// this - TechMania's format stops at an ongoing body - but the arcade ships the pair, and
        /// <c>line_in_end</c> is <c>line_nor_end</c>'s "in" to <c>longholdgaugein</c>'s.
        /// </summary>
        private static readonly Dictionary<GameplayPreviewNoteKind, string> ArcadeHeldTrailCaps =
            new Dictionary<GameplayPreviewNoteKind, string>
            {
                { GameplayPreviewNoteKind.Hold, "line_in_end" },
            };

        /// <summary>
        /// The line the arcade runs between the members of a group. A chain and a repeat run are
        /// several notes joined by one bar, not one note with a duration, so the line is art in its
        /// own right rather than a stretched head.
        /// </summary>
        private static readonly Dictionary<GameplayPreviewNoteKind, string> ArcadeLines =
            new Dictionary<GameplayPreviewNoteKind, string>
            {
                { GameplayPreviewNoteKind.ChainHead, "notepressline" },
                { GameplayPreviewNoteKind.ChainNode, "notepressline" },
                { GameplayPreviewNoteKind.RepeatHead, "noterepeatline" },
                { GameplayPreviewNoteKind.RepeatHeadHold, "noterepeatline" },
                { GameplayPreviewNoteKind.Repeat, "noterepeatline" },
                { GameplayPreviewNoteKind.RepeatHold, "noterepeatline" },
            };


        private readonly Dictionary<GameplayPreviewNoteKind, TechnikaNoteSprite> _cache =
            new Dictionary<GameplayPreviewNoteKind, TechnikaNoteSprite>();

        private readonly Dictionary<GameplayPreviewNoteKind, TechnikaNoteSprite> _lines =
            new Dictionary<GameplayPreviewNoteKind, TechnikaNoteSprite>();

        private readonly Dictionary<(GameplayPreviewNoteKind, bool), TechnikaNoteSprite> _trailCaps =
            new Dictionary<(GameplayPreviewNoteKind, bool), TechnikaNoteSprite>();

        private readonly Dictionary<(GameplayPreviewNoteKind, bool), TechnikaNoteSprite> _trailBodies =
            new Dictionary<(GameplayPreviewNoteKind, bool), TechnikaNoteSprite>();

        private readonly string _localRoot;

        /// <summary>
        /// Packaged sheets keyed by file name, so the trail caps/bodies and run lines resolve
        /// through one cache rather than reloading a strip for every kind that shares it.
        /// </summary>
        private readonly Dictionary<string, TechnikaNoteSprite> _packagedSheets =
            new Dictionary<string, TechnikaNoteSprite>(StringComparer.OrdinalIgnoreCase);

        private TechnikaNoteSprite _ring;
        private bool _ringResolved;

        private TechnikaNoteSprite _coolBomb;
        private bool _coolBombResolved;

        private TechnikaNoteSprites(string localRoot)
        {
            _localRoot = localRoot;
        }

        /// <summary>The local folder in use, or null when only packaged glyphs are available.</summary>
        public string LocalRoot
        {
            get { return _localRoot; }
        }

        /// <summary>True when the owner's own arcade sprites are being drawn.</summary>
        public bool UsesLocalAssets
        {
            get { return _localRoot != null; }
        }

        /// <summary>"ARCADE SPRITES" or "PACKAGED GLYPHS", for the panel header.</summary>
        public string SourceLabel
        {
            get { return UsesLocalAssets ? "ARCADE SPRITES" : "PACKAGED GLYPHS"; }
        }

        /// <summary>
        /// The glow strip drawn around an approaching note, or null when the local folder is absent
        /// or does not carry one. Null is a normal answer: the renderer tweens a vector ring
        /// instead, which is the same read at lower fidelity.
        /// </summary>
        public TechnikaNoteSprite Ring
        {
            get
            {
                if (!_ringResolved)
                {
                    _ringResolved = true;
                    _ring = LoadLocal(ArcadeRing);
                }
                return _ring;
            }
        }

        /// <summary>
        /// The burst the arcade fires where a note is struck, as its twenty-three frames, or null
        /// when the local folder is absent or carries no <c>CoolBomb</c> tree. Null is a normal
        /// answer: the renderer draws a fading ring instead, which says a note was hit here without
        /// claiming to be the arcade's art.
        /// </summary>
        public TechnikaNoteSprite CoolBomb
        {
            get
            {
                if (!_coolBombResolved)
                {
                    _coolBombResolved = true;
                    _coolBomb = LoadLocalSequence(ArcadeEffectFolder, ArcadeHitEffect);
                }
                return _coolBomb;
            }
        }

        /// <summary>
        /// The frame size every other glyph is measured against: the tap's, because the arcade draws
        /// its tap exactly one lane tall and authors everything else around it.
        ///
        /// <para>
        /// The set does not use one canvas size. Measured, the tap and the repeats are 90 px frames,
        /// a long note's head and a chain's head are 116, a chain node is 76 and the approach glow is
        /// 121 - and the glyph inside each one fills a different share of it again (a chain node is a
        /// 50 px dot in its 76 px frame). Drawing every frame into the same box on screen therefore
        /// throws away the proportions the arcade authored between them, which is the "notes are all
        /// different sizes" the playfield showed; scaling each frame by its own size against this one
        /// restores them, with no per-kind table to keep correct.
        /// </para>
        ///
        /// <para>
        /// With one exception, which is why <see cref="ScaleOf"/> exists and callers use that rather
        /// than the bare ratio: 116 against 90 is not an authored size difference at all. Both are a
        /// whole note frame, one lane pitch tall, in the two mixing modes - and an extracted set
        /// carries the two modes' art side by side. See
        /// <see cref="TechnikaPlayfieldMetrics.IsNoteFrameSize"/>.
        /// </para>
        ///
        /// <para>
        /// Zero when nothing can be resolved, which a caller reads as "no normalisation available"
        /// and falls back to drawing at the lane size. The packaged set is one canvas throughout, so
        /// there the ratio is 1 for every kind and nothing moves.
        /// </para>
        /// </summary>
        public double ReferenceFrameSize
        {
            get
            {
                TechnikaNoteSprite basic = For(GameplayPreviewNoteKind.Basic);
                return basic == null ? 0.0 : basic.FrameSize;
            }
        }

        /// <summary>
        /// How much larger than the lane box one strip's glyph is authored, so the renderer can draw
        /// it at the size it was drawn at. 1.0 when either side cannot be measured.
        ///
        /// <para>
        /// A whole note frame draws at the lane box exactly, whichever of the two mode pitches it was
        /// authored at, because that is what a note frame means - the reference is only needed to
        /// place art that is <em>not</em> a note frame, and the two are told apart by
        /// <see cref="TechnikaPlayfieldMetrics.IsNoteFrameSize"/>. Without that the ratio reads a
        /// 3-line glyph in a 4-line set as authored a third larger and draws a chain head over the
        /// note it leads.
        /// </para>
        /// </summary>
        public double ScaleOf(TechnikaNoteSprite sprite)
        {
            double reference = ReferenceFrameSize;
            if (sprite == null || sprite.FrameSize <= 0 || reference <= 0)
            {
                return 1.0;
            }
            if (TechnikaPlayfieldMetrics.IsNoteFrameSize(sprite.FrameSize))
            {
                return 1.0;
            }
            return sprite.FrameSize / reference;
        }

        /// <summary>
        /// <see cref="ScaleOf"/> for the glyph a kind is drawn with. 1.0 when the set has no art for
        /// the kind.
        /// </summary>
        public double ScaleFor(GameplayPreviewNoteKind kind)
        {
            return ScaleOf(For(kind));
        }

        public static TechnikaNoteSprites Load()
        {
            return new TechnikaNoteSprites(FindLocalRoot());
        }

        /// <summary>
        /// The cap that closes a held note's body, or null when the kind is not a hold or the loaded
        /// set does not carry the file. Null is a normal answer: the renderer falls back to a themed
        /// bar, which spans the same duration at lower fidelity.
        ///
        /// <para>
        /// <paramref name="ongoing"/> asks for the actively-held variant, and falls back to the
        /// resting one where the set has no separate art for it.
        /// </para>
        /// </summary>
        public TechnikaNoteSprite TrailCap(GameplayPreviewNoteKind kind, bool ongoing)
        {
            return Resolve(_trailCaps, ArcadeTrailCaps, ArcadeHeldTrailCaps, kind, ongoing);
        }

        /// <summary>
        /// The cross-section a held note's body is run out of, or null when the kind has no body
        /// sheet - the drag curve, whose body is a cut through its cap - or the loaded set does not
        /// carry one. <paramref name="ongoing"/> asks for the actively-held variant.
        /// </summary>
        public TechnikaNoteSprite TrailBody(GameplayPreviewNoteKind kind, bool ongoing)
        {
            return Resolve(_trailBodies, ArcadeTrailBodies, ArcadeHeldTrailBodies, kind, ongoing);
        }

        /// <summary>
        /// One entry of a two-state art table, cached per kind and state. The held table is consulted
        /// first and the resting one is the fallback, so a set that has no "in" variant of a piece
        /// simply keeps drawing the one it has rather than losing the piece.
        /// </summary>
        private TechnikaNoteSprite Resolve(
            Dictionary<(GameplayPreviewNoteKind, bool), TechnikaNoteSprite> cache,
            Dictionary<GameplayPreviewNoteKind, string> resting,
            Dictionary<GameplayPreviewNoteKind, string> held,
            GameplayPreviewNoteKind kind,
            bool ongoing)
        {
            TechnikaNoteSprite cached;
            if (cache.TryGetValue((kind, ongoing), out cached))
            {
                return cached;
            }

            string name;
            TechnikaNoteSprite resolved = null;
            // The actively-held variant has no packaged art of its own - it exists only in an
            // owner's extracted set - so it never falls across to the packaged resting sheet.
            if (ongoing && held.TryGetValue(kind, out name))
            {
                resolved = LoadLocal(name);
            }
            if (resolved == null && resting.TryGetValue(kind, out name))
            {
                resolved = LoadLocal(name) ?? LoadPackagedSheet(name);
            }
            cache[(kind, ongoing)] = resolved;
            return resolved;
        }

        /// <summary>
        /// The line joining the members of a chain or repeat run, or null when the kind has no line
        /// or the loaded set does not carry one. Null is a normal answer: the renderer falls back to
        /// a themed bar, which joins the same two points at lower fidelity.
        /// </summary>
        public TechnikaNoteSprite Line(GameplayPreviewNoteKind kind)
        {
            TechnikaNoteSprite cached;
            if (_lines.TryGetValue(kind, out cached))
            {
                return cached;
            }

            string name;
            TechnikaNoteSprite resolved = ArcadeLines.TryGetValue(kind, out name)
                ? LoadLocal(name) ?? LoadPackagedSheet(name)
                : null;
            _lines[kind] = resolved;
            return resolved;
        }

        /// <summary>
        /// The glyph for a note kind, or null if neither source produced one. Null is a normal
        /// answer, not a failure: the renderer falls back to vector shapes, which is what keeps a
        /// missing or malformed image from blanking the preview.
        /// </summary>
        public TechnikaNoteSprite For(GameplayPreviewNoteKind kind)
        {
            TechnikaNoteSprite cached;
            if (_cache.TryGetValue(kind, out cached))
            {
                return cached;
            }

            TechnikaNoteSprite resolved = LoadLocal(kind) ?? LoadPackaged(kind);
            _cache[kind] = resolved;
            return resolved;
        }

        // -----------------------------------------------------------------------------------
        // Sources
        // -----------------------------------------------------------------------------------

        private TechnikaNoteSprite LoadLocal(GameplayPreviewNoteKind kind)
        {
            string name;
            if (!ArcadeGlyphs.TryGetValue(kind, out name))
            {
                return null;
            }
            return LoadLocal(name);
        }

        private TechnikaNoteSprite LoadLocal(string name)
        {
            if (_localRoot == null)
            {
                return null;
            }

            BitmapSource image = LoadFrozenImage(Path.Combine(_localRoot, name + ".png"));
            return image == null ? null : TechnikaNoteSprite.Slice(image);
        }

        private static BitmapSource LoadFrozenImage(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                BitmapImage image = new BitmapImage();
                image.BeginInit();
                // OnLoad plus a stream, not a UriSource: it reads the file once and closes the
                // handle, so the owner's extraction stays unlocked and replaceable while the
                // editor is running.
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.None;
                using (FileStream stream = File.OpenRead(path))
                {
                    image.StreamSource = stream;
                    image.EndInit();
                }
                image.Freeze();
                return image;
            }
            catch (IOException ex)
            {
                DiagnosticLog.Exception("technika.sprite", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                DiagnosticLog.Exception("technika.sprite", ex);
            }
            catch (NotSupportedException ex)
            {
                // A file that is not actually a decodable image. Fall through to the packaged
                // glyph rather than losing the note.
                DiagnosticLog.Exception("technika.sprite", ex);
            }
            return null;
        }

        /// <summary>
        /// One effect's frames, read from <paramref name="folder"/> beside the note art as
        /// <c>&lt;prefix&gt;_0000.png</c> upward, or null when that folder is not there.
        ///
        /// <para>
        /// The arcade keeps its effects in their own tree next to the notes rather than in the note
        /// set, so this looks for <paramref name="folder"/> at the note root and then in each
        /// parent above it: an owner who copies one folder of PNGs across and an owner who copies
        /// the whole <c>MainGame</c> tree both end up with a working burst.
        /// </para>
        /// </summary>
        private TechnikaNoteSprite LoadLocalSequence(string folder, string prefix)
        {
            string directory = FindEffectFolder(folder, prefix);
            if (directory == null)
            {
                return null;
            }

            try
            {
                string[] files = Directory.GetFiles(directory, prefix + "_*.png");
                // The arcade zero-pads its frame numbers to four digits, so name order is frame
                // order and there is no number to parse.
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);

                List<ImageSource> frames = new List<ImageSource>(files.Length);
                for (int i = 0; i < files.Length; i++)
                {
                    BitmapSource frame = LoadFrozenImage(files[i]);
                    if (frame != null)
                    {
                        frames.Add(frame);
                    }
                }
                return TechnikaNoteSprite.Sequence(frames);
            }
            catch (IOException ex)
            {
                DiagnosticLog.Exception("technika.sprite", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                DiagnosticLog.Exception("technika.sprite", ex);
            }
            return null;
        }

        private string FindEffectFolder(string folder, string prefix)
        {
            if (_localRoot == null)
            {
                return null;
            }

            // The note root's own leaf is the set the glyphs came from - "0" in an arcade tree -
            // and the burst should come from the same one, so the picture stays of one piece.
            string preferredSet = Path.GetFileName(_localRoot);
            string current = _localRoot;
            for (int depth = 0; depth < EffectSearchDepth && current != null; depth++)
            {
                string resolved = ResolveEffectFolder(
                    Path.Combine(current, folder), prefix, preferredSet);
                if (resolved != null)
                {
                    return resolved;
                }

                // An unwrapped copy: the arcade keeps its frames under a folder named like the
                // effect (CoolBomb\0\cool), but a copy of one set - the Shino-Toku tree is the
                // common case - has cool\ hanging straight off the set root. The wrapped shape
                // above keeps precedence, so an owner who has both sees no change.
                resolved = ResolveEffectFolder(current, prefix, preferredSet);
                if (resolved != null)
                {
                    return resolved;
                }
                current = Path.GetDirectoryName(current);
            }
            return null;
        }

        private static string ResolveEffectFolder(
            string candidate, string prefix, string preferredSet)
        {
            try
            {
                if (!Directory.Exists(candidate))
                {
                    return null;
                }

                // A flat copy of one effect: CoolBomb\cool\cool_0000.png.
                string direct = Path.Combine(candidate, prefix);
                if (HasFrames(direct, prefix))
                {
                    return Path.GetFullPath(direct);
                }

                // The arcade's own shape, one numbered set per folder: CoolBomb\0\cool\.
                if (!string.IsNullOrWhiteSpace(preferredSet))
                {
                    string preferred = Path.Combine(candidate, preferredSet, prefix);
                    if (HasFrames(preferred, prefix))
                    {
                        return Path.GetFullPath(preferred);
                    }
                }

                string[] children = Directory.GetDirectories(candidate);
                Array.Sort(children, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < children.Length; i++)
                {
                    string nested = Path.Combine(children[i], prefix);
                    if (HasFrames(nested, prefix))
                    {
                        return Path.GetFullPath(nested);
                    }
                }
            }
            catch (IOException)
            {
                // An unreadable candidate is simply not the folder we are looking for.
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (ArgumentException)
            {
                // A set name that is not a legal path fragment.
            }
            return null;
        }

        private static bool HasFrames(string directory, string prefix)
        {
            return Directory.Exists(directory)
                && Directory.GetFiles(directory, prefix + "_*.png").Length > 0;
        }

        private TechnikaNoteSprite LoadPackaged(GameplayPreviewNoteKind kind)
        {
            string name;
            return PackagedGlyphs.TryGetValue(kind, out name)
                ? LoadPackagedSheet(name)
                : null;
        }

        /// <summary>
        /// One packaged sheet by the arcade's own file name, from the same <c>Timeline/Notes</c>
        /// resources the editor timeline draws - the glossy arcade sheets, not the flat fallback
        /// glyphs the preview used to carry. Cached by name so a strip loads once no matter how
        /// many kinds share it. Returns null for a sheet the repository does not ship (the run
        /// lines and the actively-held trails), which leaves the caller on its themed fallback.
        /// </summary>
        private TechnikaNoteSprite LoadPackagedSheet(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            TechnikaNoteSprite cached;
            if (_packagedSheets.TryGetValue(name, out cached))
            {
                return cached;
            }

            TechnikaNoteSprite sprite = null;
            try
            {
                BitmapImage image = new BitmapImage(new Uri(
                    "pack://application:,,,/DJMaxEditor.Studio;component/Timeline/Notes/" +
                    name + ".png",
                    UriKind.Absolute));
                image.Freeze();
                sprite = TechnikaNoteSprite.Slice(image);
            }
            catch (IOException ex)
            {
                DiagnosticLog.Exception("technika.sprite", ex);
            }
            catch (NotSupportedException ex)
            {
                // A pack URI the resource table does not contain - an unshipped arcade sheet -
                // comes back as an unsupported image rather than a missing file.
                DiagnosticLog.Exception("technika.sprite", ex);
            }

            // Cache nulls too: a kind without a packaged line would otherwise rebuild and
            // rethrow for every note of every frame.
            _packagedSheets[name] = sprite;
            return sprite;
        }

        private static string FindLocalRoot()
        {
            foreach (string candidate in CandidatePaths())
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }
                string resolved = ResolveRoot(candidate);
                if (resolved != null)
                {
                    return resolved;
                }
            }
            return null;
        }

        /// <summary>
        /// A folder holding sprites, or its first subfolder that does. The arcade keeps six
        /// complete note sets side by side in numbered folders, so an owner who copies that tree
        /// across lands one level above the files; descending once means the copy works as-is.
        /// Effects live a level deeper still (<c>0\cool\cool_0000.png</c>), and a copy of just
        /// that tree declares no glyphs at all: it is accepted as a root too - glyph lookups come
        /// back null and the renderer draws the packaged set, while the effect resolver finds the
        /// bursts by its own tree walk. Also what the Shino-Toku clone looks like.
        /// </summary>
        private static string ResolveRoot(string candidate)
        {
            try
            {
                if (!Directory.Exists(candidate))
                {
                    return null;
                }
                if (Directory.GetFiles(candidate, "*.png").Length > 0)
                {
                    return Path.GetFullPath(candidate);
                }

                string[] children = Directory.GetDirectories(candidate);
                Array.Sort(children, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < children.Length; i++)
                {
                    if (Directory.GetFiles(children[i], "*.png").Length > 0)
                    {
                        return Path.GetFullPath(children[i]);
                    }
                }

                if (LooksLikeSpriteTree(candidate, children))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch (IOException)
            {
                // An unreadable candidate is simply not the folder we are looking for.
            }
            catch (UnauthorizedAccessException)
            {
            }
            return null;
        }

        /// <summary>
        /// Whether any frame PNGs hang off the candidate within two levels - the depth a copied
        /// effect tree (<c>cool\cool_0000.png</c>, or one numbered set above it) puts them at.
        /// Two levels and no more, so "some random folder" is still not a sprite root.
        /// </summary>
        private static bool LooksLikeSpriteTree(string candidate, string[] children)
        {
            for (int i = 0; i < children.Length; i++)
            {
                string[] grandchildren = Directory.GetDirectories(children[i]);
                for (int j = 0; j < grandchildren.Length; j++)
                {
                    if (Directory.GetFiles(grandchildren[j], "*.png").Length > 0)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static IEnumerable<string> CandidatePaths()
        {
            yield return Environment.GetEnvironmentVariable(PathVariable);

            string directory = Path.GetDirectoryName(
                typeof(TechnikaNoteSprites).Assembly.Location);
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = AppDomain.CurrentDomain.BaseDirectory;
            }
            if (!string.IsNullOrWhiteSpace(directory))
            {
                yield return Path.Combine(directory, "LocalAssets", "Technika2");
            }
        }
    }
}
