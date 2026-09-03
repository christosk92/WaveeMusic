# "About this release" — a deterministic facts block (album / single detail rail)

## Context — what the video shows and why it happens

`bugg.mp4` (3.1 s, three navigations: album *Rode Draad* → single *Ik Ben Niet Meer Bang* → single *Wacht Op Mij*). Frame by frame, the "About this release" block under the Play button:

1. **First paint after navigating**: a *three-tile row* — `1 Songs · 3 min Length · 2026 Released` (year only) — with the ℗ line under it, the whole block fading in dimmed.
2. **~200 ms later**: the block *re-composes* into `Songs · Length` on line 1 and a **full-width** `March 27, 2026 Released` tile on line 2, plus a fourth `BLØF Label` tile on its own full-width line when the release has a label.
3. **The swap is animated**: the date tile FLIPs from its old rect (line 1, right of Length, ~68 DIP wide) to its new rect (line 2, 256 DIP wide) on Position **and** Size, while the old `2026` and new `October 9, 2025` text runs sit *on top of each other* in an unconstrained ZStack cross-fade — the montage frame where "3 min Length" and "October 9, 2025" overlap.
4. Tile widths differ per release (`1 Songs` 155 DIP vs `3 min Length` 187 DIP) because every tile is content-sized.

**Root causes (all verified, one file: `src/apps/Wavee/Features/Detail/DetailTrailing.cs`, class `AlbumTrailing`):**

| # | What | Where |
|---|---|---|
| R1 | **Row composition is an accident of text measurement.** Tiles are `Direction=0, Wrap=true, Gap=8` with `Grow=1, Basis=0, MinWidth=0` (`:312-330`). The engine's wrap path ignores `FlexBasis` (`FlexLayout.cs:1371-1448` breaks lines on *measured* width, then grows per line), so a tile's width = its text metrics + 24 padding + an equal share of the line's leftover. At the default rail (280 − 24 = **256 DIP**) three tiles with a *year* fit one line (~228 DIP); once `Released` becomes `February 7, 2025` (~180 DIP base) it no longer fits beside Songs+Length, wraps to line 2 alone and `Grow=1` stretches it to the full row. A later `Label` tile wraps to line 3 the same way. Nothing authored this. | `DetailTrailing.cs:222, 312-330` |
| R2 | **The value refines across hydration rungs, in place, while the user looks at it.** `Open` (tracklist) gives count/length and `Released = m.Year`; `Rich` (kind 183) brings `ReleaseDate` (+ precision) and `Copyright`; `Full` (getAlbum, requested lazily by this very pane at `:165-171`) brings `Label`, `CourtesyLine`, `OtherVersions`. Each rung re-renders the tiles from `AlbumFactTiles` (`:244-276`), which reads only the *formatted* `m.ReleaseDate`/`m.Year` and never `m.ReleaseDatePrecision` (carried on the model at `DetailPage.cs:699`, unused). | `HydrationLevel.cs:78-87`, `DetailTrailing.cs:165-171, 244-276` |
| R3 | **Two animations fire on the same tile at once.** `TileReflow` = `LayoutTransition(Position \| Size, Tween(Expressive.Fast), SizeMode.Reveal)` (`:307-310`) FLIPs the surviving `fact:released` tile across lines; simultaneously the value box keyed `"v:" + value` inside a `ZStack` remounts under `MotionRecipes.TextSwap` (`:320-326`), so the outgoing and incoming runs are co-resident, blurred and half-opaque for 150 ms — and the ZStack measures to the wider run mid-swap, which re-thrashes the wrap decision. The block itself enters with `DetailRail.FadeUp` + `Stagger 45` (`:214, 236`). | `DetailTrailing.cs:307-326` |
| R4 | **All derivation lives in the render method**, including a `DateTimeOffset.UtcNow` read for the Released/Releases tense (`:268`), the not-yet-out scan and the `"n of total"` string — the pattern the codebase forbids ("derived facts live on the model": `DetailNotice` + `PlaylistPageNoticeRules`, `LikedFactsRules`, `TrackExpandedFacts` are the precedents; `LikedFactsPanel.cs:64-66` even documents "`now` is read ONCE at the panel boundary"). No pure class, no test. | `DetailTrailing.cs:244-276` |
| R5 | Six English literals are hardcoded (`:219, 261, 263, 272, 344`, `TODO(loc)` at `:243`) while `Strings.Detail.AboutRelease / FactSongs / FactLength / FactLabel / FactOfCount / OtherVersions` exist unused in `en-US.json:350-354`. | |
| R6 | The 18 px/11 px stat tile is hand-copied four times with diverging motion specs: `DetailTrailing.CompactStatTile`, `ModulePage.FactTile` (`:502-540`, "byte-for-byte the same tile"), `PreReleaseCountdown.UnitTile` (`:99-137`, "ten lines mirrored rather than shared"), `TrackFactsStrip` (own `TileReflow` copy, `:52-55`). | |

**Why it can be deterministic.** The rail width is authored (`WaveeSize.RailAlbum = 280`, modes 224/188, grip 180–480, padding 16+8 → the row is `railW − 24`), the engine has a real grid (`Ui.UniformGrid` / `GridEl` with star tracks, `Factories.cs:138-155`), the model already carries every input (`Tracks`, `ReleaseDate`, `ReleaseDatePrecision`, `ReleaseInstant`, `Label`, `Copyright`, `CourtesyLine`), and the store merge is monotone (`Store.cs:196-204`: a fact never regresses). So the block can have **one shape from its first paint**, with values that only ever refine *in place*.

---

## Design

### D1. The facts are a model projection — `AlbumReleaseFacts` (pure) folded into `DetailModel`

New `Features/Detail/AlbumReleaseFactsRules.cs` (engine-free; source-included in `Wavee.Tests.csproj` like `PlaylistPageNoticeRules.cs`):

```csharp
/// <summary>The "About this release" block as DATA: computed once per projection in DetailPage (the mapper), read by
/// every surface that shows it (rail, compact header, vertical arm). Nothing here is decided in a Render — the
/// composition below is FIXED, only the strings refine as hydration rungs land (Open → Rich → Full), and the store's
/// monotone merge guarantees a value never goes back to null once shown.</summary>
public sealed record AlbumReleaseFacts(
    string? Songs,          // "10" · "8 of 10" (not-yet-out tracks excluded from the count that is OUT) · null before Open
    string? Length,         // "30 min" / "1 hr 12 min" (DetailFormat.TotalTime) · null when no track has a duration yet
    string? Released,       // "2025" → "November 2025" → "November 4, 2025" as precision rises · null before any year
    bool ReleasesInFuture,  // caption "Releases" instead of "Released" (ReleaseInstant > now, `now` passed in)
    string? Label,          // "BLØF" — a NOTE line, never a tile (arrives at Full, must not reshape the grid)
    IReadOnlyList<string> Notes)   // courtesy line, ℗/© lines (11 px), in that order
{
    public static readonly AlbumReleaseFacts Empty = new(null, null, null, false, null, Array.Empty<string>());
    public bool HasTiles => Songs is not null || Length is not null || Released is not null;
    public bool IsEmpty  => !HasTiles && Label is null && Notes.Count == 0;
}

public static class AlbumReleaseFactsRules
{
    public static AlbumReleaseFacts For(IReadOnlyList<Track> tracks, string? releaseDateIso, string? precision, int? year,
                                        DateTimeOffset? releaseInstant, string? label, string? courtesy, string? copyright,
                                        DateTimeOffset now)
    { /* the exact arithmetic now in AlbumFactTiles + ReleaseNotes, FormatReleaseDate moved here from DetailPage.cs:715-728 */ }
}
```

- `DetailModel` gains `public AlbumReleaseFacts ReleaseFacts { get; init; } = AlbumReleaseFacts.Empty;` written by the one mapper next to `Notice` (`DetailPage.cs:~705`), with `now` read once at that boundary.
- `AlbumTrailing.HasReleasePanel(m)` → `!m.ReleaseFacts.IsEmpty || m.OtherVersions is { Count: > 0 }`; `AlbumFactTiles`/`ReleaseNotes` deleted.
- `FormatReleaseDate` leaves `DetailPage` (it is a rule, not a mapper concern); `m.ReleaseDate` stays as the formatted string other surfaces read.

### D2. One fixed grid, from the first paint

```
BoxEl Key="release-about" Direction=1 Gap=Spacing.M
├─ Eyebrow(Strings.Detail.AboutRelease)
├─ GridEl Key="release-facts"  Columns=[Star(1), Star(1)]  ColGap=Spacing.S  RowGap=Spacing.S      ← authored: 2 equal columns
│   ├─ StatTile("songs",    facts.Songs    ?? "—", Strings.Detail.FactSongs)                       row 1, col 1
│   ├─ StatTile("length",   facts.Length   ?? "—", Strings.Detail.FactLength)                      row 1, col 2
│   └─ StatTile("released", facts.Released ?? "—", facts.ReleasesInFuture ? FactReleases : FactReleased)  row 2, ColSpan=2
└─ BoxEl Key="release-notes" Direction=1 Gap=3
    ├─ NoteText("note:label",     Strings.Detail.FactLabel + ": " + facts.Label)   (when present)
    └─ NoteText × Notes
```

- The grid is mounted only once `facts.HasTiles` (i.e. the Open rung — count, length and at least the year are all known then), so "—" is a rare fallback (a duration-less tracklist), not the normal first paint.
- **Released always spans the row.** A year alone in a full-width tile is fine and, more importantly, it is the same rect the full date will occupy; the refinement `2025` → `November 4, 2025` is a text change inside a fixed box. `ReleaseDatePrecision` is now consumed by the rules (D1), not by layout.
- **Label is a note line**, never a tile. It arrives at the Full rung, seconds later; a tile would either reshape the grid (today) or need a reserved empty slot. As `Label: BLØF` under the tiles it appends below without touching them (the notes column shoves the rest of the rail by one 11 px line — `DetailRail.Shove`, position only).
- Rail widths: 256 DIP → 124-DIP columns; grip floor 180 → 74; ceiling 480 → 224; modes 224/188 → 96/82. `StatTile` value text uses `MaxLines=1, Trim=CharacterEllipsis, MinWidth=0` so the grid never grows past its columns.

### D3. Motion — one thing at a time, never a reflow

- `StatTile`: `Layout = DetailRail.Shove` (**Position only**) — the tile may travel when the rail above it changes height, it never resizes. `TileReflow` (Position|Size) is deleted here and in `TrackFactsStrip`.
- Value refinement: keep the `TextSwap` cross-fade (opacity + 4 px) but inside a tile with an authored width and `ClipToBounds`; the ZStack's size is the tile's, so a swap can't change measurement. The two runs still overlap for 150 ms — that is the WinUI text-swap idiom on a *fixed* box; what looked ugly was the overlap *plus* the box travelling and growing.
- Entrance: the block keeps `DetailRail.FadeUp` + `Stagger 45` once, at mount. Notes lines get `FadeUp` when they appear (they are appended, nothing above them moves).

### D4. One tile grammar — `Components/StatTile.cs`

```csharp
/// <summary>The 18 px/800 value over an 11 px caption on FillCardSecondary — the ONE stat tile. Value swaps cross-fade in
/// place (TextSwap) inside a box whose width the PARENT authors (grid column / flex share); the tile itself never
/// measures to its text, so a longer value can never reshape its row.</summary>
public static Element StatTile(string key, string value, string caption, LayoutTransition? layout = null, Element? trailing = null)
```
`DetailTrailing.CompactStatTile`, `ModulePage.FactTile`, `PreReleaseCountdown.UnitTile` and `TrackFactsStrip`'s tile call it (their captions/values unchanged; `PreReleaseCountdown` keeps its own 4-column `UniformGrid` since it is already a fixed grid). `LikedFactsPanel.YearCard` is a different grammar and stays.

### D5. Loc
Use the existing keys (`AboutRelease`, `FactSongs`, `FactLength`, `FactLabel`, `FactOfCount` for `"{n} of {total}"`, `OtherVersions`); delete the literals and the `TODO(loc)`.

---

## Files

| File | Change |
|---|---|
| NEW `Features/Detail/AlbumReleaseFactsRules.cs` | D1 record + rules (`FormatReleaseDate` moves in from `DetailPage.cs:715-728`) |
| `Features/Detail/DetailPage.cs` (mapper ~:694-705) / `DetailConfig.cs` (`DetailModel`) | `ReleaseFacts` init-only property, computed once with `now` |
| `Features/Detail/DetailTrailing.cs` | `ReleasePanel` → D2 tree; delete `AlbumFactTiles`, `ReleaseNotes`, `CompactStatTile`, `TileReflow`; `HasReleasePanel` reads the record; loc keys |
| NEW `Components/StatTile.cs` | D4 |
| `Features/Modules/ModulePage.cs:502-540`, `Components/PreReleaseCountdown.cs:99-137`, `Components/TrackFactsStrip.cs:52-55` | call `StatTile`; delete the private copies and `TrackFactsStrip`'s `TileReflow` |
| `Wavee.Tests/Wavee.Tests.csproj` | `Compile Include` for `AlbumReleaseFactsRules.cs` |
| NEW `Wavee.Tests/AlbumReleaseFactsRulesTests.cs` | see below |
| `CHANGELOG.md` `[0.2.7]` Fixed | "The album page's "About this release" tiles no longer re-arrange themselves as details arrive: the block is a fixed two-column grid from the first paint (Songs · Length over a full-width release date), the date refines in place from year to full date, the label joins the notes below, and nothing slides or overlaps. (#G)" |

Tests (`AlbumReleaseFactsRulesTests`, pure): `Songs_CountsOnlyReleasedTracks_AsNOfTotal`, `Length_NullWhenNoDurations`, `Released_ByPrecision_YearMonthDay`, `Released_FallsBackToYear_BeforeRichRung`, `ReleasesInFuture_UsesInjectedNow`, `Label_IsANote_NotATile` (record shape), `Notes_CourtesyBeforeCopyright_SkipsEmpty`, `Empty_WhenNothingKnown`, `HasTiles_FalseUntilOpen`. `DetailPage` mapping is covered by the existing detail projection tests' fixture if it constructs a `DetailModel` — extend with one assertion that `ReleaseFacts` is populated; otherwise the rules tests are the seam (the mapper is a one-line call).

## Sequencing (Sonnet subagents on disjoint files; the orchestrator builds/tests/captures)

| Lane | Files |
|---|---|
| A | `AlbumReleaseFactsRules.cs` (new) + tests + the csproj include |
| B | `Components/StatTile.cs` (new), `ModulePage.cs`, `PreReleaseCountdown.cs`, `TrackFactsStrip.cs` |
| C (after A's record signature is fixed — it is above) | `DetailTrailing.cs`, `DetailPage.cs` mapper, `DetailConfig.cs` model property, loc literals |
| — | CHANGELOG (needs issue #G, created with approval), gates, captures |

## Verification

- `dotnet build Wavee.slnx` Debug + Release clean; `dotnet test src/apps/Wavee.Tests` green (+ the new rules tests); engine untouched.
- Capture (`ops/release/tools/Drive-WaveeWindow.ps1`, real session or `--fake`): open an album, then a single with a label, then a single without; at each navigation take `-Out` at t≈0 and t≈+2 s. Expect: identical tile rects in both captures (grid unchanged), the date text refined in place, `Label: …` appearing only as a note line, no frame in which two value runs occupy different rects. Drag the rail grip to 180 and 480: two equal columns at every width, the date row spanning both.
- Reduced motion on: no cross-fade, same rects.
