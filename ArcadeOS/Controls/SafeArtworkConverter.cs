using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArcadeOS.Services;
namespace ArcadeOS.Controls;
public sealed class SafeArtworkConverter : IValueConverter
{
    private readonly Dictionary<string, ImageSource?> cache = new(StringComparer.OrdinalIgnoreCase);
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ImageSource image) return image;
        if (value is not string path || !File.Exists(path)) return null;
        string? key = null;
        try
        {
            var info = new FileInfo(path); if (info.Length > 16 * 1024 * 1024 || info.Length == 0) return null;
            key = path + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            if (cache.TryGetValue(key, out var existing)) return existing;
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || (long)frame.PixelWidth * frame.PixelHeight > 80_000_000) throw new InvalidDataException("Artwork dimensions exceeded the safe decode limit.");
            stream.Position = 0;
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (frame.PixelHeight >= frame.PixelWidth) bitmap.DecodePixelHeight = Math.Min(1000, frame.PixelHeight);
            else bitmap.DecodePixelWidth = Math.Min(1000, frame.PixelWidth);
            bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            if (cache.Count >= 24) cache.Clear(); cache[key] = bitmap; return bitmap;
        }
        catch (Exception ex) when (MetadataDiagnostics.Recoverable(ex))
        {
            if (key is not null) { if (cache.Count >= 24) cache.Clear(); cache[key] = null; }
            MetadataDiagnostics.Record(AutomaticMetadataService.DefaultCacheDirectory, ex, "Artwork decode: " + path);
            try
            {
                string managedRoot = Path.GetFullPath(Path.Combine(AutomaticMetadataService.DefaultCacheDirectory, "covers")) + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(path);
                if (full.StartsWith(managedRoot, StringComparison.OrdinalIgnoreCase)
                    && System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), @"^[a-f0-9]{64}(?:-broad|-pinball)?\.(?:png|jpg)$"))
                { File.Move(full, full + ".invalid", true); File.SetLastWriteTimeUtc(full + ".invalid", DateTime.UtcNow); }
            }
            catch (Exception cacheError) when (MetadataDiagnostics.Recoverable(cacheError)) { }
            return null;
        }
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
