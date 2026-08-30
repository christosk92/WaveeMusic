using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Xunit;
using Xm = Wavee.Protocol.ExtendedMetadata;
using Pb = Wavee.Protocol.Metadata;

namespace Wavee.Tests;

// The REAL extended-metadata source, exercised end-to-end (build request → parse response → project) over crafted
// protobuf — the same wire shape spclient returns. No network needed; the live POST is the only unverifiable part.
public class ExtendedMetadataSourceTests
{
    static SessionContext Ctx => new("me", "US", "premium", "en", Tier.Premium, false);
    static byte[] Bytes(byte fill) { var a = new byte[16]; Array.Fill(a, fill); return a; }
    static ByteString Gid(byte fill) => ByteString.CopyFrom(Bytes(fill));

    static byte[] CraftTrackResponse(string name, int durationMs, bool isExplicit, bool includeAlbumCover = false)
    {
        var track = new Pb.Track { Gid = Gid(0x11), Name = name, Duration = durationMs, Explicit = isExplicit };
        track.Artist.Add(new Pb.Artist { Gid = Gid(0xAA), Name = "Artist One" });
        track.Album = new Pb.Album { Gid = Gid(0xBB), Name = "Album One" };
        track.ExternalId.Add(new Pb.ExternalId { Type = "isrc", Id = "USRC17607839" });   // field 10 → projected Track.Isrc
        if (includeAlbumCover)
        {
            track.Album.CoverGroup = new Pb.ImageGroup();
            track.Album.CoverGroup.Image.Add(new Pb.Image
            {
                FileId = Gid(0xCC),
                Size = Pb.Image.Types.Size.Default,
                Width = 300,
                Height = 300,
            });
        }

        var array = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.TrackV4 };
        array.ExtensionData.Add(new Xm.EntityExtensionData { EntityUri = "spotify:track:x", ExtensionData = Any.Pack(track) });
        var resp = new Xm.BatchedExtensionResponse();
        resp.ExtendedMetadata.Add(array);
        return resp.ToByteArray();
    }

    [Fact]
    public void ProjectResponse_ParsesTrackProto_IntoDomain()
    {
        var store = new InMemoryStore();
        ExtendedMetadataSource.ProjectResponse(CraftTrackResponse("Real Song", 234000, true), store);

        var t = Assert.Single(store.QueryTracks());   // uri is base62(gid), so look it up via the store
        Assert.Equal("Real Song", t.Title);
        Assert.Equal(234000, t.DurationMs);
        Assert.True(t.IsExplicit);
        Assert.Equal("Artist One", t.Artists[0].Name);
        Assert.StartsWith("spotify:artist:", t.Artists[0].Uri);
        Assert.Equal("Album One", t.Album.Name);
        Assert.StartsWith("spotify:track:", t.Uri);
    }

    [Fact]
    public void ProjectResponse_ExtractsIsrc_FromExternalId()
    {
        var store = new InMemoryStore();
        ExtendedMetadataSource.ProjectResponse(CraftTrackResponse("Real Song", 234000, true), store);
        // The LeanTrack parser now reads Track.external_id (field 10) instead of discarding it.
        Assert.Equal("USRC17607839", Assert.Single(store.QueryTracks()).Isrc);
    }

    [Fact]
    public void ProjectResponse_ProjectsTrackAlbumCover()
    {
        var store = new InMemoryStore();
        ExtendedMetadataSource.ProjectResponse(CraftTrackResponse("Cover Song", 234000, false, includeAlbumCover: true), store);

        var t = Assert.Single(store.QueryTracks());
        Assert.NotNull(t.Image);
        Assert.StartsWith("https://i.scdn.co/image/", t.Image!.Url);
        Assert.Equal(300, t.Image.Width);
        Assert.Equal(300, t.Image.Height);
    }

    [Fact]
    public void ProjectResponse_ProjectsAlbumAndArtist()
    {
        var store = new InMemoryStore();

        var album = new Pb.Album { Gid = Gid(0x01), Name = "The Album", Date = new Pb.Date { Year = 2021 } };
        album.Artist.Add(new Pb.Artist { Gid = Gid(0x02), Name = "AA" });
        var albumArray = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.AlbumV4 };
        albumArray.ExtensionData.Add(new Xm.EntityExtensionData { EntityUri = "spotify:album:x", ExtensionData = Any.Pack(album) });

        var artist = new Pb.Artist { Gid = Gid(0x03), Name = "The Artist" };
        var artistArray = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.ArtistV4 };
        artistArray.ExtensionData.Add(new Xm.EntityExtensionData { EntityUri = "spotify:artist:x", ExtensionData = Any.Pack(artist) });

        var resp = new Xm.BatchedExtensionResponse();
        resp.ExtendedMetadata.Add(albumArray);
        resp.ExtendedMetadata.Add(artistArray);
        ExtendedMetadataSource.ProjectResponse(resp.ToByteArray(), store);

        var al = store.GetAlbum("spotify:album:" + Base62.Encode(Bytes(0x01)));   // uri = base62(gid), the source's scheme
        Assert.NotNull(al);
        Assert.Equal("The Album", al!.Name);
        Assert.Equal(2021, al.Year);
        Assert.Equal("The Artist", store.GetArtist("spotify:artist:" + Base62.Encode(Bytes(0x03)))!.Name);
    }

    // FetchAsync_BuildsBatchedPost_AndProjects and GzipRequest_RoundTripsAValidBatchedRequest are DELETED with the
    // unconditional bulk arm (FetchAsync/GzipRequest/EntityRef, hydration-facade-plan.md 1.6). Replacements:
    //   the POST shape + transport headers -> XmCatalogFetchTests.Post_CarriesTheTransportHeaders_AndProjects
    //   the routing-kind -> catalogue-kind mapping (incl. playlist=205, collection skipped)
    //     -> XmCatalogFetchTests.MixedKinds_RideOnePost + .UrisWithNoCatalogueKind_AreNeverSent

    // The HEADER-TRAIT bundle (178/179/220) and its four GzipRequest/SyncAll cases are DELETED: `headerTraits` was a
    // parameter of the bulk arm, and fusing extra kinds under a uri is now the catalogue arm's ordinary shape.
    // Replacements: XmCatalogFetchTests.ExtraKinds_AreFusedUnderTheSameEntityRequest (extra kinds ride the SAME
    // EntityRequest as the catalogue kind, one POST) and .ClientFeatureId_ComesFromTheSurface (the per-surface
    // attribution header, including the Recents `mdata_esperanto` flavour and the unattributed no-header case).

    [Fact]
    public void ProjectResponse_DedupesRepeatedArtistRef_AcrossTracks()
    {
        var store = new InMemoryStore();
        var sharedArtist = Gid(0x55);
        var t1 = new Pb.Track { Gid = Gid(0x01), Name = "One" }; t1.Artist.Add(new Pb.Artist { Gid = sharedArtist, Name = "Shared" });
        var t2 = new Pb.Track { Gid = Gid(0x02), Name = "Two" }; t2.Artist.Add(new Pb.Artist { Gid = sharedArtist, Name = "Shared" });
        var array = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.TrackV4 };
        array.ExtensionData.Add(new Xm.EntityExtensionData { EntityUri = "spotify:track:1", ExtensionData = Any.Pack(t1) });
        array.ExtensionData.Add(new Xm.EntityExtensionData { EntityUri = "spotify:track:2", ExtensionData = Any.Pack(t2) });
        var resp = new Xm.BatchedExtensionResponse();
        resp.ExtendedMetadata.Add(array);

        ExtendedMetadataSource.ProjectResponse(resp.ToByteArray(), store);

        var tracks = store.QueryTracks();
        Assert.Equal(2, tracks.Count);
        Assert.Same(tracks[0].Artists[0], tracks[1].Artists[0]);   // memoized → the SAME ArtistRef instance, not two copies
    }

    // ── ArtistV4 discography projection (the V4 path that replaced queryArtistDiscography*) ──
    // One AlbumGroup head = one gid-only stub album; album[0] is the representative release (versions grouped).
    static Pb.AlbumGroup Group(byte gid)
    {
        var g = new Pb.AlbumGroup();
        g.Album.Add(new Pb.Album { Gid = Gid(gid) });   // gid only → a stub (Name/Cover absent, as on the real wire)
        return g;
    }

    static byte[] CraftArtistResponse(Pb.Artist artist, string uri = "spotify:artist:x")
    {
        var array = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.ArtistV4 };
        array.ExtensionData.Add(new Xm.EntityExtensionData { EntityUri = uri, ExtensionData = Any.Pack(artist) });
        var resp = new Xm.BatchedExtensionResponse();
        resp.ExtendedMetadata.Add(array);
        return resp.ToByteArray();
    }

    [Fact]
    public void ProjectResponse_ArtistV4_ProjectsDiscographyStubs_TotalsAndBio()
    {
        var store = new InMemoryStore();
        var artist = new Pb.Artist { Gid = Gid(0x30), Name = "Disco Artist" };
        artist.AlbumGroup.Add(Group(0x40)); artist.AlbumGroup.Add(Group(0x41));           // 2 albums
        artist.SingleGroup.Add(Group(0x50)); artist.SingleGroup.Add(Group(0x51)); artist.SingleGroup.Add(Group(0x52));   // 3 singles
        artist.CompilationGroup.Add(Group(0x60));                                          // 1 compilation
        artist.AppearsOnGroup.Add(Group(0x70));                                            // 1 appears-on
        artist.Biography.Add(new Pb.Biography { Text = "A short bio." });

        ExtendedMetadataSource.ProjectResponse(CraftArtistResponse(artist), store);

        var a = store.GetArtist("spotify:artist:" + Base62.Encode(Bytes(0x30)));
        Assert.NotNull(a);
        // Own discography = 2 albums + 3 singles + 1 compilation, in that group order.
        Assert.Equal(6, a!.TopAlbums!.Count);
        Assert.Equal(AlbumKind.Album, a.TopAlbums[0].Kind);
        Assert.Equal(AlbumKind.Album, a.TopAlbums[1].Kind);
        Assert.Equal(AlbumKind.Single, a.TopAlbums[2].Kind);
        Assert.Equal(AlbumKind.Single, a.TopAlbums[4].Kind);
        Assert.Equal(AlbumKind.Compilation, a.TopAlbums[5].Kind);
        Assert.Equal(0, a.TopAlbums[0].Name.Length);   // gid-only stub (assembly upgrades it later)
        // Facet totals ARE the group counts now.
        Assert.Equal(2, a.AlbumsTotal);
        Assert.Equal(3, a.SinglesTotal);
        Assert.Equal(1, a.CompilationsTotal);
        // Appears-on stubs + biography.
        Assert.Single(a.AppearsOn!);
        Assert.Equal("A short bio.", a.Bio);
        // Top-track gids are NOT written to Artist.TopTracks (that would trip the stats gate).
        Assert.True(a.TopTracks is null or { Count: 0 });
    }

    [Fact]
    public void ProjectResponse_AlbumV4_TypeEp_MapsToEpKind()
    {
        var store = new InMemoryStore();
        var album = new Pb.Album { Gid = Gid(0x81), Name = "An EP", Type = Pb.Album.Types.Type.Ep };   // wire type 4 = EP
        var array = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.AlbumV4 };
        array.ExtensionData.Add(new Xm.EntityExtensionData { EntityUri = "spotify:album:x", ExtensionData = Any.Pack(album) });
        var resp = new Xm.BatchedExtensionResponse();
        resp.ExtendedMetadata.Add(array);

        ExtendedMetadataSource.ProjectResponse(resp.ToByteArray(), store);

        var al = store.GetAlbum("spotify:album:" + Base62.Encode(Bytes(0x81)));
        Assert.NotNull(al);
        Assert.Equal(AlbumKind.EP, al!.Kind);
    }

    [Fact]
    public void ProjectResponse_ProjectsShowAndEpisode()
    {
        var store = new InMemoryStore();

        var show = new Pb.Show { Gid = Gid(0x21), Name = "My Show", Publisher = "Acme Media", Description = "A show" };
        show.CoverImage = new Pb.ImageGroup();
        show.CoverImage.Image.Add(new Pb.Image { FileId = Gid(0x99) });   // Size defaults to DEFAULT(0) → PickImage picks it
        var showArray = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.ShowV4 };
        showArray.ExtensionData.Add(new Xm.EntityExtensionData { EntityUri = "spotify:show:x", ExtensionData = Any.Pack(show) });

        var ep = new Pb.Episode { Gid = Gid(0x22), Name = "Ep 1", Duration = 5000, Description = "first",
            PublishTime = new Pb.Date { Year = 2024, Month = 3, Day = 2 } };
        ep.Show = new Pb.Show { Gid = Gid(0x21), Name = "My Show" };   // embedded show → ShowName
        var epArray = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.EpisodeV4 };
        epArray.ExtensionData.Add(new Xm.EntityExtensionData { EntityUri = "spotify:episode:x", ExtensionData = Any.Pack(ep) });

        var resp = new Xm.BatchedExtensionResponse();
        resp.ExtendedMetadata.Add(showArray);
        resp.ExtendedMetadata.Add(epArray);
        ExtendedMetadataSource.ProjectResponse(resp.ToByteArray(), store);

        var sh = store.GetShow("spotify:show:" + Base62.Encode(Bytes(0x21)));
        Assert.NotNull(sh);
        Assert.Equal("My Show", sh!.Name);
        Assert.Equal("Acme Media", sh.Publisher);
        Assert.NotNull(sh.Cover);                                  // PickImage projected the cover_image

        var epi = store.GetEpisode("spotify:episode:" + Base62.Encode(Bytes(0x22)));
        Assert.NotNull(epi);
        Assert.Equal("Ep 1", epi!.Title);
        Assert.Equal(5000, epi.DurationMs);
        Assert.Equal("My Show", epi.ShowName);                     // from the embedded show ref
        Assert.Equal(2024, epi.PublishedAt.Year);
        Assert.Equal(3, epi.PublishedAt.Month);
    }
}

// The stub-safe discography merge (StoreEntityMerge.MergeAlbumCards, exercised through InMemoryStore.UpsertArtist): a
// name-less ArtistV4 stub must never downgrade a hydrated card, but incoming order + Kind win.
public class ArtistCardMergeTests
{
    const string ArtistUri = "spotify:artist:ar";
    static Album Card(string id, string name, AlbumKind kind = AlbumKind.Album, int year = 2020)
        => new(id, "spotify:album:" + id, name, null, Array.Empty<ArtistRef>(), year, 0, null, kind);
    static Artist ArtistWith(params Album[] cards) => new("ar", ArtistUri, "Ar", null, cards);

    [Fact]
    public void StubOverHydrated_KeepsName_TakesIncomingKind()
    {
        var store = new InMemoryStore();
        store.UpsertArtist(ArtistWith(Card("a1", "Hydrated Name", AlbumKind.Album)));   // a hydrated card
        store.UpsertArtist(ArtistWith(Card("a1", "", AlbumKind.Single)));               // a later name-less stub, different Kind

        var result = store.GetArtist(ArtistUri)!.TopAlbums!;
        var only = Assert.Single(result);
        Assert.Equal("Hydrated Name", only.Name);        // the hydrated card was kept…
        Assert.Equal(AlbumKind.Single, only.Kind);       // …but adopts the incoming (authoritative) Kind
    }

    [Fact]
    public void HydratedOverStub_Upgrades()
    {
        var store = new InMemoryStore();
        store.UpsertArtist(ArtistWith(Card("a1", "", AlbumKind.Album)));                // a stub first
        store.UpsertArtist(ArtistWith(Card("a1", "Hydrated Name", AlbumKind.Album)));   // a hydrated write upgrades it

        var only = Assert.Single(store.GetArtist(ArtistUri)!.TopAlbums!);
        Assert.Equal("Hydrated Name", only.Name);
    }

    [Fact]
    public void NullIncoming_KeepsCurrent()
    {
        var store = new InMemoryStore();
        store.UpsertArtist(ArtistWith(Card("a1", "Hydrated Name")));
        // A GraphQL-stats write neutralizes discography fields (TopAlbums: null) → the V4 cards must survive.
        store.UpsertArtist(new Artist("ar", ArtistUri, "Ar", null, TopAlbums: null, MonthlyListeners: 999));

        var result = store.GetArtist(ArtistUri)!;
        Assert.Equal("Hydrated Name", Assert.Single(result.TopAlbums!).Name);
        Assert.Equal(999, result.MonthlyListeners);
    }
}

// ArtistDiscography.Assemble: upgrade stub cards to resident AlbumV4 cards, DATE_DESC, with tracklists stripped off the
// embedded cards (an Artist row must not persist hundreds of tracklists).
public class ArtistDiscographyAssembleTests
{
    const string ArtistUri = "spotify:artist:ar";
    static Track Trk(string id) => new(id, "spotify:track:" + id, "T" + id, Array.Empty<ArtistRef>(),
        new AlbumRef("", "", ""), 1000, false, null);

    [Fact]
    public void Assemble_UpgradesStubs_SortsDateDesc_StripsTracklists()
    {
        var store = new InMemoryStore();
        // Stub discography (gid-only, Kind carried).
        store.UpsertArtist(new Artist("ar", ArtistUri, "Ar", null, new[]
        {
            new Album("a1", "spotify:album:a1", "", null, Array.Empty<ArtistRef>(), 0, 0, null, AlbumKind.Album),
            new Album("a2", "spotify:album:a2", "", null, Array.Empty<ArtistRef>(), 0, 0, null, AlbumKind.Single),
        }));
        // Resident hydrated AlbumV4 cards with tracklists + years.
        store.UpsertAlbum(new Album("a1", "spotify:album:a1", "Old Album", null, Array.Empty<ArtistRef>(), 2018, 1,
            new[] { Trk("t1") }, AlbumKind.Album, Hydration: AlbumHydrationLevel.Tracks));
        store.UpsertAlbum(new Album("a2", "spotify:album:a2", "New Single", null, Array.Empty<ArtistRef>(), 2022, 1,
            new[] { Trk("t2") }, AlbumKind.Single, Hydration: AlbumHydrationLevel.Tracks));

        ArtistDiscography.Assemble(store, ArtistUri);

        var cards = store.GetArtist(ArtistUri)!.TopAlbums!;
        Assert.Equal(2, cards.Count);
        Assert.Equal("spotify:album:a2", cards[0].Uri);   // 2022 first (DATE_DESC)
        Assert.Equal("spotify:album:a1", cards[1].Uri);   // 2018 second
        Assert.Equal("New Single", cards[0].Name);        // upgraded from the stub
        Assert.Equal(AlbumKind.Single, cards[0].Kind);    // stub Kind preserved
        Assert.Null(cards[0].Tracks);                     // tracklist stripped off the embedded card
        Assert.Null(cards[1].Tracks);
    }
}
