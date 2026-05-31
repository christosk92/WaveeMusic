using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using CommunityToolkit.WinUI.Animations;

namespace Wavee.UI.WinUI.Helpers;

/// <summary>
/// Shared crossfade state machine + warm-cache trigger that all the content pages
/// (Album / Playlist / Show / Episode) used to duplicate inline. Owns the three
/// flags (<c>_showingContent</c>, <c>_crossfadeScheduled</c>, <c>_isNavigatingAway</c>),
/// the <see cref="ShimmerLoadGate"/>, and the <c>[xfade]</c> log emission. Pages
/// keep all their page-specific code (activation, connected animations, flyout
/// rebuilding, narrow/wide visual states, etc.) and just call into the controller
/// at the existing seams.
///
/// Lifecycle:
///   • Page ctor: <c>PageController = new ContentPageController(this, logger);</c>
///     before <c>InitializeComponent()</c> so the XAML's
///     <c>x:Load="{x:Bind ShimmerGate.IsLoaded, Mode=OneWay}"</c> binds to the
///     gate the controller owns.
///   • Page <c>ViewModel.PropertyChanged</c> handler → <c>PageController.OnIsLoadingChanged()</c>
///     when the VM's IsLoading property flips.
///   • Page <c>OnNavigatedTo</c> / <c>RefreshWithParameter</c>: call
///     <c>PageController.ResetForNewLoad()</c>, run page-specific activation, then
///     <c>DispatcherQueue.TryEnqueue(PageController.TryShowContentNow)</c> for warm-cache.
///   • Page <c>OnNavigatedFrom</c>: <c>PageController.IsNavigatingAway = true;</c>
/// </summary>
public sealed class ContentPageController
{
    private readonly IContentPageHost _host;
    private readonly ILogger? _logger;
    private bool _showingContent;
    private bool _crossfadeScheduled;
    private bool _isNavigatingAway;

    public ContentPageController(IContentPageHost host, ILogger? logger = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _logger = logger;
    }

    public ShimmerLoadGate ShimmerGate { get; } = new();

    public bool IsShowingContent => _showingContent;
    public bool IsCrossfadeScheduled => _crossfadeScheduled;

    public bool IsNavigatingAway
    {
        get => _isNavigatingAway;
        set => _isNavigatingAway = value;
    }

    /// <summary>
    /// Page calls this from its <c>ViewModel.PropertyChanged</c> handler when the VM's
    /// <c>IsLoading</c> property changes. Schedules a crossfade if loading just finished
    /// and content isn't already showing.
    /// </summary>
    public void OnIsLoadingChanged()
    {
        var isLoading = _host.IsLoading;
        string action;
        if (isLoading) action = "skip-still-loading";
        else if (_showingContent) action = "skip-already-shown";
        else if (_crossfadeScheduled) action = "skip-already-scheduled";
        else action = "schedule";

        _logger?.LogDebug(
            "[xfade][{Page}] propchg.isLoading val={Val} showing={Showing} scheduled={Scheduled} action={Action}",
            _host.PageIdForLogging, isLoading, _showingContent, _crossfadeScheduled, action);

        if (action == "schedule")
            ScheduleCrossfade();
    }

    /// <summary>
    /// Yields twice (so XAML measures the freshly-bound content tree before the fade
    /// starts), then runs the crossfade unless a navigation-away or already-showing
    /// race intervened.
    /// </summary>
    public async void ScheduleCrossfade()
    {
        _logger?.LogDebug(
            "[xfade][{Page}] schedule.enter showing={Showing} scheduled={Scheduled} navAway={NavAway} isLoading={IsLoading}",
            _host.PageIdForLogging, _showingContent, _crossfadeScheduled, _isNavigatingAway, _host.IsLoading);

        _crossfadeScheduled = true;
        await Task.Yield();
        await CompositionFrameAwaiter.NextFrameAsync();

        var bail = _isNavigatingAway || _showingContent;
        _logger?.LogDebug(
            "[xfade][{Page}] schedule.gate navAway={NavAway} showing={Showing} action={Action}",
            _host.PageIdForLogging, _isNavigatingAway, _showingContent, bail ? "bail" : "run");

        if (bail)
        {
            _crossfadeScheduled = false;
            return;
        }

        await CrossfadeToContentAsync();
    }

    /// <summary>
    /// Warm-cache trigger. Same-id re-navigation can leave <c>IsLoading</c> at <c>false</c>
    /// across the entire transition, so <see cref="OnIsLoadingChanged"/> never fires the
    /// schedule branch. The page calls this from <c>DispatcherQueue.TryEnqueue</c> after
    /// activating the VM to cover that case.
    /// </summary>
    public void TryShowContentNow()
    {
        if (_showingContent || _crossfadeScheduled || _host.IsLoading || !_host.HasContent)
            return;
        ScheduleCrossfade();
    }

    /// <summary>
    /// Reset the state machine and re-arm the shimmer for a fresh load.
    /// Call from the page's <c>OnNavigatedTo</c> / <c>RefreshWithParameter</c> path
    /// before activating the VM.
    /// </summary>
    public void ResetForNewLoad()
    {
        _isNavigatingAway = false;
        _showingContent = false;
        _crossfadeScheduled = false;
        ShimmerGate.Reset(() => _host.ShimmerContainer, () => _host.ContentContainer, _host.CrossfadeLayer);

        _logger?.LogDebug(
            "[xfade][{Page}] reset showing={Showing} scheduled={Scheduled} navAway={NavAway}",
            _host.PageIdForLogging, _showingContent, _crossfadeScheduled, _isNavigatingAway);
    }

    /// <summary>
    /// Warm same-type re-navigation reveal: fade the content root through
    /// without touching the shimmer gate, so a structurally-identical page
    /// (album→album, playlist→playlist) gets a perceptible "new content" beat
    /// instead of an instant snap OR a skeleton flash over already-good pixels.
    /// Honors the OS reduced-motion setting. Pages call this on a same-type
    /// refresh with usable prefill, in place of <see cref="ResetForNewLoad"/>.
    /// </summary>
    public void CrossfadeContentSwap()
    {
        _showingContent = true;
        _crossfadeScheduled = false;
        ShimmerGate.IsLoaded = false;

        var content = _host.ContentContainer;
        var layer = _host.CrossfadeLayer;

        if (!ReducedMotion.AnimationsEnabled)
        {
            content.Opacity = 1;
            if (layer == FrameworkLayer.Composition)
                ElementCompositionPreview.GetElementVisual(content).Opacity = 1;
            return;
        }

        AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(220), layer: layer)
            .Start(content);
    }

    private async Task CrossfadeToContentAsync()
    {
        if (_showingContent) return;
        _showingContent = true;
        _crossfadeScheduled = false;

        var shimmer = _host.ShimmerContainer;
        var content = _host.ContentContainer;

        if (shimmer is not null)
        {
            if (_host.CrossfadeLayer == CommunityToolkit.WinUI.Animations.FrameworkLayer.Composition)
            {
                var shimmerVisualOpacity = ElementCompositionPreview.GetElementVisual(shimmer).Opacity;
                var contentVisualOpacity = ElementCompositionPreview.GetElementVisual(content).Opacity;
                _logger?.LogDebug(
                    "[xfade][{Page}] xfade.start shimmerXaml={ShimmerXaml} shimmerVisual={ShimmerVisual} contentVisual={ContentVisual} shimmerVisible={ShimmerVisible}",
                    _host.PageIdForLogging, shimmer.Opacity, shimmerVisualOpacity, contentVisualOpacity, shimmer.Visibility);
            }
            else
            {
                _logger?.LogDebug(
                    "[xfade][{Page}] xfade.start shimmerXaml={ShimmerXaml} contentXaml={ContentXaml} shimmerVisible={ShimmerVisible}",
                    _host.PageIdForLogging, shimmer.Opacity, content.Opacity, shimmer.Visibility);
            }
        }
        else
        {
            _logger?.LogDebug("[xfade][{Page}] xfade.start shimmerXaml=null", _host.PageIdForLogging);
        }

        await ShimmerGate.RunCrossfadeAsync(shimmer, content, _host.CrossfadeLayer,
            continuePredicate: () => _showingContent);

        if (_showingContent)
        {
            if (_host.CrossfadeLayer == CommunityToolkit.WinUI.Animations.FrameworkLayer.Composition)
            {
                _logger?.LogDebug(
                    "[xfade][{Page}] xfade.shimmerCollapsed contentVisual={ContentVisual}",
                    _host.PageIdForLogging, ElementCompositionPreview.GetElementVisual(content).Opacity);
            }
            else
            {
                _logger?.LogDebug(
                    "[xfade][{Page}] xfade.shimmerCollapsed contentXaml={ContentXaml}",
                    _host.PageIdForLogging, content.Opacity);
            }
        }
    }

}
