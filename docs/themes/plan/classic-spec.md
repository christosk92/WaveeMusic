# Wavee Classic: production layout spec per page, reconstructed from code (main @ 21abd98e)

I did read-only research for this: no edits, builds, runs or commits. Six parallel read-only mappers checked the inventory maps against the code. I spot-checked the type ramp, the Surface presets, the Vacancy grammar, row heights and art sizes, the Autoplay key, the NotFound page and the sidebar catalogue myself.

Conventions:
- Paths are relative to `C:\wavee\fgpu\WaveeMusic\src\apps\Wavee\`.
- Sizes are in DIP. Type is written as size/line-height/weight.
- Copy is quoted from `assets\loc\en-US.json`. A code key `Strings.A.B` is the JSON key `a.b`.
- "Display face" means "Segoe UI Variable Display" (Design.cs:1194).

---

## 0. Shared vocabulary

### Tokens
- **Spacing:** XXS 2 · XS 4 · S 8 · M 12 · L 16 · XL 20 · XXL 24 · XXXL 32 · PageWide 36.
- **Radii:** Control 4 · Card/Overlay 8 · Pill 16 · Full 999.
- **Design.Size** (Design.cs:62-128):
  - PageMaxW 1600, SectionGap 32, SectionGapWide 40, GridGap 12.
  - Dock.Reserve 72 (equals PlayerBarH 72).
  - Thumb 32/40/48/56/64.
  - ShelfCard 148–188. WideTile 330–440, 16:9.

### Type ramp
Engine ramp (fluent-gpu `Dsl/Typography.cs:39-46`):

| Style | Size/line/weight |
|---|---|
| Caption | 12/16 (secondary ink) |
| Body | 14/20 |
| BodyStrong | 14/20/600 |
| BodyLarge | 18/24 |
| Subtitle | 20/28/600 |
| Title | 28/36/600 |
| TitleLarge | 40/52/600 |
| Display | 68/92/600 |

App aliases (Design.cs:1190-1470):

| Alias | Definition |
|---|---|
| TrackTitle | 14/20/600 |
| TrackMeta | Caption, secondary |
| Eyebrow | 12/16/600, +30 tracking, never uppercased |
| MicroMeta | 11/15 |
| DenseMeta | 13/18 |
| DenseTitle | 13/18/600 |
| RailHeader | 20/28/600 |
| ModuleHeader | 20/28/600, Display face, −6 tracking. Optional same-baseline meta run at 12 px tertiary |
| PageHero | 28/36/600 |
| DetailHero | 28/36/600, Display face, −20; the frame resizes it |
| SurfaceDisplay | 40/52/400, Display face (mastheads) |
| HeroTitle | 40/52/600, Display face |
| ArtistDisplay | 84/96/700 |
| ArtistTitle | 48/60/700 |
| ArtistCompactTitle | 32/40/700 |
| PickQuote | 28/36/400, Display face |
| PivotLabel | 19/25/350, Display face |
| StatHero | 28/36/350 |
| FacetTitleStyle | SelectorBar at 28/36, Display face; 400 at rest, 600 selected; no pill. Compact variant 20/28 |

### Surface presets (Platform\Surface.Rules.cs:77-126)

| Preset | Geometry |
|---|---|
| Grid | Stacked: cover r8 (circle for artists), 1 title line, 1 caption line. Hover reveals a 44 accent FAB centred on the cover and a 30 "…" scrim circle top-right, inset 8 |
| Shelf | Grid at a fixed width. Height w+50 with 1 caption line, w+66/68 with 2 |
| Video | 16:9, FAB always shown |
| Row(48) | Art r4, padding 8, gap 12. FAB 30 (44 above 56 art). Floor 64. Trailing 32 "…" |
| RowLarge | 84 art, 112 floor |
| RowTile | Row on FillCardDefault with a 1px hairline; the hairline turns accent at 50% while the item plays |
| RowOutline | Row inside a 3/3 dashed border |
| EpisodeRow | 56 art, 2-line title, 72 floor |
| SidebarTile | 28 FAB |

- Plate states: hover FillSubtleSecondary, pressed FillSubtleTertiary.
- Playing card: a scrim pill holding a 14px equalizer.

### Vacancy (Controls.cs:1212-1268)
- Centred, padding 24, gap 4.
- Headline: PageHero, or Subtitle at Compact scale. Caption: TrackMeta.
- An optional Standard button sits 16 below. It is never accent.
- Voices:
  - Error: "Something went wrong." / "Check your connection and try again." + "Retry".
  - Empty and NoMatch: "Nothing here yet" / "When there's something to show, it'll appear here."
  - Offline: never used by any page.

### Buttons and small controls (Controls.Cta.cs)
- **PlayButton:** Accent, 32 tall, r4, 14/600, min width 120, label "Play" ("Pause" while its context plays), tinted by the page accent.
- **IconAction:** 32×32, r4, subtle.
- **TextAction:** no plate, 14/600, secondary ink; the primary one is accent.
- **FollowToggle:** "Follow" / "Following".
- **AccentHeader:** RailHeader with a 20×2 accent rule below.
- **ExplicitBadge:** 14×14, r2, TextTertiary, "E" at 9.5/700.

### Skeletons
Derived from the real tree, so the skeleton has the same geometry as the loaded page. Reveal is FadeOnly.

---

## 1. Shell frame

**Window:** Mica ground. Title row: tabs, back/forward/history, omnibar, bell, profile chip. Body: [sidebar | content | right rail]. Player bar along the bottom, 72 tall.

**Sidebar** (`sidebar.layout.id`, Sidebar.Layout.cs:14-60):
- Classic layout sections:
  1. Home
  2. Pinned
  3. Collections: "Liked Songs", "Albums", "Artists", "Podcasts", "Audiobooks"
  4. Playlists
  5. "Recently played"
  6. "New releases"
  7. Settings
- Library layout sections: Home · Pinned · "Your Library" (chips, sort, List/Grid) · Settings.
- Section headers are 40 tall, 14/600 secondary. Playlists has a "+".
- Width 180–460.
- Bands: ≥700 expanded; 528–660 compact rail; below 528 a drawer.
- Density: Default (two-line rows) or Compact (one-line).
- Empty states: "Your sidebar is empty"; "No playlists yet — use + to create one".

**Browse-family masthead** (Shell.Masthead.UI.cs:37-125):
- Inset 36, 32 from the top, transparent.
- SurfaceDisplay title, with crumbs in tertiary separated by "›".
- Reserves 84; content clips under it with a 24 feather.

**NotFound** (Shell.UI.cs:1002): the route glyph at 40 tertiary, then PageHero "Page not found", then a Standard "Go home" button. Gap 12.

**Page motion:** default "Classic" (90 + 210 ms).

---

## 2. HOME (Home\*: HomeScreen.cs, Facet.UI.cs, Zones.UI.cs, Daylist.UI.cs, ZonePlanner.cs, PodcastPlanner.cs)

### Frame
- Shell tint behind one ScrollView. The tint comes from the first zone's art (the daylist on All).
- Column: max 1600, padding 36/24/36/104, gap 20.
- No greeting headline (HomeScreen.cs:26). The greeting appears only in the daylist eyebrow.

### Facet row (Facet.UI.cs:66-196)
- 43 tall: a 40 row plus a 3px loading bar.
- Sticky 12 from the top, no fill. Content clips at 55 with a 24 feather.
- Words: a SelectorBar in FacetTitleStyle (28/36 Display face). Unselected 400 secondary; selected 600 primary.
  - Items are 40 tall with padding 8/2, 24 apart; the first starts at −8.
  - Below 600 width they shrink to 20/28.
  - Labels: "All", then the server chips ("Music", "Podcasts", "Audiobooks").
- Right side: a ToggleButton with the server sub-label (or "Following") and a Friends glyph.
- Load failure: an Error InfoBar under the band, "Couldn't load {what}" / "Check your connection and try again." / "Retry". It slides in 8 over 167 ms.
- Switching facets dims the old content to 0.6 (167 ms).

### Zone order
**All** (zones are 40 apart):
1. Daylist
2. Recently played (grid)
3. Made for you (shelf; Discover Weekly as lead)
4. Wide editorial 16:9 shelf
5. From artists you follow (release list)
6. Because you like (clusters)
7. Jump back in
8. Radio and mixes
9. Browse (tiles + Charts)
10. "Customize home" link

**Music:** one shelf per server section, then Because you like, Radio, Browse.

**Podcasts:** New episodes · Continue listening · Your shows · Videos you might like · Episodes you might like · Because you listen to · Shows you might like.

### Zone header
- 44 tall, sticky under the facet band at 55.
- Left: a ModuleHeader title with a 12px tertiary subtitle on the same baseline. Not clickable.
- Right: a "See all" link. Only when there is more than one page: a divider, ‹, a PipsPager, ›.
- Titles and default subtitles:

| Title | Default subtitle |
|---|---|
| "Recently played" | "Pick up where you left off" (tool link: "Listening history") |
| "Made for you" | "Daily Mixes and moods picked for you" |
| "From artists you follow" | "Fresh playlists and releases from artists you follow" |
| "Because you like" | "Grouped from the artists you play most" |
| "Jump back in" | "Things you've opened lately" |
| "Radio and mixes" | "Non-stop music based on the artists and songs you play" |
| "Browse" | "Official Spotify playlists and this week's editorial picks" |

### Daylist card
- Ui.Card, r8, 1px stroke, no padding, min height 320, full width, clickable.
- Header art is cover-fit, focused at y 0.4.
- Veil from the left: α .96 @0, .92 @.30, .35 @.62, 0 @1.
- Copy column: padding 32/32/12/24. Width clamp(.5 × inner, 340, 560); full width when inner is under 532. Content is spread top-to-bottom (space-between).
- Copy, top to bottom:
  - Eyebrow (Body, secondary): "Good afternoon, Chris · your daylist". Greeting: morning 4–12, afternoon 12–17, evening otherwise.
  - Title: HeroTitle 40/52 when the copy is ≥480 wide, otherwise 28/36. Max 2 lines.
  - Tags line: up to 6 secondary link tags, separated by tertiary " · ".
  - Meta: "Playlist · Spotify · {n} songs".
  - Actions (20 below, gap 8): Accent "Play" (min 120; "Pause" while playing) · Standard "Shuffle" · heart · "…".
- Clock:
  - A 16 ProgressRing with "Next daylist in **hh:mm:ss**", and the caption "sunday evening arrives at 17:00".
  - Five ProgressBars, gap 4, labelled "Early morning · Morning · Afternoon · Evening · Night" (the current one at 600).
  - Updating state: "Updating your daylist…". Late state: "Your next daylist is running late" + "Check again".
- The DaylistClock setting picks the clock: Both / UpdateTime ("Next update at HH:mm") / Countdown.

### Recently played grid
- Min column 200, max 4 columns. Gaps 12 (columns) / 8 (rows). Row height 64.
- 2·cols−1 RowTile items, then the history tile.
- Item tile: 48 art, title 14/600, subtitle "Playlist · 42 songs" plus a tertiary " · 12 min ago". While playing: an equalizer and " · Playing now" or " · playing on {device}".
- History tile (RowOutline):
  - 48 headphones plate on AccentSubtle.
  - "Listening history".
  - A 56×14 seven-bar sparkline and "{n} plays this week".
  - Trailing chevron.

### Other zone bodies
- **Shelves:** Shelf cards 148–188 wide, gap 0, fade 24.
  - A lead card spans 2 cells when there are ≥3 columns and it has a header image. Caption: "{subtitle} · {n} songs".
  - Wide shelves: 330–440 at 16:9, with a meta line.
- **ReleaseList:** min column 320, max 2 columns, max 6 rows of Row(48). Trailing: a type chip ("Album", "Single"; MicroMeta 600 on a subtle pill) and a date ("3 d ago").
- **Clusters:** min column 300, max 3 columns. Each is a Ui.Card (padding 16): BodyStrong name, "More like …", and up to 3 Row(40).
- **Browse tiles:** min column 220, max 4 columns, max 12. SettingsCard (68 tall, 48 art).
  - Followed by "Charts" / "Updated daily": "Top 50" ("The 50 most-played tracks worldwide") and "Viral 50" ("The tracks trending right now").
- **Episodes:** min column 320, 2 columns (Continue listening: 3). EpisodeRow with "Today · 42 min" / "… min left" and a 120 progress bar.

### States
- **Skeletons:** All = daylist + an 8-tile grid + 2 shelves of 6. Podcasts = lead + continue + shows.
- **Error:** Error vacancy.
- **Empty facet**, each with a Standard button:
  - "Nothing here yet" / "Your recommendations will show up here soon." / "Browse"
  - "No audiobooks yet" / "Audiobooks you save will show up here." / "Browse audiobooks"
  - "No podcasts yet" / "Shows and episodes you follow will show up here." / "Browse podcasts"
- **Customize home:** PageHero "Customize home", "Show, hide and reorder Home's sections.", "Reset to default". Rows are 48 tall with a grip, ⌃⌄ and a toggle; hidden rows sit at .55 opacity.
- **See-all page:** masthead, "{count} items", Grid cards with min cell 176.

### Settings
Only DaylistClock changes Home. Home does not read HideTrackArtwork or ColorWashes; its tint is always on.

---

## 3. BROWSE (Browse.Page.cs, Browse.UI.cs, Browse.Rules.cs, Browse.Modules.cs)

### Directory
- Masthead "Browse". Body padding 36, bottom 96.
- Bands are 16 apart; each has an Eyebrow label 8 above it.

1. **"Top":** Word pills, 36 tall, full radius, FillControlDefault + 1px border, BodyStrong text. Hover: accent border and a slight scale.
   - Labels: "Music", "Podcasts", "Audiobooks", "Live Events".
2. **"Charts":** Fold deck. Header: ModuleHeader + chevron.
   - Tiles are 176 tall, at least 440 wide, at most 2 per row. r8, stroke, shadow.
   - A radial accent wash at 22%.
   - Three 124 covers fanned at −11°, +5° and −2°.
   - Title 28/36 Display face at 400, up to 3 lines, max 70% of the tile width.
3. **"For you":** Name pills, 32 tall: an 8×8 colour pip + Body text.
4. **"Genres":** Link grid. 3 columns above 720, 2 above 380, else 1. Each link is a pip + secondary text (accent on hover). Sorted A–Z.
5. **"Mood & activity":** Bar tiles, 52 tall. Columns = floor(w/132), gap 4. r8, stroke, shadow.
   - A corner colour wash at .20 (.34 on hover) and a 3px colour stripe on the left.
   - CardTitle text, up to 2 lines, bottom-left.
6. **"More":** Peek tiles, 88 tall. Columns = clamp(w/168, 1, 4). ModuleHeader title.
   - An 80 cover rotated −8°, overhanging the tile by 24.

**States:**
- Loading: seed bands.
- Error vacancy.
- "This category isn't available right now".
- Charts empty: "No charts right now".

### Category page
- Crumb "Browse › X". Padding 36, 100 at the top.
- Shelves are 16 apart: a ModuleHeader + chevron, then 148–188 cards.
- The page ends with "Explore all categories" (12/16 accent).
- A single-section category shows a Grid of 148–188 cards, gap 12.

---

## 4. SEARCH

### No query
An empty query routes to Browse (Shell.cs:385). There is no recent-searches page.

### Omnibar
- Placeholder "Search songs, artists, albums...", width 280–420.
- Popup: min width 400, max height 560.
  - Up to 6 query rows, 40 tall. The typed part is bold, the rest secondary.
  - Up to 10 rich rows: Row(44), subtitle "Song · Artist", with Save/Follow.
  - Empty: "No results found". Failed: "Couldn't load suggestions" + "Retry".

### Results page
- **Header** (padding 16/12/16/8): the query echoed lowercase in SurfaceDisplay 40/52/400.
- **Tabs:** "All", "Songs", "Albums", "Playlists", "Audiobooks", "Podcasts & shows", "Artists", "Episodes", "Profiles", "Genres", "Authors".
  - Body 14 with a tertiary count.
  - The selected tab has a 3px accent underline that grows in over 260 ms.
- **Body** padding 16/8/16/96. The body slides when the tab changes.

**All tab:**
1. **Top result card:** 228 tall, r8, FillCardDefault, stroke. Radial wash of the art accent at .55, centred at (.88, .40).
   - Accent eyebrow "Top result · Artist".
   - PageHero title, 2 lines.
   - Subtitle.
   - Chips "Matched title" / "Lyrics match".
   - Actions: art-tinted Play, Follow/Save, "…".
   - Cover 300×260 (an ellipse for an artist), offset +40/−16, rotated −2.5°.
2. **"Best matches":** TickHeader (a 3×14 accent stripe + RailHeader). A paged shelf of 64 rows; columns = clamp(floor((w+12)/292), 1, 3).
3. **"Playlists"** shelf.
4. **"Genres"** link grid.
5. **"Related searches":** lowercase 20/28 Display face at 400, secondary, accent on hover.

**Other tabs:**
- Rows: Row(48) with a 32 "…" lane. Subtitle such as "Song · Artist" or "Music video · …"; "Lyrics match" in accent. Trailing: a Follow toggle or a 16 heart.
- Songs: auto grid, min column 280.
- Albums and Playlists: Grid cards, columns floor((w+16)/196), gaps 16/20.
- Other tabs: a single column, gap 8.

**States:**
- Shimmer: a hero bone + 12 cards, or 10 row bones.
- No match:
  - "No results found" / "Nothing matched \"{query}\". Try a different spelling or keyword."
  - Per tab: "No audiobooks found", "No podcasts or shows found", "No episodes found", "No profiles found", "No authors found".
- Error + Retry.
- HideTrackArtwork removes the song-row art (Search.Page.cs:136).

---

## 5. DETAIL FRAME + TRACK TABLE (Album / Playlist / Liked / Show / Episode)

### Modes (Detail.cs:73-137)

| Mode | Width | Layout |
|---|---|---|
| 0 | ≥820 | two-column |
| 1 | ≥660 | two-column |
| 2 | ≥560 | two-column |
| 3 | <540 (exits at 580) | vertical hero |

- The setting "Track page layout = Hero" (`detail.page.layout`=1) forces mode 3 (Detail.UI.cs:703).
- Table tiers by right-column width: ≥860 0, ≥720 1, ≥560 2, ≥440 3, ≥340 4, ≥300 5, else 6.

### Two-column layout [rail | 16 grip | right]
- Max 1600, notice strip on top.
- **Rail width:** Album and Show 280; Playlist, Liked and Uniform 240. Modes 1 and 2 rest at 224 and 188. Drag range 180–480.
- **Collapsed rail:** a 96 strip with an 80 cover, the title at 12/16/600 on 2 lines, and an 80×28 › button.
- **Rail fill:** FillLayerDefault. Liked is transparent.

**Rail column** (padding 16/24/8/24, gap 14), top to bottom:
1. Cover: rail−24 (256 at 280; 216 at 240), r8, shadow, saturation 1.18.
2. Eyebrow (album and show), tertiary: "Album · 2013" / "EP" / "Single" / "Compilation".
3. Owner row (playlist, above the title): a 24 avatar and the name at 14/600.
4. Title: DetailHero at 600. Capped at 40 when the window is ≥900 tall, else 28. Steps down to fit 3 lines, floor 20. Line height = round(size × 1.33).
5. Face pile (album).
6. Meta (not on albums): TrackMeta, 2 lines. Playlist: "50 songs · 18.7M saves · 2 hr 59 min". Liked: "1,204 songs · 71 hr 3 min".
7. Daylist strip (28) or chart caption.
8. CTA row: [Play 120×32 accent], then [♥][Share][⋯] as 32 r4 icons.
   - Album: ♥ + Share. Playlist: ♥ + Share + ⋯. Liked: Share only.
   - No Shuffle in the rail.
9. Description: 12/18 secondary, 6 lines (3 when the window is under 760 tall), "… More".
10. Insights cards.

**Notices** (Informational InfoBar):
- "This playlist was deleted."
- "You no longer have access to this playlist."
- "This playlist couldn’t be created."
- "Minified album view — track details load on the full album page."

**Rail skeleton:** the same rows as bars.

### Vertical / hero arm (Detail.UI.Hero.cs)
- Side-by-side when ≥424 wide: cover = clamp((w−72) × .44, 144, 240).
- Stacked below that: cover = clamp(w − 2·pad, 96, 280). Padding 24 (16 when narrow).
- Text column:
  - Eyebrow: "Album · 2013" / "Playlist" / "Playlist · private" / "Playlist · collaborative" / "Your Library" / "Local files".
  - DetailHero title. The size cap scales from 28 at 360 to 96 at 1100.
  - A 20×2 accent rule.
  - Attribution: album artists at 600 accent; playlist owner at 600 secondary.
  - Meta.
  - Actions: [Play] [Shuffle] [♥] [Share] [⋯] as 32 icons.
- Description: 13px, 2–4 lines.
- **Stuck band:** 56 tall, no fill, 1px hairline. BodyStrong title over a Caption tertiary byline. Text actions: "Find" · "Filter" · ["Insights"] · "Play" (accent).
- Rows are plain and full-bleed, no zebra.

### Command bar (Track.Table.Chrome.cs:144-912)
- 44 tall. Labelled buttons are 32 tall: a 14 icon + Caption 12/600 secondary.
- Contents, left to right:
  - "Play next" split button (menu: "Add to queue")
  - "Shuffle"
  - Tune (sparkle; "Tune this playlist")
  - Divider
  - Sort: "Custom order / Title / Artist / Album / Date added / Duration / Plays". An accent plate at α .11 when not custom. Plus "Ascending" / "Descending".
  - Row size: "Compact / Default / Cozy / Comfortable". Opens a 240 popup with a 4-stop slider.
  - "Select"
  - ⋯
  - Spacer, then search.
- Overflow order: Shuffle, Sort, Row size, Select.
- ⋯ menu: the overflowed items, "BPM · Key column", "Plays column", "Select", "Copy to playlist ▸" / "Add to playlist ▸".
- **Search, at rest:** a magnifier ("Search this list") and a funnel ("Filter tracks"). The funnel gets an accent plate and a badge when filters are active.
- **Search, open:** the field is 160–280 wide, r4, with an accent underline while focused.
- **Filter flyout** (368 wide):
  - "Filter tracks".
  - "Search in": Everything / Title / Artist / Album.
  - "Explicit content" and "Video tracks": All / Hide / Only.
  - "Liked only", "Available to play".
  - Sections: Duration, Date added, Tempo, Source.
  - "Clear all filters".

### Columns and lanes
- **Header row:** Modern 36 tall (Caption 600); Classic 32 tall (MicroMeta 600). Labels tertiary; the sorted column secondary.
- **Labels:** "#" (empty in Classic) · "Title" ("Song" in the vertical arm) · "Artist" (Classic only) · "Album" · "Added by" · "Date added" · "Plays" · "BPM · Key" · a clock glyph ("Time").
- **Lane widths:** # 28 · ♥ 28 · art · Title 1fr (min 120) · Artist .75fr · Album .75fr · Added by 132 · Date 88 · Plays 52 · BPM 80 · Duration 52 · video/⋯ 40 · chevron 26.
- **Lanes by tier:**

| Lane | Shown when |
|---|---|
| Album | tier <2 |
| Added by | tier <1 |
| Date added | tier <3 |
| Plays | tier <3; always on albums; opt-in on playlists |
| BPM | tier ≤3 and opted in |
| ♥ | tier <5 |
| Art | Modern, playlist/Liked, tier <5 |
| Artist (Classic) | tier <4 |

- Column gap 12. Side padding 16/12/8 by tier.
- Default sort: Liked by Date added, newest first. Everything else: custom order.

### Row heights (Track.Rules.cs:166, 215)

| Density | Compact | Default | Cozy | Comfortable |
|---|---|---|---|---|
| Modern row | 40 | 48 | 56 | 64 |
| Classic row | 36 | 40 | 44 | 48 |
| Modern art | 32 | 32 | 40 | 48 |

Classic never shows the art lane (Track.Rules.cs:160).

### Cells
- **#:**
  - Modern: tertiary number. Playing row: a 13 accent equalizer. Top track: an accent ★. Chart rank change: ▲/▼/"New". Buffering: a 16 ring.
  - Classic: empty. Playing row: an accent Volume glyph.
  - Hover: a 12 ▶ (⏸ when playing).
- **♥:** Modern accent (filled) / tertiary outline. Classic primary / secondary.
- **Title (Modern):** 14/600 (accent and marquee when playing). Subline 12/16: "[E] Artist, Artist [· Album]". The E badge appears only on playlist and Liked rows.
- **Title (Classic):** one 14/20 line, "Title  ·  Artists". Inline 12 film glyph for videos. A trailing "Explicit" word-mark at 9/12/600, bordered, .6 opacity.
- **Text cells:** Modern 12/16, Classic 14/20. Values like "Today" / "N days ago" / "Sep 28, 2024", "1.2K", "3:47", and a Camelot swatch with "128.0 · 8B".
- **Video lane:** a film glyph, replaced by ⋯ on hover; without a video, ⋯ at .45. Chevron: › (accent ⌄ when open).
- **Unavailable:** .45 opacity; the duration reads "Audio unavailable" or the release date.
- **Playing row:** Modern shows an accent title and the equalizer. Classic turns the whole row accent and shows the Volume glyph.

### Row skin (Track.Table.cs:2061-2151)

| | Modern | Classic |
|---|---|---|
| Plate | inset 8, r6 | full-bleed, square |
| Rest | zebra: odd rows RowZebra with a 1px card border | 1px bottom divider |
| Hover | RowHover (dark: white 0x0F; light: 5.1% black) | RowHover |
| Selected | 3×16 accent pill on the left | RowHover fill |
| Header | 36 | 32 |

### Table states
- Loading: 12 shimmer rows.
- Empty: "Nothing here yet".
- No filter match: "No songs match your filter".
- Failed: "Couldn't load these songs" + "Retry".

### Drawer and context menu
- **Row drawer:**
  - Facts: "Plays", "BPM", "Key", "Added", "ISRC", …
  - "Versions and formats": 51-tall rows ("This track" with a waveform; "Music video" 76×43; "Alternate audio").
  - "Use my default quality".
  - "View credits".
- **Context menu**, in order:
  - Header.
  - Icon strip: Play / "Play next" / "Play after" / "Save".
  - "Add to playlist ▸", "Move to playlist ▸", "Go to album", "Go to artist".
  - "Share ▸" (Copy link / Copy Spotify URI / Open in Spotify Web).
  - "View credits", "Go to song radio", "Video ▸".
  - "Move up/down", "Track details".
  - "Remove from this playlist".

---

## 6. ALBUM / PLAYLIST / LIKED

### Album (Album.Page.cs, Album.UI.cs)
- **Rail** (280): cover → "Album · 2019" → title → face pile → [Play][♥][Share] → countdown → "About this release". No meta line, no ⋯, no description.
- **Table:** Plays + ★ + chevron. No art column, no album column.
- **Face pile:** up to four 28 faces overlapping by −12 with "+N", then ⌄, then "A, B" at 14/700 accent. Tooltip "View all artists"; opens a 280×360 flyout.
- **Countdown card:** padding 12, r8. A 34 accent ring, "Coming soon", and days/hr/min/sec. After release: "Out now". The save button reads "Pre-save" / "Pre-saved".
- **About this release:** a 76-wide label column ("Released", "Label"), the ℗/© lines, and an "Other versions" dropdown ("Name · 2019 · Album").
- **Trailing sections:**
  - Music video: a 200×116 card, "Watch the official video" (or a "Music videos" shelf).
  - About the artist card.
  - "Fans also like": up to 8 round 148 cards.
  - "More by {artist}", "Featured on", "Merch", "Similar albums": 5-row stacks of 64 Row tiles + "Show all {count}".
- **Hero ⋯ menu:** Play next / Add to queue / Add to playlist ▸ / Pin / Share.

### Playlist (Playlist.Page.cs, Playlist.UI.cs)
- **Rail** (240): cover → owner → title → meta → daylist/chart → [Play][♥][Share][⋯] → description → insights.
- **Table:** art, album column, E badges.
- **Owned playlist:**
  - Cover editor: a scrim (52%; 68% while dropping), a camera icon, "Change cover" / "Drop image to change cover", and a "Saving…" / "Saved" chip.
  - Inline editors: hover shows a border and a pencil. Placeholders "Playlist name" / "Add an optional description". Save/Cancel pills 32 tall, r16.
  - "Invite collaborators" pill opens a 300 panel: "Collaborative playlist", "Public playlist", "Copy invite link".
  - Collaborators: a face pile + "N collaborators".
  - "Recommended songs" (two-column layout only): 40 art and a 28 round "+" ("Add to playlist"). Empty: "No suggestions right now". Plus a refresh button.
  - Tune dialog: "Tune this mix".
- **Followed playlist:** read-only; the heart means Follow.
- **Local files:** "Local files" / "On this device" / "Music imported from this computer."

### Liked Songs (User.Page.Liked.cs, User.Cover.cs, User.Facts.UI.cs)
- **Header:** "Liked Songs". Hero eyebrow "Your Library". Meta "N songs · X hr Y min". Transparent rail; Share only.
- **Cover:** a 304 canvas with a "Cover style" brush pill. Flyout note: "Built from your latest likes — the cover changes as you like songs. Stock keeps the fixed artwork."
- **The 9 cover treatments** (default **Lens**):
  - Stock
  - Lens: a blurred 3×3 grid with a heart-shaped window
  - Wall: a tilted wall that drifts
  - Rainbow: 16 covers ordered by hue
  - Marquee: 3 diagonal strips
  - Feature: newest cover at 2×2 plus 6 smaller
  - Mosaic: 9 covers, a .55 scrim and a "♥ Liked Songs" chip
  - Tone: washes, a frosted heart and the count
  - Stack: the last 5 fanned out
- **Chip bar** (48, only when chips exist): "All" + genre tags. Tags with no songs are disabled and placed at the end.
- **Lens header** (36): "Liked Sep 1 – Sep 7" + "Clear this filter".
- **Insights cards:** padding 12/10/12/11, r8, FillCardDefault, border, shadow. In the vertical arm they move into an "Insights" sheet (390 wide, or 76%).
  - Card order:
    1. "This week" or "The years"
    2. "Tempo"
    3. "Most liked"
    4. "Your blend"
    5. "Rediscover" ("Play them")
    6. Pills
    7. "Liking since …"
  - Numbers at 28/36.

---

## 7. ARTIST / PROFILE / CONCERTS

### Artist (Artist.Page.cs, Artist.UI.cs, Artist.UI.Chart.cs, Artist.Discography.cs)

**Layers, back to front:**
- Shell tint: 5% light / 14% dark.
- Blend wash: hero height + 96, α .20/.30 fading to .06/.08, then 0.
- Hero: sticky, collapses to 56.
- A 1px divider.
- Magazine column: max 1600, padding (gutter, 12, gutter, 112), gap 32.

**Hero tiers** (±24 hysteresis):

| Tier | Width | Height | Layout | Name |
|---|---|---|---|---|
| Wide | ≥880 | 440 | full-bleed photo; copy max 1120 | 84/96/700 |
| Medium | ≥600 | 384 | full-bleed photo; copy max 760 | 48/60 |
| Compact | ≥360 | 448 | photo 200 above the copy | 32/40 |
| Narrow | <360 | 468 | photo 176; actions on 2 rows | 32/40 |

- **Photo:** focus (.62, .34), parallax .15, bottom fade clamp(h × .28, 120, 180). Entrance: fade with scale 1.00 → 1.03 (deliberate).
- **Veil** (Wide/Medium only): α .96 @0, .92 @.30, .35 @.62, then 0. Colour: FillLayerDefault lerped toward the accent by .16 (light) / .24 (dark).
- **Identity:**
  - Verified: InfoBadge + "Verified artist".
  - Name, up to 2 lines.
  - Bio lead, 2 lines.
  - Meta, gap 16, no dots: "#3 in the world" (BodyStrong accent) · "20,577,457 Monthly listeners" · "8,123,456 Followers".
- **Actions:** [Play accent] [Shuffle Standard] [Follow] [32 Radio icon, "Artist radio"]. No ⋯.

**Context band** (56, hairline):
- Title in BodyStrong, max 280.
- Pivot at 14/600 with a 2px accent underline: "Albums" · "Singles & EPs" · "Compilations" · "Details".
- Text actions "Play" (accent) and "Follow".

**Sections, in order:**
1. Popular band
2. Upcoming
3. Latest release
4. Albums
5. Singles & EPs
6. Compilations
7. Appears on
8. Tour banner
9. Music videos
10. Playlists and discovery
11. Upcoming concerts
12. Merch
13. Biography (always present)
14. Gallery
15. Fans also like

Section headers are AccentHeader. There are no "Show all" and no Discography links.

**Top band:** at ≥760, the chart (grow 2) sits beside the pick or upcoming card (grow 1).

**"Top tracks" chart:**
- Pager "‹ ● ● ›" with 28 circles.
- 5 rows per column; 2 columns when the column is ≥264 wide; up to 50 tracks.
- Modern rows: 56, r6, hover fill + border, 44 art (40 when narrow), equalizer on the playing row.
- Classic rows: 48, square, 1px divider under each, 40 art, Volume glyph on the playing row.
- Cells: # · art · title 14/600 (accent when playing) over "E · film · Feat. X +N · 1.2M plays" · heart · duration 13/18.

**Pick card:**
- FillCardDefault, r8, border, shadow; wash .16 fading to 0.
- 32 avatar + "Artist pick".
- PickQuote 28/36/400, up to 4 lines.
- A 150 photo band.
- Footer: 44 cover + "Releases {date}" + Play or "Pre-save".

**Banners:**
- **Upcoming:** 88 cover, "Upcoming · Album", the big date in SurfaceDisplay, "Pre-save" + an Outline "View" button, and a countdown when ≤14 days away.
- **Latest release:** a tile with a 72 cover, "Single · Jun 12, 2026 · 3 tracks".
- **Tour:** FillCardSecondary with a 44 accent circle.
  - Eyebrow: "Upcoming show" / "On tour now" / "Upcoming tour".
  - Title at 16/700: "{name} — on tour".
  - "Next: Mar 14 · Venue · City · 12 dates total".

**Shelves:** cards 150–200 wide, gap 12, fade 36, height w+66.
- "Appears on"
- "Music videos" (16:9, caption is the duration)
- "Playlists and discovery"
- "Upcoming concerts": stubs with a 48 date column ("MAR" in accent above the day at 28/36/600)
- "Merch": price in accent
- "Gallery": square, r8
- "Fans also like": round cards, subtitle "Artist"

**Biography** (side by side at ≥820):
- Card: FillCardSecondary, padding 20/16.
  - "Biography", up to 14 lines.
  - Social links.
  - "Listened to most in": 34-tall city rows with a 4px accent bar.
- "Profile facts" tiles (basis 140, value at 28/36): "Monthly listeners", "Followers", "Albums", "Singles & EPs", "Upcoming concerts", "Related artists".

**Discography facets:**
- Sticky header at 56, 40 tall: a 3px accent spine + "Albums  24 releases" (follows the era in view) + a chevron.
- Grid: min column 180, gaps 16/20. Subtitle "Jun 12, 2026 · 17 tracks ⌄".
- Drawer:
  - A 16×8 caret; FillCardSecondary, r8.
  - Head: a 32 play FAB, a 28 cover, "Title · meta", ↗.
  - Rows on a 32 pitch: # 26 | ♥ 28 | title | 44 | ⋯ 32. Up to 12 rows per column.
  - "Show all {n} tracks".
- **Discography page:** padding 36/24/36/108. Breadcrumb "[Artist] › Albums", PageHero, SelectorBar "Albums | Singles & EPs | Compilations". Uses the system accent; no wash.

**Lightbox:** black at α 224. A 72 chrome bar: "Gallery", "3 / 12", Accent "Export image", "Close". A 56 filmstrip.

**States:** a derived shimmer (hero + 2×5 chart + 5 cards); Error vacancy.

### Profile (Profile.Page.cs, Profile.UI.cs)
- Same frame as the artist page. The wash comes from the avatar and is not clipped.
- **Hero:** a round avatar; the name stays on 1 line.

| Tier | Avatar | Height | Name |
|---|---|---|---|
| Wide | 184 | 260 | 84/96 |
| Medium | 144 | 220 | 48/60 |
| Compact | 112 | 316 | 32/40 |
| Narrow | 96 | 352 | 32/40 |

- **Stats:** StatHero 28/36/350 over 600 tertiary labels: "Followers", "Following", "Public playlists".
- **Actions:**
  - Your own profile: "Open account" ↗ + Share + ⋯.
  - Others: Follow + Share + ⋯.
- **Pivot:** "Top artists", "Playlists", "Artists", "Following", "Followers".
- **Sections** (cards w+50):
  - "Top artists this month" ("Only visible to you"; ranked, with StatHero rank numbers)
  - "Public playlists"
  - "Recently played artists"
  - "Following"
  - "Followers"
  - "See all" appears only on Following and Followers.
- **States:**
  - "No public playlists yet" / "Playlists you make public show up here."
  - "This profile isn’t available" / "It may be private, or the account was closed."
- **Following / Followers lists:**
  - Breadcrumb + PageHero + SelectorBar "Following · 1,063 | Followers · 85".
  - Chips "All · 120 | Artists · 80 | People · 40" + "Find in following".
  - A letter-grouped grid (min 176), a sticky letter and an A–Z strip.
  - "Not following anyone yet" / "No followers yet".
  - "Follows are private" / "{name} hasn’t made their follows public."

### Concerts (Concert.Page.cs, Concert.UI.cs)

**Hub:**
- Crumbs "Browse › Concerts".
- Header card: "Live music" (accent), PageHero "Concerts", "Live shows, festival dates, and artists playing near you."
- **Filter bar** (sticky at 84, FillLayerDefault, r8):
  - "Filter by" and an "N Events" ticker.
  - Where pill: MapPin + "Seattle" ⌄.
  - "Dates" pill + "This weekend".
  - Genres: "All" + the top 3 + "+N genres".
- **Feed:**
  - "Near you" / "Recommended for you"
  - "Playlists for the scene"
  - "All events": LazyGrid, min 240
- **Event tile:** padding 8/8/8/12, square cover r8. "Fri, Mar 14 · 19:30" (accent), title 14/600, "Venue · City".
- **Empty states:**
  - "No concerts found" / "Set your location to discover live music near you." + "Set location".
  - "Nothing is on sale for this location right now. Try another place or category."

**Artist schedule:**
- Split hero, 320 tall: copy 56% / photo 44%. "On tour", "12 shows · 8 cities", the next show's 56×60 DateBlock, "View event", and the location button.
- "Tour dates" card:
  - Month tabs.
  - MonthBoard: "March 2026" at 20/28/600 + "4 shows".
  - Tiles 68 tall with a 3×32 rail and a 44 day column.
  - "Show all 9".
  - A shy month pill.
- Empty: "No upcoming concerts".

**Concert detail:**
- Wide (≥920) when there are tickets: main column + a 320 tickets rail.
- Hero: "Concert", title, status pill.
- Facts: "Saturday, March 14, 2026 · 19:30", "Doors open 18:30", the venue, "Ages 18+".
- "Buy tickets".
- "Lineup": 60-tall rows, 44 avatars.
- "Related concerts".
- Missing: "Concert not available" / "This event may have been removed or is no longer listed."

---

## 8. LIBRARY / RECENTS / SHOW / EPISODE

### Library (User.Page.Library.cs, User.UI.cs, Album.Pane.cs, Artist.Reader*.cs)

**Layout:**
- Wide (≥640): [navigator | 16 grip with a 1px hairline | right pane].
- Below 640 (exits at 664): a crumb bar and drill-down (160 ms fade, 12 slide).
- Navigator: FillLayerDefault, width 340 (artists 280), range 240–560.
- Right pane: FillCardDefault.

**Toolbar:**
- PageHero "Albums" / "Artists" / "Podcasts" / "Audiobooks" + a count (14/20 tertiary).
- Sort word rail: 13.5/18; resting words at 50% ink; the active word is 600 with a 2px underline that slides over 167 ms; gap 14; ▾ when descending.
- ViewToggle: three 28 cells (list / grid / "…").
- "Filter" box.

**Sort words:**
- Albums: "recents · alphabetical · artist · recently added · release date"
- Artists: "recents · alphabetical · albums"
- Shows: "recents · alphabetical · recently added"
- Podcasts swaps the rail for a mode switch: "Followed shows · Your Episodes"

**"…" flyout:** "View as" (4 cells) and "Size" (S/M/L).

**Views:**
- Modes: 0 CompactList, 1 List (default), 2 CompactGrid, 3 Grid.
- List rows: 56 tall (compact 40), 40 art (r20 for artists, r5 otherwise), title 14/600, subtitle 12/16 ("Artist · 2013" / "3 albums · 34 songs").
- Grid: min cell 116/140/164 by size (compact 88/104/120), cover r6, title 12/600. Selection is a 3px accent ring.

**A–Z:**
- Letter plates 28 tall: 12/600, +60 tracking, tertiary, on FillCardDefault r4.
- Sticky letter.
- A–Z strip, 18 wide: current letter in accent at 700; absent letters at 30%.

**States:** 8 shimmer rows; "Nothing matches your filter"; "Pick an album" / "Pick an artist" / "Select a show to see its episodes".

**Album pane:**
- Header 160: 128 cover, "Album · 2013", title 24/30/600, artist, "13 songs · 45 min · saved 2024".
- Actions: [Play][Shuffle][Save 36][… 36], then "Open album ↗".
- Embedded track table.
- "Also by {name} in your library" (96 covers).
- Failed: "Couldn't load the songs."

**Artists reader:**
- Band 100: 72 avatar, name at 32/40/700, "In your library: 3 albums · 35 songs", [Play all][Shuffle][Follow][↗].
- Sticky sub-rail: "in your library · all releases · 142" and "newest · oldest · alphabetical".
- Album blocks: a sticky cover 120 (88 when narrow), title 20/26/600, rows 36 tall.
- A 56 spine at ≥772.

**Your Episodes:** an episode table. Empty: "Episodes you save will appear here."

### Recents (Recents.Page.cs, Recents.UI.cs)
- **Masthead:** "Recents" in SurfaceDisplay + a Subtle "Overview" button. Summary line: "84 items · Monday – Today · grouped from 212 plays".
- **Pivots:** "All · Music · Podcasts · Artists" in PivotLabel 19/25/350 with a 2px accent underline.
- **List:** a grouped list with a 44 annotated scrollbar rail.
  - Day header: 48, pinned. ModuleHeader "Today" / "Yesterday" / weekday / "Oct 3", a line, "12 items".
  - Group row: 64, Row(48). "Played 12 tracks · owner". Saved groups show a green "✓ 3 songs added".
  - Trailing: a kind chip, when, a chevron, "…".
  - Drawer: an accent spine and 40-tall child rows.
- **Overview:** month cards (min 290), 38×32 cells, legend "Quieter ■■■■■ Busier".
- **States:** 8 seed rows; "Play something and it'll show up here"; Error.

### Show (Show.Page.cs, Show.UI.cs)

**Rail** (280), top to bottom:
1. Cover
2. "Podcast" + badges ("exclusive", E, "video")
3. Title
4. Publisher
5. Rating "4.8" + "1,234 ratings · yours 5★"
6. "128 episodes · weekly · since 2019"
7. Ledger "84 played · 3 in progress · 41 to go · 5 new"
8. Accent CTA: "＋ Follow" / "Resume · 23 min left" / "Episode 1" / "Latest". When the CTA is Follow, a ghost "▶ Episode 1" sits beside it.
9. Satellites: ♥, trailer, 🔔 (disabled), Share, ⋯
10. Topics
11. Description

**Reader** (padding 24, with a 30 year strip):
1. Sticky toolbar, 48:
   - "All 128 · Unplayed 41 · In progress 3 · Played 84"
   - "Find in this show" (180)
   - "Newest · Oldest"
   - Select
2. Visit head, by visitor state:
   - New: "start here" with doors ("begin here" / "where it began" / "Latest" / "Trailer"; 132 tall), then "About".
   - Returning: "continue" hero ("**17 min** left of 31 min", "Resume"), minis, "new since you were here".
   - Caught up: "You're all caught up".
3. "Episodes 128"
4. Month groups ("october", 18/24/300)
5. Rows
6. "Load more episodes" + "More like this"

**Empty states:** "This podcast doesn't have any episodes yet"; "nothing matches".

### Episode (Episode.UI.cs, Episode.Page.cs)

**ReaderRow:**
- Padding 11/12/0/12, r6, a 1px top hairline, 64 art (48 when narrow).
- Title 14/19/600, 2 lines.
- Blurb 13/18, 2 lines.
- Meta 12/16 tertiary: "#12 · chips · new · date · 45 min".
- Right: "…" (30) and a 36 tone play disc.
- Row states:
  - In progress: "23 min left" (600, tone) + a 3px rule.
  - Played: 85% opacity + "✓ Played".
  - Now playing: a 3px tone bar, equalizer, title in tone.
  - New: a "New" tag.

**Page rail:**
1. Show link
2. Title
3. "Oct 3 · 45 min"
4. Badges
5. Accent "Play" / "Resume · 23 min left"
6. ♥ "Save to Your Episodes" · ✓ "Mark as played" · "Copy link" · ⋯
7. "About", 4 lines

**Page right side:**
- Band pivot: "About this episode · Chapters · Transcript · Comments".
- Sections max 760 wide, headings 20/28/600.
- Empty states:
  - Chapters: "No chapters are available for this episode."
  - Transcript: "No transcript is available for this episode." (560 box)
  - Comments: "Comments (N)", "Write a comment", "Post comment", "Be the first to join the conversation."

---

## 9. QUEUE / RIGHT RAIL / PLAYER BAR

### Rail frame (Rail.UI.cs)
- Width 340, range 200–500.
- Docked: transparent, 1px hairlines on the left and top, r8 top-left.
- Header 44: RailHeader "Queue" / "Lyrics" ("Transcript") / "Friend activity" / "Video", then 32 glyph buttons, then ✕.
- There are no tab pills.

### Now playing panel
- "Now playing" eyebrow with a "Player style" gear.
- Square cover round((W−16)/4)·4 (324), r8.
- Title 20/28/600, up to 3 lines. Artists 14/20. Album 12/16. Heart 36.
- Lyrics peek: two 56 rows, the next line at .38, a 3px accent bar, Display face 20/28 at 350.
- Sections:
  - "About the artist"
  - "Listened to most in" (accent bars)
  - "Merch"
  - "Next up" (52 rows)
- Empty: "Nothing playing".
- Deck (developer only, when `npv.presentation` = Player):
  - 12 presets: Record, Cassette, Reel-to-reel, CD/MiniDisc, Turntable, iPod Classic, Winamp, Hi-fi VU, Zune, WMP visualizer, Canvas drift, Picture disc.
  - Options per preset, e.g. Record: Finish, Size, Speed, Sleeve.

### Queue (Queue.UI.cs:463-713)
- Pills: "Shuffle" · "Repeat" · "∞ Autoplay". "∞ Autoplay" writes `playback.autoplay` (verified at Queue.UI.cs:326, 507, 948).
- "Playing from X ›": 12/16, the name at 700.
- Now-playing card: RowTile, 44 art, accent hairline.
- Section headers (36): "Next in queue" / "Next up" / "Autoplay" + count; "Clear"; "Similar music will keep playing".
- **Modern rows:** 54 tall, 34 art. Heart 26 + ✕ 32. Autoplay rows at 72%.
- "⌄ Show next 100 · 240 more": 40 tall, FillCardSecondary.
- Empty: "Nothing playing".
- **Classic rows:** 44 tall, square, hairline, no art. A leading heart, then "Title Artists" in one 14/20 run, then ✕.
- **Stage pane:** 56 glass rows. The first is a 104 "Next" card with 80 art and a 22/28 title. Empty: "Nothing up next".

### Player bar (Shell.PlayerBar.UI.cs; tiers in Shell.cs:1194-1310)
- 72 tall, no fill. Top edge is a 1px divider, or a loading sweep after 450 ms.
- **Left:** art 48 (40 when narrow), r6. Title 14/700 (marquee), artist. "Playing on {device}" in accent. Heart.
  - Text states: "Nothing playing" / "Loading…" / "Connection lost — reconnecting…" / "This track isn't available".
- **Centre:** Prev 32 · Play (accent circle, 40; 36 when narrow) · Next. Time labels in 44 slots; clicking the right one toggles "Show remaining time".
- **Right:** shuffle, repeat, volume, a 96 slider, lyrics, video split, queue, devices, ⛶ "Full screen", ⋯.
- Width tiers (24 hysteresis):

| Tier | Min width | Left zone | Adds |
|---|---|---|---|
| Minimal | — | 132 | art, title, heart, play, remaining time, devices, ⋯ |
| Compact | 440 | 172 | — |
| Medium | 760 | 220 | prev/next, elapsed time, volume, lyrics |
| Comfortable | 900 | 230 | shuffle/repeat, remote-device line |
| Wide | 1100 | 240 | slider, video, queue |
| Full | 1240 | 260 | expand |

- Podcasts add ±10 s and a speed control.

---

## 10. STAGE / LYRICS / VIDEO (Shell\Stage*.cs, Lyrics*, Video*)

### Stage basics
- Dark ink: white (secondary .80, tertiary .60) on a #0A0A0A veil.
- OS fullscreen, Display face.
- Aspect classes: Compact (width <600 or height ≤460), Portrait (≤.80), Ultrawide (≥2.0), otherwise Desktop.
- Geometry:
  - Top bar 48. Transport card 112. Padding 24.
  - Now-playing card 460×136 at (24, 64).
  - Gallery 444; the face is inset 492 while it is open.
  - Desktop hero art = Q4(min(.28W, .82H − 340)). At 1920×1080: 536 at (108, 128), pane (740, 88) 1084×824.

**Layers, bottom to top:**
1. Backdrop: blurred cover, blobs, scrim (.56 pane / .22 Card / .40 Verse)
2. Artist photo
3. Face (enters at scale .97)
4. Smoke (440)
5. FaceScrim (.42)
6. Spectrum
7. Now-playing card
8. Identity block
9. Pane
10. Caption
11. Chrome
12. A 3px accent hairline when the chrome hides (after 3 s idle)

### Top bar
- Gradient .42 → 0.
- Left: an 18 accent square, "Wavee", "Now playing · full screen".
- Centre: SelectorBar "Lyrics · Visualizer · Up next · Artist" with an accent pill (icon-only when Portrait/Compact).
- Right: options glyph ("Full screen options") and "Exit full screen".
- TeachingTip: "Pick your visualizer" / "Got it".

### Transport card
- Acrylic, r8.
- Row 1: elapsed time, seek bar, total or "-m:ss".
- Row 2:
  - Left: "Playing from {kind}" + the name.
  - Centre: Shuffle · Prev 40 · Play disc 56 (accent) · Next · Repeat.
  - Right: heart · mute · volume 128 · device button (72–220) · ⋯.

### Identity block
- Cover radius: thumbnail 4, hero 8, Large art 8, Centered 18, Artist 8.
- Title sizes:

| Context | Title |
|---|---|
| Card thumb | 20/28 |
| Hero | 40/52/600 |
| Large art | 34/40/700 |
| Centered | 30/36/700 |
| Portrait | 28/36 |
| Compact | 24/30 |

- Meta: "Artist · Album".
- Chips (24 tall, r4, stroke): format, "Synced lyrics", "{bpm} BPM". Card and Large art only.
- Empty: "Nothing playing".

### Layouts (`stage.layout`)
- **Card ("Visualizer first", default):**
  - Full-bleed face with smoke.
  - Acrylic card: 96 thumbnail, 20/28 title.
  - Gallery on the right.
  - Caption: width .72 × region, 160 from the bottom. Active line 40–56 at 600 (44/52 at 1080p). Unsung words at .45, with an accent bloom σ10. Previous/next lines at .35/.45 when H ≥ 600. Instrumental breaks show three dots.
- **Large art ("Large cover"):**
  - 528 cover at (112, 69) at 1080p. Title 34/40/700, meta 17/24, chips.
  - Lyrics pane 1056×724 at x 752.
  - Strip 140 tall: 72 accent capsules. Alternatives: Ring (a 312 cover with 96 capsules) or Line (120 bars).
- **Centered ("Centered cover"):** 380 cover, r18, centred. Title 30/36/700, meta 16/22. No chips, no lyrics (ShowsCaption is false).
- **Artist ("Artist photo"):**
  - Full-bleed header photo with Ken Burns 1.03 → 1.10 over 40 s.
  - Without lyrics: "NOW PLAYING" eyebrow (13/16/600, +2.34 tracking), the name at Q4(.124H) (132 at 1080p, weight 700), and a 77 identity row.
  - With lyrics (default): a 58 cover and a pane .54W wide.
  - Foot scrim .40 + .6 · dim.
  - If there is no header photo, Large art is used instead.
- Compact always uses Card.

### Modes
- **"Up next":** 28/36 header + "Autoplay" toggle. Empty: "Nothing up next".
- **Artist pane:**
  - Name 48/56/700, "{n} monthly listeners · #{rank} in the world", Follow / "Go to artist".
  - "About" at 18/28 with "Read more".
  - "This song".
  - "Popular" (56 rows).
  - "Where people listen".
  - "Fans also like" (72 avatars).

### Options popover
- min(440, W−48) × min(720, H−48), acrylic, r8, titled "Full screen".
- Tabs "Visualizer" (19) | "Layout".
- **Visualizer tab:**
  - "Now showing" 136×76.
  - Chips: "All · Lyrics · Fluent · Classics · Zune · iTunes".
  - A 3-column 16:9 grid.
  - "Every visualizer works with every layout" + "Open gallery" [G].
- **Layout tab:**
  - "Where the cover and lyrics sit on top of your visualizer."
  - Four radio rows with 84×48 diagrams: Visualizer first / Large cover / Centered cover / Artist photo.
  - "Spectrum under the cover": Bars | Ring | Line | Off.
  - "Show lyrics over the photo", "Slow pan and zoom", "Photo dimming".

### Visualizer gallery
- 444 wide. Groups:
  - Lyrics: Verse
  - Fluent: Bloom (default), Bars, Ring, Orbit, Aurora, Timeline
  - Classics: Classic, Warp, Tunnel, Ambience, Kaleido, Scope, Drift
  - Zune: Type, Mosaic, Spotlight
  - iTunes: Magneto, Flow
- Tiles in 2 columns: a 78 preview, the name, "Calm"/"Lively", a blurb.
- Settings rows: "Sensitivity", "Lyrics over visualizer", "Calm motion", "Change with the music", "Sync offset".

### Lyrics
- **Sizes:**
  - Rail: 26/33/700, padding 22, active line at 40%.
  - Stage: 36/46 up to 64, padding 64, max width 1000, active line at 38%.
  - Podcast transcripts: 16/24 (rail) and 20/30 (stage).
- **Opacity by distance:** upcoming 1/.45/.24/.14/.11; already sung .19/.13/.10. Inactive lines scale to .98.
- **Effects:** word sync uses a glyph wipe with glow; line sync uses a σ10 halo. The stage adds a 3×28 accent pill.
- **Rail depth of field:** σ 0/1.25/2.5/4/5.5/6.5 × the blur strength (−1 = Auto = 100). The stage never applies DoF.
- **States:**
  - "Nothing playing"
  - "No lyrics available"
  - 6-bar shimmer
  - Unsynced plain text: 28/36 (stage), 19/26 (rail)
  - "Resync" pill
  - "Synced lyrics aren't available while a video is playing"
  - AI footer: "Word timing generated on this PC by AI. It can be off by a moment."

### Video
- **Menu:** "Dock in rail" / "Play in a mini player" / "Play in a separate window" / "Full screen" / "Always on top" / "Turn off video". Unavailable items show the reason.
- **Docked:** rail width, height 120–560, a 30 hover strip.
- **PiP:** 360×202 (min 240×135), r8, bottom-right at (16, 88).
- **Pop-out:** a 44 title band.
- **Fullscreen:** a 56 band with a 44 round exit button.
- **Join poster:** cover at .4 + "Loading…" or "Can't play — check your connection".
- **OnMedia transport:**
  - Prev 36 / Play 44 / Next 36, then seek, then mute / volume (52×168) / ⋯.
  - ⋯ menu: Aspect (Fit / Crop / Stretch / Original / 16:9 / 4:3 / 21:9 / 2.39:1), Quality (Auto / NNNp), Speed (0.5–2×).

---

## 11. SETTINGS (Screens\Settings.UI*.cs)

### Shell
- Masthead padding 36/16/36/12: a 24 gear + PageHero "Settings".
- SelectorBar tabs + divider: "General · Appearance · Playback · Notifications · Storage & cache · Privacy & diagnostics · About".
- One scroller per tab, padding 36/16/36/36. The column is left-aligned, max 1000. Cards are 4 apart.
- **Section header:** 32 above / 8 below; a 16 glyph + BodyStrong + caption.
- **SettingsCard:** min 68, padding 16, r4, 20 icon, header 14, description 12. Wraps below 476.
- **Expander items:** 52 tall, padding 58/8/44/8.
- **Picker cards:** 116×84, r8. Selected: 2px accent + AccentSubtle. The label sits under the card.
- **Confirm dialogs:** 320 wide. The destructive button is on the LEFT; "Cancel" is the default.
- Developer-only rows (Page motion, Hero) are removed entirely when developer mode is off.

### Appearance
**Theme** ("Theme, zoom and shell effects")
1. "Theme": System | Light | Dark (250 ms cross-fade).
2. "Zoom": "Auto ({n}%)", 50–250%.
3. "Marquee text" (on): "Scroll overflowing titles instead of truncating them".
4. "Color washes" (on): "Tint the shell and page surfaces from artwork".
5. Developer only: "Page motion": Fluent / Spatial / WinUI / Classic / None.

**Lists**

6. Expander "Row density": cards Compact / Default / Cozy / Comfortable. Item: "Always hide track artwork".
7. Expander "Track list style": cards Modern / Classic. "Choose artwork-forward rows or a classic column table."
8. Expander "Track page layout": cards Automatic / Hero. Items: "Keep left-rail same size" and "Clear all remembered sizes" ("Clear").
9. "Daylist clock": "Countdown and update time" / "Update time only" / "Countdown only".

**Sidebar** ("Saved on this PC")

10. Expander "Layout": two 200-wide pane cards (96 preview, "Active" pill), captioned "Sections you can show, hide and reorder." / "Spotify's one list with filters."
    - Items: "Density" (Default / Compact), "Reset this layout", "Edit sidebar…", "Reset everything…".

**Lyrics**

11. "Lyrics blur": 0–100 + "Auto".
12. AI card: "Word-by-word lyrics with on-device AI". States: Off / Unavailable / Download / Downloading / Paused / Preparing / Error / Ready.

**Full screen**

13. "Visualizer": 19 options, Bloom.
14. "Layout": Visualizer first / Large cover / Centered cover / Artist photo.
15. "Sensitivity".
16. "Lyrics over the visualizer".
17. "Sync offset".
18. "Calm motion".
19. "Change with the music".

**Now playing**

20. Developer only: "Hero".
21. "Player style": 12 options, default Record.

Not offered: an accent picker, a Mica toggle, font size.

### Other tabs
- **General:**
  - "App language".
  - "Open spotify: links in Wavee".
  - Tray: "Show Wavee's icon" (Always / Only while Wavee is hidden / Never), "Closing hides Wavee", "Minimizing hides Wavee", "Start hidden at sign-in", "Open Wavee when you sign in to Windows". Disabled rows explain why, e.g. "Turned off while the icon is set to Never".
  - "Graphics adapter".
- **Playback:**
  - Runtime card: Ready / "Not set up" + "Set up" / "Problem" + "Retry setup".
  - "Audio quality" (Normal / High / Very high / Lossless).
  - "On metered connections".
  - "Remember volume", "Autoplay", "Audio normalization".
  - "Loudness level" (Quiet / Normal / Loud).
  - "Album normalization".
  - "Equalizer" (Flat / Bass boost / Treble boost / Vocal / Radio / EQ proof + curve).
  - "Crossfade" (0–12).
  - "Video quality", "Video Auto on metered connections", "Prepare videos ahead of time", "Attached videos".
  - "Show remaining time".
- **Notifications:**
  - A blocked-by-Windows InfoBar.
  - "Windows notifications", "Play a sound", "Quiet hours".
  - Per-topic Off | In Wavee | Windows: "New albums and singles", "New podcast episodes", "Pre-saved albums, on release day", "Concerts and live shows", "New followers", "Your daylist refreshed", "Wavee updates", "Your own library activity".
- **Storage & cache:**
  - Usage card: "Total" + a 10-tall stacked bar + legend. Also "Reading storage sizes…" / "Could not read storage sizes".
  - Hue-stripe rows: "Library database", "Playback runtime", "Logs", "Local store & history", "Image cache".
  - "Cache encrypted audio", "Cache license keys".
  - "Audio body budget" (Fixed size / Drive share / Unlimited).
  - "Cache location".
  - "Metadata cache size" (64 MB).
  - "Factory reset" ("Reset Wavee").
- **Privacy & diagnostics:**
  - "What leaves this PC" ("No telemetry").
  - "Crash reports" (Off / Ask each time / Automatic).
  - "Your data on the crash service", "Privacy policy".
  - "Saved reports".
  - "Log viewer", "Detail level", "Log files", "Report a problem".
  - "Playback runtime", "Spotify Connect", "Realtime capture".
  - "Developer mode". Its items are greyed out while it is off: FPS overlay, Simulate an update, Send a test crash report.
- **About:**
  - Hero card: padding 24/22, a 64 AccentSubtle tile with a 30 MusicNote.
    - "Wavee {version} “{name}”" at 24/600.
    - Pills: channel and state.
    - "Built … · last checked …".
    - "Check for updates", "What’s new in {name}".
  - Rows: update status, "Release channel", "Install a waiting update when I quit Wavee", "Download on metered connections", "Show “What's new” after an update".
  - A list of links.
  - "Wavee is an independent client and is not affiliated with or endorsed by Spotify AB."
  - "Wavee right now".
  - Licences.

---

## 12. SETUP / WHAT'S NEW / FEEDBACK / CRASH / RECOVERY / LOGS

### Setup wizard
- **Plate:** modal 762×490 (min 320×184), FillSolidBase, stroke, dialog shadow; the shell behind is dimmed.
- **Body:** padding 24. A 192-wide Lottie icon column (only when the window is ≥770 wide). Title 28/36. Back button 30.
- **Footer** (80): a 210 progress column ("Step {n} of {total}" + a 162 bar), then [Primary Accent | Secondary Standard].
- **Pages:**
  - **Terms:** "Licence agreement & terms". "Help fix crashes" choice: No / Ask me first / Always send. Buttons "Accept" | "Decline".
  - **Sign in:** "Sign in to Spotify".
    - "Continue in your browser".
    - "Scan the code with your phone": QR 80, "{CODE} · spotify.com/pair · Expires in mm:ss".
    - "Wavee needs Spotify Premium."
    - Progress steps: "Connecting to Spotify · Preparing your library · Starting audio · Almost there".
    - Confirmation: "Is this you?".
    - Buttons by state: "Log in" | "Close"; "Yes, continue" | "Not me"; "Try again"; "Get a new code"; "Go to Premium".
  - **Layout** (first run only): "Choose your sidebar" with 224 cards. Button "Next".
  - **Local playback:** "Set up local playback". "Download from Spotify's CDN", "Advanced". Buttons "Download & set up" | "Not now", then "Open Wavee".
- **Sign-in door:** a 460 ContentDialog with the same cards (QR 96).

### What's new
- **Header:** a 22 Tag icon + PageHero "What's new". Content column + a 208 "Releases" rail.
- **Banner:** "Since you last looked: {count} releases — {from} → {to}. Showing everything new." + "Only the latest".
- **Hero:** r12, FillCardSecondary.
  - Pills "Latest" / "Stable".
  - "Wavee {v} “{name}”" at 30/600.
  - Meta: Released · Requires · Tag.
  - "Open on GitHub" / "Copy link".
- **"Highlights":** up to 3 cards (≤420 wide). 16:9 poster with a pill ("New" / "Improved" / "Rebuilt"). Title 13.5/18, body 12.5/17 (4 lines), "Read more".
- **Change sections:** a 22 badge ("Added" +, "Changed" ~, "Fixed" ✓, "Removed" −). Rows padding 12/9; at most 8 shown, then "Show all {count}".
  - Chips like "#412 · open", plus an avatar stack.
- **Empty:** "No release notes were found for this version."
- **After-update plate:** 720 wide. "Welcome to {product}" at 26/600. Footer: "Don't show this after updates" | "Full release notes" | Accent "Got it".

### Feedback dialog
- 548 wide, "Report a problem". Buttons "Open GitHub" | "Cancel".
- Type: Bug / Feature / Question / Idea.
- "Title" ("One line that says what's wrong").
- Bug fields: "What happened", "Steps to reproduce", "Expected behaviour", "Area".
- "Include diagnostics and the last {count} log lines".
- "Report preview" 500×220, with "Copy report" / "Save as…".

### Crash prompt
- 548 wide. "Wavee closed unexpectedly" (or "Wavee stopped responding").
- Buttons "Send report" | "Not now".
- InfoBar "Last run ended …".
- "What's in the report" (4 bullets) + "Read the full report".
- "Include a memory snapshot ({size})".
- "Always send reports, don't ask again".

### Recovery
- A Win32 TaskDialog in the OS theme: "Wavee couldn't start".
- Choices: "Start Wavee" / "Send the crash report" / "Reset Wavee…" + View report / Open folder / Copy / Quit.

### Logs page
- Breadcrumb "Settings › Privacy & diagnostics › Logs".
- Filter: "Filter logs" with All / Info+ / Warnings / Errors.
- Columns: Time 92 / Level 58 / Category 96 / Message. Rows 36.
- Empty: "No matching logs".

---

## 13. Settings that change presentation

| Key | Values (default) | What it changes on screen |
|---|---|---|
| `theme.mode` | System / Light / Dark | Polarity everywhere |
| accent | OS only | Pages also tint from the artwork-graded accent |
| `appearance.zoom*` | Auto | Scale |
| `appearance.marquee.enabled` | on | Title scrolling on the playing row and the player bar |
| `appearance.colorWashes.enabled` | on | Shell tint, page tone, artist/profile wash, Recents wash. Off = flat. Home's tint is always on; the artist veil and Play tint stay |
| `detail.rowstyle` | 0 Modern / 1 Classic | Table skin, queue skin, artist chart rows |
| `detail.rowdensity` | 0 / 1 / 2 / 3 (default 1) | Row heights (Modern 40/48/56/64, Classic 36/40/44/48) and art size; also the library album pane |
| `appearance.trackArtwork.hidden` | off | Art lane hidden in tables, Search, the chart, the queue (shows a glyph), Recents, NPV Popular, the library show pane |
| `detail.page.layout` | Automatic / Hero | Hero = vertical arm everywhere |
| `detail.rail.uniform` (+ per-kind width and collapse) | off | One shared 240 rail. Width range 180–480; collapsed = 96 strip |
| `detail.tempoColumn` / `detail.playsColumn` | off | BPM·Key (80) and Plays (52) lanes |
| `detail.sort.*` | custom (Liked: date, descending) | Sort plate |
| `appearance.likedCover.style` | Lens | Liked Songs cover |
| `appearance.daylistClock` | Both / UpdateTime / Countdown | Daylist clock face |
| `sidebar.layout.id` | Classic / Library | Sidebar sections |
| `sidebar.pane.density` | Default / Compact | Sidebar rows |
| Sidebar view (sidebar.json) | List / Grid | Library pane rows or tiles |
| `sidebar.pane.width` | 180–460 | Sidebar width |
| `library.<kind>.view/size/sort/desc/leftw` | List / M / recents / 340 (280) | Library navigator |
| `podcast.views` | per show | Reader filter and sort |
| `appearance.lyrics.blurStrength` | −1 = Auto (100) | Rail depth of field and halo |
| `stage.*` | mode Lyrics, visualizer Bloom, layout Card, spectrum Bars, heroLyrics on, heroMotion on, heroDim 75, calm off, moments on, lyricsOverlay on, sensitivity 1, sync 0, galleryOpen on | See §10 |
| `npv.presentation` (developer) / `npv.player.style` | Cover / Record | Rail hero |
| `playerbar.duration.remaining` | on | Remaining time vs total |
| `playback.autoplay` | on | Queue autoplay pill and section |
| `video.*`, `playback.video.quality` | docked / fit / ontop / Auto | Video surface |
| `appearance.pageMotion.style` (developer) | Classic | Navigation recipe |
| Developer mode / FPS | off | Developer surfaces |

Two other inputs also change presentation: the window width, and OS reduced motion (fades only, no Ken Burns, no marquee).

---

## 14. abstraction-map §4 and §6.1 summarised

### §4 Switch dissolution
Each switch gets one of four verdicts:
- (a) a Classic option, either Classic structure (CS) or Classic style (CY);
- (b) another theme's option;
- (c) a core preference;
- (d) deleted.

A migration latch `theme.bootstrap.version=1` moves old keys to `theme.<id>.<layer>.<option>` or to typed core preferences.

**(a) Classic options:**
- Washes → CY `style.surfaces` (Off/Subtle/Rich) + `accentFromArtwork` + `nowPlayingColors`.
- Row style → CS `structure.rowStyle` (Modern/Classic). This deletes about 324 `classic` branches, `ColumnSet.Classic`, and the `classic|modern` cache, list and queue keys.
- Density → CY `style.density`, four steps; WinUI and Zune define their own; delivered via `StyleMetrics.Row`.
- Sidebar layout → CS `structure.sidebar` (Classic/Library). The Zune value is deleted (the Zune nav is the Zune theme).
- Sidebar density and covers → CS `libraryDensity` / `showCovers`. The library list/grid choice becomes state.
- Hide track art → CS `hideTrackArt`.
- Marquee → CS `marquee`.
- Page layout and rail uniform → CS `pageLayout` / `railUniform`. Rail widths become state, and "clear remembered sizes" becomes an action.
- Stage layout, spectrum, heroLyrics, heroMotion and heroDim → CS options.
- NPV presentation → CS `heroPresentation` (developer visibility).
- Lyrics blur → CS `lyricsBlur`.
- Width ladders → structure policies; Classic keeps today's.

**(c) Core preferences:**
- Theme mode, Liked cover, daylist clock, tempo/plays columns, zoom.
- Stage mode, visualizer, sensitivity, sync, calm, moments, overlay, gallery, tip.
- Deck, player-bar remaining time, AI lyrics, video, reduced motion, developer, FPS, StoreShot, tray, notifications, update, language.
- Library view/size/sort/desc/scope, podcast views, per-list sort, autoplay.

**(d) Deleted:**
- PageMotionStyle (each structure picks its own; Classic = "Classic").
- PR205's PageHead hoisting, the ArtistBleed gate, chrome ink, the entrance clock.
- Legacy CTA pills, `Prefs.BumpAll`.
- PivotLabel/Facet styles are replaced by a Choice contract.

**Accent:** Classic uses the system accent; WinUI uses `useWindowsAccent`; Zune offers 5 swatches.

### §6.1 Wavee Classic
- **Goal:** pixel-identical to production. Acceptance is a golden-frame comparison against main for every route, panel mode and stage layout, in light and dark.
- **Views:** the Classic structure owns every view: sidebar + title row with tabs, rail, player bar, stage (4 layouts), lyrics DoF, queue (2 skins), detail frame (rail + vertical), Home zones, Browse, search, library, recents, profile, concerts, settings, setup, release notes, dialogs, menus, toasts. Diagnostics and logs come from Common.
- **Policies (unchanged):**
  - Bands 700/660/528.
  - Pane 180–460.
  - Rail 200–500.
  - Player tiers 440/760/900/1100/1240, 24 hysteresis.
  - The chrome allocator.
- **Capabilities:** Tabs, NavTree, FriendsPanel, ForwardButton and HistoryMenu are true; MaxBandPins is 0.
- **Structure options:**
  - sidebar {Classic, Library} = Classic
  - showCovers = false
  - libraryDensity {Default, Compact}
  - rowStyle {Modern, Classic} = Modern
  - hideTrackArt = false
  - marquee = true
  - pageLayout {Automatic, Hero}
  - railUniform
  - lyricsBlur
  - heroPresentation {Cover, Player} (developer)
  - stageLayout {Card, LargeArt, Centered, Artist}
  - stageSpectrum, stageHeroLyrics, stageHeroMotion, stageHeroDim
  - action: clearRememberedSizes
- **Style options:**
  - density {Compact, Default, Cozy, Comfortable} = Default
  - surfaces {Off, Subtle, Rich}
  - accentFromArtwork = true
  - nowPlayingColors = true
- **State:** pane width and collapse, library list/grid, rail widths, detail rail widths, library column widths, docked video height.
- **Style:** the engine neutral palette, the system accent, today's Design.* values as the role table, Mica (MicaAlt false).

---

## 15. Caveats
- Corrections to the earlier maps:
  - Radii.Card is 8 (a stale comment says 14).
  - The Centered stage never shows a caption.
  - BackdropScrim is a flat .42.
  - Setup has 4 pages.
  - `docs\guide\fullscreen-visualizers.md` is stale.
- Classic rows never show art, regardless of HideTrackArtwork (Track.Rules.cs:160).
- The E badge appears only on playlist and Liked rows.
- The two-column rail has no Shuffle.
- The artist page has no "Show all", no discography links and no ⋯.
- There is no recent-searches page.
- `stage.nextFrom` and `VacancyVoice.Offline` are unused.
- Liked Songs is described as it is on main. PR 205's Liked redesign and the Zune hoisting are not in production and are excluded.
- Server-supplied titles (shelf names, daylist tags) are dynamic, so those strings are illustrative. Every quoted UI chrome string was checked against en-US.json.
