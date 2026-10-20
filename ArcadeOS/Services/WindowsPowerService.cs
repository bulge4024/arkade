using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
namespace ArcadeOS.Services;

public enum ComputerPowerAction { PowerOff, Sleep, Restart }
public interface IComputerPowerService { Task ExecuteAsync(ComputerPowerAction action); }
public sealed class WindowsPowerService : IComputerPowerService
{
    public static ProcessStartInfo CreateShutdownCommand(ComputerPowerAction action)
    {
        if (action is not (ComputerPowerAction.PowerOff or ComputerPowerAction.Restart)) throw new ArgumentOutOfRangeException(nameof(action));
        var info = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "shutdown.exe")) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(action == ComputerPowerAction.PowerOff ? "/s" : "/r");
        info.ArgumentList.Add("/t"); info.ArgumentList.Add("0");
        return info;
    }
    public async Task ExecuteAsync(ComputerPowerAction action)
    {
        if (action == ComputerPowerAction.Sleep) { await Task.Run(Sleep).ConfigureAwait(false); return; }
        using var process = Process.Start(CreateShutdownCommand(action)) ?? throw new InvalidOperationException("Windows could not open its power controls.");
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Windows declined the power request (code {process.ExitCode}).");
    }
    private static void Sleep()
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x28, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var enabled = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
            if (!AdjustTokenPrivileges(token, false, ref enabled, Marshal.SizeOf<TokenPrivileges>(), out var previous, out _)
                || Marshal.GetLastWin32Error() != 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { if (!SetSuspendState(false, false, false)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
            finally { AdjustTokenPrivileges(token, false, ref previous, Marshal.SizeOf<TokenPrivileges>(), out _, out _); }
        }
        finally { CloseHandle(token); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges state, int size, out TokenPrivileges previous, out int returned);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("powrprof.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate, [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool disableWakeEvents);
}

