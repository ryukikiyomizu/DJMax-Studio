using System.Windows;
using System.Windows.Media;

namespace DJMaxEditor.Studio.Timeline
{
    /// <summary>Which screen axis time runs along.</summary>
    internal enum TimelineOrientation
    {
        /// <summary>Time down the screen, lanes across it: the ptSequencer / DJMax reading.</summary>
        Vertical = 0,

        /// <summary>Time across the screen, lanes down it: the DAW reading.</summary>
        Horizontal = 1
    }

    /// <summary>
    /// The one place the horizontal timeline exists.
    /// <para>
    /// Everything that positions anything on the Studio timeline - the projection, the layout, the
    /// frame, hit testing, scroll and zoom - is built by <c>VerticalTimelineViewModel</c> in what
    /// this class calls <i>surface space</i>: time along Y, lanes along X, with
    /// <c>VerticalCoordinateSystem</c> as the single place the up/down flip is implemented. None of
    /// that is specific to which way the screen runs, so the horizontal layout is not a second
    /// projection, a second frame or a second canvas: it is the same surface, reflected across its
    /// own diagonal on the way to the screen.
    /// </para>
    /// <para>
    /// The reflection is what lands the chrome where a DAW puts it. In surface space the lane names
    /// are a strip along the top and the measure labels a gutter down the left; transposed, the
    /// names become the track column on the left and the measure labels the ruler across the top -
    /// where VOCALOID, a piano roll and ptSequencer's own horizontal view all keep them. The two
    /// thicknesses have to trade places for that to look right, which is what
    /// <c>VerticalTimelineViewModel.SetChromeSizes</c> is for.
    /// </para>
    /// <para>
    /// One matrix and its true inverse, held together, so a caller that draws through
    /// <see cref="ScreenFromSurface"/> and hit-tests through <see cref="ToSurface(Point)"/> cannot
    /// disagree with itself about where a tick is - the same property
    /// <c>VerticalCoordinateSystem</c> buys by centralizing <c>TickToY</c> and <c>YToTick</c>.
    /// </para>
    /// </summary>
    internal sealed class TimelineSurfaceMap
    {
        private readonly Matrix _toScreen;
        private readonly Matrix _toSurface;
        private readonly MatrixTransform _screenFromSurface;
        private readonly MatrixTransform _surfaceFromScreen;

        private TimelineSurfaceMap(
            TimelineOrientation orientation,
            double screenWidth,
            double screenHeight,
            bool flipCrossAxis)
        {
            Orientation = orientation;

            if (orientation == TimelineOrientation.Horizontal)
            {
                // screenX = surfaceY (time), screenY = surfaceX (lanes). flipCrossAxis measures the
                // cross axis back from the far edge instead, so a value lane grows the way every
                // DAW draws one - up from the bottom, not down from the top.
                _toScreen = flipCrossAxis
                    ? new Matrix(0, -1, 1, 0, 0, screenHeight)
                    : new Matrix(0, 1, 1, 0, 0, 0);
                SurfaceWidth = screenHeight;
                SurfaceHeight = screenWidth;
            }
            else
            {
                // Identity, and deliberately also for flipCrossAxis: the vertical surface is the
                // one every existing test and every existing pixel measurement was taken against,
                // so it must come out of here bit-for-bit unchanged.
                _toScreen = Matrix.Identity;
                SurfaceWidth = screenWidth;
                SurfaceHeight = screenHeight;
            }

            // Invert through a local. Matrix is a mutable struct, so calling Invert() on the
            // readonly field would invert a defensive copy and silently leave the field alone -
            // the same "the compiler agrees, the runtime does something else" shape as the
            // Array.Clear-on-a-WaveBuffer bug in SampleBuffers.
            Matrix inverse = _toScreen;
            inverse.Invert();
            _toSurface = inverse;

            _screenFromSurface = new MatrixTransform(_toScreen);
            _screenFromSurface.Freeze();
            _surfaceFromScreen = new MatrixTransform(_toSurface);
            _surfaceFromScreen.Freeze();
        }

        /// <summary>A map for a surface that is not laid out yet; safe to hit-test through.</summary>
        public static readonly TimelineSurfaceMap VerticalIdentity =
            new TimelineSurfaceMap(TimelineOrientation.Vertical, 0, 0, false);

        public static TimelineSurfaceMap Create(
            TimelineOrientation orientation, double screenWidth, double screenHeight)
        {
            return new TimelineSurfaceMap(orientation, screenWidth, screenHeight, false);
        }

        /// <summary>
        /// As <see cref="Create"/>, but with the cross axis measured from the far edge. For the
        /// note-volume lane: transposed, its value axis becomes vertical, and a volume of zero
        /// belongs at the bottom of the lane rather than at the top.
        /// </summary>
        public static TimelineSurfaceMap CreateFlipped(
            TimelineOrientation orientation, double screenWidth, double screenHeight)
        {
            return new TimelineSurfaceMap(orientation, screenWidth, screenHeight, true);
        }

        public TimelineOrientation Orientation { get; private set; }

        /// <summary>True when surface and screen axes are swapped, i.e. horizontal.</summary>
        public bool IsTransposed
        {
            get { return Orientation == TimelineOrientation.Horizontal; }
        }

        /// <summary>Width to build the frame with: the screen's height when transposed.</summary>
        public double SurfaceWidth { get; private set; }

        /// <summary>Height to build the frame with: the screen's width when transposed.</summary>
        public double SurfaceHeight { get; private set; }

        /// <summary>Push before drawing surface-space geometry.</summary>
        public Transform ScreenFromSurface
        {
            get { return _screenFromSurface; }
        }

        /// <summary>
        /// Push <i>on top of</i> <see cref="ScreenFromSurface"/> to get back to real screen space.
        /// Text needs it: a glyph run drawn under the reflection comes out mirrored, so labels are
        /// positioned transposed and then drawn upright.
        /// </summary>
        public Transform SurfaceFromScreen
        {
            get { return _surfaceFromScreen; }
        }

        public Point ToScreen(Point surface)
        {
            return _toScreen.Transform(surface);
        }

        public Point ToSurface(Point screen)
        {
            return _toSurface.Transform(screen);
        }

        public Rect ToScreen(Rect surface)
        {
            Rect rect = surface;
            rect.Transform(_toScreen);
            return rect;
        }
    }
}
