using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wavee.Backend;
using Wavee.Backend.Hydration;
using Wavee.Backend.Metadata;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.SpotifyLive;
using Xunit;
using Ca = Wavee.Protocol.ContentAgnostic;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Tests;

// ── Extension kind 186 (CREDITS_V2_TRAIT): the uncapped credits drawer ───────────────────────────────────────────────
// The GraphQL NPV path caps contributors at 10; 186 hands back the whole liner note, already grouped and already
// ordered, plus the record label the attribution line prints. These drive the real service over crafted protobuf: wire
// order and grouping survive the projection, an absent artist_uri means "not linkable", and the two ways a track can
// have no drawer (a 404, or a non-track uri) both answer null without asking twice.
public class TrackCreditsTests
{
    const string TrackUri = "spotify:track:0spnMEFDuWTQRTsI941Q5n";
    const string Label = "WaterTower Music";

    static SessionContext Ctx => new("me", "US", "premium", "en", Tier.Premium, false);
    static CancellationToken CT => TestContext.Current.CancellationToken;

    // ── the payload ───────────────────────────────────────────────────────────────────────────────────────────────────
    // The four groups the probe saw on every track, in the server's own order. Alan Meyerson and Geoff Foster are the
    // real shape of an unlinked row: a name and a role, no artist page.

    static Ca.CreditRow Row(string name, string role, string group, string? artistUri = null)
    {
        var row = new Ca.CreditRow { Name = name, Role = role, Group = new Ca.CreditRow.Types.Group { Name = group } };
        if (artistUri is not null) { row.ArtistUri = artistUri; row.Nav = artistUri; }
        return row;
    }

    static Ca.CreditsTrait Message(string? label = Label)
    {
        var msg = new Ca.CreditsTrait();
        msg.Rows.Add(Row("Hans Zimmer", "Main Artist", "Artist", "spotify:artist:0YC192cP3KPCRWx8zr8MfZ"));
        msg.Rows.Add(Row("Hans Zimmer", "Composer", "Composition & Lyrics", "spotify:artist:0YC192cP3KPCRWx8zr8MfZ"));
        msg.Rows.Add(Row("Lorne Balfe", "Music", "Composition & Lyrics", "spotify:artist:4Ge8kJtDcuLzXTXK0aVNPq"));
        msg.Rows.Add(Row("Alan Meyerson", "Mixer", "Production & Engineering"));
        msg.Rows.Add(Row("Geoff Foster", "Recorded by", "Production & Engineering"));
        msg.Rows.Add(Row("Johnny Marr", "Guitar", "Performers", "spotify:artist:5cktN8sB0ie0iZgTQFwEz1"));
        msg.Rows.Add(Row("Anthony Pleeth", "Cello", "Performers"));
        if (label is not null) msg.Label = new Ca.CreditsTrait.Types.Label { Name = label };
        return msg;
    }

    // ── the harness ───────────────────────────────────────────────────────────────────────────────────────────────────
    // `answers` maps entity_uri → payload (null = the 404 the probe got on every album and artist). The responder stamps
    // the per-entity status header, which is what lets the etag cache seal a real Missing row rather than leaving the
    // key unsealed.

    sealed class Wire
    {
        public readonly List<string> Requested = new();
        public readonly HashSet<Xm.ExtensionKind> Kinds = new();
        public readonly List<string?> FeatureIds = new();
        public int Posts;

        /// <summary>Holds every request until the test releases it — ASYNCHRONOUSLY, because <see cref="FakeExchange"/>
        /// answers on the calling thread and blocking there would deadlock the very coalescing this exposes.</summary>
        public readonly TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    sealed class GatedExchange(IHttpExchange inner, Task gate) : IHttpExchange
    {
        public async Task<HttpResp> SendAsync(HttpReq req, CancellationToken ct)
        {
            await gate.ConfigureAwait(false);
            return await inner.SendAsync(req, ct).ConfigureAwait(false);
        }
    }

    static (ExtensionReader Reader, ExtensionEtagCache Cache, Wire Log) Rig(Func<string, Ca.CreditsTrait?> answers, bool gated = false)
    {
        var log = new Wire();
        var http = new FakeExchange((req, _) =>
        {
            log.Posts++;
            log.FeatureIds.Add(req.Headers.TryGetValue("client-feature-id", out var cfid) ? cfid : null);
            var parsed = Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(req.Body!));
            var array = new Xm.EntityExtensionDataArray
            {
                ExtensionKind = Xm.ExtensionKind.CreditsV2Trait,
                Header = new Xm.EntityExtensionDataArrayHeader { OfflineTtlInSeconds = 86_400 },
            };
            foreach (var er in parsed.EntityRequest)
            {
                log.Requested.Add(er.EntityUri);
                foreach (var q in er.Query) log.Kinds.Add(q.ExtensionKind);
                var payload = answers(er.EntityUri);
                var data = new Xm.EntityExtensionData
                {
                    EntityUri = er.EntityUri,
                    Header = new Xm.EntityExtensionDataHeader { StatusCode = payload is null ? 404 : 200 },
                };
                if (payload is not null) data.ExtensionData = new Any { Value = payload.ToByteString() };
                array.ExtensionData.Add(data);
            }
            var resp = new Xm.BatchedExtensionResponse();
            resp.ExtendedMetadata.Add(array);
            return new HttpResp(200, new Dictionary<string, string>(), resp.ToByteArray());
        });
        var em = new ExtendedMetadataSource(gated ? new GatedExchange(http, log.Gate.Task) : http,
                                           () => "https://spclient.test", () => Ctx);
        var cache = new ExtensionEtagCache(em, () => Ctx);
        // The service is THIN over this reader (design §2.5) — the answers-including-negatives table, the coalescing
        // slot and the attribution header all live here now, so the tests drive the real reader, not a stand-in.
        return (new ExtensionReader(cache, new NegativeMemo()), cache, log);
    }

    static Func<string, Ca.CreditsTrait?> Only(string uri, Ca.CreditsTrait msg) => u => u == uri ? msg : null;

    // ── the drawer ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheDrawer_KeepsWireOrder_TheServerGrouping_AndTheLabel()
    {
        var (reader, cache, log) = Rig(Only(TrackUri, Message()));
        var svc = new SpotifyTrackCreditsService(reader);

        var credits = await svc.GetAsync(TrackUri, CT);

        Assert.NotNull(credits);
        // Wire order verbatim — the server already grouped and ranked these; re-sorting would put the drawer out of
        // step with every other Spotify client.
        Assert.Equal(
            new[] { "Hans Zimmer", "Hans Zimmer", "Lorne Balfe", "Alan Meyerson", "Geoff Foster", "Johnny Marr", "Anthony Pleeth" },
            credits!.Credits.Select(c => c.Name).ToArray());
        Assert.Equal(
            new string?[] { "Artist", "Composition & Lyrics", "Composition & Lyrics", "Production & Engineering",
                            "Production & Engineering", "Performers", "Performers" },
            credits.Credits.Select(c => c.RoleGroup).ToArray());
        Assert.Equal("Main Artist", credits.Credits[0].Role);
        Assert.Equal("Recorded by", credits.Credits[4].Role);
        // The record label IS the attribution line — one source, same shape as TrackNpvInfo.CreditSources.
        Assert.Equal(new[] { Label }, credits.Sources);
        Assert.Contains(Xm.ExtensionKind.CreditsV2Trait, log.Kinds);
    }

    [Fact]
    public async Task ARowIsLinkable_ExactlyWhenTheWireGaveItAnArtistUri()
    {
        var (reader, cache, _) = Rig(Only(TrackUri, Message()));
        var svc = new SpotifyTrackCreditsService(reader);

        var credits = await svc.GetAsync(TrackUri, CT);

        // Many engineers have a name and a role and nothing else — those rows must render as plain text, never as a
        // link to nowhere.
        Assert.Equal(new[] { true, true, true, false, false, true, false }, credits!.Credits.Select(c => c.Linkable).ToArray());
        foreach (var c in credits.Credits)
            Assert.Equal(c.Linkable, c.ArtistUri is { Length: > 0 });
        Assert.Equal("spotify:artist:5cktN8sB0ie0iZgTQFwEz1", credits.Credits[5].ArtistUri);
        Assert.Null(credits.Credits[3].ArtistUri);
    }

    [Fact]
    public async Task ALabellessPayload_StillRendersItsRows()
    {
        var (reader, cache, _) = Rig(Only(TrackUri, Message(label: null)));
        var svc = new SpotifyTrackCreditsService(reader);

        var credits = await svc.GetAsync(TrackUri, CT);

        Assert.Equal(7, credits!.Credits.Count);
        Assert.Empty(credits.Sources);
    }

    [Fact]
    public async Task A200WithNoUsableRow_IsTheSameAnswerAsA404()
    {
        var (reader, cache, _) = Rig(Only(TrackUri, new Ca.CreditsTrait { Label = new Ca.CreditsTrait.Types.Label { Name = Label } }));
        var svc = new SpotifyTrackCreditsService(reader);

        Assert.Null(await svc.GetAsync(TrackUri, CT));
    }

    // ── the negatives ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A404_IsANullAnswer_AndNothingReAsksTheWire()
    {
        // 404 is the ordinary answer for a track whose label filed no credits — the surface keeps the NPV rows.
        var (reader, cache, log) = Rig(_ => null);
        var svc = new SpotifyTrackCreditsService(reader);

        Assert.Null(await svc.GetAsync(TrackUri, CT));
        Assert.Equal(1, log.Posts);

        // The per-session negative stops the re-render storm…
        Assert.Null(await svc.GetAsync(TrackUri, CT));
        Assert.Equal(1, log.Posts);

        // …and the etag cache's 24 h Missing row stops even a service over a FRESH reader (its own parsed cache, its
        // own negative memo) from paying for the same miss — the durable half of the answer is one tier down.
        var second = new SpotifyTrackCreditsService(new ExtensionReader(cache, new NegativeMemo()));
        Assert.Null(await second.GetAsync(TrackUri, CT));
        Assert.Equal(1, log.Posts);
    }

    [Fact]
    public async Task NonTrackUris_AreAnsweredWithoutARequest()
    {
        // Albums and artists 404 on this kind (the probe's two non-track entities), so the guard lives before the
        // request exists — asking would be pure waste.
        var (reader, cache, log) = Rig(_ => Message());
        var svc = new SpotifyTrackCreditsService(reader);

        Assert.Null(await svc.GetAsync("spotify:album:5xLkGYD86FbxWY7DcQP0Fk", CT));
        Assert.Null(await svc.GetAsync("spotify:artist:0YC192cP3KPCRWx8zr8MfZ", CT));
        Assert.Null(await svc.GetAsync("", CT));
        Assert.Equal(0, log.Posts);
        Assert.Empty(log.Requested);
    }

    [Fact]
    public async Task TheRequestBodyCarriesKind186_AndNothingElse()
    {
        var (reader, cache, log) = Rig(Only(TrackUri, Message()));
        var svc = new SpotifyTrackCreditsService(reader);

        await svc.GetAsync(TrackUri, CT);

        Assert.Equal(new[] { TrackUri }, log.Requested);
        Assert.Equal(new[] { Xm.ExtensionKind.CreditsV2Trait }, log.Kinds);
        Assert.Equal(186, (int)Xm.ExtensionKind.CreditsV2Trait);
    }

    [Fact]
    public async Task TheCreditsRead_CarriesTheClientFeatureId()
    {
        // There used to be TWO arms here — an etag-cache arm and a raw-source fallback — and only the raw one stamped
        // the attribution. There is now ONE arm, through the reader, and TraitSurfaces.ClientFeatureId(Credits) is what
        // decides the header, so the drawer's traffic is attributed exactly like the desktop client's.
        var (reader, _, log) = Rig(Only(TrackUri, Message()));
        var svc = new SpotifyTrackCreditsService(reader);

        var credits = await svc.GetAsync(TrackUri, CT);

        Assert.Equal(7, credits!.Credits.Count);
        Assert.Equal(new[] { Label }, credits.Sources);
        Assert.Equal(1, log.Posts);
        Assert.Equal(new string?[] { "track_metadata_loader" }, log.FeatureIds);
    }

    [Fact]
    public async Task TwoConcurrentAsks_ShareONERequest()
    {
        // The Now Playing rail and the "View credits" dialog routinely ask for the same track at the same moment. The
        // wire is HELD until both readers are attached, so this is real coalescing rather than the second ask simply
        // finding the first one's cached answer.
        var (reader, _, log) = Rig(Only(TrackUri, Message()), gated: true);
        var svc = new SpotifyTrackCreditsService(reader);

        var first = svc.GetAsync(TrackUri, CT);
        var second = svc.GetAsync(TrackUri, CT);
        log.Gate.SetResult();
        var both = await Task.WhenAll(first, second);

        Assert.Equal(1, log.Posts);
        Assert.All(both, c => Assert.Equal(7, c!.Credits.Count));
        Assert.Same(both[0], both[1]);        // ONE parsed answer, not two decodes of the same 40 KB
    }
}
