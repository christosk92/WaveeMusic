using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;
using Wavee.SpotifyLive;
using Xunit;

namespace Wavee.Tests;

/// <summary>Covers issue #105: a daylist whose window (<c>Meta.ExpiresAtMs</c>) has already closed must never be
/// trusted from cache just because it carries a REAL title and <c>NeedsHydration</c> is false — the bug that let
/// Sunday's Home hero keep showing Saturday night's daylist for a full minute after the countdown hit zero. Mirrors
/// <see cref="HomeComposerDaylistHydrationTests"/>'s fixture pattern (fake <c>readHeader</c>/<c>fetchHeader</c>/
/// <c>probeRevision</c>/<c>requery</c> delegates), driving the clock explicitly since these tests are all about
/// what "now" is relative to <c>ExpiresAtMs</c>.</summary>
public class HomeDaylistExpiryTests
{
    const string Uri = "spotify:playlist:37i9dQZF1EP6YuccBxUcC1";
    const string StaleTitle = "confidence baddie saturday night";
    const string RolledOverTitle = "k-ballad korean ost sunday late night";

    static byte[] Rev(int counter) => [0, 0, 0, (byte)counter, 0xAB, (byte)counter];

    // A card the composer already resolved to a REAL title (NeedsHydration false) — the exact shape a TTL-cached,
    // offline-served Home body carried in the reported bug, whose window may or may not have closed yet.
    static HomeCard RealTitleCard(string title, long expiresAtMs) => new(
        Uri, title, "Generic description", null, HomeCardKind.Playlist,
        Meta: new HomeCardMeta(Format: "daylist", TrackCount: 50,
            Seeds: ["teen pop", "mid 2010s", "friday afternoon"], OwnerName: "Spotify",
            GenericTitle: "daylist", NeedsHydration: false, ExpiresAtMs: expiresAtMs));

    static HomePlaylistHeader Header(string title) => new(title, "Exact description", "Spotify", null, 50);

    static LiveHomeResult Feed(HomeCard card) => new(
        [new(HomeGroupKind.Hero, null, [card])], null, "Good afternoon",
        [new("spotify:section:daylist", "Your daylist", null, [card], 1, 1)]);

    static Func<long> Clock(long start) { long t = start; return () => t; }

    [Fact]
    public async Task ExpiredWindow_WithRealTitle_IsShallow_AndHeaderIsFetched()
    {
        int fetches = 0;
        var hydrator = new HomeDaylistHydrator(
            _ => null,
            (_, _) => { fetches++; return Task.CompletedTask; },
            (_, _) => Task.FromResult<byte[]?>(Rev(1)),
            (_, _) => Task.FromResult(LiveHomeResult.Empty),
            nowMs: Clock(10_000));

        // now (10 000) is PAST the window's end (1 000) — expired, real title, NeedsHydration false: still a
        // candidate, because trusting NeedsHydration alone is exactly what let yesterday's title survive.
        await hydrator.ResolveAsync(Feed(RealTitleCard(StaleTitle, 1_000)), TestContext.Current.CancellationToken);
        Assert.Equal(1, fetches);
    }

    [Fact]
    public async Task LiveWindow_WithRealTitle_IsNotShallow_AndHeaderIsNeverFetched()
    {
        int fetches = 0;
        var hydrator = new HomeDaylistHydrator(
            _ => null,
            (_, _) => { fetches++; return Task.CompletedTask; },
            (_, _) => Task.FromResult<byte[]?>(Rev(1)),
            (_, _) => Task.FromResult(LiveHomeResult.Empty),
            nowMs: Clock(10_000));

        // now (10 000) is BEFORE the window's end (999 999) — nothing about a real, still-current title needs
        // re-checking, exactly as it did before this fix.
        var source = Feed(RealTitleCard(StaleTitle, 999_999));
        var result = await hydrator.ResolveAsync(source, TestContext.Current.CancellationToken);
        Assert.Equal(0, fetches);
        Assert.Same(source, result);   // never shallow ⇒ ResolveAsync's early return, not even an overlay pass
    }

    [Fact]
    public async Task ExpiredRetry_Coalesces5Minutes_ThenFetchesAgain()
    {
        long now = 10_000;
        int fetches = 0;
        var hydrator = new HomeDaylistHydrator(
            _ => null,
            (uri, _) => { fetches++; return Task.CompletedTask; },   // never writes a header ⇒ readHeader stays null
            (_, _) => Task.FromResult<byte[]?>(Rev(1)),
            (_, _) => Task.FromResult(LiveHomeResult.Empty),
            nowMs: () => now);

        var card = RealTitleCard(StaleTitle, 1_000);   // permanently expired relative to `now` from here on

        await hydrator.ResolveAsync(Feed(card), TestContext.Current.CancellationToken);
        Assert.Equal(1, fetches);

        // A lagging server: the SAME closed window keeps coming back on the ordinary 60 s poll. Well inside the
        // 5-minute gate, no second fetch.
        now += 60_000;
        await hydrator.ResolveAsync(Feed(card), TestContext.Current.CancellationToken);
        Assert.Equal(1, fetches);

        // Past ExpiredRetryMs since the recorded fetch: worth asking again.
        now += HomeDaylistHydrator.ExpiredRetryMs;
        await hydrator.ResolveAsync(Feed(card), TestContext.Current.CancellationToken);
        Assert.Equal(2, fetches);
    }

    [Fact]
    public async Task ExpiredWindow_FetchedHeaderWithNewTitle_OverlaysAndClaimsTheRequery()
    {
        int refreshes = 0, fetches = 0;
        var headers = new Dictionary<string, HomePlaylistHeader>(StringComparer.Ordinal);   // the "store", written by fetchHeader
        // The requery FAILS outright — the deliberately hard case: it proves the overlay does not depend on it.
        // A card admitted via the expired-window path carries NeedsHydration=false (it already has a real title), so
        // this also pins that OverlayCards' eligibility is membership in `exact`, not that flag — the flag alone
        // would silently drop this overlay the moment the requery failed.
        var hydrator = new HomeDaylistHydrator(
            uri => headers.TryGetValue(uri, out var h) ? h : null,
            (uri, _) => { fetches++; headers[uri] = Header(RolledOverTitle); return Task.CompletedTask; },
            (_, _) => Task.FromResult<byte[]?>(Rev(1)),
            (_, _) =>
            {
                refreshes++;
                return Task.FromException<LiveHomeResult>(new InvalidOperationException("home unavailable"));
            },
            nowMs: Clock(10_000));

        var result = await hydrator.ResolveAsync(Feed(RealTitleCard(StaleTitle, 1_000)),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, fetches);                    // the resident store had nothing, so the network was asked
        Assert.Equal(1, refreshes);
        Assert.Equal(1, hydrator.IdentityVersion);   // the epoch every parked Home page compares against stepped
        Assert.Equal(RolledOverTitle, Assert.Single(Assert.Single(result.Groups).Cards).Title);
        Assert.True(hydrator.Hydrated(Uri));
    }

    [Fact]
    public async Task Hydrated_IsTrueForASeenLiveDaylist_ThatWasNeverClaimed()
    {
        var hydrator = new HomeDaylistHydrator(
            _ => null,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.FromResult<byte[]?>(Rev(1)),
            (_, _) => Task.FromResult(LiveHomeResult.Empty),
            nowMs: Clock(10_000));

        Assert.False(hydrator.Hydrated(Uri));

        // A live window, a real (non-generic) title, NeedsHydration false — never shallow, so ResolveAsync claims
        // nothing for it. Rule 2 says Hydrated must still answer true: the feed carries this URI's identity, so a
        // detail-page rewrite of its header must be able to wake Home.
        await hydrator.ResolveAsync(Feed(RealTitleCard(StaleTitle, 999_999)), TestContext.Current.CancellationToken);

        Assert.True(hydrator.Hydrated(Uri));
    }

    [Fact]
    public async Task Revalidate_ReturnsTrueForAnExpiredSeenWindow_AndFalseAgainAfterTheRolloverLands()
    {
        long now = 10_000;
        HomePlaylistHeader? resident = null;   // null until the rollover "lands" in the store
        var hydrator = new HomeDaylistHydrator(
            _ => resident,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.FromResult<byte[]?>(Rev(1)),   // constant revision throughout — isolates the window signal
            (_, _) => Task.FromResult(Feed(RealTitleCard(RolledOverTitle, 999_999))),
            nowMs: () => now);

        var card = RealTitleCard(StaleTitle, 1_000);   // permanently expired relative to `now` from here on

        // First read: the window has closed but the store has nothing new yet (the lagging-server case) — no title
        // change, so nothing is claimed and the pending mark survives.
        await hydrator.ResolveAsync(Feed(card), TestContext.Current.CancellationToken);
        Assert.True(await hydrator.RevalidateAsync(TestContext.Current.CancellationToken));

        // The rollover lands in the store (a same-session detail-page rewrite, say) — still within ExpiredRetryMs of
        // the first fetch, so this is also pinning that the resident recheck runs independent of that gate.
        resident = Header(RolledOverTitle);
        await hydrator.ResolveAsync(Feed(card), TestContext.Current.CancellationToken);
        Assert.False(await hydrator.RevalidateAsync(TestContext.Current.CancellationToken));
    }
}
