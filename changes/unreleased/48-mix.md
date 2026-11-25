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
and the filter sweeps as the preset describes, and the song on screen changes when the outgoing one is over. Every
ingredient Spotify can put in a transition sounds: the bass, mid and treble EQ (a cut band is really gone), the filter
sweeps, the noise sweep, the echo, delay and reverb (which ring out after the outgoing song has faded) and the beat loops.
A beatmatched pair plays the incoming song at Spotify's saved speed, then eases it back to normal speed in small steps
after the transition, and the speed is shown to your other devices as it changes. Shuffled pairs get Auto too. A local
file, a song you queued, an episode or a video still gets your normal Crossfade or gapless join. Not yet: Spotify's own
beatmatched Auto (Auto treats every pair as unmatched). The curves are Spotify's, the sound is Wavee's own mixer, so a
transition sounds close to, not identical with, the official app.

**Mix previews and Mix over Connect.** When Wavee is the device playing and you edit a transition in Spotify's app on
another device, each preview now plays on Wavee (the end of the outgoing song, the transition, then the incoming song),
and closing the editor puts back what was playing, where it was. Wavee offers these previews to Spotify again. Wavee can
play a preview of any pair itself too, an edit not saved yet included, and puts the song, its position and its play or
pause state back afterwards; while another device plays, the preview is sent to that device instead. A phone or computer
controlling Wavee now sees every upcoming transition of a mixed playlist, sees the incoming song as playing from the moment
it starts (with its speed when beatmatched), and sees the song's end at the transition. A mixed playlist started from
another device starts on the song you picked and plays mixed. Controlling another device that plays a mixed playlist, the
queue and the player bar show its next transition.
