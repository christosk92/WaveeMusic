# Verified corrections — read before trusting anything in `research/`

Durable home: `docs/plans/wavee/spotify-wire-research/` (this file). The files in `research/` are
**raw, unverified agent output** from workflow run `wf_5a5408b2-258`. This file records what was
checked directly against the wire, and what turned out to be wrong — including earlier claims in
`E2E-DIFF.md`.

---

## 1. My decoder was truncating every zstd response at 64 KB ⛔

Spotify's extended-metadata and Pathfinder responses are **multi-frame zstd**.

```python
zstd.ZstdDecompressor().decompressobj().decompress(body)          # STOPS AFTER FRAME 1
zstd.ZstdDecompressor().stream_reader(body, read_across_frames=True).read()   # correct
```

Measured on `omg.saz`:

| session | single-frame | multi-frame | lost |
|---|---|---|---|
| 0135 (home) | 65,536 | 241,679 | **176,143 B (73%)** |
| 0161 | 65,536 | 241,510 | 175,974 B |
| 0132 | 65,536 | 101,857 | 36,321 B |

**Consequence:** every extension-kind count in `E2E-DIFF.md` before this discovery is an undercount.
Hash findings are unaffected — those come from *request* bodies, which are gzip, not zstd.

Wavee's own `src/apps/Wavee/Backend/Spotify/SpotifyZstd.cs:9` already documents this hazard
("known to truncate multi-frame bodies, so we never lean on it"). The throwaway analysis tooling
did not inherit that knowledge.

**Corrected census** (multi-frame decode, omg + all + VIDEO + waveforms + someMoreMaybe):

| n | kind | message | Wavee consumes? |
|---|---|---|---|
| 217,148 | 10 `TRACK_V4` | `metadata.Track` | ✅ |
| 138,613 | 182 | `ConsumptionExperienceTrait` | ✅ |
| 128,451 | 179 | `VisualIdentityTrait` | ❌ |
| 128,419 | 249 | `ContentExperienceTrait` | ❌ |
| 128,232 | 212 | `PlaybackTrait` | ✅ |
| 127,877 | 178 | `IdentityTrait` | ❌ |
| 11,567 | 99 | `VideoAssociations` | ✅ |
| **11,559** | **222** | `audio_attributes.v2.AudioAttributes` | ❌ |
| 11,523 | 98 | `AudioAssociations` | ❌ |
| 11,323 | 85 | `OriginalVideo` | ✅ |
| 882 | 239 | `ContentCapabilityTrait` | ❌ |
| 747 | 16 | `CANVAS_V1` | ❌ |
| **215** | **6** | `TRACK_DESCRIPTOR` | ❌ |

---

## 2. `home`'s hash is NOT stale — my earlier claim RETRACTED ⛔

I reported Wavee's `HomeHash` as stale and made it a P0. That was wrong.

Scanning every capture for Wavee's `9052ac65ff42aefe6d39c45c184d9144cf8dbcc233ea1a76f8649264ad3e7896`:

| capture | session | op | status |
|---|---|---|---|
| `all.saz` | 0029 | home | **HTTP 200** |
| `playback_remote.saz` | 002 | home | **HTTP 200** |
| `playback_remote.saz` | 037 | home | **HTTP 200** |
| `signals_2.saz` | 053 | home | **HTTP 200** |

Spotify keeps **old persisted-query documents alive**. The newer `5366cbf1…` (18×) is simply what the
shipping client prefers.

### The structural lesson

> **"Wavee's hash differs from the capture" does NOT mean "broken." It means unverified.**

Only an actual HTTP 400 in `wavee.log` proves breakage — and `PathfinderClient.QueryBodyBytesAsync`
already logs exactly that string.

**Hash status, measured:**

| op | Wavee's hash on the wire | verdict |
|---|---|---|
| `home` | 4× HTTP 200 | ✅ fine, don't touch |
| `queryArtistOverview` | **0 occurrences** | ⚠️ unverified |
| `searchAlbums` | **0 occurrences** | ⚠️ unverified |
| `queryNpvArtist` | 1× HTTP 200 | ✅ both documents live |
| all 12 concert ops | identical to capture | ✅ **zero drift** |

⚠️ The synthesis agent's ranked item #1 asserts a stale query "400s and takes the whole Albums search tab
with it." Its *own* evidence field contradicts this, and the `home` result disproves the general claim.
Treat `searchAlbums` as **unverified — migrate to reduce risk**, not as a confirmed outage.

---

## 3. Kind 222 is NOT Mix-lens-gated — my earlier claim RETRACTED ⛔

I reported kind 222 (and the `playlistmixing` family) as gated behind `AUTO_LENS == "mix"`, based on
seeing ~40 occurrences. With the corrected decoder there are **11,559**. It is broadly present.

The gating claim was an artifact of the truncated decode. Kinds 217/218/219/225/237 do still appear far
more narrowly, so *some* of the family is gated — but 222 is not.

---

## 4. Kind 6 `TRACK_DESCRIPTOR` — the "single sample" caveat is RESOLVED ✅

`docs/plans/wavee/liked-songs-content-filters-plan.md` warned that kind 6 appeared "once, for one track"
and that `descriptors` being repeated was "inherited from the upstream definition, not proven here."

Corrected: **210 payloads**, and multi-descriptor is the norm.

Descriptors per track — histogram (count of tracks by number of descriptors):

```
1:6  2:8  3:5  4:9  5:7  6:9  7:9  8:5  9:8  10:12  11:10  12:12  13:9  14:6
15:10  16:17  17:6  18:8  19:8  20:5  21:9  22:7  23:4  24:4  25:6  26:3  27:3  28:3  30:1  33:1
```

Range 1–33, median ~13. Example:

```
spotify:track:02BcXEH1zJYbXSabPtNlKf
  ('pop', 0.901, 'Pop')            ('dance pop', 0.858, 'Dance Pop')
  ('fast', 0.785, 'Fast')          ('energetic', 0.765, 'Energetic')
  ('electropop', 0.756, 'Electropop')  ('hip hop', 0.752, 'Hip Hop')
  ('upbeat', 0.731, 'Upbeat')      ('pop rap', 0.697, 'Pop Rap')
  ('synthpop', 0.681, 'Synthpop')  ('soundtrack', 0.675, 'Soundtrack')
  ('pump up', 0.65, 'Pump Up')     ('motivation', 0.609, 'Motivation')
```

Two facts for the implementation:
- Descriptors arrive **sorted by descending weight** — take the top N for chips, no client-side sort.
- `text` is the lowercase match token, `display_name` is the Title-Case label. **Join on `text`**,
  case-insensitively. They are not the same string.

A real multi-tag fixture can now replace the planned synthetic test.

---

## 5. Kind 142 is not the waveform — my earlier claim RETRACTED ⛔

I initially called kind 142 `ListTunerAudioAnalysis` "the closest thing to a drawable waveform."
`waveforms.saz` produced the real one:

**Kind 237 `spotify.playlistmixing.extensions.mixthreebandwaveforms.ThreeBandWaveforms`**

```
sample_rate = 1 : 44100
hop_ms      = 2 : 20        → 50 Hz, one byte per 20 ms per band
band_low    = 3 : bytes[12886]  max 211  mean 52.0
band_mid    = 4 : bytes[12466]  max 125  mean 47.0
band_high   = 5 : bytes[12466]  max  89  mean 17.1
```

**Rate confirmed against a known duration** — track `3UEwPrMwvnqXs2nv4yDwTm` ("sacrifice"), stated ~4:09:
`12466 ÷ 50 Hz = 249.32 s = 4:09.3` ✅. That in turn confirms kind 218 at `17452 ÷ 249.32 = 70.0 Hz`,
exactly `22050/315`.

Kind 142 at 50 Hz implies 253.2 s for the same track — ~4 s long. Different framing, separate product.

**Two unresolved oddities in 237:** `band_low` is 420 bytes longer than the other two (which are exactly
equal), so a naive shared-timebase render drifts ~8 s by track end; and the low/mid/high ordering is
**inferred** from descending energy, not named in the proto.

---

## 6. Decode traps confirmed by the workflow (independent of the above)

- Per-entity XM `status_code` includes **451** and **400**, not just 200/304/404.
- A **200 with an absent `Any.value`** is a valid "nothing here" — kind 85 `ORIGINAL_VIDEO` did this
  10,069 / 10,069 times. Neither is a decode failure.

---

## What survived unchallenged

- All 12 **concert** hashes match byte-for-byte (`concerts.saz` + `concerts_v2.saz` exercise every one).
  Only gap: `ConcertCountHash` is not asserted in `ConcertCaptureContractTests.cs`.
- `searchAlbums` capture hash `64ae1fe6…`, `queryArtistOverview` capture hash `ae0e2958…`.
- The search-facet ops and their shared variable shape, incl. the two traps
  (`searchAudiobooks` sends `includePreReleases: true`; `searchFullEpisodes` has a different minimal shape).
- One `sha256Hash` can host **multiple named operations**, disambiguated by `operationName`.
- Kind 222 payload shape (tempo/key/Camelot), self-validated: 1B=B, 7B=F, 11B=A all match the Camelot wheel.
