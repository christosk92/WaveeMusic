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
`scroll`, `frames 60`). With `contrast` on (the default) it presents one frame and reads it back.

**What "lint min=error clean" means.** `summary.error` is 0. `summary` counts only the listed **fail** findings of the
**gate** rules (today's nine), so a report-only rule or a finding under review never breaks it. Read the rest anyway:
`outcomes.review` names what the lint could not decide, and the `todo` rules report the spacing and alignment class.

## Args

| Arg | Default | Meaning |
|---|---|---|
| `rules` | all | Comma list or JSON array. Takes ids (`text.trim`), today's names (`trim`; `clip` = text.clip + overlap.sibling, `type` = type.floor + type.scale), families (`align`, `align.*`) and `all`. Any case. An unknown word fails and lists the names. |
| `scope` | `screen` | `screen`: nodes at least half visible. `content`: what scrolling reaches (below a scroller nothing cuts along its axis; the window still cuts across it, so a panel parked past the edge stays out). `all`: everything captured (the lint before scopes). |
| `root`, `nth` | tree + overlays | Lint only the subtree of the node keyed exactly so (`nth` picks among copies). |
| `min` | `info` | `info`, `warn` (`warning`), `error`: findings below it are neither listed nor counted. |
| `outcomes` | `fail,review` | Which outcomes are LISTED: any of `fail`, `review`, `suppressed`, or `all`. Every outcome is counted either way. |
| `limit` | 100 | Findings listed per rule (after `min` and `outcomes`); a rule cut is named in `truncated`. |
| `baseline` | none | A JSON-lines file of accepted findings (below). A missing file or an entry without `fp` or `reason` fails the command. |
| `save` | none | Write the snapshot the rules saw to this file (JSON; directories created). The reply says where: `saved`. |
| `replay` | none | Lint a saved snapshot instead of the mount. Nothing is read from the page; `root`/`nth` do not apply. |
| `typeFloor` | 11 | `type.floor`: text under this size (DIP) is an error. |
| `targetFloor`, `targetFloorCompact` | 32, 40 | `target.size`: the floors above Compact and at Compact. |
| `gapMin` | 2 | `space.stack`: stacked text blocks closer than this TOUCH (a report); an overlap is a warning. |
| `allowTrim` | none | Key prefixes whose text may ellipsise: a per-call `TrimOk` intent (a near-miss is still reported). |
| `allowDuplicate` | none | Labels (or glyph icon names, `Shuffle`) `duplicate.action` lets repeat. |
| `ignoreUnder` | `shell.titlebar,shell.playerbar` | Key prefixes `duplicate.action` skips. Passing it replaces the default. |

`crossing` takes the same options for the lint it runs after its settle (not `root`, `save` or `replay`).

## The reply

Head (every old key stays): `theme`, `tier`, `size{w,h}`, `route`, `trimSource` (`dwrite`, or `headless-model` under
`--text headless`), `devices`, `replay` (the file, on a replay), `saved` (on `save`), `guide` (`{id, edges, matched,
policies}`; `false` when the theme registers no guide, as Classic: `matched` counts the edges an in-scope node matched),
`scope`, `textRules` (only under the headless text model: the text rules only review), then:

| Key | Meaning |
|---|---|
| `summary{error,warn,info,nodes,edges,inScope}` | Listed fail findings of the gate rules by severity; `nodes` captured, `inScope` judged. |
| `outcomes{fail,review,suppressed}` | Every finding at or above `min`, listed or not. |
| `levels{gate,todo}` | Fail findings at or above `min`, per level. |
| `suppressed{intent,baseline}` | What suppressed them: a guide intent (or an implicit one), or the baseline. |
| `baseline{file,entries,matched}` | With `baseline=`: entries read and entries that matched a finding (an unmatched one may be fixed). |
| `rules{<name>{checked,error,warn,info,review,suppressed,level,ids}}` | Per rule, by the name a finding reports: `checked` is how many things it looked at (0 = nothing to judge, not "clean"). |
| `truncated[]` | Rules cut at `limit`. |
| `findings[]` | Errors first within a rule, then warnings; fails before reviews before suppressions. |

### A finding

```json
{"rule":"align.inset","id":"align.inset","severity":"warn","outcome":"fail","level":"todo",
 "message":"zune-history:* (3 sections) leads at x=24; its region's column line is x=16 (6 leads: shim:*, zune-home-pin:*, zune-home-quick:*, zune-home-daylist): +8",
 "keys":["zune-history:spotify:album:41b0hsQwhVkMc3NQcvB0NF/0/0/1/0/0/0/0/0","..."],
 "measured":{"x":24,"d":8},"expected":{"x":16,"support":6},
 "fp":"align.inset|zune|home|zune-history:*|lead:+8","group":{"count":3,"pattern":"zune-history:*"},
 "rects":[[24,374.58,44,44],"..."],
 "fix":"move the section's extra lead into its plate (padding on the hover plate only) so its ink sits on the column; leave the column alone"}
```

| Field | Meaning |
|---|---|
| `rule` | Today's name for the nine gate rules (`trim`, `clip`, ...), the id for a new rule. Kept so old readers keep working. |
| `id` | The rule id, `family.name`. |
| `severity` | `error`, `warn`, `info`. |
| `outcome` | `fail`: the rule's verdict. `review`: it cannot decide; look at the render (`reason` says why). `suppressed`: an intent or a baseline entry matched (`reason`, `suppressedBy`: `intent` or `baseline`). Suppressed findings are listed only with `outcomes=all` (or `suppressed`). |
| `level` | `gate` (today's nine rules) or `todo` (report-only until the rule earns `gate` on the replay set). |
| `message` | One line with the numbers. |
| `keys` | Up to six member refs: a key, else the dump path of a keyless node (`<nearest key>/0/2`). |
| `rects` | The members' VISIBLE rects `[x, y, w, h]` (window DIP), in the order of `keys`: where to look, and what to box on a render. |
| `measured`, `expected` | Named numbers where they mean something. |
| `fp` | The fingerprint `id|theme|route|pattern|cause`: no sizes and no positions, so one baseline line covers a flaw at every window size. |
| `group` | `count`: how many members the finding collapses (rows, runs, cells); `pattern`: the family (`zune-history:*`, `c.title`, an ink colour). |
| `fix` | One line. It never says to shrink Zune display type, to add, remove or retime motion, or to remove or disable a feature. On Classic it is always "report; do not fix unless asked (Classic)". |
| `reason`, `suppressedBy` | Why it is under review or suppressed. |

## Scope and the visible rect

Every rule judges only nodes in the scope. A node's **visible rect** is its presented rect cut by the window, by every
ancestor that clips its children or scrolls, and by every authored clip (a sticky or collapse cut, a reveal box); its
**on-screen fraction** is the share of its rect that remains. `scope=screen` keeps a node when that share is at least 50 %.
Hidden, parked, exiting and fully transparent subtrees never reach the snapshot. Occlusion (a scrim over the page) is not a
cut. API (`Diagnostics.InspectVisible`):

- `Compute(nodes, windowW, windowH, visible, onScreen, inContent)`: pure, over a depth-first snapshot node list.
- `TryGetVisibleRect(SceneStore, NodeHandle, in RectF window, out RectF visible, out float fraction)`: any live node, UI thread
  (for a click's actionability: under `MinFraction` the target is not on screen).
- `Fraction(rect, visible)`, `InScope(scope, onScreen, inContent)`, `TryParseScope`, `Name(scope)`, `MinFraction` (0.5).

## The rules

Severities below are the rule's own; `level` decides whether a fail counts in `summary`.

### Gate rules (today's nine, by id; the alias is what `rule` says)

**`edge.left` (gutter), `edge.right` (right-edge).** For each `LintEdge` of the active guide, the leftmost (rightmost)
IN-SCOPE node matching its keys, prefixes, `UnderKey` and `GlyphsOnly` is measured; off by more than max(tolerance, 0.5) is
an error (an advisory edge: warn). A parked or scrolled-away copy is never picked; an edge nothing on screen matches is not
checked (`guide.matched` counts the ones that were). Cause: right-of / left-of. FP classes: an edge whose key a view keyed on
something else. Suppression: `Allow` policy, baseline.

**`text.clip` (clip).** Word text whose ink runs past a clipping ancestor: vertical error, horizontal warn. The ink is the
box, a no-wrap run's natural height, and (1.2 x size - line height) / 2 above and below under a tighter explicit line height.
A **scroller judges its own axis by its edges**: no finding where scrolling still reveals the cut text (the fold of a
vertical list) or the edge fades (an edge fade, a scroller's auto fade); a cut at a hard horizontal scroll edge is a warn and
a vertical scroll edge scrolling cannot open (the top at offset 0) an error (cause `scroll-edge:<side>`); a guide `Bleed`
makes it intended. Under the headless text model: review. FP classes: display type whose real ascenders sit inside the 1.2
em guess (the Zune album panorama "5 past the top" with its ascenders intact: true ink metrics are a later engine query).

**`overlap.sibling` (clip).** In-scope siblings with text or a target overlapping by more than 1 x 1 in a parent that is no
ZStack and no scroller: warn. FP classes: a layout overlap with a sibling that does not paint (the Zune album's
`unavailable:bar` under its table header), an overlay drawn without a ZStack.

**`text.trim` (trim).** Text the engine reports trimmed, by class (first match wins; the class is the fingerprint's cause):

| Class | When | Severity, outcome |
|---|---|---|
| `model` | the mount measures with the headless text model | warn, review (and the head's `textRules` says so) |
| `never` | a guide `NeverTrim` covers it | error |
| `overflow` | `Trim=None` and the words spill their box | error |
| `near-miss` | a no-wrap run short by at most max(2 DIP, 1.5 % of its natural width) | warn (checked before any intent) |
| `designed` | a guide `TrimOk`, an `allowTrim` prefix, or MaxLines >= 1 plus an ellipsis inside a repeated key family | info, suppressed; one info line counts them all ("N designed ellipses: zune-surface-shelf x8, ...") |
| `hard-clip` | `Trim=Clip` | warn |
| `single` | a lone ellipsis outside any repeated family | warn, review |

**`type.floor` (type).** One error per size under `typeFloor`, members collapsed. Glyphs are not type; symbols count.
**`type.scale` (type).** One info per (size, line height, weight) in use. A report, never a verdict.

**`target.size` (target).** Interactive, enabled, in-scope targets whose **effective area** is under the floor: under 40 x
40 at window tier Compact is an error, under 32 x 32 above it a warn (`targetFloorCompact`, `targetFloor`). The effective
area is the largest interactive ancestor that runs the SAME click and contains the node (a role-only node hands its click to
the first clickable ancestor); a target filling its interactive parent is the parent's. Exempt: Slider, ScrollBar,
ProgressBar, Hyperlink, and drag bars (thin across, at least 4 x the floor long: splitters, seek lines). Targets that share a
key pattern or a parent are ONE finding listing their sizes (a pager's dots, a crumb trail). Cause: narrow / low.

**`duplicate.action` (duplicate).** The same verb on the same subject offered in two homes on screen (the album band's
Shuffle and its song list's Shuffle). A verb is a Button, ToggleButton, MenuItem or role-less target that is not a content
card (art, or a title and a subtitle, under it) and is not a link; it is matched by role and label, a glyph by its icon name
(`glyph:Shuffle`). The subject is the first `spotify:` entity in its keys, else the page's own entity. A home is the key
family it sits in, so "See all" per shelf, pins' play buttons (each on its own playlist) and one album in two shelves are no
findings. A guide `RepeatOk` makes it intended; `allowDuplicate`, `ignoreUnder` as above.

**`space.stack` (rhythm).** Stacked text blocks in one in-scope column: an overlap (gap under -0.5) is a warn; blocks
closer than `gapMin` TOUCH and are an info (a line box carries its leading, so a title and subtitle 0 apart read fine); the
gaps themselves are an info. Grouped by the column's key pattern: every row's stack of one family is one line.

**`contrast.text` (contrast).** Each in-scope run's declared ink (times its opacity) against the colour most of its box was
rendered in, GROUPED by (ink, ground): one finding per pair with its runs as members (an album page's 38 crumb and meta runs
are one finding). Needs 4.5 : 1, or 3 : 1 for glyphs, 24 DIP and up and bold 18.66 and up. Small type and glyphs are read
without their own anti-aliased ink. A **busy ground** (no colour holds 45 % of a box) on a solid plate is judged against the
plate's declared fill; on art, a gradient or a wash it is a `review` grouped by that ground ("13 runs (3 inks) sit on a busy
ground (a gradient zune.material.wash:*)"), never silence. No frame (`gpu=none`, headless, inside `crossing`): `checked` 0.

### Report-only rules (level `todo`)

Each ships with an algorithm note in its code and a guide policy to declare intent.

**`align.inset`** (warn; port of design-qa `proto/leads.py`). The owner's "history at 24 vs pins at 16" class.
1. Members: in-scope ink (text, image, gradient, visible fill or border), not a glyph, not mid-animation, not in a horizontal
   scroller, not a PLATE (an ink node other ink sits on), not placed by centring or end alignment (the node or an ancestor up
   to its section, by its own AlignSelf/JustifySelf or its parent's AlignItems/Justify, or its text alignment), and not an
   end-aligned column (cells of one family and cell path whose right or centre edges agree while their lefts do not).
2. Groups: (scroll region x backdrop).
3. Leads: members with nothing of the group wholly to their left on an overlapping line.
4. The column line: the most common lead x shared by at least 3 leads.
5. A lead 4 < |d| <= 16 DIP off the line is a finding when its section (nearest keyed node) has nothing on the line; one
   finding per (pattern, d rounded to 1 DIP).
Suppression: guide `Hang` or `Indent` with a value within 2 DIP of d. FP classes: an optical hang without a policy, a deliberately
indented sub-list. Near misses (0.5-4 DIP) are a later rule (`align.near-miss`).

**`align.baseline`** (warn, outcome review; port of `proto/columns.py` row offsets). For each repeated key family (3 or more
keyed instances in scope): (a) a keyed CELL inside a row of another repeated family: its first text's offset from the row's
top, one cell per row, grouped by (cell family, row family); (b) a ROW family's own texts by cell path. A group of 3 or more
whose offsets spread over 0.5 DIP drifts, unless their bottoms or centres agree (a bottom-anchored or centred caption whose
line count varies). The rows themselves are never measured against the list (the prototype's false positive). Offsets are
line-box tops, not baselines: the outcome is review. Suppression: `Allow` policy with rule `align.baseline`.

**`space.scale`** (info). Every DECLARED gap (with 2 or more children), padding side and margin side that is not 0, on an
in-scope node with ink under it, must be on the scale: 2, a multiple of 4 up to 64, or a value of the active definition's
`Spacing.*` (XXS..XXXL, Gutter, PageWide, PageNarrow, Card, Inner, StackS/M/L), the style's `R.Space` (SectionGap,
SectionGapWide, GridGap) or `R.Shape` radii. A control-internal value (3, 5, 6, 7, 9, 10, 11, 14, 15) under a control with a
role is the control's. Grouped by (value, family pattern); the message names the nearest spacing token. Declared values, not
measured gaps (45-53 % of measured insets are off the grid by centring and line boxes). Suppression: `OffScaleOk`. FP classes:
values a board draws outside the tokens (Zune's 6, 10, 14, 18, 22), hairline nudges (1 DIP), positions a record derives
(a timeline's slot offsets past 64).

**`text.placeholder`** (error for `[missing.key]` and `{argument}`, warn for NaN, Infinity, null, undefined as a word).
On-screen word runs that are not content: what `Loc.Get` renders for a missing key and `Loc.Format` for a missing argument.
Grouped by (kind, pattern). Suppression: `Allow`.

**`key.hygiene`** (warn). In-scope keys that break selectors, one finding per (class, key family): a control character
(the route host key `2\x1fhome\x1f`), a trailing `:` (`zune-home-scroll:`), a run id (`#` + 3 or more digits), a tier, form or
column-count word (`compact`..`huge4k`, `form`, `3col`, `cols4`, `2up`: the owner rule), and the same key on two or more
nodes that only `nth` tells apart. Not a duplicate: a copy nested in another copy, a repeated family's rows keyed alike, and
copies that each sit under a keyed ancestor of their own (`root=<that key>` finds them). Suppression: `Allow`.

## Guides and intents

A theme describes what only it knows as data, through `IThemeLintGuide` (registered with `ThemeInspection.RegisterLintGuide`):

- `Edges(LintContext)`: `LintEdge`s for `edge.left` / `edge.right`.
- `Policies(LintContext)` (default: none): `LintPolicy(Intent, Prefix, Reason, Value, Rule)`, intents for every node whose key
  or an ancestor's starts with `Prefix`: `TrimOk`, `NeverTrim`, `Hang` (Value DIP, negative = left), `Indent`, `Bleed`,
  `OffScaleOk`, `RepeatOk`, `Allow` (any finding of `Rule`). The reason must let a reviewer check it: name the board.

Precedence: guide intent, then baseline, then the rule's default. A matched intent makes a finding `suppressed` and counts
it in `suppressed.intent`; it never hides it from `outcomes=all`.

| Theme | Edges | Policies |
|---|---|---|
| Zune | `ZuneLintGuide`: titlebar.lead, band.first-pivot, page.column, page.title; Small and Standard also band.last-action, page.column right, playbar.last-glyph | `TrimOk` on `zune-home-pin:`, `zune-history:`, `zune-surface-shelf`, `zune-shelf-card:`, `zune-tile:`, `zune-cluster-row:`, `zune-chart:` (Home captions ellipsise on one line, Z-Home and Z-Home-Compact); `Hang` on `zune.detail.panorama` by the record's `PanoramaNudgeX` (Z-Album) |
| WinUI | `WinUiLintGuide`: page.title on the content inset | none |
| Classic | no guide: `guide:false`, no edges, no intents; findings are reported, not fixed | |

## Baselines (accepted findings)

`baseline=<file>`: JSON lines, one accepted finding each; `#` lines and blank lines are comments.

```json
{"fp":"align.inset|zune|home|zune-history:*|lead:+8","rule":"align.inset","reason":"history rows sit 8 in: the Zune home lane owns the fix","by":"agent","date":"2026-10-20"}
```

A finding whose `fp` matches is `suppressed` (`suppressedBy: baseline`, `reason` = the entry's reason, by and date), counted in
`suppressed.baseline` and listed with `outcomes=all`. `fp` and a non-empty `reason` are mandatory (the command fails
otherwise). Fingerprints hold no sizes or positions, so an entry holds across window sizes and re-mounts; `baseline.matched`
below `entries` means an accepted finding no longer occurs (fixed, or off screen at this size). Never baseline an `error`
without the owner, and never use a baseline to hide a feature someone switched off.

## Saved snapshots and replay

`lint save=<file>` writes the snapshot the rules saw: window, theme, route, text source, the guide's edges and policies, the
theme's spacing scale, and every captured node with its rect, flags, scroll range and fades, authored clip, declared layout,
paint, click identity and text run (with the ground the rendered frame put under it, so `contrast.text` replays without
pixels). Derived fields (`on`, `family`, `cell`) are written for a reader and recomputed on replay. A Zune Home 400 snapshot is
about 0.26 MB, a Classic or WinUI Home at 1440 about 0.6 MB.

`lint replay=<file>` runs the same pure rules on it: any scope, rules, baseline or `outcomes`. A replay of a live lint's
snapshot gives the same findings, fingerprints and counts. Uses: validating a rule on pinned pages while the branch moves
(the six reference mounts), showing a rule on a doctored copy (one node moved, one text replaced) after a lane fixed the
real flaw, and auditing a page without mounting it. Today the server checks for a mount before `lint` runs, so a replay
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
a fresh server, `lint min=warn`:

| Mount | Before: warn+ | After: gate warn+ (summary) | + todo warn+ fails | review | before: findings on wholly off-screen nodes |
|---|---|---|---|---|---|
| Zune Home 400 | 19 | 0 | 4 (align.inset 1, key.hygiene 3) | 5 | 13 |
| Zune Home 1440 | 33 | 4 | 3 | 4 | 3 |
| Zune album 1440 | 66 | 12 | 2 | 4 | 8 |
| Zune artist 1440 | 33 | 9 | 2 | 2 | 5 |
| Classic Home 1440 | 77 | 6 | 2 | 4 | 39 |
| WinUI Home 1440 | 85 | 4 | 2 | 3 | 44 |

After the change no finding names a wholly off-screen node at `scope=screen`. The real flaws the old lint missed or drowned
are reported: the 0.71 DIP near-miss on the Zune Home chart caption (below the fold at 400 x 860: `scope=content`, or scroll to
it), the Zune Home 400 history inset (+8, `align.inset`), the Zune artist album art inset (+8), the album title-baseline drift
(`align.baseline`), the album's band-vs-list Shuffle and More, and one contrast finding for the 38-run token pair. Labels per
finding (true / false / intended): `C:/WAVEE/scratch/inspect-p1/i3/labels.txt`.

## Known limits

- The text ink is a 1.2 em guess, not glyph ink metrics: tight display type can read as clipped (the Zune album panorama).
- `align.baseline` compares line-box tops (review only) until the engine reports first baselines.
- `space.scale` knows the token scale, not the boards': Zune's board values (6, 10, 14, 18, 22) report as info until the guide
  declares them.
- Contrast reads one rendered frame: text over art and gradients is a review, not a measurement.
- A replay needs a mounted server until the dispatcher lets `lint replay=` run unmounted.
