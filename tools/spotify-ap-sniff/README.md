# Spotify AP traffic sniffer

Hooks the Spotify desktop client's Shannon cipher and dumps every decrypted Access-Point packet. Lets us see `hm://` Mercury URIs and dealer pushes that the official client uses — particularly useful for finding any play-history endpoint we haven't reverse-engineered yet.

The AP protocol on TCP port 4070 is encrypted with Shannon cipher post-handshake, so Wireshark alone shows ciphertext. This script intercepts INSIDE the Spotify process, after decryption.

## Why this exists

Wavee's gabo POST gets 200 OK but Recently Played stays empty. Hypothesis: Spotify desktop sends play-history via a Mercury push to a `hm://` URI we don't know about (gabo would be redundant analytics in that case). This script tells us yes/no in ~5 minutes of clicking play.

## Setup

```pwsh
# 1. Install Frida tools
pip install frida-tools

# 2. Make sure Spotify desktop is installed (NOT the Microsoft Store version — that one
#    runs in an AppContainer and Frida can't attach without extra dance).
#    Get it from https://www.spotify.com/download/windows/

# 3. Run the script — it spawns Spotify with the hook attached
frida -l hook.js -f "$env:APPDATA\Spotify\Spotify.exe"

# (Or attach to an already-running Spotify by name)
frida -l hook.js -n Spotify.exe
```

## What you'll see

For every Mercury request/response the desktop makes:

```
[Mercury OUT] seq=42 method=SEND uri=hm://event-service/v1/events  (123 bytes)
[Mercury IN ] seq=42 status=200 uri=hm://event-service/v1/events
[Mercury OUT] seq=43 method=SEND uri=hm://track-playback/v1/some-new-thing  (456 bytes)
```

What to do during capture:
1. Sign in to Spotify desktop
2. Click play on any track
3. Let it play 10–30 seconds
4. Click the next track button
5. Let that play through to the end

What to look for in the output:
- Any `hm://` URI we don't already use in Wavee — especially anything mentioning `played-state`, `track-playback`, `state-machine`, `events`, `metrics`, `signal`
- Whether `hm://event-service/v1/events` actually fires (we believe it's dead but desktop might still try)
- Any push received on `hm://...` channels we don't subscribe to

## Where the hook function lives in Spotify.exe

Spotify uses libcrypto-shannon under the hood. The Shannon cipher routines have stable signatures across Spotify versions — typically inlined as `shn_decrypt` / `shn_encrypt` taking `(ctx*, buf*, len)`. The script does pattern-matching to find them; if Spotify changes their build the script may need updating. Symbol-stripped binary, so we go by signature heuristics, not names.
