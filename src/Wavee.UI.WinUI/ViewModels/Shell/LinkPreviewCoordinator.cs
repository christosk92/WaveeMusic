using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core;
using Wavee.UI.Contracts;
using Wavee.UI.Services.Infra;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.ViewModels.Shell;

/// <summary>
/// Owns the omnibar's Spotify URL / URI paste flow. When the user types or
/// pastes a recognizable Spotify link, the omnibar swaps its three-section
/// suggestion list for a single "Open link" card; this coordinator fetches
/// the entity's real title / cover so the placeholder card upgrades in place.
///
/// <para>The coordinator never touches XAML state directly. The omnibar
/// subscribes to <see cref="PreviewReady"/> with a per-link / per-text token
/// and decides whether to apply the result (the active query may have
/// changed by the time the network fetch resolves).</para>
///
/// <para>Extracted from <c>ShellViewModel</c> as part of the shell decomposition
/// — link-paste lifecycle was the single biggest chunk of omnibar-adjacent
/// code that didn't belong on the parent VM.</para>
/// </summary>
public sealed class LinkPreviewCoordinator
{
    private readonly ISpotifyLinkPreviewService? _linkPreviewService;
    private readonly IBackgroundWorkRunner _backgroundWork;
    private readonly ILogger? _logger;

    // CTS scoped to the currently-typed URL paste, so a fresh keystroke
    // cancels any in-flight preview fetch before the result lands on a
    // stale query.
    private CancellationTokenSource? _linkPreviewCts;

    public LinkPreviewCoordinator(
        ISpotifyLinkPreviewService? linkPreviewService,
        IBackgroundWorkRunner backgroundWork,
        ILogger? logger = null)
    {
        _linkPreviewService = linkPreviewService;
        _backgroundWork = backgroundWork;
        _logger = logger;
    }

    /// <summary>
    /// Raised on the thread the work runner schedules — typically a
    /// background thread. The omnibar marshals to the UI thread when it
    /// reads the result.
    /// </summary>
    public event Action<LinkPreviewResult>? PreviewReady;

    /// <summary>True when a link-preview service was registered.</summary>
    public bool IsPreviewServiceAvailable => _linkPreviewService is not null;

    /// <summary>
    /// Cancels any in-flight preview fetch. Called by the omnibar when the
    /// active query stops looking like a link (so a late preview result
    /// can't overwrite freshly-rendered search suggestions).
    /// </summary>
    public void Cancel()
    {
        _linkPreviewCts?.Cancel();
    }

    /// <summary>
    /// Kicks off an async fetch for the given link. The previous CTS is
    /// cancelled first so only one fetch is ever in flight; the
    /// <paramref name="rawText"/> is echoed back through
    /// <see cref="PreviewReady"/> so the omnibar can guard against stale
    /// results.
    /// </summary>
    public void StartPreview(SpotifyLink link, string rawText)
    {
        _linkPreviewCts?.Cancel();
        _linkPreviewCts?.Dispose();
        _linkPreviewCts = new CancellationTokenSource();

        if (_linkPreviewService is null) return;

        _backgroundWork.Run(
            ct => ResolveAsync(link, rawText, ct),
            "LinkPreviewCoordinator.Resolve",
            _linkPreviewCts.Token);
    }

    private async Task ResolveAsync(SpotifyLink link, string rawText, CancellationToken ct)
    {
        if (_linkPreviewService is null) return;

        LinkPreview? preview;
        try
        {
            preview = await _linkPreviewService.ResolveAsync(link, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[LinkPreview] Resolve failed for {Uri}", link.CanonicalUri);
            return;
        }

        if (ct.IsCancellationRequested) return;
        if (preview is null) return;

        PreviewReady?.Invoke(new LinkPreviewResult(link, rawText, preview));
    }

    public void Dispose()
    {
        _linkPreviewCts?.Cancel();
        _linkPreviewCts?.Dispose();
        _linkPreviewCts = null;
    }
}

/// <summary>
/// Payload of <see cref="LinkPreviewCoordinator.PreviewReady"/>. Carries the
/// originating link, the raw text the user typed (so the omnibar can compare
/// against its <c>_activeSearchText</c> for staleness), and the resolved
/// metadata to upgrade the placeholder card with.
/// </summary>
public sealed record LinkPreviewResult(
    SpotifyLink Link,
    string RawText,
    LinkPreview Preview);
