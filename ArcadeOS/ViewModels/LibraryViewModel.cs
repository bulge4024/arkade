using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Runtime.CompilerServices;
using ArcadeOS.Models;
using ArcadeOS.Services;

namespace ArcadeOS.ViewModels;

public sealed class LibraryViewModel : INotifyPropertyChanged
{
    public const string RecentCategory = "Last played";
    private readonly RecentGamesService recentGames = new();
    public void RecordPlayed(Game game, DateTimeOffset? when = null)
    {
        if (!game.CanLaunch) return;
        recentGames.Record(game, when ?? DateTimeOffset.UtcNow);
        RebuildSystems(); Refresh();
    }
    private DateTimeOffset? PlayedAt(Game game) => recentGames.LastPlayed(game) is { } saved
        ? game.LastPlayed is { } imported && imported > saved ? imported : saved : game.LastPlayed;
    private Game[] RecentGames() => library.Where(game => PlayedAt(game) is not null)
        .OrderByDescending(PlayedAt).ThenBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    private Game[] library =
    [
        new("NEON CIRCUIT", "Arcade", "1992", "1–2 players", "CHASE THE LIGHT.", "Thread the midnight circuit in a neon-soaked race against the clock. An original demo entry for the ArcadeOS library.", "#56F4DD", "#103A46", "◈"),
        new("ORBIT BREAKER", "Arcade", "1987", "1–2 players", "BEYOND THE LAST STAR.", "Protect the outer colonies through waves of cosmic invaders. Original placeholder game and artwork.", "#AF91FF", "#29204D", "✦"),
        new("SOLAR FLARE", "Pinball", "1995", "1–4 players", "IGNITE THE PLAYFIELD.", "Light the solar lanes and build a stellar multiball. Original placeholder pinball table.", "#FFB765", "#49291C", "☀"),
        new("PIXEL QUEST", "Console", "1991", "1 player", "A WORLD TO DISCOVER.", "Explore forgotten ruins and luminous forests. Original placeholder adventure entry.", "#8CDF93", "#193F35", "⌘"),
        new("AFTER HOURS", "Arcade", "1989", "1–2 players", "THE CITY IS YOURS.", "Keep the rhythm alive beneath the city lights. Original placeholder arcade entry.", "#FF79B5", "#481F43", "♫"),
        new("DEEP SPACE", "Pinball", "1997", "1–4 players", "ONE MORE ORBIT.", "Send the ball into orbit and unlock the deep-space jackpot. Original placeholder pinball table.", "#70BDFF", "#192F53", "◎"),
        new("TURBO VALLEY", "Console", "1994", "1–2 players", "TAKE THE LONG WAY.", "Find your racing line through a sunlit mountain pass. Original placeholder racing entry.", "#FFD76B", "#493C20", "≋"),
        new("NIGHT SHIFT", "Arcade", "1986", "1–2 players", "KEEP THE LIGHTS ON.", "A late-night score chase in an electric metropolis. Original placeholder arcade entry.", "#79E0EF", "#173D4D", "⚡"),
        new("LUNAR TEMPLE", "Console", "1993", "1 player", "FOLLOW THE MOON.", "Solve the mysteries of a temple among the stars. Original placeholder puzzle entry.", "#C6A9FF", "#36254B", "☽")
    ];
    public ObservableCollection<Game> Games { get; } = [];
    public ObservableCollection<SystemTile> Systems { get; } = [];
    private SystemTile? selectedSystem;
    public SystemTile? SelectedSystem { get => selectedSystem; set { selectedSystem = value; Changed(); } }
    public string SystemCount => $"{Math.Max(0, Systems.Count - 1):00} SYSTEMS";
    private IReadOnlyDictionary<string, string> systemImages = new Dictionary<string, string>();
    public void SetSystemImages(IReadOnlyDictionary<string, string> images)
    {
        if (systemImages.Count == images.Count && systemImages.All(item => images.TryGetValue(item.Key, out var path) && path == item.Value)) return;
        systemImages = images;
        RebuildSystems();
    }
    public string[] Categories { get; private set; } = [RecentCategory, "All games", "Mega Drive", "SNES", "Visual Pinball", "Arcade", "Pinball", "Console"];
    private readonly Dictionary<string, Game> lastSelection = new();
    private string librarySource = "DEMO COLLECTION";
    public string LibrarySource { get => librarySource; set { librarySource = value; Changed(); } }
    private string launchMessage = "Select Play to start. Back returns to the library.";
    public string LaunchMessage { get => launchMessage; set { launchMessage = value; Changed(); } }
    private bool isUpdating;
    public bool IsUpdating { get => isUpdating; set { if (isUpdating == value) return; isUpdating = value; Changed(); } }
    private string updateMessage = "";
    public string UpdateMessage { get => updateMessage; set { if (updateMessage == value) return; updateMessage = value; Changed(); } }
    public Game[] GetLibrarySnapshot() => library;
    public bool MatchesLibrary(IEnumerable<Game> games) => library.SequenceEqual(games);
    public void SetLibrary(IEnumerable<Game> games)
    {
        var previousCategory = Category;
        var remembered = lastSelection.ToArray();
        library = games.ToArray();
        lastSelection.Clear();
        var byKey = library.ToDictionary(GameKey, StringComparer.OrdinalIgnoreCase);
        foreach (var (platform, game) in remembered)
        {
            byKey.TryGetValue(GameKey(game), out var replacement);
            if (replacement is not null) lastSelection[platform] = replacement;
        }
        var categories = new[] { RecentCategory, "All games", "Mega Drive", "SNES", "Visual Pinball" }
            .Concat(library.Select(g => g.Platform).Distinct().Order()).Distinct().ToArray();
        if (!Categories.SequenceEqual(categories)) { Categories = categories; Changed(nameof(Categories)); }
        RebuildSystems();
        category = Categories.Contains(previousCategory) ? previousCategory : "All games";
        if (category != previousCategory) Changed(nameof(Category));
        Refresh();
        LibrarySource = $"RETROBAT  /  {library.Length} GAMES";
    }
    private void RebuildSystems()
    {
        var previousSystem = SelectedSystem?.Name;
        var grouped = library.GroupBy(game => game.Platform).ToDictionary(group => group.Key, group => group.ToArray());
        var tiles = new[] { RecentCategory, "Mega Drive", "SNES", "Visual Pinball" }.Concat(library.Select(g => g.Platform).Order()).Distinct()
            .Select(name =>
            {
                var games = name == RecentCategory ? RecentGames() : grouped.GetValueOrDefault(name) ?? [];
                var (subtitle, accent, symbol) = name switch
                {
                    RecentCategory => ("PICK UP WHERE YOU LEFT OFF", "#EAC574", "↶"),
                    "Mega Drive" => ("SEGA  /  16-BIT CLASSICS", "#79B8FA", "≋"),
                    "SNES" => ("NINTENDO  /  16-BIT CLASSICS", "#BAA0F5", "✣"),
                    "Visual Pinball" => ("VIRTUAL PINBALL  /  VPX TABLES", "#EAC574", "◎"),
                    "Ports" => ("CLASSIC GAMES  /  NATIVE PORTS", "#76D6BA", "⌘"),
                    _ => ("THE CABINET COLLECTION", "#EAC574", "◈")
                };
                return new SystemTile(name, subtitle, games.Length, accent, symbol,
                    games.FirstOrDefault(game => game.ArtworkPath is not null)?.ArtworkPath)
                    { ConsoleImagePath = systemImages.GetValueOrDefault(name) };
            }).ToArray();
        Reconcile(Systems, tiles, tile => tile.Name);
        SelectedSystem = Systems.FirstOrDefault(tile => tile.Name == previousSystem) ?? Systems.FirstOrDefault();
        Changed(nameof(SystemCount));
    }
    public ObservableCollection<string> Letters { get; } = [];
    private string? selectedLetter;
    public string? SelectedLetter
    {
        get => selectedLetter;
        set
        {
            if (value is null || selectedLetter == value) return;
            selectedLetter = value; Changed();
            if (Games.FirstOrDefault(game => GameLetter(game) == value) is { } first) SelectedGame = first;
        }
    }
    public static string GameLetter(Game game)
    {
        string title = game.Title.TrimStart().Normalize(NormalizationForm.FormD);
        char first = title.Length > 0 ? char.ToUpperInvariant(title[0]) : '#';
        return first is >= 'A' and <= 'Z' ? first.ToString() : "#";
    }
    private Game? selectedGame;
    public Game? SelectedGame { get => selectedGame; set { if (ReferenceEquals(selectedGame, value)) return; selectedGame = value; if (value is not null) { lastSelection[category] = value; selectedLetter = GameLetter(value); } else selectedLetter = null; Changed(nameof(SelectedLetter)); Changed(); } }
    private string category = "All games";
    public string Category { get => category; set { if (category == value) return; category = value; Refresh(); Changed(); } }
    private string status = "Your next high score starts here.";
    public string Status { get => status; set { status = value; Changed(); } }
    public string LibraryCount => $"{Games.Count:00} GAMES  /  {Category.ToUpperInvariant()}";
    public LibraryViewModel() { RebuildSystems(); Refresh(); }
    private void Refresh()
    {
        var desired = Category == RecentCategory ? RecentGames() : library.Where(g => Category == "All games" || g.Platform == Category)
            .OrderBy(GameLetter, StringComparer.Ordinal).ThenBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        string? previousKey = lastSelection.TryGetValue(Category, out var previous) ? GameKey(previous) : null;
        Reconcile(Games, desired, GameKey);
        Reconcile(Letters, desired.Select(GameLetter).Distinct().Order(StringComparer.Ordinal).ToArray(), letter => letter);
        SelectedGame = Games.FirstOrDefault(game => GameKey(game).Equals(previousKey, StringComparison.OrdinalIgnoreCase)) ?? Games.FirstOrDefault();
        Changed(nameof(LibraryCount));
    }
    private static string GameKey(Game game) => game.RomPath ?? game.Platform + "|" + game.Title;
    private static void Reconcile<T>(ObservableCollection<T> collection, IReadOnlyList<T> desired, Func<T, string> key)
    {
        var wanted = desired.Select(key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = collection.Count - 1; i >= 0; i--) if (!wanted.Contains(key(collection[i]))) collection.RemoveAt(i);
        var existing = collection.ToDictionary(key, StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < desired.Count; index++)
        {
            T next = desired[index];
            string nextKey = key(next);
            if (index < collection.Count && key(collection[index]).Equals(nextKey, StringComparison.OrdinalIgnoreCase))
            {
                if (!EqualityComparer<T>.Default.Equals(collection[index], next)) collection[index] = next;
                continue;
            }
            if (existing.TryGetValue(nextKey, out var old))
            {
                int current = collection.IndexOf(old);
                if (current >= 0) collection.Move(current, index); else collection.Insert(index, next);
                if (!EqualityComparer<T>.Default.Equals(collection[index], next)) collection[index] = next;
            }
            else collection.Insert(index, next);
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
