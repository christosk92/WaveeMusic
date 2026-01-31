# WaveeMusic

A high-performance Spotify client for Windows, built with .NET 10 and WinUI 3.

![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)
![WinUI](https://img.shields.io/badge/WinUI-3-0078D4?logo=windows)
![License](https://img.shields.io/badge/License-MIT-green)

## Features

### Desktop Application
- **Modern UI** - WinUI 3 with Mica backdrop and Fluent Design
- **Browser-like Tabs** - Pin, compact, drag-and-drop, and context menus
- **Sidebar Navigation** - Quick access to Home, Library, Search, and playlists
- **Full Player Controls** - Play, pause, seek, shuffle, repeat, volume, and queue

### Spotify Connect
- **Remote Playback Control** - Control playback on any Spotify Connect device
- **Real-Time Sync** - WebSocket-based Dealer protocol for instant updates
- **Device Management** - Transfer playback between devices seamlessly

### Audio Pipeline
- **BASS Audio Engine** - High-quality local playback with BASS decoder
- **Audio Processing** - 10-band equalizer, compressor, limiter, normalization
- **HTTP Streaming** - Native support for radio streams and URLs
- **Crossfade** - Smooth transitions between tracks

### Authentication
- **OAuth 2.0** - Authorization Code Flow with PKCE (browser-based)
- **Device Code Flow** - Console and headless authentication
- **Secure Storage** - Encrypted credential caching

## Screenshots

![Console](screenshots/console.png)

## Quick Start

### Prerequisites
- Windows 10 version 1809 or later
- .NET 10.0 SDK
- Spotify Premium account

### Running the Desktop App

```bash
git clone https://github.com/christosk92/WaveeMusic.git
cd WaveeMusic
dotnet run --project Wavee.UI.WinUI
```

### Running the Console App

```bash
dotnet run --project Wavee.Console
```

## Project Structure

```
WaveeMusic/
├── Wavee/                          # Core library
│   ├── Core/                       # Authentication, session, crypto
│   ├── Connect/                    # Spotify Connect protocol
│   │   ├── Playback/               # Audio pipeline and processors
│   │   └── DealerClient.cs         # WebSocket orchestrator
│   ├── OAuth/                      # OAuth 2.0 flows
│   └── Protocol/                   # Protobuf definitions
├── Wavee.UI.WinUI/                 # WinUI 3 Desktop App
│   ├── Controls/                   # TabBar, Sidebar, PlayerBar
│   ├── Views/                      # Home, Library, Search, Artist, Album
│   ├── ViewModels/                 # MVVM view models
│   └── Services/                   # Navigation, theming
├── Wavee.Console/                  # Interactive console app
└── Wavee.Tests/                    # Test suite
```

## Technology Stack

| Component | Technology |
|-----------|------------|
| **Framework** | .NET 10.0, C# 13 |
| **UI** | WinUI 3, Windows App SDK |
| **Audio** | BASS Audio Library |
| **Protocols** | Protocol Buffers, WebSocket |
| **Reactive** | System.Reactive (Rx.NET) |
| **MVVM** | CommunityToolkit.Mvvm |

## Implementation Status

### Complete
- OAuth 2.0 authentication (Authorization Code + Device Code)
- Spotify Connect protocol (Dealer WebSocket)
- Remote playback control (play, pause, seek, skip, shuffle, repeat)
- Device state synchronization
- Search functionality (GraphQL Pathfinder API)
- Audio pipeline with BASS decoder
- Audio processors (equalizer, compressor, limiter, normalization)

### In Progress
- WinUI 3 desktop application
- Playlist management
- Library synchronization

### Planned
- Mercury protocol for metadata
- Offline playback
- Lyrics display

## Building

```bash
# Build entire solution
dotnet build

# Build release
dotnet build -c Release

# Run tests
dotnet test
```

## Contributing

Contributions are welcome! See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

Areas where help is needed:
- UI/UX improvements
- Additional audio format support
- Platform-specific features
- Documentation and examples

## License

MIT License - see [LICENSE](LICENSE) for details.

## Disclaimer

This project is not affiliated with Spotify AB. All trademarks are property of their respective owners. WaveeMusic is for educational and personal use only. Users must comply with Spotify's Terms of Service and have a valid Spotify Premium subscription.

---

**Status**: Active Development | **Platform**: Windows 10/11
