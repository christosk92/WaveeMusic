namespace Wavee;

/// <summary>Which kind of library-search hit is being committed into the browse selection.</summary>
public enum LibrarySelectKind { Artist, Album }

/// <summary>
/// The pure rule behind "Your Library" search SELECT-IN-PLACE: given the hit that was clicked and the page's shape
/// (artists view? collapsed?), decide exactly which pieces of page state change. Split out of <c>LibraryPage</c>
/// (precedent: <see cref="LibraryLayoutBreakpoints"/>) so the decision is source-includable and unit-testable without
/// an engine, a window or a store.
/// <para>Every field is a "leave it alone" by default: a null <see cref="SelectedKey"/>/<see cref="AlbumKey"/>/
/// <see cref="Depth"/> means the corresponding signal is NOT written. That matters because the two keys are the
/// PERSISTED pair — an album pick inside the artists view must move the discography selection without disturbing the
/// artist, and an artist pick must reset the discography (a new artist has no chosen release yet).</para>
/// </summary>
public readonly record struct LibrarySelectionCommit(string? SelectedKey, string? AlbumKey, bool ClearFilter, int? Depth)
{
    /// <summary>Write nothing — the hit carried no usable uri.</summary>
    public static readonly LibrarySelectionCommit None = default;

    public bool IsNone => SelectedKey is null && AlbumKey is null && !ClearFilter && Depth is null;

    /// <summary>
    /// The commit for one hit.
    /// <list type="bullet">
    /// <item><b>Artist</b> → select the artist and RESET the discography key (the 3rd column re-picks the new artist's
    /// first release), collapsed depth 1 (the artist's discography).</item>
    /// <item><b>Album, albums/podcasts view</b> → it IS the master selection, collapsed depth 1 (the detail pane).</item>
    /// <item><b>Album, artists view</b> → it is the DISCOGRAPHY pick (3rd column). Its owning artist is committed too,
    /// because a search hit can be reached without ever clicking the artist row above it (the results auto-select the
    /// first matched artist), and a discography key that does not belong to the selected artist is exactly the
    /// incoherent state <c>SyncDisco</c> would otherwise have to guess about. Collapsed depth 2 (the tracks level).</item>
    /// </list>
    /// The filter is always cleared: the search view is gated on a non-empty query, so leaving it up would hide the
    /// browse panes the selection was just committed into.
    /// </summary>
    public static LibrarySelectionCommit For(LibrarySelectKind kind, bool artistsView, bool collapsed, string uri,
                                             string ownerArtistUri = "")
    {
        if (string.IsNullOrEmpty(uri)) return None;
        if (kind == LibrarySelectKind.Artist)
            return new("artist:" + uri, "", ClearFilter: true, collapsed ? 1 : null);
        if (!artistsView)
            return new("album:" + uri, null, ClearFilter: true, collapsed ? 1 : null);
        return new(string.IsNullOrEmpty(ownerArtistUri) ? null : "artist:" + ownerArtistUri,
                   "album:" + uri, ClearFilter: true, collapsed ? 2 : null);
    }

    public static LibrarySelectionCommit ForArtist(bool artistsView, bool collapsed, string uri)
        => For(LibrarySelectKind.Artist, artistsView, collapsed, uri);

    public static LibrarySelectionCommit ForAlbum(bool artistsView, bool collapsed, string uri, string ownerArtistUri)
        => For(LibrarySelectKind.Album, artistsView, collapsed, uri, ownerArtistUri);
}
