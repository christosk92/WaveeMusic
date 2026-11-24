---
kind: fixed
issues: [240]
contract: none
contract-note: ""
---
**Local files with no tags no longer look broken.** A song with no artist tag now says "Unknown artist" everywhere an
artist line shows: the player bar, Now playing, the queue, the mini player, track lists in every theme, the Windows media
controls and the tray. A song with no cover shows Wavee's music-note placeholder instead of a blank square (the player bar
showed no thumbnail at all), and a local file's own cover (its embedded picture, or a cover image in its folder) now shows
in the lists, the player bar, Now playing and the media controls as soon as Wavee has read it, fading in over the
placeholder without moving anything. (#240)
