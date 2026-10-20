# Driving Wavee as an agent: `Wavee.exe --inspect`

`--inspect` starts a long-running server that runs the **real app** headless. No window appears and nothing is shared
with a copy of Wavee the owner has open. An agent sends one JSON line per command and gets one JSON line back. It can
mount any route at any size, theme and light/dark kind, click, type and scroll, and read the scene as structured data:
every node's key, rect, colours, flags and text metrics. When pixels matter, it can also write a PNG.

The server code is in `src/apps/Wavee/Screens/Diagnostics.Inspect*.cs`, and the client is `tools/inspect.py`.

## Starting it

```powershell
# from WaveeMusic\, after `dotnet build src/apps/Wavee/Wavee.csproj`
python -I tools/inspect.py -c "mount w=400 h=860 theme=zune" -c "tiers"     # the client starts and stops the server
src\apps\Wavee\bin\Debug\net10.0-windows10.0.26100.0\Wavee.exe --inspect      # or talk to it yourself on stdin/stdout
Wavee.exe --inspect --pipe wavee-inspect                                      # or over \\.\pipe\wavee-inspect
```

| Flag | Meaning |
|---|---|
| `--pipe <name>` | Serve one client on `\\.\pipe\<name>` (stdout first prints `{"kind":"listening","pipe":...}`). Only a process of the user who started the server can connect (`PipeOptions.CurrentUserOnly`). Default: stdin/stdout. |
| `--profile <dir>` | The scratch profile. The default is a new folder under `%TEMP%\wavee-inspect`, which is deleted at exit. The user's `%LOCALAPPDATA%\Wavee` is refused. |
| `--text dwrite\|headless` | `dwrite` (default): real DirectWrite metrics, plus the offscreen GPU for `render`. `headless`: a 0.55 × size font model and a recording device. |
| `--fg <switches>` | Engine switches (`EngineSwitches`), e.g. `--fg motion` writes the projected-motion decisions (capture, animate, snap) to stderr. `tools/inspect.py` drains stderr on a thread, so a chatty server cannot block. |

What the server guarantees:

- **The data is fake.** The server boots `Shell.Headless.Boot` (`--fake` seed, memory settings, no log file, no crash
  writers), and `Wire.GoOffline` refuses every request. If anything reaches the network, the mount, status and dump
  replies carry `"networkLeaks": n`.
- **It never touches the owner's app or profile.** It never takes the single-instance gate, so it runs beside the
  owner's Wavee. It never reads or writes the owner's profile, and it never logs in. Never pass `--spotify-*` flags.
- **One UI thread runs everything.** The server owns that thread. Frames advance only through `frames`, `settle` and the
  verbs that settle by themselves, so the same commands give the same answers.
- **Covers are graded by a fixture.** Every cover gets the same fixture colour (`InspectKit.FixtureGrade`), so the
  surfaces that colour themselves from artwork exist.

The server's first line is `{"kind":"ready","profile":...,"text":"dwrite","trim":"dwrite","verbs":[...]}`, and its last
line is `{"kind":"bye"}`. The process exits 0 after `quit` or at the end of input, 64 on a bad command line, and 3 when
the boot fails.

## Protocol

There is one command per line, in either of two forms:

```json
{"cmd":"mount","args":{"w":400,"h":860,"theme":"zune"},"id":1}
```
```text
mount w=400 h=860 theme=zune
```

- **JSON form.** The args can also sit at the top level, beside `cmd` and `id`. A `--headless`-style `"args":[...]`
  array fills the verb's positional names.
- **Text form.** Bare words fill the positional names: `goto home`, `find zune-band-pivot:home`, `frames 30`. `type` takes
  the rest of the line as its text.
  - **Quoting.** A value in double quotes keeps its blanks and is always a string: `lint allowDuplicate="See all"`. Inside
    the quotes `\"` is a quote and `\\` a backslash; any other backslash stays as it is, so a Windows path needs nothing.
  - **Lists.** A value in brackets is a list, carried exactly as the JSON form would carry it:
    `lint rules=[clip,trim] allowDuplicate=["See all",Play]`, `trace 60 wheel=[3,3,-6]`. Unquoted numbers and `true`/`false`
    are JSON literals; every other item is a string. A comma list without brackets (`rules=clip,trim`) still works.
- **Strict args.** Every verb declares its args (the verb table beside `VerbNames` in `Diagnostics.Inspect.Protocol.cs`).
  Names are case-sensitive, and an arg the verb does not declare is refused before anything runs:
  `{"ok":false,"error":"unknown arg 'rule' for lint (did you mean 'rules'?)"}`. A miscased name is pointed at its right
  spelling (`keysonly`: did you mean `keysOnly`), so `mount them=zune` fails instead of quietly mounting the active theme.
- **`help`** lists the verbs with a one-line summary each. `help verb=lint` (or `help lint`) gives that verb's positional
  names and every arg with its type, default, choices, aliases and a line of doc, plus one example. `help schema=json`
  returns the whole table (`schema {version, grammar, verbs:[...]}`).
- `#` lines and blank lines get no reply.

Every command gets exactly **one** reply line. A JSON command's `id` comes back on the reply; a text command's id is -1.

```json
{"id":1,"ok":true,"cmd":"mount","frames":38,"settled":true, ...}
{"id":2,"ok":false,"cmd":"click","error":"no mounted node keyed 'zune-band-pivot:nope'"}
```

**Examples.** The reply values shown in this guide are illustrative, not measured.

**Units.** Every geometry value is in window DIP, and every `rect` is the **presented** absolute rect
(`SceneStore.PresentedAbsoluteRect`), which is what paint and hit-testing use. Colours are `#RRGGBBAA`.

**Settling.** The verbs that change the screen (`mount`, `goto`, `resize`, `click`, `hover`, `key`, `type`, `scroll`,
`play`, `theme`, `option`, `state`, `dialog`) settle by themselves: they run frames until nothing moves for two frames in a
row, at most `max` frames (600 by default). Nothing moves means no animation track, no scroller in user-driven motion
(`AppHost.AnyUserScrollMoving`: a wheel notch's glide, a fling), and no wake reason (`AppHost.CurrentWakeReasons`) but the
ambient ones: the caret blink, the warm cadence, a host timer coming due, and the frame-clock tickers that run for as long
as a track plays (the playhead, a visualizer). The replies report `frames` and `settled`. A settle that hit its cap adds
`busy`, what still moved: `{"key","kind":"anim"}` for a node with live tracks (named by its nearest keyed ancestor),
`{"key","kind":"scroll","motion":"Wheel"}` for a scroller in motion, and `{"kind":"wake","reason":"ImageReady"}` for a wake
reason with no node. Pass `settle=false` to skip the settle and step the frames yourself with `frames`.

A looping animation never ends, so a page that shows one never settles. On Zune the playing track's row animates for as
long as it plays: a `play` (or any verb after it) on a page that lists that row runs to `max`, and `busy` names the row.
Use `settle=false` with `frames N` there, or a small `max`.

**What changed (`after`).** The verbs that act (`goto`, `click`, `hover`, `key`, `type`, `scroll`, `play`, `option`,
`theme`, `state`, `dialog`) end their reply, when something is mounted, with `after:{route, focused, overlays, hit}`:

- `route`: the route the app is on now, in `status`'s form.
- `focused`: the key of the focused node, or null.
- `overlays`: the open overlay layers, top first, as `{"key":"overlay:3","first":"crash-prompt:fake0001"}` (the layer and
  the first key inside it).
- `hit`: the key of the node the pointer resolved to. For a pointer verb, what its press or wheel landed on; for any other
  verb, what lies under the pointer now. Null before any pointer verb, or over nothing that takes input.

Keys name the nearest keyed ancestor when the node itself has none.

## Verbs

### `mount {w, h, scale?, theme?, kind?, route? | uri? | deeplink?, gpu?, keepRoute?}`

Mounts the whole app at `w` × `h` DIP. Any mount that is already up is replaced, and the next mount does not inherit
what the last one left behind: the `state` vacancy is cleared, playback stops and the deck empties (as a sign-out empties
it), the play queue is forgotten and the fullscreen stage closes. Not every static is reset (the video surface of a
`video=` mount is not), so a fresh process per configuration is still the rule; `status` counts the `mounts` a server has
done so a reused one shows. A mount without `theme` or `kind` keeps the active ones.

| Arg | Values |
|---|---|
| `scale` | DPI scale, 1 by default, at most 4. |
| `theme` | `classic`, `zune` or `winui` (default: the active theme; a fresh server is on Classic). Zune turns Developer mode on; the others turn it off. |
| `kind` | `dark` or `light`. |
| `gpu` | `warp` (default) or `hardware`: the offscreen D3D12 surface, so `render` works. `none`: DirectWrite over the recording device (no pixels). |
| `keepRoute=true` | Keeps the current navigation. Without it, the mount starts from a fresh Home. |
| `setup=true` or `signin` | Mounts a first run: the setup wizard's plate opens over the shell (give `frames` ~120 before `render`). `signin` asks for the sign-in page; today the plate still opens on its first page, so click `next` to reach it. |
| `feedback=bug` or `feature` | Opens the report form over the page. |
| `video=fullscreen`, `docked` or `floating` | Puts the video surface up with a fake track playing (no frames decode offline: the chrome and menu draw over an empty picture; the fullscreen chrome is a hover band: add `chrome=shown` to draw it at rest at every tier). |

The navigation args are the ones `goto` takes.

```json
{"cmd":"mount","args":{"w":400,"h":860,"theme":"zune","kind":"dark","route":"home"},"id":1}
→ {"id":1,"ok":true,"cmd":"mount","frames":41,"settled":true,"size":{"w":400,"h":860,"scale":1},"route":"home",
   "text":"dwrite","devices":"offscreen (D3D12 (WARP))","render":true,
   "tiers":{"window":"Compact","height":"Regular","content":"Compact","contentWidth":400,"viewportWidth":400,
            "viewportHeight":860,"resizing":false,"rungs":[]},"theme":"zune","kind":"dark"}
```

If the offscreen surface cannot be created (no D3D12 or WARP), `devices` reads `headless-gpu+dwrite (<reason>)`. The
text is still true, but `render` answers `ok:false`.

`mount` also takes the fullscreen stage's preferences: `stage=lyrics|visualizer|queue|artist`, `layout=card|large|centered|artist`
and `gallery=on|off` (set before the mount, the first-run tip marked seen), so `key f11` opens the stage straight on that look.

### `goto {route, arg?} | {uri} | {deeplink}`

- `route` is a route key, the same string a pin or `wavee://open?route=` uses: `home`, `browse`, `search` (with
  `arg`), `liked`, `recents`, `settings`, `album:spotify:album:<id>`, and so on.
- `uri` opens an entity's page.
- `deeplink` runs a whole `wavee://` payload through the OS door (`Shell.ApplyDeepLink`).

```json
{"cmd":"goto","args":{"uri":"spotify:album:4aawyAB9vmqN3uQ7FjRGTy"},"id":3}
```

### `resize {w, h}`

Resizes the window and settles. Use it to check a tier crossing on a live mount.

### `frames {n}` and `settle {max?}`

`frames` runs exactly `n` frames and replies with `{"frames":n,"animating":bool,"scrolling":bool,"wake":[...],"ambient":[...]}`:
`animating` is a live animation track, `scrolling` a scroller in user-driven motion, `wake` the wake reasons that keep a
settle going and `ambient` the ones it ignores (see Settling). `settle` runs frames until nothing moves for two frames, for
at most `max` frames, and replies with `{"frames","settled"}` (plus `busy` at the cap).

### `reset`

Unmounts, clears the `state` vacancy, playback, the queue and the fullscreen stage (as `mount` does), resets the navigation
to a fresh Home, and makes Classic dark the active theme.

### `unmount`, `status`, `help`, `quit`

- `status` reports whether something is mounted, the frames run so far, `mounts` (how many mounts this server has done:
  above 1 means state that a mount does not clear may carry over), the theme and kind, the route, the text backend and the
  network leaks. When something is mounted, it adds the mount facts, `animating` and `trimSource`.
- `help` lists the verbs with their summaries; `help verb=<name>` and `help schema=json` describe the args (see Protocol).
- `quit` unmounts and ends the server.

### `click`, `hover {key | x,y, nth?, force?, mods?, button?}`

These aim at a node keyed exactly `key`, or at a point (`x`, `y`, window DIP, no checks).

- `click` sends a pointer move, a press and a release. `button=right` sends a right click, and `mods=ctrl+shift` holds
  modifiers.
- `hover` only moves the pointer.

**Actionability.** A `key` target must be actionable, the way a user could reach it:

| Check | Refused with `reason` |
|---|---|
| It is shown: live, linked to the root, visible and opaque on its whole chain, with a box (`SceneStore.IsShown`; a parked keep-alive page is not). | `notShown` |
| At least 50 % of its presented rect is visible after the window, every ancestor that clips (`ClipsToBounds`, a scroll viewport) and every ancestor's authored clip rect. | `offscreen` (scroll it into view first) |
| Neither it nor an ancestor is disabled. | `disabled` |
| The hit test at the centre of its visible part lands inside it, on nothing, or on an ancestor it is a passive part of (a label in a button). | `covered`, naming the key that covers it |

The pointer goes to the centre of the visible part. A refusal is `ok:false` with the reason, and `matches` lists every node
keyed so with its `rect`, `onScreen` share, `actionable`, `reason`, `why` and `hit`. `force=true` skips the checks (and aims at
the presented centre of a node with nothing visible), as the verbs did before.

**`nth`** counts the actionable matches only (every match with `force=true`), in **reading order**: top first (to the whole
DIP), then left. `find <key>` lists the matches in the same order with the `nth` each one answers to. The reply says which it
took: `target {nth, of, matches, onScreen, order, forced?}`, beside `at` and `key`; acting verbs add `after` (see Protocol).

```json
{"cmd":"click","args":{"key":"see-all:home:made-for-you"},"id":4}
→ {"id":4,"ok":true,"cmd":"click","frames":52,"settled":true,"at":{"x":1325.33,"y":592.58},"key":"see-all:home:made-for-you",
   "target":{"nth":0,"of":1,"matches":1,"onScreen":1,"order":"reading order (top, then left), actionable matches only"},
   "after":{"route":"home-section:spotify:section:wavee-mockup-made-for-you Made for you","focused":null,"overlays":[],
            "hit":"see-all:home:made-for-you"}}
{"cmd":"click","args":{"key":"see-all:home:made-for-you"},"id":5}          (on the album page: Home is parked)
→ {"id":5,"ok":false,"cmd":"click","error":"'see-all:home:made-for-you' is not actionable: not shown (hidden, parked, transparent
   or no box on its chain); force=true aims at it anyway","reason":"notShown","matches":[{"rect":{...},"onScreen":0,
   "actionable":false,"reason":"notShown","why":"...","hit":"about:link"}]}
```

### `key {key, repeat?}` and `type {text}`

`key` takes these names:

- `enter`, `esc`, `tab`, `space`, `backspace`, `delete`, `insert`
- `left`, `up`, `right`, `down`, `home`, `end`, `pageup`, `pagedown`, `apps`
- `f1` to `f12`, `a` to `z`, `0` to `9`, or a decimal virtual-key code
- any of these with `ctrl+`, `shift+`, `alt+` or `win+` in front, such as `ctrl+f`

`type` sends character input to whatever has focus.

```text
key ctrl+f
type daft punk
key enter
```

### `scroll {key | x,y, dy?, dx?}`

Turns the mouse wheel at the target: `dy` notches, positive toward the end of the content (3 by default). A `key` target
is checked and picked as `click` does (`nth`, `force`). The settle waits for the scroller to stop, so the reply's
`scroll {offset, max}` (with `key`: the first scroll viewport under that node) is where the glide ended.

### `play {uri, fromMs?, next?, ago?}`

Plays one fake track or episode as its own context. This is the headless probe's recipe: `BuildContextQueue`, then
`Queue.Replace`, then `Playback.PlayNow`. `next=N` (a `spotify:track:tr<k>` uri) adds the N tracks `tr<k+1>`... to the same context, so the
stage's "up next" page has rows. The lyrics media clock runs in the harness frame clock's epoch (`Shell.Headless.Boot`), so a playing
track follows its lyrics: render the stage while playing, no pause needed. `ago=N` backdates the play in the play log by N minutes (the Up next page's "played before this" reads it).

### `state {kind}` and `dialog {kind}`

`state kind=empty|nomatch|error|offline|none` shows the shared vacancy in place of the page (`none` puts the page back); `loading` opens a search for
`zzqx`, which the fake catalogue never answers (the page stays on its skeleton for the first frames). Any other search query is answered from the fake
catalogue by name; `goto uri=spotify:artist:arzzqx` is the pending artist. `offline-rows` keeps the page and reads the session as down in its rows.
`mount` and `reset` clear the vacancy. `dialog kind=crash|hang|report` opens the crash prompt (over a made-up bundle, through the shell's own overlay host)
or Report a problem over the **current** page: it never navigates, and the reply's `over` names the page underneath. `key esc` closes it.

### `theme {id}`

- Mounted: runs the user's live theme transaction (`ThemeHost.Switch`: prewarm, freeze, remount, thaw). The reply
  carries the `outcome`.
- Not mounted: selects the theme for the next mount.

### `option {themeId?, id?, value?}`

- Without `id`: lists the theme's user options, with each one's qualified id, kind, current value and choices.
- With `id` (qualified, like `structure.surfaces`, or plain) and `value`: sets the option through `ThemeHost.Apply`,
  the same path the Settings page uses.

Which `value` each option kind takes:

| Option kind | `value` |
|---|---|
| Select | Its choice name (`"Rich"`) or its index. |
| Toggle | `true` or `false`. |
| Slider | A number. |
| Colour | `"#RRGGBB"` or `"#RRGGBBAA"`. |

### `find {key | prefix}`

`find <key>` (or `key=`) lists the live nodes keyed exactly so; `prefix=` lists those whose key starts with it. The list is
in reading order (top, then left). Each entry has:

| Field | Meaning |
|---|---|
| `rect` | The presented rect. |
| `shown` | `SceneStore.IsShown`: on the tree's visible chain (a parked page's nodes are not). |
| `onScreen` | The share of the rect that can be seen, 0..1, after the window and every clipping ancestor (0 when not shown). |
| `actionable` | What `click`, `hover` and `scroll` check (see `click`). An actionable entry has `nth`, the index a pointer verb's `nth` takes for this key; any other has `why`. |
| `kind`, `text?` | The element type and the first text under it (a button's label; an icon glyph prints as `glyph:<name>`, see `dump`). |

```json
{"cmd":"find","args":{"prefix":"zune-band-pivot:"},"id":5}
→ {"id":5,"ok":true,"cmd":"find","nodes":[{"key":"zune-band-pivot:home","rect":{"x":16,"y":48,"w":70.83,"h":44},
   "shown":true,"onScreen":1,"actionable":true,"nth":0,"kind":"Box","text":"Home"}, ...,
   {"key":"zune-band-pivot:recents","rect":{"x":329.6,"y":48,"w":92.78,"h":44},"shown":true,"onScreen":0.76,
   "actionable":true,"nth":0,"kind":"Box","text":"Recents"}],"count":4,"order":"reading order (top, then left); ..."}
```

### `dump {root?, nth?, depth?, keysOnly?, visibleOnly?, text?, max?}`

Dumps the scene as one flat array in depth-first child order. `parent` is an index into that array.

| Arg | Effect |
|---|---|
| `root` | Starts at the node with that key (every match; `nth` picks one). Without it, the dump covers the whole tree plus the overlays that do not hang under the root. |
| `visibleOnly` | `true` by default: skips hidden, parked and transparent subtrees, and nodes with an empty box. |
| `keysOnly=true` | Lists keyed nodes only. |
| `depth` | Limits how deep the walk goes. |
| `text=false` | Leaves out the text blocks. |
| `max` | The most nodes to list; 4000 by default. The reply says `"truncated":true` when the limit cuts the list. |

```json
{"cmd":"dump","args":{"root":"zune-band-pivot:home"},"id":6}
→ {"id":6,"ok":true,"cmd":"dump","nodes":[
    {"i":0,"parent":-1,"depth":0,"key":"zune-band-pivot:home","path":"zune-band-pivot:home","kind":"Component",
     "rect":{"x":16,"y":44,"w":78,"h":40},"flags":{"visible":true,"focusable":true,"hitTest":true},"role":"Button",
     "handlers":["click","key"],"children":1},
    {"i":1,"parent":0,"depth":1,"path":"zune-band-pivot:home/0","kind":"Text","visual":"Text",
     "rect":{"x":16,"y":46,"w":78,"h":36},
     "text":{"string":"home","sizeDip":30,"lineHeight":36,"weight":300,"color":"#FFFFFFFF","case":"Lower",
             "trimmed":false,"natural":{"w":71.4,"h":36}},
     "flags":{"visible":true},"children":0}],
   "count":2,"trimSource":"dwrite"}
```

Each node has these fields. Fields marked with `?` appear only when they apply (no `fill` on a transparent node, no
`border` when it is 0 wide, and so on).

| Field | Meaning |
|---|---|
| `key?` | The node key (`Element.Key`, from `SceneStore.DebugKeyOf`). |
| `path` | The nearest keyed ancestor and the child-index path below it (`NodeDescriber`). |
| `kind` | The element type. |
| `visual?` | The visual kind. |
| `rect` | The presented absolute rect. |
| `opacity?` | The node's own opacity, when below 1. |
| `fill?` | The fill colour. |
| `border?` | `{w, color}`. |
| `corners?` | `[tl, tr, br, bl]`. |
| `clip?` | `true` when the node clips to its bounds, or `{x, y, w, h}` for an authored clip rect. |
| `text?` | `string` (the resolved StringId), `sizeDip`, `lineHeight?`, `weight`, `family?`, `color`, `case?`, `maxLines?`, `wrap?`, `trim?`, `charSpacing?`, `trimmed`, `natural {w, h}`, `overflowX?`, `overflowY?`. A span run has an empty `string` and a `spans` count. An icon glyph (one or two private-use characters) prints as `glyph:<name>`, the `Icons` constant (`Icons.TryGetName`: `glyph:Bell`), or `glyph:U+F5B0` when the engine's table has no name for it, with its `codepoint` beside it. |
| `flags` | `visible`, `focusable`, `focused`, `hovered`, `pressed`, `disabled`, `clipsToBounds`, `scrollable`, `hitTest`. |
| `role?` | The automation role. |
| `handlers?` | `click`, `key`, `pointer`, `char`, `repeat`, `pressed`, `context`, `focus`, `drag`, `wheel`, `selectableText`, `spanLinks`, `gesture`, `clickRequestsContext`, `rowCells`. |
| `scroll?` | `{offset, max, viewport, content, axis, items?}`. |
| `anim?` | `true` while the node has live animation tracks. |
| `children` | The node's child count. |

**`trimmed`** comes from the engine's `AppHost.QueryTextOverflow`. It re-measures the run without the cut, through the
host's own font system, and compares that with the laid-out box:

- A single-line run is trimmed when it is wider than its box.
- A wrapping run is trimmed when it is taller than its box, or than its line budget allows.

`natural` is the size the run wants. `trimSource` says whether the answer rests on real metrics: `dwrite` means it
does; `headless-model` means `--text headless` was used and the answer is not truthful.

### `render {path, scale?, alpha?}`

Writes the current frame of the offscreen surface to a PNG. By default the image is flattened over the window
background; `alpha=true` keeps the translucency. If `scale` differs from the mount's, the server renders at that scale
and then restores the mount's.

```json
{"cmd":"render","args":{"path":"C:/tmp/zune-home-400.png"},"id":7}
→ {"id":7,"ok":true,"cmd":"render","path":"C:\\tmp\\zune-home-400.png","widthPx":400,"heightPx":860,"scale":1,
   "devices":"offscreen (D3D12 (WARP))"}
```

`render` is unavailable with `gpu=none`, under `--text headless`, and when the surface could not be created. In those
cases it answers `{"ok":false,"error":...,"why":...}`.

### `tiers`

Returns the window's current `TierFacts`:

- `window`, `height` and `content` tiers
- `contentWidth`, `viewportWidth` and `viewportHeight`
- `resizing`
- the crossed `rungs`, by id (as `mount`, `status` and `crossing` list them)

It also returns the active `theme` and `kind`. With nothing mounted it fails (`nothing is mounted`): the facts belong to a
mounted window.

### `lint {rules?, root?, nth?, min?, limit?, ...}`

Checks the mounted scene for the flaws a visual audit looks for, and returns structured findings. It reads the scene as it
is, so `settle` first (the verbs that change the screen already do). Only what paints is checked: hidden, parked,
transparent and empty-box nodes are not in it.

| Arg | Meaning |
|---|---|
| `rules` | Which rules run: a comma list (`rules=gutter,clip`) or a JSON array. Default: all nine. |
| `root`, `nth` | Lint only the subtree of the node with that key (`nth` picks among duplicates). Default: the whole tree and the overlays. |
| `min` | Leaves out findings below this severity: `info` (default), `warn` or `error`. |
| `limit` | The most findings one rule reports, 100 by default. The reply lists the rules it cut in `truncated`. |
| `typeFloor` | `type`: text under this size in DIP is an error. 11 by default. |
| `targetFloor`, `targetFloorCompact` | `target`: the smallest hit target outside Compact (32) and at Compact (40). |
| `gapMin` | `rhythm`: stacked text blocks closer than this are cramped. 2 DIP by default. |
| `allowTrim` | `trim`: keys or key prefixes (of the text or any ancestor) whose text may be trimmed. |
| `allowDuplicate` | `duplicate`: labels that may appear twice. |
| `ignoreUnder` | `duplicate`: key prefixes it does not look inside. Default `shell.titlebar,shell.playerbar`. |

```json
{"cmd":"lint","args":{"rules":"gutter,clip","min":"warn"},"id":8}
→ {"id":8,"ok":true,"cmd":"lint","theme":"zune","tier":"Standard","size":{"w":2560,"h":1440},"guide":true,
   "summary":{"error":1,"warn":0,"info":0,"nodes":812,"edges":7},
   "rules":{"gutter":{"checked":4,"error":1,"warn":0,"info":0},"clip":{"checked":143,"error":0,"warn":0,"info":0}},
   "findings":[{"rule":"gutter","severity":"error",
     "message":"the band's first pivot (band.first-pivot): left edge at 48, expected 80 (-32)",
     "keys":["zune-band-pivot:home"],"measured":{"x":48,"delta":-32},"expected":{"x":80,"tolerance":1}}]}
```

**A finding** has these fields:

| Field | Meaning |
|---|---|
| `rule` | The rule that found it. |
| `severity` | `error`, `warn` or `info`. Errors sort first within a rule. |
| `message` | One line, with the numbers in it. |
| `keys` | The nodes involved, by key. A node without a key is named by its dump `path`. Use them with `find`, `dump root=` and `click`. |
| `measured?` | What the scene has, as named numbers (`x`, `w`, `inkBottom`, `gap`, `size`, ...). |
| `expected?` | What the rule wanted (`x`, `w`, `clipBottom`, `minSize`, `minGap`, ...). |

`summary` counts the findings by severity. `rules` gives each rule's `checked` (how many things it looked at) and its
counts, so a rule with no findings can be told apart from a rule that had nothing to check.

**The rules** and what they check:

| Rule | What it reports | Severity |
|---|---|---|
| `gutter` | The active theme's left-edge expectations: the title-bar lead on the tier's gutter, the band's first pivot and the page column's content on the page column (Zune: `ZuneFrameRules.ColumnLead(width, gutter)` in the content column). Off by more than 1 DIP. | `error` (`warn` for an expectation the theme marks advisory, such as Back at Compact) |
| `right-edge` | The theme's right-edge expectations: the band's last row-2 action, the page's right content edge and the play bar's last glyph agree within 1 DIP. Zune checks this from Small to Standard. | `error`, the play bar's glyph `warn` |
| `clip` | (a) Text whose ink runs outside the nearest ancestor that clips it: clipped descenders, a title taller than its fixed box. The ink is the text box, the natural height of an unwrapped run, and the 1.2 × size a tight explicit line height needs. An axis the clipping ancestor scrolls on is allowed (a pivot row fading at the window's edge), and so is everything above it. (b) Sibling boxes that overlap by more than 1 DIP, when both carry text or a target and their parent is not a ZStack or a scroller. | (a) vertical `error`, horizontal `warn`; (b) `warn` |
| `trim` | Text the engine reports trimmed (`text.trimmed`), with its natural size against its box, and whether it is ellipsised, hard-clipped or overflowing. | `warn` |
| `type` | Every distinct size / line height / weight with its count (the type scale), and an error per size under `typeFloor`. Icon glyphs are not text here. | scale `info`; floor `error` |
| `target` | Click targets smaller than 40 × 40 at Compact (32 elsewhere). Sliders, scroll bars, progress bars and inline links are exempt, and a target that fills its interactive parent is that parent's. | `error` at Compact, `warn` elsewhere |
| `duplicate` | The same action visible twice: click targets with the same role and label whose surfaces differ (the first key of the nearest keyed ancestor, up to the first `:`). The title bar and play bar are skipped. Rows of one list share a surface, so they are not duplicates. | `warn` |
| `contrast` | Each text run's declared colour (times its own and its ancestors' opacity) against the colour most of its box is painted in on the rendered frame (the lint presents one frame at rest and reads it back, like `render`). Body text needs 4.5 : 1; 24 DIP and up, bold 18.66 DIP and up, and icon glyphs need 3 : 1. Skipped, not passed: disabled subtrees, nodes mid-animation, transparent ink, span runs, boxes mostly off the frame, and boxes over a busy background (art, a gradient: no colour holds half the box). A glyph that fills its box is judged on what is left once its own colour is taken out. No offscreen surface (`gpu=none`), no check (`checked` is 0). The page behind a modal scrim is read dimmed against its undimmed ink, so lint a dialog with `root=` set to it. | `warn` |
| `rhythm` | The vertical gaps between consecutive text blocks stacked in one column (`info`, one per container), and a warning for a gap under `gapMin`, or an overlap. | `info` / `warn` |

The edge rules need the theme's help: the theme registers a **lint guide** (`IThemeLintGuide`, registered through
`ThemeInspection.RegisterLintGuide` when the theme is built). A guide describes where its edges belong in the current window
tier, as data, and core resolves them against the scene by key. A theme with no guide (Classic) is not checked by `gutter` and
`right-edge`; the reply says `"guide":false`. An edge whose node is not on screen is not checked.

### `crossing {from, to, frames?, items?, max?, ...}`

Resizes across a tier edge the way the user does, and reports what broke on the way. `from` and `to` are
`{"w":1440,"h":960}` objects (text form: `from=1440x960 to=1440x400`).

1. It mounts at `from` (or resizes the mount you have, and navigates when `route`, `uri` or `deeplink` is given), settles, and
   remembers every shown keyed node and its rect. With nothing mounted, the mount args apply (`theme`, `kind`, `route`, `gpu`,
   `scale`); `remount=true` forces a fresh mount.
2. It resizes to `to`. **Narrowing** goes through the OS modal size
   loop: a keep-alive paint inside the loop, then `frames` more keep-alive paints outside it (default 1), which still lay out
   the old tier at the new size, then one more keep-alive paint, the **commit frame**, where the new tier lands. **Widening**
   skips the loop: `frames` keep-alive paints, then a normal frame as the commit.
3. It settles, lints the settled scene (the same options as `lint`) and replies.

| Reply field | Meaning |
|---|---|
| `tierBefore`, `tierResized`, `tierAfter` | `{window, height, content, rungs}` at the start, after the resized frames and after the commit; `rungs` lists the crossed rung ids, as `tiers` does. |
| `committed` | The tier or a rung changed. `heldOldTier`: the resized frames still read the old tier (the engine's rule). |
| `counts` | `tracked`, `changedOnCommit`, `glided`, `snapped`, `jumped`, `remounted`, `vanished`, `appeared`, `recounted`. |
| `remounted` | Keyed nodes (unique before) that are a different node after the settle: identity, state and motion lost. |
| `vanished` | Keyed nodes that are gone or no longer shown after the settle. A tier that drops a part on purpose shows up here. |
| `appeared` | Keys shown after that were not before. |
| `recounted` | Keys that repeat (rows) whose count changed: `{key, before, after}`. |
| `snapped` | Nodes whose laid-out rect changed **on the commit frame** (compared with the resized frame, so plain window resizing does not count) with nothing animating it: no track on the node or an ancestor, and the presented rect already equal to the laid-out one. Each has `from`, `to` and `dx`, `dy`, `dw`, `dh`. Only the topmost of a subtree is listed. |
| `jumped` | The snapped nodes that only moved (same size): the content below something that changed height, shifting at once. |
| `settle` | `{frames, settled, busy?}` for the final settle (`busy` as in Settling, when it hit `max`). |
| `lint` | The settled scene's lint: `theme`, `tier`, `size`, `guide`, `summary`, `rules`, `findings` as in `lint`. |
| `reducedMotion` | Under reduced motion nothing glides, so every change reads as snapped. |
| `items` | An arg: how many entries each list shows, 40 by default. |

```json
{"cmd":"crossing","args":{"from":{"w":1440,"h":960},"to":{"w":1440,"h":400},"theme":"zune","route":"home"},"id":9}
→ {"id":9,"ok":true,"cmd":"crossing","from":{"w":1440,"h":960},"to":{"w":1440,"h":400},"narrowing":true,"resizedFrames":1,
   "tierBefore":{"window":"Standard","height":"Regular","content":"Standard","rungs":["home.daylist.full"]},
   "tierResized":{"window":"Standard","height":"Regular","content":"Standard","rungs":["home.daylist.full"]},
   "tierAfter":{"window":"Standard","height":"Short","content":"Standard","rungs":[]},"committed":true,"heldOldTier":true,
   "reducedMotion":false,"counts":{"tracked":402,"changedOnCommit":31,"glided":30,"snapped":1,"jumped":0,"remounted":0,
   "vanished":0,"appeared":0,"recounted":0},"remounted":[],"vanished":[],"appeared":[],"recounted":[],
   "snapped":[{"key":"zune-home-daylist","from":{"x":48,"y":132,"w":1344,"h":220},"to":{"x":48,"y":132,"w":1344,"h":160},
               "dx":0,"dy":0,"dw":0,"dh":-60}],"jumped":[],"settle":{"frames":24,"settled":true},
   "lint":{"theme":"zune","tier":"Standard","size":{"w":1440,"h":400},"guide":true,"summary":{...},"rules":{...},"findings":[...]}}
```

`python -I tools/inspect.py --findings -c "mount w=400 h=860 theme=zune" -c "lint min=warn"` prints a lint (or the lint of a
crossing) as one line per finding (an icon glyph in a message prints as `glyph:U+XXXX`).

**A verdict: `--gate`.** `python -I tools/inspect.py --gate error -f lint.txt` runs the file as usual, then prints one
summary line and exits **1** when any lint (or crossing's lint) reported a finding at or above the gate's severity
(`info`, `warn` or `error`; a finding whose `outcome` is `suppressed` does not count), **2** when any reply was `ok:false`
(that wins), else **0**. The finding counts come from the replies, so the lint's own `min` and `limit` apply first.

```text
gate error: 1 lint reply, 0 error, 18 warn, 0 info (0 at or above error); 0 ok:false replies -> exit 0
gate warn: 1 lint reply, 0 error, 18 warn, 0 info (18 at or above warn); 0 ok:false replies -> exit 1
gate error: 0 lint replies, 0 error, 0 warn, 0 info (0 at or above error); 1 ok:false reply -> exit 2 (first: lint: unknown arg 'rule' for lint (did you mean 'rules'?))
```

Without `--gate` the client exits 0 whatever the replies say (`--fail-fast` stops at the first `ok:false` with exit 2).

### `trace {frames, keys?, prefix?, scroll?, every?, ...}`

Runs `frames` frames on the host and returns one row per frame plus a summary. **The inspect host runs on a fixed 16 ms fake
frame clock: frames are not real time, and the `ms` fields are the CPU cost of each frame while the motion plays on that clock**
(Debug builds and a cold JIT inflate the first frames; trace twice and read the second).

| Arg | Meaning |
|---|---|
| `frames` | Frames to run (1 to 5000; positional: `trace 90`). |
| `keys`, `prefix` | Nodes to watch: exact keys and key prefixes (comma list or JSON array; a prefix may carry control characters, so use the JSON form for `"2"`). Matches are re-resolved every frame, so a page that mounts mid-trace is picked up. At most `max` nodes (20 by default, 500 at most); duplicates of a key are `key#1`, `key#2`. |
| `scroll` | `main` (the shown vertical scroller with the most room, re-picked each frame) or a node key (the first scroller under it). |
| `every` | Emit every Nth row; the summary always covers every frame. `rows=false` leaves only the summary. |
| `jump`, `snap` | Thresholds in DIP: 2 and 0.5. |
| `census` | `true` turns the host's render census on: a slow frame's row then carries `census`, the components that made it slow. Off by default (it costs frame time). |
| `wheel`, `wheelEvery`, `wheelCount`, `wheelKey`, `wheelX`, `wheelY` | Mouse-wheel input during the trace: notches (a number, or a list that cycles: `3,3,-6` or `[3,3,-6]`), one event every `wheelEvery` frames, `wheelCount` events, aimed at `wheelKey`, a point, or the traced scroller. |
| `pan`, `panFrames`, `panStart`, `panEvery`, `panCount`, `panKind` | A touchpad (or `panKind=touch`) contact: DIP per frame (cycled; a number or a list) for `panFrames` frames from `panStart`, then the lift (fling or edge spring). `panEvery`/`panCount` repeat it. The wheel clamps hard at the bounds; only a contact stream rubber-bands. |

Any settling verb takes `trace=N` (and the options above) to trace instead of settling: `goto route=home trace=90`. Pass
`settle=false` (implied by `trace`) so the transition frames are visible. `click`/`scroll` start the trace on the frame the release /
notch lands on.

Each **row**: `f` (frame in the trace), `abs`, `animating`, `ms` (`wall`, `cpu` = flush+layout+anim+record, `flush`, `reactiveFlush`,
`virtualRealize`, `layout`, `solve`, `layoutEffects`, `connected`, `reflowSeed`, `anim`, `record`, `imagePump`, `realizeCatchup`, `submit`,
`longestUnit`), `n` (`measure`, `arrange`, `scopedMarks`, `escapes`, `localResolves`, `rootSolves` = escapes not absorbed locally,
`fullLayoutInferred`, `components`, `reactiveUnits`, `bindingFires`, `bindingWrites`, `textShapes`, `textShapeMisses`, `nodesVisited`,
`drawNodes`, `imageApplied`, `gc0/1/2`), `nodes` (`w` = index into `watch`, `lay` and `pres` = `[x,y,w,h]` laid-out and presented rects,
`track` = a track is live on the node or an ancestor, `shown`, `op` = opacity multiplied up the chain) and `scroll` (`offset`,
`lastShown`, `velocity`, `max`, `content`, `viewport`, `motion`, `user`).

The **summary**: `settledAt`, `rootSolveFrames`, `escapeFrames`, `fullLayoutFramesInferred` (measures with no dirty marks), measure and component
totals, the `max` frame for layout/cpu/wall/record/flush/submit, `cpuOver8ms/16ms`, `snaps` (laid-out and presented rect changed with no track),
`jumps` (presented rect moved over `jump` DIP in a frame under a track; `prevMag` tells a discontinuity from steady motion), and `scroll`:
`reversals`, `overshoots` (runs outside [0, max], with the deepest point), `stalls`, `uneven` deltas, `layoutDuringScroll` and `deltas`
statistics. `python -I tools/inspect.py --trace ...` prints the summary as text.

## Typical workflows

### Audit a page at four sizes

```text
mount w=400  h=860  theme=zune route=home
tiers
dump keysOnly=true
render path=out/home-400.png
mount w=800  h=960  theme=zune route=home
dump keysOnly=true
mount w=1440 h=960  theme=zune route=home
dump keysOnly=true
mount w=2560 h=1440 theme=zune route=home
dump keysOnly=true
```

What to compare across the four sizes (`lint` does all of this for you, per mount):

- The left edges of the title-bar lead, the band and the first content block, against the tier's gutter (`gutter`).
- Every `text` with `trimmed: true` (`trim`).
- The distinct `sizeDip` / `lineHeight` pairs, which make up the type scale (`type`).
- The hit targets under 40 DIP at Compact (`target`).

`python -I tools/inspect.py -f audit.txt --pretty` runs such a file.

### Lint a page at four sizes

```text
mount w=400  h=860  theme=zune route=home
lint min=warn
mount w=800  h=960  theme=zune route=home
lint min=warn
mount w=1440 h=960  theme=zune route=home
lint min=warn
mount w=2560 h=1440 theme=zune route=home
lint min=warn
```

`python -I tools/inspect.py --findings -f lint.txt` prints each finding as `[error] gutter: message <keys>`; add
`--gate error` for an exit code. Follow a finding's `keys` with `dump root=<key> depth=2` to see the node, or
`render path=...` to see it.

### Check a crossing

One verb does the sequence below and compares for you (see `crossing`):

```text
crossing from=1440x960 to=1440x400 theme=zune route=home
```

Read it by hand with:

```text
mount w=660 h=860 theme=zune route=home settle=true
find prefix=zune-band-pivot:
resize w=620 h=860 settle=false
frames 1
find prefix=zune-band-pivot:
settle
find prefix=zune-band-pivot:
```

Read the results this way:

- A node whose key disappears and comes back across the edge was remounted.
- A rect that differs between the first frame after the resize and the settled state either snapped or tracked an
  animation. Step through with `frames 1` to tell which.
- A content rect that moves by the band's height change, with no `anim`, is a layout jump.

### Click through and inspect

```text
mount w=1180 h=760 theme=classic
find prefix=<a card key family>                  # pick a match with actionable:true; its nth
click key=<that key> nth=<its nth>               # the reply's after.route says where it went
dump root=<the page's root key> depth=3
```

## Notes

- **One process, one boot.** `Shell.Headless.Boot` runs once per process. A mount replaces the previous one; it does
  not reboot.
- **One session.** `Diagnostics.InspectSession` is the UI-thread half, and it owns no thread.
- **One verb table.** `Diagnostics.Inspect.Specs` (`Diagnostics.Inspect.Protocol.cs`) declares every verb's positional
  names, args (type, default, choices, aliases, doc), example, whether it needs a mount and whether it acts. The strict-arg
  check, `help` and the `after` block all read it: a verb that reads a new arg declares it there, or the arg is refused.
  The lint args are one line each in their own marked list.
- **The helpers are shared.** `Diagnostics.InspectKit` holds Until, Settle (and `busy`), Subtree, the visible-rect math
  and the actionability check (`Assess`, deciding through the pure `Diagnostics.Actionability`), Play, the fixture grader
  and the theme activation.
- **The lint rules are pure.** `Diagnostics.InspectLint` (`Diagnostics.Inspect.Lint.cs`) turns a `Snapshot` of nodes into
  findings with no engine and no host.
  `Diagnostics.InspectLintScene` reads the mounted scene into that snapshot and writes the report.
- **No reflection.** JSON is parsed with `JsonDocument` and written with `Utf8JsonWriter`, so the server is AOT-safe.
