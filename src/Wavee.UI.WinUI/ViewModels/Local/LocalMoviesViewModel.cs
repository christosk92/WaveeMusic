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

public sealed partial class LocalMoviesViewModel : ObservableObject, IDisposable
{
    private readonly ILocalLibraryFacade? _facade;
    private readonly DispatcherQueue _dispatcher;
    private readonly IDisposable? _changesSub;

    public ObservableCollection<LocalMovie> Movies { get; } = new();
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isEmpty;

    /// <summary>
    /// Hero-header subtitle, e.g. <c>"38 movies · 6 unwatched"</c>. Recomputed
    /// on every load + facade change.
    /// </summary>
    [ObservableProperty] private string? _subtitleText;

    public LocalMoviesViewModel(ILocalLibraryFacade? facade = null)
    {
        _facade = facade;
        _dispatcher = DispatcherQueue.GetForCurrentThread() ?? throw new InvalidOperationException();
        if (_facade is not null)
            _changesSub = _facade.Changes.Subscribe(__change => _dispatcher.TryEnqueue(() => _ = LoadAsync()));
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_facade is null) return;
        _dispatcher.TryEnqueue(() => IsLoading = true);
        var movies = await _facade.GetMoviesAsync(ct);
        _dispatcher.TryEnqueue(() =>
        {
            Movies.Clear();
            foreach (var m in movies) Movies.Add(m);
            IsEmpty = movies.Count == 0;
            IsLoading = false;
            SubtitleText = BuildSubtitle(movies);
        });
    }

    private static string BuildSubtitle(IReadOnlyList<LocalMovie> movies)
    {
        if (movies.Count == 0) return "No movies in library";
        var label = movies.Count == 1 ? "movie" : "movies";
        var unwatched = movies.Count(m => m.WatchedAt is null);
        return unwatched > 0
            ? $"{movies.Count} {label} · {unwatched} unwatched"
            : $"{movies.Count} {label}";
    }

    public void Dispose() => _changesSub?.Dispose();
}
