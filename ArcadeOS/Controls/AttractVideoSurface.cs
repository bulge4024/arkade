using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ArcadeOS.Controls;
public sealed class AttractVideoSurface : UserControl
{
    private readonly Grid host = new() { Background = Brushes.Black };
    private readonly TextBlock fallback = new() { Text = "ARKADE", FontSize = 40, FontWeight = FontWeights.Black,
        Foreground = new SolidColorBrush(Color.FromRgb(70, 65, 48)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private MediaElement? current;
    public string? CurrentPath { get; private set; }
    public bool IsMuted => current?.IsMuted != false && (current?.Volume ?? 0) == 0;
    public event Action? ClipOpened;
    public event Action? ClipEnded;
    public event Action<string>? ClipFailed;
    public AttractVideoSurface()
    {
        Focusable = false; IsHitTestVisible = false; Content = host; host.Children.Add(fallback);
        Unloaded += (_, _) => Stop();
    }
    public void Play(string path)
    {
        Stop(); CurrentPath = path;
        var media = new MediaElement { IsMuted = true, Volume = 0, LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close,
            Stretch = Stretch.Uniform, Focusable = false, IsHitTestVisible = false, Opacity = 0 };
        current = media; host.Children.Add(media);
        media.MediaOpened += (_, _) => { if (current != media) return; fallback.Visibility = Visibility.Collapsed; media.Opacity = 1; ClipOpened?.Invoke(); };
        media.MediaEnded += (_, _) => { if (current == media) ClipEnded?.Invoke(); };
        media.MediaFailed += (_, _) => { if (current == media) ClipFailed?.Invoke(path); };
        try { media.Source = new Uri(path, UriKind.Absolute); media.Play(); }
        catch (Exception ex) when (Services.MetadataDiagnostics.Recoverable(ex)) { ClipFailed?.Invoke(path); }
    }
    public void Stop()
    {
        var old = current; current = null; CurrentPath = null;
        if (old is not null)
        {
            try { old.Close(); old.Source = null; }
            catch (Exception ex) when (Services.MetadataDiagnostics.Recoverable(ex)) { }
            finally { host.Children.Remove(old); }
        }
        fallback.Visibility = Visibility.Visible;
    }
}
