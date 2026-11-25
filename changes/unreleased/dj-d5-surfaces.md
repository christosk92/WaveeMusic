---
kind: added
issues: []
contract: additive
contract-note: "DjChrome (new record); PlayerBarView.Dj and StageTransportFacts.Dj (DjChrome?); QueueProps.ShuffleLock, RepeatLock, Segments and SegmentOf, QueueSegment (new); QueueSlot.IsSegmentHeader (a NextUp header slot whose Pos is a segment index). D5b: MiniPlayerProps.SpokenText (Func<string>, defaults to \"\"): the DJ's words for the mini player's caption line; a narration row's QueueRowText names no uri; the stage caption, the lyrics peek and the mini lyrics read narration documents through their existing props"
---
**DJ shows on Home like any other list, and "Let DJ pick" is here.** The DJ card leads its row in the DJ's own colours with what the DJ
says on it, and Play on it starts the DJ. The DJ's own page shows what the DJ will play next, segment by segment. While DJ plays, the
player bar, the full-screen player and the mini player show which segment is on ("DJ · Vibes") and a "Let DJ pick" button that skips to
the DJ's next segment, keeping the songs you queued; the queue lists the upcoming songs under each segment's name and marks where "Let DJ
pick" lands. The queue's own shuffle and repeat switches are greyed while DJ plays, as the bar's are. After "Let DJ pick" the DJ says a word about
the change, then the new segment starts. When DJ plays on another of your devices, "Let DJ pick" on Wavee asks that device to pick, and
"Let DJ pick" on your phone works on Wavee. (All three themes.)

**You can read what the DJ says.** While the DJ talks, its words show where the lyrics would: on the full-screen player's lyrics and
over its visualizer, in the side panel's lyrics and the Now playing card, and in the mini player, whose second line becomes the DJ's
words; "No lyrics available" no longer shows during a DJ line. This works when the DJ plays on another of your devices too: Wavee then
shows "Up next" from the DJ instead of "Loading…", the segment the DJ is on ("DJ · Throwbacks"), and "Let DJ pick" only when that
device can take it (its tooltip says when the DJ is not ready yet); the times and the heart stay hidden while the DJ talks, and moving
playback (to this PC or another speaker) waits until the DJ is done, saying why. In the queue, the song the DJ is leading into shows
under its segment as the DJ's next song, not as one you queued, and the Classic queue no longer offers to like a DJ line. Spotify now
hears about DJ plays as its own app reports them: each DJ line, and the DJ's picks with the DJ's recommendation ids. (All three themes.)
