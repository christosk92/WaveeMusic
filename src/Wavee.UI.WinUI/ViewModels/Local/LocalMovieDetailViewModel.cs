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

public sealed partial class LocalMovieDetailViewModel : ObservableObject, IDisposable
{
    private readonly ILocalLibraryFacade? _facade;
    private readonly DispatcherQueue _dispatcher;
    private readonly IDisposable? _changesSub;
    private string? _trackUri;
    private string? _paletteSourceUrl;

    [ObservableProperty] private LocalMovie? _movie;
    public ObservableCollection<LocalSubtitle>      Subtitles   { get; } = new();
    public ObservableCollection<LocalEmbeddedTrack> AudioTracks { get; } = new();
    public ObservableCollection<LocalCastMember>    Cast        { get; } = new();
    [ObservableProperty] private bool _isLoading;

    /// <summary>
    /// <c>#RRGGBB</c> dominant accent extracted from the movie's backdrop /
    /// poster. Drives <c>HeroHeader.ColorHex</c> (lower-third color blend).
    /// </summary>
    [ObservableProperty] private string? _headerHeroColorHex;

    public LocalMovieDetailViewModel(ILocalLibraryFacade? facade = null)
    {
        _facade = facade;
        _dispatcher = DispatcherQueue.GetForCurrentThread() ?? throw new InvalidOperationException();
        if (_facade is not null)
            _changesSub = _facade.Changes.Subscribe(__change =>
            {
                if (_trackUri is not null) _dispatcher.TryEnqueue(() => _ = LoadAsync(_trackUri!));
            });
    }

    public async Task LoadAsync(string trackUri, CancellationToken ct = default)
    {
        if (_facade is null) return;
        _trackUri = trackUri;
        _dispatcher.TryEnqueue(() => IsLoading = true);
        var movie = await _facade.GetMovieAsync(trackUri, ct);
        IReadOnlyList<LocalSubtitle> subs = Array.Empty<LocalSubtitle>();
        IReadOnlyList<LocalEmbeddedTrack> audios = Array.Empty<LocalEmbeddedTrack>();
        IReadOnlyList<LocalCastMember> cast = Array.Empty<LocalCastMember>();
        if (movie is not null)
        {
            // Parallel-fetch subs / audio / cast — independent queries, all on
            // the same connection-per-call helper. Cheap for typical movie sizes.
            var subsTask = _facade.GetSubtitlesForAsync(movie.FilePath, ct);
            var audiosTask = _facade.GetAudioTracksForAsync(movie.FilePath, ct);
            var castTask = _facade.GetMovieCastAsync(trackUri, ct);
            await Task.WhenAll(subsTask, audiosTask, castTask);
            subs = subsTask.Result;
            audios = audiosTask.Result;
            cast = castTask.Result;
        }
        _dispatcher.TryEnqueue(() =>
        {
            Movie = movie;
            Subtitles.Clear();
            foreach (var s in subs) Subtitles.Add(s);
            AudioTracks.Clear();
            foreach (var a in audios) AudioTracks.Add(a);
            Cast.Clear();
            foreach (var c in cast) Cast.Add(c);
            IsLoading = false;
        });

        var heroUrl = movie?.BackdropUri ?? movie?.PosterUri;
        if (!string.IsNullOrEmpty(heroUrl) && !string.Equals(heroUrl, _paletteSourceUrl, StringComparison.Ordinal))
        {
            _paletteSourceUrl = heroUrl;
            var hex = await LocalImagePaletteHelper.TryExtractDominantHexAsync(heroUrl);
            _dispatcher.TryEnqueue(() => HeaderHeroColorHex = hex);
        }
    }

    public void Dispose() => _changesSub?.Dispose();
}
