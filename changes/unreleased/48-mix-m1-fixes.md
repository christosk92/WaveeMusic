---
kind: fixed
issues: [48]
contract: none
contract-note: ""
---
**A transition edited in Spotify keeps all its settings.** Wavee reads Spotify's saved transitions field by field and writes
them back the same way, so a pair saved as beatmatched over 2 bars, with an EQ choice or a drawn curve, keeps every setting
when it is edited in Wavee. A pair saved in Spotify as "No transition" now shows "No mix" and plays as a plain track change;
Wavee used to read it as a 1 ms fade at the start of the song.
