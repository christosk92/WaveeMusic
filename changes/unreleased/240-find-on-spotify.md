---
kind: changed
issues: [240]
contract: additive
contract-note: "LocalCollectionView gains LooseSongs; LinkView gains Finding; TrackDrawerBodyArgs gains optional Link (TrackDrawerLink: a file's Spotify section); LocalLinkPage.OpenOptIn takes an optional song"
---
**Every song on your PC can be found on Spotify from its own menu.** A song's menu in On this PC now always offers
**Find on Spotify…** (it was "Spotify match…", and only there once matching was switched on in Settings). With matching
off it first explains, right there, what is searched, what never leaves your PC and that your files stay untouched, with
**Turn on and find** or Cancel; then it looks that song up at once and opens its candidates, each with why it was proposed,
a search box and Not on Spotify. A song's Track details has a Spotify section: what it is linked to and why, with Remove
link, or "Not linked to Spotify" with Find on Spotify…. The offer on On this PC counts songs that have no album too
("…to 1 song", "…to 3 albums and 2 songs"), so a library of loose songs sees it. In Classic, WinUI and Zune. (#240)
