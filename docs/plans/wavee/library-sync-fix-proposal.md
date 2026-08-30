# Wavee Library Sync — Diagnosis + Fix Proposal

> Generated 2026-07-03 by a multi-agent diagnosis workflow (5 pipeline mappers + WaveeMusic protocol reference + adversarial verification of every root cause against the code). All 5 root causes survived verification; 0 refuted.

## Diagnosis summary

All five symptoms reduce to composition/wiring failures, not broken algorithms â€” the diff/apply/fetch/router/mapper units are correct and unit-tested but are never assembled into the running app. Root causes, ordered by symptom surface: RC1 â€” the whole inbound sync orchestrator (DealerRouter + CollectionFetcher + FetchRootlistAsync) is wired ONLY in the `--spotify-sync` CLI one-shot (SpotifyLibrarySync.cs, against a throwaway store) and is entirely absent from LiveSessionHost.StartAsync, so at runtime playlist/collection pushes are parsed then dropped, collections are never paged/deltaed, and the rootlist is never re-fetched (explains stale playlists, undiffed collections, and missing inbound follow-state). RC2 â€” the mutation write path is bound to a StubTransport (Services.cs:159/162, Seam.cs:30) that fakes 200-OK with no I/O and is never swapped to the live transport in GoLive (only player/devices/session/connectivity/lyrics are switchable), so every like/save/follow reconciles to Confirmed locally but never networks. RC3 â€” playlist follow is mis-modeled: SetForUri has no playlist branch (falls to 'liked'), no rootlist add/remove strategy exists, and BuildUnion never folds the rootlist into Saved, so follow writes go to the wrong set and IsSaved(playlist) is always false (pill stuck on 'Follow') â€” independent of RC1/RC2. RC4 â€” even with a live transport, SetReplayStrategy hits a fabricated `/collection/{set}/{verb}/{key}` route with wrong set names instead of the `/collection/v2/write` protobuf service, so likes would still fail (currently masked by RC2). RC5 â€” playlist detail fetch is first-open-only (Membership.Count==0 gate, no TTL), compounding RC1's stale-playlist symptom. RC1+RC2 together cover all five symptoms; RC3 is required to fully explain the playlist-follow symptom; RC4/RC5 are latent/compounding causes that must also be fixed for a complete repair.

## Verified root causes

### RC1 — The entire live library-sync orchestrator (DealerRouter + CollectionFetcher + rootlist fetch) is never composed into the running app â€” it exists only in the --spotify-sync CLI one-shot

**Anchor:** `Wavee/SpotifyLive/LiveSessionHost.cs:118` — **verdict:** partially-correct

LiveSessionHost.StartAsync is the real go-live composition root (invoked on login from WaveeApp). Its 'Live data wiring into the SAME store' block (LiveSessionHost.cs:118-163) builds a PlaylistFetcher used ONLY for on-open detail fetch (OnDemandFetch, :137-144) and background header hydration (HydratePlaylistHeadersAsync, :162, which merely iterates the already-persisted store.Rootlist()). It builds and Start()s the LiveDealerTransport (:79/:95) but hands it only to LiveConnect (playback/Connect state). It NEVER constructs a DealerRouter, NEVER constructs a CollectionFetcher / calls FetchSetAsync, and NEVER calls PlaylistFetcher.FetchRootlistAsync. Grep confirms every 'new DealerRouter', 'new CollectionFetcher', 'FetchRootlistAsync', and '.FetchSetAsync' call in shipping code lives exclusively in SpotifyLibrarySync.cs (the `--spotify-sync` one-shot: CollectionFetcher :36, FetchRootlistAsync :41, FetchSetAsync :49, DealerRouter :62-64), SpotifyLibraryProbe.cs (:55/:77/:81), and Wavee.Tests â€” none in LiveSessionHost/Services/WaveeApp. SpotifyLibrarySync opens its OWN throwaway SqliteColdStore, listens 20s, then returns, so even when run it never feeds the live app's store. Consequence at runtime: playlist/collection hm:// pushes are received and parsed by LiveDealerTransport.ReceiveLoop and published to _events, but nothing subscribes Events('hm://playlist/'|'hm://collection/'), so they are dropped; no initial collection paging/delta ever runs; the rootlist is never re-fetched. The library is frozen to whatever a prior manual --spotify-sync last wrote to library.db. This is a pure composition/wiring gap: the diff/apply/fetch/router units themselves are correct and unit-tested. Note the store layer is a correct offline-first read-through cache by design (no TTL/etag/self-invalidation; freshness delegated to these absent fetchers), so it cannot mask the gap.

<details><summary>Verifier notes (adversarial re-check)</summary>

RC1's PRIMARY structural claim is CONFIRMED by direct inspection; its explanation of the "liking a song does nothing" symptom is REFUTED (misattributed to the wrong gap).

CONFIRMED â€” inbound orchestrator never composed into the running app:
- Login composition root: LiveSessionHost.StartAsync is invoked on login from WaveeApp.cs:71 and WaveeApp.cs:92 (interactive + browser paths). The CLI RunAsync (LiveSessionHost.cs:670) is separate.
- LiveSessionHost.cs:118 is exactly the "// Live data wiring into the SAME store the catalog reads" comment; the block runs :119-163.
- Inside that block it constructs ONLY a PlaylistFetcher (LiveSessionHost.cs:124). It is used for (a) OnDemandFetch on detail-open (:139 fetcher.FetchPlaylistAsync, guarded :137-144) and (b) HydratePlaylistHeadersAsync (:162). HydratePlaylistHeadersAsync (:223-256) merely iterates store.Rootlist() (:230, :244) â€” the already-persisted rootlist â€” and calls FetchPlaylistHeaderAsync/FetchPlaylistAsync per entry. It NEVER calls FetchRootlistAsync.
- Grep (excluding obj/bin, tests): every `new DealerRouter`, `new CollectionFetcher`, `FetchRootlistAsync`, `.FetchSetAsync` in shipping code lives ONLY in SpotifyLibrarySync.cs (the `--spotify-sync` one-shot: CollectionFetcher :36, FetchRootlistAsync :41, FetchSetAsync :49, DealerRouter :62) and SpotifyLibraryProbe.cs (probe CLIs, wired at Program.cs:105/113/124). LiveSessionHost/Services/WaveeApp contain none.
- SpotifyLibrarySync.RunAsync is only reachable via Program.cs:133 (`--spotify-sync`); it builds its OWN SqliteColdStore/CachedStore (SpotifyLibrarySync.cs:29-30), listens 20s (:80), returns â€” never feeds the running app's in-memory store.
- Inbound push subscribers in the live path: ClusterMapper subscribes hm://connect-state/v1/cluster (ClusterMapper.cs:112), Connect.cs subscribes hm://pusher (Connect.cs:19), ConnectCommand subscribes Requests (ConnectCommand.cs:109). The ONLY code that subscribes Events("hm://") and dispatches hm://playlist/ + hm://collection/ is DealerRouter (DealerRouter.cs:29,34-35) â€” never constructed in the live path. So playlist/collection pushes are received/parsed by LiveDealerTransport but dropped. This fully explains: playlists stale (no rootlist re-fetch, no /diff), collections never diff (no CollectionFetcher paging/delta at runtime), inbound follow-state never reconciled.

REFUTED / MISATTRIBUTED â€” "liking a song does nothing":
- Liking is an OUTBOUND write, not gated by the missing inbound DealerRouter. Path: SaveButton/PlayerBar â†’ LibraryBridge.ToggleSaved (LibraryBridge.cs:65) â†’ SetSaved (:67-75, does an optimistic Saved.Value flip at :73) â†’ EngineMutationSource.SetSavedAsync (Seam.cs:44) â†’ MutationEngine.Save (optimistic store.SetSaved Pending, Mutation.cs:119-128) + Drain (Seam.cs:48).
- The drain replays over the transport captured in EngineMutationSource, which is StubTransport (Services.cs:159, passed at :162). StubTransport.Request returns Resp(true,200) (Transport.cs:76), so the drain "succeeds" and marks the row Confirmed (Mutation.cs:171) and persists to SQLite (CachedStore.SetSaved :193). So liking is NOT a no-op: the heart flips optimistically and the state persists locally â€” it merely never reaches Spotify's servers.
- The real outbound gap: GoLive (Services.cs:196-202) swaps only Player/Devices/Session/Connectivity/Lyrics; AttachLive (:210) only stores the host+credStore. NOTHING ever replaces the mutation StubTransport with the live dealer transport, despite the aspirational comment "replaced by the live hm:// dealer transport on connect" (Services.cs:159). Grep confirms EngineMutationSource is constructed exactly once (Services.cs:162) with the stub, and .Drain only runs at Seam.cs:48 over that captured stub. This is a DISTINCT wiring gap from the missing DealerRouter/CollectionFetcher orchestrator.

Net: RC1 correctly identifies a real composition gap (the inbound library-sync orchestrator is never wired into the app and lives only in the --spotify-sync/probe CLIs), which genuinely explains the stale-playlists / no-collection-diff / no-inbound-follow-reflection symptoms. But it wrongly folds the "liking does nothing" symptom under "no DealerRouter to reconcile"; that symptom's actual cause is the outbound mutation transport being a permanently-stubbed StubTransport (Services.cs:159) that GoLive never swaps, and liking does apply+persist locally (optimistic), it just never syncs upstream.

</details>

### RC2 — The mutation write transport is permanently a StubTransport that fakes success and is never promoted to the live dealer transport

**Anchor:** `Wavee/App/Services.cs:159` — **verdict:** confirmed

Services.CreateReal builds `new StubTransport()` (Services.cs:159, comment: 'replaced by the live hm:// dealer transport on connect') and passes it to EngineMutationSource (:162), which captures it as a readonly field (_transport, Seam.cs:20/:30). Every write path â€” LibraryBridge.SetSaved optimistic flip (LibraryBridge.cs:73) then _mut.SetSavedAsync (:74) â†’ EngineMutationSource.SetSavedAsync â†’ _mut.Drain(_transport,...) (Seam.cs:47-48) â€” drains against that stub. StubTransport.Request returns Resp(true, empty, 200) with zero network I/O, so MutationEngine.Drain marks the op Confirmed and removes it from the outbox (Mutation.cs:159-172): the intent is discarded as 'synced' while nothing leaves the machine. Unlike Player/Devices/Session/Connectivity/Lyrics (which are Switchable* facades swapped in GoLive, Services.cs:198-202), the mutation transport has NO switchable wrapper and GoLive (Services.cs:196-205) never touches it; the live LiveDealerTransport built in LiveSessionHost is handed only to LiveConnect/metadata/lyrics, never to the MutationEngine. So the transport stays the stub for the whole session.

<details><summary>Verifier notes (adversarial re-check)</summary>

Every cited fact verified by reading the actual code. Services.cs:159 builds `new StubTransport()` with the exact comment "replaced by the live hm:// dealer transport on connect"; Services.cs:162 passes it to EngineMutationSource. Seam.cs:20 declares `readonly ITransport _transport`, assigned only in the ctor (Seam.cs:30), drained at Seam.cs:48 (`await _mut.Drain(_transport,...)`). There is NO setter/swap method on EngineMutationSource (grep for SetTransport/_transport across the whole tree, obj/bin excluded, found none). StubTransport.Request returns `Resp(true, empty, 200)` with zero I/O (Transport.cs:76). SetReplayStrategy.Replay returns r.Ok=true against the stub (Mutation.cs:50-55); MutationEngine.Drain on ok removes the op from the outbox, removes it from durable, and marks "set" ops SyncState.Confirmed (Mutation.cs:159-172) â€” intent discarded as synced without networking. No compensating mechanism: GoLive (Services.cs:196-205) only re-points SwitchablePlayer/Devices/Session/Connectivity/Lyrics and never touches mutations; LiveSessionHost.StartAsync:114 calls svc.GoLive with only player/devices/session/connectivity/lyrics, and the LiveDealerTransport it builds (LiveSessionHost.cs:79) is threaded only into LiveConnect/metadata/lyrics/album-enrichment/video/read-fetchers (LiveSessionHost.cs:118-163), never into the mutation engine, which is never rebuilt on go-live. The only other LiveDealerTransport (SpotifyLibrarySync.cs:61) is a separate one-shot --spotify-sync READ command that performs no write drain. Thus the mutation write transport is permanently the StubTransport for the whole session, faking success and discarding intents. Confirmed for the outbound write path (like/save/follow-unfollow). RC2 asserts only the write transport; the read-side "never diffs" symptom is a distinct path not part of this specific claim.

</details>

### RC3 — Playlist follow is mis-modeled: routed as a collection-set save with no rootlist branch, no rootlist mutation strategy exists, and followed playlists are never folded into the Saved set that the UI reads

**Anchor:** `Wavee/Backend/Seam.cs:84` — **verdict:** confirmed

Following a playlist should be a playlist4 rootlist ADD/REM op (reference: PlaylistMutationService.SetPlaylistFollowedCoreAsync â†’ POST /playlist/v2/user/{username}/rootlist/changes with a ListChanges Op), NOT a collection write. In this code the FollowButton routes a spotify:playlist: URI through the SAME ToggleSavedâ†’SetSavedAsync path as track likes. EngineMutationSource.SetForUri (Seam.cs:84-90) has branches only for track/album/artist/show/episode; a playlist URI falls through to the default _setId='liked', so a follow is written as a liked track into the wrong set. There is NO rootlist add/remove MutationStrategy at all â€” only SetReplayStrategy (collection saves) and OpRebaseStrategy (which is /playlist/v2/{path}/changes for editing a playlist's OWN track membership, not rootlist membership). On the read side, BuildUnion (Seam.cs:92-98) unions only the five collection sets and the fallback set; the rootlist is never folded in, so LibraryBridge.Saved/IsSaved(playlistUri) is permanently false. This defect is independent of RC1/RC2: even with sync and a live transport wired, following a playlist would still write to the wrong endpoint/set and the pill would still never show 'Following'.

<details><summary>Verifier notes (adversarial re-check)</summary>

Every link in RC3's chain holds up under direct inspection.

1. Follow is routed through the SAME path as track likes. FollowButton.OnClick = () => lib.ToggleSaved(_uri) (Wavee/Components/SaveButton.cs:60), identical to SaveButton.OnClick (SaveButton.cs:36). FollowButton is fed playlist URIs (LibraryPage.cs:439/:555), and the playlist detail rail's save affordance is a SaveButton bound to m.ContextUri = spotify:playlist:... (DetailRail.cs:99-100). LibraryBridge.ToggleSaved â†’ SetSaved â†’ _mut.SetSavedAsync(uri, saved) (App/LibraryBridge.cs:65,74).

2. SetSavedAsync routes to a collection save. EngineMutationSource.SetSavedAsync calls _mut.Save(SetForUri(uri), uri, saved) (Backend/Seam.cs:47), which uses the "set" strategy (MutationEngine.Save, Mutation.cs:119-121).

3. SetForUri has NO playlist branch. Seam.cs:84-90 branches only for spotify:track:/album:/artist:/show:/episode:; a spotify:playlist: URI falls through to _setId, which defaults to "liked" (constructor default, Seam.cs:28; production ctor at Services.cs:162 passes no override â†’ "liked"). So a playlist follow is written as a liked-collection entry.

4. The "set" strategy writes a COLLECTION endpoint, not a rootlist op. SetReplayStrategy.Replay POSTs /collection/{setId}/{verb}/{key} (Mutation.cs:52-54). Confirmed a collection write.

5. NO rootlist ADD/REM strategy exists. Only two strategies are registered in production: SetReplayStrategy + OpRebaseStrategy (Services.cs:157-158; also Scaffold.cs:25 registers only SetReplayStrategy). MutationEngine._strategies is keyed "set" and "oprebase" (Mutation.cs:42-43,64-65). OpRebaseStrategy.Replay POSTs /playlist/v2/{path}/changes (Mutation.cs:79-81) â€” that path is the playlist's OWN track membership (EntityKeyâ†’path is the playlist itself), exactly as RC3 states, NOT the user rootlist. No strategy targets /playlist/v2/user/{username}/rootlist/changes; grep for rootlist/changes and SetPlaylistFollowed found zero references in live source (only obj/, docs, and the read-side FetchRootlistAsync which is a GET, PlaylistFetcher.cs:49). The referenced PlaylistMutationService.SetPlaylistFollowedCoreAsync does not exist in this codebase.

6. Read side never folds the rootlist into Saved. EngineMutationSource.BuildUnion unions only AllSets = {liked,albums,artists,shows,episodes} plus the _setId fallback (Seam.cs:79,92-98); the store's rootlist (_rootlist, Store.cs:183/334-340) is never read. The follow pill's IsSaved reads LibraryBridge.Saved (LibraryBridge.cs:61), which mirrors EngineMutationSource.Saved via SavedChanged (LibraryBridge.cs:35,43). Therefore IsSaved(spotify:playlist:...) can only become true if the playlist landed in one of those five collection sets â€” which a genuine rootlist follow would never populate. StoreLibrarySource does have a CollectionKind.Playlists (StoreLibrarySource.cs:312-318), but that is the separate catalog/browse read source, not the mutation Saved set the follow pill reads â€” it does not compensate.

Net: a playlist follow is mis-routed as a "liked" collection write to /collection/liked/add/{playlistUri} (wrong endpoint/set), and the pill's IsSaved permanently excludes the rootlist, so it can never render "Following". Both symptoms explained; no compensating mechanism found. Independent of transport/sync being live.

</details>

### RC4 — SetReplayStrategy targets a fabricated REST endpoint with wrong set names instead of the collection/v2/write protobuf service â€” a real save would still fail once a live transport is wired

**Anchor:** `Wavee/Backend/Mutation.cs:53` — **verdict:** confirmed

SetReplayStrategy.Replay POSTs `/collection/{setId}/{verb}/{entityKey}` with an empty body (Mutation.cs:53). The reference protocol shows the real library write is `POST /collection/v2/write`, Content-Type application/vnd.collection-v2.spotify.proto, body WriteRequest{ username, set, items=[CollectionItem{uri, added_at, is_removed}], client_update_id }. The set names are also wrong: the reference uses set='collection' for BOTH tracks and albums (disambiguated by URI prefix), 'artist' (singular), 'show' (singular); this code uses 'liked'/'albums'/'artists'/'shows'/'episodes' (SetForUri, Seam.cs:85-89). Today this is fully MASKED by RC2 (the stub returns Ok for any path), so it adds no symptom surface yet â€” but it is a genuine second root cause of 'liking a song does nothing': fixing RC2 alone (swapping in a live transport) would produce a real HTTP call to a non-existent route/shape and the save would still not persist. Listed after RC2 because its effect is currently latent.

<details><summary>Verifier notes (adversarial re-check)</summary>

RC4 holds up under direct inspection.

WRONG ENDPOINT/BODY â€” CONFIRMED. Mutation.cs:52-53 (SetReplayStrategy.Replay): `var verb = op.TargetSaved ? "add" : "remove";` then `await t.Request(Channel.Spclient, $"/collection/{op.SetId}/{verb}/{op.EntityKey}", default, ct)`. The path is a fabricated `/collection/{setId}/{verb}/{entityKey}` route; the body is `default` (empty ReadOnlyMemory<byte>); no Content-Type header and no WriteRequest protobuf are constructed.

REAL PROTOCOL EXISTS BUT IS UNUSED â€” CONFIRMED. The correct write shape is present in the repo but wired only for reads. collection2v2.proto:42-47 defines `WriteRequest { username=1, set=2, repeated CollectionItem items=3, client_update_id=4 }` and CollectionItem (uri, added_at, is_removed) â€” exactly the shape the claim cites. The read path (CollectionFetcher.cs:83,91) POSTs `/collection/v2/delta` and `/collection/v2/paging` with Content-Type `application/vnd.collection-v2.spotify.proto` (CollectionFetcher.cs:20,97-98). No `/collection/v2/write` route exists anywhere in non-generated source, and `WriteRequest` is referenced ONLY in generated obj/ protobuf .cs and the .proto â€” never constructed or POSTed by any write path. The sole save/unsave write path is SetReplayStrategy.Replay (Mutation.cs:53).

WRONG SET NAMES â€” CONFIRMED, with in-code corroboration. op.SetId flows from EngineMutationSource.SetForUri (Seam.cs:84-90), which returns the logical names "liked"/"albums"/"artists"/"shows"/"episodes" (AllSets, Seam.cs:79). CollectionFetcher.cs:114-126 explicitly maps these to the real wire sets via WireSet(): likedâ†’"collection", albumsâ†’"collection" (split by URI prefix), artistsâ†’"artist", showsâ†’"show", episodesâ†’"listenlater", and the comment (114-117) states verbatim: "the real wire sets are 'collection' (tracks AND albums, mixed), 'artist', 'show', and 'listenlater' ... all singular; there is no 'albums'/'artists'/'shows'/'episodes' set. Sending those names is the other half of the /paging 400 (InvalidArgument on the set string)." The write path (Mutation.cs:53) sends op.SetId raw â€” i.e. exactly the wrong logical names, never passed through WireSet().

CURRENTLY MASKED (RC2) â€” CONFIRMED. StubTransport.Request returns `new Resp(true, ...)` for any route (Transport.cs:76), so Drain's `return r.Ok` (Mutation.cs:54) always succeeds and the op reconciles to Confirmed (Mutation.cs:171). The real app wires EngineMutationSource with a StubTransport (Services.cs:159, comment "replaced by the live hm:// dealer transport on connect"; mutations built at Services.cs:162, registered at 175). A live ITransport (LiveDealerTransport, LiveDealerTransport.cs:19) exists, so the claim's latent-second-root-cause framing is accurate: swapping it in produces a real call to the non-existent route/shape with wrong set names, and the save would not persist.

MINOR CORRECTION (strengthens claim): the call is not actually a POST. Body is `default` (empty) with no method override; per the ITransport contract (Transport.cs:37: "GET when body empty, else POST") and StubTransport.cs:73, this defaults to GET. So a live transport would issue a bodyless GET to the fabricated route â€” even more wrong than described, but the defect is identical in substance.

No compensating write mechanism exists: grep for WriteRequest / collection/v2/write / any other SetSaved server write path found none outside the read fetcher and generated protos.

</details>

### RC5 — Playlist detail re-fetch is first-open-only (gated on empty membership) with no TTL â€” once a tracklist is persisted it is never re-fetched on-demand

**Anchor:** `Wavee/Backend/Library/StoreLibrarySource.cs:106` — **verdict:** confirmed

EnsureFetchedAsync gates the playlist branch on `_store.Membership(uri).Count == 0` (StoreLibrarySource.cs:106-107): it fires OnDemandFetch only when membership is empty. Albums and artists get freshness re-checks (hydration level / TTL, :108-119) but playlists get none, so once any membership is persisted the on-open path never re-fetches. Combined with RC1 (no push, no rootlist re-diff), there is no runtime path â€” push, TTL, or reopen â€” that ever refreshes a playlist's tracks after first load; DetailPage's UseAsyncResource is also keyed on route.Name so re-navigation reads only the cached membership. This compounds RC1 for the 'playlists stale' symptom rather than causing a distinct one, so it ranks last.

<details><summary>Verifier notes (adversarial re-check)</summary>

RC5's cited behavior is real and verified by direct inspection, and no compensating runtime mechanism exists.

CITED BEHAVIOR CONFIRMED (StoreLibrarySource.cs):
- EnsureFetchedAsync (lines 101-121). Line 103-104: returns immediately if OnDemandFetch is null. Line 106-107: the playlist branch sets `need = _store.Membership(uri).Count == 0` â€” fires OnDemandFetch ONLY when persisted membership is empty (first open). No TTL, no revision/freshness check.
- Contrast confirmed: album branch (108-112) re-fetches on `album.Hydration < AlbumHydrationLevel.Full` or empty tracks; artist branch (113-119) re-fetches on `DateTimeOffset.UtcNow - a.FetchedAt > ArtistOverviewTtl` (12h TTL, line 97-118). Playlists get neither hydration nor TTL recheck.
- Both single-item read (GetPlaylistAsync line 51-59 â†’ EnsureFetchedAsync) and StreamTracksAsync (line 123-130 â†’ EnsureFetchedAsync) go through the same gate, so no read path forces a re-fetch of a persisted tracklist.

OnDemandFetch WIRING CONFIRMED: LiveSessionHost.cs:137-144 sets OnDemandFetch; line 139 routes playlists to fetcher.FetchPlaylistAsync (which calls store.SetMembership, PlaylistFetcher.cs:32-37). So the write path exists but is only reachable when membership is empty.

REOPEN PATH CONFIRMED: DetailPage.cs:54 keys UseAsyncResource on `route.Name`, so re-navigating to a playlist re-invokes LoadAsyncâ†’GetPlaylistAsyncâ†’EnsureFetchedAsync, which hits the same empty-membership gate â€” cached membership is returned unchanged.

BACKGROUND HYDRATION does NOT compensate: LiveSessionHost HydratePlaylistHeadersAsync (invoked line 162) skips any playlist with a tracklist â€” line 249 `if (store.Membership(e.Uri).Count > 0) continue;` â€” so it also never refreshes a persisted tracklist.

REFUTATION ATTEMPT (push path) FAILED TO REFUTE: A push-driven re-sync DOES exist in code â€” DealerRouter.OnPlaylist (DealerRouter.cs:38-63) applies parent-rev ops in place or calls markPlaylistStale; SpotifyLibrarySync.cs:62-64 wires markPlaylistStale â†’ `_ = playlistFetcher.FetchPlaylistAsync(uri, ct)`. BUT the only DealerRouter instantiation is SpotifyLibrarySync.cs:62, and SpotifyLibrarySync.RunAsync is invoked only from Program.cs:133 â€” a CLI subcommand/probe. The actual GUI runtime is LiveSessionHost.RunAsync (Program.cs:142), which creates a LiveDealerTransport only for Connect/now-playing (line 79, 94-95) and wires NO DealerRouter, no hm:// playlist/collection push routing, no markPlaylistStale (grep of LiveSessionHost.cs shows only LiveDealerTransport, no DealerRouter). So in the real app there is no push, no TTL, and no reopen path that refreshes a persisted playlist tracklist.

Net: the on-open/reopen playlist path is first-open-only (empty-membership gated) with no TTL, exactly as claimed. The claim's self-ranking (compounds RC1, ranks last) is a prioritization note, not a factual assertion to verify.

</details>

---

# Proposal: Fix Playlist + Collection Sync in Wavee

## 0. Problem restatement (what the verified root causes actually mean)

The engine has **correct, unit-tested sync units** â€” `PlaylistFetcher`, `CollectionFetcher`, `CollectionDeltaApplier`, `PlaylistDiffApplier`, `DealerRouter`, `MutationEngine` with `SetReplayStrategy`/`OpRebaseStrategy`, and a durable SQLite outbox. What is broken is **composition and protocol fidelity**, in five independent places:

| RC | One-line defect | Where |
|----|-----------------|-------|
| RC1 | The inbound sync orchestrator (rootlist fetch + `CollectionFetcher` + `DealerRouter`) is only composed in the `--spotify-sync` CLI one-shot (`SpotifyLibrarySync.cs:36-64`), never in the live app path (`LiveSessionHost.cs:118-163`). | `LiveSessionHost.StartAsync` |
| RC2 | The mutation write transport is a permanent `StubTransport` (`Services.cs:159`, captured readonly at `Seam.cs:30`); `GoLive` (`Services.cs:196-205`) never swaps it. | `Services.cs` / `Seam.cs` |
| RC3 | Playlist follow is routed as a `"liked"` collection save (`Seam.cs:84-90` has no playlist branch â†’ default `_setId="liked"`), no rootlist strategy exists, and `BuildUnion` (`Seam.cs:92-98`) never folds the rootlist, so the pill can never show "Following". | `Seam.cs` / `Mutation.cs` |
| RC4 | `SetReplayStrategy.Replay` (`Mutation.cs:52-53`) hits a fabricated `/collection/{setId}/{verb}/{key}` route with an empty body and the wrong logical set names â€” the real protocol is `POST /collection/v2/write` with a `WriteRequest` protobuf. | `Mutation.cs` |
| RC5 | Playlist on-open fetch is first-open-only (`StoreLibrarySource.cs:106-107` gates on empty membership) with no TTL and no `/diff`. | `StoreLibrarySource.cs` |

The store layer is already a correct offline-first read-through cache (`CachedStore.cs:38-41` bulk-loads entities + saved-state at startup; `SetMembership`/`SetRootlist` dual-write to SQLite at `CachedStore.cs:104-126`). It is not the problem; it is the **substrate** the fix builds on.

---

## 1. Sync architecture: one owner, one loop, one source of truth

### 1.1 Source of truth
`IStore` (`Store.cs:25-62`, impl `CachedStore` over `SqliteColdStore`) is and remains the single source of truth for library/playlist state. Every read path already flows through it (`StoreLibrarySource`, `EngineMutationSource.BuildUnion`, `LibraryBridge.Saved`). The fix does **not** introduce a second cache â€” it adds the missing *writers* into this store and serializes them.

### 1.2 New single owner: `LibrarySync` (serialized sync loop)
Create **`Wavee/Backend/Sync/LibrarySync.cs`** (Backend, so it is unit-testable against `StubTransport.PushEvent`, exactly like `DealerRouter`). It owns:

- the injected `PlaylistFetcher`, `CollectionFetcher`, and rootlist fetch;
- a single-consumer `System.Threading.Channels.Channel<SyncCommand>` (unbounded, `SingleReader=true`) draining on one background task â€” the **serialization point** (Â§6);
- the `MutationEngine.Drain` trigger, so inbound diffs and outbound write reconciliation never interleave against the store on two threads.

`SyncCommand` is a POD discriminated struct:
```
enum SyncKind { InitialHydrate, RootlistPush, PlaylistPush, CollectionPush, DrainWrites }
readonly record struct SyncCommand(SyncKind Kind, string Uri, byte[]? ParentRev, byte[]? NewRev, IReadOnlyList<PlaylistOp>? Ops, string? Set);
```
The loop is the only code that calls `PlaylistFetcher.*`, `CollectionFetcher.FetchSetAsync`, `PlaylistDiffApplier.Apply`, and `MutationEngine.Drain` at runtime. `DealerRouter`, the write path, and the initial-hydrate bootstrap all **enqueue commands** rather than touching the store directly.

### 1.3 Server revisions / sync tokens (storage + advance)
Already present, reuse verbatim:
- **Collection sync token** â€” `collection_rev(account,set_id,revision,synced_at)` (`SqliteColdStore.cs:53`), read/written via `GetCollectionRevision`/`SetCollectionRevision` (`SqliteColdStore.cs:151-161,284`). `CollectionFetcher` advances it after each successful delta/page (`CollectionFetcher.cs:57,77`).
- **Playlist revision** â€” `playlists(uri,base_rev)` (`SqliteColdStore.cs:56`), `GetPlaylistRevision` (`SqliteColdStore.cs:180-189`), surfaced by `IStore.PlaylistRevision` (`Store.cs:53`, `CachedStore.cs:121`) and written by `SetMembership`.

**Gap to close (new):** the **rootlist has no stored revision**. `FetchRootlistAsync` (`PlaylistFetcher.cs:49-76`) discards `slc.Revision`, and `SetRootlist`/`ReplaceRootlist` (`Store.cs:332-336`, `SqliteColdStore.cs:250-279`) carry no rev column. Add a rootlist revision:
- Store the rootlist under the `playlists` table keyed by its URI (`spotify:user:{u}:rootlist`) â€” no schema change needed, reuse `playlists.base_rev` via a new `IStore.SetRootlist(entries, byte[]? rev)` overload + `IStore.RootlistRevision()`.
- This is what makes revision-gated rootlist `/diff` (Â§2.3) possible.

### 1.4 Cold-start hydration vs online reconciliation
- **Cold start (offline-first):** unchanged â€” `CachedStore` ctor bulk-loads from SQLite (`CachedStore.cs:38-41`); the UI renders the persisted library instantly with zero network.
- **Online reconciliation:** on go-live, `LibrarySync` enqueues one `InitialHydrate`. The loop runs, in order: (1) rootlist (revision-gated diff if a rev is stored, else full â€” Â§2.3), (2) each collection set (token-gated delta, else full paging â€” Â§3), (3) then opens the dealer firehose + `DealerRouter` for steady-state pushes. All under `store.BeginBulk()` (`Store.cs:371`) so the whole reconcile coalesces into **one** UI refresh signal.

---

## 2. Playlist sync

### 2.1 Initial fetch + rootlist sync (composition â€” fixes RC1)
In **`LiveSessionHost.StartAsync`**, inside the existing `if (svc.RealStore is { } store â€¦)` block (`LiveSessionHost.cs:119-163`), replace the header-only hydration with real sync composition. The `PlaylistFetcher` already exists there (`LiveSessionHost.cs:124`); add the missing pieces that today live only in `SpotifyLibrarySync.cs:36-64`:

```
var collectionFetcher = new CollectionFetcher(live.Pipeline, () => live.BaseUrl, () => live.Username, store,
    s => cold.GetCollectionRevision(s), (s, r) => cold.SetCollectionRevision(s, r, nowUnix), md.SyncAllAsync);
var sync = new LibrarySync(store, fetcher, collectionFetcher, mutEngine, () => sessionHost.Current, log, cts.Token);
var router = new DealerRouter(transport, store,
    (uri, parent, newRev, ops) => sync.Enqueue(SyncCommand.PlaylistPush(uri, parent, newRev, ops)),
    set => sync.Enqueue(SyncCommand.CollectionPush(set)));
sync.Enqueue(SyncCommand.InitialHydrate);          // rootlist + all sets, revision/token-gated
```
Two composition-root plumbing needs: `LiveSessionHost` currently only sees `IStore store` â€” it must also see the concrete `SqliteColdStore cold` (for the revision getter/setter) and the `MutationEngine` + `SessionContextHost`. Expose them on `Services` (add `public SqliteColdStore? RealCold`, `public MutationEngine? RealMutations`, `public SessionContextHost? RealSessionHost`, set in `CreateReal` alongside `RealStore` at `Services.cs:188-189`).

Delete/retire `HydratePlaylistHeadersAsync` (`LiveSessionHost.cs:223-256`) as the freshness path â€” `InitialHydrate`'s rootlist fetch (`FetchRootlistAsync`) + on-open detail fetch supersede it. (Keep the cover-mosaic hydration as a cosmetic follow-up if desired, but it must not be the *only* rootlist writer.)

### 2.2 Diff on notification (parent-rev fast path â€” already correct)
`DealerRouter.OnPlaylist` (`DealerRouter.cs:38-63`) already implements the reference "parent-rev gate": if the playlist is resident and `stored == parent_revision` byte-equal, it applies ops in place via `PlaylistDiffApplier.Apply` (`DealerRouter.cs:52-59`) with zero round-trip; otherwise it marks stale. **The only change** is that its two callbacks must enqueue into `LibrarySync` (Â§2.1) instead of directly firing `FetchPlaylistAsync`, and its constructor signature widens to pass `parent/newRev/ops` through to the loop for the in-place apply decision (or keep the in-place apply inside the router but funnel the store write through the loop â€” pick one; recommended: router decodes, loop applies, for a single writer).

### 2.3 Revision-gated `/diff` + fallback full refetch (new â€” fixes RC5)
Add **`PlaylistFetcher.FetchPlaylistDiffAsync(uri, ct)`** mirroring the reference `GetPlaylistDiffAsync`:
- `GET /playlist/v2/{path}/diff?revision={enc}&handlesContent=&hint_revision={enc}` where `{enc} = Uri.EscapeDataString(FormatRevision(rev))` and `FormatRevision` = `"{int32BE_counter},{lowerhexhash}"` with the comma percent-encoded (`%2C`). `handlesContent=` is required as an empty param.
- Response handling: **200** â†’ `SelectedListContent.Diff` â†’ `PlaylistWireMapper.MapOps` â†’ `PlaylistDiffApplier.Apply` onto the resident membership â†’ `SetMembership(uri, list, to_revision)`. **304** â†’ up-to-date, no-op. **509** (stale editorial) or torn apply (`ArgumentOutOfRangeException` from the applier) â†’ fall back to full `FetchPlaylistAsync` (`PlaylistFetcher.cs:32-39`).

The `LibrarySync` `PlaylistPush` handler decision tree:
1. resident + `stored == parent` â†’ apply ops in place (the router's fast path);
2. resident but rev mismatch â†’ `FetchPlaylistDiffAsync` (revision-gated);
3. not resident / cold â†’ mark dirty only (anti-herd, as today), revalidate lazily on next open.

**RC5 on-open freshness:** change `StoreLibrarySource.EnsureFetchedAsync` (`StoreLibrarySource.cs:106-107`) from the empty-membership gate to a revision/TTL recheck symmetric with albums/artists (`StoreLibrarySource.cs:108-119`). On open, if the resident revision is older than a short TTL (or unknown), issue `FetchPlaylistDiffAsync` (cheap 304 when current) rather than never re-fetching. This is what unblocks "reopen a playlist refreshes its tracklist".

### 2.4 UI signal update (already wired, no change)
Every store write bumps a per-URI version and emits `StoreChange` (`Store.cs:348-353`). `StoreLibrarySource` re-raises `CollectionsChanged` (`StoreLibrarySource.cs:42,48`) and the detail-page `UseAsyncResource` re-reads. No new signal plumbing â€” the diff/apply lands in the store and the existing reactive chain repaints. (The one addition is the rootlistâ†’`Saved` fold for the follow pill, Â§4.3.)

---

## 3. Collection sync

`CollectionFetcher` (`CollectionFetcher.cs`) is already correct and protocol-faithful for **reads**: token-gated `/collection/v2/delta` (`CollectionFetcher.cs:80-85`), full `/collection/v2/paging` loop under `BeginBulk` (`CollectionFetcher.cs:63-77`), correct `Content-Type: application/vnd.collection-v2.spotify.proto` (`CollectionFetcher.cs:20,97`), correct wire-set mapping via `WireSet()` (`CollectionFetcher.cs:118-126`: `liked/albumsâ†’collection`, `artistsâ†’artist`, `showsâ†’show`, `episodesâ†’listenlater`) and URI-prefix disambiguation of the shared `collection` set (`CollectionFetcher.cs:129-145`), sync-token advance (`CollectionFetcher.cs:57,77`).

**The only defect is that it is never called at runtime** (RC1). Fixes:
- **Paged initial fetch + delta:** `LibrarySync`'s `InitialHydrate` loops the five logical sets (`"liked","albums","artists","shows","episodes"` â€” same list as `SpotifyLibrarySync.cs:19` / `Seam.cs:79`) through `FetchSetAsync`. With no stored token it pages; with a token it deltas and falls back to paging when `delta_update_possible==false` (`CollectionFetcher.cs:52-61`).
- **Delta application:** already via `CollectionDeltaApplier.Apply` per item (add/remove by `is_removed`), tokens advancing after apply. No change.
- **Push-driven delta:** `DealerRouter.OnCollection` (`DealerRouter.cs:65-67`) marks the set stale â†’ `LibrarySync` `CollectionPush` handler runs `FetchSetAsync(set)` (token-gated delta â€” cheap). This is the reference "re-run incremental sync" pattern for a non-parseable/opaque push payload.

**Write acknowledgment** for collection writes is covered by Â§4 (the outbox drain marks the row Confirmed on the real `/collection/v2/write` 200, and the inbound `PubSubUpdate` on `hm://collection/collection/{user}` reconciles the authoritative state).

---

## 4. Writes (optimistic â†’ server â†’ rollback/reconcile)

### 4.1 Transport promotion (fixes RC2) â€” the Switchable pattern
The codebase already has the exact idiom for runtime backend swap: `SwitchablePlayer/Devices/Session/Connectivity/Lyrics` (`Services.cs:183-186`, swapped in `GoLive`, `Services.cs:196-205`). The mutation transport is the one that was left out.

Create **`Wavee/Backend/SwitchableTransport.cs`** implementing `ITransport` (`Transport.cs:34-46`), wrapping an inner `ITransport` with a volatile swap (`SetInner`), delegating `Request/Events/Requests/Reply/Publish`. Then:
- `Services.CreateReal` (`Services.cs:159-162`): `var transport = new SwitchableTransport(new StubTransport());` passed to `EngineMutationSource`. Store it as `Services.MutTransport`.
- `GoLive` (or a dedicated `AttachLiveTransport`) calls `MutTransport.SetInner(liveDealerTransport)` â€” the same `LiveDealerTransport` built at `LiveSessionHost.cs:79`. Now `EngineMutationSource.SetSavedAsync â†’ _mut.Drain(_transport, â€¦)` (`Seam.cs:47-48`) replays over the live socket.

No change to `EngineMutationSource`'s readonly `_transport` field â€” it holds the switchable, which is stable for the session.

### 4.2 Real collection write (fixes RC4) â€” like/unlike, save/unsave album
Rewrite `SetReplayStrategy.Replay` (`Mutation.cs:50-55`) to the real protocol. Add **`CollectionWriteMapper`** (or extend `CollectionWireMapper`) to build the protobuf using the already-generated `Wavee.Protocol.Collection` types (`Col.WriteRequest`, `Col.CollectionItem` â€” same namespace `CollectionFetcher` uses at `CollectionFetcher.cs:7,82,89`):
```
POST /collection/v2/write
Content-Type / Accept: application/vnd.collection-v2.spotify.proto
body = WriteRequest {
    username, set = WireSet(op.SetId),                 // "collection" | "artist" | "show" | "listenlater"
    items = [ CollectionItem { uri = op.EntityKey, added_at = <unix SECONDS>, is_removed = !op.TargetSaved } ],
    client_update_id = Guid.NewGuid().ToString("N")
}
```
- **Move `WireSet` to a shared owner.** It currently lives privately in `CollectionFetcher.cs:118-126`. Promote it to a `public static` helper (e.g. `CollectionSets.WireSet`) so the write strategy maps the logical set name to the wire set â€” this is the exact fix the `CollectionFetcher.cs:114-117` comment predicts ("Sending those names is the other half of the /paging 400"). The write must send `"collection"`/`"artist"`/`"show"`/`"listenlater"`, never `"albums"`/`"artists"`/`"shows"`/`"episodes"`.
- `added_at` is **int32 UNIX seconds** for collection (contrast: playlist `ItemAttributes.timestamp` is int64 ms â€” Â§4.4).
- The strategy must POST with the body + content-type; note `LiveDealerTransport.Request` (`LiveDealerTransport.cs:58-71`) defaults the verb to POST when body is non-empty and stamps `application/protobuf` only if no Content-Type is supplied â€” so pass the vendor content-type explicitly in the `headers` arg.

Optimistic apply (`Mutation.cs:47-48`) and rollback (`Mutation.cs:57-58`) are unchanged and correct; reconcile-to-`Confirmed` on 200 (`Mutation.cs:171`) now reflects a real server write.

### 4.3 Playlist follow/unfollow = rootlist ADD/REM (fixes RC3)
This is a **different protocol** from collection writes and needs a new strategy + routing.

**New strategy `RootlistFollowStrategy` (type `"rootlist"`)** in `Mutation.cs`:
- `ApplyOptimistic`: mark follow-state locally so the pill flips this frame. Two coordinated writes: (a) `store.SetSaved("playlists", playlistUri, follow, Pending)` for the `Saved` union (Â§4.5), and (b) optimistically insert/remove the rootlist entry so the sidebar updates (best-effort; the authoritative rootlist push reconciles).
- `Replay`: build a `ListChanges` rootlist op and POST it:
  ```
  POST /playlist/v2/user/{username}/rootlist/changes
  Content-Type: application/x-www-form-urlencoded          // MANDATORY despite binary protobuf body
  + first-party identity headers (Â§4.4)
  Follow:   Op{ ADD, Add{ from_index=0, items=[Item{ uri, attributes{ timestamp=<ms>, public=true } }] } }
  Unfollow: Op{ REM, Rem{ from_index=<currentIndex>, length=1 } }
  body = ListChanges{ base_revision=<rootlist rev>, deltas=[Delta{ ops, info{ user, timestamp } }],
                      want_resulting_revisions=true, want_sync_result=true, nonces=[rand] }
  ```
  Reuse `PlaylistWireMapper.BuildChanges` (`PlaylistWireMapper.cs:83-96`) as the base, extended to accept the `info`/`nonces`/`want_*` fields and to accept an `ItemAttributes{timestamp,public}` on the ADD item (today `BuildAdd` only sets `Uri`, `PlaylistWireMapper.cs:107-114`). The `base_revision` and the current index come from the store's rootlist + rootlist revision (Â§1.3). A **409** conflict â†’ refetch rootlist + retry (surfaces as `!Ok` â†’ the outbox re-attempts, mirroring `OpRebaseStrategy`'s comment at `Mutation.cs:82`).

**New `MutationEngine.Follow(playlistUri, follow)`** (sibling to `Save`, `Mutation.cs:119-128`) that creates a `"rootlist"`-typed `OutboxOp`. Register `RootlistFollowStrategy` in both production wirings: `Services.cs:157-158` and `Scaffold.cs:25`.

**Routing (fixes the mis-route):** `EngineMutationSource.SetSavedAsync` (`Seam.cs:44-49`) branches on URI kind:
```
if (uri.StartsWith("spotify:playlist:")) _mut.Follow(uri, saved);
else                                     _mut.Save(SetForUri(uri), uri, saved);
await _mut.Drain(_transport, _ctx(), ct);
```
`SetForUri` (`Seam.cs:84-90`) no longer needs a playlist case (it never reaches the collection path), removing the silent fall-through to `"liked"`.

### 4.4 First-party identity headers (new, required for all `/playlist/v2/*` writes)
The reference gateway silently no-ops (200 to a passive read handler) unless rootlist/playlist mutations carry the first-party identity. Add a shared header builder (e.g. `SpotifyHeaders.PlaylistV2Mutation`) supplying: `Content-Type: application/x-www-form-urlencoded`, `App-Platform`, `Spotify-App-Version: 128800483`, `spotify-playlist-sync-reason: CAk=`, `Accept-Language: en`, `Cache-Control: no-store`, `spotify-accept-geoblock: dummy`, `spotify-dsa-mode-enabled: false`, `Origin`, plus the `client-token`. These pass through `LiveDealerTransport.Request`'s `headers` arg (`LiveDealerTransport.cs:63-66`). `SpotifyClientIdentity.AppVersionHeader` already exists (`LiveSessionHost.cs:396`). Responses may be zstd-compressed (`Content-Encoding: zstd`, magic `28 B5 2F FD`) â€” add a decompress guard on the reply path.

Timestamp units to respect (the cross-cutting gotcha): collection `added_at` = **int32 seconds**; playlist/rootlist `ItemAttributes.timestamp` = **int64 milliseconds**.

### 4.5 Fold rootlist follow-state into `Saved` (fixes RC3 read side)
`EngineMutationSource.BuildUnion` (`Seam.cs:92-98`) unions only `AllSets` + the fallback. Add the followed playlists:
- Maintain a logical `"playlists"` set: `LibrarySync`'s rootlist sync marks each rootlist playlist entry via `store.SetSaved("playlists", uri, true, Confirmed)` (and clears removed ones on a rootlist diff), so `BuildUnion` gains `foreach (var u in store.SavedUris("playlists"))`.
- `OnStoreChange` (`Seam.cs:53-77`): the rootlist bump arrives as `StoreChange("rootlist")` (`Store.cs:335`). Handle it as a full-union rebuild (like the bulk branch, `Seam.cs:58-62`) so the `Following` pill flips when the rootlist changes inbound.
- `FollowButton.IsSaved` (`SaveButton.cs:52` â†’ `LibraryBridge.IsSaved` â†’ `Saved.Value.Contains`) then correctly renders "Following". No UI change.

### 4.6 Rollback + reconcile summary
Optimistic flip (`LibraryBridge.SetSaved`, `LibraryBridge.cs:73`) â†’ outbox (`MutationEngine.Save`/`Follow`) â†’ drain over the live transport â†’ **200** reconciles to `Confirmed` by identity (`Mutation.cs:159-172`, safe against a newer coalesced intent) â†’ **terminal failure** (â‰¥10 attempts, `Mutation.cs:184`) rolls back the optimistic write + dead-letters. Inbound dealer pushes (`PubSubUpdate` for collections, `RootlistModificationInfo` for follows) provide the authoritative reconcile.

---

## 5. Realtime: dealer subscriptions + notificationâ†’diffâ†’storeâ†’signal flow

### 5.1 Subscriptions to route
`DealerRouter` subscribes exactly one prefix: `transport.Events("hm://")` (`DealerRouter.cs:29`) and demuxes `hm://playlist/` vs `hm://collection/` (`DealerRouter.cs:34-35`). `LiveDealerTransport` already fans MESSAGE frames to prefix subscribers (`LiveDealerTransport.cs:167-168, 229-238`). The reference-subscribed prefixes are `hm://playlist/`, `hm://collection/`, and `collection-update`. The current single `hm://` subscription covers the first two; **the missing piece is purely that `DealerRouter` is never constructed in the live path** (RC1) â€” Connect already subscribes its own prefixes independently (`ClusterMapper`, `Connect.cs`, `ConnectCommand`), so adding `DealerRouter` is non-conflicting.

Rootlist pushes arrive on `hm://playlist/â€¦/rootlist` (and legacy `hm://playlist/{id}`); collection track/album pushes on `hm://collection/collection/{user}`. Extend `DealerRouter` to decode the rootlist topic (`RootlistModificationInfo{ new_revision, parent_revision, ops }`) â€” today `OnPlaylist` (`DealerRouter.cs:38-63`) only handles `PlaylistModificationInfo`. Add a rootlist branch that enqueues a `RootlistPush` command (parent-rev gated against the stored rootlist revision from Â§1.3, else full rootlist refetch).

### 5.2 Flow
`LiveDealerTransport.ReceiveLoop` (`LiveDealerTransport.cs:167`) â†’ `WireEvent` â†’ `DealerRouter.OnEvent` (`DealerRouter.cs:32`) decodes topic + protobuf â†’ **enqueues a `SyncCommand`** into `LibrarySync` â†’ the single loop applies (in-place ops / `/diff` / `/delta` / full refetch) to `IStore` â†’ `StoreChange` emitted (`Store.cs:352`) â†’ `StoreLibrarySource.CollectionsChanged` + `EngineMutationSource.SavedChanged` â†’ `LibraryBridge.Saved` signal (`LibraryBridge.cs:43`) marshalled to the UI thread via `post` â†’ components re-skin. Payload decoding (base64-concat â†’ gunzip if `Transfer-Encoding: gzip` â†’ protobuf) is already handled by `DealerFrameParser` upstream of the router.

---

## 6. Ordering / concurrency

**Single-writer discipline** is the design rule. Today three actors can write the store concurrently: on-open fetch (`OnDemandFetch`, `LiveSessionHost.cs:137`), inbound pushes (via `DealerRouter`), and outbound write drains (`MutationEngine.Drain`). The store is lock-guarded (`InMemoryStore` `_gate`), so it won't corrupt, but *logical* races exist (a `/diff` apply racing a concurrent full refetch of the same playlist; a write drain racing an inbound reconcile of the same URI).

Serialize through `LibrarySync`'s single command loop:
- **Inbound diffs/deltas/rootlist** â†’ `SyncCommand` (already the design in Â§1.2/Â§5.2).
- **Outbound writes** â†’ `EngineMutationSource.SetSavedAsync` still applies the optimistic store write inline (must be instant for the UI frame), but the **drain** is enqueued as `DrainWrites` so replay+reconcile runs on the same loop, never concurrently with an inbound diff for the same entity.
- **Per-entity coalescing** is already handled inside `MutationEngine` (set rows coalesce by `(set,entity)`, `Mutation.cs:96-102,125`; oprebase rows append). The loop provides the cross-cutting ordering the engine can't.
- **On-open fetch** stays direct (it targets a single URI the user is viewing and only writes that URI's membership); to be fully strict it can also be routed as a `SyncCommand`, but that is optional hardening â€” the practical races are diff-vs-drain, which the loop covers.

`BeginBulk`/`EndBulk` (`Store.cs:371-382`) remains the coalescing primitive for multi-write bursts (initial hydrate, multi-page collection snapshot).

---

## 7. Phased landing plan

Each phase builds cleanly (`dotnet build src/FluentGpu.slnx`), keeps the `FluentGpu.VerticalSlice` gates green, and is independently shippable. Wavee-side verification uses the live app (`--real-backend` + login) and the existing `--connect-live` smoke path (`LiveSessionHost.RunAsync`, `LiveSessionHost.cs:670`).

### Phase 1 â€” Compose the inbound read orchestrator (fixes RC1; unblocks RC5 read path)
**Files changed:** `Wavee/SpotifyLive/LiveSessionHost.cs` (:118-163 â€” build `CollectionFetcher`, call `FetchRootlistAsync`, construct `DealerRouter`, kick `InitialHydrate`); `Wavee/App/Services.cs` (:188-189 â€” expose `RealCold`, `RealMutations`, `RealSessionHost`).
**New files:** `Wavee/Backend/Sync/LibrarySync.cs` (the loop, minus writes for now).
**What it fixes:** playlists re-fetch (rootlist + on-open), collections page/delta at runtime, inbound `hm://playlist`/`hm://collection` pushes apply instead of being dropped.
**Test:** unit â€” `LibrarySync` drains `InitialHydrate` against a `StubTransport` with crafted `SelectedListContent`/`PageResponse`, asserting store membership + saved-set + tokens; `DealerRouter` push via `StubTransport.PushEvent` enqueues + applies. Live â€” log in, confirm rootlist count + collection counts populate and a playlist edited on the phone reflects within seconds.

### Phase 2 â€” Promote the write transport (fixes RC2)
**Files:** `Wavee/App/Services.cs` (:159-162, :196-205 â€” `SwitchableTransport`, swap on `GoLive`); `Wavee/SpotifyLive/LiveSessionHost.cs` (:114 â€” pass the live transport to the swap).
**New files:** `Wavee/Backend/SwitchableTransport.cs`.
**What it fixes:** write drains now reach the network (necessary precondition; still hits the wrong endpoint until Phase 3).
**Test:** unit â€” `SwitchableTransport.SetInner` routes `Request` to the new inner; assert `EngineMutationSource` drains over the swapped transport. Live â€” a like now produces an outbound HTTP call (observe via transport log).

### Phase 3 â€” Real collection write (fixes RC4)
**Files:** `Wavee/Backend/Mutation.cs` (:50-55 â€” `SetReplayStrategy.Replay` â†’ `/collection/v2/write`); `Wavee/Backend/Collections/CollectionFetcher.cs` (:118-126 â€” promote `WireSet` to shared owner).
**New files:** `Wavee/Backend/Collections/CollectionWriteMapper.cs` (builds `WriteRequest`); shared `CollectionSets.WireSet`.
**What it fixes:** liking a track and saving an album persist server-side (survive the next sync).
**Test:** unit â€” `SetReplayStrategy.Replay` posts to `/collection/v2/write` with the correct wire set, `is_removed` flag, and vendor content-type (assert via `StubTransport.LastRequestRoute/Body/Headers`, `Transport.cs:63-66`). Live â€” like a song, confirm it appears in Liked Songs on another client; verify round-trip after a fresh `InitialHydrate`.

### Phase 4 â€” Playlist follow via rootlist (fixes RC3)
**Files:** `Wavee/Backend/Seam.cs` (:44-49 routing, :79/:92-98 union fold, :53-77 rootlist change handling); `Wavee/Backend/Mutation.cs` (new `RootlistFollowStrategy` + `MutationEngine.Follow`); `Wavee/App/Services.cs` (:157-158) + `Scaffold.cs` (:25) strategy registration; `Wavee/Backend/Playlists/PlaylistWireMapper.cs` (:83-114 â€” rootlist `ListChanges` with info/nonces/attributes); `Wavee/Backend/Spotify/SpotifyHeaders.cs` (first-party header builder); rootlist revision storage in `Store.cs`/`CachedStore.cs`/`SqliteColdStore.cs` (Â§1.3).
**What it fixes:** follow/unfollow a playlist writes the correct rootlist op; the pill renders "Following" from the folded rootlist state.
**Test:** unit â€” follow routes to `"rootlist"` strategy, posts `/playlist/v2/user/{u}/rootlist/changes` form-urlencoded with a valid `ListChanges`; `BuildUnion` includes rootlist playlists; `IsSaved(spotify:playlist:â€¦)` true after a rootlist push. Live â€” follow a playlist, confirm it appears in the sidebar + on another client, unfollow reverses it, and a follow from the phone flips the pill inbound.

### Phase 5 â€” Revision-gated `/diff` + on-open freshness (completes RC5)
**Files:** `Wavee/Backend/Playlists/PlaylistFetcher.cs` (new `FetchPlaylistDiffAsync` with `FormatRevision`/304/509 handling); `Wavee/Backend/Library/StoreLibrarySource.cs` (:106-107 â€” TTL/revision recheck for playlists); `Wavee/Backend/Sync/LibrarySync.cs` (diff decision tree Â§2.3); `DealerRouter` rootlist-topic decode (Â§5.1).
**What it fixes:** reopening a playlist refreshes it (cheap 304 when current); rev-mismatch pushes take the `/diff` path instead of a full refetch; rootlist pushes apply incrementally.
**Test:** unit â€” `FetchPlaylistDiffAsync` applies a crafted `Diff`, no-ops on 304, falls back to full on 509/torn apply; `EnsureFetchedAsync` re-fetches a stale playlist. Live â€” edit a playlist externally, reopen, confirm the change without a full reload; toggle airplane mode to confirm graceful fallback.

### Phase 6 â€” Concurrency hardening (serialization)
**Files:** `Wavee/Backend/Seam.cs` (route the drain as `DrainWrites` into `LibrarySync`); `Wavee/Backend/Sync/LibrarySync.cs` (single-loop drain + inbound serialization); optional `OnDemandFetch` routing.
**What it fixes:** eliminates diff-vs-drain and diff-vs-refetch logical races on the same entity; makes the sync path a strict single writer.
**Test:** unit â€” interleave a `PlaylistPush` and a `DrainWrites` for the same URI, assert deterministic final store state + one coalesced signal; stress a burst of pushes + writes and assert no lost/duplicated membership. Regression â€” full `VerticalSlice` zero-alloc gates stay green (the loop uses a pre-allocated channel; command structs are POD).

---

## Appendix: file/type change map

**New files**
- `Wavee/Backend/Sync/LibrarySync.cs` â€” single-owner serialized sync loop + `SyncCommand`.
- `Wavee/Backend/SwitchableTransport.cs` â€” runtime transport swap (mirrors `SwitchablePlayer`).
- `Wavee/Backend/Collections/CollectionWriteMapper.cs` â€” `WriteRequest` builder; `CollectionSets.WireSet` shared owner.
- `RootlistFollowStrategy` (in `Wavee/Backend/Mutation.cs`) â€” `"rootlist"` mutation type.
- First-party header builder (in `Wavee/Backend/Spotify/SpotifyHeaders.cs`).

**Changed files**
- `Wavee/SpotifyLive/LiveSessionHost.cs` (:114, :118-163) â€” compose `CollectionFetcher`/`DealerRouter`/`LibrarySync`, call `FetchRootlistAsync`, swap the live transport into mutations.
- `Wavee/App/Services.cs` (:157-159, :162, :188-189, :196-205) â€” `SwitchableTransport`, register `RootlistFollowStrategy`, expose `RealCold`/`RealMutations`/`RealSessionHost`, swap transport in `GoLive`.
- `Wavee/Backend/Seam.cs` (:44-49, :53-77, :79, :84-90, :92-98) â€” playlist follow routing, rootlist change handling, fold rootlist into `Saved`.
- `Wavee/Backend/Mutation.cs` (:50-55, +`Follow`, +`RootlistFollowStrategy`) â€” real `/collection/v2/write`, rootlist strategy.
- `Wavee/Backend/Playlists/PlaylistFetcher.cs` (+`FetchPlaylistDiffAsync`) and `PlaylistWireMapper.cs` (:83-114) â€” `/diff`, rootlist `ListChanges` with attributes/info/nonces.
- `Wavee/Backend/Library/StoreLibrarySource.cs` (:106-107) â€” playlist TTL/revision recheck.
- `Wavee/Backend/Collections/CollectionFetcher.cs` (:118-126) â€” promote `WireSet`.
- `Wavee/Backend/Realtime/DealerRouter.cs` (:24-67) â€” enqueue into `LibrarySync`, decode rootlist topic.
- `Wavee/Backend/Store.cs`, `Persistence/CachedStore.cs`, `Persistence/SqliteColdStore.cs` â€” rootlist revision storage (`SetRootlist(entries, rev)` / `RootlistRevision()`).
- `Wavee/Backend/Scaffold.cs` (:25) â€” register `RootlistFollowStrategy` in the test/scaffold wiring.

**Unchanged (correct as-is, reused)**
- `CollectionDeltaApplier`, `PlaylistDiffApplier`, `CollectionWireMapper`, the durable outbox (`SqliteColdStore` `IMutationOutbox`), `LibraryBridge`, `SaveButton`/`FollowButton` (the pill reads `IsSaved`, which starts working once Â§4.5 lands), `LiveDealerTransport` (already passes headers/verb through; no change needed for form-urlencoded beyond the caller supplying headers).

---

# Appendix: Wire-protocol reference (extracted from WaveeMusic, reference-only)

## Protocols

## playlist4 diff-based playlist sync

**Owner file:** `C:\WAVEE\WaveeMusic\src\Wavee\Core\Http\SpClient.cs` (Playlist API region, ~L1572-2250). Proto: `C:\WAVEE\WaveeMusic\src\Wavee\Protocol\Protos\playlist4_external.proto` (package `spotify.playlist4.proto`, proto2). Dealer parsing: `C:\WAVEE\WaveeMusic\src\Wavee\Connect\LibraryChangeManager.cs`. Local diff application: `PlaylistCacheService.TryApplyMercuryOpsAsync`.

### Revision format (the key concept)
A playlist revision is a byte blob: 4-byte big-endian counter + remaining bytes = hash. `FormatRevision` (SpClient.cs L2240) turns it into the wire string `"{counter},{hexhash}"`, e.g. `123,ab12cd...` (hash lowercased). The comma MUST be percent-encoded (%2C) in query strings or the gateway returns a non-standard 509.

### Full fetch (GET) â€” `GetPlaylistAsync` (L1587)
`GET {base}/playlist/v2/{path}{?decorate=&from=&length=}` where `path` = URI with `spotify:` stripped and `:`â†’`/`. So `spotify:playlist:XXX` â†’ `playlist/XXX`; `spotify:user:U:rootlist` â†’ `user/U/rootlist`.
- Query params: `decorate=revision,attributes,length,owner,capabilities` (comma-joined), `from={start}`, `length={n}`.
- Headers: `Authorization: Bearer`, `Accept: application/x-protobuf`, UA.
- Response: protobuf `SelectedListContent` (stream-parsed for LOH safety).

### Diff fetch (GET) â€” `GetPlaylistDiffAsync` (L1676)
`GET {base}/playlist/v2/{path}/diff?revision={enc}&handlesContent=&hint_revision={enc}`
- `{enc}` = `Uri.EscapeDataString(FormatRevision(revision))` (the `counter,hash` string, commaâ†’%2C).
- `handlesContent=` is REQUIRED as an empty-value param. `hint_revision` reuses the same revision string (they only validate presence, not relation).
- Headers: Bearer, `Accept: application/x-protobuf`, UA.
- Responses: 200 â†’ `SelectedListContent` with `diff` populated (`Diff{ from_revision, ops[], to_revision }`); **304 Not Modified** = "your revision is current" â†’ synthesize `SelectedListContent{ UpToDate=true, Revision=<sent> }`; 509 common on editorial mixes (revision too stale) â†’ treat as fallback-to-full-fetch.

### SelectedListContent (response shape, proto L186)
`revision(1 bytes), length(2 int32), attributes(3 ListAttributes), contents(5 ListItems), diff(6 Diff), sync_result(7 Diff), resulting_revisions(8 repeated bytes), multiple_heads(9 bool), up_to_date(10 bool), nonces(14 repeated int64), timestamp(15), owner_username(16), abuse_reporting_enabled(17), capabilities(18), geoblock(19)`. `ListItems{ pos(1), truncated(2), items(3 Item[]), meta_items(4), available_signals(5) }`. `Item{ uri(1 required), attributes(2 ItemAttributes) }`.

### Diff / Op vocabulary (proto L114-176)
`Diff{ from_revision(1 bytes req), ops(2 Op[]), to_revision(3 bytes req) }`.
`Op{ kind(1 enum), add(2), rem(3), mov(4), update_item_attributes(5), update_list_attributes(6) }`. Kind enum: `KIND_UNKNOWN=0, ADD=2, REM=3, MOV=4, UPDATE_ITEM_ATTRIBUTES=5, UPDATE_LIST_ATTRIBUTES=6`.
`Add{ from_index(1), items(2 Item[]), add_last(4 bool), add_first(5 bool) }`. `Rem{ from_index(1), length(2), items(3), items_as_key(7 bool) }`. `Mov{ from_index(1), length(2), to_index(3) â€” all required }`.

### Incremental-sync decision flow
Local `PlaylistCacheService`: on a dealer push carrying full `ops[]` + `parent_revision`, if cached `Revision == parent_revision`, apply the ops locally with ZERO `/diff` round-trip (`TryApplyMercuryOpsAsync`). Otherwise call `GetPlaylistDiffAsync(uri, cachedRevision)`; on 304 no-op, on ops apply to reach `to_revision`, on 5xx fall back to a full `GetPlaylistAsync`.

### Rootlist sync
The rootlist is just a playlist: `spotify:user:{username}:rootlist` â†’ `GetPlaylistAsync` with path `user/{username}/rootlist`. Its `contents.items` are playlist URIs plus folder markers `spotify:start-group:{16hex}:{urlname}` / `spotify:end-group:{16hex}` (name is `Uri.EscapeDataString` with `%20`â†’`+`). Rootlist push notification carries `RootlistModificationInfo{ new_revision(1), parent_revision(2), ops(3) }`.

## collection/v2 sync

**Owner:** `SpClient.cs` Collection API region (L1205-1406). Proto: `C:\WAVEE\WaveeMusic\src\Wavee\Protocol\Protos\collection2v2.proto` (package `spotify.collection.proto.v2`, `Wavee.Protocol.Collection`). Sync driver: `C:\WAVEE\WaveeMusic\src\Wavee\Core\Library\Spotify\SpotifyLibraryService.cs`.

### Content-Type (all three endpoints)
`application/vnd.collection-v2.spotify.proto` â€” set on BOTH `Accept` and request-body `Content-Type`. Headers: Bearer + UA only (client-token NOT required here).

### Paging (full sync) â€” `GetCollectionPageAsync` (L1216)
`POST {base}/collection/v2/paging`. Body `PageRequest{ username(1), set(2), pagination_token(3), limit(4) }` (default limit 300). Response `PageResponse{ items(1 CollectionItem[]), next_page_token(2), sync_token(3) }`. Loop while `next_page_token` non-empty; store final `sync_token` for next delta.

### Delta (incremental) â€” `GetCollectionDeltaAsync` (L1289)
`POST {base}/collection/v2/delta`. Body `DeltaRequest{ username(1), set(2), last_sync_token(3) }`. Response `DeltaResponse{ delta_update_possible(1 bool), items(2 CollectionItem[]), sync_token(3) }`. If `delta_update_possible==false` (stored token too old), fall back to full paging. Each item with `is_removed` â†’ remove; else add. Store new `sync_token`.

### Write â€” `WriteCollectionAsync` (L1355)
`POST {base}/collection/v2/write`. Body `WriteRequest{ username(1), set(2), items(3 CollectionItem[]), client_update_id(4) }`. `client_update_id` = `Guid.NewGuid().ToString("N")` (dedup token). Add vs remove via the `is_removed` flag on each item.

### CollectionItem (proto L18)
`CollectionItem{ uri(1 string), added_at(2 int32 â€” UNIX SECONDS), is_removed(3 bool) }`. Note: added_at is int32 seconds here (contrast: playlist ItemAttributes.timestamp is int64 ms).

### Set names (critical mapping â€” SpotifyLibraryService.cs L26-33, LibraryOpDispatch.cs L56)
`collection` = BOTH tracks and albums (`spotify:track:*` + `spotify:album:*`; disambiguated by URI prefix; revision keys `collection:Track` / `collection:Album`). `artist` (singular) = followed artists. `show` (singular) = saved shows. `ylpin` = Your-Library pins. `listenlater` = saved episodes. `ban` / `artistban` = bans. `enhanced` = enhanced-playlist overlays. Sync-token DB keys are `<set>` or `<set>:<itemType>` for mixed sets.

### PubSub delta (dealer inbound) â€” `PubSubUpdate` (proto L49)
`PubSubUpdate{ username(1 opt), set(2 opt), items(3 CollectionItem[]), client_update_id(4 opt) }` â€” the shape pushed on `hm://collection/collection/{user}/...` for track/album changes.

## Write endpoints

All write shapes verified against SpClient.cs + the mutation services.

### Like / unlike a track
`POST {base}/collection/v2/write`, Content-Type `application/vnd.collection-v2.spotify.proto`.
Body: `WriteRequest{ username, set="collection", items=[ CollectionItem{ uri="spotify:track:XXX", added_at=<unix_seconds>, is_removed=false(like)/true(unlike) } ], client_update_id=<guid N> }`.
Entry: `SpotifyLibraryService.SaveTrackAsync/RemoveTrackAsync` â†’ outbox â†’ `LibraryOpDispatch.WriteAsync` â†’ `WriteCollectionAsync(username, "collection", [item])`.

### Save / unsave an album
IDENTICAL to track but `uri="spotify:album:XXX"` â€” same `set="collection"` (tracks and albums share the collection set; server disambiguates by URI prefix). `is_removed` toggles. Entry: `SaveAlbumAsync/RemoveAlbumAsync`.

### Follow / unfollow an artist
`POST {base}/collection/v2/write` with `set="artist"` (SINGULAR), `items=[CollectionItem{ uri="spotify:artist:XXX", added_at, is_removed }]`. Entry: `SpotifyLibraryService.FollowArtistAsync/UnfollowArtistAsync` (SpotifyLibraryService.cs L915/919). NOTE: artist follow is a collection write, NOT a playlist4 op.

### Subscribe / unsubscribe a show (podcast)
Same collection/v2/write, `set="show"` (singular), `uri="spotify:show:XXX"`.

### Follow / unfollow a PLAYLIST = rootlist add/remove (playlist4, NOT collection)
This is the important distinction. "Follow playlist" == add its URI to your rootlist; "unfollow" == remove it. `PlaylistMutationService.SetPlaylistFollowedCoreAsync` (L662).
`POST {base}/playlist/v2/user/{username}/rootlist/changes` (`PostRootlistChangesAsync`, SpClient.cs L2007).
Body protobuf `ListChanges` but **Content-Type header MUST be `application/x-www-form-urlencoded`** despite binary protobuf body (gateway routes non-matching content-type to a passive read-only handler).
- Follow: `Op{ kind=ADD, add=Add{ from_index=0, items=[Item{ uri=playlistUri, attributes=ItemAttributes{ timestamp=<ms>, public=true } }] } }`.
- Unfollow: `Op{ kind=REM, rem=Rem{ from_index=<existingIndex>, length=1 } }`.
`ListChanges{ base_revision=<rootlist revision bytes>, deltas=[Delta{ ops=[op], info=ChangeInfo{ user=username, timestamp=<ms> } }], want_resulting_revisions=true, want_sync_result=true, nonces=[<random 1..int32max>] }`.

### Add tracks to a playlist â€” `PlaylistAddTracksHandler` + `ChangePlaylistAsync`
`POST {base}/playlist/v2/{path}/changes` (path = `playlist/{id}`), Content-Type `application/x-www-form-urlencoded`, body `ListChanges`.
Op: `Op{ kind=ADD, add=Add{ add_last=true, items=[Item{ uri, attributes=ItemAttributes{ timestamp=<ms> } }] } }`. Chunk size 500 URIs/request. `base_revision` = cached playlist revision. Response = fresh `SelectedListContent` (may be zstd-compressed: Content-Encoding `zstd`, magic `28 B5 2F FD` â€” `MaybeDecompressZstd`). Duplicates ARE allowed; retries resume via a persisted chunk cursor, never dedupe.

### Shared playlist-v2 mutation request builder (`BuildPlaylistV2Request`, L2056)
All `/playlist/v2/*` mutations (changes, rootlist changes, create, register-image, signals) need the first-party identity or the gateway 200-OKs a passive handler:
`UserAgent`=first-party desktop, `Accept-Language: en`, `Cache-Control: no-store`, `App-Platform: Win32_ARM64`, `Spotify-App-Version: 128800483`, `spotify-accept-geoblock: dummy`, `spotify-dsa-mode-enabled: false`, `spotify-playlist-sync-reason: CAk=` (base64; `/signals` uses `CA8QAQ==`), `Origin`, plus `client-token`.
Responses: 409 Conflict = revision conflict â†’ refetch + retry.

### Create empty playlist â€” `CreateEmptyPlaylistAsync` (L1957)
`POST {base}/playlist/v2/playlist`, form-urlencoded, body `ListUpdateRequest{ attributes=ListAttributes{ name }, info=ChangeInfo{ user, timestamp=<ms> } }`. Response `CreateListReply{ uri(1 req), revision(2) }`. (Then add to rootlist separately.)

### Create folder / rename / delete / move (rootlist) â€” RootlistService.cs
All via `PostRootlistChangesAsync`. Folder = pair of `spotify:start-group:{16hex}:{name}` + `spotify:end-group:{16hex}` added as two ADD ops. Rename = REM+ADD start marker. Delete folder = REM end then start (`items_as_key=true`). Move = `Op{ kind=MOV, mov=Mov{ from_index, length, to_index } }` (folder span = end-start+1).

## Dealer notifications

**Owner:** `C:\WAVEE\WaveeMusic\src\Wavee\Connect\LibraryChangeManager.cs`. Dealer transport doc: `C:\WAVEE\WaveeMusic\src\Wavee\Connect\DEALER_PROTOCOL.md`. Reaction wiring: `C:\WAVEE\WaveeMusic\src\Wavee.UI.WinUI\Data\Contexts\LibrarySyncOrchestrator.cs`.

### Dealer transport
WebSocket `wss://{dealer-host}/?access_token={token}`. Messages are JSON `{type, uri, headers, payloads[]}`. Payload decoding: concatenate base64-decoded `payloads[]`, then if header `Transfer-Encoding: gzip` gunzip, then parse as protobuf (Content-Type `application/x-protobuf`). `text/plain`/`application/json` variants decode single payload. Subscriptions match by URI PREFIX.

### Subscribed URI prefixes (LibraryChangeManager L46-50)
`hm://collection/`, `hm://playlist/`, and any URI containing `collection-update`.

### Dispatch by URI shape (OnLibraryMessage, L94-114) and payload each carries
1. **`.../rootlist`** â†’ parse `RootlistModificationInfo{ new_revision(1), parent_revision(2), ops(3 Op[]) }`. Emits `LibraryChangeEvent{ Set="playlists", IsRootlist=true, NewRevision }`. Triggers: orchestrator sends `PlaylistsChangedMessage` â†’ sidebar playlist-tree refresh.
2. **`.../playlist/v2/playlist/{id}`** â†’ parse `PlaylistModificationInfo{ uri(1 bytes), new_revision(2), parent_revision(3), ops(4 Op[]) }`. Event carries `FromRevision`(=parent_revision), `NewRevision`, full `Ops` list, and derived add/remove `Items` (from ADD op items with `attributes.timestamp` ms, and REM op items). Enables ZERO-round-trip local diff apply when cached revision == parent_revision (else refetch that one playlist).
3. **`.../collection/collection/{user}`** (tracks/albums PubSub) â†’ parse `PubSubUpdate{ username(1), set(2), items(3 CollectionItem[]), client_update_id(4) }`. Items â†’ add/remove in DB directly (added_at is UNIX SECONDS here).
4. **`.../list/liked-songs-artist/{artistId}`** â†’ no payload; signal-only event (`Set="liked-songs-artist"`, artistId regex-extracted).
5. **Empty payload OR `.../json` suffix** â†’ for `set==ylpin` emit a basic event; otherwise drop. The `/json` text/plain form is a dupe of the binary `/collection/{user}` form and is intentionally ignored.
6. **Anything else** â†’ `BuildBasicEvent`, `Set` resolved by `DetermineSetFromUri`.

### Set classification from URI (DetermineSetFromUri, L285)
Path-segment parse of `hm://collection/<set>/<userId>[/json]`: second segment `ylpin`â†’ylpin, `listenlater`â†’listenlater, `artist`â†’artists, `show`â†’shows, `collection`â†’collection. Anything with `playlist`/`rootlist`â†’playlists.

### Client reaction (LibrarySyncOrchestrator.WireDealerChanges)
- Always broadcast `LibraryDataChangedMessage` â†’ `LibraryDataService` coalesces (150ms window) into `DataChanged` â†’ UI refresh.
- `Set=="playlists"` â†’ also `PlaylistsChangedMessage` (sidebar shape).
- `Set=="ylpin"` â†’ payload is opaque (items can't be decoded), so kick off a background `SyncYlPinsAsync()` (sync-token-aware, ~few-hundred-byte delta) and re-broadcast on completion. This is what makes the sidebar Pinned section update live when pinning from Spotify mobile. This "re-run incremental sync" fallback is the pattern for any set whose push payload isn't parseable.

### Also on the same dealer socket (from DEALER_PROTOCOL.md, not library)
`hm://pusher/v1/connections/{id}` header `Spotify-Connection-Id` (store for PUT connect-state), `hm://connect-state/v1/*` (cluster/volume/logout), `spotify:user:attributes:update` (UserAttributesUpdate map). Legacy `hm://playlist/{id}` also carries `PlaylistModificationInfo`.

## Cross-cutting gotchas

All citations are from C:\WAVEE\WaveeMusic (read-only reference). Key files: src\Wavee\Core\Http\SpClient.cs (all HTTP endpoints), src\Wavee\Protocol\Protos\playlist4_external.proto and collection2v2.proto (wire shapes), src\Wavee\Connect\LibraryChangeManager.cs (dealer parsing), src\Wavee\Core\Library\Spotify\SpotifyLibraryService.cs + Outbox\LibraryOpDispatch.cs (set-name mapping), src\Wavee.UI\Services\Playlists\RootlistService.cs + PlaylistMutationService.cs + Helpers\RootlistGraph.cs (rootlist/follow write construction), src\Wavee\Core\Playlists\Outbox\PlaylistAddTracksHandler.cs (add-tracks). Guide overview: .agents\guides\library-and-sync.md.

Cross-cutting gotchas to carry into a fresh implementation: (1) playlist revision wire string = "int32BE_counter,lowerhexhash" with comma %2C-encoded; (2) collection/v2 uses Content-Type application/vnd.collection-v2.spotify.proto and CollectionItem.added_at is int32 UNIX SECONDS; (3) playlist4 ItemAttributes.timestamp is int64 MILLISECONDS; (4) all /playlist/v2/* mutation POSTs need Content-Type application/x-www-form-urlencoded (binary protobuf body) + first-party identity headers (App-Platform, Spotify-App-Version 128800483, spotify-playlist-sync-reason CAk=, client-token) or the gateway silently no-ops; (5) /changes responses can be zstd-compressed (magic 28 B5 2F FD); (6) diff endpoint 304 = up-to-date, 509 = revision too stale (fall back to full fetch); (7) artist/show/track/album saves are collection/v2/write, but playlist follow is a rootlist ADD/REM op.
