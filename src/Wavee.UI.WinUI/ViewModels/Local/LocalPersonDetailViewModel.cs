using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Wavee.Local.Models;
using Wavee.UI.Library.Local;

namespace Wavee.UI.WinUI.ViewModels.Local;

/// <summary>
/// Backs <c>LocalPersonDetailPage</c>. Loads a TMDB person's bio + photo
/// (one TMDB call) and cross-references with the local library to surface
/// which TV shows and movies in the user's collection they appear in.
/// All work is explicit-user-click — fires when the user navigates here, never
/// on a timer or scan completion.
/// </summary>
public sealed partial class LocalPersonDetailViewModel : ObservableObject, IDisposable
{
    private readonly ILocalLibraryFacade? _facade;
    private readonly DispatcherQueue _dispatcher;
    private readonly IDisposable? _changesSub;
    private int? _personId;

    [ObservableProperty] private LocalPersonInfo? _person;
    public ObservableCollection<LocalShow> ShowsInLibrary { get; } = new();
    public ObservableCollection<LocalMovie> MoviesInLibrary { get; } = new();
    [ObservableProperty] private bool _isLoading;

    /// <summary>
    /// Provisional name + photo carried over from the cast strip on the
    /// previous page so the hero renders something before the TMDB fetch
    /// lands. The page rebinds to <see cref="Person"/> once that resolves.
    /// </summary>
    [ObservableProperty] private string? _seedName;
    [ObservableProperty] private string? _seedImageUri;

    public LocalPersonDetailViewModel(ILocalLibraryFacade? facade = null)
    {
        _facade = facade;
        _dispatcher = DispatcherQueue.GetForCurrentThread()
                      ?? throw new InvalidOperationException();
        if (_facade is not null)
            _changesSub = _facade.Changes.Subscribe(__change =>
            {
                if (_personId is { } pid) _dispatcher.TryEnqueue(() => _ = LoadAsync(pid));
            });
    }

    /// <summary>
    /// Seed display values from the previous page's cast row (avoids a blank
    /// hero while TMDB resolves). The seed image URI may be a
    /// <c>wavee-artwork://</c> URI from <c>LocalCastMember.ProfileImageUri</c>.
    /// </summary>
    public void Prefill(string? name, string? imageUri)
    {
        _dispatcher.TryEnqueue(() =>
        {
            SeedName = name;
            SeedImageUri = imageUri;
        });
    }

    public async Task LoadAsync(int personId, CancellationToken ct = default)
    {
        if (_facade is null) return;
        _personId = personId;
        _dispatcher.TryEnqueue(() => IsLoading = true);

        // Parallel: TMDB person fetch + two local library queries. The local
        // queries answer "does this person appear in anything I own?" using
        // person_id columns we already store in local_show_cast /
        // local_movie_cast — no extra TMDB call required for cross-reference.
        var personTask = _facade.GetTmdbPersonAsync(personId, ct);
        var showsTask = _facade.GetShowsByPersonIdAsync(personId, ct);
        var moviesTask = _facade.GetMoviesByPersonIdAsync(personId, ct);
        await Task.WhenAll(personTask, showsTask, moviesTask);
        var person = await personTask;
        var shows = await showsTask;
        var movies = await moviesTask;
        _dispatcher.TryEnqueue(() =>
        {
            Person = person;
            ShowsInLibrary.Clear();
            foreach (var s in shows) ShowsInLibrary.Add(s);
            MoviesInLibrary.Clear();
            foreach (var m in movies) MoviesInLibrary.Add(m);
            IsLoading = false;
        });
    }

    public void Dispose() => _changesSub?.Dispose();
}
