---
kind: added
issues: []
contract: additive
contract-note: "PlayerBarView gains ShuffleLock and RepeatLock (string?, the tooltip saying why), ProgressSuppressed (bool: an AI DJ narration's progress is not shown) and Dj (DjChrome?); StageTransportFacts and PanelTransportFacts gain ShuffleLock and RepeatLock, StageTransportFacts.Dj; DjChrome and QueueSegment (new); QueueProps.ShuffleLock, RepeatLock, Segments and SegmentOf; QueueSlot.IsSegmentHeader (a NextUp header slot whose Pos is a segment index); MiniPlayerProps.SpokenText (Func<string>, defaults to \"\"); a narration row's QueueRowText names no uri"
---
**Spotify's DJ plays on Wavee.** Start DJ from its card on Home, its page or the sidebar, or pick Wavee as the speaker for DJ on
your phone or desktop (that used to be accepted and then ignored): Wavee asks Spotify for the DJ's session and plays its songs in
the DJ's order, fetching more as it goes. While DJ plays, shuffle and repeat are off and their buttons say "Not available with DJ";
your own shuffle and repeat come back when you play something else. Moving DJ from your phone to Wavee, or restarting Wavee while DJ
was playing, keeps the song you were on and carries on with the DJ's next songs; the DJ does not welcome you a second time. (All
three themes.)

**The DJ talks, over the music.** Its voice comes as in Spotify's own app: a welcome when it starts, an intro before each run of
songs, a short sign-off after it, and a word after "Let DJ pick". An intro talks over the start of the song it introduces, which
comes in quietly under the voice and swells up as it finishes (timed to the song's own intro point when Spotify marks one); a
sign-off talks over the last seconds of a run with the song turned down beneath it. Between the DJ's own songs, each fades out on
its end point while the next fades in on its start point, instead of stopping and starting. While the DJ's voice is playing the
player shows "Welcome" or "Up next" from "DJ X" with its picture, no times and no heart, and seeking waits until the voice is
done; repeat-one and moving playback to another device wait only until the song has started under the voice. Next goes straight
to the music (once the song is playing under the voice, past it too), and Previous plays the song the voice belongs to from the
start. The voice never shows in the queue or in what you played, and a line that cannot be fetched is skipped.

**You can read what the DJ says.** Its words show where the lyrics would, until the voice ends: on the full-screen player's lyrics
and over its visualizer, in the side panel's lyrics and the Now playing card, and as the mini player's second line; "No lyrics
available" no longer shows during a DJ line.

**"Let DJ pick", and the queue by segment.** The player bar, the full-screen player and the mini player show which segment is on
("DJ · Vibes") and a "Let DJ pick" button that skips to the DJ's next segment, keeping the songs you queued. The queue lists the
upcoming songs under each segment's name and marks where "Let DJ pick" lands; the song the DJ is leading into shows under its
segment as the DJ's next song, not as one you queued, also while the DJ is still talking over it. The queue's shuffle and repeat
switches are greyed while DJ plays, and the Classic queue no longer offers to like a DJ line.

**DJ across your devices.** While Wavee plays DJ, your other devices see what the official desktop app shows them: that DJ is
playing, the segment, shuffle and repeat off, the DJ talking, and the same fades. Starting DJ in Wavee while another device plays
starts DJ there. When DJ plays on another device, Wavee shows "Up next" from the DJ instead of "Loading…", the DJ's words, the
segment ("DJ · Throwbacks"), and "Let DJ pick" only when that device can take it (its tooltip says when the DJ is not ready yet);
the times and the heart stay hidden while it talks, and moving playback waits until the DJ is done, saying why. "Let DJ pick" on
your phone works on Wavee.

Spotify hears about DJ plays as its own app reports them: each DJ line, the DJ's picks with its recommendation ids, and how long the
voice and the music, or two DJ songs, played together.
