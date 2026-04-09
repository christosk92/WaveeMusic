using System;
using Microsoft.Extensions.DependencyInjection;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Parent view model for the Library page. Lazily resolves the three
/// sub-view models and caches them for the lifetime of the LibraryPage,
/// so switching tabs inside the library does not re-instantiate state.
/// </summary>
public sealed class LibraryPageViewModel
{
    private readonly IServiceProvider _services;

    private AlbumsLibraryViewModel? _albums;
    private ArtistsLibraryViewModel? _artists;
    private LikedSongsViewModel? _likedSongs;

    public LibraryPageViewModel(IServiceProvider services)
    {
        _services = services;
    }

    public AlbumsLibraryViewModel Albums =>
        _albums ??= _services.GetRequiredService<AlbumsLibraryViewModel>();

    public ArtistsLibraryViewModel Artists =>
        _artists ??= _services.GetRequiredService<ArtistsLibraryViewModel>();

    public LikedSongsViewModel LikedSongs =>
        _likedSongs ??= _services.GetRequiredService<LikedSongsViewModel>();
}
