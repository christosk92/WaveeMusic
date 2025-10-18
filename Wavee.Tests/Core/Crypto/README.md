# Cryptographic Implementation Validation

This directory contains **cryptographically validated** implementations of Spotify protocol ciphers, verified against **librespot** (the authoritative open-source Spotify client).

## Why Validation Matters

Cryptographic implementations must be **byte-perfect**. Even a single bit difference in encryption/decryption can cause:
- Complete failure to communicate with Spotify servers
- Silent data corruption
- Authentication failures
- Protocol incompatibility

To ensure our C# implementations are correct, we:
1. ✅ Generated **ground truth test vectors** from librespot's Rust implementation
2. ✅ Validated our C# code produces **identical output** for the same inputs
3. ✅ Cross-verified encryption, MAC generation, and all edge cases

---

## Validated Implementations

### 1. **AudioDecryptStream** - AES-128-CTR Big Endian
- **Purpose**: Decrypt Spotify audio files
- **Algorithm**: AES-128 in CTR mode with Big Endian counter increment
- **IV**: Hardcoded `0x72e067fbddcbcf77ebe8bc643f630d93`
- **Validation**: 9/9 tests passed against librespot
- **Test Vector Generator**: `generate_audio_decrypt_vectors.rs`

**Key Features:**
- Full seeking support (random access to any position)
- Streaming decryption (on-the-fly)
- Matches librespot's `audio/src/decrypt.rs` exactly

**Test Coverage:**
- ✅ Basic decryption
- ✅ Seeking to various positions (0, 15, 16, 17, 31, 32)
- ✅ Cross-block boundary reads
- ✅ Multi-block decryption
- ✅ Empty data handling

---

### 2. **ShannonCipher** - Shannon Stream Cipher
- **Purpose**: Encrypt/decrypt Spotify protocol packets
- **Algorithm**: Shannon cipher (Qualcomm design by Gregory Rose)
- **MAC**: Integrated message authentication
- **Validation**: 28/28 tests passed (12 librespot + 16 unit tests)
- **Test Vector Generator**: `generate_shannon_vectors.rs`

**Key Features:**
- Combined encryption + MAC (authenticated encryption)
- Nonce-based (incrementing for each packet)
- Separate encrypt/decrypt logic (MAC timing differs)
- Matches librespot's `shannon` crate v0.2.0 exactly

**Critical Implementation Details:**
- **Encrypt**: MACs plaintext **BEFORE** XORing with keystream
- **Decrypt**: XORs **FIRST** to recover plaintext, **THEN** MACs
- MAC always computed on plaintext, never ciphertext

**Test Coverage:**
- ✅ Basic encryption with various nonces
- ✅ Empty data
- ✅ Non-word-aligned data (13 bytes = "Hello, World!")
- ✅ ApCodec-style packet format (cmd + length + payload)
- ✅ Sequential nonces (protocol packet sequencing)
- ✅ Large data (100 bytes, multi-word processing)
- ✅ MAC generation and verification
- ✅ Round-trip encrypt/decrypt

---

## How to Regenerate Test Vectors

### Prerequisites
1. **Rust toolchain** (stable-x86_64-pc-windows-msvc recommended on Windows)
   ```bash
   rustup default stable-x86_64-pc-windows-msvc
   ```

2. **librespot source code** (already included in this repo at `librespot/`)

### Generate Audio Decrypt Vectors

```bash
cd C:\Users\ckara\Personal\Wavee\librespot\audio
cargo run --example generate_test_vectors
```

**Output:** Test vectors showing:
- Key, plaintext, encrypted data for various test cases
- Seeking behavior at block boundaries
- C#-formatted arrays ready for copy/paste

### Generate Shannon Cipher Vectors

```bash
cd C:\Users\ckara\Personal\Wavee\librespot\core
cargo run --example generate_shannon_vectors
```

**Output:** Test vectors showing:
- Encryption with different nonces
- MAC values
- ApCodec-style packet encryption
- C#-formatted arrays ready for copy/paste

---
## Running Validation Tests

### All Crypto Tests
```bash
cd C:\Users\ckara\Personal\Wavee\Wavee
dotnet test Wavee.Tests/Wavee.Tests.csproj --filter "Namespace~Wavee.Tests.Core.Crypto"
```

### AudioDecryptStream Only
```bash
dotnet test --filter "FullyQualifiedName~AudioDecryptStream"
```

### ShannonCipher Only
```bash
dotnet test --filter "FullyQualifiedName~ShannonCipher"
```

### Librespot Validation Only
```bash
dotnet test --filter "FullyQualifiedName~Librespot"
```

---