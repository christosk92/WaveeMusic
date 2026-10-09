// ── Wavee.Tests/SidebarFeedsTests.cs — the two feed sections' rules: Recently played (SidebarRecentsRules), New releases ──
// (SidebarNewReleasesRules), which feeds a layout demands (SidebarFeedDemands), and the two stamp maps the feeds read
// (SidebarFirstSeen, SidebarRecency, at the end of this file).
//
// Every rule is pure: the entries come from SidebarSourceIndex.Rebuild, the resident table is a dictionary behind
// ISidebarEntityPeek, and the feed is a list of Notification records. Nothing here starts the engine or the network.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>A resident-table double: a dictionary-backed peek, so the rule is tested without Entities.</summary>
sealed class DictPeek : ISidebarEntityPeek
{
    public readonly Dictionary<string, SidebarLibraryEntry> Rows = new(StringComparer.Ordinal);

    public bool TryPeek(string uri, out SidebarLibraryEntry entry) => Rows.TryGetValue(uri, out entry);
}

public class SidebarFeedsTests
{
    const string PlaylistA = "spotify:playlist:37i9dQZF1DX4sWSpwq3LiO";
    const string AlbumKnown = "spotify:album:6dVIqQ8qmQ5GBnJ9shOYGE";
    const string AlbumStub = "spotify:album:1TSZDcvlPtAnekTaItI3qO";
    const string AlbumNoTitle = "spotify:album:4aawyAB79vO75wG7WLfDzB";

    static SidebarLibraryEntry Entry(string id, SidebarEntryKind kind, string uri, string name, long sortStamp = 0)
        => new(id, kind, uri, name, "", StringId.Empty, null, ChildCount: 0, AddedAtMs: 0, SortStamp: sortStamp,
               LastVisitedTicksUtc: 0, SourceOrder: 0, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None)
        { FolderId = "", FolderName = "", FirstArtistName = "", IdentityKnown = true };

    static SidebarSourceIndex IndexOf(params SidebarLibraryEntry[] entries)
    {
        var index = new SidebarSourceIndex();
        index.Rebuild(entries);
        return index;
    }

    static SidebarPlayedContext Played(string uri, SidebarEntryKind kind, long at, string? title = null)
        => new(uri, kind, at, title);

    static Notification Release(string id, long at, string? subject, string? title, string? creator = null,
                                NotifyCategory category = NotifyCategory.NewRelease)
        => new(id, at, false, category, Title: title ?? "", Subject: subject is null ? default : EntityUri.Parse(subject),
               Creator: creator);

    // ── Recently played: each context resolves synchronously, in order, or is skipped ────────────────────────────────

    [Fact]
    public void Recents_IndexHit_StampedWithPlayTime()
    {
        var index = IndexOf(Entry(SidebarPinId.FromUri(PlaylistA)!, SidebarEntryKind.Playlist, PlaylistA, "Mix"));
        var into = new List<SidebarLibraryEntry>();

        int n = SidebarRecentsRules.Resolve([Played(PlaylistA, SidebarEntryKind.Playlist, 500)], index, null, 10, into);

        Assert.Equal(1, n);
        Assert.Equal("Mix", into[0].Name);
        Assert.Equal(500, into[0].SortStamp);
    }

    [Fact]
    public void Recents_PeekWhenIndexMisses()
    {
        var peek = new DictPeek();
        peek.Rows[AlbumKnown] = Entry("", SidebarEntryKind.Album, AlbumKnown, "Resident");
        var into = new List<SidebarLibraryEntry>();

        SidebarRecentsRules.Resolve([Played(AlbumKnown, SidebarEntryKind.Album, 700)], IndexOf(), peek, 10, into);

        var row = Assert.Single(into);
        Assert.Equal("Resident", row.Name);
        Assert.Equal(SidebarPinId.FromUri(AlbumKnown), row.Id);
        Assert.Equal(700, row.SortStamp);
    }

    [Fact]
    public void Recents_LoggedTitleFallback()
    {
        // Neither the projection nor the resident tables know it: the play log's own title names the row.
        var into = new List<SidebarLibraryEntry>();

        SidebarRecentsRules.Resolve([Played(AlbumStub, SidebarEntryKind.Album, 800, "Logged title")], IndexOf(), null, 10, into);

        var row = Assert.Single(into);
        Assert.Equal("Logged title", row.Name);
        Assert.Equal(SidebarEntryKind.Album, row.Kind);
    }

    [Fact]
    public void Recents_UnnamedSkipped_NeverBlank()
    {
        // A context nobody can name is invisible, never a grey bone (D9). An empty-named peek hit does not count either.
        var peek = new DictPeek();
        peek.Rows[AlbumStub] = Entry("", SidebarEntryKind.Album, AlbumStub, "");
        var into = new List<SidebarLibraryEntry>();

        int n = SidebarRecentsRules.Resolve([Played(AlbumStub, SidebarEntryKind.Album, 1)], IndexOf(), peek, 10, into);

        Assert.Equal(0, n);
        Assert.Empty(into);
    }

    [Fact]
    public void Recents_TracksSkipped()
    {
        // A bare track play is not a context, and an empty uri is nothing at all.
        var into = new List<SidebarLibraryEntry>();

        int n = SidebarRecentsRules.Resolve(
            [Played("spotify:track:4cOdK2wGLETKBW3PvgPWqT", SidebarEntryKind.Track, 1, "Song"), Played("", SidebarEntryKind.Album, 2, "x")],
            IndexOf(), null, 10, into);

        Assert.Equal(0, n);
    }

    [Fact]
    public void Recents_Deduped()
    {
        var index = IndexOf(Entry(SidebarPinId.FromUri(PlaylistA)!, SidebarEntryKind.Playlist, PlaylistA, "Mix"));
        var into = new List<SidebarLibraryEntry>();

        int n = SidebarRecentsRules.Resolve(
            [Played(PlaylistA, SidebarEntryKind.Playlist, 900), Played(PlaylistA, SidebarEntryKind.Playlist, 800)],
            index, null, 10, into);

        Assert.Equal(1, n);
        Assert.Equal(900, into[0].SortStamp);     // the newest play is first and wins
    }

    [Fact]
    public void Recents_Limit()
    {
        var contexts = new List<SidebarPlayedContext>();
        for (int i = 0; i < 6; i++) contexts.Add(Played("spotify:album:" + new string((char)('A' + i), 22), SidebarEntryKind.Album, 100 - i, "T" + i));
        var into = new List<SidebarLibraryEntry>();

        int n = SidebarRecentsRules.Resolve(contexts, IndexOf(), null, 3, into);

        Assert.Equal(3, n);
        Assert.Equal(3, into.Count);
    }

    [Fact]
    public void Recents_LikedCollectionIsTheLikedRoute()
    {
        var into = new List<SidebarLibraryEntry>();

        SidebarRecentsRules.Resolve([Played(EntityUri.LikedCollection, SidebarEntryKind.Playlist, 300, "Liked Songs")],
            IndexOf(), null, 10, into);

        var row = Assert.Single(into);
        Assert.Equal("liked", row.Id);
        Assert.Equal(SidebarEntryKind.AppRoute, row.Kind);
    }

    // ── New releases ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NewReleases_ShouldRefresh_Matrix()
    {
        const long min = 60_000L;
        long now = 100 * min;

        Assert.False(SidebarNewReleasesRules.ShouldRefresh(0, now, Notify.FeedState.Loading));          // never while loading
        Assert.True(SidebarNewReleasesRules.ShouldRefresh(now - min, now, Notify.FeedState.Idle));      // idle: ask now
        Assert.True(SidebarNewReleasesRules.ShouldRefresh(0, now, Notify.FeedState.Populated));         // never asked: ask now
        Assert.False(SidebarNewReleasesRules.ShouldRefresh(now - 10 * min, now, Notify.FeedState.Populated));  // 10 min ago
        Assert.True(SidebarNewReleasesRules.ShouldRefresh(now - 31 * min, now, Notify.FeedState.Populated));   // 31 min ago
    }

    [Fact]
    public void NewReleases_Fill_SkipsNonReleases_IndexHit_TitleStub_NoTitleSkipped()
    {
        var index = IndexOf(Entry(SidebarPinId.FromUri(AlbumKnown)!, SidebarEntryKind.Album, AlbumKnown, "Known", 0));
        var feed = new List<Notification>
        {
            Release("n1", 1_000, AlbumKnown, "Known title"),                                   // in the projection: its real entry
            Release("n2", 2_000, AlbumStub, "Stub", creator: "Someone"),                      // not in it: built from the row
            Release("n3", 3_000, AlbumNoTitle, ""),                                           // no entry and no title: skipped
            Release("n4", 4_000, PlaylistA, "A playlist", category: NotifyCategory.Social),   // not a release: skipped
            Release("n5", 5_000, null, "No subject"),                                         // no subject: skipped
        };
        var into = new List<SidebarLibraryEntry>();

        int n = SidebarNewReleasesRules.Fill(feed, index, into, 10);

        Assert.Equal(2, n);
        Assert.Equal("Known", into[0].Name);
        Assert.Equal(1_000, into[0].SortStamp);
        Assert.Equal("Stub", into[1].Name);
        Assert.Equal("Someone", into[1].Creator);
        Assert.Equal(2_000, into[1].SortStamp);
    }

    [Fact]
    public void NewReleases_NewSince()
    {
        var releases = new List<SidebarLibraryEntry>
        {
            Entry("a", SidebarEntryKind.Album, AlbumKnown, "A", sortStamp: 100),
            Entry("b", SidebarEntryKind.Album, AlbumStub, "B", sortStamp: 200),
            Entry("c", SidebarEntryKind.Album, AlbumNoTitle, "C", sortStamp: 300),
        };

        Assert.Equal(2, SidebarNewReleasesRules.NewSince(releases, 150));
        Assert.Equal(0, SidebarNewReleasesRules.NewSince(releases, 300));
    }

    // ── demand: a feed is read only while its section is shown ───────────────────────────────────────────────────────

    [Fact]
    public void Demand_OnlyShownFeeds()
    {
        var hidden = SidebarLayoutRules.Resolve(SidebarLayoutState.Default, SidebarLayoutId.Classic, SidebarDensity.Default);
        Assert.Equal(SidebarFeedDemand.None, SidebarFeedDemands.Of(hidden));

        var recent = SidebarLayoutRules.Apply(SidebarLayoutState.Default,
            new SetSectionShown(SidebarLayoutId.Classic, "recent", true), pinnedLocked: false).State;
        Assert.Equal(SidebarFeedDemand.Recent, SidebarFeedDemands.Of(SidebarLayoutRules.Resolve(recent, SidebarLayoutId.Classic, SidebarDensity.Default)));

        var both = SidebarLayoutRules.Apply(recent,
            new SetSectionShown(SidebarLayoutId.Classic, "newReleases", true), pinnedLocked: false).State;
        Assert.Equal(SidebarFeedDemand.Recent | SidebarFeedDemand.NewReleases,
            SidebarFeedDemands.Of(SidebarLayoutRules.Resolve(both, SidebarLayoutId.Classic, SidebarDensity.Default)));

        // Library has no feeds at all, whatever the classic layout shows.
        Assert.Equal(SidebarFeedDemand.None,
            SidebarFeedDemands.Of(SidebarLayoutRules.Resolve(both, SidebarLayoutId.Library, SidebarDensity.Default)));
    }
}

// ── SidebarFirstSeen: the bounded first-observation stamp map ───────────────────────────────────────────────────────
//
// The honest playlist "date added" proxy: playlists have no add timestamp anywhere, so the projection records the
// first time it ever observes a playlist id and sorts by that. Pure in-memory, with an injected clock so the cap
// test does not depend on wall-clock timing.

public class SidebarFirstSeenTests
{
    /// <summary>A fresh store over a monotonic, test-owned clock — the cap fact stamps 2001 ids and must not depend
    /// on wall-clock resolution.</summary>
    static SidebarFirstSeen Clocked()
    {
        long now = 0;
        return new SidebarFirstSeen(() => ++now);
    }

    [Fact]
    public void Stamp_records_the_first_observation_only()
    {
        var seen = Clocked();
        long first = seen.Stamp("pl:a");
        long again = seen.Stamp("pl:a");
        Assert.Equal(first, again);           // the SECOND observation never overwrites the first
        Assert.Equal(1, seen.Count);
    }

    [Fact]
    public void Peek_never_records_a_stamp()
    {
        var seen = Clocked();
        Assert.Equal(0L, seen.Peek("pl:a"));   // never observed
        Assert.Equal(0, seen.Count);
        seen.Stamp("pl:a");
        Assert.Equal(seen.Stamp("pl:a"), seen.Peek("pl:a"));
    }

    [Fact]
    public void NewStamps_counts_only_fresh_records_since_the_last_reset()
    {
        var seen = Clocked();
        seen.Stamp("pl:a");
        seen.Stamp("pl:b");
        seen.Stamp("pl:a");                    // already seen — not fresh
        Assert.Equal(2, seen.NewStamps);

        seen.ResetNewCount();
        Assert.Equal(0, seen.NewStamps);
        seen.Stamp("pl:c");
        Assert.Equal(1, seen.NewStamps);
    }

    [Fact]
    public void The_cap_is_2000_and_the_oldest_stamp_is_evicted_to_admit_a_new_one()
    {
        var seen = Clocked();
        Assert.Equal(2000, SidebarFirstSeen.Cap);

        for (int i = 0; i < SidebarFirstSeen.Cap; i++) seen.Stamp("pl:" + i);
        Assert.Equal(SidebarFirstSeen.Cap, seen.Count);
        Assert.True(seen.Peek("pl:0") > 0);     // the very first stamp is still present, right at the cap

        seen.Stamp("pl:new");                   // one MORE than the cap
        Assert.Equal(SidebarFirstSeen.Cap, seen.Count);          // never grows past the cap
        Assert.Equal(0L, seen.Peek("pl:0"));                     // the OLDEST stamp was evicted
        Assert.True(seen.Peek("pl:new") > 0);
    }

    [Fact]
    public void Load_rehydrates_from_the_persisted_document_and_the_oldest_stamp_wins_on_a_duplicate()
    {
        var seen = Clocked();
        seen.Load(
        [
            new KeyValuePair<string, long>("pl:a", 500),
            new KeyValuePair<string, long>("pl:a", 100),   // a duplicate row, older — must win
            new KeyValuePair<string, long>("pl:b", 300),
            new KeyValuePair<string, long>("pl:bad", 0),    // invalid — skipped
            new KeyValuePair<string, long>("", 200),        // invalid — skipped
        ]);

        Assert.Equal(100, seen.Peek("pl:a"));
        Assert.Equal(300, seen.Peek("pl:b"));
        Assert.Equal(2, seen.Count);
        Assert.Equal(0, seen.NewStamps);        // a rehydrate is not a fresh observation
    }

    [Fact]
    public void PruneTo_drops_stamps_for_ids_no_longer_in_the_library()
    {
        var seen = Clocked();
        seen.Stamp("pl:a");
        seen.Stamp("pl:b");
        seen.Stamp("pl:c");

        int removed = seen.PruneTo(["pl:a", "pl:c"]);   // pl:b no longer in the live set

        Assert.Equal(1, removed);
        Assert.Equal(2, seen.Count);
        Assert.Equal(0L, seen.Peek("pl:b"));
        Assert.True(seen.Peek("pl:a") > 0);
        Assert.True(seen.Peek("pl:c") > 0);
    }

    [Fact]
    public void CopyTo_appends_every_stamp_for_persistence()
    {
        var seen = Clocked();
        seen.Stamp("pl:a");
        seen.Stamp("pl:b");

        var into = new List<KeyValuePair<string, long>>();
        seen.CopyTo(into);

        Assert.Equal(2, into.Count);
        Assert.Contains(into, kv => kv.Key == "pl:a");
        Assert.Contains(into, kv => kv.Key == "pl:b");
    }

    [Fact]
    public void The_frozen_instance_behaves_as_just_seen_but_never_mutates()
    {
        long first = SidebarFirstSeen.Frozen.Stamp("pl:a");
        long second = SidebarFirstSeen.Frozen.Stamp("pl:a");
        Assert.True(first > 0);
        Assert.True(second > 0);
        Assert.Equal(0, SidebarFirstSeen.Frozen.Count);           // never records anything
        Assert.Equal(0, SidebarFirstSeen.Frozen.PruneTo(Array.Empty<string>()));
    }
}

// ── SidebarRecency: navigation recency for the "recently opened" FEED ONLY ──────────────────────────────────────────
//
// Explicitly NOT what the sidebar's Recents SORT reads (that reads the play log's recency) — a surface that reads
// this for anything but the "recently opened" shelf, or that calls it "recently played", is a defect these tests
// exist to catch by naming.

public class SidebarRecencyTests
{
    [Fact]
    public void Empty_has_no_visits_and_is_the_shared_seed_instance()
    {
        Assert.Equal(0, SidebarRecency.Empty.Count);
        Assert.Equal(0L, SidebarRecency.Empty.LastVisitedTicks("anything"));
        Assert.Same(SidebarRecency.Empty, SidebarRecency.Build(Array.Empty<SidebarVisit>()));
    }

    [Fact]
    public void Build_keeps_the_newest_visit_per_route_key()
    {
        // Oldest-first input; the LAST occurrence of a key is the newest visit and must win.
        var recency = SidebarRecency.Build(
        [
            new SidebarVisit("pl:a", 100),
            new SidebarVisit("pl:b", 150),
            new SidebarVisit("pl:a", 200),   // a re-visit — must overwrite the earlier stamp
        ]);

        Assert.Equal(200, recency.LastVisitedTicks("pl:a"));
        Assert.Equal(150, recency.LastVisitedTicks("pl:b"));
        Assert.Equal(2, recency.Count);
    }

    [Fact]
    public void An_id_never_visited_reads_as_zero_not_missing()
    {
        var recency = SidebarRecency.Build([new SidebarVisit("pl:a", 100)]);
        Assert.Equal(0L, recency.LastVisitedTicks("pl:never"));
        Assert.Equal(0L, recency.LastVisitedTicks(null));
    }

    [Fact]
    public void A_visit_with_an_empty_route_key_is_ignored()
    {
        var recency = SidebarRecency.Build(
        [
            new SidebarVisit("", 100),
            new SidebarVisit("pl:a", 200),
        ]);
        Assert.Equal(1, recency.Count);
        Assert.Equal(200, recency.LastVisitedTicks("pl:a"));
    }

    [Fact]
    public void The_generic_builder_reads_any_row_type_through_accessor_lambdas()
    {
        var rows = new[] { (Key: "pl:a", Ticks: 10L), (Key: "pl:b", Ticks: 20L), (Key: "pl:a", Ticks: 30L) };
        var recency = SidebarRecency.Build(rows, r => r.Key, r => r.Ticks);

        Assert.Equal(30, recency.LastVisitedTicks("pl:a"));
        Assert.Equal(20, recency.LastVisitedTicks("pl:b"));
    }
}
