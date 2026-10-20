using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ArcadeOS.Services;
namespace ArcadeOS.Controls;

public partial class NowPlayingWidget : UserControl
{
    private MusicPlayerService? music;
    private Storyboard? motion;
    private string? signature;
    public NowPlayingWidget()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (music is not null) music.PropertyChanged -= MusicChanged;
            music = DataContext as MusicPlayerService;
            if (music is not null) music.PropertyChanged += MusicChanged;
            RefreshMotion();
        };
        IsVisibleChanged += (_, _) => RefreshMotion();
        Loaded += (_, _) => RefreshMotion();
        Unloaded += (_, _) => StopMotion();
        TitleViewport.SizeChanged += (_, _) => { signature = null; RefreshMotion(); };
    }
    private void MusicChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MusicPlayerService.Animation) or nameof(MusicPlayerService.TrackName) or nameof(MusicPlayerService.IsPlaying)) RefreshMotion();
    }
    private void StopMotion()
    { motion?.Remove(this); motion = null; signature = null; }
    private void RefreshMotion()
    {
        if (!IsVisible || music?.IsPlaying != true) { StopMotion(); return; }
        string next = music.Animation + "|" + music.CurrentTrack;
        if (signature == next && motion is not null) return;
        StopMotion(); signature = next;
        CassetteArt.Visibility = music.Animation == "Cassette" ? Visibility.Visible : Visibility.Collapsed;
        RecordArt.Visibility = music.Animation == "Record" ? Visibility.Visible : Visibility.Collapsed;
        NotesArt.Visibility = music.Animation == "Notes" ? Visibility.Visible : Visibility.Collapsed;
        motion = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        void Animate(string name, string property, double from, double to, double seconds, bool reverse = false)
        {
            var animation = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds)) { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = reverse };
            Storyboard.SetTargetName(animation, name); Storyboard.SetTargetProperty(animation, new PropertyPath(property)); motion.Children.Add(animation);
        }
        string angle = "(UIElement.RenderTransform).(RotateTransform.Angle)";
        if (music.Animation == "Cassette") { Animate("LeftReel", angle, 0, 360, 2.5); Animate("RightReel", angle, 0, 360, 2.5); }
        else if (music.Animation == "Record") Animate("RecordArt", angle, 0, 360, 3);
        else { Animate("NotesArt", "(UIElement.RenderTransform).(TranslateTransform.Y)", 2, -3, .65, true); Animate("NotesArt", "Opacity", .45, 1, .65, true); }
        // Measure without the clipping viewport constraint so long filenames can scroll fully.
        var title = new FormattedText(music.TrackName, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(SongTitle.FontFamily, SongTitle.FontStyle, SongTitle.FontWeight, SongTitle.FontStretch), SongTitle.FontSize, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        SongTitle.Width = title.WidthIncludingTrailingWhitespace + 1;
        double distance = Math.Max(0, title.WidthIncludingTrailingWhitespace - TitleViewport.ActualWidth + 8);
        if (distance > 0) Animate("SongTitle", "(UIElement.RenderTransform).(TranslateTransform.X)", 0, -distance, Math.Max(3, distance / 22), true);
        else Animate("SongTitle", "Opacity", .65, 1, 2, true);
        motion.Begin(this, true);
    }
}
