# API Waste Regression Checklist

Baseline: `wasteful.saz` captured startup, browse, and two-track playback over about 90 seconds.

Manual gate after API-waste changes:

1. Start with a clean app session and Fiddler capture.
2. Sign in or silent-resume to the live backend.
3. Let startup library/rootlist hydration settle.
4. Open Home, then open one album from Home.
5. Play three tracks, then skip back once.
6. Revisit Home within 60 seconds.
7. Export the capture and compare against the baseline inventory.

Expected checks:

- Current user `user-profile-view` appears at most once during bootstrap/rootlist.
- `profile/Spotify` and `profile/spotify` do not produce separate REST profile calls.
- Repeated AMLL 404s for the same track id do not recur in the same session.
- Home and recents Pathfinder POSTs are not duplicated on quick revisit.
- Album enrichment Pathfinder calls are cache hits on re-navigation.
- Extended-metadata feature reads carry entity etags after the first response and accept 304s without payload parsing.
- Playlist/catalog hydration uses fewer full extended-metadata payloads after cache warm-up.
- Connect `PUT /connect-state/v1/devices/...` bodies remain capped when the queue has many prev/next tracks.
