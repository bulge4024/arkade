using System.Windows;
using ArcadeOS.Services;

namespace ArcadeOS;
public partial class MainWindow
{
    private DateTime nextMetadataSummary;
    private bool readingMetadataSummary;
    private MetadataLibraryStatus? metadataSummary;
    private string? metadataWorkerError;
    private async void UpdateMetadataSettingsStatus()
    {
        if (MetadataSettingsPanel.Visibility != Visibility.Visible || UtilityOverlay.Visibility != Visibility.Visible) return;
        var p = automaticMetadata.Progress;
        string mode = retroBatSettings?.AutomaticMetadataEnabled == false ? "Automatic metadata is OFF in this installation's settings."
            : p.IsRunning ? $"WORKING · {p.Completed:N0} / {p.Total:N0}\nNow: {p.CurrentGame}"
            : p.Total > 0 ? $"Last batch finished: {p.Completed:N0} / {p.Total:N0}." : "Waiting for new or retryable games.";
        MetadataSettingsStatus.Text = mode + $"\nThis batch: {p.Matched:N0} matched · {p.Artwork:N0} with covers · {p.Unmatched:N0} unmatched · {p.Failed:N0} failed."
            + (metadataSummary is { } summary ? $"\nLibrary: {summary.Covers:N0}/{summary.Total:N0} have artwork · {summary.Waiting:N0} waiting · {summary.Deferred:N0} waiting for retry · {summary.Unsupported:N0} on unsupported systems." : "")
            + (p.LastError is { } error ? "\nLast lookup error: " + error : "")
            + (metadataWorkerError is { } workerError ? "\nMetadata paused: " + workerError : "")
            + "\nUpdates appear when you return to browsing. Refresh Library rescans files; Retry Missing retries unmatched or failed games without deleting existing artwork.";
        if (readingMetadataSummary || DateTime.UtcNow < nextMetadataSummary) return;
        readingMetadataSummary = true; nextMetadataSummary = DateTime.UtcNow.AddSeconds(5);
        try
        {
            var snapshot = viewModel.GetLibrarySnapshot();
            metadataSummary = await Task.Run(() => automaticMetadata.GetLibraryStatus(snapshot));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { metadataWorkerError = ex.Message; }
        finally { readingMetadataSummary = false; }
    }
    private async void RetryMetadataClick(object sender, RoutedEventArgs e)
    {
        if (retroBatSettings?.AutomaticMetadataEnabled == false)
        { MetadataSettingsStatus.Text = "Automatic metadata is disabled in retrobat.settings.json. Enable AutomaticMetadataEnabled to use this feature."; return; }
        metadataWorkerError = null; nextMetadataSummary = DateTime.MinValue;
        RetryMetadataButton.IsEnabled = false;
        MetadataSettingsStatus.Text = "Retry queued. Missing info and covers will be checked after the current batch. You can return to browsing.";
        try { await EnrichLibraryMetadataAsync(viewModel.GetLibrarySnapshot(), retryMissing: true); }
        finally { if (!windowClosed) RetryMetadataButton.IsEnabled = true; }
    }
}
