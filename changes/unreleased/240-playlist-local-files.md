---
kind: added
issues: [240]
contract: none
---
**A playlist's local files play in Wavee.** A playlist made in the Spotify app can list songs from your computer; Wavee
showed them greyed. Now each one is looked up in your music folders (by its title, artist and length, as the Spotify
app names it) and, when the file is there, the row lights up and plays from it in place: from the playlist, in
shuffle, in the queue, offline too, and when another device asks Wavee to play that row. A row whose file is not in
your music folders stays greyed and says so ("A local file: not found in your music folders"), and the "can't be
played" bar counts only those. A row lights up or greys while the playlist is open as soon as a scan finds or loses
its file. Your other devices see the song the way the Spotify app reports one (the playlist's own name for it, its
real length), and the play is kept in your listening history. Local files saved to Liked Songs show up there too.
In Classic, WinUI and Zune. (#240)
