namespace ArcadeOS.Models;

public sealed record SystemTile(string Name, string Subtitle, int GameCount, string Accent, string Symbol, string? ArtworkPath)
{
    public string? ConsoleImagePath { get; init; }
    public string CountLabel => GameCount == 1 ? "1 GAME" : $"{GameCount} GAMES";
    public string OpenLabel => Name == "Last played" && GameCount == 0 ? "PLAY A GAME TO GET STARTED" : GameCount > 0 ? "SELECT TO EXPLORE  →" : "ADD GAMES TO GET STARTED";
}
