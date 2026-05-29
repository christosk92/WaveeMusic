using System;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wavee.UI.WinUI.Controls.Playback;

/// <summary>
/// Browser-style transient zoom HUD. Top-center pill that shows the
/// current zoom percent + inline <c>−</c> / <c>+</c> / <c>Reset</c>
/// buttons. <see cref="ShowFor"/> reveals the pill (or refreshes the
/// percent if already visible) and (re)starts a 2-second auto-dismiss
/// timer. Clicking any inline button restarts the timer too, so a user
/// who's actively tweaking the zoom keeps the HUD on screen.
///
/// <para>The HUD does NOT own the zoom logic — it raises
/// <see cref="ZoomInRequested"/> / <see cref="ZoomOutRequested"/> /
/// <see cref="ResetRequested"/>; <c>ShellPage</c> routes those into the
/// shared <c>StepZoomIndex</c> / <c>ResetZoomIndex</c> helpers so the
/// same code path runs for keyboard, HUD click, and Settings UI.</para>
/// </summary>
public sealed partial class ZoomHud : UserControl
{
    private static readonly TimeSpan AutoDismissDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FadeInDuration = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan FadeOutDuration = TimeSpan.FromMilliseconds(180);

    private readonly DispatcherTimer _autoDismissTimer;
    private bool _isShowing;

    public event EventHandler? ZoomInRequested;
    public event EventHandler? ZoomOutRequested;
    public event EventHandler? ResetRequested;

    public ZoomHud()
    {
        InitializeComponent();
        _autoDismissTimer = new DispatcherTimer { Interval = AutoDismissDelay };
        _autoDismissTimer.Tick += OnAutoDismissTick;
    }

    /// <summary>
    /// Show the HUD with the given zoom value (e.g. 1.1 → "110%"). Safe
    /// to call repeatedly — the second-plus call just updates the percent
    /// and restarts the dismiss timer, no animation re-trigger if already
    /// visible.
    /// </summary>
    public void ShowFor(double zoom)
    {
        PercentText.Text = $"{(int)Math.Round(zoom * 100)}%";

        if (!_isShowing)
        {
            _isShowing = true;
            Visibility = Visibility.Visible;
            IsHitTestVisible = true;
            AnimationBuilder.Create()
                .Opacity(from: 0, to: 1, duration: FadeInDuration)
                .Translation(axis: Axis.Y, from: -8, to: 0, duration: FadeInDuration)
                .Start(HudPill);
        }

        RestartAutoDismiss();
    }

    private void RestartAutoDismiss()
    {
        _autoDismissTimer.Stop();
        _autoDismissTimer.Start();
    }

    private void OnAutoDismissTick(object? sender, object e)
    {
        _autoDismissTimer.Stop();
        if (!_isShowing) return;

        _isShowing = false;
        IsHitTestVisible = false;
        AnimationBuilder.Create()
            .Opacity(to: 0, duration: FadeOutDuration)
            .Translation(axis: Axis.Y, to: -8, duration: FadeOutDuration)
            .Start(HudPill);

        // Collapse after the fade completes so a future ShowFor starts
        // from a clean Visibility=Collapsed → Visible transition.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!_isShowing) Visibility = Visibility.Collapsed;
        });
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        ZoomInRequested?.Invoke(this, EventArgs.Empty);
        RestartAutoDismiss();
    }

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        ZoomOutRequested?.Invoke(this, EventArgs.Empty);
        RestartAutoDismiss();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        ResetRequested?.Invoke(this, EventArgs.Empty);
        RestartAutoDismiss();
    }
}
