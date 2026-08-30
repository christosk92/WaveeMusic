using System;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// ── The one field that must be able to go back to null (StoreEntityMerge.Artist → MergeExtras) ───────────────────────
// Every other ArtistExtras facet is additive: a thin write that lacks it means "I don't know", so keeping the stored
// value is right. A pre-release is a temporary state that ENDS, and the server signals the end by dropping preReleaseV2
// from the overview — with a plain `?? current` the album ships and the artist page says "Coming soon" forever.
//
// The discriminator is Artist.OverviewFetchedAt — the OVERVIEW's own stamp, which only the queryArtistOverview write
// sets. It used to be FetchedAt, and that was wrong in a way no test caught: FetchedAt is a max-of clock any writer may
// raise, so a write that merely bumped it (the chart step, a V4 upsert carrying one) claimed authority over absences it
// knew nothing about and silently cleared the "Coming soon" card. These drive the real merge (the same entry point
// InMemoryStore.UpsertArtist calls) from both sides of that gate.
public class PreReleaseMergeTests
{
    static readonly DateTimeOffset T0 = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset T1 = T0.AddMinutes(5);

    static ArtistPreRelease Announcement(string name = "ARE YOU EVER COMING BACK?") =>
        new("spotify:album:0qi1ztU4S08zA1FsP1DUaY", name, null, DateTimeOffset.UtcNow.AddDays(30), "ALBUM");

    static PinnedItem Pin(string title) =>
        new("Pinned", title, "Single", "out now", null, "spotify:album:pin");

    static Artist Rec(ArtistPreRelease? pre, PinnedItem? pin, DateTimeOffset fetchedAt,
                      ArtistWatchFeed? watch = null, bool extras = true) =>
        new("a1", "spotify:artist:a1", "A1", null,
            Pinned: pin,
            Extras: extras ? new ArtistExtras(WatchFeed: watch, PreRelease: pre) : null,
            FetchedAt: fetchedAt, OverviewFetchedAt: fetchedAt);

    // ── PreRelease: the null-back rule, both polarities ───────────────────────────────────────────────────────────────

    [Fact]
    public void FreshOverviewWithoutAPreRelease_DropsTheStaleAnnouncement()
    {
        var current = Rec(Announcement(), null, T0);
        var incoming = Rec(null, null, T1);                    // a full overview that no longer names one

        var merged = StoreEntityMerge.Artist(current, incoming);

        Assert.Null(merged.Extras!.PreRelease);                   // the record shipped — the announcement must go
    }

    [Fact]
    public void ThinWriteWithoutAPreRelease_KeepsTheAnnouncement()
    {
        var current = Rec(Announcement(), null, T0);
        var incoming = Rec(null, null, default);               // a thin V4 / NPV / album-derived write

        var merged = StoreEntityMerge.Artist(current, incoming);

        Assert.NotNull(merged.Extras!.PreRelease);                // "I don't know" must never read as "it's gone"
        Assert.Equal("ARE YOU EVER COMING BACK?", merged.Extras.PreRelease!.Name);
    }

    [Fact]
    public void ThinWriteWithNoExtrasAtAll_KeepsTheStoredBundleWholesale()
    {
        var current = Rec(Announcement(), null, T0);
        var incoming = Rec(null, null, default, extras: false);

        var merged = StoreEntityMerge.Artist(current, incoming);

        Assert.NotNull(merged.Extras);
        Assert.NotNull(merged.Extras!.PreRelease);
    }

    [Fact]
    public void FreshOverviewWithANewPreRelease_Replaces()
    {
        var current = Rec(Announcement("OLD"), null, T0);
        var incoming = Rec(Announcement("NEW"), null, T1);

        var merged = StoreEntityMerge.Artist(current, incoming);

        Assert.Equal("NEW", merged.Extras!.PreRelease!.Name);
    }

    [Fact]
    public void TheNullBackRuleAppliesToPreReleaseOnly_NotToItsNeighbours()
    {
        // The regression this guards: MergeExtras is a positional ctor, so it is trivially easy to give a second field
        // the same authoritative treatment (or to forget one entirely — which is exactly how WatchFeed once nulled
        // itself on every non-first artist write).
        var watch = new ArtistWatchFeed("spotify:artist:a1", null, null);
        var current = Rec(Announcement(), null, T0, watch);
        var incoming = Rec(null, null, T1);                    // authoritative, and carries neither field

        var merged = StoreEntityMerge.Artist(current, incoming);

        Assert.Null(merged.Extras!.PreRelease);                   // the exception…
        Assert.NotNull(merged.Extras.WatchFeed);                  // …and everything else still additive
    }

    [Fact]
    public void AThinWriteThatBumpsFetchedAt_IsStillNotAuthoritative()
    {
        // The regression the OverviewFetchedAt split exists for. FetchedAt is a max-of stamp — the chart step and any
        // writer that happens to carry one can move it — so gating authority on it let a NON-overview write clear a
        // live pre-release. Only the overview's own stamp may claim "I know what this artist no longer has".
        var current = Rec(Announcement(), null, T0);
        var incoming = new Artist("a1", "spotify:artist:a1", "A1", null,
            Extras: new ArtistExtras(PreRelease: null),
            FetchedAt: T1, OverviewFetchedAt: default);       // newer, but NOT an overview

        var merged = StoreEntityMerge.Artist(current, incoming);

        Assert.NotNull(merged.Extras!.PreRelease);
        Assert.Equal(T1, merged.FetchedAt);                    // the max-of clock still moved…
        Assert.Equal(T0, merged.OverviewFetchedAt);            // …and the overview clock did not
    }

    // ── Pinned: deliberately NOT null-backed ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThinWrite_KeepsTheStoredPin()
    {
        var current = Rec(null, Pin("Heroine"), T0);
        var incoming = Rec(null, null, default);

        Assert.Equal("Heroine", StoreEntityMerge.Artist(current, incoming).Pinned!.Title);
    }

    [Fact]
    public void FreshOverviewWithoutAPin_STILL_KeepsIt_ByDesign()
    {
        // Documented asymmetry: a thin write also carries Pinned = null, so telling "the pin was removed" from "this
        // write doesn't know" would need the discriminator plumbed through here too. A stale pin is harmless because
        // every pre-release surface gates on PinnedItem.IsUpcoming — a wall-clock test — so a pin whose record has
        // shipped silently reverts to an ordinary promo card.
        var current = Rec(null, Pin("Heroine"), T0);
        var incoming = Rec(null, null, T1);

        Assert.Equal("Heroine", StoreEntityMerge.Artist(current, incoming).Pinned!.Title);
    }

    [Fact]
    public void AnIncomingPin_Replaces()
    {
        var current = Rec(null, Pin("Heroine"), T0);
        var incoming = Rec(null, Pin("Nostalgia"), T1);

        Assert.Equal("Nostalgia", StoreEntityMerge.Artist(current, incoming).Pinned!.Title);
    }

    [Fact]
    public void AnUpcomingPinSurvivesAThinWrite_ThroughTheRealStore()
    {
        // The same rules as reached by production: two UpsertArtist calls, the second thin — which is what the artist
        // page actually does (thin V4 upsert, then the overview upsert, in either order).
        var store = new InMemoryStore();
        var due = DateTimeOffset.UtcNow.AddDays(30);
        var pin = new PinnedItem("Pinned", "ARE YOU EVER COMING BACK?", "Album", "", null,
            "spotify:album:pin", ItemUri: "spotify:album:0qi1ztU4S08zA1FsP1DUaY", ItemType: "ALBUM",
            ItemTypename: "Album", ReleaseAt: due);

        store.UpsertArtist(Rec(Announcement(), pin, DateTimeOffset.UtcNow));
        store.UpsertArtist(Rec(null, null, default));          // the thin write lands afterwards

        var a = store.GetArtist("spotify:artist:a1");
        Assert.NotNull(a);
        Assert.True(a!.Pinned!.IsUpcoming);
        Assert.Equal("spotify:album:0qi1ztU4S08zA1FsP1DUaY", a.Pinned.TargetUri);
        Assert.NotNull(a.Extras!.PreRelease);
    }
}
