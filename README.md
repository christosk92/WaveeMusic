# WaveeMusic

A high-performance, modern .NET implementation of Spotify's proprietary protocols, enabling Spotify Connect remote playback control and client functionality.

## Overview

WaveeMusic is a sophisticated Spotify client library written in C# targeting .NET 10.0. It implements Spotify's proprietary protocols from the ground up, providing:

- **Full Spotify Connect Support** - Remote playback control across devices
- **Multiple Authentication Methods** - OAuth 2.0 (Authorization Code + Device Code flows), credentials, and cached tokens
- **Real-Time Messaging** - WebSocket-based Dealer protocol for instant command routing
- **High Performance** - Native AOT compatible with zero-allocation hot paths
- **Production Ready** - Comprehensive test coverage and enterprise-grade architecture

## Features

### Authentication & Session Management
- OAuth 2.0 Authorization Code Flow with PKCE (browser-based)
- OAuth 2.0 Device Code Flow (console/headless)
- Username/password authentication
- Encrypted credential caching
- Automatic session keep-alive
- Access Point discovery and connection

### Spotify Connect Protocol
- WebSocket-based Dealer protocol implementation
- Real-time command routing (Play, Pause, Seek, Skip, etc.)
- Automatic heartbeat and reconnection management
- Device state synchronization
- Playback state management
- Transfer playback between devices
- Queue management

### Audio Pipeline (Framework)
- Modular audio pipeline: Source → Decoder → Processors → Sink
- Plugin system for decoders and audio sources
- Audio processing chain:
  - Volume control
  - Audio normalization
  - 10-band equalizer
  - Crossfade between tracks

### Security & Performance
- Elliptic Curve Diffie-Hellman (ECDH) key exchange
- Shannon cipher encryption for AP transport
- System.IO.Pipelines for efficient streaming I/O
- Span&lt;T&gt;/Memory&lt;T&gt; for zero-copy processing
- Lock-free queues using System.Threading.Channels
- Native AOT compilation support

## Quick Start

### Prerequisites
- .NET 10.0 SDK or later
- Spotify Premium account (required for Connect features)

### Running the Console Application

```bash
# Clone the repository
git clone https://github.com/christosk92/WaveeMusic.git
cd WaveeMusic

# Build the solution
dotnet build

# Run the console application
dotnet run --project Wavee.Console
```

The console application will guide you through:
1. Selecting an OAuth flow (Authorization Code or Device Code)
2. Authenticating with Spotify
3. Establishing a session connection
4. Interactive playback control

## Project Structure

```
WaveeMusic/
├── Wavee/                          # Main library
│   ├── Core/                       # Foundation layer
│   │   ├── Authentication/         # Auth and credential management
│   │   ├── Connection/             # Handshake, transport, codec
│   │   ├── Session/                # Session orchestration
│   │   ├── Http/                   # HTTP clients (SpClient, Login5)
│   │   ├── Crypto/                 # Shannon cipher implementation
│   │   └── Utilities/              # Async workers, helpers
│   ├── Connect/                    # Spotify Connect protocol
│   │   ├── Connection/             # WebSocket dealer connection
│   │   ├── Protocol/               # Message parsing and encoding
│   │   ├── Commands/               # Typed command handlers
│   │   ├── Playback/               # Audio pipeline and processors
│   │   └── DealerClient.cs         # Main orchestrator
│   ├── OAuth/                      # OAuth 2.0 flows
│   │   ├── AuthorizationCodeFlow.cs
│   │   └── DeviceCodeFlow.cs
│   └── Protocol/                   # Protobuf definitions
│       └── Protos/                 # 60+ .proto files
├── Wavee.Console/                  # Interactive console app
├── Wavee.Tests/                    # Comprehensive test suite
└── Wavee.sln                       # Solution file
```

## Architecture

### Core Session Layer

The session layer handles AP (Access Point) connection and authentication:

```
Session → ApResolver → ApTransport (Shannon Cipher) → Spotify AP
   ↓
Handshake (ECDH) → Authenticator → KeepAlive (Ping/Pong)
```

**Key Components:**
- `Session` - Main entry point for AP connection management
- `ApResolver` - Discovers Spotify Access Points via HTTP
- `ApTransport` - Low-level TCP transport with Shannon encryption
- `Handshake` - ECDH key exchange for secure channel
- `Authenticator` - Handles authentication after handshake
- `KeepAlive` - Monitors connection health

### Dealer/Connect Layer

The Connect layer implements Spotify's real-time messaging protocol:

```
DealerClient → DealerConnection (WebSocket) → Spotify Dealer
   ↓                    ↓                           ↓
MessageParser → ConnectCommandHandler → Typed Commands
   ↓
AudioPipeline → Source → Decoder → Processors → Sink
```

**Key Components:**
- `DealerClient` - WebSocket orchestrator with reactive streams (Rx.NET)
- `HeartbeatManager` - Sends PING every 30s, expects PONG
- `ReconnectionManager` - Exponential backoff reconnection strategy
- `MessageParser` - Zero-allocation JSON parsing with Span&lt;T&gt;
- `ConnectCommandHandler` - Converts raw requests to typed commands
- `AudioPipeline` - Modular playback engine (framework ready)

### OAuth Layer

Two OAuth 2.0 flows for different use cases:

```
┌─────────────────────┐          ┌──────────────────┐
│ Authorization Code  │          │ Device Code Flow │
│ Flow with PKCE      │          │                  │
│ (Browser-based)     │          │ (Console/Server) │
└─────────────────────┘          └──────────────────┘
          ↓                                ↓
          └────────────┬───────────────────┘
                       ↓
                  OAuthClient
                       ↓
              Spotify OAuth Server
```

## Technology Stack

| Component | Technology | Purpose |
|-----------|-----------|---------|
| **Language** | C# 13 (.NET 10.0) | Modern async/await, records, pattern matching |
| **Protocols** | Protocol Buffers 3 | Compact binary serialization |
| **Reactive** | System.Reactive (Rx.NET) | Observable command streams |
| **I/O** | System.IO.Pipelines | High-performance streaming |
| **Async** | System.Threading.Channels | Lock-free message queues |
| **HTTP** | IHttpClientFactory | Dependency injection ready |
| **Logging** | Microsoft.Extensions.Logging | Structured logging |
| **Crypto** | System.Security.Cryptography | ECDH, AES, HMAC-SHA256 |
| **AOT** | Native AOT | Single-file executable support |

## Implementation Status

### ✅ Complete & Production Ready

- **Core Session Management**
  - AP discovery and connection
  - ECDH handshake with Shannon cipher encryption
  - Multi-method authentication (OAuth, credentials, cached tokens)
  - Automatic keep-alive with ping/pong
  - Credential caching with encryption

- **OAuth 2.0 Authentication**
  - Authorization Code Flow with PKCE
  - Device Code Flow
  - Token refresh and expiration handling

- **Dealer Protocol**
  - WebSocket connection with System.IO.Pipelines
  - Heartbeat management (30s PING, 3s PONG timeout)
  - Automatic reconnection with exponential backoff
  - Message parsing (JSON, protobuf, gzip encoding)
  - Observable message and request streams

- **Spotify Connect Commands**
  - Play, Pause, Resume
  - Seek to position
  - Skip Next/Previous
  - Shuffle, Repeat Context, Repeat Track
  - Transfer playback between devices
  - Queue management (add, remove, reorder)
  - Device and playback state synchronization

### ⚠️ In Progress

- **Audio Pipeline**
  - Framework complete and ready
  - Decoder plugin system implemented
  - Audio processors ready (volume, EQ, normalization, crossfade)
  - Actual audio decoders stubbed (pending implementation)
  - Audio output sinks stubbed (pending implementation)

### 📋 Planned Features

- Mercury protocol (request/response for metadata)
- Channel manager (persistent subscriptions)
- AudioKey manager (fetch decryption keys)
- Full audio decoding (Spotify's OGG Vorbis format)
- Playlist synchronization
- Search functionality
- User profile management

## Performance Characteristics

WaveeMusic is designed for high performance and low resource usage:

- **Zero-Allocation Hot Paths**: Uses `Span<T>`, `Memory<T>`, and `ArrayPool<T>` to minimize GC pressure
- **Efficient I/O**: System.IO.Pipelines reduces buffer copying
- **Lock-Free Design**: System.Threading.Channels for message passing
- **Native AOT Ready**: No reflection, source-generated JSON serialization
- **Cached Static Data**: Pre-allocated byte arrays for common messages (PING, PONG)

## Documentation

Comprehensive guides are available in the repository:

- **[OAUTH_FLOWS.md](docs/OAUTH_FLOWS.md)** - Detailed OAuth 2.0 flow specifications
- **[DEALER_PROTOCOL.md](docs/DEALER_PROTOCOL.md)** - Dealer protocol specification and message types
- **[DEALER_IMPLEMENTATION_GUIDE.md](docs/DEALER_IMPLEMENTATION_GUIDE.md)** - High-performance dealer client implementation
- **[IMPLEMENTATION_GUIDE.md](Core/Session/IMPLEMENTATION_GUIDE.md)** - Session module architecture and patterns

## Testing

The project includes comprehensive test coverage:

- 50+ test classes covering all major components
- Unit tests for protocol parsing, encoding, and state management
- Mock helpers for testing (`MockDealerConnection`, `MockSession`, etc.)
- Property-based testing infrastructure
- Protocol compliance validation

Run tests:
```bash
dotnet test
```

## Building for Native AOT

To build a self-contained, Native AOT compiled executable:

```bash
dotnet publish -c Release -r linux-x64 --self-contained
```

The resulting binary:
- No .NET runtime required
- Fast startup time
- Small memory footprint
- Single-file executable

## Contributing

Contributions are welcome! Areas where help is needed:

1. **Audio Decoders** - Implementing Spotify's audio format decoding
2. **Audio Sinks** - Platform-specific audio output implementations
3. **Mercury Protocol** - Request/response protocol for metadata
4. **Testing** - Additional test coverage and edge cases
5. **Documentation** - Additional guides and examples

## License

[Add your license information here]

## Acknowledgments

- Inspired by [librespot](https://github.com/librespot-org/librespot) (Rust implementation)
- Protocol reverse engineering by the Spotify open-source community
- Built with modern .NET and C# best practices

## Disclaimer

This project is not affiliated with, endorsed by, or in any way officially connected to Spotify AB. All product names, logos, and brands are property of their respective owners.

WaveeMusic is intended for educational and personal use only. Users must comply with Spotify's Terms of Service and have a valid Spotify Premium subscription to use Connect features.

---

**Status**: Active Development | **Version**: Alpha | **Target**: .NET 10.0
