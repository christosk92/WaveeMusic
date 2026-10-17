// ── Themes/Classic/Structure/Views/Pages/ClassicLibrary.cs — registers Classic's library family (themes plan 6.1, T10c1) ───────────────────────────
//
// The library page (`ClassicLibraryPage*.cs`) and its four panes: the album pane (`ClassicLibraryAlbumPane.cs`), the compact show pane (`ClassicLibraryShowPane.cs`), the artists' reader
// (`ClassicLibraryArtistReader*.cs`) and Your Episodes (`ClassicLibrarySavedEpisodes.cs`), with what the two album-shaped panes share (`ClassicLibraryPaneKit.cs`) and the word rails
// (`ClassicLibraryRails.cs`). The numbers are `Rules\ClassicLibraryRules.cs`, `ClassicLibraryPaneRules.cs`, `ClassicLibrarySearchRules.cs` and the collapse ladder `Rules\Tiers.Library.cs`.

using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Registers the library family's views.</summary>
internal static class ClassicLibrary
{
    /// <summary>Registers the library page and its four panes.</summary>
    internal static void Register(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.LibraryPage, ClassicLibraryPage.Template, "classic.library.page");
        views.Add(ViewKeys.LibraryAlbumPane, ClassicLibraryAlbumPane.Template, "classic.library.album-pane");
        views.Add(ViewKeys.LibraryShowPane, ClassicLibraryShowPane.Template, "classic.library.show-pane");
        views.Add(ViewKeys.LibraryArtistReader, ClassicLibraryArtistReader.Template, "classic.library.artist-reader");
        views.Add(ViewKeys.LibrarySavedEpisodes, ClassicLibrarySavedEpisodes.Template, "classic.library.saved-episodes");
    }
}
