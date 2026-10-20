using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ArcadeOS.Services;

public static class EmulatorWindowMonitor
{
    public static async Task TrackSessionAsync(Process launcher, Action? ready, Action<IntPtr>? gameWindow, CancellationToken token)
    {
        var tracked = new Dictionary<uint, Process>(); bool announced = false; DateTimeOffset? emptySince = null;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                foreach (var id in GetProcessTree((uint)launcher.Id))
                {
                    if (id == launcher.Id || tracked.ContainsKey(id)) continue;
                    try { var child = Process.GetProcessById((int)id); _ = child.Handle; tracked[id] = child; }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
                var alive = tracked.Where(pair => !Exited(pair.Value)).Select(pair => pair.Key).ToHashSet();
                if (!announced)
                {
                    IntPtr window = FindGameWindow(alive);
                    if (window != IntPtr.Zero) { announced = true; gameWindow?.Invoke(window); ready?.Invoke(); }
                }
                if (launcher.HasExited && alive.Count == 0)
                {
                    emptySince ??= DateTimeOffset.UtcNow;
                    // Allow detached children to appear in the snapshot after their launcher exits.
                    if (DateTimeOffset.UtcNow - emptySince >= TimeSpan.FromSeconds(2)) return;
                }
                else emptySince = null;
                await Task.Delay(100, token).ConfigureAwait(false);
            }
        }
        finally { foreach (var child in tracked.Values) child.Dispose(); }
    }
    private static bool Exited(Process process)
    {
        try { return process.HasExited; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
    }
    private static IntPtr FindGameWindow(HashSet<uint> processes)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint processId);
            if (processes.Contains(processId) && IsWindowVisible(window) && !IsIconic(window) && GetWindowRect(window, out var bounds)
                && bounds.Right - bounds.Left >= 160 && bounds.Bottom - bounds.Top >= 100)
            {
                try
                {
                    using var process = Process.GetProcessById((int)processId);
                    if (process.ProcessName.ToLowerInvariant() is "emulatorlauncher" or "emulationstation" or "cmd" or "conhost" or "powershell" or "pwsh") return true;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
                found = window; return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static async Task WaitForWindowAsync(Process launcher, Action ready, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (launcher.HasExited) return;
                var processes = GetProcessTree((uint)launcher.Id);
                bool found = false;
                EnumWindows((window, _) =>
                {
                    GetWindowThreadProcessId(window, out uint processId);
                    if (processes.Contains(processId) && IsWindowVisible(window) && GetWindowRect(window, out var bounds)
                        && bounds.Right - bounds.Left >= 160 && bounds.Bottom - bounds.Top >= 100)
                    { found = true; return false; }
                    return true;
                }, IntPtr.Zero);
                if (found) { ready(); return; }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            { /* A process can exit between polling and inspecting its windows. */ }
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
    }
    private static HashSet<uint> GetProcessTree(uint root)
    {
        var parents = new List<(uint Id, uint Parent)>();
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (!snapshot.IsInvalid)
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExeFile = "" };
            if (Process32First(snapshot, ref entry))
                do { parents.Add((entry.ProcessId, entry.ParentProcessId)); } while (Process32Next(snapshot, ref entry));
        }
        var result = new HashSet<uint> { root };
        bool changed;
        do
        {
            changed = false;
            foreach (var (id, parent) in parents) if (result.Contains(parent) && result.Add(id)) changed = true;
        } while (changed);
        return result;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out WindowRect rectangle);
}
