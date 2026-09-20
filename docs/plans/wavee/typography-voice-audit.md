# Typography and CTA voice audit

Wavee currently speaks **three type languages** and a **fourth CTA language** on the same screens. The design system already picked a voice (sentence case, weights 400/600, aliases in `Design.Type`). What is on screen is three eras stacked — Zune lowercase islands, Fluent sentence chrome, leftover ALL CAPS in localization — plus Spotify-style Play capsules that are neither Fluent nor Zune.

Catalog titles (`Merry Christmas`, `Mariah Carey`) are out of scope. They keep the provider’s own casing.

## Snapshot

| Metric | Value |
|---|---|
| Active type voices | 3 (Zune lower / Fluent sentence / ALL CAPS) |
| CTA language | Web capsules (not Fluent, not Zune) |
| `en-US.json` leaf strings | ~2598 |
| Sentence case | ~1265 |
| Single capitalized word (`Play`, `Album`) | ~703 |
| Zune-lowercase phrases | ~239 |
| Mixed / ICU / proper nouns | ~273 |
| Title Case | ~63 |
| ALL CAPS | ~35 |
| Named `Design.Type` aliases | 18 |
| Numeric weights in UI | 7 (300, 350, 400, 600, 650, 700, 800) |

Heuristic counts from `src/apps/Wavee/assets/loc/en-US.json` after stripping ICU `{…}` blocks. Single-word labels (`Play`) are sentence *or* title; they are usually buttons.

## Three written policies, none of them match the pixels

1. **Design system** (`docs/plans/wavee/wavee-0.3-ui/00-design-system.md` §6.7, `Design.Type.Eyebrow`): sentence case; **no** `.ToUpper()` on a localized string.
2. **Loc glossary** (`ops/loc/glossary.json`): “sentence case except eyebrows.”
3. **Library rework** (`docs/plans/wavee/library-rework-implementation.md` ~1923): lowercase `library.rail.*` **on purpose** so sidebar V3 pills can stay Title Case on `library.sort.*`.

Those three documents are why an album still shouts `ALBUM · 1994` next to `Playlist · Private`, and why the same sort is `recents` on the page and `Recents` in the sidebar.

There is no CSS-like `text-transform`. `Eyebrow` passes the string through. Translators inherit whatever English authored.

---

## Same role, different casing

Each row is one job the UI asks type to do.

| Role | Voice A | Voice B | Voice C |
|---|---|---|---|
| Entity type eyebrow | `ALBUM · 1994` (CAPS) | `Playlist · Private` (sentence) | `Podcast` (sentence) |
| Section header | `continue` / `episodes 325` (zune) | `Top tracks` (sentence) | `Queue` (Title) |
| Filter / sort rail | `All · Unplayed · Played` (sentence) | `recents · a–z · albums` (zune) | `in your library` (zune) |
| Resume / continue | `continue` (zune) | `Resume · 4 min left` (sentence) | `CONTINUE LISTENING` (CAPS) |
| Kind on a release | `2014 · ALBUM` (CAPS) | `Album` / `Song` / `Playlist` (sentence) | `episode {n}` (zune) |
| Recents label | `recents` (zune) | `Recents` (Title) | `Recently added` (sentence) |
| Now playing | `Now playing` (`player.nowPlaying`) | `Now Playing` (sidebar customizer) | — |
| Episodes word | `episodes {count}` (live header) | `Episodes` (skeleton / search) | `EPISODE` (badge) |
| Month / date group | `september 2026` (show reader, forced lower) | `September 2026` (concerts, recents) | `today` vs `Today` |
| Queue section header | `Next in queue` (Eyebrow 12/600) | `Playing next` (raw 20/600 on Stage) | same job, two rungs |

Intra-key bug: `podcast.episodes` is `"Episodes"` (skeleton) while `podcast.episodesCount` is `"episodes {count}"` (live header).

---

## How each type voice is produced

### 1. Zune lowercase

Authored into loc, or `ToLower` at render.

Examples: `continue` · `recents` · `in your library` · `september 2026`

Homes: podcast reader, library word/scope rails, search query echo, home eyebrows, Zune deck face.

### 2. Fluent sentence

Default loc voice. Official design-system policy.

Examples: `Play` · `Find in this show` · `Top tracks` · `Playlist · Private`

Homes: album/playlist/artist chrome, buttons, settings, queue, most menus.

### 3. ALL CAPS leftover

Still sitting in `en-US.json`. Eyebrow no longer transforms, so they still shout.

Examples: `ALBUM` · `EPISODE` · `DAILY MIX` · `WATCH THE OFFICIAL VIDEO` · `MOST VISITED` · `LIVE` · `YOU`

Homes: release-kind badges, home mix eyebrow, official-video label, history, live chips.

Real acronyms (`BPM`, `ISRC`, `EP`, `CD`, `SHA-256`) are fine. Phrase shouts are not.

---

## Where Zune actually lives

It is not a global style. A handful of surfaces either store lowercase copy or force it at render.

| Surface | Mechanism | Typical copy |
|---|---|---|
| Podcast reader | Loc values themselves. File header: “NO LOWER-CASING OF A LOC STRING” | `continue`, `about`, `start here`, `episodes {count}`, `weekly`, `nothing matches` |
| Month headers (show) | `DateKeys.MonthLabel(...).ToLower(CurrentCulture)` | `september 2026` |
| Library word / scope rails | `library.rail.*` and `library.scope.*` — a second key set so sidebar pills stay sentence | `recents`, `a–z`, `albums`, `in your library`, `all releases` |
| Search query echo | `query.ToLowerInvariant()` into `SurfaceDisplay` | `sleep` even if you typed `SLEEP` |
| Home eyebrows | Loc: `charts`, `weekly`, `daily`, `resume`, `unplayed`, `your daylist` | Sits above sentence-case module titles |
| `Controls.Words.Links` | `ToLower(CurrentCulture)` on catalogue topic words | Show topic chips |
| Zune now-playing deck | `TitleOf(...).ToLowerInvariant()`, weight 300, last syllable in accent | Player-style only — not page chrome |

Runtime transforms that still exist (not a global CSS flag):

| Site | Transform |
|---|---|
| `Design.Type.Eyebrow` | **none** (policy) |
| `Show.UI` month headers | `.ToLower(CurrentCulture)` |
| `Search.UI` query echo + related queries | `.ToLowerInvariant()` |
| `Controls.Words.Links` | `.ToLower(CurrentCulture)` |
| `Track.Table.Chrome` classic mode | `.ToUpper(CurrentUICulture)` |
| `Deck.Faces` / `Deck.UI` | `.ToUpperInvariant()` / `.ToLowerInvariant()` |
| `ReleaseNotes.UI` scope chips | `.ToUpperInvariant()` |
| Diagnostics level badge | `.ToUpperInvariant()` |

Hardcoded English that bypasses loc: `Deck.Faces` (`Now Playing`, `MENU`, `STEREO`); some module CTAs (`Play`, `Open on YouTube`).

---

## Worst intra-page mixes

Mixing voices **inside one view** is the annoyance, not the existence of a Zune rail.

1. **Show page (loudest).** Sentence filters (`All` / `Unplayed`) on a Zune word rail, above 24/300 lowercase `continue` / `episodes 325`, above sentence `Find in this show`, beside sentence `Podcast` and sentence `Resume`. Month `september 2026` is forced lower; recents/concerts months are not.
2. **Artist library.** Title-case `Artists 294`, Zune `recents` / `a–z` / `albums`, sentence `Filter`, then `2014 · ALBUM` in caps. Scope rail `in your library` vs band copy `In your library: …`.
3. **Album vs playlist (same hero slot).** `Detail.Text.Eyebrow` still documents `"ALBUM · 2013"` for releases and `"Playlist"` for lists. Album also ships `WATCH THE OFFICIAL VIDEO`.
4. **Artist overview.** Display 700 name + sentence `Top tracks` + Title `Albums` + zune `in your library` / `all releases` / `a–z`.
5. **Queue.** Rail uses Eyebrow 12/600 (`Next in queue`); immersive Stage uses raw 20/600 (`Playing next`) for the same job.

---

## Play buttons and CTAs — a fourth language

They look like web buttons because that is what they are specified to be. They are **not Fluent** and **not Zune**.

| Language | Primary action |
|---|---|
| Fluent AccentButton | 32-tall, `Radii.Control` (4 px) rectangle, arrow cursor, no scale |
| Zune | A word |
| Wavee media Play | 36-tall `Radii.Full` capsule, padding `18/6/18/7`, **Bold 700**, hand cursor, hover scale 1.04 |

That skin lives in `src/apps/Wavee/Platform/Controls.Cta.cs`. Five substitutions on a stock engine `Button`, documented as the “media personality.” Utility surfaces **must** keep Fluent rectangles (`Button.Create` directly). Media primaries are supposed to look like this. Closest cousin: Spotify / Apple Music.

### Clusters still don’t agree

| Cluster | Primary | Satellites | What it actually is |
|---|---|---|---|
| Album / playlist | 36 capsule, cover accent, Bold 700, “Play” | 32 × 32 `Radii.Control` ghost squares (shuffle, heart, share, more) | Web pill + Fluent icon buttons. Two grammars in one row. |
| Artist hero | Same 36 capsule, cover accent, “Play” | 36 `IconPill` circles + outlined Follow capsule (600, 1 px stroke) | Closest to the written table: “three capsules, one of which is round.” Following is a fourth shape. |
| Podcast rail | Hand-rolled box: same 36 / Full / 18-6-18-7, but weight **600** and `Tok.AccentDefault` | Heart satellite | Same silhouette, different weight, app accent instead of show tone (reports 11c/11d). |
| Settings / dialogs / empty states | Stock Button rectangles | Stock IconButton 32 | The Fluent row reserved for utility. Correct, and a different family from Play. |

The geometry table already admits three icon rows:

1. **32 × 32 `Radii.Control`** — standard icon button (toolbars, panels, rows).
2. **36 × 36 `Radii.Full`** — icon-only arm **of the pill**, only inside a CTA cluster.
3. **Circle, any diameter** — FAB on artwork only.

Album/playlist heroes use row 1 next to a media pill (row 2’s job). Follow is an outlined capsule that is not in the table (`Controls.FollowButton`). Play’s Bold 700 is also off the type-ramp “400/600 only” rule — the CTA file says Regular would not read next to hero type.

Chrome weight leaks beyond Play: player-bar title 700, notification panel title 700, toast titles 13.5/700, omnibar match spans 700, diagnostics level badge **800**.

---

## Type ramp vs ad-hoc sizes

`Design.Type` (`src/apps/Wavee/Platform/Design.cs` ~1134): every text node takes size + line height + weight from the engine’s 8-rung ramp. **NEVER** author a raw `TextEl { Size = … }`. Weight policy: **400 and 600 only**, with six named divergences (three 700 artist mastheads, three 350 SemiLights).

### Policy aliases

| Alias | Size / weight | Job |
|---|---|---|
| `TrackMeta` / `Eyebrow` | 12 / 400 or 600 | Captions and kind labels |
| `TrackTitle` / `CardTitle` | 14 / 600 | List and card titles |
| `RailHeader` / `ModuleHeader` | 20 / 600 | Section and home headers |
| `PageHero` / `DetailHero` | 28 / 600 | Album and playlist names |
| `SurfaceDisplay` | 40 / 400 | Library place names, search echo |
| `ArtistDisplay` | 84 / 700 | Artist masthead only |
| `PivotLabel` / `NpvLyric` / `StatHero` | 19–28 / 350 | Three sanctioned SemiLights |

### Off-ramp clusters

| Weight / size | Where |
|---|---|
| 300 at 18 / 24 / 26 | Podcast pivots, month heads, Words Big rail, Zune deck |
| 13 and 13.5 | Word rail, publisher line, drawer, settings utility |
| 650 | Track drawer credits |
| 700 (unsanctioned) | Play label, player-bar title, toast titles, notification panel |
| 800 | Diagnostics level badge |
| CharSpacing 60–90 | Old ALL-CAPS tracking still on `User.UI` letter plates and some podcast numerals |

`Controls.Words` is an intentional second ramp (13.5/18, 400→600; Big 20/26, 300→400). Settings section headers skip `ModuleHeader` and use `BodyStrong` 14/600.

---

## A single system that would actually hold

Not a redesign. A case, weight, and **shape** contract per *role*, so a filter is a filter and a Play is a Play on every page.

| Role | Keep | Kill |
|---|---|---|
| Buttons, menus, settings, toasts, placeholders | Sentence case, 400/600 via aliases | Title Case leftovers (`Now Playing` vs `Now playing`) |
| Entity type / kind labels | One sentence form: Album, Episode, Playlist, Podcast | `ALBUM` / `EPISODE` / `COMPILATION` in loc; `Podcast` vs `ALBUM` on the same hero slot |
| Word rails (library + podcast filters + artist scope) | Pick one: all Zune lowercase, **or** all sentence. Same component, same loc pattern | `All` / `Unplayed` sitting on the same control as `recents` / `a–z` |
| Section headers on entity pages | `ModuleHeader` / `AccentHeader` sentence: Top tracks, Episodes, About | 24/300 lowercase `continue` / `episodes` / `about` as a third header voice |
| Editorial / Zune | Contain it: search echo, Zune player face, maybe home eyebrows | Leaking Zune into type labels, CTAs, and month heads if the rest of the page is Fluent |
| Acronyms | `BPM`, `ISRC`, `EP`, `CD` | `WATCH THE OFFICIAL VIDEO`, `CONTINUE LISTENING`, `MOST VISITED`, `DAYS`/`HRS` as unit shouts |
| Media Play | One capsule, one weight, one satellite geometry | 32 ghost squares beside a 36 pill on album/playlist; Follow as a fourth shape; podcast 600 vs Play 700 |
| Utility buttons | Stock Fluent rectangles | Capsules in settings/dialogs |

If the product wants the web Play capsule, keep it — but make **every** media cluster use the same 36 pill + 36 round arms, and stop parking Fluent 32 squares next to it.

---

## Sources

| What | Where |
|---|---|
| Type aliases | `src/apps/Wavee/Platform/Design.cs` (`Design.Type`) |
| CTA / media pill | `src/apps/Wavee/Platform/Controls.Cta.cs` |
| Word rail | `src/apps/Wavee/Platform/Controls.Words.cs` |
| Follow pill | `src/apps/Wavee/Platform/Controls.cs` (`FollowButton`) |
| Detail eyebrows | `src/apps/Wavee/Entities/Detail.cs` (`Detail.Text`) |
| Show reader pivots | `src/apps/Wavee/Entities/Show.UI.cs` |
| Library rails | `src/apps/Wavee/Entities/User.UI.cs`, `User.cs` (`LibraryWordRail`) |
| English copy | `src/apps/Wavee/assets/loc/en-US.json` |
| Design contract | `docs/plans/wavee/wavee-0.3-ui/00-design-system.md` §6.1, §6.7 |
| Dual-key rail decision | `docs/plans/wavee/library-rework-implementation.md` |
| Loc tone | `ops/loc/glossary.json` |
| Weight tests | `src/apps/Wavee.Tests/DesignTests.cs` (`DesignTypeRampTests`) |
