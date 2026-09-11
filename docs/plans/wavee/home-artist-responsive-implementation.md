# Home hero, "Your top artists" row and artist header — responsive sizing plan

**Status:** planned, not started. **Scope:** `src/apps/Wavee/Features/Home/*`, `Features/Detail/*`, `Wavee.Tests`. No
engine changes — every primitive needed (`UseMeasuredWidth`, `Responsive.Of`, `Viewport.Size`, `FillRowVirtualLayout.Fit`,
`BoxEl.Wrap`) exists. Three independent fixes; one PR each or one PR with three commits. File the issue first; every
CHANGELOG bullet / commit body carries `(#n)` / `Fixes #n`.

---

## 0. Investigation (light RCA, 2026-09-05)

### 0.1 Home hero ("Good evening, … · your daylist" banner)

- Rendered by `HomeCards.HeroBand(HomeCard c, string eyebrow, …, float width)` —
  `src/apps/Wavee/Features/Home/HomeCards.cs:276-427`. Mounted from `HomePage.cs:686-695` (landing) and `:612-623`
  (facet page) inside `Responsive.Of(w => HeroBand(…, w), fallback: 900f)`; the virtual-list estimator is
  `HomeModules.HeroHeight(width) => HomeHeroLayout.HeightFor(width)` (`HomeModules.cs:634`, used at `:694`).
- All geometry comes from the engine-free `HomeHeroLayout` (`Features/Home/HomeHeroLayout.cs`, test-included via
  `Wavee.Tests.csproj:207`):
  - width tiers `MediumWidth = 700`, `WideWidth = 980` (`:20-21`) with **no hysteresis** (`:49-51`);
  - a **fixed vertical budget**: `2·CopyPaddingY(44) + Eyebrow 24 + Title(120|80|72) + 12 + Tags 32 + Meta 36 + Pulse 40 + Actions 32`
    → **Wide 384 / Medium 344 / Narrow 336** (`:31-45`, `:60-69`; pinned by `HomeHeroLayoutTests.cs:30-32`);
  - the `PulseBlock` (40, the flip-countdown row) is **always reserved**, daylist or not (`:44`; `HomeCards.cs:288-296`
    mounts an empty `BoxEl` when there is no `ExpiresAtMs`);
  - `ArtworkSize == Height` (`:55`), so the square cover is as tall as the card;
  - the height is `Height = metrics.Height` on the foreground, veil and surface (`HomeCards.cs:362`, `:375`, `:400`,
    `:419`) — never `MinHeight`, never viewport-aware.
- **Diagnosis:** on a 900×600 window the content column is ~620 wide → Narrow → a **336-DIP** card in a ~520-DIP page
  viewport (title bar + player dock removed): the hero is ~65 % of what is visible. The height depends on width only,
  never on the viewport height, and the 44+44 copy padding, the always-reserved pulse row and the 2-line title budget are
  all paid on the smallest screens too.

### 0.2 "Your top artists" row

- `HomeArtistRow : Component` — `Features/Home/HomeModules.Artists.cs:30`. Header string
  `Strings.Home.TopArtistsSub(count)` at `:168` ("Last 4 weeks · {count} tracked · select one").
- Width comes from `UseMeasuredWidth(4f)` (`:47`) → `effectiveWidth` (`:95-96`).
- The podium is a **wrapping** flex row: `Direction = 0, Wrap = true, Gap = Spacing.S, MinWidth = 0f, Padding = Edges4.All(Spacing.M)`
  (`:131-136`). That `Wrap = true` is the reported wrapping.
- Avatar size is already width-driven, but **only upwards**: `FillRowVirtualLayout.Fit(podiumContentW, minCardW: BaseArtSize(0) + 8 = 84, 9999, gap 8, perPageOverride: count)`
  (`:105-109`) → `HomeArtistRowLayout.RampScaleFor` clamps to `[MinArtScale = 1f, MaxArtScale = 1.6f]`
  (`Features/Home/HomeArtistRowLayout.cs:49-51`, `:64-73`; ramp 76/60/46 at `:46`). Ten pods at scale 1 need
  `Σ(BaseArtSize+8) + 9·8 + 24` ≈ **798 DIP**; below that the row cannot shrink, so it wraps.
- The pod itself has a **60-DIP floor**: `float w = MathF.Max(artSize + Spacing.S, 60f)` (`HomeCards.cs:994`), a fixed
  20-DIP rank badge (`:1036`), `decodePx: 128` (`:1023`) and a 2-line name (`:1058-1063`). All ten artists always render
  (`:116`; the count is `SpotifyUserTopService.TopArtists = 10`).
- The tier for the disclosure split uses `NominalTierFor` (`:99`) although `HomeArtistRowLayout.TierFor` with 24-DIP
  hysteresis exists (`HomeArtistRowLayout.cs:31-39`) — dead code today.

### 0.3 Artist detail header

- `ArtistPage.Banner(...)` — `Features/Detail/ArtistPage.Hero.cs:22-198`; width measured through `_heroWidth`
  (`:20`, `MeasureHero :111-115`, `OnBoundsChanged :195`); geometry from `ArtistHeroLayout.For(width, previousTier)`
  (`Features/Detail/ArtistHeroLayout.cs:80-90`, hysteresis `:68-78`).
- Heights: **Wide 440, Medium 384** (`:31-32`); stacked tiers **Compact 200 + 252 = 452, Narrow 176 + 300 = 476**
  (`:37-52`) — the narrow presentations are *taller* than the wide ones, and `ArtistHeroLayoutTests.cs:121-126` asserts
  exactly that (`Compact ∈ [Medium, 1.25·Medium]`, `Narrow ∈ [Medium, 1.35·Medium]`).
- Horizontal tiers centre a copy block inside `Padding(gutter, 24, gutter, 24)` with `Justify = Center`
  (`ArtistPage.Hero.cs:157-165`); the copy block itself (verified 16 · 2-line name · 2-line bio · meta · actions 36,
  gaps 8) is ~250–290 DIP, so ~100–150 DIP of the 384/440 is air.
- `HeroArt` recomputes the same metrics (`:305`) — any height change must reach both.
- Top tracks: `ArtistPopular` (`Features/Detail/ArtistPopular.cs`) — `MaxRows = 5`, `RowH = 56`, `ClassicRowH = 48`
  (`:80-83`), shelf at `:146-171`; the per-cell density tiers already key off the fitted cell width (`:215-226`).
- **Diagnosis:** the header is fixed-height per width tier, ignores the viewport height, over-reserves vertical air on
  the horizontal tiers, and the stacked tiers grow rather than shrink. Under it, five 56-DIP rows + header add ~330.

---

## 1. Fix A — home hero: viewport-aware, density-tiered, hysteretic

### 1.1 `HomeHeroLayout` (engine-free) — new inputs, two densities

```csharp
internal enum HomeHeroTier : byte { Narrow, Medium, Wide }
internal enum HomeHeroDensity : byte { Full, Compact }

internal readonly record struct HomeHeroMetrics(
    HomeHeroTier Tier, HomeHeroDensity Density,
    float Height, float CopyPaddingX, float CopyPaddingY, float ArtworkSize,
    int TitleLines, bool ShowTags, bool ShowPulse)
{
    public bool Stacked => Tier == HomeHeroTier.Narrow;
}

internal static class HomeHeroLayout
{
    public const float MediumWidth = 700f;
    public const float WideWidth = 980f;
    public const float TierHysteresis = 24f;                       // same band as ArtistHeroLayout / DetailLayoutBreakpoints

    /// <summary>Below this PAGE viewport height (window height minus the shell's title bar + player dock) the hero
    /// takes its Compact density regardless of width; Narrow is always Compact.</summary>
    public const float CompactViewportHeight = 720f;
    /// <summary>The hero never takes more than this fraction of the page viewport; the copy budget shrinks first
    /// (density), then the height clamps and the artwork (a square whose edge is the height) follows.</summary>
    public const float MaxViewportFraction = 0.42f;
    public const float MinHeight = 168f;                           // Compact, no tags, 1-line title, no pulse: 2·24 + 24 + 36 + 12 + 28 + 32 = 180 → floor a rung under it

    public const float CopyPaddingX = Spacing.L + Spacing.XXXL;    // 48
    public const float CompactCopyPaddingX = Spacing.XXL;          // 24
    public const float CopyPaddingY = Spacing.L + Spacing.XXL + Spacing.XS;  // 44
    public const float CompactCopyPaddingY = Spacing.XXL;          // 24
    public const float ArtworkFade = Spacing.XXXL * 3f;

    const float EyebrowBlock = 16f + Spacing.S;                    // 24
    const float WideTitleLine = 60f, MediumTitleLine = 40f, NarrowTitleLine = 36f;
    const float TitleMargin = Spacing.M;
    const float TagsBlock = 20f + Spacing.M;                       // 32
    const float MetaBlock = 20f + Spacing.L;                       // 36 (Full) — Compact uses 20 + Spacing.S = 28
    const float CompactMetaBlock = 20f + Spacing.S;
    const float PulseBlock = 28f + Spacing.M;                      // 40, ONLY when the card is a daylist
    const float ActionsBlock = Spacing.XXXL;                       // 32

    public static HomeHeroTier TierFor(float width, HomeHeroTier previous)
    {
        // Narrow immediately; widen back only once past threshold + hysteresis (the ArtistHeroLayout shape).
        if (previous == HomeHeroTier.Wide && width >= WideWidth - TierHysteresis) return previous;
        if (previous == HomeHeroTier.Medium && width >= MediumWidth - TierHysteresis && width < WideWidth + TierHysteresis) return previous;
        if (width >= WideWidth + (previous < HomeHeroTier.Wide ? TierHysteresis : 0f)) return HomeHeroTier.Wide;
        if (width >= MediumWidth + (previous < HomeHeroTier.Medium ? TierHysteresis : 0f)) return HomeHeroTier.Medium;
        return HomeHeroTier.Narrow;
    }

    public static HomeHeroDensity DensityFor(HomeHeroTier tier, float pageViewportHeight)
        => tier == HomeHeroTier.Narrow || (pageViewportHeight > 0f && pageViewportHeight < CompactViewportHeight)
            ? HomeHeroDensity.Compact : HomeHeroDensity.Full;

    public static HomeHeroMetrics For(float width, float pageViewportHeight, bool hasPulse, HomeHeroTier previous)
    {
        var tier = TierFor(width, previous);
        var density = DensityFor(tier, pageViewportHeight);
        bool compact = density == HomeHeroDensity.Compact;
        int titleLines = compact ? 1 : 2;
        bool showTags = !compact;
        float content = ContentHeight(tier, density, titleLines, showTags, hasPulse);
        float cap = pageViewportHeight > 0f ? MathF.Max(MinHeight, pageViewportHeight * MaxViewportFraction) : content;
        float height = MathF.Min(content, cap);
        float padX = compact ? CompactCopyPaddingX : CopyPaddingX;
        float padY = compact ? CompactCopyPaddingY : CopyPaddingY;
        return new HomeHeroMetrics(tier, density, height, padX, padY, ArtworkSize: height, titleLines, showTags, hasPulse);
    }

    public static float HeightFor(float width, float pageViewportHeight, bool hasPulse, HomeHeroTier previous)
        => For(width, pageViewportHeight, hasPulse, previous).Height;

    public static float ContentHeight(HomeHeroTier tier, HomeHeroDensity density, int titleLines, bool showTags, bool hasPulse)
    {
        float line = tier switch { HomeHeroTier.Wide => WideTitleLine, HomeHeroTier.Medium => MediumTitleLine, _ => NarrowTitleLine };
        bool compact = density == HomeHeroDensity.Compact;
        return 2f * (compact ? CompactCopyPaddingY : CopyPaddingY)
             + EyebrowBlock + titleLines * line + TitleMargin
             + (showTags ? TagsBlock : 0f)
             + (compact ? CompactMetaBlock : MetaBlock)
             + (hasPulse ? PulseBlock : 0f)
             + ActionsBlock;
    }
}
```

Resulting heights (content, before the viewport cap): Full Wide 384→**344** (no pulse) / 384 (daylist); Full Medium
**304**/344; Compact Narrow (1-line title, no tags) **180**/220; Compact Medium **184**/224. At 900×600 (page ≈ 520):
cap = max(168, 218) = 218 → the daylist hero is **218** instead of 336 (−35 %); a non-daylist one 180.

### 1.2 `HomeCards.HeroBand` (`HomeCards.cs:276-427`) — consume the metrics, do not compute them

- Signature: `HeroBand(HomeCard c, string eyebrow, string meta, …, MenuAttach? menu, in HomeHeroMetrics metrics)`
  (the caller computes metrics; `:280` `var metrics = HomeHeroLayout.For(width);` goes away; `width` is
  `metrics`-independent and still needed for the copy width — pass both or add `Width` to the record).
- `:281-286` title: `title = title with { MaxLines = metrics.TitleLines }` (today the ramp sets 2 somewhere in the
  `copy` children — set it explicitly from metrics).
- `:288-296` pulse: `Element? pulse = metrics.ShowPulse ? Embed.Comp(…FlipCountdown…) : null;` and **omit** it from the
  children list when null (the estimator no longer reserves it).
- `:321` tags row: emit only when `metrics.ShowTags`.
- `:300`, `:365-366`: padding reads `metrics.CopyPaddingX/Y` (already does — the values just change per density).
- `:362`, `:375`, `:400`, `:419`: `Height = metrics.Height` stays; `:384-385`, `:410`, `:415` artwork/aspect follow
  `metrics.ArtworkSize`/`Height` unchanged.
- In Compact density the meta row (`:327` region, Body 14/20) keeps one line with `MaxLines = 1, Trim = CharacterEllipsis`.

### 1.3 Call sites — thread the page viewport height and the tier memory

`HomePage.cs` (component): add

```csharp
var viewport = UseContext(Viewport.Size);                                   // FluentGpu.Engine/Hooks/Context.cs:22
float pageH = HomeHeroLayout.PageViewportHeight(viewport.H);                 // viewport.H − shell chrome (title bar + PlayerDock.Reserve)
var heroTier = UseRef(HomeHeroTier.Wide);
```

`PageViewportHeight` is a one-liner in `HomeHeroLayout`: `MathF.Max(0f, viewportH - ShellChromeVerticalAllowance)`
with the constant restated from the shell (the same "estimate from the viewport" idiom as
`DetailLayoutBreakpoints.EstimatePageWidthFromViewport`, `Features/Detail/DetailLayoutBreakpoints.cs:32-38`); pick
the value from `PlayerDock.Reserve` + the title bar height at implementation.

- `:686-695` and `:612-623`:
  ```csharp
  Responsive.Of(w =>
  {
      var m = HomeHeroLayout.For(w, pageH, hasPulse: h.Cards[0].Meta is { ExpiresAtMs: > 0 }, heroTier.Value);
      heroTier.Value = m.Tier;
      return HomeCards.HeroBand(h.Cards[0], HeroEyebrow(h.Cards[0], feed), CardMeta(h.Cards[0]), …, in m);
  }, fallback: 900f)
  ```
- Estimator `HomeModules.HeroHeight(width)` (`HomeModules.cs:634`) → `HeroHeight(width, pageH, hasPulse, tier)`; its
  caller at `:694` (`RowHeightEstimate(kind, width)`) gains the same three inputs — HomePage owns all of them where it
  builds `homeLayout` for `Virtual.Measured` (`HomePage.cs:514`). The estimate and the render MUST use the same
  `previous` tier or the measured seam corrects a wrong seed every resize.
- `Features/Home/DEFECT_REGISTER.md:10` quotes stale heights — update to the new table.

### 1.4 Tests

- `HomeHeroLayoutTests.cs:17-33`, `:59`: rewrite the height table for `(tier, density, hasPulse)`; add `TierFor`
  hysteresis cases (mirror `ArtistHeroLayoutTests`), `DensityFor` (Narrow ⇒ Compact; `pageH < 720` ⇒ Compact;
  `pageH == 0` ⇒ Full), the viewport cap (`For(620, 520, true, Narrow).Height == 218`), and `MinHeight` floor.
- `HomeCards.HeroBand` is engine-bound — no test; verify by running.

---

## 2. Fix B — "Your top artists": never wrap, shrink to fit

### 2.1 `HomeModules.Artists.cs`

```csharp
// :99 — use the hysteretic rule that already exists (dead code today)
var tierRef = UseRef(HomeArtistRowLayout.InitialTierForViewport(UseContext(Viewport.Size).W));
int tier = HomeArtistRowLayout.TierFor(effectiveWidth, tierRef.Value, initialized: width > 0.5f);
tierRef.Value = tier;

// :105-109 — let the fit go BELOW the prototype ramp: the min column is the pod floor, not the rank-1 pod
const float PodChrome = Spacing.S;
float podiumContentW = MathF.Max(0f, effectiveWidth - 2f * Spacing.M);
var (_, fittedPodW) = FillRowVirtualLayout.Fit(podiumContentW, HomeArtistRowLayout.MinPodWidth, 9999f, Spacing.S,
                                               perPageOverride: artists.Count);
float rampScale = HomeArtistRowLayout.RampScaleFor(fittedPodW, artists.Count, PodChrome);

// :131-136 — ONE row, always
var podium = new BoxEl
{
    Direction = 0, Wrap = false, Gap = Spacing.S, MinWidth = 0f, ClipToBounds = true,
    Padding = Edges4.All(Spacing.M),
    Children = [.. strip],
};
```

Check `FillRowVirtualLayout.Fit` (`fluent-gpu/src/FluentGpu.Engine/Scene/VirtualLayout.cs:484`): with
`perPageOverride == count` it must return `CardW = (main − gap·(count−1)) / count` **clamped to `minCardW`**; if it
does, `MinPodWidth` is the floor that lets the scale fall below 1. If it clamps differently, compute the column directly
in `HomeArtistRowLayout.FittedPodWidth(contentW, count, gap)` (pure) and skip `Fit` for this row.

### 2.2 `HomeArtistRowLayout.cs` (engine-free) — the shrink rules

```csharp
/// <summary>Shrink down to about half the prototype ramp before anything else gives (46 → 23 for the tail).</summary>
public const float MinArtScale = 0.5f;                  // was 1f (:49)
public const float MaxArtScale = 1.6f;

/// <summary>The narrowest pod. 40 = a 32 avatar + 8 chrome; below this the row stops shrinking and clips at the end.</summary>
public const float MinPodWidth = 40f;

/// <summary>The pod column for an art size: the art plus its chrome, floored — the 60 floor RankedAvatar used to hard-code
/// is what made ten pods 696 DIP wide no matter what.</summary>
public static float PodWidth(float artSize) => MathF.Max(artSize + 8f, MinPodWidth);

/// <summary>Name lines under a pod: two at full scale, one when squeezed, none when tiny (the tooltip carries it).</summary>
public static int LabelLines(float scale) => scale >= 0.85f ? 2 : scale >= 0.65f ? 1 : 0;

/// <summary>Rank badge edge: 20 at full scale, 16 when the avatar is under ~48.</summary>
public static float BadgeSize(float scale) => scale >= 0.8f ? 20f : 16f;

/// <summary>The slot every pod reserves for its art (the tallest avatar in the strip).</summary>
public static float SlotHeight(float scale) => ArtSize(0, scale);
```

### 2.3 `HomeCards.RankedAvatar` (`HomeCards.cs:991-1066`)

- `:994` `float w = HomeArtistRowLayout.PodWidth(artSize);` (drop the `60f` floor).
- Add a `float scale` parameter (or derive `scale = artSize / BaseArtSize(rank-1)` — pass it, it is cheaper and exact).
- `:1036` badge: `Width = rank >= 10 ? badge + 8f : badge, Height = badge` with `badge = HomeArtistRowLayout.BadgeSize(scale)`;
  hide the badge entirely when `scale < 0.55f` (the avatar is ~25 DIP — a 16 badge would cover it).
- `:1023` `decodePx: 128` → `decodePx: (int)MathF.Ceiling(artSize * 2f)` clamped `[64, 192]` so a 122-DIP rank-1 avatar
  at 1.6× is not upscaled from 128 and a 23-DIP one does not decode 128.
- `:1058-1063` label: `int lines = HomeArtistRowLayout.LabelLines(scale);` → omit the `Caption` when 0, else
  `MaxLines = lines`; the pod gets `ToolTip.Wrap(…, a.Name)` when lines == 0 (accessible name).
- `:997` keep `Shrink = 0f` — the *scale* does the shrinking, not flex; with `Wrap = false` + `ClipToBounds` on the
  podium, a floor-width overflow clips at the trailing edge instead of wrapping (only reachable below ~520 DIP with ten
  artists at `MinPodWidth` 40: 10·40 + 9·8 + 24 = 496).

### 2.4 Tests

- `HomeArtistRowLayoutTests.cs:85-86` (`RampScaleFor(40, 10, 8) == MinArtScale`) still holds with the new constant;
  add: `RampScaleFor(fitted 44, 10, 8)` ≈ 0.63 (between the clamps); `PodWidth(23) == 40`, `PodWidth(76) == 84`;
  `LabelLines` at 1.0/0.7/0.5 → 2/1/0; `BadgeSize` at 1.0/0.7 → 20/16; a "ten pods fit one row" property:
  `Σ PodWidth(ArtSize(i, scale)) + 9·8 ≤ contentW` for `contentW ∈ {520, 640, 800, 1000}` with the scale the row
  would compute (mirror the caller's arithmetic in the test — that is the whole guarantee "never wraps").
- Extend the `TierFor` tests (`:25-56`) to cover the row using them (no change in the rule).

---

## 3. Fix C — artist header: shorter, viewport-aware, stacked tiers shrink instead of growing

### 3.1 `ArtistHeroLayout.cs`

```csharp
public const float WideHeight = 360f;              // was 440 (:31) — 24 pad · ~290 copy · 24 pad + slack
public const float MediumHeight = 320f;            // was 384 (:32)
public const float CompactPhotoHeight = 160f;      // was 200 (:37)
public const float NarrowPhotoHeight = 136f;       // was 176 (:38)
public const float CompactExpandedIdentityHeight = 220f;   // was 252 (:49) — bio 2→1 line, meta one row, actions 36
public const float NarrowExpandedIdentityHeight = 252f;    // was 300 (:50) — bio 1 line, meta stacked, two action rows
// CompactHeight = 380, NarrowHeight = 388 (were 452 / 476)

/// <summary>The hero never exceeds this fraction of the page viewport; on horizontal tiers the air around the copy
/// gives first (Justify=Center already absorbs it), on stacked tiers the PHOTO band shrinks to a 96-DIP floor.</summary>
public const float MaxViewportFraction = 0.45f;
public const float MinPhotoHeight = 96f;

public static ArtistHeroMetrics For(float width, float pageViewportHeight, ArtistHeroTier previous)
{
    var tier = TierFor(width, previous);
    var m = tier switch { …as today, with the new constants… };
    if (pageViewportHeight <= 0f) return m;
    float cap = pageViewportHeight * MaxViewportFraction;
    if (m.MinHeight <= cap) return m;
    // Horizontal: clamp the whole hero (copy stays centred). Stacked: clamp the photo band, keep the identity band.
    float floor = m.Stacked ? IdentityHeightFor(tier) + MinPhotoHeight : CopyBudgetFor(tier);   // ~290 Wide / ~250 Medium
    return m with { MinHeight = MathF.Max(floor, cap) };
}

public static float PhotoHeightFor(in ArtistHeroMetrics m) => !m.Stacked ? m.MinHeight
    : MathF.Max(MinPhotoHeight, m.MinHeight - IdentityHeightFor(m.Tier));
```

`IdentityHeightFor(tier)` returns the two `…ExpandedIdentityHeight` constants; `CopyBudgetFor(tier)` is the horizontal
copy block worst case (verified 16 + 2-line name + 1-line bio + meta 20 + actions 36 + 4 gaps 32 + 2·24 padding) — spell
it out as constants so `ArtistHeroLayoutTests` can assert `MinHeight ≥ CopyBudgetFor(tier)` for every input.

The pre-measure variants (`HeroHeightFor`, `BlendBackdropHeightFor`, `BlendBoundaryFor`, `:107-116`) gain the same
`pageViewportHeight` parameter; grep their callers (`DetailVerticalLayout`, the artist page's skeleton/backdrop) and
thread it through.

### 3.2 `ArtistPage.Hero.cs`

- `:26-31`: `var metrics = ArtistHeroLayout.For(width, _pageViewportH, tier.Value);` where `_pageViewportH` is
  `HomeHeroLayout.PageViewportHeight`'s sibling — put the helper in a shared `Features/Shell/ShellViewport.cs`
  (`PageHeightFor(float viewportH)`) so Home and Detail use one constant. Read `UseContext(Viewport.Size).H` in
  `ArtistPage.Render` and store it next to `_heroWidth`.
- `HeroArt.Render` (`:305`) must receive the same `pageViewportH` (a second signal beside the width one, or fold both
  into one `Signal<(float W, float PageH)>`), otherwise the photo and the banner disagree by the cap.
- `:62-72` bio: `MaxLines = metrics.Tier is Wide ? 2 : 1` (the identity budgets above assume one line below Wide).
- `:149` stacked padding `Edges4(gutter, Spacing.M, gutter, Spacing.XL)` → `(gutter, Spacing.S, gutter, Spacing.M)`;
  `:163` horizontal `Edges4(gutter, Spacing.XXL, gutter, Spacing.XXL)` → `(gutter, Spacing.L, gutter, Spacing.L)`.
  Both are inside the new constants' budgets.
- `HeroActions` (`:92`): on Narrow the two action rows stay; on Compact one row (unchanged).

### 3.3 `ArtistPopular.cs` — the top-tracks band under the header

- `:82` `const float RowH = 56f;` → a per-band value computed in `Render` before `PagedShelf.Create` (`:146-171`):
  `float rowH = _classic ? ClassicRowH : bandW < ColBreakW ? 48f : 56f;` (one column ⇔ narrow band ⇔ compact rows; the
  cell density tiers at `:215-226` already assume `cellW < 340` is the squeezed shape). `MaxRows = 5` stays — the band
  pages instead of growing.
- Net effect at 900×600: header 476 → ~234 (cap) and top tracks 5×56 → 5×48; the "Popular" header and first rows are
  on screen without scrolling.

### 3.4 Tests

- `ArtistHeroLayoutTests.cs:25-33`, `:42-65`, `:92-102`: new constants.
- `:121-126` — **invert the invariant**: stacked tiers must be `≤ MediumHeight + 80` (they were required to be taller).
  Add: `For(width, pageH: 520, …).MinHeight <= 0.45·520 + ε` for every tier; `PhotoHeightFor` never below 96; the
  horizontal floor `≥ CopyBudgetFor(tier)`; hysteresis unchanged.
- `DetailVerticalLayoutTests.cs:543` (`RevealWindow_OverlapsTheHeroFadeAtEveryHeroHeight`) — feed the new heights.

---

## 4. Verification (no tests can see pixels — run it)

`dotnet run --project src/apps/Wavee -- --fake`, then at **900×600**, **1280×720**, **1920×1080** and one live width
drag across 700/980 (home) and 360/600/880 (artist):

| Check | Expected |
|---|---|
| Home hero at 900×600 | ≤ 42 % of the page viewport (~218 DIP), 1-line title, no tags, pulse only on the daylist; the row below is visible above the fold |
| Home hero drag across 980 | tier flips once per direction, no flicker in the 956–1004 band |
| Top artists at 640 / 800 / 1000 wide | always one row; avatars shrink (labels 1 line, then hidden) before anything clips |
| Artist page 900×600 | hero ≤ 45 % of page height; photo ≥ 96; name + follow/play visible; "Popular" header on screen |
| Artist page 1920×1080 | Wide 360, copy centred, no visual regression on the collapse-to-context-band scroll |
| `dotnet test src/apps/Wavee.Tests` | green after the four test files above are updated |
| Debug + Release build | clean (`TreatWarningsAsErrors`) |

---

## 5. Files touched

| File | Fix |
|---|---|
| `src/apps/Wavee/Features/Home/HomeHeroLayout.cs` (rewrite per §1.1) | A |
| `src/apps/Wavee/Features/Home/HomeCards.cs:276-427` (`HeroBand`), `:991-1066` (`RankedAvatar`) | A, B |
| `src/apps/Wavee/Features/Home/HomePage.cs:612-623`, `:686-695`, `:514` (estimator inputs) | A |
| `src/apps/Wavee/Features/Home/HomeModules.cs:634`, `:694` | A |
| `src/apps/Wavee/Features/Home/HomeModules.Artists.cs:99`, `:105-109`, `:125`, `:131-136` | B |
| `src/apps/Wavee/Features/Home/HomeArtistRowLayout.cs:49-51` + new pure rules | B |
| `src/apps/Wavee/Features/Detail/ArtistHeroLayout.cs:31-52`, `:56-58`, `:80-90`, `:107-116` | C |
| `src/apps/Wavee/Features/Detail/ArtistPage.Hero.cs:26-31`, `:62-72`, `:149`, `:163`, `:305` | C |
| `src/apps/Wavee/Features/Detail/ArtistPopular.cs:82`, `:146-171` | C |
| **new** `src/apps/Wavee/Features/Shell/ShellViewport.cs` (`PageHeightFor`) | A, C |
| `src/apps/Wavee.Tests/{HomeHeroLayoutTests,HomeArtistRowLayoutTests,ArtistHeroLayoutTests,DetailVerticalLayoutTests}.cs` | A, B, C |
| `src/apps/Wavee/Features/Home/DEFECT_REGISTER.md:10`, `CHANGELOG.md` | A |
