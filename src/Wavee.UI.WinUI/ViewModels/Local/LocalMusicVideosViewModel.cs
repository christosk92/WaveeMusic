using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Wavee.Local.Models;
using Wavee.UI.Library.Local;

namespace Wavee.UI.WinUI.ViewModels.Local;

public sealed partial class LocalMusicVideosViewModel : ObservableObject, IDisposable
{
    private readonly ILocalLibraryFacade? _facade;
    private readonly DispatcherQueue _dispatcher;
    private readonly IDisposable? _changesSub;
    private readonly ILogger? _logger;

    private CancellationTokenSource? _reloadCts;
    private int _reloadGeneration;

    public ObservableCollection<LocalMusicVideo> MusicVideos { get; } = new();
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isEmpty;

    public LocalMusicVideosViewModel(ILocalLibraryFacade? facade = null)
    {
        _facade = facade;
        _dispatcher = DispatcherQueue.GetForCurrentThread() ?? throw new InvalidOperationException();
        _logger = Ioc.Default.GetService<ILoggerFactory>()?.CreateLogger<LocalMusicVideosViewModel>();

        if (_facade is not null)
            _changesSub = _facade.Changes.Subscribe(OnLibraryChange);
    }

    private void OnLibraryChange(LocalLibraryChange change)
    {
        switch (change.Kind)
        {
            // Irrelevant to the music-videos grid — never reload for these.
            case LocalLibraryChangeKind.LikeChanged:
            case LocalLibraryChangeKind.WatchedStateChanged:
            case LocalLibraryChangeKind.EnrichmentResult:
                return;

            // Targeted in-place update: don't Clear()+repopulate the whole list.
            case LocalLibraryChangeKind.MusicVideoAssociationChanged:
                if (!string.IsNullOrEmpty(change.KeyUri))
                {
                    _dispatcher.TryEnqueue(() => RefreshLinkedSpotifyTrackUri(change.KeyUri!));
                }
                return;

            default:
                _dispatcher.TryEnqueue(() => _ = ReloadAsync());
                return;
        }
    }

    private async Task ReloadAsync()
    {
        _reloadCts?.Cancel();
        _reloadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _reloadCts = cts;
        await LoadAsync(cts.Token);
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_facade is null) return;
        var gen = Interlocked.Increment(ref _reloadGeneration);

        _dispatcher.TryEnqueue(() => IsLoading = true);

        IReadOnlyList<LocalMusicVideo> data;
        try
        {
            data = await _facade.GetMusicVideosAsync(ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "GetMusicVideosAsync failed");
            _dispatcher.TryEnqueue(() => IsLoading = false);
            return;
        }

        if (ct.IsCancellationRequested) return;

        _dispatcher.TryEnqueue(() =>
        {
            // Drop stale completions — only the most recent reload may mutate the list.
            if (gen != Volatile.Read(ref _reloadGeneration)) return;
            ApplyResults(data);
        });
    }

    /// <summary>
    /// In-place reconcile keyed by <c>TrackUri</c>. Avoids <c>Clear()</c> +
    /// repopulate, which sends a <c>Reset</c> notification that can race with
    /// flyout teardown / focus transitions and surface as a generic
    /// <c>COMException("Unspecified error")</c> from the ItemsRepeater binding.
    /// </summary>
    private void ApplyResults(IReadOnlyList<LocalMusicVideo> incoming)
    {
        try
        {
            var indexByUri = new Dictionary<string, int>(MusicVideos.Count, StringComparer.Ordinal);
            for (var i = 0; i < MusicVideos.Count; i++)
                indexByUri[MusicVideos[i].TrackUri] = i;

            for (var i = 0; i < incoming.Count; i++)
            {
                var item = incoming[i];
                if (i < MusicVideos.Count && MusicVideos[i].TrackUri == item.TrackUri)
                {
                    if (!Equals(MusicVideos[i], item))
                        MusicVideos[i] = item;
                    continue;
                }

                if (indexByUri.TryGetValue(item.TrackUri, out var existingIdx) && existingIdx > i)
                {
                    MusicVideos.Move(existingIdx, i);
                    if (!Equals(MusicVideos[i], item))
                        MusicVideos[i] = item;
                    indexByUri.Remove(item.TrackUri);
                    continue;
                }

                if (i < MusicVideos.Count)
                    MusicVideos.Insert(i, item);
                else
                    MusicVideos.Add(item);
            }

            while (MusicVideos.Count > incoming.Count)
                MusicVideos.RemoveAt(MusicVideos.Count - 1);

            IsEmpty = incoming.Count == 0;
            IsLoading = false;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            _logger?.LogDebug(ex, "MusicVideos reconcile failed (XAML COM); retrying on next dispatcher tick");
            // Best-effort retry once layout has settled; if it fails again we drop the update.
            _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                try
                {
                    ApplyResultsRetry(incoming);
                }
                catch (Exception ex2)
                {
                    _logger?.LogDebug(ex2, "MusicVideos reconcile retry failed; dropping update");
                }
            });
        }
    }

    private void ApplyResultsRetry(IReadOnlyList<LocalMusicVideo> incoming)
    {
        // Same shape as ApplyResults but without the outer COMException guard.
        var indexByUri = new Dictionary<string, int>(MusicVideos.Count, StringComparer.Ordinal);
        for (var i = 0; i < MusicVideos.Count; i++)
            indexByUri[MusicVideos[i].TrackUri] = i;

        for (var i = 0; i < incoming.Count; i++)
        {
            var item = incoming[i];
            if (i < MusicVideos.Count && MusicVideos[i].TrackUri == item.TrackUri)
            {
                if (!Equals(MusicVideos[i], item)) MusicVideos[i] = item;
                continue;
            }
            if (i < MusicVideos.Count) MusicVideos.Insert(i, item);
            else MusicVideos.Add(item);
        }
        while (MusicVideos.Count > incoming.Count)
            MusicVideos.RemoveAt(MusicVideos.Count - 1);

        IsEmpty = incoming.Count == 0;
        IsLoading = false;
    }

    private void RefreshLinkedSpotifyTrackUri(string trackUri)
    {
        if (_facade is null) return;
        _ = RefreshLinkedSpotifyTrackUriAsync(trackUri);
    }

    private async Task RefreshLinkedSpotifyTrackUriAsync(string trackUri)
    {
        try
        {
            // The facade exposes the music-videos list; pull a fresh snapshot
            // and update just the affected item's record in place.
            var data = await _facade!.GetMusicVideosAsync();
            var updated = data.FirstOrDefault(mv => string.Equals(mv.TrackUri, trackUri, StringComparison.Ordinal));
            if (updated is null) return;

            _dispatcher.TryEnqueue(() =>
            {
                try
                {
                    for (var i = 0; i < MusicVideos.Count; i++)
                    {
                        if (string.Equals(MusicVideos[i].TrackUri, trackUri, StringComparison.Ordinal))
                        {
                            if (!Equals(MusicVideos[i], updated))
                                MusicVideos[i] = updated;
                            return;
                        }
                    }
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    _logger?.LogDebug(ex, "MusicVideos in-place replace failed (XAML COM); ignoring");
                }
            });
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "RefreshLinkedSpotifyTrackUri failed");
        }
    }

    public void Dispose()
    {
        _changesSub?.Dispose();
        _reloadCts?.Cancel();
        _reloadCts?.Dispose();
    }
}
