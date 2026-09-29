# Wavee Home — user value & flows (agent 1 of 4)

## What the current page does (from Home.cs / Home.Rules.cs / Home.Page.cs — a few bullets, not a full audit)

- Home is a **prototype-authored row table**, not a raw render of the server feed. `HomeLandingProjection`
  concatenates server section groups in feed order, dedupes by card, and maps them onto a fixed set of
  **app-authored modules** (`HomeLayoutModules.DefaultOrder`) — chrome rows (Chips / Artists / Timeline /
  Charts / Sections / Tail) are **not user-orderable and never persisted**; everything else can be hidden/
  reordered via a `HomeLayoutDoc`.
- There's an **"appointments"** concept: a two-up module when two appointment-like sections exist (e.g. two
  editorial mixes), or a single appointment card falling into a quick grid when only one exists — so the page
  already tries to promote "your regular things" above the shelf pile.
- A **facet strip** (the `homeChips`: Music / Podcasts / Audiobooks, each with a Following sub-chip) filters
  the whole page. Facet pages take the **server's section order verbatim**, except **a run of consecutive
  untitled/baseline sections folds into one "discover feed"** — i.e. the current code already recognizes the
  ~20 one-item `HomeFeedBaselineSectionData` sections as noise and collapses adjacent ones together, but only
  when they're facet-filtered and only when they're consecutive.
- There's a **hero** module with fixed geometry tiers (388/348/340) and a **shell wash** — a 3-tier colour
  selector (payload accent → graded cover-art colour → nothing) drives the page's ambient background tint from
  up to three "hero" cards, selected by kind + ordinal.
- Card grids have shared geometry math (title lines, metadata line, chrome reserve) so the estimator and
  renderer agree before layout — a scroll/measurement concern, not a content one.

**Net effect for this task:** the current engine is well-built to *host* whatever shelves we hand it (hide,
reorder, fold consecutive noise, appointments, hero wash) — but it doesn't yet decide **what deserves a shelf
at all**, which is exactly the redesign question. Nothing in the current model does resume/continue-listening,
nothing distinguishes "listen now" content from "browse" content, and nothing merges same-reason baseline
sections that aren't consecutive.

## Method note

I read `home_digest.txt` first (31 sections, greeting, homeChips) and then queried the raw 900 KB response
(`home_new.json`) with Python for the fields that carry actual user value: the `Recents` section's
`ListResponseWrapper`, `headerEntity`, `iconName`, `extractedColors`, image `sources`, `formatListAttributes`,
descriptions with `<a href=spotify:...>` links, and `totalCount` vs returned-item counts. Findings below are
cited by field name so later agents can trust what's reliably present vs. what I'm inferring.

---

## 1. Jobs-to-be-done when someone opens Home, ranked

Reasoning grounded in well-established listening behaviour (Spotify's own product literature, and just what
the response shape implies): most app opens are **not** discovery sessions — they're "put music on" moments.
Session lengths are short, decision-avoidance is the dominant behaviour (people re-play the same ~20 things
constantly), and there's strong time-of-day and day-of-week ritual (mornings, workdays, Friday releases,
weekend). Desktop specifically skews toward **longer, more deliberate lean-back sessions** (people don't pull
out a desktop app for a 30-second skip decision the way they do a phone) and toward **users who already know
their library** (a left-sidebar library exists in Wavee, unlike bare mobile Spotify).

1. **Resume / keep going** (highest frequency, near-zero decision cost). "Put back on what I was just doing."
   *Evidence in the data:* `HomeRecentlyPlayedSectionData` "Recents" — a single `ListResponseWrapper` with
   **20 real, mixed-type items** (Artist, Album, Playlist, Single) tagged `recent_type_played` in
   `formatListAttributes`, plus `Liked Songs` itself appearing as a recent (`children_group_id`,
   `group_metadata`-carrying entry). This is the single most literal, lowest-friction job the API already
   answers directly — and it's buried as item 4 of 31 sections, same visual weight as "Popular radio."
2. **Play something good now, no decision** (very high frequency, "lean-back"). Daily Mix N (6 of them),
   DJ, Discover Weekly, Release Radar, "Your top mixes" (10 curated mixes each captioned with the actual
   sibling artists via `<a href=spotify:playlist:...>Artist</a>` links in the description HTML). This is
   Spotify's best-understood job and the response is *rich* here — arguably over-rich (6 Daily Mixes + 10 top
   mixes + a DJ + Discover Weekly = 18 "just play" options competing for the same slot).
3. **Weekly ritual / catch up on what's new** (high frequency, strong day-of-week signal). "It's New Music
   Friday!" (today is a Friday in-universe: `greeting` = "Good morning" and the response is dated for release
   day), Release Radar nested inside it, "Officiële Spotify playlists" also containing New Music Friday NL —
   this is the *artists-you-follow* new-release job, currently diluted by being buried inside a generic shelf
   and duplicated across two sections.
4. **Mood / moment music** (medium-high, time-of-day and context driven). "Soundtrack your Friday morning"
   (9 mood mixes: Chill Sad, Wedding, Comforting, Hopeless Romantic Soft, Chill Happy, Happy Walking, Feel
   Good Happy, Breakup, Fun Road Trip — genuinely time-stamped copy, not generic), "Recommended Stations."
   This is the job of "I don't know what I want, but I know how I feel" — currently just another anonymous
   shelf with no visual distinction from a genre shelf.
5. **Rediscover something you already love, freshly framed** (medium). "Jump back in" (a real mix of albums/
   playlists/artists you've engaged with, distinct from raw Recents by being curated rather than chronological),
   "More like Troye Sivan," "Popular radio," the ~20 single-item baseline sections ("For fans of Henry Moodie,"
   "More like ROSÉ," "Based on your recent listening"). This is a real job — surfacing an artist-adjacent pick
   you wouldn't have searched for — but the current *packaging* (20 separate one-item shelves) actively works
   against the job by burying each pick in its own near-invisible row.
6. **Explore something genuinely new to you** (lowest frequency, highest cognitive cost, but the job Home is
   uniquely suited to vs. the library/search). The Shorts rail (10 of 64 shortcuts — genres, playlists,
   artists, one broken `UnknownType` entry pointing at `spotify:user:@:collection` i.e. Liked Songs mis-typed),
   "Recommended Stations," genre/regional editorial ("Officiële Spotify playlists," K-Pop/NL-specific shelves).

Podcasts/Audiobooks are **present as chips only** — this response has effectively zero podcast/audiobook
content (facet exists, content doesn't), meaning job priority 3/4 above don't currently exist for those media
types for this user; that's a real edge state, not a design choice to make.

## 2. Per-job ideal flow, what serves it today, what's missing

**Resume.** Ideal: 0 clicks to *see* the last 3–5 things, 1 click/1 keypress to resume audio, keyboard-first
(desktop). *Serves it:* `Recents` list (20 items, richly typed — but no `playedAt` timestamp anywhere in the
payload; I checked — there is no per-item "last played 2h ago" field, only a boolean-ish `recent_type_played`
attribute key with an **empty value**). *Missing:* recency ordering confidence (is item 0 most recent? the
list order is the only signal — untested but implied), a "resume where I left off" distinction from "recently
opened" (a played-to-completion album vs. a glanced-at playlist read identically), and **no explicit
"continue" affordance** — Recents today is just another horizontal shelf, same as "Popular radio."

**Play something good, no decision.** Ideal: land on Home, one card is already "the" obvious next play (hero
treatment exists in code already), <1 click. *Serves it:* Daily Mixes, DJ, Discover Weekly all present with
real per-mix artist rosters in `description` (HTML `<a href=spotify:playlist:...>Name</a>` chains — reliably
present on every Mix/Radio-type playlist, 3–5 names each, useful as literal "why" copy with zero extra API
cost). *Missing:* a single ranked "best bet right now" — the response gives ~18 candidates with no signal for
which one to promote (no play-count, no last-mix-played, no freshness-of-mix field); the app has to decide,
the API won't.

**Catch up on new releases.** Ideal: a distinct "New for you" zone, 1 click to a single Friday drop or a
specific followed artist's new release, ideally person-attributed not just playlist-attributed. *Serves it:*
"It's New Music Friday!" contains Release Radar + regional/genre New Music shelves; "Officiële Spotify
playlists" re-lists New Music Friday NL. *Missing:* **no direct "artist you follow released X" cards** — every
new-release surface here is a curated *playlist*, never a bare new album/single from a followed artist with a
release date (`releaseDate` does not appear anywhere in the whole 900 KB payload — confirmed by full-text
search). If "new from people I follow" is wanted as a first-class job, Home's GraphQL response as captured
doesn't carry it; it would need a different query/module (e.g. reuse of the artist's discography endpoint) —
worth flagging to the API-facing agent.

**Mood/moment.** Ideal: pick a vibe in 1 click, get playback in 2. *Serves it:* "Soundtrack your Friday
morning" — the section title itself encodes time-of-day (`transformedLabel` is literally recomputed per
day-part server-side; compare "Good morning" greeting), 9 sub-mixes covering distinct moods. *Missing:*
nothing structurally — the field is genuinely good — but it's **visually identical** to every other
`HomeGenericSectionData` shelf today, so the time-of-day framing (the one truly contextual, ephemeral thing in
the whole payload) gets no special treatment.

**Rediscover.** Ideal: land on 3–6 "you'll like this because you like X" cards with the "why" visible without
a click. *Serves it:* "Jump back in" (9 items, mixed types, no baseline "why" copy — title carries the intent),
plus the ~20 `HomeFeedBaselineSectionData` (see critique below) which **do** carry a why in their section
title ("For fans of Henry Moodie," "More like ROSÉ," "Based on your recent listening," each with an `iconName`
field present on every one — I confirmed `iconName` is populated on all 20, though the digest didn't print its
value; worth agent 2 checking the icon vocabulary for visual treatment). *Missing:* grouping — see §3.

**Explore new.** Ideal: an escape hatch, not a wall — 1 click into "surprise me," not required reading.
*Serves it:* Shorts rail (genre pills, artists, playlists — `spotify:section:...` uri suggests it's itself
independently paginated, 10-of-64 returned confirming true pagination not just a display cap), "Recommended
Stations" (50 total, 10 returned), "Officiële Spotify playlists" (regional editorial). *Missing:* nothing
missing exactly, but this job is already the *best-served* job in raw section count while being the
*lowest-value* job per the frequency ranking above — the current page over-invests here relative to resume/
mood.

## 3. Critique of the raw response as a page

**Redundancy is real and measurable, not a symptom of my imagination:**
- New Music Friday NL appears **twice** verbatim: inside "It's New Music Friday!" (item 1) and again inside
  "Officiële Spotify playlists ✅" (item 5 of that shelf).
- "Pop 터지는 팝콘 한 입" appears in "More like Troye Sivan" (shelf, item 8) **and again** as its own baseline
  section [25] with the identical title "More like Troye Sivan" — same reason, same playlist, two different
  section objects.
- "Sleep Music for Deep Sleeping" appears in both the Shorts rail and "Jump back in."
- Daily Mix 1–6 appear once each in "Made For Chris," but the *artists* driving several of them (Henry Moodie
  from Daily Mix 4, Troye Sivan implicitly) resurface as reasons for 5+ of the 20 baseline sections — the same
  underlying taste signal (this user likes Henry Moodie, Troye Sivan, ROSÉ, Acda en de Munnik, Jung Seung Hwan,
  Savage Garden, 90's Nederlandstalig) is independently re-derived by the backend into **6 different reason
  clusters**, each spawning its own throwaway one-item section instead of being unified into one "because you
  like [artist]" module with 6 picks.
- The 20 `HomeFeedBaselineSectionData` sections are the single biggest structural problem: **each carries
  `total=1`** — these are not truncated shelves, they are genuinely one card each, individually titled,
  individually URI'd (`spotify:section:0JQ5KskvsLe3uTeisPkjK6` etc. — sequential-looking IDs suggesting a
  backend that generates one micro-section per recommendation rather than one section per reason-cluster).
  Rendered honestly (a full-width row per section) this is 20 rows of single-card content — worse than noise,
  it's a wall.
- The Shorts section contains one **broken entry**: `UnknownType` with `uri: "spotify:user:@:collection"` — a
  malformed pointer clearly *meant* to be "Liked Songs" (the `@` is a self-reference placeholder that didn't
  resolve). This needs graceful handling (fall back to a known "Liked Songs" card by URI pattern, or drop
  `UnknownType` items silently) rather than rendering a blank tile.

**What should merge:** the 20 baseline sections collapse naturally into **one "Because you listen to..." zone**
keyed by the artist/playlist named in each title — group by the named entity (Henry Moodie: 2 items, ROSÉ: 2,
Troye Sivan: 3, Savage Garden: 2, Acda en de Munnik: 2, Jung Seung Hwan: 2, 90's Nederlandstalig: 2, plus 3
singletons: "Made for you" ×5 unlabeled, "Based on your recent listening" ×1) into ~7 labeled sub-groups of
2–5 picks each, one row, `iconName` used as a small glyph per card. That turns 20 dead rows into one living
one.

**What should collapse/demote:** "Recommended Stations" and "Popular radio" are both generic radio walls with
no personalization signal beyond "you've heard of these artists" — near-duplicates of each other in *kind*
(both are Radio-typed playlists, 20 and 50 total respectively) if not content; fold into a single "Radio for
you" module, demote below mood/new/resume.

**What should be promoted:** Recents (currently row 4, no different from any shelf) → should anchor the top
of the page, above the hero even, because it's the literal answer to the #1 job. "Soundtrack your Friday
morning" (time-of-day mood mixes) deserves a visually distinct "right now" treatment, not shelf parity with
"Popular radio."

**What should be dropped or made conditional:** the Shorts rail's broken `UnknownType` item; true duplicates
(New Music Friday NL, "Pop 터지는 팝콘" shelf-vs-section); and — for the desktop redesign specifically —
anything that's a thin reflection of the **left-sidebar library** the user already has one click away (raw
"Liked Songs" as its own promoted card, or a shelf that's just "your playlists" with no added reasoning,
if such existed) should not get Home real estate; Home's job is stuff you *wouldn't* have gone and gotten
yourself.

## 4. Proposed information hierarchy for the new Home

Ordered top to bottom, each zone named with purpose / sources / count / why-copy. Desktop framing throughout:
wide window means shelves can show 6–8 cards at once (not 3–4 like mobile), keyboard/hover affordances matter
more than tap targets, a persistent now-playing bar already owns "what's playing," and the sidebar already
owns "my stuff" — so Home is unapologetically about **surfacing what the user didn't already have open**.

1. **Continue listening** (Resume job). Source: `HomeRecentlyPlayedSectionData` → Recents, reordered/filtered
   to prioritize things that look "left off" (long-form: albums, playlists) over one-off artist glances; 6–8
   cards, no scroll-hint needed at desktop width. Why-copy: none needed — recency itself is the reason;
   optionally a relative label if/when a timestamp field becomes available (flag to backend agent: none exists
   today).
2. **Made for [name] right now** (Play-now + mood-aware, merges jobs 2 & 4). Source: merge "Made For Chris"
   (Daily Mixes, DJ, Discover Weekly) with "Soundtrack your Friday morning" when a time-of-day mood set exists;
   one hero-weighted row, 6–8 cards, why-copy pulled straight from each item's own `description` HTML (already
   present, already names real sibling artists — reuse verbatim, just strip to plain text/chips instead of
   raw anchor tags).
3. **New for you this week** (Catch-up job). Source: "It's New Music Friday!" deduped against "Officiële
   Spotify playlists," Release Radar pinned first; 5–6 cards. Why-copy: "New this Friday" / day-of-week aware
   heading (the server already computes Friday-specific titles — reuse that literal signal for the zone label,
   not just the shelf title).
4. **Because you like [artist]** (Rediscover job — the regrouped baseline). Source: the 20
   `HomeFeedBaselineSectionData`, clustered by named entity as described in §3, into ~5–7 labeled sub-rows (or
   one row per cluster if there are ≥3 clusters with ≥2 items, else fold small clusters into "More
   recommendations for you"); each sub-row 2–5 cards with the cluster name as its own small heading. Why-copy:
   the section's own original title, verbatim ("For fans of Henry Moodie," "More like ROSÉ").
5. **Jump back in** (Rediscover, curated). Source: "Jump back in" as-is; 6–8 cards mixed types. Why-copy: none
   distinct needed — title says it.
6. **Radio & mixes for you** (merged Recommended Stations + Popular radio + "Your top mixes"). Source: three
   shelves folded to one, deduped by playlist URI, capped ~10 cards with a "show more" affordance (desktop
   affords a flyout/expand rather than a full navigate, given window size). Why-copy: "Based on artists and
   songs you play."
7. **Explore** (lowest priority, still present). Source: Shorts rail (post-filtering the broken `UnknownType`)
   + "Officiële Spotify playlists" regional/editorial leftovers not already used above; horizontally scrollable
   pill/chip rail rather than full card grid, since desktop users treat this as a glance-and-skip zone, not a
   destination.

Facet strip (Music/Podcasts/Audiobooks + Following sub-chips) stays as global page filter, unchanged in
concept — but see edge states below for what it does when a facet has no content.

## 5. Edge states

- **First-run / empty library:** no Recents, no baseline "for fans of" clusters (nothing to derive taste from
  yet). Home should fall back to editorial-only zones (New this week, Radio & mixes as genre-generic
  onboarding, Explore) and should not render empty "Continue listening" or "Because you like" zones — hide the
  zone entirely rather than show a skeleton with nothing behind it. This matches the existing hide-when-empty
  discipline implied by the appointments module (falls into quick grid rather than rendering a hole).
- **Offline:** Recents and Jump back in are the only zones backed by *already-downloaded/cached* metadata for
  a returning user — these should render from local cache with play-disabled-until-reconnect affordance on
  streaming-only items; everything server-derived (New this week, Because-you-like clusters, Radio) should
  show its last-successful-fetch snapshot with an explicit "showing saved picks, reconnect to refresh" note
  rather than a spinner that never resolves — no infinite loading state.
- **Loading:** given the desktop window shows 6-8 cards per row live, skeleton rows should reserve the real
  card geometry (the engine's existing card-chrome constants apply) so nothing reflows when data lands;
  Continue Listening and the hero zone should resolve first (cached-first) since they're the highest-value,
  lowest-latency-tolerance zones.
- **Podcasts / Audiobooks facets:** confirmed by full-text search of the captured response — this user has
  essentially zero podcast/audiobook content server-side (facet chips exist, backing sections don't). The
  redesign must treat an empty facet as a **first-class state**, not a bug: show a light "Follow shows to see
  them here" / genre-based podcast starter shelf rather than a blank page when the Podcasts chip is selected.
  Don't assume every facet will always have the same zone set as Music.
- **Friday vs. Monday home:** the response itself proves day-of-week affects section titles/content server-side
  ("It's New Music Friday!", "Soundtrack your Friday morning," a Friday-dated capture) — Monday's response
  would presumably substitute different mood-shelf titles and de-emphasize New Music Friday. The new hierarchy
  should treat zone 3 ("New for you this week") as always-present but re-titled/re-weighted by day (prominent
  Fri–Sun, quietly present but not zone-3-ranked Mon–Thu, in favor of zone 2's mood merge taking that time-of-
  day slot instead on non-release days).
