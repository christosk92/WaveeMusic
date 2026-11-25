---
kind: fixed
issues: [48]
contract: none
contract-note: ""
---
**A transition edited in Spotify keeps all its settings.** Wavee reads Spotify's saved transitions field by field and writes
them back the same way, so a pair saved as beatmatched over 2 bars, with an EQ choice or a drawn curve, keeps every setting
when it is edited in Wavee. A pair saved in Spotify as "No transition" now shows "No mix" and plays as a plain, gapless track
change, even with Crossfade on, as in Spotify; Wavee used to read it as a 1 ms fade at the start of the song.

**Mix's bass swap and filter sweeps sound like Spotify's.** The EQ and filter curves now use the official client's own value
map: the bass drops all the way out (−48 dB, was −26 dB) and can lift up to +6 dB, the low-pass opens from 120 Hz to 16 kHz
and the high-pass sweeps from 20 Hz to 9 kHz, and the bass swap moves in finer steps.

**A Mix transition stopped halfway no longer clicks.** A seek inside a transition, or a change to what plays next, used to
cut the incoming song off in one frame; now it fades out and the outgoing song comes back up over 30 ms.

**Wavee no longer claims transition previews it cannot play.** Over Spotify Connect, Wavee stopped saying it can play a Mix
editor's transition previews; the previews another Spotify app sent it while Wavee was playing were dropped.
