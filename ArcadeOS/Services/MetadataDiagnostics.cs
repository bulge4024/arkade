using System.Collections.Concurrent;
using System.IO;
namespace ArcadeOS.Services;
public static class MetadataDiagnostics
{
    private static readonly object Sync = new();
    private static readonly ConcurrentDictionary<string, DateTime> Recent = new();
    public static bool Recoverable(Exception ex) => ex is not OutOfMemoryException and not AccessViolationException and not StackOverflowException;
    public static void Record(string directory, Exception ex, string context)
    {
        string key = context + "|" + ex.GetType().Name + "|" + ex.Message;
        if (Recent.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(1)) return;
        if (Recent.Count > 1000) Recent.Clear(); Recent[key] = DateTime.UtcNow;
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(directory); string path = Path.Combine(directory, "metadata-errors.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Move(path, path + ".previous", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {context}\n{ex}\n\n");
            }
        }
        catch (Exception logError) when (Recoverable(logError)) { }
    }
}
