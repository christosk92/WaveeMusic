using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Wavee.AI.Activity;
using Wavee.AI.Artists;
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
    private readonly ArtistAiQuestionService? _artistQuestionService;

    private readonly Func<string?> _biographyProvider;
    private readonly Func<string?> _artistUriProvider;
    private readonly Func<string?> _artistNameProvider;
    private readonly Func<string?> _monthlyListenersProvider;
    private readonly Func<IReadOnlyList<string>> _topTrackNamesProvider;

    private CancellationTokenSource? _bioSummaryCts;
    private CancellationTokenSource? _artistQuestionCts;
    private CancellationTokenSource? _suggestedQuestionsCts;
    private string? _suggestedQuestionsArtistUri;
    private bool _suppressAskAiSuggestionRefresh;

    public ArtistBioViewModel(
        ArtistBioSummarizer? bioSummarizer,
        AiCapabilities? capabilities,
        ArtistAiQuestionService? artistQuestionService,
        ILogger? logger,
        Func<string?> biographyProvider,
        Func<string?> artistUriProvider,
        Func<string?> artistNameProvider,
        Func<string?> monthlyListenersProvider,
        Func<IReadOnlyList<string>> topTrackNamesProvider)
    {
        _bioSummarizer = bioSummarizer;
        _capabilities = capabilities;
        _artistQuestionService = artistQuestionService;
        _logger = logger;
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _biographyProvider = biographyProvider;
        _artistUriProvider = artistUriProvider;
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

    public ObservableCollection<AiActivityEvent> AskAiActivity { get; } = new();

    public bool HasAskAiActivity => AskAiActivity.Count > 0;

    public ObservableCollection<ArtistAskAiRecommendationVm> AskAiRecommendations { get; } = new();

    public bool HasAskAiRecommendations => AskAiRecommendations.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAskArtistAi))]
    private string _askAiQuestionText = string.Empty;

    [ObservableProperty]
    private string _askAiAnswerText = string.Empty;

    [ObservableProperty]
    private string _askAiCaption = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAskArtistAi))]
    private bool _isAskAiBusy;

    [ObservableProperty]
    private bool _hasAskAiAnswer;

    [ObservableProperty]
    private string _askAiSparkleState = "Normal";

    public bool CanAskArtistAi =>
        IsAiBioCardVisible
        && !IsAskAiBusy
        && !string.IsNullOrWhiteSpace(AskAiQuestionText);

    public ObservableCollection<string> AskAiSuggestedQuestions { get; } = new();

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
        OnPropertyChanged(nameof(CanAskArtistAi));
        AskArtistAiCommand.NotifyCanExecuteChanged();
        if (!_suppressAskAiSuggestionRefresh)
            RefreshAskAiSuggestedQuestions();
    }

    public void ResetForNewArtist()
    {
        _bioSummaryCts?.Cancel();
        _artistQuestionCts?.Cancel();
        _suggestedQuestionsCts?.Cancel();
        _suggestedQuestionsArtistUri = null;
        BioSummaryText = null;
        WasLastBioFromCache = false;
        IsBioSummaryStreaming = false;
        BioSummaryUnavailableText = null;
        IsBioSummaryLoading = false;
        AskAiQuestionText = string.Empty;
        AskAiAnswerText = string.Empty;
        AskAiCaption = string.Empty;
        IsAskAiBusy = false;
        HasAskAiAnswer = false;
        AskAiSparkleState = "Normal";
        AskAiActivity.Clear();
        OnPropertyChanged(nameof(HasAskAiActivity));
        AskAiRecommendations.Clear();
        OnPropertyChanged(nameof(HasAskAiRecommendations));
        AskAiSuggestedQuestions.Clear();
        _suppressAskAiSuggestionRefresh = true;
        try
        {
            NotifyBiographyChanged();
        }
        finally
        {
            _suppressAskAiSuggestionRefresh = false;
        }
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

    private void ApplyAskAiRecommendations(IReadOnlyList<ArtistAiRecommendation>? recommendations)
    {
        AskAiRecommendations.Clear();
        if (recommendations is not null)
        {
            foreach (var item in recommendations)
                AskAiRecommendations.Add(ArtistAskAiRecommendationVm.From(item));
        }

        OnPropertyChanged(nameof(HasAskAiRecommendations));
    }

    private void RefreshAskAiSuggestedQuestions()
    {
        var artistUri = _artistUriProvider();
        var artistName = _artistNameProvider();
        if (string.IsNullOrWhiteSpace(artistUri) || string.IsNullOrWhiteSpace(artistName))
            return;

        ApplyAskAiSuggestedQuestions(BuildFallbackAskAiSuggestions(artistName));

        if (_artistQuestionService is null || !IsAiBioCardVisible)
            return;
        if (string.Equals(_suggestedQuestionsArtistUri, artistUri, StringComparison.Ordinal))
            return;

        _suggestedQuestionsArtistUri = artistUri;
        _suggestedQuestionsCts?.Cancel();
        _suggestedQuestionsCts?.Dispose();
        var cts = _suggestedQuestionsCts = new CancellationTokenSource();

        _ = LoadGeneratedAskAiSuggestionsAsync(artistUri, artistName, cts);
    }

    private async Task LoadGeneratedAskAiSuggestionsAsync(
        string artistUri,
        string artistName,
        CancellationTokenSource cts)
    {
        try
        {
            var suggestions = await _artistQuestionService!.SuggestQuestionsAsync(
                new ArtistAiSuggestionRequest(
                    artistUri,
                    artistName,
                    Biography,
                    _topTrackNamesProvider()),
                cts.Token).ConfigureAwait(false);

            if (cts.IsCancellationRequested || suggestions.Count == 0)
                return;

            RunOnDispatcher(() =>
            {
                if (!cts.IsCancellationRequested)
                    ApplyAskAiSuggestedQuestions(MergeSuggestions(suggestions, BuildFallbackAskAiSuggestions(artistName)));
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to generate artist Ask AI suggestions.");
        }
        finally
        {
            if (cts == _suggestedQuestionsCts)
                _suggestedQuestionsCts = null;
            cts.Dispose();
        }
    }

    private void ApplyAskAiSuggestedQuestions(IEnumerable<string> suggestions)
    {
        AskAiSuggestedQuestions.Clear();
        foreach (var suggestion in suggestions
                     .Select(s => s.Trim())
                     .Where(s => s.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Take(4))
        {
            AskAiSuggestedQuestions.Add(suggestion);
        }
    }

    private static IReadOnlyList<string> MergeSuggestions(
        IReadOnlyList<string> generated,
        IReadOnlyList<string> fallback)
    {
        var merged = new List<string>(4);
        foreach (var suggestion in generated.Concat(fallback))
        {
            if (string.IsNullOrWhiteSpace(suggestion)
                || suggestion.Contains("their", StringComparison.OrdinalIgnoreCase)
                || merged.Contains(suggestion, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            merged.Add(suggestion);
            if (merged.Count >= 4)
                break;
        }

        return merged;
    }

    private static IReadOnlyList<string> BuildFallbackAskAiSuggestions(string artistName)
    {
        var name = artistName.Trim();
        if (string.IsNullOrEmpty(name))
            name = "this artist";

        return
        [
            $"What is {name}'s best song?",
            $"Show me {name}'s oldest songs",
            $"Give me lesser-known {name} tracks",
            $"Where should I start with {name}?"
        ];
    }

    [RelayCommand(CanExecute = nameof(CanAskArtistAi))]
    private async Task AskArtistAiAsync()
    {
        if (_artistQuestionService is null)
            return;

        var artistUri = _artistUriProvider();
        var artistName = _artistNameProvider();
        var question = AskAiQuestionText.Trim();
        if (string.IsNullOrWhiteSpace(artistUri)
            || string.IsNullOrWhiteSpace(artistName)
            || string.IsNullOrWhiteSpace(question))
        {
            return;
        }

        _artistQuestionCts?.Cancel();
        var cts = _artistQuestionCts = new CancellationTokenSource();

        try
        {
            IsAskAiBusy = true;
            AskAiSparkleState = "Generating";
            AskAiCaption = "Thinking";
            AskAiAnswerText = string.Empty;
            HasAskAiAnswer = false;
            AskAiActivity.Clear();
            OnPropertyChanged(nameof(HasAskAiActivity));
            AskAiRecommendations.Clear();
            OnPropertyChanged(nameof(HasAskAiRecommendations));

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

                    AskAiAnswerText = preview;
                    AskAiCaption = "Writing";
                    HasAskAiAnswer = true;
                });
            });

            var result = await _artistQuestionService.AskAsync(
                new ArtistAiQuestionRequest(
                    artistUri,
                    artistName,
                    question,
                    Biography,
                    _topTrackNamesProvider(),
                    progress,
                    new DelegateAiActivitySink(activity =>
                        RunOnDispatcher(() =>
                        {
                            AskAiActivity.Add(activity);
                            OnPropertyChanged(nameof(HasAskAiActivity));
                        }))),
                cts.Token);

            if (cts.IsCancellationRequested)
                return;

            Interlocked.Exchange(ref streamCompleted, 1);
            AskAiSparkleState = "Done";
            HasAskAiAnswer = true;
            switch (result.Kind)
            {
                case ArtistAiQuestionResultKind.Ok:
                    AskAiAnswerText = result.Text;
                    AskAiCaption = "Answer";
                    ApplyAskAiRecommendations(result.Recommendations);
                    break;
                case ArtistAiQuestionResultKind.Filtered:
                    AskAiAnswerText = "The on-device safety filter blocked this artist answer.";
                    AskAiCaption = "Filtered";
                    AskAiSparkleState = "Normal";
                    break;
                case ArtistAiQuestionResultKind.Empty:
                    AskAiAnswerText = "Ask a question about this artist first.";
                    AskAiCaption = "Empty";
                    AskAiSparkleState = "Normal";
                    break;
                case ArtistAiQuestionResultKind.Unavailable:
                    AskAiAnswerText = result.ErrorMessage ?? "Artist AI is not available right now.";
                    AskAiCaption = "Unavailable";
                    AskAiSparkleState = "Normal";
                    break;
                case ArtistAiQuestionResultKind.Error:
                    AskAiAnswerText = "Something went wrong asking the on-device model.";
                    AskAiCaption = "Error";
                    AskAiSparkleState = "Normal";
                    _logger?.LogWarning("Artist AI question failed: {Message}", result.ErrorMessage);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Artist AI question threw unexpectedly.");
            AskAiAnswerText = "Something went wrong asking the on-device model.";
            AskAiCaption = "Error";
            AskAiSparkleState = "Normal";
            HasAskAiAnswer = true;
        }
        finally
        {
            IsAskAiBusy = false;
            if (cts == _artistQuestionCts)
                _artistQuestionCts = null;
            cts.Dispose();
        }
    }

    [RelayCommand]
    private void UseAskAiSuggestion(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
            return;

        AskAiQuestionText = question;
    }

    partial void OnAskAiQuestionTextChanged(string value)
    {
        AskArtistAiCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsAskAiBusyChanged(bool value)
    {
        AskArtistAiCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _bioSummaryCts?.Cancel();
        _bioSummaryCts?.Dispose();
        _bioSummaryCts = null;
        _artistQuestionCts?.Cancel();
        _artistQuestionCts?.Dispose();
        _artistQuestionCts = null;
        _suggestedQuestionsCts?.Cancel();
        _suggestedQuestionsCts?.Dispose();
        _suggestedQuestionsCts = null;
    }

    private sealed class DelegateAiActivitySink : IAiActivitySink
    {
        private readonly Action<AiActivityEvent> _onActivity;

        public DelegateAiActivitySink(Action<AiActivityEvent> onActivity)
        {
            _onActivity = onActivity;
        }

        public void Report(AiActivityEvent activity) => _onActivity(activity);
    }
}

public sealed record ArtistAskAiRecommendationVm(
    ArtistAiRecommendationKind Kind,
    string Title,
    string? Subtitle,
    string? Uri,
    string? ImageUrl,
    string? ContextUri,
    string? Reason)
{
    public bool IsTrack => Kind == ArtistAiRecommendationKind.Track;
    public bool IsRelease => Kind == ArtistAiRecommendationKind.Release;
    public string KindLabel => IsTrack ? "TRACK" : "RELEASE";

    public static ArtistAskAiRecommendationVm From(ArtistAiRecommendation item)
        => new(
            item.Kind,
            item.Title,
            item.Subtitle,
            item.Uri,
            item.ImageUrl,
            item.ContextUri,
            item.Reason);
}
