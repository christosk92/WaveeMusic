---
kind: changed
issues: []               # part of #240 (local files); this change alone does not close it
contract: none
contract-note: ""
---
**Local files crossfade and join like Spotify songs.** Between two local files, and between a local file and a Spotify
song in either direction, Wavee now uses your crossfade, or a gapless join when the crossfade is off, instead of
stopping, pausing briefly and starting the next song. A local MP3 without a length header (no Xing/LAME block) or a raw
AAC file still plays to its real end before the next song starts, so a guessed length never cuts off the end of a song.
A playing local file now also shows its length from the start.
