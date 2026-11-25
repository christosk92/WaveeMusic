---
kind: added
issues: [48]
contract: none
contract-note: ""
---
**Spotify's Mix plays in Wavee.** A playlist Spotify has mixed now plays with its transitions: a pair edited in Spotify
plays at its saved timing with its own preset (Fade, Blend, Rise, Melt, Slam, Wave, Dissolve, Surge, Afterglow and the
ones Spotify keeps for itself) and the volume, bass and filter curves Spotify draws for it, at the transition's length
in bars and with the styles you picked in Spotify. Every other pair gets Spotify's own Auto transition, placed where
Spotify places it for pairs it does not beatmatch: the same start and length to the millisecond. The bass hands over
and the filter sweeps as the preset describes, and the song on screen changes when the outgoing one is over. Shuffled
pairs get Auto too. A local file, a song you queued, an episode or a video still gets your normal Crossfade or gapless
join. Not yet: the echo, reverb and noise effects, the mid and treble EQ moves, loops, and tempo matching (a
beatmatched pair plays at its saved timing without changing B's speed; Auto treats every pair as unmatched). The curves
are Spotify's, the sound is Wavee's own mixer, so a transition sounds close to, not identical with, the official app.
