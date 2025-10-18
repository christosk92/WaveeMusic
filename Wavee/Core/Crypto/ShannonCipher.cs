using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Wavee.Core.Crypto;

/// <summary>
/// Shannon stream cipher implementation for Spotify Connect protocol.
/// Based on the Qualcomm reference implementation (Gregory Rose).
///
/// This cipher is used to encrypt/decrypt all packets after the initial handshake.
/// Each packet uses a 4-byte big-endian nonce that is incremented after each packet.
/// </summary>
public sealed class ShannonCipher
{
    private const int N = 16; // LFSR register length
    private const int FOLD = N; // Diffusion iterations
    private const uint INITKONST = 0x6996c53a; // Initial constant during key loading
    private const int KEYP = 13; // Key insertion position in register

    // LFSR working register (16 x 32-bit words)
    private readonly uint[] _R = new uint[N];

    // CRC accumulation register (16 x 32-bit words) - for MAC
    private readonly uint[] _CRC = new uint[N];

    // Saved initial register state (for nonce reloading)
    private readonly uint[] _initR = new uint[N];

    // Key-dependent semi-constant
    private uint _konst;

    // Stream buffer (keystream output word)
    private uint _sbuf;

    // MAC accumulation buffer (partial word)
    private uint _mbuf;

    // Number of buffered bits in sbuf/mbuf
    private int _nbuf;

    /// <summary>
    /// Initializes a new Shannon cipher with the given key.
    /// </summary>
    /// <param name="key">32-byte encryption key</param>
    public ShannonCipher(ReadOnlySpan<byte> key)
    {
        if (key.Length != 32)
            throw new ArgumentException("Shannon key must be exactly 32 bytes", nameof(key));

        InitState();
        LoadKey(key, key.Length);
        GenKonst();
        SaveState();
        _nbuf = 0;
    }

    /// <summary>
    /// Sets the nonce (initialization vector) for this cipher.
    /// This is called with a 4-byte big-endian nonce before each packet.
    /// </summary>
    /// <param name="nonce">Big-endian 32-bit nonce value</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void NonceU32(uint nonce)
    {
        // Convert big-endian nonce to little-endian bytes for processing
        Span<byte> nonceBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(nonceBytes, nonce);

        ReloadState();
        _konst = INITKONST;
        LoadKey(nonceBytes, 4);
        GenKonst();
        _nbuf = 0;
    }

    /// <summary>
    /// Encrypts data in-place and accumulates MAC.
    /// MACs the plaintext BEFORE XORing with keystream.
    /// </summary>
    /// <param name="buffer">Data to encrypt (plaintext in, ciphertext out)</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Encrypt(Span<byte> buffer)
    {
        EncryptInternal(buffer);
    }

    /// <summary>
    /// Decrypts data in-place and accumulates MAC.
    /// XORs FIRST to recover plaintext, then MACs the plaintext.
    /// </summary>
    /// <param name="buffer">Data to decrypt (ciphertext in, plaintext out)</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Decrypt(Span<byte> buffer)
    {
        DecryptInternal(buffer);
    }

    /// <summary>
    /// Generates the MAC (Message Authentication Code) and writes it to the buffer.
    /// Must be called after all encryption/decryption is complete.
    /// </summary>
    /// <param name="mac">4-byte buffer to receive the MAC (big-endian)</param>
    public void Finish(Span<byte> mac)
    {
        if (mac.Length != 4)
            throw new ArgumentException("MAC must be exactly 4 bytes", nameof(mac));

        // Handle any previously buffered bytes
        if (_nbuf != 0)
        {
            MacFunc(_mbuf);
        }

        // Perturb the MAC to mark end of input
        Cycle();
        _R[KEYP] ^= INITKONST ^ (uint)(_nbuf << 3);
        _nbuf = 0;

        // Add CRC to stream register and diffuse
        for (int i = 0; i < N; i++)
        {
            _R[i] ^= _CRC[i];
        }
        Diffuse();

        // Produce 4-byte MAC output
        Cycle();
        BinaryPrimitives.WriteUInt32LittleEndian(mac, _sbuf);
    }

    /// <summary>
    /// Verifies the MAC (Message Authentication Code).
    /// Throws an exception if the MAC is invalid.
    /// </summary>
    /// <param name="receivedMac">4-byte MAC to verify</param>
    /// <exception cref="InvalidDataException">Thrown if MAC verification fails</exception>
    public void CheckMac(ReadOnlySpan<byte> receivedMac)
    {
        if (receivedMac.Length != 4)
            throw new ArgumentException("MAC must be exactly 4 bytes", nameof(receivedMac));

        Span<byte> computedMac = stackalloc byte[4];
        Finish(computedMac);

        if (!receivedMac.SequenceEqual(computedMac))
        {
            throw new InvalidDataException("Shannon MAC verification failed");
        }
    }

    #region Core Shannon Algorithm

    /// <summary>
    /// S-box 1: Nonlinear transform using rotation and XOR
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint SBox1(uint w)
    {
        w ^= RotateLeft(w, 5) | RotateLeft(w, 7);
        w ^= RotateLeft(w, 19) | RotateLeft(w, 22);
        return w;
    }

    /// <summary>
    /// S-box 2: Nonlinear transform (slightly different combination)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint SBox2(uint w)
    {
        w ^= RotateLeft(w, 7) | RotateLeft(w, 22);
        w ^= RotateLeft(w, 5) | RotateLeft(w, 19);
        return w;
    }

    /// <summary>
    /// Rotate left (circular shift)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint RotateLeft(uint value, int count)
    {
        return (value << count) | (value >> (32 - count));
    }

    /// <summary>
    /// Cycle the LFSR and calculate output word in sbuf
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Cycle()
    {
        // Nonlinear feedback function
        uint t = _R[12] ^ _R[13] ^ _konst;
        t = SBox1(t) ^ RotateLeft(_R[0], 1);

        // Shift register
        for (int i = 1; i < N; i++)
        {
            _R[i - 1] = _R[i];
        }
        _R[N - 1] = t;

        t = SBox2(_R[2] ^ _R[15]);
        _R[0] ^= t;
        _sbuf = t ^ _R[8] ^ _R[12];
    }

    /// <summary>
    /// CRC function: accumulate 32 parallel CRC-16s
    /// Uses IBM CRC-16 polynomial: x^16 + x^15 + x^2 + 1
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CrcFunc(uint input)
    {
        uint t = _CRC[0] ^ _CRC[2] ^ _CRC[15] ^ input;
        for (int j = 1; j < N; j++)
        {
            _CRC[j - 1] = _CRC[j];
        }
        _CRC[N - 1] = t;
    }

    /// <summary>
    /// MAC function: update both stream register and CRC
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MacFunc(uint input)
    {
        CrcFunc(input);
        _R[KEYP] ^= input;
    }

    /// <summary>
    /// Initialize register to known state (Fibonacci sequence)
    /// </summary>
    private void InitState()
    {
        _R[0] = 1;
        _R[1] = 1;
        for (int i = 2; i < N; i++)
        {
            _R[i] = _R[i - 1] + _R[i - 2];
        }
        _konst = INITKONST;
    }

    /// <summary>
    /// Save current register state for later reload
    /// </summary>
    private void SaveState()
    {
        Array.Copy(_R, _initR, N);
    }

    /// <summary>
    /// Reload previously saved register state
    /// </summary>
    private void ReloadState()
    {
        Array.Copy(_initR, _R, N);
    }

    /// <summary>
    /// Generate key-dependent constant
    /// </summary>
    private void GenKonst()
    {
        _konst = _R[0];
    }

    /// <summary>
    /// Extra nonlinear diffusion of register
    /// </summary>
    private void Diffuse()
    {
        for (int i = 0; i < FOLD; i++)
        {
            Cycle();
        }
    }

    /// <summary>
    /// Load key or nonce material into the register.
    /// Handles non-word-multiple lengths correctly.
    ///
    /// IMPORTANT: Input bytes are interpreted as LITTLE-ENDIAN words internally,
    /// but the nonce from Spotify is BIG-ENDIAN, so it's converted before calling this.
    /// </summary>
    private void LoadKey(ReadOnlySpan<byte> key, int keylen)
    {
        int i = 0;

        // Process full 4-byte words (little-endian)
        while (i < (keylen & ~0x3))
        {
            uint k = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(i));
            _R[KEYP] ^= k;
            Cycle();
            i += 4;
        }

        // Handle any extra bytes (zero-padded to word boundary)
        if (i < keylen)
        {
            Span<byte> xtra = stackalloc byte[4];
            xtra.Clear();

            int j = 0;
            while (i < keylen)
            {
                xtra[j++] = key[i++];
            }

            uint k = BinaryPrimitives.ReadUInt32LittleEndian(xtra);
            _R[KEYP] ^= k;
            Cycle();
        }

        // Fold in the key length
        _R[KEYP] ^= (uint)keylen;
        Cycle();

        // Save a copy of the register to CRC
        Array.Copy(_R, _CRC, N);

        // Diffuse
        Diffuse();

        // XOR the copy back (makes key loading irreversible)
        for (i = 0; i < N; i++)
        {
            _R[i] ^= _CRC[i];
        }
    }

    /// <summary>
    /// Encrypts data: MACs plaintext BEFORE XORing with keystream.
    /// This matches librespot's encrypt() behavior.
    /// </summary>
    private void EncryptInternal(Span<byte> buffer)
    {
        int offset = 0;
        int remaining = buffer.Length;

        // Handle any previously buffered bytes from last operation
        while (_nbuf != 0 && remaining > 0)
        {
            // Accumulate plaintext byte into mbuf for MAC
            _mbuf ^= (uint)(buffer[offset]) << (32 - _nbuf);

            // XOR with keystream
            buffer[offset] ^= (byte)((_sbuf >> (32 - _nbuf)) & 0xFF);

            offset++;
            _nbuf -= 8;
            remaining--;
        }

        // If we finished a buffered word, MAC it
        if (_nbuf == 0 && offset > 0)
        {
            MacFunc(_mbuf);
            _mbuf = 0;
        }

        // Process full words (4 bytes at a time)
        while (remaining >= 4)
        {
            Cycle();

            // Read plaintext word (little-endian)
            uint plaintextWord = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset, 4));

            // MAC the plaintext BEFORE encrypting
            MacFunc(plaintextWord);

            // Encrypt: XOR with keystream
            uint ciphertextWord = plaintextWord ^ _sbuf;

            // Write ciphertext back (little-endian)
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(offset, 4), ciphertextWord);

            offset += 4;
            remaining -= 4;
        }

        // Handle any trailing bytes (< 4 bytes)
        if (remaining > 0)
        {
            Cycle();
            _mbuf = 0;
            _nbuf = 32;

            while (remaining > 0)
            {
                // Accumulate plaintext byte into mbuf for MAC
                _mbuf ^= (uint)(buffer[offset]) << (32 - _nbuf);

                // XOR with keystream to encrypt
                buffer[offset] ^= (byte)((_sbuf >> (32 - _nbuf)) & 0xFF);

                offset++;
                _nbuf -= 8;
                remaining--;
            }
        }
    }

    /// <summary>
    /// Decrypts data: XORs FIRST to get plaintext, then MACs the plaintext.
    /// This matches librespot's decrypt() behavior.
    /// </summary>
    private void DecryptInternal(Span<byte> buffer)
    {
        int offset = 0;
        int remaining = buffer.Length;

        // Handle any previously buffered bytes from last operation
        while (_nbuf != 0 && remaining > 0)
        {
            // XOR with keystream FIRST to decrypt
            buffer[offset] ^= (byte)((_sbuf >> (32 - _nbuf)) & 0xFF);

            // Accumulate decrypted plaintext byte into mbuf for MAC
            _mbuf ^= (uint)(buffer[offset]) << (32 - _nbuf);

            offset++;
            _nbuf -= 8;
            remaining--;
        }

        // If we finished a buffered word, MAC it
        if (_nbuf == 0 && offset > 0)
        {
            MacFunc(_mbuf);
            _mbuf = 0;
        }

        // Process full words (4 bytes at a time)
        while (remaining >= 4)
        {
            Cycle();

            // Read ciphertext word (little-endian)
            uint ciphertextWord = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset, 4));

            // Decrypt: XOR with keystream to get plaintext
            uint plaintextWord = ciphertextWord ^ _sbuf;

            // MAC the plaintext AFTER decrypting
            MacFunc(plaintextWord);

            // Write plaintext back (little-endian)
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(offset, 4), plaintextWord);

            offset += 4;
            remaining -= 4;
        }

        // Handle any trailing bytes (< 4 bytes)
        if (remaining > 0)
        {
            Cycle();
            _mbuf = 0;
            _nbuf = 32;

            while (remaining > 0)
            {
                // XOR with keystream FIRST to decrypt
                buffer[offset] ^= (byte)((_sbuf >> (32 - _nbuf)) & 0xFF);

                // Accumulate decrypted plaintext byte into mbuf for MAC
                _mbuf ^= (uint)(buffer[offset]) << (32 - _nbuf);

                offset++;
                _nbuf -= 8;
                remaining--;
            }
        }
    }

    #endregion
}
