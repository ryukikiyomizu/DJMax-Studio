using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.CompilerServices;

namespace DJMaxEditor.Controls.Editor.Renderers
{
    /// <summary>
    /// A string already rasterised into a bitmap, plus where the text sits inside it.
    /// </summary>
    /// <remarks>
    /// The offsets exist so a caller can reproduce <see cref="Graphics.DrawString(string,
    /// Font, Brush, float, float)"/> exactly. Anchoring on the bitmap instead would shift
    /// the label by the padding and by the drop shadow, which only hangs off the bottom
    /// right, and a note label that is two pixels out of centre is immediately visible.
    /// </remarks>
    internal sealed class TextImage
    {
        internal TextImage(Bitmap image, int originX, int originY, int textWidth, int textHeight)
        {
            Image = image;
            OriginX = originX;
            OriginY = originY;
            TextWidth = textWidth;
            TextHeight = textHeight;
        }

        public Bitmap Image { get; private set; }

        /// <summary>X of the text's top-left corner inside <see cref="Image"/>.</summary>
        public int OriginX { get; private set; }

        /// <summary>Y of the text's top-left corner inside <see cref="Image"/>.</summary>
        public int OriginY { get; private set; }

        /// <summary>Measured width of the text box, shadow excluded.</summary>
        public int TextWidth { get; private set; }

        /// <summary>Measured height of the text box, shadow excluded.</summary>
        public int TextHeight { get; private set; }

        /// <summary>Destination for a label whose text box is centred on a point.</summary>
        public Rectangle CenteredOn(int centerX, int centerY)
        {
            return new Rectangle(
                centerX - OriginX - (TextWidth / 2),
                centerY - OriginY - (TextHeight / 2),
                Image.Width,
                Image.Height);
        }

        /// <summary>Destination for a label whose text box starts at a point.</summary>
        public Rectangle StartingAt(int x, int y)
        {
            return new Rectangle(x - OriginX, y - OriginY, Image.Width, Image.Height);
        }
    }

    /// <summary>
    /// Rasterises each distinct label once and hands back the bitmap forever after.
    /// <para>
    /// This is the fix for the playtest report that V1 "really lags especially with the
    /// Attr 0". Both horizontal surfaces draw notes through a world scale transform, and
    /// GDI+ will not use its cached glyph rasteriser under a transform — it flattens every
    /// glyph into a filled path per call. A dense frame issued around 600 of those, which
    /// measured at 148ms; the same frame blitting cached bitmaps does no glyph work at all.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Entries are created on demand rather than precomputed: the reachable label space is
    /// over a thousand strings across the display modes but a real chart touches a handful,
    /// and a bitmap per string is far more memory than a string per string.
    /// </remarks>
    internal static class TextImageCache
    {
        /// <summary>
        /// Smallest on-screen text height, in device pixels, still worth drawing. Below
        /// this a label is a grey smudge, so it is skipped rather than rasterised — which
        /// is what keeps the V2 timeline's 0.2-scale theme art cheap.
        /// </summary>
        public const float MinimumLegibleHeight = 7f;

        /// <summary>Transparent margin so bilinear downscaling has edge pixels to read.</summary>
        private const int Padding = 2;

        /// <summary>
        /// Ceiling on distinct rasterised labels. Reaching it means something is feeding
        /// the cache unbounded text; callers fall back to <c>DrawString</c> rather than
        /// growing without limit.
        /// </summary>
        private const int MaximumEntries = 2048;

        private static readonly Dictionary<Key, TextImage> Images =
            new Dictionary<Key, TextImage>();

        private static readonly object Gate = new object();

        private static Bitmap _measureSurface;
        private static Graphics _measureGraphics;

        /// <summary>
        /// True when text in <paramref name="font"/> would still be readable after the
        /// caller's transform scales it by <paramref name="scale"/>.
        /// </summary>
        public static bool IsLegible(float fontHeight, float scale)
        {
            return fontHeight * scale >= MinimumLegibleHeight;
        }

        public static TextImage Get(Font font, string text, Color fill)
        {
            return Get(font, text, fill, Color.Empty, 0);
        }

        /// <summary>
        /// The bitmap for <paramref name="text"/>, or null when the cache is full and the
        /// caller should draw the string directly.
        /// </summary>
        public static TextImage Get(
            Font font,
            string text,
            Color fill,
            Color shadow,
            int shadowDistance)
        {
            if (font == null || string.IsNullOrEmpty(text))
            {
                return null;
            }

            if (shadowDistance <= 0)
            {
                shadow = Color.Empty;
                shadowDistance = 0;
            }

            var key = new Key(font, text, fill, shadow, shadowDistance);
            lock (Gate)
            {
                TextImage cached;
                if (Images.TryGetValue(key, out cached))
                {
                    return cached;
                }

                if (Images.Count >= MaximumEntries)
                {
                    return null;
                }

                cached = Rasterise(font, text, fill, shadow, shadowDistance);
                Images.Add(key, cached);
                return cached;
            }
        }

        private static TextImage Rasterise(
            Font font,
            string text,
            Color fill,
            Color shadow,
            int shadowDistance)
        {
            SizeF measured = Measure(font, text);
            int textWidth = Math.Max(1, (int)Math.Ceiling(measured.Width));
            int textHeight = Math.Max(1, (int)Math.Ceiling(measured.Height));
            int width = textWidth + shadowDistance + (Padding * 2);
            int height = textHeight + shadowDistance + (Padding * 2);

            var image = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(image))
            {
                // Grid-fitted antialiasing rather than ClearType: subpixel text cannot be
                // composited over a transparent background without colour fringing.
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                if (shadowDistance > 0 && shadow != Color.Empty)
                {
                    using (var brush = new SolidBrush(shadow))
                    {
                        graphics.DrawString(
                            text, font, brush, Padding + shadowDistance, Padding + shadowDistance);
                    }
                }

                using (var brush = new SolidBrush(fill))
                {
                    graphics.DrawString(text, font, brush, Padding, Padding);
                }
            }

            return new TextImage(image, Padding, Padding, textWidth, textHeight);
        }

        /// <summary>
        /// Measures against one long-lived 1x1 surface. Creating a <see cref="Bitmap"/> and
        /// a <see cref="Graphics"/> per measurement is two native handles per new label.
        /// </summary>
        private static SizeF Measure(Font font, string text)
        {
            if (_measureGraphics == null)
            {
                _measureSurface = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
                _measureGraphics = Graphics.FromImage(_measureSurface);
            }
            return _measureGraphics.MeasureString(text, font);
        }

        private struct Key : IEquatable<Key>
        {
            private readonly Font _font;
            private readonly string _text;
            private readonly int _fill;
            private readonly int _shadow;
            private readonly int _shadowDistance;

            internal Key(Font font, string text, Color fill, Color shadow, int shadowDistance)
            {
                _font = font;
                _text = text;
                _fill = fill.ToArgb();
                _shadow = shadow.ToArgb();
                _shadowDistance = shadowDistance;
            }

            public bool Equals(Key other)
            {
                // Reference equality on the font: renderers hold their fonts for their whole
                // lifetime, so identity is both correct and cheaper than comparing families.
                return ReferenceEquals(_font, other._font) &&
                    string.Equals(_text, other._text, StringComparison.Ordinal) &&
                    _fill == other._fill &&
                    _shadow == other._shadow &&
                    _shadowDistance == other._shadowDistance;
            }

            public override bool Equals(object obj)
            {
                return obj is Key && Equals((Key)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = RuntimeHelpers.GetHashCode(_font);
                    hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(_text);
                    hash = (hash * 31) + _fill;
                    hash = (hash * 31) + _shadow;
                    return (hash * 31) + _shadowDistance;
                }
            }
        }
    }
}
