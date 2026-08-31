using System;

namespace DJMaxEditor.Studio.Video
{
    /// <summary>
    /// The source the shell holds when there is no decoder to hold.
    /// <para>
    /// Windows N and KN editions ship without Media Foundation and without the H.264, MPEG-4 and
    /// VC-1 decoders, so <see cref="MediaFoundationBgaSource"/> cannot even be constructed usefully
    /// there. Rather than let the shell carry a null and null-check every call site, it carries this
    /// - every operation succeeds at doing nothing, and <see cref="Open"/> reports why.
    /// </para>
    /// </summary>
    public sealed class NullBgaSource : IBgaSource
    {
        public bool IsOpen => false;

        public int PixelWidth => 0;

        public int PixelHeight => 0;

        public TimeSpan Duration => TimeSpan.Zero;

        public double FrameRate => 0.0;

        public string Description => string.Empty;

        public bool Open(string path, out string error)
        {
            error = "Video preview unavailable.";
            return false;
        }

        public void Close()
        {
        }

        public bool TryGetFrame(TimeSpan position, BgaFrameBuffer destination)
        {
            return false;
        }

        public void Dispose()
        {
        }
    }
}
