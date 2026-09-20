# Podcast reader and player refinements

Scope: the approved transcript, row alignment, toolbar, reactions, saved-state, chapter timeline and edge-fade refinements, plus the subsequently reported titlebar width and search-focus bugs. This builds on `podcast-ui-repair-implementation.md`; it does not claim the older reports have all been visually resolved.

## Component layout

```text
Show reader
  fixed RailBody (one row, no horizontal scroll)
    All | Unplayed | In progress | Played | Search | Sort | Date | Select
    under pressure: Search icon -> selection/date/sort in More
    narrowest: selected status menu | Search icon | More
  episode ItemsView (AutoEdgeFade)
    [3px accent][8px clearance][art][title / description / metadata][actions]

Episode discussion
  PersonPicture | author / date
                | comment
                | reaction count / Reactions / replies
  anchored reaction flyout (320 wide, window-constrained)
    eight reaction choices in two rows
    All / emoji filters with totals
    bounded reactor list (PersonPicture, pagination, AutoEdgeFade)

Player
  episode Save / transport / -10 / +10 / speed
  seek bar + non-hit-testing chapter boundary ticks
    hover: chapter title, range and pointer time
  transcript rail (16/24/500; expanded 20/30/500)
    existing lyrics timing, active/near/distant effects and edge fades
```

## Implementation seams

`ShowToolbarLayout.Resolve(available, filters, sortWidth, hasDate, previousInline)` is the pure pressure decision. `RailBody` measures localized labels and counts, then renders a stable keyed row. `ReaderSearch` holds an anchored search popup over the same query signal as the inline field. `ReaderMenuButton` uses the existing overlay/menu infrastructure. `Episode.RowGrid` reserves `ToneBarWidth + 8f` before artwork in every row state.

```csharp
var layout = ShowToolbarLayout.Resolve(usable, filtersWidth, sortWidth, !book, _inline);
_inline = layout.InlineSearch;
// Same query authority in either presentation:
Embed.Comp(new ReaderSearchProps(m.Find, findLabel, layout.InlineSearch),
    static () => new ReaderSearch());
```

`Lyrics.Surface` supplies subject-dependent timed and untimed metrics. Text, extent measurement and shimmer derive from the same profile. Playing podcast transcripts use the existing lyrics rendering pipeline; `TranscriptPresentation.Neutral(podcast, ownsPlayback)` keeps independently browsed documents readable. Provider timing and the source playback clock remain authoritative.

Episode menus and the bottom player resolve `ActionIcons.Save` against `Spotify.Podcasts.IsSaved`, and mutate through `Spotify.Podcasts.ToggleSaved`. Music continues to use its track library authority.

Chapter data is leased by account, entity scope and episode through a shared UI resource. The chapter tab and player reuse it. A pure timeline projection normalizes valid intervals and calculates hit lookup independently of visual tick coalescing. Hover is descriptive; seek mechanics remain on the existing player slider.

The reaction flyout uses the existing overlay focus/light-dismiss contract and `PersonPicture`; mutation failures retain confirmed state and display errors. Reactions are filtered and paged without expanding the parent comment document.

The titlebar follow-up addresses measured tab allocation and keeps the inline AutoSuggestBox editor at a stable reconciler position when suggestions open or close, preventing typing from remounting the focused editor.

## Verification

Behavioral tests cover toolbar pressure and localization widths, transcript profiles and ownership/effects, chapter interval boundaries and resource lifecycle, reaction decoding and palette decisions, and titlebar/search regressions. Root runs Debug and Release public-source app builds, app tests and Pester; engine gates run for the AutoSuggestBox change. Live visual verification is recorded separately from automated results. No extra application windows are launched over the user's current session.
