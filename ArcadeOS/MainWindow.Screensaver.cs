using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ArcadeOS.Services;

namespace ArcadeOS;
public partial class MainWindow
{
    private readonly ScreensaverIdlePolicy screensaverIdle = new(Environment.TickCount64);
    private readonly WindowsDisplayAwake screensaverDisplay = new();
    private readonly DispatcherTimer screensaverTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly HashSet<Key> screensaverWakeKeys = [];
    private readonly HashSet<string> rejectedSaverVideos = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> saverFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private ScreensaverPlaylist? screensaverPlaylist;
    private HwndSource? screensaverMessageSource;
    private IInputElement? screensaverFocus;
    private Cursor? screensaverCursor;
    private bool preparingScreensaver, screensaverWasWindowed, queuedScreensaverClip, screensaverClipOpened, screensaverClosing, screensaverRestorePending;
    private NativePoint screensaverMouse;
    private long screensaverClipDeadline, screensaverSuppressUntil;
    private bool ScreensaverActive => ScreensaverOverlay.Visibility == Visibility.Visible;
    private bool ScreensaverEligible => !windowClosed && !launching && !powerBusy && !downloadingUpdate && IsActive && IsVisible && WindowState != WindowState.Minimized
        && StartupSplash.Visibility != Visibility.Visible && UtilityOverlay.Visibility != Visibility.Visible && DetailsOverlay.Visibility != Visibility.Visible
        && PowerOverlay.Visibility != Visibility.Visible && UpdateOverlay.Visibility != Visibility.Visible && ControllerOverlay.Visibility != Visibility.Visible && !RomBrowserOpen;
    private void NoteScreensaverActivity() => screensaverIdle.Touch(Environment.TickCount64);
    private void InitializeScreensaver()
    {
        SourceInitialized += (_, _) =>
        {
            screensaverMessageSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            screensaverMessageSource?.AddHook(HandleScreensaverPowerMessage);
        };
        Loaded += (_, _) => { GetCursorPos(out screensaverMouse); NoteScreensaverActivity(); screensaverTimer.Start(); };
        Closed += (_, _) => { screensaverClosing = true; screensaverTimer.Stop(); StopScreensaver(restore: false); screensaverDisplay.Dispose(); screensaverMessageSource?.RemoveHook(HandleScreensaverPowerMessage); };
        Activated += (_, _) => { NoteScreensaverActivity(); if (screensaverRestorePending && fullscreen) { screensaverRestorePending = false; ToggleFullscreenClick(this, new RoutedEventArgs()); } };
        Deactivated += (_, _) => { screensaverWakeKeys.Clear(); NoteScreensaverActivity(); StopScreensaver(restore: false); screensaverDisplay.SetActive(false); };
        PreviewKeyUp += (_, e) => { if (screensaverWakeKeys.Remove(e.Key == Key.System ? e.SystemKey : e.Key)) e.Handled = true; };
        PreviewMouseDown += (_, e) => { NoteScreensaverActivity(); if (ScreensaverActive) { StopScreensaver(); e.Handled = true; } };
        PreviewMouseWheel += (_, e) => { NoteScreensaverActivity(); if (ScreensaverActive) { StopScreensaver(); e.Handled = true; } };
        PreviewMouseMove += (_, _) =>
        {
            if (!GetCursorPos(out var point) || point.X == screensaverMouse.X && point.Y == screensaverMouse.Y) return;
            screensaverMouse = point; NoteScreensaverActivity(); if (ScreensaverActive) StopScreensaver();
        };
        ScreensaverVideo.ClipOpened += () => { screensaverClipOpened = true; screensaverClipDeadline = Environment.TickCount64 + 30_000; };
        ScreensaverVideo.ClipEnded += QueueNextScreensaverClip;
        ScreensaverVideo.ClipFailed += path =>
        {
            screensaverPlaylist?.Reject(path);
            if (saverFingerprints.TryGetValue(path, out var fingerprint)) rejectedSaverVideos.Add(fingerprint);
            QueueNextScreensaverClip();
        };
        screensaverTimer.Tick += async (_, _) =>
        {
            try { await TickScreensaverAsync(Environment.TickCount64); }
            catch (Exception ex) when (MetadataDiagnostics.Recoverable(ex))
            { StopScreensaver(); MetadataDiagnostics.Record(AutomaticMetadataService.DefaultCacheDirectory, ex, "Screensaver"); }
        };
    }
    private IntPtr HandleScreensaverPowerMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0112 && ((wParam.ToInt64() & 0xfff0) is 0xf140 or 0xf170) && IsActive && !launching && IsVisible)
        { handled = true; return IntPtr.Zero; }
        return IntPtr.Zero;
    }
    private async Task TickScreensaverAsync(long now)
    {
        bool ownsDisplay = !windowClosed && IsVisible && IsActive && !launching && WindowState != WindowState.Minimized;
        screensaverDisplay.SetActive(ownsDisplay);
        if (ScreensaverActive)
        {
            if (!ScreensaverEligible) { StopScreensaver(restore: false); return; }
            if (now >= screensaverClipDeadline)
            {
                if (!screensaverClipOpened && ScreensaverVideo.CurrentPath is { } stalled)
                {
                    screensaverPlaylist?.Reject(stalled);
                    if (saverFingerprints.TryGetValue(stalled, out var fingerprint)) rejectedSaverVideos.Add(fingerprint);
                }
                QueueNextScreensaverClip();
            }
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.None) NoteScreensaverActivity();
        if (!screensaverIdle.IsDue(now, ScreensaverEligible) || preparingScreensaver) return;
        preparingScreensaver = true;
        try
        {
            var snapshot = viewModel.GetLibrarySnapshot();
            var paths = snapshot.Select(game => game.VideoPath).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var candidates = await Task.Run(() =>
            {
                var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in paths)
                {
                    try
                    {
                        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || !new[] { ".mp4", ".m4v", ".wmv", ".avi" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
                        var info = new FileInfo(path); found[path] = path + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
                    }
                    catch (Exception ex) when (MetadataDiagnostics.Recoverable(ex)) { }
                }
                return found;
            });
            // Input or a game/dialog may have arrived while the file list was checked.
            if (!screensaverIdle.IsDue(Environment.TickCount64, ScreensaverEligible)) return;
            saverFingerprints = candidates;
            screensaverPlaylist = new(candidates.Where(p => !rejectedSaverVideos.Contains(p.Value)).Select(p => p.Key));
            string? first = screensaverPlaylist.Next();
            if (first is null) { NoteScreensaverActivity(); return; }
            screensaverFocus = Keyboard.FocusedElement; screensaverCursor = Mouse.OverrideCursor;
            screensaverWasWindowed = !fullscreen;
            if (screensaverWasWindowed) ToggleFullscreenClick(this, new RoutedEventArgs());
            StopGamePreview(); music.SetSuspended(true); Mouse.OverrideCursor = Cursors.None;
            ScreensaverOverlay.Visibility = Visibility.Visible; PlayScreensaverClip(first);
        }
        finally { preparingScreensaver = false; }
    }
    private void PlayScreensaverClip(string path)
    {
        screensaverClipOpened = false;
        screensaverClipDeadline = Environment.TickCount64 + 10_000; // Skip an unreadable or stalled decoder.
        ScreensaverVideo.Play(path);
    }
    private void QueueNextScreensaverClip()
    {
        if (!ScreensaverActive || queuedScreensaverClip) return;
        queuedScreensaverClip = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            queuedScreensaverClip = false;
            if (!ScreensaverActive) return;
            // Watchdog expiry before opening is a failed clip; expiry during playback simply moves on.
            string? next = screensaverPlaylist?.Next();
            if (next is null) { StopScreensaver(); return; }
            PlayScreensaverClip(next);
        }));
    }
    private void StopScreensaver(bool restore = true)
    {
        if (!ScreensaverActive) return;
        ScreensaverVideo.Stop(); ScreensaverOverlay.Visibility = Visibility.Collapsed;
        Mouse.OverrideCursor = screensaverCursor;
        if (screensaverWasWindowed && fullscreen && !windowClosed && !screensaverClosing)
        {
            if (IsActive) ToggleFullscreenClick(this, new RoutedEventArgs());
            else screensaverRestorePending = true;
        }
        screensaverWasWindowed = false; screensaverPlaylist = null;
        music.SetSuspended(launching || screensaverClosing); NoteScreensaverActivity();
        screensaverSuppressUntil = Environment.TickCount64 + 250;
        controllerInput.RequireNeutral();
        if (restore && IsActive && screensaverFocus is UIElement { IsVisible: true, IsEnabled: true } target) target.Focus();
        if (!windowClosed && !screensaverClosing && !launching) UpdateGamePreview();
    }
    private bool HandleScreensaverKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        NoteScreensaverActivity();
        if (screensaverWakeKeys.Contains(key)) { e.Handled = true; return true; }
        if (!ScreensaverActive) return false;
        screensaverWakeKeys.Add(key); StopScreensaver(); e.Handled = true; return true;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    private bool HandleScreensaverController(bool active)
    {
        if (!active || !controllerInput.HasRawInput) return false;
        NoteScreensaverActivity();
        if (!ScreensaverActive) return false;
        StopScreensaver(); return true;
    }
}
