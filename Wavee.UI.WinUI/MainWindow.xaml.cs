using System;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Controls;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Views;
using WinUIEx;

namespace Wavee.UI.WinUI;

public sealed partial class MainWindow : WindowEx
{
    private static MainWindow? _instance;
    public static MainWindow Instance => _instance ??= new();

    public nint WindowHandle { get; }

    // Memory release on minimize / lose focus.
    // Single timer instance, restarted on each event so we naturally debounce.
    // _alreadyReleasedSinceLastFocus prevents hammering the GC if the user leaves
    // the window deactivated for a long time — we release once, then wait for them
    // to come back before we're allowed to release again.
    private DispatcherQueueTimer? _memoryReleaseTimer;
    private bool _alreadyReleasedSinceLastFocus;
    private ILogger? _memoryReleaseLogger;
    private bool _allowWindowClose;
    private bool _closeConfirmationInFlight;

    private MainWindow()
    {
        InitializeComponent();

        WindowHandle = this.GetWindowHandle();

        Closed += OnClosed;
        Activated += OnActivatedForMemoryRelease;
        VisibilityChanged += OnVisibilityChangedForMemoryRelease;
        AppWindow.Changed += OnAppWindowChangedForMemoryRelease;
        AppWindow.Closing += OnAppWindowClosing;

        // Extend content into titlebar
        ExtendsContentIntoTitleBar = true;

        // Make titlebar buttons blend with content
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(20, 128, 128, 128);
        AppWindow.TitleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(40, 128, 128, 128);
        // Let Windows auto-pick foreground based on theme (don't set = auto)
        AppWindow.TitleBar.ButtonForegroundColor = null;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = null;
    }

    private async void OnClosed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
    {
        Activated -= OnActivatedForMemoryRelease;
        VisibilityChanged -= OnVisibilityChangedForMemoryRelease;
        AppWindow.Changed -= OnAppWindowChangedForMemoryRelease;
        AppWindow.Closing -= OnAppWindowClosing;
        if (_memoryReleaseTimer != null)
        {
            _memoryReleaseTimer.Stop();
            _memoryReleaseTimer.Tick -= OnMemoryReleaseTick;
            _memoryReleaseTimer = null;
        }

        var shellVm = Ioc.Default.GetService<Wavee.UI.WinUI.ViewModels.ShellViewModel>();
        shellVm?.Cleanup();

        try
        {
            await Helpers.Application.AppLifecycleHelper.TeardownPlaybackEngineAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Playback teardown failed during window close: {ex}");
        }

        try
        {
            await App.ShutdownHostAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Host shutdown failed during window close: {ex}");
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowWindowClose)
            return;

        var shellSession = Ioc.Default.GetService<IShellSessionService>();
        if (shellSession == null)
            return;

        if (!shellSession.AskBeforeClosingTabs)
        {
            ApplyCloseTabsBehavior(shellSession.CloseTabsBehavior);
            return;
        }

        args.Cancel = true;

        if (_closeConfirmationInFlight)
            return;

        _closeConfirmationInFlight = true;
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await ConfirmCloseAsync(shellSession);
            }
            finally
            {
                _closeConfirmationInFlight = false;
            }
        });
    }

    private async Task ConfirmCloseAsync(IShellSessionService shellSession)
    {
        if (RootFrame.XamlRoot == null)
            return;

        CloseTabsDialogResult result;
        try
        {
            result = await CloseTabsDialog.ShowAsync(RootFrame.XamlRoot, shellSession.AskBeforeClosingTabs);
        }
        catch
        {
            return;
        }

        if (result.Choice == CloseTabsDialogChoice.Cancel)
            return;

        var behavior = result.Choice == CloseTabsDialogChoice.Save
            ? CloseTabsBehavior.Save
            : CloseTabsBehavior.Discard;

        shellSession.UpdateClosePreference(result.AlwaysAsk, behavior);
        ApplyCloseTabsBehavior(behavior);

        _allowWindowClose = true;
        Close();
    }

    private static void ApplyCloseTabsBehavior(CloseTabsBehavior behavior)
    {
        var shellVm = Ioc.Default.GetService<ViewModels.ShellViewModel>();
        var shellSession = Ioc.Default.GetService<IShellSessionService>();
        if (shellSession == null)
            return;

        if (behavior == CloseTabsBehavior.Save)
            shellVm?.PersistTabSession();
        else
            shellSession.ClearTabs();
    }

    // ── Memory release on minimize / lose focus ──────────────────────────
    //
    // Window.Activated fires constantly during normal use (any click outside the
    // window deactivates it briefly), so a long debounce + a "release once per
    // background session" guard keeps us out of the way of normal alt-tabbing.
    //
    // AppWindow.Changed is the reliable minimize signal in WinUI 3. VisibilityChanged
    // is kept as a fallback for hide-to-tray / explicit hide paths.

    private static readonly TimeSpan MinimizeReleaseDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan DeactivateReleaseDelay = TimeSpan.FromSeconds(30);

    private void OnActivatedForMemoryRelease(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            ScheduleMemoryRelease(DeactivateReleaseDelay, "deactivated");
        }
        else
        {
            CancelPendingRelease();
            _alreadyReleasedSinceLastFocus = false;
        }
    }

    private void OnVisibilityChangedForMemoryRelease(object sender, WindowVisibilityChangedEventArgs args)
    {
        if (!args.Visible)
            ScheduleMemoryRelease(MinimizeReleaseDelay, "minimized");
        else
            CancelPendingRelease();
    }

    private void OnAppWindowChangedForMemoryRelease(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
        {
            ScheduleMemoryRelease(MinimizeReleaseDelay, "minimized");
        }
        else if (_pendingReleaseReason == "minimized")
        {
            CancelPendingRelease();
            _alreadyReleasedSinceLastFocus = false;
        }
    }

    private void ScheduleMemoryRelease(TimeSpan delay, string reason)
    {
        if (_alreadyReleasedSinceLastFocus) return;

        _memoryReleaseLogger ??= Ioc.Default.GetService<ILogger<MainWindow>>();
        _memoryReleaseTimer ??= DispatcherQueue.CreateTimer();
        _memoryReleaseTimer.Interval = delay;
        _memoryReleaseTimer.IsRepeating = false;

        _memoryReleaseTimer.Stop();
        _memoryReleaseTimer.Tick -= OnMemoryReleaseTick;
        _memoryReleaseTimer.Tick += OnMemoryReleaseTick;
        _pendingReleaseReason = reason;
        _memoryReleaseTimer.Start();
    }

    private string _pendingReleaseReason = "";

    private void OnMemoryReleaseTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        sender.Tick -= OnMemoryReleaseTick;
        _alreadyReleasedSinceLastFocus = true;
        MemoryReleaseHelper.ReleaseWorkingSet(_memoryReleaseLogger, _pendingReleaseReason);
    }

    private void CancelPendingRelease()
    {
        if (_memoryReleaseTimer == null) return;
        _memoryReleaseTimer.Stop();
        _memoryReleaseTimer.Tick -= OnMemoryReleaseTick;
    }

    public async Task InitializeApplicationAsync()
    {
        // Load persistent settings
        var settingsService = Ioc.Default.GetRequiredService<ISettingsService>();
        await settingsService.LoadAsync();
        App.AppModel.InitializeFromSettings();

        // Initialize color service (before theme, so brushes are cached early)
        var colorService = Ioc.Default.GetRequiredService<ThemeColorService>();
        colorService.Initialize(RootFrame);

        // Initialize theme service
        var themeService = Ioc.Default.GetRequiredService<IThemeService>();
        themeService.Initialize(RootFrame);

        // Always navigate to ShellPage (app works without Spotify login)
        RootFrame.Navigate(typeof(ShellPage));

        // Show "What's New" dialog once the shell has rendered
        _ = ShowWhatsNewAfterDelayAsync(settingsService);

        // Try to restore cached Spotify session in background (non-blocking)
        var authState = Ioc.Default.GetRequiredService<IAuthState>();
        _ = Task.Run(async () =>
        {
            try
            {
                await authState.TryRestoreSessionAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Session restore failed: {ex}");
                // Not critical -- user can connect manually via sidebar button
            }
        });
    }

    public async Task RestartApplicationAsync()
    {
        var settingsService = Ioc.Default.GetService<ISettingsService>();
        if (settingsService is not null)
        {
            await settingsService.SaveAsync();
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            processPath = Process.GetCurrentProcess().MainModule?.FileName;
        }

        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("Could not determine the current process path for restart.");
        }

        Process.Start(new ProcessStartInfo(processPath)
        {
            UseShellExecute = true,
            WorkingDirectory = AppContext.BaseDirectory
        });

        Application.Current.Exit();
    }

    private async Task ShowWhatsNewAfterDelayAsync(ISettingsService settingsService)
    {
        try
        {
            // Wait for the visual tree to fully materialize
            await Task.Delay(1500);
            if (Content?.XamlRoot is not { } xamlRoot) return;

            await Controls.WhatsNewDialog.ShowIfNeededAsync(
                xamlRoot,
                settingsService,
                Ioc.Default.GetRequiredService<IUpdateService>());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"What's New dialog failed: {ex}");
        }
    }
}
