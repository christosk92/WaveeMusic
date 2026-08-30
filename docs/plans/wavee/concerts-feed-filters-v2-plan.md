# Concerts v2: feed filters, live counts, and per-genre carousels

Source: `concerts_v2.saz` (Fiddler capture, 2026-07-14, 113 sessions decoded). This documents every concert-relevant
Pathfinder operation observed, then proposes the UI upgrade the extra variables unlock. All request/variable shapes
below are copied verbatim from the capture, not inferred.

## 1. Inventory — what the capture shows

### 1.1 Operations (persisted-query hashes are wire contract)

| Operation | sha256Hash | Status in Wavee |
|---|---|---|
| `concertFeed` | `9cae2dbee3f47904c60bab45256260b3ddb9844d5ef25038c17112619d14ce9a` | **Used** — but only 3 of its 6 variables |
| `concertCount` | `29be9d486e073a49268e13ed9e2d2180187e669fcb7a19b98011aca7ab61b141` | **New** — not wired |
| `concertConcepts` | `a409c1eb39b6345e7993d424d2408b65a6699bafc2b8a03217033e517cd76b72` | Used |
| `concertLocationDetails` | `b13f195349f188fee25480ae889d782852d68663bf07743c654244454750d681` | Used (location snapshot) |
| `userLocation` / `inferredUserLocation` | `079939…` / `5db4c5…` | Used |

### 1.2 `concertFeed` variables — the full set

```json
{"geoHash":"dr5regy3zfj0","geonameId":"5128581",
 "dateRange":{"from":"2026-07-17","to":"2026-07-19"},
 "conceptUris":["spotify:concept:0d5Bcm…","spotify:concept:4S6nB8…","spotify:concept:5eYB5S…"],
 "radiusInKm":25,"paginationKey":null}
```

- **`dateRange {from,to}`** (ISO dates) — Wavee always sends `null`. The capture shows weekend windows
  (`07-17→07-19`, `07-24→07-26`) and a free-form span (`08-05→08-20`): the official client's
  Today / This weekend / Choose dates filter.
- **`conceptUris` is an ARRAY** — Wavee sends a single-element array (one selected genre). With multiple
  URIs the feed responds with **one `ConcertCarousel` per concept**, keyed `"edm events near you"`,
  `"christian events near you"`, `"latin events near you"` — the server builds the per-genre shelves for us.
- **`radiusInKm`** — 25/50/100 observed. Wavee hardcodes 100.
- **`geonameId` + `geoHash` may BOTH be null** — the server then falls back to the saved account location
  (session 077 returned a full NYC feed with all-null variables).

### 1.3 `concertCount` — the live filter preview (NEW)

```json
// request                                                         // response
{"geonameId":"5128581","radiusInKm":100,                           {"data":{"concerts":{"concerts":
 "dateRange":null,"conceptUris":null}                                {"totalCount":9191}}}}
```

Observed fired per radius step (100→9191, 50→7620, 25→6657) while the user dragged the radius control:
it powers a **"Show N events"** live preview as filters change, without refetching the feed.

### 1.4 Response section vocabulary

- `ConcertCarousel { key, uri, isBeta, ubiIdentifier, concerts[] }` — key `"concerts-near-you"` or
  `"<genre> events near you"` (multi-concept mode).
- `LiveEventSection { key, concerts[] }` — key `"recommended-events"`.
- `AllEvents { paginationKey, sections[] }`.
- Carousel items can be `ConcertGroup { concerts[] }` — a multi-night run pre-grouped by the server
  (Wavee's month-board collapses runs client-side; the hub can reuse the server grouping).
- `concertConcepts` items carry a **`weight`** (5.0 → 0.0): the personalized ordering for the genre
  token row (weighted first, alphabetical tail).

## 2. Proposal — the hub filter bar v2

One pinned filter surface (the current genre-token card grows two controls), and every control funnels into the
ONE existing `RequeryFeed` generation discipline. No new pages.

```
┌ FILTER BY ─────────────────────────────────────────────────────────────────────┐
│ [All] [✓EDM] [Pop] [R&B] …   ·   [Any dates ▾] [Within 25 km ▾]   ·  6,657 events │
└─────────────────────────────────────────────────────────────────────────────────┘
```

- Genre tokens become **multi-select** (they're already ToggleButton pills — only the model is single-select).
- A **date pill** opens a small flyout: Any dates / Today / This weekend / Next weekend / a from–to picker.
- A **radius pill** cycles/opens 25 / 50 / 100 km.
- The trailing **live count** comes from `concertCount`, debounced ~250 ms behind filter edits.
- Multi-select genres render the server's per-genre carousels (section key → `ConcertHub.ConceptLabel` title).

### 2.1 `Wavee.Core/Domain/ConcertServices.cs` — query model

```csharp
public sealed record ConcertDateRange(DateOnly From, DateOnly To);

public sealed record ConcertFeedQuery(
    ConcertPlace? Location = null,
    IReadOnlyList<string>? ConceptUris = null,   // was: string? ConceptUri
    int? RadiusKm = 100,
    ConcertDateRange? DateRange = null,          // new
    string? PaginationKey = null);

public interface IConcertService
{
    // …existing members unchanged…
    Task<int?> GetFeedCountAsync(ConcertFeedQuery query, CancellationToken cancellationToken = default);
}
```

### 2.2 `Wavee/SpotifyLive/ConcertPathfinderRequests.cs` — wire writers

```csharp
public static void WriteFeed(Utf8JsonWriter writer, ConcertFeedQuery query)
{
    // …geoHash/geonameId exactly as today…
    if (query.DateRange is { } range)
    {
        writer.WriteStartObject("dateRange");
        writer.WriteString("from", range.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        writer.WriteString("to", range.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }
    else writer.WriteNull("dateRange");

    writer.WritePropertyName("conceptUris");
    if (query.ConceptUris is not { Count: > 0 } concepts) writer.WriteNullValue();
    else
    {
        writer.WriteStartArray();
        foreach (var uri in concepts) writer.WriteStringValue(uri);
        writer.WriteEndArray();
    }
    // …radiusInKm/paginationKey exactly as today…
}

// concertCount reuses the same variables minus paginationKey/geoHash (capture sends geonameId+radius+range+concepts).
public static void WriteFeedCount(Utf8JsonWriter writer, ConcertFeedQuery query) { /* same fields, no paginationKey */ }
```

### 2.3 `Wavee/SpotifyLive/PathfinderClient.cs` + `SpotifyConcertService.cs`

```csharp
public const string ConcertCount = "concertCount";
public const string ConcertCountHash = "29be9d486e073a49268e13ed9e2d2180187e669fcb7a19b98011aca7ab61b141";
```

```csharp
public async Task<int?> GetFeedCountAsync(ConcertFeedQuery query, CancellationToken ct = default)
{
    using var document = await QueryAsync(PathfinderOps.ConcertCount, PathfinderOps.ConcertCountHash,
        w => ConcertPathfinderRequests.WriteFeedCount(w, query), ct, TimeSpan.Zero).ConfigureAwait(false);
    return document is null ? null : ConcertPathfinderMapper.MapFeedCount(document.RootElement);
    // MapFeedCount: data.concerts.concerts.totalCount (int) — fixture: concert-count.json
}
```

### 2.4 `Wavee/Features/Concerts/ConcertHubModel.cs` — pure decisions (unit-tested)

```csharp
// Multi-select toggle replaces ToggleConcept's single-slot swap.
public static IReadOnlyList<string> ToggleConcept(IReadOnlyList<string> selected, string uri);
// Preset date windows, pure (now in, range out) so weekend math is testable:
public static ConcertDateRange? PresetRange(ConcertDatePreset preset, DateTimeOffset now);
public enum ConcertDatePreset { Any, Today, ThisWeekend, NextWeekend, Custom }
// Section title: key "edm events near you" → concept "EDM" via ConceptLabel; known keys keep loc'd captions.
public static string SectionTitle(string key, ConcertFeedSectionKind kind);
```

### 2.5 `ConcertHubPage` wiring

- `Signal<string?> _concept` → `Signal<IReadOnlyList<string>> _concepts` feeding `ConcertFeedQuery.ConceptUris`;
  `Signal<ConcertDatePreset>` + `Signal<ConcertDateRange?>`; `Signal<int?> _radius`.
- Every setter funnels through the existing `RequeryFeed(svc)` (generation bump → cancel → refetch), unchanged.
- A `FetchCount` sibling of `FetchConcepts` posts the debounced `concertCount` into `Signal<int?> _matchCount`;
  render shows `Strings.Concerts.MatchCount(count)` (`"{count, plural, one {# event} other {# events}}"`).
- Loc keys: `concerts.filter.anyDates/today/thisWeekend/nextWeekend/withinKm ("Within {km} km")/matchCount`.

### 2.6 Tests (`Wavee.Tests`)

- `WriteFeed` golden JSON: dateRange object + multi-concept array + explicit nulls (extend `ConcertPathfinderTests`).
- `MapFeedCount` fixture. `PresetRange` weekend math across a week boundary. Multi-select `ToggleConcept`.
- Feed mapper: carousel key `"edm events near you"` → titled concept section (fixture `concert-feed-concepts.json`).

## 3. UI implementation — the rev-7 concept (segmented-pill fusion)

Approved concept (artifact rev 7): `[📍 New York City ▾] │ [📅 Dates ▾] [This weekend] │ [✓All][top-3 genres][+N genres]`,
where a date selection FUSES the chip into the pill as a raised inner capsule: `⟨✓ This weekend⟩ Jul 17 – 19 ▾`.
All engine surfaces below verified against `src/` (Element.Animate/LayoutTransition+EnterExit, FrameClock.Tick,
AnimEngine.Animate/HasTracks, MotionRecipes, the ShyPillLingerClock per-frame idiom).

### 3.1 Files

| File | Role |
|---|---|
| `Wavee/Features/Concerts/ConcertFilterBar.cs` (new) | The pinned filter card: where pill, when area, tokens, count. Owns NO queries — writes a `ConcertHubFilters` signal the page reacts to. |
| `Wavee/Features/Concerts/ConcertDateFlyout.cs` (new) | Drill-down flyout content (root ▸ month ▸ calendar), anchored via the existing `IOverlayService` pattern (`ConcertLocationController` precedent). |
| `Wavee/Features/Concerts/CountTicker.cs` (new) | The animated count leaf (§3.4). |
| `Wavee/Components/ConcertUi.cs` | + `SegmentedDatePill`, `WherePill` pure factories (FilterToken exists). |
| `Wavee/Features/Concerts/ConcertHubModel.cs` | + pure helpers: `PresetRange`, multi-`ToggleConcept`, `TopConcepts`, `WhenLabel`. |
| `Wavee/Features/Concerts/ConcertHubPage.cs` | Slims: hosts the bar, funnels `ConcertHubFilters` changes into the ONE existing `RequeryFeed` generation discipline. |

### 3.2 State model (pure, unit-tested)

```csharp
public enum ConcertWhenKind { Any, Today, ThisWeekend, NextWeekend, Custom }

/// <summary>The full filter tuple the bar edits. Name is the fused pill's segment text ("This weekend",
/// "August", "Weekend", "Custom"); Range is what goes on the wire AND renders as "Jul 17 – 19".</summary>
public sealed record ConcertWhen(ConcertWhenKind Kind, string Name, ConcertDateRange? Range)
{
    public static readonly ConcertWhen Any = new(ConcertWhenKind.Any, "", null);
}

public sealed record ConcertHubFilters(
    ConcertPlace? Place, int RadiusKm, ConcertWhen When, IReadOnlyList<string> ConceptUris);

// ConcertHub statics (Wavee.Tests):
public static ConcertDateRange? PresetRange(ConcertWhenKind kind, DateTimeOffset now);      // weekend = Fri–Sun
public static IReadOnlyList<string> ToggleConcept(IReadOnlyList<string> selected, string uri);
/// <summary>Rest-state tokens: the provider's weight order is ALREADY the response order (mapper preserves it) —
/// take the first three, always append any selected concept not in that head, cap none when expanded.</summary>
public static IReadOnlyList<ConcertConcept> TopConcepts(IReadOnlyList<ConcertConcept> all,
    IReadOnlyList<string> selected, bool expanded, int top = 3);
```

### 3.3 The segmented pill + fusion choreography

The reconciler cannot MOVE a node across parents (keyed reuse is per-parent), so the fuse is a keyed state swap
choreographed with the landed Enter/Exit legs — no `FG_DETACHED_FLY` dependency:

- **Rest** when-area children: `datePill (Key="when-pill")`, `chip (Key="when-chip")`.
- **Active**: one `fusedPill (Key="when-pill")` — SAME key, so the node is reused and its width change animates
  via `Size: SizeMode.Reflow, Axes: SizeAxes.Width` (the FilterToken recipe). Children: `seg`, `dates`, chevron.

```csharp
// ConcertUi.SegmentedDatePill — the fused state. Outer rides the reused "when-pill" node (width reflow);
// the segment ENTERS from the chip's direction (right) and the chip EXITS toward the pill (left): the two
// legs overlap in time, reading as the dock. Depth: seg carries its own small shadow on the accent surface.
public static Element SegmentedDatePill(string name, string rangeText, Action onClick) => new BoxEl
{
    Key = "when-pill",
    Animate = new LayoutTransition(
        TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(260f, Easing.SmoothOut),
        Size: SizeMode.Reflow, Axes: SizeAxes.Width),
    Direction = 0, Height = 32f, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 8f,
    Padding = new Edges4(3f, 3f, 12f, 3f),
    Corners = CornerRadius4.All(WaveeRadius.Pill),
    Fill = Tok.AccentDefault, HoverFill = Tok.AccentSecondary, PressedFill = Tok.AccentTertiary,
    Shadow = Elevation.Card,
    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
    Children =
    [
        new BoxEl   // the docked segment — raised card capsule, the chip's visual survivor
        {
            Key = "when-seg",
            Animate = new LayoutTransition(
                TransitionChannels.Position | TransitionChannels.Opacity,
                TransitionDynamics.Tween(300f, Easing.SmoothOut),
                Enter: new EnterExit(Dx: 56f, Opacity: 0.4f, Active: true)),   // arrives FROM the chip's side
            Direction = 0, Height = 26f, AlignItems = FlexAlign.Center, Gap = 5f,
            Padding = new Edges4(11f, 0f, 11f, 0f), Corners = CornerRadius4.All(13f),
            Fill = Tok.FillCardDefault, Shadow = Elevation.Flyout,
            Children =
            [
                Icon(Mdl.Check, 12f, Tok.AccentTextPrimary) with { Shrink = 0f },
                Body(name) with { Color = Tok.AccentTextPrimary, Weight = 600, MaxLines = 1 },
            ],
        },
        Body(rangeText) with { Color = Tok.TextOnAccentPrimary, Weight = 600, MaxLines = 1 },
        Icon(Mdl.ChevronDown, 10f, Tok.TextOnAccentPrimary) with { Shrink = 0f },
    ],
};
```

The rest-state chip declares the reverse leg so removal flies it INTO the pill:

```csharp
chip = ConcertUi.FilterToken(label, selected: false, onTap) with
{
    Key = "when-chip",
    Animate = new LayoutTransition(TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Tween(220f, Easing.SmoothIn),
        Exit: new EnterExit(Dx: -56f, Opacity: 0f, Active: true)),
};
```

Un-fuse (Anytime) is the mirror: the seg's Exit slides right/fades, the chip's Enter slides in from the pill.
The `Dx: 56f` constant approximates the chip↔pill gap; a Phase-2 polish can compute it from realized rects and
route through `DetachedAnimSlab`/`RecordDetached` once `FG_DETACHED_FLY` is proven — the declarative version
ships first and degrades to a crossfade under reduced-motion (the engine folds that in; never a hook branch).

### 3.4 The count — YES, it counts up/down (CountTicker)

The number animates numerically (9,191 → 6,657 eases through the intermediate values), using the
`ShyPillLingerClock` idiom so the per-frame wake exists ONLY while a tween runs (hub is long-lived; a permanent
`FrameClock.Tick` subscription is not acceptable):

```csharp
/// <summary>Animated integer readout. Parent hands the debounced target; while a tween is active a keyed child
/// clock re-renders THIS LEAF every frame (never the page — the virtual-list remount gotcha) and eases the shown
/// value; on settle the clock unmounts and the component costs nothing. ~380ms cubic ease-out.</summary>
sealed class CountTicker : Component
{
    public required IReadSignal<int?> Target;
    readonly Signal<double> _shown = new(0);
    int _animatingTo = int.MinValue;
    double _from; long _startTicks;

    public override Element Render()
    {
        int target = Target.Value ?? 0;                    // subscribe: new debounced count re-renders us
        bool animating = (int)Math.Round(_shown.Peek()) != target;
        if (animating && _animatingTo != target) { _animatingTo = target; _from = _shown.Peek(); _startTicks = 0; }

        var text = WaveeType.TrackTitle(((int)Math.Round(_shown.Value)).ToString("N0", CultureInfo.CurrentCulture))
            with { MaxLines = 1 };
        var kids = new List<Element>(2)
        {
            // No tabular numerals in the text stack (verified): reserve width + right-align so the card's
            // caption row never reflows while digits spin.
            new BoxEl { MinWidth = 64f, Direction = 0, Justify = FlexJustify.End, Children = [ text ] },
        };
        if (animating)
            kids.Add(Embed.Comp(() => new TickerClock
            {
                OnFrame = now =>
                {
                    if (_startTicks == 0) _startTicks = now;
                    double p = Math.Min(1.0, (now - _startTicks) / (double)TimeSpan.FromMilliseconds(380).Ticks);
                    p = 1 - Math.Pow(1 - p, 3);
                    _shown.Value = _from + (_animatingTo - _from) * p;
                },
            }) with { Key = "count-tick:" + target });     // retarget mid-flight = remount = clean restart
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Children = kids.ToArray() };
    }
}

/// <summary>ShyPillLingerClock's shape: an invisible node subscribed to FrameClock.Tick, calling OnFrame with
/// the frame timestamp while mounted. One per-frame wake, only while a count tween is in flight.</summary>
sealed class TickerClock : Component
{
    public required Action<long> OnFrame;
    public override Element Render()
    {
        _ = UseContext(FrameClock.Tick);
        UseEffect(() => OnFrame(DateTime.UtcNow.Ticks));   // match ShyPillLingerClock's tick-keyed effect shape
        return new BoxEl { HitTestVisible = false, Width = 0f, Height = 0f };
    }
}
```

Wire: `_matchCount` (Signal<int?>) is fed by a debounced `FetchCount` sibling of `FetchConcepts` — 250 ms
`Task.Delay` + the SAME generation guard, calling `GetFeedCountAsync` (§2.3, `ttl: TimeSpan.Zero` — counts must
never replay stale). The feed itself only refetches on committed changes; the count previews every edit.

### 3.5 Tokens, flyout, phasing

- **Tokens**: `TopConcepts` renders All + top-3 + selected + the dashed `+N genres` toggle (a FilterToken variant);
  expansion re-renders the strip — new tokens stagger in via `Animate = Position|Opacity` with `Enter(Dx:-8, Opacity:0)`.
- **Flyout**: one panel component with a `view` signal (root | month); month leaf = quick rows + the Su–Sa day
  grid (buttons over `DateOnly`, provider-preserved offsets are irrelevant here — dates only); the apply button
  text IS a `CountTicker` bound to a per-range debounced count. **Phase 1 ships header + apply-button counts only**;
  per-row counts (one `concertCount` per visible row, response-cached) are Phase 2 — a burst of 8 uncached
  queries per open is not acceptable until measured.
- **Localization**: every new label through `Strings.Concerts.Filter.*` (dates, thisWeekend, nextWeekend,
  withinKm `{km}`, moreGenres `{count}`, showLess, eventCount plural, allOf `{month}`, weekend, custom, showEvents `{count}`).
- **Order of work**: (1) wire layer §2 + tests → (2) `ConcertFilterBar` static states (rest/fused, no motion) →
  (3) fusion legs + count ticker → (4) flyout drill-down → (5) `+N` expansion + polish pass with screenshots.
- **Tests**: §2.6 plus `TopConcepts` (top-3/selected union/expanded), `PresetRange` across a Fri/Sat/Sun "now",
  `WhenLabel` formatting, CountTicker easing function (pure part extracted).

### Non-goals / kept honest

- `ConcertGroup` (server-side run grouping) is mapped but the hub keeps rendering flat cards in v1 of this
  change; the month-board's client grouping stays the only run UI until we design a hub group tile.
- `searchTopResultsList(includeArtistHasConcertsField)` and the concerts editorial playlist rows exist in the
  capture but are search-surface concerns, out of scope here.
