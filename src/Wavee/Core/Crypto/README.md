### AudioDecryptStream.cs ⚠️
**Status:** Proprietary - NOT INCLUDED

This file has been excluded from the open source repository due to licensing and intellectual property restrictions.

**Why it's excluded:**
- Contains Spotify's proprietary audio file decryption algorithm
- Implements AES-128-CTR with specific parameters and initialization vectors that are part of Spotify's DRM system
- Cannot be legally distributed without proper licensing from Spotify

**What it does** (for reference):
- Provides transparent AES-128-CTR decryption for Spotify audio streams
- Supports seeking within encrypted audio files
- Handles both encrypted and unencrypted audio files

**Note for developers:**
- The **connection protocol** (handshake, Shannon cipher, packet framing) IS fully open source and included
- The **audio decryption** is the only proprietary component
- You will need to implement AudioDecryptStream yourself or obtain proper licensing if you need audio playback functionality
- The test files (`AudioDecryptStreamTests.cs`, `AudioDecryptStreamLibrespotTests.cs`) remain in the repository for documentation and reference purposes

---

**Legal Notice:** This exclusion is to respect Spotify's intellectual property rights and digital rights management (DRM) technology. Any implementation of audio decryption must comply with applicable laws and Spotify's terms of service.
