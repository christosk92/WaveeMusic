# Wavee's sidebar — the unified extension platform

> **Scope.** This is the Wavee *app*'s left sidebar: three user-selectable designs over one renderer, a full-page
> customizer, a versioned local layout document, and the registries that make a sidebar section a *contribution*
> rather than a hard-coded branch. It is app-level architecture, not FluentGpu engine architecture — the engine
> guide is [README.md](./README.md).
>
> Agent-facing companion (file maps, pitfalls, known issues, test inventory):
> `.claude/skills/wavee-sidebar/`.

---

## 1. The one idea

The sidebar used to be one hard-coded component. It then briefly became **three** — Classic's hand-built body,
Library V3's own list/row/rail stack, Curated's planner + slots — which shared only leaf primitives. Paddings,
count badges, section rhythm and motion therefore tripled and drifted: four different left insets in one app, an
accent count pill in one mode and a quiet number in another, no section rhythm, no collapse motion.

It is now **one renderer and three documents**:

```
                    Classic              Library V3              Wavee Curated
                       │                     │                        │
        a locked built-in document   a synthesized ephemeral    the user's persisted
        rebuilt from code, never     document derived from       document in
        persisted                    filter/sort/view/search     sidebar-layout.json
                       │                     │                        │
                       └──────── SidebarPaneConfig (the ONE seam) ─────┘
                                             │
                                    ┌────────▼─────────┐
                                    │   SidebarPane    │   one virtualized ItemsView
                                    │  + SidebarPaneSlot │  14 row kinds
                                    │  + SidebarPaneRail │  the 56-DIP rail
                                    └──────────────────┘
```

A *document* is a `SidebarCustomLayout` — a template id plus a list of `SidebarSectionSpec`. A *renderer* takes a
document plus a live projection of your library, flattens them into one flat `SidebarRow[]`, and draws it. A mode
contributes a document and a small config record of delegates. Drift becomes impossible by construction, because
the renderer takes nothing else.

| | Classic | Library V3 | Wavee Curated |
|---|---|---|---|
| document | `SidebarBuiltInDocuments.Classic(pinnedOpen, libraryOpen, playlistsOpen)` | `LibraryV3Document.Build(in LibraryV3DocState)` | `SidebarPreferences.Layout` |
| persisted? | no (three collapse flags only) | no | yes, `sidebar-layout.json` |
| user-editable? | no | no (its chrome owns the state) | yes, in the customizer |
| chrome | none beyond the pane | header band, toolbar, chip rails, breadcrumb (through `Config.Head`) | none beyond the pane |
| mode component | `Features/Sidebar/WaveeSidebar.cs` | `Modes/LibraryV3Sidebar.cs` | `Modes/CuratedSidebar.cs` |

Switching applies live with no restart. `Features/Sidebar/SidebarHost.cs` reads `SidebarPreferences.Design` and
mounts the mode component under a design-derived `Key`, so a switch is a genuine **remount** — fresh hooks, fresh
section and scroll state — cross-faded on `MotionTok.ControlFast`. It is mounted twice: the docked pane and the
narrow overlay drawer.

**Pins are unlimited and shared across all three designs** (a `SidebarPinStore` in `SidebarPreferences`, persisted
in the same document). App routes, playlists, albums, artists, shows and playlist folders are pinnable; tracks are
not.

### The Shortcuts section is shared across all three designs too

The document also carries **one global shortcut band** — `SidebarCustomLayout.TopBar`, whose wire member still spells
the name it had when it lived in a shell toolbar. It is the app's primary navigation: Home lives there, and it is the
only Home the built-in Classic document, the Curated default template and Library V3's synthesized document have.

It is **materialised as an ordinary section**, not rendered by a bespoke component:
`SidebarShortcutsSection.Prepend(document, topBar)` puts a `StaticLinks` section titled **Shortcuts** at the head of the
document each design hands the renderer, carrying the sentinel id `SidebarIds.TopBarSection`. Consequences worth
knowing:

- The planner, the row slot, the 56-DIP rail (`ShowInRail`), the reorder band and the selection indicator all serve it
  with **no** band-specific code, and Library V3 gains built-in navigation it never had.
- The band is a RENDER-PATH projection only. The persisted document never contains the sentinel section — the reducer
  has no arm for it, and every section-scoped command aimed at it is an `UnknownSection` rejection. Its **items** are
  fully editable.
- Because the section carries the sentinel, edits aimed at it must reach the band's own command family. That is one
  decision in one place: `SidebarItemCommands.Add/Move/Remove` route the sentinel to
  `AddTopBarItem`/`MoveTopBarItem`/`RemoveTopBarItem` and every real section id to `AddItem`/`MoveItem`/`RemoveItem`;
  `ItemsIn`/`FindItem` are the matching read side.
- **Nothing about the wire changed** — no schema change, no migration, the same 6-item cap, the same undo ring, the same
  `SidebarWireCarry` handling. Model identifiers (`TopBar`, `AddTopBarItem`, the JSON member) stay; only the user-facing
  strings became "Shortcuts".
- An **empty** band contributes no section at all: emptying it is a legitimate choice, and `Prepend` then returns the
  document unchanged.

### What `SidebarPaneConfig` may and may not carry

Every member is a **delegate or a flag** — never a snapshot. The config is built once and frozen into the pane
(FluentGpu component props freeze at mount), so a value member would pin the first frame's state forever.

```csharp
var config = UseMemo(() => new SidebarPaneConfig
{
    Design          = SidebarDesign.Curated,           // log + scroll identity ONLY; the renderer never branches on it
    ScrollKeyPrefix = "sidebar.curated",               // the pane appends ".drawer" for the drawer mount
    Document        = () => _prefs?.Layout ?? fallback,
    SetSectionCollapsed = (id, c) => _prefs?.Dispatch(new SetSectionCollapsed(id, c)),
    ReadOnly        = false,
    SearchHead      = true,
    Edit            = ReadEditSession,                 // Func<SidebarEditState?>; non-null ⇒ the pane IS the canvas
    OnCustomize     = OpenCustomizer,
    OnCreatePlaylist = CreatePlaylist,
}, DepKey.Empty);

return Embed.Comp(() => new SidebarPane(config, _route, _go, _compact, _expandedWidth, _inDrawer));
```

The remaining members: `Input` (fold the mode's own filter/sort/search into the planner input), `ModeEpoch` (mode
state folded to one int, read both in the plan's dep key and in every realized row's epoch), `Head` (arbitrary mode
chrome above the scroll surface), `ShowLayoutMenu` / `RailLayoutMenu`, `RailFooter`, `ActivateFolder` (disclose vs
navigate — it replaces the row's click *and* its context-menu verb, so the two cannot disagree),
`IsReorderableSection`, `CommitReorder`.

The rule that keeps this honest: **a mode may not reach around the config.** If a mode needs something the renderer
lacks, it becomes a config member or a planner-input option — never a `switch (Config.Design)`.

### Why the document is not rendered section by section

`SidebarRowPlanner.Build(document, input, buffers)` flattens (document × projection) into ONE `SidebarRow[]`
covering 13 row kinds — `SectionHeader`, `HeaderLabel`, `Divider`, `IconRow`, `EntityRow`, `FolderHeader`,
`GridStrip`, `Placeholder`, `Empty`, `Skeleton`, `CreateAction`, `EntityCard`, `PromptRow` — and the pane renders
that through one `ItemsView.CreateBound` over a measured variable-extent layout. That is what lets a 10 000-entry
playlist tree or library query virtualize end to end; a `Grow=1` list inside an outer `ScrollView` cannot. There
are therefore no nested scrollers and no `Flow.For` over the projection anywhere in the pane.

The 14th kind, `SectionCard`, is emitted only by the second entry point `SidebarRowPlanner.BuildEdit` (§2). Edit mode
obeys the same rule rather than substituting a hand-built card column for the list: a pane-level fork would have
re-created the nested-scroller shape the unification deleted, and cost the cards virtualization, the reorder-band
machinery and the section rhythm.

Rows are POD: no string is allocated during planning. A projected row carries an index into the plan's entry list;
a hand-placed row carries its item key. Metrics come from **one** ladder (`SidebarRowMetrics` —
`HeightFor(density, hasSubtitle)` = 32 / 40|44 / 44|48, `ArtFor` = 20/32/40, `IndentFor(depth)`), and the pane's
padding `(8, 8, 8, 12)` is applied **once**, around the virtualized list, by `SidebarPaneMetrics.PanePad`.

---

## 2. The customizer

Wavee Curated is edited **on the real sidebar**. There is no preview, because there is nothing to preview: the docked
pane beside the page is already rendering the exact document being edited, so it *is* the canvas. Two surfaces, not
four regions:

**The canvas — the live pane in edit mode.** One new `SidebarPaneConfig` member turns it on: `Func<SidebarEditState?>?
Edit` (a delegate, like every other member — the config freezes at mount). Non-null and the pane plans through
`SidebarRowPlanner.BuildEdit` instead of `Build`: every top-level section becomes one uniform-height `SectionCard` row
— grip · kind glyph · title · count · eye · "…" — and the ONE expanded section has its real body planned right
underneath by the same per-kind planners the live pane always used. It is still one flat `SidebarRow[]` through one
virtualized `ItemsView`, so edit mode costs neither virtualization nor the section rhythm.

- One section expands at a time. A 60-row expanded sidebar makes section dragging a scroll-fight, and uniform card
  pitch is what lets `Reorderable` work without variable-extent insertion geometry. A "Show section contents" switch on
  the companion page reveals every body at once for item-level work, and deliberately **disarms** section drag — the
  card run is no longer contiguous, so the slot math would address body rows as if they were cards.
- **Hidden sections keep their card** (dimmed, eye-off) and never reveal a body. Nothing vanishes into an invisible
  elsewhere; equally, the editor never draws rows the user's own sidebar does not have.
- Section reorder commits `MoveSection`; item reorder inside an expanded section commits through
  `SidebarItemCommands`, so a move inside the Shortcuts section emits `MoveTopBarItem`, not `MoveItem`. Grab, move,
  drop and cancel are announced through the engine's live-region seam.
- The **Shortcuts head** carries no grip, no eye and no "…": it is not in `Sections`, so those commands would be
  `UnknownSection` rejections, and an affordance that silently rejects is worse than one that is not offered. Its items
  are fully editable.
- Per-section options open as a **popover anchored to that card's "…"**, reusing the generated control set from
  `Curated/SidebarPropertyPanel.cs`. Only the host changed — from a docked column to a popover — so the options panel
  can never be silently editing a different section than the one you clicked.
- Structural drag is armed **only** in edit mode. Outside it, pin/unpin and pin reordering stay live as always.

The pure rules behind all of that live in `Features/Sidebar/Data/SidebarEditPlan.cs` (`ShowsBody`,
`SectionsReorderable`, `Fold`, `CardCount`, `IsPinnedCard`, `ToMoveSection`, `ToAddSection`) and are unit-tested. Two
index spaces meet in the last two: band slots enumerate the plan's card rows (over the RENDER document, which carries
the materialised Shortcuts head), while the commands index the PERSISTED document, which does not.

**The companion page** at route `sidebar-customize` (`SidebarLayoutMenu.CustomizeRoute`) is one scrolling column at
every window width — no tier ladder, no region that disappears when the window narrows. Top to bottom: the header
(Back · title · Undo · Redo · Reset · Done, where Done returns through the shell's real back stack); the three designs
as a preset control plus the five templates as cards; the **persistent palette**; the hidden-section recovery list; and
an Advanced block pointing at `sidebar-layout.json`.

The palette's first group is **Destinations** — the real app pages, generated from `SidebarPinId.PinnableRoutes` plus
settings / API console / concerts, and labelled through `ShellNav.Dest` so they follow the UI culture and can never
disagree with the tab strip or the breadcrumb. Typing "home" therefore answers with **Home**, and clicking it is one
undoable `AddSection(StaticLinks, Item: route)` that produces a working row — not an empty "Links" section. With a
`StaticLinks` section selected the same click appends into it instead. Palette chips are also `Drag.Source`s onto the
canvas; clicking always works, so drag is never the only way.

Every edit is a **command**. `SidebarPreferences.Dispatch(command)` reduces it, and if anything actually changed:
pushes the pre-image onto a 50-step undo ring, clears redo, bumps `LayoutVersion`, and autosaves. The companion page
therefore never talks to the renderer — it dispatches, and the live sidebar re-plans from the one document.

Undo is a **pre-image snapshot**, not an inverse command: the document is immutable records, so an edit rebuilds
only the spine and structurally shares the rest. That is why "apply template" and "reset" are ordinary single
undoable steps with no special machinery.

The 18 commands: `AddSection`, `RemoveSection`, `DuplicateSection`, `RenameSection`, `SetSectionHidden`,
`SetSectionCollapsed`, `MoveSection`, `AddItem`, `MoveItem`, `RemoveItem`, `SetItemLabel`, `SetItemIcon`,
`SetDisplayOption`, `SetQuery`, `SetExtensionConfig`, `SetItemAction`, `ApplyTemplate`, `ResetLayout`.

A command that changes nothing returns a `SidebarRejectReason` (`NoChange`, `SectionCapReached`,
`KindDoesNotAcceptItems`, `ConfigTooLarge`, `ExtensionRefMissing`, …) and the customizer shows an inline message.
Because a rejection does **not** bump `LayoutVersion`, every controlled control in the property panel also folds a
`RejectEpoch` into its dep key — otherwise a rejected edit would leave a control showing a value the document never
accepted.

Property controls for an `Extension` section are **generated** from the contributing source's
`ISidebarDataSource.ConfigSchema` and written back through `SetExtensionConfig`. The host owns only four display
fields for such a section (density, collapsed-by-default, show-in-rail, max items); everything else is the
source's.

---

## 3. The layout document (`sidebar-layout.json`, version 2)

One file, local only, at `%LOCALAPPDATA%\Wavee\WaveeMusic\sidebar-layout.json` — beside `history.json`. Written
atomically (temp → `File.Replace`, which installs the new file *and* rotates the previous good one into `.bak` in
one call). Scalars (width, collapsed, active design, V3 view state, onboarding markers) live in app settings
instead, not here.

It carries three payloads: the shared **pins**, the **V3 local overlay** (custom order, expanded folders,
first-seen stamps), and the **Curated document**.

```jsonc
{
  "version": 2,
  "updatedAtMs": 1785000000000,
  "appVersion": "0.9.0",

  "pins": [
    { "id": "liked",                              "kind": 0, "uri": "", "name": "Liked Songs", "addedAtMs": 1784900000000 },
    { "id": "spotify:playlist:37i9dQZF1DXcBWIGoYBM5M",
      "kind": 1, "uri": "spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", "name": "Today's Top Hits",
      "addedAtMs": 1784900500000 }
  ],

  "v3": {
    "customOrder":     ["spotify:playlist:1", "spotify:playlist:2"],
    "expandedFolders": ["spotify:folder:abc"],
    "firstSeen":       [ { "id": "spotify:playlist:1", "ms": 1784800000000 } ]
  },

  "curated": {
    "templateId": "curated",
    "sections": [
      {
        "id": "sec_1a2b3c4d",
        "kind": "pinned",
        "titleLocKey": "sidebar.pinned"
      },
      {
        "id": "sec_2b3c4d5e",
        "kind": "collectionShortcuts",
        "titleLocKey": "sidebar.yourLibrary",
        "display": { "artwork": false, "subtitles": false, "countBadges": true },
        "items": [
          { "id": "itm_11112222", "target": "route", "key": "liked",    "icon": "Heart" },
          { "id": "itm_33334444", "target": "route", "key": "albums",   "icon": "Album" },
          { "id": "itm_55556666", "target": "route", "key": "podcasts", "icon": "RadioTower" }
        ]
      },
      {
        "id": "sec_3c4d5e6f",
        "kind": "entityList",
        "title": "Deep cuts",
        "display": { "density": "compact", "presentation": "grid", "gridColumns": 3,
                     "maxItems": 24, "inlineControls": true, "emptyBehavior": "compactHint" },
        "query": {
          "kinds": ["playlists", "albums"],
          "sort": "recentlyAdded",
          "descending": true,
          "qualifier": "byYou",
          "excludeUris": ["spotify:playlist:37i9dQZF1DXcBWIGoYBM5M"]
        }
      },
      {
        "id": "sec_4d5e6f70",
        "kind": "extension",
        "titleLocKey": "sidebar.section.artistTopTracks",
        "collapsed": true,
        "display": { "maxItems": 5 },
        "extension": {
          "extensionId": "wavee",
          "contributionId": "artist.topTracks",
          "schemaVersion": 1,
          "config": { "artistUri": "spotify:artist:0OdUWJ0sBjDrqHygGUXeCF" }
        }
      },
      {
        "id": "sec_5e6f7081",
        "kind": "customGroup",
        "title": "Focus",
        "items": [
          { "id": "itm_77778888", "target": "entity", "key": "spotify:album:abc",
            "entityKind": "album", "label": "Rain", "fallbackTitle": "Rain",
            "fallbackImageUrl": "https://i.scdn.co/image/…" },
          { "id": "itm_9999aaaa", "target": "action", "key": "",
            "action": { "providerId": "wavee", "actionId": "play",
                        "targetMode": "fixedEntity", "targetKey": "spotify:playlist:xyz" } }
        ]
      },
      { "id": "sec_6f708192", "kind": "divider" }
    ]
  }
}
```

Reading that example:

- **`kind` is a string, never a number.** `"pinned"`, `"jumpBackIn"`, `"collectionShortcuts"`, `"playlistTree"`,
  `"entityList"`, `"staticLinks"`, `"customGroup"`, `"header"`, `"divider"`, `"entityEmbed"`, `"newReleases"`,
  `"concerts"`, `"extension"`. Enum *values* are append-only and wire *strings* are never renamed.
- **Only options that differ from the default are written.** A section with no `"display"` block is
  `SidebarDisplayOptions.Default`. That makes adding a display option a non-breaking change in both directions.
- **`title` vs `titleLocKey`.** A template-authored title carries a loc key so it follows the UI culture; renaming
  a section sets `title` and clears `titleLocKey`.
- **`fallbackTitle` / `fallbackImageUrl` are last-known-good.** They are re-stamped every time the item resolves,
  so a row whose entity later disappears (unfollowed elsewhere, offline cold cache, account switch) still renders —
  dimmed, with an "unavailable" affordance. **Nothing is ever auto-removed;** only an explicit `RemoveItem`
  deletes.
- **`includeUris` / `excludeUris`** turn "only these artists" into a *query* rather than a hand-maintained item
  list. A non-empty `includeUris` restricts the result to exactly those uris (still filtered by `kinds`, still
  sorted); `excludeUris` drops from whatever remains. Each set is truncated at 500 uris rather than rejected.
- **An `extension` section's `config` is raw, opaque JSON.** Nothing in the persistence layer inspects it. The only
  rule applied anywhere is a 64 KiB per-section cap — and going over is a *save fault*, never a truncation.
- **An `action` item never stores an internal action enum.** It stores a namespaced `(providerId, actionId)` pair
  plus a target mode (`"none"`, `"fixedEntity"`, `"fixedTrack"`, `"nowPlaying"`, `"activeRoute"`), so a binding
  written by a newer build — or one whose extension is currently missing — round-trips untouched and simply renders
  disabled with a reason.

### Preserve, don't destroy

Two mechanisms make opening a newer build's document non-destructive:

1. An unrecognized `kind` string is preserved as an **opaque section blob at its original index** and re-emitted on
   the next save. It renders as nothing.
2. Unknown **members** anywhere in the tree are captured by `[JsonExtensionData]` and re-attached on write, matched
   by the owning section/item id. So a field a future build adds to a section *this* build understands also
   survives.

Both ride a `SidebarWireCarry` that the preferences service threads from load to every save. A missing `version` is
treated as malformed (not as v1); a `version` above the current one is `TooNew`. A corrupt primary falls back to
`.bak`, then to the built-in Curated layout **in memory** — the bytes on disk are kept and the customizer surfaces
the fault, so a user can never lose their layout to a parse error. The document budget is 2 MiB; over budget,
`Commit()` no-ops and in-memory state runs ahead of disk until the document shrinks.

v1 → v2 is an **identity** migration: an existing document loads unchanged and stamps `"version": 2` on its next
ordinary save.

### Never written

Spotify's rootlist. Folder CRUD and track-drag-to-playlist are deliberately out of scope, and V3's "custom order"
is a purely local overlay. Runtime extension state and secrets never enter this document.

---

## 4. Data: how rows actually arrive

```
LibraryStore · HistoryStore · PlayLogStore · PlaybackBridge · Spotify feed services
                          │
              ISidebarDataSource adapters (nine, first-party)
                          │
      SidebarProjectionBinder ── the ONE rebuild driver, mounted once at the app root
                          │
        SidebarProjectionInput ── entries, tree, pins, recents, feeds, extension slices,
                          │        per-source health, search, folder expansion
             SidebarRowPlanner.Build / BuildRail  (pure)
                          │
                     SidebarPane
```

The binder is the only thing that rebuilds the projection: one unified pass over the library's warm cells, history
recency, the play log and the pin store, gated on a 12-lane trigger fold so a redundant render costs one struct
compare. It also resolves every `Extension` section's contribution to a row slice and records the availability
verdict the planner turns into rows.

Every *decision* it makes lives in an engine-free, unit-tested half (`SidebarBinderPipeline`, `SidebarSourceMap`,
`SidebarProjection`, `SidebarSort`); the binder class itself is only the subscription/store/signal shell. Because a
plain service cannot own a reactive effect, the binder is driven by a zero-size always-mounted pump component
(`binder.MountPoint()`) that reads every trigger signal in its render and syncs from an effect. It is mounted
**once at the app root**, not inside the sidebar: the docked pane and the drawer come and go, the projection may
not.

A degraded source never produces a blank hole:

| Source state | The pane shows |
|---|---|
| `Pending`, nothing yet | skeleton rows |
| `Ready`, zero rows | a quiet per-kind hint ("Play something and it'll show up here"), section still visible |
| an actionable degraded state (e.g. concerts with no location set) | one prompt row with the action that fixes it |
| the extension is missing / disabled / schema-incompatible | one "Manage extension" prompt row; the section **keeps its spec** |
| the source failed but had succeeded before | its last-good slice, replayed |

---

## 5. The extension contracts — as they exist today

The milestone-1 bet: **first-party is literally the trusted extension named `"wavee"`.** There is no privileged
non-extension registration path, so the sandboxed host that comes later has nothing new to invent.

```csharp
public interface IWaveeExtension
{
    void Register(IWaveeExtensionRegistrar registrar);
}

public interface IWaveeExtensionRegistrar
{
    void RegisterAction(WaveeActionDescriptor descriptor);   // a bindable verb, "wavee.play"
    void RegisterDataSource(ISidebarDataSource source);      // a row producer,  "wavee.library"
}
```

`WaveeExtensionRegistry` is the one registry for every contribution kind. It is built once from the composition
root, registers the first-party table first, and then is read-only from the render path (UI thread, no lock, no
off-thread producer). **Duplicate keys: first wins** — a second registration under a live key is refused and
recorded as a diagnostic, so nothing can shadow a first-party contribution. Nothing is ever *unregistered*: a
disabled extension is filtered at the consumption site, and an unresolvable key yields a visible-but-disabled row
with a reason rather than a vanishing one.

### Actions

A `WaveeActionDescriptor` is the **bound** model, and it is deliberately a different shape from the app's existing
`AppAction` context-menu model. An `AppAction` acts on a live target built at menu-open time; a descriptor acts on a
*persisted* binding whose target is a mode plus a key, so it must resolve the target itself, must be able to say
**why** it cannot, and must survive a restart. First-party descriptors *wrap* the existing internal verbs rather
than re-implementing them.

The thirteen registered today:

`wavee.play` · `wavee.playNext` · `wavee.addToQueue` · `wavee.toggleLike` · `wavee.save` · `wavee.open` ·
`wavee.goToAlbum` · `wavee.goToArtist` · `wavee.copyLink` · `wavee.songRadio` · `wavee.artistRadio` ·
`wavee.pinToSidebar` · `wavee.unpinFromSidebar`

`Resolve(services, binding)` folds *every* reason a row can be disabled into one call — an unsupported target mode,
a missing target key, nothing playing, no resolvable route, the descriptor's own enablement veto, and a
confirmation-required action with no overlay to confirm in — so a row's disabled state and `Execute`'s refusal can
never disagree. A destructive descriptor routes through the app's existing confirmation surface and **refuses to
run** when there is no overlay; a null overlay never degrades into an unconfirmed run.

### Data sources

A source declares its **capabilities** — item type, which filter facets it honours, which sorts it can serve, and
whether it pages — so the customizer offers exactly those and never offers a control it would silently ignore. Its
`ConfigSchema` (semantic field kinds only: string / int / bool / entity-uri / enum / uri-list — never a raw colour,
pixel or duration) is what the options popover generates property controls from. Its health is surfaced verbatim as the
planner's source state.

`Fill(into, in request)` runs on the rebuild path: append into the caller's list, no LINQ, no closures, no per-row
allocation, and never a blocking wait — a source that has not resolved yet returns 0 with state `Pending`.

### The guardrails (why M3–M5 will bolt on without rework)

1. The registrar interfaces are already the SDK's shape. A sandboxed extension will never implement
   `IWaveeExtension` in-process: its manifest contributions get **replayed onto the same registrar** by the host,
   so the registry cannot tell first-party from third-party apart from the key's publisher segment and the
   permission set.
2. **No UI looks up the internal action table**, and **nothing `switch`es on an extension id.** Bound rows resolve
   through the registry; contributed sections resolve through a contribution host. The planner never even sees an
   extension id.
3. Unknown refs, configs, kinds and members round-trip untouched.
4. The binder already exposes a per-contribution cached-snapshot seam and per-source health — the two things a
   stale badge and a failure matrix need.
5. Permissions are **declared honestly today and unenforced**, so enforcement turns on without re-authoring the
   table.
6. Runtime extension state and secrets never enter the layout document.
7. Budgets are enforced where they are cheap and never by truncation: 64 KiB per section config, 2 MiB per
   document, 128 chars per registry key, 500 uris per include/exclude set, 40 sections, 500 items per section, 40
   rail tiles.

### What is deliberately not built

There is **no sandboxed extension host, no worker isolation, no public SDK, no manifest loader, and no extensions
page**. Nothing untrusted executes inside Wavee, `RequiredPermissions` is recorded but inert, `ArgumentSchema` is
opaque, and the hand-written first-party registration table is exactly that — hand-written (the generator that
would emit it is later work against the same call shape). "Manage extension" navigates to the customizer. Every
"extension" today is the trusted `"wavee"` publisher.

For the roadmap and the boundaries that gate further extension-host work, see
[`../plans/wavee/wavee-sidebar-extension-platform.md`](../plans/wavee/wavee-sidebar-extension-platform.md).

---

## 6. Worked example — a first-party source and an action, end to end

Say you want a **"Made for you"** section (a feed of personalized mixes) plus a bindable **"Shuffle this"** action.

### 6a. The data source

**1 — claim the id.** In `Features/Sidebar/Data/SidebarDataSource.cs`, add to `SidebarContributions`:

```csharp
public const string MadeForYou = "wavee.madeForYou";

public static readonly string[] FirstParty =
[
    Library, HistoryVisited, HistoryPlayed, PlaylistTree, ArtistTopTracks,
    NewReleases, Concerts, Queue, NowPlaying,
    MadeForYou,                                  // ← the customizer's Extensions palette group reads this array
];
```

**2 — put the mapping where it can be tested.** The pure mapper goes in
`Features/Sidebar/Data/SidebarSourceMap.cs` (engine-free, source-included by the test project), *not* in the
adapter — that file is where everything that can be *wrong* lives. Follow the existing `FromTrack` / `Tracks`
pair as the shape: a `FromX` that builds one `SidebarLibraryEntry` and an appender that dedupes by id and honours
`max`.

```csharp
/// <summary>One personalized-mix row. SourceOrder doubles as the tiebreak so the feed never reshuffles.</summary>
public static SidebarLibraryEntry FromMix(Mix m, int order) =>
    new(m.Uri, SidebarEntryKind.Playlist, m.Uri, m.Title, m.Subtitle,
        m.Cover, null,
        ChildCount: m.TrackCount, AddedAtMs: 0,
        SortStamp: 0,
        LastVisitedTicksUtc: 0,
        SourceOrder: order, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.BySpotify)
    { FolderId = "", FolderName = "", FirstArtistName = "" };

public static int Mixes(IReadOnlyList<Mix>? mixes, List<SidebarLibraryEntry> into, int max)
{
    if (mixes is null || max <= 0) return 0;
    int n = 0;
    for (int i = 0; i < mixes.Count && n < max; i++)
    {
        var m = mixes[i];
        if (m.Uri.Length == 0 || ContainsId(into, m.Uri)) continue;   // the row key must stay unique
        into.Add(FromMix(m, n));
        n++;
    }
    return n;
}
```

`SidebarLibraryEntry` is a 15-parameter positional record struct; passing the tail by name (as above and as
`FromTrack` does) is the house style, because a positional mistake in that tail is invisible.

**3 — write the adapter** in `Features/Sidebar/Data/Sources/`. It holds the engine-bound service and does nothing
else interesting:

```csharp
sealed class MadeForYouSource : SidebarDataSourceBase
{
    readonly IMixService _svc;
    Action<Action>? _post;

    public MadeForYouSource(IMixService svc) : base(SidebarContributions.MadeForYou) => _svc = svc;

    public override SidebarConfigSchema ConfigSchema => new(1,
    [
        new SidebarConfigField("includeDailyMixes", SidebarConfigFieldKind.Bool,
                               "sidebar.source.madeForYou.includeDaily", DefaultJson: "true"),
    ]);

    public override SidebarSourceItemType ItemType    => SidebarSourceItemType.Entity;
    public override SidebarSourceSorts SupportedSorts => SidebarSourceSorts.SourceOrder;   // a feed: source order only
    public override SidebarSourcePaging Paging        => SidebarSourcePaging.TopN;

    internal void Attach(Action<Action> post) => _post = post;

    public override void EnsureFresh(in SidebarSourceRequest request)
    {
        if (_svc.IsWarm) return;
        SetHealth(SidebarSourceState.Pending);
        var post = _post;
        _ = Load();
        async Task Load()
        {
            try { await _svc.RefreshAsync().ConfigureAwait(false); }
            catch (Exception) { post?.Invoke(() => SetHealth(SidebarSourceState.Error, "sidebar.source.madeForYou.failed")); return; }
            // MUST marshal back before touching State or raising Changed.
            post?.Invoke(() => SetHealth(SidebarSourceState.Ready));
        }
    }

    public override int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request)
    {
        bool daily = request.Config.Bool("includeDailyMixes", fallback: true);
        // MaxItems 0 means "the source's own natural bound" — decide it, never treat 0 as "none".
        int max = request.MaxItems == 0 ? 10 : request.MaxItems;
        return SidebarSourceMap.Mixes(_svc.Mixes(daily), into, max);   // no LINQ, no closures, no allocation
    }
}
```

**4 — register it.** In `Data/Sources/WaveeBuiltInDataSources.cs`: construct it in `RegisterAll`, publish it in
`Publish`, and hand it the binder's `post` in `Attach`. Pass the new service from `App/Services.cs` if the existing
`RegisterAll(...)` call does not already have it.

**5 — add the loc keys** to all three of `assets/loc/{en-US,nl,ko-KR}.json`: the palette name/description
(`sidebar.section.extension*` covers the generic case, but a real contribution wants its own), the config field
label, and any state-detail key.

**6 — test it** in `SidebarDataSourceTests` (config readers + the mapper) and `SidebarProjectionBinderTests` (slice
window + availability verdict). Both suites already have a `StubSource : SidebarDataSourceBase` to copy.

**Done.** A user adds it from the customizer's Extensions palette group; the section is stored as
`{"kind": "extension", "extension": {"extensionId": "wavee", "contributionId": "madeForYou", …}}`; the options popover
generates a "Include daily mixes" toggle from your schema; the planner draws skeletons, the quiet empty hint, or
the rows. You wrote **no renderer code and no UI branch**.

### 6b. The action

**1 — declare it** in `Actions/Extensibility/BuiltInExtensionTable.cs`:

```csharp
public const string KeyShuffle = "wavee.shuffleThis";

registrar.RegisterAction(new WaveeActionDescriptor
{
    Key             = KeyShuffle,
    LabelLocKey     = "sidebar.action.shuffleThis",
    IconKey         = ActionIcons.Play,              // a SEMANTIC key, never a raw glyph
    AcceptedTargets = WaveeActionTargetModes.FixedEntity | WaveeActionTargetModes.NowPlaying,
    RequiredPermissions = [WaveePermissions.PlaybackControl],
    LegacyId        = ActionId.None,
    Run             = static (s, _, t) =>
    {
        if (s.Svc?.Player is { } p && t.Uri.Length > 0) _ = p.PlayShuffledAsync(t.Uri);
    },
});
```

`Run` receives the **already-resolved** target — never re-resolve, or the adapter can disagree with the enablement
the row rendered.

**2 — add the label** to all three locale files.

**3 — test it** in `WaveeExtensionRegistryTests` (key validity, first-wins, and that a `NowPlaying` binding with
nothing playing resolves unavailable).

**Done.** The customizer's action picker enumerates the registry, offers exactly the two target modes you declared,
disables and explains the rest, and stores the choice as
`{"target": "action", "action": {"providerId": "wavee", "actionId": "shuffleThis", "targetMode": "nowPlaying"}}`.
You touched no picker code and no menu code.
