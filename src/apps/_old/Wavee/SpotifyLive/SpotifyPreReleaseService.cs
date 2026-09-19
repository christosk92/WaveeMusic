using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Wavee.Backend.Hydration;
using Wavee.Core;
using Pb = Wavee.Protocol.PreRelease;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.SpotifyLive;

// ── Upcoming-release identity (extended-metadata kind 138) ─────────────────────────────────────────────
// One read that answers two questions at once. The wire serves the SAME payload under both a `spotify:album:` and a
// `spotify:prerelease:` entity_uri (E2E-DIFF.md §5.4.1), and the payload names both uris — so a single round trip with
// whichever uri the caller happens to hold resolves the pair in both directions. Their ids differ (see PreReleaseUris),
// so this kind is the ONLY mapping; nothing may synthesise one uri from the other.
//
// Kind 138 404s for almost every entity (3 of the 5 captured entities 404'd), which is why a miss is a cached NULL and
// never an error: "no upcoming release" is the correct answer for every album that is already out, and the announce
// surfaces simply do not render.
//
// THIN OVER IExtensionReader (design §2.5): the answers-including-negatives table, the in-flight slot, the
// etag-cache-or-raw fork and the cancellation rule are the reader's. What stays here is the projection — and the ONE
// thing that is genuinely this kind's own: the THREE-KEY PUBLISH. A positive payload names both uris, so the answer is
// SEEDED under the payload's prerelease uri and its album uri as well as under the uri the caller happened to hold.
// That is what makes one round trip serve both directions (the artist masthead resolves an album uri; the pre-save
// heart later asks with the prerelease uri) — and it is a SEED, never a wire outcome, so it deliberately does not
// touch the negative memo.
sealed class SpotifyPreReleaseService : IPreReleaseService
{
    // Unknown fields are discarded rather than retained: nothing here round-trips a payload back to the server, and the
    // corpus already shows Spotify adding fields to these trait payloads over time.
    static readonly MessageParser<Pb.Prerelease> PayloadParser = Pb.Prerelease.Parser.WithDiscardUnknownFields(true);

    readonly IExtensionReader _reader;
    readonly WaveeLogger _log;

    public SpotifyPreReleaseService(IExtensionReader reader, WaveeLogger log = default)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _log = log;
    }

    public async Task<PreReleaseLink?> ResolveAsync(string uri, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(uri)) return null;

        var link = await _reader.ReadAsync(uri, Xm.ExtensionKind.Prerelease, Link, TraitSurface.PreRelease, ct)
                                .ConfigureAwait(false);
        if (link is null) return null;

        // The other two keys of the pair. Seeding is idempotent and cheap, so it runs on every hit rather than only on
        // the fetch that produced it — the reader hands back the SAME instance either way.
        _reader.Seed(link.PreReleaseUri, Xm.ExtensionKind.Prerelease, link);
        _reader.Seed(link.AlbumUri, Xm.ExtensionKind.Prerelease, link);
        _log.Debug($"prerelease resolved {uri} -> {link.PreReleaseUri} / {link.AlbumUri} at {link.ReleaseAt}");
        return link;
    }

    /// <summary>The reader's parse hook. A HALF-LINK returns null, which the reader caches and memoizes exactly like a
    /// 404 — for every surface downstream they are the same answer.</summary>
    static PreReleaseLink? Link(ByteString payload) => Link(PayloadParser.ParseFrom(payload));

    /// <summary>Projects the wire payload onto <see cref="PreReleaseLink"/>, or null when it is not a usable link.</summary>
    static PreReleaseLink? Link(Pb.Prerelease msg)
    {
        string prereleaseUri = msg.PrereleaseUri;
        string albumUri = msg.Release?.AlbumUri ?? "";
        // HALF-LINKS ARE REJECTED. The two uris cannot be derived from one another, so a payload carrying only one of
        // them is not a link: with no album uri nothing can be navigated to, and with no prerelease uri nothing can be
        // pre-saved. Returning null here (rather than a link with an empty field) keeps that impossible state out of
        // the record entirely — every consumer downstream may assume both uris are real.
        if (prereleaseUri.Length == 0 || albumUri.Length == 0) return null;

        var release = msg.Release!;
        // seconds == 0 is "absent", not 1970: an announced-but-undated release is a real shape, and a null ReleaseAt is
        // what PreReleaseLink.IsUpcoming reads as "upcoming, date unknown".
        DateTimeOffset? releaseAt = msg.ReleaseAt is { Seconds: > 0 } t ? DateTimeOffset.FromUnixTimeSeconds(t.Seconds) : null;

        ArtistRef? artist = release.Artist is { Uri.Length: > 0 } a
            ? new ArtistRef(EntityUri.IdOf(a.Uri), a.Uri, a.Name)
            : null;

        return new PreReleaseLink(
            prereleaseUri,
            albumUri,
            releaseAt,
            Name: release.Name is { Length: > 0 } name ? name : null,
            Type: release.Type is { Length: > 0 } type ? type : null,
            Artist: artist,
            Cover: Cover(release.Images));
    }

    /// <summary>The cover to show. Prefer the "DEFAULT" rendition (600 px in the captured payload) — it is the one
    /// Spotify's own surfaces use — and fall back to the largest by area when the size names change or are absent.
    /// The LARGE rendition rides along as <see cref="Image.LargestUrl"/> so an immersive surface is not stuck with the
    /// card-sized URL. Sizes are STRINGS in this kind, unlike the integer enums of kinds 179/98.</summary>
    static Image? Cover(RepeatedField<Pb.Prerelease.Types.Image>? images)
    {
        if (images is null || images.Count == 0) return null;

        Pb.Prerelease.Types.Image? best = null, largest = null;
        foreach (var img in images)
        {
            if (img.Url.Length == 0) continue;
            if (largest is null || (long)img.Width * img.Height > (long)largest.Width * largest.Height) largest = img;
            if (best is null && string.Equals(img.Size, "DEFAULT", StringComparison.OrdinalIgnoreCase)) best = img;
        }
        best ??= largest;
        if (best is null) return null;

        string? largestUrl = largest is not null && !ReferenceEquals(largest, best) ? largest.Url : null;
        return new Image(best.Url,
            best.Width > 0 ? (int?)best.Width : null,
            best.Height > 0 ? (int?)best.Height : null,
            LargestUrl: largestUrl);
    }
}
