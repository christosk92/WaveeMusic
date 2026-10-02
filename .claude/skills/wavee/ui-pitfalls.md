# UI pitfalls seen in the shell

Bugs that look like engine invalidation failures but are app-side. Read before touching the title bar, a pill/button
row, or before diagnosing "it only updates after I resize".

## "It only updates after a resize" = a render memo whose key misses an input

The engine's `TitleBar` (`..\fluent-gpu\src\FluentGpu.Controls\TitleBar.cs`) memoizes its whole element tree on a
key built from `ContentVersion()`. The `Leading`/`Trailing` builders run **only** when that key changes; otherwise
the cached tree is returned. The app's key is `ChromeContentVersion()` in `src/apps/Wavee/Shell/Shell.UI.cs`.

Rule: **every value a title-bar builder reads must be hashed into `ChromeContentVersion()`**, and it must be the value
the builder actually reads, not its upstream source. The 2026-10-02 bug: the trailing island built from
`ChromeLayout.Value.Chip`, but the key hashed `Auth.Value`. `ChromeLayout` is published by an effect one run after
`Auth` flips, so the bar rebuilt once with the stale chip, cached it, and the next render hit the memo. The title bar
showed "Connecting..." until a resize changed the widths in the key. The fix was to hash `(int)l.Chip`.

When the symptom is "the state changed but the UI did not, until a relayout", look for a memo or version key
before suspecting the engine's dirtying. `HashCode.Combine` takes at most 8 arguments, so nest a `Combine` when
adding more.

## Pill and button rows must wrap

A header row of non-shrinking pills (`Controls.RootNoShrink` / `Shrink = 0`) in a non-wrapping `BoxEl` overflows
and clips the last pill in a narrow pane (the Queue panel's Shuffle / Repeat / Autoplay at about 280 DIP). Set
`Wrap = true` on the row's `BoxEl` (`Element.cs`, flex wrap; `Box.Wrap(gap, children)` is the factory form). `Gap`
also spaces the wrapped lines (`FlexLayout.MeasureWrap`). No width math, no breakpoints.

## Check which exe is running before diagnosing

A `dotnet run`, a VS publish-profile build (`bin\Release\net10.0\win-arm64\publish-profiles-*\Wavee.exe`) and a
script publish are different binaries with different stamps. Before you explain any behaviour, get the running path:
`Get-Process Wavee | Select-Object Id, Path, StartTime`. "Send a test crash report: no crash service set up" was
simply an unstamped publish-profile build running instead of the stamped verify build. Stop probe launches by PID
only, never by image name.
