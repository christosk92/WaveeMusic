# Home Fold tiles + hardcoded Charts

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship Home’s recommended Fold treatment (Hub stack on a Strip crop) for both Charts and Sections-for-you, with Charts sourced from hardcoded browse section URIs in `BrowseTaxonomy` — not from the Home GraphQL document.

**Architecture:** Charts is a Home chrome row (`HomeRow.Charts`), sibling to Sections: not a `HomeGroupKind`, not in `home-layout.json` v1. Tiles fetch `IBrowseService.GetSectionAsync` on constants next to the existing Charts *page* URIs. Drill-in is browse (`browseSection` / `BrowseRoutes.Page`), because `HomeSectionPage` currently treats every `spotify:section:` as `homeSection` and would 400 these IDs. Fold is one `PagedShelf` (`rows: 1`) shared by Charts and the existing section deck.

**Tech Stack:** Wavee (`src/apps/Wavee`, `Wavee.Core`, `Wavee.Tests`), FluentGpu `PagedShelf` / tokens, Pathfinder `browseSection` already wired.

**Prototype (visual spec):** `docs/plans/wavee/home-sections-v1-mica.html` — Blend tab is Fold (WinUI card + Zune crop + cover fan). Search rail is mixed-density Browse. Open it in a browser; do not launch the app to verify.

## Global Constraints

- Read `AGENTS.md` and `.claude/skills/wavee/SKILL.md` + `home-layout.md` before editing Home.
- Props freeze at mount; width-derived props go in `Key`.
- No hardcoded widths/hex/durations in C# — `HomeModuleLayout.*`, `Spacing.*`, `Tok.*`, `MotionTok.*`, `Radii.*`.
- Zero managed alloc in frame phases 6–13; no `FG_*` kill switch for this behavior.
- Do not read/edit `src/apps/.native/**`, `src/apps/Wavee.PlayPlay/**`, `private-runtimes/**`.
- User runs the app. Evidence: `dotnet build src/apps/Wavee/Wavee.csproj` and `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj`.
- Do not commit unless the user asks.

---

## Wire facts (do not rediscover)

Sources: installed Spotify `%APPDATA%\Spotify\Apps\xpui.spa` (2026-08 desktop), `Documents\Fiddler2\Captures\home_23.saz` + `browe.saz`, and `BrowseTaxonomy.Map`.

**xpui does not hardcode EQUAL or Charts page/section IDs.** Browse categories arrive from `browseAll`. The only hardcoded `/genre/0JQ5DA…` routes in xpui are Music and Podcasts (already mapped). Podcast *hub* in xpui uses a different scheme (`spotify:podcastcharts:…` REST). Wavee keeps the GraphQL browse page we already have.

Home has **no** Charts row. `format: "chart"` playlists live on **browse** pages.

### Already in `BrowseTaxonomy.Map`

| URI | Title | Band |
|---|---|---|
| `spotify:page:0JQ5DAudkNjCgYMM0TZXDw` | Charts | Charts |
| `spotify:page:0JQ5DAB3zgCauRwnvdEQjJ` | Podcast Charts | Charts |
| `spotify:page:0JQ5DAqbMKFPw634sFwguI` | EQUAL | For you |
| `spotify:page:0JQ5DAqbMKFSi39LMRT0Cy` | Music | Top |
| `spotify:page:0JQ5DArNBzkmxXHCqFLx2J` | Podcasts | Top |

### Missed — hardcode as **section** constants (this plan)

From `home_23.saz` `browsePage` of Charts / Podcast Charts. **Not** in `Map` today (Map is page-URI keyed).

| URI | Title | Items (NL capture) | Parent page |
|---|---|---|---|
| `spotify:section:0JQ5DAzQHECxDlYNI6xD1g` | Featured Charts | 4/4 | Charts |
| `spotify:section:0JQ5DAzQHECxDlYNI6xD1h` | Weekly Song Charts | 74 | Charts |
| `spotify:section:0JQ5DAzQHECxDlYNI6xD1i` | Daily Song Charts | 73 | Charts |
| `spotify:section:0JQ5DAzQHECxDlYNI6xD1x` | Now available | 1 (Music Video Charts – Global) | Charts |
| `spotify:section:0JQ5DAob0LrW8pqFzVs4ut` | (untitled shelf) | 200 shows | Podcast Charts |

### Missed — `browseAll` category **pages** currently falling to `More`

Durable (map them in Task 1 so they leave More):

| URI | Title | Suggested band |
|---|---|---|
| `spotify:page:0JQ5DAqbMKFRKgqLjIIZq4` | Video Podcasts | Top |
| `spotify:page:0JQ5DAqbMKFCfObibaOZbv` | Gaming | Genres |
| `spotify:page:0JQ5DAqbMKFEOEBCABAxo9` | Netflix | Genres |
| `spotify:page:0JQ5DAqbMKFFoimhOqWzLB` | Kids & Family | Genres |
| `spotify:page:0JQ5DAqbMKFIVNxQgRNSg0` | Decades | Genres |
| `spotify:page:0JQ5DAqbMKFOzQeOmemkuw` | TV & Movies | Genres |
| `spotify:page:0JQ5DAqbMKFziKOShCi009` | Anime | Genres |
| `spotify:page:0JQ5DAqbMKFRieVZLLoo9m` | Instrumental | Genres |
| `spotify:page:0JQ5DAqbMKFPSyykKYdCTj` | Mixed By | For you |
| `spotify:page:0JQ5DAqbMKFyehRoixyYKI` | Lifestyle & Health | Mood & activity |
| `spotify:page:0JQ5DAqbMKFHF46W0MwaAA` | Mystery & Thriller | Mood & activity |
| `spotify:page:0JQ5DAqbMKFN0lmhry1LfG` | Fiction & Literature | Mood & activity |
| `spotify:page:0JQ5DAqbMKFJ4DMqAKdPAs` | Self-Help | Mood & activity |

Seasonal — leave in More (`Your summer ID`, `Summer`, `Your Summer Persona`). Nested Fitness children (`Yoga`, `Pilates & Barre`, …) are **not** in the 70-tile `browseAll`; do not add them to the directory map.

### Not browse categories (do not map)

| URI | What it is |
|---|---|
| `spotify:page:0JQ5DArNBzkmxXHCBROWSE` | `browseAll` start page |
| `spotify:page:0JQ5DAozXW0GUBAKjHsifH` | Home document page (`home.json`) |
| `spotify:xlink:0JQ5DAozXW0GUBAKjHsifL` | Live Events — already `spotify:concerts` |
| `spotify:section:0JQ5IMCbQBLAlPhkJ41ypw` | Older “Featured Charts” id in `browe.saz`; superseded by `…D1g` |
| `spotify:page:0JQ5IMCbQBLkCuGhI0Epb1` | Old `0JQ5IMC` browse page in E2E-DIFF; not in current `browseAll` |
| xpui `spotify:podcastcharts:*` | Separate REST hub; Wavee uses GraphQL Podcast Charts page |

---

## File map

| File | Responsibility |
|---|---|
| `src/apps/Wavee/Features/Browse/BrowseTaxonomy.cs` | Page-band `Map` + new `ChartPages` / `ChartSections` / `IsBrowseSection` |
| `src/apps/Wavee.Tests/WireAdornmentTests.cs` | `BrowseTaxonomyTests` |
| `src/apps/Wavee/Features/Home/HomeSectionPage.cs` | Fetch discriminator: chart sections → `GetSectionAsync` |
| `src/apps/Wavee/Features/Home/HomeLandingProjection.cs` | `HomeRow.Charts` in `DefaultRows` + `ApplyLayout` |
| `src/apps/Wavee.Tests/HomeLayoutTests.cs` | Row-table assertion |
| `src/apps/Wavee/Features/Home/HomeModules.cs` | Fold tile + 1-row deck; estimator height |
| `src/apps/Wavee/Features/Home/HomePage.cs` | Fetch Charts; render `HomeRow.Charts`; header → Charts page |
| `src/apps/Wavee/assets/loc/en-US.json` | `home.charts` (module title); tile titles stay server-localised |
| `docs/plans/wavee/home-sections-v1-mica.html` | Visual spec (already updated) |

Reuse, do not reinvent: `PagedShelf` (SectionDeck), `SearchHero` crop (`OffsetX` / `Rotation` / `CoverColorPlane` idea), `ArtistFacePile` overlap *idea* (square covers, not avatars), `IBrowseService.GetSectionAsync`, `BrowseRoutes.Page`, `HomeSectionPage.FromBrowse`.

---

### Task 1: Hardcode chart IDs + durable browseAll pages in BrowseTaxonomy

**Files:**
- Modify: `src/apps/Wavee/Features/Browse/BrowseTaxonomy.cs`
- Test: `src/apps/Wavee.Tests/WireAdornmentTests.cs` (`BrowseTaxonomyTests`)

**Interfaces:**
- Consumes: existing `Map` / `GroupOf` / `Grouped`
- Produces: `BrowseTaxonomy.ChartPages`, `ChartSectionSpec`, `ChartSections`, `IsBrowseSection(string uri)`

- [ ] **Step 1: Write the failing tests** in `BrowseTaxonomyTests`:

```csharp
[Fact]
public void ChartPages_AreChartsBand()
{
    Assert.Equal(BrowseGroup.Charts, BrowseTaxonomy.GroupOf(Cat(BrowseTaxonomy.ChartPages.Charts, "Charts")));
    Assert.Equal(BrowseGroup.Charts, BrowseTaxonomy.GroupOf(Cat(BrowseTaxonomy.ChartPages.PodcastCharts, "Podcast Charts")));
}

[Fact]
public void ChartSections_AreTheHome23BrowseIds()
{
    Assert.Equal("spotify:section:0JQ5DAzQHECxDlYNI6xD1g", BrowseTaxonomy.ChartSections.Featured.Uri);
    Assert.Equal("spotify:section:0JQ5DAzQHECxDlYNI6xD1h", BrowseTaxonomy.ChartSections.Weekly.Uri);
    Assert.Equal("spotify:section:0JQ5DAzQHECxDlYNI6xD1i", BrowseTaxonomy.ChartSections.Daily.Uri);
    Assert.Equal("spotify:section:0JQ5DAzQHECxDlYNI6xD1x", BrowseTaxonomy.ChartSections.NowAvailable.Uri);
    Assert.Equal("spotify:section:0JQ5DAob0LrW8pqFzVs4ut", BrowseTaxonomy.ChartSections.Podcast.Uri);
}

[Fact]
public void IsBrowseSection_IsTrueOnlyForHardcodedChartSections()
{
    Assert.True(BrowseTaxonomy.IsBrowseSection("spotify:section:0JQ5DAzQHECxDlYNI6xD1g"));
    Assert.False(BrowseTaxonomy.IsBrowseSection("spotify:section:0JQ5DAuChZYPe9iDhh2mJz")); // home editorial
    Assert.False(BrowseTaxonomy.IsBrowseSection(BrowseTaxonomy.ChartPages.Charts));
}

[Fact]
public void GroupOf_DurableBrowseAllPagesLeaveMore()
{
    Assert.Equal(BrowseGroup.Top, BrowseTaxonomy.GroupOf(Cat("spotify:page:0JQ5DAqbMKFRKgqLjIIZq4", "Video Podcasts")));
    Assert.Equal(BrowseGroup.Genres, BrowseTaxonomy.GroupOf(Cat("spotify:page:0JQ5DAqbMKFCfObibaOZbv", "Gaming")));
    Assert.Equal(BrowseGroup.ForYou, BrowseTaxonomy.GroupOf(Cat("spotify:page:0JQ5DAqbMKFPSyykKYdCTj", "Mixed By")));
}
```

- [ ] **Step 2: Run tests — expect FAIL** (types missing)

```powershell
dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj --filter BrowseTaxonomyTests
```

- [ ] **Step 3: Implement** at the bottom of `BrowseTaxonomy` (keep `Map` URI-keyed, never title-keyed):

```csharp
public static class ChartPages
{
    public const string Charts = "spotify:page:0JQ5DAudkNjCgYMM0TZXDw";
    public const string PodcastCharts = "spotify:page:0JQ5DAB3zgCauRwnvdEQjJ";
}

public readonly record struct ChartSectionSpec(string Uri, string ParentPageUri);

public static class ChartSections
{
    public static readonly ChartSectionSpec Featured =
        new("spotify:section:0JQ5DAzQHECxDlYNI6xD1g", ChartPages.Charts);
    public static readonly ChartSectionSpec Weekly =
        new("spotify:section:0JQ5DAzQHECxDlYNI6xD1h", ChartPages.Charts);
    public static readonly ChartSectionSpec Daily =
        new("spotify:section:0JQ5DAzQHECxDlYNI6xD1i", ChartPages.Charts);
    public static readonly ChartSectionSpec NowAvailable =
        new("spotify:section:0JQ5DAzQHECxDlYNI6xD1x", ChartPages.Charts);
    public static readonly ChartSectionSpec Podcast =
        new("spotify:section:0JQ5DAob0LrW8pqFzVs4ut", ChartPages.PodcastCharts);

    public static readonly ChartSectionSpec[] All =
        [Featured, Weekly, Daily, NowAvailable, Podcast];
}

public static bool IsBrowseSection(string? uri)
{
    if (uri is null) return false;
    var all = ChartSections.All;
    for (int i = 0; i < all.Length; i++)
        if (string.Equals(all[i].Uri, uri, StringComparison.Ordinal)) return true;
    return false;
}
```

Point the existing Charts `Map` entries at `ChartPages.*` so the literals live once. Add the durable `browseAll` page URIs from the table above into `Map`.

- [ ] **Step 4: Re-run tests — expect PASS**

- [ ] **Step 5: Commit** (only if the user asked)

```
Hardcode Charts browse section URIs in BrowseTaxonomy.
```

---

### Task 2: Stop treating chart sections as homeSection

**Files:**
- Modify: `src/apps/Wavee/Features/Home/HomeSectionPage.cs` (`IsHomeSection` / `LoadInitialAsync` / `LoadMoreAsync`)
- Test: add to `BrowseTaxonomyTests` (discriminator is on taxonomy). Optionally a comment test in `HomeSectionPagingTests` is not enough — the branch is in the page. If you cannot unit-test the Component, keep the predicate on `BrowseTaxonomy.IsBrowseSection` and assert it in Task 1.

**Landmine:** `HomeSectionPage.IsHomeSection` is `uri.StartsWith("spotify:section:")`. Chart IDs match that and currently call `GetHomeSectionAsync` (Pathfinder `homeSection`). They must call `GetSectionAsync`.

- [ ] **Step 1: Change the predicate**

```csharp
static bool IsHomeDocumentSection(string? uri) =>
    uri is not null
    && uri.StartsWith("spotify:section:", StringComparison.Ordinal)
    && !BrowseTaxonomy.IsBrowseSection(uri);
```

Replace both `IsHomeSection(uri)` call sites in `LoadInitialAsync` / `LoadMoreAsync` with `IsHomeDocumentSection`. Keep the “no browse fallback on home failure” rule for real home URIs.

- [ ] **Step 2: Build Wavee**

```powershell
dotnet build src/apps/Wavee/Wavee.csproj
```

Expected: 0 errors.

---

### Task 3: `HomeRow.Charts` in the landing row table

**Files:**
- Modify: `src/apps/Wavee/Features/Home/HomeLandingProjection.cs`
- Test: `src/apps/Wavee.Tests/HomeLayoutTests.cs`

**Interfaces:**
- Consumes: existing chrome-anchor pattern (Timeline + Sections after Podcasts)
- Produces: `HomeRow.Charts` immediately before `HomeRow.Sections` in `DefaultRows` and `ApplyLayout`

Charts is **chrome**, like Sections: not a `HomeGroupKind`, not in `HomeLayoutModules.DefaultOrder`, not in `home-layout.json`.

- [ ] **Step 1: Failing assertion** — extend the test that currently does `Assert.Equal(HomeLandingProjection.DefaultRows, landing.Rows)` (around `HomeLayoutTests.cs:277`). After the enum + array change it will fail until `ApplyLayout` inserts Charts in the same place.

Also add:

```csharp
[Fact]
public void Projection_ChartsChromeSitsImmediatelyBeforeSections()
{
    var landing = HomeLandingProjection.Project(new HomeFeed("", []), HomeModuleTitles.Default);
    int charts = IndexOf(landing.Rows, HomeRow.Charts);
    int sections = IndexOf(landing.Rows, HomeRow.Sections);
    Assert.True(charts >= 0 && sections == charts + 1);
}
```

`IndexOf` already exists in that file.

- [ ] **Step 2: Run — expect FAIL** (`HomeRow.Charts` missing)

- [ ] **Step 3: Implement**

```csharp
enum HomeRow : byte
{
    Chips, Hero, Weekly, Quick, Recents, MixBand, Artists, ChipCards, Radio, EpisodesAndBooks,
    Queue, Books,
    Podcasts, Timeline, Charts, Sections, Editorial, Feed, Tail,
}
```

`DefaultRows`: `… Timeline, Charts, Sections, Editorial …`

In `ApplyLayout`, every `rows.Add(HomeRow.Sections)` becomes:

```csharp
rows.Add(HomeRow.Charts);
rows.Add(HomeRow.Sections);
```

(two sites: after Podcasts, and the `if (!afterPodcasts)` fallback.)

- [ ] **Step 4: Tests PASS**

```powershell
dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj --filter HomeLayoutTests
```

---

### Task 4: Fold tile + 1-row deck (Charts + Sections)

**Files:**
- Modify: `src/apps/Wavee/Features/Home/HomeModules.cs` (`SectionDeck`, `SectionTile`, `HomeModuleLayout`)
- Modify: `src/apps/Wavee/Features/Home/HomePage.cs` (estimator for `HomeRow.Sections` + new `HomeRow.Charts`)
- Loc: `src/apps/Wavee/assets/loc/en-US.json` → `"home": { "charts": "Charts" }` (module header; tiles use server titles)

**Visual contract** (from the prototype): one row; ~420–460 DIP cells; type on the left (eyebrow + Display Light title + numeral); three overlapping **square** covers falling off the right; no card plate; no chevron on the tile; pager chevrons on the module header (`PagedShelf` already owns those). Hover fans the stack. Drill-in is the tile click + header click.

Do **not** copy `ArtistFacePile` (circular avatars). Do **not** wrap Fold in `Interaction.Card`. Crop/rotation is the `SearchHero` idea (`OffsetX` / `Rotation`), expressed with `HomeModuleLayout` constants:

```csharp
public const float FoldCardMin = 340f;
public const float FoldCardMax = 460f;
public const float FoldCardHeight = 168f;
public const float FoldCover = 112f;
public const float FoldExtent = 32f + Spacing.M + FoldCardHeight + Spacing.M;
```

Replace `SectionDeckExtent` usage with `FoldExtent` (1 row, not `2 * SectionCardHeight`). Keep the old 112-DIP Now tile constants unused or delete if nothing else reads them.

- [ ] **Step 1: Estimator first** so the virtual list cannot flap. In `HomePage` extent switch:

```csharp
HomeRow.Charts => chartsCount == 0 ? 0f : HomeModuleLayout.FoldExtent + gap,
HomeRow.Sections => _sectionDeckCount == 0 ? 0f : HomeModuleLayout.FoldExtent + gap,
```

- [ ] **Step 2: `FoldDeck`** — generalize `SectionDeck`:

```csharp
public static Element FoldDeck(
    IReadOnlyList<HomeSection> sections,
    string headerTitle,
    Action<HomeSection> openSection,
    Action? openHeader = null)
    => PagedShelf.Create(sections.Count,
        (i, cardW) => FoldTile(sections[i], cardW, openSection),
        cardHeight: static _ => HomeModuleLayout.FoldCardHeight,
        header: ModuleHeader(headerTitle, null, null, openHeader),
        minCardW: HomeModuleLayout.FoldCardMin,
        maxCardW: HomeModuleLayout.FoldCardMax,
        gap: Spacing.M, rows: 1, edgeFade: HomeModuleLayout.ShelfEdgeFade,
        keyOf: i => "home-fold-tile:" + (sections[i].Uri ?? i.ToString(CultureInfo.InvariantCulture)))
       with { Key = HomeModuleLayout.SectionSetKey(sections) + ":fold" };
```

`SectionDeck(...)` becomes a one-line wrapper: `FoldDeck(sections, Loc.Get(Strings.Home.Sections), openSection)`.

- [ ] **Step 3: `FoldTile`** — `ZStack` root, `ClipToBounds`, `Height = FoldCardHeight`. Copy column `MaxWidth` ~ 48% of the card (flex: copy `Grow=1, Basis=0, MinWidth=0`; stack `Shrink=0`). Three `Surfaces.Artwork` from `section.Cards[0..2]` (skip nulls; one card still renders one cover). `OffsetX` / `Rotation` / `OffsetY` from `HomeModuleLayout` (e.g. `FoldRotA = -9f`, `FoldRotB = 4f`, `FoldRotC = -2f`). `WaveeType.Eyebrow` = `Loc.Get(Strings.Home.Charts)` only on the Charts deck — for Sections use existing subtitle/`sectionItems` numeral, not a fake “playlists” eyebrow. Title = `WaveeType.PageHero` or `WaveeType.ModuleHeader` at Display Light; count = `section.TotalCount`. `OnClick` → `openSection`. Fold width into `Key`.

Hover fan: `Element.WhileHover` / existing `While*` on the three covers if a transform channel exists; otherwise static stack is acceptable for v1 (prototype fans; reduced-motion is a value, not an `if` branch).

- [ ] **Step 4: Build Wavee** — expect clean.

---

### Task 5: Fetch Charts on Home and wire drill-in

**Files:**
- Modify: `src/apps/Wavee/Features/Home/HomePage.cs`
- Loc: `Strings.Home.Charts`

**Interfaces:**
- Consumes: `Services.Browse.GetSectionAsync`, `BrowseTaxonomy.ChartSections.All`, `HomeSectionPage.FromBrowse` (move `FromBrowse` to an engine-free helper if HomePage cannot see the private static — prefer a small `BrowseHomeCards.From(BrowseSection)` in `SpotifyBrowseMapper` or next to `HomeSectionPage` as `internal` in the same file, called from HomePage via a new `HomeCharts.Map` static in `HomeModules.cs` / a tiny `HomeCharts.cs`)
- Produces: landing-ready `IReadOnlyList<HomeSection>` for Charts; omitted tiles when a section returns null/empty

Do **not** send these URIs through `SpotifyHomeComposer` / `home` GraphQL. Do **not** add `format: chart` handling unless a test proves Home started emitting it (it does not in `home_23.saz`).

- [ ] **Step 1: Mapper helper** (engine-free, testable) — put in `src/apps/Wavee/Features/Home/HomeCharts.cs` **or** `Wavee.Core` if you need it in tests without engine. Simplest: static on `HomeSectionPage` is private today; extract:

```csharp
internal static class HomeBrowseCards
{
    public static HomeSection Section(BrowseSection s) => new(
        s.Uri, s.Title, null, MapCards(s.Cards), s.Total, s.Cards.Count);

    static IReadOnlyList<HomeCard> MapCards(IReadOnlyList<BrowseCard> cards)
    {
        var list = new HomeCard[cards.Count];
        for (int i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
            list[i] = new HomeCard(c.Uri, c.Title, c.Subtitle, c.Image, KindOf(c.Uri),
                Meta: new HomeCardMeta(Accent: c.Accent ?? 0));
        }
        return list;
    }

    static HomeCardKind KindOf(string uri) => EntityUri.KindOf(uri) switch
    {
        EntityKind.Artist => HomeCardKind.Artist,
        EntityKind.Album => HomeCardKind.Album,
        EntityKind.Show => HomeCardKind.Podcast,
        EntityKind.Episode => HomeCardKind.Episode,
        EntityKind.Track => HomeCardKind.Track,
        _ => HomeCardKind.Playlist,
    };
}
```

Point `HomeSectionPage.FromBrowse` at this helper (delete the duplicate).

- [ ] **Step 2: Fetch on HomePage** with `UseResource` (one loadable, seed empty list). Inside the async lambda, sequentially `GetSectionAsync(spec.Uri, 0)` for each `ChartSections.All` (five calls; Featured is 4 cards — enough for the stack). Skip null/empty. Do not invent titles; keep `BrowseSection.Title` (Podcast Charts shelf title may be null — then `Loc.Get(Strings.Browse.Charts)` or parent page title).

`Key` the resource on the signed-in source identity already used for Home (whatever `UseResource` deps Home feed uses) so a logout clears Charts.

- [ ] **Step 3: Render**

```csharp
case HomeRow.Charts:
    return charts.Count == 0 ? new BoxEl()
        : HomeModules.FoldDeck(charts, Loc.Get(Strings.Home.Charts), OpenSection,
            openHeader: () => go(BrowseRoutes.Page(BrowseTaxonomy.ChartPages.Charts),
                                 Loc.Get(Strings.Home.Charts)));
```

Tile click: existing `OpenSection` → `home-section:{uri}` with preview stash. After Task 2, paging uses `GetSectionAsync`. Header chevron of **Podcast Charts** tile still opens that section; the **module** header opens the Charts **page** (music). Optional later: Podcast tile header-equivalent is the tile itself.

- [ ] **Step 4: Estimator** must read the same `charts.Count` as render (`chartsCount` field updated when the loadable becomes Ready, same pattern as `_sectionDeckCount`).

- [ ] **Step 5: Build + test**

```powershell
dotnet build src/apps/Wavee/Wavee.csproj
dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj --filter "BrowseTaxonomyTests|HomeLayoutTests|BrowseMapperTests"
```

Expected: pass. User runs Wavee and opens Home — Charts Fold row above Sections; header drills to Browse Charts; Weekly/Daily drill pages page via `browseSection`.

---

### Task 6: Verification

- [ ] `dotnet build src/apps/Wavee/Wavee.csproj` — 0 errors, 0 warnings-as-errors.
- [ ] `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj` — all pass. If SourceLink file-lock flakes, retry once; do not treat a lock as an assertion failure.
- [ ] Do **not** launch the app. Hand the user the prototype + this plan.
- [ ] Confirm no `FG_*` flag, no new skeleton tree, no `homeSection` hash change.

Manual (user): Fold row visible; Featured shows 4; Weekly/Daily drill is a grid of chart playlists (`format: chart` cards already render as playlists); empty/offline Charts row collapses (height 0).

---

## Out of scope

- xpui `spotify:podcastcharts:*` REST hub
- Home composer `format: "chart"` (Home does not emit it)
- Customizer toggle for Charts (chrome, v1)
- Bleed treatment
- Nested Fitness browse pages in the directory map
- C# implementation of Fold in this planning session

## Spec coverage

| Spec | Task |
|---|---|
| Fold = Hub + Strip, default | 4 (UI), prototype already |
| Charts hardcoded taxonomy IDs | 1, 5 |
| Durable missed browseAll pages | 1 |
| Drill-in, no Show all | 4–5 (header + tile) |
| browseSection not homeSection | 2 |
| Chrome row, not customizer | 3 |
| Estimator matches renderer | 4–5 |
