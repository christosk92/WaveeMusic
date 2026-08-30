using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;
using Wavee.SpotifyLive;
using Xunit;

namespace Wavee.Tests;

public class HomeComposerDaylistHydrationTests
{
    const string Uri = "spotify:playlist:37i9dQZF1EP6YuccBxUcC1";

    static byte[] Rev(int counter) => [0, 0, 0, (byte)counter, 0xAB, (byte)counter];

    [Fact]
    public async Task DuplicateOccurrences_FetchOneHeader_RefreshHomeOnce_AndKeepAccounting()
    {
        var source = Feed(Shallow(), duplicateGroup: true);
        var headers = new Dictionary<string, HomePlaylistHeader>(StringComparer.Ordinal);
        int fetches = 0, refreshes = 0, probes = 0;

        var exactCard = Exact();
        var refreshed = Feed(exactCard, duplicateGroup: true);
        var hydrator = new HomeDaylistHydrator(
            uri => headers.TryGetValue(uri, out var h) ? h : null,
            (uri, _) =>
            {
                fetches++;
                headers[uri] = Header();
                return Task.CompletedTask;
            },
            (_, _) => { probes++; return Task.FromResult<byte[]?>(Rev(1)); },
            _ =>
            {
                refreshes++;
                return Task.FromResult(refreshed);
            });

        var result = await hydrator.ResolveAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal(1, probes);                   // one HEAD read for the URI, not one per occurrence
        Assert.Equal(1, fetches);                  // the URI occurs in two groups + the section ledger
        Assert.Equal(1, refreshes);                // one invalidation/requery, never one per occurrence
        Assert.Equal(source.Groups.Sum(g => g.Cards.Count), result.Groups.Sum(g => g.Cards.Count));
        Assert.Equal(source.Sections!.Sum(s => s.Cards.Count), result.Sections!.Sum(s => s.Cards.Count));
        Assert.All(result.Groups.SelectMany(g => g.Cards), c =>
        {
            Assert.Equal("teen pop mid 2010s friday afternoon", c.Title);
            Assert.Equal(new[] { "teen pop", "mid 2010s", "friday afternoon" }, c.Meta!.Seeds);
            Assert.False(c.Meta.NeedsHydration);
        });
        Assert.Equal("teen pop mid 2010s friday afternoon", Assert.Single(result.Sections!).Cards[0].Title);
    }

    [Fact]
    public async Task ExactResidentHeader_SkipsHeaderNetwork_AndOverlaysWhenHomeRefreshFails()
    {
        // A never-resolved URI always resolves, so the FIRST read still reads the header from the network; what this
        // pins is that the resident header carries the overlay even when the Home requery fails outright.
        var source = Feed(Shallow());
        int fetches = 0, refreshes = 0;
        var hydrator = new HomeDaylistHydrator(
            _ => Header(),
            (_, _) => { fetches++; return Task.CompletedTask; },
            (_, _) => Task.FromResult<byte[]?>(Rev(1)),
            _ =>
            {
                refreshes++;
                return Task.FromException<LiveHomeResult>(new InvalidOperationException("home unavailable"));
            });

        var result = await hydrator.ResolveAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal(1, refreshes);
        var groupCard = Assert.Single(Assert.Single(result.Groups).Cards);
        var sectionCard = Assert.Single(Assert.Single(result.Sections!).Cards);
        Assert.Equal("teen pop mid 2010s friday afternoon", groupCard.Title);
        Assert.Equal(groupCard.Title, sectionCard.Title);
        Assert.Equal("Exact description", groupCard.Subtitle);
        Assert.Equal("Spotify", groupCard.Meta!.OwnerName);
        Assert.Equal(50, groupCard.Meta.TrackCount);
        Assert.False(groupCard.Meta.NeedsHydration);

        // …and the second read, whose revision has not moved, spends NOTHING on the header network.
        int before = fetches;
        await hydrator.ResolveAsync(Feed(Shallow()), TestContext.Current.CancellationToken);
        Assert.Equal(before, fetches);
    }

    [Fact]
    public async Task RepeatedReads_RequeryHomeOnce_AndKeepOverlayingFromTheResidentHeader()
    {
        // The TTL-cached Home body recomposes the daylist as shallow on EVERY read (its `name` still equals
        // daylist_pretitle) while the store keeps the exact header — so a resident hit is the steady state, not the
        // exception. The requery invalidates and refetches UNCACHED and Home is polled on a 60 s timer, so spending one
        // per read pinned Home permanently off the Pathfinder TTL. An UNMOVED revision is what says "already
        // refreshed": reads 2 and 3 cost one head probe each and nothing else.
        int fetches = 0, refreshes = 0, probes = 0;
        var hydrator = new HomeDaylistHydrator(
            _ => Header(),
            (_, _) => { fetches++; return Task.CompletedTask; },
            (_, _) => { probes++; return Task.FromResult<byte[]?>(Rev(1)); },
            _ => { refreshes++; return Task.FromResult(Feed(Exact())); },
            nowMs: Clock());

        for (int read = 1; read <= 3; read++)
        {
            var result = await hydrator.ResolveAsync(Feed(Shallow()), TestContext.Current.CancellationToken);

            Assert.Equal(read, probes);                     // exactly one HEAD read per Home read — never per render
            Assert.Equal(1, fetches);                       // only the never-resolved first read reads the header
            Assert.Equal(1, refreshes);                     // one requery for this revision — reads 2 and 3 add none
            var card = Assert.Single(Assert.Single(result.Groups).Cards);
            Assert.Equal("teen pop mid 2010s friday afternoon", card.Title);
            Assert.False(card.Meta!.NeedsHydration);
            Assert.Equal(card.Title, Assert.Single(Assert.Single(result.Sections!).Cards).Title);
        }

        Assert.Equal(1, hydrator.IdentityVersion);          // the epoch stayed put: nothing rolled over
    }

    [Fact]
    public async Task ANewerRevisionForTheSameUri_EarnsOneMoreRequery_BumpsTheEpoch_ThenStopsAgain()
    {
        // A daylist rolls over through the day behind an advancing playlist4 revision, so the bound is per observed
        // ROLLOVER — not per URI, not per read, and not per title (a rollover that keeps its title still counts).
        // The Sunday gate having been spent must not suppress Monday's refresh.
        string title = "k-ballad korean ost sunday late night";
        var revision = Rev(1);
        int refreshes = 0;
        var hydrator = new HomeDaylistHydrator(
            _ => Header() with { Title = title },
            (_, _) => Task.CompletedTask,
            (_, _) => Task.FromResult<byte[]?>(revision),
            _ => { refreshes++; return Task.FromResult(LiveHomeResult.Empty); },   // empty body ⇒ the raw source stays the basis
            nowMs: Clock());

        var sunday = await hydrator.ResolveAsync(Feed(Shallow()), TestContext.Current.CancellationToken);
        Assert.Equal(title, Assert.Single(Assert.Single(sunday.Groups).Cards).Title);
        Assert.Equal(1, refreshes);
        Assert.Equal(1, hydrator.IdentityVersion);

        await hydrator.ResolveAsync(Feed(Shallow()), TestContext.Current.CancellationToken);
        Assert.Equal(1, refreshes);
        Assert.Equal(1, hydrator.IdentityVersion);

        title = "korean ost hallyu monday morning";
        revision = Rev(2);
        var monday = await hydrator.ResolveAsync(Feed(Shallow()), TestContext.Current.CancellationToken);
        Assert.Equal(title, Assert.Single(Assert.Single(monday.Groups).Cards).Title);
        Assert.Equal(2, refreshes);
        Assert.Equal(2, hydrator.IdentityVersion);          // the epoch step every parked Home page compares against

        await hydrator.ResolveAsync(Feed(Shallow()), TestContext.Current.CancellationToken);
        Assert.Equal(2, refreshes);
        Assert.Equal(2, hydrator.IdentityVersion);
    }

    [Fact]
    public async Task ARolloverUnderAnUnchangedTitle_StillRefreshes()
    {
        // The case a (uri, title) identity diff cannot see, and the reason the revision is the primitive: the server
        // rolled the content while the display title happened to survive.
        var revision = Rev(1);
        int fetches = 0, refreshes = 0;
        var hydrator = new HomeDaylistHydrator(
            _ => Header(),
            (_, _) => { fetches++; return Task.CompletedTask; },
            (_, _) => Task.FromResult<byte[]?>(revision),
            _ => { refreshes++; return Task.FromResult(Feed(Exact())); },
            nowMs: Clock());

        await hydrator.ResolveAsync(Feed(Shallow()), TestContext.Current.CancellationToken);
        Assert.Equal(1, fetches);
        Assert.Equal(1, refreshes);

        revision = Rev(7);
        await hydrator.ResolveAsync(Feed(Shallow()), TestContext.Current.CancellationToken);
        Assert.Equal(2, fetches);                           // the header is re-read, not trusted from the store
        Assert.Equal(2, refreshes);
        Assert.Equal(2, hydrator.IdentityVersion);
    }

    [Fact]
    public async Task Revalidate_ReportsARollover_AndIsSilentWhenNothingMoved()
    {
        // The reactivation compare a returning KeepAlive-parked page runs. It resolves nothing itself — a true answer
        // is what publishes the feed epoch.
        var revision = Rev(1);
        int probes = 0, refreshes = 0;
        var hydrator = new HomeDaylistHydrator(
            _ => Header(),
            (_, _) => Task.CompletedTask,
            (_, _) => { probes++; return Task.FromResult<byte[]?>(revision); },
            _ => { refreshes++; return Task.FromResult(Feed(Exact())); },
            nowMs: Clock());

        // Nothing hydrated yet ⇒ nothing to compare and NOT a network call.
        Assert.False(await hydrator.RevalidateAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, probes);

        await hydrator.ResolveAsync(Feed(Shallow()), TestContext.Current.CancellationToken);
        Assert.True(hydrator.Hydrated(Uri));
        int afterResolve = probes;

        Assert.False(await hydrator.RevalidateAsync(TestContext.Current.CancellationToken));
        Assert.Equal(afterResolve + 1, probes);             // one head read, and no requery followed it
        Assert.Equal(1, refreshes);

        revision = Rev(2);
        Assert.True(await hydrator.RevalidateAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, refreshes);                         // the compare itself resolves nothing
    }

    [Fact]
    public async Task ASecondCallerInsideTheCoalesceWindow_SharesOneHeadRead()
    {
        // A reactivation compare that finds a rollover, followed immediately by the read it triggers, must cost ONE
        // call. The window is request coalescing, not freshness: nothing about staleness is decided by it.
        long now = 1_000;
        int probes = 0;
        var hydrator = new HomeDaylistHydrator(
            _ => Header(),
            (_, _) => Task.CompletedTask,
            (_, _) => { probes++; return Task.FromResult<byte[]?>(Rev(1)); },
            _ => Task.FromResult(Feed(Exact())),
            nowMs: () => now);

        await hydrator.ResolveAsync(Feed(Shallow()), TestContext.Current.CancellationToken);
        Assert.Equal(1, probes);

        await hydrator.RevalidateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, probes);                            // inside the window → the completed probe answers again

        now += HomeDaylistHydrator.ProbeCoalesceMs + 1;
        await hydrator.RevalidateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, probes);                            // past it → a real head read again
    }

    [Fact]
    public async Task AFailedHeadRead_KeepsServingWhatWeHave_InsteadOfRefreshingEveryRead()
    {
        // "We learned nothing" must not read as "it changed", or an unreachable spclient turns the hero into a
        // per-read invalidate/requery storm.
        bool probeWorks = true;
        int fetches = 0, refreshes = 0;
        var hydrator = new HomeDaylistHydrator(
            _ => Header(),
            (_, _) => { fetches++; return Task.CompletedTask; },
            (_, _) => probeWorks
                ? Task.FromResult<byte[]?>(Rev(1))
                : Task.FromException<byte[]?>(new InvalidOperationException("spclient unreachable")),
            _ => { refreshes++; return Task.FromResult(Feed(Exact())); },
            nowMs: Clock());

        await hydrator.ResolveAsync(Feed(Shallow()), TestContext.Current.CancellationToken);
        Assert.Equal(1, fetches);
        Assert.Equal(1, refreshes);

        probeWorks = false;
        for (int read = 0; read < 3; read++)
        {
            var result = await hydrator.ResolveAsync(Feed(Shallow()), TestContext.Current.CancellationToken);
            Assert.Equal("teen pop mid 2010s friday afternoon", Assert.Single(Assert.Single(result.Groups).Cards).Title);
        }
        Assert.Equal(1, fetches);
        Assert.Equal(1, refreshes);
        Assert.False(await hydrator.RevalidateAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HeaderThatStillEqualsGenericPretitle_DoesNotReleaseAsExact()
    {
        var source = Feed(Shallow(title: ""));
        int fetches = 0, refreshes = 0;
        var hydrator = new HomeDaylistHydrator(
            _ => new HomePlaylistHeader("daylist", null, "Spotify", null, 50),
            (_, _) => { fetches++; return Task.CompletedTask; },
            (_, _) => Task.FromResult<byte[]?>(Rev(1)),
            _ => { refreshes++; return Task.FromResult(source); });

        var result = await hydrator.ResolveAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal(1, fetches);                  // resident generic header is rejected; one refetch is tried
        Assert.Equal(0, refreshes);                // still generic after fetch, so no cache churn loop
        Assert.Same(source, result);
    }

    [Fact]
    public async Task HeaderFailure_PreservesRawHome_AndCancellationPropagates()
    {
        var source = Feed(Shallow());
        int refreshes = 0;
        var failing = new HomeDaylistHydrator(
            _ => null,
            (_, _) => Task.FromException(new InvalidOperationException("header unavailable")),
            (_, _) => Task.FromResult<byte[]?>(Rev(1)),
            _ => { refreshes++; return Task.FromResult(source); });

        var fallback = await failing.ResolveAsync(source, TestContext.Current.CancellationToken);
        Assert.Same(source, fallback);
        Assert.Equal(0, refreshes);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = new HomeDaylistHydrator(
            _ => null,
            (_, ct) => Task.FromCanceled(ct),
            (_, ct) => Task.FromCanceled<byte[]?>(ct),
            _ => Task.FromResult(source));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.ResolveAsync(source, cts.Token));
    }

    // A clock that never repeats, so the coalesce window (request de-duplication, NOT freshness) can never make one
    // test's reads answer another's.
    static Func<long> Clock()
    {
        long t = 0;
        return () => t += HomeDaylistHydrator.ProbeCoalesceMs * 2;
    }

    static HomeCard Shallow(string title = "daylist") => new(
        Uri, title, "Generic description", null, HomeCardKind.Playlist,
        Meta: new HomeCardMeta(Format: "daylist", TrackCount: 50,
            Seeds: ["teen pop", "mid 2010s", "friday afternoon"], OwnerName: "Spotify",
            GenericTitle: "daylist", NeedsHydration: true));

    static HomeCard Exact() => Shallow() with
    {
        Title = "teen pop mid 2010s friday afternoon",
        Meta = Shallow().Meta! with { NeedsHydration = false },
    };

    static HomePlaylistHeader Header() => new(
        "teen pop mid 2010s friday afternoon", "Exact description", "Spotify", null, 50);

    static LiveHomeResult Feed(HomeCard card, bool duplicateGroup = false)
    {
        HomeGroup[] groups = duplicateGroup
            ? [new(HomeGroupKind.Hero, null, [card]), new(HomeGroupKind.QuickGrid, "Jump back in", [card])]
            : [new(HomeGroupKind.Hero, null, [card])];
        HomeSection[] sections =
        [
            new("spotify:section:daylist", "Your daylist", null, [card], 1, 1),
        ];
        return new LiveHomeResult(groups, null, "Good afternoon", sections);
    }
}
