using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArcadeOS.Models;

namespace ArcadeOS.Services;

public sealed record CachedGameMetadata
{
    public string SystemId { get; init; } = "";
    public string RomName { get; init; } = "";
    public string? Title { get; init; }
    public string? Year { get; init; }
    public string? Players { get; init; }
    public string? Genre { get; init; }
    public string? Developer { get; init; }
    public string? Publisher { get; init; }
    public string? Description { get; init; }
    public string? ArtworkFile { get; init; }
    public string? SourceUrl { get; init; }
    public string? ArtworkSourceUrl { get; init; }
    public string? ArtworkRights { get; init; }
}

// Offline catalog: separated from emulator discovery so future providers can populate
// the same cache without changing launch integrations or writing RetroBat gamelists.
public sealed class LocalMetadataService(string cacheDirectory)
{
    public LibraryScanResult Apply(LibraryScanResult scan)
    {
        string catalog = Path.Combine(cacheDirectory, "catalog.json");
        if (!File.Exists(catalog)) return scan;
        var warnings = scan.Warnings.ToList();
        try
        {
            var entries = JsonSerializer.Deserialize<CachedGameMetadata[]>(File.ReadAllText(catalog),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            var lookup = entries.Where(entry => entry.SystemId.Length > 0 && entry.RomName.Length > 0)
                .GroupBy(entry => Key(entry.SystemId, entry.RomName))
                .ToDictionary(group => group.Key, group => group.Last());
            var games = scan.Games.Select(game =>
            {
                if (game.RomPath is null || game.SystemId is null || !lookup.TryGetValue(
                    Key(game.SystemId, Path.GetFileNameWithoutExtension(game.RomPath)), out var entry)) return game;
                string? artwork = null;
                if (entry.ArtworkFile is { Length: > 0 })
                {
                    string candidate = Path.GetFullPath(entry.ArtworkFile, cacheDirectory);
                    string relative = Path.GetRelativePath(cacheDirectory, candidate);
                    if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar)
                        && File.Exists(candidate)) artwork = candidate;
                    else warnings.Add($"{game.Title}: cached cover missing or outside the cache; using existing artwork.");
                }
                return game with
                {
                    Title = Present(entry.Title, game.Title), Year = Present(entry.Year, game.Year),
                    Players = Present(entry.Players, game.Players), Description = Present(entry.Description, game.Description),
                    ArtworkPath = artwork ?? game.ArtworkPath, Genre = entry.Genre,
                    Developer = entry.Developer, Publisher = entry.Publisher, MetadataSource = entry.SourceUrl
                };
            }).ToArray();
            return new(games, warnings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            warnings.Add("ArcadeOS metadata cache could not be read; using RetroBat metadata.");
            return new(scan.Games, warnings);
        }
    }
    private static string Present(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
    private static string Key(string system, string name)
    {
        string clean = Regex.Replace(name, @"\([^)]*\)|\[[^\]]*\]", "");
        return system.ToLowerInvariant() + ":" + Regex.Replace(clean.ToLowerInvariant(), @"[^\p{L}\p{N}]", "");
    }
}
