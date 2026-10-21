using System.IO.Compression;
using System.Net;
using System.Text;
using ArcadeOS.Services;

static class Checks
{
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static byte[] Zip(params (string Name, string Text)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true)) foreach (var f in files) { using var writer = new StreamWriter(zip.CreateEntry(f.Name).Open()); writer.Write(f.Text); }
        return buffer.ToArray();
    }
    public static async Task Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "ArkadeRomChecks-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            var server = RemoteRomService.Server;
            var links = RemoteRomService.ParseIndex(server, "<a href='snes/'>SNES</a><a href='../'>parent</a><a href='/outside/'>out</a><a href='http://evil.example/snes/'>evil</a><a href='bad%2fchild/'>bad</a><a href='CON/'>reserved</a><a href='snes/'>duplicate</a>", true);
            Assert(links.Count == 1 && links[0].Name == "snes", "Unsafe directory link accepted");
            var files = RemoteRomService.ParseIndex(new(server, "snes/"), "<a href='Game%20%26%20One.zip'>game</a><a href='run.exe'>exe</a><a href='../Game.zip'>parent</a><a href='sub/Game.zip'>nested</a><a href='evil%5cGame.zip'>slash</a><a href='x.zip?download=1'>query</a>", false);
            Assert(files.Count == 1 && files[0].Name == "Game & One.zip", "Unsafe file accepted");
            Assert(!RemoteRomService.SafeName("NUL.zip") && !RemoteRomService.SafeName("game:ads.zip") && !RemoteRomService.SafeName(".."), "Invalid Windows path accepted");
            var settings = new RetroBatSettings { InstallationPath = root };
            string config = Path.Combine(root, "emulationstation", ".emulationstation"); Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "es_systems.cfg"), "<systemList><system><name>snes</name><fullname>Super Nintendo</fullname><path>~\\..\\roms\\snes</path><extension>.zip .sfc</extension><command>emulatorLauncher.exe</command></system></systemList>");
            var system = RemoteRomService.Systems(settings).Single();
            Assert(system.Folder == Path.Combine(root, "roms", "snes"), "RetroBat configured path mismatch");
            Assert(RemoteRomService.Systems(settings with { RomsRoot = Path.Combine(root, "custom") }).Single().Folder == Path.Combine(root, "custom", "snes"), "Custom ROM root ignored");
            Assert(RemoteRomService.Suggest("Nintendo - Super Nintendo Entertainment System", [system]) == system, "SNES alias missing");
            Assert(RemoteRomService.Suggest("Arcade - MAME Legacy", [system]) is null, "Ambiguous folder guessed");
            Assert(RemoteRomService.TitleKey("Batman (USA) [!].sfc") == RemoteRomService.TitleKey("Batman.zip"), "Region variant duplicate missing");
            var handler = new FakeHandler();
            using var service = new RemoteRomService(Path.Combine(root, "state", "catalog.json"), handler);
            var game = new RemoteRom("Dummy.zip", new(server, "snes/Dummy.zip"));
            handler.Data = Zip(("Dummy.sfc", "dummy data"));
            await service.DownloadAsync(game, system, false, null, default);
            string target = Path.Combine(system.Folder, game.Name); Assert(File.Exists(target), "Download missing");
            byte[] original = File.ReadAllBytes(target);
            await Reject(() => service.DownloadAsync(game, system, false, null, default), "Duplicate overwritten without consent");
            handler.Data = Encoding.UTF8.GetBytes("<html>Error</html>");
            await Reject(() => service.DownloadAsync(game, system, true, null, default), "Invalid ZIP accepted");
            Assert(original.SequenceEqual(File.ReadAllBytes(target)), "Failed replacement damaged original");
            handler.Data = Zip(("Dummy.sfc", "updated"));
            await service.DownloadAsync(game, system, true, null, default);
            Assert(!original.SequenceEqual(File.ReadAllBytes(target)), "Explicit replacement failed");
            File.WriteAllText(Path.Combine(system.Folder, "Variant (USA).sfc"), "old variant");
            await service.DownloadAsync(new("Variant.zip", new(server, "snes/Variant.zip")), system, true, null, default);
            Assert(File.Exists(Path.Combine(system.Folder, "Variant.zip")) && !File.Exists(Path.Combine(system.Folder, "Variant (USA).sfc")), "Replacement left duplicate region/file format installed");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Reject(() => service.DownloadAsync(new("Cancelled.zip", new(server, "snes/Cancelled.zip")), system, false, null, cancelled.Token), "Cancelled download completed");
            Assert(!Directory.EnumerateFiles(system.Folder, ".arkade-*.part").Any(), "Partial download leaked");
            handler.Short = true;
            await Reject(() => service.DownloadAsync(new("Short.zip", new(server, "snes/Short.zip")), system, false, null, default), "Incomplete response accepted"); handler.Short = false;
            var disc = new RomSystem("psx", "PlayStation", Path.Combine(root, "roms", "psx"), [".cue", ".bin", ".chd"]);
            var discRom = new RemoteRom("Disc.zip", new(server, "psx/Disc.zip"));
            handler.Data = Zip(("Disc.cue", "FILE Disc.bin BINARY"), ("Disc.bin", "disc data"));
            await service.DownloadAsync(discRom, disc, false, null, default);
            Assert(File.Exists(Path.Combine(disc.Folder, "Disc", "Disc.cue")), "Disc archive not extracted");
            handler.Data = Zip(("Disc.cue", "new cue"), ("Disc.bin", "new data"));
            await service.DownloadAsync(discRom, disc, true, null, default);
            Assert(File.ReadAllText(Path.Combine(disc.Folder, "Disc", "Disc.bin")) == "new data", "Extracted replacement failed");
            Assert(RemoteRomService.LocalFiles(disc).Count == 1, "Backup leaked into library scan");
            handler.Data = Zip(("../escape.cue", "bad"));
            await Reject(() => service.DownloadAsync(new("Bad.zip", new(server, "psx/Bad.zip")), disc, false, null, default), "ZIP traversal accepted");
            Assert(!File.Exists(Path.Combine(disc.Folder, "escape.cue")), "Archive escaped staging");
            handler.Data = Zip(("Disc.exe", "no"));
            await Reject(() => service.DownloadAsync(new("Bad.zip", new(server, "psx/Bad.zip")), disc, false, null, default), "Executable extracted");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Assert(service.IsDue(now), "Initial check not due");
            service.State.LastCheck = now; Assert(!service.IsDue(now.AddHours(35)) && service.IsDue(now.AddHours(36)), "36 hour schedule wrong");
            service.State.LastAttempt = now.AddHours(36); Assert(!service.IsDue(now.AddHours(36.5)), "Failure retry has no backoff");
            service.Observe("snes", [game], false); Assert(service.State.Pending.Count == 0, "Initial catalog prompted for all titles");
            var newGame = new RemoteRom("New.zip", new(server, "snes/New.zip"));
            service.Observe("snes", [game, newGame], false); Assert(service.State.Pending["snes"].SequenceEqual(["New.zip"]), "New title not detected");
            service.State.Mappings["snes"] = "snes"; service.Save();
            using (var reloaded = new RemoteRomService(Path.Combine(root, "state", "catalog.json"), new FakeHandler())) Assert(reloaded.State.Mappings["snes"] == "snes" && reloaded.State.Pending["snes"].Length == 1, "Settings not persisted");
            service.Observe("snes", [game, newGame], true); Assert(!service.State.Pending.ContainsKey("snes"), "Reviewed notification repeated");
            handler.Index = true; handler.NewTitle = false; service.State.Catalogs.Clear(); service.State.Pending.Clear(); service.State.LastCheck = null;
            await service.CheckAsync(default, [system]); handler.NewTitle = true;
            await service.CheckAsync(default, [system]); Assert(service.State.Pending["snes"].SequenceEqual(["New.zip"]), "Remote polling did not detect delta");
            Console.WriteLine("PASS: directory safety, console mapping, duplicates, replacement, cancellation, archive extraction, rollback, persistence and 36-hour polling.");
        }
        finally { Directory.Delete(root, true); }
    }
    static async Task Reject(Func<Task> operation, string message)
    {
        try { await operation(); } catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or HttpRequestException) { return; }
        throw new Exception(message);
    }
    sealed class FakeHandler : HttpMessageHandler
    {
        public byte[] Data = []; public bool Short, Index, NewTitle;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var content = Index ? Encoding.UTF8.GetBytes(request.RequestUri!.AbsolutePath == RemoteRomService.Server.AbsolutePath ? "<a href='snes/'>snes</a>" : "<a href='Dummy.zip'>dummy</a>" + (NewTitle ? "<a href='New.zip'>new</a>" : "")) : Data;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
            if (Short) response.Content.Headers.ContentLength = content.Length + 100;
            return Task.FromResult(response);
        }
    }
}

