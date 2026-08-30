using System;
using System.Collections.Generic;
using Google.Protobuf;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Hydration;
using Wavee.Core;
using Ca = Wavee.Protocol.ContentAgnostic;
using Xm = Wavee.Protocol.ExtendedMetadata;
// EntityKind: the ONE uri vocabulary (Wavee.Core), not the transport's thin Backend.Metadata projection of it.
using EntityKind = Wavee.Core.EntityKind;

namespace Wavee.SpotifyLive;

// ── The credits drawer (extended-metadata kind 186) ──────────────────────────────────────────────────────────────────
// One read that replaces the capped GraphQL contributor list. `queryNpvArtist` ships `contributorsLimit: 10`; kind 186
// hands back the entire liner note (16–40 rows across the probed tracks), already grouped ("Artist",
// "Composition & Lyrics", "Production & Engineering", "Performers") and already ordered, plus the record label the
// attribution line prints. NPV keeps running for About / TopCities / merch — its credits are now the FALLBACK, taken
// only when 186 has nothing to say.
//
// THIN OVER IExtensionReader (design §2.5). Everything this file used to own — the answers-including-negatives table,
// the in-flight coalescing slot, the etag-cache-or-raw-source fork, the "which token cancels the shared load" rule and
// the client-feature-id constant — is the reader's, once, for all four display-only readers. What is LEFT is the only
// part that is about credits: which uris are worth asking, and how the payload projects. That is the whole point of
// the split; four copies of a cache is how the two arms drifted (only the raw arm ever stamped the attribution header).
sealed class SpotifyTrackCreditsService : ITrackCreditsService
{
    // Unknown fields are discarded rather than retained: nothing here round-trips a payload back to the server, and the
    // corpus already shows Spotify adding fields to these trait payloads over time.
    static readonly MessageParser<Ca.CreditsTrait> PayloadParser = Ca.CreditsTrait.Parser.WithDiscardUnknownFields(true);

    readonly IExtensionReader _reader;
    readonly WaveeLogger _log;

    public SpotifyTrackCreditsService(IExtensionReader reader, WaveeLogger log = default)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _log = log;
    }

    public async Task<TrackCredits?> GetAsync(string trackUri, CancellationToken ct = default)
    {
        // The polymorphic guard lives here, before the request exists: albums, artists and playlists 404 on 186, so
        // asking is pure waste (and would seal a negative under a key no credits surface can ever use). EPISODES are
        // ask-once for the same reason TraitApplicability gives — the probe never covered them, and an episode whose
        // publisher filed credits is exactly the case a "tracks only" guess makes invisible forever; the reader's
        // negative memo makes the wrong guess cost exactly one request per session.
        if (trackUri is not { Length: > 0 } || EntityUri.KindOf(trackUri) is not (EntityKind.Track or EntityKind.Episode))
            return null;

        var credits = await _reader.ReadAsync(trackUri, Xm.ExtensionKind.CreditsV2Trait, ProjectCredits,
                                              TraitSurface.Credits, ct).ConfigureAwait(false);
        if (credits is not null)
            _log.Debug($"credits resolved {trackUri} -> {credits.Credits.Count} rows, sources [{string.Join(", ", credits.Sources)}]");
        return credits;
    }

    /// <summary>The reader's parse hook: bytes → the drawer's shape, or null for "no usable rows" (which the reader
    /// caches and memoizes exactly like a 404 — for the surface they are the same answer). A malformed payload throws
    /// out of here on purpose: the reader logs it and treats undecodable as the same null, in ONE place.</summary>
    static TrackCredits? ProjectCredits(ByteString payload) => Project(PayloadParser.ParseFrom(payload));

    /// <summary>Projects the wire payload onto <see cref="TrackCredits"/>, or null when it carries no usable row. Wire
    /// order is preserved verbatim — the server already grouped and ranked these, and re-sorting them here would put
    /// the drawer out of step with every other Spotify client.</summary>
    static TrackCredits? Project(Ca.CreditsTrait msg)
    {
        var rows = new List<TrackCredit>(msg.Rows.Count);
        foreach (var row in msg.Rows)
        {
            if (row.Name is not { Length: > 0 } name) continue;   // a nameless row has nothing to render
            // artist_uri (and its `nav` companion) are omitted for people Spotify has no artist page for — many
            // engineers have a name and a role and nothing else. Linkable is exactly "field 3 was present".
            string? artistUri = row.ArtistUri is { Length: > 0 } uri ? uri : null;
            rows.Add(new TrackCredit(
                name,
                row.Role,
                RoleGroup: row.Group?.Name is { Length: > 0 } group ? group : null,
                ArtistUri: artistUri,
                Linkable: artistUri is not null));
        }
        if (rows.Count == 0) return null;

        // The label is the whole attribution line, and it is optional: a payload without one renders rows and no source.
        IReadOnlyList<string> sources = msg.Label?.Name is { Length: > 0 } label ? new[] { label } : Array.Empty<string>();
        return new TrackCredits(rows, sources);
    }
}
