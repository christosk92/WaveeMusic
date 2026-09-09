# Engine render contract vs. the artist page — investigation D (2026-09-07)

Read-only investigation, engine `C:\wavee\fluent-gpu` (47 uncommitted files) + app `C:\wavee\waveemusic`
(`feat/library-v3-1`, 483 uncommitted files). Snapshot taken 08:56 WEDT. Every `file:line` below was read at that
time; both trees moved **while I read** — the facts that changed under me are called out where they matter.

**Two things happened during the investigation that change the shape of the work:**

1. The engine lane's props data gate **already landed in the working tree** while I was reading: `ShelfProps<T>.Equals`
   (items by value, delegates ignored, chrome on a second signal, `_latest` forwarders), `ShelfChrome`,
   `ResponsiveBox.Props` with a state-gated `Responsive.Of<TState>` overload, `ReuseGuard.IgnoredDelegateChanged`, and
   seven new gates in `ShelfBindingChecks` (`gate.shelf.props.*`, `gate.responsive.props.*`). Section A judges **that**
   code, not the orchestrator's summary of it (which said "items by key sequence" — the code compares items by VALUE,
   which is the correct choice).
2. The app lane's artist-page section cache **was reverted while I read**. My first `cat` of `ArtistPage.cs` showed the
   16-slot `_secEl/_secKey` cache, `Hold(Held.X, …)`, `_navigate/_playContext` forwarders and `ArtistBodyPlan.SectionKey`;
   my second read (minutes later) showed the plain `Sec(key, label, body)` version, `ArtistBodyPlan.cs` no longer exists,
   and `FakeData.cs` has no diff against HEAD. Section B therefore lists what is **left** to keep/remove/simplify, not a
   revert that is already done.

The user's position this document is written against: *the page should be SIMPLE — read the model, pass it; the engine
is patched, not worked around; animations must not depend on developer-level code.*

Measured today (Release JIT, `%LOCALAPPDATA%\Wavee\logs\wavee-20260907.log`): startup first paint 267.9 ms with only 77.1 ms
in stamped phases; artist cold opens worst 58–118 ms (`reactive` = component re-renders, 5–16 MB `hotAlloc`); show open
worst 97.4 ms (`flush=58.3`, 13 MB); steady state 0.7–2 ms/frame.

---

## A. The reconciler's component update path, and the engine contract that makes a plain page cheap

### A.1 Finding — mechanism, with evidence

**Where props are compared.** `TreeReconciler.Update` (`src/FluentGpu.Engine/Reconciler/Reconciler.cs:680-735`):

- `:682` `if (ReferenceEquals(newEl, oldEl)) return;` — the one element-identity cut-off. A parent that hands back the
  *same* `Element` instance pays nothing for that subtree.
- `:693-696` a `ComponentEl` pairs with the mounted instance when `ComponentType` and `DeriveRenderedOutput` match. The
  factory is **discarded** (autonomous component; `component-props-contract.md` "The model").
- `:702-706` `IPropsHost` → `Runtime.Batch(() => host.ApplyProps(p))` — the component diffs its own fields
  (`PagedShelfCore<T>` is one).
- `:708-717` otherwise `entry.PropsSig` (a `Signal<object?>` created at `MountComponent:976` with the **default comparer**):
  reference short-circuit, then `ps.Value = p` → `Signal<T>.SetIfChanged` (`Foundation/Signals/Signal.cs:61-68`) →
  `EqualityComparer<object>.Default.Equals` → the props **record's synthesized `Equals`**.
- `:726-733` propless reuse under `ReuseGuard.CompiledIn` runs `DebugCheckReuse(nce.Factory())` (a DEBUG frozen-field
  tripwire, `Hooks/ReuseGuard.cs:60-72`); `ReuseGuard.KeyIgnoredInSingleChildSlot` (`:78-93`) reports a `Key` in a
  single-child slot (`ReconcileSingleChild:886-894` pairs by `ElementTypeId` only).

**How record equality behaves for what the app passes.** A C# record compares members with
`EqualityComparer<TMember>.Default`:

- a **delegate** member → `Delegate.Equals` (Target + Method). A lambda that captures a render-local allocates a fresh
  display-class instance per render → **never equal**. A lambda capturing only `this` or nothing → equal.
- an **`Element`** member → the element record's synthesized `Equals` → its `Children` **array** compares by reference
  (`Dsl/Element.cs:360 public Element[] Children`) → two independently built trees are **never equal**; only the same
  instance is.
- an **`IReadOnlyList<T>`** member → reference (arrays don't override `Equals`). This is what makes `Artist`/`Track`/`Album`
  equality fail after every re-projection (see C).

So before the engine lane's change, `PagedShelf.Create(...)` built `new ShelfProps<T>(items, cardAt, title, header, …)`
every parent render (`Controls/PagedShelf.cs:121`) and the gate never held: `PagedShelfCore` re-rendered, wrote `_props`,
every realized `ShelfCardSlot` (which reads `owner._props.Value`) re-rendered and rebuilt its `MediaCard` tree. Eight
shelves + two `ResponsiveBox`es × 25–45 page renders per cold open = the measured 58–118 ms `reactive` frames.

**How a component re-render reaches the tree.** `RunComponent` (`Reconciler.cs:994-1035`): `RenderWithHooks()` →
`ReconcileSingleChild(node, newRendered, entry.Rendered)` → `Update` recursively → for a `BoxEl`, `WriteColumns` with
change detection (`SameLayoutInput:4891-4907`, `SameGridSpec`, `SameSpanShaping:4964-4979`) + `ReconcileChildren`.
Keyed children (`ReconcileWindowCore:3092-3123`): `ReferenceEquals(nk, ok)` → node untouched; same type + key →
`Update` in place; else recycle/mount. A re-render whose output is *structurally identical* therefore costs
O(nodes) column compares — no remounts — but it still costs the **element construction** and the walk.

**`Memo<T>` / `UseComputed` / `UseMemo`.** `Foundation/Signals/Memo.cs`: push-pull with an equality cut-off — an upstream
write marks the memo Dirty and cascades **Check** to its subscribers; at flush the subscriber pulls, the memo recomputes,
and **an equal recompute is silent** (subscribers resolve to Clean without running). `UseComputed`
(`Hooks/RenderContext.cs:583-590`) allocates one such memo per hook cell. `UseMemo(factory, DepKey)` (`:703-718`) is the
deps-gated cache (no tracking). Consequence: **a `UseComputed` whose body returns a fresh array every time never cuts
off** (`ArtistPage.AlbumExpand.cs:686` `…Items.ToArray()` — Part 8 item 4).

**`Skel.Region`.** `MountSkeletonRegion` (`Reconciler.cs:1131`) creates one reconcile effect;
`UpdateSkeletonRegion` (`:1162-1176`) stores the new `SkelRegionEl` and **force-schedules** the effect on every parent
re-render (`_skelForce.Add(idx); eff.Schedule()`); `ReconcileSkeletonRegion` (`:1178-1240`) reads
`loadable.State.Value` and `loadable.Value.Value` (via the `Pending`/`Content` thunks, subscribing the effect) and calls
`se.Content()` — i.e. `ArtistPage.Body(...)` — on **every** loadable value write and every page re-render; while
Pending with `ShimmerSource == null` it re-derives the whole shimmer each run (`:1185`). `Content` is a closure over the
parent's render locals, so the engine cannot know whether it would produce the same tree.

**`ReuseGuard`.** Checks (a) frozen scalar fields on propless reused components (`DebugCheckReuse`), (b) keys in
single-child slots, and now (c) `IgnoredDelegateChanged` (working tree, `ReuseGuard.cs` +21) — a re-pushed delegate whose
`Method` changed on a data-gated props record. All `DEBUG || FLUENTGPU_DIAG`, const-folded out of Release.

### A.2 Verdict on the in-flight engine lane

**Confirmed (keep as written):**

| Piece | File:line (working tree) | Verdict |
|---|---|---|
| `ShelfProps<T>.Equals` — `MaxItems` by value; `Items` by reference, else the `min(Count, MaxItems)` prefix element-wise through `EqualityComparer<T>.Default`; `CardAt`/`KeyOf`/`CustomPager`/`OnVisibleRange` ignored | `PagedShelf.cs:144-187` | **Correct.** Value equality on items (not key sequence) is the right rule — a key-only compare would miss a title/cover arrival and leave the card stale. The bound is `MaxItems` (16/50 on Wavee's shelves) so the walk is negligible. |
| `ShelfChrome(Title, Header, HasCustomPager)`, `Header` by reference, on its own signal | `:189-199`, `:280-283` | **Correct.** An `Element` can only ever match by reference (children arrays); a rebuilt header re-renders the shelf's header row (five nodes), never a card. |
| `_latest` plain field + every delegate invocation through it; `_props.SetIfChanged` drives `_contentRevision` | `:284-300`, `:314-336` | **Correct**, and it is exactly the `[Props]` generator's contract (`SourceGen/Engine/PropsGenerator.cs:20-29`: "delegate props ride a stable latest-write forwarder; a fresh lambda never re-renders"). Delegates are behaviour, not data. |
| `ReuseGuard.IgnoredDelegateChanged` (DEBUG, report-only) | `ReuseGuard.cs` +21 | Fine. |
| `ResponsiveBox.Props` — ungated overload compares `Build` as a delegate (rebuilds with the parent, by design); `Responsive.Of<TState>(state, build)` gates on `state` value | `Responsive.cs:23-49`, `:63-88` | **Correct.** A builder closure IS the data channel of the ungated overload; freezing it is the stale-content bug class. The gated overload is the shelf rule applied to a band: "the builder is a function of (state, width)". |
| `Reconciler.Mount` inherits `Parked` for every node kind + `gate.reconciler.park-mount-inherits-parked` | `Reconciler.cs:621-639`, `NavSuite.cs` +76 | **Correct** (findings 4.1). |

**Three corrections the lane must fold in before it is done:**

1. **Per-card subscription scope.** `ShelfCardSlot.Render` (`PagedShelf.cs:368-384`) reads `owner._props.Value` — the
   *whole* gated snapshot — so one item's value change re-renders **every realized card** of that shelf. The lane's own
   gate admits it (`gate.shelf.props.changed-item … "all realized cards; the props signal is the shelf-wide data seam"`).
   The engine already owns the primitive that scopes this to one card: a `Memo` cut-off. Read the item and the visible
   count through `UseComputed` (A.3.1). Tighten the gate to `== 1`.
2. **The measured probe layer is rebuilt on every core render.** `MeasuredVirtualBody` (`PagedShelf.cs:883-933`) builds
   `sampleCells[_probeSample]` with `CardAt(idx, cardW)` on **every** render, and `_probeSample` is set when `needProbe`
   (`:443`) and never reset — so after the first probe every chrome-only re-render (the artist page rebuilds `header:` each
   Body run) still constructs up to **24 card trees per shelf** for an invisible, record-culled host. Return a cached probe
   host element when `!needProbe` so the reconciler's `ReferenceEquals` short-circuit takes it (A.3.2).
3. **The items gate is only as good as the record's `Equals`.** `EqualityComparer<T>.Default` on `Track`/`Album` compares
   their `IReadOnlyList<ArtistRef> Artists` by **reference**, and `CatalogReadView.Track/Album` rebuild that array on every
   read (`Backend/Queries/CatalogReadView.cs:79-80`, `:163`). So the gate holds for `RelatedArtist`/`PlaylistRef`/
   `MusicVideo`/`Concert`/`MerchItem`/`Image` shelves (scalars + `Image`) and **does not hold** for Appears-on (`Album`),
   Popular-releases (`Album`) and the top-tracks chart (`Track`) until the domain records carry structural equality — C.3.1.
   The lane's contract-doc paragraph must say so ("Immutable domain records therefore compare by VALUE" is only true when
   the record's list members do).

**Answers to the four questions asked:**

- *Should the reconciler compare delegates by Method + Target-type?* **No.** A closure's captured values are invisible;
  Method+Target-type equality would declare `() => go(a.Uri)` equal across different `a` and — without a forwarder —
  strand the old handler. The correct contract is the one the `[Props]` generator and the lane already implement:
  delegates are **excluded from the gate and always forwarded latest-write**. `Method` inequality is useful only as the
  DEBUG drift note the lane added. (Static lambdas are compiler-cached and compare equal for free; instance lambdas that
  capture only `this` compare equal by `Delegate.Equals`; the app need not care.)
- *Should the engine memoize `Skel.Region` content?* **No** — the `Content` thunk closes over the parent's render locals,
  so the engine cannot know its inputs. The cut belongs one level up, at the **loadable**: `Loadable.SetReady(v)` writes
  `Value.Value = v` through `Signal<T>.SetIfChanged`, so a value-equal (or reference-equal) model does not re-run the region
  effect at all. That is what C delivers. The remaining re-runs (parent re-render, a real model change) are correct and,
  with the shelf gate + A.3.2, cost element construction + an O(nodes) diff — no card rebuilds.
- *Should the engine offer an element-identity cut-off for unchanged subtrees so apps never need section caches?* It
  **already exists** (`Update:682`, `ReconcileWindowCore:3102`). The way a plain declaration obtains it is a **component
  with data props** — `PagedShelf.Create(items, …)` *is* `Embed.Comp(new ShelfProps<T>(…), …)`, `DiscographySection` is a
  propless autonomous component, `ArtistPopular` likewise. Once the props gate holds, a section cache in the page is
  redundant; the 16-slot cache was compensating for the gate not holding. No new engine feature is needed here.
- *Delegate/Element-carrying props gating rules (the contract):* **data by value, delegates ignored + forwarded,
  `Element` props by reference on a separate chrome signal, collections by bounded element-wise value equality.** That is
  what the lane wrote; A.3 only scopes the card subscription, stabilizes the probe layer, and documents the record
  dependency.

### A.3 Code (engine, `C:\wavee\fluent-gpu`)

#### A.3.1 `PagedShelf.cs` — scope the card subscription to its own item (replace `ShelfCardSlot`, `:368-384`)

```csharp
    sealed class ShelfCardSlot(PagedShelfCore<T> owner, BoundItemScope<T> scope) : Component
    {
        public override Element Render()
        {
            // Memo cut-offs (Foundation/Signals/Memo.cs): the gated props signal still notifies every slot when ANY item
            // moved, but a slot whose OWN item (and the visible count) compares equal resolves Check→Clean and does NOT
            // re-render. Records compare by value, so a rebuilt-but-equal item is silent here even when the shelf-wide
            // gate could not hold (a sibling item changed).
            var item = UseComputed(() => scope.Item.Value).Value;
            int visible = UseComputed(() => owner._props.Value!.VisibleCount).Value;
            int index = scope.Index.Value;
            if ((uint)index >= (uint)visible) return new BoxEl();
            var d = owner._latest;                                // BEHAVIOUR — the newest builders, never subscribed
            float width = owner._cardWidthAgnostic ? owner._maxCardW : owner._cardW.Value;
            if (width <= 0f) width = owner._layout?.CardW ?? owner._maxCardW;
            string key = d.KeyOf?.Invoke(item, index) ?? index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new BoxEl { Direction = 1, Grow = 1f,
                Children = [d.CardAt(item, index, width) with { Key = key }] };
        }
    }
```

`BoundItemsSource.ItemSignal.Value` (`Controls/BoundItemsSource.cs:100-107`) reads the `_props` snapshot signal, so the memo
subscribes to `_props` and `slotIndex`; a `_props` write recomputes the memo; `EqualityComparer<T>.Default` on the record
decides whether this slot's render-effect runs. No allocation per re-check (the memo's `_compute` delegate is stable).

#### A.3.2 `PagedShelf.cs` — a reference-stable probe host when not probing (`MeasuredVirtualBody`, `:904-933`)

Add two fields next to `_probeHostNode` (`:344`):

```csharp
    Element? _probeHostEl;         // the last BUILT probe host — handed back by reference while not probing
    float _probeHostCardW = float.NaN;
```

Replace the block `:906-933` (`var sampleCells = …` through `Children = sampleCells, };`) with:

```csharp
            // Build the sample cells ONLY while a probe is owed (or the width they were built at moved). Otherwise hand
            // back the SAME element instance: TreeReconciler.Update short-circuits on ReferenceEquals, so a chrome-only
            // core re-render (a parent that rebuilds its `header:` every render) no longer constructs up to
            // MeasuredSampleCap card trees for an invisible, record-culled host. The cached cells' closures are inert —
            // the host is Opacity=0, HitTestVisible=false and culled by the needProbe effect.
            Element probeHost;
            if (needProbe || _probeHostEl is null || MathF.Abs(_probeHostCardW - cardW) > MeasureTolerance)
            {
                // Do NOT clear _probeNodes on a RE-probe (cardW changed): the sample cells are KEYED, so the reconciler
                // reuses the realized nodes in place and OnRealized never re-fires — cleared handles would stay null.
                var sampleCells = new Element[_probeSample];
                for (int i = 0; i < _probeSample; i++)
                {
                    int idx = i;
                    sampleCells[i] = new BoxEl
                    {
                        Key = "mshelf-probe:" + idx,
                        Direction = 1, Width = cardW,
                        OnRealized = h => _probeNodes[idx] = h,
                        Children = [ CardAt(idx, cardW) ],
                    };
                }
                probeHost = new BoxEl
                {
                    Opacity = 0f, HitTestVisible = false,
                    Direction = 0, AlignItems = FlexAlign.Start,
                    OnRealized = h => _probeHostNode = h,
                    Children = sampleCells,
                };
                _probeHostEl = probeHost;
                _probeHostCardW = cardW;
            }
            else probeHost = _probeHostEl;
```

(`CardAt(idx, cardW)` reads `_props.Value` — the probe cells re-probe on a content revision through `needProbe`, which
already includes `_measuredContentRevision.Value != contentRevision` at `:441-443`.)

#### A.3.3 `docs/design/subsystems/component-props-contract.md` — one paragraph under "Retained shelf authoring"

Append after "…and `ShelfProps<T>` for the live set…":

> **What "compared by value" requires of the item type.** The items gate uses `EqualityComparer<T>.Default`. A C# record
> compares an `IReadOnlyList<>` member by *reference*, so a record that carries a list (`Track.Artists`, `Album.Artists`,
> `Artist.TopTracks`, …) only gates when the projection hands back the same list instance or the record overrides
> `Equals` structurally. The application's domain records must do one or the other; the engine does not deep-compare
> arbitrary lists (unbounded, and it would have to know which members are data). Delegate members are never part of the
> gate: they are forwarded latest-write (`PagedShelfCore._latest`, the `[Props]` forwarder), so a card's paint must be a
> function of its item plus stable behaviour the closure captures.

### A.4 Probes (VerticalSlice, `src/FluentGpu.VerticalSlice/Suites/ShelfBindingChecks.cs`)

1. **Tighten** `gate.shelf.props.changed-item` (currently `oneItemRebuilds > 0`) to exactly one:

```csharp
        Check("gate.shelf.props.changed-item rebuilds ONLY the changed card (per-slot memo cut-off)",
            oneItemRebuilds == 1 && !FindTextNode(scene, strings, scene.Root, "g-2!").IsNull,
            $"rebuilt={oneItemRebuilds} (expected 1)");
```

2. **New** `gate.shelf.props.measured-chrome` — a `measured: true` twin of `GateProbe` (add `measured: true` to the first
   `PagedShelf.Create` in a second probe instance, drop `cardHeight`): after `Settle`, set `HeaderText.Value = "head-b"`,
   `Settle`, assert `probe.HeaderCards == 0` — the probe host must not have re-invoked `cardAt`. Before A.3.2 this
   reads 24 (or `min(8, 24)` = 8 for the 8-item probe); after, 0.

3. **New** `gate.shelf.props.item-memo-alloc` — after the changed-item check, `host.RunFrame().HotPhaseAllocBytes == 0`
   (already covered by `gate.shelf.props.gate adds no hot-phase allocations`; keep it, it now also covers the memos).

### A.5 Verification

`dotnet build src/FluentGpu.slnx` (Debug **and** Release), `dotnet run --project src/FluentGpu.VerticalSlice -- --suite scroll`
then the full suite ("ALL CHECKS PASSED"). `powershell -File docs\design\check-canon.ps1` after the contract-doc edit.

---

## B. The artist page — keep / remove list, file by file (against the working tree at 08:56)

Ground truth first: the section cache (`_secEl/_secKey`, `Sect`, `ArtistBodyPlan.SectionKey`), the held collections
(`Hold(Held.X, …)`, `SameItems`), the forwarders (`_navigate`, `_playContext`, `accentKey`), the memoized hero pieces and
the bio-parse memo are **no longer in the tree** (`ArtistBodyPlan.cs` absent; `grep _secEl|Held\.|Hold(` over
`Features/Detail` returns only unrelated `PlaylistReorderDefer.TryHold`; `git diff FakeData.cs` is empty). Nothing to
revert. What follows is the state the page must **end** in — one lease, read the model, pass it.

| File | Keep | Change |
|---|---|---|
| `ArtistPage.cs` | `QueryHooks.Use` loading (the refactor, not the lane); `_artistView` single lease handed to `ArtistPopular` (structural: one node, one binding, one post per publication); `HydrationGaps.Note` (always-on log); `Sec(key, label, body)`; the LINQ `Where(...).ToArray()` partitions (three passes over ≤ 50 albums — not a measured cost; "no worry about optimizations"). | (1) the seed memo moves out of the page into `QueryHooks` (C.3.4) — page line becomes `() => PendingArtist(uri)`; (2) the two demand effects become **auto-tracked** and stop reading the five range signals in `Render` (each shelf's `onVisibleRange` currently re-renders the whole page); (3) `fansList` is read inside the region content thunk, not in `Render`. Full method in B.1. |
| `ArtistPage.Shelves.cs`, `ArtistPage.Discography.cs` | Everything: the item-based `PagedShelf.Create(items, (item, i, w) => …, keyOf:, maxItems:, onVisibleRange:)` calls are the new engine API and are already the plain declaration. | — |
| `ArtistPage.Biography.cs` | As is (`RichText.Of` parse per band build: a ~KB HTML walk, microseconds; if a census — D.3 — ever names it, precompute a run list on the model, never memoize in the page). | — |
| `ArtistPage.Hero.cs` | `_measuredIdentityH` + `HeroArt(url, width, measuredIdentityH, …)` — a layout correctness fix (findings Part 5 §7), unrelated to render cost. | — |
| `ArtistPage.TopTracks.cs` | `new ArtistPopular(popular, _artistView!, …)`. | — |
| `ArtistPage.AlbumExpand.cs` | `DiscographySection` owning its own `ArtistReleasesQuery`; `DiscoGrid(Func<int> count, Func<int, Album?> itemAt, …)`. | `:686` `UseComputed(() => …Items.ToArray())` → return the list reference so the memo cut-off works once C makes `Value` stable (B.2). |
| `ArtistPopular.cs` | `ChartRow.Props` record + `Embed.Comp(props, factory)` (the engine idiom); `keyOf:`; `RetryTracks`. | — |
| `Wavee.Core/Fakes/FakeData.cs` | Unmodified. | — |
| `Wavee.Core/Domain/Models.cs` | — | Structural equality on the list-bearing records (C.3.1) — this is what lets the shelf gate hold for `Album`/`Track` shelves and lets the loadable coalesce. |

### B.1 `ArtistPage.Render` — the whole method as it should read (`Features/Detail/ArtistPage.cs:65-215`)

Only the block between `string uri = UriOf(route) ?? "";` (`:79`) and `bool artistReady = …` (`:124`) changes; the rest of
the method (tint binder, anchors reset, page signals, `Skel.Region`, `ScrollView`, `Ctx.Provide`) stays exactly as is,
except the `content:` line. Replace `:81-116` with:

```csharp
        // ONE lease for this node. The seed is a thunk: QueryHooks evaluates it once per specification (inside its own
        // spec-keyed effect), so the pending shape — six albums, tracks, covers, the SynthExtras magazine — is built once
        // per artist and never on a render. ArtistPopular reads this same presentation (no second lease).
        var artistView = QueryHooks.Use(Context, svc.Queries, new ArtistDetailQuery(svc.CatalogScope, uri),
            () => PendingArtist(uri), new QueryDemand(true, QueryPriority.Visible,
                [new("tracks", 0, 10), new("popular", 0, ArtistPopularTracks.ExtendedCap), new("appears", 0, 12)], []));
        _artistView = artistView;
        var artist = artistView.Loadable;
        // Demand follows the shelves' visible windows. AUTO-TRACKED (no deps): the effect re-runs when any signal it
        // reads changes — the binding, or a shelf's range — and it runs in the passive phase, so a shelf paging does
        // NOT re-render this page (the ranges are not read in Render). The binding coalesces an equal demand itself.
        UseEffect(() =>
        {
            if (artistView.Binding.Value is not { } binding) return;
            var appears = _appearsRange.Value; var related = _relatedRange.Value;
            var playlists = _playlistsRange.Value; var videos = _videosRange.Value;
            binding.SetDemand(new QueryDemand(true, QueryPriority.Visible,
                [new("tracks", 0, 10), new("popular", 0, ArtistPopularTracks.ExtendedCap),
                 new("appears", appears.First, appears.Last), new("related", related.First, related.Last),
                 new("playlists", playlists.First, playlists.Last), new("videos", videos.First, videos.Last)], []));
        });
        var fansView = QueryHooks.Use(Context, svc.Queries, new SavedArtistsQuery(svc.CatalogScope),
            static () => (IReadOnlyList<Artist>)Array.Empty<Artist>(), new QueryDemand(true, QueryPriority.Visible, [], []));
        UseEffect(() =>
        {
            if (fansView.Binding.Value is not { } binding) return;
            bool useFans = artist.Value.Value.Extras?.Related is not { Count: > 0 };
            var range = _fansRange.Value;
            var windows = useFans
                ? fansView.Loadable.Value.Value.Select((fan, index) => (fan, index)).Where(pair => pair.fan.Uri != uri).Take(12)
                    .Skip(range.First).Take(Math.Max(0, range.Last - range.First))
                    .Select(pair => new RowWindow("artists", pair.index, pair.index + 1)).ToArray()
                : Array.Empty<RowWindow>();
            binding.SetDemand(useFans ? new QueryDemand(true, QueryPriority.Visible, windows, []) : QueryDemand.None);
        });
```

and change the region's content line (`:164`) so the fans list is read by the region effect, not by this render:

```csharp
        var scroll = ScrollView(Skel.Region(artist,
            content: a => Body(a, fansView.Loadable.Value.Value, svc, go, bridge, compactInteractive, pageScroll, pageViewportHeight, pageAtEnd),
```

Delete the now-unused `var fansList = …;` line. `Body` is unchanged. Net effect: `ArtistPage.Render` subscribes to the
route, the contexts and `AppearancePrefs.Epoch` only; the region effect subscribes to the two loadables; the demand effects
subscribe to the ranges. A shelf paging costs one `SetDemand`; a fans/artist publication costs one `Body` run **only when
the model changed by value** (C).

Why the auto-tracked overload is the right one: `RenderContext.cs:596-600` — "DEFAULT (no deps) = AUTO-TRACKED: the body
runs with signal-read tracking; any signal it read re-runs it … Runs in the passive-effect drain (after paint)". The
`DepKey.From(HashCode.Combine(...))` variant needed the render to read the signals, which is the re-render trigger being
removed.

### B.2 `ArtistPage.AlbumExpand.cs:686-702` — return the reference, not a copy

```csharp
        // A memo cuts off on VALUE (Memo.cs push-pull); handing back the query's own list keeps that true — the
        // previous `.ToArray()` produced a fresh array every recompute, so the cut-off never fired and the grid, the era
        // plan and the header re-rendered on every publication.
        var itemsSignal = UseComputed(() => releases.Loadable.Value.Value.Items);
        _items = itemsSignal;
        IReadOnlyList<Album> items = itemsSignal.Value;
```

with the field `IReadSignal<IReadOnlyList<Album>>? _items;` and the `DiscoGrid` thunks
`() => _items!.Value.Count` / `index => (uint)index < (uint)_items!.Value.Count ? _items.Value[index] : null`.
`DiscographyEraBands.PlanAlbums(items)` takes `IReadOnlyList<Album>` — verify its signature; if it requires an array, it is
the one place that may allocate (`items as Album[] ?? items.ToArray()` inside the plan, not in the memo).

### B.3 Idiom for stable delegates (what the page provides, if anything)

Nothing. With the engine contract in A the page passes lambdas freely: card builders capture `go`/`play`/`this`, and the
shelf forwards the newest. The only rule the page keeps is the one the contract states: **what a card paints is a
function of its item** — state like "saved"/"playing" belongs on a signal the card reads (`TrackRow.StateOf` already does
this), never captured by the closure.

---

## C. Publication rate — the seam that makes 25–45 publications cost nothing when sections did not change

### C.1 Finding — the chain, with evidence

Per publication: `QueryService.Node.Recompute` (`Backend/Queries/QueryService.cs:312-405`) →
`_definition.Read(read)` (**re-projects the whole `Artist` graph**, `CatalogQueryDefinitions.cs:229-245` +
`CatalogReadView.Artist:171-189`, every `.Select(...).ToArray()` fresh) → the change gate at `:385-390`
(`!EqualityComparer<T>.Default.Equals(_current.Value, projected.Value)` — false for a re-projected `Artist` because its
list members compare by reference, so **every re-projection is "changed"**) → `new QuerySnapshot<T>(++_revision, …,
projected.Value, …)` (`:391`) → `Handle.Publish` → `Subscription.Publish` (`:806-840`) → `QuerySignalBinding<T,TModel>.OnNext`
→ `Enqueue` → pool `Absorb` (`App/Queries/QuerySignalBinding.cs:325-355`; its only cut-off is
`ReferenceEquals(value.Value, _lastProjectedValue)` at `:331`, which cannot hit for a re-projected value) → `Schedule` →
UI post `Deliver` → `QueryHooks` published callback (`QueryHooks.cs:46-56`) → `state.Loadable.SetReady(view)` →
`Value.Value = view` (`Loadable.cs:44`, through `Signal<T>.SetIfChanged`, record `Equals` → **not equal** → notify) → the
region effect → `Body` → fourteen sections.

Two things follow. (a) The `Signal<Artist>` coalescing that would stop the chain at the loadable exists already; it only
needs `Artist.Equals` to be true when nothing visible changed. (b) When a facet DID change (one related artist's name
arrived), `Body` must run — and then the shelf gate (A) keeps every unchanged shelf's cards mounted, provided *their*
items compare equal (the same record-equality dependency).

So the fix is not "publish less" — each of the 25–45 publications is a real per-key commit — it is "make equality true
where nothing changed", in three layers **below the page**.

Note the app already has two hand-rolled instances of exactly this: `DetailPage.cs:91`
`if (LikedFactsRules.TracksEquivalent(current.Tracks, next.Tracks)) next = next with { Tracks = current.Tracks };` and the
`ReferenceEquals` fast path in `Absorb`. C.3 makes both unnecessary as special cases.

### C.2 Verdict

- **C1 — domain records compare structurally** (`Wavee.Core/Domain/Models.cs`): `Artist`, `ArtistExtras`, `Album`,
  `Track`, `Show`, `Playlist` override `Equals`/`GetHashCode` with sequence equality on their list members. Engine-free,
  unit-testable, and it is what makes `Signal<Artist>`, `ShelfProps<T>.Equals`, `Memo<T>` and the query service's own
  change gate all tell the truth.
- **C2 — the query service carries the previous `Value` forward when equal** (`QueryService.cs:385-392`): a status-only or
  demand-wave recompute that re-projects to an equal value publishes the **same instance**, so every downstream reference
  fast path (`Absorb:331`, `ReferenceEquals` in `Update:682`, `ShelfProps.Items` reference check) hits.
- **C3 — the binding's model-level cut-off** (`QuerySignalBinding<T,TModel>.Absorb`): an equal-by-value projection keeps
  `_lastModel`; covers the mapped bindings (`DetailPage` → `DetailModel`, `HomeModules.Artists`, `LibraryPage`).
- **C4 — the seed becomes a thunk in `QueryHooks`** (the "PendingArtist memo" lives in the pipeline).
- The page does nothing.

### C.3 Code (app, `C:\wavee\waveemusic`)

#### C.3.1 `src/apps/Wavee.Core/Domain/ListEquality.cs` (new) + record overrides in `Models.cs`

```csharp
using System.Collections.Generic;

namespace Wavee.Core;

/// <summary>Sequence equality for the list members of the domain records. A C# record compares an
/// <c>IReadOnlyList&lt;T&gt;</c> member by REFERENCE, and the catalog read view rebuilds those lists on every
/// projection, so without this every re-projection reads as "changed" all the way down to the page — the query
/// service's change gate, <c>Loadable.Value</c>'s signal comparer, the engine's props gate and every memo cut-off.
/// Reference-equal first (the common case once <c>QueryService</c> carries an equal value forward), then element-wise
/// through the element type's own equality.</summary>
public static class ListEquality
{
    public static bool Sequence<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Count != b.Count) return false;
        var cmp = EqualityComparer<T>.Default;
        for (int i = 0; i < a.Count; i++) if (!cmp.Equals(a[i], b[i])) return false;
        return true;
    }

    /// <summary>Order-insensitive hash of a list's LENGTH only — a structural hash would walk every element on every
    /// dictionary insert, and these records are hashed by <c>QueryService._nodes</c> keys only through their specs,
    /// never through their values. Equality is what matters; the hash only has to agree with it.</summary>
    public static int Hash<T>(IReadOnlyList<T>? a) => a is null ? 0 : a.Count;
}
```

`Models.cs` — add a body to each record (the positional parameters are unchanged; declaring `Equals(TSelf?)` in the body
replaces the synthesized one, `GetHashCode` must follow):

```csharp
public sealed record Artist( /* unchanged positional parameters, Models.cs:70-89 */ )
{
    public bool Equals(Artist? o)
        => o is not null && (ReferenceEquals(this, o)
           || (Id == o.Id && Uri == o.Uri && Name == o.Name && Equals(Image, o.Image)
               && MonthlyListeners == o.MonthlyListeners && Followers == o.Followers && Bio == o.Bio
               && Verified == o.Verified && WorldRank == o.WorldRank && Equals(HeaderImage, o.HeaderImage)
               && Equals(Pinned, o.Pinned) && Equals(Extras, o.Extras)
               && AlbumsTotal == o.AlbumsTotal && SinglesTotal == o.SinglesTotal && CompilationsTotal == o.CompilationsTotal
               && Equals(LatestRelease, o.LatestRelease)
               && ListEquality.Sequence(TopAlbums, o.TopAlbums) && ListEquality.Sequence(TopTracks, o.TopTracks)
               && ListEquality.Sequence(AppearsOn, o.AppearsOn) && ListEquality.Sequence(PopularReleases, o.PopularReleases)));
    public override int GetHashCode() => HashCode.Combine(Id, Uri, Name, ListEquality.Hash(TopTracks), ListEquality.Hash(TopAlbums));
}

public sealed record ArtistExtras( /* unchanged, :107-118 */ )
{
    public bool Equals(ArtistExtras? o)
        => o is not null && (ReferenceEquals(this, o)
           || (Equals(Tour, o.Tour) && Equals(WatchFeed, o.WatchFeed) && Equals(PreRelease, o.PreRelease)
               && ListEquality.Sequence(Concerts, o.Concerts) && ListEquality.Sequence(Merch, o.Merch)
               && ListEquality.Sequence(Playlists, o.Playlists) && ListEquality.Sequence(MusicVideos, o.MusicVideos)
               && ListEquality.Sequence(TopCities, o.TopCities) && ListEquality.Sequence(ExternalLinks, o.ExternalLinks)
               && ListEquality.Sequence(Gallery, o.Gallery) && ListEquality.Sequence(Related, o.Related)));
    public override int GetHashCode() => HashCode.Combine(ListEquality.Hash(Related), ListEquality.Hash(Playlists), ListEquality.Hash(MusicVideos));
}

public sealed record Album( /* unchanged, :270-284 */ )
{
    public bool Equals(Album? o)
        => o is not null && (ReferenceEquals(this, o)
           || (Id == o.Id && Uri == o.Uri && Name == o.Name && Equals(Cover, o.Cover) && Year == o.Year
               && TrackCount == o.TrackCount && Kind == o.Kind && Label == o.Label && Copyright == o.Copyright
               && ReleaseDate == o.ReleaseDate && CourtesyLine == o.CourtesyLine && ReleaseDatePrecision == o.ReleaseDatePrecision
               && DiscCount == o.DiscCount && ShareUrl == o.ShareUrl && IsPreRelease == o.IsPreRelease && PreReleaseEnd == o.PreReleaseEnd
               && ListEquality.Sequence(Artists, o.Artists) && ListEquality.Sequence(Tracks, o.Tracks)
               && ListEquality.Sequence(MoreByArtist, o.MoreByArtist) && ListEquality.Sequence(ArtistsDetailed, o.ArtistsDetailed)
               && ListEquality.Sequence(OtherVersions, o.OtherVersions)));
    public override int GetHashCode() => HashCode.Combine(Id, Uri, Name, Year, TrackCount, ListEquality.Hash(Tracks));
}

public sealed record Track( /* unchanged, :285-338 */ )
{
    public bool Equals(Track? o)
        => o is not null && (ReferenceEquals(this, o)
           || (Id == o.Id && Uri == o.Uri && Title == o.Title && Equals(Album, o.Album) && DurationMs == o.DurationMs
               && IsExplicit == o.IsExplicit && Equals(Image, o.Image) && AddedAt == o.AddedAt && AddedBy == o.AddedBy
               && PlayCount == o.PlayCount && Origin == o.Origin && Availability == o.Availability && AvailableAt == o.AvailableAt
               && Source == o.Source && ContextUid == o.ContextUid && Isrc == o.Isrc && TempoBpm == o.TempoBpm
               && MusicalKey == o.MusicalKey && CamelotCode == o.CamelotCode && CamelotColor == o.CamelotColor
               && CanonicalUri == o.CanonicalUri && Year == o.Year && Chart == o.Chart
               && ListEquality.Sequence(Artists, o.Artists) && ListEquality.Sequence(Tags, o.Tags)));
    public override int GetHashCode() => HashCode.Combine(Id, Uri, Title, DurationMs, PlayCount, ListEquality.Hash(Artists));
}

public sealed record Show( /* unchanged, :526-528 */ )
{
    public bool Equals(Show? o)
        => o is not null && (ReferenceEquals(this, o)
           || (Id == o.Id && Uri == o.Uri && Name == o.Name && Publisher == o.Publisher && Equals(Cover, o.Cover)
               && Description == o.Description && TotalEpisodes == o.TotalEpisodes && PagedThrough == o.PagedThrough
               && ListEquality.Sequence(Episodes, o.Episodes)));
    public override int GetHashCode() => HashCode.Combine(Id, Uri, Name, TotalEpisodes, ListEquality.Hash(Episodes));
}

public sealed record Playlist( /* unchanged, :433-454 */ )
{
    /// <summary>The query adopted a membership baseline, including an authoritative empty playlist.</summary>
    public bool MembershipLoaded { get; init; }
    public bool Equals(Playlist? o)
        => o is not null && (ReferenceEquals(this, o)
           || (Id == o.Id && Uri == o.Uri && Name == o.Name && Description == o.Description && OwnerName == o.OwnerName
               && Equals(Cover, o.Cover) && TrackCount == o.TrackCount && Equals(Owner, o.Owner) && Capabilities == o.Capabilities
               && Format == o.Format && Source == o.Source && IsPublic == o.IsPublic && BasePermissionRevision == o.BasePermissionRevision
               && Equals(Tuning, o.Tuning) && DaylistExpiresAtMs == o.DaylistExpiresAtMs && DaylistCreatedAtMs == o.DaylistCreatedAtMs
               && DeletedByOwner == o.DeletedByOwner && ChartNewEntries == o.ChartNewEntries && ChartUpdatedAtMs == o.ChartUpdatedAtMs
               && ChartRankType == o.ChartRankType && MembershipLoaded == o.MembershipLoaded
               && ListEquality.Sequence(Tracks, o.Tracks) && ListEquality.Sequence(Collaborators, o.Collaborators)));
    public override int GetHashCode() => HashCode.Combine(Id, Uri, Name, TrackCount, ListEquality.Hash(Tracks));
}
```

Member lists above were read from `Models.cs` at 08:56; `PlaylistTuning`, `Owner`, `PinnedItem`, `Image`, `ChartEntry`,
`AlbumRef`/`ArtistRef` keep their synthesized equality (no list members on the artist path; `Image._mosaicTiles` is
null for every non-playlist image). The reflection test in C.4 fails the build the day a positional member is added
without joining the override.

#### C.3.2 `Backend/Queries/QueryService.cs:385-392` — carry an equal value forward

```csharp
                // An equal re-projection keeps the PREVIOUS instance: every downstream reference fast path (the mapped
                // binding's Absorb, the engine's ReferenceEquals short-circuits, the shelf props' Items check) then hits
                // without a deep compare, and a status-only republication carries the exact object graph it carried before.
                T value = _current is not null && EqualityComparer<T>.Default.Equals(_current.Value, projected.Value)
                    ? _current.Value : projected.Value;
                changed = _current is null || _current.OrderRevision != projected.OrderRevision
                    || _current.Failure != (_queryFailure ?? _preparationFailure)
                    || !ReferenceEquals(value, _current.Value)
                    || _current.Status != status || !_current.Problems.SequenceEqual(problems)
                    || _current.Resources.Count != resources.Count
                    || resources.Any(p => !_current.Resources.TryGetValue(p.Key, out var old) || old != p.Value);
                next = changed ? new QuerySnapshot<T>(++_revision, projected.OrderRevision, value, status, problems)
                    { Resources = resources, Failure = _queryFailure ?? _preparationFailure } : _current!;
```

#### C.3.3 `App/Queries/QuerySignalBinding.cs:325-355` — model-level cut-off in `Absorb`

```csharp
    bool Absorb(QuerySnapshot<T> value)
    {
        bool sameValue;
        TModel previousModel;
        Exception? previousError;
        lock (_gate)
        {
            if (_disposed || value.Revision < _latest.Revision) return false;
            sameValue = _projected && ReferenceEquals(value.Value, _lastProjectedValue);
            previousModel = _lastModel; previousError = _lastProjectionError;
        }
        TModel model;
        Exception? error;
        if (sameValue) { model = previousModel; error = previousError; }
        else
        {
            error = null;
            try
            {
                model = _project(value.Value);
                // Model-level cut-off: a projection that is equal BY VALUE to the last one keeps the last INSTANCE, so
                // the UI-side Loadable/Signal comparers coalesce by reference and nothing re-renders. The projection
                // still ran (it is what decided equality); only its result is de-duplicated.
                if (_projected && previousError is null && EqualityComparer<TModel>.Default.Equals(model, previousModel))
                    model = previousModel;
            }
            catch (Exception ex) { model = default!; error = ex; }
        }
        lock (_gate)
        {
            if (_disposed || value.Revision < _latest.Revision) return false;
            _latest = value;
            _lastProjectedValue = value.Value;
            _projected = true;
            _lastProjectionError = error;
            if (error is null) { _lastModel = model; _latestError = null; }
            else _latestError = error;
            return true;
        }
    }
```

`DetailModel` (`Features/Detail/DetailConfig.cs:33-80`) gets the same `Equals` override pattern over its lists (`Artists`,
`Tracks`, `Fans`, `FeaturedOn`, `MoreByArtist`, `Episodes`, `Topics`, `AlbumArtists`, `OtherVersions`, `Collaborators`;
`UserProfilesById` by reference is acceptable — it is rebuilt with `Collaborators`). Then delete `DetailPage.cs:91`
(`TracksEquivalent` held-collection) — the cut-off above subsumes it, including for `Episodes`, which it never covered
(the show page's 58 ms flush is that gap: every publication re-renders the whole `DetailShell`).

#### C.3.4 `App/Queries/QueryHooks.cs` — the seed as a thunk

```csharp
    public static QueryPresentation<T> Use<T>(RenderContext context, IQueryService? queries, QuerySpec<T>? specification,
        Func<T> seed, QueryDemand? demand = null, bool keepPrevious = false)
        => UseMapped(context, queries, specification, static value => value, seed, demand, keepPrevious);

    /// <summary>Value-seed convenience for cheap seeds (an empty list, null). An expensive pending shape passes a thunk:
    /// it is evaluated once per specification, inside the spec-keyed effect, never on a render.</summary>
    public static QueryPresentation<T> Use<T>(RenderContext context, IQueryService? queries, QuerySpec<T>? specification,
        T seed, QueryDemand? demand = null, bool keepPrevious = false)
        => UseMapped(context, queries, specification, static value => value, () => seed, demand, keepPrevious);

    public static QueryPresentation<TView> UseMapped<T, TView>(RenderContext context, IQueryService? queries,
        QuerySpec<T>? specification, Func<T, TView> project, Func<TView> seed, QueryDemand? demand = null,
        bool keepPrevious = false)
    {
        var state = context.UseMemo(() => new QueryPresentation<TView>(seed()), DepKey.Empty);
        // … unchanged until the effect body …
        context.UseEffect(() =>
        {
            if (specification is null || queries is null) { state.Loadable.SetReady(seed()); return (Action?)null; }
            if (!keepPrevious || !state.Loadable.IsReady || state.Scope != specification.Scope) state.Loadable.SetPending(seed());
            // … rest unchanged …
```

Keep the existing `UseMapped(…, TView seed, …)` overload delegating with `() => seed` so the other 17 call sites compile
unchanged.

### C.4 Tests (`src/apps/Wavee.Tests`, engine-free)

1. `ModelEqualityTests.cs`
   - `RebuiltListsCompareEqual`: two `Artist` graphs built from the same scalars with fresh arrays at every level → `Equal`,
     same `GetHashCode`; change one `Track.Title` deep in `TopTracks` → not equal.
   - `EveryPositionalMemberParticipates` (reflection over the primary constructor of `Artist`, `ArtistExtras`, `Album`,
     `Track`, `Show`, `Playlist`, `DetailModel`): for each parameter, construct a variant with that parameter replaced by a
     distinguishable value (a different string / +1 / a one-element list / a different record) and assert `!Equals`. This
     is a behaviour test over the type's shape — it never reads source — and it is what keeps the hand-written overrides
     honest when a field is added.
2. `QueryServiceValueCarryTests.cs` (via `CatalogQueryTestHost`, the shape of
   `DetailQuerySubscriptionTests.OnlyReferencedOwnerChangesReproject`): acquire `ArtistDetailQuery`, flush, take
   `before = detail.Current`; commit a change to a **referenced** resource that does not alter the projected value (re-accept
   the same `ArtistIdentityValue`); assert `after.Revision != before.Revision` (status/resources moved) **and**
   `ReferenceEquals(after.Value, before.Value)`.
3. `QuerySignalBindingCutoffTests.cs`: a fake `IQueryHandle<int>` whose `Changes` pushes two snapshots with `Value` 1 then a
   new boxed 1 (revision 1, 2); `project = v => new Artist("a", "spotify:artist:a", "A", null, TopTracks: [...fresh array...])`;
   drive `post` synchronously; assert the second `published` callback receives the **same** model reference as the first.

### C.5 Verification

`dotnet build Wavee.slnx` Debug + Release; `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj`; then a cold artist open
with `ops/tools/nav-measure.ps1` — expect `comps=` per worst frame to drop from ~140–204 to the changed sections' components
only, and no `hotAlloc` above ~2 MB on artist routes.

---

## D. First paint (≈200 ms outside every stamped phase) and the show page's 58 ms flush

### D.1 Finding

**Where "unaccounted" lives.** `FrameStats.FrameMs` is `now − frameStart` computed in `UpdateFrameTiming`
(`AppHost.cs:4563-4566`), called at `:3733` — **after** `DrainPassiveEffects()` (`:3729`) and `_strings.Tick()` (`:3730`),
which follow the submit stamp `tSubmit`. Every stamped phase ends at or before `tSubmit` (`FlushMs = tFlush − frameStart`
includes `EnsureSize`, the scroll tick and the FLIP capture; `RecordMs` includes the image pump; `SubmitMs` ends at
`tSubmit`). So `unaccounted = FrameMs − Σ(stamped)` is, to the millisecond, **phase 12: the passive-effect drain** (plus the
string-table tick, which is negligible).

Today's startup line (`seq=20`, 12:35 build): `frameMs=267.9 flush=17.5 layout=26.5 anim=6.5 record=25.4 submit=1.1
fenceWait=0.1 present=0.0` → 190.8 ms in phase 12. The second stall 1.2 s later (`seq=78`): `frameMs=119.4 flush=2.3
record=1.5` → ~115 ms in phase 12.

**What runs in phase 12 on the first frame.** Every mount `UseEffect` of the whole shell. The one that is known to do
synchronous heavy work: `QueryHooks.UseMapped`'s effect (`QueryHooks.cs:44-46`) calls `queries.Acquire(specification)`,
and `QueryService.Acquire` (`QueryService.cs:95`) runs `node.Recompute()` **synchronously on the calling thread** for a
node with no `Current` — `ReadConsistent` → `Definition.Read` → catalog reads + the full projection (`CatalogReadView`).
The raw `Acquire` sites that mount at startup: `App/LibraryStore.cs:35` (`SidebarLibraryQuery`),
`App/PlaybackBridge.cs:1117` (`QueueQuery`), `Features/Sidebar/SidebarProjectionBinder.cs:191/207/571`,
`Features/Home/HomePage.cs:157` or `Features/Detail/DetailPage.cs:100` (the restored route), plus every `QueryHooks.Use`
in the sidebar pane (`Features/Sidebar/Pane/SidebarPane.cs`) and the page. Findings Part 8 item 8 adds the sidebar's
`Rebuild()` running 2–3× per navigation from `OnSourceChanged`. On a **JIT** build each of these is also first-call JIT.
That is the attribution by code; it is not yet a measurement, which is why the first deliverable is the stamp.

**The show page.** `DetailPage.Publish` (`DetailPage.cs:70-95`) → `model.SetReady(next)` with a `DetailModel` whose
`Episodes` array is new every projection (`CatalogReadView.Show:194-195`) → `Signal<DetailModel>` notifies →
`DetailShell` re-renders → `TrackList`/episode rows (`DetailTracks.cs:1031-1064`, `ItemsView.CreateBound` with
`BoundRowContent` per row reading the `_rowsSnapshot` state signal) → every realized row re-renders. `hotAlloc=13 MB` in
one 58 ms flush for 727 nodes is a whole-list row rebuild. The `TracksEquivalent` guard at `DetailPage.cs:91` covers
`Tracks` only. **C.3.3 (DetailModel equality + the `Absorb` cut-off) is the fix**; D adds the diagnostic that names any
residue.

### D.2 Code

#### D.2.1 Engine — stamp phase 12 and the post-submit tail (`AppHost.cs`)

`FrameStats` (`AppHost.cs:21-125`), add after `PresentMs`:

```csharp
    /// <summary>Phase 12: wall time inside <c>DrainPassiveEffects()</c> — every component's passive <c>UseEffect</c>
    /// body that ran this frame. Always-on Stopwatch. This is where a first frame's mount effects (query acquisition,
    /// store subscriptions, sidebar rebuilds) land, and it is NOT inside any other stamped phase.</summary>
    public double PassiveEffectsMs { get; init; }
    /// <summary>Everything after the submit stamp until the frame's end stamp: passive effects + string-table tick +
    /// the video-surface drain. <c>FrameMs − (FlushMs + LayoutMs + AnimMs + RecordMs + SubmitMs + PostSubmitMs)</c> is now
    /// zero by construction on a rendered frame.</summary>
    public double PostSubmitMs { get; init; }
    /// <summary>Top passive-effect owners by time when <see cref="PassiveEffectsMs"/> ≥ 8 ms
    /// ("TypeName=ms,TypeName=ms,…", at most eight); empty otherwise. Built only on such frames (one string).</summary>
    public string SlowEffectOwners { get; init; }
    /// <summary>Top component types by render count when <see cref="ReactiveFlushMs"/> ≥ 16 ms ("Type×n,…", at most
    /// twelve); empty otherwise. Always-on replacement for the <c>FG_RENDER_CENSUS</c> env flag, which is deleted.</summary>
    public string TopRenderTypes { get; init; }
```

`Paint`, replace `:3729-3733`:

```csharp
            long tEffects0 = Stopwatch.GetTimestamp();
            DrainPassiveEffects();                             // 12 passive effects
            long tEffects1 = Stopwatch.GetTimestamp();
            _strings.Tick();                                   // 12.5 reclaim released text ids (behind the reader quarantine)
            if (s_allocDiag) { db = Probe(SegEffects, db, dt0); dt0 = Stopwatch.GetTimestamp(); }

            long tEnd = UpdateFrameTiming(frameStart);         // returns the stamp it used, so PostSubmitMs closes on it
```

(`UpdateFrameTiming` returns `now`: change its signature to `private long UpdateFrameTiming(long frameStart)` and
`return now;` at the end.) In the `LastStats = new FrameStats(…)` initializer add:

```csharp
                PassiveEffectsMs = ToMs(tEffects1 - tEffects0),
                PostSubmitMs = ToMs(tEnd - tSubmit),
                SlowEffectOwners = ToMs(tEffects1 - tEffects0) >= 8.0 ? DescribeSlowEffectOwners() : "",
                TopRenderTypes = reactiveFlushMs >= 16.0 ? _reconciler.DescribeTopRenderTypes() : "",
```

Effect-owner attribution, allocation-free on normal frames. `RenderContext` gains `public Type? OwnerType;` set in
`MountComponent` (`Reconciler.cs:939`): `comp.Context.OwnerType = ce.ComponentType;`. `DrainPendingEffectContexts`
(`AppHost.cs:4705-4710`) becomes instance-scoped and times per context into a fixed 8-slot table:

```csharp
    private readonly (Type? Owner, long Ticks)[] _slowEffects = new (Type?, long)[8];
    private int _slowEffectCount;

    private void DrainPendingEffectContexts(List<RenderContext> contexts, bool layout)
    {
        _slowEffectCount = 0;
        for (int i = 0; i < contexts.Count; i++)
        {
            var ctx = contexts[i];
            var q = layout ? ctx.PendingLayoutEffects : ctx.PendingEffects;
            if (q.Count == 0) continue;
            long t0 = Stopwatch.GetTimestamp();
            Drain(q);
            long dt = Stopwatch.GetTimestamp() - t0;
            NoteSlowEffect(ctx.OwnerType, dt);   // keeps the 8 largest; O(8), no allocation
        }
        contexts.Clear();
    }

    private void NoteSlowEffect(Type? owner, long ticks)
    {
        int slot = _slowEffectCount < _slowEffects.Length ? _slowEffectCount++ : -1;
        if (slot < 0)
        {
            int min = 0;
            for (int i = 1; i < _slowEffects.Length; i++) if (_slowEffects[i].Ticks < _slowEffects[min].Ticks) min = i;
            if (_slowEffects[min].Ticks >= ticks) return;
            slot = min;
        }
        _slowEffects[slot] = (owner, ticks);
    }

    private string DescribeSlowEffectOwners()
    {
        var sb = new StringBuilder(160);   // slow frames only (≥ 8 ms of effects): one string, never on the steady path
        Array.Sort(_slowEffects, 0, _slowEffectCount, Comparer<(Type?, long)>.Create((a, b) => b.Item2.CompareTo(a.Item2)));
        for (int i = 0; i < _slowEffectCount; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(_slowEffects[i].Owner?.Name ?? "root").Append('=').Append(ToMs(_slowEffects[i].Ticks).ToString("0.0", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
```

Render census, always-on and allocation-free (`Reconciler.cs:293-362`): replace `s_renderCensus` + the string-keyed
dictionary with `Dictionary<Type,int> _renderCensus = new(64)`; `BeginRenderCensus` clears it every paint;
`NoteRenderCensus(comp)` does `_renderCensus[comp.GetType()] = n + 1` (a `Type` key: no `Name` string, no boxing);
`DescribeTopRenderTypes()` sorts into the existing `_censusScratch`/`_censusSb` and returns the string — called only when
the frame is slow. Delete `MaybeDumpRenderCensus`, `LastRenderCensusDump`, the `FG_RENDER_CENSUS` flag and the `stderr`
mirror; the host no longer calls `MaybeDumpRenderCensus` at `:3257`.

#### D.2.2 App — print it (`App/NavigationFrameWatch.cs:81-90`)

```csharp
    static string Describe(in FrameStats s) =>
        "frameMs=" + F(s.FrameMs) + " flush=" + F(s.FlushMs) + " reactive=" + F(s.ReactiveFlushMs) + " realize=" + F(s.VirtualRealizeMs)
        + " layout=" + F(s.LayoutMs) + " layoutSolve=" + F(s.LayoutSolveMs) + " layoutEffects=" + F(s.LayoutEffectsMs)
        + " anim=" + F(s.AnimMs) + " record=" + F(s.RecordMs) + " imagePump=" + F(s.ImagePumpMs) + " realizeCatchup=" + F(s.RealizeCatchupMs)
        + " submit=" + F(s.SubmitMs) + " fenceWait=" + F(s.FenceWaitMs) + " present=" + F(s.PresentMs) + " gpu=" + F(s.GpuRenderMs)
        + " effects=" + F(s.PassiveEffectsMs) + " postSubmit=" + F(s.PostSubmitMs)
        + " unaccounted=" + F(s.FrameMs - s.FlushMs - s.LayoutMs - s.AnimMs - s.RecordMs - s.SubmitMs - s.PostSubmitMs)
        + " comps=" + s.ComponentsRendered + " nodes=" + s.NodesVisited + " draw=" + s.DrawNodeCount
        + " cmds=" + s.DrawCommandCount + " hotAlloc=" + s.HotPhaseAllocBytes
        + (s.SlowEffectOwners.Length > 0 ? " slowEffects=" + s.SlowEffectOwners : "")
        + (s.TopRenderTypes.Length > 0 ? " topRenders=" + s.TopRenderTypes : "");
```

(`FenceWaitMs`/`PresentMs` are sub-splits of `SubmitMs` and must not be subtracted twice — the current line does, which
is why `unaccounted` reads `-0.0` on some frames.)

#### D.2.3 The fix once the stamp confirms `Acquire` (apply only if `slowEffects=` names the query-owning components)

`QueryService.Acquire` (`:95`): a node whose identity resource is not resident cannot answer synchronously anyway — its
first `Recompute` only produces an "Unknown" snapshot and kicks cold loads. Run **that** case on the pool:

```csharp
        try
        {
            if (initialize)
            {
                // Warm nodes answer synchronously (a click on a resident album renders Ready on the click frame — the
                // C3 warm-seed contract). A COLD node's first read only yields Unknown + cold requests; doing that on the
                // UI thread inside a mount effect is the first-frame residue, so it goes to the pool and publishes back.
                if (node.HasResidentPrimary) node.Recompute();
                else _ = Task.Run(node.Recompute);
            }
            return handle;
        }
        catch { handle.Dispose(); throw; }
```

with `Node<T>.HasResidentPrimary => _owner._catalog.Peek(_definition.PrimaryKey).Knowledge == Knowledge.Present` (each
`Definition<T>` already names its identity key as the first `Required(...)` entry; expose it as `PrimaryKey`). The
`Handle.Current` before the first recompute must be a valid snapshot: seed `_current` with
`new QuerySnapshot<T>(0, 0, _definition.Empty, new QueryStatus(false, true, false, false), [])` where `Definition<T>.Empty`
is the value `Read` returns for an all-Unknown context (`ArtistDefinition`: `View.Artist(emptyRead, uri, true)` — already
total on nulls). This is gated on the census; do not apply blind.

### D.3 Probes / tests

- Engine `gate.frame.stats.phase12-stamped` (`CoreSuite`): mount a component whose passive `UseEffect` spins 5 ms
  (`Stopwatch` busy-wait, headless); assert `stats.PassiveEffectsMs ≥ 5`, `stats.PostSubmitMs ≥ stats.PassiveEffectsMs`,
  and `|FrameMs − (Flush+Layout+Anim+Record+Submit+PostSubmit)| < 0.5`. Assert `SlowEffectOwners` names the probe's type
  when the drain is ≥ 8 ms and is `""` on the next steady frame; assert `HotPhaseAllocBytes == 0` on that steady frame.
- Engine `gate.reconciler.render-census-alloc`: a 300-frame steady run with the census on allocates 0 bytes in phases 6–13.
- Wavee `NavigationFrameWatchTests` (pure): `Describe` yields `unaccounted=0.0` for a `FrameStats` whose parts sum to
  `FrameMs`, and appends `slowEffects=`/`topRenders=` only when non-empty.

### D.4 Verification

Launch the Release build, read the first `frame.stall` line: `effects=` should carry the ~190 ms and `slowEffects=` name
the owners. Then decide D.2.3. A JIT build's number will remain higher than the AOT publish's; measure the publish for the
figure that ships.

---

## E. Animations on the UI thread — the minimum engine change, and a cheap honest mitigation

### E.1 Finding (restated from the design doc, verified at today's line numbers)

- `_anim.Tick(dtMs)` is phase 7 of `Paint` on the UI thread (`AppHost.cs:3353`); record follows at `:3562`; the render
  thread only submits/presents a finished DrawList (Cut A). Any UI-thread work between two `Paint`s — a 100 ms passive
  effect, a 60 ms reactive flush — stops every animation for exactly that long (`render-thread-animation-design.md` §1.7).
- Time is **lost**, not deferred: `StopwatchFrameTimeSource.NextDeltaMs` clamps to 34 ms (`Hosting/FrameTimeSource.cs:61`);
  `AnimEngine.Tick(float)` takes that step raw (`Animation/AnimScheduler.Parity.cs:104-113`) and PASS 1 accumulates it into
  each row's `ElapsedMs` (`AnimScheduler.cs:66-113`). A 100 ms stall in a 250 ms page slide resumes 34 ms further along:
  the slide both freezes **and** finishes ~66 ms late.

### E.2 Verdict

- **Step 0 of the design doc** (`RenderPriorityPolicy` 16 ms + `FrameBudget` widening, 2–3 days) bounds the engine's own
  reconcile; it does **not** keep motion smooth under an app-side stall (§2.4 of the design). The cheap 16 ms budget is not
  the answer to this question.
- **The minimum engine change that keeps navigation motion smooth under a 100 ms UI stall** is the compositor-group
  variant (design §3.6b + §3.7): the render thread owns the compositor-channel `AnimValue` rows, ticks on the display clock
  and re-submits the last published DrawList with a per-group transform/opacity apply table. Steps 1–5 of §5; **29–41
  days** total; the recommended first commitment is **step 0 + step 1 (the group-apply equivalence spike, go/no-go), 6–9
  days**, with the documented kill condition (opacity may not factor out of the recorder walk).
- **What can be done now (one day, E.3):** remove the *lost time*. Motion still freezes for the stall, but a one-shot
  navigation transition lands at the wall-clock position it should be at, instead of stretching. This is the honest
  ceiling without moving the tick off the UI thread.

### E.3 Code — E-0 "catch up, don't stretch" (engine)

`Animation/AnimValue.cs:30-53` — one flag:

```csharp
    CatchUp       = 1 << 13,  // one-shot structural motion (page slide/fade): on a hitched frame advance by the RAW
                              // wall-clock delta, not the clamped one — the transition lands on time instead of
                              // stretching by the stall. Never set on loops, Driven rows or interaction fades.
```

`Animation/AnimClock.cs` — carry the raw delta beside the clamped one:

```csharp
    public float RawDeltaMs;  // this frame's UNCLAMPED delta (diagnostics + CatchUp rows); == DeltaMs when not hitched
```

and in `Advance`: `RawDeltaMs = wasIdleOrThrottled || FrameId == 0 ? delta : (float)Math.Max(raw, MinDeltaMs);`
(compute `raw` before the clamp branch; on the idle/throttle resume the raw gap is a *pause*, not a stall, so it is not
caught up).

`Animation/AnimScheduler.Parity.cs:104-113`:

```csharp
    public void Tick(float dtMs) => Tick(dtMs, dtMs);

    /// <summary>The host path: <paramref name="dtMs"/> is the clamped step every row takes; <paramref name="rawDtMs"/>
    /// is the unclamped wall delta that <see cref="AnimFlags.CatchUp"/> rows take instead on a hitched frame.</summary>
    public void Tick(float dtMs, float rawDtMs)
    {
        _clock.DeltaMs = dtMs;
        _clock.RawDeltaMs = MathF.Max(dtMs, rawDtMs);
        _clock.NowMs += dtMs;
        _clock.FrameId++;
        Tick(in _clock);
    }
```

`AnimScheduler.cs` PASS 1 (`:76`): `float stepMs = r.Has(AnimFlags.CatchUp) ? clock.RawDeltaMs : step;`

`AnimScheduler.Structural.cs:132-158` — mark the rows the LayoutTransition enter/exit path seeds (the page slide/fade is
exactly this path: `MotionRecipes.PageSlideForward/Back/PageFade`, `Hooks/MotionRecipes.cs:193-209`, seeded through
`KeepAliveOptions.TransitionFor` at `Reconciler.cs:1351/1363`):

```csharp
    public void SeedEnter(NodeHandle node, in EnterExit e, in LayoutTransition spec)
    {
        TransitionDynamics dyn = Normalize(spec.Dynamics);
        if (e.Opacity != 1f) SeedTerminal(node, AnimChannel.Opacity, 1f, dyn, initial: e.Opacity, delayMs: spec.DelayMs, catchUp: true);
        if (e.Dx != 0f) SeedTerminal(node, AnimChannel.TranslateX, 0f, dyn, initial: e.Dx, delayMs: spec.DelayMs, catchUp: true);
        // … Dy / Sx / Sy / Blur likewise with catchUp: true …
    }
    // SeedExit: the same six calls with catchUp: true.

    private void SeedTerminal(NodeHandle node, AnimChannel ch, float to, in TransitionDynamics dyn, float? initial = null,
                              float delayMs = 0f, bool catchUp = false)
    {
        if (ReducedSnap(ch, ReducedMotionPolicy.KeepFade)) { SnapTo(node, ch, to); return; }
        if (dyn.Kind == DynamicsKind.Spring)
            Spring(node, ch, to, SpringParams.FromResponse(dyn.Response, dyn.DampingRatio), initial, delayMs: delayMs);
        else
            Animate(node, ch, initial ?? CurrentValue(node, ch), to, dyn.DurationMs, dyn.Easing, delayMs: delayMs);
        if (catchUp) { int s = Find(node, ch); if (s >= 0) _slab.At(s).Flags |= AnimFlags.CatchUp; }
    }
```

(`Find(node, ch)` is the slab lookup `Get(node, channel, false)` uses at `AnimScheduler.cs:257`; if no non-creating
lookup exists, add `int Find(NodeHandle node, AnimChannel ch)` returning `-1` when absent.) `AnimateBounds`/FLIP,
hover/press/brush fades, loops and Driven rows are untouched.

`AppHost.cs:3353`:

```csharp
            _anim.Tick(dtMs, _frameTime is StopwatchFrameTimeSource sfts ? sfts.LastRawDeltaMs : dtMs);   // 7 animation
```

Headless (`FixedFrameTimeSource`) passes `raw == dt`, so every determinism gate is unchanged by construction.

### E.4 Probes

- `gate.anim.catchup-lands-on-time` (`AnimSuite`): seed a 250 ms `Opacity` 0→1 tween with `CatchUp`; tick
  `(16.7, 16.7)` × 3; then `(34, 150)` once; assert `ElapsedMs ≈ 200` and the value equals the un-stalled golden sampled
  at `t = 200` within 1e-4. A sibling row **without** `CatchUp` after the same ticks reads `ElapsedMs ≈ 84`.
- `gate.anim.catchup-idle-resume-not-caught-up`: `AnimClock.Advance(…, wasIdleOrThrottled: true)` after a 500 ms gap →
  `RawDeltaMs == DefaultDeltaMs` (a pause is not a stall).
- `gate.alloc.steady-zero` unchanged (no allocation touched).

### E.5 What to approve

1. **E-0 now** (~1 day): removes the stretch; no threading change; every gate keeps its meaning.
2. **Step 0 + step 1 of the design** (6–9 days): the go/no-go spike for compositor groups. Only after a green spike commit
   to steps 2–5 (a further 23–32 days) — that is the change that makes motion independent of app code.

---

## F. Execution order, and what "done" looks like

1. **Engine** (one lane, `..\fluent-gpu`): A.3.1, A.3.2, A.3.3, A.4; D.2.1; E.3/E.4. Gates: Debug + Release build,
   full VerticalSlice green, `check-canon.ps1` exit 0.
2. **Core + pipeline** (one lane, disjoint from the page): C.3.1 (`Models.cs` + `ListEquality.cs`), C.3.2, C.3.3 (+
   `DetailModel`, delete `DetailPage.cs:91`), C.3.4; tests C.4.
3. **Page** (one lane): B.1, B.2 — after (2), since B.1's `() => PendingArtist(uri)` needs the `Func<T>` overload.
4. **Diagnostics wiring** (app): D.2.2; test D.3.
5. Orchestrator only: `dotnet build Wavee.slnx` Debug + Release, `dotnet test`, `ops/tools/nav-measure.ps1` cold artist +
   show + startup. Success criteria: artist cold-open `slow33 ≤ 3`, worst `< 40 ms`, `hotAlloc < 3 MB`; show worst
   `< 25 ms`; startup `frame.stall` line's `effects=` accounts for the residue and `unaccounted=0.0`; steady state
   unchanged (`0.7–2 ms`).

**Not done, stated plainly:** nothing here moves the animation tick off the UI thread — E-0 lands the slide on time but a
100 ms stall still freezes it for 100 ms. That is the compositor-group work in E.2, and it needs the user's go on size.
