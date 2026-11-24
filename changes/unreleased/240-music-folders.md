---
kind: added
issues: []
contract: additive
contract-note: "INavRowHost.InDrawer; view keys LocalFiles.Empty, LocalFiles.Scan, LocalFiles.Pill, LocalFiles.Review with their props (LocalFilesProps.cs); optional TrackUnavailableBarArgs.SecondaryLabel/Secondary and TrackChromeTemplates.UnavailableBarExtentWithSecondary"
---
**Music folders: Local files plays the music on this PC.** Add folders in Settings › Storage › Music on this PC (or
from the empty Local files page) and every song in them appears in Local files with its title, artist, album and
length, offline too. Wavee keeps the list current on its own: new, retagged, moved and renamed files show up within a
second while it is open, a moved file keeps its place in the list and the queue, and a file that disappeared stays
listed as "Missing" until it comes back or 60 days pass (30, 60 or 90 days, your choice). The first scan reads the
music folder at thousands of songs a second and shows its progress on the page; later scans skip every folder that
did not change. A small status under Local files in the sidebar says when Wavee is scanning, what changed and what
needs attention, and opens a panel with the details. Songs added while the app is open wear "New" for the session, and
a change that lands while you are scrolled down shows a pill instead of moving the list. OneDrive files that are not
on this PC are listed without downloading them. In Classic, WinUI and Zune. (#240)
