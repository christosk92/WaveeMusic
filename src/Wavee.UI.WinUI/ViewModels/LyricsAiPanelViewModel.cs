using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Wavee.Controls.Lyrics.Models.Lyrics;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Drives the on-device-AI affordances on the expanded now-playing lyrics view.
/// Two commands:
///   - <see cref="ExplainCurrentLineCommand"/> — sends the currently synced line
///     (resolved from <see cref="LyricsViewModel.LastServicePosition"/>) to
///     <see cref="LyricsAiService.ExplainLineAsync"/>.
///   - <see cref="SummarizeSongCommand"/> — sends the whole lyric to
///     <see cref="LyricsAiService.GetLyricsMeaningAsync"/>.
///
/// Both are gated by <see cref="AiCapabilities.IsLyricsExplainEnabled"/> /
/// <see cref="AiCapabilities.IsLyricsSummarizeEnabled"/>; the bound
/// <see cref="IsExplainAvailable"/> / <see cref="IsSummarizeAvailable"/>
/// drive the affordance visibility in XAML.
///
/// Owns its own cancellation token: tapping a different command cancels the
/// previous in-flight request so the UI never gets stuck on a stale generation.
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

    /// <summary>True if Phi Silica is available and the user opted in. Per-feature
    /// gate is on <see cref="IsExplainAvailable"/> / <see cref="IsSummarizeAvailable"/>.</summary>
    public bool IsAnyAiAvailable => _capabilities.IsAiAvailableAndEnabled;

    public bool IsExplainAvailable => _capabilities.IsLyricsExplainEnabled && _lyrics.HasLyrics;
    public bool IsSummarizeAvailable => _capabilities.IsLyricsSummarizeEnabled && _lyrics.HasLyrics;

    [ObservableProperty]
    private bool _isBusy;

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
    private async Task ExplainCurrentLineAsync()
    {
        if (!_capabilities.IsLyricsExplainEnabled) return;

        var lyrics = _lyrics.CurrentLyrics;
        if (lyrics is null || lyrics.LyricsLines.Count == 0) return;

        var (_, lineIndex) = ResolveCurrentLine(lyrics);
        await ExplainLineAtIndexAsync(lineIndex);
    }

    public async Task ExplainLineAtIndexAsync(int lineIndex)
    {
        if (!_capabilities.IsLyricsExplainEnabled) return;

        var lyrics = _lyrics.CurrentLyrics;
        if (lyrics is null || lyrics.LyricsLines.Count == 0) return;
        if (lineIndex < 0 || lineIndex >= lyrics.LyricsLines.Count) return;

        var line = lyrics.LyricsLines[lineIndex];
        var lineText = SnapshotText(line?.PrimaryText);
        if (string.IsNullOrWhiteSpace(lineText))
        {
            ResultCaption = "No lyric line is active right now";
            ResultText = string.Empty;
            HasResult = true;
            return;
        }

        var fullText = SnapshotText(lyrics.WrappedOriginalText);
        if (string.IsNullOrWhiteSpace(fullText))
            fullText = lineText;

        var trackUri = BuildTrackUri(_lyrics.PlaybackState.CurrentTrackId);

        await RunGenerationAsync(
            captionWhileBusy: $"Explaining: \"{Truncate(lineText, 60)}\"",
            captionOnDone: "AI interpretation",
            invoke: (deltaProgress, ct) => _aiService.ExplainLineAsync(
                trackUri: trackUri,
                lineIndex: lineIndex,
                line: lineText,
                fullLyric: fullText,
                deltaProgress: deltaProgress,
                ct: ct));
    }

    [RelayCommand]
    private async Task SummarizeSongAsync()
    {
        if (!_capabilities.IsLyricsSummarizeEnabled) return;

        var lyrics = _lyrics.CurrentLyrics;
        if (lyrics is null || lyrics.LyricsLines.Count == 0) return;

        var fullText = lyrics.WrappedOriginalText;
        if (string.IsNullOrWhiteSpace(fullText)) return;

        await RunGenerationAsync(
            captionWhileBusy: "Finding lyrics meaning",
            captionOnDone: "Lyrics meaning",
            invoke: (deltaProgress, ct) => _aiService.GetLyricsMeaningAsync(
                trackUri: BuildTrackUri(_lyrics.PlaybackState.CurrentTrackId),
                fullLyric: fullText,
                deltaProgress: deltaProgress,
                ct: ct));
    }

    /// <summary>
    /// Manually clears the result panel without invoking AI. Bound to a small "x"
    /// dismiss button on the result chrome.
    /// </summary>
    [RelayCommand]
    private void DismissResult()
    {
        CancelActive();
        ResultText = string.Empty;
        ResultCaption = string.Empty;
        HasResult = false;
        IsResultExpanded = false;
        SparkleState = "Normal";
    }

    [RelayCommand]
    private void ToggleExpanded() => IsResultExpanded = !IsResultExpanded;

    private async Task RunGenerationAsync(
        string captionWhileBusy,
        string captionOnDone,
        Func<IProgress<string>, CancellationToken, Task<LyricsAiResult>> invoke)
    {
        CancelActive();
        var cts = _activeCts = new CancellationTokenSource();

        try
        {
            IsBusy = true;
            HasResult = true;
            ResultCaption = captionWhileBusy;
            ResultText = string.Empty;
            SparkleState = "Generating";

            // Progress<T> captures the current SynchronizationContext at
            // construction time. We always run this method on the UI thread
            // (RelayCommand handlers are dispatched there), so each delta
            // hops back to the UI thread automatically — no manual
            // DispatcherQueue.TryEnqueue needed.
            //
            // Cancellation: ignore late deltas after the user moved on. The
            // generation token is cooperative on the service side, but a
            // racing delta can still land between cancellation and op.Cancel
            // taking effect.
            var streamProgress = new Progress<string>(delta =>
            {
                if (cts.IsCancellationRequested) return;
                if (!string.IsNullOrEmpty(delta))
                    ResultText += delta;
            });

            var result = await invoke(streamProgress, cts.Token);
            if (cts.IsCancellationRequested) return;

            switch (result.Kind)
            {
                case LyricsAiResultKind.Ok:
                    // Defensive replace: if the WinRT API ever shifts to
                    // cumulative deltas (or returns a slightly different final
                    // string than the concatenated stream), prefer the canonical
                    // final text. Cache-replay path also lands here with
                    // FromCache=true and ResultText still empty (no streaming
                    // happened) — the assignment renders cached text instantly.
                    ResultText = result.Text;
                    ResultCaption = captionOnDone + (result.FromCache ? " (cached)" : string.Empty);
                    SparkleState = "Done";
                    break;
                case LyricsAiResultKind.Filtered:
                    ResultText = "The on-device safety filter blocked this generation. Try a different lyric.";
                    ResultCaption = "Filtered";
                    SparkleState = "Normal";
                    break;
                case LyricsAiResultKind.Empty:
                    ResultText = string.Empty;
                    ResultCaption = "No lyrics available";
                    SparkleState = "Normal";
                    break;
                case LyricsAiResultKind.Unavailable:
                    ResultText = "On-device AI isn't available right now.";
                    ResultCaption = "Unavailable";
                    SparkleState = "Normal";
                    break;
                case LyricsAiResultKind.Error:
                    ResultText = "Something went wrong asking the on-device model.";
                    ResultCaption = "Error";
                    SparkleState = "Normal";
                    _logger?.LogWarning("Lyrics AI generation error: {Message}", result.ErrorMessage);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // User triggered another action; swallow.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Lyrics AI generation threw unexpectedly.");
            ResultText = "Something went wrong asking the on-device model.";
            ResultCaption = "Error";
            SparkleState = "Normal";
        }
        finally
        {
            IsBusy = false;
            if (cts == _activeCts) _activeCts = null;
            cts.Dispose();
        }
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

    private (LyricsLine? line, int index) ResolveCurrentLine(LyricsData lyrics)
    {
        // Use the same interpolation the canvas uses: last service position +
        // wall-clock delta since the last position update. Phi Silica is fast
        // enough that this won't drift visibly.
        var basePos = _lyrics.LastServicePosition;
        var delta = (DateTime.UtcNow - _lyrics.LastPositionTimestamp).TotalMilliseconds;
        var nowMs = basePos + Math.Max(0, delta);

        for (int i = 0; i < lyrics.LyricsLines.Count; i++)
        {
            var line = lyrics.LyricsLines[i];
            var endMs = line.EndMs ?? (i + 1 < lyrics.LyricsLines.Count
                ? lyrics.LyricsLines[i + 1].StartMs
                : int.MaxValue);

            if (line.StartMs <= nowMs && nowMs < endMs)
                return (line, i);
        }

        // Fallback to the first non-empty line if we can't place the cursor
        // (e.g. lyrics with no time info, or position is past the last line).
        var first = lyrics.LyricsLines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.PrimaryText));
        var firstIndex = first is null ? 0 : lyrics.LyricsLines.IndexOf(first);
        return (first, firstIndex);
    }

    private static string BuildTrackUri(string? trackId) =>
        string.IsNullOrEmpty(trackId) ? "spotify:track:unknown" : $"spotify:track:{trackId}";

    private static string SnapshotText(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : new string(text.AsSpan());

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= max ? s : s[..max].TrimEnd() + "…";
    }

    private void OnLyricsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LyricsViewModel.HasLyrics):
                OnPropertyChanged(nameof(IsExplainAvailable));
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
