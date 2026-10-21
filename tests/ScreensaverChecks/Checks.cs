using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ArcadeOS.Services;
using ArcadeOS.Controls;

namespace ArcadeOS;
class Checks
{
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Policies(); System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var window = new MainWindow(); window.Show(); window.Activate(); Pump(120);
            window.RunChecks(args.FirstOrDefault()); window.Close(); app.Shutdown();
            Console.WriteLine("PASS: five-minute idle timing, blocked contexts, shuffle/rejections, display lease, keyboard/controller wake consumption, neutral gating, selection/focus restoration, no-video fallback and cleanup.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static void Policies()
    {
        var idle = new ScreensaverIdlePolicy(100);
        Check(!idle.IsDue(300099, true) && idle.IsDue(300100, true), "Five-minute boundary");
        idle.Touch(400000); Check(!idle.IsDue(600000, true), "Input did not reset idle");
        Check(!idle.IsDue(800000, false) && !idle.IsDue(800001, true) && idle.IsDue(1100000, true), "Blocked context did not reset idle");
        var clips = new ScreensaverPlaylist(["A", "a", "B", "C"], new Random(4));
        string? previous = null;
        for (int round = 0; round < 12; round++)
        {
            var next = new[] { clips.Next(), clips.Next(), clips.Next() };
            Check(next.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3, "Duplicate within shuffle round");
            Check(next[0] != previous, "Immediate round-boundary repeat"); previous = next[^1];
        }
        clips.Reject("A"); clips.Reject("B");
        Check(Enumerable.Range(0, 8).All(_ => clips.Next() == "C"), "Rejected clip played");
        clips.Reject("C"); Check(clips.Next() is null && new ScreensaverPlaylist([]).Next() is null, "Empty/broken playlist");
        var flags = new List<uint>();
        using (var lease = new WindowsDisplayAwake(f => { flags.Add(f); return 1; })) { lease.SetActive(true); lease.SetActive(true); }
        Check(flags.SequenceEqual(new uint[] { 0x80000002, 0x80000000 }), "Display lease leaked or prevented system sleep");
    }
    internal static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static void Pump(int ms)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) }; var frame = new DispatcherFrame();
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
}
public partial class MainWindow : Window
{
    private bool windowClosed, launching, powerBusy, downloadingUpdate, fullscreen, RomBrowserOpen;
    private WindowState previousState;
    private readonly Grid StartupSplash = Hidden(), UtilityOverlay = Hidden(), DetailsOverlay = Hidden(), PowerOverlay = Hidden(), UpdateOverlay = Hidden(), ControllerOverlay = Hidden(), ScreensaverOverlay = Hidden();
    private readonly AttractVideoSurface ScreensaverVideo = new();
    private readonly TestModel viewModel = new();
    private readonly TestMusic music = new();
    private readonly FakeReader reader = new();
    private readonly ControllerInputService controllerInput;
    private readonly Button selected = new() { Content = "Selected game" };
    private int actions;
    public MainWindow()
    {
        Width = 420; Height = 260; Title = "Arkade screensaver component checks";
        var root = new Grid(); root.Children.Add(selected); ScreensaverOverlay.Children.Add(ScreensaverVideo); root.Children.Add(ScreensaverOverlay); Content = root;
        controllerInput = new(reader, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        InitializeScreensaver();
        PreviewKeyDown += (_, e) => { if (!HandleScreensaverKey(e)) actions++; };
        Closed += (_, _) => windowClosed = true;
    }
    private static Grid Hidden() => new() { Visibility = Visibility.Collapsed };
    private void ToggleFullscreenClick(object sender, RoutedEventArgs e)
    {
        if (!fullscreen) { previousState = WindowState; WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; WindowState = WindowState.Maximized; }
        else { WindowState = WindowState.Normal; WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.CanResize; WindowState = previousState; }
        fullscreen = !fullscreen;
    }
    private void StopGamePreview() { }
    private void UpdateGamePreview() { }
    private void Tick()
    {
        var task = TickScreensaverAsync(Environment.TickCount64);
        for (int i = 0; !task.IsCompleted && i < 100; i++) Checks.Pump(10);
        task.GetAwaiter().GetResult();
    }
    private void Arm() => screensaverIdle.Touch(Environment.TickCount64 - ScreensaverIdlePolicy.DelayMilliseconds - 1);
    private KeyEventArgs KeyEvent(Key key, RoutedEvent type) => new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, Environment.TickCount, key) { RoutedEvent = type };
    internal void RunChecks(string? video)
    {
        screensaverTimer.Stop(); selected.Focus();
        Checks.Check(IsActive && ScreensaverEligible, "Test window not active");
        foreach (var overlay in new[] { StartupSplash, UtilityOverlay, DetailsOverlay, PowerOverlay, UpdateOverlay, ControllerOverlay })
        { overlay.Visibility = Visibility.Visible; Checks.Check(!ScreensaverEligible, "Saver allowed over dialog"); overlay.Visibility = Visibility.Collapsed; }
        launching = true; Arm(); Tick(); Checks.Check(!ScreensaverActive && !screensaverIdle.IsDue(Environment.TickCount64, true), "Saver allowed during game or immediately after"); launching = false;
        powerBusy = true; Checks.Check(!ScreensaverEligible, "Power busy eligible"); powerBusy = false;
        downloadingUpdate = true; Checks.Check(!ScreensaverEligible, "Update busy eligible"); downloadingUpdate = false;
        RomBrowserOpen = true; Checks.Check(!ScreensaverEligible, "ROM browser eligible"); RomBrowserOpen = false;
        Arm(); Tick(); Checks.Check(!ScreensaverActive, "No-video library blanked menu");
        // Use a real temporary non-video for decoder failure and a manually visible surface for input tests.
        screensaverFocus = selected; ScreensaverOverlay.Visibility = Visibility.Visible; music.SetSuspended(true);
        var wake = KeyEvent(Key.Enter, Keyboard.PreviewKeyDownEvent); RaiseEvent(wake);
        Checks.Check(wake.Handled && !ScreensaverActive && actions == 0 && selected.IsKeyboardFocused && !music.Suspended, "Wake key activated a menu item or lost focus/music");
        var held = KeyEvent(Key.Enter, Keyboard.PreviewKeyDownEvent); RaiseEvent(held); Checks.Check(held.Handled && actions == 0, "Held wake key repeated into menu");
        RaiseEvent(KeyEvent(Key.Enter, Keyboard.PreviewKeyUpEvent)); RaiseEvent(KeyEvent(Key.Enter, Keyboard.PreviewKeyDownEvent)); Checks.Check(actions == 1, "Fresh press ignored after release");
        reader.State = new("test", "Generic USB", "joystick", CabinetInput.None, 0); controllerInput.Poll(true, 0);
        ScreensaverOverlay.Visibility = Visibility.Visible;
        reader.State = reader.State with { Buttons = 1u << 20 }; controllerInput.Poll(true, 10);
        Checks.Check(controllerInput.HasRawInput && HandleScreensaverController(true) && !ScreensaverActive, "Unmapped button failed to wake");
        ScreensaverOverlay.Visibility = Visibility.Visible;
        reader.State = reader.State with { Buttons = 1 }; controllerInput.Poll(true, 20); Checks.Check(HandleScreensaverController(true), "Mapped button failed to wake");
        Checks.Check(controllerInput.Poll(true, 30) == 0, "Held wake button launched game");
        reader.State = reader.State with { Buttons = 0 }; controllerInput.Poll(true, 40);
        reader.State = reader.State with { Buttons = 1 }; Checks.Check(controllerInput.Poll(true, 50) == CabinetInput.Select, "Controller did not recover after neutral");
        reader.State = reader.State with { Buttons = 0 }; controllerInput.Poll(true, 60);
        string bad = Path.Combine(Path.GetTempPath(), "Arkade-saver-check-" + Guid.NewGuid() + ".mp4"); File.WriteAllText(bad, "broken video");
        try
        {
            viewModel.Paths = [bad]; Arm(); Tick();
            for (int i = 0; i < 60 && ScreensaverActive; i++) Checks.Pump(50);
            // Exercise the watchdog deterministically if this runner has no decoder.
            if (ScreensaverActive) { screensaverClipDeadline = 0; Tick(); Checks.Pump(50); }
            Checks.Check(!ScreensaverActive && rejectedSaverVideos.Count == 1, "Broken clip did not return to menu");
            Arm(); Tick(); Checks.Check(!ScreensaverActive, "Rejected clip retried without a file change");
            if (video is not null)
            {
                viewModel.Paths = [Path.GetFullPath(video)]; selected.Focus(); Arm(); Tick();
                for (int i = 0; i < 100 && !screensaverClipOpened; i++) Checks.Pump(50);
                Checks.Check(ScreensaverActive && fullscreen && screensaverClipOpened && ScreensaverVideo.IsMuted && music.Suspended, "Real video failed to start fullscreen and muted");
                // Short real fixture should wrap through MediaEnded without losing the overlay.
                Checks.Pump(2500); Checks.Check(ScreensaverActive && ScreensaverVideo.CurrentPath is not null, "Playlist stopped at EOF");
                RaiseEvent(KeyEvent(Key.Space, Keyboard.PreviewKeyDownEvent));
                Checks.Check(!ScreensaverActive && !fullscreen && WindowState == WindowState.Normal && ScreensaverVideo.CurrentPath is null && selected.IsKeyboardFocused, "Window/focus/decoder not restored after real playback");
                Console.WriteLine("PASS: actual muted MP4 decoding, EOF advance and fullscreen/windowed restore.");
            }
        }
        finally { StopScreensaver(); File.Delete(bad); }
    }
    private sealed class TestModel { public string[] Paths = []; public IEnumerable<TestGame> GetLibrarySnapshot() => Paths.Select(p => new TestGame(p)); }
    private sealed record TestGame(string? VideoPath);
    private sealed class TestMusic { public bool Suspended; public void SetSuspended(bool value) => Suspended = value; }
    private sealed class FakeReader : IControllerReader { public ControllerReading State = new("test", "Generic USB", "joystick", 0, 0); public IReadOnlyList<ControllerReading> Read() => [State]; }
}
