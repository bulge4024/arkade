using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Threading;
using System.IO;
namespace ArcadeOS;

public sealed record SecondaryDisplayContent(string Title, string Subtitle, string Label, string Footer, string Symbol,
    ImageSource? Artwork, ImageSource? Background = null, ImageSource? Marquee = null, string? VideoPath = null)
{ public bool HasMarquee => Marquee is not null; }
public partial class SecondaryDisplayWindow : Window, Services.ISecondaryDisplayHost
{
    private Services.DisplayScreen screen;
    private readonly DispatcherTimer videoDelay = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly HashSet<string> failedVideos = new(StringComparer.OrdinalIgnoreCase);
    private string? requestedVideoKey, pendingVideo;
    public SecondaryDisplayWindow(Services.DisplayScreen screen)
    {
        this.screen = screen; InitializeComponent();
        videoDelay.Tick += (_, _) => StartBackdropVideo();
        IsVisibleChanged += (_, _) => { if (!IsVisible) StopBackdropVideo(); else UpdateBackdropVideo((DataContext as SecondaryDisplayContent)?.VideoPath); };
        Closed += (_, _) => StopBackdropVideo();
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
        UpdateBackdropVideo(content.VideoPath);
    }
    private void UpdateBackdropVideo(string? path)
    {
        if (!IsVisible || string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path)) { StopBackdropVideo(); return; }
        string key;
        try { key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StopBackdropVideo(); return; }
        if (requestedVideoKey == key) return;
        StopBackdropVideo(); requestedVideoKey = key;
        if (failedVideos.Contains(key)) return;
        pendingVideo = path; videoDelay.Start();
    }
    private void StartBackdropVideo()
    {
        videoDelay.Stop(); var path = pendingVideo; pendingVideo = null;
        if (!IsVisible || path == null || path != (DataContext as SecondaryDisplayContent)?.VideoPath || !File.Exists(path)) return;
        BackgroundVideo.Source = new Uri(path, UriKind.Absolute); BackgroundVideo.Play();
    }
    private void StopBackdropVideo()
    {
        videoDelay.Stop(); pendingVideo = null; requestedVideoKey = null;
        BackgroundVideo.Opacity = 0; BackgroundVideo.Close(); BackgroundVideo.Source = null;
    }
    private void BackdropVideoOpened(object sender, RoutedEventArgs e)
    {
        if (!IsVisible) { StopBackdropVideo(); return; }
        BackgroundVideo.Opacity = .4;
    }
    private void BackdropVideoEnded(object sender, RoutedEventArgs e)
    {
        if (!IsVisible || BackgroundVideo.Source == null) return;
        BackgroundVideo.Position = TimeSpan.Zero; BackgroundVideo.Play();
    }
    private void BackdropVideoFailed(object sender, ExceptionRoutedEventArgs e)
    {
        var key = requestedVideoKey;
        if (key != null) { if (failedVideos.Count >= 12) failedVideos.Clear(); failedVideos.Add(key); }
        StopBackdropVideo(); requestedVideoKey = key; // Leave the fanart visible; do not retry a broken clip each refresh.
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
