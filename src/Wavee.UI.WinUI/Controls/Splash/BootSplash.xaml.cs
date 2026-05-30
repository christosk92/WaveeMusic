using System;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Wavee.UI.WinUI.Controls.Splash;

public sealed partial class BootSplash : UserControl
{
    private const int FadeDurationMs = 300;
    private const int ProgressRingShowAfterMs = 700;

    private bool _faded;

    public BootSplash()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
        => _ = ShowRingAfterDelayAsync();

    private async Task ShowRingAfterDelayAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(ProgressRingShowAfterMs));
        if (_faded || !IsLoaded) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_faded) return;
            PART_Ring.Visibility = Visibility.Visible;
        });
    }

    public Task FadeOutAsync()
    {
        if (_faded) return Task.CompletedTask;
        _faded = true;

        var visual = ElementCompositionPreview.GetElementVisual(this);
        var compositor = visual.Compositor;

        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0f, 1f);
        animation.InsertKeyFrame(1f, 0f);
        animation.Duration = TimeSpan.FromMilliseconds(FadeDurationMs);

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation("Opacity", animation);
        batch.End();

        var tcs = new TaskCompletionSource();
        batch.Completed += (_, _) =>
            DispatcherQueue.TryEnqueue(() =>
            {
                Visibility = Visibility.Collapsed;
                tcs.TrySetResult();
            });
        return tcs.Task;
    }
}