# Wavee sidebar — pitfalls

Each entry is **symptom → cause → rule**. Read the entry before debugging "why doesn't X update" or before a change
that touches the same seam.

## Rendering and props

**The window re-skins on every render.** Cause: a fresh `SidebarLayoutDoc` (or a fresh `SidebarRowPlan`) per
render, so the pane sees a new document identity and re-plans. Rule: cache the doc by its overlay reference, as
`PaneView` does. Build a new doc only when the overlay reference changes.

**A child keeps the value it was given at mount.** Cause: `Embed.Comp(() => new T { Field = value })` runs once; a
keyed child's constructor argument is frozen for its lifetime. Rule: pass a session object and read signals inside the
child's `Render`, or remount through `Key`. Do not capture a plain value for something that changes (see the engine's
`docs/design/subsystems/component-props-contract.md`).

**Zero-alloc is a row rule too.** Cause: a LINQ query or a lambda that captures state inside a row's `Render`, which runs
for every visible row on every frame. Rule: no LINQ and no capturing closures in row `Render`. Use a cached delegate
(the list's edge adapter is one such delegate) and a `static` lambda where nothing is captured.

**A header is clickable in the Outline.** Cause: the Outline reuses the row's click path for a section header. Rule: in
the Outline a header only takes focus. Toggling shown is the Enter key on the section wrapper, and nothing else.

## Layout, modes and bands

**`userCollapsed` flips when the window narrows.** Cause: the band forces a rail in Narrow and Tiny, and the forced
state was written back as the user's choice. Rule: write `UserCollapsed` only in the Wide band and only when not
editing. A forced collapse never reaches the saved setting.

**A collapse toggle appears in Undo.** Cause: the collapse toggle went through `SidebarUndoRing`. Rule: the ring holds
layout and pin edits. Collapse and expand are view state and are never recorded (`SidebarUndoRing`'s own comment says
so).

**Undo reverts two things, or two toasts appear for one action.** Cause: one user action pushed several ring entries.
Rule: one action pushes one entry; a multi-op action is one `Batch` entry and one toast.

**Pinned rows cannot be reordered while a pinned folder is expanded.** Cause: the expanded pinned folder emits its
visible subtree, so top-level pins are no longer contiguous. Rule: pane reorder is deliberately off for that section;
collapsing restores it. Do not "fix" it by reordering the planner output.

**An over-cap edit silently loses items.** Cause: a write that exceeds `MaxSections` (16), `MaxHiddenItems` (64) or
`MaxOverlayBytes` (8 KiB) was truncated. Rule: `SidebarLayoutRules.Apply` refuses an over-cap op and changes nothing.
A refused op pushes no undo and writes no file. Callers must check `Changed`.

## Data and feeds

**Recent or New releases fetch while hidden.** Cause: the planner asked for a feed the layout does not show. Rule: a
feed is demanded only while its section is shown (`SidebarFeedDemands`). Adding a feed means adding its demand rule
and a test of that rule, not a fetch in the section.

**The pane does not re-plan when a new input changes.** Cause: the binder's revision did not fold the new input. Rule: a
new planner input is folded into the binder's revision (`SidebarProjectionBinder`). `SidebarReplanCensus` names which
input moved on each re-plan; use its line before guessing.

**A pin write lands on the wrong account.** Cause: the pin store writes while the loaded account is not the live one
(the swap is in flight). Rule: `LibraryPinSync` applies and writes only when the loaded key equals the live key. Never
bypass that check, and never write from a stale closure that captured an older key.

**Liked or Home shows up in the pin list.** Cause: a Liked URI spelling that did not fold to `liked`. Rule: every Liked
spelling is folded by `SidebarPinId.FromUri` to `liked`, which the fixed-route guard refuses. Liked and Home are fixed
routes, never pins, and never written to the server pin set.

## Persistence

**A write over the live file leaves a half-written `sidebar.json`.** Cause: a direct write that bypasses the store. Rule: `SidebarFileStore` writes a
temp file, rotates the previous file to `.bak`, and sets an unreadable file aside as `.corrupt`. Route every write
through the store. A document over `MaxDocumentBytes` (256 KiB) is dropped whole and reported as
`SidebarSaveFault.DocumentTooLarge`; the file is not touched.

**A downgrade loses the user's layout.** Cause: the migration deleted the old settings values. Rule: keep the old values
in `settings.json`. The `sidebar.bootstrap.version = 2` latch prevents a second read.

**A new persisted field is silently dropped.** Cause: the DTO is missing from `SidebarStoreJsonCtx`, or the mapping
skips it. Rule: a persisted field needs the DTO, the context registration and a round-trip test in
`SidebarStoreV3Tests`. Unknown section ids are dropped on read and returned for logging. Do not write a field the reader
will not understand.

## Drag and drop

**A drag under a non-Custom sort reorders nothing.** Cause: a positional insert is refused under any sort but Custom
(`drag.clearSortingToReorder`). Rule: this is by design. A deposit into a folder or playlist still works under any sort.

**The dropped row snaps back before the write lands.** Cause: the plan rebuilt from the pre-drop projection during the
settle window. Rule: the rootlist freeze holds the published stage. Two publishes go through: a disclosure in flight
(spring-loading a folder) and the pane's own reorder commit; the latch ends with the session.

**A drop cue shows a line and a plate at once.** Cause: two cue paths drawing at the same time. Rule: a row draws a line
(Before, After, EndOfList) or a plate (Inside), never both.

**A second placement computation appears.** Cause: a new drop path recomputes the slot. Rule: `RootlistSlotResolver`
decides the slot once per hover; the drop consumes the published slot; `RootlistSlotMapper.TryMap` turns it into a
move. A new verb calls `PaneView.MoveRootlist`, never the library directly.

## Disclosures and motion

**A section or folder disclosure snaps, or a second one does not queue behind the first.** Cause: the disclosure path
bypasses `PaneView.StartDisclosure`, or a
second bookkeeping store is added. Rule: all section and folder disclosures go through `StartDisclosure` and the
`SidebarDisclosures` bookkeeping (`Shell/Sidebar.Disclosures.cs`, in `feat/smooth-reveal`). Do not add a WinUI-style
tween for the sidebar, and never a `MotionTok.Disclosure*` token (they are deleted).

## Keys

**Ctrl+Z or Ctrl+Y acts outside the sidebar.** Cause: the undo chord is handled above the sidebar, at frame level. Rule:
Ctrl+Z and Ctrl+Y are taken by the sidebar's own key handlers (`Sidebar.UI.Edit.cs`), and only with focus in the pane,
the head or the edit bar. They are never a frame chord.
