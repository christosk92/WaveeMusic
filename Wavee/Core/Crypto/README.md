# Shannon Cipher Implementation

## Overview
This is a complete C# implementation of the Shannon stream cipher used by Spotify Connect for packet encryption and MAC generation.

## Endianness Handling (CRITICAL)

### Big-Endian (Network Order)
- **Nonce Input**: The `NonceU32()` method accepts a **big-endian 32-bit** nonce value
  - This is the standard network byte order used by Spotify Connect protocol
  - The nonce is incremented for each packet: `0x00000000`, `0x00000001`, `0x00000002`, etc.

### Little-Endian (Internal Processing)
- **Internal word operations**: All 32-bit words inside the Shannon cipher use **little-endian** byte order
  - Key material is loaded as little-endian words
  - LFSR register operations use little-endian
  - Keystream generation produces little-endian words

### Conversion Flow
```
Spotify Protocol (Big-Endian Nonce)
         ↓
   NonceU32(uint nonce)  ← accepts big-endian value
         ↓
   BinaryPrimitives.WriteUInt32BigEndian()  ← convert to bytes
         ↓
   LoadKey() processes as little-endian words
         ↓
   Internal LFSR operations (little-endian)
         ↓
   Keystream XOR (byte-by-byte, endianness-neutral)
```

## Usage Example

```csharp
// Create cipher with 32-byte key
byte[] sendKey = /* ... 32 bytes from DH handshake ... */;
var sendCipher = new ShannonCipher(sendKey);

// Encoding a packet (incrementing nonce)
uint sendNonce = 0;
while (true)
{
    // Set big-endian nonce
    sendCipher.NonceU32(sendNonce++);

    // Encrypt payload in-place
    byte[] packet = /* ... cmd (1) + length (2) + payload (N) ... */;
    sendCipher.Encrypt(packet);

    // Generate 4-byte MAC
    byte[] mac = new byte[4];
    sendCipher.Finish(mac);

    // Send: packet || mac
}
```

## Algorithm Details

### LFSR Structure
- 16-element shift register of 32-bit words
- Feedback polynomial with nonlinear S-boxes
- Two S-box functions (SBox1 and SBox2) using rotation and XOR

### MAC Generation
- 32 parallel CRC-16 registers
- IBM CRC-16 polynomial: x^16 + x^15 + x^2 + 1
- Combined with stream register diffusion

### Key Loading
1. Initialize register to Fibonacci sequence
2. Fold in key material word-by-word
3. Diffuse for 16 cycles
4. XOR back copy to make irreversible

### Nonce Handling
1. Reload saved register state
2. Reset constant to INITKONST
3. Load nonce as key material
4. Generate new key-dependent constant
5. Ready for encryption/decryption

## Performance Considerations

- Uses `Span<byte>` for zero-copy operations
- Inlined hot-path methods (`Cycle()`, `SBox1()`, etc.)
- Processes full 32-bit words when possible
- Stack-allocated temporary buffers

## Testing

Run basic tests:
```csharp
ShannonCipherTest.RunBasicTests();
```

For production use, verify against actual Spotify Connect handshake vectors from librespot.

## References

- Original Shannon specification: https://eprint.iacr.org/2007/044.pdf
- Qualcomm reference implementation (C): https://github.com/timniederhausen/shannon
- librespot usage: https://github.com/librespot-org/librespot/blob/master/core/src/connection/codec.rs
