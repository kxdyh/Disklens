using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DiskLens.App.Services;

/// <summary>Uses the current monitor's full bounds and restores the original window.</summary>
public sealed class WindowFullscreenController(Window window)
{
    private WindowSnapshot? _snapshot;
    private HwndSource? _source;
    private IntPtr _monitor;
    private NativeRect _monitorBounds;
    private DispatcherOperation? _pendingBounds;

    private const uint PositionFlags = 0x0020 | 0x0010 | 0x0004; // FRAMECHANGED | NOACTIVATE | NOZORDER

    public bool IsFullscreen => _snapshot is not null;

    public void SetFullscreen(bool fullscreen)
    {
        window.VerifyAccess();
        if (fullscreen == IsFullscreen)
        {
            return;
        }

        if (!fullscreen)
        {
            RestoreWindow();
            return;
        }

        var handle = new WindowInteropHelper(window).EnsureHandle();
        _monitor = NativeMethods.MonitorFromWindow(handle, 2 /* MONITOR_DEFAULTTONEAREST */);
        ReadMonitorBounds(handle);
        var bounds = window.WindowState == WindowState.Normal ? CurrentBounds() : window.RestoreBounds;
        if (bounds.IsEmpty || !double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top))
        {
            bounds = CurrentBounds();
        }

        NativeRect? nativeBounds = window.WindowState == WindowState.Normal
            && NativeMethods.GetWindowRect(handle, out var currentNativeBounds) ? currentNativeBounds : null;
        _snapshot = new WindowSnapshot(window.WindowState, window.WindowStyle, window.ResizeMode,
            bounds, window.MinWidth, window.MinHeight, window.MaxWidth, window.MaxHeight, window.SizeToContent, nativeBounds);

        try
        {
            _source = HwndSource.FromHwnd(handle)
                ?? throw new InvalidOperationException("The fullscreen window has no HWND source.");
            _source.AddHook(WindowMessage);
            window.Closed += WindowClosed;
            window.StateChanged += WindowStateChanged;
            // Work-area constraints and content sizing must not limit monitor coverage.
            window.MinWidth = 0;
            window.MinHeight = 0;
            window.MaxWidth = double.PositiveInfinity;
            window.MaxHeight = double.PositiveInfinity;
            window.SizeToContent = SizeToContent.Manual;
            window.WindowState = WindowState.Normal;
            window.WindowStyle = WindowStyle.None;
            window.ResizeMode = ResizeMode.NoResize;
            ApplyMonitorBounds();
            QueueMonitorBounds();
        }
        catch
        {
            RestoreWindow();
            throw;
        }
    }

    private void ReadMonitorBounds(IntPtr handle)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(_monitor, ref info))
        {
            _monitor = NativeMethods.MonitorFromWindow(handle, 2);
            if (!NativeMethods.GetMonitorInfo(_monitor, ref info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read fullscreen monitor bounds.");
        }
        _monitorBounds = info.Monitor;
    }

    private void ApplyMonitorBounds()
    {
        if (_source is null || _source.IsDisposed || !IsFullscreen || window.WindowState == WindowState.Minimized)
            return;

        ReadMonitorBounds(_source.Handle);
        // Keep WPF's DIP properties consistent too. A native-only resize is otherwise
        // undone by the next layout pass. The HWND hook protects the physical bounds
        // while per-monitor DPI changes update the composition target's transform.
        var bounds = new Rect(_monitorBounds.Left, _monitorBounds.Top,
            _monitorBounds.Right - _monitorBounds.Left, _monitorBounds.Bottom - _monitorBounds.Top);
        var logicalBounds = bounds;
        logicalBounds.Transform(_source.CompositionTarget.TransformFromDevice);
        window.Left = logicalBounds.Left;
        window.Top = logicalBounds.Top;
        window.Width = logicalBounds.Width;
        window.Height = logicalBounds.Height;
        if (!NativeMethods.SetWindowPos(_source.Handle, IntPtr.Zero, _monitorBounds.Left, _monitorBounds.Top,
                _monitorBounds.Right - _monitorBounds.Left, _monitorBounds.Bottom - _monitorBounds.Top, PositionFlags))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to position the fullscreen window.");
    }

    private IntPtr WindowMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!IsFullscreen) return IntPtr.Zero;

        if (message == 0x0046 /* WM_WINDOWPOSCHANGING */
            && window.WindowState != WindowState.Minimized && !NativeMethods.IsIconic(handle))
        {
            var position = Marshal.PtrToStructure<WindowPosition>(lParam);
            // Enforce monitor coordinates for later WPF layout/size updates as well.
            // Do not move a window for a pure activation/Z-order notification.
            if ((position.Flags & 0x0003 /* NOSIZE | NOMOVE */) != 0x0003)
            {
                position.X = _monitorBounds.Left;
                position.Y = _monitorBounds.Top;
                position.Width = _monitorBounds.Right - _monitorBounds.Left;
                position.Height = _monitorBounds.Bottom - _monitorBounds.Top;
                position.Flags &= ~0x0003u;
                Marshal.StructureToPtr(position, lParam, false);
            }
        }
        else if (message is 0x02E0 /* WM_DPICHANGED */ or 0x007E /* WM_DISPLAYCHANGE */)
        {
            QueueMonitorBounds();
        }
        return IntPtr.Zero;
    }

    private void QueueMonitorBounds()
    {
        if (_pendingBounds?.Status == DispatcherOperationStatus.Pending) return;
        _pendingBounds = window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _pendingBounds = null;
            ApplyMonitorBounds();
        }));
    }

    private void Detach()
    {
        _pendingBounds?.Abort();
        _pendingBounds = null;
        if (_source is { IsDisposed: false }) _source.RemoveHook(WindowMessage);
        _source = null;
        window.Closed -= WindowClosed;
        window.StateChanged -= WindowStateChanged;
    }

    private void WindowStateChanged(object? sender, EventArgs e)
    {
        if (IsFullscreen && window.WindowState != WindowState.Minimized) QueueMonitorBounds();
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        _snapshot = null;
        Detach();
    }

    private Rect CurrentBounds() => new(
        double.IsFinite(window.Left) ? window.Left : 0,
        double.IsFinite(window.Top) ? window.Top : 0,
        double.IsFinite(window.Width) && window.Width > 0 ? window.Width : Math.Max(1, window.ActualWidth),
        double.IsFinite(window.Height) && window.Height > 0 ? window.Height : Math.Max(1, window.ActualHeight));

    private void RestoreWindow()
    {
        var snapshot = _snapshot!;
        _snapshot = null;
        Detach();
        window.WindowState = WindowState.Normal;
        window.WindowStyle = snapshot.Style;
        window.ResizeMode = snapshot.ResizeMode;
        window.Left = snapshot.Bounds.Left;
        window.Top = snapshot.Bounds.Top;
        window.Width = snapshot.Bounds.Width;
        window.Height = snapshot.Bounds.Height;
        window.MaxWidth = snapshot.MaxWidth;
        window.MaxHeight = snapshot.MaxHeight;
        window.MinWidth = snapshot.MinWidth;
        window.MinHeight = snapshot.MinHeight;
        window.SizeToContent = snapshot.SizeToContent;
        window.WindowState = snapshot.State;
        if (snapshot.NativeBounds is { } native && snapshot.SizeToContent == SizeToContent.Manual)
        {
            NativeMethods.SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero,
                native.Left, native.Top, native.Right - native.Left, native.Bottom - native.Top, PositionFlags);
        }
    }

    private sealed record WindowSnapshot(WindowState State, WindowStyle Style, ResizeMode ResizeMode,
        Rect Bounds, double MinWidth, double MinHeight, double MaxWidth, double MaxHeight,
        SizeToContent SizeToContent, NativeRect? NativeBounds);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public IntPtr Window;
        public IntPtr InsertAfter;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public uint Flags;
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
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
            int x, int y, int width, int height, uint flags);
    }
}
