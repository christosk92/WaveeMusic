using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Wavee.Local;
using Wavee.UI.Library.Local;

namespace Wavee.UI.WinUI.ViewModels.Local;

public sealed partial class LocalMusicViewModel : ObservableObject, IDisposable
{
    private readonly ILocalLibraryFacade? _facade;
    private readonly DispatcherQueue _dispatcher;
    private readonly IDisposable? _changesSub;

    public ObservableCollection<LocalTrackRow> Tracks { get; } = new();
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _showOnlyLiked;

    public LocalMusicViewModel(ILocalLibraryFacade? facade = null)
    {
        _facade = facade;
        _dispatcher = DispatcherQueue.GetForCurrentThread() ?? throw new InvalidOperationException();
        if (_facade is not null)
            _changesSub = _facade.Changes.Subscribe(__change => _dispatcher.TryEnqueue(() => _ = LoadAsync()));
    }

    partial void OnShowOnlyLikedChanged(bool value) => _ = LoadAsync();

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_facade is null) return;
        _dispatcher.TryEnqueue(() => IsLoading = true);
        var data = ShowOnlyLiked
            ? await _facade.GetLikedTracksAsync(ct)
            : await _facade.GetMusicTracksAsync(500, ct);
        _dispatcher.TryEnqueue(() =>
        {
            Tracks.Clear();
            foreach (var t in data) Tracks.Add(t);
            IsEmpty = data.Count == 0;
            IsLoading = false;
        });
    }

    public void Dispose() => _changesSub?.Dispose();
}
