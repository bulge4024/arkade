using System.Windows.Controls;
using ArcadeOS.Services;
namespace ArcadeOS.Controls;
public partial class DownloadProgressWidget : UserControl
{
    public DownloadProgressWidget() => InitializeComponent();
    public void Update(RomQueueProgress progress)
    {
        QueueTitle.Text = progress.IsRunning ? $"↓ DOWNLOADS · {Math.Min(progress.Completed + progress.Failed + 1, progress.Total)}/{progress.Total}" : $"↓ {progress.Completed}/{progress.Total} DOWNLOADED";
        FileTitle.Text = progress.IsRunning ? progress.Name : progress.Status;
        string eta = progress.Remaining is { } remaining ? remaining.TotalHours >= 1 ? $"{(int)remaining.TotalHours}:{remaining.Minutes:00}:{remaining.Seconds:00}" : $"{(int)remaining.TotalMinutes}:{remaining.Seconds:00}" : "calculating…";
        TransferDetails.Text = !progress.IsRunning ? progress.Cancelled ? "Cancelled · completed games kept" : progress.Failed > 0 ? $"{progress.Failed} failed · details in Download Games" : "Ready in your library"
            : progress.Status != "Downloading" ? progress.Status
            : (progress.Percent is { } percent ? $"{percent:F0}% · " : "") + $"{progress.Speed / 1048576d:F1} MB/s · file ETA {eta}";
        TransferProgress.IsIndeterminate = progress.IsRunning && progress.Percent is null;
        TransferProgress.Value = progress.IsRunning ? progress.Percent ?? 0 : 100;
    }
}
