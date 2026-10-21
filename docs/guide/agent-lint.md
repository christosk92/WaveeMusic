# The `--inspect` lint: rules, findings, baselines and saved snapshots

`lint` is the design-QA verb of the agent server (`docs/guide/agent-inspect.md` covers the server, its protocol and every
other verb). It reads the mounted scene, judges **what a person can see**, and returns findings an agent can act on: each
with a rule id, an outcome, a level, a fingerprint, its members collapsed into one group, and a one-line fix.

Code: `src/apps/Wavee/Screens/Diagnostics.Inspect.Lint*.cs` (the pure rules: `.Lint.cs` the gate rules, `.Lint.Layout.cs`,
`.Lint.Content.cs` and `.Lint.Ink.cs` the todo rules; `.Lint.Scene.cs` the capture and the reply, `.Lint.Store.cs` saved
snapshots and baselines) and `Diagnostics.Inspect.Visible.cs` (visible rects). Theme intents:
`src/apps/Wavee/Theming/Composition/ThemeLintGuide.cs`; the Zune and WinUI guides in their themes' `Structure/Rules`.

## Running it

Put the commands in a file (a JSON line needs no shell quoting) and run `tools/inspect.py` on a copy of your build:

```text
mount w=400 h=860 theme=zune route=home
frames 90
{"cmd":"lint","args":{"min":"warn"}}
```

`python -I tools/inspect.py --exe <your run copy>/Wavee.exe --findings -f home.txt` prints a head line and one line per
finding (`[warn ] align.baseline (review, todo): ...`: a finding that is not a fail of a gate rule says what it is). Add
`--gate error` for an exit code: 1 when a gate rule's fail finding at or above the severity was listed (reviews, suppressed
findings and report lines never count; `--gate-todo` counts the todo rules' fails too), 2 on any `ok:false`. The raw reply (without `--findings`) carries everything below. Lint reads the scene as it is: settle first (after a
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
| `outcomes` | `fail,review,report` | Which outcomes are LISTED: any of `fail`, `review`, `suppressed`, `report`, or `all`. Every outcome is counted either way. An empty list fails. |
| `limit` | 100 | Findings listed per reported rule name (`clip` holds two ids; the cap applies once); a name cut is in `truncated`. A whole number, clamped to 1..10000 as it always was (`limit=0` lists one); `1.5` or `abc` fails. |
| `baseline` | none | A JSON-lines file of accepted findings (below). A path string; a missing file or an entry without `fp` or `reason` fails the command. |
| `save` | none | Write the snapshot the rules saw to this file (JSON; directories created). The reply says where: `saved`. A path string (`save=true` fails). |
| `replay` | none | Lint a saved snapshot instead of the mount. A path string. Nothing is read from the page; `root`/`nth` fail with it. |
| `typeFloor` | 11 | `type.floor`: text under this size (DIP) is an error. A number above 0. |
| `targetFloor`, `targetFloorCompact` | 32, 40 | `target.size`: the floors above Compact and at Compact. Numbers above 0. |
| `gapMin` | 2 | `space.stack`: stacked text blocks closer than this TOUCH (a report); an overlap is a warning. 0 or more. |
| `allowTrim` | none | Key prefixes whose text may ellipsise: a per-call `TrimOk` intent (a near-miss and a hard clip are still reported). An empty list means none. |
| `allowDuplicate` | none | Labels (or glyph icon names, `Shuffle`) `duplicate.action` and `text.duplicate` let repeat. An empty list means none. |
| `ignoreUnder` | `shell.titlebar,shell.playerbar` | Key prefixes `duplicate.action` and `text.duplicate` skip. Passing it replaces the default; `ignoreUnder=[]` (or `ignoreUnder=`) clears it. |

A number that is not a finite number in range, an empty `rules` or `outcomes`, and a path that is not a non-empty string fail
the command and name the arg; nothing falls back silently. Every arg above is declared in the server's verb table
(`Diagnostics.Inspect.Protocol.cs`, the marked lint-arg list, plus `root`, `nth`, `save` and `replay` on `lint` itself), so
`help verb=lint` and `help schema=json` list them and any other name (or a miscased one: `Scope=content`) is refused before
the lint runs (`lint rule=clip`: "unknown arg 'rule' for lint (did you mean 'rules'?)"). The table also hands every list arg
to the lint as a JSON array of strings (`[2024,Play]` keeps 2024 as text) and checks `rules` and `outcomes` words against
their choices. `crossing` takes the same options for the lint it runs after its settle, except `root`, `nth`, `save` and
`replay`: the table does not declare them there, so they are refused as unknown args.

## The reply

Head (every old key stays): `theme`, `tier`, `size{w,h}`, `route` (the page the lint SAW, read from its keep-alive slot key:
mid-navigation that is still the old page), `navigating` (only when the shell already names another route: the page it is
going to), `trimSource` (`dwrite`, or `headless-model` under `--text headless`), `devices`, `replay` (the file, on a replay),
`saved` (on `save`), `guide` (`{id, edges, matched, policies}`; `false` when the theme registers no guide, as Classic:
`matched` counts the edges an in-scope node matched), `scope`, `modal` (`{scrim, covered}`, only when a dialog's scrim covers
the page), `occluded` (only when above 0: nodes an opaque fill painted after them hides, a menu's plate or a pinned header),
`animating` (`{nodes, subtree, scrolling}`: nodes with live animation tracks, the nodes in those subtrees, and scrollers still
moving in the frame read), `textRules` (only under the headless text model: the text rules only review), then:

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
| `fp` | The fingerprint `id|theme|route|pattern|cause`: no sizes and no positions. The pattern is the members' own nearest keyed node's (`zune-chart:*` for the chart caption), never a family counted over what is in scope, so one flaw keeps ONE fingerprint at `scope=screen`, `scope=content`, under `root=`, at every scroll position and in a replay, and one baseline line covers it at every window size where its cause is the same. A cause that names a size class (`narrow`/`low` of a target, the type size of `type.floor`) can differ between sizes. Two fingerprints are broad on purpose: `type.floor` is `type|size:N` (every run of that size on the route: the fix is one type role), and `contrast.text` is ink and plate (every run of that ink on that ground: the fix is one colour role). A verdict can still change with the frame (a run on art reads busy at one scroll position and measurable at another): that is another cause, not an unstable fingerprint. |
| `group` | `count`: how many members the finding collapses (rows, runs, cells); `pattern`: the family (`zune-history:*`, `c.title`, an ink colour). |
| `fix` | One line. It never says to shrink Zune display type, to add, remove or retime motion, or to remove or disable a feature. On Classic it is always "report; do not fix unless asked (Classic)". |
| `reason`, `suppressedBy` | Why it is under review or suppressed. |

### Key families

Many rules group by a node's **family**, read from its key (`InspectLint.FamilyOfKey`): the key up to a `#`, `|` or control
character, split at `:`. The first segment heads the family; it stops before the first segment that is an id or a name (a
digit, a capital, a space, longer than 24 characters) or `spotify` (an entity id follows), and, in a key of three or more
segments, before its last segment (the slot). So `zune-history:spotify:track:x` is `zune-history`, `zs:link:privacy` is
`zs:link`, `settings:section:Links` is `settings:section`, and an entity key `spotify:page:x` is `spotify:page`; `zs:title`
and `zs:body` (parts of one page) stay two families. A family seen 3 times or more among the nodes SCROLLING REACHES (not the
scope's: the scope and the scroll position never change which families repeat) is a REPEATED family; its `pattern` is the
family plus `:*` when keys carry more (`zune-history:*`). A node under an instance has a CELL PATH (child ordinals from the
instance); a cell 3 or more instances carry is a REPEATED CELL (a caption per card), one only some instances carry is that
instance's own (one zone's eyebrow). `duplicate.action` reads a verb's HOME by the key's first segment only (`trail:moreby`
and `trail:similar` are one home).

## Scope and the visible rect

Every rule judges only nodes in the scope. A node's **visible rect** is its presented rect cut by the window, by every
ancestor that clips its children or scrolls, and by every authored clip (a sticky or collapse cut, a reveal box); its
**on-screen fraction** is the share of its rect that remains. `scope=screen` keeps a node when that share is at least 50 %,
or when its visible part fills at least half of the viewport it sits in AND that viewport (its nearest ancestor that cuts) is
on screen itself (its **cover**: a page column or a section taller than two screens is on screen and is judged, so the edge,
overlap and spacing rules keep seeing it; a card cut to a sliver at the fold is no viewport, so what fills it is a sliver too
and stays out). Hidden, parked, exiting and fully transparent subtrees never reach the snapshot; a box with no area that clips
(a collapsed reveal) is captured, so it hides what is under it. A **modal scrim** (an interactive fill over 95 % of the window,
a dialog's light-dismiss backdrop) covers everything painted before it: those nodes are out of scope at `screen` and `content`
and the head says `modal`. An **opaque fill painted later** (alpha 98 % or more, not text, not an image, not an ancestor: a
menu's plate, a dialog's card, a header pinned over rows, the front layer of stacked art) that covers half or more of a node's
visible rect takes it off the screen (scrolling or dismissing reveals it, so `content` still reaches it; the head counts them
in `occluded`); under half, its runs may read the overlay as their ground, and `contrast.text` reviews them. API
(`Diagnostics.InspectVisible`):

- `Compute(nodes, windowW, windowH, visible, onScreen, inContent[, reach, cover])`: pure, over a depth-first snapshot node
  list; a root (`Parent` -1) that is `Seeded` starts from its `SeedScreen`/`SeedContent` cut and `SeedShown` (a flag, not a
  sentinel rect: a cut opened along a horizontal scroller's axis is as wide as `RectF.Infinite`).
- `CutChildren(rect, clips, scrollAxis, clipRect, ref screen, ref content)`: the one step both halves run per node.
- `AncestorCut(SceneStore, NodeHandle, in RectF window, out screen, out content[, out bool shown])`: the cut a live node's
  ancestors put on it and whether the viewport it sits in is on screen (what `root=` seeds its root with).
- `TryGetVisibleRect(SceneStore, NodeHandle, in RectF window, out RectF visible, out float fraction)`: any live node, UI thread:
  the engine's `SceneStore.VisibleWindowRect` (transforms, presented clip sizes, authored clips) cut by the window and by any
  scroller that does not clip; the fraction is of the painted rect. A VISIBILITY test only: a node under a scrim, a menu or a
  pinned header still reports what its clips leave of it.
- `Shown(SceneStore, NodeHandle)`: no node from it up is hidden, parked, exiting or transparent (what `TryGetVisibleRect`
  asks before it measures). `OnScreen(fraction)`: the one threshold (`MinFraction`, slack included) the scopes and a pointer
  verb both read.
- For a click there is no second copy: the pointer verbs' actionability (`InspectKit.Assess` in Diagnostics.Inspect.Kit.cs:
  `click`, `hover`, `scroll key=`, `find`'s `onScreen`) reads `TryGetVisibleRect`, so what `click` refuses as off screen and
  what `scope=screen` leaves out are judged alike; it adds the hit test at the centre of the visible rect (the node, inside
  it, or an ancestor its click belongs to), the disabled check and the passive-target rules (see agent-inspect.md).
- `Fraction(rect, visible)`, `InScope(scope, onScreen, inContent, cover)`, `TryParseScope`, `Name(scope)`, `MinFraction` (0.5;
  a fraction 0.001 under it still counts, so a row exactly half visible survives a saved snapshot's round trip).

## The rules

Severities below are the rule's own; `level` decides whether a fail counts in `summary`.

### Gate rules (today's nine, by id; the alias is what `rule` says)

**`edge.left` (gutter), `edge.right` (right-edge).** For each `LintEdge` of the active guide, the leftmost (rightmost)
matching node whose measured EDGE is in scope (on screen and not cut: a page column taller than the screen still shows its
left edge) is measured, matching its keys, prefixes, `UnderKey` and `GlyphsOnly`; off by more than max(tolerance, 0.5) is an
error (an advisory edge: warn). A parked, covered, mid-animation or scrolled-away copy is never picked; an edge nothing matches is not
checked (`guide.matched` counts the ones that were). When the FIRST match on the window (the leftmost; the rightmost for
`edge.right`) is hidden by an overlay (a modal scrim, or an opaque fill painted after it over half or more of what shows of it),
the edge is not checked either: the next match is another node, not the edge (the search suggestions over the band's first
pivots at Compact made the old rule measure the next visible pivot, +313.6 in the gallery's `Z-Overlays-Compact.1`/`.2`). Cause: right-of / left-of. FP classes: an edge whose key a view keyed on
something else. Suppression: `Allow` policy, baseline.

**`text.clip` (clip).** Word text whose ink runs past a clipping ancestor: vertical error, horizontal warn. The ink is the
box, a no-wrap run's natural height, and (1.2 x size - line height) / 2 above and below under a tighter explicit line height
(the **ink guess**). When the laid-out box fits the clip and only the ink guess runs past it, the finding is a `review`
(cause `top:ink-guess`, reason "ink guess, not glyph metrics"): display type whose real ascenders sit inside (the Zune album
and settings panoramas). A cut at the TOP of text under a key the theme's guide lists in `DesignedTopBleeds` (Zune: the detail
panorama `zune.detail.panorama` and the settings title `zs:title`, display type set on its exact line flush with the edge above
it, ascenders inside) is intended: `suppressed` by intent, cause `top:designed-bleed`, counted in `suppressed.intent`; a cut at
the bottom (descenders) still fails. A **scroller judges its own axis by its edges**: no finding where scrolling still reveals the cut text
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
| `designed` | an ELLIPSIS under a guide `TrimOk`, an `allowTrim` prefix, or MaxLines >= 1 in a REPEATED CELL (the same cell in 3 instances of a key family: a caption per card; not any text under a page section that repeats) | warn, suppressed (counted in `suppressed.intent` at `min=warn`); one report line counts them all ("N designed ellipses: zune-surface-shelf x8, ...") |
| `single` | a lone ellipsis outside any repeated cell | warn, review |
| `model` | the mount measures with the headless text model and the trim is not a designed ellipsis | warn, review (the head's `textRules` says so) |

**`type.floor` (type).** One error per size under `typeFloor`, members collapsed; the message names their families, the
fingerprint is the size alone (pattern `type`). Glyphs are not type; symbols count.
**`type.scale` (type).** One report line per (size, line height, weight) in use. Never a verdict.

**`target.size` (target).** Interactive, enabled, in-scope targets with a press handler (a role alone takes no press and is
no target) that reach under the floor: under 40 x 40 at window tier Compact is an error, under 32 x 32 above it a warn
(`targetFloorCompact`, `targetFloor`). A target reaches as far as the larger of two rects. Its **effective area** is the
largest interactive ancestor that runs the SAME click and contains the node; a target filling its interactive parent is the
parent's. Its **hit rect** is what the engine's hit test takes: the box grown by its hit slop (`BoxEl.HitSlop` plus the host's
touch floor, 40 at a Compact window: `TouchTargetPolicy`), cut by every clipping ancestor (a scroller only across its axis
while the target is not wholly in its view), by the boxes of the other press targets beside it in the same scrolled plane
(their real hits win), by press targets that paint nothing (an auto-hidden bar: the snapshot's `hitBlockers`) and by a scroll
bar's lane (the slop stops where a lane starts, read at capture along the target's middle line: the node's `lane`); the
message names what cut it ("24 x 24 (hit 36 x 40 with its slop; cut by beside ...)"). A target its clipping ancestors hide
whole is not on screen. Exempt: Slider, ScrollBar, ProgressBar, Pager (a pips pager's dots are one track), Hyperlink, and drag
bars: thin across, at least 4 x the floor long, AND handling a drag, a pointer press or a gesture itself or through the
ancestor its click belongs to (a splitter, a seek line; a 16 x 130 text button that only clicks is no bar and is judged).
Targets are grouped by the key pattern of what they sit in (their parent's nearest key): the band's Play and Shuffle, a
pager's chevrons and a crumb trail are one finding each, listing their sizes. Cause: narrow / low / narrow+low.

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
(ink, the plate under it that paints a ground, else the page): one finding per pair with its runs as members (an album page's
39 crumb and meta runs are one finding; art that tints the ground a little differently under every run stays one finding, and
the message lists the grounds read, the worst first). The fingerprint is the ink and that plate, never the sampled colour.
Needs 4.5 : 1, or 3 : 1 for glyphs, 24 DIP and up and bold 18.66 and up. Small type and glyphs are read without their own
anti-aliased ink. A **busy ground** (no colour holds 45 % of a box) is judged against its solid plate's declared fill only when
the frame agrees (the colour that covers most of the box is that fill, within 10 of 255 a channel: anti-aliasing spread it
thin). Otherwise (art, a scrim, a gradient, a wash) it is judged against the **rendered pixels around its glyphs at their
worst** (`Pixels.TryWorstContrast`, read with the frame and saved with the snapshot as the run's `worst`): the glyphs are found
in the frame (pixels past a quarter of the way from the box's median grey to the ink, plus 1.5 DIP either side), the gaps
between and around them are the ground, and the ratio is read at the 10th percentile of the per-pixel ratios (the p90 worst
ground, WCAG G18). A glyph that fills its box (an icon, a filled heart) is judged on the 3 DIP ring around it, less what is
ink-coloured there; a run whose words cannot be told from their ground (dark ink on a dark photograph, light on a bright one)
is judged over every pixel, which says it does not read. A failure over an image (an image node under most of the box) is an
`error`, over a gradient or a wash a `warn`, both `fail`, grouped and fingerprinted by (ink, what it sits on: cause
`media:gradient over a plate zune-search-hint`): "6 runs (...) in #ffffff87 over a
gradient artist-veil-tone:* over an image read 3.57 : 1 on their p90 ground #43506d". To judge text over a real photograph,
mount with `images=real` (the fake decoder paints every image as a gradient). A busy run with no worst read (a snapshot saved
before it was) is a `review` grouped by that ground ("13 runs (3 inks) sit on a busy ground (a gradient
zune.material.wash:*)"). A run the frame gave no ground for, a run in a subtree still animating (a page fading in or out:
`mid-animation`), and a run an opaque overlay partly covers (`covered`) are reviews too, before any pixel judgement: never
silence, never a verdict from a frame that is not the page's. No frame (`gpu=none`, headless, inside `crossing`): `checked`
0. Skipped as incidental text: a run whose own and ancestors' opacity multiply below 0.5 (faded on purpose: the stage's
distance-faded lyric lines), and a fainter glow copy of the same words at the same box (a karaoke line's bloom: the strongest
copy is judged). FP class: under `root=`, a plate outside the subtree is not seen, so a run on it reads as "on the page"; a hover highlight
that is not a node of its own reads as the page.

### Report-only rules (level `todo`)

Each ships with an algorithm note in its code and a guide policy to declare intent.

**`align.inset`** (warn; port of design-qa `proto/leads.py`). The owner's "history at 24 vs pins at 16" class.
1. Members: ink (text, image, gradient, visible fill or border) that scrolling reaches (the WHOLE scroll content), not a
   glyph, not in an animating subtree (a page sliding in is not where it lands), not in a horizontal scroller, not a PLATE (an
   ink node other ink sits on), not a ground as wide as its scroll region (a full-bleed fill or image), not placed by centring
   or end alignment (the node or an ancestor up to its section, by its own AlignSelf/JustifySelf or its parent's
   AlignItems/Justify, or its text alignment), and not an end-aligned column (cells of one family and cell path whose right or
   centre edges agree while their lefts do not).
2. Groups: (scroll region x backdrop).
3. Leads: members that nothing of their group STARTS to the left of on an overlapping line. "Nothing" counts every ink node of
   the group, the ones step 1 left out included: a label beside a play disc (a plate) or an icon leads nothing, and neither does
   the back layer of stacked or offset art (its front layer starts left of it).
4. The column line: on the page's own ground, the guide's `Column` edge (Zune and WinUI: the page title's x) when at least 3
   leads sit on it; else the x the most leads share (at least 3), unless an x LEFT of it holds at least half that support (then
   the leftmost such x: headers under which more rows are inset keep the line). Read over the whole content, it does not move
   while the page scrolls, and three outdented leads (a hang without a policy) cannot move a column of twenty.
5. A lead IN SCOPE 4 < |d| <= 16 DIP off the line is a finding when its section (nearest keyed node) has no lead on the line;
   one finding per (pattern, d rounded to 1 DIP), listing one member per section. The message names where the line came from.
Suppression: guide `Hang` or `Indent` with a value within 2 DIP of d. FP classes: an optical hang without a policy (it now
reports itself, -d, rather than moving the line), a deliberately indented sub-list, and, without a guide column, a page whose
inset rows outnumber the leads on the real column more than two to one in a group whose sections do not nest them. Near misses
(0.5-4 DIP) are a later rule (`align.near-miss`).

THE COLUMN PASS (cause `column:+d`) judges what the region pass cannot: content that ink sits LEFT of on its line (an aside
beside a song list, the inside of a card, a right pane), and leads more than 16 DIP off their region's line. A misalignment is a
lead that differs from its column's lead without a visible container that would justify it:
1. Columns: every vertical stack with two or more children, not a ZStack, not in a rail, not moving, not a scroller, and not a
   frame column that holds a page slot (the title bar, band and page stacked are the guide's edges, not one page's content).
2. A child's lead: the leftmost OUTERMOST ink in its subtree (a drawn visual such as a progress ring's arc counts as ink),
   skipping lines (a rule or a selection bar, 4 DIP or thinner), anything placed by centring or end alignment, and a GROUND (a
   plate at least 90 % as wide as the child: a selected row's fill; what sits on it leads). A narrower plate (a pill, a chip)
   leads itself. The column's lead (the REFERENCE) is its leftmost child lead; a lead's declared `Hang` or `Indent` is taken off
   its x first (the Zune panorama's -8 and the artist name's -8 never become a column's lead).
3. A child lead in scope that the region pass did not judge (it is no region lead within 16 of its line) and that sits
   4 < d <= 32 DIP right of the reference is a finding, unless a VISIBLE CONTAINER justifies it (the plate it sits on starts at or
   right of the reference: a filled card whose art is flush and whose words are padded) or it sits on a HANGING column (ink in
   another child starts within 4 of its x while other ink starts left of that ink on its line: a checkbox's help under its label,
   the rows of an indexed list that carry no letter, a title beside its number).
4. One finding per (reference, d rounded to 1 DIP), named by the nearest keyed node holding the reference and the leads.
The window: the Zune boards put every section, card and row on the page column and inset only on plates; the smallest offset
between two real columns they draw is 56 (a track number to its title; album art to its words is 68), so 32 (twice the 16 card
inset) keeps a second column out. The Zune album aside's about card is the class: its eyebrow, name and bio at x=1152 under its
art at x=1132, on no fill (+20; the board, Z-Album, draws the aside on one x).

**`text.duplicate`** (warn; the P1 verifier's "About the artist" twice). The same visible words twice in one column within a
glance: a heading repeated as the eyebrow of the card under it.
1. Runs: in-scope word runs (not glyphs, not mid-animation, at least half opaque) of 4 or more characters with a letter, compared
   folded (case and runs of white space ignored: Zune lowercases a heading the view wrote capitalised); not a control's label (a
   button's or a link's own words: a verb seen twice is `duplicate.action`'s, a "See all" per shelf one link per shelf), not
   under `ignoreUnder` (the title bar's crumbs and the play bar repeat the page's words by design), not in `allowDuplicate`.
2. Pairs: two runs of one folded text in one scroll region and one PAGE (the nearest keep-alive page slot: the band's title and
   the panorama are chrome and page, both drawn on Z-Album) whose boxes share x and stand at most 240 DIP apart vertically. Not a
   glow copy (the same box); not REPEATED CONTENT: two instances of one repeated key family, two runs at the same child path below
   their lowest common ancestor (unkeyed rows, release-note sections that each carry a "closed" tag, two rails showing one album
   card), or two runs that both sit in click targets (cards and rows: a show named like its publisher; their words are content).
3. One finding per (region, page, text); pattern the later copy's, cause `repeats:<the first copy's pattern>`.
Fix: say it once; keep the heading and drop the repeat inside the section (or give the repeat its own words). Suppression: a guide
`Allow` for `text.duplicate`.

**`target.ink`** (warn; the album list header's `c.num`). A click target a person cannot see: an in-scope, enabled, pressable
target (the `target.size` set, its exemptions included) whose subtree draws nothing at rest (no text, image, gradient, visible
fill or border, no drawn visual; a hover fill is not ink at rest). Not a finding: the click shows elsewhere (an ancestor running
the same click draws), content another node draws covers a quarter of it or more (the Liked page's tempo chart under its hit
bands), a drag bar, or a target over half the window or more (a light-dismiss catcher). Grouped as `target.size` groups (the
parent's pattern), cause `no-ink`. Fix: give the target ink at rest (its label or glyph) or hand its click to a control a person
can see; never remove the feature. The Zune list header's number column (28 x 36, sorts by track number) is the class.

**`space.dead`** (info; a report rule, never promoted). A slot that keeps its room while nothing fills it: "Nothing playing" at
x=72 beside the play bar's empty 44 x 44 art slot, no plate (Zune Home 400; 48 x 48 at 1440, and Classic and WinUI alike). An
in-scope box, not moving, not a target, with a declared width and height of at least 24 DIP that draws nothing, LEADING a row (no
ZStack): nothing before it in the row draws and a sibling after it does. Not reported: a gap between two groups (the title bar's
drag room before the caption buttons) and a cell of a repeated family another instance fills (a column reserved across rows: the
expand chevron some recents rows carry). A slot reserved ON PURPOSE (the no-jump rule: room kept for art that arrives) is
declared in the theme's guide as an `Allow` for `space.dead` on its key prefix, naming the board: the finding is then
suppressed by intent. Fix: give the empty slot its plate at rest (a placeholder fill or glyph), or declare the reservation;
never drop a reservation that keeps the row from jumping.

**`align.baseline`** (warn, outcome review; port of `proto/columns.py` row offsets). For each repeated key family: (a) a keyed
CELL inside a row of another repeated family: its first text's offset from the row's top, one cell per row, grouped by (cell
family, row family); (b) a ROW family's own texts by cell path. Only in-scope texts are measured. A group of 3 or more whose
offsets spread over 0.5 DIP drifts. When several groups hold the SAME texts (a card's title measured against its strip, its
shelf and its zone), only the innermost row's is judged. It is excused when the bottoms or centres agree (a bottom-anchored or
centred caption whose line count varies), when the "rows" differ in height by more than max(2 DIP, 10 %) (sections of a page,
not rows of a list), when keyed cells differ in width by as much (a shelf of wide cards beside square ones: another card
form), or when every drift follows its box's height (a block above the text grew: an optional header). The rows themselves are
never measured against the list (the prototype's false positive). An unkeyed cell is named by its type style
(`text 20/600 in zone:home:*`), never by a dump index path. Offsets are line-box tops, not baselines: the outcome is review.
Suppression: `Allow` policy with rule `align.baseline`.

**`space.scale`** (info). Every DECLARED gap (with 2 or more children), padding side and margin side that is not 0, on an
in-scope node with ink under it, must be on the scale: 2, a multiple of 4 up to 64, or a value of the active definition's
`Spacing.*` (XXS..XXXL, Gutter, PageWide, PageNarrow, Card, Inner, StackS/M/L), the style's `R.Space` (SectionGap,
SectionGapWide, GridGap), `R.Shape` radii, or a step the theme's guide declares from its boards (`BoardScale`: Zune's
`board.6`, `board.10`, `board.14`, `board.18`, `board.22`). A control-internal value (3, 5, 6, 7, 9, 10, 11, 14, 15) under a control with a
role is the control's. Grouped by (value, the node's nearest keyed pattern); the message names the nearest spacing token. Declared values, not
measured gaps (45-53 % of measured insets are off the grid by centring and line boxes). Suppression: `OffScaleOk`. FP classes:
values a board draws outside the tokens that its guide does not declare, hairline nudges (1 DIP), positions a record derives
(a timeline's slot offsets past 64).

**`text.placeholder`** (error for a missing loc key or argument, warn for a leaked value). On-screen word runs:
- a missing key as `Loc.Get` renders it: `[a.b]` with a loc key's shape inside (two or more dot-separated segments, each a
  lowercase letter then letters, digits, `_` or `-`: `[home.quickPins]`; never `[Pt.2]` or `[Intro.Outro]`);
- a missing argument as `Loc.Format` renders it: `{name}`, a camelCase identifier (never `{Remix}`);
- a leaked value: `NaN`, `null`, `undefined` or `-Infinity` as the whole text; any of them and `Infinity` where a formatted
  value sits: next to a digit, `:`, `%`, `/`, `×` or a placeholder (`NaN:NaN`, `NaN%`, `Infinity / 3`), or before a unit word
  (`NaN plays`, `NaN songs`, `NaN min`, `NaN followers`, ...); and `NaN`, `null` or `undefined` beside a meta line's separator
  (`Album • null`). "Infinity" alone, or beside a separator, is a title.
In catalogue content (text inside a row or card of a repeated family that is a click target or carries art) a bracketed loc key
counts only as the WHOLE text, and a missing argument or a leaked value only where a formatted value sits: a track row's
`NaN:NaN` duration and `NaN plays` count, a title "null" or "{intro}" does not. Grouped by (kind, the node's nearest keyed
pattern). FP class: a catalogue title that is nothing but a bracketed lowercase dotted word. Suppression: `Allow`.

**`key.hygiene`** (warn, level todo: never in `summary`). In-scope keys that break selectors, one finding
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
- `DesignedTopBleeds` (default: none): keys whose display type sits on its exact line flush with a clipping edge above it, by
  design; `text.clip` suppresses a cut at the top of text under them (above).
- `Policies(LintContext)` (default: none): `LintPolicy(Intent, Prefix, Reason, Value, Rule)`, intents for every node whose key
  or an ancestor's starts with `Prefix`: `TrimOk`, `NeverTrim`, `Hang` (Value DIP, negative = left), `Indent`, `Bleed`,
  `OffScaleOk`, `RepeatOk`, `Allow` (any finding of `Rule` whose members ALL lie under the prefix: a reserved slot is
  `Allow` for `space.dead` on the slot's key). The reason must let a reviewer check it: name the board. A board draws one page:
  `LintContext.Route` (`home`, `album`) scopes a page's policies.
- `BoardScale` (default: none): `LintScaleStep(Name, Value, Reason)`, the steps the theme's boards space with beyond the shared
  tokens; the capture adds them to the snapshot's scale, so `space.scale` takes them as on the scale (a replay reads them back).

Precedence: guide intent, then baseline, then the rule's default. A matched intent makes a finding `suppressed` and counts
it in `suppressed.intent`; it never hides it from `outcomes=all`.

| Theme | Edges | Policies |
|---|---|---|
| Zune | `ZuneLintGuide`: titlebar.lead, band.first-pivot, page.column, page.title (`Column`); Small and Standard also band.last-action, page.column right, playbar.last-glyph | On Home only: `TrimOk` on `zune-home-pin:`, `zune-history:`, `zune-surface-shelf`, `zune-shelf-card:`, `zune-tile:`, `zune-cluster-row:`, `zune-chart:` (Home captions ellipsise on one line, Z-Home and Z-Home-Compact). Everywhere: `Hang` on `zune.detail.panorama` by the record's `PanoramaNudgeX` (Z-Album), on `zune-artist-name` by `ZuneArtistRules.NameNudge` (Z-Artist) and on `zs:title` by `ZuneSettingsRules.PanoramaBleed` (Z-Settings). `DesignedTopBleeds`: `zune.detail.panorama`, `zs:title`. `BoardScale`: 6, 10, 14, 18, 22 (the boards' 2-DIP steps to 24) |
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

`lint save=<file>` writes the snapshot the rules saw: window, theme, route, text source, the guide's edges, policies and top bleeds, the
theme's spacing scale, and every captured node with its rect, flags, scroll range, fades and motion, authored clip, the cut a
`root=` subtree started from (`seed`, `seedContent`, `seedShown`), declared layout, paint (with `visual`: a drawn visual kind beyond
a box, a text run or an image, such as `Path`, `ListRow`, an SDF `Arc`, a `BorderBrush` or an `Acrylic` material; a snapshot saved
before it was captured reads none), click identity and whether it drags,
and text run (with the ground the rendered frame put under it, and over a busy ground its worst ratio, so `contrast.text`
replays without pixels). Numbers are written at round-trip float precision. Derived fields (`on`, `covered`, `occluded`,
`family`, `cell`) are written for a reader and recomputed on replay. A Zune Home 400 snapshot is about 0.33 MB, a Classic or
WinUI Home at 1440 about 0.5-0.6 MB.

`lint replay=<file>` runs the same pure rules on it: any scope, rules, baseline or `outcomes`. A replay of a live lint's
snapshot gives the same findings, fingerprints and counts (checked on the Zune album at 1440: 53 of 53). The reader is strict:
a file that is not a lint snapshot, is newer than the reader, has no window size, has no nodes (a missing, empty or non-array
`nodes`), a node without `p` or `r`, a rect that is not four finite numbers with w and h at or above 0, a parent that does not
come before its child, a field of the wrong type (`"size":"big"`), an unknown policy intent, a policy without a prefix and a
reason, or an edge without rule, id and expected fails with the node index and the field. Uses: validating a rule on pinned
pages while the branch moves (the six
reference mounts), showing a rule on a doctored copy (one node moved, one text replaced, a policy added) after a lane fixed
the real flaw, and auditing a page without mounting it. A replay needs **no mount**: the server routes `lint replay=` before
its mount check, so a fresh server can lint a saved page at once (`status` still reads `mounts:0`).

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

| Mount | Before: warn+ | Lane i3: gate / every level | Integrated: gate warn+ (summary) | Integrated: every level | review | suppressed.intent |
|---|---|---|---|---|---|---|
| Zune Home 400 | 19 | 0 / 5 | 0 | 5 (align.inset zune-history +8; key.hygiene x4) | 0 | 2 |
| Zune Home 1440 | 33 | 4 / 8 | 6 | 10 (key.hygiene x4) | 0 | 4 |
| Zune album 1440 | 66 | 10 / 12 | 9 | 11 (key.hygiene x2) | 1 | 1 |
| Zune artist 1440 | 33 | 7 / 9 | 8 | 10 (align.inset album art +8; key.hygiene) | 0 | 0 |
| Classic Home 1440 | 77 | 6 / 9 | 5 | 8 (key.hygiene x3) | 1 | 2 |
| WinUI Home 1440 | 85 | 4 / 7 | 3 | 6 (key.hygiene x3) | 0 | 1 |

"Integrated" is feat/inspect-p1 with feat/themes-p6 951f4af2 (`C:/WAVEE/scratch/inspect-p1/integration/final/six`); the
paragraph after the list says what changed between the two.

`key.hygiene` reports at warn (level todo), so the "every level" column counts its true keys: the route host key's control
character, `zune-home-scroll:`, `home:scroll:` and `facet:` with a trailing colon, and the form words in
`zune-home-daylist-form:*` and `tracks:standard:*`. No finding names a wholly off-screen node at `scope=screen` (0 of 330 across
both screen lints of every mount; before lane i3, 112 of 313 did), and across three scroll sweeps no member under 15 % visible
is judged (5 slivers before the viewport test). One flaw keeps one fingerprint: 0 screen-vs-content splits over three sweeps
(19 before). The real flaws the old lint missed or drowned are reported: the 0.71 DIP near-miss on the Zune Home chart caption
(below the fold at 400 x 860: `scope=content`, `root=zune-chart:top50 scope=content`, or scroll to it; one fingerprint
`text.trim|zune|home|zune-chart:*|near-miss` everywhere), the Zune Home 400 history inset (+8) and, below the fold, the release
and cluster rows (+8), the Zune artist album art inset (+8), the album title-baseline drift (`align.baseline`), the WinUI Home
wide zone's header sitting 4 lower in its zone (`scope=content`), the album's band-vs-list Shuffle and More, and one contrast
finding per token pair. Labels per finding (true / false / intended): `C:/WAVEE/scratch/inspect-p1/i3/r3/labels.txt`.

After the integration (feat/inspect-p1 with feat/themes-p6 951f4af2: busy grounds judged on the rendered pixels, the
designed top bleeds, the target hit rect), the album's panorama cut is a suppressed designed bleed. The busy-ground reviews
are gone where the frame could be read: those runs are judged on their pixels now (Zune Home 400: 5 reviews before, none after, every run passing). The new fails
are contrast over pictures: the Zune Home search hint's "ctrl f" over its pill (2.61 : 1) and "Pinned" over the wash (3.16),
the Zune artist crumb over the material bleed (an error, 3.67). Still 0 of 286 screen findings name a wholly off-screen
node, and the replay of each of the six snapshots gives the same findings, fingerprints and counts.

## Known limits

- The text ink is a 1.2 em guess, not glyph ink metrics: a clip only the guess makes is a review until the engine reports ink.
- `align.baseline` compares line-box tops (review only) until the engine reports first baselines.
- `space.scale` knows the token scale and the steps a guide declares (`BoardScale`: Zune's 6, 10, 14, 18, 22); WinUI and Classic
  declare none, so a board value of theirs reports as info.
- `text.duplicate` cannot tell a label from content by its words: two copies both in click targets are content and not judged, so a
  label repeated inside one card (an eyebrow and a title in the same link) goes unreported.
- Contrast reads one rendered frame: over art the ground is the frame's pixels at their p90 worst, so the fake decoder's
  gradients stand in for photographs unless the mount says `images=real`; under `root=` the plates outside the subtree are
  not seen.
- Occlusion knows opaque FILLS painted later (a menu's plate, a dialog's card); an image or a translucent overlay painted over
  a node does not take it off the screen.
- `crossing` lints the settled scene without reading a frame: its `contrast.text` checks nothing (`checked` 0); its head names
  the devices it ran on.
