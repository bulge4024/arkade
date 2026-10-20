using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArcadeOS.Services;

var root = Path.Combine(Path.GetTempPath(), "Arkade-updater-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var version = new Version(0, 2, 8);
    string tag = "v0.2.8", zipName = "Arkade-Cabinet-Test-0.2.8-win-x64.zip", hashName = "Arkade-Cabinet-Test-0.2.8-win-x64.sha256";
    var archive = MakePackage(version);
    var digest = Convert.ToHexString(SHA256.HashData(archive));
    object Release(string releaseTag, string host = "github.com", bool draft = false, bool complete = true) => new
    {
        tag_name = releaseTag, draft, prerelease = true, body = "Test release notes",
        assets = (complete ? new[] { zipName, hashName } : new[] { zipName }).Select(n => new { name = n, state = "uploaded", size = archive.Length,
            browser_download_url = $"https://{host}/bulge4024/arkade/releases/download/{releaseTag}/{n}" })
    };
    string feed = JsonSerializer.Serialize(new[] { Release(tag), Release(tag, draft: true), Release(tag, "example.com") });
    var selected = AppUpdateService.SelectRelease(feed, new Version(0, 1, 4))!;
    Check(selected.Version == version, "Newer public test release not detected");
    Check(AppUpdateService.SelectRelease(feed, new Version(0, 2, 8, 0)) == null, "Same-version update offered");
    Check(AppUpdateService.SelectRelease(feed, new Version(0, 2, 9)) == null, "Downgrade offered");
    Check(AppUpdateService.SelectRelease(JsonSerializer.Serialize(new[] { Release(tag, complete: false) }), new Version(0, 1, 0)) == null, "Incomplete release offered");
    Check(AppUpdateService.SelectRelease(JsonSerializer.Serialize(new[] { Release(tag, "example.com") }), new Version(0, 1, 0)) == null, "Foreign download host accepted");
    var handler = new FakeHandler(feed, archive, digest + "  " + zipName);
    var stateRoot = Path.Combine(root, "state");
    var service = new AppUpdateService(new HttpClient(handler), stateRoot, new Version(0, 1, 4));
    Check(service.IsDue(DateTimeOffset.UtcNow), "First check not due");
    var release = await service.CheckAsync(); Check(release != null, "Check failed");
    Check(!service.IsDue(DateTimeOffset.UtcNow.AddHours(47)), "Check repeated before 48 hours");
    Check(service.IsDue(DateTimeOffset.UtcNow.AddHours(49)), "Check did not become due after 48 hours");
    Check(!new AppUpdateService(new HttpClient(handler), stateRoot, new Version(0, 1, 4)).IsDue(DateTimeOffset.UtcNow), "Restart forgot last check");
    var payload = await service.DownloadAsync(release!, null, CancellationToken.None);
    Check(File.Exists(Path.Combine(payload, "Arkade.exe")), "Verified payload missing");
    handler.Checksum = new string('0', 64) + "  " + zipName;
    await Throws<InvalidDataException>(() => service.DownloadAsync(release!, null, CancellationToken.None), "Checksum mismatch accepted");
    using (var cancelled = new CancellationTokenSource()) { cancelled.Cancel(); await Throws<OperationCanceledException>(() => service.DownloadAsync(release!, null, cancelled.Token), "Cancelled download continued"); }
    handler.Fail = true;
    var offline = new AppUpdateService(new HttpClient(handler), Path.Combine(root, "offline"), new Version(0, 1, 4));
    await Throws<HttpRequestException>(() => offline.CheckAsync(), "Offline check succeeded");
    Check(!offline.IsDue(DateTimeOffset.UtcNow.AddHours(1)) && offline.IsDue(DateTimeOffset.UtcNow.AddHours(7)), "Failure retry backoff incorrect");
    foreach (var badPath in new[] { "../escape.txt", "app/../../escape.txt", "app/C:escape.txt", "app\\..\\escape.txt" })
    {
        var bad = Path.Combine(root, Guid.NewGuid() + ".zip"); File.WriteAllBytes(bad, MakePackage(version, badPath));
        try { AppUpdateService.ExtractVerified(bad, Path.Combine(root, Guid.NewGuid().ToString()), version); throw new Exception("Unsafe archive accepted"); } catch (InvalidDataException) { }
    }
    var wrong = Path.Combine(root, "wrong-version.zip"); File.WriteAllBytes(wrong, MakePackage(version, metadataVersion: "9.9.9"));
    try { AppUpdateService.ExtractVerified(wrong, Path.Combine(root, "wrong"), version); throw new Exception("Mismatched version accepted"); } catch (InvalidDataException) { }
    Console.WriteLine("PASS: release selection, no downgrades, complete assets, trusted URLs, persisted 48-hour interval, offline backoff, SHA256 verification, cancellation, archive traversal and payload version validation. No installed app was changed.");
}
finally { Directory.Delete(root, true); }

static byte[] MakePackage(Version v, string? extra = null, string? metadataVersion = null)
{
    using var buffer = new MemoryStream();
    using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
    {
        string prefix = $"Arkade-Cabinet-Test-{v}/";
        void Add(string name, string content) { using var writer = new StreamWriter(zip.CreateEntry(prefix + name).Open()); writer.Write(content); }
        foreach (var file in new[] { "Arkade.exe", "Arkade.dll", "Arkade.runtimeconfig.json", "hostfxr.dll" }) Add("app/" + file, "fixture");
        Add("BUILD-INFO.json", JsonSerializer.Serialize(new { Product = "Arkade", Version = metadataVersion ?? v.ToString(), Platform = "win-x64" }));
        if (extra != null) Add(extra, "unsafe");
    }
    return buffer.ToArray();
}
static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static async Task Throws<T>(Func<Task> action, string message) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception(message); }
sealed class FakeHandler(string feed, byte[] archive, string checksum) : HttpMessageHandler
{
    public string Checksum = checksum; public bool Fail;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Fail) throw new HttpRequestException("Offline fixture");
        HttpContent content = request.RequestUri!.Host == "api.github.com" ? new StringContent(feed, Encoding.UTF8, "application/json") : request.RequestUri.AbsolutePath.EndsWith(".zip") ? new ByteArrayContent(archive) : new StringContent(Checksum);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}
