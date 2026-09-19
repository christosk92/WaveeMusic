// ── Spotify/Spotify.Library.cs ────────────────────────────────────────────────────────────────────────────────────────
// the library host: the login-time sync, dealer pushes, the collection writes behind User.Dispatch, the sidebar's
// library write seam, the ylpin pin bridge, and the pre-save drop scheduler's attach
//
// Role: SHELL
// Owner: F (L3: the list pushes, the reconnect's list revalidation, the planner's Online gate — wave D3)
// Wave: gap batch B2; D3
// Budget: 1100 lines (the per-list half lives in Spotify.Library.Lists.cs, a named partial)
// Spec: gap register G-042 (sync), G-043, G-048, G-049, G-062, G-089; decision D11 ("a Spotify library host implements
//       Sidebar.LibraryWrites and the sync; App.cs installs it; pages never call Spotify.Api directly");
//       docs/plans/wavee/cache-integrity-and-playlist-diff-implementation.md §3.3 (dealer), §3.5 (the Online gate)
//
// NOTHING HERE DECIDES. When to sync, which relation a push names, which op a gesture is, whether a pin is swept, what
// a body's bytes are — all of that is `Spotify.Encode.cs` (CORE, tested). This file feeds those rules values off the
// session and the tables, sends what they build through `Spotify.Api`, and posts the answers back (C1, C9):
//
//     Spotify.Status ─┐                         ┌─ RefreshEdge(rootlist, pins, liked, artists, shows, recents)
//     ScopeEpoch ─────┼─ WatchSession ─ Decide ─┤    (the provider walks them; the commit lands them)
//                     │                         └─ ReleaseDrops.Attach (G-089)
//     edges.Changed ──┴─ WatchEdges ─ AfterPublish ─ paging · pins → LibraryPinSync · rootlist rows · owned caps · drops
//     dealer push ─── OnDealerPush ─ Classify ─┬─ collections, rootlist: 250 ms settle ─ RefreshEdge (rootlist: by head)
//                                              └─ one playlist: DecodePush ─ post ─ ListPush.Decide ─ replay in place ·
//                                                 dirty · /diff now (Spotify.Library.Lists.cs)
//     Online ──────── the planner's gate opens (Fetch.CanSend) ─ Fetch.Pump: the held boot burst leaves as full batches
//     User.Like/Save/Follow ─ Dispatch ─ Api.CollectionAdd/Remove ─ post Settle
//     sidebar gesture ─ Writes.* ─ Encode ops ─ ApplyLocally + LandRootlist (optimistic) ─ Api.RootlistChanges ─ post
//
// WATCHING WITHOUT A COMPONENT. The session phase and the edge tables publish as engine signals, and this host is not
// in the render tree. It owns a private `ReactiveRuntime` whose two `Effect`s subscribe to those signals; the runtime's
// frame request POSTS one flush through `Spotify.Post` (the UI poster), and each effect only POSTS its work — so nothing
// runs inside a tracked computation, and nothing here runs off the UI thread except the blocking sends on the api
// workers (`Spotify.Api.Run`).
//
// WRITES ARE OPTIMISTIC, ANNOUNCED AFTER THE SERVER AGREES. A rootlist gesture lands the locally applied stream at once
// (the reply is bookkeeping only, so this IS the new tree) and toasts only on the 200; a refusal reads the rootlist back
// and says why. A collection write is `User.Add/Remove`'s pending edge (C6), settled here. Nothing is replayed after a
// failure or a restart (C6: the pending bit is the outbox).
//
// THE HELD HEAD UNDER AN OPTIMISTIC TREE (wave D3). Landing the tree FORGETS the rootlist's held revision
// (`LandRootlistTree` → `Entities.ForgetListRevision`): the tree is no longer the list that head describes, and a
// `/rootlist/diff` from it would replay our own ops over a tree that already holds them. The 200's head is held again
// only when it is provably that tree (`AdoptRootlistHead`, rule `OptimisticHead.MayAdopt`); otherwise, and after every
// refusal (`ReadRootlistBack`), the next rootlist ask is a full read. The disk keeps the pre-edit pair until then.

using InfoBarSeverity = FluentGpu.Controls.InfoBarSeverity;
using Loc = FluentGpu.Localization.Loc;

namespace Wavee;

/// <summary>The collection half of the optimistic write path (User.cs §5): the Spotify library host sends it.</summary>
public readonly partial struct User
{
    static partial void Dispatch(LibraryEdgeKind kind, bool add, int userSlot, int targetSlot, EntityId targetId)
        => Spotify.Library.Dispatch(kind, add, userSlot, targetSlot, targetId);
}

public static partial class Spotify
{
    /// <summary>The library host. SHELL; UI thread unless a member says otherwise. See the file header; the per-list
    /// section (dealer pushes for one list, the stamps, the reconnect's revalidation) is Spotify.Library.Lists.cs.</summary>
    public static partial class Library
    {
        // ══ 1. install ══════════════════════════════════════════════════════════════════════════════════════════════

        static bool s_installed;
        static FluentGpu.Signals.ReactiveRuntime? s_runtime;
        static FluentGpu.Signals.Effect? s_sessionWatch, s_edgeWatch;
        static bool s_flushPosted;
        static LibraryPinSync? s_pinSync;
        static LibrarySyncMemo s_syncMemo;
        static bool s_dropsAttached;

        static readonly Action s_flush = static () => { s_flushPosted = false; s_runtime?.Flush(); };
        static readonly Action s_syncNow = SyncNow;
        static readonly Action s_pumpFetch = Fetch.Pump;
        static bool s_wasOnline;
        static readonly Action s_afterPublish = AfterPublish;
        static readonly Action s_flushPushes = FlushPushes;
        static readonly Action s_pumpWaiters = PumpWaiters;
        static readonly Action s_settleProgress = static () =>
        {
            if (Entities.Current is { } scope) Telemetry.SettleProgressUnreachable(scope);
        };

        /// <summary>THE sidebar's library write seam (<see cref="Sidebar.LibraryWrites"/>), every member implemented.</summary>
        public static SidebarLibraryWrites Writes { get; } = new()
        {
            MoveRootlist = MoveRootlist,
            DepositTracks = DepositTracks,
            CreatePlaylist = CreatePlaylist,
            CreatePlaylistWith = CreatePlaylistWith,
            NewFolderWith = NewFolderWith,
            RenameFolder = RenameFolder,
            DeleteFolder = DeleteFolder,
            ResolveTracks = ResolveTracks,
        };

        /// <summary>Compose the host, once, on the UI thread, AFTER <c>Entities.Boot</c>, <c>Spotify.Boot</c> and
        /// <c>Sidebar.Boot</c> (the pin store must be loaded before the bridge takes its local-change hook) and BEFORE
        /// <c>Shell.InstallUi</c> (the pane reads the seam at mount). Idempotent. No request goes out here: the sync waits for
        /// the session to reach Online — and so does the planner (<see cref="Fetch.CanSend"/>, below).</summary>
        public static void Install()
        {
            if (s_installed) return;
            s_installed = true;
            // THE PLANNER'S ONLINE GATE (plan §3.5): a Spotify bucket is HELD — never dropped, never un-asked — until the
            // session is Online, so the boot burst leaves as a few full batches instead of as unauthenticated requests to
            // the fallback spclient that 401 and retry. The Online transition pumps (WatchSession). Local files, modules
            // and the fake catalog are never held; the disk leg is never gated.
            Fetch.CanSend = static p => p != EntityProvider.Spotify || Spotify.Current.IsOnline;
            Sidebar.LibraryWrites = Writes;
            s_pinSync = new LibraryPinSync(Sidebar.Pins, Platform.Settings, WritePin, IsPinWritePending,
                static id => string.Equals(id, "liked", StringComparison.Ordinal) ? Loc.Get("nav.likedSongs") : "");
            s_runtime = new FluentGpu.Signals.ReactiveRuntime { FrameRequested = RequestFlush };
            s_sessionWatch = new FluentGpu.Signals.Effect(s_runtime, WatchSession);
            s_edgeWatch = new FluentGpu.Signals.Effect(s_runtime, WatchEdges);
            Log.Info("library", "library host installed (sync on Online, planner held until Online, sidebar writes, pin bridge)");
        }

        /// <summary>The exit tail: stop watching, drop the timers, hand the pin store's hook back. Idempotent; UI thread.</summary>
        public static void Shutdown()
        {
            s_sessionWatch?.Dispose();
            s_edgeWatch?.Dispose();
            s_sessionWatch = s_edgeWatch = null;
            s_pushTimer?.Dispose();
            s_waiterTimer?.Dispose();
            s_pinSync?.Dispose();
            if (ReferenceEquals(Sidebar.LibraryWrites, Writes)) Sidebar.LibraryWrites = null;
            for (int i = 0; i < s_waiters.Count; i++) s_waiters[i].Done.TrySetResult([]);
            s_waiters.Clear();
            s_installed = false;
        }

        /// <summary>The api workers the host sends through, and the calls it makes — one place, so the network half is a
        /// value a test can replace and a reader can audit.</summary>
        public sealed class Transport
        {
            public Func<Action, bool> Run = Api.Run;
            public Func<string, LibraryEdgeKind, string, Api.Result> CollectionAdd =
                static (username, set, uri) => Api.CollectionAdd(username, set, [uri], CancellationToken.None);
            public Func<string, LibraryEdgeKind, string, Api.Result> CollectionRemove =
                static (username, set, uri) => Api.CollectionRemove(username, set, [uri], CancellationToken.None);
            public Func<string, Api.Result> Rootlist = static username => Api.Rootlist(username, CancellationToken.None);
            public Func<string, byte[], Api.Result> RootlistChanges =
                static (username, body) => Api.RootlistChanges(username, body, CancellationToken.None);
            public Func<string, byte[], Api.Result> PlaylistCreate =
                static (id, body) => Api.PlaylistCreate(id, body, CancellationToken.None);
            public Func<string, byte[], Api.Result> PlaylistChanges =
                static (id, body) => Api.PlaylistChanges(id, body, CancellationToken.None);
            public Func<string, Api.Result> Playlist = static id => Api.Playlist(id, CancellationToken.None);
        }

        /// <summary>The live transport.</summary>
        public static Transport Net { get; set; } = new();

        static void RequestFlush()
        {
            if (s_flushPosted) return;
            s_flushPosted = true;
            Post(s_flush);
        }

        /// <summary>The signed-in account's scope: a Spotify catalog scope whose account row resolved.</summary>
        static bool IsAccountScope(Scope? scope)
            => scope is not null && scope.Key.Provider == "spotify" && scope.MeSlot > Table.None && scope.Key.Account.Length > 0;

        /// <summary>A write may go out: an account scope, and a session that can send.</summary>
        static bool CanWrite(out Scope scope, out string username)
        {
            scope = Entities.Current;
            username = IsAccountScope(scope) ? scope.Key.Account : "";
            return username.Length > 0 && Current.IsOnline;
        }

        static long UnixNowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        static void Unavailable()
            => Notify.Say(Loc.Get("drag.libraryUnavailable"), InfoBarSeverity.Warning, dedupeKey: "library.unavailable");

        static void Announce(string text)
        {
            if (FluentGpu.Input.Announcer.IsAvailable) FluentGpu.Input.Announcer.SayThrottled(text);
        }

        // ══ 2. the login-time sync (G-042) ══════════════════════════════════════════════════════════════════════════

        /// <summary>Tracked: re-runs when the phase or the scope generation moves. Posts; never works inline. The
        /// transition INTO Online also opens the planner's gate: one pump, so what <see cref="Fetch.CanSend"/> held while
        /// the session was connecting leaves now — a held bucket is not a wake deadline, and an idle window would otherwise
        /// hold it until something unrelated ticked. A reconnect the sync's 30 s guard skips still pumps. A session that
        /// cannot reach Spotify never syncs, so it settles the podcast hydrate FAILED instead
        /// (<see cref="Telemetry.ProgressUnreachable"/>: Reconnecting or Failed — an offline launch included): without it the
        /// show page's visit head would wait on <see cref="Telemetry.ProgressSettled"/> for good (podcast plan §6.1).</summary>
        static void WatchSession()
        {
            var phase = Status.Value;
            uint scopeGeneration = Entities.ScopeEpoch.Value;
            bool online = phase == SessionPhase.Online;
            if (online && !s_wasOnline) Post(s_pumpFetch);
            s_wasOnline = online;
            if (Telemetry.ProgressUnreachable(phase)) Post(s_settleProgress);
            var verdict = LibrarySyncRules.Decide(ref s_syncMemo, online, IsAccountScope(Entities.Current),
                                                  Current.Epoch, scopeGeneration, Environment.TickCount64);
            if (verdict == LibrarySyncVerdict.Sync) Post(s_syncNow);
            else if (verdict == LibrarySyncVerdict.RateLimited)
                Log.Info("library", "reconnect resync skipped: the last sync of this scope is under 30 s old");
        }

        /// <summary>Ask for the whole library again. The provider walks each relation (the rootlist and recents through
        /// their held revisions, the collection sets page by page) and the commit lands them; the edge watch does the rest.
        /// Saved albums ride the shared <c>collection</c> walk the Liked ask pays for (Api's edge walk stages both), so
        /// they are not asked twice. A SECOND sync of the same scope is a reconnect: the lists on screen and the dirty ones
        /// are revalidated, every other stamp forgotten (<see cref="RevalidateListsAfterReconnect"/>).</summary>
        static void SyncNow()
        {
            var scope = Entities.Current;
            if (!IsAccountScope(scope) || !Current.IsOnline) return;
            // FIRST: whatever a 401/403 un-asked before the session authorised (an artist's top tracks, a page's rows)
            // is planned again now that it will be answered — the pages that asked have long since mounted and nothing
            // else re-asks for them (Fetch.Resume; the seed's SeedRetryOn is the same verdict) — and it leaves at once,
            // with whatever the Online gate was holding.
            Fetch.Resume();
            Fetch.Pump();
            int me = scope.MeSlot;
            Entities.RefreshEdge(FetchEdge.Rootlist, me, FetchPriority.Visible);
            Entities.RefreshEdge(FetchEdge.Pins, me, FetchPriority.Visible);
            Entities.RefreshEdge(FetchEdge.Liked, me, FetchPriority.Visible);
            Entities.RefreshEdge(FetchEdge.FollowedArtists, me, FetchPriority.Visible);
            Entities.RefreshEdge(FetchEdge.SavedShows, me, FetchPriority.Visible);
            Entities.RefreshEdge(FetchEdge.Recents, me, FetchPriority.Prefetch);
            Entities.Ensure(new User(me), UserFields.Identity, FetchPriority.Visible);
            // Podcast progress: ONE ListCurrentStates for every resume point touched in the last 180 days, folded into the
            // episode rows (podcast plan §5.8). Every sync of the scope — a reconnect catches up on what another device
            // played meanwhile, and keeps the show page's settled head (Telemetry.ProgressSettled) rather than resetting it.
            Telemetry.HydrateProgress(scope);
            SyncBans(scope);
            _ = Podcasts.ReadSavedAsync(CancellationToken.None);
            if (ReferenceEquals(scope, s_syncedScope)) RevalidateListsAfterReconnect(scope);
            s_syncedScope = scope;
            if (!s_dropsAttached)
            {
                s_dropsAttached = true;
                Notify.ReleaseDrops.Attach(ResolveDrops);          // G-089: reconciles once now, then on every saved-set change
            }
            Log.Info("library", "library sync asked: rootlist, pins, liked + albums, artists, shows, recents, podcast progress");
        }

        // ══ 3. the edge watch ═══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Tracked: every relation the host follows, read AFTER the scope generation so a switch re-subscribes to
        /// the new table set. Posts the work.</summary>
        static void WatchEdges()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            var e = scope.Edges;
            _ = e.Rootlist.Changed.Value;
            _ = e.Pins.Changed.Value;
            _ = e.Liked.Changed.Value;
            _ = e.SavedAlbums.Changed.Value;
            _ = e.FollowedArtists.Changed.Value;
            _ = e.SavedShows.Changed.Value;
            _ = e.AlbumTracks.Changed.Value;
            _ = e.PlaylistTracks.Changed.Value;
            _ = scope.Playlists.Changed.Value;
            Post(s_afterPublish);
        }

        static Scope? s_seenScope;
        static uint s_seenRootlist, s_seenPins, s_seenSavedAlbums, s_seenCapsRootlist, s_seenPlaylists;
        static readonly List<PinWire> s_pinWires = new();
        static int[] s_rowScratch = new int[64];

        static readonly FetchEdge[] s_pagedEdges =
            [FetchEdge.Liked, FetchEdge.SavedAlbums, FetchEdge.FollowedArtists, FetchEdge.SavedShows, FetchEdge.Pins];

        /// <summary>After a publication: finish a partial relation, converge the pins, keep the sidebar's rows and the
        /// owned playlists' capabilities asked, reconcile the pre-save drops, and finish any track resolve that was
        /// waiting. Each step runs only when the version it depends on moved.</summary>
        static void AfterPublish()
        {
            var scope = Entities.Current;
            if (!ReferenceEquals(scope, s_seenScope))
            {
                s_seenScope = scope;
                s_seenRootlist = s_seenPins = s_seenSavedAlbums = s_seenCapsRootlist = s_seenPlaylists = uint.MaxValue;
                // A scope switch — a sign-in, a sign-out, a market change: every freshness stamp described a list of the
                // scope that is gone (C7), and a stale "revalidated" would let a restored baseline paint unasked.
                ListStamps.ForgetAll();
                s_rootlistAskedFor = null;
            }
            if (s_waiters.Count > 0) PumpWaiters();
            if (!IsAccountScope(scope)) return;
            int me = scope.MeSlot;
            var e = scope.Edges;

            // A relation answered in pages stays Partial until its length reaches the total: ask the next page.
            foreach (var edge in s_pagedEdges)
            {
                if (Fetch.EdgeTableOf(scope, edge) is not { } table || table.State(me) != EdgeState.Partial) continue;
                int have = table.Count(me);
                if (have > 0 && !table.WasAsked(me, have)) Entities.EnsureEdge(edge, me, have, FetchPriority.Prefetch);
            }
            // The shared walk stages albums beside liked tracks; a liked answer that landed without them asks once.
            if (e.Liked.State(me) == EdgeState.Complete && e.SavedAlbums.State(me) == EdgeState.Unknown
                && !e.SavedAlbums.WasAsked(me, 0))
                Entities.EnsureEdge(FetchEdge.SavedAlbums, me, 0, FetchPriority.Prefetch);

            uint pins = e.Pins.Version(me);
            if (pins != s_seenPins)
            {
                s_seenPins = pins;
                if (s_pinSync is not null)
                {
                    Encode.PinWires(new User(me), s_pinWires);
                    s_pinSync.ApplyServer(s_pinWires, e.Pins.State(me) == EdgeState.Complete);
                }
            }

            uint albums = e.SavedAlbums.Version(me);
            if (albums != s_seenSavedAlbums)
            {
                s_seenSavedAlbums = albums;
                if (s_dropsAttached) Notify.ReleaseDrops.RequestReconcile();
            }

            uint rootlist = e.Rootlist.Version(me);
            if (rootlist != s_seenRootlist)
            {
                s_seenRootlist = rootlist;
                EnsureRootlistRows(scope, me);
            }

            // G-048: the owned rows' capability block, whenever the rootlist or a playlist header moved.
            uint playlists = scope.Playlists.Changed.Peek();
            if (rootlist != s_seenCapsRootlist || playlists != s_seenPlaylists)
            {
                s_seenCapsRootlist = rootlist;
                s_seenPlaylists = playlists;
                var staging = Staging.Rent();
                staging.Epoch = scope.Epoch;
                if (LibraryCaps.StageOwned(scope, staging) > 0)
                {
                    Entities.Commit(staging);
                    Entities.Publish();
                }
                Staging.Return(staging);
            }
        }

        /// <summary>The sidebar paints every rootlist row: ask their titles and covers as ONE batched metadata POST
        /// (<see cref="PlaylistFields.Row"/>), markers skipped. The planner dedupes rows already known or asked.</summary>
        static void EnsureRootlistRows(Scope scope, int me)
        {
            var targets = scope.Edges.Rootlist.Targets(me);
            if (s_rowScratch.Length < targets.Length) s_rowScratch = new int[Math.Max(targets.Length, s_rowScratch.Length * 2)];
            int n = 0;
            for (int i = 0; i < targets.Length; i++) if (targets[i] > Table.None) s_rowScratch[n++] = targets[i];
            if (n > 0) Entities.Ensure(scope.Playlists, s_rowScratch.AsSpan(0, n), (uint)PlaylistFields.Row, FetchPriority.Prefetch);
        }

        /// <summary>The release-drop scheduler's resolver (G-089): the account's saved prereleases, with their covers. A
        /// fresh list per call — the scheduler reads it after this returns.</summary>
        static IReadOnlyList<Notify.DropLink> ResolveDrops()
        {
            var scope = Entities.Current;
            var links = new List<Notify.DropLink>();
            if (IsAccountScope(scope))
                LibraryDrops.Resolve(scope, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), links, Controls.ArtUrl);
            return links;
        }

        // ══ 4. dealer pushes ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>How long a burst of pushes for the same relations folds into one read. The dealer sends the rootlist
        /// head twice (v2 and legacy topics) and a multi-select edit elsewhere as several collection pushes. A push for ONE
        /// playlist is never settled: its ops chain, each push's parent the previous one's head.</summary>
        public const int PushSettleMs = 250;

        static int s_pendingPush;
        static System.Threading.Timer? s_pushTimer;

        /// <summary>A dealer MESSAGE: its topic and its decoded body. ANY THREAD (the dealer's). A push for ONE playlist
        /// (<see cref="LibraryPush.Playlist"/>) is decoded here — pure (<see cref="PlaylistOps.DecodePush"/>), off the UI
        /// thread — and POSTED as it is, in arrival order (<c>PlaylistPushed</c>, Spotify.Library.Lists.cs). A rootlist
        /// push leaves its head for the settle's dedupe; every library push folds into the pending set and arms the
        /// settle. The one caller is <c>Spotify.Connect.OnDealer</c>'s fall-through for frames it does not read itself.
        /// <paramref name="payload"/> is a view into the dealer thread's scratch: decoded before this returns, never kept.
        /// An empty one decodes as an unreadable push, which marks the list dirty — never applies.</summary>
        public static void OnDealerPush(ReadOnlySpan<byte> topic, ReadOnlySpan<byte> payload = default)
        {
            if (OnBanPush(topic)) return;
            if (OnShowPush(topic, payload)) return;
            if (Podcasts.OnSavedEpisodesPush(topic)) return;
            var push = LibraryPushRules.Classify(topic);
            if (push == LibraryPush.None) return;
            if (push == LibraryPush.Playlist)
            {
                PostPlaylistPush(System.Text.Encoding.UTF8.GetString(LibraryPushRules.PlaylistId(topic)), DecodePushBody(payload));
                return;
            }
            if ((push & LibraryPush.Rootlist) != 0) NoteRootlistPush(payload);
            int before, after;
            do
            {
                before = Volatile.Read(ref s_pendingPush);
                after = before | (int)push;
            } while (Interlocked.CompareExchange(ref s_pendingPush, after, before) != before);
            if (before != 0) return;                                        // a settle is already armed; this folds into it
            var timer = LazyInitializer.EnsureInitialized(ref s_pushTimer,
                static () => new System.Threading.Timer(static _ => Post(s_flushPushes), null, Timeout.Infinite, Timeout.Infinite));
            timer.Change(PushSettleMs, Timeout.Infinite);
        }

        static void FlushPushes()
        {
            var push = (LibraryPush)Interlocked.Exchange(ref s_pendingPush, 0);
            var scope = Entities.Current;
            if (push == LibraryPush.None || !IsAccountScope(scope)) return;
            int me = scope.MeSlot;
            if ((push & LibraryPush.Rootlist) != 0) FlushRootlistPush(scope, me);
            if ((push & (LibraryPush.Liked | LibraryPush.SavedAlbums)) != 0) Entities.RefreshEdge(FetchEdge.Liked, me);
            if ((push & LibraryPush.FollowedArtists) != 0) Entities.RefreshEdge(FetchEdge.FollowedArtists, me);
            if ((push & LibraryPush.SavedShows) != 0) Entities.RefreshEdge(FetchEdge.SavedShows, me);
            if ((push & LibraryPush.Pins) != 0) Entities.RefreshEdge(FetchEdge.Pins, me);
        }

        // ══ 5. collection writes (User.Dispatch) and the pin writes ═════════════════════════════════════════════════

        /// <summary>User.cs's shell seam. Only the SIGNED-IN account of a Spotify scope writes: a fake scope, a test, or a
        /// row that is not <see cref="Scope.MeSlot"/> keeps the model half alone.</summary>
        internal static void Dispatch(LibraryEdgeKind kind, bool add, int userSlot, int targetSlot, EntityId targetId)
        {
            var scope = Entities.Current;
            if (!IsAccountScope(scope) || userSlot != scope.MeSlot) return;
            string uri = targetId.Text;
            WriteCollection(scope, kind, uri, add, userSlot, targetSlot);
        }

        /// <summary>The pin bridge's write: one ylpin write for a wire uri (a row, Liked, or a folder).</summary>
        static void WritePin(string wireUri, bool pinned)
        {
            var scope = Entities.Current;
            if (!IsAccountScope(scope)) return;
            WriteCollection(scope, LibraryEdgeKind.Pins, wireUri, pinned, scope.MeSlot, Table.None);
        }

        static void WriteCollection(Scope scope, LibraryEdgeKind kind, string uri, bool add, int userSlot, int targetSlot)
        {
            if (uri.Length == 0 || !CanWrite(out _, out string username))
            {
                SettleCollection(scope, kind, uri, add, userSlot, targetSlot, ok: false, status: 0);
                return;
            }
            if (kind == LibraryEdgeKind.Pins) NotePinWrite(uri, inFlight: true);
            var net = Net;
            bool queued = net.Run(() =>
            {
                Api.Result result = add ? net.CollectionAdd(username, kind, uri) : net.CollectionRemove(username, kind, uri);
                bool ok = result.Ok;
                int status = result.Status;
                Post(() => SettleCollection(scope, kind, uri, add, userSlot, targetSlot, ok, status));
            });
            if (!queued) SettleCollection(scope, kind, uri, add, userSlot, targetSlot, ok: false, status: 0);
        }

        static void SettleCollection(Scope scope, LibraryEdgeKind kind, string uri, bool add, int userSlot, int targetSlot,
                                     bool ok, int status)
        {
            if (kind == LibraryEdgeKind.Pins) NotePinWrite(uri, inFlight: false);
            if (!ReferenceEquals(scope, Entities.Current)) return;          // the scope moved on (C7): its edges are gone
            if (targetSlot > Table.None) new User(userSlot).Settle(kind, targetSlot, ok);
            if (ok)
            {
                if (kind == LibraryEdgeKind.Pins) MirrorPin(scope, uri, add);
                if (kind == LibraryEdgeKind.SavedAlbums && targetSlot > Table.None && new Album(targetSlot).IsPreRelease)
                {
                    var link = LibraryDrops.LinkOf(new Album(targetSlot), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    Notify.ReleaseDrops.OnSavedChanged(in link, add);
                }
                Entities.Publish();
                return;
            }
            Entities.Publish();
            Log.Warn("library", "collection write refused (" + Api.WireSet(kind) + (add ? " add" : " remove") + ", status " + status + ")");
            Notify.Say(Loc.Get(status == 0 ? "drag.libraryUnavailable" : "library.writeFailed"), InfoBarSeverity.Error,
                       dedupeKey: "library.write-failed");
        }

        // ══ 5b. the delta apply (gap-fix "library delta sync", stage 2) ═════════════════════════════════════════════

        /// <summary>THE apply: posted from <see cref="Api.TryCollectionDelta"/> after a clean delta answer decodes off
        /// the network thread (<c>Spotify.Api.Library.cs</c>). UI THREAD ONLY (C1) — this is the one place stage 2's
        /// adds and removes touch a live relation, through the SAME primitives an optimistic write uses
        /// (<c>EdgeTable&lt;TEdge&gt;.Insert</c> / <c>.Remove</c>), never through the Staging/commit pipeline the full
        /// walk uses (that pipeline only knows whole-list replace, which is exactly why a delta cannot ride it).
        ///
        /// <para>THE LEDGER'S INVARIANT, enforced here and nowhere else: apply only when the live list's count still
        /// matches the baseline the delta was asked against — a local optimistic like/unlike since then is exactly the
        /// doubt that must force a fallback, never an apply on top of a moved target. After applying, the resulting
        /// count must reconcile with what the delta's own items implied, or the whole apply is worthless and is simply
        /// never persisted: the relation is asked for again, and with no ledger entry surviving, that next ask is a
        /// FULL walk — never a second delta stacked on an unverified one.</para></summary>
        internal static void ApplyCollectionDelta(LibraryEdgeKind set, string username, List<CollectionDeltaItem> items,
            int baselineCount, int added, int removed, string newSyncToken)
        {
            var scope = Entities.Current;
            if (!IsAccountScope(scope) || !string.Equals(scope.Key.Account, username, StringComparison.Ordinal)) return;
            int me = scope.MeSlot;
            var relation = User.Relation(set);

            if (!LibrarySyncLedger.BaselineMatches(relation.Count(me), new LedgerEntry(newSyncToken, baselineCount)))
            {
                // Drifted since the ask went out (an optimistic write, a concurrent full walk, a scope hiccup):
                // never guess which of the delta's items still apply — fall back to the full, authoritative walk.
                ForfeitLedger(set, username, me, "baseline", relation.Count(me), baselineCount);
                return;
            }

            // Counted over what THIS relation can hold, never over the raw answer: `collection` is ONE wire set for
            // two relations (liked tracks, saved albums), so a delta asked for one carries the other's items too, and
            // counting those made every such delta "fail to reconcile" (2026-09-18).
            added = 0;
            removed = 0;
            foreach (var item in items)
            {
                if (!TryResolveDeltaTarget(scope, set, item.Uri, out int targetSlot, out byte flags)) continue;
                if (item.Removed) { relation.Remove(me, targetSlot); removed++; }
                else { relation.Insert(me, targetSlot, new LibraryEdge(item.AddedAt, flags), at: 0); added++; }
            }

            if (!LibrarySyncLedger.PostApplyMatches(baselineCount, relation.Count(me), added, removed))
            {
                // The delta's own items did not reconcile with what actually landed (a duplicate add, a remove that
                // named nothing we held): discard it — never persist the new token — and ask again.
                ForfeitLedger(set, username, me, "reconcile", relation.Count(me), baselineCount + added - removed);
                return;
            }

            Store.MetaSet(Api.CollectionMetaKey(username, set), LibrarySyncLedger.Encode(newSyncToken, relation.Count(me)));
            Entities.Publish();
        }

        /// <summary>A delta that cannot be trusted FORFEITS its ledger entry before the relation is asked for again.
        /// Without the erase the re-ask was handed the SAME token and count (<c>Fetch.FillRevisions</c> reads this
        /// key), took the delta path again, failed the same check again and re-asked again — an unbounded
        /// <c>/collection/v2/delta</c> loop, hundreds of 200s a minute, for as long as the app ran (2026-09-18). An
        /// empty entry decodes as <see cref="LedgerEntry.Empty"/>, so the next ask is the FULL walk — which the doc on
        /// <see cref="ApplyCollectionDelta"/> always promised and the code never did.</summary>
        static void ForfeitLedger(LibraryEdgeKind set, string username, int me, string why, int have, int want)
        {
            Log.Warn("library", "collection delta discarded (" + set + ", " + why + ": have " + have + ", want " + want
                                 + ") — ledger forfeited, falling back to the full walk");
            Store.MetaSet(Api.CollectionMetaKey(username, set), "");
            Entities.RefreshEdge(EdgeFor(set), me);
        }

        static FetchEdge EdgeFor(LibraryEdgeKind set) => set switch
        {
            LibraryEdgeKind.SavedAlbums => FetchEdge.SavedAlbums,
            LibraryEdgeKind.FollowedArtists => FetchEdge.FollowedArtists,
            LibraryEdgeKind.SavedShows => FetchEdge.SavedShows,
            LibraryEdgeKind.Pins => FetchEdge.Pins,
            _ => FetchEdge.Liked,
        };

        /// <summary>A delta item's uri → the target slot it names (interning it if new, exactly like an optimistic
        /// write would) and, for <see cref="LibraryEdgeKind.Pins"/> only, the <see cref="PinKind"/> byte the payload's
        /// <c>Flags</c> carries (<see cref="MirrorPin"/>'s own shape). Everything else is filtered to its own
        /// relation's kind, exactly like the full walk's fold (<c>Decode.LibraryRelationOf</c>) — a wrong-kind uri in
        /// the answer is skipped, never miss-filed.</summary>
        static bool TryResolveDeltaTarget(Scope scope, LibraryEdgeKind set, string uri, out int targetSlot, out byte flags)
        {
            targetSlot = Table.None;
            flags = 0;
            if (set == LibraryEdgeKind.Pins)
            {
                bool pinned = PinOf(scope, uri, out PinKind kind, out targetSlot) && targetSlot > Table.None;
                if (pinned) flags = (byte)kind;
                return pinned;
            }

            EntityKind wantKind = set switch
            {
                LibraryEdgeKind.SavedAlbums => EntityKind.Album,
                LibraryEdgeKind.FollowedArtists => EntityKind.Artist,
                LibraryEdgeKind.SavedShows => EntityKind.Show,
                _ => EntityKind.Track,
            };
            var id = EntityId.Parse(uri.AsSpan());
            if (id.Kind != wantKind) return false;
            var table = Entities.TableFor(id.Kind);
            if (table is null) return false;
            targetSlot = table.Slot(id);
            return targetSlot > Table.None;
        }

        // A pin whose write is in flight — or settled in the last minute, before any read could reflect it — is never
        // swept by the bridge: a pins read that raced the write would otherwise un-pin what the user just pinned.
        const long PinWriteGraceMs = 60_000;
        static readonly Dictionary<string, long> s_pinWrites = new(StringComparer.Ordinal);

        static void NotePinWrite(string uri, bool inFlight) => s_pinWrites[uri] = inFlight ? long.MaxValue : Environment.TickCount64;

        static bool IsPinWritePending(string uri)
            => s_pinWrites.TryGetValue(uri, out long at) && (at == long.MaxValue || Environment.TickCount64 - at < PinWriteGraceMs);

        /// <summary>Reflect an accepted pin write in the Pins edge, so the bridge's next look agrees with the server without
        /// a read. Rebuilt whole rather than spliced: a pin's target is only an identity WITH its kind byte, and a
        /// target-keyed splice could collide a folder's id value with a row slot.</summary>
        static void MirrorPin(Scope scope, string wireUri, bool pinned)
        {
            int me = scope.MeSlot;
            var relation = scope.Edges.Pins;
            if (!PinOf(scope, wireUri, out var kind, out int target)) return;
            var targets = relation.Targets(me);
            var payload = relation.Payload(me);
            var nextTargets = new List<int>(targets.Length + 1);
            var nextPayload = new List<LibraryEdge>(targets.Length + 1);
            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] == target && (PinKind)payload[i].Flags == kind) continue;
                nextTargets.Add(targets[i]);
                nextPayload.Add(payload[i]);
            }
            if (pinned)
            {
                nextTargets.Add(target);
                nextPayload.Add(new LibraryEdge((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), (byte)kind));
            }
            var state = relation.State(me);
            relation.Replace(me, nextTargets.ToArray(), nextPayload.ToArray(), state == EdgeState.Unknown ? EdgeState.Partial : state,
                             nextTargets.Count);
        }

        /// <summary>A wire pin uri → its kind and target, the same resolution the staged commit applies.</summary>
        static bool PinOf(Scope scope, string wireUri, out PinKind kind, out int target)
        {
            kind = PinKind.Unknown;
            target = Table.None;
            Span<byte> utf8 = stackalloc byte[256];
            if (System.Text.Encoding.UTF8.GetByteCount(wireUri) > utf8.Length) return false;
            int n = System.Text.Encoding.UTF8.GetBytes(wireUri, utf8);
            var bytes = utf8[..n];
            if (User.IsLikedPinUri(bytes)) { kind = PinKind.Liked; return true; }
            var folder = User.FolderIdOf(bytes);
            if (!folder.IsEmpty) { kind = PinKind.Folder; target = Entities.Intern(folder).Value; return true; }
            if (EntityUri.IsPrerelease(wireUri.AsSpan())) return false;
            var id = EntityId.Parse(wireUri.AsSpan());
            kind = User.PinKindFor(id.Kind);
            var table = kind == PinKind.Unknown ? null : Entities.TableFor(id.Kind);
            if (table is null) { kind = PinKind.Unknown; return false; }
            target = table.Slot(id);
            return true;
        }

        // ══ 6. the rootlist writes (Sidebar.LibraryWrites) ══════════════════════════════════════════════════════════

        /// <summary>Build a gesture's ops against the stream the account holds now.</summary>
        delegate bool RootlistPlan(IReadOnlyList<RootlistEntry> entries, long nowMs, out List<PlaylistOp> ops,
                                   out RootlistMoveCheck reason);

        /// <summary>THE rootlist write: read the live stream, build the ops, land the locally applied tree (the optimistic
        /// half — the reply will not carry one — which forgets the held head: <see cref="LandRootlistTree"/>), send against
        /// the head the tree was computed over, and on the 200 hold the resulting head when it is provably that tree
        /// (<see cref="AdoptRootlistHead"/>) and run <paramref name="confirmed"/>. A refusal to BUILD says why and sends
        /// nothing; a refusal from the SERVER reads the rootlist back in full and says why.</summary>
        static void WriteRootlist(string verb, RootlistPlan plan, Action? confirmed)
        {
            if (!CanWrite(out var scope, out string username)) { Unavailable(); return; }
            int me = scope.MeSlot;
            var entries = new List<RootlistEntry>();
            Encode.RootlistEntries(new User(me), entries);
            long now = UnixNowMs();
            if (!plan(entries, now, out var ops, out var reason))
            {
                Log.Info("library", "rootlist " + verb + " not sent: " + reason);
                string key = reason switch
                {
                    RootlistMoveCheck.Cycle => "drag.cantMoveIntoItself",
                    RootlistMoveCheck.NoOp or RootlistMoveCheck.SameItem => "",
                    _ => "drag.cantMoveHere",
                };
                if (key.Length > 0) Notify.Say(Loc.Get(key), InfoBarSeverity.Informational, dedupeKey: "library.rootlist-refused");
                return;
            }
            List<RootlistEntry> after;
            try { after = Encode.ApplyLocally(entries, ops); }
            catch (ArgumentOutOfRangeException ex)
            {
                Log.Warn("library", "rootlist " + verb + " does not apply to the held stream", ex);
                Notify.Say(Loc.Get("library.moveFailed"), InfoBarSeverity.Error, dedupeKey: "library.rootlist-failed");
                return;
            }
            OptimisticRootlist landed = LandRootlistTree(scope, after);
            Entities.Publish();

            PlaylistOp[] body = ops.ToArray();
            var net = Net;
            if (!net.Run(() => PostRootlist(net, scope, username, landed, body, now, verb, confirmed)))
                RootlistFailed(scope, verb, 0);
        }

        /// <summary>What a rootlist write's optimistic landing leaves for its confirm (<see cref="AdoptRootlistHead"/>):
        /// <paramref name="Base"/>, the head the landed tree was computed over — the base the write sends; "" when none was
        /// held — and <paramref name="Version"/>, the rootlist edge's version right after the landing.</summary>
        public readonly record struct OptimisticRootlist(string Base, uint Version);

        /// <summary>THE OPTIMISTIC HALF OF EVERY ROOTLIST WRITE (the moves, follow/unfollow, the folder verbs, a create's
        /// filing, a delete): land the locally applied <paramref name="tree"/> as the account's rootlist and FORGET the held
        /// head (<see cref="Entities.ForgetListRevision"/>) — the tree is no longer the list that head describes, so the next
        /// rootlist ask is a full read, never a <c>/rootlist/diff</c> whose ops (our own, echoed) replay over a tree that
        /// already holds them. The disk keeps the pre-edit pair. Returns the head the tree was computed over and the
        /// version the landing left. UI thread; the caller publishes. Public for the facts (no <c>InternalsVisibleTo</c>).</summary>
        public static OptimisticRootlist LandRootlistTree(Scope scope, IReadOnlyList<RootlistEntry> tree)
        {
            ArgumentNullException.ThrowIfNull(scope);
            int me = scope.MeSlot;
            var held = scope.Edges.RootlistRevision(me);
            string based = held.IsEmpty ? "" : Entities.Strings.Resolve(held);
            Encode.LandRootlist(new User(me), tree);
            Entities.ForgetListRevision(scope, EdgeRelation.Rootlist, me);
            return new OptimisticRootlist(based, scope.Edges.Rootlist.Version(me));
        }

        /// <summary>A write's 200: hold its reply <paramref name="head"/> again ONLY when it is provably the tree
        /// <paramref name="landed"/> put there (<see cref="OptimisticHead.MayAdopt"/>: the direct successor of the base the
        /// tree was computed over, with nothing replacing the rows or re-arming a head since). True when held; otherwise the
        /// head stays forgotten and the next rootlist ask — usually this write's own dealer echo — reads in full. UI thread.</summary>
        public static bool AdoptRootlistHead(Scope scope, OptimisticRootlist landed, string head)
        {
            ArgumentNullException.ThrowIfNull(scope);
            int me = scope.MeSlot;
            bool untouched = scope.Edges.Rootlist.Version(me) == landed.Version && scope.Edges.RootlistRevision(me).IsEmpty;
            if (!OptimisticHead.MayAdopt(landed.Base, head, untouched)) return false;
            scope.Edges.SetRootlistRevision(me, Entities.Strings.Intern(head));
            return true;
        }

        /// <summary>The landed tree is not the server's — a refused write, a create that did not file where it landed, a
        /// delete either way: FORGET whatever head is held, then read the rootlist back. A FULL read: a <c>/diff</c> from
        /// any held head would be answered about a tree the server never had ("unchanged", or ops replayed onto it). UI
        /// thread, in <paramref name="scope"/> = the current one. Public for the facts.</summary>
        public static void ReadRootlistBack(Scope scope)
        {
            ArgumentNullException.ThrowIfNull(scope);
            Entities.ForgetListRevision(scope, EdgeRelation.Rootlist, scope.MeSlot);
            Entities.RefreshEdge(FetchEdge.Rootlist, scope.MeSlot);
        }

        /// <summary>API THREAD. The head the write is based on is the one the landed tree was computed over; with none held
        /// (the rootlist has not answered this scope, or another write's tree is still unconfirmed) it is read first.</summary>
        static void PostRootlist(Transport net, Scope scope, string username, OptimisticRootlist landed, PlaylistOp[] ops, long nowMs,
                                 string verb, Action? confirmed)
        {
            Span<byte> revision = stackalloc byte[Encode.MaxRevisionBytes];
            int length = Encode.RevisionBytes(landed.Base, revision);
            if (length == 0)
            {
                Api.Result read = net.Rootlist(username);
                if (read.Ok) length = Encode.ResultingRevision(read.Bytes, revision);
                if (length == 0)
                {
                    int readStatus = read.Ok ? 0 : read.Status;
                    Post(() => RootlistFailed(scope, verb, readStatus));
                    return;
                }
            }
            byte[] request = Encode.RootlistChanges(revision[..length], ops, username, nowMs, Encode.NewNonce());
            Api.Result result = net.RootlistChanges(username, request);
            if (!result.Ok)
            {
                int status = result.Status;
                Post(() => RootlistFailed(scope, verb, status));
                return;
            }
            string head = HeadOf(result.Bytes);
            Post(() => RootlistConfirmed(scope, landed, head, confirmed));
        }

        /// <summary>A reply's resulting head in the <c>{counter},{hex}</c> spelling the edge holds, or "".</summary>
        static string HeadOf(ReadOnlySpan<byte> reply)
        {
            Span<byte> head = stackalloc byte[Encode.MaxRevisionBytes];
            int length = Encode.ResultingRevision(reply, head);
            if (length == 0) return "";
            Span<char> text = stackalloc char[16 + 2 * Encode.MaxRevisionBytes];
            int written = Api.FormatRevision(head[..length], text);
            return written == 0 ? "" : new string(text[..written]);
        }

        static void RootlistConfirmed(Scope scope, OptimisticRootlist landed, string head, Action? confirmed)
        {
            if (!ReferenceEquals(scope, Entities.Current)) return;
            AdoptRootlistHead(scope, landed, head);
            confirmed?.Invoke();
        }

        static void RootlistFailed(Scope scope, string verb, int status)
        {
            Log.Warn("library", "rootlist " + verb + " refused (status " + status + ")");
            if (!ReferenceEquals(scope, Entities.Current)) return;
            // The optimistic tree is not the server's: forget any head, read the truth back in full.
            ReadRootlistBack(scope);
            string key = status switch
            {
                409 => "library.conflict",
                403 => "detail.edit.forbidden",
                0 => "drag.libraryUnavailable",
                _ => "library.moveFailed",
            };
            Notify.Say(Loc.Get(key), InfoBarSeverity.Error, dedupeKey: "library.rootlist-failed");
        }

        /// <summary>The seam's move: ONE batch, whatever the selection size, then "Moved to …" with Undo back to the pre-move
        /// anchors the pane captured.</summary>
        static void MoveRootlist(IReadOnlyList<RootlistItemRef> items, RootlistItemRef target, RootlistDropPlacement placement,
                                 string destinationName, IReadOnlyList<RootlistMove>? undo)
        {
            var moves = RootlistBatchOrder.For(items, target, placement);
            if (moves.Count == 0 || target.Key.Length == 0)
            {
                Notify.Say(Loc.Get("drag.cantMoveHere"), InfoBarSeverity.Informational, dedupeKey: "library.rootlist-refused");
                return;
            }
            int count = moves.Count;
            WriteRootlist("move",
                (IReadOnlyList<RootlistEntry> entries, long _, out List<PlaylistOp> ops, out RootlistMoveCheck reason)
                    => Encode.TryBuildMoves(entries, moves, out ops, out reason),
                () => ConfirmMove(count, destinationName, undo));
        }

        static void ConfirmMove(int count, string destinationName, IReadOnlyList<RootlistMove>? undo)
        {
            string where = count > 1
                ? Loc.Format("drag.movedManyTo", ("count", count),
                             ("name", destinationName.Length > 0 ? destinationName : Loc.Get("sidebar.yourLibrary")))
                : destinationName.Length > 0 ? Loc.Format("drag.movedTo", ("name", destinationName)) : Loc.Get("drag.movedToLibrary");
            if (undo is { Count: > 0 } back)
                Notify.Say(where, InfoBarSeverity.Success, Loc.Get("sidebar.pin.undo"), () => UndoMove(back));
            else
            {
                Announce(where);
                Notify.Say(where, InfoBarSeverity.Success);
            }
        }

        static void UndoMove(IReadOnlyList<RootlistMove> undo)
            => WriteRootlist("undo move",
                (IReadOnlyList<RootlistEntry> entries, long _, out List<PlaylistOp> ops, out RootlistMoveCheck reason)
                    => Encode.TryBuildMoves(entries, undo, out ops, out reason),
                null);

        /// <summary>Follow / unfollow a playlist through the rootlist (User.LibrarySeam's playlist heart): follow files it
        /// at the top, unfollow removes its row; a no-op state sends nothing.</summary>
        public static void FollowPlaylist(string playlistUri, bool follow)
        {
            if (string.IsNullOrEmpty(playlistUri)) return;
            WriteRootlist(follow ? "follow" : "unfollow",
                (IReadOnlyList<RootlistEntry> entries, long now, out List<PlaylistOp> ops, out RootlistMoveCheck reason) =>
                {
                    ops = new List<PlaylistOp>(1);
                    var present = Encode.RootlistRemove(entries, playlistUri);
                    if (follow) { if (present is null) ops.Add(Encode.RootlistAdd(playlistUri, 0, now)); }
                    else if (present is { } remove) ops.Add(remove);
                    reason = ops.Count > 0 ? RootlistMoveCheck.Ok : RootlistMoveCheck.NoOp;
                    return ops.Count > 0;
                },
                null);
        }

        /// <summary>"New folder" / "New folder from this" / "New folder inside": the pair and every filed item in ONE delta
        /// (the create ops, then the moves built against the stream with the new pair in it) — atomic, where 0.2.9 posted
        /// the create and the filing separately. An empty item list is the plain verb: this seam has no overlay to ask for a
        /// name in, so the folder is created as "New Folder" and renamed through the row's own Rename prompt.</summary>
        static void NewFolderWith(string? parentFolderId, IReadOnlyList<RootlistItemRef> items)
        {
            string name = Loc.Get("sidebar.newFolder");
            string groupId = Encode.NewGroupId();
            IReadOnlyList<RootlistMove> moves = items.Count > 0
                ? RootlistBatchOrder.For(items, new RootlistItemRef(groupId, IsFolder: true), RootlistDropPlacement.Inside)
                : Array.Empty<RootlistMove>();
            int count = moves.Count;
            // Created OPEN: a folder made to hold things that opens closed reads as a failed create.
            Sidebar.SetFolderExpanded(groupId, true);
            WriteRootlist("folder create",
                (IReadOnlyList<RootlistEntry> entries, long now, out List<PlaylistOp> ops, out RootlistMoveCheck reason) =>
                {
                    ops = new List<PlaylistOp>(2 + moves.Count);
                    int at = Encode.PlacementIndex(entries, parentFolderId);
                    if (at < 0) { reason = RootlistMoveCheck.Missing; return false; }
                    ops.AddRange(Encode.CreateFolder(entries, groupId, name, at, now));
                    reason = RootlistMoveCheck.Ok;
                    if (moves.Count == 0) return true;
                    var withFolder = Encode.ApplyLocally(entries, ops);
                    if (!Encode.TryBuildMoves(withFolder, moves, out var filing, out reason)) return false;
                    ops.AddRange(filing);
                    return true;
                },
                () =>
                {
                    Announce(Loc.Get("sidebar.folderCreated") + ": " + name);
                    Notify.Say(count > 0 ? Loc.Format("sidebar.folderCreatedWith", ("name", name), ("count", count))
                                         : Loc.Get("sidebar.folderCreated"), InfoBarSeverity.Success);
                });
        }

        /// <summary>Rename a folder (the pane hands the trimmed NEW name): REM + ADD of the start marker with its original
        /// stamp. The group id is untouched, so expansion and pins ride through.</summary>
        static void RenameFolder(string groupId, string newName)
        {
            if (groupId.Length == 0 || newName.Length == 0) return;
            WriteRootlist("folder rename",
                (IReadOnlyList<RootlistEntry> entries, long now, out List<PlaylistOp> ops, out RootlistMoveCheck reason) =>
                {
                    var built = Encode.RenameFolder(entries, groupId, newName, now);
                    ops = built is null ? new List<PlaylistOp>() : new List<PlaylistOp>(built);
                    reason = built is null ? RootlistMoveCheck.Missing : RootlistMoveCheck.Ok;
                    return built is not null;
                },
                () => Announce(Loc.Get("sidebar.folderRenamed") + ": " + newName));
        }

        /// <summary>Delete a folder behind the shell's confirm dialog; the playlists inside move up a level. Refuses — out
        /// loud — when there is no overlay to confirm in (a confirmation-required action never runs unconfirmed).</summary>
        static void DeleteFolder(string groupId, string name, int childCount)
        {
            if (groupId.Length == 0) return;
            var services = Actions.Services;
            if (services.CanConfirm?.Invoke() != true || services.Confirm is not { } confirm)
            {
                Unavailable();
                return;
            }
            confirm(new ConfirmRequest("sidebar.deleteFolder", "library.deleteFolderConfirmBody", "sidebar.deleteFolder", () =>
                WriteRootlist("folder delete",
                    (IReadOnlyList<RootlistEntry> entries, long _, out List<PlaylistOp> ops, out RootlistMoveCheck reason) =>
                    {
                        var built = Encode.DeleteFolder(entries, groupId);
                        ops = built is null ? new List<PlaylistOp>() : new List<PlaylistOp>(built);
                        reason = built is null ? RootlistMoveCheck.Missing : RootlistMoveCheck.Ok;
                        return built is not null;
                    },
                    () =>
                    {
                        Announce(Loc.Get("sidebar.folderDeleted") + ": " + name);
                        Notify.Say(Loc.Get("sidebar.folderDeleted"), InfoBarSeverity.Success);
                    })));
        }

        // ══ 7. create a playlist ════════════════════════════════════════════════════════════════════════════════════

        static void CreatePlaylist(string? folderId, bool navigate) => StartCreate(folderId, navigate, then: null);

        /// <summary>Create from a dropped track set: create, then a deposit, then ONE toast (the deposit's).</summary>
        static void CreatePlaylistWith(DragPayload payload)
            => StartCreate(null, navigate: false, then: (uri, name) => Deposit(uri, name, payload));

        /// <summary>THE create flow. The row is real before anything is sent — a numbered name, the account as owner with
        /// the owner's capabilities, an empty COMPLETE membership (a 0-track owner page, not a skeleton), and the rootlist
        /// row at its placement — so a navigation lands on a page that renders. Then: the create <c>/changes</c> to the
        /// client-minted id (a031), and the rootlist ADD (a042). A refused create is terminal for that id: the notice says
        /// so and Retry mints a new one.</summary>
        static void StartCreate(string? folderId, bool navigate, Action<string, string>? then)
        {
            if (!CanWrite(out var scope, out string username)) { Unavailable(); return; }
            int me = scope.MeSlot;
            var entries = new List<RootlistEntry>();
            Encode.RootlistEntries(new User(me), entries);
            int at = Math.Max(0, Encode.PlacementIndex(entries, folderId));   // a folder that vanished files at the top

            var taken = new List<string>(entries.Count);
            var targets = scope.Edges.Rootlist.Targets(me);
            for (int i = 0; i < targets.Length; i++)
                if (targets[i] > Table.None) taken.Add(Entities.Strings.Resolve(new Playlist(targets[i]).TitleId));
            string name = Encode.NextPlaylistName(taken, Loc.Get("sidebar.newPlaylist"));

            string id = Api.NewPlaylistId();
            string uri = "spotify:playlist:" + id;
            long now = UnixNowMs();
            int slot = SeedCreated(scope, uri, name);
            if (slot == Table.None) { Notify.Say(Loc.Get("detail.edit.createFailed"), InfoBarSeverity.Error); return; }

            PlaylistOp add = Encode.RootlistAdd(uri, at, now);
            OptimisticRootlist landed = LandRootlistTree(scope, Encode.ApplyLocally(entries, [add]));
            Entities.Publish();
            if (navigate) { Playlist.PlaylistCreateIntent.Arm(uri); Actions.Services.Go?.Invoke(Shell.For(new EntityUri(scope.Playlists.Id[slot]), name)); }

            var net = Net;
            if (!net.Run(() => PostCreate(net, scope, username, id, uri, name, landed, add, now, slot, folderId, navigate, then)))
                CreateFailed(scope, slot, folderId, navigate, then, 0);
        }

        /// <summary>The optimistic row: staged and committed like any answer, at THIN authority so the server's own read
        /// overwrites it.</summary>
        static int SeedCreated(Scope scope, string uri, string name)
        {
            if (!EntityId.TryParseGid(uri.AsSpan(), out var id)) return Table.None;
            var staging = Staging.Rent();
            staging.Epoch = scope.Epoch;
            ref var row = ref staging.Playlists.RowFor(new StagedId(id), Authority.Thin,
                (uint)(PlaylistFields.Identity | PlaylistFields.Capabilities));
            row.Title = staging.AddText(System.Text.Encoding.UTF8.GetBytes(name));
            row.OwnerUri = new StagedId(scope.Users.Id[scope.MeSlot]);
            row.Caps = (byte)LibraryCaps.Owner;
            row.Flags = (uint)PlaylistFlags.Public;
            row.FlagsMask = (uint)PlaylistFlags.Public;
            Entities.Commit(staging);
            Staging.Return(staging);
            int slot = scope.Playlists.Slot(id);
            new Playlist(slot).MarkCreatePending();
            scope.Edges.PlaylistTracks.Replace(slot, ReadOnlySpan<int>.Empty, ReadOnlySpan<PlaylistTrackEdge>.Empty,
                                               EdgeState.Complete, 0);
            return slot;
        }

        /// <summary>API THREAD. The create, then the rootlist ADD. A 409 on the ADD means the rootlist moved under us: the
        /// playlist EXISTS, so it is filed at the top against a freshly read head rather than left out of the library —
        /// and then it is NOT where the landed tree put it (<paramref name="add"/>'s placement), which the confirm must
        /// know: only the ADD that was landed can vouch for a head.</summary>
        static void PostCreate(Transport net, Scope scope, string username, string id, string uri, string name,
                               OptimisticRootlist landed, PlaylistOp add, long nowMs, int slot, string? folderId, bool navigate,
                               Action<string, string>? then)
        {
            Api.Result created = net.PlaylistCreate(id, Encode.CreateChanges(name, username, nowMs, Encode.NewNonce()));
            if (!created.Ok)
            {
                int status = created.Status;
                Post(() => CreateFailed(scope, slot, folderId, navigate, then, status));
                return;
            }

            Span<byte> revision = stackalloc byte[Encode.MaxRevisionBytes];
            int length = Encode.RevisionBytes(landed.Base, revision);
            Api.Result filed = default;
            bool asLanded = false;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (length == 0 || attempt > 0)
                {
                    Api.Result read = net.Rootlist(username);
                    length = read.Ok ? Encode.ResultingRevision(read.Bytes, revision) : 0;
                    if (length == 0) break;
                }
                PlaylistOp op = attempt == 0 ? add : Encode.RootlistAdd(uri, 0, nowMs);
                filed = net.RootlistChanges(username, Encode.RootlistChanges(revision[..length], [op], username, nowMs, Encode.NewNonce()));
                asLanded = attempt == 0;
                if (filed.Status != 409) break;
            }
            string head = filed.Ok ? HeadOf(filed.Bytes) : "";
            bool inLibrary = filed.Ok;
            bool filedAsLanded = inLibrary && asLanded;
            int filedStatus = filed.Status;
            Post(() => CreateConfirmed(scope, slot, uri, name, landed, filedAsLanded, head, inLibrary, filedStatus, then));
        }

        /// <summary>The create's verdict. Filed with the ADD the tree landed: its head is held when provable
        /// (<see cref="AdoptRootlistHead"/>). Filed by the retry at the top: the landed tree has the row elsewhere, so it is
        /// read back in full. Not filed: read back, and said.</summary>
        static void CreateConfirmed(Scope scope, int slot, string uri, string name, OptimisticRootlist landed, bool filedAsLanded,
                                    string head, bool inLibrary, int status, Action<string, string>? then)
        {
            if (!ReferenceEquals(scope, Entities.Current)) return;
            new Playlist(slot).SettleCreate(ok: true);
            if (filedAsLanded) AdoptRootlistHead(scope, landed, head);
            else if (inLibrary) ReadRootlistBack(scope);
            if (!inLibrary)
            {
                // The playlist exists; only its rootlist row did not stick. Read the rootlist back and say so.
                Log.Warn("library", "created playlist not filed in the rootlist (status " + status + ")");
                ReadRootlistBack(scope);
                Notify.Say(Loc.Get(status == 409 ? "library.conflict" : "library.moveFailed"), InfoBarSeverity.Error);
            }
            Entities.Publish();
            Announce(Loc.Get("detail.newPlaylist") + ": " + name);
            then?.Invoke(uri, name);
        }

        static void CreateFailed(Scope scope, int slot, string? folderId, bool navigate, Action<string, string>? then, int status)
        {
            Log.Warn("library", "playlist create refused (status " + status + ")");
            if (!ReferenceEquals(scope, Entities.Current)) return;
            new Playlist(slot).SettleCreate(ok: false);
            ReadRootlistBack(scope);
            Entities.Publish();
            // A rejected id is never reused: Retry re-runs the whole flow and mints a new one.
            Notify.Say(Loc.Get("detail.edit.createFailed"), InfoBarSeverity.Error, Loc.Get("common.retry"),
                       () => StartCreate(folderId, navigate, then));
        }

        // ══ 8. deposit tracks ═══════════════════════════════════════════════════════════════════════════════════════

        static void DepositTracks(string playlistUri, string name, DragPayload payload) => Deposit(playlistUri, name, payload);

        /// <summary>Copy a payload's tracks into a playlist the pane already decided is writable: ONE ADD add_last with a
        /// client-minted item id per row (a046), against the playlist's current head, then "Added to {name}". A drop of a
        /// list onto itself is a reorder the page owns, never a copy.</summary>
        static void Deposit(string playlistUri, string name, DragPayload payload)
        {
            if (!CanWrite(out _, out _)) { Unavailable(); return; }
            if (string.Equals(payload.SourcePlaylistUri, playlistUri, StringComparison.Ordinal)
                || string.Equals(payload.Uri, playlistUri, StringComparison.Ordinal)) return;
            Task<Track[]> resolving;
            try { resolving = payload.ResolveTracksAsync(); }
            catch (Exception ex)
            {
                Log.Warn("library", "a deposit's tracks could not be resolved", ex);
                return;
            }
            resolving.ContinueWith(t =>
            {
                Track[] tracks = t.IsCompletedSuccessfully ? t.Result : [];
                Post(() => DepositResolved(playlistUri, name, tracks));
            }, TaskScheduler.Default);
        }

        static void DepositResolved(string playlistUri, string name, Track[] tracks)
        {
            if (!CanWrite(out var scope, out string username)) { Unavailable(); return; }
            long now = UnixNowMs();
            var members = new List<PlaylistMember>(tracks.Length);
            for (int i = 0; i < tracks.Length; i++)
            {
                if (!tracks[i].IsValid) continue;
                string uri = tracks[i].Id.Text;
                if (uri.Length > 0) members.Add(new PlaylistMember(uri, Api.NewItemId(), now));
            }
            if (members.Count == 0)
            {
                Notify.Say(Loc.Get("library.nothingToAdd"), InfoBarSeverity.Informational, dedupeKey: "library.nothing-to-add");
                return;
            }
            scope.Playlists.TryGetSlot(playlistUri.AsSpan(), out int slot);
            string id = EntityUri.IdOf(playlistUri.AsSpan()).ToString();
            PlaylistOp op = Encode.AppendTracks(members);
            var net = Net;
            if (!net.Run(() => PostDeposit(net, scope, username, id, slot, name, op, now)))
                DepositFailed(0);
        }

        /// <summary>API THREAD. The head is the playlist's own, read now (nothing holds a playlist revision); one retry
        /// against a fresh head on a 409 — an append does not depend on positions, so the rebase is exact.</summary>
        static void PostDeposit(Transport net, Scope scope, string username, string id, int slot, string name, PlaylistOp op, long nowMs)
        {
            Span<byte> revision = stackalloc byte[Encode.MaxRevisionBytes];
            int status = 0;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Api.Result read = net.Playlist(id);
                int length = read.Ok ? Encode.ResultingRevision(read.Bytes, revision) : 0;
                if (length == 0) { status = read.Ok ? 0 : read.Status; break; }
                Api.Result result = net.PlaylistChanges(id, Encode.PlaylistChanges(revision[..length], [op], username, nowMs, Encode.NewNonce()));
                if (result.Ok)
                {
                    Post(() => DepositConfirmed(scope, slot, name));
                    return;
                }
                status = result.Status;
                if (status != 409) break;
            }
            int failed = status;
            Post(() => DepositFailed(failed));
        }

        static void DepositConfirmed(Scope scope, int slot, string name)
        {
            if (ReferenceEquals(scope, Entities.Current) && slot > Table.None)
            {
                Entities.RefreshEdge(FetchEdge.PlaylistTracks, slot);
                Playlist.RememberDeposit(new Playlist(slot).Uri.Text);
            }
            Notify.Say(Loc.Format("detail.addedToPlaylist", ("name", name)), InfoBarSeverity.Success);
        }

        static void DepositFailed(int status)
        {
            Log.Warn("library", "track deposit refused (status " + status + ")");
            string key = status switch
            {
                403 => "detail.edit.forbidden",
                404 or 410 => "detail.edit.deletedElsewhere",
                409 => "detail.edit.conflict",
                0 => "drag.libraryUnavailable",
                _ => "detail.edit.failed",
            };
            Notify.Say(Loc.Get(key), InfoBarSeverity.Error, dedupeKey: "library.deposit-failed");
        }

        // ══ 9. resolve an entity's tracks (for a deposit) ═══════════════════════════════════════════════════════════

        /// <summary>How long a resolve waits for a relation that has not landed before it answers with what is resident.</summary>
        public const int ResolveTimeoutMs = 15_000;

        sealed class Waiter(FetchEdge edge, int parent, Scope scope, TaskCompletionSource<Track[]> done, CancellationToken ct, long deadline)
        {
            public readonly FetchEdge Edge = edge;
            public readonly int Parent = parent;
            public readonly Scope Scope = scope;
            public readonly TaskCompletionSource<Track[]> Done = done;
            public readonly CancellationToken Ct = ct;
            public readonly long Deadline = deadline;
        }

        static readonly List<Waiter> s_waiters = new();
        static System.Threading.Timer? s_waiterTimer;

        /// <summary>The seam's resolver: a track is itself; an album's, a playlist's and the Liked collection's tracks are
        /// their relation, asked through the edge door and answered once it lands (Complete, or failed, or the timeout —
        /// whatever is resident then). A show answers nothing: its members are episodes, not tracks. Any thread in; the
        /// work is posted.</summary>
        static Task<Track[]> ResolveTracks(string uri, CancellationToken ct)
        {
            var done = new TaskCompletionSource<Track[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (ct.IsCancellationRequested) { done.TrySetCanceled(ct); return done.Task; }
            Post(() => BeginResolve(uri, done, ct));
            return done.Task;
        }

        static void BeginResolve(string uri, TaskCompletionSource<Track[]> done, CancellationToken ct)
        {
            var scope = Entities.Current;
            var id = EntityId.Parse(uri.AsSpan());
            FetchEdge edge;
            int parent;
            switch (id.Kind)
            {
                case EntityKind.Track:
                    done.TrySetResult([Entities.Track(id)]);
                    return;
                case EntityKind.Album:
                    edge = FetchEdge.AlbumTracks;
                    parent = scope.Albums.Slot(id);
                    break;
                case EntityKind.Playlist:
                    edge = FetchEdge.PlaylistTracks;
                    parent = scope.Playlists.Slot(id);
                    break;
                case EntityKind.Collection when EntityUri.IsLikedCollection(uri.AsSpan()) && scope.MeSlot > Table.None:
                    edge = FetchEdge.Liked;
                    parent = scope.MeSlot;
                    break;
                default:
                    done.TrySetResult([]);
                    return;
            }
            var waiter = new Waiter(edge, parent, scope, done, ct, Environment.TickCount64 + ResolveTimeoutMs);
            if (TryFinish(waiter)) return;
            Entities.EnsureEdge(edge, parent, 0, FetchPriority.Visible);
            s_waiters.Add(waiter);
            if (ct.CanBeCanceled) ct.Register(static () => Post(s_pumpWaiters));
            s_waiterTimer ??= new System.Threading.Timer(static _ => Post(s_pumpWaiters), null, Timeout.Infinite, Timeout.Infinite);
            s_waiterTimer.Change(ResolveTimeoutMs + 50, Timeout.Infinite);
        }

        static void PumpWaiters()
        {
            for (int i = s_waiters.Count - 1; i >= 0; i--)
                if (TryFinish(s_waiters[i])) s_waiters.RemoveAt(i);
        }

        static bool TryFinish(Waiter w)
        {
            if (w.Ct.IsCancellationRequested) { w.Done.TrySetCanceled(w.Ct); return true; }
            if (!ReferenceEquals(w.Scope, Entities.Current)) { w.Done.TrySetResult([]); return true; }
            if (Fetch.EdgeTableOf(w.Scope, w.Edge) is not { } table) { w.Done.TrySetResult([]); return true; }
            var state = table.Readiness(w.Parent);
            bool expired = Environment.TickCount64 >= w.Deadline;
            if (state == EdgeState.Partial && !expired)
            {
                int have = table.Count(w.Parent);
                if (!table.WasAsked(w.Parent, have)) Entities.EnsureEdge(w.Edge, w.Parent, have, FetchPriority.Visible);
                return false;
            }
            if (state is EdgeState.Unknown && !expired) return false;
            ReadOnlySpan<int> slots = w.Edge switch
            {
                FetchEdge.AlbumTracks => w.Scope.Edges.AlbumTracks.Targets(w.Parent),
                FetchEdge.PlaylistTracks => w.Scope.Edges.PlaylistTracks.Targets(w.Parent),
                _ => w.Scope.Edges.Liked.Targets(w.Parent),
            };
            int n = 0;
            for (int i = 0; i < slots.Length; i++) if (slots[i] > Table.None) n++;
            var tracks = new Track[n];
            for (int i = 0, k = 0; i < slots.Length; i++) if (slots[i] > Table.None) tracks[k++] = new Track(slots[i]);
            w.Done.TrySetResult(tracks);
            return true;
        }
    }
}
