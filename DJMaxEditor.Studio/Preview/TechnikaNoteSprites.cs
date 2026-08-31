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
    /// One note glyph: a single image, or the horizontal strip of square frames the arcade
    /// animates it from.
    ///
    /// <para>
    /// TECHNIKA's notes are not still pictures. Every head is a ten frame loop - the tap's inner
    /// bar fills and drains, the long note's arrow sweeps through it - and the ring around an
    /// approaching note is twenty frames of a glow that opens and settles. Taking frame zero and
    /// discarding the rest leaves a flat disc, which is precisely what makes a playfield read as a
    /// generic vertical-scroller skin rather than as TECHNIKA. Frames are cropped once, frozen,
    /// and handed out by position in the loop.
    /// </para>
    /// </summary>
    internal sealed class TechnikaNoteSprite
    {
        private readonly ImageSource[] _frames;

        private int _peakFrame = -1;

        private double[] _sweepOffsets;

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
        /// Slices a horizontal strip of square frames, or keeps the image whole when it is not one.
        /// The test is exact divisibility of width by height, which every arcade strip satisfies
        /// (900/90 = 10, 1160/116 = 10, 2420/121 = 20) and a single square glyph satisfies with a
        /// count of one - so a strip and a still image drop in through the same path.
        /// </summary>
        internal static TechnikaNoteSprite Slice(BitmapSource source)
        {
            int width = source.PixelWidth;
            int height = source.PixelHeight;
            if (width <= 0 || height <= 0 || width % height != 0)
            {
                return new TechnikaNoteSprite(new ImageSource[] { source }, height, width);
            }

            int count = width / height;
            ImageSource[] frames = new ImageSource[count];
            for (int i = 0; i < count; i++)
            {
                CroppedBitmap frame = new CroppedBitmap(
                    source, new Int32Rect(i * height, 0, height, height));
                frame.Freeze();
                frames[i] = frame;
            }
            return new TechnikaNoteSprite(frames, height);
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
    /// extracted arcade sprites into; if that yields nothing, the six glyphs already in this
    /// repository (<c>DJMaxEditor\Resources\TechmaniaNotes</c>, linked into this project as WPF
    /// resources) are used. The fallback is not a placeholder - it is what the legacy preview has
    /// always drawn - so a fresh clone gets real note art with no setup at all.
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
        /// The glyph the arcade swaps a long note's head for while it is actually being held. Arcade
        /// only; the packaged set has one glyph for the whole gesture.
        /// </summary>
        private const string ArcadeHeld = "longnotehold";


        /// <summary>
        /// Packaged glyph per note kind. Six files cover nine kinds because the held variants
        /// share a head with their un-held counterparts - the tail is what distinguishes them,
        /// and the renderer draws that itself.
        /// </summary>
        private static readonly Dictionary<GameplayPreviewNoteKind, string> PackagedGlyphs =
            new Dictionary<GameplayPreviewNoteKind, string>
            {
                { GameplayPreviewNoteKind.Basic, "Basic" },
                { GameplayPreviewNoteKind.Drag, "Basic" },
                { GameplayPreviewNoteKind.Generic, "Basic" },
                { GameplayPreviewNoteKind.ChainHead, "ChainHead" },
                { GameplayPreviewNoteKind.ChainNode, "ChainNode" },
                { GameplayPreviewNoteKind.Hold, "HoldHead" },
                { GameplayPreviewNoteKind.RepeatHead, "RepeatHead" },
                { GameplayPreviewNoteKind.RepeatHeadHold, "RepeatHead" },
                { GameplayPreviewNoteKind.Repeat, "Repeat" },
                { GameplayPreviewNoteKind.RepeatHold, "Repeat" },
            };

        /// <summary>
        /// The arcade's own file name per note kind, for the local folder. Names are the game's,
        /// so an owner can copy files across without renaming them.
        ///
        /// <para>
        /// Every kind that has art of its own gets it: a long note's head is <c>longnote</c>, which
        /// is a different glyph from the <c>longnotehold</c> the arcade swaps in while the note is
        /// actually being held (see <see cref="Held"/>). Only the pairs the arcade itself draws
        /// identically share an entry - a held variant is distinguished by its tail, not its head.
        /// </para>
        /// </summary>
        private static readonly Dictionary<GameplayPreviewNoteKind, string> ArcadeGlyphs =
            new Dictionary<GameplayPreviewNoteKind, string>
            {
                { GameplayPreviewNoteKind.Basic, "Note_Basic" },
                { GameplayPreviewNoteKind.Drag, "Note_Basic" },
                { GameplayPreviewNoteKind.Generic, "Note_Basic" },
                { GameplayPreviewNoteKind.ChainHead, "notepressstart" },
                { GameplayPreviewNoteKind.ChainNode, "notepressnote" },
                { GameplayPreviewNoteKind.Hold, "longnote" },
                { GameplayPreviewNoteKind.RepeatHead, "noterepeat" },
                { GameplayPreviewNoteKind.RepeatHeadHold, "noterepeat" },
                { GameplayPreviewNoteKind.Repeat, "repeattail" },
                { GameplayPreviewNoteKind.RepeatHold, "repeattail" },
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

        private readonly string _localRoot;

        private TechnikaNoteSprite _ring;
        private bool _ringResolved;

        private TechnikaNoteSprite _held;
        private bool _heldResolved;

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
        /// The frame size every other glyph is measured against: the tap's, because the arcade draws
        /// its tap exactly one lane tall and authors everything else around it.
        ///
        /// <para>
        /// The set does not use one canvas size. Measured, the tap and the repeats are 90 px frames,
        /// a long note's head and a chain's head are 116, a chain node is 76 and the approach glow is
        /// 121 - and the glyph inside each one fills a different share of it again (a chain node is a
        /// 50 px dot in its 76 px frame). Drawing every frame into the same box on screen therefore
        /// throws the relative sizes away: it shrinks a long note's head by a fifth and blows a chain
        /// node up by a half, which is the "notes are all different sizes" the playfield showed.
        /// Scaling each frame by its own size against this one restores exactly the proportions the
        /// arcade authored, with no per-kind table to keep correct.
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
        /// How much larger than the reference frame a kind's glyph is authored, so the renderer can
        /// draw it at the size it was drawn at. 1.0 when either side cannot be measured.
        /// </summary>
        public double ScaleFor(GameplayPreviewNoteKind kind)
        {
            TechnikaNoteSprite sprite = For(kind);
            double reference = ReferenceFrameSize;
            if (sprite == null || sprite.FrameSize <= 0 || reference <= 0)
            {
                return 1.0;
            }
            return sprite.FrameSize / reference;
        }

        public static TechnikaNoteSprites Load()
        {
            return new TechnikaNoteSprites(FindLocalRoot());
        }

        /// <summary>
        /// The glyph a long note wears while it is being held, or null when the loaded set has no
        /// separate one. Null means "keep drawing the head", which is what the packaged set wants.
        /// </summary>
        public TechnikaNoteSprite Held
        {
            get
            {
                if (!_heldResolved)
                {
                    _heldResolved = true;
                    _held = LoadLocal(ArcadeHeld);
                }
                return _held;
            }
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
                ? LoadLocal(name)
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

            string path = Path.Combine(_localRoot, name + ".png");
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
                return TechnikaNoteSprite.Slice(image);
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

        private static TechnikaNoteSprite LoadPackaged(GameplayPreviewNoteKind kind)
        {
            string name;
            if (!PackagedGlyphs.TryGetValue(kind, out name))
            {
                return null;
            }

            try
            {
                BitmapImage image = new BitmapImage(new Uri(
                    "pack://application:,,,/DJMaxEditor.Studio;component/Preview/Notes/" +
                    name + ".png",
                    UriKind.Absolute));
                image.Freeze();
                return TechnikaNoteSprite.Slice(image);
            }
            catch (IOException ex)
            {
                DiagnosticLog.Exception("technika.sprite", ex);
            }
            return null;
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
