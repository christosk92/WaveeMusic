using System;
using Microsoft.Extensions.DependencyInjection;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Parent view model for the Library page. Lazily resolves the three
/// sub-view models and caches them for the lifetime of the LibraryPage,
/// so switching tabs inside the library does not re-instantiate state.
/// </summary>
public sealed class LibraryPageViewModel : IDisposable
{
    private readonly IServiceProvider _services;
    private bool _disposed;

    private AlbumsLibraryViewModel? _albums;
    private ArtistsLibraryViewModel? _artists;
    private LikedSongsViewModel? _likedSongs;

    public LibraryPageViewModel(IServiceProvider services)
    {
        _services = services;
    }

    public AlbumsLibraryViewModel Albums =>
        !_disposed
            ? _albums ??= _services.GetRequiredService<AlbumsLibraryViewModel>()
            : throw new ObjectDisposedException(nameof(LibraryPageViewModel));

    public ArtistsLibraryViewModel Artists =>
        !_disposed
            ? _artists ??= _services.GetRequiredService<ArtistsLibraryViewModel>()
            : throw new ObjectDisposedException(nameof(LibraryPageViewModel));

    public LikedSongsViewModel LikedSongs =>
        !_disposed
            ? _likedSongs ??= _services.GetRequiredService<LikedSongsViewModel>()
            : throw new ObjectDisposedException(nameof(LibraryPageViewModel));

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        DisposeIfNeeded(ref _albums);
        DisposeIfNeeded(ref _artists);
        DisposeIfNeeded(ref _likedSongs);
    }

    private static void DisposeIfNeeded<T>(ref T? value)
        where T : class
    {
        if (value is IDisposable disposable)
            disposable.Dispose();

        value = null;
    }
}
