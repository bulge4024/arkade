using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ArcadeOS.Services;

public sealed record RomSystem(string Id, string Name, string Folder, string[] Extensions);
public sealed record RemoteRom(string Name, Uri Url);
public sealed record RomDownloadProgress(string Name, long Bytes, long? Total);
public sealed class RemoteRomState
{
    public DateTimeOffset? LastCheck { get; set; }
    public DateTimeOffset? LastAttempt { get; set; }
    public Dictionary<string, string> Mappings { get; set; } = new();
    public Dictionary<string, string[]> Catalogs { get; set; } = new();
    public Dictionary<string, string[]> Pending { get; set; } = new();
}

public sealed class RemoteRomService : IDisposable
{
    public static readonly Uri Server = new("http://51.255.74.178/R4ms/");
    private readonly HttpClient client;
    private readonly string statePath;
    public RemoteRomState State { get; private set; }
    public RemoteRomService(string? statePath = null, HttpMessageHandler? handler = null)
    {
        this.statePath = statePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArcadeOS", "Downloads", "catalog.json");
        client = handler is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler);
        client.Timeout = TimeSpan.FromMinutes(30);
        State = new();
        try { if (File.Exists(this.statePath)) State = JsonSerializer.Deserialize<RemoteRomState>(File.ReadAllText(this.statePath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }
    public bool IsDue(DateTimeOffset now) => (State.LastCheck is null || now - State.LastCheck >= TimeSpan.FromHours(36))
        && (State.LastAttempt is null || now - State.LastAttempt >= TimeSpan.FromHours(1));
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        File.WriteAllText(statePath + ".tmp", JsonSerializer.Serialize(State)); File.Move(statePath + ".tmp", statePath, true);
    }
    public static IReadOnlyList<RomSystem> Systems(RetroBatSettings settings)
    {
        string home = Path.Combine(settings.InstallationPath, "emulationstation");
        using var reader = XmlReader.Create(Path.Combine(home, ".emulationstation", "es_systems.cfg"), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader).Root!.Elements("system")
            .Where(s => ((string?)s.Element("command"))?.Contains("emulatorLauncher.exe", StringComparison.OrdinalIgnoreCase) == true)
            .Select(s =>
            {
                string id = (string)s.Element("name")!, value = (string)s.Element("path")!;
                if (value.StartsWith('~')) value = home + value[1..];
                value = value.Replace("%HOME%", home, StringComparison.OrdinalIgnoreCase);
                return new RomSystem(id, (string?)s.Element("fullname") ?? id,
                    settings.RomsRoot is { Length: > 0 } ? Path.GetFullPath(Path.Combine(settings.RomsRoot, id)) : Path.GetFullPath(Environment.ExpandEnvironmentVariables(value), home),
                    ((string?)s.Element("extension") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }).OrderBy(s => s.Name).ToArray();
    }
    public static bool SafeName(string name) => name.Length is > 0 and < 220 && name is not "." and not ".."
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.Contains('/') && !name.Contains('\\')
        && !name.EndsWith('.') && !name.EndsWith(' ') && !Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)", RegexOptions.IgnoreCase);
    public static IReadOnlyList<RemoteRom> ParseIndex(Uri folder, string html, bool directories)
    {
        var entries = new Dictionary<string, RemoteRom>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(html, "href\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)))
        {
            string href = WebUtility.HtmlDecode(match.Groups[1].Value);
            if (!Uri.TryCreate(folder, href, out var uri) || uri.Scheme != folder.Scheme || uri.Authority != folder.Authority || uri.Query.Length > 0 || uri.Fragment.Length > 0) continue;
            string prefix = folder.AbsolutePath;
            if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string relative = Uri.UnescapeDataString(uri.AbsolutePath[prefix.Length..]);
            bool dir = relative.EndsWith('/'); string name = dir ? relative[..^1] : relative;
            if (dir != directories || !SafeName(name)) continue;
            if (!dir && !AllowedFile(name)) continue;
            entries.TryAdd(name, new(name, uri));
        }
        return entries.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static bool AllowedFile(string name) => new[] { ".zip", ".7z", ".nes", ".sfc", ".smc", ".md", ".gen", ".bin", ".cue", ".chd", ".iso", ".cso", ".pbp", ".gba", ".gb", ".gbc", ".n64", ".z64", ".v64", ".nds", ".3ds", ".cdi", ".gdi", ".vpx", ".a26", ".a78", ".sms", ".gg", ".32x", ".pce", ".ngp", ".ngc", ".lnx", ".j64", ".jag", ".col", ".vec", ".adf", ".d64", ".tap", ".z80", ".wad", ".m3u", ".rvz", ".wbfs", ".nrg" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
    public async Task<IReadOnlyList<RemoteRom>> ListAsync(Uri folder, bool directories, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(45));
        using var response = await client.GetAsync(folder, HttpCompletionOption.ResponseHeadersRead, deadline.Token); response.EnsureSuccessStatusCode();
        using var input = await response.Content.ReadAsStreamAsync(deadline.Token); using var output = new MemoryStream();
        byte[] buffer = new byte[32768]; int count;
        while ((count = await input.ReadAsync(buffer, deadline.Token)) > 0)
        { if (output.Length + count > 8 * 1024 * 1024) throw new InvalidDataException("Server directory listing is too large."); await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token); }
        return ParseIndex(folder, System.Text.Encoding.UTF8.GetString(output.ToArray()), directories);
    }
    public static RomSystem? Suggest(string folder, IReadOnlyList<RomSystem> systems)
    {
        string key = Regex.Replace(folder.ToLowerInvariant(), "[^a-z0-9]", "");
        var exact = systems.Where(s => key == Regex.Replace(s.Id.ToLowerInvariant(), "[^a-z0-9]", "") || key == Regex.Replace(s.Name.ToLowerInvariant(), "[^a-z0-9]", "")).ToArray();
        if (exact.Length == 1) return exact[0];
        string? id = folder switch
        {
            "Sega - Mega Drive - Genesis" => "megadrive", "Sony - PlayStation" => "psx", "Nintendo - Super Nintendo Entertainment System" => "snes",
            "Nintendo - Nintendo Entertainment System" => "nes", "Sega - 32X" => "sega32x", "Arcade - MAME" => "mame",
            "Sega - Dreamcast" => "dreamcast", "Sega - Saturn" => "saturn", "Sega - Mega-CD" => "segacd", "Sega - Game Gear" => "gamegear",
            "Sega - Master System - Mark III" => "mastersystem", "Nintendo - Game Boy" => "gb", "Nintendo - Game Boy Color" => "gbc",
            "Nintendo - Game Boy Advance" => "gba", "Nintendo - Nintendo 64" => "n64", "Nintendo - Nintendo DS" => "nds",
            "Nintendo - GameCube" => "gamecube", "Sony - PlayStation 2" => "ps2", "Sony - PlayStation Portable" => "psp", _ => null
        };
        return systems.SingleOrDefault(s => s.Id == id);
    }
    public static string TitleKey(string path) => Regex.Replace(Regex.Replace(Path.GetFileNameWithoutExtension(path).ToLowerInvariant(), @"\([^)]*\)|\[[^]]*\]", ""), "[^a-z0-9]", "");
    public static Dictionary<string, string> LocalFiles(RomSystem system)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(system.Folder)) return result;
        foreach (string path in Directory.EnumerateFiles(system.Folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System }))
            if (system.Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                result.TryAdd(TitleKey(path), path);
                string relative = Path.GetRelativePath(system.Folder, path);
                if (relative.Contains(Path.DirectorySeparatorChar)) result.TryAdd(TitleKey(relative.Split(Path.DirectorySeparatorChar)[0] + ".zip"), path);
            }
        return result;
    }
    public void Observe(string folder, IReadOnlyList<RemoteRom> files, bool reviewed)
    {
        var names = files.Select(f => f.Name).ToArray();
        if (reviewed) State.Pending.Remove(folder);
        else if (State.Catalogs.TryGetValue(folder, out var previous))
            State.Pending[folder] = State.Pending.GetValueOrDefault(folder, []).Concat(names.Except(previous, StringComparer.OrdinalIgnoreCase)).Intersect(names, StringComparer.OrdinalIgnoreCase).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        State.Catalogs[folder] = names;
    }
    public async Task CheckAsync(CancellationToken token, IReadOnlyList<RomSystem>? systems = null)
    {
        State.LastAttempt = DateTimeOffset.UtcNow; Save();
        var folders = await ListAsync(Server, true, token);
        foreach (var folder in folders)
        {
            var files = await ListAsync(folder.Url, false, token);
            bool newFolder = !State.Catalogs.ContainsKey(folder.Name) && State.LastCheck is not null;
            Observe(folder.Name, files, false);
            if (newFolder) State.Pending[folder.Name] = files.Select(f => f.Name).ToArray();
            RomSystem? system = systems is null ? null : State.Mappings.TryGetValue(folder.Name, out string? id)
                ? systems.FirstOrDefault(s => s.Id == id) : Suggest(folder.Name, systems);
            if (system is not null && State.Pending.TryGetValue(folder.Name, out var pending))
            {
                var local = await Task.Run(() => LocalFiles(system), token);
                State.Pending[folder.Name] = pending.Where(name => !local.ContainsKey(TitleKey(name))).ToArray();
            }
        }
        State.LastCheck = DateTimeOffset.UtcNow; Save();
    }
    public async Task DownloadAsync(RemoteRom rom, RomSystem system, bool replace, IProgress<RomDownloadProgress>? progress, CancellationToken token)
    {
        if (!SafeName(rom.Name) || rom.Url.Authority != Server.Authority || !rom.Url.AbsolutePath.StartsWith(Server.AbsolutePath, StringComparison.Ordinal)) throw new InvalidDataException("Invalid download path.");
        Directory.CreateDirectory(system.Folder);
        // Never write through a junction or symbolic link, including an ancestor.
        for (DirectoryInfo? d = new(system.Folder); d is not null; d = d.Parent)
            if (d.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("The destination uses a linked folder. Choose a direct RetroBat ROM folder.");
        string destination = Path.Combine(system.Folder, rom.Name);
        string gameFolder = Path.Combine(system.Folder, Path.GetFileNameWithoutExtension(rom.Name));
        bool supported = system.Extensions.Contains(Path.GetExtension(rom.Name), StringComparer.OrdinalIgnoreCase);
        string? installed = LocalFiles(system).GetValueOrDefault(TitleKey(rom.Name));
        if ((installed is not null || File.Exists(destination)) && !replace) throw new IOException("This game is already installed. Enable Replace existing first.");
        // Preserve existing filenames for equivalent region-tagged archives, but never change the file format.
        if (installed is not null && Path.GetExtension(installed).Equals(Path.GetExtension(rom.Name), StringComparison.OrdinalIgnoreCase)) destination = installed;
        else if (!supported && installed is not null && Path.GetDirectoryName(installed) != system.Folder && !installed.StartsWith(gameFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The installed game is inside a differently named folder. Move that version before replacing it to preserve its companion files.");
        if (!supported && !rom.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new IOException("This archive format is not supported by this console. Supply a playable file or ZIP on the server.");
        string temp = Path.Combine(system.Folder, ".arkade-" + Guid.NewGuid().ToString("N") + ".part");
        string extraction = temp + "-extract";
        try
        {
            using var response = await client.GetAsync(rom.Url, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
            long? size = response.Content.Headers.ContentLength;
            if (size > 64L * 1024 * 1024 * 1024) throw new IOException("File exceeds the 64 GB download limit.");
            using (var input = await response.Content.ReadAsStreamAsync(token))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                byte[] buffer = new byte[131072]; long total = 0, last = 0; int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    total += count; if (total > 64L * 1024 * 1024 * 1024) throw new IOException("Download exceeds 64 GB.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    if (Environment.TickCount64 - last > 150) { progress?.Report(new(rom.Name, total, size)); last = Environment.TickCount64; }
                }
                if (total == 0 || size is { } expected && total != expected) throw new IOException("Download is incomplete.");
                progress?.Report(new(rom.Name, total, size));
            }
            token.ThrowIfCancellationRequested();
            if (supported)
            {
                if (rom.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { using var archive = ZipFile.OpenRead(temp); if (archive.Entries.Count == 0) throw new InvalidDataException("The downloaded ZIP is empty."); }
                string? oldFile = null;
                if (installed is not null && !installed.Equals(destination, StringComparison.OrdinalIgnoreCase)) oldFile = BackupFile(installed, system.Folder);
                try { File.Move(temp, destination, replace); }
                catch { if (oldFile is not null) File.Move(oldFile, installed!); throw; }
                return;
            }
            // Disc systems often require unzipping. Keep cue/bin and other companion files together in one game folder.
            Directory.CreateDirectory(extraction);
            using (var zip = ZipFile.OpenRead(temp))
            {
                if (zip.Entries.Count > 10000 || zip.Entries.Sum(e => e.Length) > 128L * 1024 * 1024 * 1024) throw new IOException("Archive exceeds extraction limits.");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    if (relative.Split(Path.DirectorySeparatorChar).Where(p => p.Length > 0).Any(p => !SafeName(p))) throw new InvalidDataException("Unsafe archive entry.");
                    string target = Path.GetFullPath(relative, extraction);
                    if (!target.StartsWith(extraction + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !seen.Add(target)) throw new InvalidDataException("Unsafe or duplicate archive entry.");
                    if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                    if (!AllowedFile(entry.Name)) throw new InvalidDataException("Archive contains a non-game file: " + entry.Name);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!); entry.ExtractToFile(target);
                }
                if (!Directory.EnumerateFiles(extraction, "*", SearchOption.AllDirectories).Any(p => system.Extensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))) throw new IOException("Archive contains no playable files for this console.");
            }
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(gameFolder))
            {
                if (!replace) throw new IOException("An extracted game folder already exists. Enable Replace existing first.");
                if (new DirectoryInfo(gameFolder).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("The existing game folder is a link.");
                string backup = Path.Combine(system.Folder, ".arkade-backup-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(backup); File.SetAttributes(backup, FileAttributes.Hidden);
                string old = Path.Combine(backup, "previous"); Directory.Move(gameFolder, old);
                try { Directory.Move(extraction, gameFolder); }
                catch { Directory.Move(old, gameFolder); Directory.Delete(backup); throw; }
                // Keep the previous extracted set hidden for recovery; library scanning skips it.
            }
            else
            {
                string? oldFile = installed is not null && File.Exists(installed) ? BackupFile(installed, system.Folder) : null;
                try { Directory.Move(extraction, gameFolder); }
                catch { if (oldFile is not null) File.Move(oldFile, installed!); throw; }
            }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); if (Directory.Exists(extraction)) Directory.Delete(extraction, true); }
    }
    private static string BackupFile(string path, string root)
    {
        string backup = Path.Combine(root, ".arkade-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup); File.SetAttributes(backup, FileAttributes.Hidden);
        string destination = Path.Combine(backup, Path.GetFileName(path)); File.Move(path, destination); return destination;
    }
    public void Dispose() => client.Dispose();
}
