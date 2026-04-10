using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Data.Contracts;
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

    private MainWindow()
    {
        InitializeComponent();

        WindowHandle = this.GetWindowHandle();

        Closed += OnClosed;
        Activated += OnActivatedForMemoryRelease;
        VisibilityChanged += OnVisibilityChangedForMemoryRelease;

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
        if (_memoryReleaseTimer != null)
        {
            _memoryReleaseTimer.Stop();
            _memoryReleaseTimer.Tick -= OnMemoryReleaseTick;
            _memoryReleaseTimer = null;
        }

        var shellVm = Ioc.Default.GetService<Wavee.UI.WinUI.ViewModels.ShellViewModel>();
        shellVm?.Cleanup();

        // Force save settings on exit
        var settings = Ioc.Default.GetService<ISettingsService>();
        if (settings is not null)
        {
            await settings.SaveAsync();
        }
    }

    // ── Memory release on minimize / lose focus ──────────────────────────
    //
    // Window.Activated fires constantly during normal use (any click outside the
    // window deactivates it briefly), so a long debounce + a "release once per
    // background session" guard keeps us out of the way of normal alt-tabbing.
    //
    // Window.VisibilityChanged fires on actual minimize / hide-to-tray, which is
    // a much stronger signal that the user genuinely isn't looking at us, so it
    // gets a much shorter debounce.

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
