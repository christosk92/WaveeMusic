# Command palette & shortcuts

Wavee's Ctrl+K palette (`WaveeCommandPalette` in `src/apps/Wavee/Features/Shell/WaveePalette.cs`) is a port of the gallery `CommandPalette`: `Popup.Create(FocusTrap: true, Chrome: Popup)` over a `TextBox` + ranked result list. The type is not named `WaveePalette` — that name is already the cover-colour mapper in `Design/WaveePalette.cs`.

## How to add a command

Edit the **static builtin table** in `WaveeCommands.CreateBuiltins()` (`src/apps/Wavee/Features/Shell/WaveeCommands.cs`), or register a `WaveeActionDescriptor` that accepts `NowPlaying`, `ActiveRoute`, or `None`. Registry actions are merged at palette-open via `WaveeCommands.BuildIndex`.

A builtin is one `WaveeCommands.Entry`:

```csharp
Nav("nav.home", Loc.Get(Strings.Nav.Home), Icons.Home, "home"),
Play("playback.next", Loc.Get(Strings.Player.Next), Icons.Next, PlaybackVerb.Next),
Set("settings.theme", Loc.Get(Strings.Settings.Appearance.Theme), Icons.Brush, SettingsVerb.ToggleTheme),
```

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
| Space | Play/pause | `column.OnKeyDown` → `OnShellKey` (not an accelerator) |
| Mouse XButton1/2 | — | **Not delivered.** `InputDispatcher` handles buttons 0/1/2 only. |

### Space semantics

`InputDispatcher.OnKey` runs focused routing **before** accelerators, and accelerators only match Ctrl/Alt or F1–F12. Bare Space therefore never fires a `KeyAccelerator`. The shell listens on the chrome column's `OnKeyDown` so Space bubbles there only after the focused node declined it.

Buttons consume Space as activation (dispatcher arms click, no bubble). Editors (`AutomationRole.Text` or `InteractionInfo.CharBit`) are skipped in `FocusedIsTextEditor` so a typed space is not also play/pause (`EditableText` inserts Space via `OnChar`, not `OnKeyDown`). Trust that contract; do not add a shell-level Space accelerator.

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
