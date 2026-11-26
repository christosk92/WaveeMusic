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
file, a song you queued, an episode or a video still gets your normal Crossfade or gapless join. Auto now beatmatches
exactly the pairs Spotify beatmatches: a 2 to 16 bar overlap on the downbeats, the incoming song at the speed that lines
its beats up with the outgoing one, and the preset Spotify would pick. It also picks the same window Spotify picks, to the
millisecond: Wavee now reads where both songs have vocals, all of Spotify's cue points and the songs' genres, as Spotify
does. The curves are Spotify's, the sound is Wavee's own mixer, so a transition sounds close to, not
identical with, the official app.


**Edit a transition the way Spotify does.** Open a pair from its seam, the Transitions list or the Mix menu and the
transition editor opens under the list, in each theme's own look: every preset by Spotify's name (Auto, Fade, Rise,
Blend, Wave, Melt, Slam, Dissolve, Surge, Afterglow, No transition, and under More the thirteen Spotify uses for Auto),
both songs' waveforms around the join with their beats and the volume curves, where you drag the outgoing song to move
the mix-out point, the incoming song to move where it comes in, the end of the overlap to lengthen it, and a curve's
handle to bend it, every point snapping to the beat. The length steps by beats, or by bars when the pair is
beatmatched. Customize picks the volume, EQ, filter, effects and looping style, and turns Beatmatching on or off.
Hear it plays the transition as you have it, unsaved edits included, round and round until you stop it, then puts back
what was playing. Undo, Redo, Cancel and Reset to Auto are there; Save writes the transition to Spotify on a playlist you
can edit. On a mixed playlist you can't edit, Save asks first, as Spotify does: Duplicate Mix makes your own copy with
this transition in it and opens it, or Save on this PC keeps it here only. A curve you drew in Wavee or in Spotify now
plays as drawn. A pair set to No transition, in Spotify or with Wavee's "Don't mix into the next song", goes straight into
the next song without your Crossfade. The editor scrolls with the page under the list's header, so all of it is in reach
on a small window, and opening it from a song further down brings it into view.

**Mix previews and Mix over Connect.** When Wavee is the device playing and you edit a transition in Spotify's app on
another device, each preview now plays on Wavee (the end of the outgoing song, the transition, then the incoming song),
and closing the editor puts back what was playing, where it was. Wavee offers these previews to Spotify again. Hear it in
Wavee's own editor puts the song, its position and its play or pause state back afterwards too, and while another device
plays, the preview is sent to that device instead. A phone or computer
controlling Wavee now sees every upcoming transition of a mixed playlist, sees the incoming song as playing from the moment
it starts (with its speed when beatmatched), and sees the song's end at the transition. A mixed playlist started from
another device starts on the song you picked and plays mixed. Controlling another device that plays a mixed playlist, the
queue and the player bar show its next transition.

**Smart reorder, Duplicate Mix and Premium.** On a mixed playlist you own or collaborate on, the Mix menu's Smart reorder
asks Spotify for its order for smooth transitions and shows it in place under the list bar: every song with its tempo and
key and the place it comes from. Reorder moves the songs in one edit, so every device sees the new order, and Undo puts
the old order back. On a mixed playlist you can't edit, the Mix menu says so and offers Duplicate Mix: Spotify copies the
playlist with its transitions, the copy lands at the top of your library with Mix on, and it opens. Mixed playlists say
"Mixed" in your library and in the sidebar's two-line rows. An account without Premium sees what Mix needs instead of a
Mix action it can't use.
