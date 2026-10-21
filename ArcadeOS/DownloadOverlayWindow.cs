using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ArcadeOS.Controls;
using ArcadeOS.Services;

namespace ArcadeOS;
// A small, click-through tool window; it never activates or changes emulator focus.
public sealed class DownloadOverlayWindow : Window
{
    private readonly DownloadProgressWidget widget = new();
    public DownloadOverlayWindow()
    {
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Focusable = false; IsHitTestVisible = false; Topmost = true;
        Width = 222; Height = 72; Content = widget;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtrW(handle, -20, new IntPtr(GetWindowLongPtrW(handle, -20).ToInt64() | 0x08000000 | 0x80 | 0x20));
        };
    }
    public static bool IsGameForeground(IntPtr game)
    {
        if (game == IntPtr.Zero) return false;
        GetWindowThreadProcessId(game, out uint gameProcess); GetWindowThreadProcessId(GetForegroundWindow(), out uint foregroundProcess);
        return gameProcess != 0 && gameProcess == foregroundProcess;
    }
    public void Update(Window cabinet, RomQueueProgress progress)
    {
        Width = widget.Width = cabinet.ActualWidth < 620 ? 180 : 222;
        Left = cabinet.Left + cabinet.ActualWidth - Width - 22; Top = cabinet.Top + 20;
        widget.Update(progress); if (!IsVisible) Show();
    }
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtrW(IntPtr h, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint process);
}
