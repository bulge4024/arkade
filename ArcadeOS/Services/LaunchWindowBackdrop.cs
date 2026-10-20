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
    }
    public static void PlaceBehindGame(Window window, IntPtr game)
    {
        if (game == IntPtr.Zero) return;
        var handle = new WindowInteropHelper(window).Handle;
        // Keep the transition immediately underneath the emulator; never use Topmost.
        SetWindowPos(handle, game, 0, 0, 0, 0, 0x0013);
        SetForegroundWindow(game);
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowLongPtrW(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
}
