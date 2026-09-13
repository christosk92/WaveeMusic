namespace Wavee.Core;

/// <summary>The two URI schemes one upcoming release answers to — and the one thing that is NOT true about them: the
/// ids DIFFER. The same record is <c>spotify:prerelease:0iqKCCqFwlqzSnJgV22Nmh</c> AND
/// <c>spotify:album:0qi1ztU4S08zA1FsP1DUaY</c> (docs/plans/wavee/spotify-wire-research/research/02-XM-CENSUS.md:392),
/// so nothing here — or anywhere else — may synthesise one uri from the other by swapping a prefix.
///
/// Extended-metadata kind 138 is the ONLY mapping between them, and it answers to either key: ask it with whichever
/// uri you happen to hold and it returns the pair. These helpers exist so every caller asks "which scheme is this?"
/// the same way, instead of open-coding a <c>StartsWith</c> that quietly disagrees about the trailing colon.</summary>
public static class PreReleaseUris
{
    public const string PreReleaseScheme = "spotify:prerelease:";
    public const string AlbumScheme = "spotify:album:";

    // The scheme question is answered by the ONE parser (hydration-facade-design.md §1.1): a uri's EntityKind IS which
    // scheme it is, so these two predicates and every routing switch can no longer disagree about a trailing colon.
    public static bool IsPreRelease(string? uri) =>
        uri is not null && EntityUri.KindOf(uri) == EntityKind.Prerelease;

    public static bool IsAlbum(string? uri) =>
        uri is not null && EntityUri.KindOf(uri) == EntityKind.Album;
}
