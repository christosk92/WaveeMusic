using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Wavee.Local.Models;
using Wavee.UI.Library.Local;
using Wavee.UI.WinUI.Helpers;

namespace Wavee.UI.WinUI.ViewModels.Local;

public sealed partial class LocalShowDetailViewModel : ObservableObject, IDisposable
{
    private readonly ILocalLibraryFacade? _facade;
    private readonly DispatcherQueue _dispatcher;
    private readonly IDisposable? _changesSub;
    private string? _showId;
    private string? _paletteSourceUrl;

    [ObservableProperty] private LocalShow? _show;
    public ObservableCollection<LocalSeason> Seasons { get; } = new();
    public ObservableCollection<LocalEpisode> EpisodesForSelectedSeason { get; } = new();
    /// <summary>v21 — principal cast (top-10) for the show, loaded alongside seasons.</summary>
    public ObservableCollection<LocalCastMember> ShowCast { get; } = new();

    [ObservableProperty] private LocalSeason? _selectedSeason;
    [ObservableProperty] private bool _isLoading;

    /// <summary>
    /// <c>#RRGGBB</c> dominant accent extracted from the show's poster /
    /// backdrop. Binds to <c>HeroHeader.ColorHex</c> so the hero's lower
    /// third picks up the show's palette like ArtistPage does. Null until
    /// extraction finishes (or stays null if there's no on-disk artwork).
    /// </summary>
    [ObservableProperty] private string? _headerHeroColorHex;

    public LocalShowDetailViewModel(ILocalLibraryFacade? facade = null)
    {
        _facade = facade;
        _dispatcher = DispatcherQueue.GetForCurrentThread()
                      ?? throw new InvalidOperationException();
        if (_facade is not null)
            _changesSub = _facade.Changes.Subscribe(__change =>
            {
                if (_showId is not null) _dispatcher.TryEnqueue(() => _ = LoadAsync(_showId!));
            });
    }

    public async Task LoadAsync(string showId, CancellationToken ct = default)
    {
        if (_facade is null) return;
        _showId = showId;
        _dispatcher.TryEnqueue(() => IsLoading = true);
        // Parallel-fetch the three reads: details, seasons, cast. Cast may
        // be empty until the show has been TMDB-synced; an empty list just
        // collapses the cast strip on the page.
        var showTask = _facade.GetShowAsync(showId, ct);
        var seasonsTask = _facade.GetShowSeasonsAsync(showId, ct);
        var castTask = _facade.GetShowCastAsync(showId, ct);
        await Task.WhenAll(showTask, seasonsTask, castTask);
        var show = await showTask;
        var seasons = await seasonsTask;
        var cast = await castTask;
        _dispatcher.TryEnqueue(() =>
        {
            Show = show;
            Seasons.Clear();
            foreach (var s in seasons) Seasons.Add(s);
            ShowCast.Clear();
            foreach (var m in cast) ShowCast.Add(m);
            // Default to the first season; the SelectedSeason setter rebuilds
            // EpisodesForSelectedSeason.
            SelectedSeason = Seasons.FirstOrDefault();
            IsLoading = false;
        });

        var heroUrl = show?.BackdropArtworkUri ?? show?.PosterArtworkUri;
        if (!string.IsNullOrEmpty(heroUrl) && !string.Equals(heroUrl, _paletteSourceUrl, StringComparison.Ordinal))
        {
            _paletteSourceUrl = heroUrl;
            var hex = await LocalImagePaletteHelper.TryExtractDominantHexAsync(heroUrl);
            _dispatcher.TryEnqueue(() => HeaderHeroColorHex = hex);
        }
    }

    /// <summary>
    /// Multiple-season selector hook. Setting <see cref="SelectedSeason"/> from
    /// the page's <c>SelectorBar</c> rebuilds the visible episodes list in
    /// dispatcher order so the UI doesn't tear during a season switch.
    /// </summary>
    partial void OnSelectedSeasonChanged(LocalSeason? value)
    {
        EpisodesForSelectedSeason.Clear();
        if (value is null) return;
        foreach (var ep in value.Episodes)
            EpisodesForSelectedSeason.Add(ep);
    }

    /// <summary>
    /// Returns the next episode to play when the user taps "Play" on the hero:
    /// the first unwatched on-disk episode in season order, or — if everything
    /// is watched — the very first on-disk episode. Missing-from-disk roster
    /// entries (TrackUri is null) are skipped, they can't be played.
    /// </summary>
    public LocalEpisode? PickPlayTarget()
    {
        var onDisk = Seasons.SelectMany(s => s.Episodes)
            .Where(e => !string.IsNullOrEmpty(e.TrackUri))
            .ToList();
        var firstUnwatched = onDisk.FirstOrDefault(e => e.WatchedAt is null);
        return firstUnwatched ?? onDisk.FirstOrDefault();
    }

    public async Task MarkAllWatchedAsync(bool watched, CancellationToken ct = default)
    {
        if (_facade is null) return;
        // Skip missing-from-disk roster rows — they have no TrackUri to mark.
        var all = Seasons.SelectMany(s => s.Episodes)
            .Where(e => !string.IsNullOrEmpty(e.TrackUri))
            .ToList();
        foreach (var ep in all)
        {
            // Skip no-op writes to keep the facade's change-events list short.
            var alreadyWatched = ep.WatchedAt is > 0;
            if (alreadyWatched == watched) continue;
            await _facade.MarkWatchedAsync(ep.TrackUri!, watched, ct);
        }
    }

    public void Dispose() => _changesSub?.Dispose();
}
