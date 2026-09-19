# Podcast implementation, 2026-09-19

Worktree: `C:/WAVEE/wavee-0.3`; engine: `C:/WAVEE/fluent-gpu-pin`.
Matching engine commit: `1a8de5d49629d22ad7a906edc447780cd8fd1ab5` on `feat/0.3-engine-live`.
This report records the implementation following the session-2 handoff and supplied verycomplex3/4 captures. Existing uncommitted work is preserved. No additional capture is required.

## Implemented contracts

| Area | Implementation |
| --- | --- |
| Show membership | Native playlist4 show read/diff, coherent pagination, canonical membership item IDs, disk baseline, reopen/dealer/reconnect revalidation |
| Show facts | Separate authority for rating (37), appearance (179), topics (3), HTML (54); episode number 89 and season 88 |
| Progress | Duration units; repeated/grouped revisions with full timestamp ordering; independent explicit completion; compound mark commands; account-scoped durable delivery |
| Saved episodes | Paginated listen-later discovery, full canonical list read after accepted writes, actual item IDs for removal, dealer invalidation, Your Episodes library tab |
| Episode reader | Routed detail page, description/access state, explicit resume/beginning/position intents, chapters, transcript, discussion, recommendations, saved/completion actions |
| Discussion/rating | Capture-confirmed request identities and response discriminators, replies/reactions pagination, eligibility/pending/sensitive states, account-guarded writes, rating invalidation |
| Transcript | Descriptor URLs, title/sentence grammar, cache key account/episode/language/variant, ETag/TTL, invalid-document and cache-size checks |
| Playback speed | Persistent global episode rate, Connect desired/actual rate, pitch-preserving engine processing, source-time playback clock, seek/reset/EOF handling |
| Video | Episode video resolution and playback-rate propagation using content time |
| Telemetry | Per-event sequence numbering and acceptance/rejection handling, durable retry journal, bounded shutdown drain |
| Catalog/library | Metadata coalescing and conditional per-entity cache, native list headers/revisions/paging, ban and artistban exclusions |

## Comparison findings

All numbered rows refer to section 6 of `findings-wavee-vs-official.md`.

| Finding | Implementation |
| --- | --- |
| 1 | Correct seconds/nanoseconds resume-point codec and grouped revision ordering |
| 2 | Faulted remote resume retries; parked seek publishes; command outcome logging and ordered receipt states |
| 3 | Range response total is authoritative over implausible HEAD length |
| 4 | Tray quit retires Connect and journals final progress; bounded telemetry flush |
| 5 | Playback ID starts at load; context session IDs survive ordinary row changes |
| 6 | Command play origin and metadata survive resolver merge |
| 7 | Ten previous rows, canonical UIDs, autoplay delimiter, show index omitted |
| 8 | Autopodcast uses recent episodes, correct request encoding, and excludes current/recent/banned entries |
| 9 | Actual audio endpoint information, episode speed capability, registration/device-state corrections |
| 10 | OS identity uses three version components |
| 11 | Dealer-g2 discovery and response parsing |
| 12 | Shared metadata cache coalesces duplicate reads and honors TTL/ETag |
| 13 | Single late episode prefetch, with remaining wall time accounting for speed |
| 14 | Outbound seek and resume command fields corrected |
| 15 | Herodotus language headers; Connect device display name distinguishes Wavee |
| 16 | Existing login-time progress hydration retained; no per-play revisions read |
| 17 | Existing AP/dealer lifetime separation and keepalive retained |

Intentional differences: full player snapshots remain requested because ingestion requires them. The private key implementation and its version identity remain outside this task's fenced public workspace. Notification preferences have no verified write contract and display a localized unavailable explanation. Paywall metadata alone does not block an entitled account; only explicit unplayability blocks the reader's play action.

## Validation

| Gate | Result |
| --- | --- |
| Wavee Debug, public-only | Passed, zero warnings/errors |
| Wavee Release, public-only | Passed, zero warnings/errors; a transient preview-compiler crash was retried successfully |
| Wavee.Tests | Passed: 10,799; skipped: 1 (real module process handshake); failed: 0. Release podcast subset: 96/96 passed |
| Engine Debug / Release | Passed; existing gallery/test analyzer warnings remain (164/165 on full rebuild) |
| Engine.Tests | Passed: 480/480, including playback-rate duration, pitch, EOF, seek and allocation cases |
| Windows.Tests | Passed: 515/515 |
| VerticalSlice | Passed: all 1,597 checks |
| Canon | Passed, 33 live documents checked |
| Release Pester | First full run 371 passed, one failed, one skipped; publish-script BOM removed; affected suite rerun 98/98 passed |
| Isolated diagnostics | Passed for JIT and the ARM64 NativeAOT output, no-login/silent profiles, verdict code 0 |
| Offline show and episode routes | Mounted and exited without errors; separate scratch profiles |
| Pixel screenshot | Not verified: hidden HWND produced a black image; a displayed window is required by the existing GPU harness |
| ARM64 NativeAOT publish | Passed, public-only; output `C:/WAVEE/validation/podcast-20260919/publish-arm64/` |

Logs and test results: `C:/WAVEE/validation/podcast-20260919/`. No fresh Spotify recording or live-account write was used to validate these changes. The private playback sources were excluded from every build/test. A transient FLAC allocation failure passed in isolation and on the final full suite without changing the decoder or test.

Integration corrections included restoring the missing SessionFault enum from HEAD, adding the missing Playlist.PageStamp test field, correcting generated episode number/season fields to 89/88, and updating stale tests for capture-confirmed behavior. Two engine scroll tests were corrected to the already implemented parked/paced-input behavior; no production scrolling code changed.

The implementation gate is complete with the explicit pixel/live-service limitations above. The publish is a local public-only validation artifact; no release, installer, or issue was published. The owner subsequently requested committing and pushing both implementation branches.

## Issue references

Read-only triage found existing references #96 (video speed), #98 (playlist sync) and #152 (tray). Additional podcast/Connect/progress issue drafts are prepared locally below; they have not been published. Existing changelog placeholder issue references were preserved. Repository rules require user approval for modifying GitHub calls, so no issues or comments were created automatically.

Draft: **Podcast reader and account state parity**. The supplied captures demonstrate incorrect progress decoding, missing native show membership, and incomplete episode surfaces. Implement native show/saved-list contracts, independent completion, chapters/transcript/discussion/rating, and account-scoped durable delivery. Validation is recorded above.

Draft: **Remote episode playback and Connect state parity**. A faulted episode acknowledges resume/seek without updating state; loading states reuse old identities and autoplay can repeat the current episode. Implement retry/report semantics, ordered command receipts, load-time identity, canonical queue state, conservative external length detection, and final retirement.
