namespace Wavee.UI.WinUI.Controls.Cards;

/// <summary>
/// Lazy-realization helpers for <see cref="BaselineHomeCard"/>: the small
/// <see cref="Microsoft.UI.Xaml.FrameworkElement.FindName(string)"/> wrappers
/// that drive <c>x:Load="False"</c> subtree realization on demand.
///
/// <para>Centralising these here keeps the realization decisions next to each
/// other, and makes it easy to inspect at a glance which named subtrees are
/// deferred — hover chrome, canvas preview host, prev/next preview buttons,
/// shimmer, and the preview visualiser. The shapes that aren't deferred
/// (TitleText, SubtitleText, CoverThumbImage, …) are accessed directly via
/// the generated XAML backing fields.</para>
/// </summary>
public sealed partial class BaselineHomeCard
{
    private void EnsureHoverChromeRealized()
    {
        if (HoverChrome != null)
            return;

        _ = FindName(nameof(HoverChrome));
    }

    private void EnsureCanvasPreviewHostRealized()
    {
        if (CanvasPreviewHost != null)
            return;

        _ = FindName(nameof(CanvasPreviewHost));
    }

    private void EnsurePreviewNavigationButtonsRealized()
    {
        if (PreviousPreviewTrackButton == null)
            _ = FindName(nameof(PreviousPreviewTrackButton));
        if (NextPreviewTrackButton == null)
            _ = FindName(nameof(NextPreviewTrackButton));
    }

    private void EnsureShimmerRealized()
    {
        if (ShimmerOverlay != null)
            return;

        _ = FindName(nameof(ShimmerOverlay));
    }

    private void EnsurePreviewVisualizerRealized()
    {
        if (PreviewVisualizer != null)
            return;

        _ = FindName(nameof(PreviewOverlayRoot));
    }

    private void EnsureContextPlayButtonRealized()
    {
        if (ContextPlayButton != null)
            return;
        _ = FindName(nameof(ContextPlayButton));
    }
}
