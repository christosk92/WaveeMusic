using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wavee.Backend;
using Wavee.Backend.Catalog;
using Wavee.Backend.Metadata;
using Wavee.Backend.Persistence;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.Core.Catalog;
using Wavee.SpotifyLive;
using Wavee.SpotifyLive.Catalog;
using Xunit;
using Xm = Wavee.Protocol.ExtendedMetadata;
using Pb = Wavee.Protocol.Metadata;

namespace Wavee.Tests.ApiWaste;

public sealed class CatalogDemandWasteTests
{
    static ByteString Gid(byte fill) { var a = new byte[16]; Array.Fill(a, fill); return ByteString.CopyFrom(a); }
    static string Id(byte fill) => Base62.Encode(Gid(fill).Span);
    static string AlbumUri(byte fill) => "spotify:album:" + Id(fill);
    static string TrackUri(byte fill) => "spotify:track:" + Id(fill);
    static string ArtistUri(byte fill) => "spotify:artist:" + Id(fill);

    /// <summary>An ArtistV4 payload with an N-album "Albums" discography (one representative release per group,
    /// the real shape) — big enough to span several 50-row RelationProjection pages.</summary>
    static byte[] ArtistResponse(byte gid, int albumCount)
    {
        var artist = new Pb.Artist { Gid = Gid(gid), Name = "Artist " + Id(gid) };
        for (int i = 0; i < albumCount; i++)
        {
            var group = new Pb.AlbumGroup();
            group.Album.Add(new Pb.Album { Gid = Gid((byte)(1 + i % 200)), Name = "Album " + i, Date = new Pb.Date { Year = 2024 } });
            artist.AlbumGroup.Add(group);
        }
        return Wrap(Xm.ExtensionKind.ArtistV4, "spotify:artist:" + Id(gid), artist);
    }

    /// <summary>An AlbumV4 payload with <paramref name="rows"/> disc tracks. <paramref name="named"/> false makes them
    /// gid-only — AlbumV4 can establish ordered membership before its track identities are available.</summary>
    static byte[] AlbumResponse(byte gid, int rows, bool named)
    {
        var album = new Pb.Album { Gid = Gid(gid), Name = "Album " + Id(gid), Date = new Pb.Date { Year = 2024 } };
        album.Artist.Add(new Pb.Artist { Gid = Gid(0xAA), Name = "Artist One" });
        var disc = new Pb.Disc { Number = 1 };
        for (int i = 0; i < rows; i++)
        {
            var t = new Pb.Track { Gid = Gid((byte)(0x10 + i)), Duration = 210_000 };
            if (named) t.Name = "Song " + i;
            disc.Track.Add(t);
        }
        album.Disc.Add(disc);
        return Wrap(Xm.ExtensionKind.AlbumV4, "spotify:album:" + Id(gid), album);
    }

    static byte[] TrackResponse(byte gid, string name, bool full)
    {
        var track = new Pb.Track { Gid = Gid(gid), Name = name, Duration = 210_000 };
        if (full)
        {
            track.Artist.Add(new Pb.Artist { Gid = Gid(0xAA), Name = "Artist One" });
            track.Album = new Pb.Album { Gid = Gid(0xBB), Name = "Album One" };
        }
        return Wrap(Xm.ExtensionKind.TrackV4, TrackUri(gid), track);
    }

    static byte[] Wrap(Xm.ExtensionKind kind, string uri, IMessage payload)
    {
        var array = new Xm.EntityExtensionDataArray { ExtensionKind = kind };
        array.ExtensionData.Add(new Xm.EntityExtensionData
        {
            EntityUri = uri,
            Header = new Xm.EntityExtensionDataHeader { StatusCode = 200, OfflineTtlInSeconds = 3600 },
            ExtensionData = Any.Pack(payload),
        });
        var resp = new Xm.BatchedExtensionResponse();
        resp.ExtendedMetadata.Add(array);
        return resp.ToByteArray();
    }

    /// <summary>Merge several per-kind responses into ONE body — a mixed batch answers in a single response.</summary>
    static byte[] Merge(params byte[][] parts)
    {
        var resp = new Xm.BatchedExtensionResponse();
        foreach (var p in parts)
            resp.ExtendedMetadata.AddRange(Xm.BatchedExtensionResponse.Parser.ParseFrom(p).ExtendedMetadata);
        return resp.ToByteArray();
    }


    sealed class Rig : IAsyncDisposable
    {
        public List<(string Uri, Xm.ExtensionKind Kind)> Asked { get; } = [];
        public CatalogQueryTestHost Host { get; }
        public Rig(bool named = true)
        {
            var http = new FakeExchange((req, _) =>
            {
                if (!req.Url.Contains("extended-metadata", StringComparison.Ordinal))
                    return new HttpResp(200, new Dictionary<string,string>(), Encoding.UTF8.GetBytes("{\"data\":{}}"));
                var request = Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(req.Body!));
                var response = new Xm.BatchedExtensionResponse();
                foreach (var entity in request.EntityRequest)
                    foreach (var query in entity.Query)
                    {
                        Asked.Add((entity.EntityUri, query.ExtensionKind));
                        byte[]? bytes = query.ExtensionKind switch
                        {
                            Xm.ExtensionKind.AlbumV4 => AlbumResponse(0xBB, 12, named),
                            Xm.ExtensionKind.TrackV4 => TrackResponse((byte)(0x10 + Enumerable.Range(0,12).FirstOrDefault(i => TrackUri((byte)(0x10+i)) == entity.EntityUri)), "Resolved song", true),
                            Xm.ExtensionKind.ArtistV4 => ArtistResponse(0xCC, 130),
                            _ => null,
                        };
                        if (bytes is not null) response.ExtendedMetadata.AddRange(Xm.BatchedExtensionResponse.Parser.ParseFrom(bytes).ExtendedMetadata);
                        else
                        {
                            var group = new Xm.EntityExtensionDataArray { ExtensionKind = query.ExtensionKind };
                            group.ExtensionData.Add(new Xm.EntityExtensionData { EntityUri = entity.EntityUri,
                                Header = new Xm.EntityExtensionDataHeader { StatusCode = 404 } });
                            response.ExtendedMetadata.Add(group);
                        }
                    }
                return new HttpResp(200, new Dictionary<string,string>(), response.ToByteArray());
            });
            var provider = new SpotifyCatalogResourceProvider(new ExtendedMetadataSource(http, () => "https://spclient.test",
                () => new("test", "NL", "premium", "en", Tier.Premium, false)), new MemoryDataPersistence(), new PathfinderClient(http), http,
                () => "https://spclient.test", () => new("", "", "", "", "", "", "", "", "", ""), TimeProvider.System);
            Host = new(provider);
        }
        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    [Fact]
    public async Task RelatedFacetsShareOneWireQueryForTheirCommonExtension()
    {
        await using var rig = new Rig();
        var scope = rig.Host.Scope;
        await rig.Host.Data.Resources.EnsureAsync([
            new(scope, AlbumUri(0xBB), FacetKind.AlbumIdentity),
            new(scope, AlbumUri(0xBB), FacetKind.AlbumTracks, new(0,50))]);
        Assert.Single(rig.Asked.Where(ask => ask.Kind == Xm.ExtensionKind.AlbumV4));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReopeningAnAlbumDoesNotRepeatWarmIdentityOrRowRequests(bool named)
    {
        await using var rig = new Rig(named);
        var first = await rig.Host.Library.GetAlbumAsync(AlbumUri(0xBB));
        Assert.Equal(12, first.Tracks!.Count);
        Assert.All(first.Tracks, track => Assert.NotEmpty(track.Title));
        int asked = rig.Asked.Count;
        await rig.Host.Library.GetAlbumAsync(AlbumUri(0xBB));
        Assert.Equal(asked, rig.Asked.Count);
        Assert.All(rig.Asked.GroupBy(ask => ask), group => Assert.Single(group));
    }

    [Fact]
    public async Task RepeatedAndOverlappingRowsRequestOnlyPreviouslyUnknownFacets()
    {
        await using var rig = new Rig();
        var scope = rig.Host.Scope;
        ResourceKey Key(byte id) => new(scope, TrackUri(id), FacetKind.TrackIdentity);
        await rig.Host.Data.Resources.EnsureAsync([Key(0x10), Key(0x11)]);
        await rig.Host.Data.Resources.EnsureAsync([Key(0x11), Key(0x12)]);
        Assert.Equal(3, rig.Asked.Count(ask => ask.Kind == Xm.ExtensionKind.TrackV4));
        Assert.All(rig.Asked.GroupBy(ask => ask), group => Assert.Single(group));
    }

    [Fact]
    public async Task ArtistDiscographyThreePagesPerFilter_CostsOnlyOneMoreArtistV4PostAfterPageZero()
    {
        await using var rig = new Rig();
        var scope = rig.Host.Scope;
        string uri = ArtistUri(0xCC);
        // "Albums" only (query.Kind), matching one DiscographySection's own ArtistReleasesQuery — the artist
        // response wires a 130-album "Albums" group, i.e. 3 RelationProjection pages of 50/50/30.
        using var handle = rig.Host.Data.Queries.Acquire(new ArtistReleasesQuery(scope, uri, DiscographyKind.Albums));
        // ArtistReleasesQuery is a RETAINED collection (its own doc comment: "Membership spans pages") — it pages
        // membership fully to Total once known regardless of the visible window, so even a narrow demand (the
        // freshly opened, unscrolled section's own demand from ArtistPage.AlbumExpand.cs) ends up loading all
        // 130 albums. What must NOT happen is one round trip per page getting there (Part 5 item 3a): page 0
        // costs its own POST (Total unknown yet); RelationProjection.Require then names every remaining page
        // (50, 100) in ONE wave, and the provider decodes one ArtistV4 document for all of them — ONE more POST
        // regardless of how many pages that is.
        handle.SetDemand(QueryDemand.Initial);
        await WaitAsync(() => handle.Current.Value.Items.Count >= 130);
        Assert.Equal(2, rig.Asked.Count(ask => ask.Kind == Xm.ExtensionKind.ArtistV4));
    }

    static async Task WaitAsync(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(1, deadline.Token);
    }
}
