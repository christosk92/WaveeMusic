// ── Wavee.Tests/SpotifyCoreTests.cs — Shannon, the AP codec, the handshake fold, hashcash, PKCE, hex ──────────────
//
// Wave 2's gate for the LIFTED protocol mechanics in Spotify/Spotify.cs (plan §5 Wave 2: "Shannon/handshake
// vectors"). These are fixed specs, so they are pinned to VECTORS, not to a round trip: the Shannon vectors come
// from librespot's shannon crate (0.2.9 pinned the same five in `_old/Wavee.Tests/CryptoTests.cs`) and the PKCE
// vector from RFC 7636 Appendix B. Nothing here opens a socket, and nothing needs a Scope.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class ShannonTests
{
    // the librespot shannon crate's test key
    static readonly byte[] Key =
    [
        0x00,0x01,0x02,0x03,0x04,0x05,0x06,0x07, 0x08,0x09,0x0a,0x0b,0x0c,0x0d,0x0e,0x0f,
        0x10,0x11,0x12,0x13,0x14,0x15,0x16,0x17, 0x18,0x19,0x1a,0x1b,0x1c,0x1d,0x1e,0x1f,
    ];

    static (byte[] Encrypted, byte[] Mac) Run(uint nonce, byte[] plain)
    {
        var cipher = new Spotify.Shannon(Key);
        cipher.Nonce(nonce);
        var encrypted = (byte[])plain.Clone();
        cipher.Encrypt(encrypted);
        var mac = new byte[4];
        cipher.Finish(mac);
        return (encrypted, mac);
    }

    [Fact]
    public void Vector1_nonce0()
    {
        var (encrypted, mac) = Run(0, [0x01, 0x02, 0x03, 0x04]);
        Assert.Equal(new byte[] { 0xcb, 0x7f, 0xea, 0x2f }, encrypted);
        Assert.Equal(new byte[] { 0x80, 0x3a, 0x07, 0x7f }, mac);
    }

    [Fact]
    public void Vector2_nonce1()
    {
        var (encrypted, mac) = Run(1, [0x01, 0x02, 0x03, 0x04]);
        Assert.Equal(new byte[] { 0xba, 0x95, 0x25, 0xab }, encrypted);
        Assert.Equal(new byte[] { 0xae, 0x02, 0xa2, 0xc0 }, mac);
    }

    [Fact]
    public void Vector3_empty_data_is_mac_only()
    {
        var (encrypted, mac) = Run(0, []);
        Assert.Empty(encrypted);
        Assert.Equal(new byte[] { 0x0a, 0xab, 0x57, 0x02 }, mac);
    }

    [Fact]
    public void Vector4_non_word_aligned()
    {
        var (encrypted, mac) = Run(0, [0x48, 0x65, 0x6c, 0x6c, 0x6f, 0x2c, 0x20, 0x57, 0x6f, 0x72, 0x6c, 0x64, 0x21]);
        Assert.Equal(new byte[] { 0x82, 0x18, 0x85, 0x47, 0x57, 0x85, 0xea, 0x2e, 0xae, 0x01, 0x7e, 0xfe, 0xbe }, encrypted);
        Assert.Equal(new byte[] { 0x81, 0x49, 0x3f, 0x7e }, mac);
    }

    [Fact]
    public void Vector5_ap_packet_shaped_plaintext()
    {
        var (encrypted, mac) = Run(0, [0x42, 0x00, 0x04, 0xaa, 0xbb, 0xcc, 0xdd]);
        Assert.Equal(new byte[] { 0x88, 0x7d, 0xed, 0x81, 0x9f, 0x11, 0xf0 }, encrypted);
        Assert.Equal(new byte[] { 0x27, 0xa1, 0xfc, 0x02 }, mac);
    }

    [Theory]
    [InlineData(0u, new byte[] { 0xd8, 0x49, 0xbf, 0x53 }, new byte[] { 0xb2, 0x3d, 0xaf, 0x43 })]
    [InlineData(1u, new byte[] { 0xa9, 0xa3, 0x70, 0xd7 }, new byte[] { 0x1f, 0xb6, 0xf1, 0x9b })]
    [InlineData(2u, new byte[] { 0xdd, 0x3f, 0x13, 0x8e }, new byte[] { 0xf0, 0x78, 0x5c, 0x24 })]
    public void Vector6_sequential_nonces(uint nonce, byte[] expectedCipher, byte[] expectedMac)
    {
        var (encrypted, mac) = Run(nonce, [0x12, 0x34, 0x56, 0x78]);
        Assert.Equal(expectedCipher, encrypted);
        Assert.Equal(expectedMac, mac);
    }

    [Fact]
    public void Vector7_large_data()
    {
        var plain = new byte[100];
        for (int i = 0; i < plain.Length; i++) plain[i] = (byte)i;
        var (encrypted, mac) = Run(0, plain);
        Assert.Equal(new byte[]
        {
            0xca,0x7c,0xeb,0x28,0x6e,0x95,0x17,0xed, 0xc8,0x3c,0xd2,0x8b,0x9a,0xe9,0x2a,0x5c,
            0x25,0x3e,0x58,0x9a,0x57,0x3d,0x30,0x58, 0x79,0x41,0x8d,0xac,0x83,0x28,0xf1,0xa3,
        }, encrypted[..32]);
        Assert.Equal(new byte[] { 0x05, 0x88, 0x75, 0x07 }, mac);
    }

    [Fact]
    public void Decrypt_returns_the_plaintext_and_the_same_mac()
    {
        byte[] original = [0x01, 0x02, 0x03, 0x04];
        var (encrypted, encryptMac) = Run(0, original);

        var cipher = new Spotify.Shannon(Key);
        cipher.Nonce(0);
        var decrypted = (byte[])encrypted.Clone();
        cipher.Decrypt(decrypted);
        var decryptMac = new byte[4];
        cipher.Finish(decryptMac);

        Assert.Equal(original, decrypted);
        Assert.Equal(encryptMac, decryptMac);
    }

    [Fact]
    public void CheckMac_is_true_for_the_computed_mac_and_false_for_anything_else()
    {
        var good = new Spotify.Shannon(Key);
        good.Nonce(0);
        var buffer = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        good.Encrypt(buffer);
        Assert.True(good.CheckMac([0x80, 0x3a, 0x07, 0x7f]));

        var bad = new Spotify.Shannon(Key);
        bad.Nonce(0);
        var other = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        bad.Encrypt(other);
        Assert.False(bad.CheckMac([0, 0, 0, 0]));
    }

    [Fact]
    public void A_copy_of_the_struct_forks_the_keystream_rather_than_sharing_it()
    {
        // The struct semantics are load-bearing: the codec holds two of these by VALUE, and a stray copy would
        // silently desync a channel. Two ciphers keyed the same way answer the same bytes.
        var a = new Spotify.Shannon(Key);
        var b = a;
        a.Nonce(7);
        b.Nonce(7);
        var left = new byte[] { 1, 2, 3, 4, 5 };
        var right = new byte[] { 1, 2, 3, 4, 5 };
        a.Encrypt(left);
        b.Encrypt(right);
        Assert.Equal(left, right);
    }
}

public class ApCodecTests
{
    // client.send == server.recv; server.send == client.recv — the real directional keying.
    static (Spotify.ApCodec Client, Spotify.ApCodec Server) Pair()
    {
        var k1 = new byte[32];
        var k2 = new byte[32];
        new Random(1).NextBytes(k1);
        new Random(2).NextBytes(k2);
        return (new Spotify.ApCodec(k1, k2), new Spotify.ApCodec(k2, k1));
    }

    static byte[] Decode(ref Spotify.ApCodec codec, byte[] frame, out byte cmd)
    {
        var (command, length) = codec.BeginDecode(frame.AsSpan(0, 3));
        cmd = command;
        Assert.True(codec.EndDecode(frame.AsSpan(3, length), frame.AsSpan(3 + length, 4)));
        return frame.AsSpan(3, length).ToArray();
    }

    [Fact]
    public void Round_trips_client_to_server()
    {
        var (client, server) = Pair();
        byte[] payload = [0xaa, 0xbb, 0xcc, 0xdd, 0xee];
        var frame = new byte[Spotify.ApCodec.FrameLength(payload.Length)];
        Assert.Equal(frame.Length, client.Encode(0x04, payload, frame));
        Assert.Equal(payload, Decode(ref server, frame, out byte cmd));
        Assert.Equal(0x04, cmd);
    }

    [Fact]
    public void Round_trips_an_empty_payload_server_to_client()
    {
        var (client, server) = Pair();
        var frame = new byte[Spotify.ApCodec.FrameLength(0)];
        server.Encode(0x09, [], frame);
        Assert.Empty(Decode(ref client, frame, out byte cmd));
        Assert.Equal(0x09, cmd);
    }

    [Fact]
    public void The_nonce_advances_so_the_same_packet_encodes_differently()
    {
        var (client, _) = Pair();
        byte[] payload = [1, 2, 3, 4];
        var first = new byte[Spotify.ApCodec.FrameLength(payload.Length)];
        var second = new byte[first.Length];
        client.Encode(0x04, payload, first);
        client.Encode(0x04, payload, second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void A_tampered_payload_fails_the_mac_check()
    {
        var (client, server) = Pair();
        byte[] payload = [1, 2, 3, 4];
        var frame = new byte[Spotify.ApCodec.FrameLength(payload.Length)];
        client.Encode(0x04, payload, frame);
        frame[5] ^= 0xFF;
        var (_, length) = server.BeginDecode(frame.AsSpan(0, 3));
        Assert.False(server.EndDecode(frame.AsSpan(3, length), frame.AsSpan(3 + length, 4)));
    }

    [Fact]
    public void A_sequence_of_packets_round_trips_in_order()
    {
        var (client, server) = Pair();
        for (int i = 0; i < 5; i++)
        {
            byte[] payload = [(byte)i, (byte)(i + 1)];
            var frame = new byte[Spotify.ApCodec.FrameLength(payload.Length)];
            client.Encode((byte)(0x10 + i), payload, frame);
            Assert.Equal(payload, Decode(ref server, frame, out byte cmd));
            Assert.Equal((byte)(0x10 + i), cmd);
        }
    }
}

public class HandshakeTests
{
    static byte[] Private(int seed)
    {
        var priv = new byte[Spotify.Handshake.PrivateKeySize];
        new Random(seed).NextBytes(priv);
        return priv;
    }

    static byte[] Public(byte[] priv)
    {
        var pub = new byte[Spotify.Handshake.ModulusSize];
        Assert.Equal(Spotify.Handshake.ModulusSize, Spotify.Handshake.PublicKey(priv, pub));
        return pub;
    }

    [Fact]
    public void The_shared_secret_is_symmetric_and_always_96_bytes()
    {
        byte[] a = Private(1), b = Private(2);
        byte[] pa = Public(a), pb = Public(b);

        var left = new byte[Spotify.Handshake.ModulusSize];
        var right = new byte[Spotify.Handshake.ModulusSize];
        Assert.Equal(96, Spotify.Handshake.SharedSecret(a, pb, left));
        Assert.Equal(96, Spotify.Handshake.SharedSecret(b, pa, right));
        Assert.Equal(left, right);
    }

    [Fact]
    public void Different_key_pairs_produce_different_secrets()
    {
        byte[] a = Private(3);
        var withB = new byte[96];
        var withC = new byte[96];
        Spotify.Handshake.SharedSecret(a, Public(Private(4)), withB);
        Spotify.Handshake.SharedSecret(a, Public(Private(5)), withC);
        Assert.NotEqual(withB, withC);
    }

    [Fact]
    public void Key_derivation_is_deterministic_with_the_documented_lengths()
    {
        byte[] secret = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] accumulator = [10, 20, 30, 40, 50];

        var challenge1 = new byte[20];
        var send1 = new byte[32];
        var receive1 = new byte[32];
        Spotify.Handshake.DeriveKeys(secret, accumulator, challenge1, send1, receive1);

        var challenge2 = new byte[20];
        var send2 = new byte[32];
        var receive2 = new byte[32];
        Spotify.Handshake.DeriveKeys(secret, accumulator, challenge2, send2, receive2);

        Assert.Equal(challenge1, challenge2);
        Assert.Equal(send1, send2);
        Assert.NotEqual(send1, receive1);       // send != receive, or the channel would decrypt its own packets
    }

    [Fact]
    public void A_different_secret_derives_different_keys()
    {
        byte[] accumulator = [10, 20, 30];
        var sendA = new byte[32];
        var sendB = new byte[32];
        Spotify.Handshake.DeriveKeys([1, 1, 1, 1], accumulator, new byte[20], sendA, new byte[32]);
        Spotify.Handshake.DeriveKeys([2, 2, 2, 2], accumulator, new byte[20], sendB, new byte[32]);
        Assert.NotEqual(sendA, sendB);
    }

    [Fact]
    public void The_hello_frame_is_00_04_then_the_whole_frame_length()
    {
        byte[] proto = [9, 9, 9];
        var frame = new byte[6 + proto.Length];
        Assert.Equal(frame.Length, Spotify.Handshake.WriteHelloFrame(proto, frame));
        Assert.Equal(0x00, frame[0]);
        Assert.Equal(0x04, frame[1]);
        Assert.Equal(frame.Length, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(2)));
        Assert.Equal(proto, frame[6..]);
    }

    [Fact]
    public void The_response_frame_counts_itself_in_its_own_length()
    {
        byte[] proto = [7, 7];
        var frame = new byte[4 + proto.Length];
        Assert.Equal(frame.Length, Spotify.Handshake.WriteResponseFrame(proto, frame));
        Assert.Equal(frame.Length, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(frame));
    }

    [Theory]
    [InlineData(0u, 0)]           // nonsense
    [InlineData(3u, 0)]           // smaller than the prefix itself
    [InlineData(0x200000u, 0)]    // 2 MiB: untrusted, pre-auth, refused
    [InlineData(1024u, 1020)]
    public void The_ap_response_length_refuses_anything_out_of_range(uint size, int expected)
    {
        var prefix = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(prefix, size);
        Assert.Equal(expected, Spotify.Handshake.ApResponseBodyLength(prefix));
    }

    [Fact]
    public void The_preferred_locale_body_has_the_captured_shape()
    {
        var body = new byte[64];
        int n = Spotify.Handshake.WritePreferredLocale("en"u8, body);
        Assert.Equal(5 + 16 + 2, n);
        Assert.Equal(0x10, body[2]);
        Assert.Equal(0x02, body[4]);
        Assert.Equal("preferred-locale", System.Text.Encoding.ASCII.GetString(body, 5, 16));
        Assert.Equal("en", System.Text.Encoding.ASCII.GetString(body, 21, 2));
    }

    [Fact]
    public void A_garbage_signature_fails_closed()
    {
        Assert.False(Spotify.Handshake.VerifyGs(new byte[96], new byte[256]));
        Assert.False(Spotify.Handshake.VerifyGs(new byte[96], []));
    }
}

public class HashcashTests
{
    [Fact]
    public void The_solved_suffix_meets_the_difficulty()
    {
        byte[] prefix = Convert.FromHexString("0123456789abcdef0123456789abcdef01234567");
        const int target = 8;                                  // ~256 hashes on average

        var suffix = new byte[16];
        long tries = Spotify.Hashcash.Solve([], prefix, target, suffix);
        Assert.True(tries >= 1);

        var input = new byte[prefix.Length + 16];
        prefix.CopyTo(input, 0);
        suffix.CopyTo(input, prefix.Length);
        Assert.True(Spotify.Hashcash.LeadingZeroBits(System.Security.Cryptography.SHA1.HashData(input)) >= target);
    }

    [Fact]
    public void The_same_seed_solves_to_the_same_suffix()
    {
        byte[] prefix = [1, 2, 3, 4];
        var first = new byte[16];
        var second = new byte[16];
        Spotify.Hashcash.Solve("ctx"u8, prefix, 8, first);
        Spotify.Hashcash.Solve("ctx"u8, prefix, 8, second);
        Assert.Equal(first, second);       // seeded identically (all zero) ⇒ the search is deterministic
    }

    [Fact]
    public void Leading_zero_bits_counts_bits_and_not_bytes()
    {
        Assert.Equal(0, Spotify.Hashcash.LeadingZeroBits([0xFF]));
        Assert.Equal(8, Spotify.Hashcash.LeadingZeroBits([0x00, 0xFF]));
        Assert.Equal(12, Spotify.Hashcash.LeadingZeroBits([0x00, 0x0F]));
        Assert.Equal(1, Spotify.Hashcash.LeadingZeroBits([0x40]));
    }
}

public class PkceTests
{
    [Fact]
    public void The_challenge_matches_the_rfc7636_appendix_b_vector()
    {
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        const string expected = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
        Span<char> challenge = stackalloc char[64];
        int n = Spotify.Pkce.Challenge(verifier, challenge);
        Assert.Equal(expected, new string(challenge[..n]));
    }

    [Fact]
    public void A_verifier_is_the_right_length_and_charset()
    {
        Span<char> verifier = stackalloc char[64];
        Spotify.Pkce.NewVerifier(verifier);
        foreach (char c in verifier)
            Assert.Contains(c, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~");
    }

    [Fact]
    public void A_challenge_is_base64url_without_padding()
    {
        Span<char> verifier = stackalloc char[64];
        Spotify.Pkce.NewVerifier(verifier);
        Span<char> challenge = stackalloc char[64];
        int n = Spotify.Pkce.Challenge(verifier, challenge);
        string text = new(challenge[..n]);
        Assert.DoesNotContain('=', text);
        Assert.DoesNotContain('+', text);
        Assert.DoesNotContain('/', text);
    }
}

/// <summary>G-030 — the interactive sign-in's wire decisions (`Spotify.OAuth`), pinned against the shapes RFC 6749 / 7636 /
/// 8628 define and 0.2.9's `DeviceCodeFlowTests` drove: the authorize url, the loopback redirect, the token endpoint's
/// answers and the device-grant poll ladder. No socket, no clock.</summary>
public class OAuthTests
{
    const string State = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

    static byte[] Utf8(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    [Fact]
    public void The_authorize_url_carries_the_pkce_challenge_the_state_and_the_loopback_redirect()
    {
        string redirect = Spotify.OAuth.RedirectUri(53117);
        Assert.Equal("http://127.0.0.1:53117/login", redirect);

        string url = Spotify.OAuth.AuthorizeUrl("client-id", redirect, "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", State);
        Assert.StartsWith("https://accounts.spotify.com/authorize?client_id=client-id&response_type=code", url);
        Assert.Contains("&redirect_uri=http%3A%2F%2F127.0.0.1%3A53117%2Flogin", url);
        Assert.Contains("&code_challenge_method=S256&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", url);
        Assert.Contains("&state=" + State, url);
        Assert.Contains("&scope=streaming%20", url);
    }

    [Fact]
    public void The_form_bodies_are_url_encoded()
    {
        Assert.Equal("grant_type=authorization_code&code=a%2Fb&redirect_uri=http%3A%2F%2F127.0.0.1%3A1%2Flogin&client_id=c&code_verifier=v",
            Spotify.OAuth.CodeExchangeForm("a/b", "http://127.0.0.1:1/login", "c", "v"));
        Assert.Equal("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code&device_code=DC&client_id=c",
            Spotify.OAuth.DevicePollForm("DC", "c"));
        Assert.StartsWith("client_id=c&scope=streaming%20", Spotify.OAuth.DeviceAuthorizeForm("c"));
    }

    [Fact]
    public void The_request_target_is_the_get_line_or_nothing()
    {
        Assert.Equal("/login?code=x&state=y",
            System.Text.Encoding.ASCII.GetString(Spotify.OAuth.RequestTarget(Utf8("GET /login?code=x&state=y HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"))));
        Assert.True(Spotify.OAuth.RequestTarget(Utf8("")).IsEmpty);                       // a pre-connect that said nothing
        Assert.True(Spotify.OAuth.RequestTarget(Utf8("GET /login?code=x")).IsEmpty);      // no end of line yet
        Assert.True(Spotify.OAuth.RequestTarget(Utf8("POST /login HTTP/1.1\r\n")).IsEmpty);
    }

    [Fact]
    public void A_redirect_with_our_state_yields_the_unescaped_code()
    {
        var outcome = Spotify.OAuth.ParseRedirect(Utf8("/login?code=AQ%2Bb_c-d&state=" + State), State, out string code);
        Assert.Equal(Spotify.OAuth.Redirect.Code, outcome);
        Assert.Equal("AQ+b_c-d", code);
    }

    [Fact]
    public void A_redirect_is_believed_only_with_our_state()
    {
        Assert.Equal(Spotify.OAuth.Redirect.StateMismatch, Spotify.OAuth.ParseRedirect(Utf8("/login?code=abc&state=other"), State, out _));
        Assert.Equal(Spotify.OAuth.Redirect.StateMismatch, Spotify.OAuth.ParseRedirect(Utf8("/login?code=abc"), State, out _));
        // A refusal carrying somebody else's state is not this sign-in's refusal either.
        Assert.Equal(Spotify.OAuth.Redirect.StateMismatch, Spotify.OAuth.ParseRedirect(Utf8("/login?error=access_denied&state=x"), State, out _));
    }

    [Fact]
    public void A_refusal_a_stray_path_and_an_empty_answer_are_told_apart()
    {
        Assert.Equal(Spotify.OAuth.Redirect.Denied, Spotify.OAuth.ParseRedirect(Utf8("/login?error=access_denied&state=" + State), State, out string code));
        Assert.Equal("", code);
        Assert.Equal(Spotify.OAuth.Redirect.NotOurs, Spotify.OAuth.ParseRedirect(Utf8("/favicon.ico"), State, out _));
        Assert.Equal(Spotify.OAuth.Redirect.Incomplete, Spotify.OAuth.ParseRedirect(Utf8("/login?state=" + State), State, out _));
    }

    [Fact]
    public void A_token_answer_yields_the_access_token_and_its_lifetime()
    {
        var answer = Spotify.OAuth.ParseToken(200, Utf8("""{"access_token":"AT","token_type":"Bearer","expires_in":3599,"refresh_token":"RT","scope":"streaming"}"""),
            out string token, out int expires);
        Assert.Equal(Spotify.OAuth.TokenAnswer.Token, answer);
        Assert.Equal("AT", token);
        Assert.Equal(3599, expires);
    }

    [Theory]
    [InlineData(400, """{"error":"authorization_pending"}""", Spotify.OAuth.TokenAnswer.Pending)]
    [InlineData(400, """{"error":"slow_down"}""", Spotify.OAuth.TokenAnswer.SlowDown)]
    [InlineData(400, """{"error":"expired_token"}""", Spotify.OAuth.TokenAnswer.Expired)]
    [InlineData(400, """{"error":"access_denied"}""", Spotify.OAuth.TokenAnswer.Denied)]
    [InlineData(400, """{"error":"invalid_grant","error_description":"Invalid authorization code"}""", Spotify.OAuth.TokenAnswer.Refused)]
    [InlineData(500, """{"error":"server_error"}""", Spotify.OAuth.TokenAnswer.Transient)]
    [InlineData(502, "<html>Bad Gateway</html>", Spotify.OAuth.TokenAnswer.Transient)]
    [InlineData(200, "", Spotify.OAuth.TokenAnswer.Transient)]
    [InlineData(200, """{"token_type":"Bearer"}""", Spotify.OAuth.TokenAnswer.Transient)]
    public void Every_other_token_answer_folds_to_its_rung(int status, string body, Spotify.OAuth.TokenAnswer expected)
    {
        Assert.Equal(expected, Spotify.OAuth.ParseToken(status, Utf8(body), out string token, out _));
        Assert.Equal("", token);
    }

    [Fact]
    public void A_device_authorization_answer_parses_and_falls_back_sensibly()
    {
        Assert.True(Spotify.OAuth.TryParseDeviceCode(200, Utf8("""{"device_code":"DC","user_code":"ABCD","verification_uri":"https://spotify.com/pair","verification_uri_complete":"https://spotify.com/pair?code=ABCD","expires_in":600,"interval":5}"""),
            out var full));
        Assert.Equal(new Spotify.OAuth.DeviceCode("DC", "ABCD", "https://spotify.com/pair", "https://spotify.com/pair?code=ABCD", 600, 5), full);

        Assert.True(Spotify.OAuth.TryParseDeviceCode(200, Utf8("""{"device_code":"DC","user_code":"WXYZ","verification_uri":"https:\/\/spotify.com\/pair","expires_in":300}"""),
            out var bare));
        Assert.Equal("https://spotify.com/pair", bare.VerificationUriComplete);   // an escaped slash, and the missing complete uri
        Assert.Equal(5, bare.IntervalSeconds);                                     // RFC 8628's default

        Assert.False(Spotify.OAuth.TryParseDeviceCode(400, Utf8("""{"error":"invalid_client"}"""), out _));
        Assert.False(Spotify.OAuth.TryParseDeviceCode(200, Utf8("""{"device_code":"DC","verification_uri":"u","expires_in":300}"""), out _));
        Assert.False(Spotify.OAuth.TryParseDeviceCode(200, Utf8("not json"), out _));
    }

    [Fact]
    public void The_poll_ladder_widens_only_on_slow_down_and_stops_on_a_terminal_answer()
    {
        Assert.Equal(10_000, Spotify.OAuth.NextPollIntervalMs(Spotify.OAuth.TokenAnswer.SlowDown, 5_000));
        Assert.Equal(15_000, Spotify.OAuth.NextPollIntervalMs(Spotify.OAuth.TokenAnswer.SlowDown, 10_000));
        Assert.Equal(5_000, Spotify.OAuth.NextPollIntervalMs(Spotify.OAuth.TokenAnswer.Pending, 5_000));
        Assert.Equal(5_000, Spotify.OAuth.NextPollIntervalMs(Spotify.OAuth.TokenAnswer.Transient, 5_000));

        Assert.True(Spotify.OAuth.KeepsPolling(Spotify.OAuth.TokenAnswer.Pending));
        Assert.True(Spotify.OAuth.KeepsPolling(Spotify.OAuth.TokenAnswer.SlowDown));
        Assert.True(Spotify.OAuth.KeepsPolling(Spotify.OAuth.TokenAnswer.Transient));   // one blip must not kill a pairing
        Assert.False(Spotify.OAuth.KeepsPolling(Spotify.OAuth.TokenAnswer.Token));
        Assert.False(Spotify.OAuth.KeepsPolling(Spotify.OAuth.TokenAnswer.Expired));
        Assert.False(Spotify.OAuth.KeepsPolling(Spotify.OAuth.TokenAnswer.Denied));
        Assert.False(Spotify.OAuth.KeepsPolling(Spotify.OAuth.TokenAnswer.Refused));
    }

    [Fact]
    public void The_sign_in_state_moves_one_flow_at_a_time()
    {
        var idle = Spotify.SignInState.Idle;
        Assert.False(idle.HasChallenge);
        var waiting = idle.With(Spotify.SignInMethod.Browser, Spotify.SignInStage.Waiting);
        Assert.Equal(Spotify.SignInStage.Waiting, waiting.Browser);
        Assert.Equal(Spotify.SignInStage.Idle, waiting.Code);
        var coded = waiting with { Code = Spotify.SignInStage.Waiting, UserCode = "ABCD" };
        Assert.True(coded.HasChallenge);
        var failed = coded.With(Spotify.SignInMethod.DeviceCode, Spotify.SignInStage.Failed, Spotify.SignInError.Network);
        Assert.Equal(Spotify.SignInStage.Waiting, failed.Browser);
        Assert.Equal(Spotify.SignInError.Network, failed.Error);
        Assert.False(failed.HasChallenge);
    }
}

/// <summary>G-073 — the two dealer MESSAGE topics beside the cluster: the inbound volume (its `SetVolumeCommand` read the way
/// 0.2.9 read it, and the body our own `Connect.VolumeBody` writes reads back) and the logout request.</summary>
public class DealerTopicTests
{
    static Spotify.DealerMessage Message(string uri, byte[] scratch)
        => Spotify.DealerFrame.Parse(System.Text.Encoding.UTF8.GetBytes("{\"type\":\"message\",\"uri\":\"" + uri + "\",\"payloads\":[]}"), scratch);

    [Fact]
    public void The_volume_and_logout_topics_are_classified_and_the_cluster_is_neither()
    {
        var scratch = new byte[1024];
        var volume = Message("hm://connect-state/v1/connect/volume", scratch);
        Assert.True(volume.IsConnectVolume);
        Assert.False(volume.IsConnectLogout);
        Assert.False(volume.IsClusterUpdate);

        var logout = Message("hm://connect-state/v1/connect/logout", scratch);
        Assert.True(logout.IsConnectLogout);
        Assert.False(logout.IsConnectVolume);

        var cluster = Message("hm://connect-state/v1/cluster", scratch);
        Assert.False(cluster.IsConnectVolume);
        Assert.False(cluster.IsConnectLogout);
    }

    [Fact]
    public void A_set_volume_command_reads_its_volume_and_the_senders_message_id()
    {
        // volume 19496, command_options { message_id 300 }, logging_params {}, connection_type "wlan"
        byte[] body = [0x08, 0xa8, 0x98, 0x01, 0x12, 0x03, 0x08, 0xac, 0x02, 0x1a, 0x00, 0x22, 0x04, (byte)'w', (byte)'l', (byte)'a', (byte)'n'];
        Assert.True(Spotify.DealerFrame.TryReadSetVolume(body, out int volume, out uint messageId));
        Assert.Equal(19496, volume);
        Assert.Equal(300u, messageId);
    }

    [Fact]
    public void The_volume_body_we_send_reads_back()
    {
        Span<byte> body = stackalloc byte[16];
        int n = Spotify.Connect.VolumeBody(65535, body);
        Assert.True(Spotify.DealerFrame.TryReadSetVolume(body[..n], out int volume, out uint messageId));
        Assert.Equal(65535, volume);
        Assert.Equal(0u, messageId);
    }

    [Fact]
    public void A_body_without_a_volume_or_a_truncated_one_is_refused()
    {
        Assert.False(Spotify.DealerFrame.TryReadSetVolume([], out _, out _));
        Assert.False(Spotify.DealerFrame.TryReadSetVolume([0x22, 0x04, (byte)'w', (byte)'l', (byte)'a', (byte)'n'], out _, out _));
        Assert.False(Spotify.DealerFrame.TryReadSetVolume([0x08, 0xa8], out _, out _));   // the varint never ends
    }
}

public class SpotifyTextTests
{
    [Fact]
    public void Hex_round_trips_a_file_id()
    {
        byte[] fileId = new byte[20];
        for (int i = 0; i < fileId.Length; i++) fileId[i] = (byte)(i * 7);
        Span<char> hex = stackalloc char[40];
        Assert.Equal(40, Spotify.Hex.Encode(fileId, hex));
        Assert.Equal(Convert.ToHexStringLower(fileId), new string(hex));

        var back = new byte[20];
        Assert.True(Spotify.Hex.TryDecode(hex, back));
        Assert.Equal(fileId, back);
    }

    [Fact]
    public void Hex_refuses_an_odd_length_or_a_non_digit()
    {
        Assert.False(Spotify.Hex.TryDecode("abc", new byte[2]));
        Assert.False(Spotify.Hex.TryDecode("zz", new byte[1]));
    }

    [Fact]
    public void ProductXml_reads_the_tier_and_an_element()
    {
        ReadOnlySpan<byte> xml = "<products><product><type>premium</type><catalogue>premium</catalogue><country>NL</country></product></products>"u8;
        Assert.Equal(Spotify.Tier.Premium, Spotify.ProductXml.TierOf(xml));
        Assert.Equal("NL", System.Text.Encoding.UTF8.GetString(Spotify.ProductXml.Value(xml, "country"u8)));
    }

    [Fact]
    public void ProductXml_treats_a_missing_or_broken_product_as_unknown()
    {
        Assert.Equal(Spotify.Tier.Unknown, Spotify.ProductXml.TierOf("<products></products>"u8));
        Assert.Equal(Spotify.Tier.Unknown, Spotify.ProductXml.TierOf("not xml at all"u8));
        Assert.Equal(Spotify.Tier.Free, Spotify.ProductXml.TierOf("<product><type>free</type></product>"u8));
    }

    [Fact]
    public void Apresolve_hosts_come_back_in_order_as_ranges()
    {
        ReadOnlySpan<byte> json = """
            {"accesspoint":["ap-gew4.spotify.com:4070","ap-gue1.spotify.com:443"],"dealer":["dealer.spotify.com:443"]}
            """u8;
        Span<Range> ranges = stackalloc Range[8];
        int n = Spotify.ParseHosts(json, "accesspoint"u8, ranges);
        Assert.Equal(2, n);
        Assert.Equal("ap-gew4.spotify.com:4070", System.Text.Encoding.UTF8.GetString(json[ranges[0]]));
        Assert.Equal("ap-gue1.spotify.com:443", System.Text.Encoding.UTF8.GetString(json[ranges[1]]));

        Assert.Equal(1, Spotify.ParseHosts(json, "dealer"u8, ranges));
        Assert.Equal(0, Spotify.ParseHosts(json, "spclient"u8, ranges));
    }

    [Fact]
    public void A_host_port_entry_splits_and_falls_back()
    {
        var (length, port) = Spotify.SplitHostPort("ap-gew4.spotify.com:4070"u8, 443);
        Assert.Equal("ap-gew4.spotify.com".Length, length);
        Assert.Equal(4070, port);

        var (bare, fallback) = Spotify.SplitHostPort("dealer.spotify.com"u8, 443);
        Assert.Equal("dealer.spotify.com".Length, bare);
        Assert.Equal(443, fallback);
    }
}

public class ServerClockTests
{
    [Fact]
    public void A_probe_round_keeps_the_lowest_rtt_sample_with_midpoint_correction()
    {
        var session = default(Spotify.Session);
        long best = long.MaxValue;
        const long trueOffset = 4000;

        // three samples, rtt 100 / 40 / 80 — the middle one wins.
        Spotify.ObserveClockProbe(ref session, 1000, 1100 + trueOffset, 1100, ref best);
        Spotify.ObserveClockProbe(ref session, 1100, 1140 + trueOffset, 1140, ref best);
        Spotify.ObserveClockProbe(ref session, 1140, 1220 + trueOffset, 1220, ref best);

        Assert.True(session.ClockSynced);
        Assert.True(session.ClockProbed);
        Assert.Equal(40, session.ClockRttMs);
        Assert.Equal(4020, session.ClockOffsetMs);   // true 4000 + half of the 40 ms round trip
    }

    [Fact]
    public void Server_now_is_zero_until_the_clock_is_synced()
    {
        var session = default(Spotify.Session);
        Assert.Equal(0, Spotify.ServerNowMs(session, 1000));
    }

    [Fact]
    public void A_passive_sample_bootstraps_the_offset_but_never_overrides_a_probe()
    {
        var session = default(Spotify.Session);
        Assert.False(Spotify.ObservePassiveClock(ref session, 6000, 2000));
        Assert.True(session.ClockSynced);
        Assert.False(session.ClockProbed);
        Assert.Equal(6000, Spotify.ServerNowMs(session, 2000));

        long best = long.MaxValue;
        Spotify.ObserveClockProbe(ref session, 1000, 5000, 1000, ref best);
        long probed = session.ClockOffsetMs;
        Assert.False(Spotify.ObservePassiveClock(ref session, 1000 + probed, 1000));
        Assert.Equal(probed, session.ClockOffsetMs);
    }

    [Fact]
    public void A_passive_sample_far_from_the_probed_offset_asks_for_an_early_re_probe()
    {
        var session = default(Spotify.Session);
        long best = long.MaxValue;
        Spotify.ObserveClockProbe(ref session, 1000, 5000, 1000, ref best);
        Assert.True(Spotify.ObservePassiveClock(ref session, 5000 + Spotify.ClockDriftTriggerMs + 1, 1000));
    }
}
