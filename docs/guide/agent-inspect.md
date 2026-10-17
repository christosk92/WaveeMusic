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
| `--pipe <name>` | Serve one client on `\\.\pipe\<name>` (stdout first prints `{"kind":"listening","pipe":...}`). Default: stdin/stdout. |
| `--profile <dir>` | The scratch profile. The default is a new folder under `%TEMP%\wavee-inspect`, which is deleted at exit. The user's `%LOCALAPPDATA%\Wavee` is refused. |
| `--text dwrite\|headless` | `dwrite` (default): real DirectWrite metrics, plus the offscreen GPU for `render`. `headless`: the shell tests' 0.55 × size font model and recording device. |

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
- **Text form.** Bare words fill the positional names: `goto home`, `find zune-band-pivot:`, `frames 30`. `type` takes
  the rest of the line as its text.
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
`play`, `theme`, `option`) settle by themselves: they run frames until no animation track is left, at most `max`
frames (600 by default). Their replies report `frames` and `settled`. Pass `settle=false` to skip this and step the
frames yourself with `frames`.

## Verbs

### `mount {w, h, scale?, theme?, kind?, route? | uri? | deeplink?, gpu?, keepRoute?}`

Mounts the whole app at `w` × `h` DIP. Any mount that is already up is replaced.

| Arg | Values |
|---|---|
| `scale` | DPI scale, 1 by default, at most 4. |
| `theme` | `classic` or `zune`. Zune turns Developer mode on; Classic turns it off. |
| `kind` | `dark` or `light`. |
| `gpu` | `warp` (default) or `hardware`: the offscreen D3D12 surface, so `render` works. `none`: DirectWrite over the recording device (no pixels). |
| `keepRoute=true` | Keeps the current navigation. Without it, the mount starts from a fresh Home. |

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

`frames` runs exactly `n` frames and replies with `{"frames":n,"animating":bool}`. `settle` runs frames until no
animation track is left, for at most `max` frames, and replies with `{"frames","settled"}`.

### `reset`

Unmounts, resets the navigation to a fresh Home, and makes Classic dark the active theme.

### `unmount`, `status`, `help`, `quit`

- `status` reports whether something is mounted, the frames run so far, the theme and kind, the route, the text backend
  and the network leaks. When something is mounted, it adds the mount facts, `animating` and `trimSource`.
- `help` lists the verbs.
- `quit` unmounts and ends the server.

### `click`, `hover {key | x,y, nth?, mods?, button?}`

These aim at the centre of a node's presented rect, or at a point. `nth` picks among nodes that share a key.

- `click` sends a pointer move, a press and a release. `button=right` sends a right click, and `mods=ctrl+shift` holds
  modifiers.
- `hover` only moves the pointer.

```json
{"cmd":"click","args":{"key":"zune-band-pivot:library"},"id":4}
→ {"id":4,"ok":true,"cmd":"click","frames":36,"settled":true,"at":{"x":212.5,"y":70},"key":"zune-band-pivot:library"}
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

Turns the mouse wheel at the target: `dy` notches, positive toward the end of the content (3 by default). With `key`,
the reply carries `scroll {offset, max}` of the first scroll viewport under that node.

### `play {uri, fromMs?}`

Plays one fake track or episode as its own context. This is the headless probe's recipe: `BuildContextQueue`, then
`Queue.Replace`, then `Playback.PlayNow`.

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

Lists the live nodes whose key equals `key` or starts with `prefix`. Each entry has its rect, whether it is `shown`,
its kind, and the first text under it (a button's label).

```json
{"cmd":"find","args":{"prefix":"zune-band-pivot:"},"id":5}
→ {"id":5,"ok":true,"cmd":"find","nodes":[{"key":"zune-band-pivot:home","rect":{"x":16,"y":44,"w":78,"h":40},
   "shown":true,"kind":"Component","text":"home"}, ...],"count":4}
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
| `text?` | `string` (the resolved StringId), `sizeDip`, `lineHeight?`, `weight`, `family?`, `color`, `case?`, `maxLines?`, `wrap?`, `trim?`, `charSpacing?`, `trimmed`, `natural {w, h}`, `overflowX?`, `overflowY?`. A span run has an empty `string` and a `spans` count. |
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
- the crossed `rungs`, by id

It also returns the active `theme` and `kind`.

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

What to compare across the four sizes:

- The left edges of the title-bar lead, the band and the first content block, against the tier's gutter.
- Every `text` with `trimmed: true`.
- The distinct `sizeDip` / `lineHeight` pairs, which make up the type scale.
- The hit targets under 40 DIP at Compact.

`python -I tools/inspect.py -f audit.txt --pretty` runs such a file.

### Check a crossing

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
click key=<a card key from dump keysOnly=true>
status
dump root=<the page's root key> depth=3
```

## Notes

- **One process, one boot.** `Shell.Headless.Boot` runs once per process. A mount replaces the previous one; it does
  not reboot.
- **Shell tests use the same session.** `Diagnostics.InspectSession` is the UI-thread half, and it owns no thread.
  `Wavee.ShellTests/InspectSessionMountedTests.cs` drives it on the test host.
- **The helpers are shared.** `Diagnostics.InspectKit` holds Until, Settle, Subtree, Play, the fixture grader and the
  theme activation. The shell tests call it for Play, the fixture grader and the Zune activation.
- **No reflection.** JSON is parsed with `JsonDocument` and written with `Utf8JsonWriter`, so the server is AOT-safe.
