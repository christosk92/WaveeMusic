---
kind: added
issues: []
contract: none
contract-note: ""
---
**WAV and AIFF files play.** Local files in WAV (including RF64 and BW64 for files over 4 GB) and AIFF/AIFC now play, from
8-bit to 32-bit and 32/64-bit float, with an exact length, sample-exact seeking and gapless joins and crossfades like FLAC.
Surround files (5.1, 7.1) are mixed down to stereo. Their quality reads like "WAV 24/96" and they count as lossless. Files
Wavee had already listed as "Can't play: a WAV file" are read again once, so they turn playable without a rescan. WAV files
that hold ADPCM, µ-law, A-law or MP3 audio still say why they can't play. (#240)
