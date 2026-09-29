# Handoff — Wavee Home page (2026-09-27)

## Paste this as the first message of a new session

> You're taking over the Wavee **Home page** redesign in `C:\wavee\waveemusic` (engine: `C:\wavee\fluent-gpu`). Read
> `CLAUDE.md` in both repos, the engine skill `C:\wavee\fluent-gpu\.claude\skills\fluentgpu\SKILL.md`, and this file:
> `docs/plans/wavee/home-handoff-2026-09-27.md`. The owner (Christos) is very frustrated after six passes. Work carefully:
> verify on screen at HIS window size before claiming anything, and don't hand-roll UI.
>
> **Immediate tasks, in order:**
> 1. **Hover feels delayed** on Home (owner's top complaint). Find the root cause, measuring rather than guessing. Suspects, all
>    unverified: the fifth/sixth pass added `HoverDurationMs` / brush transitions; the zone `Design.Entrance.Row(i)`
>    `Animate` on zone boxes; the E24 record-time hover-scope change in `fluent-gpu/src/FluentGpu.Engine/Render/SceneRecorder.cs`
>    (`ForChild` / `scopeBoundary`); `CardShell` lift spring in `Platform/Controls.Art.cs`; `Interaction.Tile` recipe.
>    Compare against Browse (`Entities/Browse.Page.cs`), where hover is fine. Fix at the source; no band-aid timers.
> 2. **The daylist card is ugly at his width** (`Home/Daylist.UI.cs`): the header image takes about 2/3 of the card, the title
>    wraps to "cutesy / korean r&...", the eyebrow and tags truncate, Play and Shuffle stack vertically, "…" sits alone,
>    and the text overflows the card (the eyebrow pokes above the top edge, the countdown bar hangs below). Rebalance:
>    give the text column priority (a MinWidth that fits title + one action row, around 340 DIP), cap the art (it
>    shrinks first), make the row stretch so the card grows with the text, and keep the actions on one row.
> 3. After those, run the **NativeAOT arm64 build** the owner asked for: from **PowerShell** (the Bash tool reports x64 on
>    this ARM64 box) run the publish script with `-Arch arm64` (see memory note "Bash tool reports x64 on this ARM64 box";
>    script under `ops/`, e.g. `publish-wavee-aot.ps1`). Report the output path.
>
> Then re-check the rest of the page on screen and fix whatever still looks off. Ask him before large redesigns.

## Owner rules (from memory + this session)
- Use the **stock FluentGpu controls + the app's shared controls** (`Platform/Controls*.cs`, `Entities/Browse.*.cs`)
  the way Artist/Browse/Search do. No Home-private card templates, hover recipes, or width frameworks. Extend a shared
  control in a small, general way when something is missing.
- **Sticky headers:** no background, no acrylic. The content below clips itself with `.StickyClip(inset)` plus
  `EdgeFade = new EdgeFadeSpec(EdgeMask.Top, band) { WhileStuck = true }` (the Browse masthead / Artist band idiom), and
  the next header pushes the previous one out. He likes sticky headers.
- **Design intent over pixel copying.** The prototype canvas (`docs/plans/wavee/home-redesign/canvas-v3/*.dc.html`, live at
  https://claude.ai/artifact/Qxb9bZUyCjqVsBv21gy8m8) shows intent. He likes its hero images, the Recently played tiles
  with the dashed "Your listening history" tile, the Discover Weekly / Radio wide leads, the New Music Friday 16:9
  tiles, and the 48-art Browse tiles.
- **Verify at his real size:** window 1717×1150 px at 150 % with the right "now playing" panel open. The Home page
  measures ~760 DIP, so inner content is ~696 DIP. Also check 1440×900. Dark theme first, then light.
- No width math copied from mockups; no band-aids; delete obsolete code; pure-rule tests only (no source-text tests);
  every fix references an issue (#n) when committing. **Nothing is committed yet.** Ask before committing.
- Never kill Wavee by image name. Stop probe launches by PID only; he may have his own instance running.

## Current state (all uncommitted)
- **Snapshot branch:** `snapshot/home-remediation-2026-09-26` (local) holds the pre-rebuild Home tree.
- **Plan and history:** `docs/plans/wavee/home-rebuild-implementation.md`. Read the last two sections, "Fifth pass —
  stock controls" and "Sixth pass — prototype visuals on the shared controls". The skill doc is
  `.claude/skills/wavee/home-layout.md`.
- **Home files** (`src/apps/Wavee/Home/`): `HomeScreen.cs` (page shell: ScrollView, greeting, sticky `FacetRow`,
  `SkelRegionEl`), `Facet.UI.cs` (stock `SelectorBar` + Following `ToggleButton` + `ProgressBar` + `InfoBar`;
  `Facet.FacetRowH`), `Zones.UI.cs` (every zone: `ModuleHeader` sticky headers, Browse-style `PagedShelf` with wide
  lead / 16:9, `MediaRow` Tile recents plus the dashed history tile, `SettingsCard` browse tiles), `Daylist.UI.cs`, and the
  kept data pipeline (`Model.cs`, `ZonePlanner.cs`, `PodcastPlanner.cs`, `Facets.cs`, `RecentsCells.cs`, `DaylistSource.cs`,
  `Time.cs`, rules files).
- **Shared-control extensions (sixth pass, `Platform/Controls.Art.cs`, `Controls.cs`, `Design.cs`,
  `Entities/Browse.Cards.cs`):** `CardData.CoverAspect/Meta`, `ShelfHeight(w, aspect, lines)`, `MediaRow(..., RowSkin skin)`
  (Plain/ListRow/Tile/Outline), `Controls.RelatesNow`, `Controls.Cover(..., focusY)`, `IconPlate`,
  `TileCardStyle/Parts`, `Design.Size.WideTile*`, `ShelfItem(WideArt, CaptionLines, Meta)`, `ShelfCell(..., coverAspect)`.
  Also: `NowPlayingOverlay` builds no play button when `OnPlay` is null.
- **Engine changes (`C:\wavee\fluent-gpu`, uncommitted, each with a VerticalSlice gate):**
  - E19 `Segmented.Style.ItemPadding`
  - E20 `EnterExit.DelayMs`
  - E21/E22 `PagedShelf` `leadCardAt` / `leadMinColumns`
  - E23 `GridEl.MaxColumns` + `AutoFillColumnCount`
  - E24 record-time hover scope in `SceneRecorder`
  - E25 `SettingsCard.HeaderIconElement` / `HeaderIconSize`
  - E26 `Interaction.Tile`
  - E27 `SettingsCard` skeleton proxy
  - `MapFromNode` scroll effect (parallax, now unused by Home)
  - `SurfacePool.TargetCensus` race fix (a real crash fix)
  VerticalSlice fails 4 `gate.shelf.*` checks (controller.goto, keyboard.page-follow, keyboard.alloc,
  lift.elevate-unchanged). They were already failing from pre-existing uncommitted `PagedShelf` work.
- **Build/tests:** `dotnet build Wavee.slnx -m:1 -nodeReuse:false -p:UseSharedCompilation=false` (use `-m:1`; the
  engine obj DLL gets locked otherwise) builds in Debug. `Wavee.Tests` pass except the pre-existing
  `AlbumReleaseFactsRulesTests.Length_IsSpelledOnce_TheTileAndTheMetaLineAgree` (from in-flight Album edits, not Home). The
  Release build and Release tests for the sixth pass have **not** been run yet.
- **Checked on screen after the sixth pass:** only the top of the page (live account). The daylist card is broken as
  described above. Recents, shelves, NMF, Browse and Podcasts are not yet verified at his size after the sixth pass.

## Driving the app (see memory "Launching and capturing Wavee")
- Launch sandbox-free: `Start-Process src\apps\Wavee\bin\Debug\net10.0\Wavee.exe -ArgumentList "wavee://open?route=home" -PassThru`
  (record the PID). `--fake` gives offline demo data.
- Resize and capture: `ops/release/tools/Drive-WaveeWindow.ps1 -Move "0,0,1717,1150"`, `-Out file.png`, `-Click "x,y"`
  (client DIP), `-Link wavee://...`.
- The scratchpad helpers from this session (`shot.ps1`, `wheel.ps1`) were temporary. The wheel helper must verify that the
  foreground window belongs to the Wavee PID before injecting. Hover can't be shown by synthetic cursor moves in
  PrintWindow captures, so for hover timing measure through the engine's diagnostics/logs, or ask the owner.
- `home.form` / `facet.*` log lines were added for debugging (`HomeScreen.cs`, `facet.select/landed/verdict/content`).
  Keep or trim them deliberately.
