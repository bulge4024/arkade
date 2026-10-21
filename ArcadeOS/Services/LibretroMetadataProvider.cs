using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArcadeOS.Models;

namespace ArcadeOS.Services;

public sealed class LibretroMetadataProvider(HttpClient client, string cacheDirectory) : IGameMetadataProvider
{
    private static readonly IReadOnlyDictionary<string, string> Platforms = MetadataPlatforms.Libretro;
    private static readonly string[] Fields = ["developer", "publisher", "genre", "releaseyear", "maxusers"];
    private sealed record PlatformDatabase(Dictionary<string, Dictionary<string, string>> Rows, Dictionary<string, string[]> Names);
    private readonly Dictionary<string, PlatformDatabase> loadedDatabases = new();
    public bool Supports(string systemId) => Platforms.ContainsKey(systemId);

    public async Task<CachedGameMetadata?> FindAsync(Game game, CancellationToken cancellationToken)
        => await FindCoreAsync(game, false, cancellationToken).ConfigureAwait(false);
    public Task<CachedGameMetadata?> FindRelaxedAsync(Game game, CancellationToken cancellationToken)
        => FindCoreAsync(game, true, cancellationToken);
    private async Task<CachedGameMetadata?> FindCoreAsync(Game game, bool relaxed, CancellationToken cancellationToken)
    {
        if (game.SystemId is null || game.RomPath is null || !Platforms.TryGetValue(game.SystemId, out var platform)) return null;
        if (!loadedDatabases.TryGetValue(platform, out var database))
        {
            if (loadedDatabases.Count >= 3) loadedDatabases.Clear();
            var rows = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string field in Fields)
            {
                string text = await GetDatabaseAsync(platform, field, cancellationToken).ConfigureAwait(false);
                ParseDat(text, rows);
            }
            // Some disc consoles only have a title catalog, rather than editorial metadata.
            // This still supplies exact game names for box-art lookup.
            if (rows.Count == 0)
                ParseDat(await GetDatabaseAsync(platform, "redump", cancellationToken).ConfigureAwait(false), rows);
            database = new(rows, rows.Keys.GroupBy(MetadataNames.Normalize).ToDictionary(group => group.Key, group => group.ToArray()));
            loadedDatabases[platform] = database;
        }
        string romName = Path.GetFileNameWithoutExtension(game.RomPath);
        var queries = romName.Split('~', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var matches = queries.SelectMany(query => database.Names.GetValueOrDefault(MetadataNames.Normalize(query)) ?? []).Distinct().ToArray();
        if (matches.Length == 0 && platform == "Sega - 32X" && File.Exists(game.RomPath)
            && Path.GetExtension(game.RomPath).Equals(".32x", StringComparison.OrdinalIgnoreCase)
            && new FileInfo(game.RomPath).Length <= 16 * 1024 * 1024)
        {
            string crc = await RomCrcAsync(game.RomPath, cancellationToken).ConfigureAwait(false);
            matches = database.Rows.Where(row => row.Value.GetValueOrDefault("crc")?.Equals(crc, StringComparison.OrdinalIgnoreCase) == true)
                .Select(row => row.Key).ToArray();
        }
        if (matches.Length == 0 && relaxed)
        {
            var keys = queries.Select(query => TitleMatcher.Find(query, database.Names.Keys)).OfType<string>().Distinct().Take(2).ToArray();
            string? key = keys.Length == 1 ? keys[0] : null;
            if (key is not null) matches = database.Names[key];
        }
        // Exact region wins. Untagged filenames prefer a retail US entry, then Europe.
        string? match = matches.OrderByDescending(name => name.Equals(romName, StringComparison.OrdinalIgnoreCase))
            .ThenBy(name => RegionRank(name, romName)).ThenBy(name => name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (match is null) return null;
        var data = database.Rows[match];
        string? Value(string key) => data.TryGetValue(key, out var value) ? value
            : matches.Select(name => database.Rows[name].GetValueOrDefault(key)).FirstOrDefault(value => value is not null);
        string title = MetadataNames.Clean(match);
        string? year = Value("releaseyear");
        string? genre = Value("genre");
        string? publisher = Value("publisher");
        string? players = Value("users");
        string? cover = null;
        string? coverUrl = null;
        foreach (string candidate in matches.OrderBy(name => name == match ? 0 : 1).ThenBy(name => RegionRank(name, romName)).Take(3))
        {
            string fileName = Regex.Replace(candidate, "[&*/:`<>?\\\\|\"]", "_") + ".png";
            string url = $"https://raw.githubusercontent.com/libretro-thumbnails/{platform.Replace(' ', '_')}/master/Named_Boxarts/{Uri.EscapeDataString(fileName)}";
            string destination = Path.Combine(cacheDirectory, "covers", AutomaticMetadataService.CacheKey(game) + ".png");
            if (File.Exists(destination) || await DownloadCoverAsync(url, destination, cancellationToken).ConfigureAwait(false))
            { cover = Path.GetRelativePath(cacheDirectory, destination); coverUrl = url; break; }
        }
        string description = $"{title} is a {genre ?? "video game"} title for {game.Platform}."
            + (publisher is not null ? $" Published by {publisher}." : "")
            + (year is not null ? $" Released in {year}." : "");
        return new CachedGameMetadata
        {
            SystemId = game.SystemId, RomName = romName, Title = title, Year = year,
            Genre = genre, Developer = Value("developer"), Publisher = publisher,
            Players = players is null ? null : players == "1" ? "1 player" : $"1–{players} players",
            Description = description, ArtworkFile = cover,
            SourceUrl = $"https://github.com/libretro/libretro-database/tree/master/metadat",
            ArtworkSourceUrl = coverUrl, ArtworkRights = "Game box art remains copyright of its respective owners; cached for the personal cabinet."
        };
    }
    private static int RegionRank(string name, string requested)
    {
        int variant = Regex.IsMatch(name, @"\((Sample|Beta|Proto|Demo)", RegexOptions.IgnoreCase) ? 100 : 0;
        if (requested.Contains("(Europe)", StringComparison.OrdinalIgnoreCase)) return variant + (name.Contains("(Europe)") ? 0 : 10);
        if (requested.Contains("(Japan)", StringComparison.OrdinalIgnoreCase)) return variant + (name.Contains("(Japan)") ? 0 : 10);
        return variant + (name.Contains("(USA)") ? 0 : name.Contains("(Europe)") ? 1 : 2);
    }
    private async Task<string> GetDatabaseAsync(string platform, string field, CancellationToken cancellationToken)
    {
        string path = Path.Combine(cacheDirectory, "databases", platform.Replace(' ', '_') + "-" + field + ".dat");
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Cached database exceeded its size limit: " + Path.GetFileName(path));
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        string url = $"https://raw.githubusercontent.com/libretro/libretro-database/master/metadat/{field}/{Uri.EscapeDataString(platform)}.dat";
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            await AutomaticMetadataService.WriteAtomicAsync(path, "", cancellationToken).ConfigureAwait(false);
            return "";
        }
        response.EnsureSuccessStatusCode();
        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (text.Length > 16 * 1024 * 1024) throw new InvalidDataException("Metadata database exceeded its size limit.");
        await AutomaticMetadataService.WriteAtomicAsync(path, text, cancellationToken).ConfigureAwait(false);
        return text;
    }
    public static void ParseDat(string text, Dictionary<string, Dictionary<string, string>> rows)
    {
        foreach (Match block in Regex.Matches(text, @"(?ms)^game\s*\(\s*(.*?)^\)"))
        {
            var fieldMatches = Regex.Matches(block.Groups[1].Value, "(?m)^\\s*(comment|name|developer|publisher|genre|releaseyear|users)\\s+(?:\"([^\"]*)\"|([0-9]+))")
                .Cast<Match>();
            var fields = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in fieldMatches)
            {
                string value = field.Groups[2].Success ? field.Groups[2].Value : field.Groups[3].Value;
                if (!fields.ContainsKey(field.Groups[1].Value) || value.Length > 0) fields[field.Groups[1].Value] = value;
            }
            var crc = Regex.Match(block.Groups[1].Value, @"rom\s*\(\s*crc\s+([A-Fa-f0-9]+)");
            if (crc.Success) fields["crc"] = crc.Groups[1].Value;
            string? name = fields.GetValueOrDefault("comment") ?? fields.GetValueOrDefault("name");
            if (name is null) continue;
            if (!rows.TryGetValue(name, out var row)) rows[name] = row = new(StringComparer.OrdinalIgnoreCase);
            foreach (var field in fields) row[field.Key] = field.Value;
        }
    }
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(number =>
    {
        uint value = (uint)number;
        for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320U ^ (value >> 1) : value >> 1;
        return value;
    }).ToArray();
    private static async Task<string> RomCrcAsync(string path, CancellationToken token)
    {
        uint crc = 0xffffffff;
        await using var stream = File.OpenRead(path);
        byte[] bytes = new byte[65536]; int count;
        while ((count = await stream.ReadAsync(bytes, token).ConfigureAwait(false)) > 0)
            for (int i = 0; i < count; i++) crc = CrcTable[(crc ^ bytes[i]) & 255] ^ (crc >> 8);
        return (crc ^ 0xffffffff).ToString("X8");
    }
    internal async Task<bool> DownloadCoverAsync(string url, string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var bytes = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > 8 * 1024 * 1024) throw new InvalidDataException("Cover exceeded its size limit.");
            buffer.Write(bytes, 0, count);
        }
        byte[] image = buffer.ToArray();
        if (image.Length < 8 || image[0] != 137 || image[1] != 80 || image[2] != 78 || image[3] != 71)
            throw new InvalidDataException("The provider returned an invalid PNG cover.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temp, image, cancellationToken).ConfigureAwait(false); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return true;
    }
}

