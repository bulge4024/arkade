using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ArcadeOS.ViewModels;
using ArcadeOS.Services;
using System.IO;

namespace ArcadeOS;

public partial class MainWindow : Window
{
    private HwndSource? musicMessageSource;
    private readonly SecondaryDisplayService secondaryDisplay;
    private readonly DispatcherTimer secondaryDelay = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private ArcadeOS.Models.Game? companionRunningGame;
    private void QueueSecondaryDisplay()
    {
        if (windowClosed) return;
        secondaryDelay.Stop(); secondaryDelay.Start();
    }
    private void RefreshSecondaryDisplay()
    {
        secondaryDelay.Stop(); if (windowClosed || !IsLoaded) return;
        secondaryDisplay.Update(new WindowInteropHelper(this).Handle, viewModel.SelectedSystem, viewModel.SelectedGame,
            browsingSystems, companionRunningGame, FindResource("RecentArtwork") as ImageSource);
        if (SecondaryScreenPanel.Visibility == Visibility.Visible) RefreshSecondarySetting();
    }
    private void DisplayConfigurationChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() => { if (!windowClosed) { secondaryDisplay.Hide(); QueueSecondaryDisplay(); } }));
    }
    private void RefreshSecondarySetting()
    {
        SecondaryScreenButton.Content = secondaryDisplay.SettingLabel;
        SecondaryScreenStatus.Text = secondaryDisplay.Status(new WindowInteropHelper(this).Handle) + "\nSelect to cycle automatic, off and detected displays.";
    }
    private void CycleSecondaryScreen(object sender, RoutedEventArgs e)
    {
        try { secondaryDisplay.CycleTarget(new WindowInteropHelper(this).Handle); RefreshSecondaryDisplay(); RefreshSecondarySetting(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SecondaryScreenStatus.Text = "Could not save the second-screen setting. " + ex.Message; }
    }
    private readonly IComputerPowerService computerPower;
    private ComputerPowerAction? pendingPower;
    private bool powerBusy;
    private readonly WindowsStartupService windowsStartup = new(Environment.ProcessPath ?? "");
    private readonly MusicPlayerService music = new();
    private readonly LibraryViewModel viewModel = new();
    private readonly DispatcherTimer previewDelay = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private string? pendingPreviewPath;
    private readonly HashSet<string> failedPreviews = new(StringComparer.OrdinalIgnoreCase);
    private bool CanPlayPreview => !ScreensaverActive && !windowClosed && !launching && !browsingSystems && IsVisible
        && WindowState != WindowState.Minimized && StartupSplash.Visibility != Visibility.Visible && DetailsOverlay.Visibility != Visibility.Visible
        && UtilityOverlay.Visibility != Visibility.Visible && PowerOverlay.Visibility != Visibility.Visible && UpdateOverlay.Visibility != Visibility.Visible;
    private void UpdateGamePreview()
    {
        string? path = viewModel.SelectedGame?.VideoPath;
        if (!CanPlayPreview || path is null || !File.Exists(path) || failedPreviews.Contains(path)) { StopGamePreview(); return; }
        if (GamePreviewVideo.Source?.LocalPath == path || pendingPreviewPath == path) return;
        StopGamePreview(); pendingPreviewPath = path; previewDelay.Start();
    }
    private void StopGamePreview()
    {
        previewDelay.Stop(); pendingPreviewPath = null;
        GamePreviewVideo.BeginAnimation(OpacityProperty, null); GamePreviewVideo.Opacity = 0;
        GamePreviewVideo.Close(); GamePreviewVideo.Source = null;
    }
    private void StartGamePreview()
    {
        previewDelay.Stop(); string? path = pendingPreviewPath; pendingPreviewPath = null;
        if (!CanPlayPreview || path is null || path != viewModel.SelectedGame?.VideoPath || !File.Exists(path)) return;
        GamePreviewVideo.Source = new Uri(path, UriKind.Absolute); GamePreviewVideo.Play();
    }
    private void OnGamePreviewOpened(object sender, RoutedEventArgs e)
    {
        if (!CanPlayPreview || GamePreviewVideo.Source?.LocalPath != viewModel.SelectedGame?.VideoPath) { StopGamePreview(); return; }
        GamePreviewVideo.BeginAnimation(OpacityProperty, new DoubleAnimation(0, .85, TimeSpan.FromMilliseconds(250)));
    }
    private void OnGamePreviewEnded(object sender, RoutedEventArgs e)
    {
        if (!CanPlayPreview || GamePreviewVideo.Source is null) { StopGamePreview(); return; }
        GamePreviewVideo.Position = TimeSpan.Zero; GamePreviewVideo.Play();
    }
    private void OnGamePreviewFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (GamePreviewVideo.Source is { } source) failedPreviews.Add(source.LocalPath);
        StopGamePreview();
    }
    private bool fullscreen;
    private WindowState previousState;
    private enum FocusZone { Consoles, Alphabet, Games, Platforms, Actions, Footer }
    private FocusZone focusZone;
    private readonly DispatcherTimer splashTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly DispatcherTimer libraryRefreshTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool hasLoadedRealLibrary;
    private bool dismissingSplash;
    private string? splashFailure;
    private RetroBatSettings? retroBatSettings;
    private bool launching;
    private bool windowClosed;
    private bool refreshingLibrary;
    private bool browsingSystems = true;
    private bool homeFooter;
    private readonly AutomaticMetadataService automaticMetadata = new(AutomaticMetadataService.DefaultCacheDirectory);
    private readonly CancellationTokenSource metadataCancellation = new();
    private volatile bool metadataRefreshPending;
    private bool applyingMetadataBatch;
    private bool applyingLibrary;
    private string? lastPresentedGame;
    private readonly DispatcherTimer metadataUiTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
    private DateTime lastMetadataBatch = DateTime.MinValue;
    private readonly DispatcherTimer launchStatusTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    public static readonly DependencyProperty CoverCardHeightProperty = DependencyProperty.Register(
        nameof(CoverCardHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(210d));
    public double CoverCardHeight { get => (double)GetValue(CoverCardHeightProperty); set => SetValue(CoverCardHeightProperty, value); }
    public static readonly DependencyProperty SystemCardHeightProperty = DependencyProperty.Register(
        nameof(SystemCardHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(220d));
    public double SystemCardHeight { get => (double)GetValue(SystemCardHeightProperty); set => SetValue(SystemCardHeightProperty, value); }
    public static readonly DependencyProperty SystemConsoleImageHeightProperty = DependencyProperty.Register(
        nameof(SystemConsoleImageHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(160d));
    public double SystemConsoleImageHeight { get => (double)GetValue(SystemConsoleImageHeightProperty); set => SetValue(SystemConsoleImageHeightProperty, value); }
    public MainWindow() : this(new WindowsPowerService()) { }
    public MainWindow(IComputerPowerService computerPower) : this(computerPower, new SecondaryDisplayService()) { }
    public MainWindow(IComputerPowerService computerPower, SecondaryDisplayService secondaryDisplay, AppUpdateService? updateService = null, ControllerInputService? controllerService = null)
    {
        this.controllerInput = controllerService ?? new ControllerInputService();
        this.appUpdates = updateService ?? new AppUpdateService();
        this.computerPower = computerPower;
        this.secondaryDisplay = secondaryDisplay;
        InitializeComponent(); InitializeUpdates(); InitializeControllers(); InitializeRomDownloads(); InitializeScreensaver();
        DataContext = viewModel;
        MusicPanel.DataContext = music;
        NowPlayingDisplay.DataContext = music;
        SourceInitialized += (_, _) => { musicMessageSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle); musicMessageSource?.AddHook(HandleMusicCommand); };
        Loaded += async (_, _) => await music.LoadFolderAsync();
        secondaryDelay.Tick += (_, _) => RefreshSecondaryDisplay();
        Loaded += (_, _) => { Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplayConfigurationChanged; QueueSecondaryDisplay(); };
        LocationChanged += (_, _) => QueueSecondaryDisplay();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LibraryViewModel.SelectedSystem) or nameof(LibraryViewModel.SelectedGame) or nameof(LibraryViewModel.Category)) QueueSecondaryDisplay();
        };
        previewDelay.Tick += (_, _) => StartGamePreview();
        viewModel.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LibraryViewModel.SelectedGame)) UpdateGamePreview(); };
        StateChanged += (_, _) => UpdateGamePreview();
        IsVisibleChanged += (_, _) => { if (IsVisible) UpdateGamePreview(); else StopGamePreview(); };
        launchStatusTimer.Tick += (_, _) =>
        {
            launchStatusTimer.Stop();
            LaunchStatusText.Text = "Still starting… Some games take a little longer.";
        };
        automaticMetadata.MetadataUpdated += () => metadataRefreshPending = true;
        metadataUiTimer.Tick += async (_, _) =>
        {
            UpdateProgressIndicator();
            if (metadataRefreshPending && DateTime.UtcNow - lastMetadataBatch >= TimeSpan.FromSeconds(2))
                await ApplyMetadataBatchAsync();
        };
        Loaded += async (_, _) => { StartSplashVideo(); libraryRefreshTimer.Start(); metadataUiTimer.Start(); await LoadRetroBatLibraryAsync(); };
        libraryRefreshTimer.Tick += async (_, _) => await RefreshLibraryPeriodicallyAsync();
        splashTimer.Tick += (_, _) => DismissSplash();
        Closed += (_, _) => { windowClosed = true; secondaryDelay.Stop(); Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplayConfigurationChanged; secondaryDisplay.Dispose(); musicMessageSource?.RemoveHook(HandleMusicCommand); music.Dispose(); StopGamePreview(); metadataCancellation.Cancel(); splashTimer.Stop(); launchStatusTimer.Stop(); libraryRefreshTimer.Stop(); metadataUiTimer.Stop(); SplashVideo.Close(); };
        GameGrid.PreviewMouseWheel += SmoothScroll;
        SizeChanged += (_, _) =>
        {
            NowPlayingDisplay.Width = ActualWidth < 620 ? 180 : 222;
            FeaturedRow.Height = new GridLength(Math.Clamp(ActualHeight * 0.23, 170, 240));
            CoverCardHeight = Math.Clamp((ActualWidth - 80) / 4 * 1.05 + 42, 145, 220);
            SystemCardHeight = Math.Clamp((ActualHeight - 350) / 2 - 20, 245, 320);
            SystemConsoleImageHeight = Math.Clamp(SystemCardHeight - 150, 95, 180);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!windowClosed && GameGrid.SelectedItem is { } selected) GameGrid.ScrollIntoView(selected);
            }));
        };
        ToggleFullscreenClick(this, new RoutedEventArgs());
    }
    private void StartSplashVideo()
    {
        var videoPath = Path.Combine(Path.GetDirectoryName(typeof(MainWindow).Assembly.Location)!, "Assets", "Arkade-intro.mp4");
        if (!File.Exists(videoPath))
        {
            splashFailure = "Intro video is missing. The library is still available.";
            DismissSplash();
            return;
        }
        // A watchdog prevents an unavailable codec or stalled media from blocking the cabinet.
        splashTimer.Start();
        SplashVideo.Source = new Uri(videoPath, UriKind.Absolute);
        SplashVideo.Play();
    }
    private void OnSplashVideoOpened(object sender, RoutedEventArgs e)
    {
        if (dismissingSplash || windowClosed) return;
        splashTimer.Stop();
        splashTimer.Interval = SplashVideo.NaturalDuration.HasTimeSpan
            ? SplashVideo.NaturalDuration.TimeSpan + TimeSpan.FromSeconds(2)
            : TimeSpan.FromMinutes(2);
        splashTimer.Start();
    }
    private void OnSplashVideoEnded(object sender, RoutedEventArgs e) => DismissSplash();
    private void OnSplashVideoFailed(object sender, ExceptionRoutedEventArgs e)
    {
        splashFailure = "Intro video could not play. The library is still available.";
        DismissSplash();
    }
    private bool CanRefreshInBackground => !ScreensaverActive && !RomBrowserOpen && !windowClosed && !launching && !refreshingLibrary && !applyingMetadataBatch
        && WindowState != WindowState.Minimized && StartupSplash.Visibility != Visibility.Visible && DetailsOverlay.Visibility != Visibility.Visible
        && UtilityOverlay.Visibility != Visibility.Visible && PowerOverlay.Visibility != Visibility.Visible && UpdateOverlay.Visibility != Visibility.Visible;
    private async Task RefreshLibraryPeriodicallyAsync()
    {
        if (CanRefreshInBackground) await LoadRetroBatLibraryAsync(background: true);
    }
    private async Task LoadRetroBatLibraryAsync(bool background = false)
    {
        if (refreshingLibrary || applyingMetadataBatch) return;
        refreshingLibrary = true;
        UpdateProgressIndicator();
        try
        {
            var result = await Task.Run(() =>
            {
                var settings = RetroBatSettings.Load(Path.Combine(Path.GetDirectoryName(typeof(MainWindow).Assembly.Location)!, "retrobat.settings.json"));
                var scan = new RetroBatLibraryService(settings).Scan();
                var cache = Path.Combine(Path.GetDirectoryName(typeof(MainWindow).Assembly.Location)!, "LibraryCache");
                var enriched = automaticMetadata.ApplyCached(new LocalMetadataService(cache).Apply(scan));
                return (Settings: settings, Scan: enriched, Images: new SystemArtworkService(settings.InstallationPath).Resolve(enriched.Games));
            });
            if (windowClosed) return;
            // The user may have opened details or started a game while the worker was scanning.
            if (background && (launching || StartupSplash.Visibility == Visibility.Visible
                || DetailsOverlay.Visibility == Visibility.Visible || UtilityOverlay.Visibility == Visibility.Visible)) return;
            retroBatSettings = result.Settings;
            viewModel.SetSystemImages(result.Images);
            if (result.Settings.AutomaticMetadataEnabled) _ = EnrichLibraryMetadataAsync(result.Scan.Games);
            if (result.Scan.Games.Count > 0 || hasLoadedRealLibrary)
            {
                if (viewModel.MatchesLibrary(result.Scan.Games)) return;
                ApplyLibrarySnapshot(result.Scan.Games);
                hasLoadedRealLibrary = true;
                if (!background) viewModel.Status = result.Scan.Warnings.Count == 0
                    ? "RetroBat library ready. Select a game to play."
                    : $"Library ready with {result.Scan.Warnings.Count} scan warning(s). Some systems may be incomplete.";
            }
            else
            {
                if (background) return;
                viewModel.LibrarySource = "RETROBAT  /  NO GAMES FOUND";
                viewModel.Status = "No RetroBat games found. Showing demo collection.";
            }
        }
        catch (Exception ex) when (MetadataDiagnostics.Recoverable(ex))
        {
            if (background)
            {
                System.Diagnostics.Trace.TraceWarning($"Background library refresh failed: {ex.Message}");
                return;
            }
            viewModel.LibrarySource = "RETROBAT UNAVAILABLE / DEMO";
            viewModel.Status = $"Could not load RetroBat: {ex.Message}";
        }
        finally { refreshingLibrary = false; UpdateProgressIndicator(); }
    }
    private void ApplyLibrarySnapshot(IReadOnlyList<ArcadeOS.Models.Game> games)
    {
        if (viewModel.MatchesLibrary(games)) return;
        var panel = LibrarySurface.Visibility == Visibility.Visible ? FindTilePanel(GameGrid) : null;
        int anchorIndex = panel?.FirstVisibleIndex ?? -1;
        var anchor = anchorIndex >= 0 && anchorIndex < viewModel.Games.Count ? viewModel.Games[anchorIndex] : null;
        double withinRow = panel is null ? 0 : panel.VerticalOffset - anchorIndex / panel.Columns * panel.RowStride;
        applyingLibrary = true;
        try
        {
            viewModel.SetLibrary(games);
            UpdateEmptyLibrary();
            if (panel is not null && anchor is not null)
            {
                int next = -1;
                for (int index = 0; index < viewModel.Games.Count; index++)
                    if (anchor.RomPath is not null
                        ? string.Equals(viewModel.Games[index].RomPath, anchor.RomPath, StringComparison.OrdinalIgnoreCase)
                        : viewModel.Games[index].Title == anchor.Title && viewModel.Games[index].Platform == anchor.Platform)
                    { next = index; break; }
                if (next >= 0)
                {
                    GameGrid.UpdateLayout();
                    panel.SetVerticalOffset(next / panel.Columns * panel.RowStride + withinRow);
                }
            }
        }
        finally { applyingLibrary = false; }
        lastPresentedGame = viewModel.SelectedGame?.RomPath ?? viewModel.SelectedGame?.Title;
    }
    private static ArcadeOS.Controls.VirtualizingTilePanel? FindTilePanel(DependencyObject parent)
    {
        if (parent is ArcadeOS.Controls.VirtualizingTilePanel panel) return panel;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindTilePanel(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        return null;
    }
    private async Task ApplyMetadataBatchAsync()
    {
        if (!CanRefreshInBackground) return;
        applyingMetadataBatch = true;
        metadataRefreshPending = false;
        lastMetadataBatch = DateTime.UtcNow;
        var snapshot = viewModel.GetLibrarySnapshot();
        try
        {
            var updated = await Task.Run(() => automaticMetadata.ApplyCached(new LibraryScanResult(snapshot, [])));
            if (windowClosed) return;
            if (launching || DetailsOverlay.Visibility == Visibility.Visible || UtilityOverlay.Visibility == Visibility.Visible
                || !ReferenceEquals(snapshot, viewModel.GetLibrarySnapshot())) { metadataRefreshPending = true; return; }
            ApplyLibrarySnapshot(updated.Games);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { metadataRefreshPending = true; }
        finally { applyingMetadataBatch = false; UpdateProgressIndicator(); }
    }
    private void UpdateProgressIndicator()
    {
        if (windowClosed) return;
        var progress = automaticMetadata.Progress;
        viewModel.IsUpdating = refreshingLibrary || applyingMetadataBatch || progress.IsRunning;
        viewModel.UpdateMessage = refreshingLibrary ? "Updating library… You can keep browsing."
            : progress.IsRunning ? $"Game info {progress.Completed:N0}/{progress.Total:N0} · covers {progress.Artwork:N0} · {progress.CurrentGame}"
            : applyingMetadataBatch ? "Applying artwork updates…" : "";
        UpdateMetadataSettingsStatus();
    }
    private async Task EnrichLibraryMetadataAsync(IReadOnlyList<ArcadeOS.Models.Game> games, bool retryMissing = false)
    {
        try { await Task.Run(() => automaticMetadata.EnrichAsync(games, metadataCancellation.Token, retryMissing), metadataCancellation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (MetadataDiagnostics.Recoverable(ex))
        { metadataWorkerError = ex.Message; MetadataDiagnostics.Record(AutomaticMetadataService.DefaultCacheDirectory, ex, "Metadata worker"); }
    }
    private async void RefreshLibraryClick(object sender, RoutedEventArgs e)
    {
        if (refreshingLibrary) return;
        RefreshLibraryButton.IsEnabled = false;
        UtilityText.Text = "Scanning game folders…";
        await LoadRetroBatLibraryAsync();
        if (windowClosed) return;
        RefreshLibraryButton.IsEnabled = true;
        UtilityText.Text = viewModel.LibrarySource + "\n\n" + viewModel.Status + "\n\nSelect Back to browse your updated library.";
        RefreshLibraryButton.Focus();
    }
    private async void LaunchGame(object sender, RoutedEventArgs e)
    {
        if (UpdateOverlay.Visibility == Visibility.Visible || launching || retroBatSettings is null || viewModel.SelectedGame is not { CanLaunch: true } game) return;
        StopGamePreview();
        bool recordedPlay = false;
        launching = true; downloadGameWindow = IntPtr.Zero;
        companionRunningGame = game;
        // Hide the companion before starting pinball so it cannot cover a DMD window.
        RefreshSecondaryDisplay();
        music.SetSuspended(true);
        viewModel.LaunchMessage = $"Starting {game.Title}…";
        LaunchPhaseText.Text = "NOW LAUNCHING"; LaunchStatusText.Text = "Preparing your game…";
        LaunchWindowBackdrop.SetBackgroundMode(this, true);
        LaunchSplash.Visibility = Visibility.Visible;
        launchStatusTimer.Start();
        try
        {
            // Let the transition paint before RetroBat begins its launch work.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(150, metadataCancellation.Token);
            int code = await new RetroBatLaunchService(retroBatSettings).LaunchAsync(game, () => { },
                () => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!launching || windowClosed) return;
                    viewModel.RecordPlayed(game); recordedPlay = true;
                    launchStatusTimer.Stop();
                    LaunchStatusText.Text = "Your game is running. Arkade is ready in the background.";
                })), metadataCancellation.Token, handle => Dispatcher.BeginInvoke(new Action(() =>
                { if (launching && !windowClosed) { downloadGameWindow = handle; LaunchWindowBackdrop.PlaceBehindGame(this, handle); } })));
            if (windowClosed) return;
            if (code == 0)
            {
                if (!recordedPlay) viewModel.RecordPlayed(game);
                LaunchWindowBackdrop.SetBackgroundMode(this, false);
                LaunchPhaseText.Text = "RETURNING TO ARKADE"; LaunchStatusText.Text = "Restoring your game library…";
                LaunchWindowBackdrop.RestoreForeground(this);
                await Task.Delay(TimeSpan.FromSeconds(3), metadataCancellation.Token);
                CloseDetails(sender, e);
                viewModel.Status = $"Welcome back. {game.Title} is still selected.";
            }
            else ReportLaunchFailure($"RetroBat exited with code {code}. Back returns to the library.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { ReportLaunchFailure($"Could not start the game: {ex.Message}"); }
        catch (OperationCanceledException) when (windowClosed) { }
        finally
        {
            launching = false; downloadGameWindow = IntPtr.Zero;
            companionRunningGame = null;
            if (!windowClosed) RefreshSecondaryDisplay();
            if (!windowClosed) music.SetSuspended(false);
            launchStatusTimer.Stop();
            LaunchWindowBackdrop.SetBackgroundMode(this, false);
            LaunchSplash.Visibility = Visibility.Collapsed;
            if (!windowClosed)
            {
                await RestoreMenuFocusAsync();
                if (metadataRefreshPending) _ = ApplyMetadataBatchAsync();
                UpdateGamePreview();
            }
        }
    }
    private async Task RestoreMenuFocusAsync()
    {
        foreach (int delay in new[] { 0, 100, 250, 500, 750 })
        {
            if (delay > 0) await Task.Delay(delay);
            if (windowClosed || launching) return;
            if (!LaunchWindowBackdrop.RestoreForeground(this)) continue;
            await Dispatcher.InvokeAsync(() =>
            {
                var target = DetailsOverlay.Visibility == Visibility.Visible ? (IInputElement)LaunchButton : GameGrid;
                Focus(); Keyboard.Focus(target);
            }, DispatcherPriority.Input);
            if (IsActive) return;
        }
    }

    private void DismissSplash()
    {
        if (dismissingSplash) return;
        dismissingSplash = true;
        splashTimer.Stop();
        SplashVideo.Volume = 0;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(350));
        fade.Completed += (_, _) =>
        {
            SplashVideo.Close();
            StartupSplash.Visibility = Visibility.Collapsed;
            QueueSecondaryDisplay();
            SetCategoryMode(false);
            if (splashFailure is not null) viewModel.Status = splashFailure;
            if (metadataRefreshPending) _ = ApplyMetadataBatchAsync();
        };
        StartupSplash.BeginAnimation(OpacityProperty, fade);
    }
    private ScrollViewer? libraryScroller;
    private double scrollTarget;
    public static readonly DependencyProperty AnimatedOffsetProperty = DependencyProperty.Register(
        "AnimatedOffset", typeof(double), typeof(MainWindow), new PropertyMetadata(0d, (owner, args) =>
        ((MainWindow)owner).libraryScroller?.ScrollToVerticalOffset((double)args.NewValue)));
    private void SmoothScroll(object sender, MouseWheelEventArgs e)
    {
        libraryScroller ??= FindScroller(GameGrid);
        if (libraryScroller is null) return;
        if (!HasAnimatedProperties) scrollTarget = libraryScroller.VerticalOffset;
        scrollTarget = Math.Clamp(scrollTarget - e.Delta, 0, libraryScroller.ScrollableHeight);
        BeginAnimation(AnimatedOffsetProperty, new DoubleAnimation(libraryScroller.VerticalOffset, scrollTarget,
            TimeSpan.FromMilliseconds(180)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        e.Handled = true;
    }
    private static ScrollViewer? FindScroller(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScroller(child) is { } nested) return nested;
        }
        return null;
    }
    private void OnConsoleRailSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (browsingSystems || applyingLibrary || launching || ConsoleRail.SelectedItem is not ArcadeOS.Models.SystemTile system) return;
        viewModel.Category = system.Name;
        UpdateEmptyLibrary();
        ConsoleRail.ScrollIntoView(system);
        // Switching a system keeps the joystick in the console rail.
        // The view model restores the last selected game for that system.
    }
    private void OnAlphabetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AlphabetRail.SelectedItem is { } letter) AlphabetRail.ScrollIntoView(letter);
    }
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (applyingLibrary || GameGrid.SelectedItem is not ArcadeOS.Models.Game selected) return;
        string identity = selected.RomPath ?? selected.Title;
        if (identity == lastPresentedGame) return;
        lastPresentedGame = identity;
        BeginAnimation(AnimatedOffsetProperty, null);
        if (GameGrid.SelectedItem is { } game) GameGrid.ScrollIntoView(game);
        FeaturedPanel?.BeginAnimation(OpacityProperty, new DoubleAnimation(0.65, 1, TimeSpan.FromMilliseconds(180)));
    }
    private void ShowDetails(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedGame is null) return;
        StopGamePreview();
        DetailsOverlay.Visibility = Visibility.Visible;
        LibrarySurface.IsEnabled = false;
        viewModel.LaunchMessage = viewModel.SelectedGame.CanLaunch
            ? "Select Play to start. Use the emulator's existing exit shortcut to return here."
            : "This is a demo entry. Back returns to the library.";
        if (viewModel.SelectedGame.CanLaunch) LaunchButton.Focus();
        else CloseDetailsButton.Focus();
    }
    private void CloseDetails(object sender, RoutedEventArgs e)
    {
        DetailsOverlay.Visibility = Visibility.Collapsed;
        UpdateGamePreview();
        LibrarySurface.IsEnabled = true;
        SetZone(FocusZone.Games);
        if (metadataRefreshPending) _ = ApplyMetadataBatchAsync();
    }
    private void ReportLaunchFailure(string message)
    {
        ShowDetails(this, new RoutedEventArgs());
        viewModel.LaunchMessage = message;
    }
    private void SetCategoryMode(bool active) => SetZone(active ? FocusZone.Platforms : FocusZone.Games);
    private void OnSystemSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SystemGrid.SelectedItem is { } system) SystemGrid.ScrollIntoView(system);
    }
    private void OpenSystem(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedSystem is not { } system) return;
        viewModel.Category = system.Name;
        browsingSystems = false;
        homeFooter = false;
        SystemHome.Visibility = Visibility.Collapsed;
        LibrarySurface.Visibility = Visibility.Visible;
        UpdateEmptyLibrary();
        SetZone(FocusZone.Games);
        UpdateGamePreview();
        QueueSecondaryDisplay();
    }
    private void ShowSystemHomeClick(object sender, RoutedEventArgs e) => ShowSystemHome();
    private void ShowSystemHome()
    {
        viewModel.SelectedSystem = viewModel.Systems.FirstOrDefault(system => system.Name == viewModel.Category)
            ?? viewModel.SelectedSystem;
        browsingSystems = true;
        QueueSecondaryDisplay();
        StopGamePreview();
        homeFooter = false;
        LibrarySurface.Visibility = Visibility.Collapsed;
        SystemHome.Visibility = Visibility.Visible;
        SystemGrid.Focus();
        if (viewModel.SelectedSystem is { } system) SystemGrid.ScrollIntoView(system);
    }
    private void UpdateEmptyLibrary()
    {
        bool empty = viewModel.Games.Count == 0;
        EmptyLibraryMessage.Text = viewModel.Category == LibraryViewModel.RecentCategory
            ? "Your next game starts the story.\n\nGames you play will appear here, newest first.\n\nMove left to choose a console."
            : "No games in this system yet.\n\nAdd ROMs to its configured folder. New games appear automatically within 30 seconds while browsing.\n\nPress Back to choose another system.";
        EmptyLibraryMessage.Visibility = EmptySystemHero.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        FeaturedPanel.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }
    private void SetZone(FocusZone zone)
    {
        if (browsingSystems) { SystemGrid.Focus(); return; }
        if (zone == FocusZone.Platforms) { ShowSystemHome(); return; }
        focusZone = zone;
        viewModel.Status = zone switch
        {
            FocusZone.Platforms => "← →  PLATFORM     ↓  GAME ACTIONS     BACK  RETURN",
            FocusZone.Consoles => "↑ ↓  CHANGE SYSTEM     RIGHT / SELECT  LETTERS     BACK  GAME COVERS",
            FocusZone.Alphabet => "LEFT  SYSTEMS     ↑ ↓  LETTER     RIGHT / SELECT  GAME COVERS     BACK  GAME COVERS",
            FocusZone.Actions => "← →  PLAY / DETAILS     ↑  SYSTEMS     ↓  GAME COVERS",
            FocusZone.Footer => "← →  START / HELP / SETTINGS     ↑  GAME COVERS",
            _ => "LEFT AT ROW EDGE  A–Z     JOYSTICK  BROWSE     SELECT  DETAILS     START  PLAY     BACK  SYSTEMS"
        };
        switch (zone)
        {
            case FocusZone.Platforms:
                CategoryPicker.ScrollIntoView(viewModel.Category);
                CategoryPicker.UpdateLayout();
                if (CategoryPicker.ItemContainerGenerator.ContainerFromItem(viewModel.Category) is ListBoxItem tab) tab.Focus();
                else CategoryPicker.Focus();
                break;
            case FocusZone.Consoles:
                ConsoleRail.Focus();
                if (viewModel.SelectedSystem is { } system) ConsoleRail.ScrollIntoView(system);
                break;
            case FocusZone.Alphabet:
                AlphabetRail.Focus();
                if (viewModel.SelectedLetter is { } letter) AlphabetRail.ScrollIntoView(letter);
                break;
            case FocusZone.Actions: (HeroPlayButton.IsEnabled ? HeroPlayButton : HeroDetailsButton).Focus(); break;
            case FocusZone.Footer: (FooterPlayButton.IsEnabled ? FooterPlayButton : HelpButton).Focus(); break;
            default: GameGrid.Focus(); break;
        }
    }
    private void ShowHelp(object sender, RoutedEventArgs e)
    {
        MetadataSettingsPanel.Visibility = Visibility.Collapsed;
        RemoteLibraryButton.Visibility = Visibility.Collapsed;
        ControllerSettingsPanel.Visibility = Visibility.Collapsed;
        UpdateSettingsPanel.Visibility = Visibility.Collapsed;
        SecondaryScreenPanel.Visibility = Visibility.Collapsed;
        WindowsStartupPanel.Visibility = Visibility.Collapsed;
        MusicPanel.Visibility = Visibility.Collapsed;
        UtilityTitle.Text = "CABINET CONTROLS";
        UtilityText.Text = "Choose a system with the joystick, then press SELECT to open its games.\n\nSELECT on a game opens details. START launches the selected game. BACK returns one level: details → games → systems.\n\nMove LEFT from the first column to reach the alphabet. UP / DOWN chooses a letter and jumps to its games; RIGHT or SELECT returns to the covers. Move LEFT again from the alphabet to switch consoles with UP / DOWN. RIGHT or SELECT returns to the alphabet. Each console remembers its selected game.\n\nOnly letters with games appear; # contains numbers and symbols.\n\nMove UP from the first game row to reach Play and Details. Move DOWN from the last row for Help and Settings.\n\nMENU returns to systems; on the home screen it selects the bottom controls.\n\nMusic: media keys or F6 play/pause, F7 next, F8 previous. Choose the folder in Settings. Music pauses during games.\n\nKeyboard: arrows, Enter / Space = Select, 1 = Start, Tab = Menu, Esc = Back. F11 toggles fullscreen.\n\nExit games using your emulator’s existing exit shortcut.";
        FullscreenButton.Visibility = Visibility.Collapsed;
        RefreshLibraryButton.Visibility = Visibility.Collapsed;
        OpenUtility();
        UtilityBackButton.Focus();
    }
    private void ShowSettings(object sender, RoutedEventArgs e)
    {
        MetadataSettingsPanel.Visibility = Visibility.Visible;
        RemoteLibraryButton.Visibility = Visibility.Visible;
        ControllerSettingsPanel.Visibility = Visibility.Visible;
        UpdateSettingsPanel.Visibility = Visibility.Visible; RefreshUpdateStatus();
        SecondaryScreenPanel.Visibility = Visibility.Visible;
        RefreshSecondarySetting();
        WindowsStartupPanel.Visibility = Visibility.Visible;
        RefreshWindowsStartup();
        MusicPanel.Visibility = Visibility.Visible;
        UtilityTitle.Text = "CABINET SETTINGS";
        FullscreenButton.Visibility = Visibility.Visible;
        RefreshLibraryButton.Visibility = Visibility.Visible;
        UpdateDisplayText();
        OpenUtility();
        FullscreenButton.Focus();
    }
    private void UpdateDisplayText() => UtilityText.Text = $"Display mode: {(fullscreen ? "Fullscreen" : "Windowed")}\n\nThe library refreshes automatically every 30 seconds while browsing. Refresh pauses during games and dialogs.\n\nRefresh Library runs a scan now. Keyboard shortcut: F5.\n\nScreensaver: random muted gameplay videos after 5 minutes idle. Any key or controller button returns to the same menu.\n\nJoystick directions choose an action. Select confirms; Back returns.";
    private IntPtr HandleMusicCommand(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0319 && (ScreensaverActive || Environment.TickCount64 < screensaverSuppressUntil))
        { if (ScreensaverActive) StopScreensaver(); handled = true; return new IntPtr(1); }
        if (message != 0x0319 || launching || StartupSplash.Visibility == Visibility.Visible) return IntPtr.Zero;
        int command = (int)((lParam.ToInt64() >> 16) & 0x0fff);
        switch (command)
        {
            case 14: music.Toggle(); break;
            case 11: music.Next(); break;
            case 12: music.Previous(); break;
            case 46: if (!music.IsPlaying) music.Toggle(); break;
            case 47: case 13: if (music.IsPlaying) music.Toggle(); break;
            default: return IntPtr.Zero;
        }
        handled = true; return new IntPtr(1);
    }
    private Button[] PowerButtons => [PowerOffButton, PowerSleepButton, PowerRestartButton, PowerConfirmButton, PowerCancelButton];
    private void ShowPower(object sender, RoutedEventArgs e)
    {
        StopGamePreview(); pendingPower = null;
        PowerChoices.Visibility = Visibility.Visible; PowerConfirmButton.Visibility = Visibility.Collapsed;
        PowerMessage.Text = "Choose what you would like the computer to do."; PowerCancelButton.Content = "BACK TO ARKADE";
        SystemHome.IsEnabled = LibrarySurface.IsEnabled = false;
        PowerOverlay.Visibility = Visibility.Visible; PowerOverlay.UpdateLayout(); PowerCancelButton.Focus();
    }
    private void ChoosePowerOff(object sender, RoutedEventArgs e) => PreparePower(ComputerPowerAction.PowerOff);
    private void ChooseSleep(object sender, RoutedEventArgs e) => PreparePower(ComputerPowerAction.Sleep);
    private void ChooseRestart(object sender, RoutedEventArgs e) => PreparePower(ComputerPowerAction.Restart);
    private void PreparePower(ComputerPowerAction action)
    {
        pendingPower = action;
        PowerChoices.Visibility = Visibility.Collapsed; PowerConfirmButton.Visibility = Visibility.Visible;
        PowerConfirmButton.Content = action switch { ComputerPowerAction.PowerOff => "YES, POWER OFF", ComputerPowerAction.Sleep => "YES, SLEEP", _ => "YES, RESTART" };
        PowerMessage.Text = action switch
        {
            ComputerPowerAction.PowerOff => "Power off this computer? Save any open work before continuing.",
            ComputerPowerAction.Sleep => "Put this computer to sleep? You can wake it using the cabinet's normal wake controls.",
            _ => "Restart this computer? Save any open work before continuing."
        };
        PowerCancelButton.Content = "CANCEL"; PowerOverlay.UpdateLayout(); PowerCancelButton.Focus();
    }
    private void CancelPower(object sender, RoutedEventArgs e)
    {
        if (powerBusy) return;
        pendingPower = null; PowerOverlay.Visibility = Visibility.Collapsed;
        SystemHome.IsEnabled = LibrarySurface.IsEnabled = true;
        if (browsingSystems) { homeFooter = true; HomePowerButton.Focus(); } else SetZone(FocusZone.Games);
        UpdateGamePreview();
    }
    private async void ConfirmPower(object sender, RoutedEventArgs e)
    {
        if (powerBusy || pendingPower is not { } action) return;
        powerBusy = true; PowerOverlay.IsEnabled = false; music.SetSuspended(true);
        PowerMessage.Text = "Sending the confirmed power request to Windows…";
        try
        {
            await computerPower.ExecuteAsync(action);
            viewModel.Status = action == ComputerPowerAction.Sleep ? "Welcome back." : "Power request sent to Windows.";
            powerBusy = false; CancelPower(sender, e);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        { PowerMessage.Text = "Windows could not complete that request. " + ex.Message; pendingPower = null; PowerConfirmButton.Visibility = Visibility.Collapsed; }
        finally { powerBusy = false; PowerOverlay.IsEnabled = true; if (!windowClosed) { music.SetSuspended(false); PowerCancelButton.Focus(); } }
    }
    private void RefreshWindowsStartup()
    {
        try
        {
            WindowsStartupButton.Content = "START WITH WINDOWS: " + (windowsStartup.IsEnabled ? "ON" : "OFF");
            WindowsStartupStatus.Text = "Open Arkade automatically when you sign in to Windows. Applies to this Windows user.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        { WindowsStartupButton.Content = "START WITH WINDOWS"; WindowsStartupStatus.Text = "Windows startup settings could not be read. " + ex.Message; }
    }
    private void WindowsStartupClick(object sender, RoutedEventArgs e)
    {
        try { windowsStartup.SetEnabled(!windowsStartup.IsEnabled); RefreshWindowsStartup(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        { WindowsStartupStatus.Text = "Could not change Windows startup. " + ex.Message; }
    }
    private void MusicAnimationClick(object sender, RoutedEventArgs e) => music.CycleAnimation();
    private void MusicPlayClick(object sender, RoutedEventArgs e) => music.Toggle();
    private void MusicNextClick(object sender, RoutedEventArgs e) => music.Next();
    private void MusicPreviousClick(object sender, RoutedEventArgs e) => music.Previous();
    private void MusicQuieterClick(object sender, RoutedEventArgs e) => music.ChangeVolume(-.1);
    private void MusicLouderClick(object sender, RoutedEventArgs e) => music.ChangeVolume(.1);
    private async void MusicFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose your cabinet music folder", Multiselect = false };
        if (Directory.Exists(music.Folder)) dialog.InitialDirectory = music.Folder;
        if (dialog.ShowDialog(this) == true) await music.LoadFolderAsync(dialog.FolderName);
    }
    private Button[] UtilityButtons => [FullscreenButton, RefreshLibraryButton, RemoteLibraryButton, RetryMetadataButton, CheckUpdatesButton, ControllerSetupButton, WindowsStartupButton, SecondaryScreenButton, MusicPreviousButton, MusicPlayButton, MusicNextButton,
        MusicFolderButton, MusicQuieterButton, MusicLouderButton, MusicAnimationButton, UtilityBackButton];
    private void OpenUtility()
    {
        StopGamePreview();
        LibrarySurface.IsEnabled = false;
        SystemHome.IsEnabled = false;
        UtilityOverlay.Visibility = Visibility.Visible;
    }
    private void CloseUtility(object sender, RoutedEventArgs e)
    {
        UtilityOverlay.Visibility = Visibility.Collapsed;
        UpdateGamePreview();
        LibrarySurface.IsEnabled = true;
        SystemHome.IsEnabled = true;
        if (browsingSystems) { homeFooter = true; HomeHelpButton.Focus(); }
        else SetZone(FocusZone.Footer);
        if (metadataRefreshPending) _ = ApplyMetadataBatchAsync();
    }
    private void ToggleFullscreenClick(object sender, RoutedEventArgs e)
    {
        if (!fullscreen)
        {
            previousState = WindowState;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = previousState;
        }
        fullscreen = !fullscreen;
        if (UtilityOverlay.Visibility == Visibility.Visible && FullscreenButton.Visibility == Visibility.Visible) UpdateDisplayText();
    }
    private static void ClickFocused(params Button[] buttons)
    {
        var button = buttons.FirstOrDefault(b => b.IsKeyboardFocusWithin && b.IsEnabled);
        button?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static void MoveButtonFocus(int delta, params Button[] buttons)
    {
        var enabled = buttons.Where(b => b.IsEnabled && b.IsVisible).ToArray();
        if (enabled.Length == 0) return;
        int current = Array.FindIndex(enabled, b => b.IsKeyboardFocusWithin);
        enabled[(current + delta + enabled.Length) % enabled.Length].Focus();
    }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleScreensaverKey(e)) return;
        if (RomBrowserOpen) { HandleRomKey(e); return; }
        if (ControllerOverlay.Visibility == Visibility.Visible) { HandleControllerSetupKey(e); return; }
        if (UpdateOverlay.Visibility == Visibility.Visible) { HandleUpdateKey(e); return; }
        if (launching) { e.Handled = true; return; }
        if (e.Key == Key.F11) { if (!e.IsRepeat) ToggleFullscreenClick(sender, e); e.Handled = true; return; }
        if (StartupSplash.Visibility == Visibility.Visible)
        {
            if (!e.IsRepeat && e.Key is (Key.Enter or Key.Space or Key.Escape or Key.D1 or Key.NumPad1)) DismissSplash();
            e.Handled = true;
            return;
        }
        if (e.Key is Key.MediaPlayPause or Key.MediaNextTrack or Key.MediaPreviousTrack or Key.F6 or Key.F7 or Key.F8)
        {
            if (!e.IsRepeat)
            {
                if (e.Key is Key.MediaPlayPause or Key.F6) music.Toggle();
                else if (e.Key is Key.MediaNextTrack or Key.F7) music.Next();
                else music.Previous();
            }
            e.Handled = true; return;
        }
        if (e.Key == Key.F5 && PowerOverlay.Visibility != Visibility.Visible)
        {
            if (!e.IsRepeat && DetailsOverlay.Visibility != Visibility.Visible)
            {
                ShowSettings(sender, e);
                RefreshLibraryClick(sender, e);
            }
            e.Handled = true;
            return;
        }
        bool select = e.Key is Key.Enter or Key.Space;
        bool direction = e.Key is Key.Left or Key.Right or Key.Up or Key.Down;
        int step = e.Key is Key.Left or Key.Up ? -1 : 1;
        if (PowerOverlay.Visibility == Visibility.Visible)
        {
            if (!powerBusy)
            {
                if (!e.IsRepeat && e.Key == Key.Escape) CancelPower(sender, e);
                else if (!e.IsRepeat && (select || e.Key is Key.D1 or Key.NumPad1)) ClickFocused(PowerButtons);
                else if (direction || e.Key == Key.Tab) MoveButtonFocus(step, PowerButtons);
            }
            e.Handled = true; return;
        }
        if (UtilityOverlay.Visibility == Visibility.Visible)
        {
            if (!e.IsRepeat && e.Key == Key.Escape) CloseUtility(sender, e);
            else if (!e.IsRepeat && (select || e.Key is Key.D1 or Key.NumPad1)) ClickFocused(UtilityButtons);
            else if (direction || e.Key == Key.Tab) MoveButtonFocus(step, UtilityButtons);
            e.Handled = true;
            return;
        }
        if (DetailsOverlay.Visibility == Visibility.Visible)
        {
            if (!e.IsRepeat && e.Key == Key.Escape) CloseDetails(sender, e);
            else if (!e.IsRepeat && select) ClickFocused(LaunchButton, CloseDetailsButton);
            else if (!e.IsRepeat && e.Key is (Key.D1 or Key.NumPad1)) LaunchGame(sender, e);
            else if (e.Key is Key.Left or Key.Right or Key.Tab) MoveButtonFocus(step, LaunchButton, CloseDetailsButton);
            else if (e.Key == Key.Up) DetailsScroll.LineUp();
            else if (e.Key == Key.Down) DetailsScroll.LineDown();
            e.Handled = true;
            return;
        }
        if (browsingSystems)
        {
            HandleSystemKeys(e, select, direction, step);
            return;
        }
        if (!e.IsRepeat && e.Key is (Key.D1 or Key.NumPad1)) { LaunchGame(sender, e); e.Handled = true; return; }
        if (e.Key == Key.Tab)
        {
            if (!e.IsRepeat) SetZone(focusZone == FocusZone.Platforms ? FocusZone.Games : FocusZone.Platforms);
            e.Handled = true; return;
        }
        if (e.Key == Key.Escape)
        {
            if (focusZone == FocusZone.Consoles || ConsoleRail.IsKeyboardFocusWithin) SetZone(FocusZone.Games);
            else if (focusZone == FocusZone.Alphabet || AlphabetRail.IsKeyboardFocusWithin) SetZone(FocusZone.Games);
            else ShowSystemHome();
            e.Handled = true; return;
        }
        // Mouse focus and cabinet focus share the same navigation path.
        if (ConsoleRail.IsKeyboardFocusWithin) focusZone = FocusZone.Consoles;
        else if (AlphabetRail.IsKeyboardFocusWithin) focusZone = FocusZone.Alphabet;
        else if (GameGrid.IsKeyboardFocusWithin) focusZone = FocusZone.Games;
        else if (CategoryPicker.IsKeyboardFocusWithin) focusZone = FocusZone.Platforms;
        else if (HeroPlayButton.IsKeyboardFocusWithin || HeroDetailsButton.IsKeyboardFocusWithin) focusZone = FocusZone.Actions;
        else if (FooterPlayButton.IsKeyboardFocusWithin || HelpButton.IsKeyboardFocusWithin || SettingsButton.IsKeyboardFocusWithin) focusZone = FocusZone.Footer;
        if (focusZone == FocusZone.Consoles)
        {
            if (e.Key is Key.Up or Key.Down && viewModel.Systems.Count > 0)
            {
                int index = Math.Max(0, ConsoleRail.SelectedIndex);
                viewModel.SelectedSystem = viewModel.Systems[Math.Clamp(index + step, 0, viewModel.Systems.Count - 1)];
            }
            else if (e.Key == Key.Right || select) SetZone(viewModel.Letters.Count > 0 ? FocusZone.Alphabet : FocusZone.Games);
            else if (e.Key == Key.Home && viewModel.Systems.Count > 0) viewModel.SelectedSystem = viewModel.Systems[0];
            else if (e.Key == Key.End && viewModel.Systems.Count > 0) viewModel.SelectedSystem = viewModel.Systems[^1];
        }
        else if (focusZone == FocusZone.Alphabet)
        {
            if (e.Key is Key.Up or Key.Down && viewModel.Letters.Count > 0)
            {
                int index = Math.Max(0, AlphabetRail.SelectedIndex);
                viewModel.SelectedLetter = viewModel.Letters[Math.Clamp(index + step, 0, viewModel.Letters.Count - 1)];
            }
            else if (e.Key == Key.Left) SetZone(FocusZone.Consoles);
            else if (e.Key == Key.Right || select) SetZone(FocusZone.Games);
            else if (e.Key == Key.Home && viewModel.Letters.Count > 0) viewModel.SelectedLetter = viewModel.Letters[0];
            else if (e.Key == Key.End && viewModel.Letters.Count > 0) viewModel.SelectedLetter = viewModel.Letters[^1];
        }
        else if (focusZone == FocusZone.Platforms)
        {
            if (e.Key is Key.Left or Key.Right)
            {
                int index = Array.IndexOf(viewModel.Categories, viewModel.Category);
                viewModel.Category = viewModel.Categories[(index + step + viewModel.Categories.Length) % viewModel.Categories.Length];
                SetZone(FocusZone.Platforms);
            }
            else if (e.Key == Key.Down || select) SetZone(FocusZone.Actions);
        }
        else if (focusZone == FocusZone.Actions)
        {
            if (e.Key is Key.Left or Key.Right) MoveButtonFocus(step, HeroPlayButton, HeroDetailsButton);
            else if (e.Key == Key.Up) SetZone(FocusZone.Platforms);
            else if (e.Key == Key.Down) SetZone(FocusZone.Games);
            else if (!e.IsRepeat && select) ClickFocused(HeroPlayButton, HeroDetailsButton);
        }
        else if (focusZone == FocusZone.Footer)
        {
            if (e.Key is Key.Left or Key.Right) MoveButtonFocus(step, FooterPlayButton, HelpButton, SettingsButton);
            else if (e.Key == Key.Up) SetZone(FocusZone.Games);
            else if (!e.IsRepeat && select) ClickFocused(FooterPlayButton, HelpButton, SettingsButton);
        }
        else if (viewModel.Games.Count == 0)
        {
            if (e.Key == Key.Left) SetZone(FocusZone.Consoles);
            else if (e.Key == Key.Down) SetZone(FocusZone.Footer);
            else if (e.Key == Key.Up) ShowSystemHome();
        }
        else if (viewModel.Games.Count > 0)
        {
            int index = Math.Max(0, GameGrid.SelectedIndex);
            if (e.Key == Key.Left && index % 4 == 0) SetZone(FocusZone.Alphabet);
            else if (e.Key == Key.Up && index < 4) SetZone(FocusZone.Actions);
            else if (e.Key == Key.Down && index / 4 == (viewModel.Games.Count - 1) / 4) SetZone(FocusZone.Footer);
            else if (direction)
            {
                int delta = e.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -4, _ => 4 };
                int target = Math.Clamp(index + delta, 0, viewModel.Games.Count - 1);
                if (e.Key is Key.Left or Key.Right && index / 4 != target / 4) target = index;
                viewModel.SelectedGame = viewModel.Games[target];
                GameGrid.Focus();
            }
            else if (!e.IsRepeat && select) ShowDetails(sender, e);
            else if (e.Key == Key.Home) viewModel.SelectedGame = viewModel.Games[0];
            else if (e.Key == Key.End) viewModel.SelectedGame = viewModel.Games[^1];
        }
        if (direction || select || e.Key is Key.Home or Key.End) e.Handled = true;
    }
    private void HandleSystemKeys(KeyEventArgs e, bool select, bool direction, int step)
    {
        bool start = e.Key is Key.D1 or Key.NumPad1;
        if (e.Key == Key.Tab)
        {
            if (!e.IsRepeat)
            {
                homeFooter = !homeFooter;
                if (homeFooter) OpenSystemButton.Focus(); else SystemGrid.Focus();
            }
        }
        else if (e.Key == Key.Escape) { homeFooter = false; SystemGrid.Focus(); }
        else if (homeFooter || OpenSystemButton.IsKeyboardFocusWithin || HomeHelpButton.IsKeyboardFocusWithin || HomeSettingsButton.IsKeyboardFocusWithin || HomePowerButton.IsKeyboardFocusWithin)
        {
            homeFooter = true;
            if (e.Key is Key.Left or Key.Right) MoveButtonFocus(step, OpenSystemButton, HomeHelpButton, HomeSettingsButton, HomePowerButton);
            else if (e.Key == Key.Up) { homeFooter = false; SystemGrid.Focus(); }
            else if (!e.IsRepeat && (select || start)) ClickFocused(OpenSystemButton, HomeHelpButton, HomeSettingsButton, HomePowerButton);
        }
        else if (!e.IsRepeat && (select || start)) OpenSystem(this, e);
        else if (viewModel.Systems.Count > 0)
        {
            int index = Math.Max(0, SystemGrid.SelectedIndex);
            if (e.Key == Key.Down && index / 2 == (viewModel.Systems.Count - 1) / 2)
            { homeFooter = true; OpenSystemButton.Focus(); }
            else if (direction)
            {
                int delta = e.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -2, _ => 2 };
                int target = Math.Clamp(index + delta, 0, viewModel.Systems.Count - 1);
                if (e.Key is Key.Left or Key.Right && index / 2 != target / 2) target = index;
                viewModel.SelectedSystem = viewModel.Systems[target];
                SystemGrid.Focus();
            }
            else if (e.Key == Key.Home) viewModel.SelectedSystem = viewModel.Systems[0];
            else if (e.Key == Key.End) viewModel.SelectedSystem = viewModel.Systems[^1];
        }
        e.Handled = direction || select || start || e.Key is Key.Tab or Key.Escape or Key.Home or Key.End;
    }
}
