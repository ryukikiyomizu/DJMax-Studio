using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace DJMaxEditor.Studio
{
    /// <summary>
    /// Application entry point.
    /// </summary>
    public partial class App : Application
    {
        private static readonly string CrashLogPath = Path.Combine(
            Path.GetDirectoryName(typeof(App).Assembly.Location) ?? ".", "studio-crash.log");

        /// <summary>
        /// Command line: an optional chart path to open on start. Kept as a field rather than
        /// re-read from Environment so tests can drive startup without touching the process.
        /// </summary>
        public string StartupChartPath { get; private set; }

        /// <summary>
        /// Command line: an optional BGA video to attach on start, as the second argument.
        ///
        /// Charts and their video live in different folders in every real DJMax layout - the
        /// keysounds sit beside the chart, the video one level up with the rest of the pack - so
        /// there is no reliable way to guess it. This is how a repeatable startup gets both.
        /// </summary>
        public string StartupBgaPath { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            // The chart formats parse numbers with invariant rules; a comma-decimal locale
            // silently corrupts tempo and volume values otherwise. The legacy editor had the
            // same trap and solved it per call site, which is why one BPM bug kept coming back.
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

            // Headless verification mode. Renders the playfield to PNG and exits without ever
            // showing a window, so a build can be checked without a foreground window and without
            // synthetic keystrokes landing in whatever app the user is actually using.
            if (e.Args != null && e.Args.Length > 0 &&
                string.Equals(e.Args[0], Preview.PlayfieldProbe.Switch, StringComparison.Ordinal))
            {
                int code = Preview.PlayfieldProbe.Run(e.Args);
                Shutdown(code);
                return;
            }

            // The same idea for the audio path: render the keysound mix to a WAV and measure it,
            // because a scheduler that sounds wrong sounds wrong in the samples and nowhere else.
            if (e.Args != null && e.Args.Length > 0 &&
                string.Equals(e.Args[0], Audio.KeysoundRenderProbe.Switch, StringComparison.Ordinal))
            {
                int code = Audio.KeysoundRenderProbe.Run(e.Args);
                Shutdown(code);
                return;
            }

            // And the one thing neither of those can see. Both render through a null output, which
            // hands the graph an honest float[] and passes a finished buffer straight to the
            // assertion. A real device does not: NAudio's SampleToWaveProvider re-types the driver's
            // own byte[] as a float[], and a range argument that is right for samples is a quarter of
            // the range in bytes. That is what the pause tail turned out to be, and no offline probe
            // could have seen it. This mode opens the real device and measures the endpoint's own mix.
            if (e.Args != null && e.Args.Length > 0 &&
                string.Equals(e.Args[0], Audio.PauseTailProbe.Switch, StringComparison.Ordinal))
            {
                int code = Audio.PauseTailProbe.Run(e.Args);
                Shutdown(code);
                return;
            }

            // And the question neither of those answers: what is actually on the tracks the
            // TECHNIKA playfield does not draw. "The other tracks do not show up" is only a defect
            // if those tracks carry gameplay, which is a property of the corpus and not of the code.
            if (e.Args != null && e.Args.Length > 0 &&
                string.Equals(e.Args[0], Preview.TrackCensusProbe.Switch, StringComparison.Ordinal))
            {
                int code = Preview.TrackCensusProbe.Run(e.Args);
                Shutdown(code);
                return;
            }

            if (e.Args != null && e.Args.Length > 0 && File.Exists(e.Args[0]))
            {
                StartupChartPath = e.Args[0];
            }

            if (e.Args != null && e.Args.Length > 1 && File.Exists(e.Args[1]))
            {
                StartupBgaPath = e.Args[1];
            }

            base.OnStartup(e);

            // Built here rather than through StartupUri so StartupChartPath is already set when the
            // window's Loaded handler reads it, and so a construction failure lands in our own
            // crash log instead of the generic XAML-parse dialog.
            MainWindow = new Shell.MainWindow();
            MainWindow.Show();
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // A chart editor that dies on a bad file loses the user's work. Log, tell them, and
            // stay alive; only a genuinely corrupt render pass should ever take the app down.
            Log("dispatcher", e.Exception);
            MessageBox.Show(
                e.Exception.Message + Environment.NewLine + Environment.NewLine +
                "Details were written to " + CrashLogPath,
                "DJMax Studio hit an error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            e.Handled = true;
        }

        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Log("domain", e.ExceptionObject as Exception);
        }

        private static void Log(string source, Exception exception)
        {
            try
            {
                StringBuilder text = new StringBuilder();
                text.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("] ");
                text.Append(source).AppendLine();
                text.AppendLine(exception == null ? "(no exception object)" : exception.ToString());
                text.AppendLine();
                File.AppendAllText(CrashLogPath, text.ToString());
            }
            catch (IOException)
            {
                // Logging must never be the thing that crashes us.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
