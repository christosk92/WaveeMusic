# Track filter v2 — implementation plan

Status: planned 2026-09-06, not started. Design: `track-filter-v2-mica.html` (published artifact
<https://claude.ai/code/artifact/829ee4c4-4781-4969-8604-1a437740cfc9>). Issue: to be filed before the first commit
(`github-triage`; `type: enhancement`, `area: detail-pages`, milestone `0.2.x Breaker`).

## Context

`TrackFilterFlyout` (`src/apps/Wavee/Features/Detail/TrackFilterFlyout.cs`) is a settings dialog inside a popover: a
34-DIP accent icon tile, a two-line header ("Filter tracks / All tracks"), three captioned groups, a `Segmented`
scope picker, two three-way `Segmented` traits whose first segment reads "All", two `CheckBox`es each wrapped in a
bordered card, four `Expander` disclosures for the ranges (with a scroll-into-view timer and an open-only-one rule),
and a "No filters applied / Clear all filters" footer. Six questions cost ~520 DIP of chrome and four container
styles, and the answers are hidden behind expanders.

The redesign keeps the model (`TrackFilterState`, `TrackFilterModel.Matches`, `TrackFilterCapabilities`) and changes
the vocabulary and the chrome: one row per facet whose options are **words** (the chosen one 600 weight with the Home
tab's 2-DIP accent underline), real `CheckBox` rows for the binary facets, the search **scope moves into the search
box**, the page's own lenses (artist / year window / tag on Liked Songs) appear as chosen words with an ×, and the
only chrome is a title, the live count ("2 active · 133 of 148 songs") and Clear.

## Wireframe (392 wide, Full capabilities)

```
┌ Filter    2 active · 133 of 148 songs                          Clear ┐  40
├──────────────────────────────────────────────────────────────────────┤
│ ▢ Explicit      Show   Hide   Only                                    │  34
│                        ‾‾‾‾                                           │
│ ▭ Video         Show   Hide   Only                                    │
│ ◷ Length        Any   Under 3 min   3–5 min   Over 5 min              │
│ ♪ Tempo         Any   Slow ‹90   Mid 90–119   Upbeat 120–139          │
│                 Fast 140+                                             │  (wraps)
│ ▦ Added         Any time   7 days   30 days   6 months   1 year       │
│ ▤ Source        Any   Streamed   Local                                │  (HasMixedOrigin only)
├──────────────────────────────────────────────────────────────────────┤
│ ☐ ♡ Liked songs only                                                  │  34
│ ☐ ▷ Playable only                              2 unavailable here     │
└──────────────────────────────────────────────────────────────────────┘
   Liked Songs adds, ABOVE the rule:  Artist  [Phil Collins ×]  ·  Year  [1981–1985 ×]  ·  Tag  Any  Soft rock
```

Component tree:

```
FilterButton (DetailTracks.cs)             unchanged badge/anchor; passes a counts delegate
└─ Overlay popup → TrackFilterFlyout        rewritten: header · rows · rule · checks
   ├─ Header                                TextEl 16/600 · summary 12/tertiary · Clear (WaveeCta.TextAction)
   ├─ for each TrackFilterRows.Row:
   │    Lens   → LensRow   (label · [value ×])
   │    Choice → WordRadio (label+glyph column 104 · words)          ← new shared control, Components/WordRadio.cs
   │    Check  → CheckRow  (CheckBox.Create, glyph, trailing note)
   └─ Divider between the choice rows and the check rows
DetailTrackSearchField (DetailTracks.cs)    RightAffix gains the scope chip "Everything ▾" (MenuFlyout radio rows)
```

## Workstreams (parallel subagents on disjoint files; only the orchestrator builds/tests/launches)

### W1 — `TrackFilterRows`: the pure row model (engine-free, tested)

New `src/apps/Wavee/Features/Detail/TrackFilterRows.cs`, source-included in `Wavee.Tests.csproj` beside
`TrackFilterModel.cs` (it is; `TrackFilterModelTests` proves the include). No Loc/Icons/Element: rows carry enum
identities and the formatter (W3) turns them into words.

```csharp
public enum TrackFilterRowKind : byte { Lens, Choice, Check }
public enum TrackFilterFacet : byte { Explicit, Video, Length, Tempo, Added, Source, LikedOnly, PlayableOnly, ArtistLens, YearLens, TagLens }

/// <summary>One row of the flyout. Choice rows carry their option count and the selected index; Check rows a bool;
/// Lens rows the display text the × clears. Rows a page cannot answer are ABSENT (never disabled), except a facet
/// that is non-default — a state the user set must stay visible so it can be cleared.</summary>
public readonly record struct TrackFilterRow(TrackFilterRowKind Kind, TrackFilterFacet Facet,
                                            int OptionCount, int Selected, bool Checked, string? LensText);

public static class TrackFilterRows
{
    public static List<TrackFilterRow> For(in TrackFilterCapabilities caps, in TrackFilterState s)
    {
        var rows = new List<TrackFilterRow>(10);
        if (s.ArtistId is { Length: > 0 }) rows.Add(Lens(TrackFilterFacet.ArtistLens, s.ArtistName ?? s.ArtistId));
        if (s.ReleaseYearMin != 0 || s.ReleaseYearMax != 0) rows.Add(Lens(TrackFilterFacet.YearLens, YearText(s)));
        if (s.Tag is { Length: > 0 }) rows.Add(Lens(TrackFilterFacet.TagLens, s.Tag));
        rows.Add(Choice(TrackFilterFacet.Explicit, 3, (int)s.ExplicitMode));
        if (caps.HasVideo || s.VideoMode != TrackTraitMode.All) rows.Add(Choice(TrackFilterFacet.Video, 3, (int)s.VideoMode));
        rows.Add(Choice(TrackFilterFacet.Length, 4, (int)s.Duration));
        if (caps.HasTempo || s.Tempo != TrackTempoBand.Any) rows.Add(Choice(TrackFilterFacet.Tempo, 5, (int)s.Tempo));
        if (caps.HasDateAdded || s.Added != TrackAddedRange.Any) rows.Add(Choice(TrackFilterFacet.Added, 5, (int)s.Added));
        if (caps.HasMixedOrigin || s.Origin != TrackOriginFilter.Any) rows.Add(Choice(TrackFilterFacet.Source, 3, (int)s.Origin));
        if (caps.HasLibrary || s.LikedOnly) rows.Add(Check(TrackFilterFacet.LikedOnly, s.LikedOnly));
        if (caps.HasUnavailable || s.PlayableOnly) rows.Add(Check(TrackFilterFacet.PlayableOnly, s.PlayableOnly));
        return rows;
    }

    /// <summary>What a tap on option <paramref name="index"/> of a Choice row writes. Added goes through
    /// WithAddedRange (the sparkline window is the same question). One switch, so the flyout never re-derives it.</summary>
    public static TrackFilterState Apply(in TrackFilterState s, TrackFilterFacet facet, int index) => facet switch
    {
        TrackFilterFacet.Explicit => s with { ExplicitMode = (TrackTraitMode)index },
        TrackFilterFacet.Video    => s with { VideoMode = (TrackTraitMode)index },
        TrackFilterFacet.Length   => s with { Duration = (TrackDurationRange)index },
        TrackFilterFacet.Tempo    => s with { Tempo = (TrackTempoBand)index },
        TrackFilterFacet.Added    => s.WithAddedRange((TrackAddedRange)index),
        TrackFilterFacet.Source   => s with { Origin = (TrackOriginFilter)index },
        _ => s,
    };

    public static TrackFilterState Toggle(in TrackFilterState s, TrackFilterFacet facet, bool on) => facet switch
    {
        TrackFilterFacet.LikedOnly    => s with { Flags = on ? s.Flags | TrackFilterFlags.LikedOnly : s.Flags & ~TrackFilterFlags.LikedOnly },
        TrackFilterFacet.PlayableOnly => s with { Flags = on ? s.Flags | TrackFilterFlags.PlayableOnly : s.Flags & ~TrackFilterFlags.PlayableOnly },
        _ => s,
    };

    public static TrackFilterState ClearLens(in TrackFilterState s, TrackFilterFacet facet) => facet switch
    {
        TrackFilterFacet.ArtistLens => s.WithArtist(null),
        TrackFilterFacet.YearLens   => s.WithReleaseYear(0, 0),
        TrackFilterFacet.TagLens    => s with { Tag = null },
        _ => s,
    };
}
```

`TrackFilterState.ActiveCount` stops counting `SearchScope` (the scope is a property of the search box, not a
filter; the funnel badge must not light for it). `TrackFilterModelTests` gets the corresponding case flipped, plus
new `TrackFilterRowsTests`: row order per capability set (Full / album / Liked with lenses / local-files playlist);
a non-default facet stays even when its capability is off; `Apply`/`Toggle`/`ClearLens` write exactly one facet;
`Added` clears the sparkline window.

### W2 — `WordRadio`: the option row as words (shared control)

New `src/apps/Wavee/Components/WordRadio.cs`. Built on the engine's `RadioButtons` so the keyboard and automation
semantics come for free (one tab stop per row, ←/→ move, Space/Enter picks, `AutomationRole.RadioButton`); the
glyph is hidden through the control's own style seam and each item draws its word.

```csharp
public readonly record struct WordRadioItem(string Word, string? Hint = null);   // Hint: "‹90", "90–119" …

public static class WordRadio
{
    static readonly RadioButton.Style Words = RadioButton.DefaultStyle with
    {
        ShowGlyph = false, ContentGap = 0f, MinHeight = 32f, FontSize = 13.5f,
    };

    public static Element Create(IReadOnlyList<WordRadioItem> items, Signal<int> value, Action<int> onChange)
        => RadioButtons.Create(items.Count, i => Word(items[i], i, value), value, onChange,
                               maxColumns: items.Count, style: Words);

    static Element Word(WordRadioItem item, int index, Signal<int> value) => new BoxEl
    {
        Direction = 1, Padding = new Edges4(8f, 7f, 8f, 4f), Corners = Radii.ControlAll,
        Children =
        [
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Baseline, Gap = 4f,
                Children =
                [
                    new TextEl(item.Word)
                    {
                        Size = 13.5f, MaxLines = 1,
                        Weight = Prop.Of(() => value.Value == index ? (ushort)600 : (ushort)400),
                        Color = Prop.Of(() => value.Value == index ? Tok.TextPrimary : Tok.TextSecondary),
                    },
                    .. item.Hint is { Length: > 0 } h ? [new TextEl(h) { Size = 11f, Color = Tok.TextTertiary }] : Array.Empty<Element>(),
                ],
            },
            // The Home tab's mark: 2 DIP, accent, transparent when not chosen so the word never shifts.
            new BoxEl { Height = 2f, Margin = new Edges4(0f, 3f, 0f, 0f), Corners = CornerRadius4.All(1f),
                        Fill = Prop.Of(() => value.Value == index ? Tok.AccentDefault : ColorF.Transparent) },
        ],
    };
}
```

Verify at implementation time: (1) `RadioButtons.Create(count, itemContent, …)` lays items in a wrapping grid at
`maxColumns` and that hover paints on the item root (else wrap each word in `.Interactive(Interaction.Subtle)`);
(2) `RadioButton.Style.ShowGlyph = false` removes the 20-DIP glyph column entirely (the doc says it does); (3) the
`Weight`/`Color`/`Fill` binds are `Prop` channels the reconciler keeps (they are static per item index, so the
mount-only-binding pitfall does not apply). If (1) fails, the fallback is the `LibraryV3Chips.Rove` pattern: a row
of `Role = RadioButton` boxes with roving focus by key — the same keyboard contract, hand-rolled.

### W3 — `TrackFilterFlyout` rewrite

`src/apps/Wavee/Features/Detail/TrackFilterFlyout.cs` is rewritten around `TrackFilterRows` (delete the `Section`
class, the disclosure parts, the reveal timer, `OpenOnly`, `ScopePicker`, `TraitFacet`, `StatusChoice`,
`Disclosure`, the icon tile, the subtitle, the footer). Per-facet `Signal<int>`s stay (they are what `WordRadio`
binds), seeded from `_filters.Peek()` at construction and re-synced in an effect keyed on `_filters.Value` so an
external change (a lens click on the facts panel while the flyout is open) is reflected.

```csharp
public override Element Render()
{
    var s = _filters.Value;
    var (shown, total) = _counts();                          // W5's delegate — the header's "133 of 148 songs"
    var rows = TrackFilterRows.For(in _caps, in s);
    var kids = new List<Element>(rows.Count + 3) { Header(s.ActiveCount, shown, total) , Rule() };
    bool ruleBeforeChecks = false;
    foreach (var row in rows)
    {
        if (row.Kind == TrackFilterRowKind.Check && !ruleBeforeChecks) { kids.Add(Rule()); ruleBeforeChecks = true; }
        kids.Add(row.Kind switch
        {
            TrackFilterRowKind.Lens => LensRow(row),
            TrackFilterRowKind.Choice => ChoiceRow(row),
            _ => CheckRow(row),
        });
    }
    return new BoxEl { Direction = 1, Width = 392f, Padding = new Edges4(4f, 6f, 4f, 8f), Children = [.. kids] };
}

Element Header(int active, int shown, int total) => new BoxEl
{
    Direction = 0, Height = 40f, AlignItems = FlexAlign.Center, Gap = 10f, Padding = new Edges4(14f, 0f, 12f, 0f),
    Children =
    [
        new TextEl(Loc.Get(Strings.Detail.Filter.Short)) { Size = 16f, Weight = 600 },                // "Filter"
        new TextEl(active == 0 ? Strings.Detail.Filter.SummaryAll(total) : Strings.Detail.Filter.Summary(active, shown, total))
            { Size = 12f, Color = Tok.TextTertiary, Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        WaveeCta.TextAction(Loc.Get(Strings.Detail.Filter.Clear), ClearAll, enabled: active > 0),
    ],
};

Element ChoiceRow(in TrackFilterRow row) => new GridEl
{
    Columns = [TrackSize.Pixels(104f), TrackSize.Star()], ColGap = 8f, MinHeight = 34f,
    Padding = new Edges4(14f, 0f, 8f, 0f),
    Children =
    [
        Label(TrackFilterWords.Glyph(row.Facet), TrackFilterWords.Label(row.Facet)),   // 13 secondary + 14-DIP glyph, top-aligned at 8
        WordRadio.Create(TrackFilterWords.Options(row.Facet), SignalFor(row.Facet),
                         i => _setFilters(TrackFilterRows.Apply(_filters.Peek(), row.Facet, i))),
    ],
};
```

`CheckRow` = `CheckBox.Create(label, signal, on => _setFilters(TrackFilterRows.Toggle(_filters.Peek(), facet, on)),
style: CheckBox.DefaultStyle with { MinHeight = 34f, FontSize = 13.5f, ContentGap = 10f })` preceded by a 16-DIP
glyph, in a plain 34-DIP hover-plate row (no border, no fill) — `PlayableOnly` adds a trailing tertiary 11 note
`Strings.Detail.Filter.UnavailableHere(n)` when the host reports `n` unavailable tracks (a new
`TrackFilterCapabilities.UnavailableCount`, default 0; the note is omitted at 0). `LensRow` = label + one chosen word
(`[Phil Collins ×]`: the word in the chosen style, the × a 20-DIP `IconButton` calling
`TrackFilterRows.ClearLens`).

`TrackFilterWords` (same file, engine-bound) is the one table from facet → `(glyph, label, options)`:
Explicit → `Icons.Important`, "Explicit", [Show, Hide, Only]; Video → `Icons.Movie`, "Video", same; Length →
`Icons.Clock`, "Length", [Any, Under 3 min, 3–5 min, Over 5 min]; Tempo → `Icons.MusicNote`, "Tempo",
[Any, Slow ‹90, Mid 90–119, Upbeat 120–139, Fast 140+]; Added → `Icons.Calendar`, "Added", [Any time, 7 days,
30 days, 6 months, 1 year]; Source → `Icons.Folder`, "Source", [Any, Streamed, Local].

### W4 — the scope chip in the search box

`DetailTrackSearchField` (`DetailTracks.cs` ~:4150-4228) takes `IReadSignal<TrackFilterState> filters` and
`Action<TrackFilterState> setFilters` from both mount sites (`:2247`, `:2279` — the same `h.Filters`/`h.SetFilters`
the `FilterButton` gets). Its `RightAffix` gains, before the clear button, a chip:

```csharp
Element ScopeChip() => ToolTip.Wrap(new BoxEl
{
    Direction = 0, Gap = 4f, Height = 24f, Padding = new Edges4(8f, 0f, 6f, 0f), AlignSelf = FlexAlign.Center,
    Corners = Radii.ControlAll, Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
    OnClick = ToggleScopeMenu, OnRealized = h => scopeAnchor.Value = h,
    Children =
    [
        new TextEl(Prop.Of(() => TrackFilterWords.Scope(_filters.Value.SearchScope))) { Size = 12f, Weight = 600, Color = Tok.TextSecondary },
        Icon(Icons.ChevronDown, 11f, Tok.TextTertiary),
    ],
}.Interactive(Interaction.Subtle), Loc.Get(Strings.Detail.Filter.SearchIn));
```

The menu is `MenuFlyout.Create` with four `MenuFlyoutItem.RadioItem`s (Everything · Title · Artist · Album) built at
open time, `FlyoutPlacement.BottomEdgeAlignedRight`, writing `_setFilters(f with { SearchScope = … })`. The chip
shows only while the field is expanded (it lives in the affix row, which is the expanded state). `Placeholder` follows
the scope ("Search titles" …) through the existing `searchThisList` key plus three new ones.

### W5 — the count seam

`FilterButton` gains a `Func<(int Shown, int Total)> counts` ctor parameter it forwards to the flyout. The host
(`DetailTracks`'s list component, where `View(snapshot)` filters) implements it as a pure loop over the current
snapshot with the SAME `TrackFilterModel.Matches` call `View` uses (`hasVideo`, `isSaved`, `now`) — no signal write
(a render-time write is forbidden), no cache: the loop runs only while the flyout is open and the flyout re-renders
on `_filters`. Both mount sites (`:2105`, `:2279`) pass it. `TrackFilterCapabilities` gains `UnavailableCount`
(computed where `HasUnavailable` is).

### W6 — localization (`assets/loc/{en-US,nl,ko-KR}.json`, CRLF, no BOM)

Add under `detail.filter`: `show` "Show" · `length` "Length" · `any` "Any" · `underThreeShort` "Under 3 min" ·
`threeToFiveShort` "3–5 min" · `overFiveShort` "Over 5 min" · `tempoSlow` "Slow" · `tempoMid` "Mid" ·
`tempoUpbeat` "Upbeat" · `tempoFast` "Fast" · `tempoHintSlow` "‹90" · `tempoHintMid` "90–119" ·
`tempoHintUpbeat` "120–139" · `tempoHintFast` "140+" · `added7d` "7 days" · `added30d` "30 days" ·
`added6mo` "6 months" · `added1y` "1 year" · `addedShort` "Added" · `explicitShort` "Explicit" ·
`videoShort` "Video" · `likedSongsOnly` "Liked songs only" · `playableOnlyShort` "Playable only" ·
`unavailableHere` `{count, plural, one {# unavailable here} other {# unavailable here}}` ·
`summary` "{active} active · {shown} of {total} songs" · `summaryAll` `{total, plural, one {# song} other {# songs}}` ·
`searchTitles` "Search titles" · `searchArtists` "Search artists" · `searchAlbums` "Search albums" ·
`artistLens` "Artist" · `yearLens` "Year" · `tagLens` "Tag".
Retire (all three files): `content`, `moreFilters`, `noFiltersApplied`, `clearFilters`, `allTracks`, `activeCount`,
`all`, `anyDuration`, `anyTempo`, `anySource`, `tempoUnder90`…`tempo140Up` (the long forms), `underThree`…`overFive`
(the long forms), `lastSevenDays`…`lastYear` (the long forms), `explicitContent`, `videoTracks`, `likedOnly`,
`playableOnly`, `duration`. Check each key's remaining readers with grep before retiring; `title` ("Filter tracks")
stays as the funnel tooltip. No leaf may equal its group name (`pitfalls.md`).

### W7 — tests, docs, changelog

Tests: `TrackFilterRowsTests` (W1), `TrackFilterModelTests` (`ActiveCount` no longer counts the scope), a
`WordRadio` layout smoke test only if the engine's headless harness already covers `RadioButtons` (do not add a
source-text test). Docs: `.claude/skills/wavee/SKILL.md`'s detail-page notes if they name the flyout; CHANGELOG
bullet under `### Changed` ending with the issue ref; commit body `Fixes #n`.

## Verification (orchestrator)

1. `dotnet build Wavee.slnx -c Release` (the running Debug app locks Debug output; see memory) and
   `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj --filter "FullyQualifiedName~TrackFilter|FullyQualifiedName~Tempo|FullyQualifiedName~ContentFilter"`.
2. Eyeball, `dotnet run --project src/apps/Wavee -- --fake` then a signed-in session: the flyout on a playlist
   (all rows), an album (no Added/Playable), Liked Songs with an artist lens and a year bin set from the facts panel
   (lens rows with ×, count and badge agree), a local-files playlist (Source row); Tempo wraps to two lines without
   clipping at 392; keyboard: Tab enters a row, ←/→ move the underline, Space picks, Tab leaves; the scope chip in the
   search box changes the placeholder and never lights the funnel; Clear resets every row and the chip.
3. Whole suite once at the end.
