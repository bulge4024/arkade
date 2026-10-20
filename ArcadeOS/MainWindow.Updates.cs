using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ArcadeOS.Services;

namespace ArcadeOS;

public partial class MainWindow
{
    private readonly AppUpdateService appUpdates;
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool checkingUpdates, downloadingUpdate;
    private AppRelease? availableUpdate;
    private AppRelease? queuedUpdate;
    private CancellationTokenSource? downloadCancellation;
    private bool SettingsOpen => UtilityOverlay.Visibility == Visibility.Visible && FullscreenButton.Visibility == Visibility.Visible;
    private bool CanOfferUpdate => !windowClosed && !launching && !powerBusy && !checkingUpdates && !downloadingUpdate
        && IsVisible && WindowState != WindowState.Minimized && StartupSplash.Visibility != Visibility.Visible
        && ControllerOverlay.Visibility != Visibility.Visible && UtilityOverlay.Visibility != Visibility.Visible && DetailsOverlay.Visibility != Visibility.Visible && PowerOverlay.Visibility != Visibility.Visible && UpdateOverlay.Visibility != Visibility.Visible;
    private void InitializeUpdates()
    {
        updateTimer.Tick += async (_, _) =>
        {
            if (!CanOfferUpdate) return;
            if (queuedUpdate is { } pending) { queuedUpdate = null; ShowUpdate(pending); }
            else if (appUpdates.IsDue(DateTimeOffset.UtcNow)) await CheckUpdatesAsync(false);
        };
        Loaded += (_, _) => updateTimer.Start();
        Closed += (_, _) => { updateTimer.Stop(); downloadCancellation?.Cancel(); };
    }
    private void RefreshUpdateStatus()
    {
        UpdateSettingsStatus.Text = $"Installed: {appUpdates.CurrentVersion.ToString(3)} · Checks every 2 days while Arkade is open.\n" +
            (appUpdates.State.LastChecked is { } date ? $"Last checked: {date.ToLocalTime():g}." : "Not checked yet.") + "\nApp folder: " + AppContext.BaseDirectory;
        var result = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArcadeOS", "Updates", "last-install.txt");
        try { if (File.Exists(result)) UpdateSettingsStatus.Text += "\n" + File.ReadAllText(result); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private async void CheckUpdatesClick(object sender, RoutedEventArgs e) => await CheckUpdatesAsync(true);
    private async Task CheckUpdatesAsync(bool manual)
    {
        if (checkingUpdates || downloadingUpdate || launching || windowClosed) return;
        checkingUpdates = true; CheckUpdatesButton.IsEnabled = false;
        if (manual) UpdateSettingsStatus.Text = "Checking GitHub for a new Arkade release…";
        try
        {
            var release = await appUpdates.CheckAsync(metadataCancellation.Token);
            if (windowClosed) return;
            RefreshUpdateStatus();
            // Recheck after the network request: a game or dialog may have opened while it ran.
            bool canShow = ControllerOverlay.Visibility != Visibility.Visible && !launching && !powerBusy && PowerOverlay.Visibility != Visibility.Visible && DetailsOverlay.Visibility != Visibility.Visible && StartupSplash.Visibility != Visibility.Visible
                && IsVisible && WindowState != WindowState.Minimized && (manual ? SettingsOpen : UtilityOverlay.Visibility != Visibility.Visible);
            if (release is null) { if (manual) UpdateSettingsStatus.Text = $"You're up to date. Installed: {appUpdates.CurrentVersion.ToString(3)}.\nAutomatic checks run every 2 days."; }
            else if (canShow) ShowUpdate(release);
            else { queuedUpdate = release; UpdateSettingsStatus.Text = $"Arkade {release.Version} is available. Choose Check for updates when ready."; }
        }
        catch (OperationCanceledException) { if (manual && !windowClosed) UpdateSettingsStatus.Text = "The update check timed out. Please try again when connected to the internet."; }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        { if (manual && !windowClosed) UpdateSettingsStatus.Text = "Could not check for updates. " + ex.Message; }
        finally { checkingUpdates = false; if (!windowClosed) CheckUpdatesButton.IsEnabled = true; }
    }
    private void ShowUpdate(AppRelease release)
    {
        queuedUpdate = null; availableUpdate = release; StopGamePreview();
        SystemHome.IsEnabled = LibrarySurface.IsEnabled = UtilityOverlay.IsEnabled = false;
        UpdateTitle.Text = $"ARKADE {release.Version} IS AVAILABLE";
        UpdateMessage.Text = $"Installed: {appUpdates.CurrentVersion.ToString(3)}\n\nDownload and install this update? Arkade will close briefly, then reopen. Your cabinet settings, music and game library will be kept.\n\n" +
            (release.Notes.Length > 2200 ? release.Notes[..2200] : release.Notes);
        UpdateInstallButton.Visibility = Visibility.Visible; UpdateInstallButton.IsEnabled = true;
        UpdateLaterButton.Content = "LATER"; UpdateProgress.Visibility = Visibility.Collapsed;
        UpdateOverlay.Visibility = Visibility.Visible; UpdateOverlay.UpdateLayout(); UpdateLaterButton.Focus();
    }
    private void CloseUpdate(object sender, RoutedEventArgs e)
    {
        if (downloadingUpdate) { downloadCancellation?.Cancel(); return; }
        UpdateOverlay.Visibility = Visibility.Collapsed;
        SystemHome.IsEnabled = LibrarySurface.IsEnabled = UtilityOverlay.IsEnabled = true;
        if (SettingsOpen) CheckUpdatesButton.Focus(); else if (browsingSystems) HomeSettingsButton.Focus(); else SetZone(FocusZone.Footer);
        UpdateGamePreview();
    }
    private async void InstallUpdateClick(object sender, RoutedEventArgs e)
    {
        if (downloadingUpdate || availableUpdate is not { } release) return;
        downloadingUpdate = true; UpdateInstallButton.IsEnabled = false; UpdateLaterButton.Content = "CANCEL DOWNLOAD";
        UpdateProgress.Visibility = Visibility.Visible; UpdateProgress.Value = 0; UpdateMessage.Text = "Downloading and verifying the update…";
        downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(metadataCancellation.Token);
        try
        {
            var payload = await appUpdates.DownloadAsync(release, new Progress<double>(p => { if (!windowClosed) UpdateProgress.Value = p; }), downloadCancellation.Token);
            if (windowClosed) return;
            UpdateMessage.Text = "Preparing installation. Arkade will reopen automatically…";
            UpdateLaterButton.IsEnabled = false;
            await appUpdates.StartInstallAsync(payload);
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException) { if (!windowClosed) UpdateMessage.Text = "Download cancelled. Nothing was installed."; }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or KeyNotFoundException)
        { if (!windowClosed) UpdateMessage.Text = "The update could not be installed. Your current installation is unchanged.\n\n" + ex.Message; }
        finally
        {
            downloadingUpdate = false; downloadCancellation.Dispose(); downloadCancellation = null;
            if (!windowClosed) { UpdateInstallButton.IsEnabled = true; UpdateLaterButton.IsEnabled = true; UpdateLaterButton.Content = "BACK"; UpdateLaterButton.Focus(); }
        }
    }
    private void HandleUpdateKey(KeyEventArgs e)
    {
        if (!e.IsRepeat && e.Key == Key.Escape) CloseUpdate(this, e);
        else if (!e.IsRepeat && e.Key is Key.Enter or Key.Space or Key.D1 or Key.NumPad1) ClickFocused(UpdateInstallButton, UpdateLaterButton);
        else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Tab) MoveButtonFocus(e.Key is Key.Left or Key.Up ? -1 : 1, UpdateInstallButton, UpdateLaterButton);
        e.Handled = true;
    }
}
