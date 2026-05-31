using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Wavee.Core.Playlists;
using Wavee.UI.Contracts;
using Wavee.UI.Services;
using Wavee.UI.Services.Playlists;
using Wavee.UI.Services.Tracks;
using Wavee.UI.WinUI.Controls.Swipe;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers.Navigation;
using Windows.UI;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Drives the "Refresh with swipes" page: wraps a <see cref="RefreshPlaylistSession"/>, plays 30s
/// snippets through the isolated preview coordinator, morphs the immersive background from each
/// card's palette, persists progress after every decision, and exposes the review / done commands.
/// </summary>
public sealed partial class RefreshPlaylistViewModel : ObservableObject
{
    private const double SnippetSeconds = 30;
    private static readonly Color DefaultPrimary = Color.FromArgb(255, 0x32, 0x2a, 0x4a);
    private static readonly Color DefaultAccent = Color.FromArgb(255, 0x24, 0x3a, 0x46);

    private readonly ILibraryDataService _library;
    private readonly IPlaylistCacheService _playlistCache;
    private readonly IPlaylistMutationService _mutation;
    private readonly IPreviewUrlResolver _previewUrls;
    private readonly ITrackColorResolver _colors;
    private readonly ICardPreviewPlaybackCoordinator _previews;
    private readonly IRefreshSessionStore _store;
    private readonly ILogger<RefreshPlaylistViewModel>? _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _clock;
    private readonly Guid _ownerId = Guid.NewGuid();

    private RefreshPlaylistSession? _session;
    private string _playlistId = "";
    private string? _currentCardUri;
    private long _playStartTicks;

    public RefreshPlaylistViewModel(
        ILibraryDataService library,
        IPlaylistCacheService playlistCache,
        IPlaylistMutationService mutation,
        IPreviewUrlResolver previewUrls,
        ITrackColorResolver colors,
        ICardPreviewPlaybackCoordinator previews,
        IRefreshSessionStore store,
        ILogger<RefreshPlaylistViewModel>? logger = null)
    {
        _library = library;
        _playlistCache = playlistCache;
        _mutation = mutation;
        _previewUrls = previewUrls;
        _colors = colors;
        _previews = previews;
        _store = store;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _clock = _dispatcher.CreateTimer();
        _clock.Interval = TimeSpan.FromMilliseconds(100);
        _clock.Tick += (_, _) => TickClock();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAuditioning), nameof(ShowEmptyState))]
    public partial bool IsLoading { get; set; } = true;
    [ObservableProperty] public partial string PlaylistName { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAuditioning))]
    public partial RefreshCard? CurrentCard { get; set; }
    [ObservableProperty] public partial RefreshCard? PeekCard1 { get; set; }
    [ObservableProperty] public partial RefreshCard? PeekCard2 { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAuditioning), nameof(IsReview), nameof(IsDone))]
    public partial RefreshPhase Phase { get; set; } = RefreshPhase.Auditioning;
    [ObservableProperty] public partial SwipePreviewState PreviewState { get; set; }
    [ObservableProperty] public partial double PreviewProgress { get; set; }
    [ObservableProperty] public partial int KeptCount { get; set; }
    [ObservableProperty] public partial int RemovedCount { get; set; }
    [ObservableProperty] public partial int RemainingCount { get; set; }
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial Color BackgroundPrimary { get; set; } = DefaultPrimary;
    [ObservableProperty] public partial Color BackgroundAccent { get; set; } = DefaultAccent;
    [ObservableProperty] public partial bool ShowHowItWorks { get; set; }
    [ObservableProperty] public partial bool ShowReconcileBanner { get; set; }
    [ObservableProperty] public partial string ReconcileText { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAuditioning), nameof(ShowEmptyState))]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAuditioning), nameof(ShowEmptyState))]
    public partial bool HasError { get; set; }

    [ObservableProperty] public partial string EmptyMessage { get; set; } = "There are no tracks here to refresh.";

    public ObservableCollection<RefreshCard> RemovedCards { get; } = new();
    public ObservableCollection<RefreshCard> UpNext { get; } = new();

    public bool HasStagedDecisions => _session?.HasStagedDecisions ?? false;
    public bool IsAuditioning => Phase == RefreshPhase.Auditioning && !IsLoading && !IsEmpty && !HasError && CurrentCard is not null;
    public bool ShowEmptyState => !IsLoading && (IsEmpty || HasError);
    public bool IsReview => Phase == RefreshPhase.Review;
    public bool IsDone => Phase == RefreshPhase.Done;

    public async Task LoadAsync(RefreshPlaylistParameter p)
    {
        PlaylistName = p.PlaylistName;
        _playlistId = p.PlaylistId;
        IsLoading = true;
        try
        {
            string? baseRevision = null;
            try
            {
                var cached = await _playlistCache.GetPlaylistAsync(p.PlaylistId, forceRefresh: true);
                baseRevision = Convert.ToBase64String(cached.Revision);
            }
            catch (Exception ex) { _logger?.LogDebug(ex, "Refresh load: force-refresh failed for {Id}", p.PlaylistId); }

            var tracks = await _library.GetPlaylistTracksAsync(p.PlaylistId);
            var saved = await _store.LoadAsync(p.PlaylistId);

            if (saved is null)
            {
                _session = RefreshPlaylistSession.Start(p.PlaylistId, tracks, baseRevision, _mutation);
            }
            else
            {
                _session = RefreshPlaylistSession.Resume(tracks, baseRevision, saved, _mutation);
                if (_session.LastDiff.HasChanges)
                {
                    ReconcileText = BuildReconcileText(_session.LastDiff);
                    ShowReconcileBanner = true;
                }
            }

            _session.StateChanged += OnSessionChanged;
            IsEmpty = _session.Phase == RefreshPhase.Empty;
            // Always open on the welcome card — nothing plays until the user starts.
            ShowHowItWorks = _session.Phase == RefreshPhase.Auditioning;
            await PersistAsync();
            SyncFromSession();
            _ = UpdateBackgroundAsync(_session.CurrentCard);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Refresh load failed for {Id}", p.PlaylistId);
            EmptyMessage = "Couldn't load this playlist to refresh. Go back and try again.";
            HasError = true;
        }
        finally { IsLoading = false; }
    }

    private static string BuildReconcileText(RefreshDiffSummary d)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (d.Added > 0) parts.Add($"{d.Added} added");
        if (d.Removed > 0) parts.Add($"{d.Removed} removed");
        return "This playlist changed on another device — " + string.Join(", ", parts) + ". Reconciled.";
    }

    // ── session sync + persistence ──

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.HasThreadAccess) Apply();
        else _dispatcher.TryEnqueue(Apply);

        void Apply()
        {
            SyncFromSession();
            if (_session is { Phase: RefreshPhase.Done }) _ = _store.ClearAsync(_playlistId);
            else if (_session is { Phase: not RefreshPhase.Applying }) _ = PersistAsync();
        }
    }

    private Task PersistAsync()
        => _session is null ? Task.CompletedTask : _store.SaveAsync(_session.Snapshot(), _session.RemainingCount);

    private void SyncFromSession()
    {
        if (_session is null) return;
        var deck = _session.Deck;
        var cursor = _session.CurrentIndex;

        CurrentCard = _session.CurrentCard;
        PeekCard1 = cursor + 1 < deck.Count ? deck[cursor + 1] : null;
        PeekCard2 = cursor + 2 < deck.Count ? deck[cursor + 2] : null;
        Phase = _session.Phase;
        KeptCount = _session.KeptCount;
        RemovedCount = _session.RemovedCount;
        RemainingCount = _session.RemainingCount;
        Progress = deck.Count == 0 ? 0 : Math.Clamp((double)cursor / deck.Count, 0, 1);

        RemovedCards.Clear();
        foreach (var c in _session.RemovedCards) RemovedCards.Add(c);
        UpNext.Clear();
        foreach (var c in _session.UpNext(3)) UpNext.Add(c);

        if (CurrentCard?.Uri != _currentCardUri)
        {
            _currentCardUri = CurrentCard?.Uri;
            StopSnippet();
            if (_session.Phase == RefreshPhase.Auditioning)
            {
                _ = UpdateBackgroundAsync(CurrentCard);                 // palette is prefetched → resolves instantly
                ResolveAvailability(CurrentCard);   // shows "preview unavailable" if needed — does NOT play
                _ = PrefetchAsync();
            }
        }
    }

    /// <summary>
    /// Determines whether the current card has a snippet (so the card can show "preview unavailable")
    /// without starting playback. Snippets only play on an explicit play / Space.
    /// </summary>
    private async void ResolveAvailability(RefreshCard? card)
    {
        PreviewProgress = 0;
        PreviewState = SwipePreviewState.None;
        if (card is null) return;
        try
        {
            var url = await _previewUrls.ResolveAsync(card.Uri);
            if (_session?.CurrentCard?.Uri != card.Uri) return;   // advanced while resolving
            PreviewState = string.IsNullOrEmpty(url) ? SwipePreviewState.Unavailable : SwipePreviewState.None;
        }
        catch (Exception ex) { _logger?.LogDebug(ex, "Preview availability check failed for {Uri}", card.Uri); }
    }

    // ── page-invoked deck operations ──

    public void CommitInDirection(SwipeDirection dir) { StopSnippet(); _session?.Decide(dir); }
    public void SkipCurrent() { StopSnippet(); _session?.Skip(); }
    public void UndoLast() { StopSnippet(); _session?.UndoLast(); }
    public void Finish() { StopSnippet(); _session?.Finish(); }

    // ── commands ──

    [RelayCommand] private void ToggleSnippet() => ToggleCurrentSnippet();

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (_session is null) return;
        try { await _playlistCache.GetPlaylistAsync(_playlistId, forceRefresh: true); } catch { /* apply against cache anyway */ }
        await _session.ApplyAsync();   // OnSessionChanged advances Phase → Done + clears the saved session
    }

    [RelayCommand]
    private async Task DiscardAsync()
    {
        StopSnippet();
        await _store.ClearAsync(_playlistId);
        NavigationHelpers.OpenPlaylist(_playlistId, PlaylistName);
    }

    [RelayCommand] private void StartOver() { _session?.Restart(); ShowReconcileBanner = false; }

    [RelayCommand] private void ViewPlaylist() { StopSnippet(); NavigationHelpers.OpenPlaylist(_playlistId, PlaylistName); }

    [RelayCommand] private void UnRemove(string? uri) { if (!string.IsNullOrEmpty(uri)) _session?.UnRemove(uri); }

    [RelayCommand]
    private void DismissHowItWorks() => ShowHowItWorks = false;   // start auditioning; snippet plays only on explicit play / Space

    [RelayCommand] private void DismissBanner() => ShowReconcileBanner = false;

    // ── snippet playback (isolated coordinator) ──

    private async void StartCurrentSnippet()
    {
        StopSnippet();
        PreviewProgress = 0;
        var card = _session?.CurrentCard;
        if (card is null) return;
        PreviewState = SwipePreviewState.Loading;
        try
        {
            var url = await _previewUrls.ResolveAsync(card.Uri);
            if (_session?.CurrentCard?.Uri != card.Uri) return;   // advanced while resolving
            if (string.IsNullOrEmpty(url)) { PreviewState = SwipePreviewState.Unavailable; return; }
            await _previews.StartImmediate(new CardPreviewRequest(_ownerId, url, _ => { }, OnPreviewState, OnPreviewCompleted));
        }
        catch (Exception ex) { _logger?.LogDebug(ex, "Snippet start failed for {Uri}", card.Uri); }
    }

    private void ToggleCurrentSnippet()
    {
        if (PreviewState == SwipePreviewState.Playing) { _ = _previews.CancelOwner(_ownerId); StopClock(); PreviewState = SwipePreviewState.None; }
        else StartCurrentSnippet();
    }

    private void StopSnippet() { _ = _previews.CancelOwner(_ownerId); StopClock(); }

    private void OnPreviewState(CardPreviewPlaybackState s) => _dispatcher.TryEnqueue(() =>
    {
        if (s.IsPlaying) { PreviewState = SwipePreviewState.Playing; StartClock(); }
        else if (s.IsPending) { PreviewState = SwipePreviewState.Loading; StopClock(); }
        else StopClock();
    });

    private void OnPreviewCompleted() => _dispatcher.TryEnqueue(() => { PreviewProgress = 1; StopClock(); });

    private void StartClock()
    {
        _playStartTicks = Environment.TickCount64 - (long)(PreviewProgress * SnippetSeconds * 1000);
        _clock.Start();
    }
    private void StopClock() => _clock.Stop();
    private void TickClock()
    {
        var elapsed = (Environment.TickCount64 - _playStartTicks) / 1000.0;
        PreviewProgress = Math.Min(1, elapsed / SnippetSeconds);
        if (PreviewProgress >= 1) StopClock();
    }

    // ── palette / immersive background ──

    private async Task UpdateBackgroundAsync(RefreshCard? card)
    {
        if (card is null) return;
        try
        {
            var palette = await _colors.ResolveAsync(card.Uri, card.ImageUrl);   // cached + prefetched → usually instant
            if (_session?.CurrentCard?.Uri != card.Uri) return;                   // advanced while resolving
            if (palette is { } p)
            {
                BackgroundPrimary = ParseHex(p.PrimaryHex, DefaultPrimary);
                BackgroundAccent = ParseHex(p.AccentHex, DefaultAccent);
            }
        }
        catch (Exception ex) { _logger?.LogDebug(ex, "Background palette failed for {Uri}", card.Uri); }
    }

    private static Color ParseHex(string? hex, Color fallback)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#' || hex.Length < 7) return fallback;
        try
        {
            return Color.FromArgb(255,
                Convert.ToByte(hex.Substring(1, 2), 16),
                Convert.ToByte(hex.Substring(3, 2), 16),
                Convert.ToByte(hex.Substring(5, 2), 16));
        }
        catch { return fallback; }
    }

    // ── prefetch (look-ahead so a swipe is never a cold load) ──

    private Task PrefetchAsync()
    {
        if (_session is null) return Task.CompletedTask;
        var next = _session.UpNext(3);
        if (next.Count == 0) return Task.CompletedTask;
        _ = _previewUrls.PrefetchAsync(next.Select(c => c.Uri).ToList());                       // 30s snippet URLs
        _ = _colors.PrefetchAsync(next.Select(c => (c.Uri, c.ImageUrl)).ToList());              // background palettes
        return Task.CompletedTask;
    }

    public void Teardown() { _ = _previews.UnregisterOwner(_ownerId); StopClock(); }
}
