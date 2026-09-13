using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wavee.Backend;
using Wavee.Backend.Hydration;
using Wavee.Backend.Metadata;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Xunit;
using Xm = Wavee.Protocol.ExtendedMetadata;
using Pb = Wavee.Protocol.Metadata;

namespace Wavee.Tests;

// The catalogue arm (design §2.2), driven over the REAL ExtendedMetadataSource + ExtensionEtagCache against a
// FakeExchange — so what is pinned is the wire shape (one POST, mixed kinds, extras fused under the uri) rather than a
// re-description of it.
public class XmCatalogFetchTests
{
    static SessionContext Ctx => new("me", "US", "premium", "en", Tier.Premium, false);
    static ByteString Gid(byte fill) { var a = new byte[16]; Array.Fill(a, fill); return ByteString.CopyFrom(a); }

    /// <summary>The uri ProjectTrack derives from a gid — the only uri a TrackV4 payload can land under.</summary>
    static string TrackUri(byte fill) => "spotify:track:" + Base62.Encode(Gid(fill).Span);

    static byte[] TrackResponse(byte gid, string name)
    {
        var track = new Pb.Track { Gid = Gid(gid), Name = name, Duration = 210_000 };
        track.Artist.Add(new Pb.Artist { Gid = Gid(0xAA), Name = "Artist One" });
        track.Album = new Pb.Album { Gid = Gid(0xBB), Name = "Album One" };
        var array = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.TrackV4 };
        array.ExtensionData.Add(new Xm.EntityExtensionData
        {
            EntityUri = TrackUri(gid),
            Header = new Xm.EntityExtensionDataHeader { StatusCode = 200, OfflineTtlInSeconds = 3600 },
            ExtensionData = Any.Pack(track),
        });
        var resp = new Xm.BatchedExtensionResponse();
        resp.ExtendedMetadata.Add(array);
        return resp.ToByteArray();
    }

    static byte[] PlaylistHeaderResponse(string uri, string name)
    {
        var array = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.ListMetadataV2 };
        array.ExtensionData.Add(new Xm.EntityExtensionData
        {
            EntityUri = uri,
            ExtensionData = Any.Pack(new Xm.ListMetadataV2 { Name = name }),
        });
        var resp = new Xm.BatchedExtensionResponse();
        resp.ExtendedMetadata.Add(array);
        return resp.ToByteArray();
    }

    static (XmCatalogFetch Fetch, InMemoryStore Store, FakeExchange Http, List<HttpReq> Sent) Harness(
        Func<HttpReq, int, byte[]> body)
    {
        var sent = new List<HttpReq>();
        var http = new FakeExchange((req, call) =>
        {
            sent.Add(req);
            return new HttpResp(200, new Dictionary<string, string>(), body(req, call));
        });
        var store = new InMemoryStore();
        var source = new ExtendedMetadataSource(http, () => "https://spclient.test", () => Ctx);
        var cache = new ExtensionEtagCache(source, () => Ctx);
        return (new XmCatalogFetch(cache, store), store, http, sent);
    }

    static Xm.BatchedEntityRequest Decode(HttpReq req)
        => Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(req.Body!));

    [Fact]
    public async Task MixedKinds_RideOnePost()
    {
        var (fetch, _, http, sent) = Harness((_, _) => new Xm.BatchedExtensionResponse().ToByteArray());
        var uris = new[]
        {
            EntityUri.Parse("spotify:track:t1"),
            EntityUri.Parse("spotify:album:al1"),
            EntityUri.Parse("spotify:playlist:p1"),
            EntityUri.Parse("spotify:show:s1"),
        };

        await fetch.FetchAsync(uris, null, TraitSurface.AlbumOpen, TestContext.Current.CancellationToken);

        Assert.Equal(1, http.Calls);
        var body = Decode(Assert.Single(sent));
        Assert.Equal(4, body.EntityRequest.Count);
        var kinds = body.EntityRequest.ToDictionary(e => e.EntityUri, e => e.Query.Single().ExtensionKind);
        Assert.Equal(Xm.ExtensionKind.TrackV4, kinds["spotify:track:t1"]);
        Assert.Equal(Xm.ExtensionKind.AlbumV4, kinds["spotify:album:al1"]);
        // A playlist's header is 205, NOT a V4 — the one asymmetry, and the reason a playlist pointer used to be
        // dropped before a query was written.
        Assert.Equal(Xm.ExtensionKind.ListMetadataV2, kinds["spotify:playlist:p1"]);
        Assert.Equal(Xm.ExtensionKind.ShowV4, kinds["spotify:show:s1"]);
    }

    [Fact]
    public async Task ExtraKinds_AreFusedUnderTheSameEntityRequest()
    {
        var (fetch, _, http, sent) = Harness((_, _) => new Xm.BatchedExtensionResponse().ToByteArray());

        await fetch.FetchAsync([EntityUri.Parse("spotify:album:al1")],
            [("spotify:album:al1", (int)Xm.ExtensionKind.PublishingMetadataTrait)],
            TraitSurface.AlbumOpen, TestContext.Current.CancellationToken);

        Assert.Equal(1, http.Calls);   // a Rich album open is ONE POST, not a V4 pass plus a publishing pass
        var entity = Assert.Single(Decode(Assert.Single(sent)).EntityRequest);
        Assert.Equal("spotify:album:al1", entity.EntityUri);
        Assert.Equal([Xm.ExtensionKind.AlbumV4, Xm.ExtensionKind.PublishingMetadataTrait],
            entity.Query.Select(q => q.ExtensionKind).ToArray());
    }

    [Fact]
    public async Task UrisWithNoCatalogueKind_AreNeverSent()
    {
        var (fetch, _, http, _) = Harness((_, _) => new Xm.BatchedExtensionResponse().ToByteArray());

        var landed = await fetch.FetchAsync(
            [EntityUri.Parse("spotify:collection:tracks"), EntityUri.Parse("spotify:user:bob"),
             EntityUri.Parse("spotify:concert:c1")],
            null, TraitSurface.None, TestContext.Current.CancellationToken);

        Assert.Equal(0, http.Calls);
        Assert.Empty(landed);
    }

    [Fact]
    public async Task Landed_IsWhatProjected_NotWhatWasAsked()
    {
        string projected = TrackUri(0x11);
        var (fetch, store, _, _) = Harness((_, _) => TrackResponse(0x11, "Real Song"));

        var landed = await fetch.FetchAsync(
            [EntityUri.Parse(projected), EntityUri.Parse("spotify:album:al1")],
            null, TraitSurface.Queue, TestContext.Current.CancellationToken);

        // The album was requested and the server said nothing about it: it must stay OUT of landed so the ledger does
        // not seal an entity that never arrived (outcome seeding, not batch-membership seeding).
        Assert.Equal(projected, Assert.Single(landed));
        Assert.Equal("Real Song", store.GetTrack(projected)!.Title);
        Assert.Null(store.GetAlbum("spotify:album:al1"));
    }

    [Fact]
    public async Task ClientFeatureId_ComesFromTheSurface()
    {
        var (fetch, _, _, sent) = Harness((_, _) => new Xm.BatchedExtensionResponse().ToByteArray());

        await fetch.FetchAsync([EntityUri.Parse("spotify:album:al1")], null, TraitSurface.Recents,
            TestContext.Current.CancellationToken);
        await fetch.FetchAsync([EntityUri.Parse("spotify:album:al2")], null, TraitSurface.AlbumOpen,
            TestContext.Current.CancellationToken);
        await fetch.FetchAsync([EntityUri.Parse("spotify:album:al3")], null, TraitSurface.None,
            TestContext.Current.CancellationToken);

        Assert.Equal("mdata_esperanto", sent[0].Headers["client-feature-id"]);
        Assert.Equal("track_metadata_loader", sent[1].Headers["client-feature-id"]);
        // Unattributed traffic omits the header — the pre-existing wire shape, not an invented attribution.
        Assert.False(sent[2].Headers.ContainsKey("client-feature-id"));
    }

    // Ported from MetadataSourceTests.FetchAsync_BuildsBatchedPost_AndProjects when the unconditional bulk arm died:
    // the transport contract (POST, the extended-metadata path, gzipped protobuf body, Accept-Language from the session)
    // is unchanged - only the caller is. Projection through to the store is asserted in the same pass, because a POST
    // that lands nothing is the failure mode this case exists to catch.
    [Fact]
    public async Task Post_CarriesTheTransportHeaders_AndProjects()
    {
        string projected = TrackUri(0x33);
        var (fetch, store, _, sent) = Harness((_, _) => TrackResponse(0x33, "Fetched Track"));

        await fetch.FetchAsync([EntityUri.Parse(projected)], null, TraitSurface.Queue, TestContext.Current.CancellationToken);

        var req = Assert.Single(sent);
        Assert.Equal("POST", req.Method);
        Assert.EndsWith("/extended-metadata/v0/extended-metadata", req.Url);
        Assert.Equal("gzip", req.Headers["Content-Encoding"]);          // request body gzipped
        Assert.Equal("application/protobuf", req.Headers["Content-Type"]);
        Assert.Equal("en", req.Headers["Accept-Language"]);
        Assert.NotNull(req.Body);
        Assert.Equal("Fetched Track", store.GetTrack(projected)!.Title);
    }

    [Fact]
    public async Task SecondPass_IsServedFromTheEtagCache()
    {
        string projected = TrackUri(0x22);
        var (fetch, _, http, _) = Harness((_, _) => TrackResponse(0x22, "Cached Song"));

        await fetch.FetchAsync([EntityUri.Parse(projected)], null, TraitSurface.Queue, TestContext.Current.CancellationToken);
        var landed = await fetch.FetchAsync([EntityUri.Parse(projected)], null, TraitSurface.Queue, TestContext.Current.CancellationToken);

        // The cached payload is re-projected (idempotent) without a second POST — the etag cache is REQUIRED for
        // exactly this, and the raw-fallback arm that skipped it is what re-downloaded payloads already held.
        Assert.Equal(1, http.Calls);
        Assert.Equal(projected, Assert.Single(landed));
    }

    // Cause (5) of the stale-daylist-header defect: a user-initiated open (Revalidate:true) must not be answered from
    // a cached body that is well inside its own 6h TTL — a rolling-identity playlist (a daylist) can roll to a new
    // edition server-side while its cached LIST_METADATA_V2 row still looks perfectly fresh from here.
    [Fact]
    public async Task RevalidateTrue_ForcesAFreshGet_EvenInsideTheEtagTtl()
    {
        const string uri = "spotify:playlist:p1";
        int call = 0;
        var (fetch, store, http, _) = Harness((_, _) =>
        {
            call++;
            return PlaylistHeaderResponse(uri, call == 1 ? "Old Edition" : "New Edition");
        });

        await fetch.FetchAsync([EntityUri.Parse(uri)], null, TraitSurface.None, TestContext.Current.CancellationToken);
        Assert.Equal("Old Edition", store.GetPlaylist(uri)!.Name);

        // Without revalidate, a second ask inside the TTL is the ordinary cache hit — no second POST.
        await fetch.FetchAsync([EntityUri.Parse(uri)], null, TraitSurface.None, TestContext.Current.CancellationToken);
        Assert.Equal(1, http.Calls);
        Assert.Equal("Old Edition", store.GetPlaylist(uri)!.Name);

        // revalidate:true marks the cached row stale first, so this ask goes back to the wire and adopts the new body.
        await fetch.FetchAsync([EntityUri.Parse(uri)], null, TraitSurface.None, TestContext.Current.CancellationToken, revalidate: true);
        Assert.Equal(2, http.Calls);
        Assert.Equal("New Edition", store.GetPlaylist(uri)!.Name);
    }
}
