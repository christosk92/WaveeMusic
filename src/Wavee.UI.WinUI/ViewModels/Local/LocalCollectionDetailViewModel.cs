using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Wavee.Local;
using Wavee.Local.Models;
using Wavee.UI.Library.Local;

namespace Wavee.UI.WinUI.ViewModels.Local;

public sealed partial class LocalCollectionDetailViewModel : ObservableObject, IDisposable
{
    private readonly ILocalLibraryFacade? _facade;
    private readonly DispatcherQueue _dispatcher;
    private readonly IDisposable? _changesSub;
    private string? _collectionId;

    [ObservableProperty] private LocalCollection? _collection;
    public ObservableCollection<LocalTrackRow> Members { get; } = new();
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isEmpty;

    public LocalCollectionDetailViewModel(ILocalLibraryFacade? facade = null)
    {
        _facade = facade;
        _dispatcher = DispatcherQueue.GetForCurrentThread() ?? throw new InvalidOperationException();
        if (_facade is not null)
            _changesSub = _facade.Changes.Subscribe(__change =>
            {
                if (_collectionId is not null)
                    _dispatcher.TryEnqueue(() => _ = LoadAsync(_collectionId!));
            });
    }

    public async Task LoadAsync(string collectionId, CancellationToken ct = default)
    {
        _collectionId = collectionId;
        if (_facade is null) return;
        _dispatcher.TryEnqueue(() => IsLoading = true);
        var coll = await _facade.GetCollectionAsync(collectionId, ct);
        var members = await _facade.GetCollectionMembersAsync(collectionId, ct);
        _dispatcher.TryEnqueue(() =>
        {
            Collection = coll;
            Members.Clear();
            foreach (var m in members) Members.Add(m);
            IsEmpty = members.Count == 0;
            IsLoading = false;
        });
    }

    public void Dispose() => _changesSub?.Dispose();
}
