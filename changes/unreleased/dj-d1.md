---
kind: fixed
issues: []
contract: additive
contract-note: "PlayerBarView, StageTransportFacts and PanelTransportFacts gain optional ShuffleLock and RepeatLock (string?): shuffle / repeat forbidden by the playing context, with the tooltip saying why"
---
**Starting DJ on Wavee from another device now plays it.** Choosing Wavee as the speaker for Spotify's DJ on your phone or
desktop was accepted and then ignored, so Wavee kept playing what it played before. Wavee now asks Spotify for the DJ's
session and plays its songs in the DJ's order, fetching more as it goes, and the same happens when you start the DJ list
on Wavee itself. While DJ plays, shuffle and repeat are off and their buttons say "Not available with DJ"; your own
shuffle and repeat come back when you play something else. (All three themes.)
