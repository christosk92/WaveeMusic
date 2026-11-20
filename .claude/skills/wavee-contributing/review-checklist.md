# What reviews keep finding

Each item came out of a runtime review of a real PR (the number says which). Check your change against all of them
before you open a PR; the inspector commands are in `docs/guide/agent-inspect.md`.

## Motion and layout

1. **Things that move together animate together.** When one part eases to a new place, its neighbour must not jump
   there on the first frame. (#233: the title column eased over about 12 frames while the heart beside it jumped from
   x=162 to x=267 in one frame on a track change.)
   Check: trace the acting verb and look for parts that move at once:
   `play spotify:track:tr7 next=3`, then `transport do=next trace=60 prefix=like`.
2. **Check narrow widths.** Render at 800 wide and at the Compact tier, in every theme you touched: names ellipsize,
   never cut mid-word, and a new mark never squeezes the text it sits beside. (#234: at 800 wide a new mark took 51 DIP
   and the names were cut with no ellipsis.)
   Check: `mount w=800 h=900 theme=classic route=home`, then `render path=...`, and the same at `w=400`.
3. **Fix the shared control, not each caller.** When a control misbehaves (a trim test, a tooltip, a hit area), fix it
   where it is defined so every caller gets the fix. (#233: padding one title by 4 DIP to dodge `Controls.TrimTip`'s
   4-DIP measuring grid, instead of teaching `TrimTip` that "within one grid step" fits.)
4. **Finish the issue, or say which part the PR does.** (#235 fixed the focus ring that a click on the bar drew, which
   is half of #232; Space still did nothing after the click.)

## Render cost

5. **No signal reads in `Render` on a drag, scroll or pointer path.** Reading `.Value` in `Render` re-renders that
   component on every change; bind the value instead (`Width = Prop.Of(() => ...)`, a bound `Transform`). (#236: a
   splitter drag re-rendered 12 components and measured about 1,050 nodes per frame, 11 to 27 ms each against an
   8.3 ms budget at 120 Hz, with a GC in the middle.)
   Check: `hover x=<x> y=<y> trace=36 drag=-6 dragFrames=20 dragStart=4 dragKey=<grip key> census=true`; the census
   names the components each frame re-rendered.
6. **Keep reads inside the smallest slot.** A signal read in a frame template re-renders the whole frame. (#236:
   pressing and releasing the grip re-rendered 17 components of the detail frame.)
7. **Nothing allocates per row or per frame.** `EntityId.Text` builds a new string for a real (gid) id; the duration
   lane and the artist runs ask on every row, so test cheap facts first (`Uri.Id.Form == EntityForm.Text`). (#238.)
   The fake catalogue's ids are text-form, so the inspector hides this: reason about real ids.
8. **Always-mounted chrome must be cheap.** A wrapper on a control that is mounted on every list but rarely shown (a
   tooltip around a hidden bar's close button) can cost megabytes. (#238: one scroll frame of Liked Songs allocated
   4.6 MB with two Gen0 and one Gen1 GC, against about 160 KB before.) Mount it only while shown.
   Check: `perf on`, scroll the list (`scroll key=<list scroll key> dy=14` a few times), `perf report`: `hotAllocBytes`,
   the `alloc` rows and any `gc:[...]`.

## Data and contracts

9. **Bind to typed state, not to display text.** A mark that shows for lossless audio reads the opened stream format,
   not the text of a quality badge. (#234: the player bar read the badge string; the device list already read
   `Playback.Audio.PlayingOpened.Format`.)
10. **Each surface has its own strings.** Don't borrow another screen's `Loc` key because the words match today; they
    drift apart later. (#234 borrowed a Settings string for the player bar.)
11. **Theme contract changes are declared, never numbered.** New members of a props record are optional in a minor;
    `required` is breaking (#234 added a `required` member). Set `contract:` in the change file; never edit
    `ContractVersion` (#236 and #238 each picked a number that collided with the release's).

## The PR itself

12. **The change file, not the CHANGELOG.** (#233, #234, #236, #237 and #238 all had CHANGELOG bullets that ended up
    under an already released version after a release happened while they were open. Change files cannot strand.)
13. **The description covers the whole diff, with screenshots.** (#236 described only the scrollbar while the diff also
    added a resizable column, three remembered widths and a contract change; #238 described one fix of three.) When the
    changes are unrelated, open separate PRs.
14. **The base is `release/<next>`.** A PR into `main` waits until it is moved; nothing merges into `main` by hand.
