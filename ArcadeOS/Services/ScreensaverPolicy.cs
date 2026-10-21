using System.Runtime.InteropServices;
namespace ArcadeOS.Services;

public sealed class ScreensaverIdlePolicy(long now)
{
    public const long DelayMilliseconds = 5 * 60 * 1000;
    private long lastActivity = now;
    public void Touch(long time) => lastActivity = time;
    public bool IsDue(long time, bool eligible)
    {
        if (!eligible) { Touch(time); return false; }
        return time - lastActivity >= DelayMilliseconds;
    }
}
public sealed class ScreensaverPlaylist(IEnumerable<string> paths, Random? random = null)
{
    private readonly string[] paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private readonly Random random = random ?? Random.Shared;
    private readonly HashSet<string> failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> remaining = [];
    private string? last;
    public void Reject(string path) { failed.Add(path); remaining.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase)); }
    public string? Next()
    {
        if (remaining.Count == 0)
        {
            remaining.AddRange(paths.Where(p => !failed.Contains(p)));
            random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(remaining));
            if (remaining.Count > 1 && remaining[^1] == last) (remaining[0], remaining[^1]) = (remaining[^1], remaining[0]);
        }
        if (remaining.Count == 0) return null;
        string path = remaining[^1]; remaining.RemoveAt(remaining.Count - 1); return last = path;
    }
}
// Temporary per-thread lease: does not modify Windows power settings or prevent system sleep.
public sealed class WindowsDisplayAwake(Func<uint, uint>? setState = null) : IDisposable
{
    private readonly Func<uint, uint> setState = setState ?? SetThreadExecutionState;
    private bool held;
    public void SetActive(bool active)
    {
        if (active == held) return;
        uint result = setState(active ? 0x80000002u : 0x80000000u);
        if (result != 0) held = active;
    }
    public void Dispose() => SetActive(false);
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
}
