namespace Wavee.UI.WinUI.Data.Parameters;

/// <summary>
/// Which discography group the dedicated <c>ArtistDiscographyPage</c> renders.
/// </summary>
public enum ArtistDiscographyGroupKind
{
    Albums,
    Singles,
}

/// <summary>
/// Navigation parameter for the dedicated artist-discography page. Carries the
/// parent artist's identity + the requested group so the destination page can
/// render its breadcrumb and either share the parent <c>ArtistViewModel</c>'s
/// already-paginated items (same-tab nav from <c>ArtistPage</c>) or refetch
/// the group fresh (deep-link / tab restore / out-of-band entry).
/// </summary>
public sealed record ArtistDiscographyNavigationParameter(
    string ArtistUri,
    string ArtistName,
    ArtistDiscographyGroupKind GroupKind,
    string? ArtistImageUrl = null);
