---
kind: fixed
issues: []               # found by the #240 audio verification; no issue of its own
contract: none
contract-note: ""
---
**MP3s no longer skip and crackle every minute or so.** A bug in the MP3 decoder dropped a moment of audio and played a
short burst of noise right after it, about every 50 seconds in a typical song. Gapless MP3 playback is now exact for
songs encoded with FFmpeg too, and seeking in an MP3 lands on the moment you picked instead of up to a twentieth of a
second off.
