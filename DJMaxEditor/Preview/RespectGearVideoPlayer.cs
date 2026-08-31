using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace DJMaxEditor.Preview
{
    /// <summary>
    /// Streams the optional 502x244 gear-bottom MP4 through ffmpeg on a worker
    /// thread. The paint path only composites the most recently decoded frame;
    /// it never rebuilds animation layers on the UI thread.
    /// </summary>
    internal sealed class RespectGearVideoPlayer : IDisposable
    {
        public const int FrameWidth = 502;
        public const int FrameHeight = 244;
        // The gear-bottom loop decodes at the source video's native frame rate
        // (paced by ffmpeg's -re); playback is uncapped, so we do not force a fixed
        // output fps. The compositor always draws the most recently decoded frame.
        private readonly object _sync = new object();
        private readonly string _videoPath;
        private Process _process;
        private Thread _reader;
        private Bitmap _frame;
        private bool _disposed;

        public RespectGearVideoPlayer(string videoPath)
        {
            _videoPath = videoPath;
        }

        public event EventHandler FrameReady;

        public bool IsAvailable
        {
            get
            {
                return File.Exists(_videoPath) && FindFfmpeg() != null;
            }
        }

        public void Start()
        {
            if (_disposed || _process != null || !File.Exists(_videoPath)) return;
            string ffmpeg = FindFfmpeg();
            if (ffmpeg == null) return;

            var start = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = "-hide_banner -loglevel error -re -stream_loop -1 -i \"" +
                    _videoPath + "\" -an -vf scale=" + FrameWidth + ":" + FrameHeight +
                    " -f rawvideo -pix_fmt bgra pipe:1",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            try
            {
                _process = Process.Start(start);
                if (_process == null) return;
                _process.ErrorDataReceived += delegate { };
                _process.BeginErrorReadLine();
                _reader = new Thread(ReadFrames)
                {
                    IsBackground = true,
                    Name = "DJMAX gear video decoder"
                };
                _reader.Start();
            }
            catch (InvalidOperationException)
            {
                _process = null;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                _process = null;
            }
        }

        public bool Draw(Graphics graphics, Rectangle target)
        {
            lock (_sync)
            {
                if (_frame == null || target.Width <= 0 || target.Height <= 0)
                {
                    return false;
                }
                graphics.DrawImage(_frame, target);
                return true;
            }
        }

        public void Dispose()
        {
            _disposed = true;
            Process process = _process;
            _process = null;
            if (process != null)
            {
                try
                {
                    if (!process.HasExited) process.Kill();
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
                process.Dispose();
            }

            lock (_sync)
            {
                if (_frame != null) _frame.Dispose();
                _frame = null;
            }
        }

        private void ReadFrames()
        {
            Process process = _process;
            if (process == null) return;
            int frameBytes = FrameWidth * FrameHeight * 4;
            byte[] bytes = new byte[frameBytes];

            try
            {
                Stream stream = process.StandardOutput.BaseStream;
                while (!_disposed && ReadExact(stream, bytes, frameBytes))
                {
                    Bitmap next = CreateBitmap(bytes);
                    lock (_sync)
                    {
                        Bitmap previous = _frame;
                        _frame = next;
                        if (previous != null) previous.Dispose();
                    }
                    EventHandler ready = FrameReady;
                    if (ready != null) ready(this, EventArgs.Empty);
                }
            }
            catch (IOException)
            {
                // Static gear art remains available if the decoder exits.
            }
            catch (ObjectDisposedException)
            {
                // Normal during preview shutdown.
            }
        }

        private static Bitmap CreateBitmap(byte[] bytes)
        {
            var bitmap = new Bitmap(
                FrameWidth, FrameHeight, PixelFormat.Format32bppArgb);
            BitmapData data = bitmap.LockBits(
                new Rectangle(0, 0, FrameWidth, FrameHeight),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            return bitmap;
        }

        private static bool ReadExact(Stream stream, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0) return false;
                offset += read;
            }
            return true;
        }

        private static string FindFfmpeg()
        {
            string configured = Environment.GetEnvironmentVariable(
                "DJMAX_EDITOR_FFMPEG");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            {
                return configured;
            }

            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string directory in path.Split(Path.PathSeparator))
            {
                try
                {
                    string candidate = Path.Combine(directory.Trim(), "ffmpeg.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                }
            }

            string installed = @"C:\Program Files\ffmpeg\bin\ffmpeg.exe";
            return File.Exists(installed) ? installed : null;
        }
    }
}
