using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using DJMaxEditor.Studio.Design;

namespace DJMaxEditor.Studio.Shell
{
    /// <summary>
    /// A chromeless window with an app-drawn title bar.
    ///
    /// VOCALOID 6 does the same thing - its resource paths show a `FlatTitleWindow` with
    /// swappable `Themes/{theme}.xaml` and `Themes/Accents/{accent}.xaml` dictionaries - and it
    /// is the right call for a tool: the OS caption is a bright strip of foreign colour above a
    /// dark canvas, and it wastes 32px that a transport readout can use instead.
    ///
    /// The two things that go wrong with chromeless WPF windows are both handled here:
    /// a maximised window covering the taskbar (WM_GETMINMAXINFO), and the 7px phantom border
    /// WindowChrome leaves around a maximised client area (MaximizedPadding).
    /// </summary>
    public class StudioWindow : Window
    {
        public static readonly DependencyProperty TitleBarHeightProperty =
            DependencyProperty.Register(
                "TitleBarHeight",
                typeof(double),
                typeof(StudioWindow),
                new PropertyMetadata(StudioMetrics.TitleBarHeight, OnTitleBarHeightChanged));

        /// <summary>Padding the content needs when maximised so it is not clipped by the chrome.</summary>
        public static readonly DependencyProperty MaximizedPaddingProperty =
            DependencyProperty.Register(
                "MaximizedPadding",
                typeof(Thickness),
                typeof(StudioWindow),
                new PropertyMetadata(new Thickness(0)));

        private HwndSource _hwndSource;

        public StudioWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = false;
            ResizeMode = ResizeMode.CanResize;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            ApplyChrome();

            CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand, (s, e) => SystemCommands.MinimizeWindow(this)));
            CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand, (s, e) => SystemCommands.MaximizeWindow(this)));
            CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand, (s, e) => SystemCommands.RestoreWindow(this)));
            CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (s, e) => SystemCommands.CloseWindow(this)));

            StateChanged += (s, e) => UpdateMaximizedPadding();
        }

        public double TitleBarHeight
        {
            get { return (double)GetValue(TitleBarHeightProperty); }
            set { SetValue(TitleBarHeightProperty, value); }
        }

        public Thickness MaximizedPadding
        {
            get { return (Thickness)GetValue(MaximizedPaddingProperty); }
            private set { SetValue(MaximizedPaddingProperty, value); }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwndSource = (HwndSource)PresentationSource.FromVisual(this);
            if (_hwndSource != null)
            {
                _hwndSource.AddHook(WindowProc);
            }
            UpdateMaximizedPadding();
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_hwndSource != null)
            {
                _hwndSource.RemoveHook(WindowProc);
                _hwndSource = null;
            }
            base.OnClosed(e);
        }

        /// <summary>
        /// Drag anywhere on the title bar; double-click toggles maximise. Call this from the
        /// title bar's MouseLeftButtonDown handler.
        /// </summary>
        public void BeginTitleBarDrag(MouseButtonEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                e.Handled = true;
                return;
            }

            // DragMove throws if the button is already up by the time we get here, which happens
            // with a fast click on a slow frame.
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                try
                {
                    DragMove();
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        private static void OnTitleBarHeightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            StudioWindow window = d as StudioWindow;
            if (window != null)
            {
                window.ApplyChrome();
            }
        }

        private void ApplyChrome()
        {
            WindowChrome chrome = new WindowChrome();
            chrome.CaptionHeight = TitleBarHeight;
            // 6px is the smallest grab band that still feels like a resize edge at 100% scaling.
            chrome.ResizeBorderThickness = new Thickness(6);
            chrome.GlassFrameThickness = new Thickness(0);
            chrome.CornerRadius = new CornerRadius(0);
            chrome.UseAeroCaptionButtons = false;
            WindowChrome.SetWindowChrome(this, chrome);
        }

        private void UpdateMaximizedPadding()
        {
            // WindowChrome inflates a maximised window by the resize border on every edge, so
            // without this the first and last few pixels of content sit off-screen.
            MaximizedPadding = WindowState == WindowState.Maximized
                ? new Thickness(7, 7, 7, 7)
                : new Thickness(0);
        }

        private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_GETMINMAXINFO = 0x0024;
            if (msg == WM_GETMINMAXINFO)
            {
                if (ConstrainToWorkArea(hwnd, lParam))
                {
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// Clamps the maximised size to the monitor's work area so a maximised chromeless window
        /// does not swallow the taskbar. Returns false if the monitor could not be resolved, in
        /// which case the default (wrong, but harmless) behaviour stands.
        /// </summary>
        private static bool ConstrainToWorkArea(IntPtr hwnd, IntPtr lParam)
        {
            const int MONITOR_DEFAULTTONEAREST = 0x00000002;

            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
            {
                return false;
            }

            MonitorInfo info = new MonitorInfo();
            info.Size = Marshal.SizeOf(typeof(MonitorInfo));
            if (!GetMonitorInfo(monitor, ref info))
            {
                return false;
            }

            MinMaxInfo minMax = (MinMaxInfo)Marshal.PtrToStructure(lParam, typeof(MinMaxInfo));
            minMax.MaxPosition.X = info.WorkArea.Left - info.Monitor.Left;
            minMax.MaxPosition.Y = info.WorkArea.Top - info.Monitor.Top;
            minMax.MaxSize.X = info.WorkArea.Right - info.WorkArea.Left;
            minMax.MaxSize.Y = info.WorkArea.Bottom - info.WorkArea.Top;
            Marshal.StructureToPtr(minMax, lParam, true);
            return true;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MinMaxInfo
        {
            public NativePoint Reserved;
            public NativePoint MaxSize;
            public NativePoint MaxPosition;
            public NativePoint MinTrackSize;
            public NativePoint MaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect WorkArea;
            public uint Flags;
        }
    }
}
