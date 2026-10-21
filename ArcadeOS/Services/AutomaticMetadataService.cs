using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using ArcadeOS.Models;

namespace ArcadeOS.Services;

public sealed record AutomaticMetadataEntry(CachedGameMetadata? Metadata, DateTimeOffset RetryAfter, string Status, int MatcherVersion = 0);
public sealed record MetadataProgress(int Completed, int Total, bool IsRunning, string CurrentGame = "", int Matched = 0,
    int Artwork = 0, int Unmatched = 0, int Failed = 0, string? LastError = null);
public sealed record MetadataLibraryStatus(int Total, int Covers, int Unsupported, int Waiting, int Deferred);

public sealed class AutomaticMetadataService
{
    private readonly string directory;
    private readonly IGameMetadataProvider provider;
    private readonly object queueLock = new();
    private Task? activeWorker;
    private IReadOnlyList<Game>? queuedGames;
    private bool queuedRetry;
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
    public Task EnrichAsync(IReadOnlyList<Game> games, CancellationToken cancellationToken = default, bool retryMissing = false)
    {
        lock (queueLock)
        {
            queuedGames = games; queuedRetry |= retryMissing;
            return activeWorker ??= Task.Run(() => DrainQueueAsync(cancellationToken), CancellationToken.None);
        }
    }
    private async Task DrainQueueAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                IReadOnlyList<Game> games; bool retry;
                lock (queueLock)
                {
                    if (queuedGames is null) { activeWorker = null; return; }
                    games = queuedGames; queuedGames = null; retry = queuedRetry; queuedRetry = false;
                }
                token.ThrowIfCancellationRequested();
                if (retry)
                    foreach (var game in games.Where(g => g.SystemId is not null && provider.Supports(g.SystemId)))
                    {
                        var entry = ReadEntry(game);
                        if (entry is not null && (entry.Metadata is null || !HasCover(game, entry)))
                        {
                            entry = entry with { RetryAfter = DateTimeOffset.MinValue }; Remember(game, entry);
                            await WriteAtomicAsync(EntryPath(game), JsonSerializer.Serialize(entry), token).ConfigureAwait(false);
                        }
                    }
                await EnrichBatchAsync(games, token).ConfigureAwait(false);
            }
        }
        catch
        {
            lock (queueLock) { activeWorker = null; queuedGames = null; queuedRetry = false; }
            throw;
        }
    }
    private bool HasCover(Game game, AutomaticMetadataEntry? entry) => File.Exists(game.ArtworkPath)
        || entry?.Metadata?.ArtworkFile is { } cover && File.Exists(Path.Combine(directory, cover));
    public MetadataLibraryStatus GetLibraryStatus(IReadOnlyList<Game> games)
    {
        int covers = 0, unsupported = 0, waiting = 0, deferred = 0;
        foreach (var game in games)
        {
            var entry = ReadEntry(game);
            if (HasCover(game, entry)) covers++;
            if (game.SystemId is null || !provider.Supports(game.SystemId)) { unsupported++; continue; }
            if (NeedsLookup(game)) waiting++;
            else if (!HasCover(game, entry) && entry?.RetryAfter > DateTimeOffset.UtcNow) deferred++;
        }
        return new(games.Count, covers, unsupported, waiting, deferred);
    }
    private async Task EnrichBatchAsync(IReadOnlyList<Game> games, CancellationToken cancellationToken)
    {
        var pending = games.Where(NeedsLookup).ToArray();
        // Keep the last useful batch result instead of erasing it on each 30-second folder scan.
        if (pending.Length == 0) return;
        Volatile.Write(ref progress, new(0, pending.Length, true));
        int completed = 0, matched = 0, artwork = 0, unmatched = 0, failed = 0; string? lastError = null;
        try
        {
            foreach (var game in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Volatile.Write(ref progress, new(completed, pending.Length, true, game.Platform + " / " + game.Title, matched, artwork, unmatched, failed, lastError));
                var entry = ReadEntry(game);
                try
                {
                    var result = await provider.FindAsync(game, cancellationToken).ConfigureAwait(false);
                    if (result is null) result = await provider.FindRelaxedAsync(game, cancellationToken).ConfigureAwait(false);
                    var retry = DateTimeOffset.UtcNow + (result?.ArtworkFile is not null ? TimeSpan.FromDays(30) : TimeSpan.FromHours(24));
                    entry = new(result ?? entry?.Metadata, retry, result is null ? "Unmatched" : "Matched", 5);
                    await WriteAtomicAsync(EntryPath(game), JsonSerializer.Serialize(entry), cancellationToken).ConfigureAwait(false);
                    Remember(game, entry);
                    if (result is null) unmatched++; else { matched++; if (HasCover(game, entry)) artwork++; MetadataUpdated?.Invoke(); }
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException
                    or System.Text.RegularExpressions.RegexMatchTimeoutException || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    failed++; lastError = ex is OperationCanceledException ? "Metadata server timed out; retrying in 5 minutes." : ex.Message;
                    entry = new(entry?.Metadata, DateTimeOffset.UtcNow.AddMinutes(5), "Retry pending: " + lastError, 5);
                    Remember(game, entry);
                    try { await WriteAtomicAsync(EntryPath(game), JsonSerializer.Serialize(entry), cancellationToken).ConfigureAwait(false); }
                    catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException) { }
                }
                finally { Volatile.Write(ref progress, new(++completed, pending.Length, true, game.Platform + " / " + game.Title, matched, artwork, unmatched, failed, lastError)); }
            }
        }
        finally { var last = Progress; Volatile.Write(ref progress, last with { IsRunning = false }); }
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
