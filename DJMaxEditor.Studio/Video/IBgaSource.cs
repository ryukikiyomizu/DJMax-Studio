using System;

namespace DJMaxEditor.Studio.Video
{
    /// <summary>
    /// A decoder the BGA preview can pull single frames out of, by absolute time.
    /// <para>
    /// This seam exists for two reasons. First, the only real implementation
    /// (<see cref="MediaFoundationBgaSource"/>) needs Media Foundation, which is simply absent on
    /// Windows N/KN editions until the user installs the Media Feature Pack - so the shell has to
    /// be able to hold a <see cref="NullBgaSource"/> instead and keep running. Second, nothing
    /// about the preview's letterboxing, same-frame suppression or clock mapping needs a real
    /// codec to be tested, and a test double that hands back a synthetic gradient exercises all of
    /// it without a media file or a working MF stack.
    /// </para>
    /// <para>
    /// There is deliberately no play/pause/rate here. The editor's sequencer owns time; a source is
    /// a pure function from a position to a frame. That is the same reason Media Foundation's
    /// source reader was chosen over the media engine - see
    /// <see cref="MediaFoundationBgaSource"/>.
    /// </para>
    /// </summary>
    public interface IBgaSource : IDisposable
    {
        bool IsOpen { get; }

        int PixelWidth { get; }

        int PixelHeight { get; }

        /// <summary><see cref="TimeSpan.Zero"/> when the container does not declare a duration.</summary>
        TimeSpan Duration { get; }

        /// <summary>Frames per second, or 0 when unknown.</summary>
        double FrameRate { get; }

        /// <summary>Codec / container summary for the shell's status line.</summary>
        string Description { get; }

        /// <summary>
        /// Opens <paramref name="path"/>. Must never throw: a failure is a human-readable
        /// <paramref name="error"/> and a <c>false</c> return, because the caller is a UI action
        /// (drop a file on the preview) and a codec that is not installed is an ordinary outcome,
        /// not a bug.
        /// </summary>
        bool Open(string path, out string error);

        void Close();

        /// <summary>
        /// Decodes the frame at or just before <paramref name="position"/> and copies BGRA32 into
        /// <paramref name="destination"/>. Returns false if no frame is available.
        /// </summary>
        bool TryGetFrame(TimeSpan position, BgaFrameBuffer destination);
    }

    /// <summary>
    /// One decoded frame, in the layout <see cref="System.Windows.Media.PixelFormats.Bgr32"/>
    /// wants, reused across frames.
    /// <para>
    /// This is on the scrub path, which runs at composition rate. A 1280x720 frame is 3.5 MB, so
    /// allocating one per frame would push ~100 MB/s of short-lived large-object-heap traffic
    /// while the playhead moves - which is exactly the kind of allocation-in-the-paint-loop
    /// problem the timeline renderer was rewritten to eliminate. Hence
    /// <see cref="Resize(int, int)"/> reallocates only when the dimensions actually change.
    /// </para>
    /// </summary>
    public sealed class BgaFrameBuffer
    {
        public BgaFrameBuffer()
        {
            // Not TimeSpan.Zero: zero is a perfectly valid timestamp (the first frame of every
            // video), so a zero sentinel would make "buffer is empty" indistinguishable from
            // "buffer holds frame 0" and the preview's same-frame suppression would skip the very
            // first paint. MinValue cannot collide with a real presentation time.
            Timestamp = TimeSpan.MinValue;
        }

        public byte[] Pixels { get; private set; }

        public int Width { get; private set; }

        public int Height { get; private set; }

        public int Stride { get; private set; }

        /// <summary>Presentation time of the frame currently in <see cref="Pixels"/>.</summary>
        public TimeSpan Timestamp { get; set; }

        /// <summary>True once a source has written a frame into this buffer.</summary>
        public bool HasFrame => Pixels != null && Timestamp != TimeSpan.MinValue;

        public void Resize(int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                return;
            }

            if (Pixels != null && Width == width && Height == height)
            {
                return;
            }

            Width = width;
            Height = height;
            Stride = width * 4;
            Pixels = new byte[Stride * height];
            Timestamp = TimeSpan.MinValue;
        }

        public void Clear()
        {
            Timestamp = TimeSpan.MinValue;
        }
    }
}
