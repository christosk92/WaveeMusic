# Typography, casing and CTA convergence — implementation plan

## Context

`docs/plans/wavee/typography-voice-audit.md` found Wavee speaking three type voices plus a fourth CTA
language. Six scanners across the whole app then quantified it and corrected two of the audit's claims.

What is actually true:

| | |
|---|---|
| `TextEl.Size =` sites | 723 (549 literal, 13 in ternaries, 12 computed) across 75 files |
| Distinct literal sizes ≤ 30 | 25, against an engine ramp of 8 rungs |
| `Weight = 700` sites | 40 across 19 files; **3** are the sanctioned `Design.cs` aliases |
| Other weights | 650 ×7 · 300 ×11 · 350 ×5 · 800 ×2 · **540** ×1 · **500** ×1 |
| Real `.ToUpper`-on-a-localized-string violations | **4**, in 2 files |
| ALL-CAPS phrase shouts in `en-US.json` | 25 (+12 real acronyms) |
| Title Case values violating the glossary | 92 |
| Hand-rolled button families | ~20, with 4 diameters for "play this collection" |

Two corrections to the audit worth carrying forward:

1. **The CTA problem is not "web pill next to Fluent squares."** `Controls.Cta` appears on detail/album/artist/home
   heroes and nowhere in shell chrome, the utility screens or `Track.Table.Chrome.cs`. Every other surface hand-rolls
   its own circle/square/pill. The defect is the *absence of a shared primitive*, not two competing ones.
2. **The ramp doesn't fit the app.** 11 (61 text sites, 21 files) and 13 (65 text sites, 28 files) are not
   indiscipline — they are rungs the engine ramp does not have. A dense music client needs a tighter small end than
   Fluent's document ramp gives.

**Nothing can regress-guard any of this today.** `DesignTypeRampTests` asserts alias numbers over 11 of 20 aliases;
`LocPackTests` asserts placeholders only. The Eyebrow test says it outright — sentence case is *"a parity item rather
than a fact."*

**Interaction with the pending drafts.** Commit `2f059309` drafted translations for the 1,626 missing nl/ko keys;
they sit unimported in `ops/loc/work/`. The overlap with this plan is partial: `detail.watchOfficialVideo`,
`detail.badge.album`, `detail.preReleaseUnitDays`, `library.sort.recents` and `whatsNew.you` are in those packets,
while `home.dailyMix`, `podcast.continueListening`, `nav.history.mostVisited`, `sidebar.section.nowPlaying` and
`videoOverride.customLabel` return nothing in `drafts-nl-all.json`. No urgency in either direction — the drafts are
machine-generated and re-running `draft` for the rows whose English moved is a tooling loop. Doing W1 before an
import is merely tidier than doing it after.

## Decisions taken

| Fork | Decision |
|---|---|
| The 11/13 rung gap | **Add the missing rungs.** Four new `Design.Type` aliases, ~126 sites repoint, zero visual change. |
| CTA geometry | **Record the contract, defer the pixels.** Fix only the two worst; document the rest. Target voice is **Zune / Fluent / WinUI**, not the Spotify web capsule. |
| Word rails | **One vocabulary, keep the Zune rails.** Same words everywhere; rails stay lowercase, pills stay sentence case. |
| Sequencing | **All waves start now**, in parallel with the D-plan backend queue. |

## Conflict surface

The 0.3 worktree has 12 uncommitted files. Three are in this plan's path and are **edit-with-care** —
re-read before touching, never revert a hunk that isn't ours:

- `src/apps/Wavee/Platform/Design.cs` — W4 adds aliases here
- `src/apps/Wavee/Shell/Shell.Chrome.cs`, `Shell/Shell.Masthead.UI.cs` — not touched by any wave, leave alone
- `src/apps/Wavee/Entities/Playlist.UI.cs` — only touched if W4 migration reaches it; defer it to last

Per repo rule, implementation is parallel subagents on **disjoint files**; only the orchestrator builds, tests or
launches — and not between waves unless asked, because the owner's running Debug instance locks Debug output.

---

## W1 — the copy

**Files:** `src/apps/Wavee/assets/loc/en-US.json`, `ops/loc/keep-english.json`, affected rows in `ops/loc/work/drafts-{nl,ko}-*.json`.
Touches no C# file. Can land immediately.

1. **25 phrase shouts → sentence case.** `detail.badge.{album,compilation,episode,explicit,single}`,
   `detail.preReleaseUnit{Days,Hours,Minutes,Seconds}`, `detail.row.chartNew`, `detail.watchOfficialVideo`,
   `videoOverride.customLabel`, `home.dailyMix`, `nav.history.mostVisited`, `notifications.release.*`,
   `play.{live,goLive}`, `tray.live`, `podcast.continueListening`, `whatsNew.you`, `detail.artist`.
   Keep the 12 real acronyms (BPM, ISRC, SHA-256, CD, LCD, VU, PPM, EP, OK, 12″ LP).
   `"DAILY MIX"` is in `keep-english.json` — it stays *English*, which is not the same as staying *shouted*;
   update that entry to `"Daily mix"`.
2. **92 Title Case values → sentence case**, except the glossary's fixed terms (`Wavee`, `Spotify`, `Liked Songs`,
   `Your Library`) and real proper nouns (`Spotify Connect`, `iPod Classic`, `Microsoft Store`).
   Start with the twinned contradictions: `sidebar.section.nowPlaying` "Now Playing" → "Now playing"
   (twins `player.nowPlaying`, `settings.nowPlaying.title`).
3. **Intra-key contradictions — but not the rail ones.** `podcast.tabs.*` (about/chapters/transcript/comments) is a
   **Zune rail** under the W7 decision: lowercase there against `podcast.reader.*`'s `Chapters`/`Transcript`/`Comments`
   is the same legitimate split as `library.rail` vs `library.sort`, and it stays.
   What does get fixed is the strays *outside* the rail namespaces: bare `podcast.about` = "about" (a lowercase stray
   directly under `podcast.*`, not in `podcast.tabs.*`), `podcast.latest`, `podcast.new`, `podcast.trailer`,
   `podcast.older`/`newer`, and the four-way "played" split (`podcast.filter.played` "Played" / `podcast.played`
   "played" / `podcast.markAllPlayed` "mark all played" / `podcast.menu.markPlayed` "Mark as played").
   Delete the exact-duplicate `podcast.filter.inProgress` / `podcast.inProgress` pair.
4. **Units.** One system: lowercase abbreviated (`hr`, `min`, `sec`). Kills the `DAYS`/`HRS`/`MIN`/`SEC` set and its
   inconsistent truncation. Move `detail.saveCountCompact` and `podcast.episodesCount` onto ICU plurals, matching
   `library.nAlbums`.
5. **Re-draft** the nl/ko rows whose English changed — `pack`/`draft` for those keys only. Cheaper before an import
   than after, but either order works.

### What W1 does to the copy — measured, not assumed

| | before | after |
|---|---|---|
| Leaf keys | 2,598 | 2,598 (no key is deleted except the one exact duplicate) |
| Distinct values | 2,231 | **2,194** (−37, −1.7%) |
| Keys sharing a value | 604 (14.1%) | ~676 (15.6%) |

35 clusters in the file differ **only** by case. **13 are the Zune rails** and are protected by the W7 decision —
`library.rail.albums` "albums" stays distinct from `nav.albums` "Albums", `library.rail.creator` "artist" from the
twelve keys saying "Artist". **22 genuinely merge**: About · ALBUM · ARTIST · EPISODE · EXPLICIT · Followers ·
Latest · Local Files · Monthly listeners · More like this · MOST VISITED · NEW · New Folder · Next · Now Playing ·
Played · Previous · Resume · Today · Unplayed · YOU · "You're all caught up".

The shape matters more than the count. Nearly every shout collapses onto a word the file already carries many times:
`detail.badge.album` "ALBUM" → "Album", which **10 other keys already say**; `detail.artist` "ARTIST" → "Artist",
already under **12 keys**. The caps were not carrying variety — they were the only thing making those keys look
distinct. The voice does not flatten; the flatness was already there and the shouting hid it.

So the true after-effect is **exposed key redundancy**: 12 keys = "Artist", 10 = "Album", 10 = "All", 8 = "Play",
7 = "Podcast". **Do not merge them.** One key per surface is deliberate insulation — Dutch and Korean may need
different renderings for a column header, a jump-list kind and a fallback name. Instead W2 *reports* the clusters so
a reviewer sees them before translating one English word twelve different ways.

## W2 — the gate that keeps W1 true

**Files:** `ops/loc/Wavee.LocPack/Casing.cs` (new), `ops/loc/Wavee.LocPack/Program.cs`, `src/apps/Wavee.Tests/LocCasingTests.cs` (new).

The rules go in a pure class and the *rules* get unit-tested — the `SetupGating` / `ReleaseNotesValidation` pattern.
The JSON file itself is checked by a tool command, never by a test, so the **no-source-text-tests** rule is untouched.
`LocCatalog.FlattenJson(string)` (`ops/loc/Wavee.LocPack/Catalog.cs:24`) already takes raw JSON — reuse it, don't
write a parser.

```csharp
public enum LocVoice { Sentence, TitleCase, AllCaps, Lowercase, Acronym, Ambiguous }

public static class LocCasing
{
    /// <summary>Classifies ONE value's voice. ICU {…} blocks are stripped first; a single capitalized word is
    /// Ambiguous (it is a button label or a sentence, and the value alone cannot say which).</summary>
    public static LocVoice Classify(string value);

    /// <summary>Every key whose voice violates the glossary, given the acronym allowlist and the eyebrow key
    /// patterns that are permitted to be lowercase.</summary>
    public static IReadOnlyList<(string Key, string Value, LocVoice Voice, string Why)>
        Violations(IReadOnlyDictionary<string, string> flat, IReadOnlyCollection<string> acronyms,
                   IReadOnlyCollection<string> lowercaseKeyPrefixes);

    /// <summary>Keys in one namespace that do the same job with different casing — the podcast.episodes /
    /// podcast.episodesCount class of bug, found by stem rather than by hand. A cluster whose variants are all
    /// explained by a sanctioned lowercase namespace is NOT a contradiction: 13 of the 35 case-only clusters in
    /// the file are the Zune rails doing their job.</summary>
    public static IReadOnlyList<(string A, string B)> Contradictions(IReadOnlyDictionary<string, string> flat,
                                                                     IReadOnlyCollection<string> lowercaseKeyPrefixes);

    /// <summary>Keys that share one English value. NOT an error — one key per surface is deliberate insulation,
    /// because nl/ko may need different renderings for a column header, a jump-list kind and a fallback name.
    /// Reported so a reviewer can SEE that 12 keys say "Artist" before returning 12 different Dutch words.</summary>
    public static IReadOnlyList<(string Value, IReadOnlyList<string> Keys)>
        SharedValueClusters(IReadOnlyDictionary<string, string> flat, int minKeys = 2);
}
```

Wire it as `dotnet run --project ops/loc/Wavee.LocPack -- lint` alongside the existing `pack`/`draft`/`qa`/`import`
commands (`Program.cs:24-28`). The lowercase-prefix allowlist is the recorded Zune set: `library.rail.`,
`library.scope.`, `library.readerSort.`, `podcast.badge.`, `podcast.cadence.`, `podcast.tabs.`, `whatsNew.issue.`,
and the `home.*Eye` eyebrows.

## W3 — the four caps mechanisms

**Files:** `src/apps/Wavee/Entities/Track.Table.Chrome.cs`, `src/apps/Wavee/Shell/Deck.Faces.cs`.

- `Track.Table.Chrome.cs:632` `Caps(label, classic) => label.ToUpper(CurrentUICulture)` and the inline twin at `:733`
  uppercase `Loc.Get(Strings.Detail.Column.*)`. Delete `Caps`; classic mode keeps its 11 px rung and
  `Design.Type.EyebrowTracking`, loses the transform.
  *Note for the design system:* `HLabel` (`:637`) is the only place in the app that pairs `EyebrowTracking` with real
  caps — which is what 30/1000 em is for. Removing the caps here means the tracking should go too, or the label reads
  gappy. Drop `CharSpacing` on the classic arm in the same change.
- `Deck.Faces.cs:946` and `:1201` — `Strings.Resolve(badge).ToUpperInvariant()` on the stream-format badge, the same
  line copy-pasted across the cassette VFD and WMP faces. The deck faces are a sanctioned skin-fidelity exception for
  *hardcoded* device chrome; a **localized** string is not device chrome. Resolve without the transform.

## W4 — the missing rungs, then the migration

**Files:** `src/apps/Wavee/Platform/Design.cs` (⚠ uncommitted), `src/apps/Wavee.Tests/DesignTests.cs`, then ~40 UI files in batches.

### W4a — add the rungs

```csharp
/// <summary>Badges, counts, timestamps, chart marks — the rung BELOW caption. The engine ramp stops at 12/16
/// because it is a document ramp; a dense media row needs one step under it, and 61 sites had already invented
/// it as a raw 11f with no line height.</summary>
public static TextEl MicroMeta(string s) => Ui.Caption(s) with { Size = 11f, LineHeight = 15f };

/// <summary>Credits, list metadata, drawer body — between caption and body. 65 sites had invented it as 13f.</summary>
public static TextEl DenseMeta(string s) => Ui.Body(s) with { Size = 13f, LineHeight = 18f };

/// <summary>A title in a dense list (drawer rows, chart rows) — DenseMeta's weight pair.</summary>
public static TextEl DenseTitle(string s) => Ui.BodyStrong(s) with { Size = 13f, LineHeight = 18f };

/// <summary>A sheet or card heading, between BodyStrong and Subtitle.</summary>
public static TextEl SheetTitle(string s) => Ui.BodyLarge(s) with { Size = 16f, LineHeight = 22f, Weight = 600 };
```

The class doc (`Design.cs:1145-1160`) must change with them: the three-part contract stands, but "the engine ramp
carries the pair" becomes "eight engine rungs plus four app rungs, and no thirteenth."

### W4b — the tests that make it real

`DesignTests.cs` needs three edits, and one of them is a rewrite:

- add the four aliases to `OnRampAliases` (`:26-30`) so they inherit the size/line-height/weight contract and the
  400/600 assertion;
- close the 9-alias hole — `ArtistDisplay`, `ArtistTitle`, `ArtistCompactTitle`, `PivotLabel`, `NpvLyric`, `StatHero`,
  `PickQuote`, `FoldTitle`, `NowPlayingTitle` are untested for their own numbers;
- rewrite `PivotLabel_is_the_one_off_ramp_rung` (`:86-93`). Its premise — one off-ramp rung, a third is a
  regression — is false the moment these land. It becomes an enumeration: *these* rungs are sanctioned, with their
  numbers, and a new one has to be added here with its reason.

### W4c — migrate

~126 text sites on 11/13, plus the half-steps that collapse onto the new rungs (12.5 ×15, 11.5 ×11, 13.5 ×7,
10.5 ×4, 9.5 ×3). Batch by file, disjoint per subagent. Representative paths:
`Entities/Artist.UI.Chart.cs`, `Entities/Artist.Discography.cs`, `Entities/Album.Pane.cs`, `Entities/Show.Pane.cs`,
`Entities/Track.Drawer.cs`, `Shell/Sidebar.UI.Rows.cs`, `Shell/Sidebar.UI.LibraryV3.cs`, `Shell/Shell.UI.cs`,
`Screens/Settings.UI.*.cs`.

Two whole files define **parallel undocumented ramps** and should be converted wholesale rather than site by site:
`Screens/Setup.UI.Runtime.cs:601-800` (`RuntimeText` 13/18, `RuntimeLead` 16/22, `RuntimeStatus` 14/20-600 …, ~18
call sites) and `Screens/Diagnostics.UI.cs` (~90 literal sites, exactly one alias call in 1615 lines).

**Three traps for the migrating agents:**
- *Looks compliant, isn't* — `Sidebar.UI.LibraryV3.cs:1462` starts from the `Caption` alias then overrides `Size`
  with a literal. Grepping `Design.Type` scores it clean.
- *Dead overrides* — `Detail.UI.cs:1266`, `:1607` restate an alias's own defaults as literals.
- *Invisible to a size grep* — `Size = compact ? 24f : 32f` and `Size = 14f * 0.68f` (renders 9.52) never match a
  literal pattern. 13 ternaries and 12 computed sites exist; find them by reading, not grepping.

**Out of scope by design:** `Shell/Deck.UI.cs` and `Shell/Deck.Faces.cs` reference `Design.Type` zero times on
purpose (device-skin fidelity), and `Platform/Controls.Words.cs` is a sanctioned second ramp.

## W5 — the six named fixes

Small, disjoint, high value.

| File:line | Fix |
|---|---|
| `Entities/Artist.Reader.cs:1252` | The artist masthead hand-rolls `Size = compact ? 24f : 32f, Weight = 600, CharSpacing = -10f` on the UI face. Call `Design.Type.ArtistCompactTitle` (32/40/**700**/−12/DisplayFace) — the alias written for exactly this role, which this file never calls. `Artist.Reader.cs` uses no header alias anywhere in 1930 lines. |
| `Platform/Controls.Podcast.cs:272` | 11/14 at weight **700** with `CharSpacing = 90` — 3× `EyebrowTracking`, a caps-era eyebrow bypassing `Eyebrow`. Route to `Design.Type.Eyebrow`. |
| `Shell/Sidebar.Customizer.UI.cs:1908-1929` | Raw C# glyph enum identifiers render as row labels (`"FavoriteStar"`, `"RefineSparkle"`). Add loc keys. |
| `Entities/Artist.Discography.cs:251-262` | Decade band labels built by English string concatenation, never touching `Loc`. The one real i18n gap found. |
| `Entities/Track.Drawer.cs:439` + `:649` | Weights **540** and **650** four lines apart, two escapes for one semibold intent. Both → `DenseTitle`. |
| `Shell/Deck.Faces.cs:116` | Weight **500**, a tenth weight. Inside the sanctioned skin exception — record it in the deck-faces doc rather than changing pixels. |

## W6 — the CTA contract (documented), two fixes (shipped)

**Doc:** a new section in `docs/plans/wavee/wavee-0.3-ui/00-design-system.md` §6, carrying the census and the rules.

The target voice is **Zune / Fluent / WinUI** — the design system should say so. The 36-tall `Radii.Full` capsule at
Bold 700 with hand cursor and hover 1.04 (`Platform/Controls.Cta.cs:104-118`) is the one element that is neither:
it is a web button, and §6.1 currently blesses it. Record it as **under review**, with the Fluent alternative stated,
and leave its pixels alone this round.

Record but do not change: `Controls.FollowButton` (`Controls.cs:809`, a second implementation of the capsule, as its
own comment admits), `PreSaveButton` (`:750`, 4 px radius), `SaveButton` (`:733`, a circle on non-media rows),
`Controls.Podcast.DoorPlay` (`:366`, a circle on a flat card), the Concert filter-pill family, the `User.Facts`
lens pill, the `Detail` 40 px FAB, the discography 26/28 px circles, `Browse.UI.cs:66` using raw `999f` instead of
the `Radii.Full` token, and the notification panel's three chips at three radii (`Shell.UI.cs:1586, 1913, 1926`).

**Ship two:**
1. **One primary play.** `Shell/Shell.PlayerBar.UI.cs:1100-1119` renders a plate-less bare glyph; `Shell/Stage.UI.cs:156-169`
   renders a filled accent circle. Same verb, both mounted when the Stage opens over the bar. Converge on the
   Fluent/WinUI transport treatment. The player bar's own comment notes the docs once claimed a filled circle that
   was never painted — resolve the doc and the pixels together.
2. **One diameter ladder.** `Controls.Cta.PlayFab` defaults to 44 (`Controls.Cta.cs:190`) and is mounted at 36
   (`Artist.Reader.cs:1186`) and 32 (`:1919`), with a hand-rolled 26 at `Artist.Discography.cs:1244`. Collapse to the
   three sanctioned rows.

Also record the fact that broke the weight census: **the media pill's boldness is `Bold = true`, not `Weight = 700`**
(`Controls.Cta.cs:112`), so every Play capsule is invisible to a weight grep. `Bold = true` exists in exactly two
files app-wide.

## W7 — one vocabulary for the rails

**Files:** `src/apps/Wavee/assets/loc/en-US.json`, plus the rail/pill consumers in `Entities/User.UI.cs`,
`Entities/User.cs`, `Shell/Sidebar.UI.LibraryV3.cs`.

`library.sort.*` (Title), `library.rail.*` (lowercase) and `library.readerSort.*` (lowercase) are three vocabularies
for five concepts, and two diverge in **wording**: `creator` → "artist", `releaseDate` → "year". Collapse to one word
set. Rails keep lowercase (the recorded Zune decision in `library-rework-implementation.md`); pills keep sentence
case. Same words on both.

| Concept | Pill | Rail |
|---|---|---|
| recents | Recents | recents |
| alphabetical | Alphabetical | alphabetical |
| creator | Artist | artist |
| releaseDate | Release date | release date |
| recentlyAdded | Recently added | recently added |

Same treatment for `podcast.sort.*` (Title) vs `library.readerSort.*` (lowercase), which contradict each other
across features on newest/oldest.

---

## Issues, CHANGELOG, commits

Repo rule: every fix references its issue — the CHANGELOG bullet ends with a **trailing** `(#n)` (a mid-sentence
`(#n)` is ignored by `Get-ChangelogEntryRefs`), the commit body carries `Fixes #n`, and
`ops/release/wavee-release.ps1:509` fails hard on a mismatch. One issue per wave, not per site.
**Ask before any `gh` call** (`github-triage` skill).

## Verification

Owner-run, not orchestrator-run — the repo rule is no builds between waves, and the running Debug instance locks
Debug output (MSB3021/27).

1. `dotnet run --project ops/loc/Wavee.LocPack -- lint` — zero violations after W1, and the shared-value report
   showing the expected clusters (12 × "Artist", 10 × "Album", 10 × "All"). This is the gate that makes W1 permanent.
   Expect distinct values to land at **2,194** and the 13 Zune-rail clusters to still be there — if they vanished,
   the rails were flattened and W7 was mis-implemented.
2. `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj` — `LocCasingTests` and the rewritten `DesignTypeRampTests` green.
3. `dotnet build Wavee.slnx` **and** `-c Release` — the engine's diag gates arm differently per configuration.
4. `dotnet run --project src/apps/Wavee -- --fake` — offline pass over album, artist, show, library, queue, settings,
   diagnostics. W4 claims zero visual change; the four rungs are pixel-identical to the literals they replace, so any
   visible movement is a migration bug.
5. Deep-link spot checks against the live-verification workflow for the W5/W6 surfaces (artist masthead, podcast door,
   player bar over Stage).

---

# Appendix — the evidence

Six scanners over disjoint file sets: shell chrome (22 files) · utility screens (16) · shared controls, menus and
palette · secondary entity pages (31) · the loc file and a transform census · a ninth-file group (Track.UI*,
Artist.*, the panes). Every claim below carries a `file:line` that was read, not grepped.

## A. What the audit had right

Voices 1–3 are confirmed: authored Zune lowercase, Fluent sentence case as policy, ALL-CAPS leftovers in the JSON.
The `library.rail.*` dual-key decision, the `Controls.Words` second ramp, and the deck faces' total exemption are
all real and deliberate.

## B. What the audit had wrong

**1. `Controls.Cta` is not "parked next to Fluent squares".** It appears on detail/album/artist/home heroes and
**nowhere** in the 22 shell-chrome files, the 16 utility files, or `Track.Table.Chrome.cs`. Instead ~20 surfaces
hand-roll their own. Sharpest symptom: the same verb in two opposite languages, mounted together —
`Shell.PlayerBar.UI.cs:1100` (plate-less glyph) vs `Stage.UI.cs:156` (filled accent circle).

**2. The `Cta.cs` *family* does reach list rows**, even though the pill does not: `Controls.PlayFab` at 36
(`Artist.Reader.cs:1186`) and 32 (`:1919`), against its own 44 default (`Controls.Cta.cs:190`), plus a hand-rolled
26 (`Artist.Discography.cs:1244`). Four diameters, one job.

**3. `Platform/Notify.cs` is not where the toast type lives** — it, `Tray.cs` and `Update.Host.cs` contain no
`TextEl` at all (pure Win32/WinRT shells). The rendered toasts are in `Shell/Shell.Overlays.UI.cs`.

**4. `Entities/Palette.cs` is the cover-colour palette**, not the command palette (`Shell/Shell.Palette.cs`).

## C. Census

| Measure | Value |
|---|---|
| `TextEl.Size =` sites | 723 — 549 literal, 13 ternary, 12 computed |
| Files with a literal size | 75 |
| Distinct literal sizes ≤ 30 | 25 (ramp has 8) |
| 11 px text sites / 13 px text sites | 61 in 21 files / 65 in 28 files |
| `Weight = 700` | 40 across 19 files; 3 sanctioned (`Design.cs:1274, 1280, 1286`) |
| `Weight = 650 / 300 / 350 / 800` | 7 / 11 / 5 / 2 |
| `Weight = 540` | `Track.Drawer.cs:439` — comment admits "0.2.9's own cut, ported as pixels" |
| `Weight = 500` | `Deck.Faces.cs:116` |
| `Bold = true` | 2 files only — **the media pill's weight is invisible to a weight grep** |
| Alias adoption | `Eyebrow` 34 files · `TrackMeta` 18 · `PageHero` 14 · `TrackTitle` 13 · then a cliff to 1–2 files for 9 of the 20 aliases |

## D. Transforms — 4 real violations, not a class of them

Of ~45 `.ToUpper`/`.ToLower` call sites app-wide, only four operate on a localized string:
`Track.Table.Chrome.cs:632` and `:733`; `Deck.Faces.cs:946` and `:1201`. Everything else is catalog data, enum
names, hex, URIs, search-index keys or developer-console tokens.

Two that look like violations and are not: `Controls.Words.cs:311` lowercases Spotify topic titles (catalog data,
scoped by its own doc-comment), and `Show.UI.cs:76` lowercases a `CultureInfo` month name, which is culture-formatted
rather than `Loc`-sourced.

A transform census is structurally blind to authored caps — `Track.UI.cs:469` ("NEW") and `:784` ("EXPLICIT") shout
with no transform to count.

## E. Traps for whoever implements this

- **Looks compliant, isn't.** `Sidebar.UI.LibraryV3.cs:1462` starts from the `Caption` alias, then overrides `Size`
  with a literal. A `Design.Type` adoption grep scores the file clean.
- **Dead overrides.** `Detail.UI.cs:1266`, `:1607` restate an alias's own defaults as literals — harmless until the
  alias moves.
- **Invisible to a size grep.** `Artist.Reader.cs:1252` is `Size = compact ? 24f : 32f`; `Track.UI.Bound.cs:470` is
  `Size = 14f * 0.68f` and renders 9.52. Read, don't grep.
- **The alias cannot save a caps string.** `Shell.History.UI.cs:230` is a correct `Design.Type.Eyebrow` that still
  shouts, because the caps are in the JSON. The ramp work and the copy work are independent.
- **Tracking was built for caps.** `Track.Table.Chrome.cs:637` is the only site pairing `EyebrowTracking` with real
  caps — which is what 30/1000 em is for. `Eyebrow` applies that same tracking to sentence case everywhere else.

## F. Whole files off the ramp

`Screens/Diagnostics.UI.cs` — one alias call in 1615 lines. `Screens/ReleaseNotes.UI.cs` — zero.
`Screens/Setup.UI.Runtime.cs:601-800` — a parallel private ramp (`RuntimeText` 13/18, `RuntimeLead` 16/22,
`RuntimeStatus` 14/20-600 …) across ~18 call sites. `Entities/Artist.Reader.cs` — no header alias in 1930 lines.

## G. Deliberate, leave alone

`Shell/Deck.UI.cs` + `Deck.Faces.cs` (zero `Design.Type`, device-skin fidelity) · `Platform/Controls.Words.cs`
(sanctioned second ramp) · `library.rail.*` lowercase (recorded decision) · `detail.trackFacts.major/minor`
(follows a tonic) · the `home.*Eye` eyebrows (the glossary's one exception) · `Rail.UI.cs:419`, which carries an
in-code comment actively defending sentence case.

## H. Enforcement gap

`DesignTypeRampTests` asserts alias numbers over 11 of 20 aliases and nothing about call sites. `LocPackTests`
asserts placeholders, ICU classification and round-trip fidelity — nothing about casing. The Eyebrow test states the
limit in its own comment: sentence case is *"a parity item rather than a fact"*. Under the repo's no-source-text-tests
rule, the ramp is enforceable only by API shape; the copy is enforceable by a data lint, which is W2.
