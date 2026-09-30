# Lyrics: port the four grey sources + reranker v2 (research synthesis, 2026-09-29)

> Status: RESEARCH / PROPOSAL - not approved yet. Produced by a 6-agent research workflow (port map, reranker audit, prior art, design docs + field evidence, synthesis, completeness critic). The critic's corrections at the end OVERRIDE the plan where they conflict and must be folded in before implementation.

# Plan: port the four missing lyrics sources and rebuild the reranker for seven

## Summary

0.3 shows three sources because `Lyrics.Boot` registers only spotify, amll and lrclib (`src/apps/Wavee/Shell/Lyrics.Host.cs:1909-1926`). `Options.EnableGreyProviders` (`:75`) is declared and never read. The header at `:43-46` says "OFF BY DEFAULT in 0.2.9 too", which misreads 0.2.9: its live host (`LiveSessionHost.cs` ~1244-1260) turned the four grey sources on, with 30000/30000/1200 ms budgets. The cost shows in the field caches: 0.2.9 picked word-synced lyrics for 74% of tracks (kugou won 34 of 46), 0.3 for 2% (spotify line lyrics won 922 of 1009).

The plan:
1. **Port** kugou (KRC), netease (YRC/LRC), qq (QRC plus the 15-round DES) and musixmatch (richsync) into the existing `ISource` seam. Each file stays small, adds a POST seam, a negative cache and a circuit breaker, and gets bounded budgets.
2. **Add a stage-0 metadata matcher** (`MetadataMatch`). Each search source uses it to pick which search hit to fetch, and it supplies a confidence value to the reranker. This takes Unilyric's version of the Lyricify scheme, plus lx-music's version-marker hard gate.
3. **Reranker v2**: CJK-aware text comparison, a consensus reference when Spotify has no lyrics, an offset cap, a recall term, a deterministic tiebreak, and an end to the first pass locking in a wrong winner.

The finished plan goes in `docs/plans/wavee/lyrics-grey-sources-implementation.md`. Every fix carries the issue number (see Owner decisions).

---

## Port design

**Files.** `Lyrics.Host.cs` is at 1927 lines against a budget of about 2300, so the port goes into new partials:
- `Shell/Lyrics.Crypto.cs`: KRC/QRC decryption and the DES, copied byte for byte from 0.2.9 `Lyricify/{DESHelper,LyricCrypto}.cs`. Keep the Apache-2.0 header.
- `Shell/Lyrics.Match.cs`: `MetadataMatch`, `VersionMarkers` and `CjkFold`, all pure.
- `Shell/Lyrics.CjkTable.cs`: an embedded OpenCC `TSCharacters` single-character map (about 4k pairs, Apache-2.0) as a `FrozenDictionary<char,char>`.
- `Shell/Lyrics.Grey.cs`: `SourceGuard`, the keyword ladder and the browser User-Agent.
- One file per source: `Shell/Lyrics.Grey.{Kugou,Qq,Netease,Musixmatch}.cs`.

**HTTP seam** (`Lyrics.Host.cs:95-151`):
```csharp
public interface IHttpPost : IHttpWithStatus
{
    Task<HttpResult> PostAsync(string url, HttpContent body,
        IReadOnlyDictionary<string, string>? headers, CancellationToken ct);
}
public sealed class HttpFetch : IHttpPost
{
    // Grey client: own handler, UseCookies=false (a hand-set Cookie header must not merge with a shared jar),
    // browser UA set ONCE by the ctor (a headers["User-Agent"] would append a second value).
    public static HttpFetch Grey { get; } = new(GreyClient, "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Wavee/1.0");
    // GetAsync/PostAsync share one Send(HttpRequestMessage) — status + body, catch → HttpResult(0,null), OCE rethrown.
}
```

**`SourceGuard`** (pure, clock injected). It is the per-source negative cache and circuit breaker the `ISource` contract already asks for (`:83-85`), and 0.2.9 never had it:
```csharp
public sealed class SourceGuard(Func<long> nowMs)
{
    public enum MissKind { SongNotFound, NoLyricForSong }
    public static long Ttl(MissKind k) => k == MissKind.SongNotFound ? 6 * H : 24 * H;
    public bool TryGetMiss(string trackId, out MissKind kind, out long ageMs);
    public void Miss(string trackId, MissKind kind);             // transport errors are NEVER cached
    public bool IsOpen(out string reason, out long untilMs);     // breaker
    public void Trip(string reason, long forMs);                 // mxm captcha 2h, 402 quota 6h
    public void Failure(string reason);                          // 3 consecutive transport failures → open 10 min
    public void Success();
}
```
When the guard is open or holds a cached miss, the source returns null right away and first calls `Probe.Note(Id, "skipped: breaker open until 14:32 (captcha)")` or `"cached miss NoLyricForSong 3h ago"`.

**Shared ladder** (`Lyrics.Grey.cs`):
- It walks `Query.Variants(req)` (`Lyrics.cs:1489`) and collects hits from every variant into one pool, deduplicated by `(provider, id)`, keeping the best band (the Unilyric rule).
- It stops as soon as a hit reaches `Perfect` or `VeryHigh`.
- It fetches the best hit at or above `MatchBand.Medium`. If that hit has no body, it falls through to the next one, trying at most 2 bodies (Unilyric's "Comprehensive" mode).
- "Song found, no lyric" is recorded as a `NoLyricForSong` miss, and the ladder is not repeated for it.

**JSON.** Use `System.Text.Json` source generation, one context per source file:
```csharp
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(KugouSongSearch))] [JsonSerializable(typeof(KugouLyricSearch))] [JsonSerializable(typeof(KugouDownload))]
internal sealed partial class KugouJson : JsonSerializerContext;
internal sealed record KugouSongSearch(KugouSongData? Data);
internal sealed record KugouSongData(KugouSong[]? Info);
internal sealed record KugouSong(string? Hash, string? SongName, string? SingerName, string? AlbumName, int Duration /*sec*/);
```
- Musixmatch is the one exception and stays on `JsonDocument`. Its `body` is `[]` when empty and an object otherwise, which a typed contract cannot model. `JsonDocument` is reflection-free, so it is still AOT-safe.
- The qq request body is written with `Utf8JsonWriter`, replacing 0.2.9's hand-escaping `JsonString`.

**Per source.** The URLs, flows and priors are 0.2.9's, from `git show 99e398d7^:.../Sources/GreySources.cs`.

**kugou** (prior 0.5)
- Search at `mobilecdn…/search/song`, which is plain http. The hit list goes through `MetadataMatch.Pick` with `DurationGrainMs = 1000`.
- Lyric search, then `download?ver=1&client=pc&id=…&accesskey=…&fmt=krc`. The comments at `:132-135` apply: use `id`, never `kgid`, and send `ver=1`.
- A `{"status":400}` body is a `NoLyricForSong` miss, with `status` and `info` copied into the note.
- Then `Crypto.DecryptKrc`, `CaptureRaw` of the decrypted KRC, and `WordFormats.ParseKrc` (`Lyrics.cs:1891`).

**netease** (prior 0.5)
- Headers: `Referer` plus `Cookie: appver=8.9.70; os=pc`.
- Calls `api/search/get/web`; hit durations are in ms (`DurationGrainMs = 1`).
- Then `api/song/lyric?…&tv=1&yv=1`. It prefers `yrc` and falls back to `lrc`.
- `tlyric` and `romalrc` are captured for wave 3.

**qq** (prior 0.55)
- Search is a POST to `musicu.fcg` and includes Lyricify's `comm` block, copied verbatim from its `QQMusic/Api.cs`. Without it the server answers code 2001.
- `lyric_download.fcg` is a form POST.
- **Replace `LongestHexRun`.** Parse the XML and decrypt `<content>` only (pure `QrcXml.Split(xml) → (orig, ts, roma)`). The romanization block is usually the longest, so the old rule could pick it.
- If decryption fails and the XML carries plain LRC, fall back to that.

**musixmatch** (prior 0.7)
- Token fetch uses 0.2.9's code (12 h TTL, semaphore, rejects `UpgradeOnly…`). Profile constants live in one `MxmProfile` record so the profile can be swapped.
- `BuildSubtitlesUrl` stays pure. It adds `&track_spotify_id=` to `&track_isrc=`, the lyrics-plus technique.
- `message.header.status_code` is checked before any body is read:
  - 401 with `hint=renew`: drop the token, retry once.
  - 401 with `hint=captcha`: `Trip(2h)`.
  - 402: `Trip(6h)`.
- Reads `has_richsync`, `instrumental` and `restricted`. `instrumental=1` produces the instrumental notice instead of a miss.
- An ISRC or Spotify-id hit gets `MatchBasis.Isrc` and `Confidence = 1`. A search hit's `matcher.track.get` `confidence` maps onto bands: 1000 Perfect, 950 VeryHigh, 900 High, 750 PrettyHigh, 600 Medium.

**Registration** (`Boot`, `:1916`):
- Add `new Sources.Musixmatch(HttpFetch.Grey), Qq, Netease, Kugou`.
- **Delete `EnableGreyProviders`.** It is a dead switch, and the project allows no behaviour switches or legacy paths. Rewrite the headers at `:29-30` and `:43-46`.
- New `Options` defaults: `PerSourceTimeoutMs 12000, TotalTimeoutMs 15000, FirstHitGraceMs 2000`. The ladder's early stop and the per-song miss cache bound kugou at 3 GETs once a song is found.

---

## Reranker v2 design

All of this is in `Lyrics.cs`: `Reranker` at `:2791-3093`, `Candidate` at `:1447` and `Decision` at `:1460`.

**1. Stage-0 metadata match** (`Lyrics.Match.cs`). Take Unilyric's version of the Lyricify scheme: 7-point bands, renormalised over the fields both sides have.
- **Title** runs a cascade: fold (NFKC, lowercase, T→S, `[]`→`()`), then:
  - exact match → Perfect;
  - `x - y` equals `x (y)` → VeryHigh;
  - a deluxe/explicit/feat suffix only one side has → VeryHigh;
  - the bases match and one side has a bracket → Low;
  - ≥80% positional characters equal at the same length → High;
  - otherwise LCS bands at 90/80/68/55.
- **Artist:** Jaccard over a greedy fuzzy match (containment, or Levenshtein ≥ 0.88).
- **Album:** weight 0.4.
- **Duration:** a Gaussian, `7·exp(−d²/2σ²)`, with `d = max(0, |Δ| − grain/2)` and σ = 700 ms. A source that reports whole seconds therefore does not look like a Medium match through rounding alone.
- **Hard gates** return `MatchBand.None` with a reason:
  - |Δ| > 5000 ms (the same limit lrclib uses today);
  - a `VersionMarkers` mismatch (lx-music: live/remix/cover/inst/karaoke/伴奏/现场/ライブ…, each of which must appear on both sides or on neither);
  - a hit with duration 0, which does not gate but drops that dimension and caps the band at High. In 0.2.9 such a hit won automatically.

```csharp
public enum MatchBand { None, VeryLow, Low, Medium, PrettyHigh, High, VeryHigh, Perfect }
public readonly record struct Hit(string Id, string Title, IReadOnlyList<string> Artists, string? Album, long DurationMs, int DurationGrainMs);
public readonly record struct MatchScore(MatchBand Band, double Confidence, double Title, double Artist, double Album, double Duration, string Reason);
public static class MetadataMatch
{
    public static MatchScore Score(Request req, in Hit hit);
    public static int Pick(Request req, IReadOnlyList<Hit> hits, MatchBand floor, out MatchScore best); // -1 = none
}
```
`Candidate` gains `double Confidence = 1.0, MatchBand Band = MatchBand.Perfect, string? MatchNote = null`. Identity and ISRC matches keep the defaults. lrclib also switches to `Pick`, replacing its closest-duration-only rule at `Lyrics.Host.cs:889`.

**2. CJK-aware text.** Failure modes 1, 2 and 9 in the audit.
- `Text.Normalize` (`Lyrics.cs:1865`) becomes a comparison-only fold: NFKC width, lowercase, T→S, katakana→hiragana. It is never used for display.
- `Tokens` (`:2960`) splits runs of Han characters and kana into one token per character. Latin text and Hangul still split on spaces.
- `WordCount` (`:2079`) counts CJK characters as units. The implausible-timing limit is 8 units/s for Latin-dominant lines and 12/s for CJK-dominant lines.
- New `Timing.HasIncoherentWordTiming`: words outside `[line.start−200, line.end+200]`, or not monotone, on ≥ 30% of lines means the word timing is stripped.

With this, a correct QQ, NetEase or Kugou document matches a Chinese Spotify reference, and `TrimUnalignedEdges` can reach its ≥ 3 LCS pairs.

**3. Recall term.** Failure mode 8. Add `recall = lcs / refNonBlankLines`. Promotion to a tier needs `recall ≥ 0.6` for every candidate, trusted ones included. A trusted partial document is still verified, but only at line tier, so it cannot win on tier over a complete reference.

**4. Offset guard.** Failure modes 3 and 6.
- `|applied| ≤ 1500 ms`. Beyond that, `timing = 0.4`, the reason reads `"offset 3.2s > cap (live/remix cut?)"`, and the candidate is not tier-verified.
- A trusted syllable candidate (AMLL, Musixmatch ISRC) is only moved onto Spotify's timing when `MAD ≤ 300` and there are at least 6 pairs. Otherwise it keeps its own clock. The field data motivates this: AMLL applied −1099 ms.

**5. Consensus reference when Spotify has no lyrics.** Failure modes 10 and 12. This finally gives `MatchBasis.Consensus` a producer.
```csharp
static Doc? ConsensusReference(IReadOnlyList<Candidate> cs, out int[] support)
// pairwise (i<j, different ProviderId): agree ⇔ text≥0.5 ∧ coverage≥0.6 ∧ (both synced ⇒ MAD≤600)
// support[i] = #agreeing peers; reference = argmax(support, then trusted, then Sync≥Line, then Confidence)
// null when max support == 0
```
- When it is not null, the normal reference path runs against it, and stage two runs too.
- Without a reference, verification is: trusted, or `support ≥ 1 ∧ Confidence ≥ High`.
- A single metadata candidate with no support is ranked by score only and is never tier-promoted. A title-only search can therefore no longer hand karaoke from the wrong song a win just because Spotify had no lyrics.
- Cost: at most 21 pairwise LCS runs, which is small.

**6. Prior × confidence.** The prior term becomes `WPrior * Prior * Confidence`. Confidence also gates tier promotion for `MetadataSearch` candidates, which need Band ≥ Medium. The weights do not change; tests pin them.

**7. Deterministic order.** Failure mode 7. The stage-two sort key is `(tierDelivered, verified, score, Prior*Confidence, ProviderId)`. Arrival order stops mattering.

**8. First-pass lock-in.** Failure mode 11; the change is in the aggregator in `Lyrics.Host.cs`.
- `willContinue = Richness(winner) < 3 || !winner.Verified || referencePending || anyPendingTrustedSource`.
- The gold short-circuit requires the reference to have arrived.
- `DiskCache` writes an unverified winner with `provisional: true`. A provisional disk hit counts as Richness < 3, so `UpgradeDiskHitAsync` runs again (`:1102`).
- The 42 track-wide miss envelopes get a 7-day TTL.

---

## Diagnostics / inspector

The inspector already builds its rows from `SearchReport` (`Screens/Diagnostics.cs:475-482, 583-609`), so the four new sources appear there without any UI change. What is new is the explanation of each decision.

- **`SourceTrace` gains** `MatchBasis Basis, MatchBand Band, double Confidence, string Match`. For example: `"kugou#a1b2 VeryHigh title=7 artist=7 dur=+320ms (σ1000)"`.
- **`Decision` gains** `double Recall, int Support, bool OffsetCapped, string TieBreak`.
- **The `SearchReport.Summary` line** names the reference as `ref=spotify`, `ref=consensus(kugou,qq,netease)` or `ref=none`.
- **Every gate calls `Probe.Note`:**
  - the top 3 scored search hits, with bands and reasons;
  - version-marker and duration rejections (`"rejected 'Song (Live)': marker live≠∅"`);
  - breaker trips and skips;
  - negative-cache hits;
  - Musixmatch `status_code` and `hint`;
  - kugou's `status` and `info`;
  - the qq XML split (`"orig 4.1k, ts 3.9k, roma 5.2k → orig"`).
- **Always-on `Log` lines**, under the existing `[lyrics]` category, which is already whitelisted:
  - `source=kugou band=VeryHigh conf=0.93 dur=+320ms`
  - `breaker source=musixmatch open 2h reason=captcha`
  - the decision line gains `ref=` plus `recall=` and `support=`.
- **Inspector table** (`Screens/Diagnostics.cs` source table): add **Match**, **Recall** and **Support** columns, plus a "Why not" cell that shows `Decision.Reason` / `TieBreak`. The row formatting moves into the pure `LyricsInspectorRules`, which is tested.

---

## Tests

No test reads source text. All network goes through a fake `IHttpPost` keyed by URL prefix.

| File | Covers |
|---|---|
| `LyricsCryptoTests.cs` (new) | Existing `kugou-krc-caribbean-queen.krc` fixture decrypts and parses. DES known-answer vector (from Lyricify's tests). QRC encrypt→decrypt round trip using `ENCRYPT` mode. Bad hex, length not a multiple of 8, corrupt zlib all return null without throwing. |
| `LyricsMatchTests.cs` (new) | Lyricify and Unilyric `matcher.rs` cases. Version markers (live, 伴奏, カバー). T→S folding (愛情 = 爱情). Duration grain (Δ 400 ms at 1 s grain ≥ High). Duration 0 caps at High. 5 s hard gate. |
| `LyricsGreySourceTests.cs` (new) | Per source: search → id → lyric. The duration or marker pick skips a live cut. Ladder stops on Perfect. `NoLyricForSong` cached, so a second call makes no HTTP request. Transport error not cached. 3 failures open the breaker. Kugou `{"status":400}`. QQ XML with orig/ts/roma selects orig. Netease prefers yrc and falls back to lrc. Musixmatch: `renew` retries once, `captcha` trips the breaker, `instrumental=1` gives the instrumental notice, `BuildSubtitlesUrl` ISRC+spotify id vs query, decoy body (existing fixture) rejected. |
| `LyricsRerankerTests.cs` (new; there are none today) | CJK karaoke verified against a CJK reference. Romanized reference does not verify. Consensus with no reference (3 agreeing beat 1 wrong-song syllable). Lone metadata syllable candidate not promoted. Offset 3 s capped. Trusted AMLL not snapped when MAD > 300. Trusted partial loses on recall. AMLL vs Musixmatch tie is independent of order (shuffled input gives the same winner). Bilingual LRC. `TrimUnalignedEdges`. |
| `LyricsHostTests.cs` (extend) | Provisional disk write, then upgrade on replay. Reference pending means pending sources are not cancelled. Gold short-circuit waits for the reference. |
| `LyricsInspectorRulesTests.cs` (extend) | Match, Recall, Support and Why-not formatting. |

**Fixtures** go in `Wavee.Tests/Fixtures/lyrics/`. Reuse the Caribbean Queen set. Add response shapes with **synthetic lyric text**, to avoid licensing questions:
- `kugou-search-*.json`, `kugou-status400.json`
- `netease-{search,lyric-yrc,lyric-lrconly}.json`
- `qq-lyric-3blocks.xml` (generated from a synthetic QRC by the test's own DES encrypt)
- `mxm-{token,captcha-401,renew-401,instrumental,richsync-empty}.json`
- `cjk-{ref,krc,romanized}.*`

---

## Waves

The orchestrator owns the files listed per agent. There are no builds between waves (the owner's rule); the owner runs Debug and Release builds and the tests at the end.

**W1** (4 agents in parallel)
- **A:** `Lyrics.Crypto.cs` and `LyricsCryptoTests.cs`
- **B:** `Lyrics.Match.cs`, `Lyrics.CjkTable.cs` and `LyricsMatchTests.cs`
- **C:** `Lyrics.Host.cs`: `IHttpPost`, the grey client, `Options` defaults, deletion of `EnableGreyProviders`, headers, new `SourceTrace` fields. Also `Lyrics.Grey.cs` (`SourceGuard`, ladder)
- **D:** `Lyrics.cs`: `Candidate` and `Decision` fields, tokenizer and fold, `WordCount`/incoherence gate, recall, offset guard, consensus, tiebreak. Also `LyricsRerankerTests.cs`

**W2** (3 agents, depends on W1)
- **E1:** `Lyrics.Grey.Kugou.cs`, `Lyrics.Grey.Qq.cs`, and the kugou/qq half of `LyricsGreySourceTests.cs`, split into `LyricsGreyCnTests.cs`
- **E2:** `Lyrics.Grey.Netease.cs`, `Lyrics.Grey.Musixmatch.cs` and `LyricsGreyMxmTests.cs`
- **F:** `Lyrics.Host.cs` aggregator changes: `Boot` registration, `willContinue`/gold/provisional disk write, lrclib moving to `MetadataMatch.Pick`, plus `LyricsHostTests.cs`

**W3** (2 agents)
- **G:** `Screens/Diagnostics.cs` and `LyricsInspectorRulesTests.cs`
- **H** (optional, owner decides): `Lyrics.cs` `Text.MergeSecondary`, a same-timestamp bilingual merge in `ParseLrc` (`:1635`), and structured translations (netease `tlyric`/`romalrc`, qq `contentts`/`contentroma`, the KRC `[language:]` block where `type 1` is the translation). Also cross-check the `CreditRules` lexicon against the LyricsX and lyrics-plus lists.

**W4** (later)
- NetEase `eapi` (AES-128-ECB via `Aes.EncryptEcb`)
- Using the AMLL index's `ncmMusicId`/`qqMusicId` to skip search
- Honouring `Retry-After` for lrclib

---

## Risks

- **Terms of service and legality.** These are unofficial APIs; Musixmatch's terms prohibit scraping outright. Track metadata (title, artist, ISRC, Spotify id) goes to Chinese services and to Musixmatch, which is a privacy disclosure item.
- **Musixmatch:**
  - Captcha 401s are tied to IP address.
  - The token expires; this is handled by `renew` plus the 12 h TTL.
  - The web API has required HMAC-signed URLs since Feb 2026. Whether `apic-desktop` without a signature still works is **unverified**. If it is refused, the breaker parks the source and the inspector says why, so the failure is visible rather than silent.
- **NetEase.** The plain `/api` endpoint is exposed to risk control; eapi is the fallback (W4).
- **QQ.** 2001 without `comm`, VIP-restricted songs, and negative or empty QRC timestamps. The parser must drop those lines.
- **Kugou** runs over plain http, which exposes it to tampering. This is acceptable because the output goes through the decoy gates. It has been stable since 2022.
- **Latency.** About four times the traffic per track. Budgets are bounded at 12/15 s per source and in total, and the grace window stays at 2 s, so the UI takes Spotify's lyrics first and the upgrade arrives in the background.
- **Rebalancing the reranker could regress English picks.** The existing `LyricsHostTests` (`:66-192`) stay green and are the guard.
- **Tokenizer blast radius.** T→S and kana folding change which lines match, and so which credit and header lines get trimmed.

## Owner decisions

1. **Default.** On by default, as 0.2.9 did (recommended), or opt-in behind a Settings > Lyrics toggle per source. The design plan's `:486` still says "user/product decision". An environment variable is not an option.
2. **Include Musixmatch at all,** given its terms and captchas, and which profile: desktop (0.2.9) or android (Lyricify's current default).
3. **NetEase:** ship `/api` now and move to eapi in W4, or do eapi now.
4. **W3-H** (translations and bilingual merge) in this cut or later.
5. **Budgets:** 12000/15000/2000 as proposed, or 0.2.9's 30000/30000/1200.
6. **Fixture policy:** synthetic lyric text for all new fixtures (recommended).
7. **The issue number** to file for this, which the CHANGELOG `(#n)` and the commit's `Fixes #n` need.

---

# Completeness critic (must be folded in)

**Wrong or unverified claims**

1. **"There are no reranker tests today" is wrong.** A class named `LyricsRerankerTests` already exists at `Wavee.Tests/LyricsHostTests.cs:16`, and it calls `Reranker.Rank` (`:59-97`). A new `LyricsRerankerTests.cs` would declare the same type twice and fail to compile. Either extend the existing class or rename/move it, and give that to one agent.
2. **The existing KRC fixture is already decrypted.** `kugou-krc-caribbean-queen.krc` is plain text starting `[id:$…][ti:…]`. "Existing fixture decrypts and parses" cannot work. An encrypted KRC fixture has to be made (XOR + zlib in the test).
3. **The inspector has no table, and the pure class has a different name.**
   - `Screens/Diagnostics.cs:475-482` is `RawSources` and `:583-609` is the text export. The provider cards are in `Screens/Diagnostics.UI.cs:1392-1440` (`ProviderCard`), inside a 492 px dialog (`:1213`), so "Match/Recall/Support columns" does not fit.
   - The pure class is `Diagnostics.LyricsReport`, not `LyricsInspectorRules`.
   - A second surface, `Lyrics.UI.cs:2080-2175` (`SourceRow`, the in-lyrics debug panel), is missing from W3.
   - New labels need `Strings.*` entries, because the UI text goes through `Loc.Get`.
4. **The gold short-circuit already waits for the reference.** `Lyrics.Host.cs:1179-1180` checks `collected.ContainsKey(ref) || !HasPending(...)`, and the same check is at `:1343`. Item 8 describes this as new work.
5. **"Always-on Log lines" is wrong as written.** Per-source and search lines are `Log.Debug` (`:1540-1545`), and `Log.MinLevel` defaults to Info (`Platform.cs:694`). They never reach the file. Only `LogDecision` (`:1557`) is Info. The new `source=`/`breaker` lines must be `Log.Info`.
6. **NFKC may not work in this build.** `Directory.Build.props:13` sets `InvariantGlobalization=true`, and nothing in the app calls `string.Normalize(NormalizationForm…)` today. NFKC behaviour under invariant mode with NativeAOT is unverified. The safer route is a hand-written width fold (FF01–FF5E, half-width kana), the approach `Query.Normalize` already takes.
7. **Small citation errors.** `ParseLrc` is at `Lyrics.cs:1596`, not `:1635`. The 74% / 34-of-46 / 922-of-1009 field statistics are not reproducible from the repo.

**Missing pieces**

- **ISRC is usually absent.**
  - `ResolveRequest` ensures only `TrackFields.Identity` (`Lyrics.Host.cs:1801`).
  - `RequestFrom` fills `Isrc` only if it is already known (`Lyrics.cs:1428`).
  - 0.2.9 resolved the full track specifically to get the ISRC (`LiveSessionHost.cs` ~1262).
  - Without it, Musixmatch's ISRC path (`Confidence = 1`, gold) is mostly inert. The plan needs an `Ensure(…Isrc)` or a short wait.
- **Replays will re-query everything every session.** `UpgradeDiskHitAsync` (`:1102`, `:1255`) runs a full fan-out on every disk hit with Richness < 3, and today most cached tracks are Spotify line lyrics. `SourceGuard` is memory-only, so once grey ships, each of those tracks re-queries all 4 grey sources every session, indefinitely. The per-source miss state needs to be persisted, for example in the envelope.
- **Refetch will not reach a guarded source.** `RefetchAsync` (`:1512-1522`) clears only memory and disk. `SourceGuard` misses (and AMLL's `_misses`) survive, so the inspector's Refetch never re-queries a grey source. Add `ISource.Forget(trackId)`.
- **Musixmatch subtitle fallback is missing.** 0.2.9 fell back to the LRC `track.subtitles.get` in the same macro response when richsync was missing or unsingable (GreySources `:420-439`). The plan ports only richsync, and the existing decoy fixture is an LRC subtitle, so that decoy test has no code path to run through.
- **NetEase risk control is not handled.** Its JSON `code` (-460 / -462 on HTTP 200) is not listed as a failure mode or a breaker trigger.
- **The HTTP layer drops bodies on errors.** `HttpFetch.GetAsync` keeps the body only on 2xx (`:139`), so a transport-level 401/402 from Musixmatch loses its `hint`. The grey client should keep bodies on non-2xx.
- **The grey client must be wrapped in `Wire.Handler`.** Otherwise it bypasses connection attribution, storm detection and capture (`Platform.Wire.cs:117`). With the ladder, 40 calls per minute to one endpoint (`StormCalls`) is reachable while skipping tracks.
- **Timeouts and cancellations must not trip the breaker.** `FetchOne`'s `CancelAfter` (`:1572`) arrives inside the source as an OCE, indistinguishable from the gold cancellation (`srcCts.Cancel`). `SourceGuard.Failure` must never count an OCE.
- **Consensus lets the reference verify itself.** The consensus reference is itself a candidate, and comparing it to itself gives text = 1 and timing = 1. That repeats Spotify's existing self-compare but without Spotify's identity guarantee, so a 1-support document promotes itself. Exclude it or mark it specially. Wiring also sits in the Host (`ReferenceOf` / `TrimEdgesAgainstReference`, `:1225-1250`), not in `Lyrics.cs`.
- **The `Text.Normalize` change reaches further than the plan says.**
  - `CreditRules.Tokens` (`Lyrics.cs:2742`)
  - `IsInstrumental` (`:2566`)
  - `LyricsParserTests.cs:593-594`, which is pinned
  - `Diagnostics.cs:549`, which uses `WordCount`
  - `TrimUnalignedEdges`

  No wave owns the affected tests.
- **The per-source toggle cannot change at runtime.** `Enabled` is filtered once in the `Aggregator` constructor (`:985-987`). A Settings toggle needs a rebuild or `ClearCache` path, and the plan does not say which.
- **Other gaps:**
  - The capture budget is shared (`Probe.MaxTotalChars` 640k, 6 payloads per source), so kugou's ladder and a large AMLL payload can crowd later sources out.
  - The evidence bundle (`Diagnostics.Host.cs:581`) and the text export's hard-coded `prior × .05` formula (`Diagnostics.cs:596`) change when prior becomes `Prior*Confidence`.
  - OpenCC's `TSCharacters` contains non-BMP characters and one-to-many entries that a `FrozenDictionary<char,char>` cannot hold.
  - QRC encrypt for the round-trip test needs the triple-DES order inverted; only decrypt exists in 0.2.9 `LyricCrypto.cs`.

**Ordering and dependency mistakes**

- **W1 agents depend on each other inside the same wave.**
  - C's ladder (`Lyrics.Grey.cs`) calls `MetadataMatch.Pick` from B.
  - C's `SourceTrace` and D's `Candidate` both use `MatchBand` from B.

  With no builds between waves, signature drift surfaces only at the end. Freeze `MatchBand`, `Hit`, `MatchScore`, the `Candidate`/`Decision` fields and the source constructor signatures verbatim in the plan, or give the shared types to one agent.
- **W2 has the same problem.** F registers E1/E2's constructors in `Boot` while they are being written. Only frozen constructor signatures avoid a mismatch.
- **D's `Text.Normalize` change breaks tests nobody owns** (`LyricsParserTests`, `LyricsRulesTests` for credit rules). Assign them.
- **W3-G's targets are wrong.** Use `Diagnostics.UI.cs`, the `Strings` files and `Lyrics.UI.cs:2143`, not `Diagnostics.cs` alone.

**Top 5 risks**

1. **A traffic storm from replaying the disk cache.** 922 cached line documents times 4 grey sources, re-queried every session, with no persistent miss. Expect Musixmatch captchas and NetEase risk control within days.
2. **Musixmatch as gold.** An ISRC richsync match triggers `IsGold` (`:996`) and cuts the fan-out short. Combined with the new offset rules and a decoy that is not LRC (the gate at `:1590-1597` is duration/uniformity only), a wrong Musixmatch document can win outright.
3. **The CJK fold changing credit trimming and English picks,** plus the NFKC failure mode under invariant globalization in a NativeAOT Release build that no Debug test will catch.
4. **Consensus self-verification.** A lone wrong-song karaoke becomes its own reference when Spotify has no lyrics, which is exactly the failure item 5 is meant to prevent.
5. **Parallel W1/W2 agents writing against types that do not exist yet,** with no intermediate build. The duplicate `LyricsRerankerTests` class alone guarantees a failed end build.

---

# APPROVED (owner, 2026-09-29) + FROZEN CONTRACTS — implementation reads THIS section first

Owner decisions: all four sources ON BY DEFAULT (no setting, no switch — `EnableGreyProviders` is deleted); Musixmatch
INCLUDED with the 0.2.9 desktop profile (+ the LRC `track.subtitles.get` fallback); scope = port + reranker v2
(translations/bilingual W3-H deferred); budgets PerSourceTimeoutMs 12000 / TotalTimeoutMs 15000 / FirstHitGraceMs 2000;
synthetic lyric text in all new fixtures.

## Critic corrections folded in (these override the plan above)
1. Reranker tests: `LyricsRerankerTests` already exists in `Wavee.Tests/LyricsHostTests.cs:16` — new reranker tests go in
   NEW file `Wavee.Tests/LyricsRerankerV2Tests.cs`, class `LyricsRerankerV2Tests` (never a second `LyricsRerankerTests`).
2. `kugou-krc-caribbean-queen.krc` is ALREADY decrypted text — crypto tests build encrypted inputs themselves with the
   test-only encrypt helpers below.
3. The inspector is CARDS: `Screens/Diagnostics.UI.cs` `ProviderCard` (~1392-1440, 492-px dialog) + the pure
   `Diagnostics.LyricsReport` + the text export in `Screens/Diagnostics.cs` (~583-609, incl. the hard-coded `prior × .05`
   formula → `Prior × Confidence × WPrior`) + the in-lyrics debug panel `Lyrics.UI.cs` `SourceRow` (~2080-2175). New
   labels go through `Strings.*` / `Loc.Get`. No table columns — add lines to the card.
4. The gold short-circuit ALREADY waits for the reference (`Lyrics.Host.cs:1179-1180, :1343`) — don't re-add it.
5. New per-source / breaker / band lines are `Log.Info` (Debug never reaches the file; `[lyrics]` category).
6. NO `string.Normalize(NFKC)` (InvariantGlobalization=true + NativeAOT): comparison fold is hand-written — full-width
   FF01–FF5E → ASCII, half-width katakana FF66–FF9D → full-width, lowercase (ToLowerInvariant), T→S via the embedded
   table, katakana 30A1–30F6 → hiragana (−0x60).
7. OpenCC `TSCharacters`: keep only BMP, single-char→single-char pairs (drop non-BMP and one-to-many) → `FrozenDictionary<char,char>`.
8. ISRC: `ResolveRequest` also ensures `TrackFields.Isrc` with a bounded wait (≤ 1500 ms after Identity; proceed without it).
9. Persist per-source misses: `SourceGuard` misses survive sessions (small AOT-safe JSON under `%LOCALAPPDATA%\Wavee\lyrics\source-misses.json`,
   source-gen context, capped 5000 entries, TTL-pruned on load). Otherwise every cached line-lyrics track re-queries all
   4 grey sources every session (traffic storm → captchas / risk control).
10. `ISource.Forget(string trackId)` (default no-op interface method); `RefetchAsync` calls it on every source (AMLL clears `_misses`).
11. Musixmatch subtitle fallback (0.2.9 GreySources :420-439) is ported.
12. NetEase JSON `code` -460/-462 (on HTTP 200) → `Trip(30 min, "risk control")`.
13. The grey client KEEPS bodies on non-2xx (Musixmatch 401 `hint`), is wrapped in `Wire.Handler("lyrics-grey", …)`, `UseCookies=false`.
14. `OperationCanceledException` NEVER counts as a `SourceGuard.Failure` (timeouts and gold-cancel both arrive as OCE).
15. Consensus reference never verifies itself: a candidate chosen as the consensus reference is verified only by
    `support >= 1` from a DIFFERENT provider; its self-compare is excluded. Wiring is in the Host (`ReferenceOf` /
    `TrimEdgesAgainstReference` ~1225-1250).
16. The fold change owns its blast radius: the agent changing `Text.Normalize`/`Tokens`/`WordCount` updates
    `LyricsParserTests.cs:593-594`, credit-rule tests (`LyricsRulesTests`), `Diagnostics.cs:549` (WordCount) and
    `TrimUnalignedEdges` expectations as needed, keeping every English expectation green.
17. Musixmatch as gold: an ISRC richsync hit is gold ONLY if it also passes the decoy gates AND (reference absent OR
    text agreement ≥ 0.5 with the reference) — never gold on basis alone.
18. Capture budget: grey sources capture only the FINAL lyric payload (not every search page) so they cannot crowd out others.

## Frozen C# contracts (all nested in `public static partial class Lyrics`, namespace `Wavee`)

```csharp
// ── Shell/Lyrics.Match.cs (W1-B) ─────────────────────────────────────────────────────────────────
public enum MatchBand : byte { None, VeryLow, Low, Medium, PrettyHigh, High, VeryHigh, Perfect }
public readonly record struct Hit(string Id, string Title, IReadOnlyList<string> Artists, string? Album,
                                  long DurationMs, int DurationGrainMs);
public readonly record struct MatchScore(MatchBand Band, double Confidence, double Title, double Artist,
                                         double Album, double Duration, string Reason);
public static class MetadataMatch
{
    public static MatchScore Score(Request req, in Hit hit);
    /// <returns>index of the best hit at or above <paramref name="floor"/>, or -1.</returns>
    public static int Pick(Request req, IReadOnlyList<Hit> hits, MatchBand floor, out MatchScore best);
    /// <summary>"a1b2 VeryHigh title=7 artist=7 dur=+320ms" — the inspector/log breadcrumb.</summary>
    public static string Describe(in Hit hit, in MatchScore s);
}
public static class VersionMarkers { public static bool Mismatch(string requestTitle, string hitTitle, out string marker); }
public static class CjkFold { public static string Fold(string s); public static bool IsCjk(char c); }

// ── Shell/Lyrics.Crypto.cs (W1-A) ────────────────────────────────────────────────────────────────
public static class Crypto
{
    public static string? DecryptKrc(byte[] data);        // "krc1" header + xor key + zlib; null on any failure
    public static string? DecryptQrc(string hexCipher);   // hex → 3DES(Lyricify DESHelper) → zlib; null on failure
    public static byte[] EncryptKrcForTests(string text); // inverse, used by tests only
    public static string EncryptQrcForTests(string text); // inverse (triple-DES order inverted), tests only
}
public static class QrcXml { public static (string? Orig, string? Ts, string? Roma) Split(string xml); }  // (W1-A)

// ── Shell/Lyrics.cs (W1-D) — additive, existing positional ctors unchanged ──────────────────────
public sealed record Candidate(string ProviderId, double Prior, MatchBasis Basis, Doc Document)
{   // existing members kept
    public double Confidence { get; init; } = 1.0;
    public MatchBand Band { get; init; } = MatchBand.Perfect;
    public string? MatchNote { get; init; }
}
public sealed record Decision(
    string ProviderId, SyncKind Sync, double Score,
    double TextAgreement, double Coverage, double TimingScore, long AppliedOffsetMs, string Reason,
    double SyncScore = 0d, bool Verified = false,
    double Recall = 0d, int Support = 0, bool OffsetCapped = false, string TieBreak = "");
// Reranker: public static Ranked Rank(IReadOnlyList<Candidate> cs, Doc? reference) keeps its signature;
// new: public static Doc? ConsensusReference(IReadOnlyList<Candidate> cs, out int[] support, out int referenceIndex);

// ── Shell/Lyrics.Host.cs + Shell/Lyrics.Grey.cs (W1-C) ───────────────────────────────────────────
public interface IHttpPost : IHttpWithStatus
{
    Task<HttpResult> PostAsync(string url, HttpContent body, IReadOnlyDictionary<string, string>? headers, CancellationToken ct);
}
// HttpFetch : IHttpPost ; public static HttpFetch Grey { get; }  (browser UA set once, keeps non-2xx bodies)
public interface ISource { /* existing */ void Forget(string trackId) { } }
public static partial class Sources { /* existing Amll / SpotifyNative / LrcLib; grey sources add partial files */ }
public sealed record SourceTrace(/* existing … */ double SyncScore = 0d,
    MatchBand Band = MatchBand.None, double Confidence = 0d, string Match = "", double Recall = 0d, int Support = 0);
public sealed class SourceGuard
{
    public enum MissKind : byte { SongNotFound, NoLyricForSong }
    public SourceGuard(string sourceId, Func<long> nowMs);
    public string SourceId { get; }
    public static long Ttl(MissKind k);                               // 6 h / 24 h
    public bool TryGetMiss(string trackId, out MissKind kind, out long ageMs);
    public void Miss(string trackId, MissKind kind);                  // persisted (correction 9)
    public void Forget(string trackId);
    public bool IsOpen(out string reason, out long untilMs);
    public void Trip(string reason, long forMs);
    public void Failure(string reason);                               // never called for OCE; 3 in a row → open 10 min
    public void Success();
    public static SourceGuard For(string sourceId);                   // process-wide registry, wall clock, persistence
}
public static class GreyLadder
{
    /// Walks Query.Variants(req); pools hits across variants (dedup by Id, keep best band); stops early on
    /// Perfect/VeryHigh; returns up to 2 hits ≥ Medium, best first, each with its MatchScore; notes the top 3.
    public static Task<IReadOnlyList<(Hit Hit, MatchScore Score)>> SearchAsync(string sourceId, Request req,
        Func<string /*keyword*/, CancellationToken, Task<IReadOnlyList<Hit>?>> search, CancellationToken ct);
    public const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Wavee/1.0";
}

// ── grey sources (W2-E1/E2) — nested in `Sources`, one partial file each ──────────────────────────
public sealed class Kugou : ISource      { public Kugou(IHttpPost http); }       // Id "kugou",      Prior 0.50
public sealed class Qq : ISource         { public Qq(IHttpPost http); }          // Id "qq",         Prior 0.55
public sealed class Netease : ISource    { public Netease(IHttpPost http); }     // Id "netease",    Prior 0.50
public sealed class Musixmatch : ISource { public Musixmatch(IHttpPost http); }  // Id "musixmatch", Prior 0.70
// Each: Enabled => true; uses SourceGuard.For(Id); GreyLadder for search; Candidate { Confidence, Band, MatchNote } set;
// Probe.Note at every gate; Log.Info("lyrics", "source=<id> band=… conf=… dur=…"); CaptureRaw only the final payload.
```

## Waves (disjoint files; the orchestrator alone builds/tests after each wave)
- **W1** A: `Lyrics.Crypto.cs` + `Wavee.Tests/LyricsCryptoTests.cs` · B: `Lyrics.Match.cs`, `Lyrics.CjkTable.cs` +
  `Wavee.Tests/LyricsMatchTests.cs` · C: `Lyrics.Host.cs` (HTTP seam, grey client, Options defaults, delete
  EnableGreyProviders + fix headers :29-30/:43-46, SourceTrace fields, `Sources` → partial, `ISource.Forget`,
  ResolveRequest ISRC wait, Info logs) + `Lyrics.Grey.cs` (SourceGuard + persistence, GreyLadder) +
  `Wavee.Tests/LyricsGreyInfraTests.cs` · D: `Lyrics.cs` (Candidate/Decision fields, fold/Tokens/WordCount/incoherence
  gate, recall, offset guard, consensus, prior×confidence, deterministic tiebreak, Musixmatch-gold guard helper) +
  `Wavee.Tests/LyricsRerankerV2Tests.cs` + the blast-radius test updates (correction 16).
- **W2** E1: `Lyrics.Grey.Kugou.cs`, `Lyrics.Grey.Qq.cs` + `Wavee.Tests/LyricsGreyCnTests.cs` · E2:
  `Lyrics.Grey.Netease.cs`, `Lyrics.Grey.Musixmatch.cs` + `Wavee.Tests/LyricsGreyMxmTests.cs` (netease tests too) ·
  F: `Lyrics.Host.cs` aggregator (Boot registration of the 4, willContinue/provisional disk write, lrclib →
  `MetadataMatch.Pick`, Refetch → Forget, consensus wiring into ReferenceOf/TrimEdges excluding self-verify, gold guard)
  + `Wavee.Tests/LyricsHostTests.cs` extensions.
- **W3** G: `Screens/Diagnostics.UI.cs` (ProviderCard lines), `Screens/Diagnostics.cs` (LyricsReport + export formula),
  `Lyrics.UI.cs` SourceRow, `Strings` entries + tests of the pure report formatting.
