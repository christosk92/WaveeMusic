// ── Wavee.Tests/BootOrderingTests.cs — the route table after a real boot, and the three ordering chains that only
// live in App.cs's comments today ───────────────────────────────────────────────────────────────────────────────────
//
// App.cs used to call nine page-owner installs before the window existed; most of that is a page factory nothing
// needs until the user first navigates to that page (Shell.cs §1.3 moves Home.InstallPages, Album.InstallPages and
// Playlist.InstallPages onto Shell.PageFor's miss arm). The hazard the task brief calls out: the ordering constraints
// among the nine calls exist ONLY as comments, and nothing caught a break — so this file is written to stand on its
// own, unchanged, whether Shell.PageFor is the old dumb array read or the new miss-installs-once seam:
//
//   ROUTE_TABLE  every RouteKind resolves a page after a full (eager) boot, NotFound excepted — the exhaustive
//                enumeration ShellRouteTableTests already pins the SHAPE of (31 kinds); this pins that PageFor
//                actually answers non-null for every one of them once every owner has installed.
//   LAZY_GROUP   the NEW half: a miss on one kind in a lazy group installs the whole group once, not per kind, and
//                never re-installs on a later miss (the cheap hit path stays cheap).
//   CHAIN 1      Track.InstallActions() AFTER Queue.InstallUi(): AppActions.Register is first-wins per id, and Queue
//                and Track both register Play/PlayNext/AddToQueue — Queue's must win.
//   CHAIN 2      Artist.InstallPages() (which chains Concert.InstallPages) sets Sidebar.ConcertsFetch — an EAGER
//                install precisely because the sidebar pane's first mount, which happens before any navigation could
//                reach an Artist/Concert route, reads it.
//   CHAIN 3      Sidebar.Boot() before Sidebar.InstallActionSeams() before Spotify.Library.Install(): the library
//                host's own doc comment says why (it hands Sidebar.LibraryWrites its seam, and the pin bridge needs
//                the pin store Boot() loads) — and InstallActionSeams's IsPinned/SetPinned only answer correctly once
//                that store is live.
//
// No source-text tests: every fact below calls the real production Install* methods and reads real, live registry
// state (Shell.PageFor, AppActions.Find, the ActionServices seams, Sidebar's own pin store) — nothing here greps
// App.cs or Shell.cs.
//
// GLOBAL STATE, KEPT LOCAL: Shell's route table, AppActions and a handful of cross-cutting seams (Track.MenuSeams,
// Sidebar.ConcertsFetch, Sidebar.LibraryWrites, ActionServices) are process-wide statics with no reset of their own —
// that is why Diagnostics.Install() is deliberately NOT called here (besides its two SetPage calls it starts a real
// Windows network-cost subscription and a recurring background timer that would outlive this whole test process); its
// two routes are registered directly, through the exact same public factories it uses. Shell's page table is the one
// piece every other test in the suite can actually observe (ShellRouteTableTests asserts Settings starts unregistered)
// so it is snapshotted and restored around every fact; the rest are either idempotent by construction (AppActions'
// first-wins, the ??= seams) or reset explicitly at the top of the fact that depends on starting from null.

using System;
using System.IO;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public sealed class BootOrderingTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-boot-ordering-tests", Guid.NewGuid().ToString("n"));
    readonly Shell.PageFactory?[] _pagesSnapshot = Shell.SnapshotPagesForTests();

    SidebarLayoutStore FreshSidebarStore() => new(Path.Combine(_dir, "sidebar-layout.json"));

    public void Dispose()
    {
        Shell.RestorePagesForTests(_pagesSnapshot);
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    // ══ ROUTE_TABLE: every RouteKind resolves a page after a full boot, NotFound excepted ═══════════════════════════

    [Fact]
    public void Every_route_kind_resolves_a_page_after_a_full_boot_except_not_found()
    {
        TestScope.Fresh();

        // The nine App.cs page-owner installs, in App.cs's own order — six still eager there, three (Home, Album,
        // Playlist) not called here at all: their kinds are resolved below through Shell.PageFor itself, exercising
        // the SAME miss-installs-once seam a real navigation would.
        Shell.InstallUi();
        Modules.InstallUi();
        Queue.InstallUi();
        Track.InstallActions();
        Artist.InstallPages();
        Settings.InstallScreens();
        // Diagnostics.Install() itself is out of scope (see the file header) — its two routes, through its own public
        // factories:
        Shell.SetPage(Shell.RouteKind.PlaybackDiagnostics, static (in Shell.Route _) => Diagnostics.RuntimePage());
        Shell.SetPage(Shell.RouteKind.ConnectDiagnostics, static (in Shell.Route _) => Diagnostics.ConnectPage());

        // Exhaustive, not a hand-picked subset (the brief's explicit instruction): every kind, one arm each.
        for (int i = 0; i < Shell.RouteKindCount; i++)
        {
            var kind = (Shell.RouteKind)i;
            var route = new Shell.Route(kind);
            if (kind == Shell.RouteKind.NotFound)
            {
                // The one INTENTIONALLY factory-less kind: PageFor's null is what routes the frame to the not-found
                // arm (Shell.UI.cs's NotFoundPage), never a crash on a missing registration.
                Assert.Null(Shell.PageFor(route));
                continue;
            }
            Assert.True(Shell.PageFor(route) is not null, $"{kind} has no page after boot");
        }

        // Pinned by ShellRouteTableTests too — restated here because a drift in the count would silently narrow the
        // loop above into "most kinds", exactly what this guard exists to refuse. 31 since the podcast rework's
        // wave P2 appended RouteKind.Episode (plan §5.11) — its page resolves through the SAME Album.InstallPages
        // lazy-group miss the loop above already exercises via RouteKind.Album/Prerelease/Show.
        Assert.Equal(31, Shell.RouteKindCount);
    }

    // ══ LAZY_GROUP: a miss installs its whole group once, the hit path never re-installs ══════════════════════════

    [Fact]
    public void A_lazy_group_installs_on_the_first_miss_covers_every_kind_in_it_and_never_reinstalls()
    {
        // Reset explicitly rather than assume a fresh process: Track.MenuSeams.ViewCredits is a process-wide static
        // another fact in this same class may already have set.
        Track.MenuSeams.ViewCredits = null;

        Assert.NotNull(Shell.PageFor(new Shell.Route(Shell.RouteKind.Album)));
        // Album.InstallPages ran as a SIDE EFFECT of that one miss — its other seam is proof the real install method
        // ran, not a stand-in.
        Assert.NotNull(Track.MenuSeams.ViewCredits);

        // The REST of the same group resolves too, from that one trigger — a group installs once, not per kind.
        Assert.NotNull(Shell.PageFor(new Shell.Route(Shell.RouteKind.Prerelease)));
        Assert.NotNull(Shell.PageFor(new Shell.Route(Shell.RouteKind.Show)));

        // Re-asking is the (common) hit path, never a second install: clobber the seam Album.InstallPages sets and
        // ask again — a re-install would silently repair it, and it must not.
        Track.MenuSeams.ViewCredits = null;
        Assert.NotNull(Shell.PageFor(new Shell.Route(Shell.RouteKind.Album)));
        Assert.Null(Track.MenuSeams.ViewCredits);
    }

    // ══ CHAIN 1: Track.InstallActions() AFTER Queue.InstallUi(), so Queue's registrations win ═════════════════════

    [Fact]
    public void Queue_InstallUi_before_Track_InstallActions_means_queues_registrations_win_the_shared_ids()
    {
        Queue.InstallUi();
        var queuesPlay = AppActions.Find(ActionId.Play);
        var queuesPlayNext = AppActions.Find(ActionId.PlayNext);
        var queuesAddToQueue = AppActions.Find(ActionId.AddToQueue);
        Assert.NotNull(queuesPlay);
        Assert.NotNull(queuesPlayNext);
        Assert.NotNull(queuesAddToQueue);

        Track.InstallActions();   // the documented order (App.cs's own comment): AFTER Queue.InstallUi

        // AppActions.Register is first-wins per id: Track's attempt to register the SAME three ids is refused, so the
        // table still holds the EXACT instances Queue.InstallUi created (not merely equal ones).
        Assert.Same(queuesPlay, AppActions.Find(ActionId.Play));
        Assert.Same(queuesPlayNext, AppActions.Find(ActionId.PlayNext));
        Assert.Same(queuesAddToQueue, AppActions.Find(ActionId.AddToQueue));

        // And Track's OWN ids — nothing Queue also registers — land normally, proving InstallActions genuinely ran
        // rather than silently no-oping altogether.
        Assert.NotNull(AppActions.Find(ActionId.ToggleLike));
        Assert.NotNull(AppActions.Find(ActionId.ViewCredits));
    }

    // ══ CHAIN 2: Artist.InstallPages() before the sidebar pane's first mount ═══════════════════════════════════════

    [Fact]
    public void Artist_InstallPages_sets_the_sidebar_concerts_seam_the_pane_reads_at_its_first_mount()
    {
        // Reset explicitly: Sidebar.ConcertsFetch is a process-wide static another fact may already have set, and the
        // whole point of this fact is that Artist.InstallPages is what sets it — not a coincidence of run order.
        Sidebar.ConcertsFetch = null;
        Assert.Null(Sidebar.ConcertsFetch);

        Artist.InstallPages();

        Assert.NotNull(Sidebar.ConcertsFetch);
        // This is exactly why Artist.InstallPages stays EAGER in App.cs rather than moving to Shell.PageFor's lazy
        // groups with Home/Album/Playlist: the sidebar pane mounts as part of Shell.Run(), before any navigation
        // could reach an Artist or Concert route, so a lazy trigger keyed on THOSE routes would run too late.
    }

    // ══ CHAIN 3: Sidebar.Boot() → Sidebar.InstallActionSeams() → Spotify.Library.Install() ════════════════════════

    [Fact]
    public void Sidebar_boot_then_action_seams_then_library_install_wires_a_working_pin_bridge()
    {
        TestScope.Fresh();
        Sidebar.UseStore(FreshSidebarStore());

        Sidebar.Boot();                 // "design, pane state, the layout document and pins" — loads the pin store
        Sidebar.InstallActionSeams();   // Actions.Services.IsPinned / SetPinned
        Spotify.Library.Install();      // "after Sidebar.Boot, before the pane mounts" — the pin bridge + the seam

        // Spotify.Library.Install's own contract: it hands the sidebar its ONE library-write seam.
        Assert.Same(Spotify.Library.Writes, Sidebar.LibraryWrites);

        // InstallActionSeams's two seams only answer correctly because Sidebar.Boot already loaded a live (here,
        // empty) pin store for them to read — a fresh route starts unpinned...
        var liked = new Shell.Route(Shell.RouteKind.Liked);
        Assert.NotNull(Actions.Services.IsPinned);
        Assert.False(Actions.Services.IsPinned!(liked));

        // ...and the SAME seam reflects a pin the moment the store it depends on carries one, proving the seam reads
        // Sidebar's LIVE state rather than a snapshot taken before Boot() ran.
        Assert.True(Sidebar.Pin(new SidebarPin("liked", SidebarEntryKind.AppRoute, "", "Liked Songs", AddedAtMs: 1)));
        Assert.True(Actions.Services.IsPinned!(liked));

        // Flush the pin's debounced write now, while the temp directory this fact pointed Sidebar.UseStore at still
        // exists — Dispose deletes it right after this fact returns (only THIS fact touches the store at all).
        Sidebar.Shutdown();
    }
}
