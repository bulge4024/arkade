using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
namespace ArcadeOS;

public sealed record SecondaryDisplayContent(string Title, string Subtitle, string Label, string Footer, string Symbol,
    ImageSource? Artwork, ImageSource? Background = null, ImageSource? Marquee = null)
{ public bool HasMarquee => Marquee is not null; }
public partial class SecondaryDisplayWindow : Window, Services.ISecondaryDisplayHost
{
    private Services.DisplayScreen screen;
    public SecondaryDisplayWindow(Services.DisplayScreen screen)
    {
        this.screen = screen; InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            long style = IntPtr.Size == 8 ? GetWindowLongPtr(handle, -20).ToInt64() : GetWindowLong(handle, -20);
            // A visual-only companion: never take keyboard/joystick focus or become an app-switch target.
            if (IntPtr.Size == 8) SetWindowLongPtr(handle, -20, new IntPtr(style | 0x08000000L | 0x80L));
            else SetWindowLong(handle, -20, (int)(style | 0x08000000L | 0x80L));
            Position();
        };
    }
    public void Present(Services.DisplayScreen target, SecondaryDisplayContent content)
    {
        screen = target;
        if (!Equals(DataContext, content)) DataContext = content;
        if (!IsVisible) Show(); Position();
    }
    private void Position()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) SetWindowPos(handle, IntPtr.Zero, screen.X, screen.Y, screen.Width, screen.Height, 0x0010 | 0x0004);
    }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint="SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint="GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint="SetWindowLongW")] private static extern int SetWindowLong(IntPtr window, int index, int value);
}
