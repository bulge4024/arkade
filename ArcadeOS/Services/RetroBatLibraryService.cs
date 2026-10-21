using System.IO;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using ArcadeOS.Models;

namespace ArcadeOS.Services;

public sealed record LibraryScanResult(IReadOnlyList<Game> Games, IReadOnlyList<string> Warnings);

public sealed class RetroBatLibraryService(RetroBatSettings settings)
{
    private static XDocument ReadXml(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 });
        return XDocument.Load(reader);
    }

    public LibraryScanResult Scan()
    {
        var games = new List<Game>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var home = Path.Combine(settings.InstallationPath, "emulationstation");
        var config = Path.Combine(home, ".emulationstation", "es_systems.cfg");
        if (!File.Exists(config)) throw new FileNotFoundException("RetroBat's system list was not found.", config);
        foreach (var system in ReadXml(config).Root?.Elements("system") ?? [])
        {
            var id = (string?)system.Element("name");
            var configuredPath = (string?)system.Element("path");
            var command = (string?)system.Element("command") ?? "";
            // Include Visual Pinball tables; skip utility menus and other unconnected pinball systems.
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(configuredPath)
                || !command.Contains("emulatorLauncher.exe", StringComparison.OrdinalIgnoreCase)
                || id is "retrobat" or "fpinball" or "pinballfx" or "pinballfx2" or "pinballfx3" or "pinballm") continue;
            try
            {
                string folder = settings.RomsRoot is { Length: > 0 }
                    ? Path.GetFullPath(Path.Combine(settings.RomsRoot, id))
                    : ResolvePath(configuredPath, home);
                if (!Directory.Exists(folder)) continue;
                var extensions = ((string?)system.Element("extension") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(ext => ext.StartsWith('.')).ToHashSet(StringComparer.OrdinalIgnoreCase);
                // ROM archives and support files are not playable tables. Discover extracted VPX tables only.
                if (id == "vpinball") extensions = new(StringComparer.OrdinalIgnoreCase) { ".vpx" };
                var metadata = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
                var gamelist = Path.Combine(folder, "gamelist.xml");
                if (File.Exists(gamelist))
                {
                    try
                    {
                        foreach (var entry in ReadXml(gamelist).Root?.Elements("game") ?? [])
                        {
                            if ((string?)entry.Element("path") is { Length: > 0 } relative)
                                metadata[Path.GetFullPath(relative, folder)] = entry;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or XmlException or ArgumentException)
                    { warnings.Add($"{id}: metadata unavailable; using filenames."); }
                }
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System };
                foreach (var path in Directory.EnumerateFiles(folder, "*", options))
                {
                    if (!extensions.Contains(Path.GetExtension(path)) || !seen.Add(path)) continue;
                    metadata.TryGetValue(path, out var entry);
                    if (string.Equals((string?)entry?.Element("hidden"), "true", StringComparison.OrdinalIgnoreCase)) continue;
                    string title = (string?)entry?.Element("name") ?? Path.GetFileNameWithoutExtension(path);
                    string hardware = (string?)system.Element("hardware") ?? (id == "ports" ? "port" : "console");
                    string category = hardware switch { "pinball" => "Pinball", "arcade" => "Arcade", "computer" => "Computer", "port" => "Ports", _ => "Console" };
                    string systemTitle = (string?)system.Element("fullname") ?? id;
                    string? artwork = (string?)entry?.Element("thumbnail") ?? (string?)entry?.Element("image");
                    string? artworkPath = artwork is { Length: > 0 } ? Path.GetFullPath(artwork, folder) : null;
                    if (!File.Exists(artworkPath)) artworkPath = null;
                    games.Add(new Game(title, category, (string?)entry?.Element("releasedate") ?? "",
                        (string?)entry?.Element("players") ?? "See game controls", systemTitle.ToUpperInvariant(),
                        (string?)entry?.Element("desc") ?? $"Play {title} using your existing RetroBat setup.",
                        "#56F4DD", "#103A46", "◈") { RomPath = path, SystemId = id, SystemName = systemTitle, SystemManufacturer = (string?)system.Element("manufacturer"), SystemAliases = (string?)system.Element("platform"), ArtworkPath = artworkPath,
                            MarqueePath = FindMedia((string?)entry?.Element("marquee"), folder, path, "images", "-marquee", [".png", ".jpg", ".jpeg"])
                                ?? FindMedia((string?)entry?.Element("wheel"), folder, path, "marquees", "-marquee", [".png", ".jpg", ".jpeg"]),
                            VideoPath = FindMedia((string?)entry?.Element("video"), folder, path, "videos", "-video", [".mp4", ".m4v", ".wmv", ".avi"]),
                            FanartPath = FindMedia((string?)entry?.Element("fanart"), folder, path, "images", "-fanart", [".jpg", ".png", ".jpeg"]),
                            LastPlayed = DateTime.TryParseExact((string?)entry?.Element("lastplayed"), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeLocal, out var played) ? new DateTimeOffset(played) : null });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { warnings.Add($"{id}: {ex.Message}"); }
        }
        return new(games.OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase).ToArray(), warnings);
    }

    private static string? FindMedia(string? configured, string folder, string rom, string mediaFolder, string suffix, string[] extensions)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                // Never stream arbitrary gamelist URLs in the cabinet browser.
                string? candidate = Uri.TryCreate(configured, UriKind.Absolute, out var uri)
                    ? uri.IsFile ? uri.LocalPath : null : Path.GetFullPath(configured, folder);
                if (candidate is not null && extensions.Contains(Path.GetExtension(candidate), StringComparer.OrdinalIgnoreCase) && File.Exists(candidate)) return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        }
        string name = Path.GetFileNameWithoutExtension(rom);
        foreach (string extension in extensions)
            foreach (string ending in new[] { suffix, "" })
            {
                string candidate = Path.Combine(folder, mediaFolder, name + ending + extension);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }
    private static string ResolvePath(string value, string home)
    {
        if (value.StartsWith('~')) value = home + value[1..];
        value = value.Replace("%HOME%", home, StringComparison.OrdinalIgnoreCase);
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value), home);
    }
}
