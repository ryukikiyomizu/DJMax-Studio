using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace DJMaxEditor.Controls.Editor
{
    /// <summary>
    /// Thin seam over <see cref="Graphics"/> used by the legacy editor renderers.
    /// <para>
    /// The members are virtual so a test can subclass this and count the drawing calls a
    /// theme issues. That matters for the text calls in particular: text is the most
    /// expensive work in the paint loop, and counting is the only way to prove a theme is
    /// not paying for labels nobody can read.
    /// </para>
    /// </summary>
    public class GraphicsWrapper
    {
        private Graphics m_graphics;
        private float m_labelScale = 1f;

        /// <summary>
        /// What this wrapper believes the surface's interpolation mode is. Tracked instead of
        /// read back per draw because <see cref="Graphics.InterpolationMode"/> is a native
        /// round trip, and a dense frame draws hundreds of labels.
        /// </summary>
        private InterpolationMode m_interpolation = InterpolationMode.Default;

        /// <summary>The mode note art wants, i.e. whatever the caller set for the frame.</summary>
        private InterpolationMode m_artInterpolation = InterpolationMode.Default;

        /// <summary>
        /// Binds the surface for a frame. Call this <em>after</em> setting the quality modes
        /// on <paramref name="graphics"/>, so the wrapper knows which mode to put back once a
        /// label has temporarily switched the surface to bilinear.
        /// </summary>
        public void UpdateGraphics(Graphics graphics)
        {
            m_graphics = graphics;
            m_interpolation = graphics == null
                ? InterpolationMode.Default
                : graphics.InterpolationMode;
            m_artInterpolation = m_interpolation;
        }

        /// <summary>
        /// Uniform scale the caller's world transform applies to everything a renderer
        /// draws, so a theme can tell how big its text will actually land on screen.
        /// <para>
        /// GDI+ cannot use its cached glyph rasteriser once a world transform is in play:
        /// it flattens every glyph to a filled path instead, which costs roughly two orders
        /// of magnitude more than a blit. Both horizontal surfaces draw notes through a
        /// scale transform, so this is the number that decides whether a label is worth
        /// drawing at all — see <see cref="Renderers.TextImageCache.IsLegible"/>.
        /// </para>
        /// </summary>
        public virtual float LabelScale
        {
            get { return m_labelScale; }
            set { m_labelScale = value; }
        }

        public virtual void DrawLine(Pen pen, int x1, int y1, int x2, int y2)
        {
            m_graphics?.DrawLine(pen, x1, y1, x2, y2);
        }

        /// <summary>
        /// Blits a pre-rendered text bitmap. Interpolation is bilinear for text: the editor
        /// paints notes with nearest-neighbour so its pixel-art glyphs stay crisp, but
        /// nearest-neighbour drops rows and columns out of downscaled text and turns a label
        /// into confetti.
        /// </summary>
        public virtual void DrawLabel(Image image, Rectangle destination)
        {
            if (m_graphics == null || image == null)
            {
                return;
            }

            SetInterpolation(InterpolationMode.Bilinear);
            m_graphics.DrawImage(
                image,
                destination,
                0,
                0,
                image.Width,
                image.Height,
                GraphicsUnit.Pixel);
        }

        public virtual void FillRectangle(Brush brush, RectangleF rect)
        {
            m_graphics?.FillRectangle(brush, rect);
        }

        public virtual void DrawImage(Image image, Rectangle destRect, float srcX, float srcY, float srcWidth, float srcHeight, GraphicsUnit srcUnit, ImageAttributes imageAttrs)
        {
            if (m_graphics == null)
            {
                return;
            }

            // Note art is pixel art, so it goes back to the mode the caller chose for the
            // frame if a label left the surface on bilinear.
            SetInterpolation(m_artInterpolation);
            m_graphics.DrawImage(image, destRect, srcX, srcY, srcWidth, srcHeight, srcUnit, imageAttrs);
        }

        public virtual void DrawString(string s, Font font, Brush brush, float x, float y)
        {
            m_graphics?.DrawString(s, font, brush, x, y);
        }

        public virtual void DrawRectangle(Pen pen, int x, int y, int width, int height)
        {
            m_graphics?.DrawRectangle(pen, x, y, width, height);
        }

        public virtual void DrawRectangle(Pen pen, Rectangle rect)
        {
            m_graphics?.DrawRectangle(pen, rect);
        }

        public virtual void FillRectangle(Brush brush, Rectangle rect)
        {
            m_graphics?.FillRectangle(brush, rect);
        }

        public virtual void FillRectangle(Brush brush, int x, int y, int width, int height)
        {
            m_graphics?.FillRectangle(brush, x, y, width, height);
        }

        public virtual void DrawString(string s, Font font, Brush brush, float x, float y, StringFormat format)
        {
            m_graphics?.DrawString(s, font, brush, x, y, format);
        }

        private void SetInterpolation(InterpolationMode mode)
        {
            if (m_interpolation == mode)
            {
                return;
            }

            m_graphics.InterpolationMode = mode;
            m_interpolation = mode;
        }
    }
}
