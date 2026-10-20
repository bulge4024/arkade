using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ArcadeOS.Services;
public static class LaunchWindowBackdrop
{
    private const int ExtendedStyle = -20;
    private const long NoActivate = 0x08000000;
    public static void SetBackgroundMode(Window window, bool enabled)
    {
        var handle = new WindowInteropHelper(window).Handle; if (handle == IntPtr.Zero) return;
        var style = GetWindowLongPtrW(handle, ExtendedStyle).ToInt64();
        SetWindowLongPtrW(handle, ExtendedStyle, new IntPtr(enabled ? style | NoActivate : style & ~NoActivate));
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x0037); // Apply the changed activation style without moving or activating.
    }
    public static void PlaceBehindGame(Window window, IntPtr game)
    {
        if (game == IntPtr.Zero) return;
        var handle = new WindowInteropHelper(window).Handle;
        // Keep the transition immediately underneath the emulator; never use Topmost.
        SetWindowPos(handle, game, 0, 0, 0, 0, 0x0013);
        SetForegroundWindow(game);
    }
    public static bool RestoreForeground(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle; if (handle == IntPtr.Zero) return false;
        SetBackgroundMode(window, false);
        if (IsIconic(handle)) ShowWindow(handle, 9);
        window.Show(); window.Activate();
        uint currentThread = GetCurrentThreadId();
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        bool attached = foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            BringWindowToTop(handle); SetForegroundWindow(handle);
            if (GetForegroundWindow() == handle) SetFocus(handle);
        }
        finally { if (attached) AttachThreadInput(currentThread, foregroundThread, false); }
        return GetForegroundWindow() == handle;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowLongPtrW(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachThreadInput(uint from, uint to, [MarshalAs(UnmanagedType.Bool)] bool attach);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr window, int command);
}
