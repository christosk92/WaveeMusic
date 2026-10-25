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
- **Text form.** Bare words fill the positional names that no `k=v` on the line has filled, in order: `goto home`,
  `find zune-band-pivot:home`, `frames 30`, `scroll key=zune-home-scroll: 4` (4 is `dy`). `type` takes the rest of the line
  as its text (`type daft punk`), unless the line goes on with a quote or `text=`: `type "daft punk" settle=false`.
  - **Quoting.** A value in double quotes keeps its blanks and is always a string: `lint allowDuplicate="See all"`,
    `find "zune-band-title:Mia Solace"`. Inside the quotes `\"` is a quote and `\\` a backslash; any other backslash
    stays as it is, so a Windows path needs nothing, except a backslash right before the closing quote, which is written
    `\\` (`"C:\dir\\"`; `"C:\dir\"` reads `\"` as a quote and fails as unterminated). A quoted value of a list arg is one
    item, commas and all: `allowDuplicate="Hello, world"`. A hint in a reply that suggests a command quotes a key that
    needs it (`find prefix="zune-band-title:Mia S"`).
  - **Lists.** A value in brackets is a list: `lint rules=[clip,trim] allowDuplicate=["See all",Play]`,
    `trace 60 wheel=[3,3,-6]`. A comma list without brackets (`rules=clip,trim`) works too. Every list reaches the verb as
    strings (`[2024,Play]` allows `2024`). In the JSON form a list is an array; a JSON string is split at its commas.
- **Strict args.** Every verb declares its args (the verb table beside `VerbNames` in `Diagnostics.Inspect.Protocol.cs`),
  and a command that breaks the table is refused before anything runs:
  - An undeclared name: `{"ok":false,"error":"unknown arg 'rule' for lint (did you mean 'rules'?)"}`. Names are
    case-sensitive and a miscased one is pointed at its right spelling (`keysonly`: did you mean `keysOnly`), so
    `mount them=zune` fails instead of quietly mounting the active theme.
  - A value not of the arg's type, choices or range: `bad value 'middle' for button (left|right)`, `bad value 'abc' for n (a
    whole number, 0..100000)`, `bad value '-5' for n (a whole number, 0..100000)`, `bad value '1' in rules
    (gutter|right-edge|...)`, `'rules' names nothing`, `bad value 'crtl' for mods (ctrl, shift, alt or win, joined by +:
    ctrl+shift)`. A value out of range is refused, not clamped. A choice matches in any case and reaches the verb in its
    declared spelling (`theme=ZUNE` is `zune`); a bool takes `true`/`false`, `on`/`off`, `yes`/`no` or `1`/`0`, and so does
    an enum with a yes and a no among its choices (`setup=1` is `setup=true`, `gallery=no` is `gallery=false`).
  - An arg that only applies beside another: `'every' applies only beside trace=` (the trace options on a settling verb;
    `trace=0` traces nothing, so beside it they are refused too: `applies only beside trace=N with N > 0`), `'fromMs' applies
    only beside video=` (`mount`), `'nth' applies only beside key=` (a pointer verb: `nth` and `force` pick among a key's
    matches), `'nth' applies only beside root=` (`dump`, `lint`). `help verb=<name>` lists these as `requires`.
  - Two args that cannot stand together: `'x' and 'key' do not go together` (a pointer verb aims at a key or a point),
    `find key=a prefix=b`, `help verb=lint schema=json`. `help verb=<name>` lists these as `excludes`.
  - A name given twice: `'rules' given twice`. That includes a bare word that fills a name given after it
    (`lint clip rules=trim`: the bare word fills `rules`, then `rules=` comes again). A bare word after the name is given
    has no name left to fill: `lint rules=clip trim` replies `unexpected 'trim' after lint`.
- **`help`** lists the verbs with a one-line summary each. `help verb=lint` (or `help lint`) gives that verb's positional
  names and every arg with its type, default, choices, aliases, `requires`, `excludes`, `min`/`max` and a line of doc, plus
  one example. `help schema=json` returns the whole table (`schema {version, grammar, verbs:[...]}`).
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
as a track plays (the playhead, a visualizer). **Looping tracks** are ambient too: when every live, unparked animation row
is a loop (`AnimFlags.Loop`: a marquee title scrolling, a looping indicator; the engine's `AnimEngine.ActiveNonLoopCount`
is 0), the settle does not wait for them, nor for the `Anim` wake they raise. The replies report `frames` and `settled`,
`ambient:[...]` when the settle saw any of those ambient reasons (motion they drive, a frame-clock ticker, was not waited
for), and `loops:[keys]` when it ended on looping tracks: the nodes they run on (the nearest keyed ancestor), still moving
when the reply was written. Loading indicators are the exception (below): a skeleton's shimmer, an indeterminate
progress bar or a progress ring still holds the settle. A `render` after a settle catches a loop at whatever phase it has
reached. A settle that hit its cap adds `busy`, what still moved:

- `{"key","kind":"anim"}`: a node with live tracks (named by its nearest keyed ancestor). Beside the array, `tracks` is the
  engine's census of the live rows: `anim=N:Channel*k,...`, then `done=`, `parked=` and `loop=` counts when there are any.
- `{"key","kind":"loading"}`: a loading indicator still running (a skeleton's shimmer, a progress bar or ring, until the data or its error lands), with a
  `loading` sentence beside `busy` that names them (see Looping animations below).
- `{"key","kind":"scroll","motion":"Wheel"}`: a scroller in motion.
- `{"kind":"wake","reason":"ImageReady"}`: a wake reason with no node. `FrameNeeded` adds `terms`, the parts of it the
  inspector can see: `themeEpoch` (a theme token was written on the last frame), `freeze`, `engineSettle`,
  `themeTransaction`, or `explicit` (an explicit wake, a posted action, a scroll plan write or a frame owed after paint,
  which the engine does not attribute).
- An empty `busy` with `quietFrames` and a `note`: nothing moved on the last frame, but `max` came before two quiet frames
  in a row (`settle max=1`). `settle max=0` runs no frame: it replies `settled:false` with a `note` saying so, and no `busy`.

Pass `settle=false` to skip the settle and step the frames yourself with `frames`.

**Looping animations.** A loop never ends by design. Where its rows are loops the settle reads them as ambient (above):
Classic's player bar at
window Small below 760 wide lays the title out in an 88 DIP lane (the Compact bar level), "Nothing playing" is 107 DIP, so
the bar's marquee (`np-title`, motion policy F239: an overflowing title always scrolls) loops for as long as the window
stays there; a verb there settles as it does at any other width (a `resize` in 21 frames) and replies `loops:["np-title"]`
(it ran to `max` before). While a track plays, its row's indicator is a loop row too, on every page that lists it, in every
theme: Home after a `play` (Classic and WinUI: the `recent:*` cards; Zune: the `zune-cluster-row:*` row) and Liked Songs
(`c.num`, `winui.row.now`, `zune.row.now`); look for those keys in `loops`. `play` waits for its load before it settles: the
fake load opens a silent voice on the audio chain on the wall clock, which a frame-counted settle used to outrun (the reply then
read the page before the track played: no playing row, Classic's `lyrics` button still hidden, the row starting a verb later).
Its reply carries `phase` (`playing` once the load landed; `loading` if 5 s of wall clock ran out) and `loadFrames` (the
frames stepped while waiting); WinUI and Classic Home `play` then reply with the `recent:*` loops (12 of 12 fresh servers,
WARP and `gpu=none`). Motion
that never ends but is not a loop row (a frame-clock ticker is ambient already; anything else) still runs a verb to `max`,
and `busy` names it: use `settle=false` with `frames N` there, or a small `max`.
Anything else that moves beside a loop (a fade, a FLIP, a glide, a wake reason) is still waited for.

Loading indicators are not ignored: a **loading skeleton's** shimmer (the pulse the engine puts on what a `Skel.Region` shows
while its data is pending), an **indeterminate progress bar** (a looping node under the `ProgressBar` role: WinUI and Classic
search's `omni-progress` while a query is pending) and a **progress ring** (its arc's trim loops). A page that still shows one
has not landed, so the settle waits for the data or its error (WinUI search `type text=ab` settles in about 30 frames on its
suggestion rows `omni-row:0:spotify:artist:ar5` "Sable & Sun" and "Coffee Table Jazz", not after 4 on the progress rows), and a
settle that hits `max` there says `loading` (above): `type text=zzqx` keeps its progress row and runs to `max`. The fake catalogue answers on the wall clock, so a slow page can take
several hundred frames (`goto uri=spotify:album:alq2`, seeded to fail, takes 200 to 900): raise `max`.

Every settling verb, and `settle`, takes `loops`:

| `loops=` | A loop holds the settle |
|---|---|
| `ignore` (default) | Only a loading indicator: a skeleton's shimmer, an indeterminate progress bar or ring. |
| `wait` | Every loop (the settle before P2): a page with a playing row runs to `max`. |
| `skeletons` | None, the loading indicators included, so the settle can end on a skeleton or a spinner. `state kind=loading` and `kind=cold-*` take this when `loops` is not given: their skeleton is the picture (`help verb=state` gives their `loops` no single default for this reason). A loading indicator the settle did not wait for is then listed in `loops` like a loop row (`state kind=loading` replies `loops:["facet-body:0"]`). |

**What changed (`after`).** The verbs that act (`goto`, `click`, `hover`, `key`, `type`, `scroll`, `play`, `option`,
`theme`, `state`, `dialog`) end their reply, when something is mounted, with `after:{route, focused, overlays, hit}`. That
includes `option`'s listing (nothing changed: it reads what is) and a `theme` whose transaction ran out of frames
(`ok:false`):

- `route`: the route the app is on now, in `status`'s form.
- `focused`: the key of the focused node, or null.
- `overlays`: the open overlay layers, top first, as `{"key":"overlay:3","first":"crash-prompt:fake0001"}` (the layer and
  the first key inside it).
- `hit`: the key of the node the pointer resolved to. For `click` and `hover`, what the press lands on (the dispatcher's
  hit test: a node that takes the pointer); for `scroll`, the scroll viewport the wheel drives (the nearest scroller of the
  wheel's axis with room to move under the pointer, as the engine resolves it); for any other verb, what lies under the
  pointer now. Null before any pointer verb on this mount (a mount starts with no pointer), or over nothing that takes
  input.

Keys name the nearest keyed ancestor when the node itself has none.

## Verbs

### `mount {w, h, scale?, theme?, kind?, route? | uri? | deeplink?, gpu?, keepRoute?}`

Mounts the whole app at `w` × `h` DIP. Any mount that is already up is replaced, and a mount after the first does not
inherit what the last one changed. It puts back:

- the `state` vacancy (cleared), the cold playlist a `state kind=cold-*` staged (its seeded rows and header back), and
  the fullscreen stage (closed);
- the tab workspace: every tab but the first is closed and the first is active (not with `keepRoute=true`);
- playback as the boot left it (`Shell.Headless.ResetPlayback`): nothing on the deck, the play log empty, and the queue the
  `--fake` boot seeded (the 12 rows the queue panel, the stage's up next and "Next up" show before anything plays). That
  queue is the fresh state on purpose: the first mount of a server shows those 12 rows, so an emptied queue would make a
  later mount differ from it;
- the video surface a `video=` mount opened: its placement (requested, preferred home, transport owner, through the one
  write path `Video.State.Commit`) goes back to the boot's, and the idle player `player=fake` bound is unbound and disposed.
  `status` shows it as `video{resolved, requested, preferred, transport, player}`;
- every theme's option values (`option id=... value=...`), back to the theme's defaults. An `option` set while nothing is
  mounted is for the next mount, which keeps it.

The first mount of a server finds the boot's own state, which is the fresh one. Not every static is reset, so a fresh
process per configuration is still the rule; `status` counts the `mounts` a server has done so a reused one shows. A mount
without `theme` or `kind` keeps the active ones.

| Arg | Values |
|---|---|
| `scale` | DPI scale, 1 by default, at most 4. |
| `theme` | `classic`, `zune` or `winui` (default: the active theme; a fresh server is on Classic). Zune turns Developer mode on; the others turn it off. |
| `kind` | `dark` or `light`. |
| `grade` | Cover grading: `hue` (a hue per uri), `grey`/`gray` (every cover grey) or `greynow` (only the playing one grey). |
| `gpu` | `hardware` (default: the machine's hardware adapter, WARP when it has none; `devices` reads `offscreen (D3D12)`, or `offscreen (D3D12 (WARP))` on the fallback) or `warp` (WARP forced): the offscreen D3D12 surface, so `render` works. `none`: DirectWrite over the recording device (no pixels; motion checks that render nothing). Same lint and crossing counts on either surface, pixels within anti-aliasing, hardware 1.5-5x faster (10-24: Zune album 1440 3.4 s against 8.0 s). |
| `images` | `fake` (default): the engine's deterministic decoder paints every image as a per-image gradient. `real`: the app's own pipeline (WIC behind the disk-first fetcher) decodes the bundled photographs as they are, so text over a photograph renders, and lints, over the real picture. The fake artist `ar9` (The Longtime) carries a bright header photograph (`assets/covers/header-bright.jpg`, a white sky over a pale ground): the worst case for text on a photograph. |
| `keepRoute=true` | Keeps the current navigation and tabs. Without it, the mount starts from a fresh Home on the first tab. |
| `setup=true` or `signin` | (`1`, `on` and `yes` are `true`.) Mounts a first run: the setup wizard's plate opens over the shell (give `frames` ~120 before `render`). `signin` asks for the sign-in page; today the plate still opens on its first page, so click `next` to reach it. |
| `feedback=bug` or `feature` | Opens the report form over the page. |
| `marquee=on` or `off` | Sets the app's marquee setting (Settings > Appearance) before the mount. |
| `remote=<name>` | The play bar says it is playing on a remote output of that name (`--fake` has no Connect cluster). To turn the output remote on a live mount, use `play remote=<name>`. |
| `panelW=<dip>` | The side panel's stored width (clamped to the theme's drag range when it opens), e.g. `panelW=280` for WinUI's narrowest pane. |
| `video=fullscreen`, `docked`, `floating` or `pip` | Puts the video surface up (`pip` is `floating`) with `spotify:track:tr26` playing: the rich album's first row, which has a music video (`HasVideo`), so the play bar shows its video split and the placement menu opens from it (WinUI: the split's chevron is unkeyed, `click x=1324 y=924` at 1440x960 opens "Where to play the video"). No frames decode offline: the inspector's video resolver never answers while the mount lives, so the join stays in flight (the poster, then the ring and "Loading...") instead of failing to audio; the next mount lets it go. Beside it, `fromMs` sets the track's position (74000), `player=fake` binds an idle engine player, and `join=` picks the join poster's state; without `video` all three are refused. |
| `join=poster`, `working` or `failed` | The join poster as the host's switch events would leave it (`Playback.Video.Phase`, folded into `Video.JoinNow`): `working` the ring and "Loading..." (the default offline), `failed` the fault line ("Can't play this track"), `poster` the artwork alone (a player bound, nothing in flight; it binds the fake player). |
| `chrome=shown` | The chrome at rest with no auto-hide: Zune's hover-revealed video band, the video presenter's transport (`AutoHideTransportControls` off; the transport mounts only with a player: add `player=fake`) and the fullscreen stage's controls (its idle machine is off, so the card, Huge and 4K stage keep their chrome however long you wait: `mount ... stage=lyrics layout=card chrome=shown`, `play ...`, `key f11`). |

The navigation args are the ones `goto` takes.

```json
{"cmd":"mount","args":{"w":400,"h":860,"theme":"zune","kind":"dark","route":"home"},"id":1}
→ {"id":1,"ok":true,"cmd":"mount","frames":41,"settled":true,"size":{"w":400,"h":860,"scale":1},"route":"home",
   "text":"dwrite","devices":"offscreen (D3D12)","render":true,
   "tiers":{"window":"Compact","height":"Regular","content":"Compact","contentWidth":400,"viewportWidth":400,
            "viewportHeight":860,"rungs":[]},"theme":"zune","kind":"dark"}
```

If the offscreen surface cannot be created (no D3D12 or WARP), `devices` reads `headless-gpu+dwrite (<reason>)`. The
text is still true, but `render` answers `ok:false`.

`mount` also takes the fullscreen stage's preferences: `stage=lyrics|visualizer|queue|artist`, `layout=card|large|centered|artist`,
`gallery=on|off` and `heroLyrics=on|off` (the Artist layout's lyrics over the photo; off draws Z-NowPlaying-Artist's name) (set before the mount, the first-run tip marked seen), so `key f11` opens the stage straight on that look.

### `goto {route, arg?} | {uri} | {deeplink}`

- `route` is a route key, the same string a pin or `wavee://open?route=` uses: `home`, `browse`, `search` (with
  `arg`), `liked`, `recents`, `settings`, `album:spotify:album:<id>`, and so on.
- `uri` opens an entity's page.
- `deeplink` runs a whole `wavee://` payload through the OS door (`Shell.ApplyDeepLink`).

```json
{"cmd":"goto","args":{"uri":"spotify:album:4aawyAB9vmqN3uQ7FjRGTy"},"id":3}
```

### `resize {w, h, loop?, settle?, max?, trace?}`

Resizes the window and settles. Use it to check a tier crossing on a live mount.

| Arg | Values |
|---|---|
| `loop=true` | Commits the size through the OS modal size loop, as `crossing` does when it narrows. |
| `settle=false` | Replies at once (`frames:0`, `settled:false`); step with `frames` yourself. |
| `max` | The settle's frame cap (600). With `trace`, the most nodes watched. |
| `trace=N` | Traces N frames from the resize instead of settling; the `trace` args (`keys`, `prefix`, `scroll`, `every`, ...) apply. |

### `frames {n}` and `settle {max?, loops?}`

`frames` runs exactly `n` frames and replies with `{"frames":n,"animating":bool,"scrolling":bool,"wake":[...],"ambient":[...]}`:
`animating` is a live animation track, `scrolling` a scroller in user-driven motion, `wake` the wake reasons that keep a
settle going and `ambient` the ones it ignores (see Settling). With `FrameNeeded` in `wake` it adds `frameNeeded:[terms]`,
as `busy` does. `settle` runs frames until nothing moves for two frames, for at most `max` frames, and replies with
`{"frames","settled"}` (plus `busy` at the cap, `ambient` when it saw any, and `loops` when it did not wait for one; `loops=`
as on every settling verb, see Settling). `frames n` and `settle max` take 0 to 100000; a value outside is refused.

### `reset`

Unmounts, puts back what the mounts changed (as `mount` does: the `state` vacancy, the cold playlist, the stage, the tabs,
the video surface and the fake player, playback and the queue, and the theme options, even one set while nothing was mounted), resets the navigation to a fresh
Home, and makes Classic dark the active theme.

### `unmount`, `status`, `help`, `quit`

- `status` reports whether something is mounted, the frames run so far, `mounts` (how many mounts this server has done:
  above 1 means state that a mount does not clear may carry over), `video{resolved, requested, preferred, transport,
  player}` (the video surface's placement and whether a player is bound; a fresh server reads `None`, `None`, `Docked`,
  `GlobalBar`, `false`), `memory{managedBytes, heapBytes, workingSetBytes, allocatedBytes, threadAllocatedBytes, gen0, gen1, gen2, pauseMs}` (the
  managed heap now, the GC heap at its last collection, the working set, the bytes allocated since start by every thread and by the UI thread alone (exact), the collections per
  generation and the total GC pause), the theme and kind, the route, the text backend and the network leaks. When something is
  mounted, it adds `overlays[{key, first}]` (the open overlay layers, top first, as `after` lists them), the mount facts,
  `animating` and `trimSource`.
- `help` lists the verbs with their summaries; `help verb=<name>` and `help schema=json` describe the args (see Protocol).
- `quit` unmounts and ends the server. A refused `quit` (`quit now=1`: it takes no args) ends nothing. `tools/inspect.py`
  sends nothing after a `quit` and says how many lines it left.

### `click`, `hover {key | x,y, nth?, force?, stableMax?, mods?, button?}`

These aim at a node keyed exactly `key`, or at a point (`x`, `y`, window DIP, no checks). Not both: `x` or `y` beside
`key` is refused, and so are `nth` and `force` without `key`.

- `click` sends a pointer move, a press and a release. `button=right` sends a right click, and `mods=ctrl+shift` holds
  modifiers (`ctrl`, `shift`, `alt`, `win`; any other word is refused before anything moves).
- `hover` only moves the pointer.

**Actionability.** A `key` target must be actionable, the way a user could reach it:

| Check | Refused with `reason` |
|---|---|
| It is shown: live, linked to the root, visible and opaque on its whole chain, with a box (`SceneStore.IsShown`; a parked keep-alive page is not). | `notShown` |
| At least 50 % of its presented rect is visible after the window, every ancestor that clips (`ClipsToBounds`, a scroll viewport) and every ancestor's authored clip rect. | `offscreen` (scroll it into view first) |
| It is enabled, and so is the click owner a press on it reaches (the nearest node up from the hit carrying a click handler, as the dispatcher finds it). A disabled container alone does not disable an enabled button inside it. | `disabled` |
| The hit test at the centre of its visible part lands inside it. A passive target (no pointer handler of its own: a label, an icon, a hint) may also be reached through what owns it: an ancestor, a sibling part of the same click owner, or a hit target painted beneath it (the press passes through it). Not the overlay host's input-blocking layer: a passive part of an open menu or dialog (its header text, its body) that lies over that layer is covered, because a press there reaches the layer (a light-dismiss one closes the menu). | `covered`, naming the key that covers it (or, when that key is the target's own ancestor, the covering part's dump path; the input-blocking layer is named as such) |
| Something takes the pointer at that centre, when the target is meant to take it: refused when the target or an ancestor has a pointer handler but the hit test finds nothing there (hit testing off, a sticky or collapse cut), or when only parts of it have one, elsewhere (a section header whose only link is at its end), or when every part of it that has one and lies under that centre is inert (transparent, hidden or hit testing off below the target: WinUI's `winui-bar-like` and `winui-bar-lyrics` with nothing playing, reserved buttons dimmed by opacity alone, whose centres hit `winui-bar-left` and `winui-bar`). A part away from the centre does not count: a track title whose badge line is hidden (a row without badges) is reached through its row, as a user's press is. A target with no handler anywhere on its chain or in it is accepted with a `note`: nothing reacts there. | `unreachable` |

**Stable.** Then, as Playwright does, the target's presented rect must be the same on two consecutive frames: a target
still moving (an entrance, a FLIP, a wheel glide carrying it) is waited for, one frame at a time, at most `stableMax` frames
(30 by default, 0 to 600; 0 skips the check), and the matches are judged again after the wait. An idle host (no animation
row, no scroller in motion, no wake reason but the caret's and the warm cadence's) owes no frame that could move it, so the
target holds at once and no frame runs. The reply says how it held: `target.stable {how, frames}` with `how` = `idle` (no
frame run), `held` (it held after `frames` frames) or `skipped` (`stableMax=0`). A target that moved on every frame waited is
refused with `reason:"unstable"`, `frames`, its `rect` before the last frame and `now` after it; one that unmounted during the
wait is refused as unmounted. `force=true`, a point target and `scroll` (the wheel needs no still target) skip the check and
print no `stable`.

```json
{"cmd":"scroll","args":{"key":"recent:history","dy":6,"settle":false},"id":6}
{"cmd":"click","args":{"key":"recent:history","stableMax":1},"id":7}       (the glide still carries it)
→ {"id":7,"ok":false,"cmd":"click","error":"'recent:history' is not stable: its rect moved on every one of the 1 frames waited;
   settle first, raise stableMax, or force=true aims at it anyway","reason":"unstable","frames":1,
   "rect":{"x":1138,"y":614.2,"w":266,"h":64},"now":{"x":1138,"y":552.2,"w":266,"h":64}}
{"cmd":"click","args":{"key":"recent:history"},"id":8}
→ {"id":8,"ok":true,"cmd":"click","frames":35,"settled":true,"at":{"x":1271,"y":293.2},"key":"recent:history",
   "target":{"nth":0,"of":1,"matches":1,"onScreen":1,"order":"...","stable":{"how":"held","frames":9}}, ...}
```

The pointer goes to the centre of the visible part. A refusal is `ok:false` with the reason, and `matches` lists every node
keyed so with its `rect`, `onScreen` share, `actionable`, `reason`, `why` and `hit`. An accepted target can carry
`target.note` (a press that passes through it, a passive target over nothing). `force=true` skips the checks (and aims at
the presented centre of a node with nothing visible); `nth` then counts every match, still in reading order.

**`nth`** counts the actionable matches only (every match with `force=true`), in **reading order**: top first (to the whole
DIP), then left. `find <key>` lists the matches in the same order with the `nth` each one answers to. The reply says which it
took: `target {nth, of, matches, onScreen, order, forced?, note?, stable?}`, beside `at` and `key`; acting verbs add `after` (see
Protocol).

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

`type` sends character input to whatever has focus. The text form takes the rest of the line, spaces kept; to add args
(`settle=false`, `trace=N`), quote the text or use `text=`:

```text
key ctrl+f
type daft punk
type "daft punk" settle=false
key enter
```

### `scroll {key | x,y, dy?, dx?}`

Turns the mouse wheel at the target: `dy` notches, positive toward the end of the content (3 by default). A `key` target
is checked and picked as `click` does (`nth`, `force`), except that nothing needs a handler at its centre: the wheel goes to
the scroller under the pointer either way. The settle waits for the scroller to stop, so the reply's
`scroll {offset, max}` (with `key`: the first scroll viewport under that node) is where the glide ended.

### `drag {key | x,y, dx?, dy?, steps?, button?, mods?}`

A mouse drag as one command, the way a hand makes it: the pointer moves onto the press point (`key`'s visible centre, checked
and picked as `click` does: `nth`, `force`, `stableMax`; or `x`,`y`), the button goes down, the pointer travels `dx`,`dy` DIP in
`steps` equal moves (8), one per frame, and the button comes up at the end point; then the settle. The reply has `at` (the press),
`to` (the release) and `steps`. `trace=N` traces N frames from the release (a drop's settle); to trace the frames of the drag
itself use `trace`'s own `drag=` args (`drag`, `dragFrames`, `dragStart`, `dragKey`/`dragX`/`dragY`, `dragDy`: DIP per frame,
cycled).

```text
drag key=winui-bar-volume-slider dx=-60 steps=6      # the WinUI volume thumb to 0 (the glyph turns muted)
```

### `play {uri?, fromMs?, next?, ago?, remote?}`

Plays one fake track or episode as its own context. This is the headless probe's recipe: `BuildContextQueue`, then
`Queue.Replace`, then `Playback.PlayNow`. `next=N` (a `spotify:track:tr<k>` uri) adds the N tracks `tr<k+1>`... to the same context, so the
stage's "up next" page has rows. The lyrics media clock runs in the harness frame clock's epoch (`Shell.Headless.Boot`), so a playing
track follows its lyrics: render the stage while playing, no pause needed. `ago=N` backdates the play in the play log by N minutes (the Up next page's "played before this" reads it).

`remote=<name>` turns the output remote on the live mount, as a Connect transfer does while playing (`remote=off` makes it local again); without a
`uri` only the output changes. The fake load lands on the wall clock; `play uri=` waits for it (up to 5 s) before it settles and replies
`phase` and `loadFrames` (above), so the next command reads the playing bar; the remote line shows through the load as the title and artists do.

### `state {kind, page?}` and `dialog {kind}`

`state kind=empty|nomatch|error|offline|none` shows the shared vacancy in place of the page (`none` puts the page back, and steps back off the
pending search a `loading` opened while it still shows: its skeleton never lands, so every verb on it would settle to `max`); `loading` opens a search for
`zzqx`, which the fake catalogue never answers: its request stays in flight, so the page stays on its skeleton (the chips, the top result and the
rows; it replies `loops:["facet-body:0"]`), and `loading` and the `cold-*` states settle on their skeleton, `loops=skeletons` unless you pass
`loops`. Any other search query is answered from the fake catalogue by name (every fake row whose name contains it: `easy` finds the "Easy"
single and "Easy On Saturday"); a facet with no match is that facet's empty, a query with none "No results found". The search box's suggestions
come from the same catalogue (`--fake` installs that source; a live build asks Spotify), and `zzqx` keeps them on their progress row.
`goto uri=spotify:artist:arzzqx` is the pending artist. `offline-rows` keeps the page and reads the session as down in its rows.
The reply's `leftOpen[{key, first}]` lists the overlays the state opened (Zune Compact's `loading` leaves the omni flyout up):
`lint` fails them as `overlay.open` until `key esc` closes them.
`mount` and `reset` clear the vacancy. `dialog kind=crash|hang|report|lightbox` opens the crash prompt (over a made-up bundle, through the shell's own
overlay host), Report a problem, or the artist gallery's lightbox over the bundled photographs (the bright one first: text and chrome over a
photograph) over the **current** page: it never navigates, and the reply's `over` names the page underneath. **The lightbox needs a
`mount ... images=real`**: under the default fake decoder its photographs are flat gradients and it cannot be reviewed. Lint the lightbox with `root=` its key,
so the page under the modal is not judged. `key esc` closes it.

**A page's own loading and empty state: `page=`.** `state kind=loading|empty page=<p>` shows the page's OWN state instead of the
shared vacancy: `page=albums|artists|podcasts|audiobooks` (`library` is albums) writes the account's relations behind that Library tab
back to Unknown (`loading`: the navigator's skeleton, as on a cold start; the fake never answers the ask again) or to Complete with no
rows (`empty`: the tab's empty state), then opens the tab (`Entities.Fake.Shelves.cs`; Artists stages followed artists, saved albums and
likes, Podcasts and Audiobooks share saved shows); `page=liked` does the same for Liked Songs (the account's likes: its loading band, or its
empty state). `page=show` opens the pending show `spotify:show:shzzqx` (loading: header and rows on
their skeleton) or the show with no episodes `sh8` (empty); `page=episode kind=loading` opens the pending episode `epzzqx` (an episode has
no empty state). `loading` settles on the skeleton (`loops=skeletons`). The next `mount` (or `reset`) puts the account's rows back.

```text
state kind=loading page=library      # W-SkeletonList: the Albums navigator's skeleton
state kind=empty page=podcasts       # W-EmptyLibrary: Library, Podcasts with no followed show
state kind=loading page=show
```

**Staged playlist loads.** Every fake list is seeded whole, so a fake page never shows the real client's staging. The cold playlist
(`Entities.Fake.Cold.cs`, `spotify:playlist:pl6`) stages one, one step per command: `state cold-list` (header known, length and rows not:
the meta line loads, the rows shimmer), then `goto uri=spotify:playlist:pl6`, then either `state cold-rows` (the membership lands, rows 1
and 4 know their face, the rest in flight: the hero arm (Compact, and Classic/Zune's vertical page) paints rows 1 and 4 over the
others' placeholders; the two-column arm keeps the whole first page on its shimmer until the first page's faces are in,
`TableRules.RowsPending`'s hot-row gate, by design) or `state cold-warm` (the membership lands with every row restored from disk: faces known,
credits in flight, as a warm open), then `state cold-faces` (the rows' fields land). `state cold-head` instead of `cold-list` also takes the
HEADER away (title, description and owner unknown, Identity not known; the cover stays: a deep link's first frames), so the page shows its
pending head; `cold-rows` or `cold-warm` lands the header with the membership (one answer). **Any detail page's header**: `state kind=head
uri=<album|playlist|show|episode uri>` takes that page's title and every field it knows away and opens it (a cold open's head: no title, publisher,
rating or cover; its rows stay), and `state kind=head-land` puts them back on the same page, so `state kind=head-land trace=40 prefix=...` traces the head landing
in place (`Entities.Fake.Cold.cs`; the next `mount` puts it back too). A `state` with `trace=N` (or `settle=false`) runs no frames before its
trace, so the trace starts on the frame the state lands. Pass `trace=N` on a `state` command to trace its
frames. **To catch the meta-loading step** (the header known, the meta line and the rows on their shimmer), let the goto settle on the
skeleton: `state cold-list`, then `goto uri=spotify:playlist:pl6 loops=skeletons` (it replies `loops:["rail:meta", "list:pl:..."]`);
`settle=false` plus `frames 4` instead catches the OUTGOING page mid-exit, not pl6. Without `loops=skeletons` the goto waits on the
shimmer up to `max`, and nothing answers the staged list, so a long enough wait ends on its error row ("Couldn't load these songs" +
Retry: the product's answer to a list that never comes, not a skeleton fault). A real stale list also HOLDS its rows on the open's revalidation for up to `ListFreshness.BlockingBudgetMs` (1.5 s wall time):
`goto uri=spotify:playlist:pl2` from another playlist with `trace=200` catches the hold's release (the frame its rows land) inside the trace.

### `theme {id}`

- Mounted: runs the user's live theme transaction (`ThemeHost.Switch`: prewarm, freeze, remount, thaw). The reply
  carries the `outcome`.
- Not mounted: selects the theme for the next mount.

`max` caps the transaction at 900 frames by default (`option`: the apply), and, when given, the settle after it too
(otherwise that settle's 600). **`trace=N` measures the switch itself**: the traced frames are the ones that run the transaction,
from the frame after the request (watch with `prefix=`), and `transactionFrames` counts to the frame it completed on (`theme zune
trace=120 prefix=shell.`: `measureTotal` 2376 over the swap, where it read 0 when the trace began after the switch).
**A switch's own frames are not gated.** A theme switch replaces templates by design (owner decision: it resets page UI state):
the traced frames the transaction holds under its freeze are listed in `trace.switchFrames`, and the remounts there (family
`theme-switch`, `counts.remountedSwitch`) and the keys that moved there (`counts.movedInSwitch`) are reported, never counted by
`--gate`. Every other traced frame is gated as any trace is: a move on the frames around the switch still fails
(`mount ... theme=zune route=albums`, `theme classic trace=60 prefix=lib:,shell. max=300 drag=6 dragFrames=8 dragStart=20
dragKey=lib:grip`: switch frames 0-3 exempt, the drag's snaps on frames 21-27 fail the gate).

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

### `setting {tab?, id?, value?}`

The app's own settings (a theme's options are `option`). `setting` lists every Settings row as data (`SettingsDoc`): `{tab, section,
id, kind, value, writer, readOnly?, enabled?, reason?}`; `tab=appearance` lists one tab's. `setting id=<row>` reads one row;
`setting id=<row> value=<v>` sets it through the row's own writer, the verb a Settings view calls (a toggle's `true`/`false`, a
slider's number, a choice's, a segmented row's or a picker's index; an action runs with no value), and bumps the page epoch when the
writer asks the page to re-read, as a view does: a mounted Settings page, the lyrics stage and the bar read the new value as after a
click. A value set on a live mount is that mount's: the next `mount` (or `reset`) puts it back through the row's writer (toggles,
sliders, choices). One set with nothing mounted is for the next mount, which keeps it. An id on several tabs wants `tab=`; a read-only row or a module's own card (`aiLyrics`' per-file verbs) is refused.

```text
setting id=lyricsBlur value=0        # the lyrics blur off (0..100)
setting id=marquee value=false
setting tab=appearance               # the tab's rows with their values
```

The Settings tab strips key their items (`settings:tabitem:<slug>`: Classic's areas, WinUI's tabs; Zune's section list is
`zs:link:<slug>`), so `click key=settings:tabitem:appearance` switches the tab the way a user does (`goto route=settings
arg=appearance` opens it directly).

### `find {key | prefix, visibleOnly?, depth?}`

`find <key>` (or `key=`) lists the live nodes keyed exactly so; `prefix=` lists those whose key starts with it. The list is
in reading order (top, then left). Two args cut a broad prefix down (`find prefix=stage:` alone lists 30 full-window layers):
`visibleOnly=true` keeps only nodes that are shown and at least partly on screen, and `depth=N` leaves out a match nested inside
more than N other matches (`depth=0`: the outermost matches only, so `find prefix=stage: depth=0 visibleOnly=true` lists the
stage's top layers and not every part inside them). When they leave some out, `matched` is the count before they did. `find` used to be a prefix search, so an exact miss that `prefix=` would answer says so:
`"hint":"0 nodes keyed exactly 'zune-band-pivot:'; 4 keys start with it: find prefix=zune-band-pivot:"`. Each entry has:

| Field | Meaning |
|---|---|
| `rect` | The presented rect. |
| `shown` | `SceneStore.IsShown`: on the tree's visible chain (a parked page's nodes are not). |
| `onScreen` | The share of the rect that can be seen, 0..1, after the window and every clipping ancestor (0 when not shown). |
| `actionable` | What `click` and `hover` check (see `click`; `scroll` needs no handler at the centre). An actionable entry has `nth`, the index a pointer verb's `nth` takes for this key, and sometimes a `note`; any other has `why`. |
| `kind`, `text?` | The element type and the first text under it (a button's label; an icon glyph prints as `glyph:<name>`, alone or inside words, see `dump`). |
| `template?` | The template that drew the node: the id of the nearest view slot at or above it (`winui.shell.frame`, `classic.lyrics.view`). A `classic.*` template under WinUI or Zune is a stand-in (`views` lists them all). |

```json
{"cmd":"find","args":{"prefix":"zune-band-pivot:"},"id":5}
→ {"id":5,"ok":true,"cmd":"find","nodes":[{"key":"zune-band-pivot:home","rect":{"x":16,"y":48,"w":70.83,"h":44},
   "shown":true,"onScreen":1,"actionable":true,"nth":0,"kind":"Box","text":"Home"}, ...,
   {"key":"zune-band-pivot:recents","rect":{"x":329.6,"y":48,"w":92.78,"h":44},"shown":true,"onScreen":0.76,
   "actionable":true,"nth":0,"kind":"Box","text":"Recents"}],"count":4,"order":"reading order (top, then left); ..."}
```

### `dump {root?, nth?, depth?, keyDepth?, keysOnly?, visibleOnly?, text?, max?}`

Dumps the scene as one flat array in depth-first child order. `parent` is an index into that array.

| Arg | Effect |
|---|---|
| `root` | Starts at the node with that key (every match; `nth` picks one). Without it, the dump covers the whole tree plus the overlays that do not hang under the root. |
| `visibleOnly` | `true` by default: skips hidden, parked and transparent subtrees, and nodes with an empty box. |
| `keysOnly=true` | Lists keyed nodes only. |
| `depth` | Limits how deep the walk goes, counting every listed node (each unkeyed wrapper box is a level). |
| `keyDepth` | Limits the walk to this many KEYED levels below the root, whatever the boxes between: `dump root=lyrics:stage keyDepth=2` reaches the lyric lines (`ll1:0:N`, 14 boxes down), where `depth=12` lists none. |
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
| `template?`, `view?` | On the first node of a view slot (its anchor, or the first node listed under it when the anchor has no box): the template id the theme's registry gave the slot and the view key id (`"template":"classic.lyrics.view","view":"Lyrics.View"`). |
| `visual?` | The visual kind. |
| `rect` | The presented absolute rect. |
| `opacity?` | The node's own opacity, when below 1. |
| `fill?` | The fill colour. |
| `border?` | `{w, color}`. |
| `corners?` | `[tl, tr, br, bl]`. |
| `clip?` | `true` when the node clips to its bounds, or `{x, y, w, h}` for an authored clip rect. |
| `text?` | `string` (the resolved StringId), `sizeDip`, `lineHeight?`, `weight`, `family?`, `color`, `case?`, `maxLines?`, `wrap?`, `trim?`, `charSpacing?`, `trimmed`, `natural {w, h}`, `overflowX?`, `overflowY?`. A span run has an empty `string` and a `spans` count. An icon glyph (one or two private-use characters) prints as `glyph:<name>`, with its `codepoint` beside it. The name is the engine's `Icons` constant (`Icons.TryGetName`: `glyph:Bell`), else the app's own: the name a theme registered for a glyph it declares (`ThemeInspection.RegisterGlyphNames` in its composition file: `glyph:PlaySolid` for U+F5B0, `glyph:LyricsGlyph`), else `SegoeGlyphs` (`glyph:AddFriend`). Text in the app's own font (`WaveeIcons`, `family` ending `#WaveeIcons`) is named from that font's marks only (`glyph:Lyrics`, `glyph:PlayNext`), because they reuse Segoe code points (U+E900..U+E902). A glyph nothing names prints as `glyph:U+XXXX`. Text that mixes words and glyphs prints each glyph so, set apart from the words by a blank (`Afterglow glyph:U+E8B2`), and `codepoint` lists the glyphs' code points only. |
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

### `render {path, scale?, alpha?, max?}`

Writes the current frame of the offscreen surface to a PNG. By default the image is flattened over the window
background; `alpha=true` keeps the translucency. If `scale` differs from the mount's, the server renders at that scale
and then restores the mount's; `max` caps the settle after that rescale (600).

```json
{"cmd":"render","args":{"path":"C:/tmp/zune-home-400.png"},"id":7}
→ {"id":7,"ok":true,"cmd":"render","path":"C:\\tmp\\zune-home-400.png","widthPx":400,"heightPx":860,"scale":1,
   "devices":"offscreen (D3D12 (WARP))"}
```

`render` is unavailable with `gpu=none`, under `--text headless`, and when the surface could not be created. In those
cases it answers `{"ok":false,"error":...,"why":...}`.

### `tiers {theme?}`

Returns the window's current `TierFacts` (when a mount is up) and the theme's tier `policy` (always):

- `window`, `height` and `content` tiers
- `contentWidth`, `viewportWidth` and `viewportHeight`: the sizes the ladders read now, the window's layout viewport and the
  content width estimated from it (the window less the pane and the docked panels the window tier lays out:
  `WindowTiers.TryLiveSizes`)
- the crossed `rungs`, by id (as `mount`, `status` and `crossing` list them)
- `published`: `{contentWidth, viewportWidth, viewportHeight}` as the facts carry them, the sizes of their last publish (the
  mount or a tier change). The app republishes the facts only when a tier or a rung changes or a modal resize ends at a new size, never
  per resized frame nor for a window move, so after a `resize` that stays in one tier these keep the old size while the three above follow the window.

It also returns the active `theme` and `kind`. The facts belong to a mounted window: with nothing mounted, or with `theme=`
naming another theme, the reply holds the policy alone.

`policy {theme, window[], content[], height[], rungs[], ladders[], crossings[]}` reads the theme's `Rules/Tiers.cs` through its
structure (the published policy of the live mount, else the structure's at its default options), so nobody has to read the source:

- `window`, `content`, `height`: every edge `{at, narrowHold, widenHold, below, above}` (the tiers either side). An edge is crossed
  upward at `at + widenHold` and downward below `at - narrowHold`; a fresh resolve uses `at`.
- `rungs`: `{id, axis, at, narrowHold, widenHold}`; `ladders`: the named sub-ladders `{id, edges[]}`.
- `crossings`: per window and height edge (`W1008`, `H720`), the sizes `crossings` uses each way: `narrow {mount, old, new}` and
  `widen {mount, old, new}` (see `crossings`).

```json
{"cmd":"tiers","args":{"theme":"winui"},"id":3}
→ {"id":3,"ok":true,"cmd":"tiers","policy":{"theme":"winui",
   "window":[{"at":641,"narrowHold":24,"widenHold":0,"below":"Compact","above":"Small"},
             {"at":1008,"narrowHold":24,"widenHold":0,"below":"Small","above":"Standard"},
             {"at":1920,"narrowHold":24,"widenHold":0,"below":"Standard","above":"Huge"}],
   "content":[...],"height":[{"at":720,"narrowHold":24,"widenHold":0,"below":"Short","above":"Regular"}],
   "rungs":[{"id":"winui.discovery.search.beside","axis":"content","at":900,...}, ...],"ladders":[{"id":"detail.table","edges":[...]}, ...],
   "crossings":[{"edge":"W641","axis":"w","narrow":{"mount":705,"old":625,"new":609},"widen":{"mount":577,"old":633,"new":649}}, ...]}}
```

### `views {theme?, filter?, prefix?}`

Which template draws every view key of a theme (the active one, or `theme=`; nothing needs to be mounted) and which structure
layer registered every view and style key. A theme still being built (WinUI) fills the keys it does not draw yet from a stand-in
(Classic's structure): such a key is `"standin":true` and its `layer` is the stand-in's. Style keys (`AddStyle`) carry no
template id, so a style lists its key and layer only. `standins` counts both kinds; the counts cover every key whatever the
filter. `filter=standins` lists only the stand-ins, `filter=own` only the theme's own; `prefix=` only keys starting with it.

```json
{"cmd":"views","args":{"theme":"winui","filter":"standins"},"id":9}
→ {"id":9,"ok":true,"cmd":"views","theme":"winui","layer":"WinUi.Structure","standins":54,
   "counts":{"views":193,"styles":11,"standinViews":52,"standinStyles":2},
   "views":[{"key":"Concert.PlacePicker","templateId":"classic.concerts.place-picker","layer":"Classic.Structure","standin":true}, ...],
   "styles":[{"key":"Detail.Facts","layer":"Classic.Structure","standin":true},{"key":"Show.Reader","layer":"Classic.Structure","standin":true}]}
```

Classic reports `standins: 0` (it has no stand-in); WinUI 54 on 2026-10-24 (52 view keys, `Detail.Facts`, `Show.Reader`). To see
which keys on a page are a stand-in's, `find prefix=<p>` prints each node's `template`.

### `skeleton {loaded, pending?, max?, items?, tolerance?}`

Compares a page's loading skeleton with the page loaded, in DIP. It goes to `pending` (a fixture the fake catalogue never answers,
so the page stays on its skeleton: by default the `zzqx` id of the kind of `loaded`: `spotify:artist:arzzqx`, `album:alzzqx`,
`playlist:plzzqx`, `concert:cczzqx`, `show:shzzqx`, `episode:epzzqx`), settles with `loops=skeletons`, then goes to `loaded` and settles.
Liked Songs (`loaded=spotify:collection:tracks`, `pending=staged` by default) has no other uri: the verb stages the account's likes Unknown
on the page itself and puts them back in place, so its band lands as the real page's does. Every shown key of the content card (unique
keys, the pending id read as the loaded one) and every `Skel.Region` is compared: `moved[{key, skeleton, loaded, dy, dh, dx, dw}]` (a key that moved down, up or sideways, or resized; top
first), `regions[{key, skeleton, loaded, dh}]`, `counts{skeletonKeys, loadedKeys, matched, moved, regions}` and `firstMoved{key,
dy}`: the first key on screen that moved down, the jump a person sees when the data lands (null when nothing did). Report-only:
no gate reads it. `inspect.py --findings` prints it as lines.

```text
mount w=400 h=860 theme=zune route=home
skeleton loaded=spotify:artist:ar3
→ skeleton spotify:artist:arzzqx -> spotify:artist:ar3: 19 of 26 keys moved or resized; the first on screen to move down: zune-artist-hero by 40 DIP
    moved   zune-band  dy=0 dh=40        moved   zune-artist-hero  dy=40 dh=26.25     (40 + 26: the audit's 67 DIP jump at Compact)
```

At 1440 the same command reports nothing on screen moving down (`zune-artist-popular` dy 0, dh +103: its rows grow below it).

### `lint {rules?, scope?, root?, nth?, min?, limit?, outcomes?, baseline?, save?, replay?, ...}`

Checks the mounted scene (or a saved snapshot) for the flaws a visual audit looks for, and returns findings an agent can act
on. **The full reference is [`agent-lint.md`](agent-lint.md)**: every rule and its classes, the finding model (`id`,
`outcome`, `level`, `fp`, `group`, `fix`), scopes and the visible rect, theme guides and intents, baselines, and saved
snapshots. In short:

- It judges **what a person can see** (`scope=screen`, the default: nodes at least half visible after the window and every
  clip); `scope=content` reaches below the fold, `scope=all` is everything captured. It reads the scene as it is, so `settle`
  first (the verbs that change the screen already do).
- Rules have ids (`text.clip`) and, for today's nine, the old names as aliases (`clip`, `trim`, `type`, `target`,
  `duplicate`, `rhythm`, `contrast`, `gutter`, `right-edge`); `rules=` takes ids, aliases, families (`align`, `align.*`) or
  `all`. The nine are level `gate`; the newer ones (`align.inset`, `align.baseline`, `space.scale`, `text.placeholder`,
  `key.hygiene`) are level `todo`, report-only.
- Each finding has an `outcome`: `fail` (the verdict), `review` (the rule could not decide: render and look), `suppressed` (a
  guide intent or a baseline entry matched; counted in the head, listed with `outcomes=all`) or `report` (an info line that
  states what is there).
- **"lint min=error clean"** means `summary.error` is 0: `summary` counts the fail findings (and report lines) of the gate
  rules at or above `min`, listed or not. A review or a todo finding never breaks it.
- `save=<file>` writes the snapshot the rules saw; `replay=<file>` lints a saved snapshot instead of the mount and **needs no
  mount** (the server routes it before its mount check). `frame=<png>` with `replay` gives it the pixels of a `render` of the same
  frame, so contrast and the picture rules judge an older exe's capture too.
- `moving=judge` judges the frame as it is, animating subtrees included (lyrics and the stage's captions loop forever and
  otherwise only ever read `review: still animating`).

| Arg | Meaning |
|---|---|
| `rules` | Ids, aliases, families or `all` (comma list or `[a,b]`; default: all). An unknown word or an empty list is refused. |
| `scope` | `screen` (default), `content` or `all`. |
| `root`, `nth` | Lint only the subtree of the node keyed exactly so (`nth` picks among copies). Not with `replay`. |
| `min` | Leaves out findings below this severity: `info` (default), `warn` or `error`. |
| `limit` | The most findings listed per rule name, 100 by default; cut names are in `truncated`. |
| `outcomes` | The outcomes LISTED: any of `fail`, `review`, `suppressed`, `report`, or `all` (default `fail,review,report`). Every outcome is counted either way. |
| `baseline` | A JSON-lines file of accepted findings (`{fp, rule, reason, by, date}`); a match is `suppressed`. |
| `save`, `replay`, `frame` | Write the snapshot the rules saw / lint a saved snapshot / with `replay`, a PNG render of that frame (its pixels). `lint` only: `crossing` refuses them as unknown args. |
| `moving` | `review` (default: a node mid-animation is a review) or `judge` (this frame is the verdict: looping text). |
| `typeFloor` | `type.floor`: text under this size in DIP is an error. 11 by default. |
| `targetFloor`, `targetFloorCompact` | `target.size`: the smallest hit target outside Compact (32) and at Compact (40). |
| `gapMin` | `space.stack`: stacked text blocks closer than this touch (a report). 2 DIP by default. |
| `allowTrim`, `allowDuplicate`, `ignoreUnder` | `text.trim` keys or prefixes that may ellipsise; `duplicate.action` labels that may repeat and key prefixes it skips (default `shell.titlebar,shell.playerbar`). |

```json
{"cmd":"lint","args":{"rules":"gutter,clip","min":"warn"},"id":8}
→ {"id":8,"ok":true,"cmd":"lint","theme":"zune","tier":"Standard","size":{"w":2560,"h":1440},"route":"home",
   "trimSource":"dwrite","devices":"offscreen (D3D12 (WARP))","guide":{"id":"zune","edges":7,"matched":4,"policies":9},
   "scope":"screen","animating":{"nodes":0,"scrolling":0},
   "summary":{"error":1,"warn":0,"info":0,"nodes":812,"edges":7,"inScope":402},
   "outcomes":{"fail":1,"review":0,"suppressed":0,"report":0},"levels":{"gate":1,"todo":0},"suppressed":{"intent":0,"baseline":0},
   "rules":{"gutter":{"checked":4,"error":1,"warn":0,"info":0,"review":0,"suppressed":0,"level":"gate","ids":["edge.left"]}, ...},
   "findings":[{"rule":"gutter","id":"edge.left","severity":"error","outcome":"fail","level":"gate",
     "message":"the band's first pivot (band.first-pivot): left edge at 48, expected 80 (-32)",
     "keys":["zune-band-pivot:home"],"measured":{"x":48,"delta":-32},"expected":{"x":80,"tolerance":1},
     "fp":"edge.left|zune|home|zune-band-pivot:*|left-of","group":{"count":1,"pattern":"zune-band-pivot:*"},
     "rects":[[48,60,77,32]],"fix":"take the x from the surface's tier record (the guide's numbers), not a literal tuned to one size"}]}
```

Every key the lint reply had before the finding model stays (`rule`, `severity`, `message`, `keys`, `measured`,
`expected`, `summary`, `rules{checked, error, warn, info}`); `guide` is an object now, or `false` for a theme with no guide
(Classic), so a truthiness check still works.

### `crossing {from, to, frames?, items?, max?, ...}`

Resizes across a tier edge the way the user does, and reports what broke on the way. `from` and `to` are
`{"w":1440,"h":960}` objects (text form: `from=1440x960 to=1440x400`); `fromW`, `fromH`, `toW` and `toH` give the
sizes one number at a time instead.

1. It mounts at `from` (or resizes the mount you have, and navigates when `route`, `uri` or `deeplink` is given), settles, and
   remembers every shown keyed node and its rect. With nothing mounted, the mount args apply (`theme`, `kind`, `route`, `gpu`,
   `scale`); `remount=true` forces a fresh mount. Over a mount that is up (no `remount`), only the navigation args apply, and a
   mount-only arg is refused: `'theme' applies only when the crossing mounts (nothing mounted, or remount=true)`.
2. It resizes to `to`. **Narrowing** goes through the OS modal size loop: a keep-alive paint inside the loop, then
   `frames` more keep-alive paints outside it (default 1), then one more keep-alive paint, the **commit frame**. **Widening**
   skips the loop: `frames` keep-alive paints, then a normal frame as the commit. With the default `frames=1` the resized
   paints lay out the old tier at the new size and the new tier lands on the commit frame, as in a drag; with more, the new
   tier lands earlier (see below).
3. It settles, lints the settled scene (the same options as `lint`) and replies.

Before step 1's tracking, the **control** (`control=true`, the default) measures what a plain resize of the same step moves
inside the old tier: from `from` it steps the crossing's distance away from the edge (16 DIP when that leaves the old tier),
settles, and steps back to `from` the same way the crossing will step on (narrowing through the modal loop, widening not).
Every node that step snaps or jumps is recorded by key with how far it moved. At the edge, a move of a key the control moved
as far (within 1 DIP; each control move explains one edge move, largest with largest) is the plain resize's own; every other
is **excess** (`counts.excessSnapped`, `excessJumped`), what the edge adds: `inspect.py --gate` fails a crossing whose excess
is above `--max-moved` (default 0). `control:null` with `controlNote` when no same-tier step exists (every move is then
excess); `control=false` skips it.

Reading a crossing:

- The result does not depend on `gpu`: the default hardware surface, WARP and `gpu=none` report the same hold, tier states, counts and
  lists (the offscreen window reports the window flags the crossing sets, engine `9476a4bbe`). Only `settle.frames` varies:
  it changes with the machine's load, on either device. (A settle's `loops` can differ between servers on either device: the
  playing-row race under Settling.) `gpu=none` is faster; under the default, `render` works in the same
  server after the crossing.
- Start in the old tier. Edges hold differently each way: a narrowing drag keeps Zune's Huge tier down to 1904 wide, where a
  fresh mount is already Standard. The content tier also depends on the path: Classic resized from 3456 straight to 1136 is
  content Small, walked down in 16 DIP steps it is still Standard there. So at an edge, mount inside the old tier, walk to
  the old-side size with `resize`, check `tiers`, then run `crossing` without a remount:
  `mount w=1920 h=960 theme=zune route=home`, `resize 1904 960`, `tiers`, `crossing from=1904x960 to=1888x960`. Run as the
  first command at 1904, the same crossing crosses nothing, and still reads `heldOldTier:true`: compare `tierBefore` with
  `tierAfter`.
- `committed` and `heldOldTier` compare the window and height tiers and the rungs, not the content tier: an edge where only
  the content tier changes reads `committed:false`. `contentCommitted` says where the content tier left its starting band:
  `commit` (on the commit frame, as the window tier does in a drag), `resized` (already in the resized frames: it did not
  hold), `settle` (later, on a frame of the settle) or `none`. Classic 624 to 608 (content Small to Compact) reads
  `committed:false, contentCommitted:"commit"`; WinUI 992 to 976 (window Standard to Small, content Small throughout)
  reads `committed:true, contentCommitted:"none"`.
- Only `frames=1` models a drag: the old tier holds for one keep-alive paint after the resize (after the loop, when
  narrowing), and the next frame commits. From `frames=2` on, the new tier lands inside the resized frames, both ways:
  `heldOldTier` is false, `tierResized` is already the new tier, and the commit frame compares two frames after the
  commit. Zune home 1440x960 to 1440x400 reports 195 jumped at `frames=1`, 176 at `frames=2` and nothing changed at
  `frames=60`.
- A width step with no tier edge already moves nodes on the commit frame (Zune home 1504 to 1488 wide: 8 snapped,
  119 to 125 jumped, depending on how the page got to 1504). That is a continuous reflow landing one frame late, not a
  quantised layout: a shelf (`PagedShelf`) fits its cards to its own measured width, which it learns from its bounds after
  the resized frame's layout, so the strip's height follows on the next frame (the commit frame: Zune's
  `zune-home-wide:*` 2 shorter, and the 119 nodes below it 2 up) and its cards a frame after that (each 4 narrower:
  `trace` over a plain `resize` shows the three frames). The move scales with the step (Zune, `zune-home-column` from 1504: 16 DIP 2
  shorter, 32 4, 64 9; chained from 1392, 96 widening 14 taller), and Classic and WinUI show the same pattern
  (`home:column` 10 shorter at 16 DIP, their shelves glide). Small steps move by rounding, because the 16:9 tiles' height
  is rounded: four 4 DIP steps from 1504 move 0, 1, 0, 1 (127 nodes changed on each step that moves), which add up to
  the 16 DIP step's 2. So a 4 DIP step that moves 1 is no bug; small steps that do not add up to the big step's move, or a
  move that does not scale with the step, are. Set an edge's numbers against a 16 DIP step inside the same tier, reached
  the same way, before you blame the tier change.

| Reply field | Meaning |
|---|---|
| `tierBefore`, `tierResized`, `tierAfter`, `tierSettled` | `{window, height, content, rungs}` at the start, after the resized frames, after the commit and after the settle; `rungs` lists the crossed rung ids, as `tiers` does. |
| `committed` | The window or height tier or a rung changed (the content tier is not compared). `heldOldTier`: the resized frames still read the old tier (the engine's rule). |
| `contentCommitted` | Where the content tier first differed from `tierBefore.content`: `resized`, `commit`, `settle` or `none`. |
| `control` | `{from{w,h}, snapped, jumped}`: the control step (above), or null (`controlNote` says why), or `"off"`. |
| `counts` | `tracked`, `changedOnCommit`, `glided`, `snapped`, `jumped`, `excessSnapped`, `excessJumped` (past the control: what `--gate` counts), `unpainted` (changed at once but draws nothing, nor does anything under it that moved with it: a container whose children glide), `remounted`, `remountedSwap`, `remountedOutside` (what `--gate` fails on), `vanished`, `appeared`, `recounted`. Read the counts, not the length of a list. |
| `remounted` | Keyed nodes (unique before) that are a different node after the settle: identity, state and motion lost. Each has `family`: the key of the swap it sits under (the nearest node this step mounted whose registered enter AND exit are a DECLARED swap cross-fade: opacity only, and carrying the family id `SwapFamilies.Declare` gave it; the four theme helpers `ClassicTierMotion.SwapFade`, `ZuneMotion.SwapFade`, `WinUiDiscoveryMotion.SwapFade` and `WinUiScreenMotion.SwapFade` declare theirs; owner rule "a tier crossing keeps its parts" lets only such a genuinely different tree remount), or null: a rebuilt part. An undeclared opacity fade (an arm cross-fade such as Zune's `ArmFade`, a control's appear) is no family: Zune album W640's 76 arm parts count outside. Listed outside a family first. |
| `late` | Weights (report-only): `counts.excessOffscreen` is how many of the excess moves happened wholly off the screen (below the fold, scrolled out of their viewport: nobody saw them; Classic home H680 narrowing: 142 of 167), and `late[{key, frame, d}]` / `counts.late` the parts whose laid-out rect changed in the 20 settle frames after the commit with nothing easing them (a part landing late, which the commit-frame compare misses). `--gate` reads neither; `--gate-todo` fails on late reflows. A container that changes at once while its children glide is `unpainted` already. |
| `vanished` | Keyed nodes that are gone or no longer shown after the settle. A tier that drops a part on purpose shows up here. |
| `appeared` | Keys shown after that were not before. |
| `recounted` | Keys that repeat (rows) whose count changed: `{key, before, after}`. |
| `snapped` | Nodes whose laid-out rect changed **on the commit frame** (compared with the resized frame, so plain window resizing does not count) with nothing animating it: no track on the node or an ancestor, and the presented rect already equal to the laid-out one, that draw (or have something drawn under them that moved with them). Each has `from`, `to`, `dx`, `dy`, `dw`, `dh`, `excess` (false: the control moved it too), `under` and `underExcess` (the flagged nodes under it, itself included). Only the topmost of a subtree is listed. |
| `jumped` | The snapped nodes that only moved (same size): the content below something that changed height, shifting at once. |
| `settle` | `{frames, settled, busy?, ambient?}` for the final settle (`busy` and `ambient` as in Settling). `frames` varies from run to run with the machine's load. |
| `lint` | The settled scene's lint: `theme`, `tier`, `size`, `guide`, `summary`, `rules`, `findings` as in `lint`. |
| `reducedMotion` | Under reduced motion nothing glides, so every change reads as snapped. |
| `items` | An arg: how many entries each list shows, 40 by default. |

```json
{"cmd":"crossing","args":{"from":{"w":1440,"h":960},"to":{"w":1440,"h":400},"theme":"zune","route":"home"},"id":9}
→ {"id":9,"ok":true,"cmd":"crossing","from":{"w":1440,"h":960},"to":{"w":1440,"h":400},"narrowing":true,"resizedFrames":1,
   "tierBefore":{"window":"Standard","height":"Regular","content":"Standard","rungs":["home.daylist.full"]},
   "tierResized":{"window":"Standard","height":"Regular","content":"Standard","rungs":["home.daylist.full"]},
   "tierAfter":{"window":"Standard","height":"Short","content":"Standard","rungs":[]},
   "tierSettled":{"window":"Standard","height":"Short","content":"Standard","rungs":[]},"committed":true,"heldOldTier":true,
   "contentCommitted":"none","reducedMotion":false,"counts":{"tracked":402,"changedOnCommit":31,"glided":30,"snapped":1,"jumped":0,"remounted":0,
   "vanished":0,"appeared":0,"recounted":0},"remounted":[],"vanished":[],"appeared":[],"recounted":[],
   "snapped":[{"key":"zune-home-daylist","from":{"x":48,"y":132,"w":1344,"h":220},"to":{"x":48,"y":132,"w":1344,"h":160},
               "dx":0,"dy":0,"dw":0,"dh":-60}],"jumped":[],"settle":{"frames":24,"settled":true},
   "lint":{"theme":"zune","tier":"Standard","size":{"w":1440,"h":400},"guide":true,"summary":{...},"rules":{...},"findings":[...]}}
```

`python -I tools/inspect.py --findings -c "mount w=400 h=860 theme=zune" -c "lint min=warn"` prints a crossing's counts first
(EXCESS over the control, remounts outside and inside a swap family, every move), a line per remount (with its family),
vanished key and topmost move (`N nodes under it`), then a lint as a head line (`[route]`, the gate's counts of every fail,
then exactly what the lines below print: `3 warn, 11 warn todo, 1 warn review`) and one line per finding:
`[warn ] target/target.size: ...` (the alias, then the id when they differ). A finding that is not a fail of a gate rule
says what it is: `[warn ] align.baseline (review, todo): ...`, `[error] clip/text.clip (suppressed): ...`. An icon glyph in
a message prints by name as `dump` names it (`glyph:PlaySolid`), or as `glyph:U+XXXX` when nothing names it.

**A verdict: `--gate`.** `python -I tools/inspect.py --gate error -f lint.txt` runs the file as usual, then prints its
warnings, its motion failures and one summary line, and exits **2** when any reply was `ok:false` or **no lint ran** (that
wins; a crossing carries its own lint), **1** when a lint's `fails` (every fail of the rules that ran, listed or not) hold a
**failure of a gate-level rule** at or above the gate's severity (`info`, `warn` or `error`), when a crossing's excess over
its control is above `--max-moved` (default 0) or it remounted a key outside a swap family, or when a trace moved more than
`--max-moved` watched keys at once (`counts.movedAtOnce`: a snap, or an instant run) or remounted one outside a family; else
**0**. A review, a suppressed finding and a report line never count. Add `--gate-todo` to count the todo rules' failures too.
`gate WARNING` lines: a lint narrowed by `rules`, `root`, `outcomes` or `limit`, fails it counted but did not list, a crossing
that crossed no edge or ran without its control, a trace that watched nothing. A server that never comes up, dies mid-run, or
writes a line that is not JSON counts as an `ok:false` (`server: ...`): exit 2 with the summary line, never 1.

```text
gate warn: 1 lint reply, 0 error, 3 warn, 0 info failed (3 at or above warn); 0 motion failures; 0 ok:false replies -> exit 1
gate MOTION: crossing 1904x960->1888x960 moved 2 nodes at once past its control (max 0)
gate error: 1 lint reply, 0 error, 0 warn, 0 info failed (0 at or above error); 1 motion failure; 0 ok:false replies -> exit 1
gate error: 0 lint replies, 0 error, 0 warn, 0 info failed (0 at or above error); 0 motion failures; 0 ok:false replies -> exit 2 (no lint ran: ...)
```

Without `--gate` the client exits 0 whatever the replies say (`--fail-fast` stops at the first `ok:false` with exit 2).

### `crossings {routes?, edges?, dirs?, control?, w?, h?, theme?, kind?, items?, max?, ...}`

Every window and height edge of the theme (`tiers` lists them), both ways, over each route, in ONE server: 32 crossings for WinUI
over home, album, artist and playlist take about 2.5 minutes, against a fresh server per crossing before (75 minutes for the motion
audit's 104). Per row it mounts afresh inside the old tier (`mount` sizes from `tiers`: `at + 64` narrowing, `at - 64` widening; a
height edge at width `w` 1440, a width edge at height `h` 960), then runs one `crossing` from the old side to the new side with
`frames=1`: narrowing from `at - narrowHold + 8` to `at - narrowHold - 8`, widening from `at + widenHold - 8` to `at + widenHold + 8`
(the motion audit's sizes). Each crossing runs its own control step (`crossing`'s `control`, on unless you pass `control=false`), so its
`counts.excessSnapped`/`excessJumped` are what the edge adds over a plain resize of the same distance.

| Arg | Effect |
|---|---|
| `routes` | The pages, a list: a route key (`home`), a `spotify:` uri, or a fixture word: `album` (al2), `artist` (ar3), `playlist` (pl0), `show` (sh0), `episode` (ep0). Default `home,album,artist,playlist`. |
| `edges` | Only these edges by name (`edges=[W1008,H720]`). |
| `dirs` | `both` (default), `narrow` or `widen`. |
| `theme`, `kind`, `gpu`, `scale`, `images`, `grade` | Each row's mount (the active theme by default). `gpu` is `none` by default: a crossing reads rects and the scene, never a frame's pixels, and WARP rendering is what loads the machine; pass `gpu=hardware` to keep pixels for a `render` after. |
| `items` | Entries per list in each crossing reply (8). The lint options (`min` defaults to `error` here, `rules`, ...) pass on to every crossing. |

The reply: `rows`, `failed` (crossings that answered ok:false), `ms`, `table[]` (one line per row: `route, edge, dir, ok, before,
after` as window/height/content tiers, `contentCommitted`, `glided, snapped, jumped, excessSnapped, excessJumped, remounted,
remountedOutside, vanished, appeared, unpainted, ms`) and
`crossings[]`: every crossing reply exactly as `crossing` writes it, with a `row {theme, route, edge, axis, dir, mount, old, new,
ms}` member first, so whatever reads a `crossing` reply reads these the same way: `inspect.py --gate` judges every row as a crossing
(its lint, its excess moves, its remounts outside a swap family, an ok:false row), and `--findings` prints each row's crossing headed
`== theme route edge dir ==`.

```json
{"cmd":"crossings","args":{"theme":"winui","routes":["home","album","artist","playlist"]},"id":4}
→ {"id":4,"ok":true,"cmd":"crossings","theme":"winui","rows":32,"failed":0,"ms":145948,
   "table":[{"route":"album","edge":"W1920","dir":"narrow","ok":true,"before":"Huge/Regular/Standard","after":"Standard/Regular/Standard",
             "contentCommitted":"none","glided":48,"snapped":129,"jumped":7,"remounted":41,"vanished":9,"appeared":4,"ms":4151}, ...],
   "crossings":[{"row":{"theme":"winui","route":"home","edge":"W641","axis":"w","dir":"narrow","mount":"705x960","old":"625x960",
                 "new":"609x960","ms":6205},"id":4,"ok":true,"cmd":"crossing","from":{...},"to":{...}, ...}, ...]}
```

### `trace {frames, keys?, prefix?, scroll?, every?, ...}`

Runs `frames` frames on the host and returns one row per frame plus a summary. **The inspect host runs on a fixed 16 ms fake
frame clock: frames are not real time, and the `ms` fields are the CPU cost of each frame while the motion plays on that clock**
(Debug builds and a cold JIT inflate the first frames; trace twice and read the second).

| Arg | Meaning |
|---|---|
| `frames` | Frames to run (1 to 5000; positional: `trace 90`). A trace that watched no node (no `keys`/`prefix`, or none matched) says so in `warning`: its snaps and jumps read 0 because nothing was watched. |
| `keys`, `prefix` | Nodes to watch: exact keys and key prefixes (comma list or JSON array; a prefix may carry control characters, so use the JSON form for `"2"`). Matches are re-resolved every frame, so a page that mounts mid-trace is picked up. At most `max` nodes (20 by default, 500 at most); duplicates of a key are `key#1`, `key#2`. |
| `scroll` | `main` (the shown vertical scroller with the most room, re-picked each frame) or a node key (the first scroller under it). |
| `every` | Emit every Nth row; the summary always covers every frame. `rows=false` leaves only the summary. |
| `jump`, `snap` | Thresholds in DIP: 2 and 0.5. |
| `census` | `true` turns the host's render census on: a slow frame's row then carries `census`, the components that made it slow. Off by default (it costs frame time). |
| `wheel`, `wheelEvery`, `wheelCount`, `wheelKey`, `wheelX`, `wheelY` | Mouse-wheel input during the trace: notches (a number, or a list that cycles: `3,3,-6` or `[3,3,-6]`), one event every `wheelEvery` frames, `wheelCount` events, aimed at `wheelKey`, a point, or the traced scroller. |
| `pan`, `panFrames`, `panStart`, `panEvery`, `panCount`, `panKind` | A touchpad (or `panKind=touch`) contact: DIP per frame (cycled; a number or a list) for `panFrames` frames from `panStart`, then the lift (fling or edge spring). `panEvery`/`panCount` repeat it. The wheel clamps hard at the bounds; only a contact stream rubber-bands. |
| `drag`, `dragFrames`, `dragStart`, `dragKey`, `dragX`, `dragY`, `dragDy` | A mouse drag (a splitter, a slider): the button goes down at `dragStart` on `dragKey`'s centre (or `dragX`,`dragY`), the pointer moves `drag` DIP per frame along x (cycled list) and `dragDy` down for `dragFrames` frames, then the button comes up. WinUI's pane seam: `drag=4 dragFrames=14 dragX=<pane x - 8> dragY=500` narrows the pane by 56. |

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
`jumps` (presented rect moved over `jump` DIP in a frame under a track; every eased glide has some), `instants` (a run of jumps on
consecutive frames whose largest frame moves at least `instant`=4 DIP and carries 60 % of the run: the change landed at once
while something above it animated, a page reflowing in one frame beside a sliding pane), and `scroll`:
`reversals`, `overshoots` (runs outside [0, max], with the deepest point), `stalls`, `uneven` deltas, `layoutDuringScroll` and `deltas`
statistics. Beside the summary, `counts{watched, snaps, snappedKeys, instants, movedAtOnce, remounted, remountedSwap,
remountedOutside}` and `remounted[{key, frame, family}]`: a lone watched key that is a different node from one frame to the
next, with the swap family fading it in (as `crossing` names it) or null. `inspect.py --gate` fails a trace whose
`movedAtOnce` (snapped or instant keys) is above `--max-moved` or that remounted a key outside a family, and warns on a trace
that watched nothing (pass `prefix=`/`keys=`, and `max=` above 20 for a page). `python -I tools/inspect.py --trace ...`
prints the summary as text: the motion counts, the remounts, the snaps, instants and jumps (30 each), `after`.

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

- The left edges of the title-bar lead, the band and the first content block, against the tier's gutter (`edge.left`).
- Every `text` with `trimmed: true` (`text.trim`).
- The distinct `sizeDip` / `lineHeight` pairs, which make up the type scale (`type.scale`).
- The hit targets under 40 DIP at Compact (`target.size`).
- A section whose ink sits a few DIP off its region's column (`align.inset`, report-only).

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

`python -I tools/inspect.py --findings -f lint.txt` prints each finding as `[error] gutter/edge.left: message <keys>`; add
`--gate error` for an exit code (gate-rule failures only; `--gate-todo` adds the todo rules). Follow a finding's `keys` with `dump root=<key> depth=2` to see the node, or
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

### Boards beside the app: `tools/themes/gallery.py`

`python -I tools/themes/gallery.py --theme zune|winui --exe <run copy> --out <your folder>` mounts every board the map knows
(`tools/themes/gallery-map.<theme>.json`, generated by `gen-gallery-maps.py`: edit the generator, then run it) and renders it beside
the board. It prints an estimate up front and each entry's time; `--only` takes board names or exact entry ids (`Z-Artist.2`);
`--gpu hardware` is the default (WARP only with `--gpu warp`); and it writes contact sheets you can open with an image reader,
`<out>/sheets/<theme>-<family>-<n>.png` (board and renders at one scale, at most 1960 px a side). Hero and photo boards get a
second `images=real` render (`.2`); `ar9` (The Longtime) is the bright-header worst case.

Recipes the map uses, for doing the same by hand:
- Zune volume flyout: it exists only at the medium bar tier (760-1239 wide): mount 800 wide, `click key=volume` (at the full tier
  the glyph mutes). The overflow `click key=more` items have no keys (Queue sits at `(w-80, h-158)` at 800 wide).
- Zune queue panel: `click key=queue` docks it; at Huge the second pane starts on lyrics, `click key=zune-panel-pivot:Friends`
  switches it; Huge 4K shows three panes. Compact panel sheet: `click key=zune-bar-left`, then
  `click key=zune-panel-pivot:Friends force=true` (the pivots scroll).
- Home's podcasts facet at every tier, Small included: `mount route=home arg=podcasts-chip`.
- The video placement menu: right-click the bar's video slot: Zune `click key=video button=right`, WinUI
  `click key=winui-bar-video button=right` (with a `video=` mount, whose track has a video).
- Concerts: `spotify:concert:cc3n0` (`cc0n0` stays on its skeleton). The empty own playlist: `plx0`.
- `dialog kind=hang` is the crash prompt with the hang wording; the "couldn't start" recovery screen is a Win32 TaskDialog shown
  before the engine boots, which no verb draws.

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
  names, args (type, default, choices, aliases, doc), example, whether it needs a mount (`needsMount`; `lint` has `needsMountUnless:"replay"`, since a replay needs
  none) and whether it acts. The strict-arg check, `help` and the `after` block all read it: a verb that reads a new arg declares it there, or the arg is refused.
  The lint args are one line each in their own marked lists: the options `lint` and `crossing` share, and `lint`'s own
  (`save`, `replay`), which `crossing` refuses.
- **The helpers are shared.** `Diagnostics.InspectKit` holds Until, Settle (and `busy`), Subtree and the actionability
  check (`Assess`, deciding through the pure `Diagnostics.Actionability`), what a mount puts back
  (`ResetPlayback` with the boot's queue, `ResetTabs`), Play, the fixture grader and the theme activation.
- **One visible rect.** `Diagnostics.InspectVisible` (`Diagnostics.Inspect.Visible.cs`) computes what of a node a person can
  see: the lint's scopes read its pure half over a snapshot, and the pointer verbs' actionability reads its live half
  (`TryGetVisibleRect`: the engine's `VisibleWindowRect`, transforms and the node's own clip folded in, cut by the window), so
  a target `click` refuses as off screen and a node `lint` leaves out are judged by the same rect.
- **The lint rules are pure.** `Diagnostics.InspectLint` (`Diagnostics.Inspect.Lint*.cs`) turns a `Snapshot` of nodes into
  findings with no engine and no host; [`agent-lint.md`](agent-lint.md) is its reference.
  `Diagnostics.InspectLintScene` reads the mounted scene into that snapshot and writes the report.
- **No reflection.** JSON is parsed with `JsonDocument` and written with `Utf8JsonWriter`, so the server is AOT-safe.
- **Fixtures for the pages agents ask about** (`Entities.Fake*.cs`; the full list is the skill's `reference/fixtures.md`):
  - the own profile: `goto uri=spotify:user:wavee-listener` (`Entities.FakeProfileUri`, the own account's profile address with
    its CurrentUser flag: "Open account", "Only visible to you"); the account chip's Profile row still finds no route under
    `--fake`, because the fake's "me" row is keyed by the bare account name;
  - the longest album title, `spotify:album:2x6LWti2bjYS6AllSomoV7` (`LongTitleAlbumUri`), carries 12 rows (it read "Couldn't
    load these songs" before);
  - `spotify:track:tr26` (al2's first row) and al3's first row have a music video (`HasVideo`): the `video=` mounts play tr26;
  - fake covers paint the same in every process: the fake decoder's hue follows the image source, not the order images were
    requested in, so two servers render one page pixel for pixel alike.
