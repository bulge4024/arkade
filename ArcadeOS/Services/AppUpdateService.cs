using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArcadeOS.Services;

public sealed record AppRelease(Version Version, string Tag, string Notes, Uri Package, Uri Checksum, long Size);
public sealed record UpdateState(DateTimeOffset? LastChecked = null, DateTimeOffset? LastAttempt = null);
public sealed class AppUpdateService
{
    public const string Repository = "bulge4024/arkade";
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(2);
    private readonly HttpClient http;
    private readonly string stateFile;
    private readonly string downloadRoot;
    public Version CurrentVersion { get; }
    public UpdateState State { get; private set; } = new();
    public AppUpdateService(HttpClient? http = null, string? dataRoot = null, Version? currentVersion = null)
    {
        this.http = http ?? CreateHttpClient();
        var root = dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArcadeOS", "Updates");
        stateFile = Path.Combine(root, "checks.json"); downloadRoot = root;
        CurrentVersion = currentVersion ?? typeof(AppUpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
        try { State = JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(stateFile)) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }
    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Arkade-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
    public bool IsDue(DateTimeOffset now) =>
        (State.LastChecked is null || now < State.LastChecked || now - State.LastChecked >= CheckInterval) &&
        (State.LastAttempt is null || now < State.LastAttempt || now - State.LastAttempt >= TimeSpan.FromHours(6));
    private void SaveState()
    {
        Directory.CreateDirectory(downloadRoot);
        var temporary = stateFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(State)); File.Move(temporary, stateFile, true);
    }
    public async Task<AppRelease?> CheckAsync(CancellationToken token = default)
    {
        State = State with { LastAttempt = DateTimeOffset.UtcNow }; SaveState();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var json = await http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases?per_page=30", deadline.Token).ConfigureAwait(false);
        var release = SelectRelease(json, CurrentVersion);
        State = State with { LastChecked = DateTimeOffset.UtcNow }; SaveState(); return release;
    }
    public static AppRelease? SelectRelease(string json, Version current)
    {
        using var document = JsonDocument.Parse(json);
        var matches = new List<AppRelease>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.GetProperty("draft").GetBoolean()) continue;
            var tag = item.GetProperty("tag_name").GetString() ?? "";
            if (!Regex.IsMatch(tag, @"^v?\d+\.\d+\.\d+$") || !Version.TryParse(tag.TrimStart('v'), out var version) || Normalize(version) <= Normalize(current)) continue;
            string zipName = $"Arkade-Cabinet-Test-{version}-win-x64.zip", hashName = $"Arkade-Cabinet-Test-{version}-win-x64.sha256";
            Uri? zip = null, hash = null; long size = 0;
            foreach (var asset in item.GetProperty("assets").EnumerateArray())
            {
                if (asset.TryGetProperty("state", out var state) && state.GetString() != "uploaded") continue;
                var name = asset.GetProperty("name").GetString();
                if (name != zipName && name != hashName) continue;
                if (!Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var uri) ||
                    uri.Scheme != "https" || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/{tag}/", StringComparison.Ordinal) || uri.UserInfo.Length != 0) continue;
                if (name == zipName) { zip = uri; size = asset.GetProperty("size").GetInt64(); } else hash = uri;
            }
            if (zip != null && hash != null && size > 0 && size <= 512L * 1024 * 1024)
                matches.Add(new(version, tag, item.TryGetProperty("body", out var notes) ? notes.GetString() ?? "" : "", zip, hash, size));
        }
        return matches.OrderByDescending(r => r.Version).FirstOrDefault();
    }
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
    public async Task<string> DownloadAsync(AppRelease release, IProgress<double>? progress, CancellationToken token)
    {
        var folder = Path.Combine(downloadRoot, "stage-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var checksum = (await http.GetStringAsync(release.Checksum, token).ConfigureAwait(false)).Trim();
            var match = Regex.Match(checksum, @"\A([a-fA-F0-9]{64})\s+\*?([^\r\n]+)\s*\z");
            if (!match.Success || match.Groups[2].Value.Trim() != Path.GetFileName(release.Package.AbsolutePath)) throw new InvalidDataException("The release checksum is invalid.");
            var archive = Path.Combine(folder, "package.zip");
            using (var response = await http.GetAsync(release.Package, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using var output = File.Create(archive);
                var buffer = new byte[81920]; long downloaded = 0; int count;
                while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    downloaded += count; if (downloaded > release.Size || downloaded > 512L * 1024 * 1024) throw new InvalidDataException("The update exceeds its expected size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false); progress?.Report(downloaded * 100d / release.Size);
                }
                if (downloaded != release.Size) throw new InvalidDataException("The update download is incomplete.");
            }
            await using (var stream = File.OpenRead(archive))
            {
                var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
                if (!digest.Equals(match.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update verification failed. Nothing was installed.");
            }
            token.ThrowIfCancellationRequested();
            var payload = await Task.Run(() => ExtractVerified(archive, folder, release.Version, token), token).ConfigureAwait(false);
            File.Delete(archive); return payload;
        }
        catch { try { Directory.Delete(folder, true); } catch (IOException) { } throw; }
    }
    public static string ExtractVerified(string archive, string directory, Version expectedVersion, CancellationToken token = default)
    {
        using var zip = ZipFile.OpenRead(archive);
        string prefix = $"Arkade-Cabinet-Test-{expectedVersion}/";
        if (zip.Entries.Count > 10000 || zip.Entries.Sum(e => e.Length) > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("The update archive is too large.");
        var extraction = Path.GetFullPath(Path.Combine(directory, "extracted")); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Contains(':') || name.Split('/').Any(p => p is "." or "..") || !names.Add(name.TrimEnd('/'))) throw new InvalidDataException("The update contains an unsafe archive path.");
            var destination = Path.GetFullPath(Path.Combine(extraction, name));
            if (!destination.StartsWith(extraction + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid archive path.");
        }
        zip.ExtractToDirectory(extraction);
        var package = Path.Combine(extraction, prefix.TrimEnd('/'));
        using var info = JsonDocument.Parse(File.ReadAllText(Path.Combine(package, "BUILD-INFO.json")));
        if (info.RootElement.GetProperty("Product").GetString() != "Arkade" || info.RootElement.GetProperty("Version").GetString() != expectedVersion.ToString() || info.RootElement.GetProperty("Platform").GetString() != "win-x64") throw new InvalidDataException("This update does not match the selected Windows release.");
        var app = Path.Combine(package, "app");
        foreach (var file in new[] { "Arkade.exe", "Arkade.dll", "Arkade.runtimeconfig.json", "hostfxr.dll" })
            if (!File.Exists(Path.Combine(app, file))) throw new InvalidDataException("The update is missing application files.");
        return app;
    }
    public static ProcessStartInfo CreateInstallCommand(string payload, string installDirectory, int parentId, string? helperPath = null)
    {
        var script = helperPath ?? Path.Combine(AppContext.BaseDirectory, "Assets", "ApplyUpdate.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("The update helper is missing. Reinstall Arkade to restore it.", script);
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(script)! };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Payload", Path.GetFullPath(payload), "-InstallDirectory", Path.GetFullPath(installDirectory), "-ParentId", parentId.ToString() }) info.ArgumentList.Add(argument);
        return info;
    }
    public async Task StartInstallAsync(string payload)
    {
        if (!string.Equals(Path.GetFileName(Environment.ProcessPath), "Arkade.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Install the cabinet build before using in-app installation.");
        // Run a copy outside the installation so the helper never locks the directory being swapped.
        var helper = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(payload))!, "ApplyUpdate.ps1");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "ApplyUpdate.ps1"), helper, true);
        using var process = Process.Start(CreateInstallCommand(payload, AppContext.BaseDirectory, Environment.ProcessId, helper)) ?? throw new InvalidOperationException("Could not start the update installer.");
        var ready = Path.Combine(Path.GetDirectoryName(helper)!, "install.ready");
        var result = Path.Combine(Path.GetDirectoryName(helper)!, "install-result.txt");
        for (int i = 0; i < 600; i++)
        {
            if (File.Exists(ready)) return;
            if (process.HasExited) throw new IOException(File.Exists(result) ? File.ReadAllText(result) : "The installer stopped before it was ready. Nothing was replaced.");
            await Task.Delay(100);
        }
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(helper)!, "install.cancel"), "cancel");
        throw new IOException("The installer is taking too long to prepare. Arkade will stay open; please try again later.");
    }
}
