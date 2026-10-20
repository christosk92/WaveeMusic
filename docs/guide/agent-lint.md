# The `--inspect` lint: rules, findings, baselines and saved snapshots

`lint` is the design-QA verb of the agent server (`docs/guide/agent-inspect.md` covers the server, its protocol and every
other verb). It reads the mounted scene, judges **what a person can see**, and returns findings an agent can act on: each
with a rule id, an outcome, a level, a fingerprint, its members collapsed into one group, and a one-line fix.

Code: `src/apps/Wavee/Screens/Diagnostics.Inspect.Lint*.cs` (the pure rules, `.Lint.Scene.cs` the capture and the reply,
`.Lint.Store.cs` saved snapshots and baselines) and `Diagnostics.Inspect.Visible.cs` (visible rects). Theme intents:
`src/apps/Wavee/Theming/Composition/ThemeLintGuide.cs`; the Zune and WinUI guides in their themes' `Structure/Rules`.

## Running it

Put the commands in a file (a JSON line needs no shell quoting) and run `tools/inspect.py` on a copy of your build:

```text
mount w=400 h=860 theme=zune route=home
frames 90
{"cmd":"lint","args":{"min":"warn"}}
```

`python -I tools/inspect.py --exe <your run copy>/Wavee.exe --findings -f home.txt` prints a head line and one line per
finding. The raw reply (without `--findings`) carries everything below. Lint reads the scene as it is: settle first (after a
`scroll`, `frames 60`); `animating.scrolling` above 0 in the head says a scroller was still moving in the frame it read. With
`contrast` on (the default) it presents one frame and reads it back.

**What "lint min=error clean" means.** `summary.error` is 0. `summary` counts the **fail** findings (and the report lines) of
the **gate** rules (today's nine) at or above `min`, listed or not: `outcomes=` and `limit=` choose what is listed, never what
is counted. A report-only (`todo`) rule or a finding under review never breaks it. Read the rest anyway: `outcomes.review`
names what the lint could not decide, and the `todo` rules report the spacing and alignment class.

## Args

| Arg | Default | Meaning |
|---|---|---|
| `rules` | all | Comma list or JSON array of strings. Takes ids (`text.trim`), today's names (`trim`; `clip` = text.clip + overlap.sibling, `type` = type.floor + type.scale), families (`align`, `align.*`) and `all`. Any case. An unknown word, a non-string entry or an empty list fails and lists the names. |
| `scope` | `screen` | `screen`: what is on screen (below). `content`: what scrolling reaches (below a scroller nothing cuts along its axis; the window still cuts across it, so a panel parked past the edge stays out). `all`: everything captured (the lint before scopes). |
| `root`, `nth` | tree + overlays | Lint only the subtree of the node keyed exactly so (`nth` picks among copies). The subtree starts from the cut its live ancestors put on it, so it is judged as the page shows it. Not with `replay`. |
| `min` | `info` | `info`, `warn` (`warning`), `error`: findings below it are neither listed nor counted. |
| `outcomes` | `fail,review,report` | Which outcomes are LISTED: any of `fail`, `review`, `suppressed`, `report`, or `all`. Every outcome is counted either way. |
| `limit` | 100 | Findings listed per reported rule name (`clip` holds two ids; the cap applies once); a name cut is in `truncated`. A whole number from 1. |
| `baseline` | none | A JSON-lines file of accepted findings (below). A missing file or an entry without `fp` or `reason` fails the command. |
| `save` | none | Write the snapshot the rules saw to this file (JSON; directories created). The reply says where: `saved`. |
| `replay` | none | Lint a saved snapshot instead of the mount. Nothing is read from the page; `root`/`nth` fail with it. |
| `typeFloor` | 11 | `type.floor`: text under this size (DIP) is an error. A number above 0. |
| `targetFloor`, `targetFloorCompact` | 32, 40 | `target.size`: the floors above Compact and at Compact. Numbers above 0. |
| `gapMin` | 2 | `space.stack`: stacked text blocks closer than this TOUCH (a report); an overlap is a warning. 0 or more. |
| `allowTrim` | none | Key prefixes whose text may ellipsise: a per-call `TrimOk` intent (a near-miss and a hard clip are still reported). |
| `allowDuplicate` | none | Labels (or glyph icon names, `Shuffle`) `duplicate.action` lets repeat. |
| `ignoreUnder` | `shell.titlebar,shell.playerbar` | Key prefixes `duplicate.action` skips. Passing it replaces the default. |

A number that is not a finite number in range fails the command and names the arg; nothing falls back silently.
`crossing` takes the same options for the lint it runs after its settle (not `root`, `save` or `replay`;
`InspectLintScene.RejectSnapshotArgs` is the helper a verb uses to refuse those two).

## The reply

Head (every old key stays): `theme`, `tier`, `size{w,h}`, `route`, `trimSource` (`dwrite`, or `headless-model` under
`--text headless`), `devices`, `replay` (the file, on a replay), `saved` (on `save`), `guide` (`{id, edges, matched,
policies}`; `false` when the theme registers no guide, as Classic: `matched` counts the edges an in-scope node matched),
`scope`, `modal` (`{scrim, covered}`, only when a dialog's scrim covers the page), `animating` (`{nodes, scrolling}`: nodes
with live animation tracks and scrollers still moving in the frame read), `textRules` (only under the headless text model:
the text rules only review), then:

| Key | Meaning |
|---|---|
| `summary{error,warn,info,nodes,edges,inScope}` | Fail findings and report lines of the gate rules at or above `min`, by severity, listed or not; `nodes` captured, `inScope` judged. |
| `outcomes{fail,review,suppressed,report}` | Every finding at or above `min`, listed or not. |
| `levels{gate,todo}` | Fail findings at or above `min`, per level (report lines are not verdicts and are not counted here). |
| `suppressed{intent,baseline}` | What suppressed them: a guide intent (or an implicit one, a designed ellipsis), or the baseline. |
| `baseline{file,entries,considered,matched,duplicates?}` | With `baseline=`: entries read; `considered`: entries whose rule ran (only those can match); `matched`; `duplicates`: entries whose `fp` an earlier entry already holds. `matched` under `considered` means an accepted finding no longer occurs (fixed, or off screen at this size). |
| `rules{<name>{checked,error,warn,info,review,suppressed,report?,level,ids}}` | Per rule, by the name a finding reports: `checked` is how many things it looked at (0 = nothing to judge, not "clean"); the severity counts are fail findings and report lines. |
| `truncated[]` | Rule names cut at `limit`, once each. |
| `findings[]` | Per rule name: errors first, then warnings; fails before reviews before suppressions. |

### A finding

```json
{"rule":"align.inset","id":"align.inset","severity":"warn","outcome":"fail","level":"todo",
 "message":"zune-history:* (3 sections) leads at x=24; its region's column line is x=16 (the guide's page.title; 21 leads on it: shim:*, zune-home-pin:pl:*, zune-home-quick:history, zune-home-daylist): +8",
 "keys":["zune-history:spotify:album:41b0hsQwhVkMc3NQcvB0NF/0/0/1/0/0/0/0/0","..."],
 "measured":{"x":24,"d":8},"expected":{"x":16,"support":21},
 "fp":"align.inset|zune|home|zune-history:*|lead:+8","group":{"count":3,"pattern":"zune-history:*"},
 "rects":[[24,374.58,44,44],"..."],
 "fix":"move the section's extra lead into its plate (padding on the hover plate only) so its ink sits on the column; leave the column alone"}
```

| Field | Meaning |
|---|---|
| `rule` | Today's name for the nine gate rules (`trim`, `clip`, ...), the id for a new rule. Kept so old readers keep working. |
| `id` | The rule id, `family.name`. |
| `severity` | `error`, `warn`, `info`. |
| `outcome` | `fail`: the rule's verdict. `review`: it cannot decide; look at the render (`reason` says why). `suppressed`: an intent or a baseline entry matched (`reason`, `suppressedBy`: `intent` or `baseline`); listed only with `outcomes=all` (or `suppressed`). `report`: an info line that states what is there (the type styles in use, the gaps of a stack, the designed ellipses), not a verdict. |
| `level` | `gate` (today's nine rules) or `todo` (report-only until the rule earns `gate` on the replay set). |
| `message` | One line with the numbers. When findings of one rule share a fingerprint they are ONE finding: the first one's message ends "(and N more like it)" and `group.count` counts them all. |
| `keys` | Up to six member refs: a key, else the dump path of a keyless node (`<nearest key>/0/2`). |
| `rects` | The members' VISIBLE rects `[x, y, w, h]` (window DIP), in the order of `keys`: where to look, and what to box on a render. A member with nothing on screen (`scope=content`, `all`) gives its whole rect. |
| `measured`, `expected` | Named numbers where they mean something. |
| `fp` | The fingerprint `id|theme|route|pattern|cause`: no sizes and no positions. One baseline line covers a flaw at every window size where its cause and its family are the same; a cause that names a size class (`narrow`/`low` of a target, the type size of `type.floor`) can differ between sizes, and so can the family a node belongs to when fewer than three of its rows are on screen. |
| `group` | `count`: how many members the finding collapses (rows, runs, cells); `pattern`: the family (`zune-history:*`, `c.title`, an ink colour). |
| `fix` | One line. It never says to shrink Zune display type, to add, remove or retime motion, or to remove or disable a feature. On Classic it is always "report; do not fix unless asked (Classic)". |
| `reason`, `suppressedBy` | Why it is under review or suppressed. |

### Key families

Many rules group by a node's **family**, read from its key (`InspectLint.FamilyOfKey`): the key up to a `#`, `|` or control
character, split at `:`. The first segment heads the family; it stops before the first segment that is an id or a name (a
digit, a capital, a space, longer than 24 characters) or `spotify` (an entity id follows), and, in a key of three or more
segments, before its last segment (the slot). So `zune-history:spotify:track:x` is `zune-history`, `zs:link:privacy` is
`zs:link`, `settings:section:Links` is `settings:section`, and an entity key `spotify:page:x` is `spotify:page`; `zs:title`
and `zs:body` (parts of one page) stay two families. A family seen 3 times or more in scope is a REPEATED family; its
`pattern` is the family plus `:*` when keys carry more (`zune-history:*`). `duplicate.action` reads a verb's HOME by the
key's first segment only (`trail:moreby` and `trail:similar` are one home).

## Scope and the visible rect

Every rule judges only nodes in the scope. A node's **visible rect** is its presented rect cut by the window, by every
ancestor that clips its children or scrolls, and by every authored clip (a sticky or collapse cut, a reveal box); its
**on-screen fraction** is the share of its rect that remains. `scope=screen` keeps a node when that share is at least 50 %,
or when its visible part fills at least half of the viewport it sits in (its **cover**: a page column or a section taller than
two screens is on screen and is judged, so the edge, overlap and spacing rules keep seeing it). Hidden, parked, exiting and
fully transparent subtrees never reach the snapshot; a box with no area that clips (a collapsed reveal) is captured, so it
hides what is under it. A **modal scrim** (an interactive fill over 95 % of the window, a dialog's light-dismiss backdrop)
covers everything painted before it: those nodes are out of scope at `screen` and `content` and the head says `modal`. API
(`Diagnostics.InspectVisible`):

- `Compute(nodes, windowW, windowH, visible, onScreen, inContent[, reach, cover])`: pure, over a depth-first snapshot node
  list; a root (`Parent` -1) starts from its `SeedScreen`/`SeedContent` cut.
- `CutChildren(rect, clips, scrollAxis, clipRect, ref screen, ref content)`: the one step both halves run per node.
- `AncestorCut(SceneStore, NodeHandle, in RectF window, out screen, out content)`: the cut a live node's ancestors put on it
  (what `root=` seeds its root with).
- `TryGetVisibleRect(SceneStore, NodeHandle, in RectF window, out RectF visible, out float fraction)`: any live node, UI thread:
  the engine's `SceneStore.VisibleWindowRect` (transforms, presented clip sizes, authored clips: what hit-testing agrees with)
  cut by the window and by any scroller that does not clip; the fraction is of the painted rect (for a click's actionability:
  under `MinFraction` the target is not on screen).
- `Fraction(rect, visible)`, `InScope(scope, onScreen, inContent, cover)`, `TryParseScope`, `Name(scope)`, `MinFraction` (0.5;
  a fraction 0.001 under it still counts, so a row exactly half visible survives a saved snapshot's round trip).

## The rules

Severities below are the rule's own; `level` decides whether a fail counts in `summary`.

### Gate rules (today's nine, by id; the alias is what `rule` says)

**`edge.left` (gutter), `edge.right` (right-edge).** For each `LintEdge` of the active guide, the leftmost (rightmost)
matching node whose measured EDGE is in scope (on screen and not cut: a page column taller than the screen still shows its
left edge) is measured, matching its keys, prefixes, `UnderKey` and `GlyphsOnly`; off by more than max(tolerance, 0.5) is an
error (an advisory edge: warn). A parked, covered or scrolled-away copy is never picked; an edge nothing matches is not
checked (`guide.matched` counts the ones that were). Cause: right-of / left-of. FP classes: an edge whose key a view keyed on
something else. Suppression: `Allow` policy, baseline.

**`text.clip` (clip).** Word text whose ink runs past a clipping ancestor: vertical error, horizontal warn. The ink is the
box, a no-wrap run's natural height, and (1.2 x size - line height) / 2 above and below under a tighter explicit line height
(the **ink guess**). When the laid-out box fits the clip and only the ink guess runs past it, the finding is a `review`
(cause `top:ink-guess`, reason "ink guess, not glyph metrics"): display type whose real ascenders sit inside (the Zune album
and settings panoramas). A **scroller judges its own axis by its edges**: no finding where scrolling still reveals the cut text
(the fold of a vertical list) or the edge fades (an edge fade, a scroller's auto fade); a cut at a hard horizontal scroll edge
is a warn and a vertical scroll edge scrolling cannot open (the top at offset 0) an error (cause `scroll-edge:<side>`); a
guide `Bleed` makes it intended. Under the headless text model: review.

**`overlap.sibling` (clip).** In-scope siblings with text or a target overlapping by more than 1 x 1 in a parent that is no
ZStack and no scroller: warn. FP classes: an overlay drawn without a ZStack. (A bar collapsed to no height under a clip no
longer counts: the capture keeps the collapsed clip, so the bar is off screen.)

**`text.trim` (trim).** Text the engine reports trimmed, by class (first match wins; the class is the fingerprint's cause):

| Class | When | Severity, outcome |
|---|---|---|
| `never` | a guide `NeverTrim` covers it | error |
| `overflow` | `Trim=None` and the words spill their box | error |
| `near-miss` | a no-wrap run short by at most max(2 DIP, 1.5 % of its natural width) | warn (checked before any intent) |
| `hard-clip` | `Trim=Clip` (whatever the family or a guide says: the boards draw an ellipsis) | warn |
| `designed` | an ELLIPSIS under a guide `TrimOk`, an `allowTrim` prefix, or MaxLines >= 1 inside a repeated key family | warn, suppressed (counted in `suppressed.intent` at `min=warn`); one report line counts them all ("N designed ellipses: zune-surface-shelf x8, ...") |
| `single` | a lone ellipsis outside any repeated family | warn, review |
| `model` | the mount measures with the headless text model and the trim is not a designed ellipsis | warn, review (the head's `textRules` says so) |

**`type.floor` (type).** One error per size under `typeFloor`, members collapsed; the message names their families, the
fingerprint is the size alone (pattern `type`). Glyphs are not type; symbols count.
**`type.scale` (type).** One report line per (size, line height, weight) in use. Never a verdict.

**`target.size` (target).** Interactive, enabled, in-scope targets whose **effective area** is under the floor: under 40 x
40 at window tier Compact is an error, under 32 x 32 above it a warn (`targetFloorCompact`, `targetFloor`). The effective
area is the largest interactive ancestor that runs the SAME click and contains the node (a role-only node hands its click to
the first clickable ancestor); a target filling its interactive parent is the parent's. Exempt: Slider, ScrollBar,
ProgressBar, Hyperlink, and drag bars (thin across, at least 4 x the floor long: splitters, seek lines). Targets are grouped
by the key pattern of what they sit in (their parent's nearest key): the band's Play and Shuffle, a pager's dots and a crumb
trail are one finding each, listing their sizes. Cause: narrow / low / narrow+low.

**`duplicate.action` (duplicate).** The same verb on the same subject offered in two homes on screen (the album band's
Shuffle and its song list's Shuffle). A verb is a Button, ToggleButton, MenuItem or role-less target that is not a content
card (art, or a title and a subtitle, under it) and is not a link; it is matched by role and label, a glyph by its icon name
(`glyph:Shuffle`). The subject is the first `spotify:` entity in its keys, else the row or card it sits in (an instance of a
repeated family that is a click target or carries art: each track row's More acts on its own track), else the page's own
entity. A home is the first segment of its family's key, so "See all" per shelf, pins' play buttons and one album in two
shelves are no findings. A guide `RepeatOk` makes it intended; `allowDuplicate`, `ignoreUnder` as above.

**`space.stack` (rhythm).** Stacked text blocks in one in-scope column: an overlap (gap under -0.5) is a warn; blocks
closer than `gapMin` TOUCH and are a report line (a line box carries its leading, so a title and subtitle 0 apart read
fine); the gaps themselves are a report line. Grouped by the column's key pattern: every row's stack of one family is one line.

**`contrast.text` (contrast).** Each in-scope run's declared ink (times its opacity) against the colour most of the
VISIBLE part of its box was rendered in (a run cut by a scroller, a sticky header or a bar is read where it shows), GROUPED by
(ink, ground): one finding per pair with its runs as members (an album page's 39 crumb and meta runs are one finding). Needs
4.5 : 1, or 3 : 1 for glyphs, 24 DIP and up and bold 18.66 and up. Small type and glyphs are read without their own
anti-aliased ink. A **busy ground** (no colour holds 45 % of a box) on a solid plate is judged against the plate's declared
fill; on art, a gradient or a wash it is a `review` grouped by that ground ("13 runs (3 inks) sit on a busy ground (a gradient
zune.material.wash:*)"); a run on screen the frame gave no ground for is a `review` too ("no ground read"): never silence. No
frame (`gpu=none`, headless, inside `crossing`): `checked` 0. FP class: under `root=`, a plate outside the subtree is not seen,
so a run on it reads as "on the page".

### Report-only rules (level `todo`)

Each ships with an algorithm note in its code and a guide policy to declare intent.

**`align.inset`** (warn; port of design-qa `proto/leads.py`). The owner's "history at 24 vs pins at 16" class.
1. Members: ink (text, image, gradient, visible fill or border) that scrolling reaches (the WHOLE scroll content), not a
   glyph, not mid-animation, not in a horizontal scroller, not a PLATE (an ink node other ink sits on), not a ground as wide as
   its scroll region (a full-bleed fill or image), not placed by centring or end alignment (the node or an ancestor up to its
   section, by its own AlignSelf/JustifySelf or its parent's AlignItems/Justify, or its text alignment), and not an end-aligned
   column (cells of one family and cell path whose right or centre edges agree while their lefts do not).
2. Groups: (scroll region x backdrop).
3. Leads: members with nothing of the group wholly to their left on an overlapping line.
4. The column line: on the page's own ground, the guide's `Column` edge (Zune and WinUI: the page title's x) when at least 3
   leads sit on it; else the LEFTMOST x at least 3 leads share. Read over the whole content, it does not move while the page
   scrolls, and inset rows never outvote the headers they sit under.
5. A lead IN SCOPE 4 < |d| <= 16 DIP off the line is a finding when its section (nearest keyed node) has no lead on the line;
   one finding per (pattern, d rounded to 1 DIP). The message names where the line came from.
Suppression: guide `Hang` or `Indent` with a value within 2 DIP of d. FP classes: an optical hang without a policy, a
deliberately indented sub-list. Near misses (0.5-4 DIP) are a later rule (`align.near-miss`).

**`align.baseline`** (warn, outcome review; port of `proto/columns.py` row offsets). For each repeated key family (3 or more
keyed instances in scope): (a) a keyed CELL inside a row of another repeated family: its first text's offset from the row's
top, one cell per row, grouped by (cell family, row family); (b) a ROW family's own texts by cell path. A group of 3 or more
whose offsets spread over 0.5 DIP drifts, unless their bottoms or centres agree (a bottom-anchored or centred caption whose
line count varies), or the "rows" differ in height by more than max(2 DIP, 10 %) (sections of a page, not rows of a list: a
first settings section without its top rule). The rows themselves are never measured against the list (the prototype's false
positive). Offsets are line-box tops, not baselines: the outcome is review. Suppression: `Allow` policy with rule
`align.baseline`.

**`space.scale`** (info). Every DECLARED gap (with 2 or more children), padding side and margin side that is not 0, on an
in-scope node with ink under it, must be on the scale: 2, a multiple of 4 up to 64, or a value of the active definition's
`Spacing.*` (XXS..XXXL, Gutter, PageWide, PageNarrow, Card, Inner, StackS/M/L), the style's `R.Space` (SectionGap,
SectionGapWide, GridGap) or `R.Shape` radii. A control-internal value (3, 5, 6, 7, 9, 10, 11, 14, 15) under a control with a
role is the control's. Grouped by (value, family pattern); the message names the nearest spacing token. Declared values, not
measured gaps (45-53 % of measured insets are off the grid by centring and line boxes). Suppression: `OffScaleOk`. FP classes:
values a board draws outside the tokens (Zune's 6, 10, 14, 18, 22), hairline nudges (1 DIP), positions a record derives
(a timeline's slot offsets past 64).

**`text.placeholder`** (error for a missing loc key or argument, warn for a leaked value). On-screen word runs:
- a missing key as `Loc.Get` renders it: `[a.b]` with a loc key's shape inside (two or more dot-separated segments, each a
  lowercase letter then letters, digits, `_` or `-`: `[home.quickPins]`; never `[Pt.2]` or `[Intro.Outro]`);
- a missing argument as `Loc.Format` renders it: `{name}`, a camelCase identifier (never `{Remix}`);
- a leaked value: `NaN`, `null`, `undefined` or `-Infinity` as the whole text, or any of them and `Infinity` next to a number
  (`NaN:NaN`, `NaN%`, `Infinity / 3`). "Infinity" alone is a title.
In catalogue content (text inside a row or card of a repeated family that is a click target or carries art) only a bracketed
loc key that is the WHOLE text counts. Grouped by (kind, pattern). FP class: a catalogue title that is nothing but a bracketed
lowercase dotted word. Suppression: `Allow`.

**`key.hygiene`** (info: it reports selector hygiene, not a flaw anyone sees). In-scope keys that break selectors, one finding
per (class, key family): a control character (the route host key `2\x1fhome\x1f`), a trailing `:` (`zune-home-scroll:`), a run
id (`#` + 3 or more digits), a tier, form or column-count word in the key's static words (`compact`..`huge4k`, `form`, `3col`,
`cols4`, `2up`: the owner rule; the words before the first name or entity, so `zune-cluster:1:Huge Form 2up` carries a name,
not a tier word), and the same key on two or more nodes that only `nth` tells apart. Not a duplicate: a copy nested in another
copy, a repeated family's rows keyed alike, and copies that each sit under a keyed ancestor of their own (`root=<that key>`
finds them). Suppression: `Allow`.

## Guides and intents

A theme describes what only it knows as data, through `IThemeLintGuide` (registered with `ThemeInspection.RegisterLintGuide`):

- `Edges(LintContext)`: `LintEdge`s for `edge.left` / `edge.right`. An edge with `Column: true` is the page column's ink line
  `align.inset` anchors on.
- `Policies(LintContext)` (default: none): `LintPolicy(Intent, Prefix, Reason, Value, Rule)`, intents for every node whose key
  or an ancestor's starts with `Prefix`: `TrimOk`, `NeverTrim`, `Hang` (Value DIP, negative = left), `Indent`, `Bleed`,
  `OffScaleOk`, `RepeatOk`, `Allow` (any finding of `Rule` whose members ALL lie under the prefix). The reason must let a
  reviewer check it: name the board. A board draws one page: `LintContext.Route` (`home`, `album`) scopes a page's policies.

Precedence: guide intent, then baseline, then the rule's default. A matched intent makes a finding `suppressed` and counts
it in `suppressed.intent`; it never hides it from `outcomes=all`.

| Theme | Edges | Policies |
|---|---|---|
| Zune | `ZuneLintGuide`: titlebar.lead, band.first-pivot, page.column, page.title (`Column`); Small and Standard also band.last-action, page.column right, playbar.last-glyph | On Home only: `TrimOk` on `zune-home-pin:`, `zune-history:`, `zune-surface-shelf`, `zune-shelf-card:`, `zune-tile:`, `zune-cluster-row:`, `zune-chart:` (Home captions ellipsise on one line, Z-Home and Z-Home-Compact). Everywhere: `Hang` on `zune.detail.panorama` by the record's `PanoramaNudgeX` (Z-Album) and on `zs:title` by `ZuneSettingsRules.PanoramaBleed` (Z-Settings) |
| WinUI | `WinUiLintGuide`: page.title on the content inset (`Column`) | none |
| Classic | no guide: `guide:false`, no edges, no intents; findings are reported, not fixed | |

## Baselines (accepted findings)

`baseline=<file>`: JSON lines, one accepted finding each; `#` lines and blank lines are comments.

```json
{"fp":"align.inset|zune|home|zune-history:*|lead:+8","rule":"align.inset","reason":"history rows sit 8 in: the Zune home lane owns the fix","by":"agent","date":"2026-10-20"}
```

A finding whose `fp` matches is `suppressed` (`suppressedBy: baseline`, `reason` = the entry's reason, by and date), counted in
`suppressed.baseline` and listed with `outcomes=all`. `fp` and a non-empty `reason` are mandatory (the command fails
otherwise). An entry's rule is its `fp`'s first part: `baseline.considered` counts the entries whose rule ran, `matched` those
that met a finding, `duplicates` the entries whose `fp` an earlier one already holds. `matched` below `considered` means an
accepted finding no longer occurs (fixed, or off screen at this size). Never baseline an `error` without the owner, and never
use a baseline to hide a feature someone switched off.

## Saved snapshots and replay

`lint save=<file>` writes the snapshot the rules saw: window, theme, route, text source, the guide's edges and policies, the
theme's spacing scale, and every captured node with its rect, flags, scroll range, fades and motion, authored clip, the cut a
`root=` subtree started from, declared layout, paint, click identity and text run (with the ground the rendered frame put under
it, so `contrast.text` replays without pixels). Numbers are written at round-trip float precision. Derived fields (`on`,
`covered`, `family`, `cell`) are written for a reader and recomputed on replay. A Zune Home 400 snapshot is about 0.27 MB, a
Classic or WinUI Home at 1440 about 0.5-0.6 MB.

`lint replay=<file>` runs the same pure rules on it: any scope, rules, baseline or `outcomes`. A replay of a live lint's
snapshot gives the same findings, fingerprints and counts (checked on the Zune album at 1440: 59 of 59). A file that is not a
lint snapshot, is newer than the reader, has no window size, names a parent that does not come before its child, or holds a
rect that is not finite fails with the reason. Uses: validating a rule on pinned pages while the branch moves (the six
reference mounts), showing a rule on a doctored copy (one node moved, one text replaced, a policy added) after a lane fixed
the real flaw, and auditing a page without mounting it. Today the server checks for a mount before `lint` runs, so a replay
needs any mount (`mount w=400 h=300` is enough; the replay reads nothing from it).

## Fix-hint rules (every `fix` line keeps them)

- Never shrink Zune display type to clear a trim, clip or cramped finding, even where a board draws it smaller: wrap, grow
  the box, bleed with a fade, or move the secondary content. Auto-fit (`TextEl.MinSize`) is the same shrink.
- Never add, remove or retime motion to clear a finding without the owner's OK.
- Never remove, hide or disable a feature, or drop content, to clear a finding or match a board.
- Classic findings are reported, not fixed, unless the owner asked for Classic work.
- Fix causes, not nodes: numbers come from the surface's tier record or the theme's roles, never a literal tuned to one size;
  never move a floor or widen an allowance to make a real finding go away.

## Validation (2026-10-20, the six reference mounts)

Zune Home 400 x 860 and 1440 x 960, Zune album al2 and artist ar2 at 1440 x 960, Classic and WinUI Home at 1440 x 960, each in
a fresh server, `lint min=warn` (warn+ = warn or error findings with outcome fail):

| Mount | Before: warn+ | After: gate warn+ (summary) | After: every level | review | suppressed.intent |
|---|---|---|---|---|---|
| Zune Home 400 | 19 | 0 | 1 (align.inset zune-history +8) | 5 | 2 |
| Zune Home 1440 | 33 | 4 | 4 | 4 | 4 |
| Zune album 1440 | 66 | 10 | 10 | 5 | 0 |
| Zune artist 1440 | 33 | 9 | 10 (+ align.inset album art +8) | 2 | 0 |
| Classic Home 1440 | 77 | 6 | 6 | 4 | 2 |
| WinUI Home 1440 | 85 | 4 | 4 | 3 | 1 |

No finding names a wholly off-screen node at `scope=screen` (0 of 318 across both lints of every mount; before, 112 of 313
did). The real flaws the old lint missed or drowned are reported: the 0.71 DIP near-miss on the Zune Home chart caption (below
the fold at 400 x 860: `scope=content`, `root=zune-chart:top50 scope=content`, or scroll to it), the Zune Home 400 history
inset (+8) and, below the fold, the release and cluster rows (+8), the Zune artist album art inset (+8), the album
title-baseline drift (`align.baseline`), the album's band-vs-list Shuffle and More, and one contrast finding for the 39-run
token pair. Labels per finding (true / false / intended): `C:/WAVEE/scratch/inspect-p1/i3/fix/labels.txt`.

## Known limits

- The text ink is a 1.2 em guess, not glyph ink metrics: a clip only the guess makes is a review until the engine reports ink.
- `align.baseline` compares line-box tops (review only) until the engine reports first baselines.
- `space.scale` knows the token scale, not the boards': Zune's board values (6, 10, 14, 18, 22) report as info until the guide
  declares them.
- Contrast reads one rendered frame: text over art and gradients is a review, not a measurement; under `root=` the plates
  outside the subtree are not seen.
- A replay needs a mounted server until the dispatcher lets `lint replay=` run unmounted.
- `tools/inspect.py --findings` prints a finding's severity, not its outcome or level: a review or a todo finding reads like a
  plain warn there; the raw reply has both.
