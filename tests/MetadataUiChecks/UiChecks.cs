using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using ArcadeOS.Controls;
using ArcadeOS.Models;
using ArcadeOS.Services;

namespace ArcadeOS;
public partial class MainWindow : Window
{
    private readonly StackPanel MetadataSettingsPanel = new() { Visibility = Visibility.Visible };
    private readonly Grid UtilityOverlay = new() { Visibility = Visibility.Visible };
    private readonly TextBlock MetadataSettingsStatus = new();
    private readonly Button RetryMetadataButton = new();
    private readonly RetroBatSettings retroBatSettings = new();
    private readonly AutomaticMetadataService automaticMetadata;
    private readonly FakeViewModel viewModel = new();
    private bool windowClosed, forceRefreshError = true;
    private MainWindow(string root)
    {
        automaticMetadata = new(root, new BrokenProvider()); Width = 600; Height = 500; Left = 50; Top = 50;
        MetadataSettingsPanel.Children.Add(MetadataSettingsStatus); MetadataSettingsPanel.Children.Add(RetryMetadataButton);
        UtilityOverlay.Children.Add(MetadataSettingsPanel); Content = UtilityOverlay; Closed += (_, _) => windowClosed = true;
    }
    private async Task EnrichLibraryMetadataAsync(IReadOnlyList<Game> games, bool retryMissing = false)
    {
        if (forceRefreshError) throw new InvalidOperationException("Dummy refresh setup failure");
        await automaticMetadata.EnrichAsync(games, retryMissing: retryMissing);
    }
    sealed class FakeViewModel
    {
        public bool ThrowSummary;
        public IReadOnlyList<Game> GetLibrarySnapshot() => ThrowSummary ? throw new FormatException("Dummy summary failure") : [new("Example", "Console", "", "", "", "", "", "", "") { SystemId = "snes", RomPath = "Example.zip" }];
    }
    sealed class BrokenProvider : IGameMetadataProvider
    {
        public bool Supports(string id) => true;
        public Task<CachedGameMetadata?> FindAsync(Game game, CancellationToken token) => throw new ArgumentException("Duplicate provider field");
    }
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    [STAThread]
    static void Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "ArkadeMetadataUi-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("ARCADEOS_METADATA_CACHE", root);
        var app = new Application(); int unhandled = 0;
        app.DispatcherUnhandledException += (_, e) => { unhandled++; e.Handled = true; Console.Error.WriteLine(e.Exception); };
        var window = new MainWindow(root);
        window.Loaded += async (_, _) =>
        {
            try
            {
                window.RetryMetadataClick(window, new()); await Task.Delay(150);
                Assert(unhandled == 0 && window.RetryMetadataButton.IsEnabled && window.metadataWorkerError?.Contains("Dummy refresh") == true, "Manual async retry leaked an exception or remained disabled");
                window.forceRefreshError = false; window.RetryMetadataClick(window, new()); await Task.Delay(200);
                Assert(unhandled == 0 && window.automaticMetadata.Progress.Failed == 1 && window.RetryMetadataButton.IsEnabled, "Provider exception crashed the retry UI");
                window.viewModel.ThrowSummary = true; window.nextMetadataSummary = DateTime.MinValue; window.UpdateMetadataSettingsStatus(); await Task.Delay(100);
                Assert(unhandled == 0 && window.metadataWorkerError?.Contains("Dummy summary") == true, "Status timer leaked a summary exception");
                string invalid = Path.Combine(root, "broken.png"); File.WriteAllBytes(invalid, [137, 80, 78, 71, 13, 10, 26, 10]);
                var converter = new SafeArtworkConverter();
                Assert(converter.Convert(invalid, typeof(BitmapSource), null!, CultureInfo.InvariantCulture) is null, "Truncated cached artwork was not rejected");
                string covers = Path.Combine(root, "covers"); Directory.CreateDirectory(covers);
                string managed = Path.Combine(covers, new string('a', 64) + ".png"); File.WriteAllBytes(managed, [137, 80, 78, 71, 13, 10, 26, 10]);
                _ = converter.Convert(managed, typeof(BitmapSource), null!, CultureInfo.InvariantCulture);
                Assert(!File.Exists(managed) && File.Exists(managed + ".invalid") && File.Exists(invalid), "Corrupt automatic cache was not isolated safely, or custom art was modified");
                string valid = Path.Combine(root, "valid.png"); File.WriteAllBytes(valid, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j9XkAAAAASUVORK5CYII="));
                Assert(converter.Convert(valid, typeof(BitmapSource), null!, CultureInfo.InvariantCulture) is BitmapSource { IsFrozen: true }, "Valid artwork failed safe loading");
                Assert(File.Exists(Path.Combine(root, "metadata-errors.log")), "UI/decode failures were not logged");
                Console.WriteLine("PASS: actual async retry/status handlers recover, retry button restores, corrupt artwork is ignored and valid artwork loads frozen.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
            finally { window.Close(); Directory.Delete(root, true); }
        };
        app.Run(window);
    }
}
