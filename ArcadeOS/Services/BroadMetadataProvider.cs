using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using ArcadeOS.Models;

namespace ArcadeOS.Services;

// Public community database, streamed to a disk index rather than loading its 500 MB XML into memory.
public sealed class BroadMetadataProvider(HttpClient client, string directory) : IGameMetadataProvider, IMetadataStatusProvider
{
    public const string CatalogUrl = "https://gamesdb.launchbox-app.com/Metadata.zip";
    public event Action<string>? StatusChanged;
    public sealed record Row(int Id, string Name, string Platform, string? Year, string? Overview, string? Genre, string? Developer, string? Publisher, string? Players);
    public sealed record Alias(int Id, string Name);
    public sealed record Cover(int Id, string File, int Rank);
    private sealed record PlatformData(Dictionary<int, Row> Rows, Dictionary<string, int[]> Names, Dictionary<int, Cover> Covers);
    private readonly Dictionary<string, PlatformData> loaded = new(StringComparer.OrdinalIgnoreCase);
    private string[]? platformNames;
    private DateTime retryAfter, preparedUntil;
    private string? preparationError;
    private string Index => Path.Combine(directory, "broad-index-v1");
    public bool Supports(string systemId) => !string.IsNullOrWhiteSpace(systemId);
    private void Status(string message) => StatusChanged?.Invoke(message);
    public Task<CachedGameMetadata?> FindAsync(Game game, CancellationToken token) => FindCoreAsync(game, false, token);
    public Task<CachedGameMetadata?> FindRelaxedAsync(Game game, CancellationToken token) => FindCoreAsync(game, true, token);
    public static string PlatformKey(string platform) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(platform))).ToLowerInvariant();
    private async Task EnsureIndexAsync(CancellationToken token)
    {
        if (platformNames is not null && DateTime.UtcNow < preparedUntil) return;
        string manifest = Path.Combine(Index, "platforms.json");
        if (File.Exists(manifest) && DateTime.UtcNow - File.GetLastWriteTimeUtc(manifest) < TimeSpan.FromDays(7))
        {
            try { platformNames = ReadManifest(manifest); preparedUntil = DateTime.UtcNow.AddHours(24); return; }
            catch (Exception ex) when (MetadataDiagnostics.Recoverable(ex) && !token.IsCancellationRequested)
            { MetadataDiagnostics.Record(directory, ex, "Invalid broad catalog manifest; rebuilding"); }
        }
        if (DateTime.UtcNow < retryAfter) throw new IOException(preparationError ?? "Broad catalog retry is pending.");
        Directory.CreateDirectory(directory);
        string zipPath = Path.Combine(directory, ".broad-catalog-" + Guid.NewGuid().ToString("N") + ".zip");
        string staging = Index + ".stage-" + Guid.NewGuid().ToString("N");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(15));
            Status("Preparing all-console catalog: connecting…");
            using var response = await client.GetAsync(CatalogUrl, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false); response.EnsureSuccessStatusCode();
            using (var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false))
            using (var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                byte[] buffer = new byte[131072]; long bytes = 0, last = 0; int count;
                while ((count = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
                {
                    bytes += count; if (bytes > 256L * 1024 * 1024) throw new InvalidDataException("Broad catalog exceeded its download limit.");
                    await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                    if (Environment.TickCount64 - last > 500) { Status($"Preparing all-console catalog: {bytes / 1048576d:F0} MB" + (response.Content.Headers.ContentLength is { } size ? $" / {size / 1048576d:F0} MB" : "")); last = Environment.TickCount64; }
                }
            }
            platformNames = BuildIndex(zipPath, staging, deadline.Token, Status);
            string backup = Index + ".old-" + Guid.NewGuid().ToString("N");
            if (Directory.Exists(Index)) Directory.Move(Index, backup);
            try { Directory.Move(staging, Index); }
            catch { if (Directory.Exists(backup)) Directory.Move(backup, Index); throw; }
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            loaded.Clear(); preparedUntil = DateTime.UtcNow.AddHours(24);
            Status("All-console catalog ready");
        }
        catch (Exception ex) when (MetadataDiagnostics.Recoverable(ex) && !token.IsCancellationRequested)
        {
            platformNames = null; preparationError = "Broad catalog unavailable: " + ex.Message; retryAfter = DateTime.UtcNow.AddMinutes(5);
            if (!File.Exists(manifest)) throw;
            MetadataDiagnostics.Record(directory, ex, "Broad catalog refresh; using previous index");
            platformNames = ReadManifest(manifest); preparedUntil = DateTime.UtcNow.AddHours(1);
        }
        finally
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }
    private static string[] ReadManifest(string path)
    {
        if (new FileInfo(path).Length > 256 * 1024) throw new InvalidDataException("Catalog manifest is too large.");
        var names = (JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return names.Length > 0 ? names : throw new InvalidDataException("Catalog manifest contains no platforms.");
    }
    public static string[] BuildIndex(string zipPath, string destination, CancellationToken token, Action<string>? status = null)
    {
        Directory.CreateDirectory(destination);
        var writers = new Dictionary<string, StreamWriter>(); var platforms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new Dictionary<int, string>(); var covers = new Dictionary<int, Cover>();
        void Write(string platform, string kind, object value)
        {
            string path = Path.Combine(destination, PlatformKey(platform) + "-" + kind + ".jsonl");
            if (!writers.TryGetValue(path, out var writer)) writers[path] = writer = new StreamWriter(path, false, Encoding.UTF8, 16384);
            writer.WriteLine(JsonSerializer.Serialize(value, value.GetType()));
        }
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.GetEntry("Metadata.xml") ?? throw new InvalidDataException("Broad catalog has no Metadata.xml.");
            if (entry.Length > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Broad catalog XML exceeded its size limit.");
            using var input = entry.Open();
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2L * 1024 * 1024 * 1024, IgnoreWhitespace = true });
            long records = 0, last = 0;
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1 || reader.Name is not ("Game" or "GameAlternateName" or "GameImage")) continue;
                string kind = reader.Name;
                using var subtree = reader.ReadSubtree(); var row = XElement.Load(subtree);
                string? Text(string field) => (string?)row.Element(field);
                if (!int.TryParse(Text("DatabaseID"), out int id)) continue;
                if (kind == "Game" && Text("Name") is { Length: > 0 } name && Text("Platform") is { Length: > 0 } platform)
                {
                    ids[id] = platform; platforms.Add(platform);
                    string? year = DateTime.TryParse(Text("ReleaseDate"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date.Year.ToString() : null;
                    Write(platform, "games", new Row(id, name, platform, year, Text("Overview"), Text("Genres"), Text("Developer"), Text("Publisher"), Text("MaxPlayers")));
                }
                else if (kind == "GameAlternateName" && ids.TryGetValue(id, out var aliasPlatform) && Text("AlternateName") is { Length: > 0 } alias)
                    Write(aliasPlatform, "aliases", new Alias(id, alias));
                else if (kind == "GameImage" && ids.ContainsKey(id) && Text("FileName") is { } file && Guid.TryParse(Path.GetFileNameWithoutExtension(file), out _))
                {
                    int type = Text("Type") switch { "Box - Front" => 0, "Box - 3D" => 20, "Screenshot - Gameplay" => 40, "Screenshot - Game Title" => 60, _ => 100 };
                    if (type == 100) continue;
                    int rank = type + (Text("Region") switch { "North America" or "United States" => 0, "Europe" => 1, "World" => 2, _ => 5 });
                    if (!covers.TryGetValue(id, out var previous) || rank < previous.Rank) covers[id] = new(id, file, rank);
                }
                if (++records % 2000 == 0 && Environment.TickCount64 - last > 500) { status?.Invoke($"Indexing all-console catalog: {records:N0} records…"); last = Environment.TickCount64; }
            }
            foreach (var cover in covers.Values) { token.ThrowIfCancellationRequested(); Write(ids[cover.Id], "covers", cover); }
        }
        finally { foreach (var writer in writers.Values) writer.Dispose(); }
        if (platforms.Count == 0) throw new InvalidDataException("The broad catalog contains no games.");
        var result = platforms.Order().ToArray(); File.WriteAllText(Path.Combine(destination, "platforms.json"), JsonSerializer.Serialize(result)); return result;
    }
    private IEnumerable<T> ReadRows<T>(string platform, string kind)
    {
        string path = Path.Combine(Index, PlatformKey(platform) + "-" + kind + ".jsonl");
        if (!File.Exists(path)) yield break;
        foreach (string line in File.ReadLines(path)) if (JsonSerializer.Deserialize<T>(line) is { } row) yield return row;
    }
    private async Task<CachedGameMetadata?> FindCoreAsync(Game game, bool relaxed, CancellationToken token)
    {
        if (game.SystemId is null) return null;
        await EnsureIndexAsync(token).ConfigureAwait(false);
        var availablePlatforms = platformNames ?? throw new InvalidDataException("Broad catalog was not initialized.");
        string requestedPlatform = MetadataPlatforms.BroadName(game.SystemId, game.SystemName ?? game.Tagline);
        string? platform = availablePlatforms.FirstOrDefault(p => MetadataNames.Normalize(p) == MetadataNames.Normalize(requestedPlatform));
        if (platform is null)
        {
            var aliases = (game.SystemAliases ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(alias => MetadataPlatforms.BroadName(alias, null)).Append((game.SystemManufacturer ?? "") + " " + (game.SystemName ?? game.Tagline))
                .Select(MetadataNames.Normalize).Where(name => name.Length > 0).ToHashSet();
            var candidates = availablePlatforms.Where(p => aliases.Contains(MetadataNames.Normalize(p))).ToArray();
            if (candidates.Length == 1) platform = candidates[0];
        }
        if (platform is null) throw new MetadataUnavailableException("No public catalog currently lists console: " + requestedPlatform + ". Local RetroBat metadata is still used.");
        if (!loaded.TryGetValue(platform, out var data))
        {
            if (loaded.Count >= 3) loaded.Clear();
            var rows = ReadRows<Row>(platform, "games").GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.Last());
            var names = rows.Values.Select(r => (r.Id, r.Name)).Concat(ReadRows<Alias>(platform, "aliases").Select(a => (a.Id, a.Name)))
                .GroupBy(r => MetadataNames.Normalize(r.Name)).ToDictionary(g => g.Key, g => g.Select(r => r.Id).Distinct().ToArray());
            var art = ReadRows<Cover>(platform, "covers").GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.OrderBy(c => c.Rank).First());
            data = new(rows, names, art); loaded[platform] = data;
        }
        string title = Path.GetFileNameWithoutExtension(game.RomPath ?? game.Title);
        string key = MetadataNames.Normalize(title);
        if (!data.Names.ContainsKey(key) && data.Names.ContainsKey(MetadataNames.Normalize(game.Title))) key = MetadataNames.Normalize(game.Title);
        if (!data.Names.ContainsKey(key) && relaxed) key = TitleMatcher.Find(title, data.Names.Keys) ?? "";
        if (!data.Names.TryGetValue(key, out var matches) || matches.Length != 1 || !data.Rows.TryGetValue(matches[0], out var matched)) return null;
        string? artFile = null, artUrl = null;
        if (data.Covers.TryGetValue(matched.Id, out var cover))
        {
            artUrl = "https://images.launchbox-app.com/" + Uri.EscapeDataString(cover.File);
            string destination = Path.Combine(directory, "covers", AutomaticMetadataService.CacheKey(game) + "-broad.jpg");
            try
            {
                if (File.Exists(destination) || await DownloadArtAsync(artUrl, destination, token).ConfigureAwait(false)) artFile = Path.GetRelativePath(directory, destination);
            }
            catch (Exception ex) when (MetadataDiagnostics.Recoverable(ex) && !token.IsCancellationRequested) { MetadataDiagnostics.Record(directory, ex, "Broad artwork: " + game.Title); }
        }
        return new() { SystemId = game.SystemId, RomName = title, Title = matched.Name, Year = matched.Year, Genre = matched.Genre,
            Description = matched.Overview, Developer = matched.Developer, Publisher = matched.Publisher, Players = matched.Players,
            ArtworkFile = artFile, ArtworkSourceUrl = artUrl, SourceUrl = "https://gamesdb.launchbox-app.com/games/details/" + matched.Id,
            ArtworkRights = "Community-sourced artwork remains copyright of its owners; cached for this cabinet." };
    }
    private async Task<bool> DownloadArtAsync(string url, string destination, CancellationToken token)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return false; response.EnsureSuccessStatusCode();
        using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false); using var memory = new MemoryStream();
        byte[] buffer = new byte[32768]; int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        { if (memory.Length + count > 8 * 1024 * 1024) throw new InvalidDataException("Broad artwork is too large."); memory.Write(buffer, 0, count); }
        byte[] bytes = memory.ToArray();
        if (bytes.Length < 32 || !(bytes[0] == 255 && bytes[1] == 216 || bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71)) throw new InvalidDataException("Invalid artwork response.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!); string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temp, bytes, token).ConfigureAwait(false); File.Move(temp, destination, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return true;
    }
}

