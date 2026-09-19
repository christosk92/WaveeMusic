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
using Va = Wavee.Protocol.ExtendedMetadata;
using Md = Wavee.Protocol.Metadata;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Tests;

// ── The expand drawer's wire shape (kinds 99 / 98 / 5 / 237, then TrackV4 for the targets) ───────────────────────────
// Two things are pinned here and nowhere else, because both are request COUNT and request SHAPE rather than a
// projection:
//   • the four drawer planes ride ONE POST under ONE EntityRequest. The ~38 KB waveform is the reason: it is in this
//     bundle precisely because grouping makes it cost zero extra round trips, and it is out of the row bundle because
//     300 realized rows would pull ~11 MB;
//   • the association targets are resolved with TrackV4 and NOTHING ELSE. The old path asked for 222
//     (AUDIO_ATTRIBUTES_V2) alongside it and then discarded the payload — the tempo/key the drawer prints is read off
//     the STORE, written by the row bundle. One wasted kind per target on every expand is exactly the waste the
//     hydration façade exists to delete, so a regression here has to fail a test rather than a code review.
public class TrackExpansionWireTests
{
    const string TrackUri = "spotify:track:0spnMEFDuWTQRTsI941Q5n";
    const string TargetUri = "spotify:track:1aBcDeFgHiJkLmNoPqRsTu";

    static SessionContext Ctx => new("me", "US", "premium", "en", Tier.Premium, false);
    static CancellationToken CT => TestContext.Current.CancellationToken;

    sealed record Post(IReadOnlyList<string> Uris, IReadOnlyList<Xm.ExtensionKind> Kinds);

    sealed class Wire
    {
        public readonly List<Post> Posts = new();
    }

    /// <summary>One video association pointing at <see cref="TargetUri"/> — the shape kinds 98/99 actually ship: a
    /// counterpart uri and artwork, no name, which is the whole reason a second read exists.</summary>
    static ByteString Associations()
    {
        var msg = new Va.VideoAssociations
        {
            Association = new Va.Association { AssociatedUri = TargetUri },
        };
        return msg.ToByteString();
    }

    static ByteString TargetTrack()
        => new Md.Track { Name = "Live at Wembley", Duration = 214_000 }.ToByteString();

    static (SpotifyTrackExpansionService Svc, InMemoryStore Store, Wire Log) Build()
    {
        var log = new Wire();
        var http = new FakeExchange((req, _) =>
        {
            var parsed = Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(req.Body!));
            var uris = new List<string>();
            var kinds = new List<Xm.ExtensionKind>();
            var response = new Xm.BatchedExtensionResponse();
            var byKind = new Dictionary<Xm.ExtensionKind, Xm.EntityExtensionDataArray>();

            foreach (var entity in parsed.EntityRequest)
            {
                uris.Add(entity.EntityUri);
                foreach (var query in entity.Query)
                {
                    kinds.Add(query.ExtensionKind);
                    if (!byKind.TryGetValue(query.ExtensionKind, out var array))
                    {
                        array = new Xm.EntityExtensionDataArray { ExtensionKind = query.ExtensionKind };
                        byKind[query.ExtensionKind] = array;
                        response.ExtendedMetadata.Add(array);
                    }
                    // Only two kinds have anything to say on this fixture; everything else is the 404 that is the
                    // ordinary answer for a track with no alternate audio, no format ladder and no waveform.
                    ByteString? payload = (entity.EntityUri, query.ExtensionKind) switch
                    {
                        (TrackUri, Xm.ExtensionKind.VideoAssociations) => Associations(),
                        (TargetUri, Xm.ExtensionKind.TrackV4) => TargetTrack(),
                        _ => null,
                    };
                    var data = new Xm.EntityExtensionData
                    {
                        EntityUri = entity.EntityUri,
                        Header = new Xm.EntityExtensionDataHeader
                        {
                            StatusCode = payload is null ? 404 : 200,
                            OfflineTtlInSeconds = 3600,
                        },
                    };
                    if (payload is not null) data.ExtensionData = new Any { Value = payload };
                    array.ExtensionData.Add(data);
                }
            }
            lock (log.Posts) log.Posts.Add(new Post(uris, kinds));
            return new HttpResp(200, new Dictionary<string, string>(), response.ToByteArray());
        });

        var em = new ExtendedMetadataSource(http, () => "https://spclient.test", () => Ctx);
        var reader = new ExtensionReader(new ExtensionEtagCache(em, () => Ctx), new NegativeMemo());
        var store = new InMemoryStore();
        return (new SpotifyTrackExpansionService(reader, store), store, log);
    }

    [Fact]
    public async Task OnePost_CarriesAllFourDrawerPlanes_AndTheTargetsReadTrackV4Only()
    {
        var (svc, _, log) = Build();

        var expansion = await svc.GetAsync(TrackUri, CT);

        Assert.Equal(2, log.Posts.Count);

        // ── the drawer read ──────────────────────────────────────────────────────────────────────────────────────
        // ONE entity, FOUR kinds, ONE round trip. Splitting the waveform out would be four requests for one expand.
        var drawer = log.Posts[0];
        Assert.Equal(new[] { TrackUri }, drawer.Uris);
        Assert.Equal(
            new[]
            {
                Xm.ExtensionKind.VideoAssociations,      // 99
                Xm.ExtensionKind.AudioAssociations,      // 98
                Xm.ExtensionKind.AudioFiles,             //  5
                Xm.ExtensionKind.ThreebandWaveforms,     // 237
            },
            drawer.Kinds);
        Assert.Equal(99, (int)Xm.ExtensionKind.VideoAssociations);
        Assert.Equal(237, (int)Xm.ExtensionKind.ThreebandWaveforms);

        // ── the target resolve ───────────────────────────────────────────────────────────────────────────────────
        // TrackV4 and nothing else. 222's payload was discarded by the old path — the tempo comes off the store.
        var targets = log.Posts[1];
        Assert.Equal(new[] { TargetUri }, targets.Uris);
        Assert.Equal(new[] { Xm.ExtensionKind.TrackV4 }, targets.Kinds);
        Assert.DoesNotContain(Xm.ExtensionKind.AudioAttributesV2, targets.Kinds);

        // …and the drawer still says what it always said: the resolved TRACK NAME, not a raw id.
        var version = Assert.Single(expansion.Versions);
        Assert.Equal(TargetUri, version.Uri);
        Assert.Equal("Live at Wembley", version.Title);
        Assert.Equal(214_000, version.DurationMs);
        Assert.Equal(TrackVersionKind.Video, version.Kind);
    }

    [Fact]
    public async Task ReOpeningTheDrawer_CostsNothing()
    {
        // The assembled drawer is memoized: re-expanding a row must not re-walk three ~12 KB waveform bands, let alone
        // re-ask the wire.
        var (svc, _, log) = Build();

        var first = await svc.GetAsync(TrackUri, CT);
        var second = await svc.GetAsync(TrackUri, CT);

        Assert.Equal(2, log.Posts.Count);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task NonTrackUris_AreAnsweredWithoutARequest()
    {
        var (svc, _, log) = Build();

        Assert.Same(TrackExpansion.Empty, await svc.GetAsync("spotify:album:5xLkGYD86FbxWY7DcQP0Fk", CT));
        Assert.Same(TrackExpansion.Empty, await svc.GetAsync("", CT));

        Assert.Empty(log.Posts);
    }
}
