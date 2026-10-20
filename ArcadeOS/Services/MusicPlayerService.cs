using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Threading;
namespace ArcadeOS.Services;

public sealed class MusicPlayerService : INotifyPropertyChanged, IDisposable
{
    private sealed record Preferences(string Folder, double Volume, string Animation = "Cassette");
    private readonly DispatcherTimer progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    public string Animation { get; private set; } = "Cassette";
    public string AnimationLabel => "ANIMATION: " + Animation.ToUpperInvariant();
    public double Progress => player.NaturalDuration.HasTimeSpan && player.NaturalDuration.TimeSpan.TotalSeconds > 0
        ? Math.Clamp(player.Position.TotalSeconds / player.NaturalDuration.TimeSpan.TotalSeconds, 0, 1) : 0;
    public string ProgressText => player.NaturalDuration.HasTimeSpan
        ? $"{FormatTime(player.Position)} / {FormatTime(player.NaturalDuration.TimeSpan)}" : "0:00 / –:––";
    private static string FormatTime(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    public void CycleAnimation()
    {
        Animation = Animation switch { "Cassette" => "Record", "Record" => "Notes", _ => "Cassette" };
        Save(); ChangedAll();
    }
    private void ProgressChanged()
    {
        PropertyChanged?.Invoke(this, new(nameof(Progress)));
        PropertyChanged?.Invoke(this, new(nameof(ProgressText)));
    }
    private readonly MediaPlayer player = new();
    private readonly string settingsPath;
    private string[] tracks = [];
    private readonly HashSet<string> failed = new(StringComparer.OrdinalIgnoreCase);
    private int index = -1, scanVersion;
    private bool wantsPlayback, suspended, disposed, loading;
    public string Folder { get; private set; }
    public double Volume { get; private set; } = .35;
    public bool IsPlaying => wantsPlayback && !suspended && index >= 0;
    public string? CurrentTrack => index >= 0 && index < tracks.Length ? tracks[index] : null;
    public string TrackName => CurrentTrack is { } track ? Path.GetFileNameWithoutExtension(track) : "No track selected";
    public int TrackCount => tracks.Length;
    public string PlayLabel => wantsPlayback ? "PAUSE" : "PLAY";
    public string Status => tracks.Length > 0 && failed.Count >= tracks.Length ? "These audio files could not play. Choose another folder or check the audio format." : loading ? "Scanning music folder…" : tracks.Length == 0 ? "Choose a folder containing MP3, WAV or other Windows-supported audio."
        : suspended && wantsPlayback ? $"Paused for game · {TrackName}"
        : $"{(IsPlaying ? "Playing" : "Paused")} · {TrackName} · {tracks.Length} tracks · {Volume:P0}";
    public MusicPlayerService(string? settingsPath = null)
    {
        this.settingsPath = settingsPath ?? Path.Combine(AutomaticMetadataService.DefaultCacheDirectory, "music-player.json");
        Folder = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        try
        {
            if (File.Exists(this.settingsPath) && JsonSerializer.Deserialize<Preferences>(File.ReadAllText(this.settingsPath)) is { } saved)
            { Animation = saved.Animation is "Record" or "Notes" ? saved.Animation : "Cassette"; Folder = saved.Folder; Volume = double.IsFinite(saved.Volume) ? Math.Clamp(saved.Volume, 0, 1) : .35; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        progressTimer.Tick += (_, _) => ProgressChanged();
        player.MediaOpened += (_, _) => ProgressChanged();
        player.Volume = Volume;
        player.MediaEnded += (_, _) => Next();
        player.MediaFailed += (_, _) =>
        {
            if (CurrentTrack is { } track) failed.Add(track);
            if (failed.Count >= tracks.Length) { wantsPlayback = false; player.Close(); ChangedAll(); }
            else Next();
        };
    }
    public async Task LoadFolderAsync(string? folder = null)
    {
        int version = ++scanVersion;
        string target = folder ?? Folder;
        loading = true; ChangedAll();
        string[] found = await Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(target)) return Array.Empty<string>();
                var extensions = new HashSet<string>([".mp3", ".wav", ".wma", ".m4a", ".aac", ".flac"], StringComparer.OrdinalIgnoreCase);
                return Directory.EnumerateFiles(target, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System })
                    .Where(path => extensions.Contains(Path.GetExtension(path))).Take(10000).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return Array.Empty<string>(); }
        });
        if (disposed || version != scanVersion) return;
        player.Close(); Folder = target; tracks = found; index = tracks.Length > 0 ? 0 : -1; failed.Clear(); loading = false;
        if (index < 0) wantsPlayback = false;
        else OpenCurrent();
        Save(); ChangedAll();
    }
    public void Toggle()
    {
        if (disposed || loading || index < 0) { ChangedAll(); return; }
        wantsPlayback = !wantsPlayback;
        if (wantsPlayback && !suspended) player.Play(); else player.Pause();
        ChangedAll();
    }
    public void Next() => Move(1);
    public void Previous() => Move(-1);
    private void Move(int delta)
    {
        if (disposed || loading || tracks.Length == 0) return;
        for (int attempt = 0; attempt < tracks.Length; attempt++)
        {
            index = (index + delta + tracks.Length) % tracks.Length;
            if (!failed.Contains(tracks[index])) { OpenCurrent(); ChangedAll(); return; }
        }
        wantsPlayback = false; player.Close(); ChangedAll();
    }
    private void OpenCurrent()
    {
        player.Close(); player.Open(new Uri(tracks[index], UriKind.Absolute)); player.Volume = Volume;
        if (wantsPlayback && !suspended) player.Play();
    }
    public void SetSuspended(bool value)
    {
        if (suspended == value || disposed) return;
        suspended = value;
        if (wantsPlayback && index >= 0) { if (suspended) player.Pause(); else player.Play(); }
        ChangedAll();
    }
    public void ChangeVolume(double delta)
    { Volume = Math.Clamp(Volume + delta, 0, 1); player.Volume = Volume; Save(); ChangedAll(); }
    private void Save()
    {
        string temp = settingsPath + ".tmp";
        try { Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!); File.WriteAllText(temp, JsonSerializer.Serialize(new Preferences(Folder, Volume, Animation))); File.Move(temp, settingsPath, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Trace.TraceWarning($"Music preferences could not be saved: {ex.Message}"); }
    }
    public void Dispose() { disposed = true; scanVersion++; progressTimer.Stop(); player.Close(); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void ChangedAll() { if (IsPlaying && !disposed) progressTimer.Start(); else progressTimer.Stop(); ProgressChanged(); foreach (var name in new[] { nameof(Animation), nameof(AnimationLabel), nameof(Folder), nameof(Volume), nameof(TrackName), nameof(TrackCount), nameof(IsPlaying), nameof(PlayLabel), nameof(Status) }) PropertyChanged?.Invoke(this, new(name)); }
}
