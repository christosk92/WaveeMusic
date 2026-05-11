using System;
using System.Threading.Tasks;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Wavee.UI.WinUI.Helpers;

/// <summary>
/// Owns the bool DP that a page's loading-skeleton subtree binds <c>x:Load</c> to,
/// plus the shared shimmer→content crossfade. Pages own one instance and bind
/// <c>x:Load="{x:Bind ShimmerGate.IsLoaded, Mode=OneWay}"</c> on the named
/// <c>ShimmerContainer</c> element.
///
/// Lifecycle:
///   • <see cref="IsLoaded"/> defaults to true → skeleton is realized while loading.
///   • At the end of the crossfade, the helper sets the shimmer's
///     <c>Visibility</c> to <c>Collapsed</c> and flips <see cref="IsLoaded"/>
///     to false → x:Load unrealizes the entire skeleton subtree, freeing
///     compositor / Xaml peer memory while the page sits in the Frame cache.
///   • On the next reload, <see cref="Reset"/> re-arms the gate. After
///     <see cref="IsLoaded"/> goes back to true, the named XAML field is
///     re-assigned by the framework — but the value the *caller* read earlier
///     is stale. <see cref="Reset"/> therefore takes accessor delegates and
///     evaluates them AFTER realization, never a raw stale reference.
/// </summary>
public sealed class ShimmerLoadGate : DependencyObject
{
    public static readonly DependencyProperty IsLoadedProperty =
        DependencyProperty.Register(
            nameof(IsLoaded), typeof(bool), typeof(ShimmerLoadGate),
            new PropertyMetadata(true));

    public bool IsLoaded
    {
        get => (bool)GetValue(IsLoadedProperty);
        set => SetValue(IsLoadedProperty, value);
    }

    /// <summary>
    /// Run the standard 200 ms shimmer fade-out / 300 ms content fade-in
    /// (with a 100 ms content delay), then collapse the shimmer and unrealize
    /// it. The optional <paramref name="continuePredicate"/> guards the
    /// finalization so a re-entrant navigation that fires a new
    /// <c>Reset</c> during the 250 ms delay isn't clobbered by the stale
    /// <c>IsLoaded = false</c> at the end of the in-flight call.
    ///
    /// <paramref name="shimmer"/> is nullable: if x:Load realization is still
    /// pending at call time (e.g. fresh-constructed page where the named field
    /// hasn't been wired yet), the shimmer animation is skipped but the
    /// content fade-in still runs so the page reaches its visible state. The
    /// caller is responsible for triggering <see cref="Reset"/> earlier in
    /// the lifecycle to re-realize the skeleton on the next load.
    /// </summary>
    public async Task RunCrossfadeAsync(
        FrameworkElement? shimmer,
        FrameworkElement content,
        FrameworkLayer layer = FrameworkLayer.Composition,
        Func<bool>? continuePredicate = null)
    {
        if (shimmer is not null)
        {
            AnimationBuilder.Create()
                .Opacity(from: 1, to: 0, duration: TimeSpan.FromMilliseconds(200), layer: layer)
                .Start(shimmer);
        }

        AnimationBuilder.Create()
            .Opacity(from: 0, to: 1,
                     duration: TimeSpan.FromMilliseconds(300),
                     delay: TimeSpan.FromMilliseconds(100),
                     layer: layer)
            .Start(content);

        await Task.Delay(250);

        if (continuePredicate is not null && !continuePredicate()) return;
        if (shimmer is not null) shimmer.Visibility = Visibility.Collapsed;
        // Note: we deliberately do NOT set IsLoaded = false here. Unrealizing
        // the shimmer subtree was nice for heap pressure but caused a
        // realization race on the next nav — Reset's accessor returned null
        // because x:Load=true → element-assigned is async, ApplyResetState
        // got skipped, and content.Opacity stayed at 1 from this crossfade's
        // end. The next nav's crossfade then animated 0→1 against an already-
        // visible content layer, producing no visible swap and a stuck hero.
        // Keeping the ~5-rectangle shimmer subtree realized is a trivial
        // memory cost compared to that bug.
    }

    /// <summary>
    /// Re-arm the gate so the next load shows the skeleton again.
    /// Setting <see cref="IsLoaded"/> to true triggers x:Load to realize the
    /// named XAML element. In most cases realization is synchronous and the
    /// accessors return non-null on the next line, but during early page
    /// construction (e.g. inside <c>OnNavigatedTo</c>, before the page has
    /// joined the Frame's visual tree), realization can be deferred until
    /// the next dispatcher tick. We try the sync path first; if either
    /// accessor still returns null, we re-try via <see cref="DispatcherQueue"/>
    /// so the loading-state defaults are applied as soon as the field references
    /// are wired up.
    /// </summary>
    public void Reset(Func<FrameworkElement?> shimmerAccessor, Func<FrameworkElement?> contentAccessor)
    {
        IsLoaded = true;

        // Always reset content first — the content container has no x:Load gate
        // on it, so the accessor is reliably non-null on cached pages. If we
        // don't reset content.Opacity = 0 here, the previous crossfade's
        // end-state (opacity = 1) bleeds into the next nav: the new crossfade
        // animates 0 → 1 against a visual already at 1, producing no visible
        // hero swap. THIS was the "stuck on old playlist" bug.
        var content = contentAccessor();
        if (content is not null)
        {
            content.Opacity = 0;
            ElementCompositionPreview.GetElementVisual(content).Opacity = 0;
        }

        // Shimmer is x:Load-gated — its accessor can return null when
        // realization hasn't landed yet (the IsLoaded=true write above goes
        // through one dispatcher tick before the named field is assigned).
        // Apply the reset whenever it lands; the crossfade's null-shimmer
        // branch already tolerates the race (it skips the shimmer fade-out
        // animation and just fades content in).
        var shimmer = shimmerAccessor();
        if (shimmer is not null)
        {
            ApplyShimmerResetState(shimmer);
            return;
        }

        var dq = DispatcherQueue.GetForCurrentThread();
        if (dq is null) return;
        dq.TryEnqueue(() =>
        {
            var s = shimmerAccessor();
            if (s is not null) ApplyShimmerResetState(s);
        });
    }

    private static void ApplyShimmerResetState(FrameworkElement shimmer)
    {
        shimmer.Visibility = Visibility.Visible;
        shimmer.Opacity = 1;
        ElementCompositionPreview.GetElementVisual(shimmer).Opacity = 1;
    }
}
