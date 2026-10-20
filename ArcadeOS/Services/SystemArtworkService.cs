using System.IO;
using ArcadeOS.Models;

namespace ArcadeOS.Services;

public sealed class SystemArtworkService(string installationPath)
{
    public IReadOnlyDictionary<string, string> Resolve(IEnumerable<Game> games)
    {
        var names = new Dictionary<string, string> { ["Mega Drive"] = "megadrive", ["SNES"] = "snes", ["Visual Pinball"] = "vpinball" };
        foreach (var game in games) if (game.SystemId is not null) names[game.Platform] = game.SystemId;
        var result = new Dictionary<string, string>();
        foreach (var (name, id) in names)
        {
            string bundled = Path.Combine(Path.GetDirectoryName(typeof(SystemArtworkService).Assembly.Location)!, "LibraryCache", "systems", id + ".png");
            string installed = Path.Combine(installationPath, "emulationstation", ".emulationstation", "themes", "es-theme-carbon", "art", "consoles", id + ".png");
            if (File.Exists(bundled)) result[name] = bundled;
            else if (File.Exists(installed)) result[name] = installed;
        }
        return result;
    }
}
