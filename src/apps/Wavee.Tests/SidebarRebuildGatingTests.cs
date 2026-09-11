using System;
using System.Collections.Generic;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

// The performance work's pure-rule pins (items A/B/C/D of the rebuild-gating pass): a rebuild/publication that
// looks the same as last time must not fan out into new signal writes, and a shelf/pane demand rebuild must not
// re-scan more than it needs to. Every type under test here is engine-free (System + Wavee.Core(.Catalog) plus the
// real Signal<T>/Loadable<T> — see the Wavee.Tests.csproj comments next to each Compile item), so these tests drive
// the REAL decisions, not a copy of them.
public sealed class SidebarRebuildGatingTests
{
    // ── B: SidebarBinderTriggers.LibraryEpochOf ─────────────────────────────────────────────────────────────────

    [Fact]
    public void LibraryEpochOfIsUnchangedForASnapshotEqualByValue()
    {
        // Two DISTINCT LibraryQuerySnapshot instances built from the SAME per-collection references — exactly what
        // a rebuild that reuses unchanged sub-collections (or an activity-only republish) produces: a fresh outer
        // wrapper, unchanged inner content. The old identity-hash fold moved on the outer instance alone; the
        // record's own (structural-once-agent-B-lands) hash does not.
        IReadOnlyList<LibraryItem> entries = Array.Empty<LibraryItem>();
        IReadOnlyList<PlaylistNode> tree = Array.Empty<PlaylistNode>();
        var addedAt = new Dictionary<string, long>();
        var stats = new LibraryStats(3, 2, 1, 0);
        IReadOnlyList<Album> albums = Array.Empty<Album>();
        IReadOnlyList<Artist> artists = Array.Empty<Artist>();
        IReadOnlyList<Show> shows = Array.Empty<Show>();
        IReadOnlyList<PlaylistSummary> playlists = Array.Empty<PlaylistSummary>();

        // The same content fold: a zero fold means "no fold" and falls back to reference identity by design.
        var first = new LibraryQuerySnapshot(entries, tree, stats, addedAt)
            { Albums = albums, Artists = artists, Shows = shows, Playlists = playlists, ContentHash = 42 };
        var second = new LibraryQuerySnapshot(entries, tree, stats, addedAt)
            { Albums = albums, Artists = artists, Shows = shows, Playlists = playlists, ContentHash = 42 };
        Assert.False(ReferenceEquals(first, second));

        var status = new QueryStatus(true, false, false);
        var snapshotA = new QuerySnapshot<LibraryQuerySnapshot>(1, 1, first, status, Array.Empty<FacetProblem>());
        var snapshotB = new QuerySnapshot<LibraryQuerySnapshot>(2, 1, second, status, Array.Empty<FacetProblem>());

        Assert.Equal(SidebarBinderTriggers.LibraryEpochOf(snapshotA), SidebarBinderTriggers.LibraryEpochOf(snapshotB));
    }

    [Fact]
    public void LibraryEpochOfMovesWhenStatusDiffers()
    {
        var library = new LibraryQuerySnapshot([], [], new(0, 0, 0, 0), new Dictionary<string, long>());
        var ready = new QuerySnapshot<LibraryQuerySnapshot>(1, 1, library, new QueryStatus(true, false, false), []);
        var refreshing = new QuerySnapshot<LibraryQuerySnapshot>(1, 1, library, new QueryStatus(true, true, false), []);
        Assert.NotEqual(SidebarBinderTriggers.LibraryEpochOf(ready), SidebarBinderTriggers.LibraryEpochOf(refreshing));
    }

    [Fact]
    public void LibraryEpochOfIsZeroForANullSnapshot() => Assert.Equal(0, SidebarBinderTriggers.LibraryEpochOf(null));

    // ── A: LibraryStore's per-cell write gate ───────────────────────────────────────────────────────────────────

    [Fact]
    public void LibraryStoreCellIsRewrittenOnlyForADifferentListInstance()
    {
        var cell = Loadable<IReadOnlyList<Album>>.Pending(Array.Empty<Album>());
        IReadOnlyList<Album> first = new List<Album> { new("a", "spotify:album:a", "", null, [], 0, 0) };
        cell.SetReady(first);

        // The same instance again (the definition hands back the previous list when nothing moved): no write.
        LibraryStore.SetIfMoved(cell, first);
        Assert.Same(first, cell.Value.Peek());

        // A different instance with the same count IS a change (a rename keeps the count): the cell must move.
        IReadOnlyList<Album> renamed = new List<Album> { new("a", "spotify:album:a", "renamed", null, [], 0, 0) };
        LibraryStore.SetIfMoved(cell, renamed);
        Assert.Same(renamed, cell.Value.Peek());
    }

    [Fact]
    public void LibraryStoreCellUpdatesWhenTheCountActuallyChanges()
    {
        var cell = Loadable<IReadOnlyList<Album>>.Pending(Array.Empty<Album>());
        IReadOnlyList<Album> first = new List<Album> { new("a", "spotify:album:a", "", null, [], 0, 0) };
        cell.SetReady(first);

        IReadOnlyList<Album> second = new List<Album>
        {
            new("a", "spotify:album:a", "", null, [], 0, 0),
            new("b", "spotify:album:b", "", null, [], 0, 0),
        };
        LibraryStore.SetIfMoved(cell, second);

        Assert.Same(second, cell.Value.Peek());
    }

    [Fact]
    public void LibraryStoreStatsCellIsGatedByValueEqualityNotReference()
    {
        var cell = Loadable<LibraryStats>.Pending(new(0, 0, 0, 0));
        cell.SetReady(new LibraryStats(1, 2, 3, 4));

        // A NEW (but value-equal) LibraryStats record — LibraryStats.Equals is the record's own structural equality,
        // so this must not re-arm the cell at all.
        LibraryStore.SetIfMoved(cell, new LibraryStats(1, 2, 3, 4));
        var afterEqual = cell.Value.Peek();

        LibraryStore.SetIfMoved(cell, new LibraryStats(1, 2, 3, 5));
        Assert.Equal(new LibraryStats(1, 2, 3, 4), afterEqual);
        Assert.Equal(new LibraryStats(1, 2, 3, 5), cell.Value.Peek());
    }

    // ── C: ScopeRebindRules.RequiresReacquire — the account gate for SidebarProjectionBinder.RebindScope ──────────

    static CatalogScope Scope(string provider = "spotify", string account = "acct1", bool contextKnown = true,
        string storageAccount = "default", string locale = "en", string market = "US")
        => new(provider, account, locale, market, "premium", 1, false, contextKnown, storageAccount);

    [Fact]
    public void RequiresReacquire_IsFalseForAnIdenticalScope()
        => Assert.False(ScopeRebindRules.RequiresReacquire(Scope(), Scope()));

    [Fact]
    public void RequiresReacquire_IsFalseForAContextKnownFlipAlone()
    {
        // The exact case problem 3 fixes: a same-account session merely CONFIRMING itself (a "which account is
        // this?" prompt resolving to the account already assumed) must not tear down and re-acquire.
        var previous = Scope(contextKnown: false);
        var next = Scope(contextKnown: true);
        Assert.False(ScopeRebindRules.RequiresReacquire(previous, next));
    }

    [Fact]
    public void RequiresReacquire_IsFalseForResultShapingFieldsAlone()
    {
        // Locale/Market reshape a query's RESULTS, never its IDENTITY — the query definitions read them per-call.
        var previous = Scope(locale: "en", market: "US");
        var next = Scope(locale: "fr", market: "FR");
        Assert.False(ScopeRebindRules.RequiresReacquire(previous, next));
    }

    [Fact]
    public void RequiresReacquire_IsTrueForADifferentProviderAccount()
    {
        var previous = Scope(account: "acct1");
        var next = Scope(account: "acct2");
        Assert.True(ScopeRebindRules.RequiresReacquire(previous, next));
    }

    [Fact]
    public void RequiresReacquire_IsTrueForADifferentProvider()
    {
        var previous = Scope(provider: "spotify");
        var next = Scope(provider: "local");
        Assert.True(ScopeRebindRules.RequiresReacquire(previous, next));
    }

    [Fact]
    public void RequiresReacquire_IsTrueForADifferentStorageAccount()
    {
        var previous = Scope(storageAccount: "default");
        var next = Scope(storageAccount: "profile2");
        Assert.True(ScopeRebindRules.RequiresReacquire(previous, next));
    }

    // ── B: SidebarBinderPipeline.PinEntry — the partial-card merge (problem 2 / Required change B) ────────────────

    [Fact]
    public void PinEntry_NameOnly_Resolves_CoverStillNull()
    {
        var entry = SidebarBinderPipeline.PinEntry(SidebarEntryKind.Playlist, "My Playlist", null, 0, "");
        Assert.NotNull(entry);
        Assert.Equal("My Playlist", entry!.Value.Name);
        Assert.Null(entry.Value.Cover);
    }

    [Fact]
    public void PinEntry_CoverOnly_StillResolves_NameEmpty()
    {
        // The exact bug problem 2 fixes: a cover that resolved before the name used to be thrown away entirely
        // (the old guard was `name.Length == 0 ⇒ null`), leaving the pin on its bare base entry forever.
        var cover = new Image("https://example/cover.jpg");
        var entry = SidebarBinderPipeline.PinEntry(SidebarEntryKind.Playlist, "", cover, 0, "");
        Assert.NotNull(entry);
        Assert.Equal("", entry!.Value.Name);
        Assert.Same(cover, entry.Value.Cover);
    }

    [Fact]
    public void PinEntry_NeitherResolved_IsNull()
        => Assert.Null(SidebarBinderPipeline.PinEntry(SidebarEntryKind.Playlist, "", null, 0, ""));

    [Fact]
    public void ResolveUnlistedPin_MergesAPartiallyResolvedCard_CoverWithoutAName_OverTheBaseEntry()
    {
        var pin = new SidebarPin("pl:x", SidebarEntryKind.Playlist, "spotify:playlist:x", "Cached Name", 100);
        var cover = new Image("https://example/cover.jpg");
        // The card resolved a COVER but not a NAME yet — PinEntry now keeps it instead of discarding it.
        var partial = SidebarBinderPipeline.PinEntry(SidebarEntryKind.Playlist, "", cover, 12, "");

        var merged = SidebarBinderPipeline.ResolveUnlistedPin(pin, 0, partial);

        // Cover merges in from the partial card; Name falls back to the pin's own cache (never blanked).
        Assert.Same(cover, merged.Cover);
        Assert.Equal("Cached Name", merged.Name);
        Assert.Equal(12, merged.ChildCount);
    }

    [Fact]
    public void ResolveUnlistedPin_WithNoCardAtAll_RendersTheBaseEntry()
    {
        var pin = new SidebarPin("pl:x", SidebarEntryKind.Playlist, "spotify:playlist:x", "Cached Name", 100);
        var merged = SidebarBinderPipeline.ResolveUnlistedPin(pin, 0, null);
        Assert.Equal("Cached Name", merged.Name);
        Assert.Null(merged.Cover);
    }
}
