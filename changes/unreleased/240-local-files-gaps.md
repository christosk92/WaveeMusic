---
kind: added
issues: [240]
contract: additive
contract-note: "LocalShelf and LocalShelfSlot; LocalCollectionPageProps gains Shelves, ShowShelf, MoveShelf and Drop; LocalCollectionView gains HiResSongs and LastScanTicks; LocalFilesReviewRow gains RemoveLabel and Remove; INavRowHost gains RouteDropSpec; LocalTagSet gains SampleRate, BitsPerSample and Channels, LocalEntry gains FirstSeenTicks (app types, not theme contract)"
---
**On this PC fills in the rest: Hi-Res labels, Edit home, Review that can remove and re-pair, Pause, and drops that land
where you aim.** Albums and songs now say how good they are: "Hi-Res 24/96" for lossless audio above CD quality, "FLAC",
"MP3 320". Wavee reads each file's sample rate, bit depth and channels (checked against ffprobe on real files), and
the Lossless shelf counts the hi-res songs. "Recently added" now means when Wavee first found a file, not the file's own
date, so music restored from a backup shows up as new. The collection's numbers are bold and the hero says when the
last scan ran ("Up to date · scanned 2 min ago"). "Edit home" shows, hides and reorders the Home shelves and remembers
it. The missing songs' Review dialog gains Remove, and Locate now pairs the one file you pick with the song, so it keeps
its place in your playlists and queue. The library activity flyout has Pause and Resume, and scanning pauses on battery
saver unless you turn that off; a folder Wavee cannot read says "Can't read this folder" with Fix permissions (and a
subfolder can be left out); Settings gains "Pause scanning on battery saver" and "Read tags of cloud-only files". Drop
songs on the Local Files row or the On this PC page to play them and keep them in Local Files, or drop a folder there to
add it as a music folder; dragging local files over a Spotify playlist now shows the whole reason they can't go there
yet. The library's index is rebuilt once on the first start after the update. (#240.)
