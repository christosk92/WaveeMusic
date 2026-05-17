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
/// peek-line projection, and the optional on-device AI summary that runs
/// when Spotify ships no biography (Copilot+ + opt-in gated, all the work
/// hidden inside <see cref="ArtistBioSummarizer"/>).
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
    private readonly ILogger? _logger;

    private readonly Func<string?> _biographyProvider;
    private readonly Func<string?> _artistNameProvider;
    private readonly Func<string?> _monthlyListenersProvider;
    private readonly Func<IReadOnlyList<string>> _topTrackNamesProvider;

    private CancellationTokenSource? _bioSummaryCts;

    public ArtistBioViewModel(
        ArtistBioSummarizer? bioSummarizer,
        ILogger? logger,
        Func<string?> biographyProvider,
        Func<string?> artistNameProvider,
        Func<string?> monthlyListenersProvider,
        Func<IReadOnlyList<string>> topTrackNamesProvider)
    {
        _bioSummarizer = bioSummarizer;
        _logger = logger;
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
    [NotifyPropertyChangedFor(nameof(HasAboutExcerpt))]
    [NotifyPropertyChangedFor(nameof(IsBioFromAi))]
    [NotifyPropertyChangedFor(nameof(BioExcerptText))]
    [NotifyPropertyChangedFor(nameof(HeroBioLine))]
    [NotifyPropertyChangedFor(nameof(HasHeroBioLine))]
    private string? _bioSummaryText;

    [ObservableProperty] private bool _isBioSummaryLoading;

    public bool HasBioSummary => !string.IsNullOrWhiteSpace(BioSummaryText);
    public bool HasAboutExcerpt => HasBioSummary;
    public bool IsBioFromAi => HasBioSummary && !HasBiography;

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
        OnPropertyChanged(nameof(IsBioFromAi));
    }

    public void ResetForNewArtist()
    {
        _bioSummaryCts?.Cancel();
        BioSummaryText = null;
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
        if (_bioSummarizer is null) return;
        if (string.IsNullOrEmpty(artistId)) return;

        var artistName = _artistNameProvider();
        if (string.IsNullOrEmpty(artistName)) return;

        _bioSummaryCts?.Cancel();
        var cts = _bioSummaryCts = new CancellationTokenSource();

        try
        {
            IsBioSummaryLoading = true;
            BioSummaryText = null;

            var topTrackNames = _topTrackNamesProvider();
            var monthlyListeners = _monthlyListenersProvider();
            var monthlyDisplay = string.IsNullOrEmpty(monthlyListeners)
                ? null
                : $"{monthlyListeners} monthly listeners";

            var result = await _bioSummarizer.SummarizeBioAsync(
                artistId,
                artistName!,
                genres: null, // Not on overview today; passed when available
                monthlyListenersDisplay: monthlyDisplay,
                topTrackNames: topTrackNames,
                deltaProgress: null,
                ct: cts.Token);

            if (cts.IsCancellationRequested) return;
            if (result.Kind == LyricsAiResultKind.Ok)
                BioSummaryText = result.Text;
        }
        catch (OperationCanceledException) { /* artist switched */ }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LoadBioSummaryAsync failed for {ArtistId}", artistId);
        }
        finally
        {
            if (!cts.IsCancellationRequested)
                IsBioSummaryLoading = false;
        }
    }

    public void Dispose()
    {
        _bioSummaryCts?.Cancel();
        _bioSummaryCts?.Dispose();
        _bioSummaryCts = null;
    }
}
