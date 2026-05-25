using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Wavee.UI.Helpers;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Drives the on-device-AI affordance on the expanded now-playing lyrics view.
/// One command — <see cref="SummarizeSongCommand"/> — sends the whole lyric to
/// <see cref="LyricsAiService.GetLyricsMeaningAsync"/>. Gated by
/// <see cref="AiCapabilities.IsLyricsSummarizeEnabled"/>; the bound
/// <see cref="IsSummarizeAvailable"/> drives affordance visibility in XAML.
///
/// Generation lifecycle: the button itself owns the in-flight state. Clicking
/// while busy cancels the running task. Switching tracks cancels via
/// <see cref="OnLyricsPropertyChanged"/>. The result card stays hidden until
/// the await returns with a real result — never during the busy phase.
/// </summary>
public sealed partial class LyricsAiPanelViewModel : ObservableObject, IDisposable
{
    private readonly LyricsViewModel _lyrics;
    private readonly LyricsAiService _aiService;
    private readonly AiCapabilities _capabilities;
    private readonly ILogger? _logger;

    private CancellationTokenSource? _activeCts;
    private bool _disposed;

    public LyricsAiPanelViewModel(
        LyricsViewModel lyrics,
        LyricsAiService aiService,
        AiCapabilities capabilities,
        ILogger<LyricsAiPanelViewModel>? logger = null)
    {
        _lyrics = lyrics ?? throw new ArgumentNullException(nameof(lyrics));
        _aiService = aiService ?? throw new ArgumentNullException(nameof(aiService));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _logger = logger;

        _lyrics.PropertyChanged += OnLyricsPropertyChanged;
    }

    /// <summary>True if Phi Silica is available and the user opted in.</summary>
    public bool IsAnyAiAvailable => _capabilities.IsAiAvailableAndEnabled;

    public bool IsSummarizeAvailable => _capabilities.IsLyricsSummarizeEnabled && _lyrics.HasLyrics;

    /// <summary>
    /// True while the meaning generation is in flight. Drives the spinner +
    /// label swap on the affordance button. The result card never renders off
    /// this — it waits for <see cref="HasResult"/>, which is only set once the
    /// await returns with a real result.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    [NotifyPropertyChangedFor(nameof(SummarizeButtonLabel))]
    [NotifyPropertyChangedFor(nameof(SummarizeButtonTooltip))]
    private bool _isBusy;

    public bool IsNotBusy => !IsBusy;

    public string SummarizeButtonLabel => IsBusy ? "Stop" : "Lyrics meaning";
    public string SummarizeButtonTooltip => IsBusy ? "Stop generation" : "Interpret the lyrics on-device";

    [ObservableProperty]
    private string _resultText = string.Empty;

    [ObservableProperty]
    private string _resultCaption = string.Empty;

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private string _sparkleState = "Normal";

    /// <summary>
    /// Compact (false, default) vs expanded (true) result card. The compact
    /// card is a small right-anchored acrylic chip; expanded fills the lyrics
    /// tab so multi-paragraph meanings are readable. Toggle via
    /// <see cref="ToggleExpandedCommand"/>. Resets on dismiss / track change.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandToggleGlyph))]
    [NotifyPropertyChangedFor(nameof(ExpandToggleLabel))]
    private bool _isResultExpanded;

    /// <summary>Chevron glyph that swaps with the expansion state. Bound from XAML.</summary>
    public string ExpandToggleGlyph =>
        IsResultExpanded ? FluentGlyphs.ChevronUp : FluentGlyphs.ChevronDown;

    /// <summary>Footer toggle label that swaps with the expansion state. Bound from XAML.</summary>
    public string ExpandToggleLabel =>
        IsResultExpanded ? "Show less" : "Show more";

    /// <summary>Static "x" dismiss glyph — exposed on the VM so XAML never inlines a PUA literal.</summary>
    public string DismissGlyph => FluentGlyphs.Cancel;

    [RelayCommand]
    private async Task SummarizeSongAsync()
    {
        // Click-while-busy = cancel, no restart.
        if (IsBusy)
        {
            CancelActive();
            return;
        }

        if (!_capabilities.IsLyricsSummarizeEnabled) return;

        var lyrics = _lyrics.CurrentLyrics;
        if (lyrics is null || lyrics.LyricsLines.Count == 0) return;

        var fullText = lyrics.WrappedOriginalText;
        if (string.IsNullOrWhiteSpace(fullText)) return;

        await RunGenerationAsync(
            captionOnDone: "Lyrics meaning",
            invoke: (progress, ct) => _aiService.GetLyricsMeaningAsync(
                trackUri: BuildTrackUri(_lyrics.PlaybackState.CurrentTrackId),
                fullLyric: fullText,
                deltaProgress: progress,
                ct: ct,
                trackTitle: _lyrics.CurrentSongInfo?.Title,
                artistName: _lyrics.CurrentSongInfo?.Artist));
    }

    /// <summary>
    /// Manually clears the result panel without invoking AI. Bound to a small "x"
    /// dismiss button on the result chrome.
    /// </summary>
    [RelayCommand]
    private void DismissResult()
    {
        CancelActive();
        IsBusy = false;
        ResultText = string.Empty;
        ResultCaption = string.Empty;
        HasResult = false;
        IsResultExpanded = false;
        SparkleState = "Normal";
    }

    [RelayCommand]
    private void ToggleExpanded() => IsResultExpanded = !IsResultExpanded;

    private async Task RunGenerationAsync(
        string captionOnDone,
        Func<IProgress<string>, CancellationToken, Task<LyricsAiResult>> invoke)
    {
        // Cancel any prior in-flight call (track-change also flows through
        // CancelActive via DismissResult). Result card stays hidden — HasResult
        // is only set once the await returns with a real result kind.
        CancelActive();
        var cts = _activeCts = new CancellationTokenSource();

        try
        {
            IsBusy = true;
            SparkleState = "Generating";
            ResultText = string.Empty;
            ResultCaption = "Generating";
            HasResult = false;
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

                ResultText = preview;
                ResultCaption = "Generating";
                HasResult = true;
            });

            var result = await invoke(progress, cts.Token);
            if (cts.IsCancellationRequested) return;
            Interlocked.Exchange(ref streamCompleted, 1);

            switch (result.Kind)
            {
                case LyricsAiResultKind.Ok:
                    ResultText = result.Text;
                    ResultCaption = captionOnDone + (result.FromCache ? " (cached)" : string.Empty);
                    SparkleState = "Done";
                    HasResult = true;
                    break;
                case LyricsAiResultKind.Filtered:
                    ResultText = "The on-device safety filter blocked this generation. Try a different lyric.";
                    ResultCaption = "Filtered";
                    SparkleState = "Normal";
                    HasResult = true;
                    break;
                case LyricsAiResultKind.Empty:
                    ResultText = string.Empty;
                    ResultCaption = "No lyrics available";
                    SparkleState = "Normal";
                    HasResult = true;
                    break;
                case LyricsAiResultKind.Unavailable:
                    ResultText = "On-device AI isn't available right now.";
                    ResultCaption = "Unavailable";
                    SparkleState = "Normal";
                    HasResult = true;
                    break;
                case LyricsAiResultKind.Error:
                    ResultText = "Something went wrong asking the on-device model.";
                    ResultCaption = "Error";
                    SparkleState = "Normal";
                    HasResult = true;
                    _logger?.LogWarning("Lyrics AI generation error: {Message}", result.ErrorMessage);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation (user toggled the busy button or switched tracks).
            // Leave HasResult untouched so no card pops up — the button reset
            // in `finally` is the only signal.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Lyrics AI generation threw unexpectedly.");
            ResultText = "Something went wrong asking the on-device model.";
            ResultCaption = "Error";
            SparkleState = "Normal";
            HasResult = true;
        }
        finally
        {
            IsBusy = false;
            if (cts == _activeCts) _activeCts = null;
            cts.Dispose();
        }
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

    private void CancelActive()
    {
        try
        {
            _activeCts?.Cancel();
        }
        catch
        {
            // Already disposed; harmless.
        }
        _activeCts = null;
    }

    private static string BuildTrackUri(string? trackId) =>
        string.IsNullOrEmpty(trackId)
            ? SpotifyUriHelper.ToUri(SpotifyEntityKind.Track, "unknown")
            : SpotifyUriHelper.IsKind(trackId, SpotifyEntityKind.Track)
                ? trackId
                : SpotifyUriHelper.ToUri(SpotifyEntityKind.Track, trackId);

    private void OnLyricsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LyricsViewModel.HasLyrics):
                OnPropertyChanged(nameof(IsSummarizeAvailable));
                break;
            case nameof(LyricsViewModel.CurrentLyrics):
                // New track → drop any in-flight result.
                DismissResult();
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelActive();
        _lyrics.PropertyChanged -= OnLyricsPropertyChanged;
    }
}
