using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using ArcadeOS.Models;

namespace ArcadeOS.Services;

public sealed class RetroBatLaunchService(RetroBatSettings settings)
{
    public ProcessStartInfo CreateStartInfo(Game game)
    {
        if (!game.CanLaunch || !File.Exists(game.RomPath)) throw new InvalidOperationException("The game file is missing. Refresh your library before trying again.");
        if (!Regex.IsMatch(game.SystemId!, @"^[a-zA-Z0-9_-]+$")) throw new InvalidOperationException("Invalid RetroBat system identifier.");
        if (!File.Exists(settings.LauncherPath)) throw new FileNotFoundException("RetroBat's launcher was not found.", settings.LauncherPath);
        var info = new ProcessStartInfo(settings.LauncherPath)
        { WorkingDirectory = Path.GetDirectoryName(settings.LauncherPath)!, UseShellExecute = false };
        // Profiles belong to ArcadeOS; no RetroBat configuration files are edited.
        info.ArgumentList.Add("-system");
        info.ArgumentList.Add(game.SystemId!);
        info.ArgumentList.Add("-rom");
        info.ArgumentList.Add(Path.GetFullPath(game.RomPath!));
        if (settings.LaunchProfiles.TryGetValue(game.SystemId!, out var profile))
        {
            if (profile.Emulator is { Length: > 0 } emulator)
            {
                info.ArgumentList.Add("-emulator");
                info.ArgumentList.Add(emulator);
            }
            if (profile.Core is { Length: > 0 } core)
            {
                if (!Regex.IsMatch(core, @"^[a-zA-Z0-9_-]+$")) throw new InvalidOperationException("Invalid emulator core identifier.");
                if (profile.Emulator == "libretro" && !File.Exists(Path.Combine(settings.InstallationPath,
                    "emulators", "retroarch", "cores", core + "_libretro.dll")))
                    throw new FileNotFoundException($"The configured RetroArch core '{core}' is not installed.");
                info.ArgumentList.Add("-core");
                info.ArgumentList.Add(core);
            }
        }
        return info;
    }

    public async Task<int> LaunchAsync(Game game, Action started, Action? windowReady = null, CancellationToken cancellationToken = default, Action<IntPtr>? gameWindow = null)
    {
        using var process = Process.Start(CreateStartInfo(game)) ?? throw new IOException("RetroBat could not be started.");
        started();
        await Task.Run(() => EmulatorWindowMonitor.TrackSessionAsync(process, windowReady, gameWindow, cancellationToken), cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }
}
