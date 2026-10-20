using System.IO;
using System.Text.Json;

namespace ArcadeOS.Services;

public sealed record RetroBatSettings
{
    public string InstallationPath { get; init; } = @"C:\RetroBat";
    // Optional parent folder containing system folders, e.g. nes, snes, mame.
    // When absent, use the paths already configured in RetroBat's es_systems.cfg.
    public string? RomsRoot { get; init; }
    public bool AutomaticMetadataEnabled { get; init; } = true;
    public Dictionary<string, RetroBatLaunchProfile> LaunchProfiles { get; init; } = new();
    public string LauncherPath => Path.Combine(InstallationPath, "emulationstation", "emulatorLauncher.exe");
    public static RetroBatSettings Load(string filePath) => File.Exists(filePath)
        ? JsonSerializer.Deserialize<RetroBatSettings>(File.ReadAllText(filePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("RetroBat settings are empty.")
        : new();
}

public sealed record RetroBatLaunchProfile
{
    public string? Emulator { get; init; }
    public string? Core { get; init; }
}
