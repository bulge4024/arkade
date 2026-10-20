using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using ArcadeOS.Models;

namespace ArcadeOS.Services;

public sealed record AutomaticMetadataEntry(CachedGameMetadata? Metadata, DateTimeOffset RetryAfter, string Status, int MatcherVersion = 0);
public sealed record MetadataProgress(int Completed, int Total, bool IsRunning);

public sealed class AutomaticMetadataService
{
    private readonly string directory;
    private readonly IGameMetadataProvider provider;
    private readonly SemaphoreSlim worker = new(1, 1);
    private readonly ConcurrentDictionary<string, AutomaticMetadataEntry> entries = new();
    private readonly ConcurrentDictionary<string, byte> missing = new();
    private MetadataProgress progress = new(0, 0, false);
    public MetadataProgress Progress => Volatile.Read(ref progress);
    private static readonly HttpClient Client = CreateClient();
    public event Action? MetadataUpdated;
    public static string DefaultCacheDirectory => Environment.GetEnvironmentVariable("ARCADEOS_METADATA_CACHE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArcadeOS", "Metadata");
    public AutomaticMetadataService(string directory, IGameMetadataProvider? provider = null)
    { this.directory = directory; this.provider = provider ?? new CabinetMetadataProvider(Client, directory); }
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ArcadeOS/0.1");
        return client;
    }
    public static string CacheKey(Game game) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        game.SystemId + "|" + Path.GetFileNameWithoutExtension(game.RomPath ?? game.Title).ToLowerInvariant()))).ToLowerInvariant();
    private string EntryPath(Game game) => Path.Combine(directory, "entries", CacheKey(game) + ".json");
    private AutomaticMetadataEntry? ReadEntry(Game game)
    {
        string key = CacheKey(game);
        if (entries.TryGetValue(key, out var cached)) return cached;
        if (missing.ContainsKey(key)) return null;
        try
        {
            var entry = File.Exists(EntryPath(game)) ? JsonSerializer.Deserialize<AutomaticMetadataEntry>(File.ReadAllText(EntryPath(game))) : null;
            if (entry is not null) entries[key] = entry; else missing[key] = 0;
            return entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    private void Remember(Game game, AutomaticMetadataEntry entry)
    { string key = CacheKey(game); entries[key] = entry; missing.TryRemove(key, out _); }
    public LibraryScanResult ApplyCached(LibraryScanResult scan)
    {
        var games = scan.Games.Select(game =>
        {
            if (ReadEntry(game)?.Metadata is not { } data) return game;
            string? artwork = null;
            if (data.ArtworkFile is { Length: > 0 })
            {
                string path = Path.GetFullPath(data.ArtworkFile, directory);
                string relative = Path.GetRelativePath(directory, path);
                if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar)
                    && File.Exists(path)) artwork = path;
            }
            bool hasCustomTitle = game.Title != Path.GetFileNameWithoutExtension(game.RomPath);
            return game with
            {
                Title = hasCustomTitle ? game.Title : data.Title ?? game.Title,
                Year = string.IsNullOrWhiteSpace(game.Year) ? data.Year ?? "" : game.Year,
                Players = game.Players == "See game controls" ? data.Players ?? game.Players : game.Players,
                Genre = game.Genre ?? data.Genre, Developer = game.Developer ?? data.Developer,
                Publisher = game.Publisher ?? data.Publisher, ArtworkPath = game.ArtworkPath ?? artwork,
                Description = game.Description.StartsWith("Play ") && game.Description.EndsWith("using your existing RetroBat setup.")
                    ? data.Description ?? game.Description : game.Description,
                MetadataSource = game.MetadataSource ?? data.SourceUrl
            };
        }).ToArray();
        return new(games, scan.Warnings);
    }
    public async Task EnrichAsync(IReadOnlyList<Game> games, CancellationToken cancellationToken = default)
    {
        if (!await worker.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            var pending = games.Where(NeedsLookup).ToArray();
            Volatile.Write(ref progress, new(0, pending.Length, pending.Length > 0));
            int completed = 0;
            foreach (var game in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = ReadEntry(game);
                try
                {
                    var result = await provider.FindAsync(game, cancellationToken).ConfigureAwait(false);
                    if (result is null) result = await provider.FindRelaxedAsync(game, cancellationToken).ConfigureAwait(false);
                    var retry = DateTimeOffset.UtcNow + (result?.ArtworkFile is not null ? TimeSpan.FromDays(30) : TimeSpan.FromHours(24));
                    entry = new(result, retry, result is null ? "Unmatched" : "Matched", 5);
                    await WriteAtomicAsync(EntryPath(game), JsonSerializer.Serialize(entry), cancellationToken).ConfigureAwait(false);
                    Remember(game, entry);
                    if (result is not null) MetadataUpdated?.Invoke();
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException
                    || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    entry = new(entry?.Metadata, DateTimeOffset.UtcNow.AddMinutes(5), "Retry pending");
                    Remember(game, entry);
                    try { await WriteAtomicAsync(EntryPath(game), JsonSerializer.Serialize(entry), cancellationToken).ConfigureAwait(false); }
                    catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException) { }
                }
                finally { Volatile.Write(ref progress, new(++completed, pending.Length, true)); }
            }
        }
        finally { var last = Progress; Volatile.Write(ref progress, last with { IsRunning = false }); worker.Release(); }
    }
    private bool NeedsLookup(Game game)
    {
        if (game.SystemId is null || !provider.Supports(game.SystemId)) return false;
        if (game.MetadataSource is not null && game.ArtworkPath is not null && !string.IsNullOrEmpty(game.Year)
            && !string.IsNullOrEmpty(game.Genre)) return false;
        var entry = ReadEntry(game);
        bool pinballArtworkUpgrade = game.SystemId == "vpinball" && entry?.Metadata is { ArtworkFile: null } && entry.MatcherVersion < 3;
        bool coverMissing = entry?.Metadata?.ArtworkFile is { } cover && !File.Exists(Path.Combine(directory, cover));
        return entry is null || pinballArtworkUpgrade || (entry.Status == "Unmatched" && entry.MatcherVersion < 5) || entry.RetryAfter <= DateTimeOffset.UtcNow || coverMissing;
    }
    public static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, content, cancellationToken).ConfigureAwait(false); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
