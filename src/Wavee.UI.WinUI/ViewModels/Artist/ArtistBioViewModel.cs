using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Wavee.UI.Contracts;
using Wavee.UI.Formatters.Artist;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.ViewModels.Artist;

/// <summary>
/// Owns the biography surfaces: the long-form Spotify biography, the hero
/// peek-line projection, and the optional on-device AI summary (Copilot+ +
/// opt-in gated, all the work hidden inside <see cref="ArtistBioSummarizer"/>).
///
/// <para>The biography text itself comes from the parent's
/// <see cref="ArtistView"/> envelope — the bio VM stores no copy, only
/// projects through accessors. The AI summary is the only piece of state
/// the VM owns.</para>
/// </summary>
public sealed partial class ArtistBioViewModel : ObservableObject, IDisposable
{
    private const int HeroBioMaxLength = 150;

    private readonly ArtistBioSummarizer? _bioSummarizer;
    private readonly AiCapabilities? _capabilities;
    private readonly ILogger? _logger;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;

    private readonly Func<string?> _biographyProvider;
    private readonly Func<string?> _artistNameProvider;
    private readonly Func<string?> _monthlyListenersProvider;
    private readonly Func<IReadOnlyList<string>> _topTrackNamesProvider;

    private CancellationTokenSource? _bioSummaryCts;

    public ArtistBioViewModel(
        ArtistBioSummarizer? bioSummarizer,
        AiCapabilities? capabilities,
        ILogger? logger,
        Func<string?> biographyProvider,
        Func<string?> artistNameProvider,
        Func<string?> monthlyListenersProvider,
        Func<IReadOnlyList<string>> topTrackNamesProvider)
    {
        _bioSummarizer = bioSummarizer;
        _capabilities = capabilities;
        _logger = logger;
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _biographyProvider = biographyProvider;
        _artistNameProvider = artistNameProvider;
        _monthlyListenersProvider = monthlyListenersProvider;
        _topTrackNamesProvider = topTrackNamesProvider;
    }

    // ── Envelope-projected bio ──────────────────────────────────────────────

    public string? Biography => _biographyProvider();
    public bool HasBiography => !string.IsNullOrWhiteSpace(Biography);

    public string? BioPeekLine
    {
        get
        {
            var bio = Biography;
            if (string.IsNullOrWhiteSpace(bio)) return null;

            var firstSentenceEnd = bio.IndexOf(". ", StringComparison.Ordinal);
            var sentence = firstSentenceEnd > 0
                ? bio.Substring(0, firstSentenceEnd + 1)
                : bio;

            return sentence.Length > 140
                ? sentence.Substring(0, 139).TrimEnd() + "..."
                : sentence;
        }
    }

    public bool HasBioPeekLine => !string.IsNullOrEmpty(BioPeekLine);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBioSummary))]
    [NotifyPropertyChangedFor(nameof(IsAiBioGenerating))]
    [NotifyPropertyChangedFor(nameof(IsAiBioReady))]
    [NotifyPropertyChangedFor(nameof(IsAiBioUnavailable))]
    [NotifyPropertyChangedFor(nameof(BioExcerptText))]
    [NotifyPropertyChangedFor(nameof(HeroBioLine))]
    [NotifyPropertyChangedFor(nameof(HasHeroBioLine))]
    private string? _bioSummaryText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAiBioGenerating))]
    [NotifyPropertyChangedFor(nameof(IsAiBioUnavailable))]
    private bool _isBioSummaryLoading;

    [ObservableProperty]
    private bool _wasLastBioFromCache;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAiBioGenerating))]
    private bool _isBioSummaryStreaming;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAiBioGenerating))]
    [NotifyPropertyChangedFor(nameof(IsAiBioUnavailable))]
    private string? _bioSummaryUnavailableText;

    public bool HasBioSummary => !string.IsNullOrWhiteSpace(BioSummaryText);
    public bool IsAiBioCardVisible => _capabilities?.IsArtistBioSummarizeEnabled == true;
    public bool HasBiographyOrAi => HasBiography || IsAiBioCardVisible;
    public bool IsAiBioGenerating =>
        IsAiBioCardVisible && !HasBioSummary && string.IsNullOrWhiteSpace(BioSummaryUnavailableText);
    public bool IsAiBioReady => HasBioSummary;
    public bool IsAiBioUnavailable =>
        IsAiBioCardVisible && !IsBioSummaryLoading && !HasBioSummary && !string.IsNullOrWhiteSpace(BioSummaryUnavailableText);

    public string BioExcerptText => HasBioPeekLine
        ? BioPeekLine!
        : (BioSummaryText ?? string.Empty);

    public string HeroBioLine => ArtistBioTextFormatter.BuildHeroBioLine(
        Biography, BioSummaryText, _artistNameProvider(), HeroBioMaxLength);

    public bool HasHeroBioLine => !string.IsNullOrWhiteSpace(HeroBioLine);

    // ── Lifecycle ───────────────────────────────────────────────────────────

    /// <summary>
    /// Called by the parent whenever the envelope's biography source changes
    /// (new artist applied, etc). Re-raises all bio-projected properties.
    /// </summary>
    public void NotifyBiographyChanged()
    {
        OnPropertyChanged(nameof(Biography));
        OnPropertyChanged(nameof(HasBiography));
        OnPropertyChanged(nameof(BioPeekLine));
        OnPropertyChanged(nameof(HasBioPeekLine));
        OnPropertyChanged(nameof(BioExcerptText));
        OnPropertyChanged(nameof(HeroBioLine));
        OnPropertyChanged(nameof(HasHeroBioLine));
        OnPropertyChanged(nameof(IsAiBioCardVisible));
        OnPropertyChanged(nameof(HasBiographyOrAi));
        OnPropertyChanged(nameof(IsAiBioGenerating));
        OnPropertyChanged(nameof(IsAiBioReady));
        OnPropertyChanged(nameof(IsAiBioUnavailable));
    }

    public void ResetForNewArtist()
    {
        _bioSummaryCts?.Cancel();
        BioSummaryText = null;
        WasLastBioFromCache = false;
        IsBioSummaryStreaming = false;
        BioSummaryUnavailableText = null;
        IsBioSummaryLoading = false;
        NotifyBiographyChanged();
    }

    // ── On-device AI summary ────────────────────────────────────────────────

    /// <summary>
    /// Generates an on-device biography excerpt via Phi Silica. Called by
    /// the parent's overview-apply path when the ArtistOverview returned no
    /// biography. Guarded against double-runs by an internal CTS — switching
    /// to a different artist cancels the prior generation. Result lands on
    /// <see cref="BioSummaryText"/>; failures collapse the About excerpt to
    /// empty (no error chrome — the section just doesn't render).
    /// </summary>
    public async Task LoadBioSummaryAsync(string artistId)
    {
        if (_bioSummarizer is null)
        {
            BioSummaryUnavailableText = "On-device AI is not available right now.";
            return;
        }
        if (string.IsNullOrEmpty(artistId))
        {
            BioSummaryUnavailableText = "There is not enough artist context to generate a description yet.";
            return;
        }

        var artistName = _artistNameProvider();
        if (string.IsNullOrEmpty(artistName))
        {
            BioSummaryUnavailableText = "There is not enough artist context to generate a description yet.";
            return;
        }

        _bioSummaryCts?.Cancel();
        var cts = _bioSummaryCts = new CancellationTokenSource();

        try
        {
            IsBioSummaryLoading = true;
            BioSummaryText = null;
            WasLastBioFromCache = false;
            IsBioSummaryStreaming = false;
            BioSummaryUnavailableText = null;

            var topTrackNames = _topTrackNamesProvider();
            var monthlyListeners = _monthlyListenersProvider();
            var monthlyDisplay = string.IsNullOrEmpty(monthlyListeners)
                ? null
                : $"{monthlyListeners} monthly listeners";
            var streamedText = string.Empty;
            var streamCompleted = 0;
            var progress = new Progress<string>(delta =>
            {
                if (cts.IsCancellationRequested
                    || Interlocked.CompareExchange(ref streamCompleted, 0, 0) != 0)
                {
                    return;
                }

                streamedText = MergeStreamingText(streamedText, delta);
                var preview = streamedText.TrimStart();
                if (string.IsNullOrWhiteSpace(preview))
                    return;

                RunOnDispatcher(() =>
                {
                    if (cts.IsCancellationRequested
                        || Interlocked.CompareExchange(ref streamCompleted, 0, 0) != 0)
                    {
                        return;
                    }

                    IsBioSummaryStreaming = true;
                    BioSummaryText = preview;
                });
            });

            var result = await _bioSummarizer.SummarizeBioAsync(
                artistId,
                artistName!,
                genres: null, // Not on overview today; passed when available
                monthlyListenersDisplay: monthlyDisplay,
                topTrackNames: topTrackNames,
                deltaProgress: progress,
                ct: cts.Token);

            if (cts.IsCancellationRequested) return;
            Interlocked.Exchange(ref streamCompleted, 1);
            if (result.Kind == LyricsAiResultKind.Ok)
            {
                WasLastBioFromCache = result.FromCache;
                BioSummaryText = result.Text;
                IsBioSummaryStreaming = false;
            }
            else
            {
                IsBioSummaryStreaming = false;
                BioSummaryUnavailableText = result.Kind switch
                {
                    LyricsAiResultKind.Filtered => "The on-device model could not describe this artist safely.",
                    LyricsAiResultKind.Unavailable => "On-device AI is not available for this artist right now.",
                    LyricsAiResultKind.Empty => "There is not enough artist context to generate a description yet.",
                    _ => "The on-device model could not describe this artist right now.",
                };
            }
        }
        catch (OperationCanceledException) { /* artist switched */ }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LoadBioSummaryAsync failed for {ArtistId}", artistId);
            if (!cts.IsCancellationRequested)
            {
                IsBioSummaryStreaming = false;
                BioSummaryUnavailableText = "The on-device model could not describe this artist right now.";
            }
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                IsBioSummaryStreaming = false;
                IsBioSummaryLoading = false;
            }
        }
    }

    private void RunOnDispatcher(Action action)
    {
        var dispatcher = _dispatcherQueue;
        if (dispatcher is null || dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        if (!dispatcher.TryEnqueue(() => action()))
            action();
    }

    private static string MergeStreamingText(string current, string delta)
    {
        if (string.IsNullOrEmpty(delta))
            return current;

        var next = delta.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (string.IsNullOrEmpty(current))
            return next;

        if (next.StartsWith(current, StringComparison.Ordinal))
            return next;

        if (current.EndsWith(next, StringComparison.Ordinal))
            return current;

        return current + next;
    }

    public void Dispose()
    {
        _bioSummaryCts?.Cancel();
        _bioSummaryCts?.Dispose();
        _bioSummaryCts = null;
    }
}
