using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DJMaxEditor.Studio.Video
{
    /// <summary>
    /// The BGA viewport: one <see cref="WriteableBitmap"/>, drawn letterboxed and centred.
    /// <para>
    /// Deliberately a bare <see cref="FrameworkElement"/> rather than a <see cref="UserControl"/>
    /// with XAML. A UserControl brings a template, a content presenter and a logical child to walk
    /// on every layout pass, none of which earns anything here: the whole visual is one filled
    /// rectangle plus one image. This is the same reason the timeline surfaces draw directly in
    /// <c>OnRender</c>.
    /// </para>
    /// </summary>
    public sealed class BgaPreview : FrameworkElement
    {
        private static readonly Brush Backdrop = CreateBackdrop();

        private readonly BgaFrameBuffer _frame = new BgaFrameBuffer();

        private IBgaSource _source;
        private WriteableBitmap _bitmap;
        private TimeSpan _onScreen = TimeSpan.MinValue;
        private string _error;

        public BgaPreview()
        {
            // The preview is always smaller than the source (a 1280x720 BGA in a panel a few
            // hundred pixels wide), so this is a downscale. Linear is the right filter for that and
            // costs nothing on the GPU; Fant would resample a full frame on the CPU, and
            // NearestNeighbour turns fine detail into aliasing crawl as the playhead moves.
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
        }

        /// <summary>
        /// The decoder to pull frames from. Assigning a new one drops the frame on screen.
        /// <para>
        /// If nothing is assigned, the first <see cref="TryOpen"/> creates a
        /// <see cref="MediaFoundationBgaSource"/> and leaves it here, so the owner can always reach
        /// the source it ended up with. The owner disposes it; <see cref="Clear"/> only closes it,
        /// which is enough to release the Media Foundation platform reference.
        /// </para>
        /// </summary>
        public IBgaSource Source
        {
            get { return _source; }
            set
            {
                if (ReferenceEquals(_source, value))
                {
                    return;
                }
                _source = value;
                Forget();
                InvalidateVisual();
            }
        }

        /// <summary>Human readable summary of what is loaded, or the reason nothing is.</summary>
        public string StatusText
        {
            get
            {
                if (!string.IsNullOrEmpty(_error))
                {
                    return _error;
                }

                IBgaSource source = _source;
                if (source == null || !source.IsOpen)
                {
                    return string.Empty;
                }

                string text = source.PixelWidth.ToString(CultureInfo.InvariantCulture) + "x" +
                    source.PixelHeight.ToString(CultureInfo.InvariantCulture);

                if (!string.IsNullOrEmpty(source.Description))
                {
                    text = source.Description + " - " + text;
                }

                if (source.FrameRate > 0.0)
                {
                    text += " - " + source.FrameRate.ToString("0.##", CultureInfo.InvariantCulture) + " fps";
                }

                string duration = FormatDuration(source.Duration);
                if (duration != null)
                {
                    text += " - " + duration;
                }

                return text;
            }
        }

        public bool TryOpen(string path, out string error)
        {
            Forget();

            if (_source == null)
            {
                _source = new MediaFoundationBgaSource();
            }

            if (!_source.Open(path, out error))
            {
                _error = error;
                InvalidateVisual();
                return false;
            }

            _frame.Resize(_source.PixelWidth, _source.PixelHeight);
            _bitmap = new WriteableBitmap(
                _source.PixelWidth, _source.PixelHeight, 96.0, 96.0, PixelFormats.Bgr32, null);
            InvalidateVisual();
            return true;
        }

        /// <summary>
        /// Shows the frame at <paramref name="position"/>. Cheap to call at composition rate: when
        /// the position still maps to the frame already on screen this touches nothing, which is the
        /// whole reason the source reports a timestamp with the pixels.
        /// </summary>
        public void Seek(TimeSpan position)
        {
            IBgaSource source = _source;
            if (source == null || !source.IsOpen)
            {
                return;
            }

            if (!source.TryGetFrame(position, _frame))
            {
                return;
            }

            if (_frame.Timestamp == _onScreen || !_frame.HasFrame)
            {
                return;
            }

            if (_bitmap == null || _bitmap.PixelWidth != _frame.Width || _bitmap.PixelHeight != _frame.Height)
            {
                // A mid-stream MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED can resize the frame, so
                // the bitmap is rebuilt from the buffer's geometry rather than the open-time one.
                _bitmap = new WriteableBitmap(
                    _frame.Width, _frame.Height, 96.0, 96.0, PixelFormats.Bgr32, null);
            }

            _bitmap.WritePixels(
                new Int32Rect(0, 0, _frame.Width, _frame.Height),
                _frame.Pixels,
                _frame.Stride,
                0);

            _onScreen = _frame.Timestamp;
            InvalidateVisual();
        }

        public void Clear()
        {
            if (_source != null)
            {
                _source.Close();
            }
            Forget();
            InvalidateVisual();
        }

        /// <summary>
        /// The centred, aspect-preserving destination rect for a <paramref name="pixelWidth"/> by
        /// <paramref name="pixelHeight"/> image inside <paramref name="bounds"/>.
        /// </summary>
        public static Rect Letterbox(Rect bounds, int pixelWidth, int pixelHeight)
        {
            if (pixelWidth <= 0 || pixelHeight <= 0 || bounds.Width <= 0.0 || bounds.Height <= 0.0)
            {
                return Rect.Empty;
            }

            double scale = Math.Min(bounds.Width / pixelWidth, bounds.Height / pixelHeight);
            double width = pixelWidth * scale;
            double height = pixelHeight * scale;

            return new Rect(
                bounds.X + ((bounds.Width - width) / 2.0),
                bounds.Y + ((bounds.Height - height) / 2.0),
                width,
                height);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            Rect bounds = new Rect(0.0, 0.0, RenderSize.Width, RenderSize.Height);
            if (bounds.Width <= 0.0 || bounds.Height <= 0.0)
            {
                return;
            }

            drawingContext.DrawRectangle(Backdrop, null, bounds);

            if (_bitmap == null)
            {
                return;
            }

            Rect target = Letterbox(bounds, _bitmap.PixelWidth, _bitmap.PixelHeight);
            if (target.Width < 1.0 || target.Height < 1.0)
            {
                return;
            }

            drawingContext.DrawImage(_bitmap, target);
        }

        private void Forget()
        {
            _bitmap = null;
            _frame.Clear();
            _onScreen = TimeSpan.MinValue;
            _error = null;
        }

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero)
            {
                return null;
            }

            if (duration.TotalHours >= 1.0)
            {
                return ((int)duration.TotalHours).ToString(CultureInfo.InvariantCulture) + ":" +
                    duration.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":" +
                    duration.Seconds.ToString("00", CultureInfo.InvariantCulture);
            }

            return ((int)duration.TotalMinutes).ToString(CultureInfo.InvariantCulture) + ":" +
                duration.Seconds.ToString("00", CultureInfo.InvariantCulture);
        }

        private static Brush CreateBackdrop()
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00));
            brush.Freeze();
            return brush;
        }
    }
}
