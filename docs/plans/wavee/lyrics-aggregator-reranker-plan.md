<!--
Wavee synced-lyrics sourcing plan.
Revised 2026-06-29 after grounding in BetterLyrics, Lyricify-Lyrics-Helper, and the current Wavee app layout.
This is an integrated Wavee implementation plan, not a separate project and not a 1:1 port.
Companion to docs/betterlyrics-parity-plan.md, which covers the rendering/control side.
-->

# Wavee Synced Lyrics: Integrated Aggregator + Reranker

## 0. Direction

Do not create a `Wavee.Lyrics.*` project. The lyrics pipeline should live inside the existing app assembly and keep the current solution shape:

- `Wavee.Core` remains framework-neutral contracts and domain records only. No HTTP, no parser dependencies, no SharpZipLib, no Newtonsoft, no Lyricify implementation code.
- `Wavee` owns the real implementation under `Wavee/Backend/Lyrics/`.
- `Wavee.Tests` owns the fixtures and tests. No test project split.
- `Wavee/Wavee.csproj` is the only place that receives package references needed by the in-app lyrics implementation.

The goal is still the same: fan out to multiple providers, normalize all formats to one `LyricsDocument`, compare candidates against Spotify-native lyrics when available, correct constant timing offsets, and prefer real word/syllable timing only when it is actually the right lyric.

BetterLyrics is input, not a template. The important thing it teaches us is provider breadth and parser/decrypter coverage. Wavee should improve the architecture by using identity, ISRC, real track metadata, the live spclient transport, and a content/timing reranker.

## 1. What We Know

### BetterLyrics baseline

BetterLyrics searches providers in priority order or in a metadata-only best-match mode. Its matching is title/artist/album/duration similarity, not lyric content, not timing, and not ISRC. It also parses `[offset:]` but does not apply it to line timings.

That means the main Wavee improvement is not another provider list. It is a better decision engine:

- Fetch all useful candidates in parallel.
- Normalize them into the same shape.
- Compare lyric text and line timing against Spotify-native lyrics when available.
- Correct a constant timing offset.
- Reject wrong-song or bad-timing word-synced candidates instead of blindly preferring them.

### Lyricify-Lyrics-Helper reuse

The useful Lyricify pieces are:

- Data model concepts for line/word-synced lyrics.
- Metadata scoring helpers.
- Format parsers for LRC/QRC/KRC/YRC/TTML and related formats.
- QRC/KRC decrypters.
- Info-line detection for credits/headers.
- Chinese text normalization helpers.

Do not vendor Lyricify as a separate project. Copy or adapt the minimum source files into:

```text
Wavee/Backend/Lyrics/Lyricify/
```

Use namespace `Wavee.Backend.Lyrics.Lyricify`. Treat this as internal implementation code. Keep the Apache-2.0 license/notice files near the vendored folder.

Split the reuse this way:

- In-place vendor now: models needed by parsers, string/compare helpers, text parsers, QRC/KRC decrypters, info-line detection, Chinese converter.
- Port, do not import blindly: provider HTTP clients and JSON response models. Convert Newtonsoft usage to typed `System.Text.Json` or `JsonDocument`.
- Drop: unused provider stubs, NVorbis references, and anything not needed for lyrics fetch/parse/rank.

## 2. Current Wavee Seams

Grounded current paths:

- Lyrics contracts are in `Wavee.Core/Playback/Playback.cs`.
- There is also an `ILyricsSource` facet in `Wavee.Core/Sources/SeamPorts.cs`.
- Fake playback implements both `ILyricsProvider` and `ILyricsSource`.
- `Services.Lyrics` is currently an `ILyricsProvider`.
- Real mode still passes the fake player as lyrics in `Wavee/App/Services.cs`.
- Live spclient HTTP is already available as `Wavee.Backend.Spotify.IHttpExchange` / `HttpPipeline`.
- The live bootstrap that has the real pipeline is `Wavee/SpotifyLive/LiveSessionHost.cs`.
- Metadata projection is `Wavee/Backend/Metadata/ExtendedMetadataSource.cs`.
- Lean track proto is `Wavee/SpotifyLive/Protos/lean_metadata.proto`; Spotify's full `metadata.proto` already has `external_id` field 10 and `has_lyrics` field 18.

Immediate seam cleanup:

- Keep `ILyricsProvider` as the app-facing lyrics service for now.
- Keep `ILyricsSource` only as the future source-facet contract.
- Add `SwitchableLyrics : ILyricsProvider` in `Wavee/Backend/Switchable.cs`, mirroring the existing switchable pattern.
- `Services.CreateReal` should expose `SwitchableLyrics(fakePlayer)`.
- `LiveSessionHost.StartAsync` should build the real lyrics provider after live login and swap it in, either by extending `Services.GoLive(..., ILyricsProvider? lyrics = null)` or adding `Services.SetLyrics(...)`.

That preserves the UI's stable service identity and avoids rebuilding the app tree on login.

## 3. Proposed In-App Layout

```text
Wavee/
  Backend/
    Lyrics/
      AggregatingLyricsProvider.cs
      LyricsCandidate.cs
      LyricsNormalizer.cs
      LyricsReranker.cs
      LyricsCache.cs
      LyricsText.cs
      LyricsOptions.cs
      Sources/
        ILyricCandidateSource.cs
        SpotifyNativeLyricsSource.cs
        AmllTtmlDbSource.cs
        LrcLibSource.cs
        QqMusicLyricsSource.cs
        KugouLyricsSource.cs
        NeteaseLyricsSource.cs
        MusixmatchLyricsSource.cs
        AppleMusicLyricsSource.cs        # optional, opt-in
      Parsers/
        TtmlToLyricsData.cs              # thin adapter if Lyricify parser shape needs wrapping
      Lyricify/
        ...selected adapted Lyricify source files...
        LICENSE
        NOTICE
```

Responsibilities:

- `Wavee.Core`: shared records and interfaces only.
- `Wavee/Backend/Lyrics`: provider fan-out, parsing, normalization, ranking, caching.
- `Wavee/SpotifyLive/LiveSessionHost.cs`: wires live-only dependencies, because it owns `LiveSpclient.Pipeline`, base URL, session market, and login lifecycle.
- `Wavee/App/Services.cs`: keeps the switchable `ILyricsProvider` stable.
- `Wavee.Tests/Lyrics`: unit fixtures for parsing, normalization, ranking, offset correction, and fake HTTP source behavior.

No new csproj. No new solution item. No `ProjectReference`.

## 4. Core Model Additions

Keep this additive and small.

In `Wavee.Core/Playback/Playback.cs`:

```csharp
public enum LyricsSyncKind { None, Unsynced, Line, Syllable }

public sealed record LyricLine(
    long StartMs,
    string Text,
    IReadOnlyList<LyricSyllable> Syllables,
    long? EndMs = null,
    string? Translation = null,
    string? Romanization = null,
    bool IsWordByWord = false);

public sealed record LyricsDocument(
    string TrackId,
    bool IsSynced,
    IReadOnlyList<LyricLine> Lines,
    LyricsSyncKind Sync = LyricsSyncKind.Line,
    string? Provider = null,
    long OffsetMsApplied = 0);
```

In `Wavee.Core/Domain/Models.cs`, add only identity fields that are broadly useful beyond lyrics:

```csharp
string? Isrc = null,
bool? HasSpotifyLyrics = null
```

Add these at the end of `Track` so existing construction sites keep compiling.

Important: `HasSpotifyLyrics == false` must not stop the whole cascade. It should only skip or downrank the Spotify-native source. AMLL, LRCLIB, QQ/Kugou/NetEase, and Musixmatch can still have lyrics even when Spotify does not expose native lyrics.

## 5. Metadata Projection

Update `Wavee/SpotifyLive/Protos/lean_metadata.proto`:

```proto
message LeanExternalId {
    optional string type = 1;
    optional string id = 2;
}

message LeanTrack {
    optional bytes gid = 1;
    optional string name = 2;
    optional LeanAlbumRef album = 3;
    repeated LeanArtistRef artist = 4;
    optional sint32 duration = 7;
    optional bool explicit = 9;
    repeated LeanExternalId external_id = 10;
    optional bool has_lyrics = 18;
}
```

Then update `ExtendedMetadataSource.ProjectTrack` to:

- Extract `isrc` from `external_id` where `type == "isrc"` ignoring case.
- Project `HasSpotifyLyrics = t.HasHasLyrics ? t.HasLyrics : null`.
- Pass both into the `Track` record.

This unlocks Apple/Musixmatch identity matching and lets Spotify-native lyrics be skipped cheaply when the wire explicitly says none.

## 6. Provider Set

Provider priority is not winner priority. Providers produce candidates; the reranker picks the winner.

| Source | Match key | Sync | Default | Notes |
|---|---|---:|---:|---|
| AMLL TTML DB | Spotify track id | Syllable | On | Clean, no auth, direct identity match. First word-synced source to implement. |
| Spotify native | Spotify track id | Line | On | Best reference source. Candidate too, but mainly used for text/timing anchor. |
| LRCLIB | title/artist/album/duration | Line | On | Clean fallback. Use metadata match and reranker validation. |
| QQ Music | search + Lyricify metadata pick | Syllable | Opt-in | QRC. Useful for CJK coverage. Grey source. |
| Kugou | search + hash/accessKey | Syllable | Opt-in | KRC. Needs decrypt/inflate path. Grey source. |
| NetEase | search + id | Syllable/Line | Opt-in | YRC/LRC. Grey source. |
| Musixmatch | ISRC or search | Syllable | Opt-in | Good richsync coverage, token/captcha fragility. Grey source. |
| Apple Music | ISRC -> Apple song id | Syllable | Opt-in later | High quality but user-token/dev-token friction. Not required for the first Wavee implementation. |

Clean-by-default means AMLL + Spotify-native + LRCLIB. The grey providers are implemented behind config flags, not hardwired into the normal path.

## 7. Candidate Pipeline

`AggregatingLyricsProvider.GetLyricsAsync(trackId)`:

1. Resolve the `Track` from `IStore.GetTrack("spotify:track:" + trackId)` or from current playback state as a fallback.
2. Build a `LyricsRequest` with track id, uri, title, artists, album, duration, ISRC, market, and `HasSpotifyLyrics`.
3. Check the positive cache by `trackId`.
4. Fan out all enabled sources in parallel with per-source timeouts.
5. Normalize every raw result to a `LyricsCandidate`.
6. Rank all candidates together.
7. Apply the selected candidate's timing correction.
8. Cache the winning `LyricsDocument`.

Do not first-hit short-circuit. The whole point is to let a later but better word-synced candidate beat an earlier line-synced one.

Use per-source negative caching, not one global "no lyrics" result. A provider miss should not suppress other providers.

## 8. Normalization

Normalization produces display-safe lyrics and comparison-safe text.

Rules:

- Preserve original display text except for obvious metadata/credit line removal.
- Build a separate normalized comparison string per line: lowercase, punctuation stripped, whitespace collapsed, CJK traditional-to-simplified where safe.
- Drop ID3/header tags such as `[ti:]`, `[ar:]`, `[al:]`, `[by:]`, `[length:]`, `[offset:]`.
- Consume `[offset:]` and apply it before reranking.
- Use Lyricify `InfoLines` credit detection for `Lyrics by`, `Composed by`, `作词`, `作曲`, etc., plus Wavee's own additive patterns.
- Do not remove a line that has real word/syllable timing unless it is clearly metadata.
- Derive `EndMs` as last syllable end, else next line start, else a conservative fallback.
- Do not fabricate syllables in the data provider for line-only sources. Mark them as line-synced and let the visual control synthesize transient display syllables if it needs them.

This avoids polluting the reranker with headers while preserving the lyric text users actually want to see.

## 9. Reranker

The reranker is the reason Wavee can be better than BetterLyrics.

### Inputs

Each candidate carries:

- Provider id.
- `LyricsDocument`.
- Sync kind: syllable, line, unsynced.
- Match basis: identity, ISRC, metadata search, or local file.
- Raw source score if any.
- Normalized line texts.
- Applied source offset.

### Reference

- If Spotify-native line-synced lyrics are available, use them as the text/timing reference.
- If Spotify-native is absent, build a consensus text reference from the candidate set and use only intrinsic timing checks.

### Text agreement

Greedily align candidate lines to reference lines in order. Score by LCS ratio with token-aware normalization. This catches:

- wrong song,
- remix/live version mismatch,
- romanization vs original mismatch,
- header-only or truncated lyrics,
- instrumental tracks with bogus lyric files.

### Timing correction

For text-matched line pairs:

```text
delta_i = candidate.start_i - reference.start_i
globalOffset = median(delta_i)
drift = MAD(delta_i - globalOffset)
```

Interpretation:

- Low drift means the candidate is internally coherent but globally offset. Apply `-globalOffset` to the winner.
- High drift means timings are locally wrong. Penalize heavily.
- If there are too few matched lines, do not apply automatic correction.

This directly handles the "some lyrics are mistimed" problem.

### Sync gate

Syllable-synced candidates are preferred, but only after correctness is established.

A syllable candidate keeps its sync advantage only if:

- text agreement is at least 0.80, and
- timing is either reference-correct or globally correctable, and
- coverage is not badly truncated.

Otherwise it is demoted below a clean line-synced candidate. This prevents a bad word-synced lyric from beating Spotify's correct line-synced lyric.

### Score

Keep weights in one constants file:

```text
score =
  0.40 * textAgreement
+ 0.25 * syncTier
+ 0.20 * timingScore
+ 0.10 * coverage
+ 0.05 * providerPrior
```

Provider prior is only a tiebreaker. Correctness and timing dominate.

Suggested provider prior:

```text
AMLL > Apple > Musixmatch > Spotify native > LRCLIB > QQ/Kugou/NetEase
```

The CJK providers can still win when they agree with the reference and offer real word timing.

## 10. Spotify-Native Source

Implement inside `Wavee/Backend/Lyrics/Sources/SpotifyNativeLyricsSource.cs`.

Constructor dependencies:

- `IHttpExchange` live pipeline.
- `Func<string>` spclient base URL.
- session market or `from_token`.

Endpoint shape:

```text
GET {baseUrl}/color-lyrics/v2/track/{trackId}?format=json&vocalRemoval=false&market=from_token
```

Use `JsonDocument`, not reflection-based JSON. Parse:

- `lyrics.syncType`
- `lyrics.lines[].startTimeMs`
- `lyrics.lines[].words`
- provider/provenance fields when present

Spotify syllables are normally empty; mark this as line-synced.

If `Track.HasSpotifyLyrics == false`, skip this source but continue the rest of the cascade.

## 11. Wiring

Add `SwitchableLyrics`:

```csharp
public sealed class SwitchableLyrics : ILyricsProvider
{
    ILyricsProvider _inner;
    public SwitchableLyrics(ILyricsProvider inner) => _inner = inner;
    public void SetInner(ILyricsProvider inner) => Volatile.Write(ref _inner, inner);
    public Task<LyricsDocument?> GetLyricsAsync(string trackId, CancellationToken ct = default)
        => Volatile.Read(ref _inner).GetLyricsAsync(trackId, ct);
}
```

Update `Services`:

- `CreateFake`: keep `Lyrics = player`.
- `CreateReal`: set `Lyrics = new SwitchableLyrics(player)`.
- Store the switchable instance through the existing `ILyricsProvider Lyrics` property.
- Add a method or extend `GoLive` so live login can install the real lyrics provider.
- `GoOffline` resets lyrics to a fresh fake provider alongside player/devices/session.

Update `LiveSessionHost.StartAsync`:

- After `live` and store/metadata are available, construct `AggregatingLyricsProvider`.
- Include clean sources immediately: Spotify-native, AMLL, LRCLIB.
- Include grey sources only from config.
- Install it through `Services`.

The UI keeps reading `svc.Lyrics`; it does not care whether the backend is fake or live.

## 12. Caching

Use an in-memory cache first; persistent cache is optional later.

Keys:

- Winner cache: `spotifyTrackId`.
- Candidate source cache: `provider + providerVersion + spotifyTrackId + metadata fingerprint`.
- Negative cache: per provider, short TTL.

Do not cache a global no-lyrics result unless every enabled provider completed and agreed there is no lyric. Even then, TTL should be short because AMLL/LRCLIB coverage can improve.

Cache the decision metadata too:

- selected provider,
- score,
- applied offset,
- competing providers,
- reason for demotions.

That makes reranker bugs explainable.

## 13. Tests

All tests stay in `Wavee.Tests`.

Add:

```text
Wavee.Tests/Lyrics/
  LyricsNormalizerTests.cs
  LyricsRerankerTests.cs
  SpotifyNativeLyricsSourceTests.cs
  LyricsProviderAggregationTests.cs
  Fixtures/
    spotify-line.json
    amll-word.ttml
    lrclib-offset.lrc
    wrong-song-word.ttml
    credit-header.lrc
```

Required cases:

- Header and credit lines are removed before comparison/display.
- `[offset:]` is applied.
- A globally offset LRC is corrected against Spotify reference.
- A wrong-song syllable candidate loses to a correct Spotify line candidate.
- AMLL syllable candidate beats Spotify line when text agrees and timing is coherent.
- `HasSpotifyLyrics == false` skips Spotify-native but still queries AMLL/LRCLIB.
- Provider timeout does not fail the entire aggregate.
- No network in tests; use `FakeExchange` for spclient and small fake candidate sources for aggregation.

## 14. Phased Build

### Phase 0: In-place foundation

- Add model fields to `LyricsDocument`, `LyricLine`, and `Track`.
- Add `SwitchableLyrics`.
- Add `LeanExternalId`, `external_id`, and `has_lyrics` projection.
- Create `Wavee/Backend/Lyrics` skeleton and `LyricsRequest`/`LyricsCandidate`.
- Add tests proving fake mode still works and real mode can swap lyrics without rebuilding services.

Acceptance: build is clean; fake lyrics unchanged; real services expose a switchable lyrics provider; ISRC and `HasSpotifyLyrics` populate on projected tracks.

### Phase 1: Clean sources

- Implement Spotify-native source.
- Implement AMLL TTML DB by Spotify track id.
- Implement LRCLIB by title/artist/album/duration.
- Add TTML and LRC normalization with explicit header trim and offset consumption.
- Add aggregate fan-out, per-source timeout, and in-memory cache.

Acceptance: live tracks can return real lyrics from clean sources; AMLL word timing can win; Spotify-native works as reference; misses degrade gracefully.

### Phase 2: Reranker

- Implement text agreement, coverage, timing sanity, median offset correction, sync gate, and provider prior.
- Add decision logging/provenance.
- Add fixture tests for mistiming and wrong-song rejection.

Acceptance: known-offset lyrics are corrected; bad word-synced lyrics lose; good word-synced lyrics win.

### Phase 3: Lyricify CJK and Musixmatch integration

- Vendor only the required Lyricify parser/decrypter/helper files into `Wavee/Backend/Lyrics/Lyricify`.
- Port QQ/Kugou/NetEase/Musixmatch HTTP fetch code to typed STJ/`JsonDocument`.
- Keep these sources config-gated and disabled by default until tested.
- Add fixtures for QRC/KRC/YRC parse/decrypt where licensing permits storing samples.

Acceptance: at least one QQ/Kugou/NetEase word-synced candidate can be fetched, parsed, normalized, and selected by reranker when it agrees with the reference.

### Phase 4: Optional Apple and polish

- Add Apple Music only if we want the token/subscription flow.
- Add provider health/backoff.
- Add persistent cache if startup/repeated-fetch cost matters.
- Add settings UI for provider toggles only after the backend behavior is stable.

Acceptance: provider configuration is explicit, stable, and explainable; no grey source is accidentally used by default.

## 15. Risks and Decisions

| Item | Status | Decision |
|---|---|---|
| Separate project | Closed | Do not create one. In-app implementation under `Wavee/Backend/Lyrics`. |
| Core dependencies | Closed | `Wavee.Core` gets only additive records/interfaces. No parser or HTTP code. |
| Grey providers | User/product decision | Implement behind config. Default off except clean sources and Spotify-native. |
| Lyricify import size | Risk | Vendor minimum files only; no blind subtree import. |
| Newtonsoft usage | Risk | Do not introduce it. Port to STJ or `JsonDocument`. |
| SharpZipLib dependency | Check during Phase 3 | Add to `Wavee/Wavee.csproj` only if built-in inflate is insufficient. |
| `has_lyrics=false` misuse | Closed | It only gates Spotify-native, not AMLL/LRCLIB/CJK fallback. |
| Wrong word-synced winners | Main algorithm risk | Sync gate plus Spotify-reference text/timing agreement. |

## 16. Bottom Line

Build this as a first-class Wavee backend feature, not as a library project. Keep Core clean, put implementation in `Wavee/Backend/Lyrics`, wire it through `SwitchableLyrics`, and use the live spclient pipeline only after login.

The technical advantage over BetterLyrics is the reranker: Wavee knows the Spotify track id, can project ISRC, can fetch Spotify-native line lyrics as a reference, and can choose the best candidate by content and timing instead of provider priority.
