# Wavee — core Spotify protocol library

Reverse-engineered Spotify client for .NET. Authentication, the Access Point connection, Spotify Connect, metadata, playlists, audio key fetching, playback orchestration. **No UI.**

Targets `net10.0`. AOT-compatible (`EnableTrimAnalyzer`, `EnableAotAnalyzer`, `EnableSingleFileAnalyzer` all on; trim/AOT warnings treated as errors).

## What it does

This is a clean‑room implementation of the protocols Spotify's official desktop client speaks:

| Layer            | What                                                                                                                          |
|------------------|-------------------------------------------------------------------------------------------------------------------------------|
| **AP**           | TCP+TLS connection to a Spotify Access Point with Diffie-Hellman handshake and Shannon stream cipher (`Core/Crypto/`).        |
| **Mercury**      | Legacy request/response protocol multiplexed over the AP TCP connection (`Core/Mercury/MercuryManager.cs`).                   |
| **Dealer**       | WebSocket message bus for Spotify Connect (cluster state, transfer, volume, remote commands) — `Connect/`.                    |
| **SpClient**     | Modern protobuf-over-HTTPS API at `spclient.wg.spotify.com` for playlists, tracks, lyrics, presence, metadata (`Core/Http/`). |
| **Pathfinder**   | GraphQL API at `api-partner.spotify.com` for search, browse, home, profile, color extraction (`Core/Http/Pathfinder/`).       |
| **Login5**       | OAuth-style device login backend (`Core/Http/Login5Client.cs`, `OAuth/`).                                                     |
| **Audio key**    | AES-128 key fetching for Ogg Vorbis decryption over the AP audio-key channel (`Core/Audio/`).                                 |
| **PlayPlay**     | Optional fallback key derivation (Spotify property; gitignored, see `Core/Audio/PlayPlayConstants.cs`).                       |
| **Events**       | Telemetry envelope and event types posted to `gabo-receiver-service/v3/events/` (`Connect/Events/`).                          |

Heavy reactive (`System.Reactive`) usage throughout — most state is exposed as `IObservable<T>`.

## Public entry points

| Type                     | File                                            | Role                                                                    |
|--------------------------|-------------------------------------------------|-------------------------------------------------------------------------|
| `Session`                | `Core/Session/Session.cs`                       | Anchor of everything: AP connect, authenticate, lazy-init subsystems.   |
| `ISession`               | `Core/Session/ISession.cs`                      | Public interface (use this in DI).                                      |
| `PlaybackOrchestrator`   | `Audio/PlaybackOrchestrator.cs`                 | Queue, context resolution, pagination, autoplay, playback metrics.      |
| `DealerClient`           | `Connect/DealerClient.cs`                       | Spotify Connect WebSocket. `IObservable<DealerMessage>` / `Requests`.   |
| `DeviceStateManager`     | `Connect/DeviceStateManager.cs`                 | Announce this device, sync volume, handle `PutState` requests.          |
| `PlaybackStateManager`   | `Connect/PlaybackStateManager.cs`               | Reactive view of remote cluster state (current track, playing, queue).  |
| `ConnectCommandClient`   | `Connect/ConnectCommandClient.cs`               | Send Connect commands (play, pause, seek, transfer) with ack tracking.  |
| `PlaylistCacheService`   | `Core/Playlists/PlaylistCacheService.cs`        | 24h hot cache + Mercury subscriptions for differential playlist updates.|
| `ISpClient` impl         | `Core/Http/SpClient.cs`                         | Protobuf SpClient binding (one method per endpoint).                    |
| `PathfinderClient`       | `Core/Http/PathfinderClient.cs`                 | GraphQL Pathfinder binding.                                             |
| `AudioKeyManager`        | `Core/Audio/AudioKeyManager.cs`                 | AES key fetch + cache, with optional disk persistence and PlayPlay fallback.|
| `Authenticator`          | `Core/Authentication/Authenticator.cs`          | Username+password / OAuth / stored credentials.                         |
| `CredentialsCache`       | `Core/Authentication/CredentialsCache.cs`       | DPAPI-encrypted credentials blob persistence.                           |

`Session` lazy-initializes Dealer / Mercury / Pathfinder / AudioKeyManager / EventService on first access, so a process that only needs metadata never opens a Dealer WebSocket.

## Folder map

```
Wavee/
├── Audio/                  # Playback orchestration (queue, context, autoplay, normalization)
│   └── Queue/              # Queue + queue items
├── AudioIpc/               # Named-pipe IPC client to Wavee.AudioHost
├── Connect/                # Spotify Connect — Dealer WebSocket + cluster state + remote commands
│   ├── Commands/           # Command types (Play, Pause, Seek, Transfer, Shuffle, Queue ops, etc.)
│   ├── Connection/         # DealerConnection (WebSocket abstraction) + IDealerConnection
│   ├── Diagnostics/        # IRemoteStateRecorder (optional dealer/HTTP capture for the UI Debug page)
│   ├── Events/             # Gabo envelope + playback event types (RawCoreStream, BoomboxPlaybackSession, …)
│   └── Protocol/           # Zero-allocation message/parser primitives + JSON source-gen
├── Core/
│   ├── Audio/              # FileId, AudioQuality, AudioKeyManager, ProgressiveDownloader, PlayPlay (stub)
│   ├── Authentication/     # Authenticator, Credentials, CredentialsCache, BlobDecryptor
│   ├── Connection/         # AP TCP transport, ApCodec (Shannon-framed), Diffie-Hellman handshake
│   ├── Crypto/             # ShannonCipher (validated against librespot — see Crypto/README.md)
│   ├── Feedback/           # User-feedback submission API
│   ├── Http/               # SpClient, PathfinderClient, Login5Client, KeymasterTokenProvider, Lyrics, …
│   ├── Library/            # Liked songs, saved artists/albums, local-file metadata
│   ├── Mercury/            # MercuryManager (request/response over AP TCP)
│   ├── Playlists/          # PlaylistCacheService + Rootlist tree
│   ├── Session/            # Session, ApResolver, KeepAlive, PacketType, UserData
│   ├── Storage/            # SQLite-backed entity caches (track / album / artist / show / episode / user)
│   ├── Time/               # SpotifyClockService (server-time skew correction)
│   └── Utilities/          # AsyncWorker, SafeSubject, etc.
├── OAuth/                  # AuthorizationCodeFlow + DeviceCodeFlow (Spotify OAuth 2.0)
└── Protocol/
    ├── Protos/             # 64 .proto files
    └── Generated/          # Compiled C# (Grpc.Tools at build time, output excluded from default Compile)
```

## Lazy initialization

```csharp
var http = httpClientFactory; // Microsoft.Extensions.Http
var session = Session.Create(
    new SessionConfig { DeviceId = deviceId, DeviceName = "My App" },
    http,
    logger,
    remoteStateRecorder: null);

await session.ConnectAsync(credentials, credentialsCache);

// Subsystems are created on first access:
var pathfinder = session.GetPathfinderClient();
var dealer     = session.GetOrCreateDealerClient();
var keyMgr     = session.GetOrCreateAudioKeyManager();
```

A consumer that only needs a single API call (`pathfinder.SearchAsync(...)`) never opens the Dealer or AudioKey channels.

## Diagnostics

`Connect/Diagnostics/IRemoteStateRecorder` is threaded through `Session`, `DealerClient`, `DeviceStateManager`, `PlaybackStateManager`, and the HTTP clients. Implementing it lets you capture every dealer message, request, HTTP call, and state transition with timing and (optionally) raw payloads. The WinUI app uses this for its DebugPage's "remote state recorder" panel.

`null` is fine — the recorder is optional.

## AOT and trimming

All public types are kept (no reflection-based codegen at runtime). Protobuf code is generated with `Access="Public"` and excluded from default `Compile` to avoid duplication. The csproj treats every IL-trimmer/AOT warning as an error — if you add code that uses reflection, the build fails until you annotate or refactor.

`Core/Audio/PlayPlayConstants.cs` is gitignored (Spotify property). When absent, `PlayPlayConstants.Stub.cs` is used instead and the PlayPlay key fallback is disabled at runtime.

## Deeper docs

- [Connect/DEALER_PROTOCOL.md](Connect/DEALER_PROTOCOL.md) — wire-level Spotify Dealer protocol reference (message types, URI patterns, payload encoding).
- [Connect/DEALER_IMPLEMENTATION_GUIDE.md](Connect/DEALER_IMPLEMENTATION_GUIDE.md) — how `DealerClient` and friends are organized in this codebase.
- [OAuth/OAUTH_FLOWS.md](OAuth/OAUTH_FLOWS.md) — full Spotify OAuth 2.0 flow analysis (Authorization Code + PKCE and Device Code).
- [Core/Crypto/README.md](Core/Crypto/README.md) — cryptography validation notes (Shannon cipher, audio decryption).

## Dependencies

`Google.Protobuf`, `Grpc.Tools` (build-time), `System.Reactive` (preview), `Microsoft.Extensions.Http` / `Logging.Abstractions`, `Microsoft.Data.Sqlite`, `System.Security.Cryptography.ProtectedData` (DPAPI), `ZstdSharp.Port` (Dealer payload compression), `z440.atl.core` (audio metadata). Project refs: `Wavee.Playback.Contracts`, `NVorbis`.

`InternalsVisibleTo`: `Wavee.Tests`, `DynamicProxyGenAssembly2` (Castle proxies for test mocks).
