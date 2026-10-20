using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArcadeOS.Models;
using SkiaSharp;
namespace ArcadeOS.Services;

public sealed class CabinetMetadataProvider(HttpClient client, string directory) : IGameMetadataProvider
{
    private readonly LibretroMetadataProvider consoles = new(client, directory);
    private readonly PinballMetadataProvider pinball = new(client, directory);
    private readonly MameMetadataProvider arcade = new(client, directory);
    private IGameMetadataProvider For(Game game) => game.SystemId == "vpinball" ? pinball : game.SystemId == "mame" ? arcade : consoles;
    public bool Supports(string systemId) => systemId == "vpinball" || arcade.Supports(systemId) || consoles.Supports(systemId);
    public Task<CachedGameMetadata?> FindAsync(Game game, CancellationToken token) => For(game).FindAsync(game, token);
    public Task<CachedGameMetadata?> FindRelaxedAsync(Game game, CancellationToken token) => For(game).FindRelaxedAsync(game, token);
}

public sealed class PinballMetadataProvider(HttpClient client, string directory) : IGameMetadataProvider
{
    private JsonDocument? database;
    public bool Supports(string systemId) => systemId == "vpinball";
    public Task<CachedGameMetadata?> FindAsync(Game game, CancellationToken token) => FindCoreAsync(game, false, token);
    public Task<CachedGameMetadata?> FindRelaxedAsync(Game game, CancellationToken token) => FindCoreAsync(game, true, token);
    private async Task<CachedGameMetadata?> FindCoreAsync(Game game, bool relaxed, CancellationToken token)
    {
        if (!Supports(game.SystemId ?? "") || game.RomPath is null) return null;
        if (database is null)
        {
            string path = Path.Combine(directory, "databases", "vpsdb.json");
            string json;
            if (File.Exists(path)) json = await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
            else
            {
                json = await client.GetStringAsync("https://raw.githubusercontent.com/VirtualPinballSpreadsheet/vps-db/main/db/vpsdb.json", token).ConfigureAwait(false);
                if (json.Length > 32 * 1024 * 1024) throw new InvalidDataException("Pinball database exceeded its size limit.");
                await AutomaticMetadataService.WriteAtomicAsync(path, json, token).ConfigureAwait(false);
            }
            database = JsonDocument.Parse(json);
        }
        string rom = Path.GetFileNameWithoutExtension(game.RomPath);
        var clue = Regex.Match(rom, @"\((?<maker>[^()]*?)\s+(?<year>(?:19|20)\d{2})\)");
        // Table authors and revision numbers follow the manufacturer/year block.
        string title = clue.Success ? rom[..clue.Index].Trim() : MetadataNames.Clean(rom);
        string? Text(JsonElement row, string field) => row.TryGetProperty(field, out var value) ? value.ToString() : null;
        var rows = database.RootElement.EnumerateArray().Where(row =>
            (!clue.Success || Text(row, "year") == clue.Groups["year"].Value)
            && (!clue.Success || MetadataNames.Normalize(Text(row, "manufacturer") ?? "") == MetadataNames.Normalize(clue.Groups["maker"].Value)))
            .ToArray();
        string[] names = rows.Select(row => Text(row, "name") ?? "").Distinct().ToArray();
        string? match = names.FirstOrDefault(name => MetadataNames.Normalize(name) == MetadataNames.Normalize(title));
        if (match is null && relaxed) match = TitleMatcher.Find(title, names, allowSubtitle: clue.Success);
        var matches = rows.Where(row => Text(row, "name") == match).ToArray();
        if (match is null || matches.Length != 1) return null;
        var table = matches[0];
        string? maker = Text(table, "manufacturer"), year = Text(table, "year"), players = Text(table, "players");
        string? artwork = null, artworkUrl = null;
        string destination = Path.Combine(directory, "covers", AutomaticMetadataService.CacheKey(game) + "-pinball.png");
        if (table.TryGetProperty("tableFiles", out var files))
        {
            var images = files.EnumerateArray().Where(file => Text(file, "tableFormat") == "VPX")
                .OrderByDescending(file => file.TryGetProperty("authors", out var authors)
                    ? authors.EnumerateArray().Count(author => rom.Contains(author.ToString(), StringComparison.OrdinalIgnoreCase)) : 0)
                .ThenByDescending(file => Text(file, "version") is { Length: > 0 } version && rom.Contains(version, StringComparison.OrdinalIgnoreCase))
                .Select(file => Text(file, "imgUrl"));
            foreach (string url in images.Append(Text(table, "imgUrl")).OfType<string>().Where(url => url.Length > 0).Distinct().Take(4))
            {
                try
                {
                    if (!File.Exists(destination) && !await DownloadArtworkAsync(url, destination, token).ConfigureAwait(false)) continue;
                    artwork = Path.GetRelativePath(directory, destination); artworkUrl = url; break;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException || ex is OperationCanceledException && !token.IsCancellationRequested)
                { /* Keep table metadata even if an image host is temporarily unavailable. */ }
            }
        }
        return new CachedGameMetadata
        {
            SystemId = game.SystemId!, RomName = rom, Title = match, Year = year, Genre = "Pinball", Publisher = maker,
            Players = players is null ? null : players == "1" ? "1 player" : $"1–{players} players",
            Description = $"{match} is a pinball table by {maker}, released in {year}.",
            ArtworkFile = artwork, ArtworkSourceUrl = artworkUrl,
            ArtworkRights = "Table screenshot remains copyright of its respective creators; cached for the personal cabinet.",
            SourceUrl = "https://virtualpinballspreadsheet.github.io/?game=" + Uri.EscapeDataString(Text(table, "id") ?? "")
        };
    }
    private async Task<bool> DownloadArtworkAsync(string url, string destination, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Host != "virtualpinballspreadsheet.github.io") return false;
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return false;
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] bytes = new byte[16384]; int count;
        while ((count = await stream.ReadAsync(bytes, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > 8 * 1024 * 1024) throw new InvalidDataException("Pinball artwork exceeded its size limit.");
            buffer.Write(bytes, 0, count);
        }
        using var data = SKData.CreateCopy(buffer.ToArray());
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0
            || (long)codec.Info.Width * codec.Info.Height > 20_000_000) return false;
        using var bitmap = SKBitmap.Decode(codec);
        if (bitmap is null) return false;
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        if (png is null) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, png.ToArray(), token).ConfigureAwait(false);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return true;
    }

}
