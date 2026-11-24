# Wavee Mix: UX direction, revision 2 (owner request 2026-11-23)

**Revision 2 (2026-11-23, later the same day).** The owner: revision 1 was "too barebones, it's missing a lot" next
to the Spotify app. Wavee now offers **everything Spotify's Mix offers**, and keeps revision 1's quiet layout as the
*default*: depth is one click away, never on every row. Revision 1's principles stay, except principle 4, which
changes from "a few styles instead of controls" to "styles first, controls on demand". This file is the UX source of
truth for #48; it overrides the UI parts of `48-mix-implementation.md` where they differ (the plan's revision 5 note
lists what changed).

## What Spotify's Mix has (research, 2026-11-23)

Sources: Spotify's support page "Mixed playlists", Spotify's newsroom post of 2025-08-19 (and its 2026-05-19 desktop
update), Engadget's how-to (2026-07), Make Tech Easier (2025-09), and our own capture (`mix-capture-report.md`).

| # | Spotify feature | Notes |
|---|---|---|
| S1 | **Mix** button on the playlist; turn Mix off again (settings kept; off by the owner = off for every listener) | Premium; playlists you made (and collaborative ones). Not offline-editable. |
| S2 | **Auto** transitions on every pair by default | Computed by the client. |
| S3 | A **transition chip between every pair** in the list ("Auto" or the edited preset) | Revision 1 dropped this; revision 2 offers it as a view option. |
| S4 | **BPM and key per track** while Mix is on (Camelot-style keys such as 6B) | XM 222, which Wavee already decodes. |
| S5 | **Smart Reorder**: reorder the playlist by tempo and key, then tweak by hand | Engadget only; storage is a normal playlist move. |
| S6 | **Presets**: Fade, Rise, Blend, Wave, Melt, Slam (+ Auto) | Ids known: Fade = 1, Rise = 3. Blend, Wave, Melt, Slam: capture round 2. |
| S7 | **Volume** curve choice (7): Smooth crossfade, Overlap, Fade in fade out, Cut in fade out, Fade in cut out, Center cut, Crossfade | Storage unknown (O3). |
| S8 | **EQ** choice (4): Center bass, End bass swap, 3-band fade, None | Storage unknown (O3). |
| S9 | **Effect / Filter** (8, default None): low-pass and high-pass sweeps; "New effects" opt-in (looping, jog wheel) | Names beyond low/high-pass not public; the capture shows the opt-in flag `mixing-fx-looping-jogwheel`. |
| S10 | **Customize** a preset's curves (mobile) | Curves are the `audio.fade_*` lists. |
| S11 | **Two waveforms** zoomed on the join, **beat markers**, drag either song to move the transition point, an **overview bar** to scrub | Moving the in-point writes recipe 1.4 (O5). |
| S12 | **Beatmatching** switch | Recipe 1.6-1.8 (O4). |
| S13 | **Preview**: the clip loops until Stop; Save / Discard | Spotify's editor plays its own transition on open. |
| S14 | Tempo/key **warning** icons on clashing pairs | Revision 1 turned them into advice; kept. |
| S15 | **Collaborators** can mix (Premium) | Same writes. |
| S16 | **Mixed cover stickers** (mobile) | Wavee: a "Mixed" badge on the playlist art. |
| S17 | BPM / key in the **player bar** while mixing | Revision 1 left it out; revision 2 shows it while a mixed playlist plays. |
| S18 | **Shuffle**: Spotify says turn shuffle off to hear transitions | Wavee does better: Auto on shuffled pairs. |
| S19 | **Connect**: mixed playlists play as regular playlists on Connect devices | Wavee publishes the fades it plays. |

## Principles (revision 2)

1. **Mix is a way of listening first.** One Mix control on the playlist; turning it on changes how it sounds, not how
   the page is laid out.
2. **Quiet by default, everything on demand.** By default no chip between rows; one setting ("Show transitions:
   on hover · always") turns Spotify's chips on. The BPM · Key column appears with Mix and can be hidden.
3. **Edit in place, one pair at a time.** The seam card opens inline between its two rows, never a side panel.
4. **Styles first, controls on demand.** The card leads with the seven presets and one sentence per preset; Volume,
   EQ, Effect and Beatmatch sit under **Customize**, a disclosure inside the card. Touching any of them makes the
   preset read "Custom".
5. **Recommend, don't warn.** Tempo/key distance is advice, never an exclamation icon.
6. **Every pair can opt out.** "No mix" is a first-class choice (Spotify has none).
7. **Show mixing while it happens, lightly.** "Mixing into …", the overlap on the seek bar, and tempo · key in the
   player bar while a mixed playlist plays.
8. **Same words as Spotify.** Preset and control names are Spotify's, so a transition reads the same on every device.

## The design

### Playlist header
- **Mix** is a split button beside Play and Shuffle: the button turns Mix on and off; its chevron opens the **Mix menu**:
  - Turn off Mix for everyone (owner, collaborators) · Listen without Mix (this PC)
  - Smart reorder…
  - Show transitions: On hover · Always
  - Show tempo and key (the BPM · Key column)
  - Mix when shuffling (on by default)
- Meta line: "Mixed by \<owner\> · 2 of 9 transitions edited". A **"Mixed" badge** on the playlist art (and on its
  tiles in the sidebar and library).
- **Tracks / Transitions** switch in the list toolbar while Mix is on.

### Track list (Mix on, Tracks view)
- A **BPM · Key** column (e.g. "117 · 6B", the key with its Camelot colour dot), on by default with Mix.
- Seams: on hover a thin line with a "⋯ Transition" handle; an edited pair keeps a 6 px dot; "No mix" a break.
- **Show transitions: Always** puts a compact chip in each seam ("Auto", "Rise · 5 s", "No mix"), Spotify's layout.
- Without a mouse: the row menu's "Transition to next song".
- Offline: seams and chips stay; the card opens read-only ("Editing transitions needs a connection").

### Transitions view
- One card per pair: both songs with BPM · key, a compatibility hint ("tempos far apart"), a tiny overlap picture,
  the preset and length, and its state (Auto · Edited · Custom · No mix · On this PC).
- ↑/↓ moves, Enter opens, Space previews.

### The seam card (opened inline)
1. Header: the two songs with cover, BPM · key each, and the compatibility line in plain words.
2. **Preset row**: Auto · Fade · Rise · Blend · Wave · Melt · Slam · No mix (Auto shows "Auto chose Fade"). A
   "Custom" pill appears when Customize changed something. One sentence describes the selected preset.
3. **Waveform editor**: an overview bar of both songs on top (drag to scrub); below it two lanes, outgoing above and
   incoming below, zoomed on the join, with beat markers (downbeats taller). Drag the outgoing lane to move the
   mix-out point, the incoming lane to move the in-point; the overlap region snaps to beats. Read-outs: overlap in
   seconds and beats, mix-out time, in-point time. The volume curves (and the filter when one is set) are drawn over
   the lanes, light.
4. **Customize** (a disclosure, closed by default): Volume ▾ (7), EQ ▾ (4), Effect ▾ (8: None, Low-pass in,
   High-pass out, Low-pass sweep, High-pass sweep, Echo out, Loop out, Jog wheel; the last two are Spotify's "New
   effects"; exact names to confirm in capture round 2), and **Beatmatch** (switch; when on, the incoming song's
   tempo glides to meet the outgoing one).
5. Actions: **Preview** (loops 4 s either side of the join until Stop), **Reset to auto**, **Discard**, **Save**.
   Under the actions, where it applies: "Saved on this PC only" (No mix; playlists you can't edit).

### Smart reorder
- A dialog from the Mix menu: "Reorder by tempo and key". Options: Keep the first song · Smooth tempo · Match keys.
  A before/after list with each pair's compatibility; **Apply** moves the songs (one playlist edit, undoable with
  Ctrl+Z and a toast "Reordered 14 songs · Undo").

### Player bar and queue
- While a mixed playlist plays: a small **"117 BPM · 6B"** label beside the title (hideable in the Mix menu), the
  overlap as the seek bar's last brighter stretch, and "Mixing into \<next\>" during the overlap.
- Queue: "Mixing · Rise · 5.1 s" between now and next; the Crossfade row says "Mix is on for this playlist".

### Shuffle
- Auto mixes shuffled pairs; a saved transition applies when its own pair comes up. "Mix when shuffling" in the Mix
  menu turns this off (Spotify's behaviour).

### Themes
- WinUI: the card is an inline expander card; presets a segmented row that scrolls at narrow widths; Customize an
  expander. Zune: a full-width band, presets as large lowercase text tabs, Customize as a second band. Classic uses the
  WinUI layout (the owner is converging the two; no Classic-only design).

## What needs capture round 2 (blocks shipping the control, not designing it)

Blend, Wave, Melt, Slam ids (O2) · Volume / EQ / Effect storage (O3) · Beatmatching fields (O4) · in-point
1.4 (O5) · Mix off (O1) · reset (O6) · the "New effects" names. Smart reorder, the BPM · Key column, chips, the
badge, the player-bar label, offline and collaborators need no capture.
