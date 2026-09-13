using System;
using System.Collections.Generic;
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
using Pb = Wavee.Protocol.PreRelease;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Tests;

// ── Extension kind 138 (PRERELEASE): the ONLY mapping between the two uri schemes ────────────────────────────────────
// Their ids differ (spotify:prerelease:0iqKCC… IS spotify:album:0qi1ztU…), so neither uri can be computed from the
// other. The wire serves the SAME payload under either entity_uri, and SpotifyPreReleaseService exploits that with a
// three-key cache: one round trip resolves both directions. These drive the real service over crafted protobuf.
public class PreReleaseWireTests
{
    const string PreUri = "spotify:prerelease:0iqKCCqFwlqzSnJgV22Nmh";
    const string AlbumUri = "spotify:album:0qi1ztU4S08zA1FsP1DUaY";
    const long ReleaseSeconds = 1788472800;   // the captured vaultboy instant

    static SessionContext Ctx => new("me", "US", "premium", "en", Tier.Premium, false);
    static CancellationToken CT => TestContext.Current.CancellationToken;

    // ── the payload ───────────────────────────────────────────────────────────────────────────────────────────────────

    static Pb.Prerelease Message(string prereleaseUri = PreUri, string albumUri = AlbumUri,
                                 long seconds = ReleaseSeconds, bool images = true)
    {
        var msg = new Pb.Prerelease { PrereleaseUri = prereleaseUri };
        if (seconds > 0) msg.ReleaseAt = new Pb.Prerelease.Types.Timestamp { Seconds = seconds };
        msg.Release = new Pb.Prerelease.Types.Release
        {
            AlbumUri = albumUri,
            Type = "ALBUM",
            Name = "ARE YOU EVER COMING BACK?",
            Artist = new Pb.Prerelease.Types.ArtistRef { Uri = "spotify:artist:6BSCPZlmxUEbwFHOhcXHYc", Name = "vaultboy" },
        };
        if (images)
        {
            // Sizes are STRINGS in this kind (unlike the integer enums of kinds 179/98).
            msg.Release.Images.Add(new Pb.Prerelease.Types.Image { Url = "https://i.scdn.co/image/small", Size = "SMALL", Width = 128, Height = 128 });
            msg.Release.Images.Add(new Pb.Prerelease.Types.Image { Url = "https://i.scdn.co/image/default", Size = "DEFAULT", Width = 600, Height = 600 });
            msg.Release.Images.Add(new Pb.Prerelease.Types.Image { Url = "https://i.scdn.co/image/large", Size = "LARGE", Width = 1280, Height = 1280 });
        }
        return msg;
    }

    // ── the harness ───────────────────────────────────────────────────────────────────────────────────────────────────
    // `answers` maps entity_uri → payload (null = the 404 that almost every entity gets: 3 of the 5 captured entities
    // had no kind 138 at all). The responder echoes the REQUESTED uri back, exactly as the wire does.

    sealed class Wire
    {
        public readonly List<string> Requested = new();
        public readonly HashSet<Xm.ExtensionKind> Kinds = new();
        public int Posts;
    }

    static (SpotifyPreReleaseService Svc, Wire Log) Build(Func<string, Pb.Prerelease?> answers)
    {
        var log = new Wire();
        var http = new FakeExchange((req, _) =>
        {
            log.Posts++;
            var parsed = Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(req.Body!));
            var resp = new Xm.BatchedExtensionResponse();
            var array = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.Prerelease };
            foreach (var er in parsed.EntityRequest)
            {
                log.Requested.Add(er.EntityUri);
                foreach (var q in er.Query) log.Kinds.Add(q.ExtensionKind);
                if (answers(er.EntityUri) is { } payload)
                    array.ExtensionData.Add(new Xm.EntityExtensionData
                    {
                        EntityUri = er.EntityUri,
                        ExtensionData = new Any { Value = payload.ToByteString() },
                    });
            }
            if (array.ExtensionData.Count > 0) resp.ExtendedMetadata.Add(array);
            return new HttpResp(200, new Dictionary<string, string>(), resp.ToByteArray());
        });
        return (new SpotifyPreReleaseService(Reader(http)), log);
    }

    /// <summary>The service is THIN over this reader (design §2.5): the answers-including-negatives table, the
    /// coalescing slot and the etag cache all live below it now, so these tests drive the REAL read path — only the
    /// three-key seed and the half-link rejection are still this file's own code.</summary>
    static ExtensionReader Reader(IHttpExchange http)
        => new(new ExtensionEtagCache(new ExtendedMetadataSource(http, () => "https://spclient.test", () => Ctx), () => Ctx),
               new NegativeMemo());

    // Answers under BOTH keys, which is what the real wire does.
    static Func<string, Pb.Prerelease?> Both(Pb.Prerelease msg) =>
        uri => uri == PreUri || uri == AlbumUri ? msg : null;

    // ── the pair ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolvingByAlbumUri_ReturnsBothUris_AndTheReleaseInstant()
    {
        var (svc, log) = Build(Both(Message()));

        var link = await svc.ResolveAsync(AlbumUri, CT);

        Assert.NotNull(link);
        Assert.Equal(PreUri, link!.PreReleaseUri);          // what a pre-save WRITE addresses
        Assert.Equal(AlbumUri, link.AlbumUri);              // what the app NAVIGATES to
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(ReleaseSeconds), link.ReleaseAt);
        Assert.Equal("ARE YOU EVER COMING BACK?", link.Name);
        Assert.Equal("ALBUM", link.Type);
        Assert.Equal("vaultboy", link.Artist?.Name);
        Assert.Equal("6BSCPZlmxUEbwFHOhcXHYc", link.Artist?.Id);
        Assert.Contains(Xm.ExtensionKind.Prerelease, log.Kinds);
    }

    [Fact]
    public async Task ResolvingByPreReleaseUri_AnswersTheSamePair()
    {
        var (svc, _) = Build(Both(Message()));

        var link = await svc.ResolveAsync(PreUri, CT);

        Assert.NotNull(link);
        Assert.Equal(PreUri, link!.PreReleaseUri);
        Assert.Equal(AlbumUri, link.AlbumUri);
    }

    [Fact]
    public async Task OneRoundTrip_ServesBOTHDirections()
    {
        // The three-key cache is the whole point: the artist masthead resolves an ALBUM uri and the pre-save heart
        // later asks with the PRERELEASE uri (or the reverse, from a bio link) — neither pays a second request.
        var (svc, log) = Build(Both(Message()));

        var first = await svc.ResolveAsync(AlbumUri, CT);
        Assert.Equal(1, log.Posts);

        var second = await svc.ResolveAsync(PreUri, CT);
        var third = await svc.ResolveAsync(AlbumUri, CT);

        Assert.Equal(1, log.Posts);                          // no second fetch, in either direction
        Assert.Same(first, second);
        Assert.Same(first, third);
    }

    [Fact]
    public async Task ThePayloadsOwnUris_AreCachedEvenWhenTheQueryUriDiffers()
    {
        // The query uri is cached too, and it is NOT assumed to be one of the pair — a caller can hold a third
        // spelling (an alias) and still get the link, then the pair resolves free.
        const string alias = "spotify:album:aliasedEdition";
        var (svc, log) = Build(uri => uri == alias ? Message() : null);

        Assert.NotNull(await svc.ResolveAsync(alias, CT));
        Assert.Equal(1, log.Posts);

        Assert.NotNull(await svc.ResolveAsync(PreUri, CT));
        Assert.NotNull(await svc.ResolveAsync(AlbumUri, CT));
        Assert.Equal(1, log.Posts);
    }

    // ── half-links ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APayloadWithNoAlbumUri_IsNotALink()
    {
        // Nothing could be navigated to, and the album uri cannot be derived — so this is discarded rather than carried
        // as a record with an empty field.
        var (svc, _) = Build(Both(Message(albumUri: "")));

        Assert.Null(await svc.ResolveAsync(PreUri, CT));
    }

    [Fact]
    public async Task APayloadWithNoPreReleaseUri_IsNotALink()
    {
        // Nothing could be pre-saved.
        var (svc, _) = Build(Both(Message(prereleaseUri: "")));

        Assert.Null(await svc.ResolveAsync(AlbumUri, CT));
    }

    [Fact]
    public async Task AHalfLinkIsCachedAsAMiss_NotRetriedOnEveryRender()
    {
        var (svc, log) = Build(Both(Message(albumUri: "")));

        Assert.Null(await svc.ResolveAsync(AlbumUri, CT));
        Assert.Null(await svc.ResolveAsync(AlbumUri, CT));

        Assert.Equal(1, log.Posts);
    }

    // ── the 404 that is the ORDINARY answer ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoExtension_ResolvesToNull_AndIsCachedNegative()
    {
        // "No upcoming release" is the correct answer for every album that is already out, so a miss must be a cached
        // null — otherwise every ordinary album open re-asks the wire for a kind it will never have.
        var (svc, log) = Build(_ => null);

        Assert.Null(await svc.ResolveAsync(AlbumUri, CT));
        Assert.Equal(1, log.Posts);

        Assert.Null(await svc.ResolveAsync(AlbumUri, CT));
        Assert.Null(await svc.ResolveAsync(AlbumUri, CT));

        Assert.Equal(1, log.Posts);                          // the negative held
    }

    [Fact]
    public async Task ANegativeForOneEntity_DoesNotSuppressAnother()
    {
        var (svc, log) = Build(uri => uri == AlbumUri ? Message() : null);

        Assert.Null(await svc.ResolveAsync("spotify:album:somethingElse", CT));
        Assert.NotNull(await svc.ResolveAsync(AlbumUri, CT));

        Assert.Equal(2, log.Posts);
    }

    [Fact]
    public async Task AnEmptyUri_ShortCircuits_WithoutTouchingTheWire()
    {
        var (svc, log) = Build(Both(Message()));

        Assert.Null(await svc.ResolveAsync("", CT));

        Assert.Equal(0, log.Posts);
    }

    [Fact]
    public async Task ATransportFailure_DegradesToNull_RatherThanThrowing()
    {
        // Best-effort by contract: a network blip must never break an album open or an artist page.
        var http = new FakeExchange((_, _) => throw new InvalidOperationException("socket"));
        var svc = new SpotifyPreReleaseService(Reader(http));

        Assert.Null(await svc.ResolveAsync(AlbumUri, CT));
    }

    [Fact]
    public async Task GarbagePayload_DegradesToNull()
    {
        var http = new FakeExchange((_, _) =>
        {
            var resp = new Xm.BatchedExtensionResponse();
            var array = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.Prerelease };
            array.ExtensionData.Add(new Xm.EntityExtensionData
            {
                EntityUri = AlbumUri,
                // Field 1 declared as a length-delimited string with a length that runs off the end of the buffer.
                ExtensionData = new Any { Value = ByteString.CopyFrom(new byte[] { 0x0A, 0x7F, 0x01 }) },
            });
            resp.ExtendedMetadata.Add(array);
            return new HttpResp(200, new Dictionary<string, string>(), resp.ToByteArray());
        });
        var svc = new SpotifyPreReleaseService(Reader(http));

        Assert.Null(await svc.ResolveAsync(AlbumUri, CT));
    }

    // ── the release instant + the covers ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ZeroSeconds_MeansUNDATED_NotNineteenSeventy()
    {
        // An announced-but-undated release is a real shape; a null ReleaseAt is what IsUpcoming reads as "upcoming,
        // date unknown". Mapping absent → the epoch would instead make it permanently lapsed.
        var (svc, _) = Build(Both(Message(seconds: 0)));

        var link = await svc.ResolveAsync(AlbumUri, CT);

        Assert.NotNull(link);
        Assert.Null(link!.ReleaseAt);
        Assert.True(link.IsUpcoming);
    }

    [Fact]
    public async Task ACachedLinkWhoseDateHasPassed_IsNoLongerUpcoming()
    {
        // The payload carries a 30-day offline TTL, so a cached link routinely outlives its own release.
        var (svc, _) = Build(Both(Message(seconds: DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeSeconds())));

        var link = await svc.ResolveAsync(AlbumUri, CT);

        Assert.NotNull(link);
        Assert.False(link!.IsUpcoming);
    }

    [Fact]
    public async Task TheCover_PrefersDEFAULT_AndCarriesLARGEAlongside()
    {
        var (svc, _) = Build(Both(Message()));

        var link = await svc.ResolveAsync(AlbumUri, CT);

        Assert.NotNull(link!.Cover);
        Assert.Equal("https://i.scdn.co/image/default", link.Cover!.Url);   // the rendition Spotify's own surfaces use
        Assert.Equal(600, link.Cover.Width!.Value);
        Assert.Equal("https://i.scdn.co/image/large", link.Cover.LargestUrl);
    }

    [Fact]
    public async Task NoImages_IsNotAnError()
    {
        var (svc, _) = Build(Both(Message(images: false)));

        var link = await svc.ResolveAsync(AlbumUri, CT);

        Assert.NotNull(link);
        Assert.Null(link!.Cover);
    }

    [Fact]
    public async Task ConcurrentResolvesOfTheSameUri_Coalesce()
    {
        // The artist masthead and the album page routinely ask for the same release at the same moment.
        var (svc, log) = Build(Both(Message()));

        var all = await Task.WhenAll(svc.ResolveAsync(AlbumUri, CT), svc.ResolveAsync(AlbumUri, CT),
                                     svc.ResolveAsync(AlbumUri, CT));

        Assert.Equal(1, log.Posts);
        Assert.All(all, l => Assert.NotNull(l));
    }
}

// The seam the composition root hands out before login, and the offline degradation.
public class PreReleaseSeamTests
{
    [Fact]
    public async Task NullService_AlwaysAnswersNull()
        => Assert.Null(await NullPreReleaseService.Instance.ResolveAsync("spotify:album:x"));

    [Fact]
    public async Task Switchable_StartsNull_ThenServesTheLiveProvider_AndBack()
    {
        var sw = new SwitchablePreReleaseService(NullPreReleaseService.Instance);
        Assert.Null(await sw.ResolveAsync("spotify:album:x"));

        var link = new PreReleaseLink("spotify:prerelease:p", "spotify:album:x", null);
        sw.SetInner(new Canned(link));
        Assert.Same(link, await sw.ResolveAsync("spotify:album:x"));

        sw.SetInner(NullPreReleaseService.Instance);              // GoOffline
        Assert.Null(await sw.ResolveAsync("spotify:album:x"));
    }

    [Fact]
    public void Switchable_RefusesANullInner()
        => Assert.Throws<ArgumentNullException>(
            () => new SwitchablePreReleaseService(NullPreReleaseService.Instance).SetInner(null!));

    sealed class Canned(PreReleaseLink link) : IPreReleaseService
    {
        public Task<PreReleaseLink?> ResolveAsync(string uri, CancellationToken ct = default)
            => Task.FromResult<PreReleaseLink?>(link);
    }
}
