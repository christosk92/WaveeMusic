using Wavee.Core.Http.Pathfinder;

namespace Wavee.UI.WinUI.Controls.Search;

/// <summary>
/// Partial-class extension that drives the top-right
/// <c>AddToPlaylistChip</c> on the search hero card. The chip's own session
/// hook handles visibility and glyph; this just pipes per-row identity in.
/// </summary>
public sealed partial class SearchResultHeroCard
{
    private void ApplyAddChipForItem(SearchResultItem? item)
    {
        if (AddChip is null) return;
        if (item is null || item.Type != SearchResultType.Track)
        {
            AddChip.IsEligible = false;
            AddChip.TrackUri = null;
            return;
        }
        AddChip.TrackUri = item.Uri;
        AddChip.TrackTitle = item.Name;
        AddChip.TrackArtistName = item.ArtistNames is { Count: > 0 } artists ? artists[0] : null;
        AddChip.TrackImageUrl = item.ImageUrl;
        AddChip.TrackDurationMs = item.DurationMs;
        AddChip.IsEligible = true;
    }
}
