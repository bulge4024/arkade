using ArcadeOS.Models;

namespace ArcadeOS.Services;

public interface IGameMetadataProvider
{
    Task<CachedGameMetadata?> FindRelaxedAsync(Game game, CancellationToken cancellationToken) => Task.FromResult<CachedGameMetadata?>(null);
    bool Supports(string systemId);
    Task<CachedGameMetadata?> FindAsync(Game game, CancellationToken cancellationToken);
}

public interface IMetadataStatusProvider { event Action<string>? StatusChanged; }
public sealed class MetadataUnavailableException(string message) : Exception(message);
