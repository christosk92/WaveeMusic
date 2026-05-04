using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.SidebarPlayer;

/// <summary>
/// Floating row of on-device-AI affordances + result panel that mounts above
/// the lyrics column on the expanded now-playing view.
///
/// Resolves <see cref="LyricsAiPanelViewModel"/> from the IoC container on
/// construction so XAML callers can drop the control in without thinking
/// about VM wiring. Disposes the VM when unloaded so cancellation tokens and
/// PropertyChanged subscriptions don't leak.
///
/// Code-behind also drives:
///   - Compact/expanded result-card sizing, including the height transition.
///   - Footer label/glyph swap to match the current expansion state.
///   - Auto-scroll the result ScrollViewer to bottom as streamed deltas
///     append to ResultText, so the latest tokens stay visible.
/// </summary>
public sealed partial class LyricsAiPanel : UserControl
{
    private const double CompactResultCardWidth = 368;
    private const double CompactResultCardHeight = 240;
    private const double ExpandedResultCardMaxWidth = 4000;
    private const double ExpandedResultCardMaxHeight = 4000;
    private const double ExpansionAnimationMilliseconds = 260;

    private Storyboard? _resultCardHeightStoryboard;
    private bool _expandedCardHeightUpdateQueued;

    public LyricsAiPanelViewModel ViewModel { get; private set; }

    public LyricsAiPanel()
    {
        // GetService (not GetRequiredService) so a unit-test or design-time
        // host that hasn't registered the AI services can still instantiate
        // the control without throwing.
        var vm = Ioc.Default.GetService<LyricsAiPanelViewModel>();
        ViewModel = vm ?? throw new InvalidOperationException(
            $"{nameof(LyricsAiPanelViewModel)} not registered in the DI container.");

        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        SizeChanged += OnSizeChanged;
        ApplyExpansionState(animate: false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        SizeChanged -= OnSizeChanged;
        ViewModel?.Dispose();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_resultCardHeightStoryboard is not null)
            return;

        if (ViewModel.IsResultExpanded)
        {
            StopHeightAnimation();
            ResultCard.Height = GetExpandedCardHeight();
            ResultCard.MaxHeight = ExpandedResultCardMaxHeight;
        }
        else
        {
            ApplyCompactCardDimensions();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LyricsAiPanelViewModel.IsResultExpanded):
                ApplyExpansionState(animate: true);
                break;
            case nameof(LyricsAiPanelViewModel.ResultText):
                QueueExpandedCardHeightUpdate();
                ScrollResultToBottom();
                break;
            case nameof(LyricsAiPanelViewModel.HasResult):
                if (!ViewModel.HasResult) break;
                // Fresh result starting — ensure scroll position resets so
                // streaming begins from the top of the body.
                ResultScrollViewer?.ChangeView(null, 0, null, disableAnimation: true);
                break;
        }
    }

    private void ApplyExpansionState(bool animate)
    {
        StopHeightAnimation();

        var fromHeight = GetCurrentCardHeight();
        var toHeight = ViewModel.IsResultExpanded
            ? GetExpandedCardHeight()
            : CompactResultCardHeight;

        if (ViewModel.IsResultExpanded)
        {
            ResultCard.Width = double.NaN;
            ResultCard.Height = fromHeight;
            ResultCard.MaxWidth = ExpandedResultCardMaxWidth;
            ResultCard.MaxHeight = Math.Min(ExpandedResultCardMaxHeight, Math.Max(fromHeight, toHeight));
            ResultCard.HorizontalAlignment = HorizontalAlignment.Stretch;
            ResultCard.VerticalAlignment = VerticalAlignment.Top;
        }
        else
        {
            ResultCard.Width = GetCompactCardWidth();
            ResultCard.Height = fromHeight;
            ResultCard.MaxWidth = CompactResultCardWidth;
            ResultCard.MaxHeight = Math.Max(fromHeight, toHeight);
            ResultCard.HorizontalAlignment = HorizontalAlignment.Right;
            ResultCard.VerticalAlignment = VerticalAlignment.Top;
        }

        if (animate && Math.Abs(fromHeight - toHeight) > 1)
            AnimateCardHeight(fromHeight, toHeight);
        else
            CompleteCardHeightChange(toHeight);
    }

    private void ApplyCompactCardDimensions()
    {
        ResultCard.Width = GetCompactCardWidth();
        ResultCard.Height = CompactResultCardHeight;
        ResultCard.MaxWidth = CompactResultCardWidth;
        ResultCard.MaxHeight = CompactResultCardHeight;
    }

    private double GetCompactCardWidth()
    {
        var availableWidth = ActualWidth - ResultCard.Margin.Left - ResultCard.Margin.Right;
        return availableWidth > 0
            ? Math.Min(CompactResultCardWidth, availableWidth)
            : CompactResultCardWidth;
    }

    private double GetCurrentCardHeight()
    {
        if (ResultCard.ActualHeight > 0)
            return ResultCard.ActualHeight;

        if (!double.IsNaN(ResultCard.Height) && ResultCard.Height > 0)
            return ResultCard.Height;

        return CompactResultCardHeight;
    }

    private double GetExpandedCardHeight()
    {
        var containerHeight = RootGrid.ActualHeight > 0 ? RootGrid.ActualHeight : ActualHeight;
        var top = AiActionsRow.ActualHeight
                  + AiActionsRow.Margin.Top
                  + AiActionsRow.Margin.Bottom
                  + ResultCard.Margin.Top;

        try
        {
            top = ResultCard.TransformToVisual(RootGrid).TransformPoint(new Point(0, 0)).Y;
        }
        catch
        {
            // Transform is unavailable during the first layout pass.
        }

        var availableHeight = containerHeight - top - ResultCard.Margin.Bottom;
        var maxHeight = availableHeight > CompactResultCardHeight
            ? availableHeight
            : CompactResultCardHeight;

        return maxHeight;
    }

    private void AnimateCardHeight(double from, double to)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(ExpansionAnimationMilliseconds)),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        Storyboard.SetTarget(animation, ResultCard);
        Storyboard.SetTargetProperty(animation, nameof(Height));

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_resultCardHeightStoryboard, storyboard))
                return;

            _resultCardHeightStoryboard = null;
            CompleteCardHeightChange(to);
        };

        _resultCardHeightStoryboard = storyboard;
        storyboard.Begin();
    }

    private void CompleteCardHeightChange(double height)
    {
        ResultCard.Height = height;
        ResultCard.MaxHeight = ViewModel.IsResultExpanded
            ? ExpandedResultCardMaxHeight
            : CompactResultCardHeight;
    }

    private void StopHeightAnimation()
    {
        if (_resultCardHeightStoryboard is null)
            return;

        _resultCardHeightStoryboard.Stop();
        _resultCardHeightStoryboard = null;
    }

    private void QueueExpandedCardHeightUpdate()
    {
        if (!ViewModel.IsResultExpanded || _expandedCardHeightUpdateQueued)
            return;

        _expandedCardHeightUpdateQueued = true;
        if (this.DispatcherQueue?.TryEnqueue(() =>
        {
            _expandedCardHeightUpdateQueued = false;
            if (!ViewModel.IsResultExpanded || !ViewModel.HasResult || _resultCardHeightStoryboard is not null)
                return;

            var targetHeight = GetExpandedCardHeight();
            if (Math.Abs(GetCurrentCardHeight() - targetHeight) <= 1)
                return;

            ResultCard.Height = targetHeight;
            ResultCard.MaxHeight = ExpandedResultCardMaxHeight;
        }) != true)
        {
            _expandedCardHeightUpdateQueued = false;
        }
    }

    private void ScrollResultToBottom()
    {
        if (ResultScrollViewer is null) return;
        // Defer to the next layout pass so the new text has been measured
        // before we attempt to scroll past its bottom.
        this.DispatcherQueue?.TryEnqueue(() =>
        {
            var extent = ResultScrollViewer.ExtentHeight;
            if (extent > 0)
                ResultScrollViewer.ChangeView(null, extent, null, disableAnimation: true);
        });
    }
}
