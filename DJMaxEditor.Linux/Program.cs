using System;
using System.Linq;
using System.Text;
using Avalonia;

namespace DJMaxEditor.Linux
{
    internal static class Program
    {
        public static string InitialChartPath { get; private set; }

        [STAThread]
        public static void Main(string[] args)
        {
            if (args.Any(argument => string.Equals(argument, "--version", StringComparison.Ordinal)))
            {
                Console.WriteLine("DJMax Studio Linux 0.1.0");
                return;
            }

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            InitialChartPath = args.FirstOrDefault(argument => !argument.StartsWith("-"));
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace();
        }
    }
}
