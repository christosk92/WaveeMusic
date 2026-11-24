---
kind: changed
issues: [240]
contract: additive
contract-note: "TrackDrawerEntry gains an optional Label; the drawer's entry kind 4 (DrawerRules.FileKind) is a linked file on its Spotify song; Track.HasLinkedFile; LinkEvidence.LengthsDiffer"
---
**A linked song shows on both sides, and a length that is off is never matched for you.** Once your file is linked to a
Spotify song, that song's own Versions and formats (on its album, in a playlist) lists your file, "Your file · MP3 320"
with its length: play it, queue it, show it in its folder or remove the link from its menu. Spotify keeps playing the
Spotify version unless you pick your file. The Spotify song's row carries a small file mark. A candidate whose length
differs from your file by more than a few seconds is now always "to check", with the reason in plain words ("Lengths
differ: 4:12 vs 3:39"), even when other details agree; it is never accepted automatically, also with "Accept exact
matches automatically" on. In Classic, WinUI and Zune. (#240)
