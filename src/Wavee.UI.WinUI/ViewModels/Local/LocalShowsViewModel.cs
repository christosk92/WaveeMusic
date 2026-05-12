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

namespace Wavee.UI.WinUI.ViewModels.Local;

public sealed partial class LocalShowsViewModel : ObservableObject, IDisposable
{
    private readonly ILocalLibraryFacade? _facade;
    private readonly DispatcherQueue _dispatcher;
    private readonly IDisposable? _changesSub;

    public ObservableCollection<LocalShow> Shows { get; } = new();
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isEmpty;

    /// <summary>
    /// Hero-header subtitle, e.g. <c>"12 shows · 4 unwatched"</c>. Recomputed
    /// on every load + facade change.
    /// </summary>
    [ObservableProperty] private string? _subtitleText;

    public LocalShowsViewModel(ILocalLibraryFacade? facade = null)
    {
        _facade = facade;
        _dispatcher = DispatcherQueue.GetForCurrentThread()
                      ?? throw new InvalidOperationException("LocalShowsViewModel must be constructed on a UI thread.");
        if (_facade is not null)
            _changesSub = _facade.Changes.Subscribe(__change => _dispatcher.TryEnqueue(() => _ = LoadAsync()));
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_facade is null) return;
        _dispatcher.TryEnqueue(() => IsLoading = true);
        var data = await _facade.GetShowsAsync(ct);
        _dispatcher.TryEnqueue(() =>
        {
            Shows.Clear();
            foreach (var s in data) Shows.Add(s);
            IsEmpty = data.Count == 0;
            IsLoading = false;
            SubtitleText = BuildSubtitle(data);
        });
    }

    private static string BuildSubtitle(IReadOnlyList<LocalShow> shows)
    {
        if (shows.Count == 0) return "No shows in library";
        var shows_label = shows.Count == 1 ? "show" : "shows";
        var unwatched = shows.Sum(s => s.UnwatchedCount);
        return unwatched > 0
            ? $"{shows.Count} {shows_label} · {unwatched} unwatched"
            : $"{shows.Count} {shows_label}";
    }

    public void Dispose() => _changesSub?.Dispose();
}
