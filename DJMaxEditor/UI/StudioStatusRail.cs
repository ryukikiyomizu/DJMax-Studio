using System.Drawing;
using System.Windows.Forms;

namespace DJMaxEditor.UI
{
    /// <summary>
    /// The bottom status bar: what is selected, which tool is active, and what the open document
    /// allows.
    /// </summary>
    /// <remarks>
    /// The separator above the text used to be two lines - a full-width cyan rule with a
    /// violet bar over the first fifth of it. Nothing set that width, nothing read it, and it
    /// moved when the window resized, so it read as a progress bar that never progressed. It is
    /// one grey hairline now: this rail is chrome, and the accent colours are reserved for things
    /// that actually change state.
    /// <para>
    /// Every GDI object is owned for the control's lifetime. This repaints on every selection
    /// change, which during a marquee drag is every mouse move, and the old code allocated four
    /// pens and brushes plus a <see cref="StringFormat"/> that was never disposed on each one.
    /// </para>
    /// </remarks>
    public sealed class StudioStatusRail : Control
    {
        private readonly Pen _separator = new Pen(StudioDesignSystem.Border);
        private readonly SolidBrush _muted = new SolidBrush(StudioDesignSystem.Muted);
        private readonly SolidBrush _bright = new SolidBrush(StudioDesignSystem.Frost);
        private readonly StringFormat _left;
        private readonly StringFormat _center;
        private readonly StringFormat _right;
        private string _leftText = "Ready";
        private string _centerText = "Quantize  1/8";
        private string _rightText = "No document";

        public StudioStatusRail()
        {
            BackColor = StudioDesignSystem.Void;
            Dock = DockStyle.Bottom;
            DoubleBuffered = true;
            Font = StudioDesignSystem.BodyFont(8.5f);
            ForeColor = StudioDesignSystem.Muted;
            Height = 28;
            MinimumSize = new Size(0, 28);

            _left = CreateFormat(StringAlignment.Near);
            _center = CreateFormat(StringAlignment.Center);
            _right = CreateFormat(StringAlignment.Far);
        }

        public void SetStatus(string left, string center, string right)
        {
            _leftText = string.IsNullOrWhiteSpace(left) ? "Ready" : left;
            _centerText = string.IsNullOrWhiteSpace(center) ? "Quantize  --" : center;
            _rightText = string.IsNullOrWhiteSpace(right) ? "No document" : right;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Rectangle bounds = ClientRectangle;

            e.Graphics.DrawLine(_separator, 0, 0, bounds.Width, 0);

            e.Graphics.DrawString(_leftText, Font, _bright,
                new RectangleF(12, 3, bounds.Width * 0.4f, bounds.Height - 3), _left);
            e.Graphics.DrawString(_centerText, Font, _muted,
                new RectangleF(bounds.Width * 0.38f, 3, bounds.Width * 0.24f, bounds.Height - 3), _center);
            e.Graphics.DrawString(_rightText, Font, _muted,
                new RectangleF(bounds.Width * 0.62f, 3, bounds.Width * 0.38f - 12, bounds.Height - 3), _right);
        }

        private static StringFormat CreateFormat(StringAlignment alignment)
        {
            return new StringFormat
            {
                Alignment = alignment,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _separator.Dispose();
                _muted.Dispose();
                _bright.Dispose();
                _left.Dispose();
                _center.Dispose();
                _right.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
