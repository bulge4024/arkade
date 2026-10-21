using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ArcadeOS.Services;

namespace ArcadeOS;
public partial class MainWindow
{
    private readonly RemoteRomService remoteRoms = new();
    private readonly DispatcherTimer romCheckTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private Border romOverlay = null!;
    private TextBlock romStatus = null!;
    private ListBox romList = null!;
    private CheckBox romReplace = null!;
    private Button romDownload = null!, romAll = null!, romNone = null!, romBack = null!, romConsole = null!, romMap = null!;
    private CancellationTokenSource? romCancellation;
    private bool romBusy, romChecking, romShowingGames, romMapping;
    private string? romNotified;
    private RemoteRom? romFolder;
    private RomSystem? romSystem;
    private IReadOnlyList<RomSystem> romSystems = [];
    private readonly List<(RemoteRom Rom, CheckBox Box, string? Existing)> romRows = [];
    private bool RomBrowserOpen => romOverlay?.Visibility == Visibility.Visible;
    private Button[] RomButtons => [romConsole, romMap, romAll, romNone, romDownload, romBack];
    private bool RomIdle => CanOfferUpdate && !RomBrowserOpen && !romBusy && !romChecking && IsActive;
    private void InitializeRomDownloads()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new()); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var title = new TextBlock { Text = "REMOTE GAME LIBRARY", FontSize = 26, FontWeight = FontWeights.Bold, Foreground = Brushes.Gold };
        grid.Children.Add(title);
        romStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 12), FontSize = 15 }; Grid.SetRow(romStatus, 1); grid.Children.Add(romStatus);
        romList = new ListBox { Background = new SolidColorBrush(Color.FromRgb(16, 23, 34)), Foreground = Brushes.White, FontSize = 18, Margin = new(0, 0, 0, 10) };
        ScrollViewer.SetCanContentScroll(romList, true); VirtualizingPanel.SetIsVirtualizing(romList, true);
        Grid.SetRow(romList, 2); grid.Children.Add(romList);
        romReplace = new CheckBox { Content = "REPLACE EXISTING: OFF — installed games cannot be selected", Margin = new(0, 8, 0, 12), Foreground = Brushes.White, FontSize = 15 };
        romReplace.Click += (_, _) => UpdateRomRows(); Grid.SetRow(romReplace, 3); grid.Children.Add(romReplace);
        var buttons = new WrapPanel(); Grid.SetRow(buttons, 4); grid.Children.Add(buttons);
        Button Add(string text, RoutedEventHandler click)
        {
            var button = new Button { Content = text, Margin = new(0, 4, 8, 4), Padding = new(12, 10, 12, 10) };
            button.Click += click; buttons.Children.Add(button); return button;
        }
        romConsole = Add("CONSOLES", async (_, _) => await LoadRemoteConsolesAsync());
        romMap = Add("CHANGE DESTINATION", (_, _) => ShowRomMapping());
        romAll = Add("SELECT ALL", (_, _) => { foreach (var row in romRows.Where(r => r.Box.IsEnabled)) row.Box.IsChecked = true; });
        romNone = Add("UNCHECK ALL", (_, _) => { foreach (var row in romRows) row.Box.IsChecked = false; });
        romDownload = Add("DOWNLOAD SELECTED", async (_, _) => await DownloadSelectedRomsAsync());
        romBack = Add("BACK", (_, _) => CloseRomBrowser());
        romOverlay = new Border { Background = new SolidColorBrush(Color.FromRgb(16, 23, 34)), BorderBrush = Brushes.Gold, BorderThickness = new(1), CornerRadius = new(15), Padding = new(24), Margin = new(22, 16, 22, 16), Child = grid, Visibility = Visibility.Collapsed };
        Panel.SetZIndex(romOverlay, 98); ((Grid)Content).Children.Add(romOverlay);
        romList.MouseDoubleClick += async (_, _) => { if (!romShowingGames || romMapping) await OpenRemoteSelectionAsync(); };
        Loaded += (_, _) => romCheckTimer.Start();
        Closed += (_, _) => { romCheckTimer.Stop(); romCancellation?.Cancel(); remoteRoms.Dispose(); };
        romCheckTimer.Tick += async (_, _) =>
        {
            if (!RomIdle || retroBatSettings is null) return;
            string pending = string.Join("|", remoteRoms.State.Pending.Where(p => p.Value.Length > 0).SelectMany(p => p.Value.Select(f => p.Key + "/" + f)).Order());
            if (pending.Length > 0 && pending != romNotified)
            {
                romNotified = pending; await OpenRomBrowserAsync();
                romStatus.Text = "NEW GAMES ARE AVAILABLE. Choose a console to review and select downloads, or BACK to leave them for later."; return;
            }
            if (!remoteRoms.IsDue(DateTimeOffset.UtcNow)) return;
            romChecking = true;
            try { using var timeout = CancellationTokenSource.CreateLinkedTokenSource(metadataCancellation.Token); timeout.CancelAfter(TimeSpan.FromMinutes(10)); await remoteRoms.CheckAsync(timeout.Token, await Task.Run(() => RemoteRomService.Systems(retroBatSettings), timeout.Token)); }
            catch (Exception ex) when (RomExpected(ex)) { /* Retry after one hour; never interrupt a game on network failure. */ }
            finally { romChecking = false; }
        };
    }
    private static bool RomExpected(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException or System.Xml.XmlException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException or System.Security.SecurityException;
    private async void OpenRomDownloads(object sender, RoutedEventArgs e) => await OpenRomBrowserAsync();
    private async Task OpenRomBrowserAsync()
    {
        if (romChecking) { UtilityText.Text = "The remote library check is running in the background. Please try again shortly."; return; }
        if (retroBatSettings is null) { UtilityText.Text = "RetroBat must be configured before downloading games."; return; }
        StopGamePreview(); SystemHome.IsEnabled = LibrarySurface.IsEnabled = UtilityOverlay.IsEnabled = false;
        romOverlay.Visibility = Visibility.Visible; romOverlay.UpdateLayout(); romBack.Focus();
        await LoadRemoteConsolesAsync();
    }
    private void SetRomBusy(bool busy)
    {
        romBusy = busy; romList.IsEnabled = !busy; romReplace.IsEnabled = !busy;
        foreach (var button in RomButtons) button.IsEnabled = !busy || button == romBack;
        romBack.Content = busy ? "CANCEL" : "BACK";
    }
    private void ConfigureRomView()
    {
        romReplace.Visibility = romAll.Visibility = romNone.Visibility = romDownload.Visibility = romShowingGames && !romMapping ? Visibility.Visible : Visibility.Collapsed;
        romMap.Visibility = romFolder is not null && !romMapping ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task LoadRemoteConsolesAsync()
    {
        if (romBusy) return;
        SetRomBusy(true); romShowingGames = romMapping = false; romFolder = null; romRows.Clear(); romList.Items.Clear(); ConfigureRomView();
        romStatus.Text = "Connecting to your server…";
        romCancellation = CancellationTokenSource.CreateLinkedTokenSource(metadataCancellation.Token);
        try
        {
            romSystems = await Task.Run(() => RemoteRomService.Systems(retroBatSettings!), romCancellation.Token);
            var folders = await remoteRoms.ListAsync(RemoteRomService.Server, true, romCancellation.Token);
            foreach (var folder in folders)
            {
                int pending = remoteRoms.State.Pending.GetValueOrDefault(folder.Name, []).Length;
                romList.Items.Add(new ListBoxItem { Content = folder.Name + (pending > 0 ? $"  · {pending} NEW" : ""), Tag = folder, Padding = new(12) });
            }
            romStatus.Text = "Choose a console. SELECT opens it. UP / DOWN moves; RIGHT reaches actions; BACK closes. Checks for new games run every 36 hours while Arkade is open.";
            if (romList.Items.Count > 0) romList.SelectedIndex = 0;
        }
        catch (Exception ex) when (RomExpected(ex)) { romStatus.Text = "Could not load the remote library. " + ex.Message; }
        finally { romCancellation.Dispose(); romCancellation = null; SetRomBusy(false); romList.Focus(); }
    }
    private async Task OpenRemoteSelectionAsync()
    {
        if (romBusy) return;
        if (romMapping)
        {
            if ((romList.SelectedItem as ListBoxItem)?.Tag is not RomSystem system) return;
            // First selection displays the exact path; a second Select explicitly confirms it.
            if (romSystem != system) { romSystem = system; romStatus.Text = $"CONFIRM DESTINATION: {system.Name}\n{system.Folder}\nPress SELECT again to save this mapping, or choose another system."; return; }
            remoteRoms.State.Mappings[romFolder!.Name] = system.Id; remoteRoms.Save(); await LoadRemoteGamesAsync(); return;
        }
        if ((romList.SelectedItem as ListBoxItem)?.Tag is not RemoteRom folder) return;
        romFolder = folder;
        romSystem = remoteRoms.State.Mappings.TryGetValue(folder.Name, out string? id) ? romSystems.FirstOrDefault(s => s.Id == id) : RemoteRomService.Suggest(folder.Name, romSystems);
        if (romSystem is null) ShowRomMapping(); else await LoadRemoteGamesAsync();
    }
    private void ShowRomMapping()
    {
        if (romBusy || romFolder is null) return;
        romMapping = true; romShowingGames = false; romSystem = null; romList.Items.Clear(); romRows.Clear(); ConfigureRomView();
        foreach (var system in romSystems) romList.Items.Add(new ListBoxItem { Content = system.Name + "  (" + system.Id + ")", Tag = system, Padding = new(12) });
        romStatus.Text = $"Where should “{romFolder.Name}” be saved? Choose a RetroBat system. Arkade will show the destination for confirmation before downloading.";
        romList.SelectedIndex = 0; romList.Focus();
    }
    private async Task LoadRemoteGamesAsync()
    {
        if (romFolder is null || romSystem is null) return;
        SetRomBusy(true); romMapping = false; romShowingGames = true; romRows.Clear(); romList.Items.Clear(); romReplace.IsChecked = false; ConfigureRomView();
        romStatus.Text = "Reading server files and comparing your local library…";
        romCancellation = CancellationTokenSource.CreateLinkedTokenSource(metadataCancellation.Token);
        try
        {
            var files = await remoteRoms.ListAsync(romFolder.Url, false, romCancellation.Token);
            var local = await Task.Run(() => RemoteRomService.LocalFiles(romSystem), romCancellation.Token);
            foreach (var file in files)
            {
                local.TryGetValue(RemoteRomService.TitleKey(file.Name), out string? existing);
                if (existing is null && File.Exists(Path.Combine(romSystem.Folder, file.Name))) existing = Path.Combine(romSystem.Folder, file.Name);
                var box = new CheckBox { Content = file.Name + (existing is not null ? "  · INSTALLED" : ""), Foreground = Brushes.White, FontSize = 17, Focusable = false, Padding = new(8) };
                romRows.Add((file, box, existing)); romList.Items.Add(new ListBoxItem { Content = box, HorizontalContentAlignment = HorizontalAlignment.Stretch });
            }
            remoteRoms.Observe(romFolder.Name, files, true); remoteRoms.Save();
            romStatus.Text = $"{romFolder.Name} → {romSystem.Name}\n{romSystem.Folder}\n{files.Count} server files · {romRows.Count(r => r.Existing is not null)} installed. SELECT toggles a game; RIGHT reaches selection and download controls.";
            if (romList.Items.Count > 0) romList.SelectedIndex = 0;
        }
        catch (Exception ex) when (RomExpected(ex)) { romStatus.Text = "Could not compare this console. " + ex.Message; }
        finally { romCancellation.Dispose(); romCancellation = null; SetRomBusy(false); UpdateRomRows(); romList.Focus(); }
    }
    private void UpdateRomRows()
    {
        bool replace = romReplace.IsChecked == true;
        romReplace.Content = replace ? "REPLACE EXISTING: ON — selected installed files will be overwritten" : "REPLACE EXISTING: OFF — installed games cannot be selected";
        foreach (var row in romRows) { row.Box.IsEnabled = row.Existing is null || replace; if (!row.Box.IsEnabled) row.Box.IsChecked = false; row.Box.Opacity = row.Box.IsEnabled ? 1 : .4; }
    }
    private async Task DownloadSelectedRomsAsync()
    {
        if (romBusy || romSystem is null) return;
        var selected = romRows.Where(r => r.Box.IsChecked == true && r.Box.IsEnabled).ToArray();
        if (selected.Length == 0) { romStatus.Text = "Select at least one game first."; return; }
        bool replace = romReplace.IsChecked == true; SetRomBusy(true);
        romCancellation = CancellationTokenSource.CreateLinkedTokenSource(metadataCancellation.Token);
        int completed = 0; var failures = new List<string>();
        try
        {
            foreach (var row in selected)
            {
                romCancellation.Token.ThrowIfCancellationRequested();
                romStatus.Text = $"Downloading {completed + failures.Count + 1}/{selected.Length}: {row.Rom.Name}…";
                try
                {
                    var progress = new Progress<RomDownloadProgress>(p => romStatus.Text = $"Downloading {completed + failures.Count + 1}/{selected.Length}: {p.Name}\n{p.Bytes / 1048576d:F1} MB" + (p.Total is { } total ? $" / {total / 1048576d:F1} MB" : "") + " - BACK cancels.");
                    await Task.Run(() => remoteRoms.DownloadAsync(row.Rom, romSystem, replace, progress, romCancellation.Token));
                    completed++; row.Box.IsChecked = false;
                }
                catch (Exception ex) when (RomExpected(ex) && ex is not OperationCanceledException) { failures.Add(row.Rom.Name + ": " + ex.Message); }
            }
        }
        catch (OperationCanceledException) { failures.Add("Cancelled. Finished downloads were kept; partial downloads were removed."); }
        finally
        {
            romCancellation.Dispose(); romCancellation = null; SetRomBusy(false);
            await LoadRetroBatLibraryAsync();
            await LoadRemoteGamesAsync();
            romStatus.Text = $"Downloaded {completed}/{selected.Length}. Your library has been refreshed.\n" + string.Join("\n", failures.Take(5)); romBack.Focus();
        }
    }
    private void CloseRomBrowser()
    {
        if (romBusy) { romCancellation?.Cancel(); return; }
        romOverlay.Visibility = Visibility.Collapsed; SystemHome.IsEnabled = LibrarySurface.IsEnabled = UtilityOverlay.IsEnabled = true; RemoteLibraryButton.Focus(); UpdateGamePreview();
    }
    private async void HandleRomKey(KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key == Key.Escape && !e.IsRepeat) { CloseRomBrowser(); return; }
        if (romBusy) return;
        bool select = e.Key is Key.Enter or Key.Space or Key.D1 or Key.NumPad1;
        if (romList.IsKeyboardFocusWithin)
        {
            if (e.Key is Key.Up or Key.Down) { romList.SelectedIndex = Math.Clamp(romList.SelectedIndex + (e.Key == Key.Up ? -1 : 1), 0, Math.Max(0, romList.Items.Count - 1)); if (romList.SelectedItem is { } item) romList.ScrollIntoView(item); }
            else if (e.Key is Key.Right or Key.Tab) { if (romReplace.IsVisible) romReplace.Focus(); else romBack.Focus(); }
            else if (select && !e.IsRepeat)
            {
                if (!romShowingGames || romMapping) await OpenRemoteSelectionAsync();
                else if (romList.SelectedIndex is var index && index >= 0 && index < romRows.Count && romRows[index].Box.IsEnabled) romRows[index].Box.IsChecked = romRows[index].Box.IsChecked != true;
            }
        }
        else if (romReplace.IsKeyboardFocusWithin)
        {
            if (select && !e.IsRepeat) { romReplace.IsChecked = romReplace.IsChecked != true; UpdateRomRows(); }
            else if (e.Key is Key.Up or Key.Left) romList.Focus();
            else if (e.Key is Key.Down or Key.Right or Key.Tab) romAll.Focus();
        }
        else if (select && !e.IsRepeat) ClickFocused(RomButtons);
        else if (e.Key is Key.Up or Key.Left) { if (e.Key == Key.Up) romList.Focus(); else MoveButtonFocus(-1, RomButtons); }
        else if (e.Key is Key.Down or Key.Right or Key.Tab) MoveButtonFocus(1, RomButtons);
    }
}

