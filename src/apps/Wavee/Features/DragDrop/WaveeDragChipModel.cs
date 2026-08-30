using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

/// <summary>The resource kinds a <see cref="WaveeResourceDragPayload"/> can carry. Declared beside the engine-free chip
/// model (rather than next to the payload) so the chip resolution rules can be compiled — and unit-tested — without
/// pulling in the FluentGpu.Controls chip type.</summary>
enum WaveeResourceKind : byte
{
    Route, Playlist, Album, Artist, Show, Folder, Track, Episode,
}

/// <summary>
/// The PURE resolution of a drag payload into the four data pieces the framework-owned drag chip renders
/// (<c>DragChipSpec</c>): title, subtitle, artwork URL and item count. The chip ELEMENT is framework-owned and the
/// spec type lives in FluentGpu.Controls, so the only part with real decisions in it — which line wins for a track
/// drag versus an entity drag, where the art comes from, what the badge counts — is factored out here, engine-free,
/// where <c>Wavee.Tests</c> can compile it.
/// </summary>
readonly record struct WaveeDragChipModel(string? Title, string? Subtitle, string? ArtUrl, int Count)
{
    /// <summary>Resolve one payload's chip data.
    /// <para>A TRACK snapshot names its FIRST track (title + first artist) and counts the WHOLE selection: the corner
    /// count badge is what communicates "and N−1 more", so a multi-select chip stays a real track card instead of
    /// degrading into a bare "3 songs" label (the payload's own <c>Name</c> is that label). Every other resource names
    /// itself and has no second line.</para>
    /// <para>A ROOTLIST multi-select (several sidebar playlists/folders dragged as one) carries no tracks at all, so it
    /// counts through <paramref name="rootlistCount"/> — the SAME badge and stacked backdrop the framework already draws
    /// for a song selection, because "I am carrying five things" is one idea and deserves one visual.</para></summary>
    public static WaveeDragChipModel For(string? name, string? artUrl, IReadOnlyList<Track>? tracks,
                                         int rootlistCount = 1)
    {
        if (tracks is not { Count: > 0 })
            return new(Nz(name), null, Nz(artUrl), rootlistCount > 1 ? rootlistCount : 1);
        var first = tracks[0];
        return new(
            Nz(first.Title) ?? Nz(name),
            first.Artists is { Count: > 0 } artists ? Nz(artists[0].Name) : null,
            Nz(artUrl) ?? ArtOf(first.Image),
            tracks.Count);
    }

    /// <summary>The single cover URL for an artwork reference: its own URL, else the first mosaic tile (a cover-less
    /// playlist carries tiles and no URL), else none. The chip is 40 DIP of art — one tile reads better there than a
    /// mosaic, which is why this collapses rather than composing.</summary>
    public static string? ArtOf(Image? image)
        => image is null ? null
            : Nz(image.Url) ?? (image.MosaicTiles is { Count: > 0 } tiles ? Nz(tiles[0]) : null);

    static string? Nz(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
