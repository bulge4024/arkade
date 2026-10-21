using System.IO.Compression;
using System.Net;
using ArcadeOS.Services;

static class Checks
{
    static void Assert(bool value, string reason) { if (!value) throw new Exception(reason); }
    static RomDownloadRequest Request(string name, string system = "snes") => new(new(name + ".zip", new(RemoteRomService.Server, system + "/" + name + ".zip")), new(system, system, Path.Combine(Path.GetTempPath(), "dummy-rom-queue", system), [".zip"]), false);
    static byte[] Zip() { using var memory = new MemoryStream(); using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true)) { using var writer = new StreamWriter(archive.CreateEntry("dummy.cue").Open()); writer.Write("dummy game data"); } return memory.ToArray(); }
    static async Task Main()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<RomDownloadRequest>();
        using var queue = new RomDownloadQueue(async (request, progress, token) =>
        {
            seen.Add(request); progress.Report(new(request.Rom.Name, 100, 1000));
            await Task.Delay(200, token); progress.Report(new(request.Rom.Name, 500, 1000));
            if (request.Rom.Name == "First.zip") { started.TrySetResult(); await release.Task.WaitAsync(token); }
            if (request.Rom.Name == "Bad.zip") throw new InvalidDataException("Dummy failed transfer");
            progress.Report(new(request.Rom.Name, 1000, 1000));
        });
        Assert(queue.Enqueue([Request("First")]) == 1, "Queue did not start"); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(queue.Progress.IsRunning && queue.Progress.Percent == 50 && queue.Progress.Speed > 0 && queue.Progress.Remaining > TimeSpan.Zero, "Live percentage, speed or ETA missing");
        Assert(queue.Enqueue([Request("First"), Request("Second", "psx"), Request("Bad"), Request("Third")]) == 3, "Duplicate queued or cross-console additions lost");
        Assert(queue.Progress.Total == 4, "Added games missing from total"); release.SetResult(); await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert(!queue.IsRunning && queue.Progress.Completed == 3 && queue.Progress.Failed == 1 && queue.Errors.Length == 1, "Failure aborted queue or counts wrong");
        Assert(seen.Single(r => r.Rom.Name == "Second.zip").System.Id == "psx", "Navigating consoles changed a queued destination");
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var written = new List<string>();
        using var cancelling = new RomDownloadQueue(async (request, progress, token) =>
        {
            if (request.Rom.Name == "Slow.zip") { blocked.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            written.Add(request.Rom.Name);
        });
        cancelling.Enqueue([Request("Done"), Request("Slow"), Request("Never")]); await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelling.Cancel(); await cancelling.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert(cancelling.Progress.Cancelled && cancelling.Progress.Completed == 1 && written.SequenceEqual(["Done.zip"]), "Cancellation lost completed games or started queued games");
        cancelling.Enqueue([Request("Again")]); await cancelling.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert(cancelling.Progress.Completed == 1 && !cancelling.Progress.Cancelled, "Cannot start a fresh batch after cancellation");
        using var unknown = new RomDownloadQueue(async (request, progress, token) => { progress.Report(new(request.Rom.Name, 300, null)); await Task.Delay(200, token); Assert(unknownLength?.Progress.Percent is null, "Invented percentage for missing Content-Length"); });
        unknownLength = unknown; unknown.Enqueue([Request("Unknown")]); await unknown.WhenIdleAsync();
        string root = Path.Combine(Path.GetTempPath(), "ArkadeCommitChecks-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            using var downloader = new RemoteRomService(Path.Combine(root, "state.json"), new Handler(Zip()));
            var system = new RomSystem("psx", "PSX", root, [".cue"]); var rom = new RemoteRom("Dummy.zip", new(RemoteRomService.Server, "psx/Dummy.zip"));
            using var cancel = new CancellationTokenSource();
            try
            {
                await downloader.DownloadAsync(rom, system, false, null, cancel.Token, async token =>
                {
                    Assert(RemoteRomService.LocalFiles(system).Count == 0, "Incomplete extraction leaked into library");
                    cancel.Cancel(); await Task.Delay(1, token);
                });
                throw new Exception("Commit cancellation ignored");
            }
            catch (OperationCanceledException) { }
            Assert(!Directory.EnumerateFileSystemEntries(root, ".arkade-*").Any(), "Cancelled extraction left partial files");
            await downloader.DownloadAsync(rom, system, false, null, default);
            Assert(RemoteRomService.LocalFiles(system).Count > 0 && !File.GetAttributes(Path.Combine(root, "Dummy")).HasFlag(FileAttributes.Hidden), "Completed extracted game remained hidden");
            var archiveSystem = new RomSystem("snes", "SNES", Path.Combine(root, "snes"), [".zip"]); Directory.CreateDirectory(archiveSystem.Folder);
            string existing = Path.Combine(archiveSystem.Folder, "Existing.zip"); File.WriteAllText(existing, "original");
            var reachedCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var replacement = downloader.DownloadAsync(new("Existing.zip", new(RemoteRomService.Server, "snes/Existing.zip")), archiveSystem, true, null, default,
                async token => { reachedCommit.SetResult(); await allowCommit.Task.WaitAsync(token); });
            await reachedCommit.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert(File.ReadAllText(existing) == "original", "Replacement overwrote a game before commit permission");
            allowCommit.SetResult(); await replacement; Assert(File.ReadAllBytes(existing).SequenceEqual(Zip()), "Deferred replacement did not install");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("PASS: background queue, progress/speed/ETA, cross-console additions, duplicates, failure isolation, cancellation, restart and hidden staging / deferred commit.");
    }
    static RomDownloadQueue? unknownLength;
    sealed class Handler(byte[] data) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }); }
}
