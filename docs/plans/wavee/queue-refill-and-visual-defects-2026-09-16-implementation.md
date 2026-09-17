# Queue refill, colour-before-first-paint and the recording defects of 2026-09-16 — implementation

Status: approved 2026-09-16. Branch `feat/0.3-structure`. Engine change in `..\fluent-gpu` lands first.

## 1. What the recording shows

`visual_bugs.mp4` (27 s) and nine screenshots, frame-stepped at 10 fps:

| # | Defect | Root cause (code-verified) |
|---|---|---|
| 1 | Fresh launch: Queue panel shows one grey two-bar row forever, no "Playing from"; after Play only the restored row; Next up / Autoplay appear after the first skip | Restored identities are asked in the BOOT scope offline and lost at `Entities.Switch`; `Rebind` re-ensures only the deck row (`Playback.Host.Wire.cs:450`); the panel's `EnsureRows` `DepKey` collides across scopes (`Queue.UI.cs:238`); nothing `Ensure`s the context entity (`ContextName`, `Queue.UI.cs:117`); `DoRestore` posts no `ContextPages`, so the first autoplay ask is the last row's endgame |
| 1b | Autoplay asked only at run-out; autoplay `next_page_url` discarded (`Playback.Host.Context.cs:673-675`); prefetch depth 1 and skipped while parked (`ArmNext`, `Playback.Transitions.cs:715`) | design gap |
| 2 | Artist/album pages paint accent blue then flip to the cover colour (Play pill, links, pivot underline, hearts, Queue chips) | Colour comes from `getDynamicColorsByUris` after mount (120 ms debounce + round-trip), never persisted; `Artist.HeaderAccent` decoded+stored but the page passes `0` (`Artist.Page.cs:846`); Album has no accent column; `Detail.AccentFor` falls to `Tok.AccentDefault` with no hold rung |
| 3 | Artist hero photo pops in at full brightness under a cool blue veil, then the veil turns red | `ImageTransition.None` + reveal gated on texture residency (`Artist.UI.cs:609,632`, `Artist.Page.cs:351`); veil seeded from `Tok.AccentDefault` (`Design.cs:2303`) |
| 4 | Album right column reserves ~550 DIP then collapses (~270 DIP on a 2-track single); video card lands a beat late | fixed three-section `TrailingSkeleton` + `SmoothResize:false` (`Album.Page.cs:620-673, 1254-1297`); `shortRelease && hasVideo` decided after the reveal |
| 5 | Card play FAB / "…" do not reveal on card hover; on discography grid cards they never appear | The ToolTip wrapper carries pointer handlers → `PointerBit` → treated as an interaction boundary by `SetHoverDescendants` (`AnimScheduler.Hover.cs:73-102`) and by the lazy-mount seed `MountsInsideHoveredScope` (`Reconciler.cs:5540`) |

## 2. Shared API contracts (several workstreams code against these)

```csharp
// Platform/AccentLadder.cs  (WS-E creates; WS-B, WS-F consume)
namespace Wavee;
public static class AccentLadder
{
    public enum Rung : byte { Graded, Payload, Held, Default }
    public readonly record struct Input(ColorF? Graded, uint Payload, bool Definite);
    public readonly record struct Result(ColorF Color, Rung Rung) { public bool Remember => Rung is Rung.Graded or Rung.Payload; }
    public static Result Resolve(in Input now, ColorF? held, ColorF fallback, Func<uint, ColorF> lift);
    // Graded → Payload(lift) → Held (only when !Definite) → Default
}
public static class AccentHold { public static ColorF? Last { get; } public static void Remember(in AccentLadder.Result r); public static void Reset(); }

// Entities/Palette.cs  (WS-C adds)
public static bool HasFreshNegative(string url);      // TryGetSlot + Negative bit within MissTtlSeconds; never enqueues

// Entities/Album.cs  (WS-D adds)
public uint Accent => T.Accent[Slot];                  // ARGB from coverArt.extractedColors.colorRaw.hex, 0 = none

// Design.Palette (existing): ChromeFromPayload(uint argb) → ColorF ; ChromeAccent(Scheme) → ColorF ; Lift/ToColor
```

## 3. Workstreams (disjoint files; only the orchestrator builds)

### WS-A — engine (`..\fluent-gpu`)
- `Element.cs` (~:291): `public bool HoverScopeTransparent { get; init; }` on `BoxEl`.
- `Columns.cs` (~:679): `InteractionInfo.HoverScopeTransparentBit = 1u << 20` (discriminator only).
- `Reconciler.cs` (~:4970): toggle the bit both ways; `MountsInsideHoveredScope` (:5540) skips transparent ancestors; comment :5116 updated.
- `AnimScheduler.Hover.cs`: `IsNestedHoverBoundary` false for transparent; `IsReveal(node)`; `EnclosingScopeHovered(node)`; `SetHover` keeps a reveal following a still-hovered nearest non-transparent scope. `SetPress` untouched.
- `ToolTip.cs:510`: wrapper `HoverScopeTransparent = true`.
- `AnimSuite.cs`: `TransparentHoverScopeChecks` gates 58e–58h registered after :101.
- Canon: `backdrop-effects-animation.md:795-806`, `SPEC-INDEX.md:78`, `scene-memory.md` (bit), `docs/guide/pitfalls.md`.

### WS-B — queue (`Playback/*`, `Entities/Queue.UI.cs`, `Shell/Stage.UI.cs`)
- `Playback.Refill.cs` (new): `RefillKind`, `Refill.Run/NearlyConsumed/Decide` (75 % or ≤3 ahead; user-queue rows ignored; history consumed).
- State `PagesKnown`, `AutoplayPages`, `Next2Id`; `AutoplayPhase.Deferred`; Inputs `AutoplayPaged`, `SessionOnline`, `Autoplayed(ctx, appended, morePages, retry)`; Effects `AutoplayPage/AutoplayPageContext`, `Prefetch2/Prefetch2Row/Prefetch2Id`.
- `CheckRefill` called from `DoQueueChanged`, `EmitLoad`, `DoContextPages` (sets `PagesKnown`, replaces inline ask), non-waiting arms of `DoPaged`/`DoAutoplayed`/`DoAutoplayPaged`, `DoRestore`, `DoRepeat`/`DoShuffle`; `DoEndingSoon`/`EndOfContext` keep the `None`-guarded fallback; `EndOfContext` pages when `Exhausted && AutoplayPages`.
- `Playback.Host.Autoplay.cs` (new): move `RequestAutoplay`/`AppendRows`/`AutoplaySeeds`; keep `s_autoplayPageUrl/Context`; `RequestAutoplayPage` via shared `FetchPage`; offline/401/403 → `retry:true`; permanent `Spotify.Status` watch → `Input.SessionOnline`.
- `Restore` posts `Input.ContextPages(ctx, false)` after `Input.Restore`; `Rebind` ensures every laid row; `ArmNext` prefetches while parked and two rows ahead (farther first); `HeadCache` 2→4.
- `Queue.UI.cs`: DepKey `(Epoch<<32)|Version`; `EnsureContext`; `ContextName` internal, shared with `Stage.UI.cs` (delete duplicate :992-1011); chips through `AccentLadder` (payload = now-playing `Album.Accent`, own hold, `_chipAccent` signal bound by `Pill` with `BrushTransitionMs`).
- Tests: `RefillTests.cs`; `PlaybackStepTests.cs` §R4-1 additions; `PlaybackTransitionTests.cs` prefetch facts.

### WS-C — palette persistence (`Entities/Palette.cs`, `Store.cs`, new `Store.Palette.cs`, new `PalettePersistence.cs`, `Shell/Shell.Host.cs` tick)
- DDL `palette(key TEXT PK, known INT, ts INT, dark BLOB, light BLOB) WITHOUT ROWID` + `ix_palette_ts`; bump `SchemaVersion`.
- `PalettePersistence`: `SchemeBytes=20`, `PersistedBits` (no `Queued`), `Encode/Decode`, `IsWorthPersisting`, `FreshOnLoad`, `LoadCutoffUnix`, `RestoreMask`.
- `Palette`: dirty slots marked in `SetDark/SetGraded/SetNegative`; `partial void PalettePersistArm()`; `DrainDirty`; `Restore(List<PaletteRowSnapshot>)`; `HasFreshNegative`.
- `Store.Palette.cs`: `ArmPaletteFlush`, `FlushPalette` (one upsert tx), `WarmPaletteCore` enqueued first in `Boot` → `Post(Palette.Restore)`; `Sweep` deletes stale rows. Frame tick in `Shell.Host.cs` calls `Store.FlushPalette()` beside `Palette.Tick()`.
- Tests: `PalettePersistenceTests.cs`, `PaletteStoreTests.cs`.

### WS-D — album accent column + trailing band (`Entities/Album.cs`, `Entities/Album.Page.cs`, `Spotify/Spotify.Decode.Pathfinder.cs`, `Spotify.Decode.cs`, `Spotify.Decode.Artist.cs`)
- `AlbumTable.Accent`, `StagedAlbum.Accent`, commit guarded `!= 0`, `AlbumShape` `("accent", Int)` save/load.
- Shared `ImageNode`/`HexColor` moved from `Spotify.Decode.Artist.cs:360-410` into `Spotify.Decode.cs`; pathfinder `coverArt` (:259-261) captures `extractedColors.colorRaw.hex` → `Node.CoverHex` → `row.Accent`.
- `Album.Page.cs:338` → `Detail.AccentFor(url, _display.Accent)`.
- `PageRules.TrailingShape/SkeletonShape/SkeletonHeight/TrailingReserved(+videoReady)/VideoDecided`; `TrailingSkeleton(in shape)`; `TrailingHost` keys the region on the shape, `SmoothResize:true`; `PendingNow` tracks `scope.Tracks.Changed` + `videoReady`.
- Tests: `AlbumPageRulesTests.cs`, album decode/shape facts.

### WS-E — ladder + consumers (`Platform/AccentLadder.cs`, `Entities/Detail.UI.cs`, `Detail.UI.Hero.cs`, `Artist.Page.cs`, `Platform/Controls.cs`)
- `AccentLadder`/`AccentHold`; `Detail.AccentFor(url, payload, fallbackUrl)` = the single adapter; `Identity.For(Album)` sets `CardAccent = a.Accent`.
- Both detail hosts provide `Design.AccentCtx.Slot` from a `Signal<Design.PageAccent>`; heart thunk at `Detail.UI.Hero.cs:421-423` removed.
- `Artist.Page.cs:841-847` → `HeaderAccent` + avatar fallback; `:882` shimmer uses `_accent.Peek()`; `:478` passes `a.HeaderAccent` to the veil/wash props (WS-F adds the field).
- Binds with `BrushTransitionMs`: `PlayPill`, `PivotLink` fills (cached thunks), `HeroIdentity` Play takes `Func<ColorF>`.
- Tests: `AccentLadderTests.cs`.

### WS-F — hero (`Entities/Artist.UI.cs`, `Platform/Design.cs`, `Entities/Palette.Host.cs`, `Entities/Artist.cs` Ensure-on-commit)
- `HeroArt`: opacity keyframes 0→1 over `Design.Motion.Standard` on the `zoom` ready edge (0 when reduced).
- `CoverKeyedVeil`/`CoverArtistBlendWash` props gain `uint PayloadAccent`; leaf ladder with `held:null`, neutral fallback `Tok.FillLayerDefault`; keyed swap with `Enter/Exit(Opacity 0)` + `MotionTok.ControlNormal`.
- `Palette.ArtistHeroVeil/ArtistBlendWash` thread the payload; `Artist.UI.cs:440` passes `a.HeaderAccent`.
- `CommitArtistRows`: `Palette.Ensure(header)` when a row gains `Header`.

### WS-G — card host (`Platform/Controls.Art.cs`, `Wavee.Tests/ControlsTests.cs`)
- Comments :39-43, :182-186, :495-505 corrected.
- `CardChromeRules.Hot(pointerIn, focusIn)`, `FocusIn(got, stillInside)`; `GridCardHost` splits `_pointerIn/_focusIn`, `_shell` from `OnRealized`, `InputHooks.Current.GetFocus` walk; `_exit` unchanged.
- Tests: six new facts.

## 4. Verification
Engine: Debug+Release build, VerticalSlice `ALL CHECKS PASSED`, `check-canon.ps1` exit 0. App: Debug+Release build, `Wavee.Tests` green. Manual: relaunch with a queue (titled rows, "Playing from", autoplay rows once online, `audio.prefetch` two rows); artist/album navigation cold+warm with no blue frame; hero fades; singles' trailing band without collapse; card FABs on body hover incl. discography.

## 5. CHANGELOG bullets (issue numbers pending)
See `CHANGELOG.md` Unreleased → Fixed; each ends `(#n)`.
