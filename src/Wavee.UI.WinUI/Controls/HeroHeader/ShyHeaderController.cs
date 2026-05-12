using System;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wavee.UI.WinUI.Controls.HeroHeader;

/// <summary>
/// Owns the per-page "hero fades + floating shy pill morphs in on scroll"
/// lifecycle. Each page used to duplicate ~80 lines of state machine for
/// this; the controller absorbs it so the page just constructs one in
/// <c>Loaded</c> and disposes it in <c>Unloaded</c>.
///
/// Behaviour mirrors the previous per-page implementation:
/// <list type="bullet">
///   <item>One in-flight TransitionHelper run at a time; scroll events
///         arriving mid-flight queue a re-check via the coalesce loop.</item>
///   <item>Pin offset is recomputed every check (window-resize friendly).</item>
///   <item>Source goes <see cref="Visibility.Collapsed"/> via the helper's
///         <c>SourceToggleMethod="ByVisibility"</c>; target goes
///         <see cref="Visibility.Visible"/> via its own toggle method.</item>
/// </list>
/// </summary>
public sealed class ShyHeaderController : IDisposable
{
    private readonly ScrollView _scrollView;
    private readonly FrameworkElement _hero;
    private readonly FrameworkElement _source;
    private readonly FrameworkElement _target;
    private readonly TransitionHelper _transition;
    private readonly Action<double> _applyHeroFade;
    private readonly Func<double> _pinOffset;
    private readonly Func<bool> _canEvaluate;
    private readonly ILogger? _logger;

    private bool _attached;
    private bool _isPinned;
    private bool _isRunning;
    private bool _recheckPending;
    private bool _disposed;

    /// <summary>
    /// When true, the controller short-circuits its scroll handler and
    /// <see cref="EvaluateAsync"/>. Set this around content-reset flows
    /// (e.g. ArtistPage's <c>LoadNewContent</c>) so the shy pill doesn't
    /// flash while the page swaps to a new entity.
    /// </summary>
    public bool Suppressed { get; set; }

    /// <param name="scrollView">ScrollView driving the fade + pin check.</param>
    /// <param name="hero">Element whose <c>ActualHeight</c> defines the hero
    /// region. Default <paramref name="pinOffset"/> uses
    /// <c>hero.ActualHeight - 120</c>. ProfilePage passes <c>IdentityCardWrap</c>
    /// here instead of an actual hero.</param>
    /// <param name="source">Element with the in-hero matched-id chrome
    /// (e.g. <c>HeroOverlayPanel</c>). The helper's <c>Source</c> is wired to
    /// this.</param>
    /// <param name="target">The floating shy pill (e.g. <c>ShyHeaderCard</c>).
    /// The helper's <c>Target</c> is wired to this.</param>
    /// <param name="transition">The TransitionHelper resource the page already
    /// declared in <c>Page.Resources</c>. Source/Target are wired here.</param>
    /// <param name="applyHeroFade">Called every scroll tick with the raw
    /// <c>0..1</c> progress (<c>VerticalOffset / hero.ActualHeight</c>). Use
    /// <see cref="ShyHeaderFade"/> factories for the common cases.</param>
    /// <param name="pinOffset">Optional override for the pin threshold.
    /// Defaults to <c>() =&gt; Max(0, hero.ActualHeight - 120)</c>.</param>
    /// <param name="canEvaluate">Optional guard. Returning false skips the
    /// shy-header evaluation (used by ArtistPage to short-circuit during
    /// navigation-away).</param>
    /// <param name="logger">Optional logger; swallowed transition exceptions
    /// are reported at <c>LogDebug</c>.</param>
    public ShyHeaderController(
        ScrollView scrollView,
        FrameworkElement hero,
        FrameworkElement source,
        FrameworkElement target,
        TransitionHelper transition,
        Action<double> applyHeroFade,
        Func<double>? pinOffset = null,
        Func<bool>? canEvaluate = null,
        ILogger? logger = null)
    {
        _scrollView = scrollView;
        _hero = hero;
        _source = source;
        _target = target;
        _transition = transition;
        _applyHeroFade = applyHeroFade;
        _pinOffset = pinOffset ?? (() => Math.Max(0, _hero.ActualHeight - 120));
        _canEvaluate = canEvaluate ?? (() => true);
        _logger = logger;

        _transition.Source = source;
        _transition.Target = target;
    }

    /// <summary>Wires the scroll handler. Idempotent.</summary>
    public void Attach()
    {
        if (_attached || _disposed) return;
        _scrollView.ViewChanged += OnViewChanged;
        _attached = true;
    }

    /// <summary>Unwires the scroll handler. Idempotent.</summary>
    public void Detach()
    {
        if (!_attached) return;
        _scrollView.ViewChanged -= OnViewChanged;
        _attached = false;
    }

    /// <summary>
    /// Resets pinned/running state, snaps Source visible + Target collapsed,
    /// and tells the TransitionHelper to land in its initial state. Safe to
    /// call any time; the page calls this from <c>Loaded</c>, from any
    /// content-reset flow, and from nav-cache restore.
    /// </summary>
    public void Reset()
    {
        _isPinned = false;
        _isRunning = false;
        _recheckPending = false;
        if (_source is not null) _source.Visibility = Visibility.Visible;
        if (_target is not null) _target.Visibility = Visibility.Collapsed;
        try { _transition.Reset(toInitialState: true); }
        catch (Exception ex) { _logger?.LogDebug(ex, "Shy header reset failed."); }
    }

    /// <summary>
    /// Stop any in-flight transition. Equivalent to
    /// <c>_transition?.Stop()</c> from the old call sites — guarded with a
    /// try/catch because the helper can throw if it was never started.
    /// </summary>
    public void Stop()
    {
        try { _transition.Stop(); }
        catch (Exception ex) { _logger?.LogDebug(ex, "Shy header stop failed."); }
    }

    /// <summary>
    /// Re-run the fade with the current scroll position. Used by pages that
    /// drive a fade outside of the scroll handler (e.g. ArtistPage's
    /// scroll-position restoration after a navigation cache resume).
    /// </summary>
    public void UpdateHeroFade()
    {
        var heroH = _hero.ActualHeight;
        if (heroH <= 0)
        {
            _applyHeroFade(0);
            return;
        }
        _applyHeroFade(Math.Clamp(_scrollView.VerticalOffset / heroH, 0.0, 1.0));
    }

    /// <summary>
    /// Public re-entry point for the evaluate loop. Page-side hooks (size
    /// changes, scroll restore after nav-cache resume) call this when they
    /// know the shy state may have shifted independently of a scroll tick.
    /// </summary>
    public Task EvaluateAsync() => EvaluateInternalAsync();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
    }

    private void OnViewChanged(ScrollView sender, object args)
    {
        UpdateHeroFade();
        if (Suppressed) return;
        _ = EvaluateInternalAsync();
    }

    private async Task EvaluateInternalAsync()
    {
        if (Suppressed) return;

        if (_isRunning)
        {
            // Coalesce: re-check once the in-flight transition lands.
            _recheckPending = true;
            return;
        }

        while (true)
        {
            if (Suppressed) return;
            if (!_canEvaluate()) return;
            if (!_hero.IsLoaded || !_target.IsLoaded) return;

            bool shouldPin = _scrollView.VerticalOffset >= _pinOffset();
            if (shouldPin == _isPinned) return;

            _isRunning = true;
            _recheckPending = false;
            try
            {
                if (shouldPin) await _transition.StartAsync();
                else await _transition.ReverseAsync();
                _isPinned = shouldPin;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Shy header transition skipped.");
                return;
            }
            finally
            {
                _isRunning = false;
            }

            if (!_recheckPending) return;
        }
    }
}
