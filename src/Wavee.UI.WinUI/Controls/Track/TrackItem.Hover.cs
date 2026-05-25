using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Wavee.UI.WinUI.Controls.Track;

/// <summary>
/// Partial-class extension on <see cref="TrackItem"/> implementing the hover
/// reveal layer: pointer-enter / pointer-exit tracking, hover-tinted background
/// fills, alternating-row + selected visual states, and the cached transparent
/// brush re-used across hover transitions.
///
/// Lives on the same class for perf — virtualized track rows can churn through
/// pointer events at high rate, and an external <c>Behavior&lt;T&gt;</c> would
/// add one instance per realized row plus an extra attached-property hop on each
/// state change. The hover code also reads and writes per-row fields owned by
/// other partials (<c>_isHovered</c>, <c>_themeColors</c>, the selection
/// indicators) which would all need DP plumbing to expose externally.
/// </summary>
public sealed partial class TrackItem
{
    #region Hover

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isHovered = true;

        if (Mode == TrackItemDisplayMode.Compact)
        {
            ApplyCompactBackground();
        }
        else
        {
            ApplyRowBackground();
        }

        UpdateOverlayState();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        ResetHoverVisualState();
        UpdateOverlayState();
    }

    private void ResetHoverVisualState()
    {
        _isHovered = false;

        if (Mode == TrackItemDisplayMode.Compact)
        {
            ApplyCompactBackground();
        }
        else
        {
            ApplyRowBackground();
        }
    }

    private void ApplyCompactBackground()
    {
        if (CompactBorder == null) return;

        if (IsSelected)
        {
            CompactBorder.Background = _isHovered
                ? (_themeColors?.GetBrush("ListViewItemBackgroundSelectedPointerOver")
                   ?? (Brush)Application.Current.Resources["ListViewItemBackgroundSelectedPointerOver"])
                : (_themeColors?.GetBrush("ListViewItemBackgroundSelected")
                   ?? (Brush)Application.Current.Resources["ListViewItemBackgroundSelected"]);
            CompactBorder.BorderBrush = _themeColors?.AccentFill
                ?? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            CompactBorder.BorderThickness = new Thickness(1.5);
        }
        else if (_isHovered)
        {
            CompactBorder.Background = _themeColors?.CardBackground
                ?? (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
            CompactBorder.BorderBrush = _themeColors?.GetBrush("CardStrokeColorDefaultBrush")
                ?? (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
            CompactBorder.BorderThickness = new Thickness(1);
        }
        else
        {
            CompactBorder.Background = TransparentBrush;
            CompactBorder.BorderBrush = TransparentBrush;
            CompactBorder.BorderThickness = new Thickness(1);
        }
    }

    private void ApplyRowBackground()
    {
        if (RowRoot == null) return;

        bool nativePillShowing = IsSelected || _isHovered;

        // Opt-in hover-tint: paint the configured hover brush and short-circuit.
        // Border collapses to invisible so the hover slab reads as a single block.
        if (_isHovered && !IsSelected && RowHoverBackgroundBrush is not null)
        {
            RowRoot.Background = RowHoverBackgroundBrush;
            RowRoot.BorderThickness = new Thickness(1);
            RowRoot.BorderBrush = TransparentBrush;
            return;
        }

        if (!nativePillShowing && (_useCardRow || _isAlternateRow))
        {
            // SubtleFill keeps alternating-row striping visible without
            // turning dark-mode rows into light slabs after a theme switch.
            RowRoot.Background = _isAlternateRow
                ? _themeColors?.SubtleFillSecondary
                  ?? (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"]
                : DefaultBackground;
        }
        else
        {
            RowRoot.Background = DefaultBackground;
        }

        // BorderThickness is always 1 — only the BorderBrush colour changes
        // between visible (alternating-row card stroke) and invisible
        // (transparent). Toggling the THICKNESS instead would add / remove
        // 2 px from the row's outer bounds on hover, shifting every cell's
        // inner content by 1 px and producing the visible flicker the user
        // reported. Keep the geometry stable; only repaint.
        RowRoot.BorderThickness = new Thickness(1);
        if (!nativePillShowing && _isAlternateRow)
        {
            RowRoot.BorderBrush = _themeColors?.CardStroke
                ?? (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        }
        else
        {
            RowRoot.BorderBrush = TransparentBrush;
        }
    }

    // Cached transparent brush — reused across hover transitions so we don't
    // allocate a new SolidColorBrush on every PointerEntered / PointerExited.
    private static readonly Brush TransparentBrush =
        new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private void UpdateSelectionVisualState()
    {
        if (CompactSelectionIndicator != null)
            CompactSelectionIndicator.Opacity = IsSelected ? 1 : 0;

        if (RowSelectionIndicator != null)
            RowSelectionIndicator.Opacity = IsSelected ? 1 : 0;

        if (Mode == TrackItemDisplayMode.Compact)
            ApplyCompactBackground();
        else
            ApplyRowBackground();

        // Keep the multi-select checkbox's checked state in sync with the
        // host-owned IsSelected.
        UpdateSelectionAffordance();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        DispatcherQueue?.TryEnqueue(RefreshThemeVisuals);
    }

    private void RefreshThemeVisuals()
    {
        if (Mode == TrackItemDisplayMode.Compact)
        {
            ApplyCompactBackground();
        }
        else
        {
            ApplyRowBackground();
            RefreshRowThemeForegrounds();
            ApplyRowProgress(Track);
            ApplyChartStatus(Track);
        }

        ResolveImageColorHint();
        RefreshPlaybackState();
        UpdateOverlayState();
    }

    private void RefreshRowThemeForegrounds()
    {
        if (Track is not null)
            RebuildArtistsSubline(Track, force: true);

        var secondaryBrush = ResolveTrackBrush("TextFillColorSecondaryBrush");
        foreach (var element in _customColElements)
        {
            if (element is TextBlock textBlock)
                textBlock.Foreground = secondaryBrush;
        }
    }

    #endregion
}
