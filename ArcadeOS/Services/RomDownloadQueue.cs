using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace ArcadeOS.Services;

public sealed record RomDownloadRequest(RemoteRom Rom, RomSystem System, bool Replace);
public sealed record RomQueueProgress(bool IsRunning = false, string Name = "", string System = "", int Completed = 0, int Total = 0,
    int Failed = 0, long Bytes = 0, long? Length = null, double Speed = 0, string Status = "", bool Cancelled = false)
{
    public double? Percent => Length is > 0 ? Math.Clamp(Bytes * 100d / Length.Value, 0, 100) : null;
    public TimeSpan? Remaining => Length is > 0 && Speed > 0 ? TimeSpan.FromSeconds(Math.Min(TimeSpan.MaxValue.TotalSeconds - 1, Math.Max(0, Length.Value - Bytes) / Speed)) : null;
}

// Owns queue lifetime independently of the browser screen and any running emulator.
public sealed class RomDownloadQueue : IDisposable
{
    public delegate Task Download(RomDownloadRequest request, IProgress<RomDownloadProgress> progress, CancellationToken token);
    private readonly Download download;
    private readonly object sync = new();
    private readonly Queue<RomDownloadRequest> queue = new();
    private readonly HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private bool disposed;
    private RomQueueProgress progress = new();
    private readonly List<string> errors = [];
    public RomQueueProgress Progress => Volatile.Read(ref progress);
    public string[] Errors { get { lock (sync) return errors.ToArray(); } }
    public bool IsRunning => Progress.IsRunning;
    public event Action<RomQueueProgress>? Changed;
    public event Action<RomDownloadRequest>? FileCompleted;
    public RomDownloadQueue(Download download) => this.download = download;
    private static string Key(RomDownloadRequest request) => Path.GetFullPath(request.System.Folder) + "|" + request.Rom.Name;
    public int Enqueue(IEnumerable<RomDownloadRequest> requests)
    {
        int added = 0;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (cancellation?.IsCancellationRequested == true) throw new InvalidOperationException("Downloads are still stopping. Please wait briefly before adding more.");
            foreach (var request in requests) if (keys.Add(Key(request))) { queue.Enqueue(request); added++; }
            if (added == 0) return 0;
            if (worker is null)
            {
                errors.Clear(); cancellation = new();
                Volatile.Write(ref progress, new(true, Total: added, Status: "Starting downloads…"));
                worker = Task.Run(() => RunAsync(cancellation.Token));
            }
            else Volatile.Write(ref progress, progress with { Total = progress.Total + added });
        }
        Publish(); return added;
    }
    public void Cancel()
    {
        lock (sync) { if (cancellation is null) return; Volatile.Write(ref progress, progress with { Status = "Stopping downloads…" }); cancellation.Cancel(); }
        Publish();
    }
    public void SetStatus(string status)
    {
        lock (sync) Volatile.Write(ref progress, progress with { Status = status });
        Publish();
    }
    public Task WhenIdleAsync() { lock (sync) return worker ?? Task.CompletedTask; }
    private void Publish() => Changed?.Invoke(Progress);
    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                RomDownloadRequest request;
                lock (sync)
                {
                    token.ThrowIfCancellationRequested();
                    if (queue.Count == 0) { Finish(false); return; }
                    request = queue.Dequeue();
                    Volatile.Write(ref progress, progress with { Name = request.Rom.Name, System = request.System.Name, Bytes = 0, Length = null, Speed = 0, Status = "Connecting…" });
                }
                Publish();
                var clock = Stopwatch.StartNew(); long lastBytes = 0; double lastTime = 0, speed = 0;
                var reporter = new ImmediateProgress(p =>
                {
                    double now = clock.Elapsed.TotalSeconds;
                    if (now - lastTime >= .15)
                    {
                        double instant = Math.Max(0, p.Bytes - lastBytes) / (now - lastTime);
                        speed = speed == 0 ? instant : speed * .7 + instant * .3; lastBytes = p.Bytes; lastTime = now;
                    }
                    lock (sync)
                        Volatile.Write(ref progress, progress with { Bytes = p.Bytes, Length = p.Total, Speed = speed, Status = p.Total is { } total && p.Bytes >= total ? "Verifying / installing…" : "Downloading" });
                    Publish();
                });
                try
                {
                    await download(request, reporter, token).ConfigureAwait(false);
                    lock (sync) Volatile.Write(ref progress, progress with { Completed = progress.Completed + 1 });
                    FileCompleted?.Invoke(request);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    lock (sync) { errors.Add(request.Rom.Name + ": " + ex.Message); Volatile.Write(ref progress, progress with { Failed = progress.Failed + 1, Status = ex.Message }); }
                }
                finally { lock (sync) keys.Remove(Key(request)); }
                Publish();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { lock (sync) Finish(true); }
        finally { Publish(); }
    }
    private void Finish(bool cancelled)
    {
        queue.Clear(); keys.Clear(); cancellation?.Dispose(); cancellation = null; worker = null;
        Volatile.Write(ref progress, progress with { IsRunning = false, Cancelled = cancelled, Speed = 0,
            Status = cancelled ? "Cancelled — finished games were kept." : progress.Failed > 0 ? $"Finished · {progress.Failed} failed. See Download Games for details." : "Downloads complete" });
    }
    public void Dispose() { lock (sync) { disposed = true; cancellation?.Cancel(); } }
    private sealed class ImmediateProgress(Action<RomDownloadProgress> report) : IProgress<RomDownloadProgress>
    { public void Report(RomDownloadProgress value) => report(value); }
}
