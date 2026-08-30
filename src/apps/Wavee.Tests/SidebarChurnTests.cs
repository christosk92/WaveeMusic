using System;
using System.Collections.Generic;
using Wavee.Core.Sidebar;
using Xunit;

namespace Wavee.Tests;

// THE THREE MECHANISMS THAT STOPPED THE SIDEBAR RE-RENDERING ITSELF (Wave 3 of the perf plan).
//
// A live diag session measured, at idle and on every track boundary/navigation, the SAME three storms:
//   • SidebarPaneSlot×52 + SidebarSelectionPill×44 + SidebarChevron×6 on every publish, because
//     `SidebarPane.PublishStage`'s wholesale `!ReferenceEquals(stage.Document, Doc)` test always tripped — Classic minted
//     a fresh document on every pane render, so the landed per-row `SidebarRowDiff` was dead code;
//   • two to three publishes per boundary/nav to begin with, because `SidebarEntries.Publish` bumped its version on
//     every projection rebuild even when the rebuild reproduced the same rows;
//   • the whole realized window again on every navigation, because selection was a raw route-signal read inside every
//     slot, every pill and the rail.
//
// All three fixes are pure decisions, deliberately split into `Features/Sidebar/Data/` (and, for Classic's cache,
// alongside its already-testable built-in document) so they are pinned HERE against production code rather than a copy.
// Getting them too EAGER is only a perf regression; too LAZY is a correctness bug — a stale row, a frozen sidebar, a
// pill left on the page you navigated away from — which is exactly what each direction below asserts.
public sealed class SidebarChurnTests
{
    // ── F3a: Classic's document instance is stable while its collapse flags are ───────────────────────────────────────

    [Fact]
    public void ClassicDocument_IsTheSameInstance_WhileTheCollapseFlagsAreUnchanged()
    {
        var cache = new ClassicDocumentCache();
        var first = cache.Get(true, true, true);
        Assert.Same(first, cache.Get(true, true, true));
        Assert.Same(first, cache.Get(true, true, true));   // the pane calls this on EVERY render
    }

    [Fact]
    public void ClassicDocument_IsRebuilt_OnEveryFlagFlip()
    {
        var cache = new ClassicDocumentCache();
        var open = cache.Get(true, true, true);

        var pinnedClosed = cache.Get(false, true, true);
        Assert.NotSame(open, pinnedClosed);
        Assert.True(pinnedClosed.Find(SidebarBuiltInDocuments.PinnedId)!.Collapsed);

        var libraryClosed = cache.Get(true, false, true);
        Assert.NotSame(pinnedClosed, libraryClosed);
        Assert.True(libraryClosed.Find(SidebarBuiltInDocuments.LibraryId)!.Collapsed);

        var playlistsClosed = cache.Get(true, true, false);
        Assert.NotSame(libraryClosed, playlistsClosed);
        Assert.True(playlistsClosed.Find(SidebarBuiltInDocuments.PlaylistsId)!.Collapsed);

        // …and flipping back is a rebuild too (one slot, not a table) — the point is instance stability per STATE, not a
        // memo of every state ever seen.
        Assert.NotSame(open, cache.Get(true, true, true));
    }

    [Fact]
    public void ClassicFlagBitmask_IsOnePerFlagCombination()
    {
        // The pane's mode epoch folds the same three flags with the same bitmask. If these two ever disagreed, a toggle
        // would re-plan against a document the cache decided not to rebuild.
        var seen = new HashSet<int>();
        for (int i = 0; i < 8; i++)
            Assert.True(seen.Add(ClassicDocumentCache.FlagsOf((i & 1) != 0, (i & 2) != 0, (i & 4) != 0)));
        Assert.Equal(0, ClassicDocumentCache.FlagsOf(false, false, false));
        Assert.Equal(7, ClassicDocumentCache.FlagsOf(true, true, true));
    }

    // ── F3b: the projection publish is content-gated ──────────────────────────────────────────────────────────────────

    static SidebarEntriesMeta Meta(int state = 0, Exception? error = null, bool pending = false,
                                   bool qualifiers = false, int pinCount = 0)
        => new(state, error, pending, qualifiers, pinCount);

    static SidebarLibraryEntry Playlist(string id, string name = "n", int childCount = 0,
                                        IReadOnlyList<string>? mosaic = null)
        => new(id, SidebarEntryKind.Playlist, "spotify:playlist:" + id, name, "", null, mosaic, childCount,
               0, 0, 0, 0, 0, false, SidebarPlaylistFlavor.None);

    [Fact]
    public void FirstPublish_AlwaysCounts_AsAChange()
    {
        var shadow = new SidebarEntriesShadow();
        Assert.True(shadow.Publish(Array.Empty<SidebarLibraryEntry>(), Meta()));
        // …and an identical EMPTY republish does not (an empty library still must not storm).
        Assert.False(shadow.Publish(Array.Empty<SidebarLibraryEntry>(), Meta()));
    }

    [Fact]
    public void AnIdenticalRepublish_IsNotAChange()
    {
        var shadow = new SidebarEntriesShadow();
        var rows = new List<SidebarLibraryEntry> { Playlist("a"), Playlist("b"), Playlist("c") };
        Assert.True(shadow.Publish(rows, Meta(pinCount: 1)));

        // The producer refills the SAME buffer in place, so the republish is a fresh list with equal content — which is
        // exactly the shape the binder pump produces on a queue/track/play-log tick.
        var again = new List<SidebarLibraryEntry> { Playlist("a"), Playlist("b"), Playlist("c") };
        Assert.False(shadow.Publish(again, Meta(pinCount: 1)));
        Assert.False(shadow.Publish(again, Meta(pinCount: 1)));
    }

    [Fact]
    public void AnyRowDelta_IsAChange()
    {
        var shadow = new SidebarEntriesShadow();
        var rows = new List<SidebarLibraryEntry> { Playlist("a"), Playlist("b") };
        Assert.True(shadow.Publish(rows, Meta()));

        Assert.True(shadow.Publish(new List<SidebarLibraryEntry> { Playlist("a") }, Meta()));                 // count
        Assert.True(shadow.Publish(new List<SidebarLibraryEntry> { Playlist("a"), Playlist("b") }, Meta()));  // count back
        Assert.True(shadow.Publish(new List<SidebarLibraryEntry> { Playlist("b"), Playlist("a") }, Meta()));  // order
        Assert.True(shadow.Publish(new List<SidebarLibraryEntry>
        {
            Playlist("b"), Playlist("a", name: "renamed"),
        }, Meta()));                                                                                          // a member
        Assert.True(shadow.Publish(new List<SidebarLibraryEntry>
        {
            Playlist("b"), Playlist("a", name: "renamed", childCount: 12),
        }, Meta()));                                                                                          // another
        // …and the settled content is quiet again.
        Assert.False(shadow.Publish(new List<SidebarLibraryEntry>
        {
            Playlist("b"), Playlist("a", name: "renamed", childCount: 12),
        }, Meta()));
    }

    [Fact]
    public void AnyMetaDelta_IsAChange_EvenWithIdenticalRows()
    {
        var shadow = new SidebarEntriesShadow();
        var rows = new List<SidebarLibraryEntry> { Playlist("a") };
        Assert.True(shadow.Publish(rows, Meta()));

        Assert.True(shadow.Publish(rows, Meta(state: 1)));                 // LoadState
        Assert.True(shadow.Publish(rows, Meta(state: 1, pending: true)));  // AnyContributingKindPending
        Assert.True(shadow.Publish(rows, Meta(state: 1, pending: true, qualifiers: true)));
        Assert.True(shadow.Publish(rows, Meta(state: 1, pending: true, qualifiers: true, pinCount: 3)));
        Assert.False(shadow.Publish(rows, Meta(state: 1, pending: true, qualifiers: true, pinCount: 3)));

        // Error compares by REFERENCE: a new failure is a new publish even when its message repeats.
        var boom = new InvalidOperationException("boom");
        Assert.True(shadow.Publish(rows, Meta(state: 2, error: boom, pending: true, qualifiers: true, pinCount: 3)));
        Assert.False(shadow.Publish(rows, Meta(state: 2, error: boom, pending: true, qualifiers: true, pinCount: 3)));
        Assert.True(shadow.Publish(rows, Meta(state: 2, error: new InvalidOperationException("boom"),
                                              pending: true, qualifiers: true, pinCount: 3)));
    }

    [Fact]
    public void MosaicTiles_CompareByValue_SoAFolderyLibraryStillSettles()
    {
        // SidebarProjection materializes a folder's tile list FRESH on every rebuild, so a reference compare would
        // report every folder row as changed and the gate would never fire for a library that has folders.
        var shadow = new SidebarEntriesShadow();
        Assert.True(shadow.Publish(new List<SidebarLibraryEntry>
        {
            Playlist("a", mosaic: new List<string> { "u1", "u2" }),
        }, Meta()));
        Assert.False(shadow.Publish(new List<SidebarLibraryEntry>
        {
            Playlist("a", mosaic: new List<string> { "u1", "u2" }),   // equal by value, different instance
        }, Meta()));
        Assert.True(shadow.Publish(new List<SidebarLibraryEntry>
        {
            Playlist("a", mosaic: new List<string> { "u1", "u3" }),   // a real cover change
        }, Meta()));
        Assert.True(shadow.Publish(new List<SidebarLibraryEntry>
        {
            Playlist("a", mosaic: null),                             // and losing the mosaic entirely
        }, Meta()));
    }

    [Fact]
    public void ThePublishedShadow_MirrorsTheLastAcceptedRebuild()
    {
        var shadow = new SidebarEntriesShadow();
        shadow.Publish(new List<SidebarLibraryEntry> { Playlist("a"), Playlist("b") }, Meta());
        Assert.Equal(2, shadow.Published.Count);
        Assert.Equal("a", shadow.Published[0].Id);

        // A skipped bump must not corrupt the snapshot the NEXT compare runs against.
        shadow.Publish(new List<SidebarLibraryEntry> { Playlist("a"), Playlist("b") }, Meta());
        Assert.Equal(2, shadow.Published.Count);
        Assert.False(shadow.Publish(new List<SidebarLibraryEntry> { Playlist("a"), Playlist("b") }, Meta()));
    }

    static SidebarLibraryEntry Folder(string id, string name = "n")
        => new(id, SidebarEntryKind.Folder, "", name, "", null, null, 0, 0, 0, 0, 0, 0, false,
              SidebarPlaylistFlavor.None);

    [Fact]
    public void HiddenFolderChildHydration_ChangesTheFullProjection_NotThePublishedRows()
    {
        // The published buffer is folder-collapse-filtered (a collapsed folder's children are omitted), but the
        // planner reads the binder's FULL flattened projection, which always includes them. A hydration that only
        // touches a child living inside a collapsed folder must therefore trip the FULL shadow even though the
        // published shadow — the ONLY gate before this change — sees no difference at all. This is exactly the
        // decision `SidebarEntries.Publish` now ORs together: the published gate alone would have left the pane
        // un-re-planned, drawing the child's stale raw-URI/"0 songs" row forever.
        var folder = Folder("f1");
        var childBefore = Playlist("c1", name: "spotify:playlist:c1", childCount: 0);
        var childAfter = Playlist("c1", name: "Road Trip", childCount: 42);

        var published = new List<SidebarLibraryEntry> { folder };                    // collapsed → child omitted
        var full = new List<SidebarLibraryEntry> { folder, childBefore };

        var publishedShadow = new SidebarEntriesShadow();
        var fullShadow = new SidebarEntriesShadow();
        Assert.True(publishedShadow.Publish(published, Meta()));   // first publish always counts
        Assert.True(fullShadow.Publish(full, default));

        // Hydrate the child inside the collapsed folder. The published rows don't change at all…
        var publishedAgain = new List<SidebarLibraryEntry> { folder };
        Assert.False(publishedShadow.Publish(publishedAgain, Meta()));

        // …but the full projection does, which is exactly what the pane needs to re-plan.
        var fullAgain = new List<SidebarLibraryEntry> { folder, childAfter };
        Assert.True(fullShadow.Publish(fullAgain, default));
    }

    // ── F3c: the selection sweep ──────────────────────────────────────────────────────────────────────────────────────

    const string Sec = "sec";

    static SidebarRow Row(SidebarRowKind kind, string key, int entryIndex = -1, int itemCount = 0)
        => new(kind, Sec, 0, entryIndex, itemCount, key);

    static SidebarSectionSpec Section(params SidebarItemSpec[] items)
        => new(Sec, SidebarSectionKind.CustomGroup, Items: items);

    static List<int> Sweep(IReadOnlyList<SidebarRow> rows, IReadOnlyList<SidebarLibraryEntry> entries,
                           SidebarSectionSpec section, string route)
    {
        var into = new List<int>();
        SidebarRowResolve.Sweep(rows, entries, _ => section, route, into);
        return into;
    }

    static List<int> Flipped(IReadOnlyList<int> previous, IReadOnlyList<int> next)
    {
        var into = new List<int>();
        SidebarRowResolve.Flipped(previous, next, into);
        return into;
    }

    /// <summary>An ENTITY ROW, a GRID STRIP over three cells, a hand-placed ROUTE row, and chrome — the shapes the pane
    /// actually draws a selection cue on, plus the ones it must never bump.</summary>
    static (SidebarRow[] Rows, SidebarLibraryEntry[] Entries, SidebarSectionSpec Section) Plan()
    {
        var entries = new[]
        {
            Playlist("pl:one"),      // 0 — the entity row
            Playlist("pl:two"),      // 1 ┐
            Playlist("pl:three"),    // 2 ├ the grid strip
            Playlist("pl:four"),     // 3 ┘
        };
        var rows = new[]
        {
            Row(SidebarRowKind.SectionHeader, Sec),                          // 0 — chrome, never selected
            Row(SidebarRowKind.EntityRow, "pl:one", entryIndex: 0),          // 1
            Row(SidebarRowKind.IconRow, "liked"),                            // 2 — a hand-placed route
            Row(SidebarRowKind.GridStrip, Sec, entryIndex: 1, itemCount: 3), // 3 — pl:two/three/four
            Row(SidebarRowKind.Divider, Sec),                                // 4 — chrome
        };
        var section = Section(new SidebarItemSpec("i:liked", SidebarItemTarget.Route, "liked"));
        return (rows, entries, section);
    }

    [Fact]
    public void TheSweep_FindsTheRowThatDrawsTheCue_ForEveryKindThatHasOne()
    {
        var (rows, entries, section) = Plan();
        Assert.Equal(new[] { 1 }, Sweep(rows, entries, section, "pl:one"));
        Assert.Equal(new[] { 2 }, Sweep(rows, entries, section, "liked"));
        // A grid strip is ONE plan row over several cells, so any cell in range selects the row (that is the unit a
        // per-row epoch can address).
        Assert.Equal(new[] { 3 }, Sweep(rows, entries, section, "pl:two"));
        Assert.Equal(new[] { 3 }, Sweep(rows, entries, section, "pl:four"));
        Assert.Empty(Sweep(rows, entries, section, "somewhere-else"));
        Assert.Empty(Sweep(rows, entries, section, ""));
    }

    [Fact]
    public void ARouteChange_FlipsExactlyTheOldAndNewMatchingRows()
    {
        var (rows, entries, section) = Plan();

        var atEntity = Sweep(rows, entries, section, "pl:one");
        var atGrid = Sweep(rows, entries, section, "pl:three");
        var atRail = Sweep(rows, entries, section, "liked");

        Assert.Equal(new[] { 1, 3 }, Flipped(atEntity, atGrid));   // entity row out, grid strip in
        Assert.Equal(new[] { 2, 3 }, Flipped(atGrid, atRail));     // grid strip out, route row in
        Assert.Equal(new[] { 1, 2 }, Flipped(atRail, atEntity));   // route row out, entity row in

        // Navigating somewhere the sidebar does not show only retires the outgoing row; arriving only lights the new one.
        Assert.Equal(new[] { 1 }, Flipped(atEntity, new List<int>()));
        Assert.Equal(new[] { 1 }, Flipped(new List<int>(), atEntity));

        // A row that stays selected is NOT bumped — a republish that did not move it must not re-render it.
        Assert.Empty(Flipped(atEntity, Sweep(rows, entries, section, "pl:one")));

        // …and moving WITHIN one grid strip is invisible: the strip row draws both cells, so nothing flipped at the row
        // level even though the selected cell moved.
        Assert.Empty(Flipped(Sweep(rows, entries, section, "pl:two"), Sweep(rows, entries, section, "pl:four")));
    }

    [Fact]
    public void ARowThatCanNeverBeSelected_IsNeverSwept()
    {
        var entries = new[] { Playlist("pl:one") };
        var section = Section(
            new SidebarItemSpec("i:act", SidebarItemTarget.Action, "wavee.play"),
            new SidebarItemSpec("i:trk", SidebarItemTarget.Track, "spotify:track:t"));

        // An ACTION item never selects even when the planner resolved an entry onto its row (the slot returns an
        // ActionRow before it ever reaches the entity branch).
        Assert.Empty(Sweep(new[] { Row(SidebarRowKind.IconRow, "wavee.play", entryIndex: 0) }, entries, section, "pl:one"));
        // A hand-placed TRACK plays; it has no route to be selected by.
        Assert.Empty(Sweep(new[] { Row(SidebarRowKind.IconRow, "spotify:track:t") }, entries, section, "spotify:track:t"));
        // Folders, chrome and unresolved retention rows draw no cue.
        foreach (var kind in new[]
        {
            SidebarRowKind.FolderHeader, SidebarRowKind.SectionHeader, SidebarRowKind.HeaderLabel,
            SidebarRowKind.Divider, SidebarRowKind.Empty, SidebarRowKind.Skeleton,
            SidebarRowKind.PromptRow,
        })
            Assert.Empty(Sweep(new[] { Row(kind, "pl:one", entryIndex: 0) }, entries, section, "pl:one"));
        Assert.Empty(Sweep(new[] { Row(SidebarRowKind.EntityRow, "pl:missing") }, entries, Section(), "pl:missing"));
    }

    [Fact]
    public void AGridStripOutOfRange_IsClampedNotThrown()
    {
        // A slot can transiently address a plan the entries no longer back (the count signal lands one hop after the
        // plan), and the sweep runs on the pane's thread against whatever pair is current.
        var entries = new[] { Playlist("pl:one") };
        Assert.Empty(Sweep(new[] { Row(SidebarRowKind.GridStrip, Sec, entryIndex: 0, itemCount: 9) },
                           entries, Section(), "pl:nine"));
        Assert.Equal(new[] { 0 }, Sweep(new[] { Row(SidebarRowKind.GridStrip, Sec, entryIndex: 0, itemCount: 9) },
                                        entries, Section(), "pl:one"));
        Assert.Empty(Sweep(new[] { Row(SidebarRowKind.GridStrip, Sec, entryIndex: 4, itemCount: 2) },
                           entries, Section(), "pl:one"));
    }

    [Fact]
    public void AnEntityCard_SelectsOnItsEntityRoute()
    {
        var entries = new[] { Playlist("pl:card") };
        Assert.Equal(new[] { 0 },
            Sweep(new[] { Row(SidebarRowKind.EntityCard, "pl:card", entryIndex: 0) }, entries, Section(), "pl:card"));
        // An UNRESOLVED card falls back to the pin route derived from its (empty) uri — i.e. nothing.
        Assert.Empty(Sweep(new[] { Row(SidebarRowKind.EntityCard, "pl:card") }, entries, Section(), "pl:card"));
    }

    // ── F3d: the pill's opacity is a BOUND read, so it can never be stale (#22/#23 — two selection pills) ─────────────
    //
    // The defect: two rows drew the left accent pill at once — the route-selected row plus either the previously opened
    // route's row or, once its node had been recycled, the NOW-PLAYING row. `SidebarSelectionPill` rendered
    // `Opacity = selected ? 1f : 0f` as a MOUNT-TIME literal off the slot's snapshot while the pane's moving-pill
    // transaction wrote the same node's opacity channel directly, so any write that lit a node the row's own state
    // called dark simply stuck. The opacity is now derived from the slot's LIVE state on every read, through the same
    // one owner the selection sweep uses. These drive that production rule directly.

    /// <summary>The pill probe, wired EXACTLY as <c>SidebarPaneSlot.PillState</c> wires it: the slot's snapshot
    /// (route/indent/top, taken at render time) re-derived against the live route AND the pane's row-level verdict
    /// (<see cref="SidebarRowResolve.SelectsRoute"/>). Everything reactive about it — the index signal, the row epoch —
    /// only decides WHEN it is read; what it returns is this.</summary>
    sealed class PillProbe
    {
        readonly IReadOnlyList<SidebarRow> _rows;
        readonly IReadOnlyList<SidebarLibraryEntry> _entries;
        readonly SidebarSectionSpec _section;
        readonly Func<string> _liveRoute;
        readonly int _index;
        readonly SidebarPillState _snapshot;   // the SLOT writes this on ITS render; the pill never re-mounts for it

        public PillProbe(IReadOnlyList<SidebarRow> rows, IReadOnlyList<SidebarLibraryEntry> entries,
                         SidebarSectionSpec section, int index, string? route, Func<string> liveRoute)
        {
            _rows = rows; _entries = entries; _section = section; _index = index; _liveRoute = liveRoute;
            // The mount-time snapshot deliberately carries the WRONG Selected (the frozen literal's whole problem):
            // nothing downstream may depend on it.
            _snapshot = new SidebarPillState(route, Selected: false, Indent: 0f, Top: 14f);
            Mounts++;
        }

        public int Mounts { get; private set; }

        public SidebarPillState Read()
        {
            string live = _liveRoute();
            var row = _rows[_index];
            return _snapshot.For(live, SidebarRowResolve.SelectsRoute(in row, _entries, _section, live));
        }

        public float Opacity => Read().Opacity;
    }

    /// <summary>Two entity rows plus a now-playing row — the shape both screenshots showed.</summary>
    static (SidebarRow[] Rows, SidebarLibraryEntry[] Entries, SidebarSectionSpec Section) PillPlan()
    {
        var entries = new[] { Playlist("pl:a"), Playlist("pl:b"), Playlist("pl:playing") };
        var rows = new[]
        {
            Row(SidebarRowKind.EntityRow, "pl:a", entryIndex: 0),
            Row(SidebarRowKind.EntityRow, "pl:b", entryIndex: 1),
            Row(SidebarRowKind.EntityRow, "pl:playing", entryIndex: 2),
        };
        return (rows, entries, Section());
    }

    [Fact]
    public void ARouteMove_DarkensTheOldPill_AndLightsTheNewOne_WithoutARemount()
    {
        var (rows, entries, section) = PillPlan();
        string route = "pl:a";
        var a = new PillProbe(rows, entries, section, 0, "pl:a", () => route);
        var b = new PillProbe(rows, entries, section, 1, "pl:b", () => route);

        Assert.True(a.Read().Selected);
        Assert.Equal(SidebarPillState.LitOpacity, a.Opacity);
        Assert.False(b.Read().Selected);
        Assert.Equal(SidebarPillState.DarkOpacity, b.Opacity);

        // The pane's ONE route read moves the selection from row A to row B (RefreshSelection bumps exactly these two).
        route = "pl:b";
        Assert.Equal(new[] { 0, 1 }, Flipped(Sweep(rows, entries, section, "pl:a"),
                                             Sweep(rows, entries, section, "pl:b")));

        // …and the OLD row is dark on its very next read — no re-render, no transaction, no remount required.
        Assert.False(a.Read().Selected);
        Assert.Equal(SidebarPillState.DarkOpacity, a.Opacity);
        Assert.True(b.Read().Selected);
        Assert.Equal(SidebarPillState.LitOpacity, b.Opacity);
        Assert.Equal(1, a.Mounts);
        Assert.Equal(1, b.Mounts);

        // Navigating somewhere the sidebar cannot show leaves EVERY pill dark — never the last-lit one.
        route = "settings";
        Assert.Equal(SidebarPillState.DarkOpacity, a.Opacity);
        Assert.Equal(SidebarPillState.DarkOpacity, b.Opacity);
    }

    [Fact]
    public void ExactlyOnePillIsLit_AndNeverThePlayingRow()
    {
        var (rows, entries, section) = PillPlan();
        string route = "pl:a";
        var probes = new[]
        {
            new PillProbe(rows, entries, section, 0, "pl:a", () => route),
            new PillProbe(rows, entries, section, 1, "pl:b", () => route),
            // Row 2 is the NOW-PLAYING row (the ||| glyph). Playback is not an input to the pill at all — which is the
            // point: there is no argument by which it could ever light one.
            new PillProbe(rows, entries, section, 2, "pl:playing", () => route),
        };

        foreach (string open in new[] { "pl:a", "pl:b", "pl:playing", "settings", "" })
        {
            route = open;
            int lit = 0;
            for (int i = 0; i < probes.Length; i++) if (probes[i].Read().Selected) lit++;
            Assert.True(lit <= 1);
            // The playing row's pill is lit ONLY when that playlist is also the open route — and then it is the open
            // route's pill, drawn on a row that also happens to be playing. One cue, one meaning.
            Assert.Equal(string.Equals(open, "pl:playing", StringComparison.Ordinal), probes[2].Read().Selected);
        }
    }

    [Fact]
    public void ThePillRule_IsRouteIdentity_AndAgreesWithTheSweep()
    {
        Assert.True(SidebarPillState.Lit("pl:a", "pl:a"));
        Assert.False(SidebarPillState.Lit("pl:a", "pl:A"));      // ordinal: a route key is an id, not display text
        Assert.False(SidebarPillState.Lit("pl:a", ""));          // no route open ⇒ nothing is lit
        Assert.False(SidebarPillState.Lit("", "pl:a"));          // a folder/track/chrome row has no route
        Assert.False(SidebarPillState.Lit(null, "pl:a"));

        // …and it is the SAME verdict the entity rule gives, so the pill can never disagree with the plate under it.
        var entry = Playlist("pl:a");
        Assert.Equal(SidebarRowResolve.EntrySelects(in entry, "pl:a"),
                     SidebarPillState.Lit(entry.RouteKey, "pl:a"));
        Assert.Equal(SidebarRowResolve.EntrySelects(in entry, "pl:b"),
                     SidebarPillState.Lit(entry.RouteKey, "pl:b"));

        // Opacity is DERIVED from the verdict — the two values the bound channel may ever write.
        Assert.Equal(1f, new SidebarPillState("pl:a", true, 0f, 0f).Opacity);
        Assert.Equal(0f, new SidebarPillState("pl:a", false, 0f, 0f).Opacity);
        // The pane's verdict is a veto: a snapshot whose route matches is still dark when the ROW does not select.
        Assert.False(new SidebarPillState("pl:a", true, 0f, 0f).For("pl:a", rowSelectsRoute: false).Selected);
        Assert.True(new SidebarPillState("pl:a", false, 0f, 0f).For("pl:a", rowSelectsRoute: true).Selected);
    }
}
