namespace Wavee.UI.WinUI.Data.Parameters;

/// <summary>
/// Navigation parameter that carries known card data to destination pages
/// so they can prefill the UI immediately while loading full details in the background.
/// </summary>
public sealed record ContentNavigationParameter
{
    public required string Uri { get; init; }
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    public string? ImageUrl { get; init; }

    // Origin-known track count for albums/playlists. Lets the destination page
    // render an exact-count skeleton (TrackDataGrid.LoadingRowCount) instead of
    // a fixed-10 placeholder that visibly collapses or grows when real rows land.
    public int? TotalTracks { get; init; }
}

/// <summary>
/// Navigation parameter for deep-linking into <c>SettingsPage</c> from the omnibar
/// settings results. The page selects <see cref="SectionTag"/> and applies
/// <see cref="GroupKey"/> through its existing in-page group filter
/// (<c>ISettingsSearchFilter.ApplySearchFilter</c>). <see cref="EntryTitle"/>
/// drives the "Showing settings for X" chrome.
/// </summary>
public sealed record SettingsNavigationParameter(string SectionTag, string GroupKey, string EntryTitle);
