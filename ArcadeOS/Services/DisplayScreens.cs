using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace ArcadeOS.Services;

public sealed record DisplayScreen(string Device, int X, int Y, int Width, int Height, bool Primary);
public static class DisplayScreens
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    { public int Size; public Rect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    private static DisplayScreen? Read(IntPtr handle)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
        return GetMonitorInfo(handle, ref info) ? new(info.Device, info.Monitor.Left, info.Monitor.Top,
            info.Monitor.Right-info.Monitor.Left, info.Monitor.Bottom-info.Monitor.Top, (info.Flags & 1) != 0) : null;
    }
    public static IReadOnlyList<DisplayScreen> GetAll()
    {
        var screens = new List<DisplayScreen>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) => { if (Read(monitor) is { } screen) screens.Add(screen); return true; }, IntPtr.Zero);
        return screens;
    }
    public static string? DeviceForWindow(IntPtr handle) => Read(MonitorFromWindow(handle, 2))?.Device;
    public static DisplayScreen? Select(IReadOnlyList<DisplayScreen> screens, string? mainDevice, string? preference)
    {
        if (preference == "off") return null;
        var main = screens.FirstOrDefault(screen => screen.Device.Equals(mainDevice, StringComparison.OrdinalIgnoreCase));
        if (main is null) return null;
        // Cloned/overlapping displays share desktop pixels: never place artwork over the launcher.
        var others = screens.Where(screen => !screen.Device.Equals(mainDevice, StringComparison.OrdinalIgnoreCase)
            && !(screen.X < main.X + main.Width && screen.X + screen.Width > main.X
                && screen.Y < main.Y + main.Height && screen.Y + screen.Height > main.Y));
        return preference is { Length: > 0 } ? others.FirstOrDefault(screen => screen.Device.Equals(preference, StringComparison.OrdinalIgnoreCase))
            : others.OrderByDescending(screen => screen.Width >= screen.Height).ThenBy(screen => screen.Device, StringComparer.Ordinal).FirstOrDefault();
    }
}
public sealed class SecondaryDisplayPreferences
{
    private readonly string path;
    // null = automatic; off = disabled; otherwise a Windows display device identifier.
    public string? Device { get; private set; }
    private sealed record Saved(string? Device);
    public SecondaryDisplayPreferences(string? path = null)
    {
        this.path = path ?? Path.Combine(AutomaticMetadataService.DefaultCacheDirectory, "secondary-display.json");
        try { if (File.Exists(this.path)) Device = JsonSerializer.Deserialize<Saved>(File.ReadAllText(this.path))?.Device; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }
    public void Set(string? device)
    {
        string temp = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(temp, JsonSerializer.Serialize(new Saved(device))); File.Move(temp, path, true); Device = device;
    }
}
