# Profile pages, daylist updating state, capture label, podcast page — implementation (2026-10-01)

> **Precedence:** *Reconciliation, round 2* (under B-D) supersedes the first reconciliation list, the B-R/B-U summaries
> and every appendix wherever they disagree; Part C's owner decisions supersede Appendix C.

One round, four parts: **A** daylist rollover face, **A2** capture "frame ignored" label, **B** profile pages
(`spotify:user:`), **C** podcast show page (filtering, right-click, overlap, toolbar). Built by two waves of Sonnet 5.5
agents; one Debug + Release build at the end.

## Context
Wavee has no profile page: `spotify:user:` links go nowhere (no RouteKind, deep link refused, owner/search/friend
clicks inert). The owner captured the official client browsing profiles; the wire research is in
`docs/plans/wavee/profile-pages-api-research.md`, and a visual concept in `docs/plans/wavee/profile-page-zune-mica.html`.
The owner approved building it with these changes to the concept:
- **Sentence case** everywhere (no lowercase UI labels); **no "profile" eyebrow** over the name.
- **Buttons in the app's normal styles** (the existing FollowToggle / Play / More families), not new capsules.
- **Adhere to FluentGpu / Wavee patterns** (read the codebase); proper **shimmer states** and the sanctioned
  **async/data-loading** path ("a page demands its whole model").
- Process: **design with Opus 5.5**, **implement with Sonnet 5.5** agents on disjoint files; orchestrator alone
  builds, once, at the end (Debug + Release); publish to a side folder for the live check.

## Part A — daylist rollover: an intermediate "updating" state (built in the same round as Part B; own issue + CHANGELOG bullet)

**Decided (owner, 2026-10-01):** the full *Updating* state (indeterminate ring, "Updating your daylist…", "{Next daypart}
is on its way", the next segment's indeterminate bar; *Late* after the ladder runs out with "Check again"; the playlist
page gets the same ring instead of frozen digits) **and** a first try at **+5 s** (`GraceMs` 30 000 → 5 000; the retry
ladder stays +30 s, +60 s, +2 min, +5 min). Bundled into the profile-pages round (one Sonnet agent owns Part A's files:
`Entities/Daylist.cs`, `Entities/Home.Host.cs` (rollover region only), `Home/Daylist.UI.cs`, the countdown leaf in
`Entities/Playlist.UI.cs`, loc keys, `Wavee.Tests/Daylist*Tests`).

**Evidence (log `%LOCALAPPDATA%\Wavee\logs\wavee-20261001.log`):** window 1790883680 ended 21:41:20; `daylist rollover:
… attempt 1 — invalidating row, tracks, band` at 21:41:50 (= `DaylistRollover.GraceMs` 30 s); the night cover downloaded
0.4 s later, i.e. the first attempt got the new edition. Not a bug in the ladder — but for those 30 s+ Home showed
"Next daylist in 00:00:00", a full ring and *Evening* still current, because `Home/Daylist.UI.cs` `DaylistClock` has no
rolling phase (it never reads `DaylistCountdown.PhaseOf`). The playlist page (`Entities/Playlist.UI.cs:~975-1015`) does
switch its caption to "Updating your daylist…" but keeps frozen `00:00:00` flip digits.

**Change:**
- Pure rule `DaylistClockFace.Of(phase, attempts, exhausted)` → `Counting | Updating | Late` (engine-free, `Entities/Daylist.cs`
  beside `DaylistCountdown`/`DaylistRollover`, tested).
- `Home.Feeds` exposes the ladder as one signal (`DaylistRolloverState { Idle, Waiting, Fetching, Exhausted }`, written
  where `ArmRollover`/`RolloverFire`/`Exhausted` already decide, `Entities/Home.Host.cs:381-500`).
- **Home `DaylistClock`**: on `Updating` the determinate ring becomes `ProgressRing.Indeterminate(Daylist.RingSize)`, line 1
  reads "Updating your daylist…" (existing key `home.daylistUpdating`), line 2 "{Next daypart} is on its way" (new key),
  the next timeline segment becomes `ProgressBar.Indeterminate(cell)` while the finished one fills; the clock leaf keys on
  (window, face) so the face swap remounts only the leaf. On `Late` (ladder exhausted): static ring, "Your next daylist is
  running late" + a "Check again" `HyperlinkButton` that resets the ladder through the existing user-pull path.
- **Playlist page countdown**: in `Rolling`, swap the frozen digits for the same indeterminate ring + caption.
- First try at +5 s (`GraceMs` 30 000 → 5 000, ladder unchanged) so an on-time edition lands in ~5 s.
- Tests: `DaylistClockFace` facts; existing `DaylistRollover` facts updated for the new grace. CHANGELOG `(#n)`.

## Part A2 — capture: stop flagging handled dealer pushes as "frame ignored" (small; same round)

**Evidence (flight recorder `logs/capture/`, 2026-09-23 → 10-01, `ops/tools/capture/decode.py --kind FrameIgnored`):**
828 `FrameIgnored (Unclassified)` — 428 `hm://playlist/v2/playlist/{id}`, 385 `social-connect/v2/broadcast_status_update`,
12 `hm://collection/{collection|artist}/{me}[/json]`, 3 `liked-songs-artist`; 0 `DecodeFailed`. `Spotify/Spotify.Connect.cs`
~:312-324 records `FrameIgnored` BEFORE `Library.OnDealerPush`, which does apply playlist ops in place and re-asks
collection relations (`LibraryPushRules.Classify`, `Spotify/Spotify.Encode.cs` ~:803-840) — so the Diagnostics anomaly list
(`Diagnostics/Capture.Recent.cs` :119/:158) shows hundreds of false ambers a day.

**Change:** `Library.OnDealerPush` returns whether it acted (`LibraryPushRules.Classify(topic) != None` plus the per-playlist
apply); `Connect` records `FrameIgnored` only when neither the local handlers nor the library took the frame, with the
reason from a pure `DealerFrameDisposition.Of(handledLocally, libraryPush, topicKind)` → `Handled | IgnoredOnPurpose |
Unread` (so `liked-songs-artist` reads IgnoredOnPurpose and `social-connect` reads Unread). Tests: the disposition facts.
Files: `Spotify/Spotify.Connect.cs` (that block), `Spotify/Spotify.Library*.cs` (OnDealerPush signature),
`Spotify/Spotify.Dealer.Rules.cs` (the rule), `Diagnostics/Capture.EchoDiff.cs` (`CaptureIgnoreReason` values),
`Wavee.Tests/DealerFrameDispositionTests.cs`. Not in scope: reading the Jam broadcast status (owner declined for now).

## Part B — profile pages

## Findings (exploration, 2026-10-01) — W = src/apps/Wavee, FG = ../fluent-gpu

### Page patterns to copy
- **Page host** = `W/Entities/Artist.Page.cs`: `InstallPages()` → `Shell.SetPage(RouteKind.X, Page)` (:63-68); factory
  `Embed.Comp(new PageProps(route.Subject, routeKey), static () => new PageHost()) with { Key = "artist:" + routeKey }`
  (:72-76); `sealed partial class PageHost : Component, IPropsHost` with all delegates cached in the ctor (:176-229);
  Render: `ScopeEpoch` first, rebind handle on subject/scope change, `UseComputed(_stampFn)` table-Changed fold,
  `UseEffect(_demand, DepKey.From(slot, epoch))` + auto-tracked `UseEffect(_demandRows)` + `UseEffect(_publishAccent)`;
  one `SkelRegionEl(Pending, Failed, Content, ShimmerSource: _shimmerFn, OnFailed, SkelReveal.Soft, …, SmoothResize:false)`
  (:384-387) inside `ScrollView` with `ScrollKey = UseContext(Shell.PageScrollScope) + routeKey`; page wrapped in
  `Ctx.Provide(Design.AccentCtx.Slot, _pageAccent, Ctx.Provide(LazyScroll.Slot, _scrollY, …))`.
- **Newer discipline** (SectionScreen / Browse CategoryPage): pure tested load rule (`BrowseLoadGate.Of`,
  `SectionScreenLoadRule.Of`); **Retry = `Entities.Refresh`, never `Ensure`** (a sealed Asked row is a no-op for Ensure);
  Artist's `_retry = Demand` is the old pattern — do not copy.
- **Data loading**: the planner is the sanctioned path (`Entities.Ensure` doc, Entities.cs:2087-2097; "readiness is read
  off the columns, never a Loadable"); `UseResource` only for non-entity one-offs. Marks: Known/Asked/Inflight/Stale/Failed
  (Entities.cs:963-1014, helpers :1347-1392). "Unavailable" (404 seal) = Asked ∧ Inflight==0 ∧ ¬Known ∧ ¬Failed
  (Concert.Page.cs:1496-1503, Search.Page Readiness). Errors via `Controls.Vacancy(VacancyVoice.Error, onAction: retry)`
  (Controls.cs:1177-1233); empty/unavailable via `Vacancy(Empty, title: …)` returned as content.
- **Shimmer**: explicit `ShimmerSource` = the real hero built with placeholder text (Artist `HeroText.Placeholder`,
  `PageShimmer` :1078-1133) + seed cards `Controls.Surface(CardData.Seed, Shape…)` / `SurfaceParts.Seed(…).Skel(seed)`;
  stateful leaves (FollowToggle) get `SkeletonProxy = s_emptyShape` or `.Skeletonized(false)`; never mount stateful
  components in a shimmer; a PagedShelf yields zero rows against an unmeasured viewport (Browse uses a non-virtual stand-in);
  reveal `Soft` gated on a BodyReady (measured width) so a complete tree rises; SKILL rule 14 (key a child, not the region root).
- **Hero**: `ArtistHeroLayout.For(width, tier)` tiers Narrow/Compact/Medium/Wide with 24-DIP hysteresis (Artist.UI.cs:52-159);
  `HeroBanner` (:380-484) sticky + collapses into the 56-DIP band; identity `HeroIdentity` (:487-537); colour via
  `Detail.AccentFor`/`Palette.ArtistBlendWash`/`ArtistHeroVeil`/`ShellTint` all taking a `uint payloadAccent`
  (Palette.Host.cs:276-298) → the profile `color` feeds them directly; `Design.PageToneFromHue` (Design.cs:906-916).
  Round avatar precedent: `Artist.Reader.cs:954-959` (`Controls.Artwork` in a clipped circle, `Elevation.Card`);
  initials: `PersonPicture.Create("", edge, displayName:)` (FG/src/FluentGpu.Controls/PersonPicture.cs:64).
- **Pivot**: Artist = scroll-spy sticky band (`Detail.Band` + `Detail.Pivot`, `Detail.BandLayout.ActiveSection`,
  Artist.Page.cs:601-694); Discography = facet in the route (`disco:<facet>:<uri>`, `KeyedByArg/PlaceByArg`,
  `SelectorBar.Create(labels, new Signal<int>(facet), onChange: i => Shell.GoTo(…))`, Artist.Discography.cs:270-318,
  :1510-1614); sticky SelectorBar idiom = Home (`.Sticky(inset, scope: PageScope)` + content `.StickyClip` +
  `EdgeFade{WhileStuck}`, Facet.UI.cs:183-188). `user:` Arg already carries the display name → a profile tab can't
  live in the route Arg. Virtual grid inside a page ScrollView measures 0 → long lists must own their scroll.
- **Shelves/cards**: `ShelfOf(items, cardAt, title, accent, cardHeight, onInvoke)` (Artist.Page.cs:1140-1158) over
  `PagedShelf` + `Controls.AccentHeader`; cards `Controls.Surface(new CardData(…){Menu}, Shape.Shelf(captionLines: 2), w)`,
  people/artists `Circular: true`; every shelf of surface cards passes `onInvoke`. Big lists: `ItemsView.CreateBound` +
  `RepeatLayout.GridFit` + `Controls.BoundSurface` (SectionScreen grid). Letter jump list precedent: `JumpStrip` /
  `StickyLetter` / `JumpIndex` (User.UI.cs:277-357, JumpIndex.cs:29-57).
- **Buttons (normal style)**: `Controls.FollowToggle { Uri, Name, Verb, Accent }` (Controls.cs:961-998; keyed embed by uri,
  `SkeletonProxy`); `Controls.IconAction` / `Album.CommandCircle` (share), `Controls.MoreButton` / `Detail.MoreButton`;
  `Button.Create(…, ButtonAppearance.Standard)`; Artist `HeroActions` (Artist.UI.cs:566-596) is the family. Share already
  works for users (`Actions.Table.cs:226-237`).
- **Type** (policy Design.cs:1148-1170: weights 400/600 + six named exceptions; a 7th is a regression — no new alias):
  names via `ArtistDisplay/ArtistTitle/ArtistCompactTitle` per tier (or `DetailHero`); counts via `Design.Type.StatHero`
  (28/36/350, unit on baseline) or meta 13/18; section headers `Controls.AccentHeader`; `Eyebrow` not used (owner rule).
  New sentence-case ICU plural loc keys (not under the existing `profile` group at en-US.json:2669); nl/ko-KR too.

### Routing + click sources
- Add `RouteKind.User` + `"user:"` row before `ConnectDiagnostics` (Shell.cs:49-76, :137-189; template row :163); key codec
  needs no change; `Shell.For` arm `EntityKind.User => RouteKind.User` (:415-432) lights up rich-text links, notification
  toasts, `OpenSocial`, `ActionRules.RouteFor`; deep link `TryParseSpotifyUri` (:645-683) + `IsEntityVerb` (:610-611);
  omnibar `RouteFor` (:2124-2132); `SurfaceOf` → `NavSurface.Detail` (:989-996). Tests pinning today: ShellRoutesTests :40
  (count 33), :430 (user refused), BootOrderingTests :82-104, ShellOmnibarTests :168, SearchFacetRulesTests :216,
  SearchHitDataTests :147-155.
- Click sources: Search `OpenHit` (Search.UI.cs:299 early return; `CanOpen` Search.Rules.cs:455); playlist owner block
  (Playlist.UI.cs:596-610) + members flyout (:714); Detail owner text (Detail.UI.Hero.cs:448-453, Detail.UI.cs:1769-1792);
  Rail friend rows (Rail.UI.cs:975-994); account flyout (Shell.Overlays.UI.cs:539-560 + Actions.Rules.cs:67, order pinned
  by ActionMenuRulesTests :46-74; `--fake` account id is not a spotify:user: → use `User.Me`).

### Data contract proposal (explore agent; to be finalised by the design)
- `UserFields`: keep Identity; Social = profile-view facts (counts, public-playlist total, flags); add `Follow = 1<<3`
  (viewer follows; not persisted). Page asks `Identity|Social|Follow`, **never `All`** (ContentFilters route ignores the uri).
- `UserTable` new columns: `Color` (uint, Identity, 0 never blanks), `PublicPlaylists` (int, Social), `Flags`
  (`UserFlags { CurrentUser, Followed, ShowFollows, AllowFollows }`, mask-merged); `StagedUser` + `CommitUsers` +
  `UserShape` persistence (`color, public_playlists, flags`); `Entities.Invalidate(User, groups)` sugar.
- Edges (new partial `Entities/User.Profile.cs`): `EdgeTable<ProfileCardEdge(EntityKind Kind, byte Flags, int Followers)>`
  `ProfilePlaylists, ProfileArtists, ProfileFollowers, ProfileFollowing` (parent = user slot); `Relation` + `FetchEdge`
  appended; `EdgeTableOf/ParentTableOf/ForEdge` arms (EdgeDoorTests sweep enforces); `CommitEdges` cross-kind arm.
- Routes: `SpclientRoute.ProfileView/ProfileFollowers/ProfileFollowing`; `s_user` = ProfileView(Identity|Social|Follow,
  primary Social) → kind-15 Metadata(Identity) → LikedContentFilters; ProfilePlaylists/ProfileArtists ride ProfileView
  (same route key → one GET); `Serves()`, `AnswerRest` arms; `AcceptFor(RequestKind.Profile) = application/x-protobuf`;
  host `spclient.wg`.
- Decoders `Spotify/Spotify.Decode.Profile.cs` (+ `Spotify/Protos/user_profile_view.proto` as the test encoder):
  `ProfileView` (row + two runs, `EndEvenIfEmpty`), `ProfileList` (Thin rows, one run); kind-15 `color` (f11); a shared
  `ProfileImage` normaliser (`spotify:image:` → i.scdn.co; mosaic token → lead tile / `Controls.Mosaic` path —
  `Controls.ArtUrl` currently breaks non-http tokens).
- Follow: Pathfinder `isFollowingUsers/followUsers/unfollowUsers` hash `c00e0cb6c7766e7230fc256cf4fe07aec63b53d1160a323940fce7b664e95596`
  (`{uris:[…]}` / `{usernames:[bare id]}`); `Library.FollowUser(uri, follow)` modelled on `SetVisibility`
  (Spotify.Api.Playlist.cs:363-392: optimistic flag + Followers±1, Bump, Publish, Net.Run, revert + toast on refusal,
  Invalidate on success, replica-lag caveat); `IsSavedUri`/`ToggleSavedUri` user arms (User.UI.cs:705-751) so
  `FollowToggle` works unchanged; no-op on `MeSlot`.
- Own profile: `User.Me` + `Home.Feeds.EnsureTopContent()` → `Edges.UserTopArtists` (Home.cs:530-536, Home.Host.cs:310-379).
- Demand: once per (user, epoch): row Ensure if unknown else Invalidate(Identity|Social|Follow) (the official client
  re-reads on every visit; precedent `PageRules.OwnerAskFor`, Playlist.Page.cs:208-211/:657-674); edges EnsureEdge while
  Unknown else InvalidateEdge; follower/following lists only when shown.
- Tests: `ProfileDecodeTests` (generated proto encoder or scrubbed `.bin` fixtures), `SpotifyApiTests` route facts,
  `FetchRoutesTests` ForEdge rows, EdgeDoorTests sweep, pure `ProfileRules` facts.

## B-R: Routes, click sources, "See all" list page (Opus design, final)

**Routes (`Shell/Shell.cs`)** — enum after `Episode`: `User, ProfileList`; rows (declaration order = enum order):
`new(RouteKind.User, "user:", true, Strings.ProfileList.Nav.Profile, Icons.Contact, false, true, false)`,
`new(RouteKind.ProfileList, "people:", true, Strings.ProfileList.Nav.Lists, Icons.Friends, false, false, true, PlaceByArg: true)`.
Codec special cases mirror Discography (`IsKnown` strict via `ProfileListRoute.TryParse`; `CarriesDisplayName` excludes
ProfileList; `Dest` → facet word; `Parse` (`DiscoSubject` → shared `FacetSubject`), `NameOf`, `ArgOf`); `Shell.For`
`EntityKind.User => RouteKind.User`; `IsEntityVerb += "user"`; `TryParseSpotifyUri` user arm; `SurfaceOf` Detail;
`RecentSurfaces` + `Omnibar.FromHistory` include User; `Omnibar.RouteFor(ItemKind.User)` when the uri is a user;
lazy page group `{User, ProfileList}` → `ProfilePages.Install()` (+ `RestorePagesForTests`). Not sidebar-pinnable yet.
**`Entities/Profile.Route.cs` (new):** `enum ProfileFacet : byte { Playlists=0, Following=1, Followers=2 }`;
`ProfileListRoute { Prefix="people:"; Key(facet, uri); TryParseArg; TryParse(in Route, out facet, out user); For(EntityUri|User, facet) => Shell.Parse(Key(…)); FacetLabelKey }`;
`ProfileRoute.For(User u) => Shell.Parse("user:" + u.Uri.Text, name)` (works for `--fake`'s bare account row);
`ProfilePages.Install()` → `SetPage(User, ProfilePage.For)` + `SetPage(ProfileList, ProfileLists.Page)`.
**Click sources:** Search `OpenHit` (user → `ProfileRoute.For`) + `CanOpen += User` (top result "Open page", no Play,
no Follow trailing on search hits); Playlist owner block (role/focus/hand/OnClick) + members flyout rows; Detail owner
text + `OwnerBlock`; Rail `FriendRow` avatar + name (nested owners, `BlocksDragArm`); account flyout `ProfileRow.Profile`
after Account (`Rows(..., hasProfile)`, `User.Me`); deep-linking skill doc rows. Added-by column deferred.
**List page (`Entities/Profile.Lists.cs` pure + `Entities/Profile.Lists.Page.cs` UI):** Discography-shaped head
(`BreadcrumbBar [name › facet]`, `PageHero`, `SelectorBar` whose change navigates; labels "Following · 1,063");
pure `ProfileListLoadRule.Of(facet, facts)` → `Pending|Ready|Empty|Hidden|Unavailable|Failed` (Unavailable > Hidden >
rows ⇒ Ready > Failed > Empty(Complete) > Pending); `ProfileListFilter` (`Counts`, `Order` = library a–z filing then name
then artists-first then slot, `Apply(chip, query)`, `WordCount`); `ProfileLetterRows` (flat header + row-of-N projection
over `RepeatLayout.Extents`, pinned extents, `JumpIndex`, `StickyLetterAt`, geometry `Key()`); `ProfileGridFit`
(MinCell 176, `CardRow = SurfaceGeometry.GridRowEstimate`, +40 action line on rows with an artist, strip ≥ 640).
UI: one `SkelRegionEl` (Soft, gated on measured width) — no outer ScrollView, the `ItemsView.CreateBound` list owns the
scroll; `RowSlot` = Recents idiom (header plate or a pinned-height row of free-mode `Controls.Surface(Shape.Grid)` cards;
artists in Following get a keyed `FollowToggle` under the card); `User.StickyLetter` + `User.JumpStrip` reused
(`LetterPlate` → internal); Following tools = chips All/Artists/People + `Controls.FindBox` (150 ms debounce);
Followers = find box; Playlists = server order, ≤10 + "Showing 10 of 94". Demand: `Ensure(Identity|Social)` if unknown;
facet edge `EnsureEdge` while Unknown else `InvalidateEdge` (re-read per visit); Retry = `RefreshEdge` (+ `Refresh` on a
failed row). States: shimmer (tool bones + two non-virtual rows of `Surface(CardData.Seed with { Circular }, Shape.Grid)`
+ `FollowToggle.SkeletonShape()`), Empty / Hidden ("Follows are private") / Unavailable / Failed via `Controls.Vacancy`.
Circular seed: `SurfaceParts.Seed(…, circular)` (one shared change — merge with the page designer's if duplicated).
**Loc:** group `profileList` (nav, menu, facet, title, chip, find, showing, card.*, empty.*, hidden.*, unavailable.*,
fallbackName) in en-US / nl / ko-KR — sentence case, ICU plurals.
**Tests:** existing pins updated (ShellRoutesTests 33→35 + refused-user line, BootOrderingTests, ShellOmnibarTests,
SearchFacetRulesTests, SearchHitDataTests, ActionMenuRulesTests, ShellNavTests); new `ProfileListRouteTests` (≈15 facts:
round trips, refusals, slots, Dest, deep link, fake row, Detail surface, not pinnable, recent/history, profile menu row)
and `ProfileListRulesTests` (load rule ×6, filter ×7, letter rows ×7, grid fit ×2).
**Ownership:** R1 = Shell.cs, Profile.Route.cs, Search.UI/Rules, Playlist.UI, Detail.UI(.Hero), Rail.UI, Actions.Rules,
Shell.Overlays.UI, skill doc + the route tests. R2 = Profile.Lists(.Page).cs, Surface.Parts/Host (circular seed),
loc `profileList` blocks, `ProfileListRulesTests`. Orchestrator: `LetterPlate` visibility in User.UI.cs (data agent's
file), align `ProfilePage.For` name.

## B-U: The profile page (Opus design, final; full code text saved in the session)

**Decisions:** ONE long page — the hero `.Sticky(0).Collapse(h−56→56)` into `Detail.Band` + `Detail.Pivot` (scroll-spy
over the sections, `FollowTextAction` in the band), each section a capped preview (≤10 playlists, ≤10 artists, ≤20 people)
with "See all" → `ProfileListRoute`. Code lives in a top-level `public static partial class Profile` (`Profile.Rules.cs`,
`Profile.Page.cs`, `Profile.UI.cs`), not nested in `User` (member-name shadowing). Hero: pure `ProfileHeroLayout` reusing
`ArtistHeroLayout.TierFor` (Wide 380 / Medium 300 / Compact 324 stacked / Narrow 360 stacked, worst-case heights,
bottom-aligned); round avatar (`Controls.Artwork` circle, decode 300; no image → `PersonPicture` initials on the person
colour); name in the tier's Artist alias (`ArtistDisplay`/`ArtistTitle`/`ArtistCompactTitle`); counts = `StatHero`
numeral over a 12/16/600 tertiary caption (Track.Drawer `Stat` idiom), each linking to its list; Followers/Following
hidden unless own ∨ ShowFollows. Actions: keyed `FollowToggle` (others only) · `Album.CommandCircle(Share)` ·
`Detail.MoreButton` (Pin/Unpin + Share ▸). **Ghost name dropped** (needs a 7th type rung — policy). Own top artists:
rank as `StatHero` in a 36-DIP column beside a circular card, "Only visible to you" chip. Colour: `Palette.ShellTint` leaf +
`Palette.ArtistBlendWash` + `Detail.AccentFor`, no `ArtistHeroVeil` (no photo under the type); pure `ProfileTone.Of`:
gradeable avatar leads (payload 0), else the profile `color` as opaque payload. Loading: page `SkelRegionEl` (Soft,
explicit `PageShimmer` = the real hero with `HeroText.PlaceholderFor` + seed shelves at the fitted width) + **one
`SkelRegionEl` per section** (FadeOnly, Group null — a late list never holds the hero); Retry = `Entities.Refresh` /
`RefreshEdge`; the stamp folds the row's Known/Asked/Inflight/Failed marks and each edge's Readiness (a 404 seal moves no
Version). `UseActivation(Reask)` re-reads on a keep-alive return (SWR). Stateful leaves proxied (`SkeletonProxy`,
`.Skeletonized(false)` on the action row). Cards: `Controls.Surface` (playlist square with `Controls.Mosaic` for
`spotify:mosaic:`, artist circle, person circle) in `PagedShelf` with `onInvoke`, `Search.DragOf/MenuOf` for menus.

**Shared vocabulary (U1 writes; everyone reads) — `Entities/Profile.Rules.cs`:** `ProfileLoadState {Pending, Ready,
Unavailable, Failed}` + `ProfileLoad.Of(valid, known, asked, inflight, failed)` (!valid→Unavailable; known→Ready (SWR);
inflight→Pending; failed→Failed; asked→Unavailable (404 seal); else Pending) + `BodyReady(state, measured, revealed)`;
`ProfileSection {TopArtists, Playlists, RecentArtists, Following, Followers}`, `ProfileSectionBody {Seed, Cards, Empty,
Error}`, `ProfileFacts`, `ProfileCard(Kind, Slot, Version, Followers, Rank, Key)`, `ProfileSections` (Count, caps 10/10/10/20,
`Key`, `CapOf`, `ShowsPeople`, `BodyOf` (rows win > Failed→Error > Complete→Empty > Seed), `Plan` (Playlists always;
optional shelves drop on empty/failed; people drop only on empty), `TopState(HomeLoad, count)`, `SeeAll`);
`ProfileHeroMetrics` + `ProfileHeroLayout` (avatar 184/144/112/96, name line 96/60/40, StatHeight 52, gaps, ActionRow 32,
`CopyHeight`, `CollapseDistance`, `WashHeight/Boundary`); `ProfileStat`/`ProfileStatCell`/`ProfileStats` (`Plan`, `PerRow`,
`Numeral` culture-grouped); `ProfileArt(Url, Tiles)` + `ProfileCover.Of` (https passthrough, `spotify:image:`/bare 40-hex →
i.scdn.co, `spotify:mosaic:` → four 300-px tiles `ab67616d00001e02…` or the lead tile; idempotent); `ProfileToneSource` +
`ProfileTone.Argb/Of`.
**U1↔U2 interface (internal static on `Profile`, in U2's `Profile.UI.cs`):** `HeroText(Name, AvatarUrl, PersonArgb,
Followers, Following, Playlists, Own, ShowFollows, Placeholder) {For(User, own, avatar); PlaceholderFor(own)}`,
`HeroActs(Share, Menu, OpenStat)`, `HeroBanner(in HeroText, uri, width, in metrics, compactCanHit, acts?, band?)`,
`MenuFor(User, name)`, `SectionTitle`, `PivotLabel`, `Shelf(section, cards, accent, seeAll?)`, `SeedShelf`, `EmptyShelf`,
`SectionError`, `Unavailable()`.
**Tests:** `ProfilePageRulesTests` (load truth table ×32, BodyReady latch, BodyOf ×7, Plan ×6, TopState ×6, SeeAll, keys,
stats ×5, cover ×4, tone ×3) and `ProfileHeroLayoutTests` (tier = artist tier ×9, heights 380/300/324/360, height sum,
collapse ends on the band, avatar/stack/rows, gutter, wash).

### Reconciliation of the three designs (orchestrator decisions)
- **One loc group `person`** (U2 owns `assets/loc/{en-US,nl,ko-KR}.json`): the page keys (topArtists, onlyYou,
  publicPlaylists, recentArtists, following, followers, pivot.*, stat.*, card.followers, empty.*) **plus** the list page's
  keys under `person.list.*` (nav.profile, nav.lists, menu.profile, title.*, chip.*, find.*, showing, card.artist,
  card.artistFollowers, card.profile, empty.following/followers, hidden.*, fallbackName). The list page's facet labels
  REUSE `person.pivot.{playlists,following,followers}`; `Strings.ProfileList.*` in the routes design becomes
  `Strings.Person.List.*` / `Strings.Person.Pivot.*`. All three files carry every key (a satellite miss is a warning →
  error).
- **Install:** the routes design's lazy group `{User, ProfileList}` in Shell.cs calls **`Profile.InstallPages()`** (U1, in
  `Profile.Page.cs`), which registers `User → Profile.Page` and `ProfileList → ProfileLists.Page`. No `ProfilePages` class,
  no `Artist.InstallPages` line.
- ~~Image tokens: `ProfileCover.Of` stays~~ — superseded by round 2 (covers = `CoverToken` + `Controls.ArtUrl` +
  `Controls.MosaicTiles`).
- **Circular seeds** (`SurfaceParts.Seed(…, circular)`) are R2's one shared change; the page uses square seeds.

## B-D: The data layer (Opus design, final)

**Findings that reshaped the contract (verified in code):**
1. A profile **404 is staged as a known negative** (`Decode.ProfileUnavailable`: Social|Follow at Full +
   `UserFlags.Unavailable` + both riding shelves Complete-empty). Left as an unanswered ask, `Fetch.Miss.ReviewMisses`
   would retry it twice then seal it Failed — so "Asked ∧ ¬Known ∧ ¬Failed" never means 404.
2. **Public playlists + recently played artists are NOT FetchEdges** — they ride `UserFields.Social` (one ProfileView
   GET); edge buckets don't dedupe against the row's disk-first leg (up to three GETs otherwise). Only
   `FetchEdge.ProfileFollowers/ProfileFollowing` exist.
3. **Social and Follow are never persisted** (a disk-restored Social would leave the riding shelves unasked → shimmer
   forever). `UserShape` persists Identity + the new `color` only. **One-time cost:** the DDL change moves every install
   to a new `library.<fp>.db` (`Store.FileName`) — a cold first launch after the update.
4. A follow write **holds Follow+Social authority at `Authority.Local`** while out, so the mount's in-flight profile GET
   can't land the pre-click state over the optimistic flip (`Table.Accepts`).
5. `Controls.ArtUrl` turns every `spotify:` token into a broken CDN url today → fixed via pure `CoverToken`.
6. `CommitUsers` blanked avatars on a Thin mention without one → an empty image clears only at Full.
7. `--fake` users have no provider → the seed answers Social + the four shelves (`Entities.Fake.Profile.cs`).

**D1 — entity / edges / persistence / seed.** `Entities/User.cs`: `UserFields.Follow = 1<<3` (All widened), new
`[Flags] UserFlags { CurrentUser, ShowFollows, AllowFollows, Unavailable, Followed; SocialMask, FollowMask }`; `UserTable`
columns `Color` (0xFFRRGGBB, 0 never blanks), `PublicPlaylists`, `Flags` (mask-merged per group), `FollowAuthority`;
`StagedUser.{Color, PublicPlaylists, Flags}`; `CommitUsers` per-group arms (avatar via `Detail.CoverLatch.AcceptsImage`);
`UserShape` = name, image, color, identity_auth. New `Entities/User.Profile.cs`: `ProfileCardFlags {ViewerFollows,
OwnerFollows}`, `ProfileShelf {Playlists, Artists, Followers, Following}`, `ProfileCardEdge(Kind, Flags, Followers)` +
`Admits(relation, kind)`, `Edges.ProfilePlaylists/ProfileArtists/ProfileFollowers/ProfileFollowing`, `User` accessors
(`Name`, `Image` (url via ArtUrl), `Color`, `PublicPlaylists`, `Flags`, `IsCurrentUser`, `IsFollowedByViewer`,
`ShowFollows`, `AllowFollows`, `IsUnavailable`, `IsAsked/IsInflight/IsFailed/IsStale`, `ProfileTargets/Cards/Readiness/
Failure/Count/Total/Version(shelf)`, `TopArtistSlots`), `Entities.Invalidate(User, groups)`. `Edges.Staging.cs`:
`Relation.Profile*` ×4, `ParentTable`, cross-kind `CommitEdges` arm. `Fetch.Routes.cs`: `FetchEdge.ProfileFollowers/
ProfileFollowing`, `PathfinderOp.IsFollowingUsers`, `SpclientRoute.ProfileView/ProfileFollowers/ProfileFollowing`;
`s_user` = ProfileView(I|S|F, primary Social) → kind-15 Metadata(Identity) → IsFollowingUsers(Follow) →
LikedContentFilters; `ForEdge` arms. `Fetch.Edges.cs` table/parent arms. `Detail.cs` `CoverLatch.RenditionRank` in pixel
edges (adds avatar 300/64, artist 320/160). Seed: `Entities.Fake.cs` (user colours, `SeedProfileSurfaces` hook) + new
`Entities.Fake.Profile.cs`. Tests: new `UserProfileCommitTests`; `StoreTests` (user test rewritten: colour round-trips,
no social on disk); `FetchRoutesTests` rows.

**D2 — transport / decoders / covers.** New `Spotify/Protos/user_profile_view.proto` (test encoder only). New
`Spotify/Spotify.Decode.Profile.cs`: `ProfileView` (row I|S|F at Full + two contiguous runs, playlists Partial with the
true total, artists `EndEvenIfEmpty`), `ProfileUnavailable`, `ProfileList` (whole list, wire order, cap 5000 → Partial),
Thin artist/user staging that never seals a nameless identity, `StageImage` (`spotify:image:` → CDN url; mosaic
verbatim), `RgbColor`. `Spotify.Decode.Traits.cs`: kind-15 field 11 colour; JSON `Profile` decoder deleted. New
`Spotify/Spotify.Api.Profile.cs`: `ProfileViewRoute/ProfileFollowersRoute/ProfileFollowingRoute` (spclient.**wg**, protobuf,
`RequestKind.Profile`), `ProfileViewAnswer`/`StageProfileView` (200 → view, 404 → known negative), `ProfileListAnswer`
(404 → empty Complete). `Spotify.Api.cs`: `Serves`/`ServesEdge` (lists offset 0 only)/`AnswerRest`/`AnswerQuery`
arms, `ProfileFallback` → protobuf view, `AcceptFor(Profile)` = protobuf; JSON `ProfileRoute`/`Profile`/`UserProfiles`
deleted. `Spotify.cs`: the `RequestKind.Profile` fold arm deleted. New pure `Entities/CoverToken.cs` (`IsImageToken`,
`ImageTokenUrl`, `FileIdOf`, `IsMosaic`, `MosaicTiles`) — **plus** a `MosaicTileId` rewrite to the 300-px tile
(`ab67616d00001e02` + id[16..] for an `ab67616d…` id; research §5: the client composes 300-px tiles). `Controls.cs`
ArtUrl hunk (token → its one file; mosaic → lead tile); new `Platform/Controls.Cover.cs` `MosaicTiles(StringId)`
(cached, 4 or none). Tests: new `ProfileDecodeTests` (≈11 incl. interleaved runs, 404, cap, zero-alloc warm decode),
`CoverTokenTests`; `DecodeTests` (fixture f11 → Int32Value, JSON test deleted), `SpotifyApiTests` (wg protobuf route,
list routes, route-walk facts, edge-gate rows), `SpotifySessionTests:1141` line deleted, `ControlsTests` ArtUrl facts.

**D3 — follow / library seam / ask + load rules.** New `Spotify/Spotify.Api.Profile.Follow.cs`: `ProfileQueries`
(hash `c00e0cb6…`, `isFollowingUsers {uris}`, `followUsers/unfollowUsers {usernames:[bare id]}`, desktop identity),
`ProfileFollowAnswer.IsFollowing/WriteSucceeded` (lenient), `Decode.FollowState`. New `Entities/User.Follow.cs`
`UserFollowWrite.Apply/Confirm/Revert` (+ `FollowSnapshot`). `Spotify.Library.cs` one `Transport.FollowUsers` field; new
`Spotify/Spotify.Library.Profile.cs` `Library.FollowUser(uri, follow)` (optimistic, one write per user, no-op on own
profile, **no settle refresh** — the SetCollaborative replica-lag precedent; refusal → revert + toast). `User.UI.cs` §8
`IsSavedUri`/`ToggleSavedUri` user arms. New `Entities/User.Profile.Rules.cs`: `ProfileSurface {Page, Followers,
Following}`, `ProfileAsk.Plan(...)`/`Plan(User, surface)`/`Apply`/`RetryHeader`/`RetryList` (first visit: Ensure row +
both lists in parallel; revisit: Invalidate row + lists (SWR); hidden follows skip lists unless own; own → top artists),
**`enum ProfileLoad {Loading, Ready, Unavailable, Failed}` + `ProfileLoadRule.Header/Riding/List/TopArtists`**
(Unavailable flag wins; known → Ready; inflight → Loading; failed → Failed; asked → Unavailable; list NoRoute →
Unavailable). Tests: new `ProfileAskTests` (10), `ProfileFollowTests` (9).

### Reconciliation, round 2 (data ↔ page ↔ routes; orchestrator decisions — binding on the implementers)
- **One load vocabulary = D3's** `ProfileLoad` enum + `ProfileLoadRule`. U1 drops its `ProfileLoadState`/`ProfileLoad.Of`
  (the 404 now arrives as Known+Unavailable, which U1's "known → Ready" would have shown as a live page) and keeps only
  `ProfileReveal.BodyReady(ProfileLoad header, measured, revealed)`. The list page's `ProfileListLoadRule` is built on
  `ProfileLoadRule.List` + the Hidden/Empty arms.
- **Demand = `ProfileAsk`.** Page `_demand` = `ProfileAsk.Apply(u, ProfileAsk.Plan(u, ProfileSurface.Page))`, DepKey (slot,
  epoch); `UseActivation` re-runs it (revisit → Invalidate, SWR). U1's `DemandLists`/`AskRow`/`AskEdge` are deleted.
  Retry = `ProfileAsk.RetryHeader`; a section retry = `RetryList(u, shelf)` (riding shelves retry with the header), top
  artists = `Home.Feeds.EnsureTopContent()`. List page uses `ProfileSurface.Followers/Following`.
- **No `FetchEdge.ProfilePlaylists/ProfileArtists`** anywhere in UI code; readiness via `User.ProfileReadiness(shelf)`.
  The page stamp also wakes on `Fetch.Settled` (mark-only changes publish no table).
- **Covers = D2's `CoverToken` + `Controls.ArtUrl` + `Controls.MosaicTiles`.** U1's `ProfileCover`/`ProfileArt` and their
  tests are dropped; avatars read `User.Image` (already a url); playlist cards use `Controls.MosaicTiles(pl.ImageId)`
  (Length 4 → `CoverOverride = Controls.Mosaic(...)`). The `Playlist.UI.cs` `CoverArt` mosaic hunk goes to R1 (same file).
- **Colour:** `User.Color` is already 0xFFRRGGBB; `ProfileTone.Argb` stays (idempotent) and `ProfileTone.Of` is unchanged.
- **Public playlists have no "See all"** (research open question 5: nothing in the capture fetched past 10). So
  `ProfileFacet { Following, Followers }` only (list page = two tabs), `ProfileSections.SeeAll` only for the two people
  sections, the "Public playlists" stat never links, and the list page's "Showing 10 of 94" arm is dropped.
- **`Controls.Library ??= User.LibrarySeam`** in the profile page host and the list page host (Show/Playlist page
  precedent) so `FollowToggle` reaches the user arms.
- Three enums stay distinct on purpose: `ProfileShelf` (data relation), `ProfileSection` (page section, adds TopArtists),
  `ProfileFacet` (list route).
- `User.UI.cs` `LetterPlate` → internal: R2 (wave 2, after D3 has finished with that file).

## Part C — podcast show page (diagnosis done, 2026-10-01; owner screenshots of "Patrick Boyle On Finance")

**Root causes (from source + log seq 12590–12782 + the local DB):**
1. **Filtering looks inconsistent.** Filter/sort/find (`Show.Page.cs:394-396`) feed only the episode list (:535-537);
   the head (hero, up-next, new-since) is built from all episodes by design (0.2.9 parity #25-26), and its hash/stamp
   ignore the filter. Real bugs: the "don't repeat the hero in the list" rule (:542-549) also runs while filtering → a
   search matching only the hero shows "Episodes 1 of 326" over an empty list; and the ~500-DIP 0.3 head (the parity
   rule was written for a 72-DIP banner) pushes the results below the fold.
2. **Right-click dead + "missing image".** The up-next mini cards (`Show.UI.cs:581-594`), the resume hero (:509-517)
   and the new-visitor doors (`DoorData`, `Controls.Podcast.cs:43-44`) carry `OnClick` only — no menu is attached (the
   new-since rows do have one). The blank area is the mini card's always-reserved 34-DIP **numeral column** (no cover by
   design, :549-565): this show's newest episodes are unnumbered, one has wire number 35.
3. **Overlap.** Engine: `FlexLayout.ArrangeWrap` (`../fluent-gpu/.../FlexLayout.cs:1576-1586`) breaks lines from the
   children's STORED widths while a cached `Measure` (:513-518) skipped re-measuring them, so a wrap row that narrows
   with clean contents overflows into an uncounted extra line and paints over the next section. Trigger here: the 30-DIP
   year strip (`Show.Page.cs:896`) appears/disappears with the filter, resizing the head's `Tiles` wrap row
   (`Show.UI.cs:155-161`). **Green line:** nothing in source paints green — investigated live (below).
4. **Cramped toolbar.** Only the chip strip can shrink (`Controls.Words.cs:257-263`); search is a fixed 180, and the
   breakpoints are constants (`Show.Rules.cs:474-485`) computed for 16-DIP padding and ~280 DIP of chips — real need is
   ~723-753 DIP, so between 640 and ~753 the chips clip ("In progres").
5. **Also found:** "More like this" remounts and refetches on every keystroke (11 identical pathfinder POSTs in 7 s;
   `Episode.Reader.cs:95-96`, `Show.Page.cs:972`); up-next and new-since show the same two episodes; the hero's
   progress/"N min left" isn't live (`Show.UI.cs:462-463,488`); every list item estimates 96 DIP (head ~500, footer ~400)
   so scroll restore is imprecise (`Show.Page.cs:681`); "More like this" cards have no menu (`Episode.Reader.cs:112-114`);
   the "nothing matches" reset leaves the narrow search field open (`Show.Page.cs:420`).

**Changes (agent C; ◆ = owner decisions 2026-10-01: results mode, keep the numeral card, app + engine fix):**
- ◆ **Results mode:** while a status filter or a search is active the head is hidden (not filtered); sort alone never
  hides it. Pure `ShowReaderRules.ResultsMode(status, find)` + `HeadSlot(head, resumeSlot, headShown)` (the hero's episode
  leaves the list only while the head shows); `ReaderSnap.HeadShown` in the head hash; `HeadStamp.Shown`; `VisitHead`
  renders an empty box when hidden (stays mounted at the same list position). Count = rows in results mode.
- ◆ **Up-next keeps the numeral card, fixed:** `Episode.Menu` attached (the Search `Search.UI.cs:492` / Recents
  `Recents.UI.cs:264` idiom) → right-click works. The lead is decided **per row, so a row never mixes**: pure
  `ShowReaderRules.MiniLead(allNumbered, allHaveArt) → Numeral | Art | None` — every card numbered → numerals (the
  design); otherwise covers (40×40 `Controls.Artwork`, card radius); neither → no lead column at all (no blank 34 DIP).
  When covers lead, a card's number moves into its meta line ("#35 · 32 min · Sep 27") like the list rows
  (`Episode.UI.cs:504`). Loading/failure placeholders reserve the same lead. Tests: all numbered → Numeral; one unnumbered
  with art → Art; no art and unnumbered → None. The resume hero gets `Episode.Menu` too; `DoorData` gains `Menu` and the
  doors attach it; "More like this" cards get their menu.
- **One episode, one place:** up-next skips episodes the new-since block shows.
- **No wrap row in the head:** the cards/doors lay out in computed equal columns (`Columns(contentWidth, 220, 12, 3)`)
  instead of `Tiles`; the year strip's 30 DIP is always reserved (filtering never reflows the list sideways); a
  measured-vs-expected height warning like `Artist.Reader.cs:866-884`; per-item-type height seeds like
  `Artist.Reader.cs:377`.
- ◆ **Engine fix** (`../fluent-gpu`): `ArrangeWrap` takes each child's base size from `Measure(c, availMain)` (cached,
  cheap) instead of stored bounds; engine test: three grow tiles at 730 → 700 → 730 → 700 with nothing dirty, the next
  sibling sits below the tiles every time.
- **Measured toolbar** (the album/playlist `CommandBarLayout.Resolve` pattern, `Track.Rules.cs:688-760`, 16-DIP
  hysteresis): `ToolbarNeeds(Filters, Sort, SelectedWord)` + `ToolbarLayout.For(width, needs, narrowPad, previous)` →
  `Full → FindIcon → CompactSort → FilterMenu`; chips never shrink (natural-width row; at the last stage they fold into a
  "selected filter ▾" menu); the chip rail exposes its total width (`Controls.Words.Rail`, which already tracks each
  word's x/width); the toolbar measures itself, not the reader. Search stays in the sticky toolbar (album/playlist
  precedent); when opened at a collapsed stage the field takes over the sort/select area and the chips stay; Ctrl+F stays
  global.
  ```
  Wide        All 326  Unplayed 320  In progress 5  Played 1   [⌕ Find in this show ]  | Newest Oldest  [✓]
  Medium      All 326  Unplayed 320  In progress 5  Played 1   ⌕  | Newest Oldest  [✓]
  Med + find  All 326  Unplayed 320  In progress 5  Played 1   [⌕ why ger          x]
  Narrow      All 326  Unplayed 320  In progress 5  Played 1   ⌕  ⇅  [✓]
  Narrowest   [Unplayed 320 ▾]                                 ⌕  ⇅  [✓]
  ```
- **Small fixes:** "More like this" request hoisted onto the page model (cached per show; no refetch on filter/keystroke);
  the hero's progress and "N min left" read the playback clock like list rows; the "nothing matches" reset also closes
  the narrow search field.
- **Files (C):** `Entities/Show.Page.cs`, `Show.UI.cs`, `Show.Rules.cs`, `Platform/Controls.Podcast.cs`,
  `Platform/Controls.Words.cs` (total-width signal), `Entities/Episode.Reader.cs`; engine `FlexLayout.cs` + its test
  (C-E, engine repo). Tests: `ShowReaderTests` (HeadSlot, ResultsMode, hero-only search → one row, count = rows,
  up-next ∩ new-since = ∅), `PodcastShowToolbarTests` replaced (Full at exact fit; for every width 300–1600 chip room ≥
  chip width unless FilterMenu; 16-DIP headroom to go richer; unmeasured → Full).

## Build — two waves of Sonnet 5.5 implementers on disjoint files (orchestrator alone builds/tests/launches)

**Step 0 (orchestrator):** write `docs/plans/wavee/profile-pages-implementation.md` — the three Opus designs' code verbatim
plus both reconciliation lists (the repo's "plans with real code" rule); each implementer brief quotes its section.

**Wave 1 — data + independent fixes (parallel):**
| Agent | Files |
|---|---|
| **D1** | User.cs, User.Profile.cs (new), Edges.Staging.cs, Fetch.Routes.cs, Fetch.Edges.cs, Detail.cs, Entities.Fake.cs, Entities.Fake.Profile.cs (new); UserProfileCommitTests (new), StoreTests, FetchRoutesTests |
| **D2** | Spotify.Api.cs, Spotify.Api.Profile.cs (new), Spotify.cs, Spotify.Decode.Profile.cs (new), Spotify.Decode.Traits.cs, Protos/user_profile_view.proto (new), CoverToken.cs (new), Controls.cs (ArtUrl hunk), Controls.Cover.cs (new); ProfileDecodeTests, CoverTokenTests (new), DecodeTests, SpotifyApiTests, SpotifySessionTests, ControlsTests |
| **D3** | Spotify.Api.Profile.Follow.cs, Spotify.Library.Profile.cs, User.Follow.cs, User.Profile.Rules.cs (all new), Spotify.Library.cs (one field), User.UI.cs (§8 arms); ProfileAskTests, ProfileFollowTests (new) |
| **A** | Daylist.cs, Home.Host.cs (rollover region), Home/Daylist.UI.cs, Playlist.UI.cs (countdown leaf only), loc daylist keys ×3, Daylist*Tests |
| **C** | Show.Page.cs, Show.UI.cs, Show.Rules.cs, Controls.Podcast.cs, Controls.Words.cs, Episode.Reader.cs; ShowReaderTests, PodcastShowToolbarTests |
| **C-E** | engine `../fluent-gpu`: FlexLayout.cs (`ArrangeWrap`) + a wrap-layout test in the engine's test suite |

No build between the waves (owner, 2026-10-01): wave 2 starts as soon as wave 1's agents report; wave-2 agents read
the landed wave-1 code instead of compiling it.

**Wave 2 — UI against the landed data (parallel):**
| Agent | Files |
|---|---|
| **R1** | Shell.cs, Profile.Route.cs (new), Search.UI.cs/Search.Rules.cs (OpenHit, CanOpen), Playlist.UI.cs (owner block, members flyout, CoverArt mosaic hunk), Detail.UI.cs/Detail.UI.Hero.cs (owner text), Rail.UI.cs (friend rows), Actions.Rules.cs + Shell.Overlays.UI.cs (Profile menu row), wavee skill deep-link doc; ShellRoutesTests, BootOrderingTests, ShellOmnibarTests, SearchFacetRulesTests, SearchHitDataTests, ActionMenuRulesTests, ShellNavTests, ProfileListRouteTests (new) |
| **R2** | Profile.Lists.cs, Profile.Lists.Page.cs (new), Surface.Parts.cs/Surface.Host.cs (circular seed), User.UI.cs (`LetterPlate` internal); ProfileListRulesTests (new) |
| **U1** | Profile.Rules.cs, Profile.Page.cs (new; `Profile.InstallPages` registers User + ProfileList); ProfilePageRulesTests, ProfileHeroLayoutTests (new) |
| **U2** | Profile.UI.cs (new); loc `person` group (page + `person.list.*`) in en-US / nl / ko-KR |
| **A2** | Spotify.Connect.cs (FrameIgnored block), Spotify.Library.cs (`OnDealerPush` returns handled), Spotify.Dealer.Rules.cs (`DealerFrameDisposition`), Capture.EchoDiff.cs; DealerFrameDispositionTests (new) |

**Wave 1 also runs C** (podcast show page, below) — its files are disjoint from every profile/daylist agent. C adds no
loc keys (it reuses existing ones); if one proves unavoidable it lists it in its report and the orchestrator adds it
after wave 2.

## Verification
- **One final build at the end, nothing in between** (owner): `dotnet build Wavee.slnx` Debug **and** Release clean
  (`TreatWarningsAsErrors`; the test project compiles as part of the solution, so the new/edited tests must compile).
  The orchestrator fixes integration errors from that one build.
- Tests are written by the agents but **not run** unless the owner asks (memory: skip the suite mid-iteration).
- Engine (C-E): the same one final build includes `dotnet build src/FluentGpu.slnx` Debug + Release in `..\fluent-gpu`
  (the app build pulls the engine in anyway); VerticalSlice/engine tests only if the owner asks.
- Live check: publish to a side folder; `--fake` (own profile, a followed user, an unfollowed user) and a real
  session (own profile with top artists; another user with 118 followers; a hidden-follows profile; a bad user id → the
  unavailable face); follow/unfollow round trip + count; "See all" → list tabs, chips, find box, letter strip; owner
  clicks from playlist, search hit, friend rail, account menu, a `spotify:user:` deep link; shimmer → reveal with no
  layout jump at all four hero tiers; daylist rollover face at a window end; Diagnostics anomaly list without the
  playlist/collection false ambers. Podcast page (Patrick Boyle On Finance): filter/search → results only with count =
  rows, sort keeps the head; right-click on up-next, resume hero, doors, More-like-this; no blank lead column; no
  overlap when toggling filters; toolbar at widths 1600 → 360 (search collapses before any chip clips); one "More like
  this" request per visit. **Green line:** reproduce with the reported filter toggles, read its pixel y and compare with
  the reader top + 48, the player-bar top and 256-px tile seams; capture `--fg layout-verify` evidence; fix in the same
  round if it is ours, else file it on fluent-gpu.
- Issues to file **after approval** (github-triage: every modifying `gh` call approved first): profile pages
  (`type: feature, area: detail-pages`), daylist updating state (`type: enhancement, area: home`), capture label
  (`type: bug, area: diagnostics`), podcast page (`type: bug, area: detail-pages`), and the wrap-layout bug on
  `christosk92/fluent-gpu`; CHANGELOG bullets end with ` (#n)`, commit bodies `Fixes #n`. Commit/push only when asked.
- After plan mode: save the owner's "no builds between waves — one final build" to the existing test-suite memory.


---

# Appendices — the design outputs, verbatim

**Precedence:** where an appendix disagrees with a *Reconciliation* section above (or with Part C's owner decisions), the reconciliation wins. Appendix line references are to the tree as of 2026-10-01.


## Appendix D — Data layer (D1, D2, D3)

I have everything I need. Below is the full data-layer design.

# Profile pages: data-layer design (D1, D2, D3)

Paths are relative to `C:\wavee\waveemusic\src\apps\Wavee` unless they say otherwise. "Proposal" means the data contract proposal in the plan file.

## 0. Findings that change the proposal

I checked each of these against the code.

1. **A 404 must be stored as a known "unavailable" answer, not left as an unanswered ask.**
   - `Fetch.Answer` calls `ReviewMisses` (`Entities/Fetch.Miss.cs:142`). A row batch whose rows didn't change gets retried twice, then sealed with `Table.Failed` set.
   - So "Asked ∧ Inflight==0 ∧ ¬Known ∧ ¬Failed" never happens for a bare 404. It becomes two wasted retries and then a "Failed" page.
   - Fix: the decoder stages `Social|Follow` plus `UserFlags.Unavailable` and two empty shelf runs (`Decode.ProfileUnavailable`).
2. **ProfilePlaylists and ProfileArtists must not be `FetchEdge`s.**
   - Edge buckets never dedupe against each other: `DropBlocked` only drops edges blocked by row batches.
   - The row's disk-first leg (`Fetch.Plan`, rows with `FetchedAt==0` go to `Store.Read`) means no row batch is in flight when the edge buckets send.
   - Result: up to three GETs of the same profile.
   - `FetchEdge`'s own doc already says a side-effect relation "is asked by asking for the row's group". These two shelves ride `UserFields.Social`.
   - Only `ProfileFollowers` and `ProfileFollowing` (separate endpoints) become `FetchEdge`s.
3. **Social must not be persisted.**
   - If it were, the disk leg would restore stale Social, and the network leg would ask only `Follow`. That goes to `isFollowingUsers`, so the riding shelves never land and shimmer forever.
   - `UserShape` therefore persists Identity only, plus the new colour.
   - Cost: the DDL changes, so every install moves to a new `library.<fp>.db` once (`Store.FileName`).
4. **A follow write races the mount-time refresh.**
   - A profile GET sent just before the click can land after it and undo the optimistic flip.
   - Fix: hold the Follow and Social group authorities at `Authority.Local` while the write is out. `Accepts` refuses Full answers over Local.
5. **`Controls.ArtUrl` breaks every `spotify:` cover token today.** It produces `https://i.scdn.co/image/spotify:image:…`. Fixed with `CoverToken` and a mosaic path (§2).
6. **The old user commit wiped avatars.** `CommitUsers` writes Image unconditionally, so a Thin mention with no avatar blanked a known one. New rule: an empty image clears only at Full (kind 15 or the profile view: "this user has no image").
7. **`--fake` would shimmer forever.** The fake users are `spotify:user:` URIs with no registered provider, so their buckets wait forever. The seed must provide Social and the four shelves.
8. **`User.Image` (a property) is safe next to `using static Ui`.** The `Image(…)` calls in `User.Cover.cs:113/309` are invocations. C# member lookup drops non-invocable members when invoking, so they still bind to `Ui.Image`.
9. **The kind-15 test fixture writes field 11 as a string** (`DecodeTests.ProfileBody`). `Message().Varint(1)` skips it and reads 0, so nothing crashes, but the fixture must become an `Int32Value`.

## 1. D1: entity, edges, routes table, persistence, seed

### 1.1 `Entities/User.cs`

**§1: `UserFields`** (add `Follow`, widen `All`) and a new **`UserFlags`** enum:

```csharp
[Flags]
public enum UserFields : uint
{
    /// <summary>Display name, avatar and the brand avatar COLOUR — what an owner line, an added-by cell, a friend row and
    /// the profile hero need. Kind 15 and the profile view both fill it.</summary>
    Identity = 1 << 0,
    /// <summary>THE PROFILE VIEW's facts (user-profile-view/v3, Spotify.Decode.Profile.cs): follower / following counts,
    /// the public-playlist total, the CurrentUser / ShowFollows / AllowFollows / Unavailable flags — AND the two shelves
    /// that ride the same answer (<see cref="Edges.ProfilePlaylists"/>, <see cref="Edges.ProfileArtists"/>), which is why
    /// they have no FetchEdge: they are asked by asking the row for this group. Never persisted (a disk-restored Social
    /// would leave the riding shelves unasked — profile pages plan, finding 3).</summary>
    Social = 1 << 1,
    /// <summary>(unchanged doc)</summary>
    ContentFilters = 1 << 2,
    /// <summary>Whether the VIEWER follows this user (<see cref="UserFlags.Followed"/>): the profile view's field 6, or
    /// <c>isFollowingUsers</c> asked alone. Its own group because a follow write moves it alone (User.Follow.cs). Never
    /// persisted.</summary>
    Follow = 1 << 3,

    All = Identity | Social | ContentFilters | Follow,
}

/// <summary>User booleans as bits (P3), each written under its GROUP's mask: <see cref="SocialMask"/> by a Social answer,
/// <see cref="FollowMask"/> by a Follow answer — so one never clears the other's bits.</summary>
[Flags]
public enum UserFlags : uint
{
    None = 0,
    /// <summary>The profile view's <c>is_current_user</c> (f10): set only on the viewer's own profile.</summary>
    CurrentUser = 1 << 0,
    /// <summary><c>show_follows</c> (f24 — research §2 marks 23/24 as a guess).</summary>
    ShowFollows = 1 << 1,
    /// <summary><c>allow_follows</c> (f23, same caveat).</summary>
    AllowFollows = 1 << 2,
    /// <summary>The profile view answered 404: a KNOWN negative, staged so the planner neither retries it as an omission
    /// (Fetch.Miss.cs) nor reads it as a failure.</summary>
    Unavailable = 1 << 3,
    /// <summary>The VIEWER follows this user (the <see cref="UserFields.Follow"/> group's one bit).</summary>
    Followed = 1 << 4,

    SocialMask = CurrentUser | ShowFollows | AllowFollows | Unavailable,
    FollowMask = Followed,
}
```

**§2: `UserTable`** (new columns, plus `GrowColumns` lines for each):

```csharp
    public Column<StringId> Name, Image;
    /// <summary>The brand avatar colour, 0xFFRRGGBB (kind 15 f11, the profile view f16, a list entry f11); 0 = none
    /// stated. Identity group; a 0 never blanks a stated colour.</summary>
    public Column<uint> Color;
    public Column<int> Followers, Following;
    /// <summary>The profile view's <c>total_public_playlists_count</c> (f9). Social group.</summary>
    public Column<int> PublicPlaylists;
    /// <summary><see cref="UserFlags"/>, mask-merged per group (CommitUsers).</summary>
    public Column<uint> Flags;
    ...
    /// <summary>Per-group authority (D16). <see cref="FollowAuthority"/> is held at Local while a follow write is out.</summary>
    public Column<byte> IdentityAuthority, ExtrasAuthority, FiltersAuthority, FollowAuthority;
    // GrowColumns: + Color, PublicPlaylists, Flags, FollowAuthority (ReleaseText unchanged: no new text column)
```

**§7: `StagedUser`**, adding these fields:

```csharp
    public int Followers, Following, PublicPlaylists;
    /// <summary>0xFFRRGGBB, 0 = not stated.</summary>
    public uint Color;
    /// <summary><see cref="UserFlags"/> this answer asserts — applied per group under that group's mask.</summary>
    public uint Flags;
```

**`CommitUsers`** (replaces the row loop body; the `CommitContentFilters` call stays):

```csharp
                if ((row.Known & (uint)UserFields.Identity) != 0
                    && t.Accepts(slot, (uint)UserFields.Identity, authority, in t.IdentityAuthority))
                {
                    t.SetText(ref t.Name, slot, s.Intern(row.Name));
                    // THE AVATAR (profile pages plan, finding 6): an incoming one passes the cover latch (a 64 never
                    // replaces a visible 300; different art replaces); an EMPTY one blanks only when the speaker is the
                    // user's own profile (Full: kind 15 / the profile view say "no image") — a Thin mention that merely
                    // carried none says nothing about the avatar.
                    if (!row.Image.IsEmpty)
                    {
                        var incoming = s.Intern(row.Image);
                        if (Detail.CoverLatch.AcceptsImage(t.Image[slot], incoming)) t.SetText(ref t.Image, slot, incoming);
                    }
                    else if (authority >= Wavee.Authority.Full) t.ClearText(ref t.Image, slot);
                    if (row.Color != 0) t.Color[slot] = row.Color;               // 0 is "not stated", never a blank
                    t.Applied(slot, (uint)UserFields.Identity, authority, ref t.IdentityAuthority);
                }

                if ((row.Known & (uint)UserFields.Social) != 0
                    && t.Accepts(slot, (uint)UserFields.Social, authority, in t.ExtrasAuthority))
                {
                    t.Followers[slot] = row.Followers;
                    t.Following[slot] = row.Following;
                    t.PublicPlaylists[slot] = row.PublicPlaylists;
                    t.Flags[slot] = (t.Flags[slot] & ~(uint)UserFlags.SocialMask) | (row.Flags & (uint)UserFlags.SocialMask);
                    t.Applied(slot, (uint)UserFields.Social, authority, ref t.ExtrasAuthority);
                }

                if ((row.Known & (uint)UserFields.Follow) != 0
                    && t.Accepts(slot, (uint)UserFields.Follow, authority, in t.FollowAuthority))
                {
                    t.Flags[slot] = (t.Flags[slot] & ~(uint)UserFlags.FollowMask) | (row.Flags & (uint)UserFlags.FollowMask);
                    t.Applied(slot, (uint)UserFields.Follow, authority, ref t.FollowAuthority);
                }
```

**`UserShape`** persists Identity (with colour) only:

```csharp
    static readonly StoreColumn[] Cols =
    [
        new("name", StoreType.Text, StoreColumnFlags.Title),
        new("image", StoreType.Text),
        new("color", StoreType.Int),
        new("identity_auth", StoreType.Int, StoreColumnFlags.Authority),
    ];
    const uint PersistedFields = (uint)UserFields.Identity;

    public override void Save(Staging s, RowWriter w)
    {
        var rows = s.StagedUsers;
        if (rows is null) return;
        var span = rows.Span;
        for (int i = 0; i < span.Length; i++)
        {
            ref readonly var row = ref span[i];
            if ((row.Known & PersistedFields) == 0) continue;     // Social / Follow / the 404 negative: never on disk
            w.Text(0, row.Name);
            w.Text(1, row.Image);
            if (row.Color != 0) w.Int(2, row.Color); else w.Null(2);   // NULL coalesces: a colourless answer keeps the stored one
            w.Int(3, (int)row.Authority);
            w.Emit(row.Id, row.Known & PersistedFields, Entities.Now, Entities.Now);
        }
    }

    public override void Load(RowReader r, Staging into)
    {
        ref var row = ref into.Users.Add();
        row.Id = r.Uri;
        row.Name = r.Text(0);
        row.Image = r.Text(1);
        row.Color = (uint)r.Int(2);
        row.Known = r.Known & PersistedFields;
        row.Authority = (Authority)r.Int(3);
    }
```

The class doc must say: Social and Follow are never persisted (finding 3), and the shape change moves installs to a new file once.

### 1.2 `Entities/User.Profile.cs` (new)

```csharp
// ── Entities/User.Profile.cs — the profile page's relations, payload and handle reads (profile pages plan, D1) ──────
using FluentGpu.Foundation;

namespace Wavee;

/// <summary>A profile card's follow bits. Viewer-relative vs owner-relative is the WIRE's distinction (research §2).</summary>
[Flags]
public enum ProfileCardFlags : byte
{
    None = 0,
    /// <summary>The VIEWER follows the card's target (playlist f7, following-list artist f7, list user f6).</summary>
    ViewerFollows = 1 << 0,
    /// <summary>The PROFILE OWNER follows the artist (recently-played f5).</summary>
    OwnerFollows = 1 << 1,
}

/// <summary>The four profile relations, as the UI names them.</summary>
public enum ProfileShelf : byte { Playlists, Artists, Followers, Following }

/// <summary>One card on a profile shelf or list: which TABLE the target slot indexes (Following mixes artists and users —
/// the <see cref="KindEdge"/> shape), its follow bits, and the follower count the card states. No text: the name and
/// cover land on the target ROW (Thin), so <see cref="Edges.ReleaseText"/> has nothing to walk here.</summary>
public readonly record struct ProfileCardEdge(EntityKind Kind, ProfileCardFlags Flags, int Followers)
{
    public EntityRef Ref(int target) => new(Kind, target);
    public bool ViewerFollows => (Flags & ProfileCardFlags.ViewerFollows) != 0;
    public bool OwnerFollows => (Flags & ProfileCardFlags.OwnerFollows) != 0;

    /// <summary>Which kinds a relation admits — the decoder's and the commit's one filter. PURE.</summary>
    public static bool Admits(Relation relation, EntityKind kind) => relation switch
    {
        Relation.ProfilePlaylists => kind == EntityKind.Playlist,
        Relation.ProfileArtists => kind == EntityKind.Artist,
        Relation.ProfileFollowers => kind == EntityKind.User,
        Relation.ProfileFollowing => kind is EntityKind.Artist or EntityKind.User,
        _ => false,
    };
}

public sealed partial class Edges
{
    /// <summary>Parent = the user row. Playlists/Artists RIDE the profile view (asked through UserFields.Social, never
    /// door-asked); Followers/Following are their own whole, unpaged reads (FetchEdge.ProfileFollowers/…Following).</summary>
    public readonly EdgeTable<ProfileCardEdge> ProfilePlaylists = new(), ProfileArtists = new(),
                                               ProfileFollowers = new(), ProfileFollowing = new();
}

public readonly partial struct User
{
    // ── identity (UI-frozen names) ──
    public string Name => Entities.Strings.Resolve(T.Name[Slot]);
    /// <summary>The avatar as a renderable url (<see cref="Controls.ArtUrl"/>), or null → paint the initial on <see cref="Color"/>.</summary>
    public string? Image => Controls.ArtUrl(T.Image[Slot]);
    /// <summary>0xFFRRGGBB brand colour, 0 = none. Gated on Identity: a recycled slot's stale column never paints.</summary>
    public uint Color => Knows(UserFields.Identity) ? T.Color[Slot] : 0u;

    // ── social (read when Knows(UserFields.Social)) ──
    public int PublicPlaylists => T.PublicPlaylists[Slot];
    public UserFlags Flags => (UserFlags)T.Flags[Slot];
    public bool IsCurrentUser => (Slot > Wavee.Table.None && Slot == Entities.Current.MeSlot) || Has(UserFields.Social, UserFlags.CurrentUser);
    public bool IsFollowedByViewer => Has(UserFields.Follow, UserFlags.Followed);
    public bool ShowFollows => Has(UserFields.Social, UserFlags.ShowFollows);
    public bool AllowFollows => Has(UserFields.Social, UserFlags.AllowFollows);
    public bool IsUnavailable => Has(UserFields.Social, UserFlags.Unavailable);
    bool Has(UserFields group, UserFlags flag) => (T.Known[Slot] & (uint)group) != 0 && (T.Flags[Slot] & (uint)flag) != 0;

    // ── the load marks a readiness rule reads (ProfileLoadRule) ──
    public bool IsAsked(UserFields groups) => (T.Asked[Slot] & (uint)groups) != 0;
    public bool IsInflight => T.Inflight[Slot] != 0;
    public bool IsFailed(UserFields groups) => T.IsFailed(Slot, (uint)groups);
    public bool IsStale(UserFields groups) => T.IsStale(Slot, (uint)groups);

    // ── the relations ──
    public static EdgeTable<ProfileCardEdge> ProfileRelation(ProfileShelf shelf) => shelf switch
    {
        ProfileShelf.Artists => Entities.Current.Edges.ProfileArtists,
        ProfileShelf.Followers => Entities.Current.Edges.ProfileFollowers,
        ProfileShelf.Following => Entities.Current.Edges.ProfileFollowing,
        _ => Entities.Current.Edges.ProfilePlaylists,
    };
    /// <summary>The door a shelf is asked through: the two lists have their own; the riding shelves have none (Social).</summary>
    public static FetchEdge FetchEdgeOf(ProfileShelf shelf) => shelf switch
    {
        ProfileShelf.Followers => FetchEdge.ProfileFollowers,
        ProfileShelf.Following => FetchEdge.ProfileFollowing,
        _ => FetchEdge.None,
    };
    public ReadOnlySpan<int> ProfileTargets(ProfileShelf shelf) => ProfileRelation(shelf).Targets(Slot);
    public ReadOnlySpan<ProfileCardEdge> ProfileCards(ProfileShelf shelf) => ProfileRelation(shelf).Payload(Slot);
    public EdgeState ProfileReadiness(ProfileShelf shelf) => ProfileRelation(shelf).Readiness(Slot);
    public int ProfileFailure(ProfileShelf shelf) => ProfileRelation(shelf).FailureOf(Slot);
    public int ProfileCount(ProfileShelf shelf) => ProfileRelation(shelf).Count(Slot);
    public int ProfileTotal(ProfileShelf shelf) => ProfileRelation(shelf).Total(Slot);
    public uint ProfileVersion(ProfileShelf shelf) => ProfileRelation(shelf).Version(Slot);
    /// <summary>The own account's "Top artists this month" (Home.Feeds.EnsureTopContent; ranked).</summary>
    public ReadOnlySpan<int> TopArtistSlots => E.UserTopArtists.Targets(Slot);
}

public static partial class Entities
{
    /// <summary>The user twin of <c>Invalidate(Playlist, …)</c>: known groups go stale (still rendering) and are asked again.</summary>
    public static void Invalidate(User row, UserFields groups, FetchPriority priority = FetchPriority.Visible)
    {
        int slot = row.Slot;
        Fetch.Invalidate(Current, Current.Users, new ReadOnlySpan<int>(in slot), (uint)groups, priority);
    }
}
```

### 1.3 `Entities/Edges.Staging.cs`

- Append to `Relation` (after `AlbumMerch`): `ProfilePlaylists, ProfileArtists, ProfileFollowers, ProfileFollowing`. Doc: "Appended: parent = user row; payload `ProfileCardEdge`; Following is cross-kind."
- `ParentTable`: add the four to the `Current.Users` arm.
- Scratch array: `static ProfileCardEdge[] s_edgeProfileCard = new ProfileCardEdge[64];`, plus a line in `Grow`.
- `CommitEdges` arm, placed before `default:`:

```csharp
                case Relation.ProfilePlaylists:
                case Relation.ProfileArtists:
                case Relation.ProfileFollowers:
                case Relation.ProfileFollowing:
                    {
                        // A profile shelf or list (User.Profile.cs). CROSS-KIND for Following (artists then users, wire
                        // order): each target resolves in its OWN kind's table and the kind rides the payload. A kind the
                        // shelf does not admit is dropped here as well as at decode. Union read: At = followers, B0 = flags.
                        int n = 0;
                        for (int j = 0; j < page.Length; j++)
                        {
                            ref readonly var e = ref page[j];
                            var kind = e.Target.Kind(s);
                            if (!ProfileCardEdge.Admits(run.Relation, kind) || TableFor(kind) is not { } table) continue;
                            int target = s.Slot(table, in e.Target);
                            if (target == Table.None) continue;
                            s_edgeTargets[n] = target;
                            s_edgeProfileCard[n++] = new ProfileCardEdge(kind, (ProfileCardFlags)e.B0, e.At);
                        }
                        Land(ProfileRelation(run.Relation), parent, n, s_edgeProfileCard, in run);
                        break;
                    }
```

```csharp
    static EdgeTable<ProfileCardEdge> ProfileRelation(Relation relation) => relation switch
    {
        Relation.ProfileArtists => Current.Edges.ProfileArtists,
        Relation.ProfileFollowers => Current.Edges.ProfileFollowers,
        Relation.ProfileFollowing => Current.Edges.ProfileFollowing,
        _ => Current.Edges.ProfilePlaylists,
    };
```

The store never persists these runs: `SaveLibraryEdgesTouchedBy` only lists the library relations. Nothing to change there.

### 1.4 `Entities/Fetch.Routes.cs`

- `FetchEdge`, appended after `ArtistConcerts` (with the doc comment from finding 2): `ProfileFollowers, ProfileFollowing`.
- `PathfinderOp`, appended: `IsFollowingUsers` (doc: `isFollowingUsers`, hash c00e0cb6…, ProfileQueries).
- `SpclientRoute`, appended:
  - `ProfileView`: `/user-profile-view/v3/profile/<id>?playlist_limit=10&artist_limit=10&episode_limit=10&market=from_token` on spclient.wg, protobuf
  - `ProfileFollowers`
  - `ProfileFollowing`
- `s_user`:

```csharp
    static readonly FetchRoute[] s_user =
    [
        // THE PROFILE VIEW (user-profile-view/v3, protobuf, spclient.wg): name, avatar, colour, counts, flags and the
        // viewer's follow — plus the public playlists and recently played artists, which land their relations from the
        // SAME answer. Justified by Social ALONE: an owner chip's / search hit's Identity ask stays the batchable kind-15
        // POST below, and a Follow-only ask takes the one-op query after it. A page asking Identity|Social|Follow sends
        // THIS route and nothing else.
        FetchRoute.Spclient(SpclientRoute.ProfileView,
            (uint)(UserFields.Identity | UserFields.Social | UserFields.Follow), primary: (uint)UserFields.Social),
        // Kind 15 — the batchable identity. The provider still runs the profile view for whoever kind 15 leaves
        // unanswered (Spotify.Api.cs ProfileFallback, G-033).
        FetchRoute.Metadata(UserProfile, (uint)UserFields.Identity),
        FetchRoute.Pathfinder(PathfinderOp.IsFollowingUsers, (uint)UserFields.Follow),
        FetchRoute.Spclient(SpclientRoute.LikedContentFilters, (uint)UserFields.ContentFilters),
    ];
```

- `ForEdge`: `FetchEdge.ProfileFollowers => FetchRoute.Spclient(SpclientRoute.ProfileFollowers, 0), FetchEdge.ProfileFollowing => FetchRoute.Spclient(SpclientRoute.ProfileFollowing, 0),`

### 1.5 `Entities/Fetch.Edges.cs`

- `EdgeTableOf`: `FetchEdge.ProfileFollowers => e.ProfileFollowers, FetchEdge.ProfileFollowing => e.ProfileFollowing,`
- `ParentTableOf`: add both to the `scope.Users` arm.

### 1.6 `Entities/Detail.cs`: `CoverLatch.RenditionRank` (pixel edges)

```csharp
        /// <summary>The PIXEL EDGE a 40-char Spotify image id's size marker names (the last 8 hex of the 16-char prefix) —
        /// albums 640/300/64, artist portraits 640/320/160, user avatars 300/64 (research §5). Only the ORDER matters
        /// (PreferVisible compares two ranks); pixels make the families comparable. Unknown prefixes rank 0.</summary>
        public static int RenditionRank(ReadOnlySpan<char> imageId)
        {
            if (imageId.Length != ImageIdLength) return 0;
            var size = imageId.Slice(8, 8);
            if (size.Equals("0000b273", StringComparison.OrdinalIgnoreCase) || size.Equals("0000e5eb", StringComparison.OrdinalIgnoreCase)) return 640;
            if (size.Equals("00005174", StringComparison.OrdinalIgnoreCase)) return 320;
            if (size.Equals("00001e02", StringComparison.OrdinalIgnoreCase) || size.Equals("0000ee85", StringComparison.OrdinalIgnoreCase)) return 300;
            if (size.Equals("0000f178", StringComparison.OrdinalIgnoreCase)) return 160;
            if (size.Equals("00004851", StringComparison.OrdinalIgnoreCase) || size.Equals("00003b82", StringComparison.OrdinalIgnoreCase)) return 64;
            return 0;
        }
```

### 1.7 Seed

**`Entities/Entities.Fake.cs`:**
- In the `SeedRows` users loop add `row.Color = s_userColors[i];` with `static readonly uint[] s_userColors = [0xFF509BF5u, 0xFFE8115Bu, 0xFF1E3264u, 0xFFAF2896u];`.
- Declare `static partial void SeedProfileSurfaces(long now0);`.
- Call it after `SeedHomeSurfaces(now0);`.

**`Entities/Entities.Fake.Profile.cs`** (new):

```csharp
namespace Wavee;

public static partial class Entities
{
    /// <summary>The four seeded users' profile facts and shelves. No provider answers a fake scope's Social ask, so without
    /// this the profile page would shimmer forever; with it the mount's Invalidate leaves the seed rendering (Stale).</summary>
    static partial void SeedProfileSurfaces(long now0)
    {
        _ = now0;
        var s = Staging.Rent();
        try
        {
            for (int u = 0; u < 4; u++)
            {
                ref var row = ref s.Users.RowFor(s.AddText(Utf8(UserUri(u))), Authority.Seed,
                    (uint)(UserFields.Social | UserFields.Follow));
                row.Followers = 3;
                row.Following = 6;
                row.PublicPlaylists = 3;
                row.Flags = (uint)(UserFlags.ShowFollows | UserFlags.AllowFollows)
                          | (u == 0 ? (uint)UserFlags.CurrentUser : 0u) | (u == 1 ? (uint)UserFlags.Followed : 0u);
            }
            Commit(s);
        }
        finally { Staging.Return(s); }

        var edges = Current.Edges;
        Span<int> targets = stackalloc int[8];
        Span<ProfileCardEdge> cards = stackalloc ProfileCardEdge[8];
        for (int u = 0; u < 4; u++)
        {
            int user = ResolveSeedSlot(EntityKind.User, UserUri(u));
            if (user == Table.None) continue;
            int n = 0;
            for (int k = 0; k < 3; k++)
            {
                targets[n] = ResolveSeedSlot(EntityKind.Playlist, PlaylistUri(u + k));
                cards[n++] = new ProfileCardEdge(EntityKind.Playlist, ProfileCardFlags.None, 3 + 5 * k);
            }
            edges.ProfilePlaylists.ReplaceRun(user, targets[..n], cards[..n]);
            n = 0;
            for (int a = 0; a < 4; a++)
            {
                targets[n] = ResolveSeedSlot(EntityKind.Artist, ArtistUri(3 * u + a));
                cards[n++] = new ProfileCardEdge(EntityKind.Artist, a == 0 ? ProfileCardFlags.OwnerFollows : ProfileCardFlags.None, 10_000 * (a + 1));
            }
            edges.ProfileArtists.ReplaceRun(user, targets[..n], cards[..n]);
            n = 0;
            for (int o = 0; o < 4; o++)
            {
                if (o == u) continue;
                targets[n] = ResolveSeedSlot(EntityKind.User, UserUri(o));
                cards[n++] = new ProfileCardEdge(EntityKind.User, ProfileCardFlags.None, 3);
            }
            edges.ProfileFollowers.ReplaceRun(user, targets[..n], cards[..n]);
            n = 0;
            for (int a = 0; a < 3; a++)
            {
                targets[n] = ResolveSeedSlot(EntityKind.Artist, ArtistUri(u + a));
                cards[n++] = new ProfileCardEdge(EntityKind.Artist, a == 1 ? ProfileCardFlags.ViewerFollows : ProfileCardFlags.None, 25_000 * (a + 1));
            }
            for (int o = 0; o < 4; o++)
            {
                if (o == u) continue;
                targets[n] = ResolveSeedSlot(EntityKind.User, UserUri(o));
                cards[n++] = new ProfileCardEdge(EntityKind.User, o == 1 ? ProfileCardFlags.ViewerFollows : ProfileCardFlags.None, 3);
            }
            edges.ProfileFollowing.ReplaceRun(user, targets[..n], cards[..n]);
        }
    }
}
```

## 2. D2: transport, decoders, proto, cover tokens

### 2.1 `Spotify/Protos/user_profile_view.proto` (new; used only as the tests' encoder)

```proto
syntax = "proto3";
package spotify.userprofileview.v3;
option csharp_namespace = "Wavee.Protocol.UserProfileView";

// GET https://spclient.wg.spotify.com/user-profile-view/v3/profile/{id}?playlist_limit=10&artist_limit=10&episode_limit=10&market=from_token
// Accept: application/x-protobuf → application/vnd.spotify.user-profile-view. Derived from the 2026-10-01 capture
// (docs/plans/wavee/profile-pages-api-research.md §2; zero unknown fields, byte-exact round trip). The app decodes it BY
// HAND (Spotify.Decode.Profile.cs); these generated classes exist for the tests' fixtures.
message UserProfile {
  string uri = 1;
  string name = 2;
  string image_url = 3;
  int32 followers_count = 4;
  int32 following_count = 5;
  bool is_following = 6;
  repeated RecentlyPlayedArtist recently_played_artists = 7;
  repeated PublicPlaylist public_playlists = 8;
  int32 total_public_playlists_count = 9;
  bool is_current_user = 10;
  bool has_spotify_name = 14;
  bool has_spotify_image = 15;
  int32 color = 16;
  bool allow_follows = 23;
  bool show_follows = 24;
  string public_id = 35;
}
message RecentlyPlayedArtist { string uri = 1; string name = 2; string image_url = 3; int32 followers_count = 4; bool owner_follows = 5; }
message PublicPlaylist { string uri = 1; string name = 2; string image_url = 3; int32 followers_count = 4; string owner_name = 5; string owner_uri = 6; bool is_following = 7; }
// GET …/profile/{id}/followers?market=from_token  and  …/following?market=from_token  (whole, unpaged)
message ProfileList { repeated ProfileEntry entries = 1; }
message ProfileEntry { string uri = 1; string name = 2; string image_url = 3; int32 followers_count = 4; bool is_following_user = 6; bool is_following = 7; bool flag8 = 8; int32 color = 11; string public_id = 13; }
```

### 2.2 `Entities/CoverToken.cs` (new, pure)

```csharp
namespace Wavee;

/// <summary>The cover TOKENS a wire may hand over instead of a url, and the one place that turns them into what an image
/// column holds and a surface paints: <c>spotify:image:&lt;file id&gt;</c> and <c>spotify:mosaic:&lt;id&gt;:&lt;id&gt;:…</c> (a
/// cover-less playlist — the client composes a 2×2 of 300-px tiles; the CDN never serves one). PURE, allocation-free,
/// thread-safe: the decoder calls the UTF-8 half on an api thread, Controls the char half on the UI thread.
/// <para>Decision: a mosaic is STORED VERBATIM in the playlist's Image column; <see cref="Controls.ArtUrl"/> paints its
/// lead tile wherever one cover is drawn and <see cref="Controls.MosaicTiles"/> hands a 2×2 all four.</para></summary>
public static class CoverToken
{
    public const string ImagePrefix = "spotify:image:", MosaicPrefix = "spotify:mosaic:";
    /// <summary>Room for <c>https://i.scdn.co/image/</c> + any image id a token carries.</summary>
    public const int MaxImageUrl = 128;

    public static bool IsImageToken(ReadOnlySpan<byte> wire) => wire.StartsWith("spotify:image:"u8);

    /// <summary>A <c>spotify:image:&lt;id&gt;</c> token → its CDN url (UTF-8) in <paramref name="into"/>; 0 when not a token,
    /// empty, or it does not fit.</summary>
    public static int ImageTokenUrl(ReadOnlySpan<byte> wire, Span<byte> into)
    {
        ReadOnlySpan<byte> prefix = "spotify:image:"u8, cdn = "https://i.scdn.co/image/"u8;
        if (!wire.StartsWith(prefix)) return 0;
        var id = wire[prefix.Length..];
        if (id.IsEmpty || cdn.Length + id.Length > into.Length) return 0;
        cdn.CopyTo(into);
        id.CopyTo(into[cdn.Length..]);
        return cdn.Length + id.Length;
    }

    /// <summary>The ONE file a value paints as a single cover: a <c>spotify:image:</c> token's id, a mosaic's LEAD tile, a
    /// bare file id itself; empty for any other <c>spotify:</c> token. A slice of the input.</summary>
    public static ReadOnlySpan<char> FileIdOf(ReadOnlySpan<char> value)
    {
        if (value.StartsWith(ImagePrefix, StringComparison.Ordinal)) return value[ImagePrefix.Length..];
        if (value.StartsWith(MosaicPrefix, StringComparison.Ordinal))
        {
            var rest = value[MosaicPrefix.Length..];
            int colon = rest.IndexOf(':');
            return colon < 0 ? rest : rest[..colon];
        }
        return value.StartsWith("spotify:", StringComparison.Ordinal) ? default : value;
    }

    public static bool IsMosaic(ReadOnlySpan<char> value) => value.StartsWith(MosaicPrefix, StringComparison.Ordinal);

    /// <summary>A mosaic token's tile ids as ranges into <paramref name="value"/>, wire order, empty segments skipped, at
    /// most <c>into.Length</c>.</summary>
    public static int MosaicTiles(ReadOnlySpan<char> value, Span<Range> into)
    {
        if (!IsMosaic(value)) return 0;
        int n = 0, start = MosaicPrefix.Length;
        while (start < value.Length && n < into.Length)
        {
            int rel = value[start..].IndexOf(':');
            int end = rel < 0 ? value.Length : start + rel;
            if (end > start) into[n++] = new Range(start, end);
            if (rel < 0) break;
            start = end + 1;
        }
        return n;
    }
}
```

### 2.3 `Platform/Controls.cs`: the `ArtUrl` hunk

Replace the concat tail after the cache probe:

```csharp
        if (hit is not null && ReferenceEquals(hit.Source, id)) return hit.Url;
        // A provider TOKEN resolves to the one file it paints as a single cover (CoverToken.FileIdOf): a spotify:image:
        // token is that id, a cover-less playlist's spotify:mosaic: (the profile view's public playlists) is its LEAD
        // tile — the 2×2 is MosaicTiles'. A bare file id is itself; any other spotify: token has no url.
        ReadOnlySpan<char> file = CoverToken.FileIdOf(id);
        if (file.IsEmpty) return null;
        string url = string.Concat(CdnPrefix.AsSpan(), file);
        s_artUrls[slot] = new ArtUrlEntry(id, url);
        return url;
```

### 2.4 `Platform/Controls.Cover.cs` (new partial)

```csharp
namespace Wavee;

public static partial class Controls
{
    const int MosaicCacheSize = 256;
    static readonly MosaicEntry?[] s_mosaics = new MosaicEntry?[MosaicCacheSize];
    sealed class MosaicEntry(string source, string[] tiles) { public readonly string Source = source; public readonly string[] Tiles = tiles; }

    /// <summary>The FOUR tile urls of a <c>spotify:mosaic:</c> cover, for <see cref="Mosaic"/>; empty for any other image
    /// (and for a 1-3-tile mosaic — Mosaic's own rule: paint <see cref="ArtUrl"/>, the lead tile, as one cover). Cached
    /// direct-mapped on the id like ArtUrl, so a re-render allocates nothing.</summary>
    public static ReadOnlySpan<string> MosaicTiles(StringId image)
    {
        if (image.IsEmpty) return default;
        string id = Entities.Strings.Resolve(image);
        if (!CoverToken.IsMosaic(id)) return default;
        int slot = image.Value & (MosaicCacheSize - 1);
        var hit = s_mosaics[slot];
        if (hit is not null && ReferenceEquals(hit.Source, id)) return hit.Tiles;
        Span<Range> ranges = stackalloc Range[4];
        if (CoverToken.MosaicTiles(id, ranges) < 4) return default;
        var tiles = new string[4];
        for (int i = 0; i < 4; i++) tiles[i] = string.Concat(CdnPrefix.AsSpan(), id.AsSpan()[ranges[i]]);
        s_mosaics[slot] = new MosaicEntry(id, tiles);
        return tiles;
    }
}
```

**Hand-off to the UI owner of `Entities/Playlist.UI.cs` (recommended).** Add this as the first lines of `CoverArt`:

```csharp
        var server = Controls.MosaicTiles(p.ImageId);                     // a profile card's server-composed 2×2
        if (server.Length == 4) return Controls.Mosaic(server, size, size, Radii.Card);
```

### 2.5 `Spotify/Spotify.Decode.Profile.cs` (new)

```csharp
// ── Spotify/Spotify.Decode.Profile.cs — user-profile-view/v3 → the user row and the four profile relations ─────────
// CORE: pure over a span (ProtoReader), no interner, no live table, zero allocation after warm-up. The identity is the uri
// the caller ASKED with, never field 1 (the kind-15 rule). Field numbers: research §2.
namespace Wavee;

public static partial class Spotify
{
    public static partial class Decode
    {
        /// <summary>A list past this many entries lands its head Partial with the true total (UI-thread commit budget; the
        /// list is unpaged and ServesEdge refuses offset &gt; 0, so nothing re-asks it).</summary>
        public const int ProfileListCap = 5000;

        /// <summary>The profile view → the row (Identity | Social | Follow at Full) + the public playlists (Partial when the
        /// total says there are more) + the recently played artists (EMPTY INCLUDED: absent field 7 is the owner's switch).</summary>
        public static void ProfileView(ReadOnlySpan<byte> proto, ReadOnlySpan<byte> userUri, Staging s)
        {
            StagedId user = Identity(s, userUri);
            if (user.IsEmpty) return;
            ReadOnlySpan<byte> name = default, image = default;
            int followers = 0, following = 0, totalPlaylists = 0;
            uint color = 0, flags = 0;

            // Pass 1: header scalars + field 8. Pass 2 owns field 7, so each run is contiguous whatever order the
            // serializer interleaved the two repeated fields in.
            var playlists = s.Run(Relation.ProfilePlaylists);
            var r = new ProtoReader(proto);
            while (r.Next())
            {
                switch (r.Field)
                {
                    case 2 when r.Wire == 2: name = r.Bytes(); break;
                    case 3 when r.Wire == 2: image = r.Bytes(); break;
                    case 4 when r.Wire == 0: followers = r.Int32(); break;
                    case 5 when r.Wire == 0: following = r.Int32(); break;
                    case 6 when r.Wire == 0: if (r.Bool()) flags |= (uint)UserFlags.Followed; break;
                    case 8 when r.Wire == 2: PublicPlaylistCard(r.Message(), s, ref playlists); break;
                    case 9 when r.Wire == 0: totalPlaylists = r.Int32(); break;
                    case 10 when r.Wire == 0: if (r.Bool()) flags |= (uint)UserFlags.CurrentUser; break;
                    case 16 when r.Wire == 0: color = RgbColor(r.Varint()); break;
                    case 23 when r.Wire == 0: if (r.Bool()) flags |= (uint)UserFlags.AllowFollows; break;
                    case 24 when r.Wire == 0: if (r.Bool()) flags |= (uint)UserFlags.ShowFollows; break;
                    default: r.Skip(); break;
                }
            }
            int cards = playlists.Count;
            int total = Math.Max(totalPlaylists, cards);
            if (cards == 0) playlists.EndEvenIfEmpty(user, total);
            else playlists.End(user, cards >= total ? EdgeState.Complete : EdgeState.Partial, total);

            var artists = s.Run(Relation.ProfileArtists);
            r = new ProtoReader(proto);
            while (r.Next())
            {
                if (r.Field == 7 && r.Wire == 2) RecentArtistCard(r.Message(), s, ref artists);
                else r.Skip();
            }
            artists.EndEvenIfEmpty(user);

            ref var row = ref s.Users.RowFor(user, Authority.Full,
                (uint)(UserFields.Identity | UserFields.Social | UserFields.Follow));
            row.Name = s.AddText(name);
            row.Image = StageImage(s, image);
            row.Color = color;
            row.Followers = followers;
            row.Following = following;
            row.PublicPlaylists = total;
            row.Flags = flags;
            s.Users.Settle();
        }

        /// <summary>The profile view's 404 → a KNOWN negative (finding 1): Social | Follow with <see cref="UserFlags.Unavailable"/>
        /// and both riding shelves Complete-empty — never an omission the miss policy retries, never a failure.</summary>
        public static void ProfileUnavailable(ReadOnlySpan<byte> userUri, Staging s)
        {
            StagedId user = Identity(s, userUri);
            if (user.IsEmpty) return;
            ref var row = ref s.Users.RowFor(user, Authority.Full, (uint)(UserFields.Social | UserFields.Follow));
            row.Flags = (uint)UserFlags.Unavailable;
            s.Users.Settle();
            var playlists = s.Run(Relation.ProfilePlaylists);
            playlists.EndEvenIfEmpty(user);
            var artists = s.Run(Relation.ProfileArtists);
            artists.EndEvenIfEmpty(user);
        }

        /// <summary>…/followers or …/following → ONE whole-list run in wire order (Following: artists, then users). An
        /// empty body is an empty, Complete list. Past <paramref name="cap"/> entries the head lands Partial with the true total.</summary>
        public static void ProfileList(ReadOnlySpan<byte> proto, ReadOnlySpan<byte> userUri, Relation relation, Staging s,
                                       int cap = ProfileListCap)
        {
            if (relation is not (Relation.ProfileFollowers or Relation.ProfileFollowing)) return;
            StagedId user = Identity(s, userUri);
            if (user.IsEmpty) return;
            var run = s.Run(relation);
            int seen = 0;
            var r = new ProtoReader(proto);
            while (r.Next())
            {
                if (r.Field != 1 || r.Wire != 2) { r.Skip(); continue; }
                if (seen++ >= cap) { r.Skip(); continue; }
                ProfileEntry(r.Message(), s, ref run, relation);
            }
            if (seen > cap && run.Count > 0) run.End(user, EdgeState.Partial, seen);
            else run.EndEvenIfEmpty(user);
        }

        static void PublicPlaylistCard(ProtoReader card, Staging s, ref EdgeRun run)
        {
            ReadOnlySpan<byte> uri = default, name = default, image = default, ownerUri = default;
            int followers = 0;
            byte flags = 0;
            while (card.Next())
            {
                switch (card.Field)
                {
                    case 1 when card.Wire == 2: uri = card.Bytes(); break;
                    case 2 when card.Wire == 2: name = card.Bytes(); break;
                    case 3 when card.Wire == 2: image = card.Bytes(); break;
                    case 4 when card.Wire == 0: followers = card.Int32(); break;
                    case 6 when card.Wire == 2: ownerUri = card.Bytes(); break;
                    case 7 when card.Wire == 0: if (card.Bool()) flags |= (byte)ProfileCardFlags.ViewerFollows; break;
                    default: card.Skip(); break;             // owner_name (5) is NOT staged: it would seal the owner's
                }                                            // Identity with no avatar (the Pathfinder User arm's rule)
            }
            StagedId id = Identity(s, uri);
            if (id.IsEmpty || id.Kind(s) != EntityKind.Playlist) return;
            ref var row = ref s.Playlists.RowFor(id, Authority.Thin, name.IsEmpty ? 0u : (uint)PlaylistFields.Identity);
            row.Title = s.AddText(name);
            row.Image = StageImage(s, image);                // a mosaic token stays verbatim (CoverToken)
            row.OwnerUri = ownerUri.IsEmpty ? default : UserUri(s, ownerUri);
            s.Playlists.Settle();
            ref var edge = ref run.Add(id);
            edge.At = followers;
            edge.B0 = flags;
        }

        static void RecentArtistCard(ProtoReader card, Staging s, ref EdgeRun run)
        {
            ReadOnlySpan<byte> uri = default, name = default, image = default;
            int followers = 0;
            byte flags = 0;
            while (card.Next())
            {
                switch (card.Field)
                {
                    case 1 when card.Wire == 2: uri = card.Bytes(); break;
                    case 2 when card.Wire == 2: name = card.Bytes(); break;
                    case 3 when card.Wire == 2: image = card.Bytes(); break;
                    case 4 when card.Wire == 0: followers = card.Int32(); break;
                    case 5 when card.Wire == 0: if (card.Bool()) flags |= (byte)ProfileCardFlags.OwnerFollows; break;
                    default: card.Skip(); break;
                }
            }
            StagedId id = Identity(s, uri);
            if (id.IsEmpty || id.Kind(s) != EntityKind.Artist) return;
            StageThinArtist(s, in id, name, image);
            ref var edge = ref run.Add(id);
            edge.At = followers;
            edge.B0 = flags;
        }

        static void ProfileEntry(ProtoReader e, Staging s, ref EdgeRun run, Relation relation)
        {
            ReadOnlySpan<byte> uri = default, name = default, image = default;
            int followers = 0;
            uint color = 0;
            byte flags = 0;
            while (e.Next())
            {
                switch (e.Field)
                {
                    case 1 when e.Wire == 2: uri = e.Bytes(); break;
                    case 2 when e.Wire == 2: name = e.Bytes(); break;
                    case 3 when e.Wire == 2: image = e.Bytes(); break;
                    case 4 when e.Wire == 0: followers = e.Int32(); break;
                    case 6 when e.Wire == 0:
                    case 7 when e.Wire == 0: if (e.Bool()) flags |= (byte)ProfileCardFlags.ViewerFollows; break;
                    case 11 when e.Wire == 0: color = RgbColor(e.Varint()); break;
                    default: e.Skip(); break;
                }
            }
            StagedId id = Identity(s, uri);
            if (id.IsEmpty) return;
            EntityKind kind = id.Kind(s);
            if (!ProfileCardEdge.Admits(relation, kind)) return;
            if (kind == EntityKind.Artist) StageThinArtist(s, in id, name, image);
            else StageThinUser(s, in id, name, image, color);
            ref var edge = ref run.Add(id);
            edge.At = followers;
            edge.B0 = flags;
        }

        /// <summary>Name (+ portrait when stated) at Thin. Nameless → no row: ArtistFields' Identity commit writes Name
        /// unconditionally, so an Image-only stage would blank a known name.</summary>
        static void StageThinArtist(Staging s, in StagedId id, ReadOnlySpan<byte> name, ReadOnlySpan<byte> image)
        {
            if (name.IsEmpty) return;
            ref var a = ref s.Artists.RowFor(id, Authority.Thin,
                (uint)ArtistFields.Name | (image.IsEmpty ? 0u : (uint)ArtistFields.Image));
            a.Name = s.AddText(name);
            a.Image = StageImage(s, image);
            s.Artists.Settle();
        }

        /// <summary>A nameless mention must not seal Identity (the Pathfinder User arm's rule).</summary>
        static void StageThinUser(Staging s, in StagedId id, ReadOnlySpan<byte> name, ReadOnlySpan<byte> image, uint color)
        {
            if (name.IsEmpty) return;
            ref var u = ref s.Users.RowFor(id, Authority.Thin, (uint)UserFields.Identity);
            u.Name = s.AddText(name);
            u.Image = StageImage(s, image);
            u.Color = color;
            s.Users.Settle();
        }

        /// <summary>A wire cover → what an image column stores: a spotify:image: token becomes its CDN url; https, pickasso
        /// and spotify:mosaic: stay verbatim (CoverToken).</summary>
        static TextRef StageImage(Staging s, ReadOnlySpan<byte> wire)
        {
            if (wire.IsEmpty) return default;
            if (!CoverToken.IsImageToken(wire)) return s.AddText(wire);
            Span<byte> buf = stackalloc byte[CoverToken.MaxImageUrl];
            int n = CoverToken.ImageTokenUrl(wire, buf);
            return n > 0 ? s.AddText(buf[..n]) : default;
        }

        /// <summary>A wire colour (int32 0xRRGGBB, any sign) → the app's 0xFFRRGGBB; 0 stays 0 ("not stated").</summary>
        public static uint RgbColor(ulong wire)
        {
            uint rgb = (uint)(wire & 0xFFFFFFu);
            return rgb == 0 ? 0u : 0xFF000000u | rgb;
        }
    }
}
```

### 2.6 `Spotify/Spotify.Decode.Traits.cs`

- In `UserProfile`, delete the whitespace-plus-`{` JSON sniff.
- Add a field-11 arm:

```csharp
                    case 11: color = RgbColor((ulong)r.Message().Varint(1)); any = true; break;   // { 1: 0xRRGGBB } (research §4)
```

- Change the call to `StageProfile(s, entityUri, name, url, color)`, and set `row.Color = color;` in `StageProfile`.
- Delete the JSON `Profile(...)` method and `using System.Text.Json;` (nothing else in the file uses it).
- Update the file header (G-045's JSON arm is gone).

### 2.7 `Spotify/Spotify.Api.Profile.cs` (new)

```csharp
using System.Text;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Api
    {
        const string ProfilePath = "/user-profile-view/v3/profile/";

        /// <summary>The profile view — the captured spelling: spclient.WG (never the resolved spclient), protobuf, ten cards
        /// per shelf. PURE.</summary>
        public static Route ProfileViewRoute(string username)
            => new(Verb.Get, ApiHost.SpclientWg,
                   ProfilePath + Escaped(username) + "?playlist_limit=10&artist_limit=10&episode_limit=10&market=from_token",
                   CommonProtobuf, RequestKind.Profile);

        /// <summary>The whole, unpaged followers list. PURE.</summary>
        public static Route ProfileFollowersRoute(string username)
            => new(Verb.Get, ApiHost.SpclientWg, ProfilePath + Escaped(username) + "/followers?market=from_token",
                   CommonProtobuf, RequestKind.Profile);

        /// <summary>The whole, unpaged following list (artists, then users). PURE.</summary>
        public static Route ProfileFollowingRoute(string username)
            => new(Verb.Get, ApiHost.SpclientWg, ProfilePath + Escaped(username) + "/following?market=from_token",
                   CommonProtobuf, RequestKind.Profile);

        public static Result ProfileView(string username, CancellationToken ct) => Send(ProfileViewRoute(username), [], ct);
        public static Result ProfileFollowers(string username, CancellationToken ct) => Send(ProfileFollowersRoute(username), [], ct);
        public static Result ProfileFollowing(string username, CancellationToken ct) => Send(ProfileFollowingRoute(username), [], ct);

        /// <summary>SpclientRoute.ProfileView for one user (a row batch's Social, or the kind-15 fallback).</summary>
        static void ProfileViewAnswer(string uri, Staging s, ref FetchOutcome outcome, uint groups)
        {
            Result result = ProfileView(UsernameOf(uri), CancellationToken.None);
            outcome.Note(in result, groups);
            StageProfileView(in result, uri, s);
        }

        /// <summary>200 → the view (an empty body is a valid, empty view); 404 → the known negative. Anything else stages
        /// nothing: the outcome says why.</summary>
        static void StageProfileView(in Result result, string uri, Staging s)
        {
            if (result.Ok) Decode.ProfileView(result.Bytes, Encoding.UTF8.GetBytes(uri), s);
            else if (result.Status == 404) Decode.ProfileUnavailable(Encoding.UTF8.GetBytes(uri), s);
        }

        /// <summary>The followers / following edge for one parent. 404 is "no list" — an empty, Complete answer.</summary>
        static void ProfileListAnswer(string uri, Relation relation, Staging s, ref FetchOutcome outcome, uint groups)
        {
            string username = UsernameOf(uri);
            Result result = relation == Relation.ProfileFollowers
                ? ProfileFollowers(username, CancellationToken.None)
                : ProfileFollowing(username, CancellationToken.None);
            outcome.Note(in result, groups);
            if (result.Ok || result.Status == 404)
                Decode.ProfileList(result.Ok ? result.Bytes : default, Encoding.UTF8.GetBytes(uri), relation, s);
        }
    }
}
```

### 2.8 `Spotify/Spotify.Api.cs`

**`Serves`:**
- Pathfinder list: add `or PathfinderOp.IsFollowingUsers`.
- Spclient list: add `or SpclientRoute.ProfileView or SpclientRoute.ProfileFollowers or SpclientRoute.ProfileFollowing`.

**`ServesEdge`:**

```csharp
            // The profile lists are whole and unpaged (research §2): a later page has nothing to land.
            FetchEdge.ProfileFollowers or FetchEdge.ProfileFollowing => offset <= 0 && Serves(FetchRoutes.ForEdge(edge, 0)),
```

**`AnswerRest` arms:**

```csharp
                case SpclientRoute.ProfileView: ProfileViewAnswer(uri, s, ref outcome, groups); return;
                case SpclientRoute.ProfileFollowers: ProfileListAnswer(uri, Relation.ProfileFollowers, s, ref outcome, groups); return;
                case SpclientRoute.ProfileFollowing: ProfileListAnswer(uri, Relation.ProfileFollowing, s, ref outcome, groups); return;
```

**`AnswerQuery` arm:** `case PathfinderOp.IsFollowingUsers: result = ProfileQueries.FollowStateAnswer(uri, s); break;`

**`ProfileFallback` body:** the loop ends with:

```csharp
                StageProfileView(ProfileView(UsernameOf(uri), CancellationToken.None), uri, s);   // was: JSON Profile + Decode.Profile
```

**`AcceptFor`:** `RequestKind.PlaylistSignals or RequestKind.Profile => "application/x-protobuf",`

**Delete:**
- `ProfileRoute(string)` and `Profile(string, ct)`.
- The unused `UserProfiles(...)`.
- The doc lines that mention them.

### 2.9 `Spotify/Spotify.cs`

Delete the `case RequestKind.Profile:` fold arm (lines 2144–2147). Keep the enum member: `AcceptFor` and the profile `Route`s use it.

## 3. D3: follow, library seam, ask and load rules

### 3.1 `Spotify/Spotify.Api.Profile.Follow.cs` (new)

```csharp
using System.Text;
using System.Text.Json;

namespace Wavee;

/// <summary>What <c>isFollowingUsers</c> said about one user.</summary>
public enum FollowAnswer : byte { Unknown, Following, NotFollowing, NotFound }

public static partial class Spotify
{
    public static partial class Api
    {
        /// <summary>The user-follow operations: ONE persisted hash, three operation names (research §3). DESKTOP identity —
        /// the capture that proved the hash and variables was the desktop client's tuple (risk R3 has the one-word flip).</summary>
        public static class ProfileQueries
        {
            const string Hash = "c00e0cb6c7766e7230fc256cf4fe07aec63b53d1160a323940fce7b664e95596";
            public static readonly Query IsFollowingUsers = new("isFollowingUsers", Hash, false);
            public static readonly Query FollowUsers = new("followUsers", Hash, false);
            public static readonly Query UnfollowUsers = new("unfollowUsers", Hash, false);

            /// <summary><c>{uris:[…]}</c> — uris, not ids. PURE.</summary>
            public static byte[] IsFollowingBody(ReadOnlySpan<string> userUris)
            {
                var vars = new Vars(IsFollowingUsers);
                vars.WriteStrings("uris", userUris);
                return vars.Finish();
            }

            /// <summary><c>{usernames:[&lt;bare id&gt;]}</c> — bare, UNESCAPED ids (UsernameOf), not uris. PURE.</summary>
            public static byte[] FollowBody(string bareId, bool follow)
            {
                var vars = new Vars(follow ? FollowUsers : UnfollowUsers);
                vars.WriteStrings("usernames", [bareId]);
                return vars.Finish();
            }

            public static Result IsFollowingQuery(string userUri, CancellationToken ct)
                => Pathfinder(IsFollowingUsers, IsFollowingBody([userUri]), ct);

            public static Result Follow(string bareId, bool follow, CancellationToken ct)
                => Pathfinder(follow ? FollowUsers : UnfollowUsers, FollowBody(bareId, follow), ct);

            /// <summary>PathfinderOp.IsFollowingUsers (a Follow-only row ask). API THREAD.</summary>
            public static Result FollowStateAnswer(string userUri, Staging s)
            {
                Result result = IsFollowingQuery(userUri, CancellationToken.None);
                if (result.Ok && result.Body.Length > 0) Decode.FollowState(result.Body, userUri, s);
                return result;
            }
        }

        /// <summary>The follow answers, PURE (JsonDocument: one parse per user click / Follow-only ask — not a hot path).</summary>
        public static class ProfileFollowAnswer
        {
            /// <summary><c>data.users[]: User{uri, following} | NotFound{uri}</c> — the entry for <paramref name="userUri"/>;
            /// a single entry whose uri spelling differs (escaping) is still the answer.</summary>
            public static FollowAnswer IsFollowing(byte[] json, string userUri)
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                        || !data.TryGetProperty("users", out var users) || users.ValueKind != JsonValueKind.Array)
                        return FollowAnswer.Unknown;
                    JsonElement? only = users.GetArrayLength() == 1 ? users[0] : null;
                    foreach (var u in users.EnumerateArray())
                    {
                        if (u.ValueKind != JsonValueKind.Object) continue;
                        bool match = u.TryGetProperty("uri", out var uri) && uri.ValueKind == JsonValueKind.String && uri.ValueEquals(userUri);
                        if (match) return Read(u);
                    }
                    return only is { ValueKind: JsonValueKind.Object } one ? Read(one) : FollowAnswer.Unknown;
                }
                catch (JsonException) { return FollowAnswer.Unknown; }

                static FollowAnswer Read(JsonElement u)
                {
                    if (u.TryGetProperty("__typename", out var t) && t.ValueKind == JsonValueKind.String && t.ValueEquals("NotFound"))
                        return FollowAnswer.NotFound;
                    if (!u.TryGetProperty("following", out var f)) return FollowAnswer.Unknown;
                    return f.ValueKind switch
                    {
                        JsonValueKind.True => FollowAnswer.Following,
                        JsonValueKind.False => FollowAnswer.NotFollowing,
                        _ => FollowAnswer.Unknown,
                    };
                }
            }

            /// <summary>Did a 200 <c>followUsers</c>/<c>unfollowUsers</c> take? LENIENT on purpose: only an explicit refusal
            /// (an errors array, a missing op, an <c>…Error</c> typename, <c>result: false</c> or a FAIL/ERROR/DENIED/NOT_ALLOWED
            /// string) is a no — a misread success would revert a real follow and toast an error; a misread refusal is
            /// reconciled by the next mount's refresh. An EMPTY body is a yes.</summary>
            public static bool WriteSucceeded(byte[] json, bool follow)
            {
                if (json.Length == 0) return true;
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return false;
                    if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                        return false;
                    if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                        || !data.TryGetProperty(follow ? "followUsers" : "unfollowUsers", out var op) || op.ValueKind != JsonValueKind.Object)
                        return false;
                    if (!op.TryGetProperty("responses", out var responses) || responses.ValueKind != JsonValueKind.Array) return true;
                    foreach (var r in responses.EnumerateArray())
                    {
                        if (r.ValueKind != JsonValueKind.Object) continue;
                        if (r.TryGetProperty("__typename", out var t) && t.ValueKind == JsonValueKind.String
                            && (t.GetString() ?? "").EndsWith("Error", StringComparison.Ordinal)) return false;
                        if (r.TryGetProperty("result", out var result) && Refuses(result)) return false;
                    }
                    return true;
                }
                catch (JsonException) { return false; }
            }

            static bool Refuses(JsonElement result) => result.ValueKind switch
            {
                JsonValueKind.False => true,
                JsonValueKind.String => result.GetString() is { } v
                    && (v.Contains("FAIL", StringComparison.OrdinalIgnoreCase) || v.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                        || v.Contains("DENIED", StringComparison.OrdinalIgnoreCase) || v.Contains("NOT_ALLOWED", StringComparison.OrdinalIgnoreCase)),
                _ => false,
            };
        }
    }

    public static partial class Decode
    {
        /// <summary><c>isFollowingUsers</c> → the row's Follow group at Full. NotFound is "not followed"; Unknown stages
        /// nothing (the miss policy retries, then seals).</summary>
        public static void FollowState(byte[] json, string userUri, Staging s)
        {
            var answer = Api.ProfileFollowAnswer.IsFollowing(json, userUri);
            if (answer == FollowAnswer.Unknown) return;
            ref var row = ref s.Users.RowFor(Identity(s, Encoding.UTF8.GetBytes(userUri)), Authority.Full, (uint)UserFields.Follow);
            row.Flags = answer == FollowAnswer.Following ? (uint)UserFlags.Followed : 0u;
            s.Users.Settle();
        }
    }
}
```

### 3.2 `Entities/User.Follow.cs` (new)

```csharp
namespace Wavee;

/// <summary>What a follow write changed, so its refusal can put it back exactly.</summary>
public readonly record struct FollowSnapshot(uint Flags, int Followers, byte FollowAuthority, byte ExtrasAuthority, uint Known);

/// <summary>THE OPTIMISTIC HALF of a user follow (Spotify.Library.FollowUser). While the write is out, the Follow and Social
/// group authorities are held at <see cref="Authority.Local"/>: a profile GET issued BEFORE the click (the mount's
/// Invalidate) can then not land the pre-click state over the flip (<c>Table.Accepts</c>: Full &lt; Local). UI thread.</summary>
public static class UserFollowWrite
{
    public static FollowSnapshot Apply(UserTable t, int slot, bool follow)
    {
        var snap = new FollowSnapshot(t.Flags[slot], t.Followers[slot], t.FollowAuthority[slot], t.ExtrasAuthority[slot], t.Known[slot]);
        uint followed = (uint)UserFlags.Followed;
        bool was = (t.Known[slot] & (uint)UserFields.Follow) != 0 && (t.Flags[slot] & followed) != 0;
        t.Flags[slot] = follow ? t.Flags[slot] | followed : t.Flags[slot] & ~followed;
        if (was != follow && (t.Known[slot] & (uint)UserFields.Social) != 0)
            t.Followers[slot] = Math.Max(0, t.Followers[slot] + (follow ? 1 : -1));
        t.FollowAuthority[slot] = (byte)Authority.Local;
        t.ExtrasAuthority[slot] = (byte)Authority.Local;
        t.Bump(slot, (uint)UserFields.Follow);        // the click IS the answer until the server says otherwise
        return snap;
    }

    /// <summary>The server agreed: the held authorities go back, so the next real answer may move the row again.</summary>
    public static void Confirm(UserTable t, int slot, in FollowSnapshot snap)
    {
        t.FollowAuthority[slot] = snap.FollowAuthority;
        t.ExtrasAuthority[slot] = snap.ExtrasAuthority;
        t.Bump(slot);
    }

    /// <summary>The server refused: the bit, the count, the authorities and the Follow known-bit as they were.</summary>
    public static void Revert(UserTable t, int slot, in FollowSnapshot snap)
    {
        t.Flags[slot] = (t.Flags[slot] & ~(uint)UserFlags.FollowMask) | (snap.Flags & (uint)UserFlags.FollowMask);
        t.Followers[slot] = snap.Followers;
        t.FollowAuthority[slot] = snap.FollowAuthority;
        t.ExtrasAuthority[slot] = snap.ExtrasAuthority;
        if ((snap.Known & (uint)UserFields.Follow) == 0) t.Known[slot] &= ~(uint)UserFields.Follow;
        t.Bump(slot);
    }
}
```

### 3.3 `Spotify/Spotify.Library.cs`

Add one field to `Transport`:

```csharp
            public Func<string, bool, Api.Result> FollowUsers =
                static (bareId, follow) => Api.ProfileQueries.Follow(bareId, follow, CancellationToken.None);
```

### 3.4 `Spotify/Spotify.Library.Profile.cs` (new)

```csharp
using InfoBarSeverity = FluentGpu.Controls.InfoBarSeverity;
using Loc = FluentGpu.Localization.Loc;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Library
    {
        static readonly HashSet<(uint Epoch, int Slot)> s_followPending = new();

        /// <summary>Follow or unfollow a user profile (Controls.FollowToggle via User.LibrarySeam). Optimistic
        /// (UserFollowWrite), one write per user at a time, a no-op on your own profile.
        /// <para><b>NO SETTLE REFRESH</b> (the SetCollaborative toggle-bounce precedent, Spotify.Api.Playlist.cs): a profile
        /// GET a few hundred ms after the write can come back from a replica that has not seen it and flip the button back.
        /// The 200 is the answer; the next mount's Invalidate (ProfileAsk) reconciles the exact follower count.</para></summary>
        public static void FollowUser(string userUri, bool follow)
        {
            var scope = Entities.Current;
            if (scope is null || string.IsNullOrEmpty(userUri)) return;
            UserTable users = scope.Users;
            if (!users.TryGetSlot(userUri.AsSpan(), out int slot) || slot == scope.MeSlot) return;
            var user = new User(slot);
            if (user.IsCurrentUser || user.IsFollowedByViewer == follow) return;
            if (s_followPending.Contains((scope.Epoch, slot))) return;          // a double click lands the first click
            if (!CanWrite(out _, out _)) { Unavailable(); return; }
            EntityId id = users.Id[slot];
            FollowSnapshot snap = UserFollowWrite.Apply(users, slot, follow);
            Entities.Publish();
            s_followPending.Add((scope.Epoch, slot));
            string bareId = Api.UsernameOf(userUri);
            var net = Net;
            bool queued = net.Run(() =>
            {
                Api.Result r = net.FollowUsers(bareId, follow);
                bool ok = r.Ok && Api.ProfileFollowAnswer.WriteSucceeded(r.Body, follow);
                int status = r.Status;
                Post(() => SettleFollow(scope, slot, id, follow, snap, ok, status));
            });
            if (!queued) SettleFollow(scope, slot, id, follow, snap, ok: false, status: 0);
        }

        static void SettleFollow(Scope scope, int slot, EntityId id, bool follow, FollowSnapshot snap, bool ok, int status)
        {
            s_followPending.Remove((scope.Epoch, slot));
            if (!ReferenceEquals(scope, Entities.Current)) return;                       // C7
            UserTable users = scope.Users;
            if (slot <= Table.None || slot >= users.Count || users.Id[slot] != id) return;  // recycled since the click
            if (ok) UserFollowWrite.Confirm(users, slot, in snap);
            else
            {
                UserFollowWrite.Revert(users, slot, in snap);
                Log.Warn("library", "follow write refused (" + (follow ? "follow" : "unfollow") + ", status " + status + ")");
                Notify.Say(Loc.Get(status == 0 ? "drag.libraryUnavailable" : "library.writeFailed"), InfoBarSeverity.Error,
                           dedupeKey: "library.write-failed");
            }
            Entities.Publish();
        }
    }
}
```

### 3.5 `Entities/User.UI.cs` §8: the user arms

- In `IsSavedUri`, after the playlist arm:

```csharp
        if (id.Kind == EntityKind.User)
        {
            _ = scope.Users.Changed.Value;
            return scope.Users.TryGetSlot(id, out int u) && new User(u).IsFollowedByViewer;
        }
```

- In `ToggleSavedUri`, after the playlist arm:

```csharp
        if (id.Kind == EntityKind.User)
        {
            bool following = scope.Users.TryGetSlot(id, out int u) && new User(u).IsFollowedByViewer;
            Spotify.Library.FollowUser(uri, !following);                    // a no-op on your own profile
            return;
        }
```

- Update the seam doc: "a user follows through `Spotify.Library.FollowUser`".

### 3.6 `Entities/User.Profile.Rules.cs` (new, pure plus two impure doors)

```csharp
namespace Wavee;

/// <summary>Which profile surface is mounting: the page, or one of its two "Show all" list pages.</summary>
public enum ProfileSurface : byte { Page, Followers, Following }
public enum ProfileRowAsk : byte { None, Ensure, Invalidate }
public enum ProfileEdgeAsk : byte { None, Ensure, Invalidate }
public readonly record struct ProfileAskPlan(ProfileRowAsk Row, ProfileEdgeAsk Followers, ProfileEdgeAsk Following, bool TopArtists);
/// <summary>A profile region's verdict: shimmer / content / "isn't available" vacancy / error vacancy with Retry.</summary>
public enum ProfileLoad : byte { Loading, Ready, Unavailable, Failed }

/// <summary>THE PROFILE DEMAND, once per (user slot, scope epoch) — the PageHost's DepKey. The official client re-reads the
/// profile and both lists on EVERY visit (research §1), so a known row is INVALIDATED (stale keeps rendering), never
/// left; a first visit fires the three reads in PARALLEL (the lists don't wait for ShowFollows); a known profile with
/// hidden follows asks no lists unless it is your own. The riding shelves have no ask — Social brings them.</summary>
public static class ProfileAsk
{
    public const UserFields PageFields = UserFields.Identity | UserFields.Social | UserFields.Follow;

    public static ProfileAskPlan Plan(ProfileSurface surface, bool knowsSocial, bool unavailable, bool showFollows,
                                      bool isCurrentUser, EdgeState followers, EdgeState following)
    {
        if (surface == ProfileSurface.Page)
        {
            bool lists = ListsWanted(knowsSocial, unavailable, showFollows, isCurrentUser);
            return new(knowsSocial ? ProfileRowAsk.Invalidate : ProfileRowAsk.Ensure,
                       lists ? Revisit(followers) : ProfileEdgeAsk.None,
                       lists ? Revisit(following) : ProfileEdgeAsk.None,
                       isCurrentUser);
        }
        // A list page: the page just refreshed the list on this visit — ask only while it is still Unknown (or un-asked by a failure).
        var row = knowsSocial ? ProfileRowAsk.None : ProfileRowAsk.Ensure;
        return surface == ProfileSurface.Followers
            ? new(row, FirstOnly(followers), ProfileEdgeAsk.None, false)
            : new(row, ProfileEdgeAsk.None, FirstOnly(following), false);
    }

    public static bool ListsWanted(bool knowsSocial, bool unavailable, bool showFollows, bool isCurrentUser)
        => !knowsSocial || (!unavailable && (showFollows || isCurrentUser));

    static ProfileEdgeAsk Revisit(EdgeState s) => s == EdgeState.Unknown ? ProfileEdgeAsk.Ensure : ProfileEdgeAsk.Invalidate;
    static ProfileEdgeAsk FirstOnly(EdgeState s) => s == EdgeState.Unknown ? ProfileEdgeAsk.Ensure : ProfileEdgeAsk.None;

    /// <summary>The plan for a live handle (reads the table).</summary>
    public static ProfileAskPlan Plan(User user, ProfileSurface surface)
        => Plan(surface, user.Knows(UserFields.Social), user.IsUnavailable, user.ShowFollows, user.IsCurrentUser,
                Entities.Current.Edges.ProfileFollowers.State(user.Slot), Entities.Current.Edges.ProfileFollowing.State(user.Slot));

    /// <summary>Execute a plan. UI thread; call from the demand effect only.</summary>
    public static void Apply(User user, in ProfileAskPlan plan)
    {
        if (!user.IsValid) return;
        if (plan.Row == ProfileRowAsk.Ensure) Entities.Ensure(user, PageFields);
        else if (plan.Row == ProfileRowAsk.Invalidate) Entities.Invalidate(user, PageFields);
        ApplyEdge(FetchEdge.ProfileFollowers, user.Slot, plan.Followers);
        ApplyEdge(FetchEdge.ProfileFollowing, user.Slot, plan.Following);
        if (plan.TopArtists) Home.Feeds.EnsureTopContent();
    }

    static void ApplyEdge(FetchEdge edge, int parent, ProfileEdgeAsk ask)
    {
        if (ask == ProfileEdgeAsk.Ensure) Entities.EnsureEdge(edge, parent);
        else if (ask == ProfileEdgeAsk.Invalidate) Entities.InvalidateEdge(edge, parent, FetchPriority.Visible);
    }

    /// <summary>The header's Retry: Refresh, never Ensure (a sealed Asked row is a no-op for Ensure).</summary>
    public static void RetryHeader(User user)
    {
        int slot = user.Slot;
        Entities.Refresh(Entities.Current.Users, new ReadOnlySpan<int>(in slot), (uint)PageFields);
    }

    /// <summary>A list's Retry (the two lists only; a riding shelf retries with the header).</summary>
    public static void RetryList(User user, ProfileShelf shelf)
    {
        var edge = User.FetchEdgeOf(shelf);
        if (edge == FetchEdge.None) RetryHeader(user);
        else Entities.RefreshEdge(edge, user.Slot);
    }
}

/// <summary>The profile's load verdicts, PURE over the marks (the SectionScreenLoadRule pattern).</summary>
public static class ProfileLoadRule
{
    /// <summary>The header, off the row's Social group. Unavailable (the 404 flag) wins; a KNOWN row is Ready even while a
    /// refresh is out or after it failed (it keeps rendering); a terminal failure of an unknown row is Failed; asked with
    /// nothing in flight and nothing known (a seal no answer will lift) is Unavailable; otherwise Loading.</summary>
    public static ProfileLoad Header(bool knowsSocial, bool unavailable, bool failed, bool asked, bool inflight)
    {
        if (unavailable) return ProfileLoad.Unavailable;
        if (knowsSocial) return ProfileLoad.Ready;
        if (inflight) return ProfileLoad.Loading;
        if (failed) return ProfileLoad.Failed;
        return asked ? ProfileLoad.Unavailable : ProfileLoad.Loading;
    }

    public static ProfileLoad Header(User u)
        => Header(u.Knows(UserFields.Social), u.IsUnavailable, u.IsFailed(UserFields.Social), u.IsAsked(UserFields.Social), u.IsInflight);

    /// <summary>A shelf that rides the header (Playlists, Artists): its own state once landed, else the header's.</summary>
    public static ProfileLoad Riding(ProfileLoad header, EdgeState state)
        => state is EdgeState.Complete or EdgeState.Partial ? ProfileLoad.Ready
         : header == ProfileLoad.Ready ? ProfileLoad.Loading : header;

    /// <summary>A door-asked list (Followers, Following): <see cref="EdgeTableBase.Readiness"/> + its failure.</summary>
    public static ProfileLoad List(EdgeState readiness, int failure) => readiness switch
    {
        EdgeState.Complete or EdgeState.Partial => ProfileLoad.Ready,
        EdgeState.Failed => failure == EdgeTableBase.NoRoute ? ProfileLoad.Unavailable : ProfileLoad.Failed,
        _ => ProfileLoad.Loading,
    };

    /// <summary>Your own "Top artists this month" (Home.Feeds.TopContentState + the relation's count).</summary>
    public static ProfileLoad TopArtists(HomeLoad state, int count)
        => count > 0 ? ProfileLoad.Ready
         : state switch { HomeLoad.Pending => ProfileLoad.Loading, HomeLoad.Failed => ProfileLoad.Failed,
                          HomeLoad.Ready => ProfileLoad.Ready, _ => ProfileLoad.Unavailable };
}
```

## 4. What the UI codes against (frozen)

**Handle.** `Entities.User(EntityUri)` and `User.Me` (both exist).

**Row reads:**
- `User.Name`, `Image` (string url, or null), `Color` (0xFFRRGGBB, 0 = none)
- `Followers`, `Following`, `PublicPlaylists` (meaningful when `Knows(UserFields.Social)`)
- `IsCurrentUser`, `IsFollowedByViewer`, `ShowFollows`, `AllowFollows`, `IsUnavailable`
- `Knows(UserFields)`, `IsAsked`, `IsInflight`, `IsFailed`, `IsStale`
- `Uri`, `NameId`, `ImageId`

**Shelves.** Call these with `ProfileShelf.{Playlists, Artists, Followers, Following}`:
- `ProfileTargets(shelf)` and `ProfileCards(shelf)`: parallel spans. `ProfileCardEdge.{Kind, Followers, ViewerFollows, OwnerFollows, Ref(target)}`.
  - Build `new Playlist(t)`, `new Artist(t)` or `new User(t)` by `Kind`.
  - Rows already hold Name/Title and the cover (Thin), so cards need no extra `Ensure`.
- `ProfileReadiness(shelf)`, `ProfileFailure(shelf)`, `ProfileCount(shelf)`, `ProfileTotal(shelf)`, `ProfileVersion(shelf)`.
- `TopArtistSlots` (own profile) plus `Home.Feeds.TopContentState`.
- Playlists can read `Partial` with `Total` = the server's count. No route pages it, so don't show "Show all" for public playlists (research §7.5).

**Demand.** The page's `_demand` runs `ProfileAsk.Apply(user, ProfileAsk.Plan(user, ProfileSurface.Page))` with `DepKey.From(slot, epoch)`.
- A list sub-page does the same with `ProfileSurface.Followers` or `ProfileSurface.Following`.
- If own-profile can resolve late (a vanity URI), add a second effect keyed (slot, epoch, IsCurrentUser) that calls `Home.Feeds.EnsureTopContent()`. It is idempotent with a 30-minute freshness window.

**Verdicts:**
- `ProfileLoadRule.Header(user)`
- `Riding(header, ProfileReadiness(Playlists/Artists))`
- `List(ProfileReadiness(Followers/Following), ProfileFailure(...))`
- `TopArtists(...)`

**Retry:** `ProfileAsk.RetryHeader(user)` and `ProfileAsk.RetryList(user, shelf)`.

**Subscriptions** (mark-only changes don't publish a table):
- `Entities.Current.Users.Changed`
- `Edges.ProfilePlaylists/ProfileArtists/ProfileFollowers/ProfileFollowing.Changed`
- `Fetch.Settled`
- For own profile: `Edges.UserTopArtists.Changed` and `Home.Feeds.TopContentState`

**Follow.** `Controls.FollowToggle { Uri = user.Uri.Text, Name = user.Name }` works unchanged once the page sets `Controls.Library ??= User.LibrarySeam`. Don't mount it on your own profile.

**Covers:**
- `Controls.ArtUrl(p.ImageId)` gives one cover (a mosaic gives its lead tile).
- `Controls.MosaicTiles(p.ImageId)` with `Length == 4` gives a 2×2 for `CardData.CoverOverride = Controls.Mosaic(...)`.
- Header colour: `user.Color` as the `payloadAccent`.

## 5. Tests

**New `Wavee.Tests/ProfileDecodeTests.cs` (D2).** Fixtures come from `Upv = Wavee.Protocol.UserProfileView`. URIs: `"spotify:playlist:" + i.ToString("D22")` and `"spotify:artist:" + i.ToString("D22")` (small i), users as text.
- `A_profile_view_lands_the_header_columns_and_flags`: Name, Image, `Color == 0xFF1E3264`, 118/1063/94, IsFollowedByViewer, ShowFollows, AllowFollows, ¬IsCurrentUser, Knows(I|S|F).
- `The_profile_view_lands_its_playlists_and_artists_as_two_runs_with_payloads`:
  - Order kept; `ProfileCardEdge(Playlist, ViewerFollows, 7)`.
  - Playlist Title; `spotify:image:` normalised to `https://i.scdn.co/image/…`; Owner == user; mosaic stored verbatim.
  - Playlists Partial with Total 94; artists Complete with names and OwnerFollows.
- `An_absent_recently_played_field_is_a_complete_empty_shelf` (and zero playlists: Complete, 0).
- `Interleaved_repeated_fields_still_land_contiguous_runs`: concatenate four single-card `UserProfile` encodings in the order 8,7,8,7.
- `A_404_profile_is_a_known_negative_not_a_miss`: Knows(Social|Follow), IsUnavailable, both shelves Complete-empty, `ProfileLoadRule.Header(u) == Unavailable`.
- `The_following_list_keeps_artists_then_users_across_two_tables`: Kinds, order, Thin names, user Color, ViewerFollows from f7 (artist) and f6 (user).
- `A_followers_list_admits_users_only`.
- `An_empty_list_answer_is_complete_and_empty`.
- `A_list_past_the_cap_lands_its_head_partial_with_the_true_total` (cap = 3).
- `A_nameless_list_user_lands_on_the_edge_without_sealing_identity`.
- `A_warm_profile_decode_allocates_nothing`: two warm-up passes of ProfileView and ProfileList, then the third allocates 0 bytes.

**`Wavee.Tests/DecodeTests.cs` (D2):**
- `ProfileBody`'s field 11 becomes an `Int32Value(0x509BF5)` wrapper.
- The kind-15 test gains `Assert.Equal(0xFF509BF5u, user.Color)` and is renamed `…_and_its_colour_rides_field_eleven`.
- Delete `A_profile_answer_in_json_reads_both_spellings…`.

**New `Wavee.Tests/CoverTokenTests.cs` (D2):**
- `ImageTokenUrl` rewrites the token.
- https, pickasso and mosaic values pass `IsImageToken == false` (kept verbatim).
- `FileIdOf` gives: the id, the lead tile, empty for `spotify:other:`, the bare id itself.
- `MosaicTiles` returns 4 ranges and skips empty segments.

**`Wavee.Tests/ControlsTests.cs`, class `ControlsArtUrlTests` (D2):**
- `A_spotify_image_token_resolves_to_its_cdn_url`
- `A_mosaic_token_resolves_to_its_lead_tile`
- `MosaicTiles_answers_four_urls_for_a_mosaic_and_none_for_a_cover` (also: the same span instance on repeat)

**`Wavee.Tests/SpotifyApiTests.cs` (D2):**
- Replace the JSON `The_profile_route_carries_the_captured_market…` with `The_profile_view_is_the_captured_wg_protobuf_get`:
  - Path `/user-profile-view/v3/profile/a%40b?playlist_limit=10&artist_limit=10&episode_limit=10&market=from_token`
  - `Host == SpclientWg`, `Headers == Common | AcceptProtobuf`, `Kind == RequestKind.Profile`
- Add `The_profile_lists_are_whole_wg_gets`.
- Route-walk facts (`SpotifyApiProviderRouteTests`):
  - `A_profile_page_ask_is_one_profile_view`: I|S|F gives a single Spclient ProfileView, sealed 0.
  - `A_follow_only_ask_is_the_one_op_query`.
  - The existing kind-15 / content-filter fact stays green.
- Edge gate theory rows: `(ProfileFollowers,0,true)`, `(ProfileFollowing,0,true)`, `(ProfileFollowers,10,false)`.

**`Wavee.Tests/SpotifySessionTests.cs` (D2):** delete the `RequestKind.Profile` fold assertion (line 1141).

**New `Wavee.Tests/UserProfileCommitTests.cs` (D1):**
- `Social_and_follow_flags_merge_under_their_own_masks`
- `An_empty_avatar_blanks_at_full_and_never_at_thin`
- `A_zero_colour_never_blanks_a_stated_one`
- `A_thinner_rendition_of_the_same_avatar_never_replaces_the_sharper` (64 after 300 at Full keeps 300)
- `Profile_shelves_commit_cross_kind_and_drop_what_a_shelf_does_not_admit`
- `IsCurrentUser_is_the_account_row_or_the_view_flag`
- `RenditionRank_orders_avatar_and_artist_renditions_by_pixels`
- `Invalidate_on_a_user_keeps_it_rendering_stale_and_re_asks` (Knows ∧ IsStale ∧ IsAsked)

**`Wavee.Tests/StoreTests.cs` (D1):**
- Rewrite the user test as `User_identity_round_trips_with_its_colour_and_social_never_reaches_the_disk`:
  - DDL has `color INT` and no `followers`.
  - Restore gives Name and Color; ¬Knows(Social|Follow); Followers 0.
  - A colourless Thin re-answer keeps the stored colour.

**`Wavee.Tests/FetchRoutesTests.cs` (D1):**
- Theory rows: ProfileFollowers and ProfileFollowing route via Spclient.
- `A_profile_page_ask_is_one_route_and_an_owner_identity_stays_kind_fifteen`: Follow-only gives `PathfinderOp.IsFollowingUsers`.
- `ForEdge(ProfileFollowers).Rest == SpclientRoute.ProfileFollowers` (and the same for Following).
- `EdgeDoorTests`' sweep covers the two new FetchEdges automatically.

**New `Wavee.Tests/ProfileAskTests.cs` (D3):**
- `A_first_visit_ensures_the_row_and_both_lists_in_parallel`
- `A_revisit_invalidates_the_known_row_and_lists`
- `Hidden_follows_skip_the_lists_on_a_revisit_but_never_on_your_own_profile`
- `An_unavailable_profile_asks_no_lists`
- `Only_your_own_profile_asks_top_artists`
- `A_list_page_asks_only_its_list_and_only_while_unknown`
- `The_header_reads_unavailable_first_ready_while_known_failed_then_sealed`
- `Riding_shelves_follow_the_header_until_they_land`
- `A_list_noroute_failure_reads_unavailable`
- `Top_artists_rule`

**New `Wavee.Tests/ProfileFollowTests.cs` (D3):**
- `IsFollowing_reads_following_not_following_and_not_found`
- `A_single_unmatched_entry_is_still_the_answer`
- `Garbage_is_unknown`
- `WriteSucceeded_accepts_the_captured_shape_and_refuses_errors_false_and_failure_strings`
- `Follow_bodies_are_the_captured_variables` (`"usernames":["a@b"]`, operationName, hash; `"uris":[…]`)
- `Apply_flips_and_counts_and_holds_a_stale_full_answer_off_until_confirm`
- `Revert_restores_flag_count_authorities_and_the_known_bit`
- `The_library_seam_reads_a_user_follow` (`User.LibrarySeam.IsSaved`)
- `Toggling_your_own_profile_is_a_no_op`

## 6. Who owns which files (parallel Sonnet agents)

**D1, entity/edges/persistence:**
- `Entities/User.cs`, `User.Profile.cs` (new), `Edges.Staging.cs`, `Fetch.Routes.cs`, `Fetch.Edges.cs`, `Detail.cs`, `Entities.Fake.cs`, `Entities.Fake.Profile.cs` (new)
- Tests: `UserProfileCommitTests.cs` (new), `StoreTests.cs`, `FetchRoutesTests.cs`

**D2, transport/decoders/covers:**
- `Spotify/Spotify.Api.cs`, `Spotify.Api.Profile.cs` (new), `Spotify.cs`, `Spotify.Decode.Profile.cs` (new), `Spotify.Decode.Traits.cs`, `Spotify/Protos/user_profile_view.proto` (new), `Entities/CoverToken.cs` (new), `Platform/Controls.cs` (ArtUrl hunk only), `Platform/Controls.Cover.cs` (new)
- Tests: `ProfileDecodeTests.cs`, `CoverTokenTests.cs` (both new), `DecodeTests.cs`, `SpotifyApiTests.cs`, `SpotifySessionTests.cs`, `ControlsTests.cs`

**D3, follow/library/rules:**
- `Spotify/Spotify.Api.Profile.Follow.cs` (new), `Spotify.Library.Profile.cs` (new), `Spotify.Library.cs` (one Transport field), `Entities/User.Follow.cs` (new), `Entities/User.UI.cs` (§8 arms), `Entities/User.Profile.Rules.cs` (new)
- Tests: `ProfileAskTests.cs`, `ProfileFollowTests.cs` (both new)

**Interfaces between the three:**
- D2 and D3 code against D1's frozen names:
  - `UserFields.Follow` and `UserFlags`
  - `StagedUser.{Color, PublicPlaylists, Flags}`
  - `Relation.Profile*` and `ProfileCardEdge.Admits` / `ProfileCardFlags`
  - `FetchEdge.Profile*`, `SpclientRoute.Profile*`, `PathfinderOp.IsFollowingUsers`
  - `UserTable.{Flags, FollowAuthority, ExtrasAuthority, Followers}`
  - the `User` accessors in §1.2
- D2's `AnswerQuery` calls D3's `Api.ProfileQueries.FollowStateAnswer(string, Staging)`.
- D3's rules call D1's accessors and `Entities.Invalidate(User, UserFields)`.
- UI agents must not edit any of these files. The one hand-off is the `Playlist.UI.cs` CoverArt hunk in §2.4.

**Existing tests that change:**
- `StoreTests` (user test)
- `DecodeTests` (JSON profile test deleted; ProfileBody field 11)
- `SpotifyApiTests` (JSON route test replaced; theory rows)
- `SpotifySessionTests:1141`
- `FetchRoutesTests` (rows)

## 7. Rejected

- **`FetchEdge.ProfilePlaylists`/`ProfileArtists`**: up to three GETs (finding 2).
- **Persisting Social**: the riding shelves are never asked after a disk restore (finding 3).
- **Invalidating right after a follow succeeds**: replica lag flips the button back. The SetCollaborative precedent applies.
- **Reading "unavailable" from marks**: the miss policy retries a 404 twice, then marks it Failed (finding 1).
- **Normalising a mosaic to its lead tile at decode time**: that loses the 2×2. Storing the token keeps both.
- **Keeping the JSON route and decoder**: legacy. The kind-15 fallback now uses the protobuf profile view.
- **Waiting for ShowFollows before asking the lists**: one extra round trip on every first visit. The official client fires them in parallel.

## 8. Open risks

| | Risk | What I did |
|---|---|---|
| R1 | The `spotify:mosaic:` tile format is unverified. I assumed colon-separated 40-hex file ids. | If wrong, tiles 404 to placeholders and nothing crashes. Pin it with one capture. |
| R2 | f23/f24 (allow/show follows) are guesses. | For normal profiles both were true in the capture. A swap only matters for hidden-follows profiles, and a hidden list's 404 lands empty. |
| R3 | Desktop vs Web identity for the follow ops. | Desktop matches the capture. If the gateway refuses with 400/403, flip `Web: true` in the three `ProfileQueries` rows. |
| R4 | The follow `result` value set is unknown. | The parser is deliberately lenient. |
| R5 | The UserShape DDL change moves every install to a new cache file once. | Cold first launch after the update, by design of `Store.FileName`. |
| R6 | List commit cost on the UI thread. | Capped at 5000 entries (Partial with the true total). Followers lists can expire signed Facebook CDN avatar URLs: they're Thin and get replaced by kind 15. |
| R7 | `User.Image` sits beside `using static Ui` in `User.Cover.cs`. | It should bind (finding 8). If the compiler disagrees, qualify the two call sites as `Ui.Image(...)`. |
| R8 | A follow click during the in-flight mount GET. | Handled by holding Local authority. A double-click while pending is ignored (the first click wins). |
| R9 | "Show all" for public playlists has no source (research §7.5). | `Total` is informational only. |

### Critical Files for Implementation
- C:\wavee\waveemusic\src\apps\Wavee\Entities\User.cs
- C:\wavee\waveemusic\src\apps\Wavee\Entities\Edges.Staging.cs
- C:\wavee\waveemusic\src\apps\Wavee\Entities\Fetch.Routes.cs
- C:\wavee\waveemusic\src\apps\Wavee\Spotify\Spotify.Api.cs
- C:\wavee\waveemusic\src\apps\Wavee\Spotify\Spotify.Decode.Traits.cs

## Appendix U — Profile page (U1, U2)

# Profile page (`spotify:user:…`) UI design: page host, hero, sections, states, loc, tests and ownership

This was a read-only design pass: nothing was built or edited. The page follows `Entities/Artist.Page.cs` and `Entities/Artist.UI.cs` throughout. Code from the data, route and list designers is assumed to have the names in §1.

---

## 0. Decisions

| Question | Decision | Why (from the code) |
|---|---|---|
| Page structure | **One long page.** The hero collapses into the 56-DIP sticky band (`Detail.Band` + `Detail.Pivot`), with the scroll-spy over the sections. Each section has "See all" to `RouteKind.ProfileList`. | The `user:` route's Arg already carries the display name (`Shell.CarriesDisplayName`), so a tab can't live in the route without changing the codec. The lists are unpaged and can hold 1,000+ entries. A virtual grid inside the page `ScrollView` measures 0 (Browse.Page.cs must-not-simplify 10), so long lists need their own scroller, which is the list route. The page's own sections are capped previews (≤10 playlists, ≤10 artists, ≤20 people), so the page is short and finite, which is exactly the shape the artist scroll-spy is built for. |
| Where the code lives | A new top-level `public static partial class Profile` (`Profile.Page.cs`, `Profile.UI.cs`, `Profile.Rules.cs`), **not** nested in `partial struct User`. | Inside `User` the contract's instance members (`Name`, `Image`, `Color`, `Following`, `Followers`) would shadow `Ui.Image`/`Color` names in UI code (the trap Artist.UI.cs works around in its "NAME NOTE"). |
| Hero geometry | A pure **`ProfileHeroLayout`**. It reuses `ArtistHeroLayout.TierFor` (same 880/600/360 thresholds, 24-DIP hysteresis) with its own heights. | The artist heights (440/384/448/468) are sized for a full-bleed photo. A round-avatar hero needs worst-case heights sized for the copy, and `.Collapse()` needs a fixed height per tier. |
| Colour | `Palette.ArtistBlendWash` + the `Palette.ShellTint` leaf + `Detail.AccentFor`. **No `ArtistHeroVeil`.** | The veil exists to keep type readable over a photo; this hero has no photo under the type. |
| Tone source | A pure `ProfileTone.Of(avatar, color, gradeable)`. When the avatar can be graded (`Palette.CanGrade`), paletteUrl = avatar and payload = 0. Otherwise paletteUrl = null and payload = `0xFF000000 \| color`. | **This deviates from the brief's "Color as payloadAccent first".** The profile `color` is one of six brand colours, unrelated to the photo (research §2 f16). Using it as payload while the avatar's grading is pending paints a known-wrong hue, then hard-cuts to the graded one (a pre-settlement swap in `VeilSettlement`). It is a one-line flip in the pure rule, which a test pins. |
| Ghost-name panorama | **Dropped.** | Door's ghost numeral is a private raw `TextEl { Size = 64, Weight = 300 }` inside `Controls.Door`, not a reusable control. The concept itself labels the ghost "**new**: one decorative display rung" at ~190 px. That is a 7th type divergence or a raw size, which Design.cs:1148-1170 forbids. Using `ArtistDisplay` as the ghost would be the same size as the name at Wide, so there'd be no panorama effect. |
| Rank numerals (own top artists) | `Design.Type.StatHero(rank, null)` (the sanctioned 28/36/350 "glanceable stat") in a 36-DIP column beside each circular card, in `TextSecondary` ink. | It's readable information, not texture, and uses an existing alias. No accent ink on decoration, per the accent budget. |
| Counts | The Track.Drawer `Stat` idiom: a `StatHero` numeral over `Caption 600 TextTertiary`. Each count links to the list route through `.Interactive(Interaction.Subtle)`. Followers/Following are hidden when `!(Own ‖ ShowFollows)`. | `Design.Type.StatHero` has exactly one precedent (Track.Drawer.cs:267), and this matches it. |
| People previews | A circular `Controls.Surface` shelf (`Circular: true`). Users without an image get a `PersonPicture` initials `CoverOverride` on their own colour. | Built only from existing controls; a faces-mosaic card would be a new composition. |
| Buttons | `Controls.FollowToggle` (keyed by uri, `SkeletonProxy`), `Album.CommandCircle(Icons.Share…)`, `Detail.MoreButton`, `HyperlinkButton` "See all", `Controls.FollowTextAction` in the band, and the stock `Vacancy` button. | No new capsules. |
| Loading | A page `SkelRegionEl` (Soft reveal) for the hero model. **Each section is its own `SkelRegionEl`** (FadeOnly, Group null), so a late list never holds the hero. Retry calls `Entities.Refresh` / `RefreshEdge`, never Ensure. | Same as the artist chart's inner region; Browse.Page.cs explains why Retry must not use Ensure. |
| Install | `Profile.InstallPages()` is called from `Artist.InstallPages()` (one line). | `BootOrderingTests` already boots `Artist.InstallPages()`, so `RouteKind.User` resolves there with no test change beyond the kind count, which the routing designer owns. |

---

## 1. Interfaces this design depends on (from the parallel designers)

The data contract, using its exact names:
- `Entities.User(EntityUri)`; `User.Me`.
- On `User`:
  - `string Name`, `StringId Image`, `uint Color` (0xRRGGBB)
  - `int Followers`, `int Following`, `int PublicPlaylists`
  - `bool IsCurrentUser`, `bool IsFollowedByViewer`, `bool ShowFollows`
  - `Knows(UserFields)`
- `UserFields.Identity | Social | Follow`.
  - If `Name`/`Image` aren't added, they mean `Entities.Strings.Resolve(NameId)` and `ImageId`.
- `Edges.ProfilePlaylists / ProfileArtists / ProfileFollowers / ProfileFollowing` : `EdgeTable<ProfileCardEdge>` (`EntityKind Kind, byte Flags, int Followers`). The parent is the user slot, and targets index the table named by `Kind`.
- `FetchEdge.ProfilePlaylists / ProfileArtists / ProfileFollowers / ProfileFollowing`.
- **`ProfileAsk`** (data designer). The page assumes `ProfileAsk.Row(bool known)` and `ProfileAsk.Edge(EdgeState state)`, each returning `ProfileAskVerdict { Ensure, Invalidate }`. They're used in exactly two places (`AskRow` and `AskEdge`); if the final shape differs, only those two lines change.
- **Route designer:**
  - `Shell.RouteKind.User` (key `"user:"`, **`ClaimsMaterial: true`** — the page publishes its own tint).
  - `Shell.For(EntityKind.User uri)` → `RouteKind.User`.
- **List designer:** `ProfileListRoute.For(User u, ProfileFacet f)` → `Shell.Route`; `ProfileFacet { Playlists, Following, Followers }`.
- **Library seam:** `Controls.Library.IsSaved` / `ToggleSaved` answer `spotify:user:` uris, so `FollowToggle` and `FollowTextAction` work unchanged.
- **`--fake`:** the seed must mark the me-row's Identity|Social|Follow and the four profile edges, or the own profile shimmers forever.

---

## 2. `Entities/Profile.Rules.cs` (U1): the pure decisions

```csharp
// ── Entities/Profile.Rules.cs ──────────────────────────────────────────────────────────────────────────────────────────
// The profile page's PURE decisions (engine-free; Wavee.Tests drives each one): the page's load verdict, the section
// plan and each section's body, the hero's tier metrics, the hero's stat cells, the image-token normaliser and the tone.
//
// Role: CORE (pure)   Owner: U1   Template: ArtistSections / ArtistHeroLayout (Artist.UI.cs §1-§3), BrowseLoadGate

using System.Globalization;
using FluentGpu.Dsl;

namespace Wavee;

// ══ 1. THE LOAD VERDICT ═══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>The page's four faces.</summary>
public enum ProfileLoadState : byte { Pending, Ready, Unavailable, Failed }

/// <summary>Read off the user row's marks, never a Loadable (Entities.Ensure doc). One ProfileView answer fills BOTH
/// Identity and Social, so the hero's model is "Identity ∧ Social known". A 404 seals the ask (Asked ∧ ¬Known ∧ ¬Failed,
/// the Concert.Page precedent); a transport failure un-asks and sets the Failed mark.</summary>
public static class ProfileLoad
{
    public static ProfileLoadState Of(bool valid, bool known, bool asked, bool inflight, bool failed)
        => !valid ? ProfileLoadState.Unavailable
         : known ? ProfileLoadState.Ready            // a stale (invalidated) row still renders — SWR
         : inflight ? ProfileLoadState.Pending
         : failed ? ProfileLoadState.Failed
         : asked ? ProfileLoadState.Unavailable      // sealed, nothing coming, no failure → private / closed
         : ProfileLoadState.Pending;                 // not asked yet: the mount effect is about to

    /// <summary>The ONE reveal: the model known and the width measured; latched once true (a refresh never re-shimmers).</summary>
    public static bool BodyReady(ProfileLoadState state, bool measured, bool alreadyRevealed)
        => alreadyRevealed || (state == ProfileLoadState.Ready && measured);
}

// ══ 2. THE SECTIONS ═══════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>Every section the page can render, in the ONE fixed order (official shelf order, research §1).</summary>
public enum ProfileSection : byte { TopArtists, Playlists, RecentArtists, Following, Followers }

/// <summary>What a present section's region shows.</summary>
public enum ProfileSectionBody : byte { Seed, Cards, Empty, Error }

/// <summary>What the page knows, read once per compose. Edge states are <c>Readiness</c> (so Failed is visible).</summary>
public readonly record struct ProfileFacts(
    bool Own, bool ShowFollows,
    EdgeState TopArtists, int TopArtistCount,
    EdgeState Playlists, int PlaylistCount,
    EdgeState RecentArtists, int RecentArtistCount,
    EdgeState Following, int FollowingCount,
    EdgeState Followers, int FollowerCount);

/// <summary>One shelf card: the row it stands for, that row's version (the shelf's value gate), the edge's follower
/// count (playlist subtitle), its 1-based rank, and its key (kind-tagged: Following mixes artist and user slots).</summary>
public readonly record struct ProfileCard(EntityKind Kind, int Slot, uint Version, int Followers, int Rank, string Key);

public static class ProfileSections
{
    public const int Count = 5;
    public const int TopCap = 10, PlaylistCap = 10, ArtistCap = 10, PeopleCap = 20;

    static readonly string[] s_keys = ["top-artists", "playlists", "recent-artists", "following", "followers"];
    public static string Key(ProfileSection s) => s_keys[(int)s];

    public static int CapOf(ProfileSection s) => s switch
    {
        ProfileSection.TopArtists => TopCap,
        ProfileSection.Playlists => PlaylistCap,
        ProfileSection.RecentArtists => ArtistCap,
        _ => PeopleCap,
    };

    /// <summary>The follow lists show on your own profile always, on another's only when its owner shows them.</summary>
    public static bool ShowsPeople(bool own, bool showFollows) => own || showFollows;

    /// <summary>Rows win over every state (a failed refresh keeps rendering what landed); then Failed → Error,
    /// Complete → Empty, anything still coming → Seed.</summary>
    public static ProfileSectionBody BodyOf(EdgeState state, int count)
        => count > 0 ? ProfileSectionBody.Cards
         : state == EdgeState.Failed ? ProfileSectionBody.Error
         : state == EdgeState.Complete ? ProfileSectionBody.Empty
         : ProfileSectionBody.Seed;

    /// <summary>The page's sections into <paramref name="into"/> (≥ <see cref="Count"/>). Public playlists is
    /// unconditional (empty is a designed state); the optional shelves drop on an empty or failed answer; a people
    /// preview drops only on an EMPTY answer (a failed one carries its own Retry).</summary>
    public static int Plan(in ProfileFacts f, Span<ProfileSection> into)
    {
        int n = 0;
        if (f.Own && Optional(f.TopArtists, f.TopArtistCount)) into[n++] = ProfileSection.TopArtists;
        into[n++] = ProfileSection.Playlists;
        if (Optional(f.RecentArtists, f.RecentArtistCount)) into[n++] = ProfileSection.RecentArtists;
        if (ShowsPeople(f.Own, f.ShowFollows))
        {
            if (People(f.Following, f.FollowingCount)) into[n++] = ProfileSection.Following;
            if (People(f.Followers, f.FollowerCount)) into[n++] = ProfileSection.Followers;
        }
        return n;
    }

    static bool Optional(EdgeState s, int count) => count > 0 || s is EdgeState.Unknown or EdgeState.Partial;
    static bool People(EdgeState s, int count) => count > 0 || s != EdgeState.Complete;

    /// <summary>Own top artists ride Home.Feeds (a HomeLoad, not an edge mark): rows → Complete; in flight → Unknown;
    /// failed → Failed; Idle (offline / --fake / concluded) or Ready-empty → Complete (the section drops).</summary>
    public static EdgeState TopState(HomeLoad load, int count)
        => count > 0 ? EdgeState.Complete
         : load == HomeLoad.Pending ? EdgeState.Unknown
         : load == HomeLoad.Failed ? EdgeState.Failed
         : EdgeState.Complete;

    /// <summary>"See all" only where the list route has a facet AND there is more than the shelf shows.</summary>
    public static bool SeeAll(ProfileSection s, int total, int shown)
        => s is ProfileSection.Playlists or ProfileSection.Following or ProfileSection.Followers && total > shown;
}

// ══ 3. THE HERO LAYOUT ════════════════════════════════════════════════════════════════════════════════════════════════

public readonly record struct ProfileHeroMetrics(
    ArtistHeroTier Tier, float Height, float Gutter, float Avatar, float NameLine, int NameLines, int StatRows,
    float TopPad, float BottomPad, float CopyMaxWidth)
{
    public bool Stacked => Tier is ArtistHeroTier.Compact or ArtistHeroTier.Narrow;
}

/// <summary>The round-avatar hero. Tiers are the ARTIST hero's (same thresholds + hysteresis — the two pages share the
/// band and the collapse arithmetic); heights are this hero's own WORST case (name at its line budget, stats at their
/// row budget), content bottom-aligned, so a short name leaves air above instead of resizing the sticky box.</summary>
public static class ProfileHeroLayout
{
    public const float WideAvatar = 184f, MediumAvatar = 144f, CompactAvatar = 112f, NarrowAvatar = 96f;
    /// <summary>The line boxes of ArtistDisplay (96), ArtistTitle (60), ArtistCompactTitle (40).</summary>
    public const float WideNameLine = 96f, MediumNameLine = 60f, CompactNameLine = 40f;
    /// <summary>StatHero's 36 + its 12/16 caption.</summary>
    public const float StatHeight = 52f, StatGap = 24f, StatRowGap = 8f;
    public const float NameGap = 12f, StackedNameGap = 8f, ActionsGap = 20f, StackedActionsGap = 16f;
    public const float AvatarGap = 36f, StackedAvatarGap = 16f;
    /// <summary>Controls.ButtonHeight — FollowToggle / IconAction / More.</summary>
    public const float ActionRow = 32f;
    /// <summary>The ab6775700000ee85 avatar rendition is 300 px; decoding larger is wasted.</summary>
    public const int AvatarDecodePx = 300;

    public static ProfileHeroMetrics For(float width, ArtistHeroTier previous)
    {
        var tier = ArtistHeroLayout.TierFor(width, previous);
        return tier switch
        {
            ArtistHeroTier.Wide => Make(tier, WideAvatar, WideNameLine, 2, 1, 40f, 32f, Spacing.PageWide, ArtistHeroLayout.WideCopyMaxWidth),
            ArtistHeroTier.Medium => Make(tier, MediumAvatar, MediumNameLine, 2, 1, 36f, 28f, Spacing.XXXL, ArtistHeroLayout.MediumCopyMaxWidth),
            ArtistHeroTier.Compact => Make(tier, CompactAvatar, CompactNameLine, 1, 1, 24f, 24f, Spacing.L, ArtistHeroLayout.CompactCopyMaxWidth),
            _ => Make(tier, NarrowAvatar, CompactNameLine, 1, 2, 20f, 20f, Spacing.PageNarrow, ArtistHeroLayout.NarrowCopyMaxWidth),
        };
    }

    static ProfileHeroMetrics Make(ArtistHeroTier tier, float avatar, float nameLine, int nameLines, int statRows,
                                   float top, float bottom, float gutter, float copyMax)
    {
        var m = new ProfileHeroMetrics(tier, 0f, gutter, avatar, nameLine, nameLines, statRows, top, bottom, copyMax);
        float copy = CopyHeight(in m);
        float body = m.Stacked ? avatar + StackedAvatarGap + copy : MathF.Max(avatar, copy);
        return m with { Height = top + body + bottom };
    }

    public static float NameToStats(in ProfileHeroMetrics m) => m.Stacked ? StackedNameGap : NameGap;
    public static float StatsToActions(in ProfileHeroMetrics m) => m.Stacked ? StackedActionsGap : ActionsGap;

    /// <summary>The identity column's worst case: name lines · gap · stat rows · gap · action row.</summary>
    public static float CopyHeight(in ProfileHeroMetrics m)
        => m.NameLine * m.NameLines + NameToStats(in m)
         + m.StatRows * StatHeight + (m.StatRows - 1) * StatRowGap
         + StatsToActions(in m) + ActionRow;

    public static float CollapseDistance(in ProfileHeroMetrics m) => ArtistHeroLayout.CollapseDistance(m.Height);
    public static float WashHeight(in ProfileHeroMetrics m) => m.Height + ArtistHeroLayout.ContentBlendTail;
    public static float WashBoundary(in ProfileHeroMetrics m) => m.Height / (m.Height + ArtistHeroLayout.ContentBlendTail);
}

// ══ 4. THE HERO'S STATS ═══════════════════════════════════════════════════════════════════════════════════════════════

public enum ProfileStat : byte { Followers, Following, Playlists }
public readonly record struct ProfileStatCell(ProfileStat Kind, int Value, bool Links);

public static class ProfileStats
{
    public const int Max = 3;

    /// <summary>Followers · Following (only when the lists show) · Public playlists (always). A zero never links.</summary>
    public static int Plan(bool own, bool showFollows, int followers, int following, int playlists, Span<ProfileStatCell> into)
    {
        int n = 0;
        if (ProfileSections.ShowsPeople(own, showFollows))
        {
            into[n++] = new(ProfileStat.Followers, Math.Max(0, followers), followers > 0);
            into[n++] = new(ProfileStat.Following, Math.Max(0, following), following > 0);
        }
        into[n++] = new(ProfileStat.Playlists, Math.Max(0, playlists), playlists > 0);
        return n;
    }

    /// <summary>Cells per row for a row budget (Narrow splits 3 → 2 + 1).</summary>
    public static int PerRow(int cells, int rows) => rows <= 1 || cells <= 1 ? Math.Max(1, cells) : (cells + rows - 1) / rows;

    public static string Numeral(int value, CultureInfo culture) => Math.Max(0, value).ToString("N0", culture);
}

// ══ 5. THE IMAGE TOKENS AND THE TONE ══════════════════════════════════════════════════════════════════════════════════

/// <summary>A cover/avatar resolved for painting: a url, and — for a cover-less playlist — exactly four mosaic tiles.</summary>
public readonly record struct ProfileArt(string? Url, string[]? Tiles);

/// <summary>Profile image forms (research §2): https, <c>spotify:image:&lt;id&gt;</c>, a bare 40-hex id, and
/// <c>spotify:mosaic:&lt;id&gt;:…</c> (the client composes a 2×2 of 300-px album tiles). <c>Controls.ArtUrl</c>
/// prefixes the CDN onto anything non-http, which breaks the two token forms — so profile surfaces resolve here.
/// Idempotent over an already-normalised https url (the data designer may normalise at decode).</summary>
public static class ProfileCover
{
    public const string Cdn = "https://i.scdn.co/image/";
    public const string ImagePrefix = "spotify:image:", MosaicPrefix = "spotify:mosaic:";
    /// <summary>The 300-px album tile size code (research §5).</summary>
    public const string MosaicTileCode = "ab67616d00001e02";
    const int ImageIdLength = 40, SizeCodeLength = 16;

    public static ProfileArt Of(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return default;
        raw = raw.Trim();
        if (raw.StartsWith("http", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return new(raw, null);
        if (raw.StartsWith(MosaicPrefix, StringComparison.Ordinal)) return Mosaic(raw.AsSpan(MosaicPrefix.Length));
        ReadOnlySpan<char> id = raw.StartsWith(ImagePrefix, StringComparison.Ordinal) ? raw.AsSpan(ImagePrefix.Length) : raw.AsSpan();
        return IsImageId(id) ? new(Cdn + id.ToString(), null) : default;
    }

    static ProfileArt Mosaic(ReadOnlySpan<char> ids)
    {
        var tiles = new string[4];
        int n = 0;
        while (!ids.IsEmpty && n < 4)
        {
            int colon = ids.IndexOf(':');
            var part = colon < 0 ? ids : ids[..colon];
            ids = colon < 0 ? default : ids[(colon + 1)..];
            if (IsImageId(part)) tiles[n++] = Cdn + MosaicTileCode + part[SizeCodeLength..].ToString();
        }
        // Fewer than four is the caller's single-cover branch (Controls.Mosaic's own rule): a 3-tile mosaic is broken.
        return n == 0 ? default : n < 4 ? new(tiles[0], null) : new(tiles[0], tiles);
    }

    public static bool IsImageId(ReadOnlySpan<char> id)
    {
        if (id.Length != ImageIdLength) return false;
        foreach (char c in id)
            if ((uint)(c - '0') > 9u && (uint)(c - 'a') > 5u && (uint)(c - 'A') > 5u) return false;
        return true;
    }
}

public readonly record struct ProfileToneSource(string? PaletteUrl, uint PayloadArgb);

/// <summary>Which colour leads the page. A GRADEABLE avatar is the person's colour (the official header uses the
/// avatar's colorRaw); the profile's brand colour is unrelated to that photo, so it is the payload ONLY when there is no
/// gradeable avatar (no image, or a non-scdn CDN) — then it paints on the first frame and is definite.</summary>
public static class ProfileTone
{
    /// <summary>0xRRGGBB → the app's payload convention (opaque ARGB); 0 stays "none".</summary>
    public static uint Argb(uint rgb) => (rgb & 0x00FFFFFFu) == 0 ? 0u : 0xFF000000u | (rgb & 0x00FFFFFFu);

    public static ProfileToneSource Of(string? avatarUrl, uint rgb, bool gradeable)
        => avatarUrl is { Length: > 0 } && gradeable ? new(avatarUrl, 0u) : new(null, Argb(rgb));
}
```

Height check (pinned by tests):

| Tier | Copy column | Height |
|---|---|---|
| Wide | 96·2+12+52+20+32 = 308 | 40+308+32 = **380** |
| Medium | 60·2+12+52+20+32 = 236 | 36+236+28 = **300** |
| Compact | 40+8+52+16+32 = 148 | 24+(112+16+148)+24 = **324** |
| Narrow | 40+8+(52·2+8)+16+32 = 208 | 20+(96+16+208)+20 = **360** |

---

## 3. `Entities/Profile.Page.cs` (U1): install, factory, page host

```csharp
// ── Entities/Profile.Page.cs ───────────────────────────────────────────────────────────────────────────────────────────
// The PROFILE page (spotify:user:…): install + mount point and the page component — demand on mount (through the data
// designer's ProfileAsk verdict), the render-cost stamp (marks folded too: a 404 seal / failure moves no Version), the
// four load faces (ProfileLoad), the derived shimmer, the person's tone (accent, blend wash, shell tint), the context
// band (Detail.Band + Detail.Pivot + the scroll spy) and one SkelRegion per section. The hero, the section bodies and
// the cards are Profile.UI.cs (U2).
//
// Role: UI   Owner: U1   Template: Entities/Artist.Page.cs §1-§2.5 (read it first; the band/spy/sentinel/clip are copied)

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Profile
{
    /// <summary>The ONE group set a profile asks — never <c>UserFields.All</c> (the ContentFilters route ignores the uri).</summary>
    internal const UserFields PageFields = UserFields.Identity | UserFields.Social | UserFields.Follow;

    /// <summary>The profile family's install. Called once from <c>Artist.InstallPages()</c> (eager group; BootOrderingTests
    /// already boots it). The list designer's <c>Shell.SetPage(Shell.RouteKind.ProfileList, …)</c> line lands HERE too.</summary>
    public static void InstallPages()
    {
        Shell.SetPage(Shell.RouteKind.User, Page);
    }

    /// <summary>Keyed by the route: user → user is a new slot and a fresh page.</summary>
    public static Element Page(in Shell.Route route)
    {
        string routeKey = Shell.NameOf(route);
        return Embed.Comp(new PageProps(route.Subject, routeKey), static () => new PageHost()) with { Key = "user:" + routeKey };
    }

    sealed record PageProps(EntityUri Subject, string RouteKey);

    sealed partial class PageHost : Component, IPropsHost
    {
        static readonly string[] s_secKeys = Keys("sec:"), s_anchorKeys = Keys("anchor:");
        static readonly Func<Element> s_emptyShape = static () => new BoxEl();

        static string[] Keys(string prefix)
        {
            var k = new string[ProfileSections.Count];
            for (int i = 0; i < k.Length; i++) k[i] = prefix + ProfileSections.Key((ProfileSection)i);
            return k;
        }

        // ── identity ──
        PageProps? _latest;
        readonly Signal<PageProps?> _props = new(null);
        bool _accentSeeded;
        Scope? _scope;
        EntityUri _subject;
        User _user;
        bool _own;
        string _name = "";
        readonly object _tintOwner = new();

        // ── geometry (latched fields — Artist.Page's discipline) ──
        readonly Signal<float> _heroWidth = new(ArtistHeroLayout.WideWidth);
        readonly Signal<int> _layoutEpoch = new(0);
        ArtistHeroTier _tier = ArtistHeroTier.Wide;
        ProfileHeroMetrics _metrics = ProfileHeroLayout.For(ArtistHeroLayout.WideWidth, ArtistHeroTier.Wide);
        float _width = ArtistHeroLayout.WideWidth;
        bool _measured;

        // ── scroll, band, spy ──
        readonly Signal<float> _scrollY = new(0f), _viewportH = new(0f);
        readonly Signal<bool> _atEnd = new(false);
        readonly Signal<bool> _compact = new(false) { DebugName = "profile.compact" };
        readonly ScrollHandle _scroll = new();
        long _scrollKey = long.MinValue;
        readonly Signal<int> _active = new(Detail.BandLayout.NoSection);
        readonly Signal<int> _pivotEpoch = new(0);
        NodeHandle _viewport;
        readonly NodeHandle[] _anchors = new NodeHandle[ProfileSections.Count];
        readonly Action<NodeHandle>[] _anchorRealized = new Action<NodeHandle>[ProfileSections.Count];
        readonly Action[] _sectionClicks = new Action[ProfileSections.Count];
        readonly ProfileSection[] _plan = new ProfileSection[ProfileSections.Count];
        int _pivotCount, _pivotHash;

        // ── colour ──
        readonly Signal<ColorF> _accent = new(AccentHold.Last ?? Tok.AccentDefault);
        readonly Signal<Design.PageAccent> _pageAccent = new(new Design.PageAccent(
            AccentHold.Last ?? Tok.AccentTextPrimary, AccentHold.Last ?? Tok.AccentDefault, ""));
        readonly Signal<ThemeKind> _theme = new(ThemeKind.Dark);
        readonly Func<ColorF> _accentFn;

        // ── readiness ──
        ProfileLoadState _load;
        bool _bodyReady, _revealed;
        Element? _body;
        long _listsAskedKey = long.MinValue;

        // ── sections (latched per compose; the region delegates read these) ──
        readonly ProfileSectionBody[] _secBody = new ProfileSectionBody[ProfileSections.Count];
        readonly Element?[] _secEl = new Element?[ProfileSections.Count];
        readonly Func<bool>[] _secPending, _secFailed;
        readonly Func<Element>[] _secContent, _secShimmer, _secFailedPanel;
        readonly Action[] _secRetry, _seeAll;

        // ── cached delegates (a node handler never captures a render's closure) ──
        readonly Func<bool> _pendingFn, _failedFn;
        readonly Func<Element> _contentFn, _shimmerFn, _failedPanelFn;
        readonly Action _demand, _demandLists, _reask, _publishAccent, _publishTheme, _resolveSpy, _bumpPivot, _retry;
        readonly Action _scrollToTop, _watchScroll;
        readonly Action<NodeHandle> _captureViewport;
        readonly Action<RectF> _measure;
        readonly Func<long> _stampFn;
        readonly HeroActs _acts;

        public PageHost()
        {
            int n = ProfileSections.Count;
            _secPending = new Func<bool>[n]; _secFailed = new Func<bool>[n];
            _secContent = new Func<Element>[n]; _secShimmer = new Func<Element>[n]; _secFailedPanel = new Func<Element>[n];
            _secRetry = new Action[n]; _seeAll = new Action[n];
            for (int i = 0; i < n; i++)
            {
                int k = i;
                var s = (ProfileSection)i;
                _anchorRealized[k] = h => _anchors[k] = h;
                _sectionClicks[k] = () => GoToSection(k);
                _secPending[k] = () => _secBody[k] == ProfileSectionBody.Seed;
                _secFailed[k] = () => _secBody[k] == ProfileSectionBody.Error;
                _secContent[k] = () => _secEl[k] ?? new BoxEl();
                _secShimmer[k] = () => SeedShelf(s, _accent.Peek(), MagazineInnerWidth());
                _secRetry[k] = () => RetrySection(s);
                _secFailedPanel[k] = () => SectionError(s, _accent.Peek(), _secRetry[k]);
                _seeAll[k] = () => OpenList(FacetOf(s));
            }
            _accentFn = () => _accent.Value;
            _pendingFn = () => !_bodyReady && _load is not (ProfileLoadState.Failed or ProfileLoadState.Unavailable);
            _failedFn = () => !_bodyReady && _load == ProfileLoadState.Failed;
            _contentFn = () => _body ?? new BoxEl();
            _shimmerFn = PageShimmer;
            _retry = Retry;
            // Retry = Entities.Refresh (un-asks first); never Demand — a sealed Asked row is a no-op for Ensure.
            _failedPanelFn = () => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: _user.IsValid ? _retry : null);
            _demand = Demand;
            _demandLists = DemandLists;
            _reask = Reask;
            _publishAccent = PublishAccent;
            _publishTheme = () => _theme.Value = Tok.Theme;
            _resolveSpy = ResolveSpy;
            _bumpPivot = () => _pivotEpoch.Value = _pivotEpoch.Peek() + 1;
            _scrollToTop = () => _scroll.ScrollTo(0.0, Design.Reduced ? ScrollMove.Immediate : ScrollMove.Glide);
            _captureViewport = h => _viewport = h;
            _measure = r =>
            {
                if (r.W <= 0f) return;
                if (!_measured) { _measured = true; _heroWidth.Value = r.W; _layoutEpoch.Value++; return; }
                if (MathF.Abs(r.W - _heroWidth.Peek()) > 0.5f) _heroWidth.Value = r.W;
            };
            _watchScroll = () =>
            {
                float y = (float)_scroll.Offset.Value, vh = (float)_scroll.ViewportSignal.Value, ch = (float)_scroll.ExtentSignal.Value;
                bool atEnd = Detail.BandLayout.IsAtScrollEnd(y, vh, ch);
                long key = HashCode.Combine((int)(y / 24f), (int)MathF.Round(vh / Spacing.XS), atEnd);
                if (key == _scrollKey) return;
                _scrollKey = key;
                _scrollY.Value = y;
                _viewportH.SetIfChanged(vh);
                _atEnd.SetIfChanged(atEnd);
            };
            _stampFn = Stamp;
            _acts = new HeroActs(
                Share: () => Episode.CopyLink(_user.IsValid ? Actions.WebLinkOf(_user.Uri) : ""),
                Menu: () => MenuFor(_user, _name),
                OpenStat: k => OpenList(FacetOf(k)));
        }

        public void ApplyProps(object props)
        {
            _latest = (PageProps)props;
            if (!_accentSeeded)
            {
                _accentSeeded = true;
                _theme.Value = Tok.Theme;
                if (_latest.Subject.IsValid) PublishAccent(Entities.User(_latest.Subject), _latest.RouteKey);
            }
            _props.Value = _latest;
        }

        public override Element Render()
        {
            var p = _props.Value ?? _latest!;
            uint scopeEpoch = Entities.ScopeEpoch.Value;              // FIRST: a scope switch re-points every table
            var scope = Entities.Current;

            if (!ReferenceEquals(_scope, scope) || !_subject.Equals(p.Subject))
            {
                _scope = scope;
                _subject = p.Subject;
                _user = p.Subject.IsValid ? Entities.User(p.Subject) : default;   // allocates the row: bind now, Ensure fills
                Array.Clear(_anchors);                                            // the body is epoch-keyed: re-register
                Array.Clear(_secEl);
                _revealed = false;
                _listsAskedKey = long.MinValue;
            }

            var shellSlot = UseContext(ShellMaterial.Slot);
            var u = _user;
            _ = UseComputed(_stampFn).Value;                       // THE render-cost fold (Stamp)
            UseEffect(_demand, DepKey.From(u.Slot, (int)scopeEpoch));
            UseEffect(_demandLists);                              // re-runs as Social (ShowFollows) lands
            UseEffect(_publishAccent);
            UseEffect(_publishTheme, DepKey.From((int)Tok.Theme));
            UseEffect(_resolveSpy);
            UseActivation(onActivated: _reask);                   // keep-alive return re-reads (the official client does)

            bool washes = Prefs.Appearance.ColorWashes();

            _ = _layoutEpoch.Value;
            _width = MathF.Max(1f, _heroWidth.Value);
            _metrics = ProfileHeroLayout.For(_width, _tier);
            _tier = _metrics.Tier;

            var users = scope.Users;
            int slot = u.Slot;
            bool valid = u.IsValid;
            _own = valid && (slot == scope.MeSlot || u.IsCurrentUser);
            _name = valid ? u.Name : "";
            _load = ProfileLoad.Of(valid,
                known: valid && u.Knows(UserFields.Identity | UserFields.Social),
                asked: valid && (users.Asked[slot] & (uint)UserFields.Social) != 0,
                inflight: valid && users.Inflight[slot] != 0,
                failed: valid && users.IsFailed(slot, (uint)UserFields.Social));
            _bodyReady = ProfileLoad.BodyReady(_load, _measured, _revealed);
            if (_bodyReady) _revealed = true;

            string routeKey = p.RouteKey;
            string? avatar = valid ? ProfileCover.Of(Entities.Strings.Resolve(u.Image)).Url : null;
            var tone = ProfileTone.Of(avatar, valid ? u.Color : 0u, avatar is { Length: > 0 } a && Palette.CanGrade(a));
            // A 0×0 leaf owns the watch + the publish. Unavailable/Failed opt out (definite → neutral chrome).
            Element tint = Palette.ShellTint(tone.PaletteUrl, ready: Detail.CoverLatch.IsUsable(tone.PaletteUrl), disabled: !washes,
                apply: _load is not (ProfileLoadState.Unavailable or ProfileLoadState.Failed),
                owner: _tintOwner, slot: shellSlot, key: "profile-tint:" + routeKey, payloadAccent: tone.PayloadArgb);

            _body = _bodyReady ? Compose(u, avatar, tone, washes)
                  : _load == ProfileLoadState.Unavailable ? Unavailable() : null;
            UseEffect(_bumpPivot, DepKey.From(_pivotHash, (int)scopeEpoch));
            UseSignalEffect(_watchScroll);

            // ONE page region; the shimmer is DERIVED from PageShimmer (never mounted). Soft: the complete tree rises as one.
            Element region = new SkelRegionEl(
                Pending: _pendingFn, Failed: _failedFn, Content: _contentFn, ShimmerSource: _shimmerFn,
                OnFailed: _failedPanelFn, Reveal: SkelReveal.Soft, Style: SkeletonStyle.Default,
                Group: null, SmoothResize: false);

            Element scroll = ScrollView(new BoxEl
            {
                Direction = 1,
                // Keyed CHILD (rule 14): a scope switch remounts the sections, whose anchors re-register after the reset.
                Children = [new BoxEl { Key = "profile-body:" + scopeEpoch.ToString(CultureInfo.InvariantCulture), Direction = 1, Children = [region] }],
            }) with
            {
                Key = "profile-scroll:" + routeKey, Grow = 1f, ScrollKey = UseContext(Shell.PageScrollScope) + routeKey,
                OnRealized = _captureViewport, EdgeCues = ScrollEdgeCues.None, Handle = _scroll,
            };

            return Ctx.Provide(Design.AccentCtx.Slot, (IReadSignal<Design.PageAccent>?)_pageAccent,
                Ctx.Provide(LazyScroll.Slot, (IReadSignal<float>?)_scrollY, new BoxEl
                {
                    Key = "profile-page:" + routeKey, Grow = 1f, Direction = 1, OnBoundsChanged = _measure,
                    Children = [tint, scroll],
                }));
        }

        // ── the loaded page ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>[wash clipped at the band] under [hero · sentinel · magazine clipped at the band] — Artist.Page's
        /// Compose, verbatim in shape. Plan, anchors and pivot are built in ONE pass so they cannot disagree.</summary>
        Element Compose(User u, string? avatar, ProfileToneSource tone, bool washes)
        {
            var scope = Entities.Current;
            var e = scope.Edges;
            int slot = u.Slot, me = scope.MeSlot;
            var m = _metrics;
            float width = _width;
            ColorF accent = _accent.Value;
            string uri = u.Uri.Text;
            bool own = _own;

            int topCount = own ? e.UserTopArtists.Count(me) : 0;
            var facts = new ProfileFacts(own, u.ShowFollows,
                own ? ProfileSections.TopState(Home.Feeds.TopContentState.Peek(), topCount) : EdgeState.Complete, topCount,
                e.ProfilePlaylists.Readiness(slot), e.ProfilePlaylists.Count(slot),
                e.ProfileArtists.Readiness(slot), e.ProfileArtists.Count(slot),
                e.ProfileFollowing.Readiness(slot), e.ProfileFollowing.Count(slot),
                e.ProfileFollowers.Readiness(slot), e.ProfileFollowers.Count(slot));
            int n = ProfileSections.Plan(in facts, _plan);

            var sections = new Element[n];
            var pivotItems = new (string Label, Action OnClick)[n];
            int hash = 17;
            for (int i = 0; i < n; i++)
            {
                var s = _plan[i];
                int k = (int)s;
                Latch(s, u, in facts, accent);
                // Each section is ITS OWN region: a late list shimmers in place, never holding the hero (Group null).
                Element region = new SkelRegionEl(
                    Pending: _secPending[k], Failed: _secFailed[k], Content: _secContent[k], ShimmerSource: _secShimmer[k],
                    OnFailed: _secFailedPanel[k], Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default,
                    Group: null, SmoothResize: false);
                sections[i] = new BoxEl
                {
                    Key = s_anchorKeys[k], Direction = 1, MinWidth = 0f, OnRealized = _anchorRealized[k],
                    Children = [new BoxEl { Key = s_secKeys[k], Direction = 1, MinWidth = 0f, Children = [region] }],
                };
                pivotItems[i] = (PivotLabel(s), _sectionClicks[k]);
                hash = unchecked(hash * 31 + k + 1);
            }
            _pivotCount = n;
            _pivotHash = hash;

            float collapse = ProfileHeroLayout.CollapseDistance(in m);
            bool compact = _compact.Value;
            Element band = BandBar(uri, own, width, m.Gutter, collapse, compact, pivotItems);
            Element hero = HeroBanner(HeroText.For(u, own, avatar), uri, width, in m, compact, _acts, band);
            Element sentinel = new BoxEl { Height = 0f, HitTestVisible = false }
                .Sticky(ArtistHeroLayout.CompactIdentityHeight, engaged: _compact);
            Element wash = Palette.ArtistBlendWash(tone.PaletteUrl, ProfileHeroLayout.WashHeight(in m),
                ProfileHeroLayout.WashBoundary(in m), disabled: !washes, key: "profile-wash:" + uri,
                payloadAccent: tone.PayloadArgb);
            Element magazine = new BoxEl
            {
                Key = "profile-under-band", Direction = 1,
                EdgeFade = new EdgeFadeSpec(EdgeMask.Top, Detail.BandLayout.ClipFadeBand) { WhileStuck = true },
                Children =
                [
                    new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault, HitTestVisible = false },
                    new BoxEl { Direction = 0, Justify = FlexJustify.Center, Children = [Magazine(sections, m.Gutter)] },
                ],
            }.StickyClip(Detail.BandLayout.ClipInset);

            return new BoxEl
            {
                ZStack = true,
                Children =
                [
                    new BoxEl { Key = "profile-wash-clip", Direction = 1, HitTestVisible = false, Children = [wash] }
                        .StickyClip(Detail.BandLayout.ClipInset),
                    new BoxEl { Direction = 1, Children = [hero, sentinel, magazine] },
                ],
            };
        }

        static Element Magazine(Element[] sections, float gutter) => new BoxEl
        {
            Direction = 1, Gap = Design.Size.SectionGap,
            Grow = 1f, Shrink = 1f, MinWidth = 0f, Basis = 0f, MaxWidth = Design.Size.PageMaxW,
            Padding = new Edges4(gutter, Spacing.M, gutter, Design.Dock.Reserve + 40f),
            Children = sections,
        };

        float MagazineInnerWidth() => MathF.Max(1f, MathF.Min(_width, Design.Size.PageMaxW) - 2f * _metrics.Gutter);

        /// <summary>One section's body verdict + element, latched for its region's delegates.</summary>
        void Latch(ProfileSection s, User u, in ProfileFacts f, ColorF accent)
        {
            int k = (int)s;
            var (state, count) = s switch
            {
                ProfileSection.TopArtists => (f.TopArtists, f.TopArtistCount),
                ProfileSection.Playlists => (f.Playlists, f.PlaylistCount),
                ProfileSection.RecentArtists => (f.RecentArtists, f.RecentArtistCount),
                ProfileSection.Following => (f.Following, f.FollowingCount),
                _ => (f.Followers, f.FollowerCount),
            };
            var body = ProfileSections.BodyOf(state, count);
            _secBody[k] = body;
            _secEl[k] = body switch
            {
                ProfileSectionBody.Cards => Shelf(s, CardsOf(s, u), accent, SeeAllFor(s, u, count)),
                ProfileSectionBody.Empty => Artist.SectionBlock(SectionTitle(s), EmptyShelf(s, f.Own), accent),
                _ => null,
            };
        }

        Action? SeeAllFor(ProfileSection s, User u, int count)
        {
            int total = s == ProfileSection.Playlists ? Math.Max(u.PublicPlaylists, count) : count;
            return ProfileSections.SeeAll(s, total, Math.Min(count, ProfileSections.CapOf(s))) ? _seeAll[(int)s] : null;
        }

        static ProfileCard[] CardsOf(ProfileSection s, User u)
        {
            var scope = Entities.Current;
            var e = scope.Edges;
            return s switch
            {
                ProfileSection.TopArtists => Ranked(e.UserTopArtists.Targets(scope.MeSlot), ProfileSections.TopCap),
                ProfileSection.Playlists => Cards(e.ProfilePlaylists, u.Slot, ProfileSections.PlaylistCap, EntityKind.Playlist),
                ProfileSection.RecentArtists => Cards(e.ProfileArtists, u.Slot, ProfileSections.ArtistCap, EntityKind.Artist),
                ProfileSection.Following => Cards(e.ProfileFollowing, u.Slot, ProfileSections.PeopleCap, EntityKind.User),
                _ => Cards(e.ProfileFollowers, u.Slot, ProfileSections.PeopleCap, EntityKind.User),
            };
        }

        static ProfileCard[] Cards(EdgeTable<ProfileCardEdge> edge, int parent, int cap, EntityKind fallback)
        {
            var targets = edge.Targets(parent);
            var payload = edge.Payload(parent);
            int n = Math.Min(targets.Length, cap);
            var items = new ProfileCard[n];
            for (int i = 0; i < n; i++)
            {
                var kind = i < payload.Length && payload[i].Kind != EntityKind.Unknown ? payload[i].Kind : fallback;
                int t = targets[i];
                items[i] = new ProfileCard(kind, t, VersionOf(kind, t), i < payload.Length ? payload[i].Followers : 0, i + 1, KeyOf(kind, t));
            }
            return items;
        }

        static ProfileCard[] Ranked(ReadOnlySpan<int> artists, int cap)
        {
            int n = Math.Min(artists.Length, cap);
            var items = new ProfileCard[n];
            for (int i = 0; i < n; i++)
                items[i] = new ProfileCard(EntityKind.Artist, artists[i], VersionOf(EntityKind.Artist, artists[i]), 0, i + 1,
                    KeyOf(EntityKind.Artist, artists[i]));
            return items;
        }

        static uint VersionOf(EntityKind kind, int slot) => Entities.TableFor(kind) is { } t ? RowFold.Version(t, slot) : 0u;

        static string KeyOf(EntityKind kind, int slot)
            => (kind switch { EntityKind.User => "u", EntityKind.Playlist => "p", _ => "a" }) + slot.ToString(CultureInfo.InvariantCulture);

        static ProfileFacet FacetOf(ProfileSection s) => s switch
        {
            ProfileSection.Following => ProfileFacet.Following,
            ProfileSection.Followers => ProfileFacet.Followers,
            _ => ProfileFacet.Playlists,
        };

        static ProfileFacet FacetOf(ProfileStat s) => s switch
        {
            ProfileStat.Following => ProfileFacet.Following,
            ProfileStat.Followers => ProfileFacet.Followers,
            _ => ProfileFacet.Playlists,
        };

        void OpenList(ProfileFacet facet)
        {
            if (_user.IsValid) Shell.GoTo(ProfileListRoute.For(_user, facet));
        }

        // ── the context band (Artist.Page §2.2, the profile arm: title · pivot · Follow as a word) ──────────────────────

        Element BandBar(string uri, bool own, float width, float gutter, float collapse, bool canHit,
                        (string Label, Action OnClick)[] pivotItems)
        {
            float actionH = Detail.BandLayout.Height - 2f * Spacing.M;
            string name = _name;
            Element title = new BoxEl
            {
                Direction = 1, MinWidth = 0f, Shrink = 1f, MaxWidth = Detail.BandLayout.TitleCap,
                Cursor = CursorId.Hand, OnClick = _scrollToTop,
                Children = [Detail.BandTitle(name)],
            };
            Element pivot = new BoxEl
            {
                Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, Height = Detail.BandLayout.Height, AlignItems = FlexAlign.Center,
                Children = [Detail.Pivot(pivotItems, _active, _accentFn)],
            };
            Element actions = own
                ? new BoxEl { Width = 0f, Height = 0f }
                : new BoxEl
                {
                    Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        Embed.Comp(() => new Controls.FollowTextAction
                            { Uri = uri, Name = name, Height = actionH, PadX = Detail.BandLayout.ActionPadX })
                            with { Key = "profile-band-follow:" + uri, SkeletonProxy = s_emptyShape },
                    ],
                };
            Element row = Detail.Band(MathF.Min(width, Design.Size.PageMaxW), gutter, [title, pivot, actions]);
            float revealStart = ArtistHeroLayout.CompactRevealStart(collapse);
            return new BoxEl
            {
                Width = width, Height = Detail.BandLayout.Height, ZStack = true,
                HitTestVisible = canHit, HitTestPassThrough = true,
                Children =
                [
                    new BoxEl { Direction = 0, Width = width, Height = Detail.BandLayout.Height, Justify = FlexJustify.Center, Children = [row] },
                    new BoxEl
                    {
                        Width = width, Height = Detail.BandLayout.Height, Direction = 1, Justify = FlexJustify.End,
                        HitTestVisible = false, Children = [Detail.BandHairline()],
                    },
                ],
            }.Reveal(revealStart, collapse - revealStart, Design.Reduced ? 0f : Spacing.XS).Skeletonized(false);
        }

        void GoToSection(int section)
        {
            var scene = Context.Scene;
            var node = _anchors[section];
            if (scene is null || node.IsNull || _viewport.IsNull || !scene.IsLive(node) || !scene.IsLive(_viewport)) return;
            scene.BringIntoView(_viewport, node, align: 0f, Design.Reduced ? ScrollMove.Immediate : ScrollMove.Glide,
                margin: Detail.BandLayout.Height);
        }

        void ResolveSpy()
        {
            _ = _scrollY.Value;
            _ = _viewportH.Value;
            bool atEnd = _atEnd.Value;
            _ = _pivotEpoch.Value;
            var scene = Context.Scene;
            if (scene is null || _viewport.IsNull || !scene.IsLive(_viewport)) return;
            int n = _pivotCount;
            if (n == 0) return;
            RectF vp = scene.AbsoluteRect(_viewport);
            Span<float> tops = stackalloc float[ProfileSections.Count];
            for (int i = 0; i < n; i++)
            {
                var node = _anchors[(int)_plan[i]];
                tops[i] = node.IsNull || !scene.IsLive(node) ? float.NaN : scene.AbsoluteRect(node).Y - vp.Y;
            }
            float vh = _viewportH.Peek();
            if (vh <= 0f) vh = vp.H;
            int at = Detail.BandLayout.ActiveSection(tops[..n], Detail.BandLayout.Height, vh, atEnd);
            if (at != -1) _active.SetIfChanged(at);
        }

        // ── demand (the planner is the ONE data path) ──────────────────────────────────────────────────────────────────

        /// <summary>Once per (user, scope): the row, the two shelves that ride the same ProfileView GET (same route key → one
        /// request), and own top artists. ProfileAsk decides Ensure (never answered) vs Invalidate (SWR re-read).</summary>
        void Demand()
        {
            var u = _user;
            if (!u.IsValid) return;
            var scope = Entities.Current;
            var e = scope.Edges;
            int slot = u.Slot;
            AskRow(u);
            AskEdge(FetchEdge.ProfilePlaylists, e.ProfilePlaylists.State(slot), slot);
            AskEdge(FetchEdge.ProfileArtists, e.ProfileArtists.State(slot), slot);
            if (slot == scope.MeSlot) Home.Feeds.EnsureTopContent();
        }

        /// <summary>Auto-tracked: the follow lists only when they will SHOW (own, or ShowFollows once Social lands);
        /// once per (user, scope).</summary>
        void DemandLists()
        {
            uint epoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Users.Changed.Value;
            var u = _user;
            if (!u.IsValid) return;
            bool own = u.Slot == scope.MeSlot || u.IsCurrentUser;
            if (!ProfileSections.ShowsPeople(own, u.Knows(UserFields.Social) && u.ShowFollows)) return;
            long key = ((long)epoch << 32) | (uint)u.Slot;
            if (key == _listsAskedKey) return;
            _listsAskedKey = key;
            AskLists(u);
        }

        /// <summary>A keep-alive return: re-ask everything shown (ProfileAsk → Invalidate: no skeleton, fresh counts).</summary>
        void Reask()
        {
            Demand();
            var u = _user;
            if (u.IsValid && ProfileSections.ShowsPeople(_own, u.Knows(UserFields.Social) && u.ShowFollows)) AskLists(u);
        }

        static void AskLists(User u)
        {
            var e = Entities.Current.Edges;
            int slot = u.Slot;
            AskEdge(FetchEdge.ProfileFollowing, e.ProfileFollowing.State(slot), slot);
            AskEdge(FetchEdge.ProfileFollowers, e.ProfileFollowers.State(slot), slot);
        }

        // THE two call sites of the data designer's ProfileAsk — the only lines to adapt if its final shape differs.
        static void AskRow(User u)
        {
            int slot = u.Slot;
            if (ProfileAsk.Row(u.Knows(PageFields)) == ProfileAskVerdict.Invalidate)
                Entities.Invalidate(Entities.Current.Users, new ReadOnlySpan<int>(in slot), (uint)PageFields);
            else
                Entities.Ensure(u, PageFields);
        }

        static void AskEdge(FetchEdge edge, EdgeState state, int parent)
        {
            if (ProfileAsk.Edge(state) == ProfileAskVerdict.Invalidate) Entities.InvalidateEdge(edge, parent);
            else Entities.EnsureEdge(edge, parent);
        }

        void Retry()
        {
            var u = _user;
            if (!u.IsValid) return;
            int slot = u.Slot;
            Entities.Refresh(Entities.Current.Users, new ReadOnlySpan<int>(in slot), (uint)PageFields);
            Entities.RefreshEdge(FetchEdge.ProfilePlaylists, slot);
            Entities.RefreshEdge(FetchEdge.ProfileArtists, slot);
        }

        void RetrySection(ProfileSection s)
        {
            var u = _user;
            if (!u.IsValid) return;
            switch (s)
            {
                case ProfileSection.TopArtists: Home.Feeds.EnsureTopContent(); break;
                case ProfileSection.Playlists: Entities.RefreshEdge(FetchEdge.ProfilePlaylists, u.Slot); break;
                case ProfileSection.RecentArtists: Entities.RefreshEdge(FetchEdge.ProfileArtists, u.Slot); break;
                case ProfileSection.Following: Entities.RefreshEdge(FetchEdge.ProfileFollowing, u.Slot); break;
                default: Entities.RefreshEdge(FetchEdge.ProfileFollowers, u.Slot); break;
            }
        }

        // ── the render-cost fold (Artist.Page.Stamp's shape) ───────────────────────────────────────────────────────────

        /// <summary>WAKE on every table/edge the page paints; PUSH only what moved for THIS user. Unlike the artist fold it
        /// also folds the row's MARKS (Known/Asked/Failed/Inflight) and each edge's READINESS: a 404 seal or a terminal
        /// failure bumps no Version, and without these the page would shimmer forever instead of turning Unavailable/Failed.</summary>
        long Stamp()
        {
            _ = Entities.ScopeEpoch.Value;
            var u = _user;
            if (!u.IsValid) return 0L;
            var scope = Entities.Current;
            var e = scope.Edges;
            var users = scope.Users;
            int slot = u.Slot, me = scope.MeSlot;

            _ = users.Changed.Value;
            _ = scope.Playlists.Changed.Value;
            _ = scope.Artists.Changed.Value;
            _ = e.ProfilePlaylists.Changed.Value;
            _ = e.ProfileArtists.Changed.Value;
            _ = e.ProfileFollowing.Changed.Value;
            _ = e.ProfileFollowers.Changed.Value;
            _ = e.UserTopArtists.Changed.Value;
            var top = Home.Feeds.TopContentState.Value;

            ulong h = RowFold.Row(RowFold.Seed, users, slot);
            h = RowFold.Add(h, users.Known[slot]);
            h = RowFold.Add(h, users.Asked[slot]);
            h = RowFold.Add(h, users.Failed[slot]);
            h = RowFold.Add(h, users.Inflight[slot] != 0 ? 1 : 0);
            h = Fold(h, e.ProfilePlaylists, slot, ProfileSections.PlaylistCap);
            h = Fold(h, e.ProfileArtists, slot, ProfileSections.ArtistCap);
            h = Fold(h, e.ProfileFollowing, slot, ProfileSections.PeopleCap);
            h = Fold(h, e.ProfileFollowers, slot, ProfileSections.PeopleCap);
            if (slot == me || u.IsCurrentUser)
            {
                h = RowFold.Add(h, (int)top);
                h = RowFold.Add(h, e.UserTopArtists.Version(me));
                var tops = e.UserTopArtists.Targets(me);
                for (int i = 0; i < tops.Length && i < ProfileSections.TopCap; i++) h = RowFold.Row(h, scope.Artists, tops[i]);
            }
            return unchecked((long)h);
        }

        static ulong Fold(ulong h, EdgeTable<ProfileCardEdge> edge, int parent, int cap)
        {
            h = RowFold.Add(h, edge.Version(parent));
            h = RowFold.Add(h, (int)edge.Readiness(parent));
            var targets = edge.Targets(parent);
            var payload = edge.Payload(parent);
            int n = Math.Min(targets.Length, cap);
            for (int i = 0; i < n; i++)
                if (Entities.TableFor(i < payload.Length ? payload[i].Kind : EntityKind.Unknown) is { } t)
                    h = RowFold.Row(h, t, targets[i]);
            return h;
        }

        // ── colour ─────────────────────────────────────────────────────────────────────────────────────────────────────

        void PublishAccent()
        {
            _ = Entities.ScopeEpoch.Value;
            _ = Entities.Current.Users.Changed.Value;
            _ = _theme.Value;
            PublishAccent(_user, _latest?.RouteKey ?? "");
        }

        /// <summary>Graded avatar → profile colour (payload) → held → default (Detail.AccentFor's ladder). The palette url
        /// is WATCHED here, inside the tracked effect, so a grading landing re-derives the accent.</summary>
        void PublishAccent(User u, string routeKey)
        {
            ProfileToneSource tone = default;
            if (u.IsValid)
            {
                string? avatar = ProfileCover.Of(Entities.Strings.Resolve(u.Image)).Url;
                tone = ProfileTone.Of(avatar, u.Color, avatar is { Length: > 0 } a && Palette.CanGrade(a));
            }
            if (tone.PaletteUrl is { Length: > 0 } url) _ = Palette.Watch(url).Value;
            var accent = Detail.AccentFor(tone.PaletteUrl, tone.PayloadArgb);
            _accent.SetIfChanged(accent);
            _pageAccent.SetIfChanged(new Design.PageAccent(accent, accent, routeKey));
        }

        // ── the derived shimmer ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A ShimmerSource, never mounted: the SAME hero composition over a placeholder (no band, the action row a
        /// Skeletonized(false) spacer), the divider, and two seed shelves at the magazine's real width — own: Top artists +
        /// Public playlists; other: Public playlists + Recently played artists.</summary>
        Element PageShimmer()
        {
            var m = _metrics;
            float inner = MagazineInnerWidth();
            var first = _own ? ProfileSection.TopArtists : ProfileSection.Playlists;
            var second = _own ? ProfileSection.Playlists : ProfileSection.RecentArtists;
            ColorF accent = _accent.Peek();
            return new BoxEl
            {
                Direction = 1,
                Children =
                [
                    HeroBanner(HeroText.PlaceholderFor(_own), "", _width, in m, false, null, null),
                    new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault },
                    new BoxEl
                    {
                        Direction = 0, Justify = FlexJustify.Center,
                        Children = [Magazine([SeedShelf(first, accent, inner), SeedShelf(second, accent, inner)], m.Gutter)],
                    },
                ],
            };
        }
    }
}
```

The one-line hook in `Entities/Artist.Page.cs` (U1):

```csharp
public static void InstallPages()
{
    Shell.SetPage(Shell.RouteKind.Artist, Page);
    Shell.SetPage(Shell.RouteKind.Discography, DiscographyPage);
    Concert.InstallPages();
    Profile.InstallPages();          // the profile family (user: + the list designer's people:) — eager, BootOrderingTests boots it
}
```

---

## 4. `Entities/Profile.UI.cs` (U2): hero, sections, cards

```csharp
// ── Entities/Profile.UI.cs ─────────────────────────────────────────────────────────────────────────────────────────────
// The profile page's LEAVES: the round-avatar hero (avatar · name · stats · actions) and its collapse into the band, the
// section headers / shelves / seeds / vacancies, and the card adapters (playlist, artist, person, ranked artist).
//
// Role: UI   Owner: U2   Template: Artist.UI.cs §4.1 (HeroBanner / HeroIdentity / HeroActions), Artist.Page.cs §2.6
//
// TYPE (Design.cs:1148-1170 — no new alias, no raw TextEl size): name = ArtistDisplay / ArtistTitle / ArtistCompactTitle by
// tier; stats and ranks = StatHero (the sanctioned 350 cut); stat captions = Caption at 600 (Track.Drawer.Stat's idiom);
// section headers = Controls.AccentHeader (RailHeader); card text = the shared surface. No eyebrow over the name.
// NAMES: no member here is called Title/Subtitle/Caption/Image/Actions — they would hide Ui.* and the Actions type.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Profile
{
    // ══ 1. THE HERO ═══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What the hero states. <see cref="PlaceholderFor"/> is the shimmer's representative shape — its words are
    /// never shown (the deriver paints bars), its avatar is a plain bone circle.</summary>
    internal readonly record struct HeroText(string Name, string? AvatarUrl, uint PersonArgb, int Followers, int Following,
                                             int Playlists, bool Own, bool ShowFollows, bool Placeholder)
    {
        public static HeroText For(User u, bool own, string? avatarUrl)
            => new(u.Name, avatarUrl, ProfileTone.Argb(u.Color), u.Followers, u.Following, u.PublicPlaylists,
                   own, u.ShowFollows, false);

        public static HeroText PlaceholderFor(bool own)
            => new("Display name", null, 0u, 1_000, 1_000, 100, own, true, true);
    }

    /// <summary>The hero's verbs, built ONCE by the page (stable instance). Null = the shimmer arm (shapes only).</summary>
    internal sealed record HeroActs(Action Share, Func<ContextMenuModel?> Menu, Action<ProfileStat> OpenStat);

    /// <summary>The round-avatar hero and its collapse into the 56-DIP band (Artist.HeroBanner's pinning, verbatim):
    /// horizontal tiers put avatar | copy bottom-aligned; stacked tiers put avatar over copy. Content sits on the hero's
    /// bottom edge inside a fixed tier height, so the sticky collapse is exact.</summary>
    internal static Element HeroBanner(in HeroText t, string uri, float width, in ProfileHeroMetrics m, bool compactCanHit,
                                       HeroActs? acts, Element? band)
    {
        float w = MathF.Max(1f, width);
        float collapse = ProfileHeroLayout.CollapseDistance(in m);
        ScrollEffectSpec[] collapseEffects =
        [
            new(ScrollEffect.Parallax(0.0, collapse, 0f, -collapse)),
            new(ScrollEffect.Fade(ArtistHeroLayout.ExpandedFadeStart(collapse), collapse, 1f, 0f)),
        ];
        Element avatar = Avatar(in t, m.Avatar);
        Element copy = Identity(in t, uri, in m, acts);
        Element content = m.Stacked
            ? new BoxEl
            {
                Direction = 1, Gap = ProfileHeroLayout.StackedAvatarGap, AlignItems = FlexAlign.Start, MinWidth = 0f,
                Children = [avatar, copy],
            }
            : new BoxEl
            {
                Direction = 0, Gap = ProfileHeroLayout.AvatarGap, AlignItems = FlexAlign.End, MinWidth = 0f,
                Children = [avatar, copy],
            };
        Element expanded = new BoxEl
        {
            Width = w, Height = m.Height, Direction = 0, Justify = FlexJustify.Center,
            HitTestVisible = !compactCanHit, ScrollEffects = collapseEffects,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Justify = FlexJustify.End, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    MaxWidth = Design.Size.PageMaxW,   // aligns with the magazine's 1600 measure
                    Padding = new Edges4(m.Gutter, m.TopPad, m.Gutter, m.BottomPad),
                    Children = [content],
                },
            ],
        };
        return new BoxEl
        {
            Direction = 1, Height = m.Height, ZStack = true,
            Children = band is null ? [expanded] : [expanded, band],
        }.Sticky(0f).Collapse(collapse, ArtistHeroLayout.CompactIdentityHeight, CollapseAnchor.Leading);
    }

    /// <summary>The photo in a clipped circle (Artist.Reader's avatar + Artist.UI's pick-avatar precedent: Controls.Artwork,
    /// so the tile is the WATCHED placeholder, not PersonPicture's frozen fill); no photo → PersonPicture initials on the
    /// person's own colour.</summary>
    static Element Avatar(in HeroText t, float edge)
    {
        if (t.Placeholder)
            return new BoxEl { Width = edge, Height = edge, Shrink = 0f, Corners = Radii.Circle(edge), Fill = Tok.FillSubtleSecondary };
        if (t.AvatarUrl is { Length: > 0 } url)
            return new BoxEl
            {
                Width = edge, Height = edge, Shrink = 0f, ZStack = true, ClipToBounds = true,
                Corners = Radii.Circle(edge), BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Shadow = Elevation.Card,
                Children = [Controls.Artwork(url, edge, edge, edge / 2f, decodePx: ProfileHeroLayout.AvatarDecodePx)],
            };
        ColorF? fill = t.PersonArgb != 0 ? Design.Palette.ToColor(t.PersonArgb) : null;
        return PersonPicture.Create("", edge, displayName: t.Name, fill: fill) with { Shrink = 0f, Shadow = Elevation.Card };
    }

    /// <summary>Name (the tier's Artist alias, line budget from the metrics) · stats · actions. No eyebrow.</summary>
    static BoxEl Identity(in HeroText t, string uri, in ProfileHeroMetrics m, HeroActs? acts)
    {
        TextEl name = (m.Tier switch
        {
            ArtistHeroTier.Wide => Design.Type.ArtistDisplay(t.Name),
            ArtistHeroTier.Medium => Design.Type.ArtistTitle(t.Name),
            _ => Design.Type.ArtistCompactTitle(t.Name),
        }) with
        {
            Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MaxLines = m.NameLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, Shrink = 1f,
            Grow = m.Stacked ? 0f : 1f, Basis = m.Stacked ? float.NaN : 0f,
            AlignSelf = m.Stacked ? FlexAlign.Stretch : FlexAlign.Auto,
            MaxWidth = m.CopyMaxWidth,
            Children =
            [
                name,
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, Margin = new Edges4(0f, ProfileHeroLayout.NameToStats(in m), 0f, 0f),
                    Children = [Stats(in t, in m, acts)],
                },
                new BoxEl
                {
                    Direction = 0, MinWidth = 0f, Margin = new Edges4(0f, ProfileHeroLayout.StatsToActions(in m), 0f, 0f),
                    Children = [HeroActions(in t, uri, acts)],
                },
            ],
        };
    }

    /// <summary>The numerals — explicit rows (Narrow's 2-row budget splits 2 + 1), never a wrap, so the height the
    /// metrics budget is the height laid out.</summary>
    static Element Stats(in HeroText t, in ProfileHeroMetrics m, HeroActs? acts)
    {
        Span<ProfileStatCell> cells = stackalloc ProfileStatCell[ProfileStats.Max];
        int n = ProfileStats.Plan(t.Own, t.ShowFollows, t.Followers, t.Following, t.Playlists, cells);
        int perRow = ProfileStats.PerRow(n, m.StatRows);
        int rows = (n + perRow - 1) / perRow;
        var lines = new Element[rows];
        for (int r = 0; r < rows; r++)
        {
            int start = r * perRow, count = Math.Min(perRow, n - start);
            var kids = new Element[count];
            for (int i = 0; i < count; i++) kids[i] = Stat(cells[start + i], acts);
            lines[r] = new BoxEl
            {
                Direction = 0, Gap = ProfileHeroLayout.StatGap, AlignItems = FlexAlign.Start, MinWidth = 0f, Children = kids,
            };
        }
        return rows == 1 ? lines[0] : new BoxEl { Direction = 1, Gap = ProfileHeroLayout.StatRowGap, MinWidth = 0f, Children = lines };
    }

    /// <summary>A StatHero numeral over its sentence-case caption (Track.Drawer.Stat). A linking stat is a subtle-plated
    /// button into the list route; the ±8/±4 margin keeps the TEXT on the copy's edge while the plate overhangs.</summary>
    static Element Stat(in ProfileStatCell c, HeroActs? acts)
    {
        var cell = new BoxEl
        {
            Direction = 1, Shrink = 1f, MinWidth = 0f, Corners = Radii.ControlAll,
            Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
            Margin = new Edges4(-Spacing.S, -Spacing.XS, -Spacing.S, -Spacing.XS),
            Children =
            [
                Design.Type.StatHero(ProfileStats.Numeral(c.Value, CultureInfo.CurrentCulture), null),
                Caption(StatLabel(in c)) with
                {
                    Weight = 600, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
            ],
        };
        if (!c.Links || acts is null) return cell;
        var open = acts.OpenStat;
        var kind = c.Kind;
        return (cell with { Role = AutomationRole.Button, Focusable = true, OnClick = () => open(kind) })
            .Interactive(Interaction.Subtle);
    }

    static string StatLabel(in ProfileStatCell c) => c.Kind switch
    {
        ProfileStat.Followers => Strings.Person.Stat.Followers(c.Value),
        ProfileStat.Following => Loc.Get(Strings.Person.Stat.Following),
        _ => Strings.Person.Stat.Playlists(c.Value),
    };

    /// <summary>[FollowToggle (others only)] [Share] [⋯] — the normal grammar (Artist.HeroActions' family). Never skeleton
    /// content: Skeletonized(false) leaves a same-size blank in the shimmer.</summary>
    static Element HeroActions(in HeroText t, string uri, HeroActs? acts)
    {
        string name = t.Name;
        var kids = new List<Element>(3);
        if (!t.Own)
            kids.Add(acts is null || uri.Length == 0
                ? Controls.FollowToggle.SkeletonShape()
                : Embed.Comp(() => new Controls.FollowToggle { Uri = uri, Name = name })
                    with { Key = "profile-follow:" + uri, SkeletonProxy = s_followShape });
        kids.Add(Album.CommandCircle(Icons.Share, Loc.Get(Strings.Menu.Share), acts?.Share ?? s_noop));
        kids.Add(acts is null
            ? new BoxEl { Width = Controls.IconButtonSize, Height = Controls.IconButtonSize, Corners = Radii.ControlAll }
            : Detail.MoreButton(acts.Menu, Controls.IconButtonSize, 16f, round: false) with { Key = "profile-more:" + uri });
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Height = ProfileHeroLayout.ActionRow,
            Children = kids.ToArray(),
        }.Skeletonized(false);
    }

    static readonly Func<Element> s_followShape = static () => Controls.FollowToggle.SkeletonShape();
    static readonly Action s_noop = static () => { };

    /// <summary>The ⋯ menu: Pin/Unpin (the route's pin pair) and Share ▸ (Copy link / Copy Spotify URI / Open in web).
    /// Null when nothing applies, so the ⋯ opens nothing rather than an empty flyout.</summary>
    internal static ContextMenuModel? MenuFor(User u, string name)
    {
        if (!u.IsValid) return null;
        var ctx = new ActionContext(new ActionTarget(TargetKind.None, [], u.Uri, name, PlaylistHost.None), Actions.Services);
        var rows = new List<MenuFlyoutItem>(4);
        if (AppActions.Find(ActionId.PinToSidebar) is { } pin && pin.EnabledFor(in ctx)) rows.Add(pin.ToMenuItem(in ctx));
        else if (AppActions.Find(ActionId.UnpinFromSidebar) is { } unpin && unpin.EnabledFor(in ctx)) rows.Add(unpin.ToMenuItem(in ctx));
        if (Actions.Menu.Share(in ctx) is { } share) { Actions.Menu.OpenGroup(rows); rows.Add(share); }
        if (rows.Count == 0) return null;
        string? art = ProfileCover.Of(Entities.Strings.Resolve(u.Image)).Url;
        return new ContextMenuModel([], rows, Actions.Menu.Header(art, name, Loc.Get(Strings.Search.TypeUser), circular: true));
    }

    // ══ 2. THE SECTIONS ═══════════════════════════════════════════════════════════════════════════════════════════════

    internal static string SectionTitle(ProfileSection s) => Loc.Get(s switch
    {
        ProfileSection.TopArtists => Strings.Person.TopArtists,
        ProfileSection.Playlists => Strings.Person.PublicPlaylists,
        ProfileSection.RecentArtists => Strings.Person.RecentArtists,
        ProfileSection.Following => Strings.Person.Following,
        _ => Strings.Person.Followers,
    });

    internal static string PivotLabel(ProfileSection s) => Loc.Get(s switch
    {
        ProfileSection.TopArtists => Strings.Person.Pivot.TopArtists,
        ProfileSection.Playlists => Strings.Person.Pivot.Playlists,
        ProfileSection.RecentArtists => Strings.Person.Pivot.Artists,
        ProfileSection.Following => Strings.Person.Pivot.Following,
        _ => Strings.Person.Pivot.Followers,
    });

    /// <summary>AccentHeader · [a quiet badge chip] · [See all] — the shelf's custom header (the shelf adds its chevrons).</summary>
    internal static Element SectionHeader(string title, ColorF accent, Action? seeAll, string? chip = null)
    {
        BoxEl head = Controls.AccentHeader(title, accent) with { Shrink = 1f };
        if (seeAll is null && chip is null) return head;
        var kids = new List<Element>(3) { head };
        if (chip is { Length: > 0 }) kids.Add(Controls.Chip(chip));
        if (seeAll is not null) kids.Add(HyperlinkButton.Create(Loc.Get(Strings.Home.SeeAll), seeAll) with { Shrink = 0f });
        return new BoxEl { Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = kids.ToArray() };
    }

    // The artist shelf's geometry (Artist.Page.ShelfOf = PagedShelf defaults) at a ONE-line caption.
    const float ShelfMinCard = 150f, ShelfMaxCard = 200f, ShelfGap = 12f, ShelfHeaderGap = 12f, ShelfChevron = 32f;
    /// <summary>The rank numeral's column beside a top-artist card.</summary>
    internal const float RankColumn = 36f;

    static readonly SurfaceShape s_card = Shape.Shelf(captionLines: 1);
    static readonly Func<float, float> s_cardHeight = static w => SurfaceGeometry.ShelfHeight(w, 1f, 1, false);
    static readonly Func<float, float> s_rankedHeight = static w => SurfaceGeometry.ShelfHeight(w - RankColumn, 1f, 1, false);
    static readonly Func<ProfileCard, int, string> s_keyOf = static (c, _) => c.Key;
    static readonly Action<ProfileCard, int> s_open = static (c, _) => Open(c);
    static readonly Func<ProfileCard, int, float, Element> s_playlistCard = static (c, _, w) => PlaylistCard(c, w);
    static readonly Func<ProfileCard, int, float, Element> s_artistCard = static (c, _, w) => ArtistCard(c, w);
    static readonly Func<ProfileCard, int, float, Element> s_personCard = static (c, _, w) => PersonCard(c, w);
    static readonly Func<ProfileCard, int, float, Element> s_rankedCard = static (c, _, w) => RankedArtistCard(c, w);

    /// <summary>A present section with rows. Every shelf of surface cards passes onInvoke (the slot owns click + focus).</summary>
    internal static Element Shelf(ProfileSection s, ProfileCard[] items, ColorF accent, Action? seeAll)
    {
        string title = SectionTitle(s);
        return s switch
        {
            ProfileSection.TopArtists => ShelfOf(items, s_rankedCard,
                SectionHeader(title, accent, null, Loc.Get(Strings.Person.OnlyYou)), s_rankedHeight),
            ProfileSection.Playlists => ShelfOf(items, s_playlistCard, SectionHeader(title, accent, seeAll), s_cardHeight),
            ProfileSection.RecentArtists => ShelfOf(items, s_artistCard, SectionHeader(title, accent, null), s_cardHeight),
            _ => ShelfOf(items, s_personCard, SectionHeader(title, accent, seeAll), s_cardHeight),
        };
    }

    static Element ShelfOf(ProfileCard[] items, Func<ProfileCard, int, float, Element> cardAt, Element header,
                           Func<float, float> cardHeight) => new BoxEl
    {
        Direction = 1, MinWidth = 0f,
        Children = [PagedShelf.Create(items, cardAt, onInvoke: s_open, cardHeight: cardHeight, header: header,
            measured: false, keyOf: s_keyOf, lift: ShelfLift.None)],
    };

    /// <summary>A section's shimmer SOURCE at the shelf's real fit: the real header, then the fitted count of SEED
    /// surfaces (a PagedShelf yields no rows against an unmeasured viewport — Browse's must-not-simplify 11).</summary>
    internal static Element SeedShelf(ProfileSection s, ColorF accent, float width)
    {
        bool ranked = s == ProfileSection.TopArtists;
        var (perPage, cardW) = FillRowVirtualLayout.Fit(width, ShelfMinCard, ShelfMaxCard, ShelfGap);
        var cells = new Element[perPage];
        for (int i = 0; i < perPage; i++)
        {
            Element seed = Controls.Surface(Controls.CardData.Seed, s_card, ranked ? cardW - RankColumn : cardW);
            cells[i] = ranked
                ? new BoxEl { Direction = 0, Width = cardW, Shrink = 0f, Children = [new BoxEl { Width = RankColumn, Shrink = 0f }, seed] }
                : seed;
        }
        return new BoxEl
        {
            Direction = 1, Gap = ShelfHeaderGap, MinWidth = 0f,
            Children =
            [
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, MinHeight = ShelfChevron, Children = [SectionHeader(SectionTitle(s), accent, null)] },
                new BoxEl { Direction = 0, Gap = ShelfGap, MinWidth = 0f, ClipToBounds = true, Children = cells },
            ],
        };
    }

    /// <summary>Public playlists, answered and empty (the only section kept when empty): own copy offers the way out.</summary>
    internal static Element EmptyShelf(ProfileSection s, bool own) => own
        ? Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact,
              title: Loc.Get(Strings.Person.Empty.PlaylistsOwn), subtitle: Loc.Get(Strings.Person.Empty.PlaylistsOwnHint))
        : Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact,
              title: Loc.Get(Strings.Person.Empty.Playlists), subtitle: "");

    /// <summary>A failed section: its header over a compact error vacancy WITH its own Retry (RefreshEdge).</summary>
    internal static Element SectionError(ProfileSection s, ColorF accent, Action retry)
        => Artist.SectionBlock(SectionTitle(s),
            Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact, onAction: retry), accent);

    /// <summary>The 404 face (private / closed account): the page keeps its frame, no Retry.</summary>
    internal static Element Unavailable()
        => Controls.Vacancy(Controls.VacancyVoice.Empty, title: Loc.Get(Strings.Person.Empty.Unavailable),
            subtitle: Loc.Get(Strings.Person.Empty.UnavailableHint));

    // ══ 3. THE CARDS ══════════════════════════════════════════════════════════════════════════════════════════════════

    static Element CardSubtitle(string text)
        => Design.Type.TrackMeta(text) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };

    static void Open(in ProfileCard c)
    {
        switch (c.Kind)
        {
            case EntityKind.Playlist:
            {
                var pl = new Playlist(c.Slot);
                Shell.GoTo(Shell.For(pl.Uri, Entities.Strings.Resolve(pl.TitleId)));
                break;
            }
            case EntityKind.Artist:
                Track.GoToArtist(new Artist(c.Slot));
                break;
            case EntityKind.User:
            {
                var u = new User(c.Slot);
                Shell.GoTo(Shell.For(u.Uri, u.Name));
                break;
            }
        }
    }

    /// <summary>Square cover (a cover-less playlist composes its 2×2 mosaic), title, "N followers" when non-zero.</summary>
    static Element PlaylistCard(ProfileCard c, float w)
    {
        var pl = new Playlist(c.Slot);
        string title = Entities.Strings.Resolve(pl.TitleId);
        var art = ProfileCover.Of(Entities.Strings.Resolve(pl.ImageId));
        float inner = SurfaceParts.InnerOf(w);
        var hit = new EntityRef(EntityKind.Playlist, c.Slot);
        var card = c;
        var data = new Controls.CardData(pl.Uri.Text, title,
            c.Followers > 0 ? CardSubtitle(Strings.Person.Card.Followers(c.Followers)) : null,
            art.Url, () => Open(card), () => Playback.PlayContext(pl.Id), Drag: Search.DragOf(hit),
            CoverOverride: art.Tiles is { } tiles ? Controls.Mosaic(tiles, inner, inner, Radii.Card) : null)
        {
            Menu = Search.MenuOf(hit),
        };
        return Controls.Surface(data, s_card, w);
    }

    static Element ArtistCard(ProfileCard c, float w)
    {
        var ar = new Artist(c.Slot);
        var hit = new EntityRef(EntityKind.Artist, c.Slot);
        var card = c;
        var data = new Controls.CardData(ar.Uri.Text, ar.Name, CardSubtitle(Loc.Get(Strings.Search.TypeArtist)),
            Controls.ArtUrl(ar.ImageId), () => Open(card), () => Playback.PlayContext(ar.Id), Circular: true,
            Drag: Search.DragOf(hit))
        {
            Menu = Search.MenuOf(hit),
        };
        return Controls.Surface(data, s_card, w);
    }

    /// <summary>A follow-list entry: an artist is the artist card; a person is a circle (photo, else initials on their own
    /// colour), "Profile", no play, no drag (Search.Drags refuses users), no menu.</summary>
    static Element PersonCard(ProfileCard c, float w)
    {
        if (c.Kind == EntityKind.Artist) return ArtistCard(c, w);
        var u = new User(c.Slot);
        string name = u.Name;
        string? url = ProfileCover.Of(Entities.Strings.Resolve(u.Image)).Url;
        float inner = SurfaceParts.InnerOf(w);
        uint argb = ProfileTone.Argb(u.Color);
        var card = c;
        var data = new Controls.CardData(u.Uri.Text, name, CardSubtitle(Loc.Get(Strings.Search.TypeUser)), url,
            () => Open(card), Circular: true,
            CoverOverride: url is null
                ? PersonPicture.Create("", inner, displayName: name, fill: argb != 0 ? Design.Palette.ToColor(argb) : null)
                : null);
        return Controls.Surface(data, s_card, w);
    }

    /// <summary>Own top artists: the rank as a StatHero numeral in a 36-DIP column at the cover's top, the circular
    /// artist card beside it (its extent is ShelfHeight(w − 36), which s_rankedHeight hands the shelf).</summary>
    static Element RankedArtistCard(ProfileCard c, float w) => new BoxEl
    {
        Direction = 0, Width = w, Shrink = 0f, AlignItems = FlexAlign.Start,
        Children =
        [
            new BoxEl
            {
                Width = RankColumn, Shrink = 0f, Direction = 1, AlignItems = FlexAlign.End, HitTestVisible = false,
                Padding = new Edges4(0f, SurfaceGeometry.ShelfGutterTop + SurfaceGeometry.ShelfPlatePad, Spacing.XS, 0f),
                Children = [Design.Type.StatHero(ProfileStats.Numeral(c.Rank, CultureInfo.CurrentCulture), null) with { Color = Tok.TextSecondary }],
            },
            ArtistCard(c, w - RankColumn),
        ],
    };
}
```

---

## 5. Loc keys (U2): new top-level group `"person"`

These are sentence case with ICU plurals. All three files must carry every key: `SatelliteMissing` is a Warning, and the build runs with `TreatWarningsAsErrors`. "See all", "Share", "Artist" and "Profile" reuse `home.seeAll`, `menu.share`, `search.typeArtist` and `search.typeUser`.

**en-US.json** (insert after the `"profile"` group):
```json
  "person": {
    "topArtists": "Top artists this month",
    "onlyYou": "Only visible to you",
    "publicPlaylists": "Public playlists",
    "recentArtists": "Recently played artists",
    "following": "Following",
    "followers": "Followers",
    "pivot": {
      "topArtists": "Top artists",
      "playlists": "Playlists",
      "artists": "Artists",
      "following": "Following",
      "followers": "Followers"
    },
    "stat": {
      "followers": "{count, plural, one {Follower} other {Followers}}",
      "following": "Following",
      "playlists": "{count, plural, one {Public playlist} other {Public playlists}}"
    },
    "card": {
      "followers": "{count, plural, one {# follower} other {# followers}}"
    },
    "empty": {
      "playlists": "No public playlists",
      "playlistsOwn": "No public playlists yet",
      "playlistsOwnHint": "Playlists you make public show up here.",
      "unavailable": "This profile isn’t available",
      "unavailableHint": "It may be private, or the account was closed."
    }
  },
```
**nl.json:**
```json
  "person": {
    "topArtists": "Topartiesten van deze maand",
    "onlyYou": "Alleen zichtbaar voor jou",
    "publicPlaylists": "Openbare playlists",
    "recentArtists": "Onlangs beluisterde artiesten",
    "following": "Volgend",
    "followers": "Volgers",
    "pivot": { "topArtists": "Topartiesten", "playlists": "Playlists", "artists": "Artiesten", "following": "Volgend", "followers": "Volgers" },
    "stat": {
      "followers": "{count, plural, one {Volger} other {Volgers}}",
      "following": "Volgend",
      "playlists": "{count, plural, one {Openbare playlist} other {Openbare playlists}}"
    },
    "card": { "followers": "{count, plural, one {# volger} other {# volgers}}" },
    "empty": {
      "playlists": "Geen openbare playlists",
      "playlistsOwn": "Nog geen openbare playlists",
      "playlistsOwnHint": "Playlists die je openbaar maakt, verschijnen hier.",
      "unavailable": "Dit profiel is niet beschikbaar",
      "unavailableHint": "Het is misschien privé, of het account is opgeheven."
    }
  },
```
**ko-KR.json:**
```json
  "person": {
    "topArtists": "이번 달 톱 아티스트",
    "onlyYou": "나에게만 표시됨",
    "publicPlaylists": "공개 플레이리스트",
    "recentArtists": "최근 재생한 아티스트",
    "following": "팔로잉",
    "followers": "팔로워",
    "pivot": { "topArtists": "톱 아티스트", "playlists": "플레이리스트", "artists": "아티스트", "following": "팔로잉", "followers": "팔로워" },
    "stat": {
      "followers": "{count, plural, other {팔로워}}",
      "following": "팔로잉",
      "playlists": "{count, plural, other {공개 플레이리스트}}"
    },
    "card": { "followers": "{count, plural, other {팔로워 #명}}" },
    "empty": {
      "playlists": "공개 플레이리스트 없음",
      "playlistsOwn": "아직 공개 플레이리스트가 없습니다",
      "playlistsOwnHint": "공개로 설정한 플레이리스트가 여기에 표시됩니다.",
      "unavailable": "이 프로필을 볼 수 없습니다",
      "unavailableHint": "비공개 프로필이거나 해지된 계정일 수 있습니다."
    }
  },
```
The `person.pivot.*` keys are shared: the list designer's facet bar should reuse them rather than mint a second set.

---

## 6. Component tree

```
Profile.Page(route)  Embed.Comp(PageProps) Key "user:"+routeKey
└ PageHost : Component, IPropsHost
  └ Ctx.Provide(Design.AccentCtx) → Ctx.Provide(LazyScroll) → BoxEl[root, OnBoundsChanged=_measure]
     ├ Palette.ShellTint                    (0×0 leaf: watch + publish, never a page re-render)
     └ ScrollView[Key, ScrollKey, Handle=_scroll, OnRealized=_captureViewport]
        └ BoxEl → BoxEl[Key "profile-body:"+epoch]
           └ SkelRegionEl(page)  Pending/Failed/Content/ShimmerSource=PageShimmer · Soft · Group null
              ├ Content(Ready) = Compose
              │  └ BoxEl[ZStack]
              │     ├ BoxEl[wash clip].StickyClip(56) → Palette.ArtistBlendWash (keyed leaf)
              │     └ BoxEl[col]
              │        ├ HeroBanner .Sticky(0).Collapse(h−56 → 56)
              │        │  ├ expanded [ScrollEffects: parallax + fade]
              │        │  │  └ inner [gutter, MaxW 1600, Justify End]
              │        │  │     └ row|col [ Avatar(Artwork circle | PersonPicture) , Identity ]
              │        │  │                  Identity = [ name(Artist* alias) , Stats(rows of Stat) , HeroActions ]
              │        │  │                  HeroActions = [ FollowToggle(keyed uri) , CommandCircle(Share) , Detail.MoreButton ]
              │        │  └ BandBar .Reveal  [ BandTitle → top · Detail.Pivot(spy) · FollowTextAction ]
              │        ├ sentinel .Sticky(56, engaged:_compact)
              │        └ magazine .StickyClip(56) EdgeFade{WhileStuck}
              │           ├ hairline
              │           └ Magazine (gap 32, MaxW 1600, dock reserve)
              │              └ ×planned: BoxEl[anchor key, OnRealized] → BoxEl[sec key] → SkelRegionEl(section) FadeOnly
              │                    Content: Cards → Shelf = PagedShelf(header=SectionHeader[AccentHeader · chip? · See all])
              │                             Empty → Artist.SectionBlock(title, Vacancy Compact)
              │                    Shimmer: SeedShelf(header + fitted seed surfaces)
              │                    OnFailed: SectionError(title + Vacancy Error Compact + Retry→RefreshEdge)
              ├ Content(Unavailable) = Profile.Unavailable()     (page vacancy, no retry)
              └ OnFailed = Vacancy(Error, Retry → Entities.Refresh + RefreshEdge)
```

---

## 7. Wireframes

**Wide (≥ 880; hero height 380). Someone else's profile:**
```
┌────────────────────────────────────────────────────────────────────────────────────────────────────┐
│▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒ ArtistBlendWash: graded avatar, else profile colour (h + 96 tail) ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│ ┐
│                                                                                    (top pad ≥ 40)  │ │
│  ╭──────────────╮                                                                                  │ │
│  │              │   Mira Vos                                ArtistDisplay 84/96/700, ≤ 2 lines     │ │
│  │    avatar    │                                                                                  │ │ 380
│  │   184 ○      │   118          1,063        94             StatHero 28/36/350                    │ │
│  │  (card ring) │   Followers    Following    Public playlists   Caption 12/16/600 tertiary        │ │
│  ╰──────────────╯   [♡ Follow]  [⇪]  [⋯]                      FollowToggle · CommandCircle · More  │ │
│  ← 36 gutter →  ←36→                                          (bottom pad 32)                     │ ┘
├────────────────────────────────────────────────────────────────────────────────────────────────────┤ divider
│  Public playlists   See all                                                            (‹)  (›)    │
│  ──                                                                                                │
│  ┌──────┐ ┌──────┐ ┌──────┐ ┌──────┐ ┌──────┐ ┌──────┐   150–200 cards, gap 12                    │
│  │      │ │▚▞▚▞▚▞│ │      │ │      │ │      │ │      │   ▚▞ = cover-less mosaic (Controls.Mosaic) │
│  └──────┘ └──────┘ └──────┘ └──────┘ └──────┘ └──────┘                                            │
│  late trains   kitchen fl…  blue hour …                                                            │
│  12 followers                                                                     ↕ 32 section gap │
│  Recently played artists                                                               (‹)  (›)    │
│  (  ○  ) (  ○  ) (  ○  ) (  ○  ) (  ○  ) (  ○  )    Artist                                         │
│  Following   See all                                                                   (‹)  (›)    │
│  (  ○  ) (  ○  ) (  JK ) ...   artists first, then people; no-photo person = initials on colour   │
│  Followers   See all                                                                   (‹)  (›)    │
└────────────────────────────────────────────────────────────────────────────────────────────────────┘
Scrolled past the hero → the 56-DIP band (one hairline, no fill):
│  Mira Vos      Playlists   Artists   Following   Followers                              Follow    │
│                ‾‾‾‾‾‾‾‾‾ 2-DIP accent underline = the spy's section                               │
```

**Own profile (Wide):** no Follow, and Top artists comes first.
```
│  ╭────────╮   Alex Moreau                                                                          │
│  │ 184 ○  │   1          18          30                                                            │
│  ╰────────╯   Follower   Following   Public playlists        [⇪] [⋯]                               │
├────────────────────────────────────────────────────────────────────────────────────────────────────┤
│  Top artists this month  [Only visible to you]                                         (‹)  (›)    │
│  1 (  ○  )   2 (  ○  )   3 (  ○  )   4 (  ○  )      StatHero rank in a 36-DIP column               │
│    Halcyon…    Mae Lorne   …                                                                       │
│  Public playlists   See all   …                                                                    │
```

**Medium (600–880; hero 300):** avatar 144, `ArtistTitle` 48/60 (≤ 2 lines), one stats row, gutter 32.
```
│  ╭──────────╮  Mira Vos                                                    │
│  │  144 ○   │  118        1,063       94                                   │
│  ╰──────────╯  Followers  Following   Public playlists                     │
│                [♡ Follow] [⇪] [⋯]                                          │
```

**Compact (360–600, stacked; hero 324):** avatar 112 over the copy; `ArtistCompactTitle` 32/40 on one line (auto-fits to 28, then ellipsis); one stats row with shrinking captions; gutter 16.
```
│  ╭───────╮                              │
│  │ 112 ○ │                              │
│  ╰───────╯                              │
│  Mira Vos                               │
│  118        1,063      94               │
│  Followers  Following  Public playlists │
│  [♡ Follow] [⇪] [⋯]                     │
```

**Narrow (< 360; hero 360):** avatar 96; stats in two explicit rows (2 + 1).
```
│  ╭──────╮                   │
│  │ 96 ○ │                   │
│  ╰──────╯                   │
│  Mira Vos                   │
│  118        1,063           │
│  Followers  Following       │
│  94                         │
│  Public playlists           │
│  [♡ Follow] [⇪] [⋯]         │
```

**Shimmer face (derived from `PageShimmer`; the same geometry as the loaded page):**
```
│░░░░░░░░░░░░░ (wash: held / neutral; tint holds — no colour flash) ░░░░░░░░░░░░░░│
│  ╭──────────╮                                                                    │
│  │ ███ bone │   ████████████████                    ← name bar(s) at 96 line box │
│  │  circle  │   ████   ████   ███                   ← numeral bars                │
│  ╰──────────╯   ████   ████   ███████               ← caption bars                │
│                 (blank 32-tall action row — Skeletonized(false))                 │
├──────────────────────────────────────────────────────────────────────────────────┤
│  ███████████████ (header bar)                                                    │
│  ┌▓▓▓▓┐ ┌▓▓▓▓┐ ┌▓▓▓▓┐ ┌▓▓▓▓┐ ┌▓▓▓▓┐   SEED surfaces at the fitted card width      │
│  ▄▄▄▄   ▄▄▄▄   ▄▄▄▄                                                             │
│  ███████████████ (second seed shelf)                                             │
```

**States:**
```
Unavailable (404 seal)         Failed (transport)                 Section error / empty
──────────────────────         ──────────────────                 ─────────────────────
This profile isn’t available   Something went wrong               Followers
It may be private, or the      <common error subtitle>            ──
account was closed.            [ Retry ]  (Standard button)       Something went wrong  [ Retry ]
                               → Entities.Refresh + RefreshEdge   Public playlists
                                                                  No public playlists yet
                                                                  Playlists you make public show up here.
```

---

## 8. Tests (U1)

**`Wavee.Tests/ProfilePageRulesTests.cs`**
```csharp
// ── Wavee.Tests/ProfilePageRulesTests.cs — the profile page's pure decisions (Entities/Profile.Rules.cs) ─────────────
using System.Globalization;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class ProfilePageRulesTests
{
    static readonly bool[] Bools = [false, true];

    // ── ProfileLoad ──
    [Fact]
    public void Invalid_IsUnavailable_WhateverTheMarks()
    {
        foreach (bool k in Bools) foreach (bool a in Bools) foreach (bool i in Bools) foreach (bool f in Bools)
            Assert.Equal(ProfileLoadState.Unavailable, ProfileLoad.Of(false, k, a, i, f));
    }

    [Fact]
    public void Known_IsReady_EvenWhileARefreshIsOutOrFailed()
    {
        foreach (bool a in Bools) foreach (bool i in Bools) foreach (bool f in Bools)
            Assert.Equal(ProfileLoadState.Ready, ProfileLoad.Of(true, true, a, i, f));
    }

    [Fact] public void Inflight_IsPending_EvenOverAFailureMark()
        => Assert.Equal(ProfileLoadState.Pending, ProfileLoad.Of(true, false, true, true, true));

    [Fact] public void FailureMark_NothingInflight_IsFailed()
        => Assert.Equal(ProfileLoadState.Failed, ProfileLoad.Of(true, false, false, false, true));

    [Fact] public void SealedAsk_NoFailure_IsUnavailable_The404()
        => Assert.Equal(ProfileLoadState.Unavailable, ProfileLoad.Of(true, false, true, false, false));

    [Fact] public void NeverAsked_IsPending()
        => Assert.Equal(ProfileLoadState.Pending, ProfileLoad.Of(true, false, false, false, false));

    [Fact]
    public void TheTruthTableIsExhaustive()
    {
        int seen = 0;
        foreach (bool v in Bools) foreach (bool k in Bools) foreach (bool a in Bools) foreach (bool i in Bools) foreach (bool f in Bools)
        {
            var expected = !v ? ProfileLoadState.Unavailable : k ? ProfileLoadState.Ready : i ? ProfileLoadState.Pending
                : f ? ProfileLoadState.Failed : a ? ProfileLoadState.Unavailable : ProfileLoadState.Pending;
            Assert.Equal(expected, ProfileLoad.Of(v, k, a, i, f));
            seen++;
        }
        Assert.Equal(32, seen);
    }

    [Fact]
    public void BodyReady_WaitsForTheMeasure_ThenLatches()
    {
        Assert.False(ProfileLoad.BodyReady(ProfileLoadState.Ready, measured: false, alreadyRevealed: false));
        Assert.True(ProfileLoad.BodyReady(ProfileLoadState.Ready, measured: true, alreadyRevealed: false));
        Assert.False(ProfileLoad.BodyReady(ProfileLoadState.Pending, measured: true, alreadyRevealed: false));
        Assert.True(ProfileLoad.BodyReady(ProfileLoadState.Failed, measured: true, alreadyRevealed: true));
    }

    // ── ProfileSections ──
    [Theory]
    [InlineData(EdgeState.Unknown, 0, ProfileSectionBody.Seed)]
    [InlineData(EdgeState.Partial, 0, ProfileSectionBody.Seed)]
    [InlineData(EdgeState.Complete, 0, ProfileSectionBody.Empty)]
    [InlineData(EdgeState.Failed, 0, ProfileSectionBody.Error)]
    [InlineData(EdgeState.Failed, 3, ProfileSectionBody.Cards)]
    [InlineData(EdgeState.Complete, 3, ProfileSectionBody.Cards)]
    [InlineData(EdgeState.Unknown, 3, ProfileSectionBody.Cards)]
    public void BodyOf_RowsWin_ThenFailedEmptySeed(EdgeState state, int count, ProfileSectionBody expected)
        => Assert.Equal(expected, ProfileSections.BodyOf(state, count));

    static ProfileFacts Facts(bool own = false, bool show = true,
        EdgeState top = EdgeState.Complete, int topN = 0, EdgeState pl = EdgeState.Complete, int plN = 3,
        EdgeState ar = EdgeState.Complete, int arN = 3, EdgeState fg = EdgeState.Complete, int fgN = 3,
        EdgeState fr = EdgeState.Complete, int frN = 3)
        => new(own, show, top, topN, pl, plN, ar, arN, fg, fgN, fr, frN);

    static ProfileSection[] Plan(in ProfileFacts f)
    {
        var into = new ProfileSection[ProfileSections.Count];
        return into[..ProfileSections.Plan(in f, into)];
    }

    [Fact]
    public void Own_Full_IsAllFive_InTheFixedOrder()
        => Assert.Equal(
            [ProfileSection.TopArtists, ProfileSection.Playlists, ProfileSection.RecentArtists, ProfileSection.Following, ProfileSection.Followers],
            Plan(Facts(own: true, topN: 10)));

    [Fact]
    public void Other_Minimal_IsPublicPlaylistsAlone_EvenEmpty()
        => Assert.Equal([ProfileSection.Playlists], Plan(Facts(show: false, plN: 0, arN: 0)));

    [Fact]
    public void TopArtists_IsOwnOnly_AndDropsOnFailureOrEmpty()
    {
        Assert.DoesNotContain(ProfileSection.TopArtists, Plan(Facts(own: false, topN: 10)));
        Assert.Contains(ProfileSection.TopArtists, Plan(Facts(own: true, top: EdgeState.Unknown, topN: 0)));
        Assert.DoesNotContain(ProfileSection.TopArtists, Plan(Facts(own: true, top: EdgeState.Failed, topN: 0)));
        Assert.DoesNotContain(ProfileSection.TopArtists, Plan(Facts(own: true, top: EdgeState.Complete, topN: 0)));
    }

    [Fact]
    public void People_HiddenWhenNotShown_AlwaysOnOwn()
    {
        Assert.DoesNotContain(ProfileSection.Following, Plan(Facts(own: false, show: false)));
        Assert.Contains(ProfileSection.Following, Plan(Facts(own: true, show: false)));
    }

    [Fact]
    public void People_EmptyDrops_FailedAndPendingStay()
    {
        Assert.DoesNotContain(ProfileSection.Followers, Plan(Facts(fr: EdgeState.Complete, frN: 0)));
        Assert.Contains(ProfileSection.Followers, Plan(Facts(fr: EdgeState.Failed, frN: 0)));
        Assert.Contains(ProfileSection.Followers, Plan(Facts(fr: EdgeState.Unknown, frN: 0)));
    }

    [Fact]
    public void RecentArtists_AbsentWhenTheOwnerHidesThem_OrTheAskFailed()
    {
        Assert.DoesNotContain(ProfileSection.RecentArtists, Plan(Facts(ar: EdgeState.Complete, arN: 0)));
        Assert.DoesNotContain(ProfileSection.RecentArtists, Plan(Facts(ar: EdgeState.Failed, arN: 0)));
        Assert.Contains(ProfileSection.RecentArtists, Plan(Facts(ar: EdgeState.Unknown, arN: 0)));
    }

    [Theory]
    [InlineData(HomeLoad.Pending, 0, EdgeState.Unknown)]
    [InlineData(HomeLoad.Failed, 0, EdgeState.Failed)]
    [InlineData(HomeLoad.Idle, 0, EdgeState.Complete)]
    [InlineData(HomeLoad.Ready, 0, EdgeState.Complete)]
    [InlineData(HomeLoad.Failed, 4, EdgeState.Complete)]
    [InlineData(HomeLoad.Pending, 4, EdgeState.Complete)]
    public void TopState_MapsTheFeedVerdict(HomeLoad load, int count, EdgeState expected)
        => Assert.Equal(expected, ProfileSections.TopState(load, count));

    [Fact]
    public void SeeAll_OnlyWhereTheListRouteHasMore()
    {
        Assert.True(ProfileSections.SeeAll(ProfileSection.Playlists, total: 94, shown: 10));
        Assert.False(ProfileSections.SeeAll(ProfileSection.Playlists, total: 10, shown: 10));
        Assert.True(ProfileSections.SeeAll(ProfileSection.Following, total: 1063, shown: 20));
        Assert.False(ProfileSections.SeeAll(ProfileSection.TopArtists, total: 50, shown: 10));
        Assert.False(ProfileSections.SeeAll(ProfileSection.RecentArtists, total: 50, shown: 10));
    }

    [Fact]
    public void Keys_AreUnique_AndCountMatchesTheEnum()
    {
        Assert.Equal(Enum.GetValues<ProfileSection>().Length, ProfileSections.Count);
        var keys = new HashSet<string>();
        for (int i = 0; i < ProfileSections.Count; i++) Assert.True(keys.Add(ProfileSections.Key((ProfileSection)i)));
    }

    // ── ProfileStats (the count labels) ──
    [Fact]
    public void Stats_Own_AreFollowersFollowingPlaylists_InOrder()
    {
        Span<ProfileStatCell> c = stackalloc ProfileStatCell[ProfileStats.Max];
        int n = ProfileStats.Plan(own: true, showFollows: false, 1, 18, 30, c);
        Assert.Equal(3, n);
        Assert.Equal(ProfileStat.Followers, c[0].Kind);
        Assert.Equal(ProfileStat.Following, c[1].Kind);
        Assert.Equal(ProfileStat.Playlists, c[2].Kind);
    }

    [Fact]
    public void Stats_HiddenFollows_LeaveOnlyPlaylists()
    {
        Span<ProfileStatCell> c = stackalloc ProfileStatCell[ProfileStats.Max];
        Assert.Equal(1, ProfileStats.Plan(own: false, showFollows: false, 118, 1063, 94, c));
        Assert.Equal(ProfileStat.Playlists, c[0].Kind);
    }

    [Fact]
    public void Stats_AZeroNeverLinks_ANegativeReadsZero()
    {
        Span<ProfileStatCell> c = stackalloc ProfileStatCell[ProfileStats.Max];
        ProfileStats.Plan(own: false, showFollows: true, 0, -4, 7, c);
        Assert.False(c[0].Links);
        Assert.Equal(0, c[1].Value);
        Assert.False(c[1].Links);
        Assert.True(c[2].Links);
    }

    [Theory]
    [InlineData(3, 1, 3)] [InlineData(3, 2, 2)] [InlineData(1, 2, 1)] [InlineData(2, 2, 1)]
    public void PerRow_SplitsTheBudget(int cells, int rows, int expected) => Assert.Equal(expected, ProfileStats.PerRow(cells, rows));

    [Fact]
    public void Numeral_GroupsInTheGivenCulture()
    {
        Assert.Equal("1,063", ProfileStats.Numeral(1063, CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("1.063", ProfileStats.Numeral(1063, CultureInfo.GetCultureInfo("nl-NL")));
        Assert.Equal("0", ProfileStats.Numeral(-1, CultureInfo.InvariantCulture));
    }

    // ── ProfileCover ──
    const string Hex40 = "ab6775700000ee85aabbccddeeff00112233aabb";

    [Fact] public void Cover_Https_PassesThrough()
        => Assert.Equal("https://x.example/a.jpg", ProfileCover.Of("https://x.example/a.jpg").Url);

    [Fact]
    public void Cover_ImageToken_AndBareId_ResolveToTheCdn()
    {
        Assert.Equal(ProfileCover.Cdn + Hex40, ProfileCover.Of(ProfileCover.ImagePrefix + Hex40).Url);
        Assert.Equal(ProfileCover.Cdn + Hex40, ProfileCover.Of(Hex40).Url);
    }

    [Fact]
    public void Cover_FourTileMosaic_ComposesAt300()
    {
        string id = "ab67616d0000b273" + "0123456789abcdef01234567";
        var art = ProfileCover.Of(ProfileCover.MosaicPrefix + id + ":" + id + ":" + id + ":" + id);
        Assert.NotNull(art.Tiles);
        Assert.Equal(4, art.Tiles!.Length);
        Assert.All(art.Tiles, t => Assert.Equal(ProfileCover.Cdn + ProfileCover.MosaicTileCode + "0123456789abcdef01234567", t));
        Assert.Equal(art.Tiles[0], art.Url);
    }

    [Fact]
    public void Cover_ShortMosaic_IsTheLeadTileAlone_GarbageIsNothing()
    {
        string id = "ab67616d0000b273" + "0123456789abcdef01234567";
        var two = ProfileCover.Of(ProfileCover.MosaicPrefix + id + ":" + id);
        Assert.Null(two.Tiles);
        Assert.NotNull(two.Url);
        Assert.Equal(default, ProfileCover.Of(ProfileCover.MosaicPrefix + "nope:nada"));
        Assert.Equal(default, ProfileCover.Of(null));
        Assert.Equal(default, ProfileCover.Of("   "));
        Assert.Equal(default, ProfileCover.Of("spotify:image:xyz"));
    }

    // ── ProfileTone ──
    [Fact]
    public void Tone_GradeableAvatar_LeadsWithNoPayload()
        => Assert.Equal(new ProfileToneSource("https://i.scdn.co/image/" + Hex40, 0u),
                        ProfileTone.Of("https://i.scdn.co/image/" + Hex40, 0x6D6CF7, gradeable: true));

    [Fact]
    public void Tone_NoOrUngradeableAvatar_IsTheProfileColourAsOpaquePayload()
    {
        Assert.Equal(new ProfileToneSource(null, 0xFF6D6CF7u), ProfileTone.Of(null, 0x6D6CF7, gradeable: false));
        Assert.Equal(new ProfileToneSource(null, 0xFFF573A0u), ProfileTone.Of("https://fb.example/a.jpg", 0xF573A0, gradeable: false));
    }

    [Fact]
    public void Argb_ZeroStaysNone_AndTheHighByteIsForced()
    {
        Assert.Equal(0u, ProfileTone.Argb(0));
        Assert.Equal(0xFF123456u, ProfileTone.Argb(0x00123456));
        Assert.Equal(0xFF123456u, ProfileTone.Argb(0x7F123456));
    }
}
```

**`Wavee.Tests/ProfileHeroLayoutTests.cs`**
```csharp
// ── Wavee.Tests/ProfileHeroLayoutTests.cs — the round-avatar hero's tiers → metrics (Entities/Profile.Rules.cs §3) ───
using FluentGpu.Dsl;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class ProfileHeroLayoutTests
{
    [Theory]
    [InlineData(380f, ArtistHeroTier.Narrow)] [InlineData(504f, ArtistHeroTier.Narrow)] [InlineData(610f, ArtistHeroTier.Compact)]
    [InlineData(700f, ArtistHeroTier.Compact)] [InlineData(850f, ArtistHeroTier.Wide)] [InlineData(1016f, ArtistHeroTier.Wide)]
    [InlineData(1064f, ArtistHeroTier.Medium)] [InlineData(300f, ArtistHeroTier.Compact)] [InlineData(1440f, ArtistHeroTier.Narrow)]
    public void Tier_IsTheArtistHerosTier_HysteresisIncluded(float width, ArtistHeroTier previous)
        => Assert.Equal(ArtistHeroLayout.TierFor(width, previous), ProfileHeroLayout.For(width, previous).Tier);

    [Theory]
    [InlineData(1440f, ArtistHeroTier.Wide, 380f)]
    [InlineData(700f, ArtistHeroTier.Medium, 300f)]
    [InlineData(500f, ArtistHeroTier.Compact, 324f)]
    [InlineData(320f, ArtistHeroTier.Narrow, 360f)]
    public void Heights_AreTheWorstCaseBudget(float width, ArtistHeroTier previous, float expected)
        => Assert.Equal(expected, ProfileHeroLayout.For(width, previous).Height);

    public static TheoryData<float, ArtistHeroTier> Tiers => new()
    {
        { 1440f, ArtistHeroTier.Wide }, { 700f, ArtistHeroTier.Medium }, { 500f, ArtistHeroTier.Compact }, { 320f, ArtistHeroTier.Narrow },
    };

    [Theory, MemberData(nameof(Tiers))]
    public void Height_IsPadsPlusTheBody_AndContainsTheCopy(float width, ArtistHeroTier previous)
    {
        var m = ProfileHeroLayout.For(width, previous);
        float copy = ProfileHeroLayout.CopyHeight(in m);
        float body = m.Stacked ? m.Avatar + ProfileHeroLayout.StackedAvatarGap + copy : MathF.Max(m.Avatar, copy);
        Assert.Equal(m.TopPad + body + m.BottomPad, m.Height);
        Assert.True(m.Height - m.TopPad - m.BottomPad >= (m.Stacked ? copy + m.Avatar : copy));
    }

    [Theory, MemberData(nameof(Tiers))]
    public void Collapse_EndsOnTheBand(float width, ArtistHeroTier previous)
    {
        var m = ProfileHeroLayout.For(width, previous);
        Assert.True(ProfileHeroLayout.CollapseDistance(in m) > 0f);
        Assert.Equal(ArtistHeroLayout.CompactIdentityHeight, m.Height - ProfileHeroLayout.CollapseDistance(in m));
    }

    [Fact]
    public void Avatar_Shrinks_StackedBelowMedium_NarrowAloneTakesTwoStatRows()
    {
        var w = ProfileHeroLayout.For(1440f, ArtistHeroTier.Wide);
        var md = ProfileHeroLayout.For(700f, ArtistHeroTier.Medium);
        var c = ProfileHeroLayout.For(500f, ArtistHeroTier.Compact);
        var n = ProfileHeroLayout.For(320f, ArtistHeroTier.Narrow);
        Assert.True(w.Avatar > md.Avatar && md.Avatar > c.Avatar && c.Avatar > n.Avatar);
        Assert.False(w.Stacked); Assert.False(md.Stacked); Assert.True(c.Stacked); Assert.True(n.Stacked);
        Assert.Equal(2, w.NameLines); Assert.Equal(2, md.NameLines); Assert.Equal(1, c.NameLines); Assert.Equal(1, n.NameLines);
        Assert.Equal(1, c.StatRows); Assert.Equal(2, n.StatRows);
    }

    [Theory, MemberData(nameof(Tiers))]
    public void Gutter_IsThePageGutter(float width, ArtistHeroTier previous)
        => Assert.Equal(ArtistHeroLayout.PageGutterFor(width), ProfileHeroLayout.For(width, previous).Gutter);

    [Theory, MemberData(nameof(Tiers))]
    public void Wash_CoversTheHeroPlusTheTail(float width, ArtistHeroTier previous)
    {
        var m = ProfileHeroLayout.For(width, previous);
        Assert.Equal(m.Height + ArtistHeroLayout.ContentBlendTail, ProfileHeroLayout.WashHeight(in m));
        float b = ProfileHeroLayout.WashBoundary(in m);
        Assert.True(b > 0f && b < 1f);
    }
}
```

---

## 9. File ownership: two parallel Sonnet implementers on disjoint files

| Implementer | Files (NEW unless noted) | Owns |
|---|---|---|
| **U1**: page host, states, rules, tests | `src/apps/Wavee/Entities/Profile.Rules.cs` | All pure types in §2. **This is the shared vocabulary; U2 reads it and never edits it.** |
| | `src/apps/Wavee/Entities/Profile.Page.cs` | §3: install, factory, `PageHost` (Render, Compose, Latch, cards from edges, band, spy, demand/ask/retry, stamp, accent, shimmer composition) |
| | `src/apps/Wavee/Entities/Artist.Page.cs` (EDIT) | One line: `Profile.InstallPages();` in `InstallPages()` |
| | `src/apps/Wavee.Tests/ProfilePageRulesTests.cs`, `ProfileHeroLayoutTests.cs` | §8 |
| **U2**: hero, sections, cards, loc | `src/apps/Wavee/Entities/Profile.UI.cs` | §4: `HeroText`, `HeroActs`, `HeroBanner`, Avatar/Identity/Stats/Stat/HeroActions, `MenuFor`, `SectionTitle`, `PivotLabel`, `SectionHeader`, `Shelf`, `SeedShelf`, `EmptyShelf`, `SectionError`, `Unavailable`, and the card adapters |
| | `src/apps/Wavee/assets/loc/en-US.json`, `nl.json`, `ko-KR.json` (EDIT) | §5: the `person` group only |

**The interface between U1 and U2.** U1 calls these `Profile` members, all `internal static` in U2's file, with exactly these signatures:
```csharp
internal readonly record struct HeroText(string Name, string? AvatarUrl, uint PersonArgb, int Followers, int Following,
                                         int Playlists, bool Own, bool ShowFollows, bool Placeholder)
{ public static HeroText For(User u, bool own, string? avatarUrl); public static HeroText PlaceholderFor(bool own); }
internal sealed record HeroActs(Action Share, Func<ContextMenuModel?> Menu, Action<ProfileStat> OpenStat);
internal static Element HeroBanner(in HeroText t, string uri, float width, in ProfileHeroMetrics m, bool compactCanHit,
                                   HeroActs? acts, Element? band);
internal static ContextMenuModel? MenuFor(User u, string name);
internal static string SectionTitle(ProfileSection s);
internal static string PivotLabel(ProfileSection s);
internal static Element Shelf(ProfileSection s, ProfileCard[] items, ColorF accent, Action? seeAll);
internal static Element SeedShelf(ProfileSection s, ColorF accent, float width);
internal static Element EmptyShelf(ProfileSection s, bool own);
internal static Element SectionError(ProfileSection s, ColorF accent, Action retry);
internal static Element Unavailable();
```
U2 reads from U1's `Profile.Rules.cs`: `ProfileHeroMetrics`, `ProfileHeroLayout.*` constants and the gap/collapse helpers, `ProfileStats.Plan/PerRow/Numeral/Max`, `ProfileStatCell`, `ProfileStat`, `ProfileSection`, `ProfileCard`, `ProfileCover.Of`, `ProfileTone.Argb`.

**Sequencing.**
- U1 and U2 start at the same time; both code against the signatures above.
- Neither runs a build or tests (fluentgpu SKILL hard rule).
- The orchestrator integrates after the data designer's edges/`ProfileAsk`/`User` members, the route designer's `RouteKind.User`/`Shell.For`/library arms and `ClaimsMaterial`, and the list designer's `ProfileListRoute`/`ProfileFacet` have landed.
- Then: Debug + Release build, plus `ProfilePageRulesTests`, `ProfileHeroLayoutTests`, `BootOrderingTests`, `ShellRoutesTests` and `LocCasingTests`.

---

## 10. Risks

1. **`ProfileAsk` shape.** I assumed `Row(bool)` / `Edge(EdgeState)` → `ProfileAskVerdict`. Only `AskRow` and `AskEdge` touch it.
2. **`ShowFollows` is a guessed wire field** (research §2: field 23/24 may be swapped). If the guess is wrong, the Following/Followers sections and counts vanish on other people's profiles, and their lists are never demanded. This needs the live check against U2 (a user with 118 followers).
3. **Tone deviation from the brief.** With a gradeable avatar the page leads with the avatar grading, and the brand colour is the payload only when there's no gradeable avatar. If the cold path (held colour, then graded) looks worse live than (brand colour, then graded), flip `ProfileTone.Of` to always pass `Argb(rgb)`. It's one line and the tests pin it.
4. **Marks must be in the stamp.** A 404 seal or a terminal failure bumps no `Version`. Without folding `Known/Asked/Failed/Inflight` and edge `Readiness`, the page would shimmer forever instead of showing Unavailable or Failed. This differs from the artist fold on purpose.
5. **`RouteKind.User` must be `ClaimsMaterial: true`.** Otherwise the content host also claims the material as neutral, and two effects fight over the tint in one flush.
6. **The `--fake` seed** must answer the me-row's Identity|Social|Follow and the four profile edges, or the own profile never leaves Pending.
7. **The `MenuFor` target uses `TargetKind.None`.** Copy link only needs `t.Uri` (`WebLinkOf` handles `/user/`). Pin/Unpin need the action resolution to derive the route via `Shell.For(user uri)`. U2 should confirm from the PinToSidebar adapter in `Actions.Table.cs`; if it doesn't resolve, the row is simply absent.
8. **Very large counts at the narrowest Compact width** (~336 DIP) shrink and ellipsize the stat cells rather than wrapping, because the height is pinned. This is accepted; Narrow uses two explicit rows.
9. **The worst-case hero height** leaves extra air above a one-line name. That's deliberate: content is bottom-aligned, so the sticky collapse needs no measurement.
10. **The Following preview** (20 cards, server order artists-first) will usually show artists only; the list route does the artists/people split.
11. **Keep-alive re-read** (`UseActivation` → `Reask`) costs one GET per back navigation. This matches the official client, and ProfileAsk makes it SWR, so there's no skeleton.
12. **Satellite loc keys:** a missing key in nl/ko-KR is a Warning, which `TreatWarningsAsErrors` turns into a build break, so U2 must add the whole `person` group to all three files.
13. **Mosaic tokens:** this assumes the mosaic parts are 40-hex image ids (the `ab67616d…` album-tile family). If they turn out to be album ids, a cover-less playlist shows a single tile or the watched placeholder. `ProfileCover` is idempotent over an https url the decoder may already have normalised.

### Critical Files for Implementation
- C:\wavee\waveemusic\src\apps\Wavee\Entities\Artist.Page.cs
- C:\wavee\waveemusic\src\apps\Wavee\Entities\Artist.UI.cs
- C:\wavee\waveemusic\src\apps\Wavee\Platform\Design.cs
- C:\wavee\waveemusic\src\apps\Wavee\Entities\Detail.UI.Hero.cs
- C:\wavee\waveemusic\src\apps\Wavee\Platform\Controls.cs

## Appendix R — Routes, click sources, list page (R1, R2)

# Profile lists ("See all") and routing: design

The design is complete and the codebase was read only; nothing was edited or built.

Two places where I depart from the brief:
- **Letter groups don't use `RepeatLayout.GridFit`.** That layout has no full-width header items. The library's own grid mode drops inline letters for this reason (User.Page.Library.cs `FirstRowOfLetter`: "A grid has no header items"). The follow lists instead use a flat projection of "letter header" and "row of N cards" items over `RepeatLayout.Extents` (the Recents and library-list pattern). Each card row is a fixed-height row of free-mode `Controls.Surface` cards; `CreateBound` doesn't provide `ItemsView.SlotRow`, so each card owns its own click, focus and menu.
- **The public-playlists facet shows at most 10, with a note** ("Showing 10 of 94"). Nothing in the capture fetched more than 10, so I didn't invent a second route. A probe of `playlist_limit=200` is a follow-up; if the server honours it, swapping to the full list is one line in the page.

## 0. Decisions

- **Route keys.**
  - `user:<user uri>` is `RouteKind.User`. The route's Arg is the display name. It claims material, is not keyed by Arg, and is a Detail surface.
  - `people:<facet digit>:<user uri>` is `RouteKind.ProfileList`, Discography-shaped: the user is the Subject, the `<digit>:<uri>` suffix is the Arg, and the facet stays in the key. It is keyed by Arg and placed by Arg, claims no material, and is a Detail surface.
  - Facets: Playlists = 0, Following = 1, Followers = 2.
- **Tab and history title** for a list route is the facet word ("Following"), from a new `Dest` arm.
- **Not pinnable in this pass.** `SidebarPinId.FromRoute` already refuses unrecognised prefixes. A pin row for a `user:` key has no projection for a title or avatar yet; a later sidebar wave adds a `user:` arm.
- **Jump list and omnibar include profiles**: `RecentSurfaces` includes `User` (the title comes from Arg), and so does `Omnibar.FromHistory`.
- **Page registration is a new lazy group** `{User, ProfileList}` that calls `ProfilePages.Install()`, the same miss-installs-once mechanism Home, Album and Playlist use.
- **Own profile goes through `ProfileRoute.For(User)`**, which builds the key with `Shell.Parse("user:" + uri)`, not `Shell.For`'s kind switch. That way `--fake`'s bare account row ("wavee-listener") still routes.
- **Search profile rows get no Follow trailing control.** A user's follow state isn't known on a search hit, so the button would say "Follow" for someone you already follow.
- **The Added-by column is deferred.** It lives in the bound track grid with no link seam; touching Track.Table isn't cheap.
- **Data path:** the page asks only `UserFields.Identity | Social`. It calls `EnsureEdge` on the current facet's relation while that relation is Unknown, and `InvalidateEdge` otherwise, so lists are re-read on every visit as the official client does. Retry uses `RefreshEdge`, plus `Entities.Refresh` on the user row when that row failed.

## 1. Routes: Shell.cs (R1)

```csharp
// enum: after Episode
Episode,
User, ProfileList,          // profile pages: the profile and its three unpaged lists
ConnectDiagnostics, CaptureDiagnostics, NotFound,

// s_routes: after the Episode row (declaration order == enum order)
new(RouteKind.User,        "user:",   true, Strings.ProfileList.Nav.Profile, Icons.Contact, false, true,  false),
new(RouteKind.ProfileList, "people:", true, Strings.ProfileList.Nav.Lists,   Icons.Friends, false, false, true, PlaceByArg: true),

// IsKnown — strict for the list (a bad digit or a missing uri is not a route)
if (route.Kind == RouteKind.ProfileList) return ProfileListRoute.TryParse(route, out _, out _);
return !row.IsPrefix || HasSubject(route.Subject) || (route.Kind == RouteKind.Discography && !route.Arg.IsEmpty);

// CarriesDisplayName: add  or RouteKind.ProfileList
// Dest: before the TitleLocKey fallback
if (route.Kind == RouteKind.ProfileList && ProfileListRoute.TryParse(route, out var facet, out _))
    return (Loc.Get(ProfileListRoute.FacetLabelKey(facet)), row.Glyph);

// Parse switch (rename DiscoSubject → FacetSubject: "the uri after the first ':'", shared)
RouteKind.ProfileList => new Route(RouteKind.ProfileList, FacetSubject(suffix), Intern(suffix), tab),
// NameOf
RouteKind.Discography or RouteKind.ProfileList => row.Key + Entities.Strings.Resolve(route.Arg),
// ArgOf
if (route.Kind is RouteKind.Discography or RouteKind.ProfileList) return null;
// For
EntityKind.User => RouteKind.User,
// IsEntityVerb
=> route is "album" or "pl" or "artist" or "show" or "prerelease" or "module" or "episode" or "user";
// TryParseSpotifyUri route switch (+ fix its summary: users now open)
EntityKind.User => "user",
// SurfaceOf Detail list: add  or RouteKind.User or RouteKind.ProfileList
// History.RecentSurfaces
if (r.Kind is not (RouteKind.Album or RouteKind.Playlist or RouteKind.Artist or RouteKind.Show or RouteKind.User)) continue;
// Omnibar.RouteFor (+ fix summary)
ItemKind.User when item.Uri.Kind == EntityKind.User => new Route(RouteKind.User, item.Uri, Intern(item.Title)),
// Omnibar.FromHistory kind switch
RouteKind.User => ItemKind.User,

// lazy group
static bool s_homeGroupInstalled, s_albumGroupInstalled, s_playlistGroupInstalled, s_profileGroupInstalled;
case RouteKind.User or RouteKind.ProfileList:
    if (s_profileGroupInstalled) return;
    s_profileGroupInstalled = true;
    ProfilePages.Install();
    break;
// RestorePagesForTests also resets s_profileGroupInstalled
```

## 2. `Entities/Profile.Route.cs` (R1, new, pure apart from the installer)

```csharp
namespace Wavee;

/// <summary>The facet a people:<facet>:<uri> route addresses. The value IS the key digit — never renumber.</summary>
public enum ProfileFacet : byte { Playlists = 0, Following = 1, Followers = 2 }

/// <summary>people:<digit>:<user uri> — DiscoRoute's shape. Subject = the user; Arg = "<digit>:<uri>".</summary>
public static class ProfileListRoute
{
    public const string Prefix = "people:";

    public static string Key(ProfileFacet facet, ReadOnlySpan<char> userUri)
        => string.Concat(Prefix, ((int)facet).ToString(CultureInfo.InvariantCulture), ":", userUri);

    public static bool TryParseArg(ReadOnlySpan<char> suffix, out ProfileFacet facet, out ReadOnlySpan<char> userUri)
    {
        facet = ProfileFacet.Playlists; userUri = default;
        if (suffix.Length < 3 || suffix[1] != ':' || suffix[0] is < '0' or > '2') return false;
        var uri = suffix[2..].Trim();
        if (uri.IsEmpty) return false;
        facet = (ProfileFacet)(suffix[0] - '0'); userUri = uri;
        return true;
    }

    public static bool TryParse(in Shell.Route route, out ProfileFacet facet, out EntityUri user)
    {
        facet = ProfileFacet.Playlists; user = default;
        if (route.Kind != Shell.RouteKind.ProfileList || route.Arg.IsEmpty) return false;
        if (!TryParseArg(Entities.Strings.Resolve(route.Arg), out facet, out _)) return false;
        user = route.Subject;
        return user.Id.Form != EntityForm.None;
    }

    /// <summary>Built through Shell.Parse so a key minted here and one from a deep link are the same route.</summary>
    public static Shell.Route For(EntityUri user, ProfileFacet facet) => Shell.Parse(Key(facet, user.Text));
    public static Shell.Route For(User u, ProfileFacet facet) => u.IsValid ? For(u.Uri, facet) : Shell.Route.None;

    public static string FacetLabelKey(ProfileFacet f) => f switch
    {
        ProfileFacet.Following => Strings.ProfileList.Facet.Following,
        ProfileFacet.Followers => Strings.ProfileList.Facet.Followers,
        _ => Strings.ProfileList.Facet.Playlists,
    };
}

/// <summary>The one "open this person" composer every click source uses (own row included: --fake's account
/// row is not a spotify:user: uri, so this goes through Parse, not Shell.For's kind switch).</summary>
public static class ProfileRoute
{
    public static Shell.Route For(User u)
        => u.IsValid ? Shell.Parse(string.Concat("user:", u.Uri.Text), u.Knows(UserFields.Identity) ? u.Name : default)
                     : Shell.Route.None;
}

public static class ProfilePages
{
    /// <summary>The {User, ProfileList} lazy group (Shell.InstallLazyGroupFor).</summary>
    public static void Install()
    {
        Shell.SetPage(Shell.RouteKind.User, static (in Shell.Route r) => ProfilePage.For(in r));     // page designer's factory
        Shell.SetPage(Shell.RouteKind.ProfileList, static (in Shell.Route r) => ProfileLists.Page(in r));   // R2
    }
}
```

`ProfilePage.For(in Shell.Route)` is the name I'm assuming for the profile-page agent's factory. If theirs differs, the orchestrator changes this one line.

## 3. Click sources (R1, exact edits)

1. **Search.UI.cs `OpenHit`.** Replace `if (hit.Kind == EntityKind.User) return;` with
   `if (hit.Kind == EntityKind.User) { Shell.GoTo(ProfileRoute.For(new User(hit.Slot)), origin); return; }`.
   The summary changes to "a profile opens its page". This covers profile hit rows and the top-result card body.
2. **Search.Rules.cs `CanOpen`.** Add `or EntityKind.User`. The top result then gets "Open page" for a profile, and still no Play.
3. **Playlist.UI.cs owner block (≈596-610).** In the `else` branch:
   ```csharp
   var owner = p.Owner; string name = NameOf(owner); var profile = ProfileRoute.For(owner);
   bool go = !profile.IsNone;
   lead = new BoxEl { /* existing Direction/Gap/AlignItems/Grow/Basis/MinWidth */,
       Role = go ? AutomationRole.Button : AutomationRole.None, Focusable = go,
       Cursor = go ? CursorId.Hand : CursorId.Arrow, OnClick = go ? () => Shell.GoTo(profile) : null,
       Children = [ /* PersonPicture as today */,
           Design.Type.TrackTitle(name) with { /* as today */, HoverColor = /* ContextBand.Link's hover ink */ } ] };
   ```
4. **Playlist.UI.cs members flyout (≈714).** `var member = u;` then `OnClick = () => { handle?.Close(); Shell.GoTo(ProfileRoute.For(member)); }`.
5. **Detail.UI.Hero.cs:448-453 (owner `TextEl`) and Detail.UI.cs `OwnerBlock` (≈1777).** When `id.Owner.Kind == EntityKind.User`, wrap in or extend the box:
   `Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, OnClick = () => Shell.GoTo(Shell.For(id.Owner, owner))`.
   Capture `var ownerUri = id.Owner;`.
6. **Rail.UI.cs `FriendRow`.** After `picture` is finalised:
   ```csharp
   var profile = ProfileRoute.For(user); Action openProfile = () => Shell.GoTo(profile);
   picture = new BoxEl { Shrink = 0f, Role = AutomationRole.Button, Cursor = CursorId.Hand, BlocksDragArm = true,
                         OnClick = openProfile, Children = [picture] };
   text[0] = new BoxEl { Direction = 0, MinWidth = 0f, Role = AutomationRole.Button, Focusable = true,
                         Cursor = CursorId.Hand, OnClick = openProfile, Children = [nameText /* + link hover ink */] };
   ```
   The row's own `OnClick` (the context) stays; the inner owner wins by the nearest-owner rule.
7. **Account flyout.**
   - Actions.Rules.cs: `enum ProfileRow { …, LogOut, Profile }`, appended because it isn't persisted.
   - `Rows(bool canPlay, bool actionsInMenu, bool hasNotifications, bool hasProfile)` builds `[Account, (Profile), Settings, …]`, matching the official client's order.
   - Shell.Overlays.UI.cs passes `hasProfile: User.Me.IsValid` and adds this case:
     `new MenuFlyoutItem(Loc.Get(Strings.ProfileList.Menu.Profile), Icons.Contact, Invoke: () => { close(); GoTo(ProfileRoute.For(User.Me)); })`.
8. **Docs.** `.claude/skills/wavee/deep-linking.md` gets two rows: `spotify:user:<id>` → Open `user`, and `wavee://open?route=user&arg=…`.

## 4. The list page (R2)

### 4.1 Pure rules: `Entities/Profile.Lists.cs`

```csharp
public enum ProfileListLoad : byte { Pending, Ready, Empty, Hidden, Unavailable, Failed }

public readonly record struct ProfileListFacts(bool UserValid, bool Unavailable, bool IsCurrentUser,
    bool FollowsKnown, bool ShowFollows, EdgeState Edge, int Count);

public static class ProfileListLoadRule
{
    /// Unavailable > Hidden > (rows present ⇒ Ready, even while a refresh failed) > Failed > Empty (Complete) > Pending.
    public static ProfileListLoad Of(ProfileFacet facet, in ProfileListFacts f)
    {
        if (!f.UserValid || f.Unavailable) return ProfileListLoad.Unavailable;
        if (facet != ProfileFacet.Playlists && f.FollowsKnown && !f.ShowFollows && !f.IsCurrentUser)
            return ProfileListLoad.Hidden;
        if (f.Count > 0) return ProfileListLoad.Ready;
        return f.Edge switch
        {
            EdgeState.Failed => ProfileListLoad.Failed,
            EdgeState.Complete => ProfileListLoad.Empty,
            _ => ProfileListLoad.Pending,
        };
    }
}

public enum ProfileChip : byte { All = 0, Artists = 1, People = 2 }

/// <summary>One entry of an unpaged list, resolved for display (engine-free: the page fills it off the tables).</summary>
public readonly record struct ProfileEntry(EntityKind Kind, int Slot, string Name, byte Flags, int Followers)
{
    public bool IsArtist => Kind == EntityKind.Artist;
    public int Letter => LibraryLetters.Of(Name);
}

public readonly record struct ProfileChipCounts(int All, int Artists, int People);

public static class ProfileListFilter
{
    public static ProfileChipCounts Counts(ReadOnlySpan<ProfileEntry> e)
    {
        int a = 0, p = 0;
        foreach (var x in e) { if (x.IsArtist) a++; else if (x.Kind == EntityKind.User) p++; }
        return new(e.Length, a, p);
    }

    /// THE display order: the library's a–z filing (LibraryLetters.Of: "the " skipped, non-A–Z → '#'),
    /// then the name OrdinalIgnoreCase, then artists before people, then slot (total, deterministic).
    public static int[] Order(ProfileEntry[] e)
    {
        var order = new int[e.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (x, y) =>
        {
            int c = e[x].Letter.CompareTo(e[y].Letter);
            if (c == 0) c = string.Compare(e[x].Name, e[y].Name, StringComparison.OrdinalIgnoreCase);
            if (c == 0) c = (e[x].IsArtist ? 0 : 1).CompareTo(e[y].IsArtist ? 0 : 1);
            return c != 0 ? c : e[x].Slot.CompareTo(e[y].Slot);
        });
        return order;
    }

    /// The visible subset in display order: the chip, then the trimmed query as a case-insensitive substring.
    public static int[] Apply(ProfileEntry[] entries, ProfileChip chip, string? query)
        => Apply(entries, Order(entries), chip, query);

    public static int[] Apply(ProfileEntry[] e, int[] order, ProfileChip chip, string? query)
    {
        var q = (query ?? "").AsSpan().Trim();
        var keep = new List<int>(order.Length);
        foreach (int i in order)
        {
            var x = e[i];
            if (chip == ProfileChip.Artists && !x.IsArtist) continue;
            if (chip == ProfileChip.People && x.Kind != EntityKind.User) continue;
            if (q.Length > 0 && !x.Name.AsSpan().Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            keep.Add(i);
        }
        return keep.ToArray();
    }

    /// "Word · 1,063", or the bare word while the count is unknown/0 (the library's ScopeWordText rule).
    public static string WordCount(string word, int count, bool known, CultureInfo c)
        => known && count > 0 ? word + " · " + count.ToString("N0", c) : word;
}

/// <summary>One flat item: a letter HEADER (Count 0) or a ROW of Count cards starting at Visible[Start].
/// Epoch makes a data landing a bound-item change (re-fires the slot) without a remount.</summary>
public readonly record struct ProfileRowItem(int Letter, int Start, int Count, bool HasArtist, uint Epoch)
{
    public bool IsHeader => Count == 0;
}

public static class ProfileGridFit
{
    public const float MinCell = 176f;                 // SectionScreen's SectionCardMinWidth
    public const float ActionLine = 32f + 8f;          // FollowToggle (32) + Spacing.S
    public const float StripMinWidth = 640f;
    public const float ToolsStackBelow = 640f;
    public static int Columns(float width, float gap) => width <= 0f ? 0 : Math.Max(1, (int)((width + gap) / (MinCell + gap)));
    public static float CellWidth(float width, int cols, float gap) => cols <= 0 ? 0f : MathF.Floor((width - (cols - 1) * gap) / cols);
    /// The card row's PINNED height — the surface's own grid estimate (≥ its rendered height), so seed == measured.
    public static float CardRow(float cellW) => SurfaceGeometry.GridRowEstimate(cellW, Shape.Grid, hasSubtitle: true);
    public static bool ShowsStrip(float width, bool lettered) => lettered && width >= StripMinWidth;
}

/// <summary>The grouped flat projection (LibraryLetters' twin for a card GRID): a header per letter, each letter's
/// cards chunked by `columns`. Offsets are prefix sums over PINNED extents, so the sticky letter and the jump never
/// drift. Reused; grows, never shrinks.</summary>
public sealed class ProfileLetterRows
{
    public const float HeaderExtent = LibraryLetters.HeaderExtent;   // 28 — the sticky overlay's own height
    ProfileRowItem[] _items = new ProfileRowItem[32];
    float[] _offset = new float[33];
    int[] _seq = new int[32];
    readonly JumpGroup[] _groups = new JumpGroup[LibraryLetters.Count];
    readonly Func<int, bool> _isHeader; readonly Func<int, int> _letterAt;
    int _flat, _groupCount; uint _present;

    public ProfileLetterRows() { _isHeader = f => _items[f].IsHeader; _letterAt = f => _items[f].Letter; }

    public int FlatCount => _flat;
    public uint Present => _present;
    public ReadOnlySpan<ProfileRowItem> Items => _items.AsSpan(0, _flat);
    public float OffsetOf(int flat) => _offset[Math.Clamp(flat, 0, _flat)];
    public float ExtentOf(int flat) => (uint)flat < (uint)_flat ? _offset[flat + 1] - _offset[flat] : HeaderExtent;
    public bool Has(int letter) => (uint)letter < LibraryLetters.Count && (_present & (1u << letter)) != 0;
    public int HeaderFlat(int letter) => JumpIndex.Resolve(_groups.AsSpan(0, _groupCount), letter);

    /// `letters`/`artist` are per VISIBLE entry, in display order (letters non-decreasing when lettered).
    public void Build(ReadOnlySpan<int> letters, ReadOnlySpan<bool> artist, int columns, bool lettered,
                      float cardRow, uint epoch)
    {
        columns = Math.Max(1, columns);
        Grow(letters.Length * 2 + LibraryLetters.Count + 1);
        _flat = 0; _present = 0; float off = 0f;
        for (int i = 0; i < letters.Length;)
        {
            int letter = lettered ? letters[i] : -1, end = i;
            while (end < letters.Length && (!lettered || letters[end] == letter)) end++;
            if (lettered) { _present |= 1u << letter; Push(new(letter, i, 0, false, epoch), HeaderExtent, ref off); }
            for (int s = i; s < end; s += columns)
            {
                int n = Math.Min(columns, end - s); bool a = false;
                for (int k = 0; k < n; k++) a |= artist[s + k];
                Push(new(letter, s, n, a, epoch), cardRow + (a ? ProfileGridFit.ActionLine : 0f), ref off);
            }
            i = end;
        }
        _offset[_flat] = off;
        for (int f = 0; f < _flat; f++) _seq[f] = f;
        _groupCount = JumpIndex.Project(_seq.AsSpan(0, _flat), _isHeader, _letterAt, _groups);
    }

    /// The letter whose band contains `offset` (binary search over the prefix sums); -1 above the first item.
    public int StickyLetterAt(float offset)
    {
        int lo = 0, hi = _flat - 1, hit = -1;
        while (lo <= hi) { int m = (lo + hi) >> 1; if (_offset[m] <= offset) { hit = m; lo = m + 1; } else hi = m - 1; }
        return hit < 0 ? -1 : _items[hit].Letter;
    }

    /// FNV-1a of the projection's GEOMETRY (letter, start, count, artist) — never the epoch: the list's remount key.
    public ulong Key() { ulong h = 14695981039346656037UL; for (int f = 0; f < _flat; f++) { var it = _items[f];
        h = (h ^ (uint)(it.Letter + 1)) * 1099511628211UL; h = (h ^ (uint)it.Start) * 1099511628211UL;
        h = (h ^ (uint)(it.Count * 2 + (it.HasArtist ? 1 : 0))) * 1099511628211UL; } return h; }

    void Push(in ProfileRowItem it, float extent, ref float off) { _items[_flat] = it; _offset[_flat] = off; _flat++; off += extent; }
    void Grow(int n) { if (_items.Length >= n) return; int s = Math.Max(n, _items.Length * 2);
        _items = new ProfileRowItem[s]; _offset = new float[s + 1]; _seq = new int[s]; }
}
```

### 4.2 UI: `Entities/Profile.Lists.Page.cs`

**Factory.**
`public static partial class ProfileLists { public static Element Page(in Shell.Route r) { string key = Shell.NameOf(r); return Embed.Comp(new PageProps(r, key), static () => new PageHost()) with { Key = "people-page:" + key }; } }`

**`PageHost : Component`.** Every delegate is cached in the constructor (the SectionScreen discipline).

*State it holds:*
- Signals: `_shape : Signal<ListShape>`, `_chip : Signal<ProfileChip>`, `_query : Signal<string>`, `_bodyW : Signal<float>` (quantised to 8 DIP), `_sticky : Signal<int>`, `_push : Signal<float>`, `_present : Signal<uint>`.
- Objects: `_letters : ProfileLetterRows`, `_handle : ScrollHandle`, `_ctl : ItemsViewController`, and a one-entry mount cache `(string key → RepeatLayout.Extents(_extentOf, 240f), ListOptions)`.
- `sealed record ListShape(ProfileListLoad Load, ProfileFacet Facet, ProfileEntry[] Entries, int[] Visible, ProfileRowItem[] Rows, string MountKey, ProfileChipCounts Counts, int Columns, float CellW, bool Lettered, int Total)`.
- `_rows = BoundItems.Project(_shape, s => s.Rows.Length, (s, i) => s.Rows[i], default)`.

*`Render()`, in this order:*
1. `UseProps`, then `UseContext(Shell.PageScrollScope)`.
2. Read `ScopeEpoch` and `Users.Changed`.
3. Parse with `ProfileListRoute.TryParse`. On a subject or scope change, rebind `_user = Entities.User(uri)`.
4. `_debounced = UseDebouncedValue(_queryFn, 150)`.
5. `UseEffect(_demand, DepKey.From(_user.Slot, (int)facet, (int)epoch))`.
6. `UseSignalEffect(_sync)`, then `UseSignalEffect(_watchLetters)`. All hooks run before the early return.
7. If the route didn't parse, return `Vacancy(Error)`.
8. Build the column: `Head()` (§5), then the region:
   ```csharp
   new SkelRegionEl(Pending: _pendingFn /* load Pending OR _bodyW == 0 */, Failed: _failedFn, Content: _contentFn,
     ShimmerSource: _shimmerFn, OnFailed: _failedPanelFn, Reveal: SkelReveal.Soft, Style: SkeletonStyle.Default,
     Group: this, SmoothResize: false)
   ```
   The region is wrapped in `BoxEl { Grow = 1, MinHeight = 0, OnBoundsChanged = _onBounds }`.
9. There's no outer `ScrollView`; the list owns the scroll.

*`Demand()`* reads only `ScopeEpoch.Value` and plain columns, so it never loops:
```csharp
var u = _user; if (!u.IsValid) return;
const UserFields head = UserFields.Identity | UserFields.Social;
if (!u.Knows(head)) Entities.Ensure(u, head);
var edge = EdgeOf(Entities.Current, _facet);
if (edge.State(u.Slot) == EdgeState.Unknown) Entities.EnsureEdge(FetchOf(_facet), u.Slot);
else Entities.InvalidateEdge(FetchOf(_facet), u.Slot, FetchPriority.Visible);   // re-read per visit; rows keep rendering
```

*`Retry()`*: if the user row `IsFailed(Social)`, call `Entities.Refresh(Users, slot, (uint)(Identity | Social))`. Always call `Entities.RefreshEdge(FetchOf(_facet), slot)`.

*`Sync()`* (a signal effect):
1. Read `Users / Artists / Playlists / EdgeOf(facet)` `.Changed`, plus `_chip`, `_debounced` and `_bodyW`.
2. Build `ProfileEntry[]` from `Targets`/`Payload`. The payload `Kind` picks the table: `Artist(slot).Name`, `User(slot).Name`, `Resolve(Playlist(slot).TitleId)`.
3. Compute the facts:
   - `Unavailable` = Asked(Social) ∧ Inflight == 0 ∧ ¬Known(Social) ∧ ¬IsFailed (Concert.Page's rule).
   - `FollowsKnown` = Knows(Social).
   - `ShowFollows`, `IsCurrentUser`, `Edge = edge.Readiness(slot)`.
4. Pick the visible set:
   - Playlists: server order, `lettered = false`.
   - Following: `Apply(entries, _chip, query)`.
   - Followers: `Apply(entries, All, query)`.
5. Set `listW = _bodyW − (ShowsStrip ? 18 + Spacing.S : 0)`, `cols = Columns(listW, Spacing.Card)`, then `_letters.Build(...)` with `CardRow(cellW)` and `epoch++`.
6. `MountKey = $"people:{facet}:{cols}:{_letters.Key():x16}"`.
7. Write `_shape`, `_present` and the load. If the sticky letter is no longer present, reset it to −1.

*`WatchLetters` and `Jump`* are verbatim the library's `WatchLetters` / `ProjectLetters` / `UpdateLetters` / `PushOf` (User.Page.Library.cs:905-962) over `_letters`, plus `_ctl.StartBringItemIntoView(_letters.HeaderFlat(letter), alignmentRatio: 0f)`. Absent letters resolve to −1 and the tap does nothing.

*Content (Ready).* A column containing `Tools()` and then a row:
- A ZStack with `ClipToBounds` and key `people:body`, holding:
  - `ItemsView.CreateBound(_rows, scope => Embed.Comp(() => new RowSlot(this, scope)), layout, opts) with { Key = shape.MountKey }`.
    - `opts`: `SelectionMode None, Selector None, IsItemInvokedEnabled = false, Controller = _ctl, Grow = 1, ContentType = i => Rows[i].IsHeader ? 1 : 0`.
    - `Scroll = new ScrollOptions { Handle = _handle, ScrollKey = scope + key, ItemClipTopInset = lettered ? 28 : NaN, ItemClipTopFadeBand = Detail.VerticalLayout.StickyFadeBand }`.
  - When lettered: `User.StickyLetter(_sticky, _push)`.
- `User.JumpStrip(_present, _sticky, _jump)` when `ShowsStrip`.

*`RowSlot(PageHost page, BoundItemScope<ProfileRowItem> scope) : Component`.* This is the Recents `RowSlot` idiom:
```csharp
var it = scope.Item.Value; var s = page.ShapeNow;
if (it.IsHeader) return new BoxEl { Height = ProfileLetterRows.HeaderExtent, Direction = 0, AlignItems = FlexAlign.End,
    HitTestVisible = false, Padding = new Edges4(Spacing.S, 0f, Spacing.S, Spacing.XS),
    Children = [User.LetterPlate(User.LetterText(it.Letter))] };
var cells = new Element[it.Count];
for (int k = 0; k < it.Count; k++) cells[k] = page.Cell(s, s.Visible[it.Start + k]);
return new BoxEl { Direction = 0, Gap = Spacing.Card, AlignItems = FlexAlign.Start,
    Height = page.Letters.ExtentOf(scope.Index.Peek()), Children = cells };   // PINNED: seed == measured
```

*`Cell(shape, entry)`* returns a fixed-width column `BoxEl { Key = "cell:" + uri, Width = s.CellW, Shrink = 0, Direction = 1 }` containing:
- `Controls.Surface(CardOf(e, s.CellW), Shape.Grid) with { Key = "card:" + uri }`.
- Only for an artist in Following, a 40-DIP centred line with `Embed.Comp(() => new Controls.FollowToggle { Uri = uri, Name = e.Name }) with { Key = "follow:" + uri }`. The uri freezes at mount, so the toggle is keyed on it.

*`CardOf`*:
- **User:** `new CardData(uri, name, Sub(text), art, () => Shell.GoTo(ProfileRoute.For(u)), Circular: true)`, with `CoverOverride = art is null ? PersonPicture.Create("", cellW − 16f, displayName: name) : null`. No menu, no play.
  - `text` is `Strings.ProfileList.Card.Followers(n)`, or `Card.Profile` when n is 0.
- **Artist and Playlist:** `var card = new HomeCard(new EntityRef(kind, slot))`, then `OnClick = HomeCardNav.Open(in card, null)`, `OnPlay = HomeCardNav.Play(in card)`, `Menu = HomeCardNav.MenuOf(in card)`, `Circular = artist`.
  - Artist subtitle: `Card.ArtistFollowers(n)`, or `Card.Artist` when n is 0.
  - Playlist subtitle: `Card.Followers(n)`, or the owner name when n is 0.
- `Sub(t) = Design.Type.TrackMeta(t) with { MaxLines = 1, Trim = CharacterEllipsis, MinWidth = 0f }`.

*`Tools()`* (Direction 1 below 640 DIP, otherwise 0, `Gap = Spacing.M`):
- **Following:** three `Controls.Chip(WordCount(word, n, true), selected, available: n > 0, () => _chip.Value = c)`, then `Controls.FindBox(_query, Loc.Get(Strings.ProfileList.Find.Following))`.
- **Followers:** `FindBox(…Find.Followers)` only.
- **Playlists:** `Design.Type.TrackMeta(Strings.ProfileList.Showing(shown, total))` only when total > shown.
- When the query narrows to zero, the content shows `Controls.Vacancy(VacancyVoice.NoMatch)` under the tools, which stay mounted so the query can be cleared.

**Circular seed (R2, shared file).** `SurfaceParts.Seed(in SurfaceShape s, float width, float height = NaN, bool circular = false)`. `StackSeed` uses `Radii.Full` corners when circular. `Controls.Surface` and `SurfaceHost`'s seed arm pass `d.Circular`, so `CardData.Seed with { Circular = true }` shimmers as a circle. **Overlap flag:** the profile-page agent may want the same change for its shelves; the orchestrator should keep one copy.

## 5. Head and states

- **Head** (always real, outside the region): `BreadcrumbBar.Create([name, facetTitle], i => { if (i == 0) Shell.GoTo(ProfileRoute.For(u)); })`, then `Design.Type.PageHero(facetTitle)` with one line, then `SelectorBar.Create(labels, new Signal<int>((int)facet), onChange: i => { if (i != (int)facet) Shell.GoTo(ProfileListRoute.For(u, (ProfileFacet)i)); })`.
  - Labels are `WordCount(facetWord, User.{PublicPlaylists|Following|Followers}, Knows(Social))`.
  - The name falls back to `Strings.ProfileList.FallbackName` while unknown.
  - `facetTitle` comes from `Strings.ProfileList.Title.*`.
- **Pending (shimmer):** tool bones (chips at 96×32 r16, find at 240×32 r4), then two non-virtual rows of `cols` (4 when unmeasured) fixed-width cells of `Controls.Surface(CardData.Seed with { Circular = facet != Playlists }, Shape.Grid)`. In Following, every other cell gets `Controls.FollowToggle.SkeletonShape()` below it. No stateful component mounts in the shimmer. Reveal is Soft, gated on `_bodyW > 0`.
- **Empty:** `Vacancy(Empty, title: Empty.{Playlists|Following|Followers})`. On your own Playlists the subtitle is `Empty.PlaylistsOwn`; otherwise the subtitle is "".
- **Hidden:** `Vacancy(Empty, title: Hidden.Title, subtitle: Hidden.Subtitle(name))`.
- **Unavailable:** `Vacancy(Empty, title: Unavailable.Title, subtitle: Unavailable.Subtitle)`, with no Retry.
- **Failed:** `Vacancy(Error, onAction: _retry)`.

## 6. Loc keys: group `profileList` (R2 owns all three files' `profileList` block)

| key | en-US | nl | ko-KR |
|---|---|---|---|
| nav.profile | Profile | Profiel | 프로필 |
| nav.lists | People | Personen | 사람 |
| menu.profile | Profile | Profiel | 프로필 |
| facet.playlists | Playlists | Playlists | 플레이리스트 |
| facet.following | Following | Volgend | 팔로잉 |
| facet.followers | Followers | Volgers | 팔로워 |
| title.playlists | Public playlists | Openbare playlists | 공개 플레이리스트 |
| title.following | Following | Volgend | 팔로잉 |
| title.followers | Followers | Volgers | 팔로워 |
| chip.all / artists / people | All / Artists / People | Alles / Artiesten / Personen | 전체 / 아티스트 / 사람 |
| find.following | Find in following | Zoeken in volgend | 팔로잉에서 찾기 |
| find.followers | Find in followers | Zoeken in volgers | 팔로워에서 찾기 |
| showing | Showing {shown} of {total} | {shown} van {total} weergegeven | {total}개 중 {shown}개 표시 |
| card.followers | {count, plural, one {# follower} other {# followers}} | {count, plural, one {# volger} other {# volgers}} | {count, plural, other {팔로워 #명}} |
| card.artist | Artist | Artiest | 아티스트 |
| card.artistFollowers | {count, plural, one {Artist · # follower} other {Artist · # followers}} | {count, plural, one {Artiest · # volger} other {Artiest · # volgers}} | {count, plural, other {아티스트 · 팔로워 #명}} |
| card.profile | Profile | Profiel | 프로필 |
| empty.playlists | No public playlists yet | Nog geen openbare playlists | 아직 공개 플레이리스트가 없습니다 |
| empty.playlistsOwn | Playlists you make public show up here. | Playlists die je openbaar maakt, verschijnen hier. | 공개로 설정한 플레이리스트가 여기에 표시됩니다. |
| empty.following | Not following anyone yet | Volgt nog niemand | 아직 팔로우하는 사람이 없습니다 |
| empty.followers | No followers yet | Nog geen volgers | 아직 팔로워가 없습니다 |
| hidden.title | Follows are private | Volglijsten zijn privé | 팔로우 목록이 비공개입니다 |
| hidden.subtitle | {name} hasn’t made their follows public. | {name} heeft deze lijsten niet openbaar gemaakt. | {name}님은 팔로우 목록을 공개하지 않았습니다. |
| unavailable.title | This profile isn’t available | Dit profiel is niet beschikbaar | 이 프로필을 사용할 수 없습니다 |
| unavailable.subtitle | It may be private, or the account was closed. | Het is misschien privé, of het account is gesloten. | 비공개이거나 계정이 해지되었을 수 있습니다. |
| fallbackName | Profile | Profiel | 프로필 |

## 7. Wireframes

```
WIDE (content ≈ 1100)                                                    
┌───────────────────────────────────────────────────────────────────────────┐
│ Mira Vos › Following                                     (BreadcrumbBar)  │
│ Following                                                (PageHero 28/36) │
│ [Playlists · 94]  [Following · 1,063]  [Followers · 118]  (SelectorBar)   │
│ (All · 1,063) (Artists · 897) (People · 166)   [⌕ Find in following    ]  │
│┌─[A]─────────────────────────────────────── (sticky plate) ──────────┐ A │
││ ( ◯ )   ( ◯ )   ( ◯ )   ( ◯ )   ( ◯ )   ( ◯ )                       │ B │
││ Ada Rhee Arden  Ana.k   Aster   Avi     Azul                        │ C │
││ Artist·… Artist ·12 fol Artist  Profile Artist                      │ … │
││ [♡ Follow][♡ Following]       [♡ Follow]      [♡ Follow]            │ Z │
││ [B]                                                                 │ # │
││ ( ◯ )   ( ◯ )   ( ◯ )   …   (fixed-height rows, list owns scroll)   │   │
│└─────────────────────────────────────────────────────────────────────┘   │
└───────────────────────────────────────────────────────────────────────────┘

NARROW (560 window, content ≈ 520): no jump strip, tools stack, 2 columns
┌──────────────────────────────────────────┐
│ Mira Vos › Followers                     │
│ Followers                                │
│ [Playlists · 94][Following · …][Follo…]  │
│ [⌕ Find in followers                  ]  │
│ [A]                                      │
│   ( ◯ )            ( ◯ )                 │
│   ana.k            Arno                  │
│   12 followers     Profile               │
│ [B] …                                    │
└──────────────────────────────────────────┘

SHIMMER (Pending; head is real, the region is the seed face)
│ Mira Vos › Following / Following / [Playlists][Following][Followers]     │
│ (▭▭▭▭) (▭▭▭▭) (▭▭▭▭)   [▭▭▭▭▭▭▭▭▭▭▭▭▭▭]                                │
│  ◯     ◯     ◯     ◯        circular seed covers + title/caption bars    │
│  ▭▭▭   ▭▭▭   ▭▭▭   ▭▭▭                                                  │
│ [♡▭▭]        [♡▭▭]          FollowToggle.SkeletonShape (Following only)  │
│  ◯     ◯     ◯     ◯                                                     │
```

Playlists facet: the same frame with square covers, no chips, no find box, no letters and no strip, plus the "Showing 10 of 94" note.

## 8. Tests

**R1: edits to existing tests**
- `ShellRoutesTests.Every_kind_has_its_own_row_at_its_own_index`: 33 → 35, with the comment updated.
- `ShellRoutesTests.The_nested_and_web_spotify_forms_are_refused…`: drop the `spotify:user:jane` line (the nested user-playlist form stays refused).
- `BootOrderingTests.Every_route_kind_resolves…`: 33 → 35, with the comment updated. The loop covers User and ProfileList through the new lazy group.
- `ShellOmnibarTests.Choosing_a_row_navigates…`: keep `RouteFor(User, default)` as IsNone; add `RouteFor(User, spotify:user:jane)` gives Kind User.
- `SearchFacetRulesTests.TheHitGatesFollowTheKind`: `(User, false, true, false, true)`.
- `SearchHitDataTests.AProfileRow_…`: add `Search.CanOpen(User)` is true, and fix the comment.
- `ActionMenuRulesTests`: every `Rows(...)` call gets `hasProfile:`. With it true, the expected arrays become `[Account, Profile, Settings, …]`. `Play_is_absent…` passes `hasProfile: false`, so `rows[1] == Settings` still holds.
- Before editing, grep Wavee.Tests for any exhaustive per-kind table (`RouteKindCount`, `RouteKind.Discography`) and add the two kinds there.

**R1: new facts in `ProfileListRouteTests.cs`**
- `User_row_is_a_material_prefix_that_carries_the_display_name`: key `user:`, IsPrefix, ClaimsMaterial, not KeyedByArg, and `Dest(user route, arg "Mira")` is "Mira".
- `A_bare_spotify_user_uri_opens_the_profile`: `DeepLink("spotify:user:jane")` gives Open with Kind User and NameOf `user:spotify:user:jane`.
- `Open_route_user_composes_the_key`: `wavee://open?route=user&arg=spotify:user:jane` gives Kind User.
- `Shell_For_a_user_uri_is_the_profile_route`.
- `ProfileList_round_trips_its_key`: Parse of `people:1:spotify:user:abc` gives Kind ProfileList, the user as Subject, `NameOf ==` the key, and `ArgOf` null.
- `ProfileList_refuses_a_bad_facet_or_missing_user`: `people:9:…`, `people:1:` and `people:x` are not IsKnown.
- `ProfileList_facets_are_distinct_places_and_slots`: `SameSlot` is false and `SlotKey` differs.
- `A_profile_list_tab_is_labelled_by_its_facet`: Dest gives the `facet.followers` text.
- `ProfileListRoute_For_equals_the_deep_link_route`.
- `ProfileRoute_For_the_fake_account_row_routes_even_without_a_spotify_uri`: Kind User and HasSubject.
- `Profile_routes_are_Detail_surfaces`.
- `Profile_routes_are_not_sidebar_pins_yet`: `SidebarPinId.FromRoute("user:…")` and `FromRoute("people:…")` are null.
- `RecentSurfaces_includes_a_visited_profile`, and `FromHistory_offers_a_visited_profile_as_a_User_item`.
- `The_profile_row_follows_Account_only_when_the_account_has_a_row`.

**R2: `ProfileListRulesTests.cs`**
- Load rule:
  - `Load_is_unavailable_for_an_invalid_or_sealed_user`
  - `Load_hides_another_users_follow_lists_once_ShowFollows_is_known_false`: own profile, Playlists facet and unknown Social are not Hidden.
  - `Load_is_pending_while_the_edge_is_unknown_or_partial_and_empty`
  - `Load_is_failed_only_with_nothing_to_show`
  - `Load_stays_ready_when_a_refresh_fails_over_present_rows`
  - `Load_is_empty_only_on_a_complete_empty_edge`
- Filter:
  - `Order_files_by_letter_then_name_then_artists_first`: "The Paper Hearts" files under P; "9 lives" and "Ålborg" file under #.
  - `Apply_Artists_keeps_only_artists`, `Apply_People_keeps_only_users`
  - `Apply_query_is_a_trimmed_case_insensitive_substring`
  - `Apply_all_and_empty_query_is_the_whole_ordered_list`
  - `Counts_split_by_kind`
  - `WordCount_appends_the_count_only_when_known_and_positive`
- Letter rows:
  - `Rows_open_one_header_per_letter_and_chunk_each_letter_by_columns`: A×5 and B×1 at 2 columns give `H(A),[0,2],[2,2],[4,1],H(B),[5,1]`.
  - `Rows_unlettered_chunk_straight_without_headers`
  - `Row_extent_adds_the_action_line_only_to_rows_holding_an_artist`
  - `HeaderFlat_resolves_through_JumpIndex_and_absent_letters_answer_minus_one`
  - `Present_has_one_bit_per_letter_shown`
  - `StickyLetterAt_follows_the_prefix_sums_across_boundaries`
  - `Key_tracks_geometry_not_the_epoch`
- Grid fit:
  - `GridFit_columns`: width 0 gives 0; 520 with gap 12 gives 2; 1200 gives 6; never below 1 once measured.
  - `ShowsStrip_only_when_lettered_and_at_least_640`

## 9. File ownership

**R1 (routes, click sources, their tests)**
- Production: `Shell/Shell.cs`, `Entities/Profile.Route.cs` (new), `Entities/Search.UI.cs`, `Entities/Search.Rules.cs`, `Entities/Playlist.UI.cs`, `Entities/Detail.UI.Hero.cs`, `Entities/Detail.UI.cs`, `Shell/Rail.UI.cs`, `Platform/Actions.Rules.cs`, `Shell/Shell.Overlays.UI.cs`, `.claude/skills/wavee/deep-linking.md`.
- Tests: `ShellRoutesTests`, `BootOrderingTests`, `ShellOmnibarTests`, `SearchFacetRulesTests`, `SearchHitDataTests`, `ActionMenuRulesTests`, `ShellNavTests` (the RecentSurfaces fact), `ProfileListRouteTests` (new).

**R2 (list page, rules, tests, loc)**
- Production: `Entities/Profile.Lists.cs` (new), `Entities/Profile.Lists.Page.cs` (new), `Platform/Surface.Parts.cs` and `Platform/Surface.Host.cs` (circular seed only).
- Loc: `assets/loc/{en-US,nl,ko-KR}.json`, the `profileList` block only.
- Tests: `ProfileListRulesTests.cs` (new).

**Interfaces between them**
- R2 consumes R1's `ProfileFacet`, `ProfileListRoute.{TryParse, For, FacetLabelKey}` and `ProfileRoute.For(User)`.
- R1 consumes R2's `ProfileLists.Page(in Shell.Route)` and the keys `Strings.ProfileList.{Nav.Profile, Nav.Lists, Menu.Profile, Facet.*}`.
- Both code against the data contract: `Entities.User`, `User.{Name, Followers, Following, PublicPlaylists, ShowFollows, IsCurrentUser}`, `Edges.Profile*`, `FetchEdge.Profile*`, `ProfileCardEdge`.

**Integration edits for the orchestrator**
- `User.UI.cs`: change `static Element LetterPlate` to `internal static`. That file likely belongs to the data agent (the user arms for the library seam), so don't hand it to R2.
- `ProfilePages.Install` names the page agent's factory; align that one line.
- Merge the circular-seed change if the page agent made the same one.

## 10. Risks

- **Field 24 (`show_follows`) is a guess** (research §2). If it's swapped with 23, Hidden would misfire. The rule only hides when Social is Known and the user isn't you, and a wrong Hidden is visible and easy to correct.
- **Focus on multi-card rows.** The slot root is still the list's roving tab stop; the cards are tab stops inside it, the same as Recents' card rows. A keyboard pass is needed in the live check.
- **Remounts while typing.** The list remounts when the grouping changes, as the library list does; the 150 ms debounce bounds it. Lists above about 1,000 entries rebuild in O(n log n) per settled keystroke, which is acceptable at the sizes seen (1,063).
- **Pinned row height.** `CardRow` uses the surface's grid estimate, which is at least the rendered height. If `SurfaceParts`' fluid grid geometry ever grows past it, the bottom of a card clips. A one-line fact pins `CardRow(w) ≥ w + 50`; R2 confirms the fluid card's true height against `GridCardChromeFor`.
- **Users without an avatar get a neutral initial.** `PersonPicture` doesn't paint the profile colour; using it is a follow-up that needs a `PersonPicture` colour parameter.

### Critical Files for Implementation
- C:\wavee\waveemusic\src\apps\Wavee\Shell\Shell.cs
- C:\wavee\waveemusic\src\apps\Wavee\Entities\Artist.Discography.cs (DiscoRoute and DiscoPageHost, the templates)
- C:\wavee\waveemusic\src\apps\Wavee\Entities\User.Page.Library.cs (letters watch, jump and sticky, the mount-key pattern)
- C:\wavee\waveemusic\src\apps\Wavee\Entities\User.cs (LibraryLetters) and C:\wavee\waveemusic\src\apps\Wavee\Entities\JumpIndex.cs
- C:\wavee\waveemusic\src\apps\Wavee\Home\SectionScreen.UI.cs (region, load-rule and own-scroll grid discipline)

## Appendix C — Podcast show page diagnosis (C, C-E)

I diagnosed all five bugs from source, design docs, the logs and a read-only query of the local library DB. The definite root causes are in the app's show-reader files; one, the card overlap, comes from a flex-wrap gap in the engine that a filter change triggers. I made no edits. The green line is the only item I could not trace to anything in the code.

**Evidence (log, DB, machine):**
- **Session.** The owner visited "Patrick Boyle On Finance" (`show:spotify:show:7uhrWlDvxzy9hLoW0EYf0b`, navIds 86/88/90) at 22:30:31–22:32:18 in `C:\Users\ChristosKarapasias\AppData\Local\Wavee\logs\wavee-20261001.log`. The file wraps (it starts at seq 17937); the session is at seq 12590–12782.
- **DB.** All 326 episodes have an image. Only 102 have a number. The show's `tone` is 0, so the page falls back to the cover accent, which is purple (`483890`/`271067`).
- **System accent** is blue (`0078D4`).

---

## 1. Filtering is inconsistent

**Where things live.** The filter, sort and find are signals on the page's `ReaderModel` (`Show.Page.cs:394-396`). `Compute()` reads them (`:470-471`) and uses them only to build the episode list (`:535-537`). The head's data comes from all episodes, never from the filtered list: ledger, resume pick, visit, head (`:524-530`), up-next and "new since" (`:565-571`). The head's change-detection hash (`:587-600`) and its render stamp (`Show.UI.cs:281`) don't include the filter or find, so the head never re-renders when you filter.

**What the design says:**
- The 0.2.9 parity rules say the "listen next" card is "picked from all episodes (never the filtered view)" (`docs/plans/wavee/wavee-0.3-ui/09-show-episode-module.md:40-43`).
- Parity checks 25–26 (`:1467-1469`): with **Played** selected the banner is "STILL present"; **Oldest** doesn't move it.
- In the prototype `podcast-show-episode-mica.html`, the list is filtered (`:543-545`) but the head is built from all episodes (`:561-568`).
- So "the head still shows unfiltered episodes" is by design.

**What is actually broken:**
- **Count says 1, list shows nothing.** The "don't repeat the hero's episode in the list" rule (`Show.Page.cs:542-544`, applied at `:548-549`) also runs while filtering. "why ger" matches only "Why Germany Stopped Working", which is the hero. So the list hides it, yet the count (`:613-614`) still includes it: you get "Episodes 1 of 326" over an empty list, with no "nothing matches" message.
- **Wrong scale.** The parity rule was written for a 72-DIP banner. The 0.3 head (hero + 3 small cards + new-since rows) is roughly 500 DIP. Under "Played" it pushes the one result below the fold and reads as part of the results.

**Proposed rule.** I suggest a "results mode": while a status filter or a search is active, the head is hidden. It still isn't filtered — it just isn't shown. Sort alone never hides it (parity #26). The hero's episode is only removed from the list while the head is shown.

```csharp
// ShowReaderRules (Show.Page.cs)
public static bool ResultsMode(Episode.Rules.Status s, ReadOnlySpan<char> find) => s != Episode.Rules.Status.All || !find.Trim().IsEmpty;
public static int HeadSlot(Head head, int resumeSlot, bool headShown) => headShown && head == Head.Returning && resumeSlot > 0 ? resumeSlot : -1;
```

- **Changes:**
  - In `Compute()`, compute `results` before `:544` and use `HeadSlot(head, resumeAt, !results)`.
  - Add a `HeadShown` field to `ReaderSnap` and mix it into the head hash.
  - Add `Shown` to `HeadStamp`. `VisitHead.Render` returns an empty `BoxEl` when hidden, so the head stays mounted at the same list position and its height simply corrects to 0.
- **Tests** in `Wavee.Tests\ShowReaderTests.cs`:
  - `HeadSlot` returns the resume episode only while the head is shown.
  - `ResultsMode` is true for a status filter or a search, never for sort.
  - When a search matches only the resume episode, the list contains exactly one row.
  - In results mode, the count equals the number of rows.
- **Decision for the owner.** The page remembers each show's filter, so someone who left "Unplayed" on lands in results mode next time. The rail's "Resume · N min left" button (`Show.Page.cs:1029-1030`) still covers continuing. If the owner prefers the head to always show, as the docs literally say, keep it and fix only the duplicate-removal rule.

## 2. Right-click does nothing; empty space where art should be

- **No menu is attached.**
  - The small up-next cards (`Show.UI.cs:581-594`) and the big resume card (`:509-517`) only have `OnClick`. Neither has `ContextMenu.Attach`.
  - The episode doors for new visitors have the same gap: `DoorData` has no menu field (`Controls.Podcast.cs:43-44`).
  - The "new since" rows *do* have a menu (`Show.UI.cs:615-616`, attached at `Episode.UI.cs:450-451`). Nothing is swallowing right-clicks; the menu simply isn't there.
- **The blank area is a numeral column, not a missing image.** The small card is designed as "numeral · title · meta, no cover" (`:549-551`). It always reserves a 34-DIP numeral column (`:561-565`), and the builder passes an empty numeral when the episode has no number (`:539,545`). Patrick Boyle's newest episodes are unnumbered (blank column); the one showing "35" carries wire episode number 35. The prototype's small card is also numeral-only (`.mini`, html `:224-226`); it assumed numbered shows.

**Fix.** Two options:
- **(a) Use the app's shared media row.** `Controls.Surface(Episode.RowData(e, new Episode.RowOptions(OnPlay: …PlayInShow(e), ShowGoToShow: false)), Shape.Row(40f))` gives art, hover, "…" and right-click in one, like Home does at `Home\Zones.UI.cs:840`.
- **(b) Keep the card, fix it.** Attach `Episode.Menu` the way Search (`Search.UI.cs:492`) and Recents (`Recents.UI.cs:264`) attach menus to their cards. Replace the numeral column with a pure rule `MiniLead(hasArt, number) → Art | Numeral | None`, draw a 40×40 artwork, and move "#35" into the meta line like the list rows do (`Episode.UI.cs:504`). Give the loading and failure placeholders the same 40-DIP lead. Tests: `(true,0)`→Art, `(false,35)`→Numeral, `(false,0)`→None.

Add a menu to the hero and to `DoorData` the same way.

## 3. Overlapping cards, and the green line

**Overlap — the engine's flex-wrap layout:**
- **Measure skips the children.** When the engine has a cached size for a node (`FlexLayout.cs:513-518`), it returns it without re-measuring the children. Those children keep their *last arranged* widths (`:465-474` documents this).
- **Arrange reuses stale widths.** `ArrangeWrap` decides line breaks from those stored widths (`:1581-1582`) and ignores the 220-DIP flex basis. The normal flex path uses the basis (`:812`).
- **Result.** When a wrap row shrinks while its contents are unchanged, the widths stretched to fill the old width now overflow. The last card drops to an extra line that the cached height never counted, and it draws over the next section. That is the "35" card under the "new since" header.

**What triggers it on this page:**
- **The year strip moves the list.** Filtering to "Played", or to the hero-only search, leaves at most one year, so the 30-DIP year strip is removed (`Show.Page.cs:896`). The list widens by 30; clearing the filter narrows it again.
- **The head never notices.** Its stamp ignores the filter, so its layout stays "clean" and the cached sizes are reused for both widths.
- **The cards' layout.** The cards sit in `Tiles` (`Show.UI.cs:155-161`), a wrapping row with 220-DIP minimum tiles.

**Fix:**
- **Engine.** In `ArrangeWrap` (`FlexLayout.cs:1576-1586`), get each child's base size from `Measure(c, availMain)` (cached, cheap) instead of its stored bounds, or exclude wrap nodes from the size cache. Add a wrap-layout test: three grow tiles laid out at 730 → 700 → 730 → 700 with nothing dirty; the next sibling must sit below the tiles every time.
- **App.**
  - Always reserve the strip's 30 DIP so filtering never reflows the list sideways.
  - Lay the cards and doors out in a computed number of equal columns, `Columns(contentWidth, 220, 12, 3)`, instead of a wrapping row.
  - Add a measured-vs-expected height warning like Artist reader's (`Artist.Reader.cs:866-884`). The show reader has none, which is why the logs show nothing.

**Green line — not found in source:**
- Nothing in the engine or app runtime paints pure green; the only greens are code-highlighting and test-suite colours.
- The scroll probe, evidence capture and FPS HUD draw no lines.
- The lines drawn in the show's colour or the accent — hero progress bar (`Show.UI.cs:488`), filter underline, player-bar top edge (`Shell.PlayerBar.UI.cs:591-599`) — would be purple or blue on this machine.
- Developer mode is on (the "lyrics debug" button shows in the 22:17 evidence frame), but nothing it gates draws a line. So this is not a leftover debug overlay.
- Since it sits at a fixed y alongside the overlap, a compositing seam is plausible, for example at the list's top clip band (48 + 24 DIP, `Show.Page.cs:921-922`). I can't confirm that from code. Next step: get the line's pixel y from the screenshot and compare it to the reader top + 48, the player-bar top and the 256-px tile boundaries; then reproduce with `--fg layout-verify` and an evidence capture.

## 4. Logs

- **ReuseGuard:** 0 hits in any log. It is compiled out of release builds (`ItemsView.cs:828-831`) and doesn't apply to this list anyway.
- **Scroll evidence:** no jump/clamp evidence for the show; the evening's evidence folders are album, home, playlist and artist pages.
- **Slow frames:**
  - Navigation frames were 19.5 / 32.6 / 14.5 ms. The page mount took 9.6 ms, of which the head was 5.84 ms.
  - Four idle frames of 11.5–13.8 ms were GPU capture/submit cost, not layout.
  - One scroll burst was marked Late (54 late presents).
- **Search typing (seq 12623–12669):** each keystroke re-renders the list and remounts the "More like this" section. That re-runs its network request: 11 identical POSTs to `pathfinder/v2/query` (231 bytes each) in about 7 s, 7 of them cancelled.

## 5. Toolbar is cramped (from your follow-up)

**Cause:**
- **Only the chips can shrink.** The toolbar row (`Show.UI.cs:180-218`) gives the chips a scrollable strip with `Grow 1, Shrink 1, Basis 0, MinWidth 0` (`Controls.Words.cs:257-263`). The search box is a fixed 180 DIP (`Show.UI.cs:238-241`), and sort, divider and select don't shrink. The chips therefore give up space first and get clipped ("In progres").
- **The breakpoints are wrong.** They are fixed constants: collapse search below 640, fold sort below 480 (`Show.Rules.cs:474-485`). They were computed assuming 16-DIP side padding and about 280 DIP of chips (`:464-469`). At full width the padding is 24 (`Show.UI.cs:45,189`), and the width used is the whole reader, including the 30-DIP year strip (`Show.Page.cs:847,896`).
- **Real need.** About 48 + ~300 (chips) + 64 (gaps) + 180 + 1 + ~100 (sort) + 30 ≈ 723 DIP of toolbar, about 753 including the strip. Between 640 and ~753, the chips clip.

**Proposal.** Measure instead of guessing, following the album/playlist command bar's existing rule (`CommandBarLayout.Resolve`, `Track.Rules.cs:688-760`, with 16-DIP hysteresis):

```csharp
public readonly record struct ToolbarNeeds(float Filters, float Sort, float SelectedWord);
public static ToolbarStage For(float railWidth, in ToolbarNeeds n, bool narrowPad, ToolbarStage? previous = null);
// Full → FindIcon → CompactSort → FilterMenu
```

- **Chips never shrink.** They become a natural-width row (no scrolling strip) at every stage except the last, where they fold into a "selected filter ▾" menu (the narrowest arm in `podcast-refinements-implementation.md:9-16`).
- **The chips' width comes from the chip rail.** It already tracks each word's position and width (`Controls.Words.cs:197-200`); expose the total as a callback or signal on `Controls.Words.Rail`.
- **Measure the toolbar itself**, not the whole reader.
- **Search open at a collapsed stage:** the field takes over the sort/select area and the chips stay. A second toolbar row is possible, since the list's clip can be updated live (`Track.Table.cs:1133-1145`), but the owner rejected the earlier three-row toolbar (`Show.Rules.cs:461`), so offer it only for an open search.
- **Ctrl+F:** the contract says it stays the global search box (`podcast-show-rework-implementation.md:240`), so don't bind it here.
- **Keep search in the sticky toolbar.** The album/playlist pages keep Find in their sticky band (`Track.Table.Chrome.cs:374-398`). The "Episodes N" heading scrolls away, and the left rail is for identity and actions.
- **Tests** to replace `PodcastShowToolbarTests`:
  - Full at the exact fitting width.
  - Search collapses before the chips: for every width from 300 to 1600, chip room ≥ chip width unless at the menu stage.
  - Moving to a richer stage needs 16 DIP of headroom.
  - An unmeasured width reads as Full.

```
Wide         All 326  Unplayed 320  In progress 5  Played 1   [⌕ Find in this show ]  | Newest Oldest  [✓]
Medium       All 326  Unplayed 320  In progress 5  Played 1   ⌕  | Newest Oldest  [✓]
Med + find   All 326  Unplayed 320  In progress 5  Played 1   [⌕ why ger          x]
Narrow       All 326  Unplayed 320  In progress 5  Played 1   ⌕  ⇅  [✓]
Narrowest    [Unplayed 320 v]                                 ⌕  ⇅  [✓]
```

## Other defects on this page

1. **"More like this" refetches on every filter or keystroke.** The request lives in the footer component (`Episode.Reader.cs:95-96`), which remounts whenever the list changes (`Show.Page.cs:972`). Hoist it onto the page model or cache it by show.
2. **Up-next and "new since" show the same episodes.** "Is Anthropic Worth Two Trillion Dollars?" and "The Doomsday Cult Inside OpenAI" are the two newest unplayed episodes and both came out after the last play (2026-09-19), so they appear in both blocks. The prototype has the same overlap (html `:535-536` vs `:562`), so this needs a decision, e.g. exclude "new" episodes from up-next for non-serial shows.
3. **Year strip flicker.** It appears and disappears with filters, shifting the whole list 30 DIP sideways (`Show.Page.cs:896`).
4. **Hero card progress isn't live.** Its "N min left" and progress bar are fixed when built (`Show.UI.cs:462-463, 488`), while list rows follow the playback clock.
5. **Height estimates.** Every list item starts at a 96-DIP estimate (`Show.Page.cs:681`), but the head is ~500+ and the footer ~400, which makes scroll position restore imprecise. Artist reader seeds real heights per item type (`Artist.Reader.cs:377`).
6. **"More like this" cards have no menu** (`ShowMenu: false`, no menu set, `Episode.Reader.cs:112-114`).
7. **"Nothing matches" reset leaves the narrow search field open.** Its reset (`Show.Page.cs:420`) clears the filter and text but not the open-field flag.