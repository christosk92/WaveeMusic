# BASS Audio Library Setup

Wavee uses the [BASS audio library](https://www.un4seen.com/) via [ManagedBass](https://github.com/ManagedBass/ManagedBass) for decoding local audio files. BASS is a powerful audio library that supports MP3, FLAC, WAV, AIFF, and more.

## Supported Formats

The `BassDecoder` supports these formats out of the box with the core BASS library:

| Format | Extension | Notes |
|--------|-----------|-------|
| MP3 | `.mp3` | Including ID3v2 tags |
| FLAC | `.flac` | Lossless audio |
| WAV | `.wav` | PCM audio |
| AIFF | `.aiff`, `.aif` | Apple audio format |

### Additional Formats (Require Plugins)

| Format | Extension | Required Plugin |
|--------|-----------|-----------------|
| AAC/M4A | `.m4a`, `.aac` | BASS_AAC |
| WMA | `.wma` | BASS_WMA |
| ALAC | `.m4a` | BASS_AAC |
| Opus | `.opus` | BASS_OPUS |
| AC3 | `.ac3` | BASS_AC3 |
| DSD | `.dsf`, `.dff` | BASS_DSD |
| MPC | `.mpc` | BASS_MPC |
| APE | `.ape` | BASS_APE |

## Installation

### Step 1: Download BASS Library

Download the BASS library from [un4seen.com](https://www.un4seen.com/bass.html):

1. Go to https://www.un4seen.com/bass.html
2. Download the appropriate version for your platform:
   - **Windows**: `bass24.zip`
   - **macOS**: `bass24-osx.zip`
   - **Linux**: `bass24-linux.zip`

### Step 2: Extract Native Libraries

Extract the native library files to the `runtimes` folder structure:

```
Wavee/
└── runtimes/
    ├── win-x64/
    │   └── native/
    │       └── bass.dll
    ├── win-x86/
    │   └── native/
    │       └── bass.dll
    ├── win-arm64/
    │   └── native/
    │       └── bass.dll
    ├── osx-x64/
    │   └── native/
    │       └── libbass.dylib
    ├── osx-arm64/
    │   └── native/
    │       └── libbass.dylib
    ├── linux-x64/
    │   └── native/
    │       └── libbass.so
    └── linux-arm64/
        └── native/
            └── libbass.so
```

**From the downloaded archives:**
- Windows: Copy `bass.dll` (x64 version) from the `x64` folder
- macOS: Copy `libbass.dylib` from the archive
- Linux: Copy `libbass.so` (x64 version) from the `x64` folder

### Step 3: Download Plugins (Optional)

For additional format support, download plugins from [un4seen.com Add-ons](https://www.un4seen.com/bass.html#addons):

#### AAC/M4A Support (BASS_AAC)

1. Download from https://www.un4seen.com/bass.html#addons (look for BASS_AAC)
2. Extract and copy to the same `runtimes/<platform>/native/` folder:
   - Windows: `bass_aac.dll`
   - macOS: `libbass_aac.dylib`
   - Linux: `libbass_aac.so`

#### Loading Plugins in Code

Plugins are automatically loaded by BASS when placed in the same directory as the main library. No code changes required.

## Licensing

**BASS is free for non-commercial use.** For commercial use, you must purchase a license from [un4seen.com](https://www.un4seen.com/bass.html#license).

- **Free**: Personal/non-commercial projects
- **Shareware License**: ~$100 USD (one-time)
- **Commercial License**: Contact un4seen for pricing

## Troubleshooting

### "Failed to initialize BASS" Error

1. Ensure the native library is in the correct `runtimes/<rid>/native/` folder
2. Check that the library architecture matches your runtime (x64 vs x86 vs ARM64)
3. On Linux, ensure `libbass.so` has execute permissions: `chmod +x libbass.so`

### Format Not Supported

1. Check if the format requires a plugin (see table above)
2. Download and install the required plugin
3. Ensure the plugin is in the same folder as the main BASS library

### macOS Security Warning

On macOS, you may need to allow the library in System Preferences > Security & Privacy after first run.

## Verification

To verify BASS is working, run the console app with a local file:

```bash
dotnet run --project Wavee.Console -- --file "path/to/audio.mp3"
```

## References

- BASS Homepage: https://www.un4seen.com/bass.html
- ManagedBass: https://github.com/ManagedBass/ManagedBass
- BASS Forum: https://www.un4seen.com/forum/
