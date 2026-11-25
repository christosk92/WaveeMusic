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
python -I tools/inspect.py --gate-perf -c "mount w=1440 h=960 theme=winui gpu=warp" -c "perf on" -c "resize 1200 900" -c "perf report"   # a frame-cost verdict
src\apps\Wavee\bin\Debug\net11.0\Wavee.exe --inspect      # or talk to it yourself on stdin/stdout
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
when the reply was written. Loading indicators are the exception (below): a region still on its placeholder, an indeterminate
progress bar (the page's loading bar `page-busy:bar` among them) or a progress ring still holds the settle. A `render` after a settle catches a loop at whatever phase it has
reached. A settle that hit its cap adds `busy`, what still moved:

- `{"key","kind":"anim"}`: a node with live tracks (named by its nearest keyed ancestor). Beside the array, `tracks` is the
  engine's census of the live rows: `anim=N:Channel*k,...`, then `done=`, `parked=` and `loop=` counts when there are any.
- `{"key","kind":"loading"}`: a loading indicator still up (a region on its placeholder, a progress bar or ring, until the data or its error lands), with a
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

Loading indicators are not ignored: a **region still on its placeholder** (a `Skel.Region` whose data is pending: the
placeholder is flat and carries no track, so the settle reads the region's state, `AppHost.IsSkeletonPending`, and only on
frames that would otherwise count as quiet), an **indeterminate progress bar** (a looping node under the `ProgressBar` role:
WinUI and Classic search's `omni-progress` while a query is pending, the page's loading bar `page-busy:bar`) and a **progress
ring** (its arc's trim loops). Under `motion=reduced` a settle now waits for data there too (the old pulse never ran there). A page that still shows one
has not landed, so the settle waits for the data or its error (WinUI search `type text=ab` settles in about 30 frames on its
suggestion rows `omni-row:0:spotify:artist:ar5` "Sable & Sun" and "Coffee Table Jazz", not after 4 on the progress rows), and a
settle that hits `max` there says `loading` (above): `type text=zzqx` keeps its progress row and runs to `max`. The fake catalogue answers on the wall clock, so a slow page can take
several hundred frames (`goto uri=spotify:album:alq2`, seeded to fail, takes 200 to 900): raise `max`.

Every settling verb, and `settle`, takes `loops`:

| `loops=` | A loop holds the settle |
|---|---|
| `ignore` (default) | Only a loading indicator: a region on its placeholder, an indeterminate progress bar or ring. |
| `wait` | Every loop (the settle before P2): a page with a playing row runs to `max`. |
| `skeletons` | None, the loading indicators included, so the settle can end on a skeleton or a spinner. `state kind=loading` and `kind=cold-*` take this when `loops` is not given: their skeleton is the picture (`help verb=state` gives their `loops` no single default for this reason). A loading indicator the settle did not wait for is then listed in `loops` like a loop row, a region on its placeholder by its placeholder's key (`state kind=loading` replies `loops:["facet-body:0"]`). |

**The WinUI library family.** Under WinUI the four library kinds (albums, artists, podcasts, audiobooks) share ONE keep-alive slot
and ONE body box, `page:library` (Classic and Zune keep `page:albums`, `page:artists`, ...): the head (`lib:head`, the kinds
`lib:kind:0`..`4`) is drawn once, and each kind is a page inside it keyed `library:<kind>` (`find prefix=library:` lists the
visited ones; parked kinds are `shown:false`). A kind switch is no page swap: trace `prefix=library:` with
`keys=lib:head,lib:kinds` to see the body slide under a head that never moves. The route still names the kind (`after.route`).

**The page loading bar.** Each page's body box (`page:<route>`) carries one thin bar on its top edge, `page-busy:bar` in the
fixed slot `page-busy` (3 DIP in Classic and WinUI, 2 in Zune). It shows once the page's placeholders (or, in Zune, its deferred
mount) have been pending 1 s and stays at least 0.4 s, fading in and out (about 11 frames each way); a load shorter than 1 s
never shows it, and nothing moves when it comes or goes. The inspect host's frames are a fixed 16 ms clock while the fake
catalogue answers on the wall clock, so an ordinary `goto` can show the bar (32 virtual frames pass in a few real
milliseconds) and its settle then also waits out the 0.4 s minimum and the fade. For timing proofs use held answers only
(`state kind=head uri=... answer=hold settle=false`, then `frames N`).

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

The pinned Home's two mount args: `home=mock|capture|new|free|podcast|region|synthetic|second` (the fixture the Home tables are
seeded from) and `homedoc=default|keep|pins0|pins200|gonepin|custom|mixed|v2hidden|toonew|corrupt|unknown` (the document the
mount starts from, written into the profile's `WaveeMusic/home.acct-<hash>.json` and loaded; default `default`; `keep` loads
the profile's file as it is, for a second process on one `--profile`). `pins0`: an empty Pinned group; `pins200`: 200 items;
`gonepin`: a pin the catalogue never answers; `custom`: the feed taken control of, two sections hidden, mixed sizes; `mixed`:
every size of every MVP source; `v2hidden`: no v3 file and a `home-layout.json` v2 with hidden zones (the migration runs);
`toonew`: a version 4 file (Home shows the default, the file stays byte for byte, `readOnly: true`); `corrupt`: an unreadable
file (set aside as `.corrupt`); `unknown`: an unknown source kind, option key and members (they round-trip byte for byte).
`reset` puts the default document back. `home=live1` and `home=live2` are redacted live answers (2026-11-16, 2026-11-17; live2's lookup
was asked for its own answer's cards, so all 20 baseline cards carry their preview songs); `tools/home-fixture.py` makes
the next one from a `cmd=homecap` folder (the procedure, with its safety rules: the fgpu skill's `verify/debug-home.md`).

`media=fake` builds every media player over a fake backend (`Diagnostics.Inspect.Media.cs`): the Spotify Canvas player and any
`MediaPlayer` built without its own backend (an audio preview, a tile's video). `mediaOpenMs` (default 120: the open's latency to
its first frame on the 16 ms clock, metadata at half), `mediaStartCostMs` (a synchronous UI-thread stall inside each open: the start
hiccup), `mediaFrameCostMs` (UI-thread work in every video pump while it plays), `mediaFailMs` (every session fails that long
after its first frame: a crashed engine). Each open logs `[inspect.media] open #n` (`status log=inspect.media`); the next mount ends it.

`home do=preview` reads the Spine Shelf's song preview (`Home/SpineShelf/PreviewAudio.cs`): the claim (the tile that owns the
preview), both voices (tile, heard gain, player state), the sound button (`sound`, `soundGain`), the duck on the user's music
(`duck`, and `playerDuck` as the local player has it), the user's own volume and amplitude, what the local player applies
(`applied` = the user's amplitude × the duck), the saved volume, and why the last claim ended (`lastRelease`: `leave`, or
`lost:<why>` when the ownership check ended it: `pointer-off-tile`, `pointer-left-window`, `window-inactive`, `minimized`,
`unmounted`, ...). It acts first when asked: `sound=on|off|toggle` (the button), `volume=0..1` (the slider), `music=pause|play|stop`.
`samples=true` lists the per-tick levels since the last read (`[ms, voice0 fade, voice1 fade, sound, duck, userAmp, applied]`) and
each curve's largest one-tick change (`maxStep`). It runs no frames (`frames N` does). `home do=shelf tile=N` stands for a pointer
resting on the tile (the ownership check counts it as held until `tile=0`); `hover key=` and `pointer path=` drive the real pointer.

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
| `grade` | Cover grading: `hue` (a hue per uri), `grey`/`gray` (every cover grey), `greynow` (only the playing one grey), or one test colour for every cover in both halves (`InspectKit.NamedCover`): `dark` (a deep navy), `teal` (#396b7a), `white` (near-white) or `yellow` (saturated): the grounds a cover-tinted surface (the stage's palette, its caption veil, the audio face's field) must hold its inks over. |
| `gpu` | `hardware` (default: the machine's hardware adapter, WARP when it has none; `devices` reads `offscreen (D3D12)`, or `offscreen (D3D12 (WARP))` on the fallback) or `warp` (WARP forced): the offscreen D3D12 surface, so `render` works. `none`: DirectWrite over the recording device (no pixels; motion checks that render nothing). Same lint and crossing counts on either surface, pixels within anti-aliasing, hardware 1.5-5x faster (10-24: Zune album 1440 3.4 s against 8.0 s). |
| `images` | `fake` (default): the engine's deterministic decoder paints every image as a per-image gradient. `real`: the app's own pipeline (WIC behind the disk-first fetcher) decodes the bundled photographs as they are, so text over a photograph renders, and lints, over the real picture. The fake artist `ar9` (The Longtime) carries a bright header photograph (`assets/covers/header-bright.jpg`, a white sky over a pale ground): the worst case for text on a photograph. |
| `motion` | `full` (default) or `reduced`: Windows' Animation effects off for this mount (`Motion.ReducedMotion`), which a headless server never reads from the OS. A structure with page motion then fades its page swaps (WinUI: ControlFaster each way) with no slide, zoom or stagger; Classic and Zune cut. |
| `keepRoute=true` | Keeps the current navigation and tabs. Without it, the mount starts from a fresh Home on the first tab. |
| `setup=true`, `signin` or `theme` | (`1`, `on` and `yes` are `true`.) Mounts a first run: the setup wizard's plate opens over the shell (give `frames` ~120 before `render`). `signin` opens it on the sign-in page, `theme` on the theme page (the picker and the active theme's options: a headless run cannot sign in to get there). |
| `feedback=bug` or `feature` | Opens the report form over the page. |
| `marquee=on` or `off` | Sets the app's marquee setting (Settings > Appearance) before the mount. |
| `remote=<name>` | The play bar says it is playing on a remote output of that name (`--fake` has no Connect cluster). To turn the output remote on a live mount, use `play remote=<name>`. |
| `panelW=<dip>` | The side panel's stored width (clamped to the theme's drag range when it opens), e.g. `panelW=280` for WinUI's narrowest pane. |
| `lyrics=word` or `line` | The shape the fake lyrics answer in: `word` (default) word-synced, `line` line-synced with no word timing (the same lines and times), the shape the AI lyrics time word by word (`state kind=ai ai=timed words=on`). Every mount sets it; a change empties the lyrics store, so every track is answered again. |
| `video=fullscreen`, `docked`, `floating` or `pip` | Puts the video surface up (`pip` is `floating`) with `spotify:track:tr26` playing: the rich album's first row, which has a music video (`HasVideo`), so the play bar shows its video split and the placement menu opens from it (WinUI: the split's chevron is unkeyed, `click x=1324 y=924` at 1440x960 opens "Where to play the video"). No frames decode offline: the inspector's video resolver never answers while the mount lives, so the join stays in flight (the poster, then the ring and "Loading...") instead of failing to audio; the next mount lets it go. Beside it, `fromMs` sets the track's position (74000), `player=fake` binds an idle engine player (`player=scripted` a source that plays, below), and `join=` picks the join poster's state; without `video` all three are refused. |
| `join=poster`, `working` or `failed` | The join poster as the host's switch events would leave it (`Playback.Video.Phase`, folded into `Video.JoinNow`): `working` the ring and "Loading..." (the default offline), `failed` the fault line ("Can't play this track"), `poster` the artwork alone (a player bound, nothing in flight; it binds the fake player). |
| `player=scripted` (`videoOpenMs=`) | A video source that behaves like a real one, for the video joins and switches: a real engine `MediaPlayer` over a scripted session that follows the deck. Every video track the deck lands on (the deck's video facts) is a fresh open on the SAME player, an in-place switch as the host's Switch plan does: Opening and the declared size at once, metadata (Buffering) at half of `videoOpenMs` (default 240, on the 16 ms frame clock), then the first frame (the natural size, the surface, Playing) at `videoOpenMs`; a deck that leaves video stops it. tr26 is 1920x1080 and every other video track 1440x1080, so a held frame's geometry can be told from its successor's. It carries a play intent, so the presenter's chrome idles and hides; it keeps the resolved source published while it opens (the inspector's resolver never answers); its geometry half binds, sizes and places the slot like the clear session, deferring a slot that shows another picture until its own first frame. `[inspect.video] open|defer|take.over|first.frame|stop` lines say what it did, and while it lives the engine's `[media.chrome]`, `[video] pump` and `[video.surface]` lines are copied into the app log, so `status log=` reads them. `status log=` reads the lines since the LAST `status log=`, whatever its text: read one filter per step (`status log="video"` takes the join, pump and source lines together). Example: `mount gpu=none w=1440 h=960 theme=classic route=home video=docked player=scripted`, `transport do=add uri=spotify:track:tr39`, `transport do=next trace=60 prefix=media`, `status log="video"`. |
| `seekPreview=on` | Beside `video=`: the video source carries seek sheets (two sizes, 246 frames at 1 s, as a real music video's manifest), so a rail's tip shows the picture under the pointer: `hover x=900 y=884` on the rail's line, or `drag`. The sheets are files, `<exe>/assets/seekpanels/<variant>/<panel>.jpg`, written beside the run copy by `python -I tools/seekpanels.py --out <run>/assets/seekpanels` (each frame carries its own time, so a render shows whether the picture and the tip's time agree); none are bundled, and without them the tip shows its plate and the time. Add `images=real` to see the frames. The picture's node is `seek-frame`; a narrow on-media rail (the mini player, a docked card at rest) keeps the time alone. `seekPreview=episode` plays an episode with chapters (`sh0e9`) instead of the video track, so the tip carries the picture and the chapter under the pointer. Precise seeking (pulling away from the rail slows the scrub) is traced with `trace 30 drag=-20 dragDy=-8 dragX=1200 dragY=884 dragFrames=24 keys=seek-frame`. Plan: `docs/plans/wavee/video-seek-preview-implementation.md`. |
| `seekManifest=<file>` | Beside `video=`: a captured v9 video manifest (a JSON file). Its `seekpanels` block becomes the source's seek sheets and the sheets are FETCHED FROM THE NETWORK through the app's sheet store into the scratch profile (`cache/images/seekpanels`), the one thing a mount lets through the refused wire. The signed urls in the file must still be valid (about a week). A fetch lands on the wall clock, not on frames: wait between commands (a client that sleeps, not `frames`) before the render. |
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

### `window {state?, active?, advance?, floor?, frames?}`

Parks the mounted window the way the OS does, restores it, blurs or re-activates it, and moves the engine's cold-maintenance
clock ahead. Use it for what happens to a window that sits minimized or in the tray (the restore-after-minimize crash, Wavee
0.3.7, was reproduced with it) and for a window the user switched away from (`active=false`).

| Arg | Values |
|---|---|
| `state` | `minimized` (the caption button: the placement reads Minimized), `hidden` (close to tray: the window reads invisible) or `restored` (back to Normal and shown). Positional: `window minimized`. |
| `active` | `false` blurs the window (`HeadlessWindow.IsActive`: the WindowBlur a Win32 deactivation sends; the dispatcher drops the pointer position, clears hover, closes light-dismiss overlays, and a pointer that was in leaves the window first: `InputHooks.PointerLeftWindowObserved`, then `WindowBlurObserved`); `true` re-activates it (WindowFocus). The pointer is back only with the next move: `hover x= y=`. Runs `frames` after the change. The fullscreen stage logs `[stage.chrome] exit as=left src=window` (a pointer that was in) and `src=blur`. A move off the window is `hover x=-50 y=-50` (`src=window`). |
| `advance` | Moves the cold-maintenance clock this many ms ahead (`AppHost.AdvanceColdMaintenanceClock`) before the frames run, so a 30 s deadline falls due on the next frame: `advance=31000`. |
| `floor` | Sets the scene slab's trim floor (`SceneStore.CapacityFloor`). An inspector mount starts as the app does (`Shell.SceneCapacity`, 8192: the slab and the render thread's snapshot slots start at that size, and the trim never cuts below it); pass `floor=0` to let the trim go down to the engine's own 256. |
| `frames` | Frames to run after the change when not restoring (2). A restore runs one frame, then settles (`settle`, `max`, `loops` and `trace=N` apply). |

**The parked path is production's.** While the window is minimized or hidden, every frame (from `window` itself and from any
other verb that steps frames: `play`, `goto`, `frames`, `settle`) is a parked `AppHost.RunFrame`: it skips `Paint` (no layout,
record or present) but still pumps, drains the UI posts, flushes the reactive work they schedule (components re-render, nodes
are created and freed) and runs the cold maintenance. A settling verb on a parked window runs to its cap and replies
`settled:false` (pass `max=60`). `state=restored` makes the next frame paint, as the restore message does.

**The clock.** The cold services read the OS uptime clock, not the inspector's 16 ms frame clock: the scene slab trim falls
due 30 s after the slab last grew or shrank, once the host has been free of input for 2 s. Without `advance` it fires only
when that much wall clock passes during the session.

The reply (every `window`, also with no args, which only runs the frames) carries `state` (`normal`, `minimized`, `hidden`),
`active` (the window's activation), `pointerInWindow` (`InputDispatcher.PointerInWindow`), `parked` (`AppHost.IsParked`), `coldRuns` (frames that ran a cold service during this command) and `scene`:

| Field | Meaning |
|---|---|
| `capacity`, `capacityBefore`, `trimmed` | The slab's column length now and before the command; `trimmed` when the trim shrank it. |
| `highWater`, `highWaterBefore` | One past the highest slot handed out since the last trim. |
| `live`, `highestLive`, `highestLiveKey` | Live nodes; the highest live slot (what keeps the trim from cutting lower) and its nearest key. |
| `floor` | The trim floor. |
| `dirty{pending, maxSlot, pastCapacity}` | The slots the next layout's subtree-dirty clear resets (they pile up while parked: no layout runs), the highest of them, and how many sit past `capacity`: above 0, the restore frame writes past the columns. |

A frame that throws replies `ok:false` with the exception, `stack` (its frames) and the facts above.

```text
→ window state=minimized
← {"id":5,"ok":true,"cmd":"window","framesRun":2,"state":"minimized","parked":true,"coldRuns":0,"scene":{"capacity":32768,...,"dirty":{"pending":38,"maxSlot":19326,"pastCapacity":0}}}
→ window advance=31000
← {"id":6,"ok":true,"cmd":"window","framesRun":2,"state":"minimized","parked":true,"coldRuns":1,"scene":{"capacity":8192,"capacityBefore":32768,"trimmed":true,...,"dirty":{"pending":2436,"maxSlot":19571,"pastCapacity":248}}}
→ window restored
→ window active=false
← {"id":7,"ok":true,"cmd":"window","framesRun":2,"state":"normal","active":false,"pointerInWindow":false,"parked":false,...}
→ window active=true
```

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
  `GlobalBar`, `false`), `canvas{shows, track, file, owner, bound, inView, run, state, natural, error?}` (Spotify Canvas: whether the
  playing track's canvas draws, which surface presents the one canvas player and whether it plays; `state`/`natural`/`error` are the
  engine player's, the only proof a canvas decodes, since a render shows the video's hole, not its frame. Fake canvases: tr3, tr20,
  tr27, tr41, tr55, tr62; tr4 and tr5 have none), `memory{managedBytes, heapBytes, workingSetBytes, allocatedBytes, threadAllocatedBytes, gen0, gen1, gen2, pauseMs}` (the
  managed heap now, the GC heap at its last collection, the working set, the bytes allocated since start by every thread and by the UI thread alone (exact), the collections per
  generation and the total GC pause), the theme and kind, the route, the text backend and the network leaks. When something is
  mounted, it adds `overlays[{key, first}]` (the open overlay layers, top first, as `after` lists them), the mount facts,
  `animating` and `trimSource`. `status log=<text>` adds `log[...]`: the app log lines (the in-memory ring; the headless boot
  writes no log file) that contain the text, written since the last `status log=` (the newest 64). A structured event with no message is
  matched and listed in its one-line form (`status log="list."` reads the `list.open`, `list.meta` and `list.settle` lines). `status log="stage.chrome"`
  (quoted: a bare `[...]` parses as a list) after a `hover`, `window active=false` or `frames` reads back the fullscreen stage's chrome edges (`exit as=left src=...`,
  `hold OverControls=0`, `hide cause=leave`).
- `help` lists the verbs with their summaries; `help verb=<name>` and `help schema=json` describe the args (see Protocol).
- `quit` unmounts and ends the server. A refused `quit` (`quit now=1`: it takes no args) ends nothing. `tools/inspect.py`
  sends nothing after a `quit` and says how many lines it left.

### `click`, `hover {key | x,y, nth?, force?, stableMax?, mods?, button?, dwell?}`

`hover ... dwell=700` rests the pointer 700 ms on the frame clock after it arrives, then settles: a rest-to-open timer or a hover
preview fires inside the rest. `pointer` (below) moves through several targets.

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

### `key {key, repeat?}` and `type {text, key? | x,y, nth?, force?}`

`key` takes these names:

- `enter`, `esc`, `tab`, `space`, `backspace`, `delete`, `insert`
- `left`, `up`, `right`, `down`, `home`, `end`, `pageup`, `pagedown`, `apps`
- `f1` to `f12`, `a` to `z`, `0` to `9`, or a decimal virtual-key code
- any of these with `ctrl+`, `shift+`, `alt+` or `win+` in front, such as `ctrl+f`

`type` sends character input to whatever has focus. The text form takes the rest of the line, spaces kept; to add args
(`settle=false`, `trace=N`), quote the text or use `text=`. `type key=<node>` (or `x`, `y`) presses that node first, as a user puts
the caret in a box (the same actionability checks as `click`; `force=true` skips them), then types; a line that starts with `key=`
reads as args, so quote text with spaces after it. This is how a run types into the Settings page's box (`settings:search`):

```text
key ctrl+f
type daft punk
type "daft punk" settle=false
type key=settings:search crossfade      # the Settings page's box (every theme binds it)
type key=settings:search "dark mode"
key enter
key esc                                 # in the Settings box: the first Escape clears it (and closes its list), a second leaves it
```

### `scroll {key | x,y, dy?, dx?}`

Turns the mouse wheel at the target: `dy` notches, positive toward the end of the content (3 by default). A `key` target
is checked and picked as `click` does (`nth`, `force`), except that nothing needs a handler at its centre: the wheel goes to
the scroller under the pointer either way. The settle waits for the scroller to stop, so the reply's
`scroll {offset, max}` (with `key`: the first scroll viewport under that node) is where the glide ended.

### `drag {key | x,y, dx?, dy?, to?, holdMs?, pauseMs?, pointer?, steps?, button?, mods?}`

A mouse drag as one command, the way a hand makes it: the pointer moves onto the press point (`key`'s visible centre, checked
and picked as `click` does: `nth`, `force`, `stableMax`; or `x`,`y`), the button goes down, the pointer travels `dx`,`dy` DIP in
`steps` equal moves (8), one per frame, and the button comes up at the end point; then the settle. The reply has `at` (the press),
`to` (the release) and `steps`. `trace=N` traces N frames from the release (a drop's settle); to trace the frames of the drag
itself use `trace`'s own `drag=` args (`drag`, `dragFrames`, `dragStart`, `dragKey`/`dragX`/`dragY`, `dragDy`: DIP per frame,
cycled).

`to=<key>` ends on that node's presented centre instead of `dx`,`dy` (a drag onto a tile); `holdMs` keeps the button down and
the pointer still that long before it moves (a touch lift's hold); `pauseMs` dwells at the end, button down, before the release
(a reorder's 140 ms dwell); `pointer=touch` sends touch events (no hover move first).

```text
drag key=winui-bar-volume-slider dx=-60 steps=6      # the WinUI volume thumb to 0 (the glyph turns muted)
drag key=home:tile:<a> to=home:tile:<b> pauseMs=200 trace=90 prefix=home:tile max=400   # a pinned Home reorder
```

### `hold {key | x,y, ms?, pointer?, then?, dx?, dy?, steps?}`

Press and hold: the pointer presses on the node's visible centre (picked as `click` does) or a point and stays down, still, for
`ms` (600) on the frame clock; then (`then=release`, the default) it comes up where it pressed, or (`then=drag`) it travels
`dx`,`dy` over `steps` frames and comes up there. `pointer=touch` sends touch events (a touch hold opens a card's menu today;
the pinned Home's tiles lift on it). Reply: `at`, `ms`, `pointer`, `then`, `to` (a drag), then the settle and `after`.

```text
hold key=home:tile:d-recents ms=600 pointer=mouse          # Zune: customize on that tile
hold key=home:tile:<a> ms=500 pointer=touch then=drag dy=160   # WinUI: lift and move
```

### `pointer {path, steps?, dwell?}`

Pointer realism for hover-driven UI. The pointer glides from where it is to each waypoint over `steps` frames (default 6), one
real `PointerMove` per frame through the window's input queue (hover enter and exit, rest timers, previews all see it), rests the
waypoint's `@ms` on the 16 ms clock (or `dwell`), then goes on. Waypoints: a key (its visible centre; refused like `hover` when
not actionable), `at:x:y` (window DIP) or `leave` (off the window: the pointer-left-window edge). Then it settles (`trace=N`
works). Reply `legs[{target, x, y, hit, frames, note?}]`.

```text
pointer path=[home:tile:d-daylist@300,home:tile:d-recents@700,leave@200] steps=6
```

### `focus {key, nth?}` and `said {clear?}`

`focus` moves keyboard focus to a node (the focus ring shows, as after Tab) without a click; `after.focused` names it. `said`
returns the announcements (the screen-reader text the app sends through `Announcer`) since the last `said`: `said[{text,
assertive, frame}]`, `count`; `clear=false` keeps them for the next read. Every acting verb's `after{}` also carries its own
`said[]` (what was announced during that verb).

### `home {do, ...}`: the pinned Home

The pinned Home's board, read back and driven through the same verbs the themes call (the board host registers them while
the board is mounted; with none mounted the acting ones go through the store, and the reply says `via: "store"`: the model and
its undo are proved, the motion and the view state are not). Acting ones settle (`trace=`, `prefix=`, `max=` work) and reply
`changed`, `item` (the entry acted on), `rev`, `undoDepth` and `after`.

| `do=` | Args | Does |
|---|---|---|
| `status` | | the board as the user has it: `theme, editing, selected, open, add{surface, kind, query, results}, columns, undo{can, label, at, depth}, redo{can, depth}, readOnly, rev, items, hidden, legacyHidden, seededFromSidebar, file{path, outcome, exists, bytes, fnv, bak, corrupt}, groups[{id, title, items[{id, key, kind, source, title, size, auto, template?, hidden?, form?, span?, rect?, realized?, hasOptions, live, paused}]}]` |
| `doc` | `path=` | the document's JSON (and writes it, indented, to the file) and `file{}` |
| `plan` | `facet=` | the planner dump: per section `index, key, fp, family, typename, title, counts{cards, usable, unsupported, duplicates, total}, template, zone, reason, entry, placed, hidden`, then the identity log's readings |
| `explain` | `facet=` | why Home shows what it shows: per rendered section (feed, placed, fold) `entry, key, kind, group, drawnTitle, template, zone, zoneKind, form?, span?, realized?, pager{page, pages}?`, Spotify's identity `spotify{uri, index, typename, title, titleBase, headerUri, kind, cards, total, firstCard}`, `identity{key, fp, family}`, the chooser's `chooser{template, zone, reason, alternates[]}` and `rhythm{decision: kept\|moved, why, final}`; a fold has `fold{members, why, runStartsAfter, runEndsBefore, rule}` and its members' sections; then `findings[]` (`duplicate-title`, `duplicate-family`: `what, entries[], hint`). The lint rule `home.duplicate` (todo, error) fails the same titles on any lint of Home |
| `verify` | | the board's own proof: `PackLayout.Verify` over every group at the live width, and the realized rects against the pack: `columns, count, faults[]` |
| `edit` | `on=`, `item=` | Classic's Edit Home / Zune's customize (a selected tile); WinUI refuses |
| `pin` / `unpin` | `uri=` (an entity uri or a route key), `group=` | the verb the menus call, confirmation included |
| `add` | `source=` (a catalogue id: `widget.upNext`, `pin:<pin id>`, `section:<key>`), `group=`, `at=` (index in the group), `size=` | add at a place |
| `remove` | `item=` | remove a widget, unpin a pin, hide a section or a shortcut |
| `size` | `item=`, `size=s\|m\|l\|full\|next` | resize, or cycle the sizes the source declares |
| `move` | `item=` and one of `by=±n`, `to=start\|end`, `onto=<item>`, `dir=left\|right\|up\|down` | the command twins (`by` steps are tagged `step:<id>`: steps within 2 s are one undo step) |
| `open` / `close` | `item=` / `refocus=` | open a tile for its options (one visit is one undo step) |
| `option` | `key=`, `value=` | a choice on the open tile |
| `surface` | `open=`, `kind=band\|allitems\|addrow\|find`, `arg=` | the theme's add surface |
| `search` | `q=`, `items=` | types into the add surface's search: `spoken`, `counts{total, widgets, spotify, library, extensions, onHome, toAdd}`, `hits[]`; an empty `q` lists everything |
| `undo` / `redo` | | one step; the reply carries `undo{}` and `redo{}` |
| `reset` | `scope=page\|everything`, `confirm=` | reset (Classic's "everything" asks in its bar: `confirm=true` answers it) |

An entry id is what `status` lists: a document item (`d-recents`, `i7f3a…`), a feed section (`feed:<section key>`), a fill cell
(`fill:<pin id>`), a fold (`fold:<first key>`) or a group head (the group id). Taking control or keeping a shortcut keeps the id.
Node keys: `home:board`, `home:group:<groupId>`, `home:tile:<entryId>`, `home:tile:<entryId>:tools`, `home:slot`, `home:add`,
`home:edit`, `home:undo`.

```text
mount w=1440 h=960 theme=winui route=home gpu=none home=capture homedoc=custom
home do=move item=feed:<key> by=1          # takes control: every section an item, the feed item last; one undo step
home do=search q=mix                       # hits across groups with counts; q=zzzz says "Nothing to add matches zzzz."
home do=undo
```

### `play {uri?, fromMs?, next?, ago?, remote?, context?, at?}`

Plays one fake track or episode as its own context. This is the headless probe's recipe: `BuildContextQueue`, then
`Queue.Replace`, then `Playback.PlayNow`. `next=N` (a `spotify:track:tr<k>` uri) adds the N tracks `tr<k+1>`... to the same context, so the
stage's "up next" page has rows. The lyrics media clock runs in the harness frame clock's epoch (`Shell.Headless.Boot`), so a playing
track follows its lyrics: render the stage while playing, no pause needed. `ago=N` backdates the play in the play log by N minutes (the Up next page's "played before this" reads it).
`context=<uri>` plays the rows in that context as a row click on its page does, from row `at=` (0 = the uri's own row).

`remote=<name>` turns the output remote on the live mount, as a Connect transfer does while playing (`remote=off` makes it local again); without a
`uri` only the output changes.

`format=<badge>` makes the deck's load report that format badge and its open the format the badge names (`Playback.StageFormat`: the open's
format, then the audio host's own reducer input), after the load
lands so the fake open's "OGG 320" cannot overwrite it; without a `uri` it restamps the playing load. A FLAC badge stands a lossless stream:
`play uri=spotify:track:tr3`, then `play format="FLAC 24/96"`, and every theme's bar shows its Lossless mark after the artists (`np-lossless`, its
tooltip the badge; Classic from Standard, WinUI in every full bar, Zune from its full level, 1240; the bar reads the opened format, the badge is its
tooltip) and the stage its format chip. `setting id=sound.losslessBadge value=false` hides the mark at once (the host then hands the themes no
format). The fake load lands on the wall clock; `play uri=` waits for it (up to 5 s) before it settles and replies
`phase` and `loadFrames` (above), so the next command reads the playing bar; the remote line shows through the load as the title and artists do.

### `transport {do, value?, uri?, ms?}`

Drives the playback sequencer the way the transport buttons, the natural end and the queue verbs do, then replies with what the deck
is on and what will play: `deck` (the track id), `cursor`, `phase`, `repeat` (off, all, one), `shuffle`, `autoplay` (the setting),
`positionMs`, `rows` (the queue as the reducer walks it, one token per row: `<bucket><provider>:<id>`, bucket `H` history, `>` the
deck, `U` user queue, `N` next up; provider `c` context, `q` queued, `a` autoplay; a trailing `!` marks a row the deck may not land on)
and `upNext` (the queue panel's rows in display order, `<section>:<id>`, section `q`, `n` or `a`). `do=` is `status`, `next`, `prev`,
`end` (the track ran out with nothing prepared, the natural-end path), `seek ms=`, `shuffle value=on|off`, `repeat value=off|all|one`,
`pause`, `resume`, `add uri=` / `playnext uri=` (the user queue), `clear` (empties the user queue), `dead uri=` (the catalog rules that track
unavailable, so the walk steps past it) or `duration ms=` (the decoder reports the playing row's real length: the same row's duration
changes, as a music video's length replacing the song's does; the seek rail's rescale, `status log="seek.rescale"`). `do=putstate` returns the connect-state PUT body the deck would send (base64, with its `quality`: the `playback_quality` read back off the bytes) and `do=cluster file=<b64 lines>` decodes captured cluster pushes (one base64 `ClusterUpdate` per line) with the real decoder, `decoded`: per frame the active device's `quality`, `activeKind`, `outputType`, the picker's `footer` words and the bar's Lossless `badge` (nothing folded, no device id written); `do=remotequality value=hifi|hifi24|veryhigh|high|normal|none` makes `--fake`'s active Connect device (after a transfer to the demo phone under `mount connect=nearby`) state that `playback_quality`, as a push re-stating it would; and `do=telemetry` the playing row's
RawCoreStream and closing segment as its registration would report them (base64, with `contentUri`, `localContentUri`); nothing
is sent (`--fake` sends no telemetry). A playlist's matched `spotify:local:` row adds `deckFile` (the file's name, never its
folder). `do=inbound file=<json>` decodes a controller's Connect `play`/`transfer` command body with the app's own decoders and hands it to
the host as the dealer glue does (no dealer, no ack). `play uri=<row> context=spotify:playlist:<id>` with the row in that list plays the list's own rows from it, as a
double-tap on the row does. `state kind=music-folders value=folder path=<dir>` runs the real scanner over a real folder into the
in-memory index (#240's real-file matching; the app's index file is never touched). `value=startup path=<dir>` instead runs the app's own start-up (`LocalLibrary.Use`) with that folder over
the scratch profile's `Wavee\Cache\localindex.bin` (copy an older build's index there first to replay its rebuild): the load, the
start-up scan and the restate run on their threads and land over the next frames; `state kind=rescope` after it is the login's scope switch. `state kind=local-peek value=counting|landed|empty|capped|none`
(after `music-folders value=none`) stages what "Find music on this PC" counted (#240, 0dm; no disk; the real peek, which counts this PC's Music and
Downloads folders by extension, runs when the card mounts on a mount with no music folder and logs `local.peek <folder> songs= cloud= videos= entries=
capped= ms=`); `trace=90 prefix=local-` on `landed` proves the card keeps still. `localindex prefix=<path start>`
lists the index (each file's LocalId, folder id, path, state and Wavee rename; `next`; `idMark`, the id high-water mark kept
outside the bin); `rename=<LocalId> title=<t>` renames a file first, `compact=true` writes the bin now, and `mark=<n>` sets the
high-water mark a previous run would have left (an inspector's settings live in memory), before a `value=startup`. The #240 LocalId
safety checks corrupt or downgrade a scratch profile's bin between two such runs (`--profile`), then read `status log="local.index"`
(`local.index.ids next= bin= floor= version=`) and `status log="local.link.identity"` (`kept dropped backfilled rebound orphans`). Under `--fake` autoplay answers from the demo catalogue (`Playback.UseFakeAutoplay`), so the
end of a context behaves as it does online; `setting id=autoplay value=false` turns it off. A sequence of `transport do=end` (or
`do=next`) replies is the sequence of tracks played:

```text
play uri=spotify:track:tr1 next=5 context=spotify:playlist:pl1 at=3   # tr1..tr6 as a playlist, from tr4
transport do=clear
transport do=repeat value=all
transport do=end                                                       # deck=tr5, then tr6, then tr1 (the wrap)
```

### `timecode {do?, samples?, every?, key?, value?}`

Reads the playback timeline tap (#154): what `Playback.Timeline.TryRead` composes from the engine's anchor
(`PcmAudioSession.TryReadTimeline`) and the pump's identity. `tap` is `{live, uri, positionMs, durationMs, rate, playing, flags,
epoch, changes}` (position extrapolated to the reply's instant; `epoch` is `<identity generation>.<engine epoch>`; `flags` adds
`Transition` while a crossfade or a #48 join is audible, with `incoming`, `master` and the window's edges from now); `engine` is the
raw anchor (`contentFrame`, `sourceFrame`, `mixFrame`, `mixRate`, `voice`, `advancing`, `anchorAgeMs`), the publisher's counters
(`publishes`, `epochs`, `driftEpochs`: epochs started only because the playhead left the extrapolated line by more than 2 ms,
`lastDriftMs`), `outputDelayMs` and `sampleClock`. `samples=N every=ms` adds `series`, N readings `every` ms apart: on a mounted
inspector the gap is run as frames held to real time (the fake deck's endpoint is paced by the 16 ms frame clock, and `frames`
runs frames back to back, faster than the session's feed, so the paced endpoint starves and loses that time). Steady play reads
`positionMs` rising by exactly the gap with one epoch; a seek, a pause (after the endpoint's queue, 2 s headless, has played out),
a resume and a track change each start one. Fake loads never prepare a join, so `Transition` is not reachable headless.

`ltc` is the LTC output (phase 3): `status` (the Settings row's words), `device`, the aux output's `state`, `auxRate`, `blocks`,
`underruns`, `auxLatencyMs`, and the generator's `rate`, `locked`, `sent` (the label written now, for the instant that block will
be heard: 2 s ahead of the tap headless, where the paced endpoint queues 2 s), `read` and `readCount` (its self-check reader
decoding the generator's own output), `matches` / `mismatches` (each decoded word against the frame the encoder finished),
`errorMs` (the chase error) and `relocations`. `do=set key=enabled|device|rate|start|level|offset value=` writes a dial through
the Settings rows' own verbs (`Playback.TimecodeOutput.Set`; headless any device id stands for the silent endpoint). A live
window takes the same through `wavee://diag?cmd=timecode&key=&value=`.

```text
timecode do=set key=device value=headless-ltc
timecode do=set key=rate value=29.97
timecode do=set key=enabled value=true
play spotify:track:tr3 fromMs=57000
timecode samples=8 every=500          # ltc.matches rises with readCount, mismatches 0, relocations 1
```

```text
play spotify:track:tr3 next=2
timecode samples=6 every=500          # positionMs +496 per reading, epoch unchanged
transport do=seek ms=60000
transport do=pause
timecode samples=8 every=500          # runs on ~2 s (the headless queue), then playing:false and held
```

### `state {kind, page?, ai?, error?, lang?}` and `dialog {kind}`

`state kind=home-refresh` lands the pinned Home's next answer (the `second` fixture) over the one the page shows: a removed
section keeps its place as a hidden entry, a new one sits after its server neighbour, a hidden one stays hidden.

`state kind=home-reanswer` lands All's next answer the way a live re-fetch does (the daylist rollover re-asks the feed's sections every
few hours): the document alone, over `home=live1` live2's and over anything else live1's (ten new baseline sections, eight that carry
another card under the same uri). The baseline lookup is then the app's own ask: under `--fake` it is answered on the next turn from the
lookup capture that document came with, through the live preview fold, and the lookup's marker takes the live door's marks (asked, then
answered without a run of its own), as it does on every fixture load. Mount `home=live1`, scroll the Spine Shelf ("Because you like")
into view, stage it with `trace=90 prefix=shelf:`: the open card's song tiles hold their place and fill in.

`state kind=home-cold`, then `home-land`, `home-following` and `home-previews` (any order) stage a live open one answer at a time: cold
forgets the All answer, the Following facet's answer and the baseline lookup the tables hold and seats All and Following in flight (the
board shows its placeholders), each land step concludes one of them. Mount from another route (`route=browse`), stage `home-cold`, then
`goto route=home`, and trace each step (`trace=90 prefix=home:tile:`): the proof that a load moves nothing at once.

`state kind=daylist-generic`, then `daylist-land`, stage a daylist rollover as the live feed serves it: the All answer's daylist in its
generic pre-title state (the name "daylist", no tags, no basis, a provisional window), then the playlist's own header read landing the
edition (a descriptive name, tags with links, a basis and a window 8 minutes later). The card must follow the land (its row gate) and
its countdown must name the header's window, the one the playlist page shows.

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
`mount` and `reset` clear the vacancy.

**The AI lyrics settings card: `state kind=ai`.** The server never starts the AI lyrics module, so the card is always "not
started" (the switch off and disabled). `state kind=ai ai=<case> error=<kind> lang=<iso>` stages the card's one status model
(`AiLyrics.Current`, through `AiLyrics.Stage`; `Diagnostics.Inspect.AiLyrics.cs`) on the Settings page that shows the card
(`route=settings arg=lyrics` in every theme, Lyrics & Now playing › Lyrics; the card is `appearance.ai-lyrics` in Classic and WinUI,
the rows `zs:ai:rows` in Zune), with
no module behind it: while it stands every module verb is inert, so a click on Try again, Remove, a language or the switch downloads
nothing, loads nothing and never touches the profile's `ai\lyrics` folder. The cases: `off`, `unavailable` (not an Arm64 PC: the
switch off and disabled), `setup` (needs its download), `downloading` (40 %, a speed and a file), `paused`, `preparing` (the first
NPU compile, 3 of 8), `ready` (default; English and Spanish installed), `lang-failed` (a language added to a ready feature failed:
the card stays Ready, with that language's own bar `ai:lang-failed` / `zs:ai:lang-failed`), `setup-failed` (a first setup stopped
on a language's file: the whole card is the error, naming it), `error` (the whole feature: a shared file, the runtime, the NPU, the
compile; the bar is `ai:error` / `zs:ai:error`), `timed` and `timing` (ready, and the PLAYING track timed by it: a finished job, or
one running at a third of its lines, so the lyrics surfaces draw the AI disclosure, `ai-lyrics-footer-clip`, or its progress line;
stage it after a `play`, e.g. the mini player: `mount w=420 h=420 mini=true face=1`, `play spotify:track:tr3 30000`,
`state kind=ai ai=timed`) and `none` ("not started" again). `ai=timed words=on` also gives the playing track's lyrics a finished job's
word timing (each line's words share its span: no aligner runs) through the store upgrade a cached result takes, so the lyrics surfaces
draw the song word by word; on a line-synced fake (`mount lyrics=line`) that is the AI dial's whole effect. A view takes the upgrade at
once only while no line is lit (paused, or before the first line), else at the next hand-off: stage it, then seek to the line. The next
mount empties the lyrics store. `error=notfound|offline|network|hash|diskfull|unknown`
(`runtime|nonpu|compile` too, for `ai=error`) picks the failure kind, with the detail the real download writes for it (the URL, the
file, the drive); `lang=` the failed language (`ja` by default; a pack language). The bars' buttons are keyed: `ai:details` /
`zs:ai:details` (reveals the raw detail in place) and `ai:retry` / `zs:ai:retry`. The reply names the `phase`, `error` and
`failedLanguage`. The next `mount` (or `reset`) puts "not started" back.

```text
mount w=1440 h=960 theme=zune route=settings arg=lyrics
state kind=ai ai=lang-failed error=diskfull
click key=zs:ai:details trace=40 prefix=zs:ai     # the raw detail opens in place, traced
```

**The Playback runtime card: `state kind=runtime`.** The server has no local playback runtime, so `Setup.Runtime.Status` stays `NotApplicable` and Settings > Playback only shows its "not set up" bar. `state kind=runtime runtime=<case> issue=<kind>` publishes a made-up status whole, as the host does (`Diagnostics.Inspect.Runtime.cs`): `problem` (default: the error bar with View diagnostics, Connect diagnostics and Retry setup; `issue=wrongarch|nopack|unsupported` its typed reason), `setup` (the not-set-up bar), `ready` (the facts) and `none` (the boot's status back). Nothing is installed or loaded; the next `mount` puts the boot's status back. The bars are keyed `zs:runtime:problem` (Zune) and `playback.runtime:problem` (Classic, WinUI). The app's own "Local playback setup" toast answers the staged status as it would a real one.

```text
mount w=480 h=860 theme=zune route=settings arg=playback
state kind=runtime runtime=problem issue=wrongarch
```

`dialog kind=crash|hang|report|lightbox` opens the crash prompt (over a made-up bundle, through the shell's own
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

**A staged book load.** The audiobook page's cold open, one answer per command, on `spotify:show:sh9` (The Long Book, a hundred chapters;
`Entities.Fake.Cold.cs`): `state cold-book` (the show's own answer: the title and subtitle joined in one string, no membership, no credits, no
lengths), then `goto uri=spotify:show:sh9 loops=skeletons` (the page stands; the chapters' place, the line and what is under the contents are
held), then `state cold-book-chapters` (the membership lands: names, no lengths, so no start times and no line yet), then
`state cold-book-facts` (the credits and every chapter's length land: the start times, the line, the listener's place). Each takes `trace=`:
watch `cpa-ch:N` / `ab-row:N` / `zb-row:N` and the hero's keys; nothing readable may snap.

**Staged playlist loads.** Every fake list is seeded whole, so a fake page never shows the real client's staging. The cold playlist
(`Entities.Fake.Cold.cs`, `spotify:playlist:pl6`) stages one, one step per command: `state cold-list` (header known, length and rows not:
the meta line loads, the rows shimmer), then `goto uri=spotify:playlist:pl6`, then either `state cold-rows` (the membership lands, rows 1
and 4 know their face, the rest in flight: the hero arm (Compact, and Classic/Zune's vertical page) paints rows 1 and 4 over the
others' placeholders; the two-column arm keeps the whole first page on its shimmer until the first page's faces are in,
`TableRules.RowsPending`'s hot-row gate, by design) or `state cold-warm` (the membership lands with every row restored from disk: faces known,
credits in flight, as a warm open), then `state cold-faces` (the rows' fields land). **A regenerated Mix** (the stale head and list a
first open holds, then the fresh answer): `goto uri=spotify:playlist:pl6 settle=false` (the seeded list is held on the open's revalidation,
which the fake never answers: the cover and description wait on their skeletons with the rows), a few `frames`, then `state kind=regen` lands a
new revision with other rows and another cover and description (a second `regen` on the settled page changes the cover in place: its
hand-over crossfade; the description alternates per `regen` between a short line and one that wraps to three lines in a 216 DIP rail, so
each one exercises the description's hand-over and its height). The rows narrate the change one by one (leave, close up, arrive:
`Design.Membership`): a re-cut has most rows leave and most arrive, the list keeps its key and its scroll. **A small remote edit**: `state
kind=regen-edit` on the settled pl6 page removes display rows 2, 5 and 6, moves row 9 to row 3 and inserts new tracks at rows 1, 4 and 30
(`tr70`..`tr130`), with a new cover, the alternating description and a new revision; it is a pure function of the shown membership and an
epoch, so it repeats. Trace either with `trace=90 keys=row,art:handover,hero-description,rail:desc prefix=list:,top:`; after `scroll key=row
dy=8` the edit is above the window and the first visible row keeps its place (the anchor). **A login's scope switch under the page**: `state kind=rescope` puts a new, empty table set in (another market, the
old set's stamps forgotten, the deck re-bound: the welcome's `Entities.Switch`, minus handing the old set's text back, which a Debug build
asserts on while the page still draws it), and `state kind=rescope-land` a few frames later lands the seed in it, the shown list Complete and
never revalidated, as the disk warm-up does. The owner's case (2026-11-15) ran under a covered window: `window state=minimized` first, then
`rescope`, `frames 30`, `rescope-land`, `frames 200`, `window state=restored`: the page's open runs on the restore and its hold settles.
`state cold-head` instead of `cold-list` also takes the
HEADER away (title, description and owner unknown, Identity not known; the cover stays: a deep link's first frames), so the page shows its
pending head; `cold-rows` or `cold-warm` lands the header with the membership (one answer). **Any detail page's header**: `state kind=head
uri=<album|playlist|show|episode uri>` takes that page's title and every field it knows away and opens it (a cold open's head: no title, publisher,
rating or cover; its rows stay), and `state kind=head-land` puts them back on the same page, so `state kind=head-land trace=40 prefix=...` traces the head landing
in place (`Entities.Fake.Cold.cs`; the next `mount` puts it back too). An **artist** uri (`ar3`) stages the late chart instead: everything is
known but the charted rows' fields, asked and in flight (the page on its skeleton); `head-land` lands them as an answer does, and nothing else moves.
**A real request, settled by hand** (`Entities.Fake.Answer.cs`): `state kind=head uri=<album|playlist|show|episode|artist> answer=hold` (an artist: its overview
and chart bit instead of the late chart; `keep=card` keeps what a card's thin row stages, the title and the cover, as live Home and search cards leave
them; `go=false` stages it without opening the page, so a card click opens it and its cover flies: `state kind=head uri=spotify:show:sh9 answer=hold
keep=card go=false` on `search long`, `click x=800 y=467 trace=40 flights=true`, then `head-land answer=fill trace=40`) also swaps a scripted
transport in for the subject's provider, so the page's OWN ask for its head goes through the fetch layer (plan, bucket, drain, send) and is held in
flight (the reply's `held`); `state kind=head-land answer=fill|omit|unfilled|fail|cancel` settles it: `fill` lands the head, `omit` answers without it
(the fetch layer's miss retries are omitted too, then the group is Failed), `unfilled` answers it as a route that did not answer, `fail` is a terminal
404, `cancel` a transport failure the fetch layer re-queues (still in flight; the retry is held again). The page turns into its error vacancy on the
settle's own publication (no timer), its Retry re-asks through the same door (held again), and `head-land answer=fill` (or the next `mount`) puts
the original transport back. An artist's hold also stages its release cards (the latest release and the discography's releases) asked and in
flight, so `head-land answer=fill part=overview`, `frames 3`, `head-land part=cards` lands the page in the real client's order (the overview and
chart first, the side column's cards a few frames later); `part=overview cards=3 trace=70 ...` lands the cards on frame 3 of the overview's own trace,
so one trace sees both; without `part` both land at once. A `state` with `trace=N` (or `settle=false`) runs no frames before its
trace, so the trace starts on the frame the state lands. Its verdict judges that landing frame too: the watched nodes are sampled once
before it (the reply's `baseline` row, frame -1) and f0 is compared with them, so a jump in the landing frame counts as moved at once.
`baseline=false` turns that off; `baseline=true` turns it on for another verb's `trace=N`. Pass `trace=N` on a `state` command to trace its
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

### `setting {category?, id?, value?}`

The app's own settings: every dial of the settings registry (`SettingsSpec`, `Screens/SettingsSpec*.cs`). `setting` lists them in
registry order, then the active theme's option dials (`live: true`): `{id, legacy?, category, group, parent?, tier?, kind, value,
writer, readOnly?, enabled?, reason?, visible?, live?}` plus `count` (the registry's dials) and `live`. `legacy` lists the old row ids
a dial answers to; `visible: false` marks one the facts compose away right now (it is still listed). `category=sound` (or an old slug,
`audio`) lists one category. `setting id=<dial>`
reads one dial, named by its registry id (`playback.crossfade`) or its old row id (`crossfade`), any case; `setting id=<dial> value=<v>`
sets it through the dial's own writer, the verb a Settings view calls (a toggle's `true`/`false`, a slider's number, a choice's, a
segmented dial's or a picker's index; naming an action presses it, with no value), and bumps the page epoch when the writer asks the
page to re-read, as a view does: a mounted Settings page, the lyrics stage and the bar read the new value as after a click. A value set
on a live mount is that mount's: the next `mount` (or `reset`) puts it back through the dial's writer (toggles, sliders, choices). One
set with nothing mounted is for the next mount, which keeps it. An old id two dials shared would want the registry id (none does
today); a read-only dial or a module's own card (`lyrics.ai.files`) is refused. A theme option is a dial too
(`appearance.option.style.density`); `option` writes it by its own grammar.

```text
setting id=lyrics.blur value=0       # the lyrics blur off (0..100)
setting id=marquee value=false       # an old row id still names its dial
setting category=sound               # one category's dials with their values
```

**The Settings page (settings rework phase 4; contract 2.0 removed the old areas and tabs).** Settings opens on its home; the strip
(Classic, WinUI) or the section list (Zune) holds the home and nine categories: `home`, `appearance`, `sound`, `playback`, `lyrics`,
`notifications`, `system`, `storage`, `account`, `help`. `goto route=settings arg=<slug>` opens one directly, and every slug the page
ever answered to still lands on the category that now holds what it opened (`SettingsSpec.Aliases.cs`): `general`, `language`,
`graphics`, `links`, `tray` -> system; `audio`, `devices`, `devices-connect`, `connect` -> sound; `video` -> playback; `fullscreen`,
`nowplaying` -> lyrics; `storage-cache`, `downloads`, `downloads-storage`, `cache` -> storage; `privacy`, `privacy-diagnostics` ->
account; `about`, `feedback`, `feedback-help`, `keyboard`, `shortcuts`, `logs`, `diagnostics` -> help; an unknown slug opens the home.
Through the deep-link door an old row id names its dial too (`goto deeplink="wavee://open?route=settings&arg=crossfade"` opens
Playback and plays the arrival on Crossfade; `goto route=settings arg=<slug>` commits the route itself, with no arrival). The old
strip keys (`settings:tabitem:<slug>`) are gone: Classic and WinUI key their strip items `settings:cat:<slug>`. Each theme keys its
nodes from `SettingsKeys` (`Theming/Contracts/SettingsPageModel.cs`), so a link's arrival, these verbs and `lint` find the same node
in every theme:

| Key | What | Theme |
|---|---|---|
| `settings:search` | the page's search box (`SettingsPageProps.Search.Box(frame)` keys the theme's box node and binds it) | all |
| `settings:cat:<slug>` | a category's strip item or list word (`home`, `appearance`, `sound`, `playback`, `lyrics`, `notifications`, `system`, `storage`, `account`, `help`) | Classic, WinUI |
| `zs:link:<slug>` | a section word of the lowercase list (the same ten slugs) | Zune |
| `settings:dial:<dialId>` | a dial's row in a category body (`settings:dial:playback.crossfade`) | Classic, WinUI |
| `zs:row:<dialId>` | a dial's row | Zune |
| `settings:result:<id>` | a search result (a dial's id, or a pointer's: `settings:result:pointer.downloads`) | all |
| `settings:explain:<dialId>` | a dial's explanation badge (only where its clips exist for the theme in both app modes: the registered dials per theme are listed in `tools/settings-explain/README.md`, Status; in every theme, Lyrics blur, Crossfade and its duration, Normalization, Loudness level, the next-video countdown, the AI lyrics, Player style and its finish, Full-screen layout, Spectrum, the mini player's face and colour) | all |

`click key=settings:cat:sound` (Zune `click key=zs:link:sound`) opens a category, `type key=settings:search crossfade` types into the
box, `key down` / `key enter` walk and open the results, `key esc` clears the box (an open suggestion list closes with it) and a second
one leaves it (the box takes Escape ahead of its field while the caret is in it, `SettingsSearchModel.Box`), `key ctrl+f` on Settings
focuses the box instead of the app's search, and `key ctrl+188` (Ctrl+,) opens Settings on its home with the box focused.
`settings model=` reads what the page is handed and `settings press=` runs the box's keys without a caret.

### `settings {q? | focus? | model? | press?, limit?}`

The settings search (`Screens/SettingsSearch.cs`): the one index the Settings box, the command palette's Settings group and this verb
ask. `settings q=<text>` ranks the dials of the active theme and the feature pointers for a query as typed (folded, stemmed, prefix
and typo matches, phrases, AND then OR) and replies `{q, count, close, allocBytes, results:[{id, pointer?, score, fields, title,
place, under?}]}`: `fields` are where the best hits came from (`title`, `alias`, `terms`, `choice`, `caption`, `group`, `category`,
`phrase`, `english`), `under` names the result's parent when the parent is a result too, `close: true` (with `note`) means no dial
matched every word and the list is close matches, and `allocBytes` is what the query allocated (a second run, so the first use's
index build is not counted: it must be 0). No result adds `noMatch`, `didYouMean` (up to three indexed words two or three edits away)
and `searchApp`, the lines the results page shows. `limit` (default 8, up to 30) caps the list. `settings focus=<link>` hands a
settings link (`category`, `category/dialId`, `dialId`, or any old slug or row id) to the deep-link door exactly as
`wavee://open?route=settings&arg=<link>` does, settles, and replies with what it named (`known`, `category`, `dial`), the focus it
recorded (`{dial, seq}`: `seq` grows on every dial link, so the same dial twice replays the arrival) and the route; a feature
pointer's id (`focus=pointer.downloads`) opens the pointer's place instead. It needs a mount. An old slug lands on the category that
now holds what it opened. `settings` alone reports the index (`entries`, `tokens`, `postings`, `trigrams`,
`phrases`, `buildMs`, `culture`) and `missingTerms`: every composed dial and pointer with no `settings.terms.<id>` in the loc files
(it must be empty).

`settings model=<category>` prints what the page is handed: with Settings mounted, the props the page host
last pushed (`source: "page"`), else the same members built now (`source: "built"`). `category` (the open one's index), `categories`
(`{index, slug, title, strip, glyph}`: the home and the nine), `focus` (the arrival: `{dial, seq, category, group, parent, expand,
highlightMs}`), `home` (`glance` `{id, title, value, dial, glyph}`, `suggestions` `{id, text, action, actionLabel, dial, act, dismiss}`,
`recent` `{dial, title, value, from, agoMs, door}`), `body` (the named category, or the open one for `model=`: `groups` of `{id, title,
subtitle, rows}`, a row `{id, kind, label, description, subgroup, glyph, value, choices, writer, enabled, reason, silent, tier, explain
{clip, caption, path}, alsoInLine, alsoIn [{surface, place, door}], option, noteBefore, noteAfter, children}`) and `search` (the page box:
`{q, active, count, close, highlight, focusTicket, box, allocBytes, results [{id, key, title, breadcrumb, spans [[start, length]], marked,
under, pointer, row {kind, value, enabled, children}}], noMatch, didYouMean, searchApp}`). With `q=`, the query is written to the box's
cell and ranked as a keystroke does (`allocBytes` is a second ranking's: it must be 0); `marked` shows the matched letters in brackets.
`settings press=down|up|enter|esc` runs the box's key handler as a key in the bound box does (Down/Up move the highlight, Enter opens the
highlighted dial (the first when none is) in its category through the deep-link door, Escape clears, a second Escape leaves) and replies
the box's state, the focus and the route.

```text
settings model=playback q=crosfade         # the categories, the home, Playback's body and the box's results
settings press=down                        # highlight the first result
settings press=enter                       # open it: route settings playback, focus playback.crossfade
settings model= q=xyzzy                    # the no-match state: noMatch, didYouMean, searchApp
```

```text
settings q="dark mode"                      # appearance.appMode first, by its phrase
settings q=crosfade                         # a typo: playback.crossfade, then its duration
settings focus=lyrics.ai                    # opens Lyrics & Now playing on the AI lyrics card, as the AI sparkle does
goto deeplink="wavee://open?route=settings&arg=playback/playback.crossfade"   # the same door from a link
```

### `drop {path, import?}`

Reads a local file with the tag readers (`LocalTags.TryRead`, #240) and imports it into Local files the way a dropped file
is staged before it plays (`Playlist.ImportLocalFile`): the row gets the file's title tag (else its file name), its
artists, album and length. `import=false` only reads (nothing is staged; no mount needed), which makes it the diagnostic
over real files. The reply carries every field read: `format`, `why` (the reason a file is unplayable or unread), `kind`
(`Audio`/`Video`, with `videoW`, `videoH`, `hasAudio`), `title`, `titleOrFileName`, `artists[]`, `album`, `albumArtist`,
`track`, `disc`, `year`, `durationMs`, `estimated` (a bitrate estimate), `kbps`, `cover{exists, offset, length, mime,
encoding, pictureType}` (where the picture is; its bytes are never read), `keys{exactTitle, looseTitle, exactArtist,
looseArtist}` (`LocalMatch`), `spotifyUri` (as the official client would name the file), `readMs`, and with an import
`row{imported, uri, title, artist, album, durationMs, duration, video, image}` (`video`: the row is a local video file and plays as one;
`image`: its cover, a video's still from the `LocalArt` cache once it has landed: a second `drop` of the file reads it). It does not play the
file; `play <row uri>` does, and a local video resolves to its file at once (the inspector's video resolver holds only network videos), so
it really opens on the video host headless (`status log="first.frame"`).

```text
drop path=C:/music/song.flac
drop path=C:/music/song.mp3 import=false
```

### `decode {path, out?, rate?, seekMs?, ms?}`

Runs a local file through the decoder playback opens it with (its content's format through `Playback.Audio.CreateDecoderFor`: WAV and
AIFF PCM since #240, FLAC, MP3, Ogg) into raw float32 little-endian stereo frames at `out=` (none: the count only), at the mix rate
`rate=` (0, the default, is the file's own rate: no resampling, so the output can be compared with `ffmpeg -f f32le` sample by
sample). `seekMs=` seeks with the decoder's own seek first; `ms=` stops early. The reply: `format`, `decoder`, `opened`, `why` (a
refusal: "a WAV file with ADPCM audio"), `tagDurationMs`, `estimated`, `sourceRate`, `sourceChannels`, `exactFrames` and `tailKnown`
(the gapless info a join plans on), `seekedTo`, `frames`, `peak`, and for PCM `label` ("WAV 24/96"), `gain`, `layoutFrames` and
`sample`. No mount, no device. (`play` of a local row under `--fake` opens no source: this is the decoder check headless.)

```text
decode path=C:/music/a.wav out=C:/scratch/a.f32
decode path=C:/music/a.aiff out=C:/scratch/a.seek.f32 seekMs=1234
```

### `dj {do?, uri?, pages?, ms?, until?, file?, track?, on?, try?}`

AI DJ (plan `docs/plans/wavee/dj-implementation.md`). Plain `dj` (`do=status`) reads the fake DJ list (`spotify:playlist:pldj`),
the lexicon session playing it (pages, segments, the lookahead), the DJ book per queue row, the context's toggle locks, and
`narration` (D2): `current` (the deck's item, its position and length as the reducer holds them, `narrating`, `canSeek`, the
`published` position and duration the surfaces see: 0 while a narration plays, and the plain-text `caption`), `rows` (each
narration row the planner laid: `slot` welcome / intro / outro, the track it `belongsTo`, its `mediaUri` and `provider` as the
wire names them, the clip `key`, the clip's state, length and bytes, when and why it was asked for, `dropped`), `first` (the deck
and the next two rows), `nextJumpTarget`, `made` (every narration this session, the prefetched jump clips included), `prefetch`
(each ask: frame ms, `why` = `resolve` / `item+5s` / `reached`, slot, segment, key, the clip before the ask), `playLog`
and `playLogNarrations` (must stay 0), and `timeline` (each deck item: start and end on the frame clock, its length).
`do=play` starts the list as a click does; `do=answers` decodes the fake lexicon answers; `do=expire` ends the session's
validity. `do=run ms=<n>` plays on in REAL time (16 ms frames paced to the wall clock, so a clip's download and its playback
keep their live proportions) for `n` ms of the frame clock, `until=change` stopping when the deck moves. `do=transfer` asks a
transfer to the first other Connect device (`mount connect=nearby`); during a narration it is refused. In the fake service the
welcome is 3.4 s (whole), page 1's intro the 24.5 s streamed clip (its length is learned when its body ends and pushed),
page 2's jump 4.9 s, page 2's outro the broken fixture (dropped) and page 3's the flaky one. A track's end is
`transport do=end`; the controls during a clip are `transport do=next|prev|seek|repeat` and `dj do=transfer`, each answered in
`status log="intent refused"` (`Narration`). Headless, the silent endpoint holds 2 s ahead of the played count, so a boundary
announced on the render clock can read up to ~2 s off a clip's length on the frame clock; the lengths themselves are exact.

`current.restorePoint` is what the session document would keep for the next launch (during a clip: the track it belongs to,
from 0). `do=jump` presses "Let DJ pick" (D5): the rest of the segment goes to history, the user's queued rows wait behind the
target, and the deck lands on the target's prefetched jump clip right before it (`first` = `jump:<segment>`, the target, ...);
`do=starve on=true|false` makes the fake service answer no more pages. The Connect half (D4,
`Screens/Diagnostics.Inspect.DjConnect.cs`): `do=envelope uri=` (the play body a start sends another device), `do=cluster file=`
(ClusterUpdates as dealer pushes, then the mirror; a foreign DJ shows "Let DJ pick", which sends the `jump` signal),
`do=transfer file=` (a controller's transfer body; a current item `spotify:media:<id>` lands on that track from 0, the welcome's on
the session's first row), `do=restore track=` (the launch restore), `do=bodies`, and `do=signal` (a made-up inbound
`{"endpoint":"signal","signal_id":"jump"}` routed as the dealer glue routes it, plus the `outbound` body Wavee sends as a
controller). `transport do=putstate` during a clip shows the narration item as the official player sends it (f7 `spotify:media:`,
`narration/<slot>`, no f6, f17 3/9/13 `narration`, `suppress_progress`, `media.manifest`); f33 carries `jump` while DJ plays.

```text
mount w=1440 h=960 theme=winui route=home
transport do=clear                 # the fake boot's queued rows would play between the welcome and the intro
dj do=play                         # welcome on the deck, the first intro and the first track after it
dj do=run ms=6000 until=change     # welcome -> intro
dj do=run ms=40000 until=change    # intro -> its track
dj do=run ms=6000                  # the next segment's jump clip is prefetched 5 s into the item
dj do=jump                         # the jump clip, then the target, then the user's queue
dj do=signal                       # the same from a controller's `jump` signal
```

### `djtts {do, fixture?, ssml?, manifest?, via?, key?, seconds?, n?, wait?, pace?, rate?, ms?, every?, disk?}`

The AI DJ's text-to-speech client (`Spotify/Spotify.DjTts.cs`). The inspector is `--fake`, so the fulfil and the CDN are the in-process
transports of `Spotify.DjTts.Fake.cs` (silent MPEG-1 Layer III clips of exact length); everything above the wire is the real code. No
mount. Fixtures: `welcome` (3.4 s), `jump` (4.9 s), `intro` (24.5 s, a cache MISS streamed with no Content-Length over 5.3 s), `flaky`
(the fulfil fails once), `broken` (fails twice: dropped).

- `djtts fulfil fixture=welcome`: the body built from the narration keys (`body` hex, `fields`, `tail` against the spec's
  `18 05 28 01 30 06 38 c4 d8 02`), what the transport received (`request`: verb, url, headers, `receivedEqualsSent`), the `status` and
  `location`, `clientFollowsRedirects` (the live handler's setting) and `cdnRequestsDuringFulfil` / `followed`. `via=manifest` (or
  `manifest=<base64>`) sends a `media.manifest`'s field 1.1.4 verbatim (built here in reverse field order, so it differs from `keysBody`).
- `djtts ensure fixture=intro wait=8000`: `Ensure`, then up to `wait=` ms for the clip (and a spill pass) to settle: its state, `why`,
  attempts, bytes, `contentLength` (−1 streamed), frames, `durationMs`, memory and disk, the CDN's `X-Cache`, first byte and done times;
  `wasCached` says whether it was a hit. `seconds=200 n=9` makes distinct clips of that length (the 64 MB cap's proof). The twice-failing
  `broken` logs `narration dropped why=` (`status log="narration dropped"`).
- `djtts cache`: the counters (hits, misses, fulfils, retries, dropped, spills, reloads, evictions, disk adopts), memory against the cap,
  the spill folder and its files, the fake wire's request counts (`cdnRequestsWithCredentials` must stay 0), every clip (least recently
  used first).
- `djtts play fixture=intro` (or `key=`): opens the clip through `SourceRoute.Tts` (a fixture is ensured first, so a MISS plays while it
  downloads) and decodes it with the pump's `CreateDecoderFor`, paced at 1x (`pace=false`: as fast as the bytes land; `ms=` stops early):
  `route`, `format`, `openedComplete` (seekable), `length` (`decodedFrames` against `expectedFrames` and the decoder's `exactFrames`),
  `gain` (the decoder's folded factor against `NormalizationFactor` for the current setting), `timing` (`firstAudioMs`,
  `bytesLandedAtFirstAudio`, `startedBeforeDownloadDone`, the position when the download ended) and the position `series`.
- `djtts gain`: the narration's gain (−16 LUFS, −3 dBTP) under off, Quiet, Normal and Loud. `djtts reset disk=true` forgets the session.

```text
djtts fulfil fixture=welcome
djtts ensure fixture=broken wait=5000
djtts play fixture=intro every=2000
```

### `hint {do, id?, blob?}` and `recent {clear?}`

The in-app hints (settings rework phase 3: `Shell/Hints.cs`, the table of design 6.3, and the budget of 6.2) and Recently changed.
Neither needs a mount.

- `hint list` (the default `do`): every hint of the table in order, `{id, target, surface, action, priority, maxShows, snoozeDays,
  dismissRetires?, state?, drawn, shows, lastDay, snoozedUntil, dismissals, retired, trigger, verdict, home}`, plus `tipsOn`, `today`
  (the local day number the budget counts in), `session` (the one counted hint this session showed), `onScreen`, `forced` and
  `suggestions` (the Settings home's top three, ids). `verdict` is the budget's answer in the app, `home` on the Settings home
  (`qualifies`, `waiting` (the trigger does not hold), `notHere` (the hint cannot exist here: no such option in this theme, no sleep
  timer yet), `notDrawn` (no theme draws its placement yet: every hint until phase 6), `tipsOff`, `retired`, `snoozed`, `shownOut`,
  `shownToday`, `otherThisSession`, `satisfied` (the state it suggests is reached), `blocked` (a never-when gate: the wizard, another
  overlay, a hidden window, the first two minutes, a track's first seconds)). `trigger` is the trigger fact alone.
- `hint show id=<hint>` forces the hint's trigger true for ONE mount: the slot of its surface mounts the hint (the Common stand-in, key
  `hint:<id>`, its button `hint:<id>:act`, "Not now" `hint:<id>:dismiss`, after an inline write "Change" `hint:<id>:change`) whatever its
  trigger and budget say, and no show is counted. On a live mount it shows at once and the reply says `shown`; with nothing mounted it
  waits for the next mount. The mount after that clears it. The surface must be on screen: the lyrics footer (open the lyrics pane or the
  full screen's lyrics) or the Home banner (route `home`); the other surfaces mount their slot with their placement (phase 6).
- `hint reset` forgets every hint's budget, the use facts (bell opens, quick relaunches, sign-in launches) and the session, as a fresh
  launch with no blob. Recently changed stays (`recent clear=true` empties it).
- `hint state` replies the persisted blob (`hints.state`); `hint state blob="<blob>"` writes it to the store and reads it back as a
  launch does. The server's settings live in memory, so this is how a run carries the hints and Recently changed over a server restart:
  read the blob, start a new server, load it.
- `recent` lists the last five dial writes, newest first: `{count, writes:[{dial, title, value, surface, from, atMs, agoMs}]}` (`value`
  is the dial's value now; `from` the words for the source: "from the inspector", "from a tip"). Every value writer of the registry
  records into it, from any surface; `setting` writes record as the inspector, and the values a mount puts back record nothing.

A mount keeps the hint blob (it is the profile's state); a dial written through `setting` stays retired after the mount puts its value
back. `hint reset` is the clean slate.

```text
hint show id=ai-lyrics          # with the lyrics pane open: the stand-in in the lyrics footer
setting id=crossfade value=true # retires the crossfade hint, and appears in `recent`
hint list
recent
```

The Lossless badge's tip (`lossless-badge`, the player bar's surface) points at the mark (`np-lossless`): WinUI's TeachingTip, Classic's and
Zune's line, each wholly above the bar's top edge (never over its title row). Its slot is mounted wherever the bar's artists line holds the mark's
place (the widths above), so stand a lossless stream there first, then force it. In every theme `click key=hint:lossless-badge:act` ("Hide
badge") writes the switch off and turns the tip into its confirmation ("Badge hidden", `hint:lossless-badge:change`), and
`hint:lossless-badge:dismiss` is "Keep it" (it retires the tip); WinUI's tip keys its bubble `hint:lossless-badge` and its row's buttons the
same way (`:act`, `:dismiss`, `:more`). It skips the two minutes after launch; the track's first seconds still hold it.

```text
play uri=spotify:track:tr3
play format="FLAC 24/96"
hint show id=lossless-badge
```

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
| `text?` | `string` (the resolved StringId), `sizeDip`, `lineHeight?`, `weight`, `family?`, `color`, `case?`, `maxLines?`, `wrap?`, `trim?`, `charSpacing?`, `trimmed`, `natural {w, h}`, `overflowX?`, `overflowY?`, `drawn? {lines, cut, ellipsis, naturalLines, overhang?}`. A span run has an empty `string` and a `spans` count. An icon glyph (one or two private-use characters) prints as `glyph:<name>`, with its `codepoint` beside it. The name is the engine's `Icons` constant (`Icons.TryGetName`: `glyph:Bell`), else the app's own: the name a theme registered for a glyph it declares (`ThemeInspection.RegisterGlyphNames` in its composition file: `glyph:PlaySolid` for U+F5B0, `glyph:LyricsGlyph`), else `SegoeGlyphs` (`glyph:AddFriend`). Text in the app's own font (`WaveeIcons`, `family` ending `#WaveeIcons`) is named from that font's marks only (`glyph:Lyrics`, `glyph:PlayNext`), because they reuse Segoe code points (U+E900..U+E902). A glyph nothing names prints as `glyph:U+XXXX`. Text that mixes words and glyphs prints each glyph so, set apart from the words by a blank (`Afterglow glyph:U+E8B2`), and `codepoint` lists the glyphs' code points only. |
| `flags` | `visible`, `focusable`, `focused`, `hovered`, `pressed`, `disabled`, `clipsToBounds`, `scrollable`, `hitTest`. |
| `role?` | The automation role. |
| `handlers?` | `click`, `key`, `pointer`, `char`, `repeat`, `pressed`, `context`, `focus`, `drag`, `wheel`, `selectableText`, `spanLinks`, `gesture`, `clickRequestsContext`, `rowCells`. |
| `scroll?` | `{offset, max, viewport, content, axis, items?}`. |
| `anim?` | `true` while the node has live animation tracks. |
| `children` | The node's child count. |

**`trimmed`** comes from the engine's `AppHost.QueryTextOverflow`. It re-measures the run UNCUT (no trim, no line cap),
through the host's own font system, and compares that natural size with the laid-out box. It says what the words want,
not what the draw did:

- A single-line run is trimmed when it is wider than its box.
- A wrapping run is trimmed when its uncut block (laid at the box width) is taller than its box. A word wider than the box
  under `WrapWholeWords` does not set `overflowX`; `drawn.overhang` reports it.

`natural` is the size the run wants. **`drawn`** is what the draw's own layout did: the run laid out once as it is drawn
(its style at the auto-fit size, its trim and line cap, at the box width). `lines` is its line count; `cut` is true when it
does not show all of the content (a line ellipsized or cut by the trim, lines dropped past `maxLines`, an overflow suffix;
text drawn whole past its box is not `cut`, the box clips that); `ellipsis` is true when an ellipsis glyph was drawn.
Beside them, from the uncut run at the same width: `naturalLines` (the lines the words want) and `overhang` (a wrapped
word drawn past the box; hanging trailing spaces do not count). The two readings disagree exactly where a defect hides:
`drawn.cut` with `trimmed: false` is text the engine ellipsised although its words fit (`text.trim` class `fits`), and
`trimmed: true` with an ellipsis trim and no `drawn.cut` is text drawn whole past its box. `drawn` is absent under the
headless model, which never draws. `trimSource` says whether the answer rests on real metrics: `dwrite` means it
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
ON THE SCREEN (`excessSnapped + excessJumped - excessOffscreen`) is above `--max-moved` (default 0); the off-screen excess
counts only under `--gate-todo` (2026-11-17: a part moved wholly off the screen is classified by what drew it, which the control
step and the edge can see differently; on screen the same part is explained by the control). `control:null` with `controlNote` when no same-tier step exists (every move is then
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
| `counts` | `tracked`, `changedOnCommit`, `glided`, `snapped`, `jumped`, `excessSnapped`, `excessJumped` (past the control; `--gate` counts them less `excessOffscreen`), `unpainted` (changed at once but draws nothing, nor does anything under it that moved with it: a container whose children glide), `remounted`, `remountedSwap`, `remountedOutside` (what `--gate` fails on), `vanished`, `appeared`, `recounted`. Read the counts, not the length of a list. |
| `remounted` | Keyed nodes (unique before) that are a different node after the settle: identity, state and motion lost. Each has `family`: the key of the swap it sits under (the nearest node this step mounted whose registered enter AND exit are a DECLARED swap cross-fade: opacity only, and carrying the family id `SwapFamilies.Declare` gave it; the four theme helpers `ClassicTierMotion.SwapFade`, `ZuneMotion.SwapFade`, `WinUiDiscoveryMotion.SwapFade` and `WinUiScreenMotion.SwapFade` declare theirs; owner rule "a tier crossing keeps its parts" lets only such a genuinely different tree remount), or null: a rebuilt part. An undeclared opacity fade (an arm cross-fade such as Zune's `ArmFade`, a control's appear) is no family: Zune album W640's 76 arm parts count outside. Listed outside a family first. |
| `late` | Weights (report-only): `counts.excessOffscreen` is how many of the excess moves happened wholly off the screen (below the fold, scrolled out of their viewport: nobody saw them; Classic home H680 narrowing: 142 of 167; `--gate` subtracts them, `--gate-todo` fails on them), and `late[{key, frame, d}]` / `counts.late` the parts whose laid-out rect changed in the 20 settle frames after the commit with nothing easing them (a part landing late, which the commit-frame compare misses). `--gate` reads neither (it subtracts the off-screen excess); `--gate-todo` fails on both. A container that changes at once while its children glide is `unpainted` already. |
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
| `keys`, `prefix` | Nodes to watch: exact keys and key prefixes (comma list or JSON array; a prefix may carry control characters, so use the JSON form for `"2"`). Matches are re-resolved every frame, so a page that mounts mid-trace is picked up. At most `max` nodes (20 by default, 500 at most); duplicates of a key are `key#1`, `key#2`, and each node keeps its label from frame to frame (a node mounting or leaving earlier in reading order renumbers nothing; a new node takes a label no node held the frame before, or a lone key's replaced node's). |
| `scroll` | `main` (the shown vertical scroller with the most room, re-picked each frame) or a node key (the first scroller under it). |
| `every` | Emit every Nth row; the summary always covers every frame. `rows=false` leaves only the summary. |
| `curves` | `curves=true`: per watched key its motion curve (`curves[{key, frames, x\|y\|w\|h\|opacity: {v[], spark}}]`, only the channels that moved) and `motionFindings[{key, channel, kind, frame, delta, before, after}]`: `step` (one frame moves over 2.5x both neighbours), `reversal` (it turns back within 2 frames: a spring's overshoot does on purpose), `stall` (a still frame between visible moves), `snap` (the whole change in one frame); `motionCounts`. |
| `jump`, `snap` | Thresholds in DIP: 2 and 0.5. |
| `census` | `true` turns the host's render census on: a slow frame's row then carries `census`, the components that made it slow. Off by default (it costs frame time). |
| `flights` | `true`: each row carries the connected navigation flights (a card's cover flying into its page's hero, and back) and the summary their take-off and hand-off (`flight`, below). Off by default. |
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
`lastShown`, `velocity`, `max`, `content`, `viewport`, `motion`, `user`). With `flights=true`, `flights[{key, phase, back, pres, dest, shadow, srcHidden, destHidden, img, radius, destRadius, mix, standIn, clip, refinds}]`:
the flight's key (any entity's uri: an album, playlist, show, episode or user; `spotify:collection:tracks` for Liked Songs; an artist never flies),
`pending|flying|landed|handingOff|abandoned`, Back or forward, the flyer's presented rect, the dest's rest rect, the shadow's opacity,
whether each end's slot is hidden, the texture the flyer shows (it cuts to the dest's own rendition of the same picture once ready), the
flyer's corner radius this frame and the dest's (a circle opening into a square cover travels with the size), the dest picture's share when the
two ends show different pictures (a Home card's wide art becoming the square cover, a generated cover: they dissolve on the flight's spring; -1 one
picture), whether a landed flyer stands in for a dest picture not drawable yet (a cold page), the flyer's visible part (a card a
scroller's edge cuts), and how often the dest was found again (`refinds`: the loaded header replacing the skeleton's plate).

An artist's page flies nothing: it GROWS out of the item that opened it (WinUI, `PageMotionPolicy.GrowInto`), and on Back shrinks toward it.
Watch the page roots (`prefix=page:`): a node's `pres` composes every scale on its chain about each node's own transform origin, so the
pivot of a scaled page is `(pres.x - s*lay.x) / (1 - s)` with `s = pres.w / lay.w` (and the same in y), and on a forward grow it is the
clicked item's centre on every frame (`flights=true` shows none).

The **summary**: `settledAt`, `rootSolveFrames`, `escapeFrames`, `fullLayoutFramesInferred` (measures with no dirty marks), measure and component
totals, the `max` frame for layout/cpu/wall/record/flush/submit, `cpuOver8ms/16ms`, `snaps` (laid-out and presented rect changed with no track),
`jumps` (presented rect moved over `jump` DIP in a frame under a track; every eased glide has some), `instants` (a run of jumps on
consecutive frames whose largest frame moves at least `instant`=4 DIP and carries 60 % of the run: the change landed at once
while something above it animated, a page reflowing in one frame beside a sliding pane; a node whose own rect stands still while
an animating ancestor's clip uncovers or covers it, a FlowReveal opening over a control, is no jump and no instant: those frames
carry its run on and are counted as `uncovered`), and `scroll`:
`reversals`, `overshoots` (runs outside [0, max], with the deepest point), `stalls`, `uneven` deltas, `layoutDuringScroll` and `deltas`
statistics. Beside the summary, `counts{watched, snaps, snappedKeys, instants, movedAtOnce, remounted, remountedSwap,
remountedOutside}` and `remounted[{key, frame, family}]`: a lone watched key that is a different node from one frame to the
next, with the swap family fading it in (as `crossing` names it) or null. `inspect.py --gate` fails a trace whose
`movedAtOnce` (snapped or instant keys) is above `--max-moved` or that remounted a key outside a family, and warns on a trace
that watched nothing (pass `prefix=`/`keys=`, and `max=` above 20 for a page). `python -I tools/inspect.py --trace ...`
prints the summary as text: the motion counts, the remounts, the snaps, instants and jumps (30 each), `after`.

With `flights=true` the reply also carries `flight{flights, startedAt, landedAt, handedOffAt, takeoffDelta, handoffDelta,
maxStepDip, radiusDelta, mixAtHandOff, stoodInFrames, refound, standingIn, abandoned, unfinished}`: the frame the flyer first showed,
landed and handed off (the real node shown: the flyer gone on a cut, or `handingOff` starting its fade over a header whose copy lies on it), the largest
edge difference between the flyer and its source on the first frame that drew it (the engine's record) and its dest on the hand-off frame,
the largest edge step between two frames, the radius and mix on the hand-off frame, the frames it stood in, whether the dest was found
again, whether the trace ended while it stood in (`standingIn`: not `unfinished`), and whether a flight was abandoned (a new navigation
mid-flight, a dest scrolled under half visible) or never ended. `inspect.py --gate` fails a flight whose `takeoffDelta`, `handoffDelta` or
`radiusDelta` is over 0.5 DIP, that handed off with its two pictures still mixed (`mixAtHandOff` between 0.01 and 0.99), or that is
`unfinished`. `status`
carries `connected{flights, pending}`. A forward flight: `click key=lazy-cell:8 trace=60 keys=hero-cover flights=true` on
`disco:0:spotify:artist:ar3`; Back: `key alt+left trace=45 keys=hero-cover,lazy-cell:8 flights=true`. Click a card on its label, not its
centre: the centre of a WinUI card's cover is its play button.

### `perf {do, budget?, judge?, census?, rows?, items?, path?, reset?}`

A frame-cost capture around any verbs (`Diagnostics.Inspect.Perf.cs`). `perf on` (needs a mount; later mounts are followed) records
every frame any later verb runs, its own frames and its settle, under that verb's label; `perf report` judges them; `perf off`
stops. The milliseconds are real CPU wall time of the frame's phases on the UI thread, while the motion plays on the 16 ms clock:
a Debug build's numbers, relative, not the shipped ones. JIT time (`FrameStats.JitMs`) is subtracted (NativeAOT ships none);
`submit` and `gpu` on `gpu=warp` are WARP's CPU rasteriser, reported apart (`gpuMaxMsWarp`), never judged as GPU numbers.

| Arg | Meaning |
|---|---|
| `do` | `on`, `off`, `report` (default), `status` (positional: `perf on`) |
| `budget` | ms. Default: the display the mount's `dt` stands for (16.67 at dt=16; dt=8.333 for 120 Hz) |
| `judge` | `ui` (default: flush + layout + anim + record + postSubmit, JIT subtracted) or `total` (adds submit) |
| `census` | `on`: the host's render census names the components of a slow flush (costs frame time: judge with it off) |
| `rows`, `items`, `path` | report: the rows listed (`over`, default: the over-budget and alloc frames; `all`; `none`), the most per list, and a JSON file of every row |
| `reset` | report: forget the frames after it |

`perf report` replies `frames, budgetMs, budgetFrom, judged, devices, note, gate{over, alloc, dropped, failed}`, `glyphsDroppedSession`, `dropped[{frame, verb, glyphs}]` (glyph quads the GPU backend did not draw: text missing for a frame), `verbs[{verb, frames,
animating, presents, over, maxMs, p95Ms, medianMs, maxFrame, jitMs, submitMaxMs, gpuMaxMs|gpuMaxMsWarp, measures, hotAllocBytes}]`,
`over[{frame, verb, ms, top, topMs, why}]` (the top phase and what is behind it: the relayout scopes and their measures for a
layout, the components for a flush, the effect owner for postSubmit), `alloc[{frame, verb, bytes, why}]` (a STEADY animation frame,
something animating and no component, reactive unit, bind write or measure, that allocated on the hot phases 6-13),
`allocOtherFrames` and `rows[{f, verb, ms{ui, jit, flush, layout, anim, record, postSubmit, submit, gpu}, measures, scopedMarks,
escapes, components, hotAlloc, animating, presented, scopes[{key, measures, kind}], census?}]`. A scope's key is the node's own, or
`key >n` for the keyed node n levels above it. `tools/inspect.py --gate-perf [MS]` exits 1 on any over, alloc or dropped frame (a row with a GC carries `gc:[g0,g1,g2]`: a GC reads as up to 8 KB of alloc) (2 on
`ok:false` or no report); `MS` sets the budget of every `perf report` line that names none. Warm the path first (run the
interaction once before `perf on`) and compare against a base build: in Debug, Home's full relayouts on a resize run near 16 ms.

```text
mount w=1440 h=960 theme=winui route=home gpu=warp
home do=page key=nmf page=2
home do=page key=nmf page=1                # warm: JIT and decodes paid here
perf on
home do=page key=nmf page=2
perf report                                # gate{over:0, alloc:0}: a 2.2 ms worst frame (Debug, WARP)
```

### `film {frames, every?, path, cols?, scale?, crop?, framesDir?, keys?, prefix?, ...}`

Motion you can look at (`Diagnostics.Inspect.Film.cs`). Traces `frames` frames from where the host stands, so start the motion
with the acting verb's `settle=false`, and keeps every `every`-th frame (and the last) in ONE contact-sheet PNG at `path`: `cols`
cells per row, each frame scaled by `scale` (default 0.5) and labelled `f12 +208ms` (frame and time on the 16 ms clock). `crop=<key>`
keeps that node's drawn rect (8 DIP around it, as it stood when the film began; it must be on the window); `framesDir=<dir>` also
writes each kept frame whole (that allocates per kept frame; without it film reuses one capture buffer and may share a `perf` window). With `keys`/`prefix` (and every trace option) the reply carries the motion `curves` (see `trace
curves=true`). Needs pixels: `gpu=warp` or `gpu=hardware`. Reply: `path, traced, cells, crop?, every, clock, cellFrames[], watch[],
curves[]?, motionFindings[]?, motionCounts?, settledAt, snaps, instants, remounted`.

```text
scroll key=home:scroll dy=14
home do=page key=nmf page=2 settle=false
film frames=36 every=3 cols=4 scale=0.6 crop=home:tile:feed:spotify:section:wavee-mockup-nmf path=C:/WAVEE/scratch/x/pager.png keys=[home-shelf-card:spotify:playlist:37i9dQZEVXbp4UblnlhiEI]
```

### `nudge {fixture?, channel?, session?, window?, policy?, clock?, trouble?, sessionAge?, starsCache?, stars?, store?, link?, fire?, tick?, raw?, reload?, refresh?, expect?, ...}`

The rate/star nudge (`Screens/RateNudge*.cs`; verb in `Diagnostics.Inspect.Nudge.cs`). With no args it reads: the decision, the ledger, the
card. It does not settle: step frames yourself (`frames 1300` is about 20.8 s on the 16 ms clock). `fire`, `tick`, `refresh` and
`expectCard` need a mount. `mount` ends by putting the `fresh` fixture back, so no run inherits the last one.

Order inside one command: `raw`, `fixture`, `channel`/`session`/`policy`/`sessionAge`/`starsCache`, `clock`, `trouble`, `window`/`store`/`link`/`stars`,
`reload`, `refresh`, `fire`, `tick`, then the reply and the `expect*` gates. A fixture, `channel`, `session`, `policy`, `sessionAge`, `starsCache` or
`trouble` re-seeds the host (the card and the armed track change are cleared); loading a fixture also resets every pin to its default.

- `fixture=` loads a ledger, the calendar and the channel (Store unless said): `fresh`, `day3`, `eligible` (30 days, 18 listening days, 26 h: the 25 h
  milestone card), `short-days`, `short-hours`, `asked-60`, `asked-130`, `asked-130-quiet`, `capped` (a new install's two asks used), `rated`,
  `starred` (github), `opted-out`, `troubled` (-2 d), `troubled-old` (-8 d), `milestone-99`, `milestone-100`, and the install that predates
  the feature: `existing` (just seeded, launch ask pending, `session=after-update`), `existing-lapsed`, `existing-later`, `existing-capped`.
- The four fakes (the only ones): calendar time is the fake seed date plus `clock=` (an absolute offset: `+130d`, `-20d`, `+6h`, `reset`),
  the window fact is `window=front|behind|minimized|tray` (default front), the Store dialog answers `store=succeeded|canceled|network|error|unavailable`
  (default succeeded), and opening a URL answers `link=ok|fail` and records the URL (`launched`). The star fetch answers `stars=120|fail`
  (default fail) and is counted (`stars.fetches`); `starsCache=86@-2h|86@-8d|none` seeds the cache; `refresh=true` runs the daily refresh now.
- `fire=true` is a track boundary through the playback effect's own entry; `tick=true` the once-a-second check. A quiet moment is `nudge fire=true`,
  `window advance=21000`, `frames 1300`, `nudge tick=true`. `trouble=now|-8d|none` writes a fault. `raw="<string>"` writes the ledger setting, `reload=true`
  re-reads it (a bad value starts fresh). `sessionAge=<minutes>` ages this launch.
- `expect=show:rate:1` (also `show:star:2`, `show:rate:1:launch`), `quiet:<Rule>` (`quiet:Idle`) or `closed`, comma-separable; `expectLedger=asks=1,last=NotNow,enabled=true`
  (the `ledger.text` fields); `expectLaunched=<url prefix>|none`; `expectCard=shown|hidden`. A mismatch is `ok:false` with `error`
  (`expected quiet:InstallAge, got show:rate:1`) and the same body, so `inspect.py --gate error` fails the run.

Reply: `channel, now, decision{show, ask, kind, askNumber, closed, blocker{rule,stage,pass,detail}|null, earliest|null, checks[{rule,stage,pass,detail}]},
ledger{text, raw}, card{shown, ask?, askNumber?, held?}, launched[], stars{shown, cached, fetches}, after`. `decision` is the host's last evaluation since the
fixture (the one that showed the card, when one did), else a fresh one that is not acted on.

```text
mount w=1440 h=960 theme=winui route=home
nudge fixture=eligible
nudge fire=true
window advance=21000
frames 1300
nudge tick=true expect=show:rate:1 expectCard=shown
click key=rate-nudge:later
nudge expectLedger=asks=1,last=NotNow expectCard=hidden
```

## Typical workflows

### Hunt a hiccup

The recipe for any Home motion (the fgpu skill's `verify/debug-home.md`; `fg hiccup "<acting verb>" --undo "<verb>"` writes and
runs it): warm the path, `perf on`, the interaction, `perf report` under `--gate-perf --gate error`; then the interaction with
`settle=false` and a `film` of it; `media=fake mediaStartCostMs=40` stages a slow video start; `home do=explain` says why a
section is there.

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
