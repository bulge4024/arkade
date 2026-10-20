using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArcadeOS.Models;
namespace ArcadeOS.Services;

public interface ISecondaryDisplayHost
{
    void Present(DisplayScreen screen, SecondaryDisplayContent content);
    void Hide();
    void Close();
}
public sealed class SecondaryDisplayService : IDisposable
{
    private readonly SecondaryDisplayPreferences preferences;
    private readonly Func<IReadOnlyList<DisplayScreen>> screens;
    private readonly Func<IntPtr, string?> mainDevice;
    private readonly Func<DisplayScreen, ISecondaryDisplayHost> createHost;
    private readonly Dictionary<string, ImageSource> images = new(StringComparer.OrdinalIgnoreCase);
    private ISecondaryDisplayHost? host;
    public SecondaryDisplayService(SecondaryDisplayPreferences? preferences = null,
        Func<IReadOnlyList<DisplayScreen>>? screens = null, Func<IntPtr, string?>? mainDevice = null,
        Func<DisplayScreen, ISecondaryDisplayHost>? createHost = null)
    {
        this.preferences = preferences ?? new(); this.screens = screens ?? DisplayScreens.GetAll;
        this.mainDevice = mainDevice ?? DisplayScreens.DeviceForWindow;
        this.createHost = createHost ?? (screen => new SecondaryDisplayWindow(screen));
    }
    public static bool IsPinball(Game game) => game.Category.Equals("Pinball", StringComparison.OrdinalIgnoreCase)
        || game.SystemId is "vpinball" or "fpinball" or "pinballfx" or "pinballfx2" or "pinballfx3" or "pinballm";
    public string SettingLabel => "SECOND SCREEN: " + (preferences.Device switch { null => "AUTO", "off" => "OFF", _ => preferences.Device.Replace(@"\\.\", "") });
    public string Status(IntPtr window)
    {
        if (preferences.Device == "off") return "Second-screen artwork is off. Pinball's DMD remains available.";
        var target = DisplayScreens.Select(screens(), mainDevice(window), preferences.Device);
        return target is null ? "No separate display available. Connect an extended display or choose another screen."
            : $"{target.Device.Replace(@"\\.\", "")} · {target.Width} × {target.Height}. Hidden while pinball is running.";
    }
    public void CycleTarget(IntPtr window)
    {
        var choices = new string?[] { "off", null }.Concat(screens().Where(screen => !screen.Device.Equals(mainDevice(window), StringComparison.OrdinalIgnoreCase)).Select(screen => screen.Device)).ToArray();
        int current = Array.FindIndex(choices, choice => string.Equals(choice, preferences.Device, StringComparison.OrdinalIgnoreCase));
        preferences.Set(choices[(current + 1) % choices.Length]);
    }
    public void Update(IntPtr window, SystemTile? system, Game? selectedGame, bool browsingSystems, Game? runningGame, ImageSource? recentArtwork = null)
    {
        // The DMD belongs exclusively to pinball from before launcher startup until it exits.
        if (runningGame is not null && IsPinball(runningGame)) { Hide(); return; }
        var target = DisplayScreens.Select(screens(), mainDevice(window), preferences.Device);
        if (target is null) { Hide(); return; }
        SecondaryDisplayContent content;
        var game = runningGame ?? (browsingSystems ? null : selectedGame);
        if (game is not null)
        {
            var art = LoadImage(game.ArtworkPath) ?? LoadImage(game.FanartPath);
            var marquee = LoadImage(game.MarqueePath);
            content = new(game.Title, marquee is not null ? game.Title : game.Platform,
                game.Platform.ToUpperInvariant() + (runningGame is null ? "  /  GAME SELECTED" : "  /  NOW PLAYING"),
                string.Join("  ·  ", new[] { game.MetadataLine, game.Players }.Where(text => !string.IsNullOrWhiteSpace(text))),
                art is null ? game.Symbol : "", art, LoadImage(game.BackgroundArtworkPath), marquee);
        }
        else if (system is not null)
        {
            var art = system.Name == ViewModels.LibraryViewModel.RecentCategory ? recentArtwork : LoadImage(system.ConsoleImagePath);
            content = new(system.Name, system.Subtitle, "CHOOSE YOUR SYSTEM", "ARKADE  /  " + system.CountLabel,
                art is null ? system.Symbol : "", art, LoadImage(system.ArtworkPath));
        }
        else content = new("ARKADE", "Every era. One cabinet.", "THE CABINET COLLECTION", "READY TO PLAY", "", null);
        host ??= createHost(target);
        host.Present(target, content);
    }
    private ImageSource? LoadImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            string key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
            if (images.TryGetValue(key, out var cached)) return cached;
            using var stream = File.OpenRead(path);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 1600; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            if (images.Count >= 12) images.Clear(); images[key] = bitmap; return bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FileFormatException) { return null; }
    }
    public void Hide() => host?.Hide();
    public void Dispose() { host?.Close(); host = null; images.Clear(); }
}
