using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using DiskLens.App.Services;
using Xunit.Abstractions;

namespace DiskLens.App.Tests;

public sealed class WindowFullscreenControllerTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(-4)] // Per-monitor DPI aware v2.
    [InlineData(-2)] // System DPI aware.
    public void Fullscreen_covers_each_monitor_and_survives_later_wpf_layout(int dpiContext) => OnSta(() =>
    {
        var previousContext = NativeMethods.SetThreadDpiAwarenessContext(new IntPtr(dpiContext));
        Assert.NotEqual(IntPtr.Zero, previousContext);
        try
        {
            var monitors = new List<MonitorInfo>();
            NativeMethods.MonitorCallback callback = (IntPtr monitor, IntPtr dc, ref NativeRect rect, IntPtr data) =>
            {
                var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
                Assert.True(NativeMethods.GetMonitorInfo(monitor, ref info));
                monitors.Add(info);
                return true;
            };
            Assert.True(NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero));
            Assert.NotEmpty(monitors);

            foreach (var monitor in monitors)
            {
                var window = CreateWindow();
                var handle = new WindowInteropHelper(window).EnsureHandle();
                try
                {
                    // Choose this monitor using native coordinates, including negative origins.
                    Assert.True(NativeMethods.SetWindowPos(handle, IntPtr.Zero,
                        monitor.Monitor.Left + 30, monitor.Monitor.Top + 30, 640, 420, 0x0014));
                    Pump();
                    var before = Bounds(handle);
                    var controller = new WindowFullscreenController(window);
                    controller.SetFullscreen(true);
                    Pump();
                    Assert.True(controller.IsFullscreen);
                    Assert.Equal(WindowState.Normal, window.WindowState);
                    Assert.Equal(monitor.Monitor, Bounds(handle));
                    Assert.Equal(SizeToContent.Manual, window.SizeToContent);

                    // Reproduce the regression: a later WPF layout requests windowed bounds.
                    window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                    {
                        window.Width = 500;
                        window.Height = 300;
                        window.Left = 80;
                        window.Top = 80;
                        window.UpdateLayout();
                    }));
                    Pump();
                    Assert.Equal(monitor.Monitor, Bounds(handle));
                    NativeMethods.SendMessage(handle, 0x007E /* WM_DISPLAYCHANGE */, IntPtr.Zero, IntPtr.Zero);
                    Pump();
                    Assert.Equal(monitor.Monitor, Bounds(handle));
                    window.WindowState = WindowState.Minimized;
                    Pump();
                    Assert.Equal(WindowState.Minimized, window.WindowState);
                    window.WindowState = WindowState.Normal;
                    Pump();
                    Assert.Equal(monitor.Monitor, Bounds(handle));
                    output.WriteLine($"DPI context {dpiContext}: monitor={monitor.Monitor}; work area={monitor.WorkArea}; actual={Bounds(handle)}");

                    controller.SetFullscreen(false);
                    Pump();
                    Assert.False(controller.IsFullscreen);
                    Assert.Equal(WindowStyle.SingleBorderWindow, window.WindowStyle);
                    Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
                    Assert.Equal(300, window.MinWidth);
                    Assert.Equal(200, window.MinHeight);
                    Assert.Equal(900, window.MaxWidth);
                    Assert.Equal(600, window.MaxHeight);
                    Assert.Equal(before, Bounds(handle));
                    window.Width = 700;
                    Pump();
                    Assert.NotEqual(monitor.Monitor, Bounds(handle));
                }
                finally { window.Close(); }
            }
        }
        finally { NativeMethods.SetThreadDpiAwarenessContext(previousContext); }
    });

    [Theory]
    [InlineData(WindowState.Normal)]
    [InlineData(WindowState.Maximized)]
    public void Exit_restores_state_and_content_sizing_even_before_deferred_entry_completes(WindowState state) => OnSta(() =>
    {
        var window = CreateWindow();
        window.SizeToContent = SizeToContent.WidthAndHeight;
        window.Content = new Border { Width = 540, Height = 320 };
        new WindowInteropHelper(window).EnsureHandle();
        window.WindowState = state;
        var controller = new WindowFullscreenController(window);
        try
        {
            for (var index = 0; index < 3; index++)
            {
                controller.SetFullscreen(true);
                controller.SetFullscreen(true); // Idempotent: do not overwrite the original snapshot.
                controller.SetFullscreen(false);
                controller.SetFullscreen(false);
                Pump();
                Assert.False(controller.IsFullscreen);
                Assert.Equal(state, window.WindowState);
                Assert.Equal(SizeToContent.WidthAndHeight, window.SizeToContent);
                Assert.Equal(WindowStyle.SingleBorderWindow, window.WindowStyle);
                Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
                Assert.Equal(900, window.MaxWidth);
                Assert.Equal(600, window.MaxHeight);
            }
            controller.SetFullscreen(true);
            window.Close();
            Pump();
            Assert.False(controller.IsFullscreen);
        }
        finally { if (new WindowInteropHelper(window).Handle != IntPtr.Zero) window.Close(); }
    });

    private static Window CreateWindow() => new()
    {
        Left = 120, Top = 100, Width = 640, Height = 420,
        MinWidth = 300, MinHeight = 200, MaxWidth = 900, MaxHeight = 600,
        WindowStartupLocation = WindowStartupLocation.Manual,
        ShowInTaskbar = false,
    };

    private static NativeRect Bounds(IntPtr handle)
    {
        Assert.True(NativeMethods.GetWindowRect(handle, out var bounds));
        return bounds;
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Fullscreen HWND test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect : IEquatable<NativeRect>
    {
        public int Left, Top, Right, Bottom;
        public readonly bool Equals(NativeRect other) => Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;
        public override readonly bool Equals(object? obj) => obj is NativeRect other && Equals(other);
        public override readonly int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);
        public override readonly string ToString() => $"[{Left},{Top}..{Right},{Bottom}]";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor, WorkArea;
        public uint Flags;
    }

    private static class NativeMethods
    {
        internal delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref NativeRect rect, IntPtr data);
        [DllImport("user32.dll")]
        internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr handle, out NativeRect bounds);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")]
        internal static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
    }
}
