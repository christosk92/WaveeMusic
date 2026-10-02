# Interaction consistency — verdict and plan

Status: **approved 2026-10-02; implementation in progress. §7 (the verified plan) supersedes §4–§6 where they differ.** Written 2026-10-02 from four read-only code audits (track
rows · cards and hero rows · selection/menus/toolbars/drag · flyouts and overlays). Every finding below carries the
file:line the auditors read; paths are under `src/apps/Wavee/` unless an engine path is given. The owner's reports that
started it: the artist page's Latest release row is not clickable and has a separate "View" button; search-flyout rows
have no right-click and no "…"; Top tracks has no selection bar; Ctrl+click plays on an album; the album drawer shows a
"1 selected · Play" pill instead of the standard selection bar.

## 1. Verdict

The app already **designed** one interaction contract (`docs/plans/wavee/wavee-0.3-ui/01-track-row.md` §6.1,
`04-detail-track-table.md` §0.1, ch 29 §2 "one menu vocabulary, never a second implementation",
`shared-media-surface-implementation.md`) and already **has** the shared pieces to deliver it (`Controls.Surface`,
`Controls.SelectionBar`, the engine `SelectionModel` behind `ItemsView`, `Track.RowMenu`, `Actions.Menu`, `Drag.Source`).
The inconsistencies are drift: surfaces that hand-build what a shared piece already does, and five places where a policy
is open-coded instead of decided once. Nothing here needs a new design; it needs the existing contract enforced by
construction — one helper per decision, every surface routed through it, and the hand-built copies deleted.

Root causes, in order of how many symptoms each explains:

1. **No click-policy owner.** What a click does is whatever the surface's list control does. `ItemsView` (engine,
   `FluentGpu.Controls/ItemsView.cs:395-399, 1451-1478`) invokes on Tap when `SelectionMode.None` and on any DoubleTap /
   Enter regardless of modifiers; six call sites open-code `ClickCount >= 2 → DoubleTap` and pass `args.Mods` through
   (`Entities/Track.Table.cs:2032`, `Entities/Artist.Discography.cs:1306`, `Entities/Artist.UI.Chart.cs:533`,
   `Platform/Surface.Bound.cs:49`, `Entities/User.UI.cs:587`, `~590`). Only the sidebar strips modifiers
   (`Shell/Sidebar.UI.Rows.cs:299`). **This is why Ctrl+click plays**: two Ctrl-clicks inside 500 ms toggle the row on,
   off, then invoke.
2. **Three track-row families.** (A) `Track.TableHost` → `ItemsView` (playlist, liked, album, show lists) — the only one
   with real selection, check lane, keyboard and the standard bar. (B) hand-rolled rows with partial copies of selection:
   artist Top tracks (`Artist.UI.Chart.cs:85, 495-534`), the album drawer (`Artist.Discography.cs:1184, 1294-1337`),
   `Track.EagerRow` (`Track.UI.cs:1049-1143`). (C) `Controls.Surface` rows, which refuse selection by design
   (`Surface.Rules.cs:19-21`).
3. **The selection bar is composed four times** around the one chrome `Controls.SelectionBar`
   (`Platform/Controls.cs:1107`): the table's `SelectionCommands` (`Track.Table.Chrome.cs:1273`, the canonical one),
   the drawer's reduced copy (`Artist.Discography.cs:1366`), `Episode.Selection.cs:144`, `Show.Page.cs:1007`. Thresholds
   differ (table: ≥2, or ≥1 with Select armed — `Track.Table.cs:930-931`; drawer and Show: `minCount: 1`). Top tracks has
   selection and **no bar at all**.
4. **Hand-built hero rows and tiles** that skip `Controls.Surface`: Latest release (`Artist.Page.cs:1727-1769`), Watch
   the official video (`Album.Page.cs:1327-1376`), About the artist (`Album.Page.cs:1210-1267`), Search top result
   (`Search.UI.cs:420-497`), the search flyout rows (`Shell/Shell.Masthead.UI.cs:572-614`), Recents group cards
   (`Recents.UI.cs:195-268`), Podcast doors (`Controls.Podcast.cs:272-370`), Album "Fans also like" chips
   (`Album.Page.cs:1175-1200`).
5. **Container menus are built five ways** (`Browse.Cards.cs:215` `CardMenu` → `HomeCardNav.MenuOf`,
   `Artist.Discography.cs:1146`, `Artist.Page.cs:1420`, `Recents.Page.cs:607`, sidebar `Sidebar.UI.Menus.cs:248-352`),
   and hero "…" menus differ again (`Album.Page.cs:391` and its duplicate `Album.UI.cs:522`, `Playlist.Page.cs:701`,
   `Show.Page.cs:299`, `Show.Pane.cs:227`, `Profile.UI.cs:257`). The same album offers different verbs on different pages.
6. **The container play verb never pauses.** Card FABs flip to a Pause glyph when their context plays
   (`Controls.Art.cs:289-297`), but almost every container adapter calls `Playback.PlayContext` (`HomeCardNav.Play`,
   `Browse.Cards.cs:161-179`; `Artist.Page.cs:1198/1290/1390`; `Album.Page.cs:1100`; `Profile.UI.cs:431`), which has no
   same-context toggle (`Playback.Host.Context.cs:89-118`) — the "pause" click restarts the context. Only `Track.Invoke`,
   `Episode.Invoke` and `Modules.PlayOrToggle` (`Modules.UI.cs:115`) toggle. *Needs one live check before the fix.*

## 2. The contract (what every surface answers)

| Gesture | Selection-backed list (track/episode tables, Top tracks, album drawer) | Preview list (queue, rail Next up, Recents, Home top tracks, search hits) | Card / tile / hero row |
|---|---|---|---|
| Single click | **select + focus** (plain replaces, Ctrl toggles, Shift extends from anchor) | play (queue: skip to row) | **open** the entity (the whole surface; never a separate View/Open button) |
| Double click (no Ctrl/Shift held) / Enter | play | play | open |
| Ctrl/Shift + click | extend selection — **never play** | — | — |
| Space · Ctrl+A · Esc | toggle · select all (2nd press clears) · clear | — | — |
| `#` cell / FAB | play/pause **toggle** | play/pause toggle | play/pause toggle (container contexts too) |
| Right-click · Menu key · Shift+F10 · "…" | one entity menu, byte-identical; Explorer semantics inside a selection | same | same |
| "…" visibility | hover/focus-revealed trailing lane, everywhere a row has a menu | same (rail/queue included) | hover-revealed corner/trailing |
| Selection UI | the standard `SelectionBar` composition, inline where a toolbar exists, the same composition floating elsewhere; shown at **≥2 selected, or ≥1 with Select armed** | — | — |
| Drag | the whole selection when the dragged row is in it, else the row | the row | the entity |
| Truncated title | tooltip with the full text | same | same |
| Text | one separator `" · "`; one kind marker (no pill **and** kind word) | same | same |

Sanctioned deviations (kept, documented): the sidebar (navigation tree: plain click navigates, its own rootlist
selection and drag); the discography grid card toggling its drawer (gains a caret so it is discoverable); queue rows
skipping rather than replacing the context.

## 3. Ranked inconsistencies (owner-visible first)

| # | Surface | Today | Fix (canonical piece) |
|---|---|---|---|
| 1 | Ctrl+click on any table/drawer/chart row | plays on the second click | `RowClickPolicy.Trigger` (§4.1) at all six sites |
| 2 | Album **single** | single click plays; Ctrl+click plays; no selection (`Detail.cs:1439` `Selection = None`, doc item 72) | **D1** — recommend Extended like Album |
| 3 | Artist **Top tracks** | selection with no bar, no check lane, no "…", no keyboard (`Artist.UI.Chart.cs`) | host on `ItemsView` (or the shared row wrapper) + the standard bar (§4.2) |
| 4 | Album **drawer** | own `SelectionModel`, pill at 1, reduced commands, no keyboard (`Artist.Discography.cs:1184-1393`) | same as #3 |
| 5 | Search **flyout** rows | hand-built; no menu, no "…", no drag, static play glyph; "Song - Will Smith"; kind twice | `Controls.Surface` + `Search.UI.HitData/MenuOf/DragOf`; `Omnibar.Item` carries an `EntityRef` (`Shell.cs:2084`); one subtitle builder (`Spotify.Decode.Browse.cs:1179-1193`) |
| 6 | Artist **Latest release** | not clickable, no menu/focus, "View" button, Play never pauses (`Artist.Page.cs:1727-1769`) | `Controls.Surface(album CardData, Shape.Row(72))` or `Album.Page.AlbumRow`; delete View |
| 7 | Container card **Play/Pause** | pause glyph restarts the context | `Playback.PlayOrToggleContext` (§4.3) in every adapter |
| 8 | Album **Watch the official video** | hand-built Video shape, "…" visible at rest (0.45) | `Controls.Surface(VideoData, Shape.RowLarge with { Play = Always })` |
| 9 | Album **About the artist** | not focusable; Follow nested inside the clickable | extract the right-rail version (`Shell/Rail.UI.cs:592-675`) into one `Controls.ArtistAboutCard` |
| 10 | Search **Top result** | separate "Open page" button; Play never pauses; "…" at rest | drop Open page; Surface rules; toggle play |
| 11 | Same entity, different menu | five container builders + hero menus | one `Menus.Container(kind, target, extras)` over `Actions.Menu` (§4.4); delete `Album.UI.MoreMenu` duplicate |
| 12 | "…" missing | queue + rail rows (`ShowMenu:false`, `Queue.UI.cs:729`, `Rail.UI.cs:840`), Top tracks, EagerRow without `set.Actions`, module cards, Artist pick, Concert promo | route through `SurfaceRules.ShowsMenuTrailing/Corner`; no caller-side `ShowMenu:false` |
| 13 | Keyboard dead zones | drawer, chart, EagerRow, Recents cards, Album About not tab stops → no Enter/Menu key | follows from #3/#4/#9/#14 |
| 14 | Recents page group cards | hand-rolled clone of `Shape.Row`, not focusable | `Controls.Surface(RowData, Shape.Row(..))` + trailing cells |
| 15 | Show episodes | plain click opens the episode, Ctrl/Space selects | **D2** |
| 16 | Album **Fans also like** | 48-high pill chips with no menu/drag vs artist page's circular cards | `Shape.Shelf` circular (or `Shape.Row` + `Circular`) on both pages |
| 17 | Selection-aware drag | three copies (`Track.Table.cs:2022`, `Artist.Discography.cs:1333`, `Artist.UI.Chart.cs:~476`); EagerRow/Episode rows not draggable | one `SelectionDragPayload` helper; Eager/episode rows draggable |
| 18 | Separators | `" · "`, `" • "` (`Search.UI.cs:290`), `"  •  "` (`Rail.UI.cs:975`), dashes (`Concert.Rules.cs:460`, `Recents.cs:662`, flyout) | one `Text.Sep` constant |
| 19 | Friends rail · notifications | entity rows with no menu | `WithContextMenu(Menus.For(target))` |
| 20 | Player bar | heart tooltip only for episodes (`Shell.PlayerBar.UI.cs:404`); no "…"; title has no tooltip | tooltip + a hover "…" opening `Stage.NowPlayingMenu` |
| 21 | Podcast door, Artist pick, Daylist | play never shows paused; door disc always visible; pick has no right-click | `NowPlayingOverlay` + toggle play + `ContextMenu.Attach` |
| 22 | Page toolbars | different vocabulary and order (labelled buttons / words / circles); Album has two Shuffles (`Album.UI.cs:650`) | one order (Play next · Shuffle · Sort · Row size · Select · "…" · Find) where it applies; delete the duplicate Shuffle |
| 23 | Ctrl+A selects persistent-prefix items | engine `SelectAll` includes hero/headers; bar's Select all filters, keyboard doesn't | filter in the shared selection helper |

## 4. New single owners (engine-free, unit-tested; shapes, verified against code in the WP briefs)

### 4.1 `RowClickPolicy` (`Platform/RowClickPolicy.cs`)
```csharp
/// <summary>THE click decision for every row and card. A double click with Ctrl or Shift held is a selection gesture,
/// never an invoke; a selection-less list invokes on a plain tap.</summary>
public static class RowClickPolicy
{
    public enum Verb : byte { None, Select, Toggle, Extend, Invoke, Open }
    public enum ListKind : byte { SelectionBacked, Preview, Card }

    public static Verb Decide(ListKind list, int clickCount, KeyModifiers mods)
    {
        bool ctrl = (mods & KeyModifiers.Control) != 0, shift = (mods & KeyModifiers.Shift) != 0;
        return list switch
        {
            ListKind.SelectionBacked when shift => Verb.Extend,
            ListKind.SelectionBacked when ctrl  => Verb.Toggle,
            ListKind.SelectionBacked            => clickCount >= 2 ? Verb.Invoke : Verb.Select,
            ListKind.Preview                    => Verb.Invoke,
            _                                   => Verb.Open,
        };
    }

    /// <summary>The ItemsView trigger for a press: a modified double click is a Tap (it extends), never a DoubleTap.</summary>
    public static ItemContainerTrigger TriggerOf(int clickCount, KeyModifiers mods)
        => clickCount >= 2 && (mods & (KeyModifiers.Control | KeyModifiers.Shift)) == 0
            ? ItemContainerTrigger.DoubleTap : ItemContainerTrigger.Tap;
}
```
Tests: every (list, clickCount, mods) cell of §2; `TriggerOf(2, Ctrl) == Tap`.

### 4.2 One selection bar composition (`Entities/Track.Selection.cs`)
`SelectionCommandSet.For(SelectionSource src)` — covers, "N selected", Play, Play next, Add to queue, Like,
Select all, "…" (`TableSelectionMore`), ✕ — over `SelectionVerbs` (`Track.Rules.cs:1640`); track, episode and mixed
variants from the one set. `SelectionBarRules.Visible(count, armed) => count >= 2 || (armed && count >= 1)` replaces
`Track.Table.cs:930-931` and every `minCount: 1`. Mounted inline by the table, floating by the drawer, Top tracks and the
Show reader. `Artist.Discography.cs:1366`, `Episode.Selection.cs:144` and the Show copy are deleted.

### 4.3 `Playback.PlayOrToggleContext(EntityId context, int? startIndex = null)`
Pause/resume when `context` is the playing context, else `PlayContext`. `HomeCardNav.Play` and the Artist/Album/Profile/
Discography adapters call it; the decision (`same context && playing → pause`, `same && paused → resume`, else play) is
a pure rule with tests.

### 4.4 `Menus.Container(EntityKind kind, EntityRef target, ContainerMenuExtras extras = default)`
The one album/artist/playlist/show/user menu in the `Actions.Menu` grammar (strip: Play · Play next · Add to queue ·
Save/Follow; rows: Add to playlist · Open · Pin/Unpin · Go to artist · Share; extras append page-only rows). Card, hero
"…", sidebar (`Sidebar.UI.Menus.cs` keeps its Organize/layout groups as extras), Recents, Search, Discography and the
friends rail call it. Order and labels are a pure table with tests.

### 4.5 `SelectionDragPayload.For(selection, indexAt, resolve)` and `Text.Sep = " · "`.

## 5. Work packages (disjoint files; implementation by parallel subagents, orchestrator builds/tests/launches)

Before implementation each WP gets a verified brief with real code (the owner's plan rule): a Fable plan pass checks
every signature above against the tree and the engine.

| WP | Scope | Files |
|---|---|---|
| 1 | `RowClickPolicy` + the six trigger sites | new `Platform/RowClickPolicy.cs`; `Track.Table.cs` (2030-2041), `Surface.Bound.cs`, `User.UI.cs` |
| 2 | Selection bar composition + rules; table switches to it | new `Entities/Track.Selection.cs`; `Track.Table.Chrome.cs`, `Episode.Selection.cs`, `Show.Page.cs` |
| 3 | Top tracks + album drawer onto `ItemsView` (or a shared selectable-row host) with the bar, keyboard, check lane, drag helper | `Artist.UI.Chart.cs`, `Artist.Discography.cs` (drawer half) |
| 4 | Toggle-aware container play | `Playback.Host.Context.cs`, `Browse.Cards.cs` (`HomeCardNav.Play`), the adapters in `Artist.Page.cs`, `Album.Page.cs`, `Album.Pane.cs`, `Profile.UI.cs` |
| 5 | `Menus.Container` + migrate card/hero/Recents/Search menus; delete `Album.UI.MoreMenu` | new `Platform/Menus.Container.cs`; `Browse.Cards.cs` (menu half), `Artist.Page.cs` (menu), `Recents.Page.cs`, `Album.Page.cs` (hero menu), `Album.UI.cs`, `Playlist.Page.cs`, `Show.Page.cs`, `Show.Pane.cs`, `Profile.UI.cs` |
| 6 | Hero rows onto `Controls.Surface`: Latest release, Watch video, About the artist (shared with the rail), Search top result, Fans also like, Artist pick menu | `Artist.Page.cs` (banner), `Album.Page.cs` (video/about/chips), `Search.UI.cs` (top result), `Rail.UI.cs` (About extraction), `Artist.UI.cs` (pick) |
| 7 | Search flyout rows onto `Controls.Surface`; `Omnibar.Item` gains `EntityRef`; one subtitle builder; one kind marker | `Shell.Masthead.UI.cs`, `Shell.cs` (Omnibar.Item), `Spotify.Decode.Browse.cs` |
| 8 | "…" everywhere via `SurfaceRules`; Recents cards onto Surface; EagerRow "…"/drag/tooltip; episode rows draggable | `Queue.UI.cs`, `Rail.UI.cs` (Next up), `Recents.UI.cs`, `Track.UI.cs` (EagerRow), `Episode.UI.cs`, `Modules.UI.cs`, `Concert.Page.cs`, `Episode.Reader.cs` |
| 9 | Separators, friends-rail/notification menus, player-bar tooltip + "…", door/daylist play state, toolbar order + duplicate Shuffle | `Search.UI.cs` (sep only, after WP6), `Rail.UI.cs` (friends, after WP6/8), `Shell.UI.cs` (notifications), `Shell.PlayerBar.UI.cs`, `Controls.Podcast.cs`, `Home/Daylist.UI.cs`, `Album.UI.cs` (after WP5), `Concert.Rules.cs`, `Recents.cs` |

Order: WP1, WP2, WP4, WP7 in parallel → WP3 (needs WP1/2) and WP5 → WP6, WP8 → WP9. Tests: pure rule tests for §4.1–4.4
(no source-text tests); Debug + Release build and `Wavee.Tests` both configurations; on-screen pass of every row in §3 at
~756 DIP and 1440.

## 6. Owner decisions

| # | Decision | Recommendation |
|---|---|---|
| D1 | Singles: keep "no selection, click plays" (doc item 72) or select like albums | **select like albums** — singles with B-sides are multi-track; one rule for every track table |
| D2 | Show episodes: plain click opens the episode page (today) or selects like tracks | **keep opening** (the episode page is the destination, play is the row's FAB) and document it as a sanctioned deviation |
| D3 | Selection bar on 1 selected (plain click) | **no** — ≥2 or Select armed, as the table does; a single selected row shows the accent pill only |
| D4 | Queue / rail rows "…" in a narrow pane | **show it on hover** like every other row (today's narrow-panel fix removed the reserved slot; a hover-revealed overlay keeps title width) |
| D5 | Discography card click toggling the drawer | **keep**, add a caret so it reads as an expander |

## 7. Verified plan (approved 2026-10-02; supersedes §4–§6)

Decisions: D1 singles select like albums; D2 show episodes keep click-opens (documented exception); D3 bar at ≥2 or ≥1 with Select armed; D4 queue/Next up get a hover-overlay `…`. Verification facts: `KeyModifiers.Ctrl` (not Control); the click owner is engine `ItemsView.OnItemInteraction`; the Top tracks chart is already a `PagedShelf`/`ItemsView`; `Omnibar.Item` gets a computed `Ref`; `Album.Page.AlbumRow` is private (use the `HomeCard` adapter); `VideoHero` only needs `MoreButton(restOpacity: 0f)`; Concert/Recents dashes are ranges, not drift.

## Context
The owner found surfaces that look alike but behave differently: the artist "Latest release" row isn't clickable and has a
separate "View" button; search-flyout rows have no right-click and no "…"; artist Top tracks can be selected but never
shows a selection bar; Ctrl+click plays on albums; the album drawer shows a "1 selected · Play" pill instead of the
standard selection bar. Four read-only audits plus a verification pass found these are drift from a contract the app
already documented (`docs/plans/wavee/wavee-0.3-ui/01-track-row.md` §6.1, `04-detail-track-table.md`, ch 29 §2) and
already has shared pieces for (`Controls.Surface`, `Controls.SelectionBar`, engine `SelectionModel`/`ItemsView`,
`Track.RowMenu`, `Actions.Menu`, `Drag.Source`). The fix is to enforce that contract by construction: one owner per
decision, every surface routed through it, hand-built copies deleted. Verdict + ranked issue list:
`docs/plans/wavee/interaction-consistency-implementation.md` (update it with §8 corrections as the first step).

## The contract (owner-approved decisions)
- Selection-backed lists (track tables incl. **singles**, Top tracks, album drawer): click selects; Ctrl toggles; Shift
  extends; **a Ctrl/Shift double-click never plays**; plain double-click/Enter plays; Space/Ctrl+A/Esc work.
- Preview lists (queue, Next up, Recents, Home top tracks, search hits): click plays. Cards/hero rows: whole surface
  opens; no separate View/Open button. Show episodes: click **opens** the episode (documented exception).
- One standard selection bar, shown at **≥2 selected or ≥1 with Select armed**; inline where a toolbar exists, the same
  composition floating elsewhere.
- Right-click, Menu key, Shift+F10 and "…" open one menu per entity kind, the same on every surface. "…" is
  hover/focus-revealed everywhere, including queue/Next up rows (overlaid, no reserved width).
- Every play FAB toggles play/pause, container contexts included. One subtitle separator `" · "`, one kind marker.

## Work packages (exclusive file ownership; parallel subagents; orchestrator builds/tests)
Order: **WP1 ∥ WP2 ∥ WP45 ∥ WP7 → WP3 ∥ WP6 → WP8 → WP9.**

**WP1 — click policy in the engine + seams** (engine `FluentGpu.Controls/ItemsView.cs`, `PagedShelf.cs`, `ListOptions.cs`,
VerticalSlice `ControlsSuite.RowFocus.cs`/`.PagedShelf.cs`; app new `Platform/RowClickPolicy.cs`, `Track.Table.cs`
(trigger line only), `User.UI.cs`, `Entities/Detail.cs` (Single → `Selection = Extended`, doc item 72 updated)).
- `ItemsView.OnItemInteraction(int, ItemContainerTrigger, KeyModifiers)` (~1452): a DoubleTap with `KeyModifiers.Ctrl`
  or `Shift` becomes a Tap. Gate `gate.virt.invoke.modifiedDoubleTap`.
- `ListOptions.IsItemSelectable: Func<int,bool>?` honoured by Ctrl+A in `OnRootKey` (~1333) and by interaction (fixes
  Ctrl+A selecting hero/header prefix items; replaces `Episode.Selection.cs` hand-walk). Gate `gate.virt.selectAll.selectableOnly`.
- `PagedShelf.Create(…, ItemsSelectionMode selectionMode = None, SelectionModel? selection = null)` threaded into both
  `ListOptions` sites (~1580, ~1699). Gate `gate.shelf.selection`.
- `RowClickPolicy.TriggerOf(clickCount, mods)` replaces the three app `ClickCount >= 2` expressions
  (`Track.Table.cs:2032`, `Surface.Bound.cs:49` (WP7 edits it), `User.UI.cs:587`).

**WP2 — one selection lane** (new `Entities/Track.Selection.cs`; `Track.Table.Chrome.cs`, `Episode.Selection.cs`,
`Show.Page.cs` bar only). Extract today's `SelectionCommands`/`BuildCommandRow`/`TableSelectionMore`
(`Track.Table.Chrome.cs:1273-1460`) into `Track.SelectionLane(int fit, in SelectionLaneArgs a)` (args: tracks, episodes,
host, Exit, SelectAll, WasVisible, AllPlayed, Overlay) and `SelectionBarRules.Visible/ChecksVisible` (lifted from
`Track.Table.cs:930-931`). Callers pass `Controls.SelectionBar(count, lane, standalone, …, minCount)` (`Controls.cs:1107`).
Delete `Episode.Selection.cs` `Commands`.

**WP45 — toggle-aware play + one container menu** (new `Playback/Playback.ContextToggle.cs`, new
`Platform/Menus.Container.cs`; `Browse.Cards.cs`, `Artist.Page.cs` (adapters + CardMenu), `Album.Page.cs` (adapters +
hero menu), `Album.Pane.cs`, `Album.UI.cs` (delete duplicate `MoreMenu`), `Profile.UI.cs`, `Recents.Page.cs`,
`Playlist.Page.cs`, `Sidebar.UI.Menus.cs` (container kinds via extras), `Artist.UI.cs` (pick FAB + right-click)).
- `ContextPlayRules.For(target, context, current, fault)` = `CardRelation.Relates` (`Playback.CardSeam.cs:26`) + the
  fault gate of `Shell.PlayerBarRules.RowVerb` → Toggle | Start; `Playback.PlayOrToggleContext(EntityId|string)` calls
  `TogglePlay` (`Playback.Host.cs:1175`) or `PlayContext` (`Playback.Host.Context.cs:89`). Switch every FAB/`OnPlay`
  adapter (HomeCardNav.Play both branches, Artist/Album/Pane/Profile/Recents/Show adapters). Leave `Actions.Services.Play`.
- `ContainerMenuRules.For(TargetKind, liked, onPage)` (pure verb table) + `Menus.Container(in ActionTarget, art,
  subtitle, in ContainerExtras)` over `Actions.Menu.Strip/AddRows/Row/Share/Header/Group/Pin` (`Actions.UI.cs:175-300`),
  targets from `ActionTarget.ForAlbum/ForArtist/ForPlaylist/ForShow` (`Actions.cs:940-957`). Browse's raw-Services
  Save/Open/Pin folds onto registered verbs with `OpenOrigin`. Hero "…" menus use `OnPage: true`.

**WP7 — search flyout on the shared surface** (`Shell.Masthead.UI.cs`, `Shell.cs` Omnibar, `Spotify.Decode.Browse.cs`,
`Platform/Surface.Bound.cs`, new `Platform/Text.cs` with `Sep = " · "`).
- `Omnibar.Item` gains computed `EntityRef Ref => Entities.Ref(Uri.Id)` (ctor unchanged). Rows render
  `Controls.SlotSurface(RowScope, CardData, Shape.Row(44f) with { MinHeight = 58f })` — extracted from
  `Controls.BoundSurface` — with a synthesized `RowScope` whose `IsFocused` tracks `s_omnibar.Highlight`, so arrows/Enter
  keep working and focus stays in the field. Menu: `Track.Menu` for tracks, `Menus.Container` for containers (WP45 lands
  first or same wave with a stub), none for users. Trailing via `SearchHitRules.TrailingOf`, drag via `Drag.Source`.
- Decoder emits detail only (drop `JoinArtists`' `" - "`); one `Search.SubtitleText(kindWord, detail)` shared with the
  search page; drop the kind pill. Verify the menu opens from inside the popup (Overlay.Service context).

**WP3 — Top tracks + album drawer on real selection** (`Artist.UI.Chart.cs`, `Artist.Discography.cs` whole file, new
`Platform/SelectionDrag.cs`). Chart: `PagedShelf.Create(…, Extended, Selection, onInvoke: StartAt)`, delete hand-rolled
`_released`/`SelectRow`, add `SelectorVisualsBound.BoundCheckLane`, floating `SelectionBar` with `Track.SelectionLane`.
Drawer: `ItemsView.CreateBound` with `RepeatLayout.Stack(RowPitch)` / `RepeatLayout.HorizontalGrid(rows: perColumn, …)`
(column-major), `IsItemSelectable = i => i < v.Shown` (Show-all excluded), lane via `Track.SelectionLane`; delete its
`SelectionCommands` and `CardMenu` (→ `Menus.Container`); discography card gains a caret. `SelectionDrag.Indices(sel,
pressed, into)` replaces the three payload copies (table's `DragPayloadFor` switches in WP8-safe follow-up).

**WP6 — hero rows** (`Artist.Page.cs` LatestBanner, `Album.Page.cs` chips/About/VideoHero, `Rail.UI.cs` About
extraction, `Search.UI.cs` top result + `Text.Sep`, new `Platform/Controls.ArtistAbout.cs`).
- Latest release → `Controls.Surface(HomeCards.GridCardData-style CardData{Eyebrow, OnClick=Track.GoToAlbum,
  OnPlay=PlayOrToggleContext, Menu, Drag}, Shape.Row(72f))`; delete View.
- About the artist → one `Controls.ArtistAboutCard` from `Rail.UI.cs:592-675` (Follow as sibling), used by album + rail.
- VideoHero: `Controls.MoreButton(restOpacity: 0f)`. Top result: drop "Open page", toggle play, hover "…".
  Album "Fans also like" chips → circular `Shape.Shelf`/`Shape.Row(32)` like the artist page.

**WP8 — "…" and keyboard everywhere** (`Queue.UI.cs`, `Rail.UI.cs` Next up, `Recents.UI.cs`, `Track.UI.cs` EagerRow,
`Episode.UI.cs`, `Modules.UI.cs`, `Concert.Page.cs`, `Episode.Reader.cs`). Remove caller-side `ShowMenu:false`; the
trailing "…" becomes a hover overlay that reserves no width (extend `SurfaceRules.ShowsMenuTrailing` /
`Surface.Parts.cs:244` if needed — check before WP8). Recents cards → `Controls.Surface(…, Shape.Row)`; EagerRow gets
"…", drag, title tooltip; episode rows draggable; module/concert-promo/More-like-this pass `Menu`/`OnPlay`.

**WP9 — remaining polish** (`Show.Page.cs`/`Show.Pane.cs` hero menus → `Menus.Container(OnPage)`, `Shell.UI.cs`
notifications menu, `Shell.PlayerBar.UI.cs` heart tooltip + hover "…" → `Stage.NowPlayingMenu`, `Controls.Podcast.cs`
door play state via `NowPlayingOverlay`, `Home/Daylist.UI.cs` toggle play, `Album.UI.cs:650` duplicate Shuffle (verify
page vs pane), friends row menu in `Rail.UI.cs`).

## Tests
Add (pure, no source reads): `RowClickPolicyTests`, `SelectionBarRulesTests`, `ContextPlayRulesTests`,
`ContainerMenuRulesTests`, `SearchSubtitleTests`, `OmnibarRowRulesTests`, `SelectionDragTests`. Engine gates listed in WP1.
Re-check: `ShellOmnibarTests`, `SearchDecodeTests`, `SearchHitDataTests` (`" • "`), `EpisodeSelectionTests`,
`AlbumDrawerVerdictTests`/`ArtistDrawerVerdictTests`, Single-config tests in Detail/Track table tests.

## Verification
1. After each wave: `dotnet build Wavee.slnx` Debug + Release; `dotnet test src/apps/Wavee.Tests` Debug + Release
   (known pre-existing failure: `ProfilePageRulesTests.Numeral_GroupsInTheGivenCulture`). After WP1: engine
   `dotnet build src/FluentGpu.slnx` Debug + Release and VerticalSlice in `..\fluent-gpu`.
2. NativeAOT arm64 build via `ops\build\publish-wavee-aot.ps1 -Arch arm64 -CrashService -Quad 0.0.x.0`, then on screen
   at ~756 DIP and 1440 (drive by WM_COPYDATA deep links, verify HWND each step): Ctrl+double-click on album/single/
   drawer/Top tracks never plays; Top tracks + drawer show the standard bar at 2; a single shows selection; search
   flyout rows right-click + hover "…" + arrows/Enter still work; Latest release opens on click, no View; card FAB
   pauses the playing album; queue/Next up hover "…" without clipping titles.
3. CHANGELOG `## [0.3.0]` bullets for user-visible changes (no ref unless an existing issue matches).

## §8 corrections to apply to the repo plan doc first
`KeyModifiers.Ctrl` (not Control); click owner is engine `ItemsView`; drop `Decide/ListKind`; chart is already a
`PagedShelf`/`ItemsView`; `Omnibar.Item` ref is computed; `AlbumRow` is private (use `HomeCard` adapter); VideoHero only
needs `restOpacity: 0`; Concert/Recents dashes are ranges; file ownership per this plan; decisions D1–D4 recorded as
chosen above.

## §9 Owner follow-up (2026-10-02, after WP8/WP9): where the hover "…" does NOT go
- **D4 reversed.** The rail queue's rows (Modern and Classic), its now-playing card and the NPV's "Next up" rows have no
  "…" button; the menu is the right-click / Menu key / swipe. One rule, `QueueRowRules.ShowsMenuButton` (false), fed to the
  surface as `CardData.ShowMenu`. The overlay landed on the row's end — the ✕ (the card: the heart) — and hid and blocked it.
- **Player bar.** WP9's hover "…" over the end of the title (left of the heart) is deleted; the cluster keeps its
  right-click (`Stage.NowPlayingMenu`).
- **Search rows** (the page and the flyout). The "…" takes its own lane after the trailing control
  (`MenuPlacement.TrailingLane`, `SearchHitRules.RowMenu`; `SurfaceRules.MenuReservesWidth` is now true for that shape):
  every search kind with a menu ends in the heart or the Follow pill, so the overlay always covered it ("Fol…"). The Top
  Result card already put its "…" in a slot beside the same control. Every other row keeps the overlay.
