---
kind: added
issues: [240]
contract: additive
contract-note: "TrackDrawerBodyArgs gains VersionCard (TrackDrawerCardWiring: the card's context handler, keys and title); TrackDrawerEntry gains Picture and Wide; MenuOptions gains VersionCard"
---
**Versions in Track details can be acted on and renamed in place.** Right-click a version card under "Versions and
formats", press Shift+F10 or the menu key on it, or use its "⋯" on hover, to open the version's own menu:
- Play, Play next, Play after.
- "Play by default", checked on the version the song plays.
- For a file of yours: Rename…, Show in folder, Find on Spotify and Remove link.
- For Spotify's version: Go to album, Share and Save.
- For a video of yours: Watch and Play as audio.

The title of a file of yours is editable where it is. A pencil shows on hover, and a click or F2 turns it into a field in
the same place. Enter or a click elsewhere renames it, Esc cancels, and the toast offers Undo. Spotify's versions keep
their titles. On a narrow window the menu opens as the bottom sheet, as a row's does. A video of yours shows a frame from the video on its card, 16:9 like
a music video, instead of the album cover. "Use as thumbnail" in its menu switches between the album cover and the
frame. "Choose frame…" lets you scrub the video and pick the frame. That frame is also what the album page's video
card, the queue and the player show for the video. Song rows keep the album cover. (#240)
