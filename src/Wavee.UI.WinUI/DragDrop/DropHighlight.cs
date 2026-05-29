using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Wavee.UI.WinUI.DragDrop;

/// <summary>
/// Shared "valid drop target" highlight for cross-surface drops (queue enqueue,
/// player bar, now-playing media frame, …). Replaces the old caption-only feedback
/// so the target surface itself lights up. Uses the same accent language as the
/// sidebar center "drop INTO" cue, so "you can drop here" reads identically across
/// the app.
///
/// <para>Implementation: overlays an accent <see cref="Rectangle"/> (border +
/// subtle fill) inside a host <see cref="Panel"/> / <see cref="Border"/> /
/// <see cref="ContentControl"/>, fading + springing it in on <see cref="Apply"/>
/// and out on <see cref="Clear"/>. Composition-only (no layout), AOT-safe.</para>
/// </summary>
public static class DropHighlight
{
    public enum Intensity
    {
        /// <summary>Full accent outline + fill — "drop INTO this" (playlist row, player bar).</summary>
        Into,
        /// <summary>Softer fill, thinner outline — "this zone accepts the drop" (queue panel body).</summary>
        Zone,
    }

    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(160);

    // One overlay rectangle per host element, kept across Apply/Clear cycles, plus
    // the intensity it's currently showing (-1 = hidden) so Apply is idempotent:
    // DragOver fires ~60×/s and must not re-allocate brushes or restart the fade.
    private static readonly Dictionary<FrameworkElement, Rectangle> _overlays = new();
    private static readonly Dictionary<Rectangle, int> _shownIntensity = new();

    public static void Apply(FrameworkElement host, Intensity intensity = Intensity.Into)
    {
        if (host is null) return;
        var overlay = EnsureOverlay(host);
        if (overlay is null) return;

        // Already showing at this intensity → nothing to do (idempotent per tick).
        if (_shownIntensity.TryGetValue(overlay, out var cur) && cur == (int)intensity)
            return;
        _shownIntensity[overlay] = (int)intensity;

        var accent = AccentColor();
        overlay.Stroke = StrokeBrush(accent);
        overlay.StrokeThickness = intensity == Intensity.Into ? 2 : 1;
        overlay.Fill = FillBrush(accent, intensity);

        overlay.Visibility = Visibility.Visible;
        var visual = ElementCompositionPreview.GetElementVisual(overlay);
        var c = visual.Compositor;

        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 1f, c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f)));
        fade.Duration = FadeIn;
        overlay.Opacity = 1;
        visual.StartAnimation("Opacity", fade);
    }

    public static void Clear(FrameworkElement host)
    {
        if (host is null || !_overlays.TryGetValue(host, out var overlay)) return;
        // Already cleared → no-op (Clear is also called speculatively each tick).
        if (!_shownIntensity.ContainsKey(overlay)) return;
        _shownIntensity.Remove(overlay);

        var visual = ElementCompositionPreview.GetElementVisual(overlay);
        var c = visual.Compositor;

        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 0f, c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f)));
        fade.Duration = FadeOut;

        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation("Opacity", fade);
        batch.End();
        batch.Completed += (_, _) =>
        {
            // Guard against a re-Apply landing during the fade-out.
            if (_shownIntensity.ContainsKey(overlay)) return;
            overlay.Opacity = 0;
            overlay.Visibility = Visibility.Collapsed;
        };
    }

    // Cached brushes — accent colour is process-stable, so two solids per intensity
    // cover every highlight without per-tick allocation.
    private static SolidColorBrush? _strokeBrush;
    private static SolidColorBrush? _fillInto;
    private static SolidColorBrush? _fillZone;

    private static SolidColorBrush StrokeBrush(Color accent) =>
        _strokeBrush ??= new SolidColorBrush(accent);

    private static SolidColorBrush FillBrush(Color accent, Intensity intensity) => intensity == Intensity.Into
        ? _fillInto ??= new SolidColorBrush(Color.FromArgb(0x24, accent.R, accent.G, accent.B))
        : _fillZone ??= new SolidColorBrush(Color.FromArgb(0x14, accent.R, accent.G, accent.B));

    /// <summary>
    /// Inserts (once) a hit-test-invisible accent rectangle on top of the host's
    /// existing content. Works for the common host shapes used by drop targets.
    /// </summary>
    private static Rectangle? EnsureOverlay(FrameworkElement host)
    {
        if (_overlays.TryGetValue(host, out var existing)) return existing;

        var rect = new Rectangle
        {
            RadiusX = 8,
            RadiusY = 8,
            IsHitTestVisible = false,
            Opacity = 0,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        if (host is Panel panel)
        {
            // Overlay stretches across all rows/columns so it covers the whole host.
            if (panel is Grid grid)
            {
                if (grid.RowDefinitions.Count > 0) Grid.SetRowSpan(rect, grid.RowDefinitions.Count);
                if (grid.ColumnDefinitions.Count > 0) Grid.SetColumnSpan(rect, grid.ColumnDefinitions.Count);
            }
            panel.Children.Add(rect);
        }
        else
        {
            // Non-Panel hosts (Border/ContentControl) can't hold a sibling overlay;
            // every current Wavee cross-surface highlight target is a Grid.
            return null;
        }

        _overlays[host] = rect;
        host.Unloaded += (_, _) =>
        {
            _overlays.Remove(host);
            _shownIntensity.Remove(rect);
        };
        return rect;
    }

    /// <summary>
    /// Safety-net: instantly hide every active highlight. Called when the global
    /// drag ends, so a highlight can't get stuck lit if a target's Drop/DragLeave
    /// didn't fire (drag cancelled, released over a non-handler, etc.).
    /// </summary>
    public static void ClearAll()
    {
        foreach (var (host, overlay) in _overlays)
        {
            _ = host;
            _shownIntensity.Remove(overlay);
            overlay.Opacity = 0;
            overlay.Visibility = Visibility.Collapsed;
        }
    }

    private static Color AccentColor()
    {
        if (Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out var v)
            && v is SolidColorBrush b)
            return b.Color;
        return Color.FromArgb(0xFF, 0x1D, 0xB9, 0x54);
    }
}
