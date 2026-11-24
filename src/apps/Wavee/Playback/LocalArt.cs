// ── Playback/LocalArt.cs ───────────────────────────────────────────────────────────────────────────────────────────
// P2-STUB: the album-art seam P2-data owns (240-p2-contract.md "Seams between lanes", plan §3.6a). The "On this PC" page (P2-page)
// calls it; until P2-data's real LocalArt lands, every cover answers null (the page's calm gradient and its placeholders). Deleted
// when P2-page rebases onto origin/feat/240-p2-data.

using FluentGpu.Signals;

namespace Wavee;

// P2-STUB
/// <summary>Cached covers of local files (P2-STUB: nothing is extracted, every answer is null).</summary>
public static class LocalArt
{
    /// <summary>file:/// uri of the cached cover for this file, or null (not extracted yet: queued; or none).</summary>
    public static string? CoverUri(long localId) => null;

    /// <summary>The album's cover: the first track of (albumArtist or first artist, album) with art, same rules.</summary>
    public static string? AlbumCoverUri(string albumArtistOrArtist, string album) => null;

    /// <summary>Bumped (at most 4x a second) when covers land; bind to it to re-read.</summary>
    public static readonly Signal<int> Epoch = new(0);
}
