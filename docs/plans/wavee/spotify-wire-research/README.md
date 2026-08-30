# Spotify wire research — index

Reverse-engineering of Spotify desktop **1.2.94.583** against Wavee's implementation.
**16 Fiddler captures, ~11,000 sessions**, decoded protobuf-first.

Promoted from the session-local `tmp/saz-analysis/` dump (gitignored) so future work has a durable,
reviewable corpus. **Raw `.saz` files and `graphql-samples/*.req.txt` stay out of git** — those still
live under `%USERPROFILE%\Documents\Fiddler2\Captures` and (optionally) `tmp/saz-analysis/`.

## Read in this order

| # | File | What it is |
|---|---|---|
| 1 | **[`CORRECTIONS.md`](CORRECTIONS.md)** | ⚠️ **Start here.** What was verified on the wire, and which earlier claims were wrong (incl. the 64 KB zstd truncation). |
| 2 | [`../spotify-wire-parity-plan.md`](../spotify-wire-parity-plan.md) | **Executable plan** — workstreams, PR sequencing, UI mapping, known outstanding. |
| 3 | [`E2E-DIFF.md`](E2E-DIFF.md) | Per-op request/response diffs vs Wavee, decoded REST bodies, drafted protos, capture recipe. Pre-dates the multi-frame-zstd fix — its kind *counts* are undercounts; its *hashes* are sound. |
| 4 | [`GAP-ANALYSIS.md`](GAP-ANALYSIS.md) | First-pass feature-level inventory. |
| 5 | [`research/`](research/) | Raw, unverified agent output from workflow `wf_5a5408b2-258`. Treat as evidence, not gospel — cross-check against `CORRECTIONS.md`. |

Sibling plan folded in as a workstream:
[`../liked-songs-content-filters-plan.md`](../liked-songs-content-filters-plan.md) — **superseded** by the parity plan (Workstream C); kept for history.

## `research/` contents

| File | Agent |
|---|---|
| [`01-SYNTHESIS.md`](research/01-SYNTHESIS.md) | Cross-referenced gap list: stale hashes, new ops, new endpoints, XM opportunities, ranked |
| [`02-XM-CENSUS.md`](research/02-XM-CENSUS.md) | Complete extension-kind census |
| [`03-XM-PAYLOADS.md`](research/03-XM-PAYLOADS.md) | Payload decode for kinds Wavee doesn't consume |
| [`04-XM-REQUEST-PATTERNS.md`](research/04-XM-REQUEST-PATTERNS.md) | Batching, etag usage, surface bundles |
| [`05-CAPTURES-omg-all.md`](research/05-CAPTURES-omg-all.md) | `omg.saz` + `all.saz` — browsing & search |
| [`06-CAPTURES-concerts.md`](research/06-CAPTURES-concerts.md) | `concerts.saz` + `concerts_v2.saz` |
| [`07-CAPTURES-video.md`](research/07-CAPTURES-video.md) | `VIDEO.saz` |
| [`08-CAPTURES-playback.md`](research/08-CAPTURES-playback.md) | `playback_remote.saz` |
| [`09-CAPTURES-playlist.md`](research/09-CAPTURES-playlist.md) | playlist mutation, permissions, lyrics |
| [`10-WAVEE-INVENTORY.md`](research/10-WAVEE-INVENTORY.md) | What Wavee implements today (code read at analysis time) |

## Supporting inventories (no auth material)

| File | What |
|---|---|
| [`graphql-operations.txt`](graphql-operations.txt) | Named Pathfinder ops seen in the first pass |
| [`spotify-endpoint-patterns.txt`](spotify-endpoint-patterns.txt) | REST / spclient endpoint pattern census |
| [`graphql-summary.json`](graphql-summary.json) | Machine-readable GraphQL op summary |

## Captures (not in repo)

`all.saz` 2290 · `omg.saz` 2440 · `VIDEO.saz` 1415 · `spotify.saz` 1147 · `playback_remote.saz` 676 ·
`content-filters.saz` 646 · `concerts.saz` 156 · `concerts_v2.saz` 113 · `playlists.saz` 88 ·
`artist_more.saz` 72 · `someMoreMaybe.saz` 58 · `waveforms.saz` 54 · `final.saz` 48 ·
`pre-release.saz` 21 · `signals_2.saz` · `lyrics.saz` · `browe.saz` · `waveforms.saz`
— under `%USERPROFILE%\Documents\Fiddler2\Captures`.

## Tooling gotchas (they cost a full analysis pass each)

1. **Multi-frame zstd.** `decompressobj().decompress()` silently stops after 64 KB.
   Use `stream_reader(body, read_across_frames=True)`. Wavee's `SpotifyZstd.cs` already documents this.
2. **De-chunk before decompressing.** A body whose hex starts `30303030…` is chunked;
   gunzipping directly fails with `Not a gzipped file (b'00')`.
3. `PYTHONIOENCODING=utf-8` on Windows, or printing decoded text raises `UnicodeEncodeError`.
4. Parse Pathfinder bodies with `json.loads`, not regex — one `sha256Hash` can host multiple
   named operations.

## What deliberately stayed out of git

- Raw `.saz` captures
- `graphql-samples/*.req.txt` / any dump that carries live `Authorization` / `client-token`
- Decoder scratch scripts under `tmp/saz-analysis/*.py` (re-create from the gotchas above if needed)

If you need a fresh decode, re-run against the Fiddler captures locally and keep outputs under `tmp/`.
