# Wave 2 — `Spotify.cs` + `Spotify.Session.cs`: the public surface (owner D)

Written 2026-09-12, for owners E and F. Everything below is `public` on `static partial class Spotify` (namespace
`Wavee`) and lives in `src/apps/Wavee/Spotify/Spotify.cs` (CORE, pure) or `Spotify.Session.cs` (SHELL, the sockets).
The list is verbatim: the signature here is the signature in the file.

Two rules the callers inherit:

* **CORE writes into your buffer.** Every routine that produces bytes or characters takes a `Span<…>` and returns the
  length. Nothing here allocates after warm-up except the four cases named in `Spotify.cs`'s header (two BigInteger
  folds, the RSA verify, and a gzipped dealer frame) — all once per login or once per push.
* **SHELL calls block, and never on the UI thread.** `AccessToken`, `RequestAudioKey` and `Reply` are for a shell
  thread (C9). `Login`, `Logout`, `Boot` and every signal read are safe from the UI thread.

---

## 1. `Spotify.cs` — CORE

### The session as a value

| Member | Notes |
|---|---|
| `enum SessionPhase : byte { Offline, Resolving, Connecting, Handshaking, Authenticating, Minting, Online, Reconnecting, Failed }` | Ordered ladder; only `Online` may build requests |
| `enum SessionFault : byte { None, NoCredential, CredentialRejected, NoAccessPoint, Network, Protocol, TokenRefused, NotPremium }` | Only `CredentialRejected` clears the stored credential |
| `enum Tier : byte { Unknown, Free, Premium }` | From the AP's 0x50 ProductInfo push; `Unknown` is treated as Free |
| `readonly record struct TokenRef(int Offset, int Length)` + `bool IsEmpty` | A slice of the session text arena; resolve with `Spotify.Utf8`/`Spotify.TextOf` |
| `struct Session` | Fields below; a VALUE — copy it, never hold a reference |
| `Session.Phase / Fault / Tier / Epoch / Attempt` | `Epoch` bumps whenever a shell must abandon in-flight work (C4) |
| `Session.Username / Country / Product` (`StringId`) | Interned on the UI thread; never resolve these off it (C1) |
| `Session.DeviceId / ClientId / Locale / AccessToken / ClientToken / ConnectionId / SpclientHost / DealerHost` (`TokenRef`) | Safe to read from any thread |
| `Session.AccessExpiresAtMs / ClientTokenExpiresAtMs` | Unix ms; 0 = none held |
| `Session.ClockOffsetMs / ClockRttMs / ClockSynced / ClockProbed` | The server-clock estimate |
| `Session.HasCredential` | A reusable credential is on disk |
| `readonly bool Session.IsOnline` | `Phase == Online` |
| `readonly bool Session.CanRequest(long nowMs)` | A bearer is held and unexpired |
| `enum SessionEventKind : byte { None, Login, Hosts, Connected, HandshakeOk, Welcome, AuthRejected, ClientTokenMinted, AccessTokenMinted, DealerOnline, Dropped, Retry, Logout }` | |
| `readonly record struct SessionEvent(SessionEventKind Kind, StringId Id, StringId Id2, StringId Id3, TokenRef Text, TokenRef Text2, long Number, bool Flag, uint Epoch)` | All arguments optional |
| `[Flags] enum SessionEffects : uint { None, ResolveHosts, OpenAp, MintClientToken, MintAccessToken, OpenDealer, SaveCredential, ClearCredential, Backoff, CloseAll }` | |
| `static SessionEffects Step(ref Session s, in SessionEvent e)` | THE state machine; pure |
| `static int BackoffMs(in Session s)` | 3 / 6 / 12 / 24 s, capped at 30 |

### Server clock

| Member | Notes |
|---|---|
| `const long ClockDriftTriggerMs = 2000` | |
| `static bool ObservePassiveClock(ref Session s, long serverTimestampMs, long localNowMs)` | The free per-cluster sample; `true` ⇒ re-probe |
| `static void ObserveClockProbe(ref Session s, long t1, long serverMs, long t2, ref long bestRtt)` | One NTP-style sample; keep the lowest RTT of a round |
| `static long ServerNowMs(in Session s, long localNowMs)` | 0 = unsynced sentinel |

### Shannon and the AP codec

| Member | Notes |
|---|---|
| `struct Shannon` : `Shannon(ReadOnlySpan<byte> key32)`, `void Nonce(uint)`, `void Encrypt(Span<byte>)`, `void Decrypt(Span<byte>)`, `void Finish(Span<byte> mac4)`, `bool CheckMac(ReadOnlySpan<byte> mac4)` | Mutable struct; pass by `ref` — a copy forks the keystream |
| `struct ApCodec` : `ApCodec(ReadOnlySpan<byte> sendKey32, ReadOnlySpan<byte> receiveKey32)` | |
| `static int ApCodec.FrameLength(int payloadLength)` | `3 + payload + 4` |
| `int ApCodec.Encode(byte cmd, ReadOnlySpan<byte> payload, Span<byte> dst)` | Returns the frame length |
| `(byte Cmd, int Length) ApCodec.BeginDecode(Span<byte> header3)` | Decrypts the header IN PLACE; advances the receive nonce |
| `bool ApCodec.EndDecode(Span<byte> payload, ReadOnlySpan<byte> mac4)` | `false` ⇒ drop the connection |

### `static class Handshake`

| Member | Notes |
|---|---|
| `const int PrivateKeySize = 95`, `const int ModulusSize = 96` | |
| `const byte CmdLogin/CmdApWelcome/CmdAuthFailure/CmdPing/CmdPong/CmdPongAck/CmdAesKeyRequest/CmdAesKey/CmdAesKeyError/CmdLocalePreamble/CmdPreferredLocale/CmdCountryCode/CmdProductInfo` | `0xAB 0xAC 0xAD 0x04 0x49 0x4A 0x0C 0x0D 0x0E 0x0F 0x74 0x1B 0x50` |
| `static int PublicKey(ReadOnlySpan<byte> priv, Span<byte> pub)` | The caller supplies the private key (CSPRNG in the shell) |
| `static int SharedSecret(ReadOnlySpan<byte> priv, ReadOnlySpan<byte> remotePublic, Span<byte> secret)` | Left-padded to 96 bytes |
| `static bool VerifyGs(ReadOnlySpan<byte> gs, ReadOnlySpan<byte> signature)` | RSA-SHA1 against Spotify's AP key; fails closed |
| `static void DeriveKeys(ReadOnlySpan<byte> sharedSecret, ReadOnlySpan<byte> accumulator, Span<byte> challenge20, Span<byte> sendKey32, Span<byte> receiveKey32)` | |
| `static int WriteHelloFrame(ReadOnlySpan<byte> proto, Span<byte> dst)` | `[00][04][size:4 BE][proto]` |
| `static int WriteResponseFrame(ReadOnlySpan<byte> proto, Span<byte> dst)` | `[size:4 BE][proto]` |
| `static int ApResponseBodyLength(ReadOnlySpan<byte> size4)` | 0 = out of range (untrusted, pre-auth) |
| `static int WritePreferredLocale(ReadOnlySpan<byte> language2, Span<byte> dst)` | The 0x74 body |

### Proof of work, PKCE, hex

| Member | Notes |
|---|---|
| `static long Hashcash.Solve(ReadOnlySpan<byte> context, ReadOnlySpan<byte> prefix, int target, Span<byte> suffix16)` | The caller SEEDS `suffix16`; the fold increments it. Returns the hash count |
| `static int Hashcash.LeadingZeroBits(ReadOnlySpan<byte> hash)` | |
| `static void Pkce.NewVerifier(Span<char> verifier)` | 43..128 chars, rejection-sampled |
| `static int Pkce.Challenge(ReadOnlySpan<char> verifier, Span<char> challenge)` | base64url(SHA256), unpadded |
| `static int Pkce.Base64Url(ReadOnlySpan<byte> data, Span<char> dst)` | |
| `static int Hex.Encode(ReadOnlySpan<byte> bytes, Span<char> dst)` | lowercase |
| `static bool Hex.TryDecode(ReadOnlySpan<char> hex, Span<byte> dst)` | |

**base62 is NOT here.** Entity ids are `Entities/Entities.cs`'s `Base62` (`TryDecode`/`Encode` over `UInt128`) and
`EntityId.WriteGid(Span<byte> dst16)`; this file deliberately carries no second copy. Hex is only for file ids.

### The dealer frame

| Member | Notes |
|---|---|
| `enum DealerFrameKind : byte { Unknown, Ping, Pong, Message, Request }` | |
| `readonly ref struct DealerMessage` with `Kind`, `ReadOnlySpan<byte> Uri / Ident / Key / ConnectionId / Payload`, `bool Truncated`, `bool IsPing`, `bool IsRequest`, `bool IsClusterUpdate` | `Payload` is base64-decoded and gunzipped already |
| `static DealerMessage DealerFrame.Parse(ReadOnlySpan<byte> utf8, byte[] scratch)` | `scratch` is the caller's reusable buffer; 256 KiB is what the session uses |
| `static int DealerFrame.WritePing(Span<byte> dst)` / `WritePong(Span<byte> dst)` | |
| `static int DealerFrame.WriteReply(Span<byte> dst, ReadOnlySpan<byte> key, bool ok)` | The key is JSON-escaped |

### The audio-key packets

| Member | Notes |
|---|---|
| `const int AudioKey.RequestLength = 42`, `const int AudioKey.KeyLength = 16` | |
| `static int AudioKey.WriteRequest(ReadOnlySpan<byte> fileId, ReadOnlySpan<byte> trackGid, uint seq, Span<byte> dst)` | |
| `static bool AudioKey.TryReadKey(ReadOnlySpan<byte> payload, out uint seq, Span<byte> key16)` | 0x0d |
| `static bool AudioKey.TryReadError(ReadOnlySpan<byte> payload, out uint seq, out int code)` | 0x0e |

### The request fold — what `Spotify.Api.cs` is built on

| Member | Notes |
|---|---|
| `enum Verb : byte { Get, Post, Put, Delete, Patch }` | |
| `enum ApiHost : byte { Spclient, SpclientWg, Pathfinder, Login5, ClientToken, ApResolve, Dealer }` | `Spclient` ⇒ `Spotify.SpclientBaseUrl()`; `Pathfinder` ⇒ `https://api-partner.spotify.com` |
| `[Flags] enum HeaderSet : uint { None, Bearer, ClientToken, Identity, AcceptLanguage, AcceptProtobuf, AcceptJson, ContentProtobuf, ContentJson, ContentForm, GzipBody, ConnectionId, NoStore, ApplyLenses, AppliedLenses, AcceptListItems, AcceptGeoblock, DsaMode, Origin, PathfinderDesktop, PathfinderWeb }` | The runner turns each flag into a header; `Identity` = App-Platform + Spotify-App-Version + User-Agent |
| `enum RequestKind : byte { ExtendedMetadata, Pathfinder, PlaylistRead, PlaylistDiff, PlaylistChanges, PlaylistCreate, PlaylistSignals, RootlistRead, RootlistChanges, RecentsPage, RecentsDiff, CollectionPage, CollectionDelta, CollectionWrite, ConnectStatePut, ConnectStateTransfer, ConnectStateCommand, ConnectStateVolume, ContextResolve, Autoplay, StorageResolve, ServerTime, Profile, Popcount, Custom }` | `Custom` takes `Args.Path/Host/Verb/Headers` verbatim |
| `ref struct RequestArgs` with writable `ReadOnlySpan<char> Id, Id2, Path`, `ReadOnlySpan<byte> Body`, `long Number`, `bool Flag`, `ApiHost Host`, `Verb Verb`, `HeaderSet Headers` | Object-initializer shaped |
| `readonly ref struct Request` with `Verb Verb`, `ApiHost Host`, `ReadOnlySpan<char> Path`, `HeaderSet Headers`, `ReadOnlySpan<byte> Body`, `ReadOnlySpan<char> SyncReason`, `bool IsEmpty` | `Path` points into the buffer you passed |
| `static Request Build(in Session s, RequestKind kind, in RequestArgs args, Span<char> path)` | Pure; a 512-char path buffer covers every route |
| `ref struct PathWriter(Span<char> buffer)` with `Written`, `Append(ReadOnlySpan<char>)`, `Append(long)`, `AppendEscaped(ReadOnlySpan<char>)` | For a `Custom` path built without a string |

The routes `Build` produces, for reference (`{id}` = `Args.Id`, `{id2}` = `Args.Id2`):

```
ExtendedMetadata      POST /extended-metadata/v0/extended-metadata          protobuf in/out
Pathfinder            POST /pathfinder/v2/query                             json; Flag = web-player platform
PlaylistRead          GET  /playlist/v2/playlist/{id}
PlaylistDiff          GET  /playlist/v2/playlist/{id}/diff?revision={id2}
PlaylistChanges       POST /playlist/v2/playlist/{id}/changes               form content-type, sync reason CAk=
PlaylistCreate        POST /playlist/v2/playlist/{id}/changes               form content-type, sync reason CAw=
PlaylistSignals       POST /playlist/v2/playlist/{id}/signals               sync reason CA8QAQ==
RootlistRead          GET  /playlist/v2/user/{id}/rootlist?decorate=revision
RootlistChanges       POST /playlist/v2/user/{id}/rootlist/changes          sync reason CAk=
RecentsPage           GET  /playlist/v2/list/recents/page                   sync reason CAwQAQ==
RecentsDiff           GET  /playlist/v2/list/recents/page/diff              sync reason CAEQAQ==, applied lenses
CollectionPage        POST /collection/v2/paging
CollectionDelta       POST /collection/v2/delta
CollectionWrite       POST /collection/v2/write
ConnectStatePut       PUT  /connect-state/v1/devices/{id}                   connection-id header, gzipped body
ConnectStateTransfer  POST /connect-state/v1/connect/transfer/from/{id}/to/{id2}
ConnectStateCommand   POST /connect-state/v1/player/command/from/{id}/to/{id2}
ConnectStateVolume    POST /connect-state/v1/connect/volume/from/{id}/to/{id2}
ContextResolve        GET  /context-resolve/v1/{id}
Autoplay              POST /context-resolve/v1/autoplay | /autopodcast      Flag = podcast
StorageResolve        GET  /storage-resolve/files/audio/interactive/{id}    id = the file id hex
ServerTime            GET  /melody/v1/time
Profile               GET  /user-profile-view/v3/profile/{id}
Popcount              GET  /popcount/v2/playlist/{id}/count
```

### Two small wire parses

| Member | Notes |
|---|---|
| `static int ParseHosts(ReadOnlySpan<byte> json, ReadOnlySpan<byte> key, Span<Range> into)` | apresolve; ranges into the json span |
| `static (int HostLength, int Port) SplitHostPort(ReadOnlySpan<byte> entry, int fallbackPort)` | |
| `static ReadOnlySpan<byte> ProductXml.Value(ReadOnlySpan<byte> xml, ReadOnlySpan<byte> element)` | The 0x50 blob, no XML DOM |
| `static Tier ProductXml.TierOf(ReadOnlySpan<byte> xml)` | |

---

## 2. `Spotify.Session.cs` — SHELL

### The one seam

| Member | Notes |
|---|---|
| `static Action<Action> Post { get; set; }` | Defaults to inline; `App.cs` sets it to `AppHost.Post`, like `Store.Post`. Wave 4 wires it |

Everything else this file needs comes from `Platform` by a **direct call** — there are no delegates to install (§4).

| Member | Notes |
|---|---|
| `enum CredentialKind : byte { None, ReusableBlob, OAuthToken }` | Numbered to match `Platform.CredentialKind`; the map is a cast |
| `readonly record struct Credential(CredentialKind Kind, string Username, string Secret)` + `bool IsEmpty` | The session's login input. `Platform.Credential` is the persisted one (it also carries login5's unread `Refresh`) |

### Identity (constants, for the `Identity` header set)

`Identity.ClientId` · `Identity.AppVersion` · `Identity.ClientVersion` · `Identity.WebPlayerAppVersion` ·
`Identity.AppPlatform` · `Identity.UserAgent` · `Identity.ClientTokenUserAgent`

### Lifecycle and state

| Member | Notes |
|---|---|
| `static void Boot()` | Statics + signals, once. No login, no socket. `App.cs` calls it |
| `static void Login()` | Starts a session with the stored credential; returns immediately |
| `static void Logout()` | Tears the epoch down, forgets the tokens, wipes the credential |
| `static Session Current { get; }` | A whole-transition snapshot; safe from any thread |
| `static Signal<SessionPhase> Status { get; }` | UI-thread signal |
| `static Signal<SessionFault> Fault { get; }` | UI-thread signal |
| `static ReadOnlySpan<byte> Utf8(TokenRef r)` | The bytes behind a session `TokenRef` |
| `static string TextOf(TokenRef r)` | …as a string (allocates) |

The device id is `Platform.DeviceId` (there is no second name for it) and the two-letter language is
`Platform.Locale.SpotifyLanguage`.

### What the request runner needs

| Member | Notes |
|---|---|
| `static string? AccessToken(bool force = false)` | BLOCKS while minting. `force` is the 401 path |
| `static string? ClientToken()` | The attestation header value, or null |
| `static string SpclientBaseUrl()` | `https://<resolved host>`, falling back to `spclient.wg.spotify.com` |
| `static string ConnectionId()` | The `X-Spotify-Connection-Id` a PutState must carry |

### Audio keys and the dealer

| Member | Notes |
|---|---|
| `enum AudioKeyResult : byte { Ok, Offline, Timeout, Rejected, Busy }` | `Busy` = the 32-slot table is full (C8): fall back, do not retry in a loop |
| `static AudioKeyResult RequestAudioKey(ReadOnlySpan<byte> fileId, ReadOnlySpan<byte> trackGid, Span<byte> key16, int timeoutMs = 5000)` | BLOCKS; shell threads only |
| `static void Reply(ReadOnlySpan<byte> key, bool ok)` | Ack a dealer REQUEST — call it from `Connect.OnDealer` |
| `static void ObserveClusterTimestamp(long serverTimestampMs)` | Feed the free clock sample every cluster carries |
| `const int DealerPingIntervalMs = 30_000` · `const int DealerDeadAfterMs = 70_000` · `const int ServerClockResyncMs = 600_000` | The three timers, all named (P10) |

### Threads this file owns

`wavee-spotify-ap` (resolve → connect → handshake → login → mint → pump), `wavee-spotify-dealer` (the websocket
receive loop) and `wavee-spotify-keepalive` (the 30 s ping, the half-open watchdog, the 10-minute clock re-probe).
All three are background threads scoped to one epoch; `Session.Epoch` bumping means every answer still in flight is
dropped rather than applied (C4).

---

## 3. What owner F must provide (this file calls it)

```csharp
// src/apps/Wavee/Spotify/Spotify.Connect.cs — owner F, plan §4.10 verbatim
internal static void OnDealer(ReadOnlySpan<byte> frame)
```

The dealer receive loop answers `ping`, takes the connection id off the pusher hello, and hands EVERY other frame to
`Connect.OnDealer(frame)`. Parse it with `Spotify.DealerFrame.Parse(frame, yourScratch)` — the parse is pure and the
loop's own scratch is not shared. Ack a REQUEST with `Spotify.Reply(msg.Key, ok: true)` and feed the cluster's
`server_timestamp_ms` to `Spotify.ObserveClusterTimestamp` so the ownership fence has a server clock (C5).

**Until `Spotify.Connect.OnDealer` exists, `Spotify.Session.cs` does not compile.** That is the one cross-file
dependency in Wave 2's D/F pair, and it is the plan's own shape.

## 4. What comes from `Platform` — wired internally, nothing to install

`Platform` landed, so the temporary delegate seams are **gone**. `Spotify.Session.cs` calls it directly, and the
dependency is one-way: `Platform.Boot()` runs before `Spotify.Boot()`, and Platform never calls into Spotify.

| Used for | Call |
|---|---|
| The stored credential | `Platform.TryLoadCredential(out var c)` → `new Spotify.Credential((Spotify.CredentialKind)(byte)c.Kind, c.Username, c.Secret)` |
| Persisting the welcome's reusable blob | `Platform.SaveCredential(in Credential)` (`Refresh: null` — Wave 2 does not model login5's refresh token) |
| Logout / a genuine AP rejection | `Platform.ClearCredential()` |
| The Connect device id (AP login, login5, the PutState path) | `Platform.DeviceId` |
| The AP locale publish and `AcceptLanguage` | `Platform.Locale.SpotifyLanguage` |
| An account name in a log line | `Platform.Redact(username)` |
| Every log line | `Log.Info/Warn("spotify", …)` — one always-on stream, no sink of our own, never a token or a key |

The **only** thing `Platform` does not supply is the client-token attestation's `device_id`: the local-machine SID
prefix (`S-1-5-21-…`). Nothing else in the app wants it and nothing persists it, so a Platform member with one caller
would be surface for its own sake — `Spotify.Session.cs` keeps ONE private `MachineSid()` that reads
`HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList` and answers null on any failure or on a
non-Windows run, in which case the attestation takes `Platform.DeviceId` (0.2.9's own fallback).

## 5. Tests

`src/apps/Wavee.Tests/SpotifyCoreTests.cs` (Shannon vectors, the AP codec, the handshake fold, hashcash, PKCE, hex,
ProductXml, apresolve, the server clock), `DealerFrameTests.cs` (the frame parse and the audio-key packets) and
`SpotifySessionTests.cs` (`Step`, `Build`, and `SessionCredentialTests` — the credential slot from the session's
side, against a real `FileLocalStore` in a temp directory with a `NoOpProtector`, joining `PlatformCollection`
because the ambient slot is process state). None of them opens a socket, boots a `Scope` or reads production
source.
