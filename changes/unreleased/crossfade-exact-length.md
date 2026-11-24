---
kind: fixed
issues: []               # found by the #240 audio verification; no issue of its own
contract: none
contract-note: ""
---
**A crossfade lasts exactly as long as you set it.** The fade now starts at a precise moment and ends on the outgoing
song's last sample, so a 5 s crossfade overlaps the two songs by 5 s. Before, it started up to a fifth of a second late:
the songs overlapped by 4.7 to 5 s and the outgoing song stopped just before its fade-out finished.
