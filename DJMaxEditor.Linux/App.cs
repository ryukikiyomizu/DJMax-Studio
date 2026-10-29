using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace DJMaxEditor.Linux
{
    public sealed class App : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            RequestedThemeVariant = ThemeVariant.Dark;
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var window = new MainWindow();
                desktop.MainWindow = window;
                if (!string.IsNullOrWhiteSpace(Program.InitialChartPath))
                {
                    window.LoadInitialFile(Program.InitialChartPath);
                }
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
