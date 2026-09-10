using System;
using System.IO;
using System.Text;

namespace DJMaxEditor.Diagnostics
{
    /// <summary>
    /// Minimal local diagnostic log. Records format-detection decisions and unexpected failures to a
    /// file next to the executable so problems are diagnosable after the fact. It deliberately records
    /// only metadata (formats, offsets, messages) — never chart or audio *contents* — and never sends
    /// anything anywhere.
    /// </summary>
    public static class DiagnosticLog
    {
        private static readonly object Gate = new object();
        private static string _path;

        public static string LogPath
        {
            get
            {
                if (_path == null)
                {
                    string dir;
                    try { dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location); }
                    catch { dir = null; }
                    if (string.IsNullOrEmpty(dir)) dir = Directory.GetCurrentDirectory();
                    _path = Path.Combine(dir, "djmaxeditor-diagnostics.log");
                }
                return _path;
            }
        }

        public static void Write(string category, string message)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{category}] {message}";
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // Diagnostics must never take down the app; swallow logging IO errors only.
            }
        }

        public static void Exception(string category, Exception ex)
        {
            if (ex == null) { Write(category, "(null exception)"); return; }
            Write(category, ex.ToString());
        }
    }
}
