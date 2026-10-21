namespace ArcadeOS.Models;

public sealed record Game(string Title, string Category, string Year, string Players,
    string Tagline, string Description, string Accent, string AccentDark, string Symbol)
{
    public DateTimeOffset? LastPlayed { get; init; }
    public string? RomPath { get; init; }
    public string? SystemId { get; init; }
    public string? SystemName { get; init; }
    public string? SystemManufacturer { get; init; }
    public string? SystemAliases { get; init; }
    public string? VideoPath { get; init; }
    public string? MarqueePath { get; init; }
    public string? FanartPath { get; init; }
    public string? BackgroundArtworkPath => FanartPath ?? ArtworkPath;
    public string? ArtworkPath { get; init; }
    public string? Genre { get; init; }
    public string? Developer { get; init; }
    public string? Publisher { get; init; }
    public string? MetadataSource { get; init; }
    public string Platform => SystemId switch
    {
        "megadrive" or "genesis" => "Mega Drive", "nes" => "NES", "snes" => "SNES",
        "sega32x" or "32x" => "Sega 32X", "mame" => "MAME", "fbneo" => "Arcade", "ports" => "Ports", "vpinball" => "Visual Pinball",
        null => Category, _ => SystemId.ToUpperInvariant()
    };
    public string MetadataLine => string.Join("  •  ", new[] { Year, Genre }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string Credits => string.Join("  /  ", new[] { Developer, Publisher }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public bool CanLaunch => RomPath is not null && SystemId is not null;
    public string Availability => CanLaunch ? "Ready to play via RetroBat" : "Demo entry • No game file connected";
}
