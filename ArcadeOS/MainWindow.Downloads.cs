using System.Windows;
using System.Windows.Threading;
using ArcadeOS.Services;

namespace ArcadeOS;
public partial class MainWindow
{
    private RomDownloadQueue romDownloads = null!;
    private readonly DispatcherTimer downloadWidgetTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private DownloadOverlayWindow? gameDownloadOverlay;
    private IntPtr downloadGameWindow;
    private bool romLibraryRefreshPending;
    private DateTime nextDownloadLibraryRefresh, downloadNoticeUntil;
    private RomQueueProgress? lastDownloadSnapshot;
    private void InitializeBackgroundDownloads()
    {
        romDownloads = new(async (request, progress, token) =>
            await remoteRoms.DownloadAsync(request.Rom, request.System, request.Replace, progress, token, beforeCommit: async cancellation =>
            {
                // New files are safe to install during play. Existing files wait until the game has closed.
                if (!request.Replace) return;
                while (await Dispatcher.InvokeAsync(() => launching))
                {
                    romDownloads.SetStatus("Replacement waiting for game to close…");
                    await Task.Delay(500, cancellation).ConfigureAwait(false);
                }
            }).ConfigureAwait(false));
        romDownloads.FileCompleted += request =>
        {
            if (!windowClosed) Dispatcher.BeginInvoke(new Action(() =>
            {
                romLibraryRefreshPending = true;
                if (!RomBrowserOpen || launching || !IsActive) return;
                if (romSystem?.Folder.Equals(request.System.Folder, StringComparison.OrdinalIgnoreCase) != true) return;
                for (int index = 0; index < romRows.Count; index++)
                {
                    var row = romRows[index];
                    if (!row.Rom.Name.Equals(request.Rom.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    row.Box.IsChecked = false; row.Box.Content = row.Rom.Name + "  · INSTALLED";
                    romRows[index] = (row.Rom, row.Box, System.IO.Path.Combine(request.System.Folder, row.Rom.Name));
                }
                UpdateRomRows();
            }));
        };
        Loaded += (_, _) => downloadWidgetTimer.Start();
        Closed += (_, _) => { downloadWidgetTimer.Stop(); romDownloads.Dispose(); gameDownloadOverlay?.Close(); };
        downloadWidgetTimer.Tick += (_, _) =>
        {
            if (windowClosed) return;
            var progress = romDownloads.Progress;
            if (!progress.IsRunning && !ReferenceEquals(progress, lastDownloadSnapshot) && progress.Total > 0)
                downloadNoticeUntil = DateTime.UtcNow.AddSeconds(12);
            lastDownloadSnapshot = progress;
            bool visible = progress.IsRunning || DateTime.UtcNow < downloadNoticeUntil;
            DownloadDisplay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            NowPlayingDisplay.Opacity = visible ? 0 : 1;
            if (visible) DownloadDisplay.Update(progress);
            romCancel.Visibility = progress.IsRunning ? Visibility.Visible : Visibility.Collapsed;
            romQueueStatus.Text = progress.IsRunning
                ? $"BACKGROUND DOWNLOADS: {progress.Completed + progress.Failed}/{progress.Total} finished · {progress.Name}\nBACK keeps downloading. Cancel Downloads stops the queue."
                : progress.Total > 0 ? $"{progress.Completed}/{progress.Total} downloaded. {progress.Status}\n" + string.Join("\n", romDownloads.Errors.Take(2)) : "";
            if (visible && launching && DownloadOverlayWindow.IsGameForeground(downloadGameWindow))
                (gameDownloadOverlay ??= new DownloadOverlayWindow()).Update(this, progress);
            else gameDownloadOverlay?.Hide();
            if (romLibraryRefreshPending && CanRefreshInBackground && DateTime.UtcNow >= nextDownloadLibraryRefresh)
            {
                romLibraryRefreshPending = false; nextDownloadLibraryRefresh = DateTime.UtcNow.AddSeconds(10);
                _ = LoadRetroBatLibraryAsync(background: true);
            }
        };
    }
}

