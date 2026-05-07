using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

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
        await Task.Delay(16);

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
        ShimmerGate.Reset(() => _host.ShimmerContainer, () => _host.ContentContainer);

        _logger?.LogDebug(
            "[xfade][{Page}] reset showing={Showing} scheduled={Scheduled} navAway={NavAway}",
            _host.PageIdForLogging, _showingContent, _crossfadeScheduled, _isNavigatingAway);
    }

    /// <summary>
    /// Bypass the crossfade entirely — used by pages that perform a connected
    /// animation (Album / Playlist / Show cover transition) where content should
    /// snap to fully visible without fading the shimmer out. Sets the flags so
    /// any pending <see cref="OnIsLoadingChanged"/> takes the
    /// <c>skip-already-shown</c> branch, and unrealises the shimmer subtree via
    /// <c>ShimmerGate.IsLoaded = false</c>.
    /// </summary>
    public void MarkContentShownDirectly()
    {
        _showingContent = true;
        _crossfadeScheduled = false;
        ShimmerGate.IsLoaded = false;

        var content = _host.ContentContainer;
        content.Opacity = 1;
        ElementCompositionPreview.GetElementVisual(content).Opacity = 1;
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
            var shimmerVisualOpacity = ElementCompositionPreview.GetElementVisual(shimmer).Opacity;
            var contentVisualOpacity = ElementCompositionPreview.GetElementVisual(content).Opacity;
            _logger?.LogDebug(
                "[xfade][{Page}] xfade.start shimmerXaml={ShimmerXaml} shimmerVisual={ShimmerVisual} contentVisual={ContentVisual} shimmerVisible={ShimmerVisible}",
                _host.PageIdForLogging, shimmer.Opacity, shimmerVisualOpacity, contentVisualOpacity, shimmer.Visibility);
        }
        else
        {
            _logger?.LogDebug("[xfade][{Page}] xfade.start shimmerXaml=null", _host.PageIdForLogging);
        }

        await ShimmerGate.RunCrossfadeAsync(shimmer, content, _host.CrossfadeLayer,
            continuePredicate: () => _showingContent);

        if (_showingContent)
        {
            _logger?.LogDebug(
                "[xfade][{Page}] xfade.shimmerCollapsed contentVisual={ContentVisual}",
                _host.PageIdForLogging, ElementCompositionPreview.GetElementVisual(content).Opacity);
        }
    }
}
