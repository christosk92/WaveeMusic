# Library stabilization plan (navigator view switching + artist reader flashing)

Status: INVESTIGATION + PLAN ONLY. Nothing in this document has been implemented.
Evidence: `C:\Users\ChristosKarapasias\Videos\still.mp4` (16.77 s, 30 fps, 1774x1142) and
`%LOCALAPPDATA%\Wavee\logs\wavee-20260918.log`, session `sid=daca24f7 pid=31276` (a **Release arm64 publish**,
`engine=release` — so every DEBUG-only engine tripwire is compiled out of the build the owner actually runs).
Clock mapping: the video starts at the `nav.route route=artists navId=3` line, `t=1789730334414`. So
`video seconds ≈ (log t − 1789730334400) / 1000`. All log times below are given as the last five digits of `t`
(`39138` = `t=1789730339138` = video 4.7 s).

Code under investigation (uncommitted trees): app `C:\WAVEE\wavee-0.3`, engine `C:\WAVEE\fluent-gpu-pin`.

---

## 0. Executive summary

1. **The view switcher does nothing because the navigator's remount key sits in a slot where the engine ignores keys.**
   `NavList()` returns `BoxEl { Key = navKey, Children = [ItemsView…] }` as the ROOT of a `Skel.Region` content thunk.
   `ReconcileSkeletonRegion` reconciles that root through `ReconcileSingleChild`, which pairs old/new by
   `ElementTypeId` ONLY — `Key` is documented as inert there. BoxEl -> BoxEl = update in place, so the child
   `ItemsView` component is REUSED and its layout / template / options (all frozen at mount) never change. Only the
   wrapper's `Padding` changes, which is the 8-DIP "twitch" seen on every click. This is a REGRESSION of the
   library rework: at HEAD the keyed box was a direct child in `NavPanel.Children` (keys honored).
2. The view "applies" later, by accident, when the sort word crosses a–z <-> not-a–z: that changes the root's CHILD
   LIST shape (`[ItemsView]` <-> `[body, strip]`), which replaces the ItemsView. That is exactly the "buggy, not as
   expected" behaviour in the video (click list: nothing; click a–z: suddenly a list; click grid: nothing; click a–z
   again: suddenly a grid).
3. The log line `library.nav.remount` LIES: it is emitted from the key string, not from a mount. The census proves
   it: `reason=view` lines are followed by `mounts=0`.
4. **A naive fix (just make the key honored) would introduce a remount storm**: `OrderKey` is in the key, and the
   cold start logs six `reason=order` key changes in 600 ms (`before=0 -> 16 -> 26 -> 38 -> 268 -> 309`). Today those
   are harmless in-place updates *because of the bug*. The video also proves an order change needs NO remount (the
   bound source re-resolves rows in place: `44722 reason=order … mounts=0`, and the list shows the new order). So
   `OrderKey` must leave the key in the same change.
5. **The artist reader is the "flashy" part.** Every artist click remounts the whole reader list (band + sub-rail +
   blocks are one list keyed by artist) under `s_listSwap` = *enter from opacity 0, no exit* — i.e. a guaranteed blank
   pane followed by a 120 ms fade. Then every data-driven reshape that is not a pure append bumps `_generation` and
   does it AGAIN: one click on Troye Sivan produced 5 shapes (`blocks=5,5,7,9,27`) and 6 mount bursts
   (281/369/85/420/230/645 nodes) within 150 ms — two visible dim/fade cycles, plus the scope count ticking
   "4" -> "51".
6. Secondary churn (not visible as flashes, but real cost on every landing batch): `EagerTrackRowHost` and
   `SpineDot` subscribe to whole-table `Changed` signals, so every track/album batch re-renders every mounted row
   and dot (`EagerTrackRowHost×14–25`, `SpineDot×18`, `ToolTip×20` per frame for ~10 consecutive frames per click).

Ordered fix list: A1 (stable host + keyed children) and A2 (one owner mints key+layout+template+options; OrderKey out
of the key) land together -> A3 (truthful mount log) -> R1 (reader hard cut, no blank) -> R2 (no generation remounts;
in-place reshape) -> R3 (catalogue readiness gate) -> E1 (engine: always-on ignored-key report) -> E2 (engine: tap
bring-into-view animates) -> C1/C2 (per-row subscriptions) -> minor items.

---

## 1. Observed defects

| # | Video time | Log | What is seen | What should be seen |
|---|-----------|-----|--------------|---------------------|
| D1 | 4.7 s, 5.3 s | `39138`, `39662` `library.nav.remount reason=view before=309 after=309` (no mounts in any census line) | In the "⋯" flyout the owner clicks cell 2 (List, view=1) then cell 1 (Compact list, view=0). The flyout's selected cell moves and the Size bank disappears, but the navigator keeps showing the big circular GRID cards. The only change: the selected card's frame grows by 8 DIP on each side (the wrapper's grid padding went to 0). | The navigator becomes a list of 60-DIP rows (then 44-DIP compact rows), selection still lit, in one frame. |
| D2 | 7.2 s, 7.8 s | `41429`, `42187` `reason=view`; census at `41429`: `nodes(upd=22 wr=7 mounts=0) top=LibraryPage×1,ViewToggleHost×1` | Toolbar grid icon, then toolbar list icon: glyph highlight toggles, cards twitch by 8 DIP, still grid cards. | Grid <-> list. |
| D3 | 8.9 s | `43324 reason=letters` | Clicking the sort word **a–z** suddenly turns the navigator into a compact LIST with letter headers + jump strip — the view chosen 1–4 s earlier finally applies, on an unrelated click. | Sort word changes only the order (+ letters); the view had already changed at D1. |
| D4 | 11.0 s | `45421 reason=view` | In a list (sort = albums) the owner clicks the grid icon: rows shift 8 DIP right/down (grid padding applied to a list), still rows. | Compact grid. |
| D5 | 12.2 s | `46614 reason=order`; `47210 reason=order … mounts=363 top=ItemsView×2` | Clicking **a–z** turns the list into the compact 2-column GRID (the view picked at 11.0 s). Clicking **albums** 0.6 s later remounts the grid again. | As D3. |
| D6 | 9.3 s, 10.3 s, 11.75 s | `43697 reason=letters mounts=185` (real remount: a–z -> recents), `44722 reason=order mounts=0`, `46154 reason=order mounts=0` | Two of the three "remounted" lines mounted nothing; the rows simply re-resolved in place and the order on screen is correct. The log claims a remount each time. | The log must state what happened (`mounted` vs `reordered in place`). |
| D7 | 11.75 s | `46154` | After recents is clicked the list jumps so the selected row (League of Legends) sits at the BOTTOM edge with ten unrelated rows above; at 9.4 s the same word put it at the TOP edge. Same sort, two different scroll positions. | Deterministic: selection revealed at a stable place (or the list opens at the top) after a sort change. |
| D8 | 1.5 s | — | Clicking a partially visible card (League of Legends) makes the whole list JUMP ~130 DIP in one frame under the cursor. | Animated minimal scroll (WinUI `BringIntoViewOptions.AnimationDesired` defaults to true), or no scroll when the card is mostly visible. |
| D9 | 0.9 s, 1.5 s, 15.0 s, 15.4 s | `library.reader.shape` bursts: `35345/35403/35412` (15,15,29), `35920/35969/35986/36037` (7,7,26,46), `49386…49436` (5,5,8,27), `49770…49911` (5,5,7,9,27); census mount bursts e.g. `49773 mounts=281`, `49781 mounts=369`, `49829 mounts=85`, `49875 mounts=420`, `49897 mounts=230`, `49915 mounts=645` | On every artist click the WHOLE right pane (artist header included) goes blank for 1–2 frames, fades in dim, then dims/fades AGAIN one or two more times as facet pages land; blocks pop in under skeleton bars; "all releases · 4" becomes "· 51"; the spine shows one empty outlined square then covers pop in. | Header swaps instantly (it is painted from resident identity); ready blocks appear instantly; pending blocks dissolve once; no second full-pane fade, ever. |
| D10 | whole reader sequence | census: `EagerTrackRowHost×14..25`, `SpineDot×18`, `ToolTip×18..20` re-rendered in ~10 consecutive frames after each click (`35405…35730`, `36054…36347`, `49454…50164`) | Not visible as a flash, but 400–650 node updates per frame for 300 ms per click. | A landing batch re-renders only the rows it touched. |
| D11 | 0.0 s | `34419 frame.slow … mounts=1785 hotAlloc=13168472 EagerTrackRowHost×61` | First frame of the Artists page mounts 1785 nodes and allocates 13 MB (61 eager track rows for the restored artist). 15.8 ms here; a 108 ms first frame in the same session at launch (`20385`). | Out of scope for this plan except that R2/R3 reduce it; listed for completeness. |
| D12 | 4.7–5.3 s | — | The open "⋯" flyout changes height when the view crosses list<->grid (the Size bank is added/removed), so the popup resizes under the cursor. | Minor. Keep the Size bank mounted and disabled (50 % opacity, not hit-testable) for list views. |
| D13 | n/a (not exercised in the video) | — | S / M / L: `size` is in the same inert key -> **cannot work either**. | Size changes the grid cell minimum (88/104/120 compact, 116/140/164 full). |
| D14 | next launch | — | `SaveState` persists `View`/`Size` even though the UI never applied them, so the next launch opens in a view the owner never saw take effect. | Consequence of D1; disappears with it. |

Note (not a defect): at the owner's `LeftW` (~240) the full grid at M (cell min 140 inside 224 − 16) resolves to ONE
column of ~190-DIP avatars. That is what the spec's ladder yields below ~310 DIP of column width
(`15-library.md:410` assumes 340). Product decision, not part of this plan.

---

## 2. Root cause per defect

### RC1 — a `Key` in a single-child slot is inert (D1, D2, D3, D4, D5, D13, D14) — CONFIRMED, REGRESSION

App, `src/apps/Wavee/Entities/User.Page.Library.cs`:

```csharp
// :869
Element ListBody() => Skel.Region(_navLoad, s_navShimmer, _navContent, SkelReveal.StaggerRows, …);
// :320
_navContent = _ => NavList();
// :913-920  (NavList)
Element list = new BoxEl
{
    Key = navKey,                                   // "nav:view:size:OrderKey:LettersKey"
    Grow = 1f, Direction = 1, MinHeight = 0f,
    Padding = grid ? new Edges4(Spacing.S, Spacing.S, Spacing.S, 0f) : default,
    Children = [ItemsView.CreateBound(_items!, template, layout, lettered ? _navOptionsLettered : _navOptions)],
};
if (!alpha) return list;                            // <- the keyed box IS the region's content root
```

Engine, `src/FluentGpu.Engine/Reconciler/Reconciler.cs`:

```csharp
// :1602-1613  ReconcileSkeletonRegion, same-branch path
else { ReconcileSingleChild(node, desired, lastEl); }

// :1219-1225  the documented contract
/// CONTRACT: this slot pairs old<->new by Element.ElementTypeId ONLY — Element.Key is honored exclusively by
/// ReconcileChildren. A key on a component's ROOT element, a provider body, or a Show body is therefore INERT

// :1241-1249
else if (oldChild is not null && oldChild.ElementTypeId == newChild.ElementTypeId)
{
    if (ReuseGuard.CompiledIn && ReuseGuard.Enabled && …nk != ok) ReportKeyIgnoredInSingleChildSlot(…);  // DEBUG only
    Update(child, newChild, oldChild);
}
```

`Update` on a BoxEl rewrites its columns (hence the padding twitch) and calls `ReconcileChildren` on
`[ComponentEl(ItemsView)]` vs `[ComponentEl(ItemsView)]`; `Update` for a `ComponentEl` of the same `ComponentType`
reuses the instance (`:1048-1090`, "the component is AUTONOMOUS"), and `ItemsView.CreateBound` is a PROPLESS
`Embed.Comp(() => new ItemsView { … Layout, RowTemplate, options … })` (`ItemsView.cs:660`, `:712-775`), so the new
layout/template/options are constructed and thrown away.

Why the view applies on an a–z click: with `alpha` true `NavList` returns an UNKEYED row box `[body, strip]`
(`:928-932`). Root BoxEl -> root BoxEl is again an in-place update, but now the CHILD lists differ
(`[ComponentEl]` vs `[BoxEl, Component]`), `ReconcileChildren` cannot pair them, and the ItemsView is replaced — with
whatever `View`/`Size` hold at that moment. Every transition in the video matches this model exactly:

| click | root before -> after | children before -> after | result |
|---|---|---|---|
| view/size change, sort ≠ a–z | keyed box -> keyed box (key ignored) | `[ItemsView]` -> `[ItemsView]` | REUSED: nothing changes (D1, D2, D4) |
| recents <-> albums | same | same | REUSED: rows re-resolve in place (correct, D6) |
| X -> a–z, a–z -> X | keyed box <-> unkeyed row | `[ItemsView]` <-> `[body, strip]` | REPLACED: pending view applies (D3, D5) |
| list <-> grid while a–z | row -> row | `[ZStack(unkeyed), strip]` <-> `[list(keyed), strip]` | replaced (keyed vs unkeyed never pair) |

Proof from the log: `41429 library.nav.remount reason=view` is followed in the same millisecond by
`frame.churn … nodes(upd=22 wr=7 plans=0 mounts=0) … top=LibraryPage×1,ViewToggleHost×1`. A real remount of a
309-item list mounts 185–363 nodes (`43697`, `47210`).

Regression evidence: `git show HEAD:src/apps/Wavee/Entities/User.Page.Library.cs` — `ListBody(shape)` returned the
keyed box directly into `NavPanel.Children` (`:427`, `:465`), i.e. through `ReconcileChildren`. The rework wrapped it
in `Skel.Region` (the shimmer cross-dissolve) and moved the key into the single-child slot.

The comment at `:232-238` ("a hook in Render ran AFTER the Skel region's own effect … the view switcher did
nothing") is a WRONG DIAGNOSIS from an earlier round: moving the layout resolution into `NavList` fixed nothing,
because the new layout never reaches a mounted ItemsView either way. Effect ordering is not the cause.

Why nobody saw the tripwire: `ReuseGuard.KeyIgnoredInSingleChildSlot` exists (`Hooks/ReuseGuard.cs:81`) but is gated
on `ReuseGuard.CompiledIn` (DEBUG). The owner runs the Release publish. -> engine step E1.

### RC2 — the remount log is derived from the key string, not from a mount (D6) — CONFIRMED

`NoteNavKey` (`User.Page.Library.cs:712-726`) is a `UseEffect(_noteKey, listKey)`. It fires when the STRING changes.
It also mislabels: `reason` is computed as `view` / else `letters` if `LettersKey` moved / else `order`, so a–z ->
recents logs `letters` and a grid-mode a–z click logs `order`.

### RC3 — `OrderKey` does not belong in the mount key (latent; becomes a storm the moment RC1 is fixed) — CONFIRMED

`NavKey` = `"nav:" + view + ":" + size + ":" + shape.OrderKey + ":" + LettersKey` (`:940-941`). Cold start of this
very session: `21237 reason=order before=0 after=16`, `21479 16->16`, `21537 16->26`, `21651 26->38`,
`21815 38->268`, `21838 268->309` — six key changes in 600 ms while `LibraryArtistsOf` grows. With keys honored each
is a full ItemsView remount (lost hover, scroll restore, image re-request, cold-realize ramp): the exact "0.2.10
remount storm" the file header says it fixed by removing `FactsKey`. The bound source (`BoundItems.Project(_shape,
…ItemAt)`, `:365`) re-resolves every realized slot when the memo republishes, and the video shows that is sufficient
(`44722`, `46154`: order changed on screen with `mounts=0`). Count changes ride `CountSignal = items.Count`
(`ItemsView.cs:754`).

What genuinely needs a fresh mount (frozen at mount): the row TEMPLATE (list row vs card, compact or not), the
LAYOUT (`Extents` 60 / 44 vs `GridFit(min)`), the OPTIONS variant (lettered: `ContentType`, `IsItemEnabledTyped`,
`ItemClipTopInset`, geometry observer) and — for the lettered list only — the per-index extent SEED (header 28 vs
row 44/60: `RepeatLayout.Extents` calls `_extentOf` only on seed/resize, so moved header positions leave stale
extents for unrealized indices and `LibraryLetters.OffsetOf` drifts from the layout).

### RC4 — one `ScrollKey` for four different layouts + selection reveal by "minimal scroll" (D7) — CONFIRMED mechanism

`_navScroll = new ScrollOptions { ScrollKey = "lib:nav:" + _kind }` (`:276`) is shared by list, compact list, both
grids and the lettered projection. A restored offset from another layout is a meaningless number. `SyncNav` then
calls `SyncSelect(idx)` -> `_navCtl.StartBringItemIntoView(idx)` with alignment NaN (`:665-671`), whose result
depends on which side of the (arbitrary) restored viewport the row happens to be: top edge at 9.4 s, bottom edge at
11.75 s.

### RC5 — pointer tap bring-into-view is unanimated (D8) — CONFIRMED (engine parity defect)

`ItemsView.cs:1463-1465`: `if (pointer) BringIntoView(i, float.NaN, animate: false);` — the comment cites WinUI's
`StartBringIntoView()` with default options, but WinUI's default `BringIntoViewOptions.AnimationDesired` is `true`.

### RC6 — the reader remounts its only list on every artist click, entering from opacity 0 with no exit (D9, first blank) — CONFIRMED

`Artist.Reader.cs`:

```csharp
// :531-533
static readonly LayoutTransition s_listSwap = new(
    TransitionChannels.Opacity, TransitionDynamics.Tween(120f, Easing.SmoothOut),
    Enter: new EnterExit(Opacity: 0f, Active: true));          // no Exit: the old list leaves at once
// :730-738
string triple = _artist + ":" + shape.Scope + ":" + shape.Sort;
string key = triple + ":" + (_narrow ? "n" : "w") + ":" + _generation;
…
Element list = new BoxEl { Key = "list:" + _listKey, Animate = _measured ? s_listSwap : null, …
    Children = [ItemsView.CreateBound(Prefix + _count + 1, _itemAt, _layout, _options)] };
```

The band (item 0) and the pinned sub-rail (item 1) are items OF this list, so the old pane is removed in the same
flush the new one mounts at opacity 0: frame N+1 is an empty card (seen at 0.93 s and 15.43 s), then a 120 ms rise.
The previous round removed the exit to cure a "ghost double image"; that traded a ghost for a blank.

### RC7 — data-driven reshapes bump `_generation` and replay RC6 (D9, second/third fade) — CONFIRMED

`:720-727`: `if (_orderCount > 0 && !IsAppendOfOrder()) _generation++;`. `ReaderShape.Build` (`:171-215`) orders
the library group, then the UNION of the three facets sorted together (`Order(scratch, n, m, perm, sort)`), so:

* a second facet landing INTERLEAVES with the first by date -> not an append -> remount + fade;
* a Year (the `newest` sort key) landing on an already listed album re-sorts -> same count, new `OrderKey`
  (`library=7 blocks=7` logged twice 49 ms apart at `35920`/`35969`; `5,5` at `49770`/`49804`) -> remount + fade.
  SUSPECTED as the specific cause of the equal-count reshapes (could also be a `Saved`/`LikedOnly` bit flipping —
  both are hashed into `OrderKey`, `:327-336`). Confirm with the reason field added in step R4.

Troye Sivan (15.4 s): 5 shapes and 6 mount bursts in 145 ms.

### RC8 — every block reveals with `SkelReveal.Soft` (opacity + rise + blur) (D9, texture) — CONFIRMED

`:1570` `Skel.Region(_load, …, SkelReveal.Soft, smoothResize: false)`. In a dense reader a blur-rise per block, on
top of RC6/RC7, reads as flicker (dim track rows at 1.0 s and 15.5 s). Once RC6/RC7 are gone one dissolve per
genuinely pending block is fine, but it should not move or blur: `FadeOnly`.

### RC9 — whole-table subscriptions in per-row components (D10) — CONFIRMED

* `Track.UI.cs:1061` (`EagerTrackRowHost.Render`): `_ = Entities.Current.Tracks.Changed.Value;`
* `Artist.Reader.cs:1521` (`SpineDot.Render`): `_ = Entities.Current.Albums.Changed.Value;` and `:1520` the whole
  shape memo. Each dot is wrapped in `Controls.Named` -> a `ToolTip` component re-renders with it.

### RC10 — flyout resizes while open (D12) — CONFIRMED, minor

`User.UI.cs:304-309`: the Size header + `SelectorBar` are appended only when `v >= 2`.

### Suspected, not proven from this evidence

* S1. After RC1 is fixed, a view change between the lettered list and a grid changes the INDEX SPACE (flat with
  headers vs rows). `_navSel` (shared `SelectionModel`) still holds the old flat index when the new ItemsView mounts;
  `SyncNav` repairs it from a passive effect afterwards. If the fresh ItemsView raises `OnChange` on mount, or a
  frame is painted first, the wrong row is lit / `OnNavSel` could `Select` the wrong artist. Would be confirmed by a
  `library.select` log line carrying `origin=list|sync` during the verification script (§6 step 6). The target
  design removes the window regardless (§3.3: the index is remapped in the same place that mints the mount).
* S2. `Album.Pane` was not exercised in the video. Its `Skel.Region` content root is `Track.Table(...)` (a
  ComponentEl) inside a pane body that IS a keyed child (`Album.Pane.cs:190`), so RC1 does not apply there; its
  `Render` subscribes to four whole tables (`:158-159`), which is the RC9 class. Verify with the script, do not
  change blind.

---

## 3. The view-switching model, end to end

### 3.1 How it is supposed to flow

```
signals            View, Size, Sort, Desc (persisted, page-owned)      Filter (not persisted)
   │
shape memo         ComputeShape: filter -> sort -> letters -> row versions      (pure data; no UI facts)
   │                 publishes NavShape(Count, FlatCount, OrderKey, FactsKey, RowsKey, LettersKey, Answered)
   │
MOUNT IDENTITY     ONE function: (View, Size, lettered, LettersKey) -> NavMount { Key, LayoutSpec, Template, Options, ScrollKey }
   │                 — the only place that knows what is frozen at mount
   │
tree               stable host (single-child slot) ─ Children:[ keyed list(NavMount.Key) , keyed sticky?, keyed strip? ]
   │                 key differs  -> ReconcileChildren removes old / mounts new  (remount)
   │                 key same     -> ItemsView untouched; bound slots re-resolve through the memo (refresh)
   │
selection          the selected ROUTE KEY is the truth; its flat index is derived for the CURRENT projection
   │                 and written to the SelectionModel BEFORE the new list mounts, re-asserted after
   │
persistence        SaveState effect (already correct)
```

### 3.2 Where the current implementation violates it

1. The key is on a single-child-slot root (RC1) -> mount identity is never delivered.
2. Mount identity is spread over five places that must agree and do not share a type: `NavKey` (`:940`),
   `LayoutOf`/`LayoutFor` (`:946-958`), the template pick (`:912`), the options pick (`:918`), the wrapper padding
   (`:917`), plus `ComputeShape` deciding `_hasLetters` from `View` (`:482`) and `RowExtent` peeking `View`
   (`:571`). `_navScroll` is a sixth, built in the constructor and blind to all of them.
3. The key contains a DATA fact (`OrderKey`) (RC3).
4. The root element's TYPE/shape changes with `alpha` (keyed box vs unkeyed row): node reuse across semantically
   different elements (defect class 2) — it is the accident that makes D3/D5 "work".
5. Selection re-apply is a passive effect keyed on the same string (`:389`), so it also runs on in-place order
   changes as if a mount had happened (`remounted = navKey != _lastNavKey`), and it reveals by minimal scroll against
   a scroll offset restored from another layout (RC4).
6. The diagnostic line reports intent, not fact (RC2).

### 3.3 Target design

**One owner, one mint.** A new engine-free pure type decides everything that is frozen at mount:

```csharp
// src/apps/Wavee.Core/Library/LibraryNavMount.cs   (engine-free, unit-tested)
public enum LibraryNavTemplate : byte { Row, RowCompact, Card, CardCompact }
public enum LibraryNavLayoutKind : byte { Extents, GridFit }

public readonly record struct LibraryNavMount(
    string Key,                    // the ONLY remount key
    LibraryNavTemplate Template,
    LibraryNavLayoutKind Layout,
    float RowExtent,               // Extents: 60 / 44 (outer extent, chrome margin included)
    float CellMin,                 // GridFit: 88/104/120 compact, 116/140/164 full
    float Gap,                     // 8
    bool Lettered,                 // flat projection live (a–z in a LIST view)
    bool Strip,                    // jump strip (a–z in any view)
    bool GridPadding,              // wrapper padding (8,8,8,0)
    string ScrollKey)              // per layout FAMILY, see below
{
    public static LibraryNavMount For(string kind, int view, int size, bool alphabetical, ulong lettersKey)
    {
        view = Math.Clamp(view, 0, 3); size = Math.Clamp(size, 0, 2);
        bool grid = view >= 2, compact = view is 0 or 2, lettered = alphabetical && !grid;
        // Order / facts / rows are NOT here: a bound list refreshes them in place. LettersKey is here ONLY while the
        // flat projection is live, because header positions are baked into the per-index extent seed.
        string key = "nav:" + view + ":" + (grid ? size : 0) + (lettered ? ":L" + lettersKey.ToString("x16") : ":F");
        string family = grid ? "grid" : lettered ? "letters" : "list";
        return new(key,
            grid ? (compact ? LibraryNavTemplate.CardCompact : LibraryNavTemplate.Card)
                 : (compact ? LibraryNavTemplate.RowCompact : LibraryNavTemplate.Row),
            grid ? LibraryNavLayoutKind.GridFit : LibraryNavLayoutKind.Extents,
            compact ? 44f : 60f,
            (compact ? 88f : 116f) + size * (compact ? 16f : 24f), 8f,
            lettered, alphabetical, grid,
            "lib:nav:" + kind + ":" + family);
    }
}
```

Notes on the key: `size` is folded to 0 for list views (S/M/L does nothing there — no remount); `LettersKey` moves
only when the set/positions of letter headers move (a row added/removed/renamed under a–z), which is rare once
hydrated. During a–z HYDRATION it would move repeatedly, so the a–z list is GATED (step A4): the navigator stays on
its shimmer until every listed row knows its title — sorting by a title that has not landed is wrong data anyway
(project rule: partial data without a readiness gate is a regression).

**One materializer in the page.** A small cache turns a `LibraryNavMount` into the engine objects, once per key:

```csharp
// User.Page.Library.cs
sealed class NavMountCache                       // page-private; NOT a component
{
    LibraryNavMount _spec; RepeatLayout _layout; ListOptions<LibraryNavItem>? _options;
    public (RepeatLayout Layout, ListOptions<LibraryNavItem> Options) Resolve(in LibraryNavMount spec, LibraryPage page)
    {
        if (_options is null || _spec.Key != spec.Key)
        {
            _spec = spec;
            _layout = spec.Layout == LibraryNavLayoutKind.GridFit
                ? RepeatLayout.GridFit(spec.CellMin, spec.Gap)
                : RepeatLayout.Extents(page._extentOf, spec.RowExtent);
            _options = page.OptionsFor(spec);          // base record `with { Scroll = … ScrollKey = spec.ScrollKey … }`
        }
        return (_layout, _options!);
    }
}
```

`ExtentOf` / `RowExtent` read `_mount.RowExtent` (the spec the memo was computed for) instead of peeking `View`.
`ComputeShape` calls `LibraryNavMount.For(...)` itself (it already reads `View`; it must now read `Size` too — a
list<->grid or S/M/L toggle then always republishes the memo, which removes the "memo compares equal so NavList
never re-runs" trap the comment at `:902-904` works around) and publishes the spec INSIDE `NavShape`:

```csharp
sealed record NavShape(int Count, int FlatCount, string OrderKey, string FactsKey, ulong RowsKey,
                       ulong LettersKey, bool Answered, bool TitlesKnown, LibraryNavMount Mount);
```

So there is ONE owner of the view state (the memo) and ONE mint (`LibraryNavMount.For`). `NavList`, `SyncNav`, the
log and the letters effects all read `shape.Mount`; nobody re-derives a key from `View.Peek()`.

**A tree whose shape never changes.**

```
Skel.Region content root  = NavHost()                        ← single-child slot: unkeyed, same type forever
  BoxEl  Direction 0 · Grow 1 · MinHeight 0 · AlignItems Stretch
  ├─ BoxEl Key "nav:body"  ZStack · Grow 1 · Basis 0 · MinWidth 0 · MinHeight 0 · ClipToBounds
  │    ├─ BoxEl Key = shape.Mount.Key   Grow 1 · Direction 1 · MinHeight 0 · Padding = GridPadding ? (8,8,8,0) : 0
  │    │    └─ ItemsView.CreateBound(_items, templateOf(Mount.Template), layout, options)
  │    └─ StickyLetter   Key "nav:sticky"            (present only when Mount.Lettered)
  └─ JumpStrip           Key "nav:strip"             (present only when Mount.Strip)
```

Every conditional sibling is keyed; the keyed list is a CHILD (`ReconcileChildren` honors it); the ZStack body is
always there so the list node is never re-used as something else.

ASCII wireframes (unchanged visuals — this plan changes no pixels at rest):

```
list (view 1)            compact list + a–z (view 0)     grid M (view 3)          compact grid + a–z (view 2)
┌──────────────────┐     ┌────────────────────┬─┐       ┌──────────────────┐     ┌──────────────────┬─┐
│ Artists 309      │     │ Artists 309        │ │       │ Artists 309      │     │ Artists 309      │ │
│ recents a–z alb ≡▦…│   │ recents a–z alb ≡▦…│ │       │ …                │     │ …                │A│
│ [Filter        ] │     │ [Filter          ] │A│       │ ┌──────────────┐ │     │ ┌─────┐ ┌─────┐  │B│
│ ◉ Name           │     │ K  (sticky)        │B│       │ │   (avatar)   │ │     │ │  ◉  │ │  ◉  │  │C│
│   3 albums · 9 s │     │ Klaus Badelt       │C│       │ │    Name      │ │     │ └─────┘ └─────┘  │…│
│ ◉ Name           │     │ Kool & The Gang    │…│       │ └──────────────┘ │     │ ┌─────┐ ┌─────┐  │ │
└──────────────────┘     └────────────────────┴─┘       └──────────────────┘     └──────────────────┴─┘
```

**Selection re-apply without a window.** `NavList()` (the one place that sees a NEW `Mount.Key` before the mount)
computes `IndexOfSlot(SelectedSlot)` for the new projection and writes it into `_navSel` under `_syncingSel`
BEFORE returning the element, when and only when `Mount.Key != _mountedKey`. (It is a plain model write inside the
region effect, not a signal write inside a component render; `SelectionModel` is the mount-frozen object the new
ItemsView will read at its first render.) `SyncNav` stays as the re-assert + adopt-row-0 effect, but it is keyed on
`Mount.Key | OrderKey | sel` and distinguishes the two cases:

* `Mount.Key` changed -> `SyncSelect(idx)` then `StartBringItemIntoView(idx, alignmentRatio: 0.5f)` (centre — a
  deterministic place, D7) because the new family's scroll offset is either its own restored one or 0.
* only `OrderKey` changed (in-place reorder) -> `SyncSelect(idx)` + minimal bring-into-view.
* only `sel` changed by a LIST pick -> nothing (the engine already revealed it).

**Remount is cheap and quiet.** No `Animate` on the keyed list (already true). Two option variants differ only by
`Entrance`: the FIRST mount of the page keeps `StaggerColdRealize = true` (the cold-realize budget ramp);
view/size remounts use `Entrance = null` so the visible window materializes in ONE frame (a 12–20 row window costs
~1–6 ms per the census: `43697 mounts=185 frameMs=2.6`, `47210 mounts=363 frameMs=7.4`). Images: a list<->grid switch
changes `DecodePx` (40 vs 256), so covers legitimately re-decode and get the engine's 120 ms `ShortRevealMs` over
their placeholder colour — keep (it is a reveal over a colour-matched placeholder, not a flash).

---

## 4. Flash / flicker inventory

| # | Source | Where | Verdict |
|---|--------|-------|---------|
| F1 | Wrapper padding toggles while the list stays (the 8-DIP twitch) | `User.Page.Library.cs:917` via RC1 | REMOVE (disappears with A1) |
| F2 | View applies on an unrelated sort click | RC1 accident | REMOVE (A1) |
| F3 | Would-be remount storm on hydration (`OrderKey` in key) | `:940` | REMOVE before it exists (A2) |
| F4 | Navigator shimmer -> rows cross-dissolve (`SkelReveal.StaggerRows`) on first answer | `:869` | KEEP (once per page life; correct) |
| F5 | Cold-realize ramp replaying on every view remount | `:338` `Entrance` in the shared options | GATE: first mount only (A2) |
| F6 | Cover re-decode fade on list<->grid (`DecodePx` 40 vs 256) | `User.UI.cs:586-605`, engine `ImageCache.ShortRevealMs` | KEEP |
| F7 | Scroll offset restored across layout families, then a minimal-scroll jump | `:276`, `:670` | REMOVE (A2: per-family `ScrollKey`; centre on mount) |
| F8 | Unanimated jump when tapping a partially visible card | engine `ItemsView.cs:1465` | REMOVE (E2: animate) |
| F9 | Selection highlight/hover lost on remount | engine `OnSubtreeRemoved` hover exit exists (uncommitted diff); selection via A2 pre-write | KEEP engine fix; A2 closes the selection window |
| F10 | Flyout height change while open | `User.UI.cs:304-309` | REMOVE (M1) |
| F11 | Reader: whole-pane blank + 120 ms fade on every artist click | `Artist.Reader.cs:531-533, :736` | REMOVE (R1: hard cut; then R2 removes the remount itself for data reshapes) |
| F12 | Reader: repeat of F11 on every non-append reshape (`_generation++`) | `:720-727` | REMOVE (R2) |
| F13 | Reader: catalogue blocks arriving facet by facet, interleaving | `ReaderShape.Build :205-214` | GATE (R3: catalogue appears once, as an append) |
| F14 | Reader: per-block `SkelReveal.Soft` blur-rise | `:1570` | CHANGE to `FadeOnly` (R1) |
| F15 | Reader: "all releases · N" ticking 4 -> 51 | `_total` Prop over `TotalReleases()` | GATE with R3 (publish the total when the facets have answered; bare word before — `ScopeWordText` already prints the bare word for 0) |
| F16 | Reader: spine shows one ringed empty square, then covers pop | `Spine()` count rides `CountSignal`; dots re-render; covers decode | KEEP the mechanism; placeholder colour already matches; C2 removes the redundant re-renders |
| F17 | Reader: scroll offset restore per `(triple, visit)` | `:788` | KEEP (R2 makes it irrelevant for data reshapes) |
| F18 | Reader: measured-extent corrections as `TrackCount` lands (`Settle`) | `:1002-1031` | KEEP (it is the sanctioned anchor-preserving repair) |
| F19 | Album pane `s_paneSwap` header-skeleton <-> body | `Album.Pane.cs:190` | KEEP, unverified — check with §6 step 9 |
| F20 | Collapsed drill `s_drillIn` / `s_drillRoot` | `User.Page.Library.cs:997-1005` | KEEP (user-initiated, 160 ms, spec) |
| F21 | Per-landing re-render of every mounted track row / spine dot / tooltip | RC9 | REMOVE (C1, C2) |

---

## 5. Fix plan

Rules that apply to every step: no env switches; no source-text tests; engine defects are fixed in the engine
(`C:\WAVEE\fluent-gpu-pin`), never worked around in the app; the orchestrator alone builds/tests. Steps marked
(parallel) touch disjoint files.

### Wave 1 — make view switching work (A1+A2+A3 are ONE change to one file; A0 parallel)

**A0 (parallel) — pure spec type + tests.**
Files: NEW `src/apps/Wavee.Core/Library/LibraryNavMount.cs` (code in §3.3; place beside `LibraryNavOrder` /
`LibraryLetters` — confirm the folder with a glob, keep it engine-free), NEW
`src/apps/Wavee.Tests/LibraryNavMountTests.cs`.
Tests (pure):
* `Key_ignores_order_and_facts` — the type has no order input at all; assert `For("artists",1,0,false,0).Key ==
  For("artists",1,2,false,123).Key` (size and lettersKey folded away in a plain list).
* `Key_changes_with_view` for all 12 ordered pairs of views.
* `Key_changes_with_size_only_in_grids` (view 2/3: S≠M≠L; view 0/1: equal).
* `Key_carries_letters_only_when_lettered` (alpha+list: differs by lettersKey; alpha+grid: equal).
* `Cell_ladder` = 88/104/120 and 116/140/164; `RowExtent` = 44/60 (pin against `User.NavRowExtent` constants via
  the public consts, not source text).
* `ScrollKey_is_per_family` (list / letters / grid).
* `Strip_without_letters_in_grid`.

**A1 — stable host, keyed children.** File: `User.Page.Library.cs`.
Replace `NavList()` (`:899-933`) with the §3.3 tree:

```csharp
Element NavList()
{
    var shape = _shape!.Value;                       // the ONLY reactive read: Mount rides the shape
    var mount = shape.Mount;
    PrepareMount(mount);                             // selection pre-write + first-mount flag, see A2
    var (layout, options) = _mounts.Resolve(mount, this);
    Element list = new BoxEl
    {
        Key = mount.Key, OnRealized = _onNavMounted,  // A3: the truthful mount signal
        Grow = 1f, Direction = 1, MinHeight = 0f,
        Padding = mount.GridPadding ? new Edges4(Spacing.S, Spacing.S, Spacing.S, 0f) : default,
        Children = [ItemsView.CreateBound(_items!, TemplateOf(mount.Template), layout, options)],
    };
    Element body = new BoxEl
    {
        Key = "nav:body", Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, ZStack = true, ClipToBounds = true,
        Children = mount.Lettered ? [list, _sticky ??= StickyLetter(_stickyLetter, _stickyPush) with { Key = "nav:sticky" }]
                                  : [list],
    };
    return new BoxEl                                  // the region's single child: unkeyed, shape-stable
    {
        Direction = 0, Grow = 1f, MinHeight = 0f, AlignItems = FlexAlign.Stretch,
        Children = mount.Strip ? [body, _strip ??= JumpStrip(_present, _stickyLetter, _jump) with { Key = "nav:strip" }]
                               : [body],
    };
}
```

(If `StickyLetter`/`JumpStrip` return a non-record `Element` that cannot take `with { Key }`, key them at
construction inside `User.UI.cs` — owner of that file does it; it is a one-line change each.)
Delete: `NavKey`, `LayoutOf`, `LayoutFor`, `_layout`, `_layoutKey`, the `View.Value`/`Size.Value` reads in `NavList`,
and the WRONG comment block at `:232-238`. Rewrite the file-header paragraph "REMOUNT VS REFRESH" to state the real
rule: *a key is honored only on a CHILD; the region root is a stable host*.

**A2 — one owner, one mint.** Same file.
* `ComputeShape` (`:424-488`): read `Size.Value`; build `var mount = LibraryNavMount.For(_kind, view, size, alpha,
  lettered ? _letters.Key() : 0UL)`; set `_mountSpec = mount` (a field read by `ExtentOf`/`RowExtent`/`ContentTypeOf`
  instead of `View.Peek()`); compute `titlesKnown` (A4); return it in `NavShape`.
  `_letters.Build(display, mount.RowExtent)`.
* `OptionsFor(in LibraryNavMount)`: `(mount.Lettered ? _navOptionsLettered : _navOptions) with { Entrance =
  _navEverMounted ? null : s_coldEntrance, Scroll = <base scroll> with { ScrollKey = mount.ScrollKey } }`. The base
  records stay constructor-built; the `with` runs once per key inside `NavMountCache`.
* `PrepareMount(mount)`: `if (mount.Key != _pendingKey) { _pendingKey = mount.Key; int idx =
  IndexOfSlot(SlotOfKey(_entity, SelectedKey.Peek())); _syncingSel = true; try { if (idx < 0) _navSel.DeselectAll();
  else _navSel.Select(idx); } finally { _syncingSel = false; } }`.
* `Render`: `string listKey = shape.Mount.Key;`
  `UseEffect(_syncNav, listKey + "|" + shape.OrderKey + "|" + sel + "|" + (fullSearch ? "s" : "b"));`
  `UseEffect(_syncLetters, …)` unchanged. Remove `UseEffect(_noteKey, listKey)` (A3 replaces it).
* `SyncNav`: `remounted` = `shape.Mount.Key != _lastNavKey`; on remount bring into view with `alignmentRatio: 0.5f`,
  otherwise minimal (see §3.3). Skip the bring-into-view entirely when the change came from `OnNavSel` (set a
  `_pickedFromList` flag there, clear it in `SyncNav`).

**A3 — truthful diagnostics.** Same file.
`_onNavMounted = _ => { … }` logs the always-on line from the keyed box's `OnRealized` (fires only on a real mount):
`[ui] library.nav.mount kind= view= size= lettered= key= reason=first|view|size|letters count=`.
A second line from `SyncNav` when only the order moved: `[ui] library.nav.reorder kind= count= selectedIndex=`.
Delete `library.nav.remount`, `NoteNavKey`, `_lastOrderKey/_lastView/_lastSize/_lastCount/_lastLetters`.
Update `.claude/skills/wavee` + memory note "nav measurement harness" consumers if they grep the old name
(`ops/tools/nav-measure.ps1` — check, orchestrator).

**A4 — a–z readiness gate.** Same file. `TitlesKnown` = every row in `_sorted[0..n)` `Knows(Title/Name)`
(one pass, already walking the rows in `FillRowVersions` — fold it in there). `SyncNavLoad`: Pending while
`!Answered` (as today) OR (`sort == Alphabetical && !TitlesKnown && no row demand has failed`). Extract the decision
into the pure `LibraryNavReadiness.IsPending(count, hasFilter, answered, alphabetical, titlesKnown, anyFailed)` in
`Wavee.Core` with tests (the `SetupGating` pattern).

Risks (wave 1): (a) `OnRealized` availability on `BoxEl` — used at `User.UI.cs:273`, fine. (b) the pre-write of
`_navSel` runs inside the region's effect: it must stay a MODEL write (no signal the page render subscribes to) —
`SelectionModel.Version` is subscribed only by non-bound ItemsViews (`ItemsView.cs:872-876`), so no loop. (c)
per-family `ScrollKey` orphans the old `lib:nav:<kind>` offset: harmless. (d) `Size` read in the memo makes S/M/L
recompute the sort for list views: negligible (309 rows) and the key does not move.

### Wave 2 — the reader stops flashing (one file: `Artist.Reader.cs`; R0 parallel)

**R0 (parallel) — pure policy + tests.** NEW `src/apps/Wavee.Core/Library/ReaderMountPolicy.cs`:

```csharp
public static class ReaderMountPolicy
{
    /// The list remounts for what the USER changed and what the layout freezes — never for data landing.
    public static string MountKey(int artist, int scope, int sort, bool narrow)
        => artist + ":" + scope + ":" + sort + ":" + (narrow ? "n" : "w");

    /// Scope 1 shows the catalogue group only once all three facets' FIRST pages have answered (or failed).
    public static bool CatalogueReady(EdgeState albums, EdgeState singles, EdgeState compilations,
                                      bool albumsFailed, bool singlesFailed, bool compilationsFailed)
        => (albums != EdgeState.Unknown || albumsFailed) && (singles != EdgeState.Unknown || singlesFailed)
        && (compilations != EdgeState.Unknown || compilationsFailed);
}
```

(`EdgeState` must be reachable from `Wavee.Core`; if it lives in the app assembly pass three `bool answered`
instead.) Tests: key ignores generation/order; key moves with each of its four inputs; gate truth table incl. failed
facets.

**R1 — no blank, no blur.** `Artist.Reader.cs`.
* Delete `s_listSwap` and `Animate = …` on the keyed list (`:531-533`, `:736`) and the `_measured` flag's use there.
  A keyed swap is synchronous inside one flush: the new band paints from resident `ArtistFields.Identity` in the
  same frame the old one leaves — a hard cut with no empty frame, which is what "clicking feels instant" needs.
* `:1570` `SkelReveal.Soft` -> `SkelReveal.FadeOnly`.

**R2 — data never remounts the list.**
* Remove `_generation`, `_order`, `_orderCount`, `IsAppendOfOrder`, `_lastOrder` (`:564, :570, :582, :709-727,
  :828-835`). `key = ReaderMountPolicy.MountKey(_artist, shape.Scope, shape.Sort, _narrow)`.
* Why this is safe with today's engine: every recyclable slot is a `ReaderItem` that reads `_r._shape!.Value`
  (`:1500`) and returns `Children = [body]` with `body` keyed `blk:<albumSlot>` (`:1572`) — so after a reshape each
  realized slot re-renders, and the keyed child diff swaps exactly the blocks whose album changed (cells are cached
  in `_cells`, a Ready cell mounts with no reveal: `lastBranch == 0`, ungrouped -> `ReconcileSkeletonRegion` plays
  nothing). Count rides `CountSignal` (`:780`). Extents: `Settle` (`:1016-1025`) is already INDEX-based — it
  compares the analytic extent of whatever block now sits at `i` with `_extents[i]` (what the table was last told
  for index `i`) and calls `CorrectMeasuredExtent(Prefix + i, extent)`, the anchor-preserving repair, for realized
  and unrealized rows alike. After a reorder that loop is exactly the re-seed.
* `SeedExtents()` stays for a real remount (`_extentKey != _listKey`).
* Keep `_visit` in the `ScrollKey` (`:788`): a new selection opens at the top.

**R3 — catalogue readiness gate.** In `Compute` (`:880-887`): when `scopeWord == 1 &&
!ReaderMountPolicy.CatalogueReady(...)` pass `default` for the three facet spans (library blocks only) and make
`TotalReleases()` report 0 (bare "all releases" word, F15); `TailKind` returns 1 with a new string
`Strings.Library.FetchingReleases` while gated (add the key to the localization tables — file owner: strings).
Result: the catalogue lands ONCE, below the library group = a pure append.

**R4 — truthful reader log.** Extend `library.reader.shape` with `reason=artist|scope|sort|append|reorder|flags` and
`firstDiff=<index>` computed against the previous block sequence (kept in a small pooled `int[]` of album slots —
this replaces `_order`), and add `[ui] library.reader.mount key=` from the keyed list's `OnRealized`. This is what
confirms or refutes the RC7 "Year landed -> re-sort" suspicion.

Risks (wave 2): (a) an in-place reorder while the user has already scrolled moves content under them; bounded to
the first ~500 ms after a click and removed for the common case by R3. (b) If a block's realized slot index maps to
a block with a very different height, one frame shows the old measured extent before the re-measure — the same
single-frame correction the reader already takes when `TrackCount` lands; acceptable, and E3 (optional) removes it.
(c) `Settle` must run after the shape change in the same flush — it already subscribes to `_shape` (`:1011`).

### Wave 3 — engine (repo `C:\WAVEE\fluent-gpu-pin`; all parallel with waves 1–2)

**E1 — the ignored-key report is always on.** Files: `src/FluentGpu.Engine/Reconciler/Reconciler.cs:1246-1248`,
`src/FluentGpu.Engine/Hooks/ReuseGuard.cs:81-92`.
Drop the `ReuseGuard.CompiledIn && ReuseGuard.Enabled` gate for THIS report only. Cost on the hot path: two
reference reads and, only when both keys are non-empty, one ordinal string compare. De-duplicate in `ReuseGuard`
with a small `HashSet<(string type, string newKeyPrefix)>` capped at 32 entries so a legitimately varying inert key
(the audited "measured width in a root key" sites the contract comment mentions) logs once, not per frame. The app
already routes `Diag.Sink` into the log — verify it does in Release (`App.cs`), otherwise wire it (app, 1 line).
Test (engine, pure reconciler test): mount `SkelRegionEl` whose content root is a BoxEl keyed "a", re-run with "b",
assert `ReuseGuard.Violations` incremented in a Release-configuration test run and the child component instance is
the same (documents the contract).
Also update `docs/design/subsystems/component-props-contract.md` + the `fluentgpu` skill with one sentence: *the
content root of `Skel.Region`, `Show`, a provider and a component is a single-child slot — put remount keys on a
child*.
Deliberately NOT proposed: honoring `Key` inside `ReconcileSingleChild`/`ReconcileSkeletonRegion`. The contract
comment records an audit that found keyed roots relying on in-place update; changing the semantic is a separate,
audited engine project. The app fix (A1) is contract-conformant, not a workaround.

**E2 — pointer tap reveals with animation.** File: `src/FluentGpu.Controls/ItemsView.cs:1465`:
`if (pointer) BringIntoView(i, float.NaN, animate: true);` (WinUI `BringIntoViewOptions.AnimationDesired` default).
Check the existing ItemsView interaction tests for an assertion on the instant offset and update it to assert the
posted Driven chase target.

**E3 (optional, after R2 proves out) — `ItemsViewController.ReseedMeasuredExtents(int fromIndex = 0)`.** Re-seeds
the measured table from the layout's analytic function from `fromIndex` on, preserving the visible anchor (same
rebase path as `CorrectMeasuredExtent`). Lets R2 replace its O(n) correction loop with one call and lets the
lettered navigator drop `LettersKey` from its mount key. Needs: `IMeasuredVirtualLayout` reseed entry point
(`Scene/VirtualLayout.cs`), controller delegate wiring (`ItemsView.cs` near `:1484-1560`), a test beside
`MeasuredVirtualExtentTests.cs` (anchor row keeps its screen position across a reseed; offset 0 stays 0).

### Wave 4 — churn (parallel, disjoint files)

**C1 — `Track.UI.cs:1061`.** Replace the whole-table `Tracks.Changed` read in `EagerTrackRowHost.Render` with a
per-row version memo: `var ver = UseComputed(() => { _ = Entities.ScopeEpoch.Value; _ = Entities.Current.Tracks
.Changed.Value; var t = Entities.Current.Tracks; return (uint)_slot < (uint)t.Count ? t.Version[_slot] : 0u; });
_ = ver.Value;` — the memo still wakes on every batch, but it is a single array read and republishes (re-rendering
the row) only when THIS row's version moved (defect class 6 works in our favour here). Confirm `Table.Version[]` is
bumped for every column a row paints (it is what `LibraryNavItem` relies on, `User.Page.Library.cs:527`).

**C2 — `Artist.Reader.cs:1516-1529` (`SpineDot`).** Same pattern over `(albumSlot, Albums.Version[albumSlot])`
derived from `_r._shape` + index; drop the direct `Albums.Changed` / `_shape` reads from `Render`. (Same file as
wave 2 — schedule after it, same agent.)

### Minor

**M1 — `User.UI.cs:301-312`.** Always emit the Size header + `SelectorBar`; for `v < 2` wrap them in a box with
`Opacity = 0.4f, HitTestVisible = false`. The flyout no longer resizes (D12).

### File ownership for parallel agents

| Agent | Files |
|---|---|
| P1 | `Wavee.Core/Library/LibraryNavMount.cs`, `LibraryNavReadiness.cs`, `Wavee.Tests/LibraryNavMountTests.cs`, `LibraryNavReadinessTests.cs` |
| P2 | `Entities/User.Page.Library.cs` (A1–A4; depends on P1's signatures in §3.3 — code against them verbatim) |
| P3 | `Wavee.Core/Library/ReaderMountPolicy.cs`, `Wavee.Tests/ReaderMountPolicyTests.cs` |
| P4 | `Entities/Artist.Reader.cs` (R1–R4, then C2), strings table for `FetchingReleases` |
| P5 | `Entities/User.UI.cs` (M1, keys on `StickyLetter`/`JumpStrip` if needed), `Entities/Track.UI.cs` (C1) |
| P6 (engine repo) | `Reconciler.cs`, `ReuseGuard.cs`, `ItemsView.cs:1465`, engine tests, contract doc + skill |

Gates (orchestrator, once per wave): `dotnet build Wavee.slnx` Debug + `-c Release`, `dotnet test
src/apps/Wavee.Tests`, and for P6 the engine's own `dotnet build src/FluentGpu.slnx` Debug + Release + its tests
(`--blame-hang-timeout`).

---

## 6. Verification script

Build to a side folder (`-o` verify dir; never stop the owner's instance), launch, open Library > Artists with a
hydrated account, tail the log. Every step lists the line(s) that prove it.

1. **Cold open, sort = recents, view = list.** Expect exactly ONE `library.nav.mount reason=first`. Expect ZERO
   further `library.nav.mount` lines while the count climbs (`library.nav.reorder` lines are fine). Proves RC3/F3.
2. **Flyout: click List, Compact list, Compact grid, Grid (1 s apart).** Each click: one `library.nav.mount
   reason=view` AND, within the same 20 ms, a `frame.churn` whose census has `mounts>0` and `ItemsView` in `top=`.
   On screen: 60-DIP rows with avatar + 2 lines; 44-DIP text rows; 2-column circles, no title; 1–2 column circles
   with title. No 8-DIP twitch without a view change. Selection ring/pill lit on the same artist each time.
   Proves RC1.
3. **Grid: click S, M, L.** One `library.nav.mount reason=size` each; cell widths step 116/140/164 (88/104/120
   compact). **List: click S/M/L (bank disabled, M1)** — no log line, no change. Proves D13.
4. **Toolbar list/grid icons** toggle family keeping compactness: one mount line each.
5. **Sort words recents -> albums -> recents in list view.** ZERO `library.nav.mount`; one `library.nav.reorder`
   each; census `mounts=0`; the selected row is visible after each click. **-> a–z:** one `library.nav.mount
   reason=letters` (lettered list, sticky letter, strip). **a–z in grid:** mount once on entering (strip appears
   = child list change, no list remount is REQUIRED — assert at most one line), none for a–z <-> albums in grid
   beyond the strip's own mount (the list key `nav:3:<size>:F` is identical — assert `mounts` is small, < 60).
6. **a–z list -> Grid -> a–z list** with a selection in the middle of the alphabet: the SAME artist stays selected
   and lit; the reader never changes artist (`library.reader.shape reason=artist` must not appear). Proves S1 closed.
7. **Tap a half-visible card/row at the viewport edge.** The list glides (several `scroll.frames`), no single-frame
   jump. Proves E2.
8. **Click five artists 0.5 s apart, scope = all releases.** Per click: exactly ONE `library.reader.mount`; any
   number of `library.reader.shape` lines but none with `reason=reorder` before the catalogue gate opens, and the
   gate opening logs `reason=append`. Record at 30 fps: no frame in which the reader card is empty; the artist name
   changes on the first frame after the click; no second dim/fade. Census after the first post-click frame:
   `EagerTrackRowHost×N` with N <= rows of blocks that actually landed in that batch (not "all mounted"), no
   `SpineDot×18` runs. Proves RC6–RC9.
9. **Albums tab:** repeat steps 2–5 (the page is shared); click six albums: `album.pane.state` goes
   Header/Pending -> Ready once per album, one `s_paneSwap` per album, no repeats. (Unverified area S2 — if it
   misbehaves, file separately; do not fold into this work.)
10. **Collapse the window under 640 DIP and back.** Navigator returns at `LeftW`; one `library.nav.mount
    reason=first`-like line per re-entry is acceptable (the wide/collapsed arms are different keyed subtrees).
11. **Release-only check for E1:** temporarily run any page that still has an inert keyed root — the log shows one
    `[reuseguard] … in a SINGLE-child slot was IGNORED` line per site, not per frame. After A1 the library page
    must produce none.
12. Restart the app: the navigator opens in the last view/size actually shown (D14).

---

## 7. What could not be determined

* Whether the equal-count reader reshapes (`blocks=7 -> 7`, `5 -> 5`) come from a Year landing (re-sort) or a
  `Saved`/`LikedOnly` flip — both hash into `OrderKey`. R4's `reason=`/`firstDiff=` fields settle it; R2 makes it
  harmless either way.
* Whether a fresh `ItemsView` raises `OnChange` from a pre-populated `SelectionModel` at mount (S1). The design
  closes the window without needing the answer.
* `Album.Pane` behaviour (not in the video).
* Whether `EdgeState` and the facet "failed" bits are reachable from `Wavee.Core` for R0 (fall back to bools).
* Whether `Diag.Sink` is wired to the Wavee log in Release (needed for E1 to be visible) — check `App.cs`.
