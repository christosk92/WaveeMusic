# Focus pitfalls — programmatic focus on composite controls

## The trap

Programmatic focus on a composite control's **chrome** is not the same as focusing its **editor**.

`InputDispatcher.OnChar` / `OnKey` walk from `_focused` **up** through ancestors and never into children. `SetFocus` will happily focus a non-`Focusable` node and paint a focus ring on it (`visual: true`). Result: the chrome looks focused, IME/caret never arm (`EditableText.HandleFocus(true)` never runs), and typing does nothing.

Pointer clicks were never affected. `NearestFocusable` resolves the innermost self-or-ancestor carrying `NodeFlags.Focusable`, so a click on the field lands on the `EditableText`.

## The rule

For a composite (`AutoSuggestBox`, editable `ComboBox`, …), resolve the editor before focusing:

```csharp
var chrome = field.Value;   // AutoSuggestBox.PartRoot — Role=ComboBox, typically not Focusable
var editor = hooks.FirstFocusableIn?.Invoke(chrome) ?? NodeHandle.Null;
if (!editor.IsNull) hooks.FocusNode?.Invoke(editor, true);
```

`InputHooks.FirstFocusableIn` is host-wired to `InputDispatcher.FirstFocusableIn`. Under `PartRoot` the first focusable descendant is the chromeless `EditableText` (the query button is later in document order). Working exemplars: `OverlayHost` (focus-trap entry) and `DetailTrackSearchField` (captures `EditableText.PartRoot` directly).

Keep `OnFocusChanged` on `PartRoot`. `SetFocus` fires GotFocus as a **bubbling** routed event (`InputDispatcher.SetFocus`, ancestor walk after the focused node itself), so a chrome-level `_focused` / `_searchFocused` signal still updates when the editor takes focus.

## Fixed sites

- `MergedSearchField` in `src/apps/Wavee/Features/Shell/MergedChromeRow.cs` — Ctrl+K / `_searchFocusRequest` used to `FocusNode(PartRoot)`.
- `LibraryV3Search` in `src/apps/Wavee/Features/Sidebar/Modes/LibraryV3/LibraryV3Search.cs` — expand-to-field used to `FocusNode(PartRoot)`.

## Regression gate

`gate.controls.autosuggest-programmatic-focus` in `src/FluentGpu.VerticalSlice/Suites/ControlsSuite.cs`:

1. `SetFocus(PartRoot)` + Char `'a'` → text stays `""` (documents the chrome-focus trap).
2. `SetFocus(FirstFocusableIn(PartRoot))` + Char `'a'` → text `== "a"` and focused role is `AutomationRole.Text`.
