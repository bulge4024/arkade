using System.Net;
using ArcadeOS.Models;
using ArcadeOS.Services;

internal static class Checks
{
    static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
    static Game Game(string title, string system = "snes") => new(title, "Console", "", "See game controls", "", "", "", "", "") { RomPath = title + ".zip", SystemId = system };
    public static async Task Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "ArkadeMetadataChecks-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            var provider = new FakeProvider(root); var service = new AutomaticMetadataService(root, provider);
            var a = Game("Alpha"); var b = Game("Beta");
            Task first = service.EnrichAsync([a]); await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(service.Progress.IsRunning && service.Progress.CurrentGame.Contains("Alpha"), "Current lookup is not visible");
            Task second = service.EnrichAsync([a, b]); Assert(ReferenceEquals(first, second), "Overlapping scans created extra workers");
            provider.Release.SetResult(); await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(provider.Seen.Contains("Beta"), "Games added during active lookup were discarded");
            var faulty = Game("Invalid"); var unknown = Game("Unmatched"); var timedout = Game("Timeout"); var unsupported = Game("Unsupported", "unsupported");
            await service.EnrichAsync([a, b, faulty, unknown, timedout, unsupported]);
            var progress = service.Progress;
            Assert(!progress.IsRunning && progress.Total == 3 && progress.Failed == 2 && progress.Unmatched == 1, "Per-game failure counts wrong");
            Assert(progress.LastError is not null, "Network failure invisible");
            var summary = service.GetLibraryStatus([a, b, faulty, unknown, timedout, unsupported]);
            Assert(summary.Covers == 2 && summary.Unsupported == 1 && summary.Deferred == 3, "Library status wrong");
            int calls = provider.Seen.Count; await service.EnrichAsync([a, b, faulty, unknown, timedout]); Assert(provider.Seen.Count == calls, "Refresh bypasses retry backoff");
            Assert(service.Progress.Failed == 2, "Idle scan erased diagnostic results");
            provider.Recovered = true;
            await service.EnrichAsync([a, b, faulty, unknown, timedout], retryMissing: true);
            Assert(provider.Seen.Count(name => name == "Alpha") == 1 && service.GetLibraryStatus([a, b, faulty, unknown, timedout]).Covers == 5, "Retry erased good artwork or skipped missing entries");
            var applied = service.ApplyCached(new([faulty], [])).Games.Single(); Assert(applied.ArtworkPath is not null, "Recovered artwork not applied");
            var provider2 = new FakeProvider(Path.Combine(root, "cancel")); var service2 = new AutomaticMetadataService(Path.Combine(root, "cancel"), provider2);
            using var cancel = new CancellationTokenSource(); Task cancelled = service2.EnrichAsync([a], cancel.Token); await provider2.Started.Task; cancel.Cancel();
            try { await cancelled; throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
            Assert(!service2.Progress.IsRunning, "Cancelled batch left progress running"); provider2.Release.TrySetResult();
            await service2.EnrichAsync([b]); Assert(provider2.Seen.Contains("Beta"), "Worker could not recover after cancellation");
            var handler = new DatHandler(); using var client = new HttpClient(handler);
            string consoleCache = Path.Combine(root, "provider"); var consoles = new LibretroMetadataProvider(client, consoleCache);
            foreach (var id in new[] { "dreamcast", "saturn", "segacd", "gamecube", "nds", "ps2", "psp", "atari2600", "jaguar", "3do" }) Assert(consoles.Supports(id), "Missing platform: " + id);
            var result = await consoles.FindAsync(Game("Example", "saturn"), default);
            Assert(result?.ArtworkFile is not null, "Title-only disc database did not produce artwork");
            int requests = handler.Requests; var nextProvider = new LibretroMetadataProvider(client, consoleCache);
            Assert((await nextProvider.FindAsync(Game("Example", "saturn"), default))?.ArtworkFile is not null && handler.Requests == requests, "Missing fields were repeatedly downloaded");
            Console.WriteLine("PASS: live progress, queued new games, per-game failures, cancellation, retry backoff, selective retry, retained artwork, new console coverage and disc title catalogs.");
        }
        finally { Directory.Delete(root, true); }
    }
    sealed class FakeProvider(string root) : IGameMetadataProvider
    {
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Seen = []; public bool Recovered;
        public bool Supports(string system) => system != "unsupported";
        public async Task<CachedGameMetadata?> FindAsync(Game game, CancellationToken token)
        {
            Seen.Add(game.Title);
            if (game.Title == "Alpha") { Started.TrySetResult(); await Release.Task.WaitAsync(token); }
            if (!Recovered)
            {
                if (game.Title == "Invalid") throw new InvalidDataException("Dummy malformed provider response");
                if (game.Title == "Timeout") throw new TaskCanceledException("Dummy timeout");
                if (game.Title == "Unmatched") return null;
            }
            Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, game.Title + ".png"), "dummy");
            return new() { Title = game.Title, ArtworkFile = game.Title + ".png", Genre = "Arcade", Year = "1991" };
        }
    }
    sealed class DatHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++; var url = request.RequestUri!.AbsoluteUri;
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (url.Contains("Named_Boxarts")) response.Content = new ByteArrayContent([137, 80, 78, 71, 13, 10, 26, 10]);
            else if (url.Contains("/redump/")) response.Content = new StringContent("game (\n name \"Example (USA)\"\n)\n");
            else { response.StatusCode = HttpStatusCode.NotFound; response.Content = new StringContent(""); }
            return Task.FromResult(response);
        }
    }
}

namespace ArcadeOS.Services
{
    // Default-provider stub keeps these unit tests isolated from SkiaSharp and external services.
    public sealed class CabinetMetadataProvider(HttpClient client, string root) : IGameMetadataProvider
    {
        readonly LibretroMetadataProvider provider = new(client, root);
        public bool Supports(string id) => provider.Supports(id);
        public Task<CachedGameMetadata?> FindAsync(Game game, CancellationToken token) => provider.FindAsync(game, token);
    }
}
