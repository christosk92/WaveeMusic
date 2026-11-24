---
kind: fixed
issues: []               # found by the #240 audio verification; no issue of its own
contract: none
contract-note: ""
---
**AAC songs (.m4a) start and seek exactly.** The silent encoder delay at the start of a local M4A file is no longer
played (about 23 ms before the music), so gapless playback into an M4A joins without a gap, and a seek lands on the
moment you picked instead of up to 70 ms earlier.
