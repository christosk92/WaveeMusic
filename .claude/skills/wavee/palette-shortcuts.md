# Command palette & shortcuts

Wavee's Ctrl+K palette (`WaveeCommandPalette` in `src/apps/Wavee/Features/Shell/WaveePalette.cs`) is a port of the gallery `CommandPalette`: `Popup.Create(FocusTrap: true, Chrome: Popup)` over a `TextBox` + ranked result list. The type is not named `WaveePalette` — that name is already the cover-colour mapper in `Design/WaveePalette.cs`.

## How to add a command

Edit the **static builtin table** in `WaveeCommands.CreateBuiltins()` (`src/apps/Wavee/Features/Shell/WaveeCommands.cs`), or register a `WaveeActionDescriptor` that accepts `NowPlaying`, `ActiveRoute`, or `None`. Registry actions are merged at palette-open via `WaveeCommands.BuildIndex`.

A builtin is one `WaveeCommands.Entry`:

```csharp
Nav("nav.home", Loc.Get(Strings.Nav.Home), Icons.Home, "home"),
Play("playback.next", Loc.Get(Strings.Player.Next), Icons.Next, PlaybackVerb.Next),
Set("settings.theme", Loc.Get(Strings.Settings.Appearance.Theme), Icons.Brush, SettingsVerb.ToggleTheme),
Set("settings.npvPresentation", Loc.Get(Strings.Player.PresentationToggle), Icons.Picture, SettingsVerb.NpvTogglePresentation),
Set("settings.npvNextStyle", Loc.Get(Strings.Player.PlayerStyleNext), Icons.Album, SettingsVerb.NpvNextStyle),
```

`settings.npvPresentation` ("Now Playing: Cover / Player") and `settings.npvNextStyle` ("Player style: next") are the Now
Playing player-styles palette entries (`docs/plans/wavee/npv-player-styles-implementation.md`) — they call the same
`NpvPlayerPrefs.TogglePresentation`/`NextStyle` writers as the header row, the gear flyout and the artwork's context
menu, tagged with `NpvDiagnostics.SourcePalette`.

Then handle the new `PlaybackVerb` / `SettingsVerb` / route key in `WaveeCommands.Invoke`. Do **not** add per-keystroke closures or LINQ — `Filter` is an array scan over pre-lowercased `LabelLower` into a caller-owned `MaxResults` buffer.

A `>` prefix (VS Code) restricts the scan to commands. Typing without `>` also appends a **Search for X** row that navigates to the Search page with the query as `Route.Arg`. Catalog search itself is the Search page pipeline (`SearchQuery.Slot`); the palette does not call it.

## Shortcut table

| Chord | Action | Wiring |
|---|---|---|
| Ctrl+K | Toggle command palette | `FocusSearchChord` → `OpenPalette` (subsumes omnibar) |
| Ctrl+F | Focus omnibar | `FindChord` → `_searchFocusRequest` (MergedChromeRow `FirstFocusableIn`) |
| Ctrl+T | New Home tab | existing `NewTabChord` |
| Alt+Left | Back | `BackChord` → `Back()` |
| Alt+Right | Forward | `ForwardChord` → `Forward()` |
| Ctrl+= / Ctrl+Shift+= / Ctrl+Numpad+ | Zoom in | `ZoomInChord` / `ZoomInShiftChord` / `ZoomInNumpadChord` → `WaveeShell.ZoomStep(+1)` |
| Ctrl+- / Ctrl+Shift+- / Ctrl+Numpad− | Zoom out | `ZoomOutChord` / `ZoomOutShiftChord` / `ZoomOutNumpadChord` → `ZoomStep(-1)` |
| Ctrl+0 / Ctrl+Numpad0 | Reset zoom | `ZoomResetChord` / `ZoomResetNumpadChord` → `ZoomStep(0)` |
| Ctrl+mouse-wheel | Zoom in/out | `InputHooks.ZoomWheel` (shell mount effect → `ZoomStep(±1)`). Mouse wheel only — the platform consumes Ctrl+touchpad scroll as pinch synthesis before the dispatcher. |
| Space | Play/pause | `column.OnKeyDown` → `OnShellKey` (not an accelerator) |
| Left / Right | Seek back / forward: 5 s, accelerating while held (15 s after 0.5 s, 30 s after 1.5 s) | Player bar focused, or the full-screen stage: `PlayerKey` → `Shell.BarKeyStep` → `KeyboardScrubLadder` |
| Shift+Left / Shift+Right | Fine seek: 1 s steps, no acceleration | same path, `PlayerKeyIntent.SeekBackFine` / `SeekForwardFine` |
| Mouse XButton1/2 | — | **Not delivered.** `InputDispatcher` handles buttons 0/1/2 only. |

### Space semantics

`InputDispatcher.OnKey` runs focused routing **before** accelerators, and accelerators only match Ctrl/Alt or F1–F12. Bare Space therefore never fires a `KeyAccelerator`. The shell listens on the chrome column's `OnKeyDown` so Space bubbles there only after the focused node declined it.

Buttons consume Space as activation (dispatcher arms click, no bubble). Editors (`AutomationRole.Text` or `InteractionInfo.CharBit`) are skipped in `FocusedIsTextEditor` so a typed space is not also play/pause (`EditableText` inserts Space via `OnChar`, not `OnKeyDown`). Trust that contract; do not add a shell-level Space accelerator.

### Seek keys: the keyboard scrub ladder

Left/Right on the focused player bar (and the full-screen stage, which calls the same `Shell.BarKeyStep`) drive `KeyboardScrubLadder` (`src/apps/Wavee/Playback/Playback.Scrub.cs`, pure, unit-tested in `KeyboardScrubLadderTests`). The engine delivers no key-up, so the ladder steps a **visual** target and commits once the key goes quiet:

| Rule | Value (`KeyboardScrubLadder` constant) |
|---|---|
| First step and the first 0.5 s held | 5 s (`StepMs`) |
| Held for 0.5 s or more | 15 s per step (`MidMs`, after `MidAfterMs = 500`) |
| Held for 1.5 s or more | 30 s per step (`FastMs`, after `FastAfterMs = 1500`) |
| Shift | 1 s per step (`FineMs`), never accelerates |
| Step rate | at most one step per 150 ms (`MinStepIntervalMs`): the OS key repeat runs at about 30 Hz, far too fast to step per repeat; a press inside the interval only keeps the hold and the grace alive |
| Commit | one seek 250 ms after the last press (`GraceMs`), from the bar's 50 ms tick (`BarKeyLadderTick`, mounted by every seek rail) |

A press after more than 250 ms of silence starts a fresh ladder from the current position, so the acceleration clock restarts. Until the commit, every rail and time label paints the target (`s_keyPreviewMs`), and it stays painted until the seek lands so the thumb never steps back. The target is clamped to the seekable span: `[0, duration]`, or a live podcast's DVR window (`SeekRail.KeySpace`); with no seekable span a press is a no-op and never commits a seek to 0. A track change drops a live ladder. The podcast ±10 s step buttons are separate (`Shell.BarSeekBy`, one seek each, no ladder); `PlayerSeekAccumulator` is gone.

### Ctrl+F / in-page filter

`DetailTracks` owns a private `_searchExpanded` with no shell-reachable ticket. Ctrl+F therefore focuses the omnibar. Keep `_searchFocusRequest` for that path so Wave 1's `FirstFocusableIn` fix in `MergedChromeRow` keeps working. Wiring an in-page filter ticket is a follow-up on the page that owns the field.

## Announcer pattern

```csharp
if (Announcer.IsAvailable)
    Announcer.Say("Command palette");           // settled edge (open)
Announcer.SayThrottled(n + " matching commands"); // keystroke run — drops intermediates
```

Compose the string on the **edge** that triggered it (palette open, query change, `CurrentTrack` write). Never inside frame phases 6–13. Test `IsAvailable` before allocating a spoken line.

Track-change `SayThrottled` lives on `WaveeShell` as a `UseEffect` over `PlaybackBridge.CurrentTrack` (signal write = track boundary, not per-frame). Like/save confirmations are not in the shell — do not chase every heart across the app.

## Focus on open

`PopupOptions.FocusTrap: true` makes OverlayHost `FocusNode(FirstFocusableIn(wrapper))`. Under the palette `TextBox` that descendant is the chromeless `EditableText`, never a `PartRoot` chrome node. Do **not** `FocusNode(PartRoot)` — IME/caret never arm. See `focus-pitfalls.md`.
