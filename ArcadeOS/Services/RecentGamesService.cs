using System.IO;
using System.Text.Json;
using ArcadeOS.Models;
namespace ArcadeOS.Services;

public sealed class RecentGamesService
{
    private readonly string path;
    private readonly Dictionary<string, DateTimeOffset> played;
    public RecentGamesService(string? path = null)
    {
        this.path = path ?? Path.Combine(AutomaticMetadataService.DefaultCacheDirectory, "recently-played.json");
        try { played = File.Exists(this.path) ? new(JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(this.path)) ?? [], StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { played = new(StringComparer.OrdinalIgnoreCase); }
    }
    private static string Key(Game game) => game.SystemId + "|" + game.RomPath;
    public DateTimeOffset? LastPlayed(Game game) => played.TryGetValue(Key(game), out var time) ? time : null;
    public void Record(Game game, DateTimeOffset when)
    {
        played[Key(game)] = when;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(played));
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.TraceWarning($"Play history could not be saved: {ex.Message}"); }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
