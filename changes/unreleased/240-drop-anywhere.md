---
kind: added
issues: []
contract: additive
contract-note: "FileDropProps gains optional Zones, Hot, CentreOffset, PanelTarget and ZoneTarget, and the new DropZoneLook record: the drop panel's zones and targets"
---
**Drop anything onto Wavee and choose what happens.** Dragging songs, a folder, a playlist file (.m3u, .m3u8, .pls) or
Spotify links over the window opens a drop panel with the actions that fit: Play now, Play next or Add to queue for
songs; Play, Add as a music folder or Add to queue for a folder; Play as a queue for a playlist file; Open or Play for a
link. Every song in a drop now counts, not only the first, and big folders are added in the background with a progress
toast and Undo. Dropping on the queue inserts at the line, the player bar plays now, a track link dropped on one of your
playlists is added to it, and a file Wavee can't play says why ("Wavee can't play .wma files yet"). WinUI and Classic show
the panel as cards; Zune as a band of large text verbs. (#240, drop anywhere.)
