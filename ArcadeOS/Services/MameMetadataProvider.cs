using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using ArcadeOS.Models;
namespace ArcadeOS.Services;

// MAME short names are identifiers, never fuzzy-match them as human titles.
public sealed class MameMetadataProvider(HttpClient client, string directory) : IGameMetadataProvider
{
    private sealed record Entry(string Title, string Year, string Maker);
    private Dictionary<string, Entry[]>? games;
    private readonly LibretroMetadataProvider images = new(client, directory);
    public bool Supports(string systemId) => systemId == "mame";
    public async Task<CachedGameMetadata?> FindAsync(Game game, CancellationToken token)
    {
        if (!Supports(game.SystemId ?? "") || game.RomPath is null) return null;
        if (games is null)
        {
            string path = Path.Combine(directory, "databases", "MAME-rom-names.dat");
            string text;
            if (File.Exists(path)) text = await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
            else
            {
                text = await client.GetStringAsync("https://raw.githubusercontent.com/libretro/libretro-database/master/metadat/mame/MAME.dat", token).ConfigureAwait(false);
                if (text.Length > 32 * 1024 * 1024) throw new InvalidDataException("Arcade database exceeded its size limit.");
                await AutomaticMetadataService.WriteAtomicAsync(path, text, token).ConfigureAwait(false);
            }
            var rows = new List<(string Rom, Entry Data)>();
            foreach (Match block in Regex.Matches(text, @"(?ms)^game\s*\(\s*(.*?)^\)"))
            {
                string value = block.Groups[1].Value;
                string Field(string field) => Regex.Match(value, "(?m)^\\s*" + field + "\\s+\"([^\"]*)\"").Groups[1].Value;
                string rom = Regex.Match(value, @"\brom\s*\(\s*name\s+""?([^\s""()]+)\.zip" , RegexOptions.IgnoreCase).Groups[1].Value;
                if (rom.Length > 0 && Field("name").Length > 0)
                    rows.Add((rom, new(Field("name"), Field("year"), Field("developer"))));
            }
            games = rows.GroupBy(row => row.Rom, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Select(row => row.Data).Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
        }
        string romName = Path.GetFileNameWithoutExtension(game.RomPath);
        if (!games.TryGetValue(romName, out var matches)) return null;
        // Consolidated sets can contain several historical revisions of the same identifier.
        // Keep a neutral display title and try their documented artwork names in order.
        var data = matches[0];
        string title = MetadataNames.Clean(data.Title);
        string? artwork = null, artworkUrl = null;
        string destination = Path.Combine(directory, "covers", AutomaticMetadataService.CacheKey(game) + ".png");
        foreach (string name in matches.Select(row => row.Title).Concat(matches.Select(row => MetadataNames.Clean(row.Title))).Distinct())
        {
            string file = Regex.Replace(name, "[&*/:`<>?\\\\|\"]", "_") + ".png";
            foreach (string kind in new[] { "Named_Boxarts", "Named_Titles", "Named_Snaps" })
            {
                string url = "https://raw.githubusercontent.com/libretro-thumbnails/MAME/master/" + kind + "/" + Uri.EscapeDataString(file);
                try
                {
                    if (!File.Exists(destination) && !await images.DownloadCoverAsync(url, destination, token).ConfigureAwait(false)) continue;
                    artwork = Path.GetRelativePath(directory, destination); artworkUrl = url; break;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException || ex is OperationCanceledException && !token.IsCancellationRequested) { }
            }
            if (artwork is not null) break;
        }
        return new CachedGameMetadata
        {
            SystemId = game.SystemId!, RomName = romName, Title = title, Year = data.Year, Developer = data.Maker,
            Genre = "Arcade", Description = $"{title} is an arcade game by {data.Maker}, released in {data.Year}.",
            ArtworkFile = artwork, ArtworkSourceUrl = artworkUrl,
            ArtworkRights = "Game artwork remains copyright of its respective owners; cached for the personal cabinet.",
            SourceUrl = "https://github.com/libretro/libretro-database/blob/master/metadat/mame/MAME.dat"
        };
    }
}
