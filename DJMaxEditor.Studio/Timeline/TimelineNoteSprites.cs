using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;
using DJMaxEditor.Controls.TimelineV2;
using DJMaxEditor.Diagnostics;
using DJMaxEditor.Studio.Preview;

namespace DJMaxEditor.Studio.Timeline
{
    /// <summary>
    /// The pieces one timeline note is drawn out of, in the arcade's own grammar: a head glyph
    /// centred on the note's onset, and - for the families that carry a duration - a run body
    /// stretched along it closed by an end cap.
    ///
    /// <para>
    /// <see cref="Body"/> may be null where <see cref="Cap"/> is not. That is not an omission: the
    /// arcade authors the drag curve as a cap and nothing else, and the legacy renderer draws its
    /// body by stretching the cap's first two columns - which is what
    /// <see cref="TechnikaNoteSprite.Stem"/> does. The other two hold families do have a body sheet
    /// of their own, and it carries a per-frame colour ramp no cut through a cap can reproduce.
    /// </para>
    /// </summary>
    internal sealed class TimelineNoteArt
    {
        public TimelineNoteArt(TechnikaNoteSprite head, TechnikaNoteSprite cap, TechnikaNoteSprite body)
        {
            Head = head;
            Cap = cap;
            Body = body;
        }

        /// <summary>The glyph centred on the note's onset. Never null in a usable instance.</summary>
        public TechnikaNoteSprite Head { get; private set; }

        /// <summary>The far end of a run, or null for a family with no duration.</summary>
        public TechnikaNoteSprite Cap { get; private set; }

        /// <summary>The run's cross-section sheet, or null to cut one out of <see cref="Cap"/>.</summary>
        public TechnikaNoteSprite Body { get; private set; }

        public bool HasTrail
        {
            get { return Cap != null; }
        }
    }

    /// <summary>
    /// The legacy editor's TECHNIKA note art, loaded once from the shell's own packaged resources
    /// and mapped onto <see cref="TechnikaNoteKind"/>.
    ///
    /// <para>
    /// The table is <c>TechnikaThemeRenderer.RenderNote</c>'s, deliberately: the old WinForms
    /// timeline is the thing this is being asked to look like, and re-deriving which PNG a repeat
    /// note is drawn from would have produced a second answer to a question that already has one.
    /// Slicing goes through <see cref="TechnikaNoteSprite.Slice"/> for the same reason - the
    /// playfield already knows that these sheets are ten-frame strips and that a square image is a
    /// single glyph, and two slicing rules in one assembly is one too many.
    /// </para>
    ///
    /// <para>
    /// One deliberate departure. Where the legacy renderer draws a hold's run with
    /// <c>line_in_end</c>, this uses <c>line_nor_end</c> and <c>longholdgauge</c>. The "in" and
    /// "nor" pairs are the actively-held and resting variants of the same run - proven on the seam
    /// colours, (0,57,111) against (0,2,111) - and a note sitting on a timeline is not being held
    /// by anybody. The playfield picks between them per frame; here the answer is always resting.
    /// </para>
    /// </summary>
    internal sealed class TimelineNoteSprites
    {
        /// <summary>
        /// The frame edge the arcade authors a plain tap against, and so the size everything else
        /// is measured in: <c>Note_Basic</c> is ten 90x90 frames. A head authored larger than this
        /// - <c>longnote</c> and <c>notepressstart</c> are 116 - is meant to be drawn larger than a
        /// tap, which is why art is scaled against this constant rather than fitted to the lane.
        /// </summary>
        public const double ReferenceFrameSize = 90.0;

        private static readonly Dictionary<TechnikaNoteKind, string[]> Table =
            new Dictionary<TechnikaNoteKind, string[]>
            {
                // { kind, { head, cap, body } } - null entries are absent pieces, not missing work.
                { TechnikaNoteKind.Basic, new[] { "Note_Basic", null, null } },
                { TechnikaNoteKind.Drag, new[] { "longnote", "longnoteline", null } },
                { TechnikaNoteKind.ChainHead, new[] { "notepressstart", null, null } },
                { TechnikaNoteKind.ChainNode, new[] { "notepressnote", null, null } },
                { TechnikaNoteKind.RepeatHead, new[] { "noterepeat", null, null } },
                { TechnikaNoteKind.RepeatHeadHold,
                    new[] { "noterepeat", "long_note_end", "long_note_line" } },
                { TechnikaNoteKind.Repeat, new[] { "repeattail", null, null } },
                { TechnikaNoteKind.RepeatHold,
                    new[] { "repeattail", "long_note_end", "long_note_line" } },
                { TechnikaNoteKind.Hold, new[] { "longnotehold", "line_nor_end", "longholdgauge" } },
            };

        private static TimelineNoteSprites _default;

        private readonly Dictionary<string, TechnikaNoteSprite> _sheets =
            new Dictionary<string, TechnikaNoteSprite>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<TechnikaNoteKind, TimelineNoteArt> _art =
            new Dictionary<TechnikaNoteKind, TimelineNoteArt>();

        private TimelineNoteSprites()
        {
        }

        /// <summary>
        /// The shared set. Built on first use and never rebuilt: the art is packaged in the
        /// assembly, so there is nothing that could change underneath it, and every frame of it is
        /// frozen.
        /// </summary>
        public static TimelineNoteSprites Default
        {
            get
            {
                if (_default == null)
                {
                    _default = new TimelineNoteSprites();
                }
                return _default;
            }
        }

        /// <summary>
        /// The art for a note kind, or null when the kind has none - which is the caller's signal
        /// to fall back to a plain rectangle rather than to draw nothing. <see cref="TechnikaNoteKind.Unknown"/>
        /// is the honest case: a tempo change, a video marker or an attribute the arcade never
        /// documented has no note glyph, and inventing one would be a lie about the chart.
        /// </summary>
        public TimelineNoteArt For(TechnikaNoteKind kind)
        {
            TimelineNoteArt art;
            if (_art.TryGetValue(kind, out art))
            {
                return art;
            }

            string[] names;
            if (Table.TryGetValue(kind, out names))
            {
                TechnikaNoteSprite head = Load(names[0]);

                // No head, no art. A cap without the glyph that names the family would draw a bar
                // the user cannot identify, which is worse than the rectangle it replaced.
                if (head != null)
                {
                    art = new TimelineNoteArt(head, Load(names[1]), Load(names[2]));
                }
            }

            _art[kind] = art;
            return art;
        }

        private TechnikaNoteSprite Load(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            TechnikaNoteSprite sprite;
            if (_sheets.TryGetValue(name, out sprite))
            {
                return sprite;
            }

            sprite = LoadPackaged(name);
            _sheets[name] = sprite;
            return sprite;
        }

        /// <summary>
        /// One sheet out of the assembly's resource table. The explicit assembly name in the pack
        /// URI matters: it resolves through that assembly's resources directly, so this works in the
        /// headless test harness where there is no <c>Application</c> to resolve a relative one.
        /// </summary>
        private static TechnikaNoteSprite LoadPackaged(string name)
        {
            try
            {
                BitmapImage image = new BitmapImage(new Uri(
                    "pack://application:,,,/DJMaxEditor.Studio;component/Timeline/Notes/" +
                    name + ".png",
                    UriKind.Absolute));
                image.Freeze();
                return TechnikaNoteSprite.Slice(image);
            }
            catch (IOException ex)
            {
                // A resource that is not in the table is a build problem, not a runtime one, and
                // the band still draws - the notes come out as rectangles.
                DiagnosticLog.Exception("timeline.sprite", ex);
            }
            catch (NotSupportedException ex)
            {
                DiagnosticLog.Exception("timeline.sprite", ex);
            }
            return null;
        }
    }
}
