using System.IO.Compression;
using System.Net;
using System.Text;
using ArcadeOS.Models;
using ArcadeOS.Services;

static class BroadChecks
{
    static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j9XkAAAAASUVORK5CYII=");
    static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
    static Game Game(string name, string system = "switch") => new(name, "Console", "", "See game controls", "", "", "", "", "") { RomPath = name + ".rom", SystemId = system, SystemName = "Nintendo Switch" };
    static byte[] Fixture()
    {
        string xml = """
        <LaunchBox>
        <Game><Name>Example Adventure</Name><DatabaseID>1</DatabaseID><Platform>Nintendo Switch</Platform><ReleaseDate>2018-01-01</ReleaseDate><Overview>A complete description.</Overview><Genres>Adventure</Genres><Developer>Example Studio</Developer><MaxPlayers>2</MaxPlayers></Game>
        <Game><Name>Future Game</Name><DatabaseID>2</DatabaseID><Platform>Future Console</Platform><ReleaseDate>2001-01-01</ReleaseDate></Game>
        <Game><Name>Native Game</Name><DatabaseID>3</DatabaseID><Platform>Sega Saturn</Platform><ReleaseDate>1995-01-01</ReleaseDate><Genres>Racing</Genres><Overview>Detailed Saturn description.</Overview></Game>
        <GameAlternateName><DatabaseID>1</DatabaseID><AlternateName>Example Alias</AlternateName></GameAlternateName>
        <GameImage><DatabaseID>1</DatabaseID><FileName>00000000-0000-0000-0000-000000000001.png</FileName><Type>Screenshot - Gameplay</Type><Region>World</Region></GameImage>
        <GameImage><DatabaseID>1</DatabaseID><FileName>00000000-0000-0000-0000-000000000002.png</FileName><Type>Box - Front</Type><Region>North America</Region></GameImage>
        </LaunchBox>
        """;
        using var bytes = new MemoryStream(); using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true)) { using var writer = new StreamWriter(archive.CreateEntry("Metadata.xml").Open()); writer.Write(xml); } return bytes.ToArray();
    }
    static async Task Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "ArkadeBroadChecks-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            if (args.Length > 1)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var platforms = BroadMetadataProvider.BuildIndex(args[1], Path.Combine(root, "broad-index-v1"), default, Console.WriteLine);
                using var realClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) }; var live = new BroadMetadataProvider(realClient, root);
                var match = await live.FindAsync(Game("Super Mario Odyssey"), default);
                Assert(match?.ArtworkFile is not null && match.Description?.Length > 20, "Live Switch metadata/cover lookup failed");
                Console.WriteLine($"PASS LIVE: {platforms.Length} platforms indexed in {clock.Elapsed.TotalSeconds:F1}s; Switch metadata and cover matched; peak working set {System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1048576} MB. No ROMs were read or downloaded."); return;
            }
            var handler = new Handler(); using var client = new HttpClient(handler); var broad = new BroadMetadataProvider(client, root); int statusCount = 0; broad.StatusChanged += _ => statusCount++;
            var result = await broad.FindAsync(Game("Example Alias"), default);
            Assert(result?.Title == "Example Adventure" && result.Year == "2018" && result.ArtworkFile is not null && result.Description == "A complete description.", "Broad metadata, aliases or artwork failed");
            Assert(handler.LastImage?.Contains("000000000002") == true && statusCount > 0, "Artwork ranking or setup progress failed");
            Assert(File.Exists(Path.Combine(root, "broad-index-v1", "platforms.json")) && handler.CatalogRequests == 1, "Catalog was not cached");
            var future = Game("Future Game", "new-console") with { SystemName = "Future Console" }; Assert((await broad.FindAsync(future, default))?.Title == "Future Game", "New configured console was rejected");
            var again = new BroadMetadataProvider(client, root); await again.FindAsync(Game("Example Alias"), default); Assert(handler.CatalogRequests == 1, "Cached catalog downloaded again");
            File.WriteAllText(Path.Combine(root, "broad-index-v1", "platforms.json"), "{broken manifest}");
            var repaired = new BroadMetadataProvider(client, root); Assert((await repaired.FindAsync(Game("Example Alias"), default))?.ArtworkFile is not null && handler.CatalogRequests == 2, "Corrupt index manifest was not rebuilt");
            var aliasConsole = Game("Example Alias", "my-console") with { SystemName = "Custom cabinet switch", SystemAliases = "switch" };
            Assert((await repaired.FindAsync(aliasConsole, default))?.ArtworkFile is not null, "Configured platform alias was ignored");
            var makerConsole = Game("Example Alias", "new-switch-system") with { SystemName = "Switch", SystemManufacturer = "Nintendo" };
            Assert((await repaired.FindAsync(makerConsole, default))?.ArtworkFile is not null, "Configured manufacturer/name lookup failed");
            var router = new CabinetMetadataProvider(client, Path.Combine(root, "router"));
            Assert(router.Supports("switch") && router.Supports("gamate") && router.Supports("new-console"), "Console filter still excludes systems");
            Assert(MetadataPlatforms.Broad.Keys.All(router.Supports), "A registered console still has no lookup route");
            var routed = await router.FindAsync(Game("Example Alias"), default); Assert(routed?.ArtworkFile is not null, "Actual provider router did not fall back for Switch");
            var native = await router.FindAsync(Game("Native Game", "saturn") with { SystemName = "Sega Saturn" }, default);
            Assert(native?.Year == "1995" && native.Genre == "Racing" && native.ArtworkFile?.EndsWith(".png") == true, "Fallback did not fill missing metadata while preserving native artwork");
            var rows = new Dictionary<string, Dictionary<string, string>>();
            LibretroMetadataProvider.ParseDat("game (\n name \"Repeated\"\n name \"Repeated\"\n genre \"Action\"\n genre \"Adventure\"\n)\n", rows);
            Assert(rows.Count == 1 && rows["Repeated"]["genre"] == "Adventure", "Repeated DAT fields still throw");
            var badProvider = new BadProvider(); var service = new AutomaticMetadataService(Path.Combine(root, "bad"), badProvider);
            var badGames = new[] { Game("Argument"), Game("Null"), Game("InvalidState"), Game("Good") };
            await service.EnrichAsync(badGames, retryMissing: true);
            Assert(service.Progress.Failed == 3 && service.Progress.Matched == 1 && !service.Progress.IsRunning, "A provider exception stopped the whole batch");
            Assert(File.Exists(Path.Combine(root, "bad", "metadata-errors.log")), "Failure did not produce a diagnostic log");
            string badCache = Path.Combine(root, "bad", "entries", AutomaticMetadataService.CacheKey(Game("Good")) + ".json");
            var corrupt = new AutomaticMetadataEntry(new() { Title = "Good", ArtworkFile = "bad\0path.png" }, DateTimeOffset.UtcNow.AddHours(1), "Matched");
            await File.WriteAllTextAsync(badCache, System.Text.Json.JsonSerializer.Serialize(corrupt));
            var restarted = new AutomaticMetadataService(Path.Combine(root, "bad"), badProvider);
            Assert(restarted.ApplyCached(new([Game("Good")], [])).Games.Single().ArtworkPath is null, "Invalid cached path escaped artwork handling");
            _ = restarted.GetLibraryStatus([Game("Good")]); await restarted.EnrichAsync([Game("Good")], retryMissing: true);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { BroadMetadataProvider.BuildIndex(Path.Combine(root, "nonexistent.zip"), Path.Combine(root, "cancelled"), cancelled.Token); } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            Console.WriteLine("PASS: actual multi-source router, all-console routing, broad disk index, exact aliases, artwork ranking, cache reuse, duplicate DAT fields, provider exception isolation, invalid cache and error logs.");
        }
        finally { if (args.Length == 0) Directory.Delete(root, true); }
    }
    sealed class Handler : HttpMessageHandler
    {
        public int CatalogRequests; public string? LastImage;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (request.RequestUri!.AbsoluteUri == BroadMetadataProvider.CatalogUrl) { CatalogRequests++; response.Content = new ByteArrayContent(Fixture()); }
            else if (request.RequestUri.AbsoluteUri.Contains("/redump/Sega%20-%20Saturn")) response.Content = new StringContent("game (\n name \"Native Game (USA)\"\n)\n");
            else if (request.RequestUri.Host == "raw.githubusercontent.com" && request.RequestUri.AbsoluteUri.Contains("Named_Boxarts")) response.Content = new ByteArrayContent(Png);
            else if (request.RequestUri.Host == "images.launchbox-app.com") { LastImage = request.RequestUri.AbsoluteUri; response.Content = new ByteArrayContent(Png); }
            else { response.StatusCode = HttpStatusCode.NotFound; response.Content = new StringContent(""); }
            return Task.FromResult(response);
        }
    }
    sealed class BadProvider : IGameMetadataProvider
    {
        public bool Supports(string id) => true;
        public Task<CachedGameMetadata?> FindAsync(Game game, CancellationToken token) => game.Title switch
        {
            "Argument" => throw new ArgumentException("Duplicate metadata field"),
            "Null" => throw new NullReferenceException("Malformed source record"),
            "InvalidState" => throw new InvalidOperationException("Unexpected JSON value type"),
            _ => Task.FromResult<CachedGameMetadata?>(new() { Title = game.Title, Year = "2000" })
        };
    }
}
