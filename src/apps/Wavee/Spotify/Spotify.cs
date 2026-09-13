// ── Spotify/Spotify.cs ─────────────────────────────────────────────────────────────────────────────────────────────
// Session struct, Shannon, DH, hashcash, PKCE, base62, dealer frame, request fold
//
// Role: CORE
// Owner: D
// Wave: 2
// Budget: 1400 lines
// Spec: plan
//
// THE PROTOCOL, WITHOUT A SOCKET. Everything Spotify's wire demands that is a *decision* rather than an I/O call
// lives here: the session state machine, the Shannon packet codec, the AP handshake fold, the two proof-of-work
// puzzles, the dealer frame parse, the audio-key packet shapes, and the request fold that turns "I want the album
// page" into (verb, host, path, header set, body). `Spotify.Session.cs` (SHELL) owns the threads and the sockets and
// calls in here; `Spotify.Api.cs` (owner F) is one function per request over `Build`.
//
// The rules this file is written under (design record §5.11/§5.12, P1-P16 / C1-C10):
//   P8/P9  no allocation after warm-up, no LINQ, no closures, no async, no boxing. Spans in, spans out: every
//          routine that produces bytes or characters writes into a buffer the caller owns and returns the length.
//          The four allocating exceptions are named where they happen and all four are once-per-login:
//          `Handshake.PublicKey`/`SharedSecret` (BigInteger.ModPow), `Handshake.VerifyGs` (RSA), and
//          `DealerFrame.Parse` ONLY when the frame is gzipped (GZipStream over the caller's scratch array).
//   P4     the batch is the API: `Build` takes the whole request, never a field at a time.
//   C1     nothing here touches a table, an edge, a signal or the interner. The SHELL decodes on its own thread and
//          posts; the UI drain is the only writer. `Session` is a VALUE — a shell reads a snapshot, never a field.
//   C2     inputs are values. `Step(ref Session, in SessionEvent)` is the whole state machine, and every transition
//          in it is a test in `SpotifyCoreTests`.
//   C4     `Session.Epoch` bumps whenever a shell must abandon in-flight work; an answer stamped with an older epoch
//          is dropped rather than applied.
//
// WHY THE TOKENS ARE SPANS AND NOT STRINGS. A session's secrets (the access token, the client token, the connection
// id, the resolved hosts) are read by the HTTP and websocket shells, on their own threads. `StringId` cannot cross
// that line — `Entities/Store.cs`'s header states the rule: "the store thread NEVER resolves", because a released id
// is recycled 16 ticks later. So the text lives in `Spotify.Text`, an APPEND-ONLY UTF-8 arena (Spotify.Session.cs):
// an entry is written once and never moved or overwritten, growth copies into a larger array while leaving the old
// one intact, and a reader that took the array reference keeps reading valid bytes forever. A `TokenRef` is (offset,
// length) into it. `StringId` is kept for the three fields the UI itself paints (username, country, product).

using System.Buffers.Binary;
using System.Buffers.Text;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using FluentGpu.Foundation;

namespace Wavee;

public static partial class Spotify
{
    // ── 1. the session as a value ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Where the session is. The ladder is strictly ordered: every phase below <see cref="SessionPhase.Online"/>
    /// has exactly one effect the shell must be running for it (see <see cref="Step"/>).</summary>
    public enum SessionPhase : byte
    {
        /// <summary>Nothing is running. The resting state, and the state a logout returns to.</summary>
        Offline = 0,
        /// <summary>Asking apresolve for access points / spclient / dealer hosts.</summary>
        Resolving = 1,
        /// <summary>A TCP socket is opening to an access point.</summary>
        Connecting = 2,
        /// <summary>ClientHello sent; the DH exchange and the RSA check are in flight.</summary>
        Handshaking = 3,
        /// <summary>The Shannon channel is up and the credential has been presented.</summary>
        Authenticating = 4,
        /// <summary>Welcome received; the client-token and the login5 access token are being minted.</summary>
        Minting = 5,
        /// <summary>Tokens held, dealer connected. The only phase in which requests may be built.</summary>
        Online = 6,
        /// <summary>Something dropped and a backoff is being served before the next attempt.</summary>
        Reconnecting = 7,
        /// <summary>Terminal for this credential: see <see cref="Session.Fault"/>. Only a new login leaves it.</summary>
        Failed = 8,
    }

    /// <summary>Why the session stopped. A fault is what the UI reports and what decides whether the stored credential
    /// survives: only <see cref="SessionFault.CredentialRejected"/> clears it (0.2.9's rule, and its reason — a
    /// connectivity blip must never wipe a valid credential).</summary>
    public enum SessionFault : byte
    {
        None = 0, NoCredential, CredentialRejected, NoAccessPoint, Network, Protocol, TokenRefused, NotPremium,
    }

    /// <summary>The account's product tier, from the AP's ProductInfo push (cmd 0x50) — the authoritative source
    /// (0.2.9 `SpotifyLiveLogin`: a missing push is treated as Free, never optimistically as Premium).</summary>
    public enum Tier : byte { Unknown = 0, Free = 1, Premium = 2 }

    /// <summary>A slice of the session text arena. Valid for the life of the process once written (file header).</summary>
    public readonly record struct TokenRef(int Offset, int Length)
    {
        public bool IsEmpty => Length == 0;
    }

    /// <summary>THE session, as a value. Copied out of <c>Spotify.Current</c> by whoever reads it, folded by
    /// <see cref="Step"/>, published by the shell. No references, so a copy is a memcpy and a reader on another
    /// thread cannot observe half a transition (the publication swaps an immutable box).</summary>
    public struct Session
    {
        public SessionPhase Phase;
        public SessionFault Fault;
        public Tier Tier;
        /// <summary>Bumps whenever a shell must abandon in-flight work (C4): a drop, a logout, a scope switch.</summary>
        public uint Epoch;
        /// <summary>Consecutive failed attempts; the backoff ladder reads it (<see cref="BackoffMs"/>).</summary>
        public uint Attempt;

        // identity the UI paints — interned on the UI thread, never resolved off it (C1).
        public StringId Username, Country, Product;

        // text the shells read as spans — append-only arena, safe from any thread (file header).
        public TokenRef DeviceId, ClientId, Locale, AccessToken, ClientToken, ConnectionId, SpclientHost, DealerHost;

        /// <summary>Unix ms after which <see cref="AccessToken"/> must be re-minted. 0 = none held.</summary>
        public long AccessExpiresAtMs;
        /// <summary>Unix ms after which <see cref="ClientToken"/> must be re-minted. 0 = none held.</summary>
        public long ClientTokenExpiresAtMs;

        // the server clock (the ownership fence reads it): serverNow = localNow + ClockOffsetMs, iff ClockSynced.
        public long ClockOffsetMs, ClockRttMs;
        public bool ClockSynced, ClockProbed;

        /// <summary>A reusable credential is on disk for this account.</summary>
        public bool HasCredential;

        public readonly bool IsOnline => Phase == SessionPhase.Online;

        /// <summary>Requests may be built: a bearer is held and has not expired as of <paramref name="nowMs"/>.</summary>
        public readonly bool CanRequest(long nowMs) => !AccessToken.IsEmpty && nowMs < AccessExpiresAtMs;
    }

    /// <summary>What happened, as a value (C2). Everything a shell learns becomes one of these and is folded on the
    /// UI thread; nothing else writes a session field.</summary>
    public enum SessionEventKind : byte
    {
        None = 0,
        /// <summary>A login was asked for. <c>Flag</c> = a stored credential is present.</summary>
        Login,
        /// <summary>apresolve answered. <c>Text</c> = the spclient host, <c>Text2</c> = the dealer host.</summary>
        Hosts,
        /// <summary>The TCP socket is open to an access point.</summary>
        Connected,
        /// <summary>The Shannon channel is negotiated (gs verified, keys derived).</summary>
        HandshakeOk,
        /// <summary>APWelcome. <c>Id</c> = username, <c>Id2</c> = country, <c>Id3</c> = product, <c>Number</c> = tier.</summary>
        Welcome,
        /// <summary>The AP rejected the credential. Terminal: the stored credential is cleared.</summary>
        AuthRejected,
        /// <summary>The client-token was minted. <c>Text</c> = the token, <c>Number</c> = expiry (unix ms).</summary>
        ClientTokenMinted,
        /// <summary>login5 answered. <c>Text</c> = the access token, <c>Number</c> = expiry (unix ms).</summary>
        AccessTokenMinted,
        /// <summary>The dealer websocket is up and sent its connection id. <c>Text</c> = the id.</summary>
        DealerOnline,
        /// <summary>Any socket dropped, or a token was refused. <c>Number</c> = a <see cref="SessionFault"/>.</summary>
        Dropped,
        /// <summary>The backoff elapsed; try again.</summary>
        Retry,
        /// <summary>The user signed out.</summary>
        Logout,
    }

    /// <summary>One folded event. A value with no spans, so a shell can post it across a thread (C2).</summary>
    public readonly record struct SessionEvent(
        SessionEventKind Kind,
        StringId Id = default, StringId Id2 = default, StringId Id3 = default,
        TokenRef Text = default, TokenRef Text2 = default,
        long Number = 0, bool Flag = false, uint Epoch = 0);

    /// <summary>What the shell must start or stop after a fold. A SET, not a list: ten events in one drain produce
    /// one union, so a burst opens one socket (C3).</summary>
    [Flags]
    public enum SessionEffects : uint
    {
        None = 0,
        ResolveHosts = 1 << 0,
        OpenAp = 1 << 1,
        MintClientToken = 1 << 2,
        MintAccessToken = 1 << 3,
        OpenDealer = 1 << 4,
        /// <summary>Persist the reusable credential the welcome carried.</summary>
        SaveCredential = 1 << 5,
        /// <summary>Wipe the stored credential — ONLY on a genuine rejection.</summary>
        ClearCredential = 1 << 6,
        /// <summary>Serve <see cref="BackoffMs"/> and then post <see cref="SessionEventKind.Retry"/>.</summary>
        Backoff = 1 << 7,
        /// <summary>Tear every socket down; the epoch has moved (C4).</summary>
        CloseAll = 1 << 8,
    }

    /// <summary>THE state machine. Pure: same session + same event ⇒ same session + same effects, no clock, no socket,
    /// no allocation. Every transition in it is a test.</summary>
    public static SessionEffects Step(ref Session s, in SessionEvent e)
    {
        switch (e.Kind)
        {
            case SessionEventKind.Login:
                s.HasCredential = e.Flag;
                if (!e.Flag) { s.Phase = SessionPhase.Failed; s.Fault = SessionFault.NoCredential; return SessionEffects.None; }
                s.Phase = SessionPhase.Resolving;
                s.Fault = SessionFault.None;
                s.Epoch++;
                return SessionEffects.ResolveHosts;

            case SessionEventKind.Hosts:
                if (s.Phase is SessionPhase.Offline or SessionPhase.Failed) return SessionEffects.None;
                s.SpclientHost = e.Text;
                s.DealerHost = e.Text2;
                s.Phase = SessionPhase.Connecting;
                return SessionEffects.OpenAp;

            case SessionEventKind.Connected:
                if (s.Phase != SessionPhase.Connecting) return SessionEffects.None;
                s.Phase = SessionPhase.Handshaking;
                return SessionEffects.None;

            case SessionEventKind.HandshakeOk:
                if (s.Phase != SessionPhase.Handshaking) return SessionEffects.None;
                s.Phase = SessionPhase.Authenticating;
                return SessionEffects.None;

            case SessionEventKind.Welcome:
                if (s.Phase is SessionPhase.Offline or SessionPhase.Failed) return SessionEffects.None;
                s.Username = e.Id;
                s.Country = e.Id2;
                s.Product = e.Id3;
                s.Tier = (Tier)(byte)e.Number;
                s.HasCredential = true;
                s.Attempt = 0;
                s.Phase = SessionPhase.Minting;
                return SessionEffects.SaveCredential | SessionEffects.MintClientToken;

            case SessionEventKind.AuthRejected:
                s.Phase = SessionPhase.Failed;
                s.Fault = SessionFault.CredentialRejected;
                s.HasCredential = false;
                s.Epoch++;
                return SessionEffects.ClearCredential | SessionEffects.CloseAll;

            case SessionEventKind.ClientTokenMinted:
                if (s.Phase is SessionPhase.Offline or SessionPhase.Failed) return SessionEffects.None;
                s.ClientToken = e.Text;
                s.ClientTokenExpiresAtMs = e.Number;
                // The bearer's mint is gated on the attestation, so the two are ordered, not parallel.
                return SessionEffects.MintAccessToken;

            case SessionEventKind.AccessTokenMinted:
                if (s.Phase is SessionPhase.Offline or SessionPhase.Failed) return SessionEffects.None;
                s.AccessToken = e.Text;
                s.AccessExpiresAtMs = e.Number;
                // A refresh while already Online must not re-open the dealer: it holds a live socket on this token.
                return s.Phase == SessionPhase.Online ? SessionEffects.None : SessionEffects.OpenDealer;

            case SessionEventKind.DealerOnline:
                if (s.Phase is SessionPhase.Offline or SessionPhase.Failed) return SessionEffects.None;
                s.ConnectionId = e.Text;
                s.Phase = SessionPhase.Online;
                s.Fault = SessionFault.None;
                s.Attempt = 0;
                return SessionEffects.None;

            case SessionEventKind.Dropped:
                if (s.Phase is SessionPhase.Offline or SessionPhase.Failed) return SessionEffects.None;
                s.Fault = (SessionFault)(byte)e.Number;
                s.ConnectionId = default;
                s.Epoch++;
                s.Attempt++;
                s.Phase = SessionPhase.Reconnecting;
                return SessionEffects.CloseAll | SessionEffects.Backoff;

            case SessionEventKind.Retry:
                if (s.Phase != SessionPhase.Reconnecting) return SessionEffects.None;
                s.Phase = SessionPhase.Resolving;
                return SessionEffects.ResolveHosts;

            case SessionEventKind.Logout:
                uint epoch = s.Epoch + 1;
                s = default;
                s.Epoch = epoch;
                return SessionEffects.CloseAll | SessionEffects.ClearCredential;

            default:
                return SessionEffects.None;
        }
    }

    /// <summary>The reconnect ladder: 3, 6, 12, 24, capped at 30 s — 0.2.9's `LiveDealerTransport` ladder, folded so
    /// the delay is a function of the session and not of a captured local.</summary>
    public static int BackoffMs(in Session s)
    {
        int shift = (int)Math.Min(s.Attempt == 0 ? 0u : s.Attempt - 1, 4u);
        return Math.Min(30, 3 * (1 << shift)) * 1000;
    }

    // ── 2. the server clock (ported from Backend/SpotifyServerClock.cs, folded into the session) ─────────────────────
    //
    // Cluster snapshots carry the SERVER's clock. To age a remote position by the transit that elapsed since it was
    // emitted, "now" has to be in the server's domain. The estimate is NTP-shaped: probe with round-trip correction,
    // keep the lowest-RTT sample of a round, and take the free per-cluster timestamp as a bootstrap. A passive sample
    // is one-way-downlink biased, so it NEVER overrides a probe — it only seeds the offset before the first probe
    // lands and reports gross drift.

    /// <summary>Drift (ms) between a passive sample and a probed offset that warrants an early re-probe.</summary>
    public const long ClockDriftTriggerMs = 2000;

    /// <summary>The free sample every cluster carries. Returns true when the caller should re-probe (drift).</summary>
    public static bool ObservePassiveClock(ref Session s, long serverTimestampMs, long localNowMs)
    {
        if (serverTimestampMs <= 0) return false;
        long passive = serverTimestampMs - localNowMs;
        if (!s.ClockSynced) { s.ClockOffsetMs = passive; s.ClockSynced = true; return false; }
        return s.ClockProbed && Math.Abs(passive - s.ClockOffsetMs) > ClockDriftTriggerMs;
    }

    /// <summary>One probe sample: local t1, the server's answer, local t2. Midpoint-corrected and kept only when its
    /// round trip beats the best of this round (the caller threads <paramref name="bestRtt"/> through the samples,
    /// starting at <see cref="long.MaxValue"/>).</summary>
    public static void ObserveClockProbe(ref Session s, long t1, long serverMs, long t2, ref long bestRtt)
    {
        if (serverMs <= 0) return;
        long rtt = Math.Max(0, t2 - t1);
        if (rtt >= bestRtt) return;
        bestRtt = rtt;
        s.ClockOffsetMs = serverMs - ((t1 + t2) / 2);   // symmetric latency ⇒ the midpoint is "now"
        s.ClockRttMs = rtt;
        s.ClockSynced = true;
        s.ClockProbed = true;
    }

    /// <summary>Server "now" in unix ms, or 0 — the unsynced sentinel the ownership fold reads to skip the
    /// offset-dependent term (it still applies the always-safe server-side delta).</summary>
    public static long ServerNowMs(in Session s, long localNowMs) => s.ClockSynced ? localNowMs + s.ClockOffsetMs : 0L;

    // ── 3. Shannon (the AP packet cipher) ────────────────────────────────────────────────────────────────────────────
    //
    // LIFTED protocol mechanic: a fixed spec that must match librespot's shannon crate bit for bit, pinned by the
    // vectors in `SpotifyCoreTests`. Ported from _old/Wavee/Backend/Spotify/Shannon.cs with one change: the three
    // 16-word registers are `[InlineArray]` fields of a STRUCT, so a codec lives in the AP thread's own frame and a
    // packet costs zero allocations (0.2.9 allocated three `uint[16]` per cipher, two ciphers per connection).

    /// <summary>Sixteen 32-bit words — a Shannon register.</summary>
    [InlineArray(ShannonWords)]
    internal struct ShannonRegister { uint _e0; }

    internal const int ShannonWords = 16;

    /// <summary>The Shannon stream cipher (Rose, Qualcomm) — Spotify's AP packet codec. Mutable struct: hold it in a
    /// field and pass it by <c>ref</c>; a copy forks the keystream.</summary>
    public struct Shannon
    {
        const uint InitKonst = 0x6996c53a;
        const int Keyp = 13;

        ShannonRegister _r, _crc, _initR;
        uint _konst, _sbuf, _mbuf;
        int _nbuf;

        /// <summary>A cipher over a 32-byte key (the derived send or receive key).</summary>
        public Shannon(ReadOnlySpan<byte> key)
        {
            if (key.Length != 32) throw new ArgumentException("Shannon key must be exactly 32 bytes", nameof(key));
            this = default;       // every field, including the three registers, before any method may read `this`
            InitState();
            LoadKey(key);
            _konst = _r[0];
            for (int i = 0; i < ShannonWords; i++) _initR[i] = _r[i];
        }

        /// <summary>Re-key for one packet: the per-direction packet counter, big-endian.</summary>
        public void Nonce(uint nonce)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, nonce);
            for (int i = 0; i < ShannonWords; i++) _r[i] = _initR[i];
            _konst = InitKonst;
            LoadKey(b);
            _konst = _r[0];
            _nbuf = 0;
            _mbuf = 0;
        }

        public void Encrypt(Span<byte> buffer)
        {
            int offset = 0, remaining = buffer.Length;
            while (_nbuf != 0 && remaining > 0)
            {
                _mbuf ^= (uint)buffer[offset] << (32 - _nbuf);
                buffer[offset] ^= (byte)((_sbuf >> (32 - _nbuf)) & 0xFF);
                offset++; _nbuf -= 8; remaining--;
            }
            if (_nbuf == 0 && offset > 0) { MacFunc(_mbuf); _mbuf = 0; }
            while (remaining >= 4)
            {
                Cycle();
                uint pt = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset, 4));
                MacFunc(pt);
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(offset, 4), pt ^ _sbuf);
                offset += 4; remaining -= 4;
            }
            if (remaining > 0)
            {
                Cycle(); _mbuf = 0; _nbuf = 32;
                while (remaining > 0)
                {
                    _mbuf ^= (uint)buffer[offset] << (32 - _nbuf);
                    buffer[offset] ^= (byte)((_sbuf >> (32 - _nbuf)) & 0xFF);
                    offset++; _nbuf -= 8; remaining--;
                }
            }
        }

        public void Decrypt(Span<byte> buffer)
        {
            int offset = 0, remaining = buffer.Length;
            while (_nbuf != 0 && remaining > 0)
            {
                buffer[offset] ^= (byte)((_sbuf >> (32 - _nbuf)) & 0xFF);
                _mbuf ^= (uint)buffer[offset] << (32 - _nbuf);
                offset++; _nbuf -= 8; remaining--;
            }
            if (_nbuf == 0 && offset > 0) { MacFunc(_mbuf); _mbuf = 0; }
            while (remaining >= 4)
            {
                Cycle();
                uint ct = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset, 4));
                uint pt = ct ^ _sbuf;
                MacFunc(pt);
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(offset, 4), pt);
                offset += 4; remaining -= 4;
            }
            if (remaining > 0)
            {
                Cycle(); _mbuf = 0; _nbuf = 32;
                while (remaining > 0)
                {
                    buffer[offset] ^= (byte)((_sbuf >> (32 - _nbuf)) & 0xFF);
                    _mbuf ^= (uint)buffer[offset] << (32 - _nbuf);
                    offset++; _nbuf -= 8; remaining--;
                }
            }
        }

        /// <summary>The 4-byte packet MAC, little-endian.</summary>
        public void Finish(Span<byte> mac4)
        {
            if (mac4.Length != 4) throw new ArgumentException("MAC must be exactly 4 bytes", nameof(mac4));
            if (_nbuf != 0) MacFunc(_mbuf);
            Cycle();
            _r[Keyp] ^= InitKonst ^ (uint)(_nbuf << 3);
            _nbuf = 0;
            for (int i = 0; i < ShannonWords; i++) _r[i] ^= _crc[i];
            Diffuse();
            Cycle();
            BinaryPrimitives.WriteUInt32LittleEndian(mac4, _sbuf);
        }

        /// <summary>Constant-time MAC check. False ⇒ the frame was tampered with (or the keystream desynced) and the
        /// connection is finished: a Shannon channel cannot resynchronise.</summary>
        public bool CheckMac(ReadOnlySpan<byte> receivedMac)
        {
            if (receivedMac.Length != 4) return false;
            Span<byte> computed = stackalloc byte[4];
            Finish(computed);
            return CryptographicOperations.FixedTimeEquals(receivedMac, computed);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint SBox1(uint w) { w ^= RotL(w, 5) | RotL(w, 7); w ^= RotL(w, 19) | RotL(w, 22); return w; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint SBox2(uint w) { w ^= RotL(w, 7) | RotL(w, 22); w ^= RotL(w, 5) | RotL(w, 19); return w; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint RotL(uint v, int c) => (v << c) | (v >> (32 - c));

        void Cycle()
        {
            uint t = _r[12] ^ _r[13] ^ _konst;
            t = SBox1(t) ^ RotL(_r[0], 1);
            for (int i = 1; i < ShannonWords; i++) _r[i - 1] = _r[i];
            _r[ShannonWords - 1] = t;
            t = SBox2(_r[2] ^ _r[15]);
            _r[0] ^= t;
            _sbuf = t ^ _r[8] ^ _r[12];
        }

        void CrcFunc(uint input)
        {
            uint t = _crc[0] ^ _crc[2] ^ _crc[15] ^ input;
            for (int j = 1; j < ShannonWords; j++) _crc[j - 1] = _crc[j];
            _crc[ShannonWords - 1] = t;
        }

        void MacFunc(uint input) { CrcFunc(input); _r[Keyp] ^= input; }

        void InitState()
        {
            _r[0] = 1; _r[1] = 1;
            for (int i = 2; i < ShannonWords; i++) _r[i] = _r[i - 1] + _r[i - 2];
            _konst = InitKonst;
        }

        void Diffuse() { for (int i = 0; i < ShannonWords; i++) Cycle(); }

        void LoadKey(ReadOnlySpan<byte> key)
        {
            int keylen = key.Length, i = 0;
            while (i < (keylen & ~0x3)) { _r[Keyp] ^= BinaryPrimitives.ReadUInt32LittleEndian(key[i..]); Cycle(); i += 4; }
            if (i < keylen)
            {
                Span<byte> xtra = stackalloc byte[4];
                xtra.Clear();
                int j = 0;
                while (i < keylen) xtra[j++] = key[i++];
                _r[Keyp] ^= BinaryPrimitives.ReadUInt32LittleEndian(xtra);
                Cycle();
            }
            _r[Keyp] ^= (uint)keylen;
            Cycle();
            for (int k = 0; k < ShannonWords; k++) _crc[k] = _r[k];
            Diffuse();
            for (int k = 0; k < ShannonWords; k++) _r[k] ^= _crc[k];
        }
    }

    /// <summary>The AP frame codec: <c>[cmd:1][len:2 BE][payload][mac:4]</c>, Shannon-encrypted with a per-direction
    /// nonce that increments once per packet. Directional — the client's send key is the server's receive key.
    /// Mutable struct: one lives in the AP thread's frame and is passed by <c>ref</c>.</summary>
    public struct ApCodec
    {
        Shannon _send, _recv;
        uint _sendNonce, _recvNonce;

        public ApCodec(ReadOnlySpan<byte> sendKey, ReadOnlySpan<byte> receiveKey)
        {
            _send = new Shannon(sendKey);
            _recv = new Shannon(receiveKey);
            _sendNonce = 0;
            _recvNonce = 0;
        }

        /// <summary>Bytes a frame of <paramref name="payloadLength"/> occupies on the wire.</summary>
        public static int FrameLength(int payloadLength) => 3 + payloadLength + 4;

        /// <summary>Encode one packet into <paramref name="dst"/>; returns the frame length.</summary>
        public int Encode(byte cmd, ReadOnlySpan<byte> payload, Span<byte> dst)
        {
            if (payload.Length > ushort.MaxValue) throw new ArgumentException("payload too large", nameof(payload));
            int n = FrameLength(payload.Length);
            if (dst.Length < n) throw new ArgumentException("destination too small", nameof(dst));
            dst[0] = cmd;
            BinaryPrimitives.WriteUInt16BigEndian(dst[1..], (ushort)payload.Length);
            payload.CopyTo(dst[3..]);
            _send.Nonce(_sendNonce++);
            _send.Encrypt(dst[..(3 + payload.Length)]);
            _send.Finish(dst.Slice(3 + payload.Length, 4));
            return n;
        }

        /// <summary>Decrypt the 3-byte header IN PLACE to learn the command and the payload length. Advances the
        /// receive nonce, so the matching <see cref="EndDecode"/> must follow or the channel is desynced.</summary>
        public (byte Cmd, int Length) BeginDecode(Span<byte> header3)
        {
            _recv.Nonce(_recvNonce++);
            _recv.Decrypt(header3[..3]);
            return (header3[0], BinaryPrimitives.ReadUInt16BigEndian(header3[1..3]));
        }

        /// <summary>Decrypt the payload in place and verify the MAC. False ⇒ drop the connection.</summary>
        public bool EndDecode(Span<byte> payload, ReadOnlySpan<byte> mac4)
        {
            _recv.Decrypt(payload);
            return _recv.CheckMac(mac4);
        }
    }

    // ── 4. the AP handshake (DH-768, the RSA check, the key derivation, the framing) ─────────────────────────────────

    /// <summary>The handshake fold: bytes in, bytes out, no I/O. The protobuf bodies are built by the SHELL (the
    /// generated <c>Wavee.Protocol</c> types); everything that decides what those bytes MEAN is here.</summary>
    public static class Handshake
    {
        // RFC 2409 / Oakley group 1 — Spotify's key exchange, generator 2.
        static ReadOnlySpan<byte> Prime =>
        [
            0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xc9,0x0f,0xda,0xa2,0x21,0x68,0xc2,
            0x34,0xc4,0xc6,0x62,0x8b,0x80,0xdc,0x1c,0xd1,0x29,0x02,0x4e,0x08,0x8a,0x67,
            0xcc,0x74,0x02,0x0b,0xbe,0xa6,0x3b,0x13,0x9b,0x22,0x51,0x4a,0x08,0x79,0x8e,
            0x34,0x04,0xdd,0xef,0x95,0x19,0xb3,0xcd,0x3a,0x43,0x1b,0x30,0x2b,0x0a,0x6d,
            0xf2,0x5f,0x14,0x37,0x4f,0xe1,0x35,0x6d,0x6d,0x51,0xc2,0x45,0xe4,0x85,0xb5,
            0x76,0x62,0x5e,0x7e,0xc6,0xf4,0x4c,0x42,0xe9,0xa6,0x3a,0x36,0x20,0xff,0xff,
            0xff,0xff,0xff,0xff,0xff,0xff,
        ];

        // Spotify's well-known 2048-bit AP server key (exponent 65537). The AP socket is RAW TCP; this signature over
        // `gs` is the ONLY thing that authenticates the peer, and verifying it BEFORE deriving keys is what stops an
        // active MITM from negotiating the channel and reading the login packet (which carries the credential).
        static ReadOnlySpan<byte> ServerKey =>
        [
            0xac,0xe0,0x46,0x0b,0xff,0xc2,0x30,0xaf,0xf4,0x6b,0xfe,0xc3,0xbf,0xbf,0x86,0x3d,
            0xa1,0x91,0xc6,0xcc,0x33,0x6c,0x93,0xa1,0x4f,0xb3,0xb0,0x16,0x12,0xac,0xac,0x6a,
            0xf1,0x80,0xe7,0xf6,0x14,0xd9,0x42,0x9d,0xbe,0x2e,0x34,0x66,0x43,0xe3,0x62,0xd2,
            0x32,0x7a,0x1a,0x0d,0x92,0x3b,0xae,0xdd,0x14,0x02,0xb1,0x81,0x55,0x05,0x61,0x04,
            0xd5,0x2c,0x96,0xa4,0x4c,0x1e,0xcc,0x02,0x4a,0xd4,0xb2,0x0c,0x00,0x1f,0x17,0xed,
            0xc2,0x2f,0xc4,0x35,0x21,0xc8,0xf0,0xcb,0xae,0xd2,0xad,0xd7,0x2b,0x0f,0x9d,0xb3,
            0xc5,0x32,0x1a,0x2a,0xfe,0x59,0xf3,0x5a,0x0d,0xac,0x68,0xf1,0xfa,0x62,0x1e,0xfb,
            0x2c,0x8d,0x0c,0xb7,0x39,0x2d,0x92,0x47,0xe3,0xd7,0x35,0x1a,0x6d,0xbd,0x24,0xc2,
            0xae,0x25,0x5b,0x88,0xff,0xab,0x73,0x29,0x8a,0x0b,0xcc,0xcd,0x0c,0x58,0x67,0x31,
            0x89,0xe8,0xbd,0x34,0x80,0x78,0x4a,0x5f,0xc9,0x6b,0x89,0x9d,0x95,0x6b,0xfc,0x86,
            0xd7,0x4f,0x33,0xa6,0x78,0x17,0x96,0xc9,0xc3,0x2d,0x0d,0x32,0xa5,0xab,0xcd,0x05,
            0x27,0xe2,0xf7,0x10,0xa3,0x96,0x13,0xc4,0x2f,0x99,0xc0,0x27,0xbf,0xed,0x04,0x9c,
            0x3c,0x27,0x58,0x04,0xb6,0xb2,0x19,0xf9,0xc1,0x2f,0x02,0xe9,0x48,0x63,0xec,0xa1,
            0xb6,0x42,0xa0,0x9d,0x48,0x25,0xf8,0xb3,0x9d,0xd0,0xe8,0x6a,0xf9,0x48,0x4d,0xa1,
            0xc2,0xba,0x86,0x30,0x42,0xea,0x9d,0xb3,0x08,0x6c,0x19,0x0e,0x48,0xb3,0x9d,0x66,
            0xeb,0x00,0x06,0xa2,0x5a,0xee,0xa1,0x1b,0x13,0x87,0x3c,0xd7,0x19,0xe6,0x55,0xbd,
        ];

        /// <summary>Bytes of DH private key the client generates.</summary>
        public const int PrivateKeySize = 95;
        /// <summary>The modulus width — every public key and every shared secret is LEFT-PADDED to it.</summary>
        public const int ModulusSize = 96;

        // AP packet commands (librespot's names).
        public const byte CmdLogin = 0xAB, CmdApWelcome = 0xAC, CmdAuthFailure = 0xAD;
        public const byte CmdPing = 0x04, CmdPong = 0x49, CmdPongAck = 0x4A;
        public const byte CmdAesKeyRequest = 0x0C, CmdAesKey = 0x0D, CmdAesKeyError = 0x0E;
        public const byte CmdLocalePreamble = 0x0F, CmdPreferredLocale = 0x74;
        public const byte CmdCountryCode = 0x1B, CmdProductInfo = 0x50;

        /// <summary>g^priv mod p, left-padded to <see cref="ModulusSize"/>. ALLOCATES (BigInteger) — once per login.
        /// The private key is the caller's: the shell fills it from the CSPRNG, so this fold is deterministic and its
        /// test needs no random source.</summary>
        public static int PublicKey(ReadOnlySpan<byte> priv, Span<byte> pub)
        {
            var p = new BigInteger(Prime, isUnsigned: true, isBigEndian: true);
            var x = new BigInteger(priv, isUnsigned: true, isBigEndian: false);
            return WritePadded(BigInteger.ModPow(2, x, p), pub);
        }

        /// <summary>remote^priv mod p, left-padded to 96 bytes. The padding is load-bearing: <c>ToByteArray</c> drops
        /// leading zeros, so ~1 in 256 secrets would otherwise be 95 bytes and derive different keys than the server
        /// (RFC 2631 §2.1.2 mandates a fixed-width ZZ).</summary>
        public static int SharedSecret(ReadOnlySpan<byte> priv, ReadOnlySpan<byte> remotePublic, Span<byte> secret)
        {
            if (remotePublic.IsEmpty) return 0;
            var p = new BigInteger(Prime, isUnsigned: true, isBigEndian: true);
            var x = new BigInteger(priv, isUnsigned: true, isBigEndian: false);
            var g = new BigInteger(remotePublic, isUnsigned: true, isBigEndian: true);
            return WritePadded(BigInteger.ModPow(g, x, p), secret);
        }

        static int WritePadded(BigInteger value, Span<byte> dst)
        {
            if (dst.Length < ModulusSize) throw new ArgumentException("needs 96 bytes", nameof(dst));
            dst[..ModulusSize].Clear();
            byte[] raw = value.ToByteArray(isUnsigned: true, isBigEndian: true);
            if (raw.Length > ModulusSize) return 0;
            raw.CopyTo(dst[(ModulusSize - raw.Length)..]);
            return ModulusSize;
        }

        /// <summary>Verify the AP's RSA-SHA1 signature over its DH public key. Fails CLOSED: a malformed signature is
        /// an invalid one. ALLOCATES (RSA) — once per login.</summary>
        public static bool VerifyGs(ReadOnlySpan<byte> gs, ReadOnlySpan<byte> signature)
        {
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportParameters(new RSAParameters { Modulus = ServerKey.ToArray(), Exponent = [0x01, 0x00, 0x01] });
                return rsa.VerifyData(gs, signature, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
            }
            catch (CryptographicException) { return false; }
        }

        /// <summary>HMAC-SHA1(secret, packets ++ i) for i in 1..5 ⇒ 100 bytes: challenge = HMAC(data[0..20], packets),
        /// send = data[20..52], receive = data[52..84]. Deterministic, and unit-tested without a server.</summary>
        public static void DeriveKeys(ReadOnlySpan<byte> sharedSecret, ReadOnlySpan<byte> accumulator,
            Span<byte> challenge20, Span<byte> sendKey32, Span<byte> receiveKey32)
        {
            if (challenge20.Length < 20 || sendKey32.Length < 32 || receiveKey32.Length < 32)
                throw new ArgumentException("challenge/send/receive need 20/32/32 bytes");

            Span<byte> data = stackalloc byte[100];
            int n = accumulator.Length + 1;
            byte[]? heap = n > 4096 ? new byte[n] : null;             // once per login; a hello+response pair is ~1 KB
            Span<byte> input = heap is not null ? heap : stackalloc byte[n];
            accumulator.CopyTo(input);
            for (int i = 1; i <= 5; i++)
            {
                input[accumulator.Length] = (byte)i;
                HMACSHA1.HashData(sharedSecret, input[..n], data.Slice((i - 1) * 20, 20));
            }
            HMACSHA1.HashData(data[..20], accumulator, challenge20);
            data.Slice(20, 32).CopyTo(sendKey32);
            data.Slice(52, 32).CopyTo(receiveKey32);
        }

        /// <summary>The ClientHello frame: <c>[0x00][0x04][size:4 BE][protobuf]</c>. The size counts the WHOLE frame,
        /// and these bytes go into the handshake accumulator exactly as written.</summary>
        public static int WriteHelloFrame(ReadOnlySpan<byte> proto, Span<byte> dst)
        {
            int n = 6 + proto.Length;
            if (dst.Length < n) throw new ArgumentException("destination too small", nameof(dst));
            dst[0] = 0x00;
            dst[1] = 0x04;
            BinaryPrimitives.WriteUInt32BigEndian(dst[2..], (uint)n);
            proto.CopyTo(dst[6..]);
            return n;
        }

        /// <summary>The ClientResponsePlaintext frame: <c>[size:4 BE][protobuf]</c>.</summary>
        public static int WriteResponseFrame(ReadOnlySpan<byte> proto, Span<byte> dst)
        {
            int n = 4 + proto.Length;
            if (dst.Length < n) throw new ArgumentException("destination too small", nameof(dst));
            BinaryPrimitives.WriteUInt32BigEndian(dst, (uint)n);
            proto.CopyTo(dst[4..]);
            return n;
        }

        /// <summary>The APResponse body length from its 4-byte size prefix. UNTRUSTED input, pre-auth: anything
        /// outside (4, 1 MiB] answers 0 and the shell drops the connection.</summary>
        public static int ApResponseBodyLength(ReadOnlySpan<byte> size4)
        {
            if (size4.Length < 4) return 0;
            uint size = BinaryPrimitives.ReadUInt32BigEndian(size4);
            return size is < 4 or > (1 << 20) ? 0 : (int)size - 4;
        }

        /// <summary>The 0x74 preferred-locale packet body: <c>[00 00 10 00 02]["preferred-locale"][xx]</c>. The AP
        /// expects a 20-byte random 0x0f preamble immediately before it (the shell sends that).</summary>
        public static int WritePreferredLocale(ReadOnlySpan<byte> language2, Span<byte> dst)
        {
            ReadOnlySpan<byte> key = "preferred-locale"u8;
            int n = 5 + key.Length + language2.Length;
            if (dst.Length < n) throw new ArgumentException("destination too small", nameof(dst));
            dst[..n].Clear();
            dst[2] = 0x10;
            dst[4] = 0x02;
            key.CopyTo(dst[5..]);
            language2.CopyTo(dst[(5 + key.Length)..]);
            return n;
        }
    }

    // ── 5. hashcash (the login5 + client-token proof of work) ────────────────────────────────────────────────────────

    /// <summary>Spotify's hashcash: find a 16-byte suffix such that SHA1(context ++ prefix ++ suffix) has at least
    /// <c>target</c> LEADING ZERO BITS. login5 hashes context = login_context with a RAW-byte suffix; the client-token
    /// variant uses an empty context and a HEX suffix — same solver, different packaging.</summary>
    public static class Hashcash
    {
        /// <summary>Solve in place. The caller seeds <paramref name="suffix16"/> (the shell fills it from the CSPRNG so
        /// two attempts never walk the same path); the fold increments it. PURE and allocation-free — a test seeds a
        /// fixed suffix and gets the same answer every run. Returns how many hashes were tried.</summary>
        public static long Solve(ReadOnlySpan<byte> context, ReadOnlySpan<byte> prefix, int target, Span<byte> suffix16)
        {
            if (suffix16.Length != 16) throw new ArgumentException("suffix must be 16 bytes", nameof(suffix16));
            if (target is <= 0 or > 160) throw new ArgumentOutOfRangeException(nameof(target));

            int n = context.Length + prefix.Length + 16;
            byte[]? heap = n > 512 ? new byte[n] : null;
            Span<byte> input = heap is not null ? heap : stackalloc byte[n];
            context.CopyTo(input);
            prefix.CopyTo(input[context.Length..]);
            Span<byte> tail = input.Slice(context.Length + prefix.Length, 16);
            suffix16.CopyTo(tail);

            Span<byte> hash = stackalloc byte[20];
            for (long tries = 1; ; tries++)
            {
                SHA1.HashData(input[..n], hash);
                if (LeadingZeroBits(hash) >= target) { tail.CopyTo(suffix16); return tries; }
                Increment(tail);
            }
        }

        /// <summary>A 128-bit big-endian increment over the suffix — a search, not a secret.</summary>
        static void Increment(Span<byte> suffix)
        {
            for (int i = suffix.Length - 1; i >= 0; i--) if (++suffix[i] != 0) return;
        }

        /// <summary>Leading zero BITS of a digest — the property the server checks.</summary>
        public static int LeadingZeroBits(ReadOnlySpan<byte> hash)
        {
            int count = 0;
            for (int i = 0; i < hash.Length; i++)
            {
                byte b = hash[i];
                if (b == 0) { count += 8; continue; }
                count += BitOperations.LeadingZeroCount((uint)b) - 24;
                break;
            }
            return count;
        }
    }

    // ── 6. PKCE (RFC 7636) ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The OAuth authorization-code exchange's proof key: verifier = <c>[A-Za-z0-9-._~]{43..128}</c>,
    /// challenge = base64url(SHA256(verifier)) with no padding. Pinned to the RFC's Appendix B vector.</summary>
    public static class Pkce
    {
        const string Unreserved = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";

        /// <summary>Fill <paramref name="verifier"/> (43..128 chars) with rejection-sampled unreserved characters —
        /// uniform over the 66-character set, where a <c>% 66</c> would be biased.</summary>
        public static void NewVerifier(Span<char> verifier)
        {
            if (verifier.Length is < 43 or > 128) throw new ArgumentOutOfRangeException(nameof(verifier));
            RandomNumberGenerator.GetItems(Unreserved.AsSpan(), verifier);
        }

        /// <summary>base64url(SHA256(verifier)), unpadded. Returns the characters written (43).</summary>
        public static int Challenge(ReadOnlySpan<char> verifier, Span<char> challenge)
        {
            Span<byte> ascii = stackalloc byte[128];
            if (verifier.Length > ascii.Length) throw new ArgumentException("verifier too long", nameof(verifier));
            for (int i = 0; i < verifier.Length; i++) ascii[i] = (byte)verifier[i];
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(ascii[..verifier.Length], hash);
            return Base64Url(hash, challenge);
        }

        /// <summary>Unpadded base64url of <paramref name="data"/>.</summary>
        public static int Base64Url(ReadOnlySpan<byte> data, Span<char> dst)
        {
            Span<char> b64 = stackalloc char[88];
            if (!Convert.TryToBase64Chars(data, b64, out int written)) throw new ArgumentException("too long", nameof(data));
            int n = 0;
            for (int i = 0; i < written; i++)
            {
                char c = b64[i];
                if (c == '=') break;
                dst[n++] = c == '+' ? '-' : c == '/' ? '_' : c;
            }
            return n;
        }
    }

    // ── 7. hex (file ids) ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Audio file ids are 20 raw bytes on the wire and lowercase hex in every url. (Entity ids are NOT hex —
    /// they are base62 over a 128-bit gid, and <see cref="Base62"/> in <c>Entities/Entities.cs</c> owns that; this
    /// file deliberately does not carry a second base62.)</summary>
    public static class Hex
    {
        const string Digits = "0123456789abcdef";

        public static int Encode(ReadOnlySpan<byte> bytes, Span<char> dst)
        {
            if (dst.Length < bytes.Length * 2) throw new ArgumentException("destination too small", nameof(dst));
            for (int i = 0; i < bytes.Length; i++)
            {
                dst[i * 2] = Digits[bytes[i] >> 4];
                dst[i * 2 + 1] = Digits[bytes[i] & 0xF];
            }
            return bytes.Length * 2;
        }

        public static bool TryDecode(ReadOnlySpan<char> hex, Span<byte> dst)
        {
            if ((hex.Length & 1) != 0 || dst.Length < hex.Length / 2) return false;
            for (int i = 0; i < hex.Length; i += 2)
            {
                int hi = Digit(hex[i]), lo = Digit(hex[i + 1]);
                if (hi < 0 || lo < 0) return false;
                dst[i / 2] = (byte)((hi << 4) | lo);
            }
            return true;
        }

        static int Digit(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
    }

    // ── 8. the dealer frame ──────────────────────────────────────────────────────────────────────────────────────────

    public enum DealerFrameKind : byte { Unknown = 0, Ping, Pong, Message, Request }

    /// <summary>One decoded dealer websocket frame. A <c>ref struct</c>: every field is a view into the frame bytes or
    /// into the caller's scratch, so a parse costs nothing and nothing outlives the receive loop's iteration.</summary>
    public readonly ref struct DealerMessage
    {
        public readonly DealerFrameKind Kind;
        /// <summary>The <c>hm://</c> topic of a MESSAGE frame.</summary>
        public readonly ReadOnlySpan<byte> Uri;
        /// <summary>The <c>message_ident</c> of a REQUEST frame.</summary>
        public readonly ReadOnlySpan<byte> Ident;
        /// <summary>The reply key of a REQUEST frame — what <c>Api.Reply</c> answers with.</summary>
        public readonly ReadOnlySpan<byte> Key;
        /// <summary>The <c>Spotify-Connection-Id</c> header, present on the dealer's first MESSAGE.</summary>
        public readonly ReadOnlySpan<byte> ConnectionId;
        /// <summary>The decoded payload: base64 chunks concatenated and, when the frame said so, gunzipped.</summary>
        public readonly ReadOnlySpan<byte> Payload;
        /// <summary>The payload did not fit the scratch buffer and was dropped (the topic is still usable).</summary>
        public readonly bool Truncated;

        internal DealerMessage(DealerFrameKind kind, ReadOnlySpan<byte> uri, ReadOnlySpan<byte> ident,
            ReadOnlySpan<byte> key, ReadOnlySpan<byte> connectionId, ReadOnlySpan<byte> payload, bool truncated)
        {
            Kind = kind;
            Uri = uri;
            Ident = ident;
            Key = key;
            ConnectionId = connectionId;
            Payload = payload;
            Truncated = truncated;
        }

        public bool IsPing => Kind == DealerFrameKind.Ping;
        public bool IsRequest => Kind == DealerFrameKind.Request;
        /// <summary>A Connect cluster push — the frame `Spotify.Connect` folds into <c>Input.Cluster</c> (plan §4.10).</summary>
        public bool IsClusterUpdate => Kind == DealerFrameKind.Message && Uri.StartsWith("hm://connect-state/v1/cluster"u8);
    }

    /// <summary>The dealer's JSON frames. Reflection-free (<c>Utf8JsonReader</c>, never a DOM) and allocation-free
    /// except for the gzip case, which <see cref="Parse"/> names.</summary>
    public static class DealerFrame
    {
        /// <summary>Parse one frame. <paramref name="scratch"/> is the caller's reusable buffer (the receive loop owns
        /// one per connection): base64 payload chunks are decoded into its head and, when the frame is gzipped,
        /// inflated into its tail — so the returned <c>Payload</c> is always ready to decode.
        /// <para>MESSAGE frames carry a <c>payloads</c> ARRAY of base64 chunks that concatenate; REQUEST frames carry a
        /// SINGULAR <c>payload</c> object whose <c>compressed</c> field is base64 → gzip → the command JSON. A
        /// non-base64 entry (social-connect ships a plain object there) is skipped rather than failing the frame:
        /// losing the topic turned a broadcast push into an Unknown in 0.2.9 and the router had nothing to classify.</para>
        /// <para>ALLOCATES only for a gzipped frame: one <c>MemoryStream</c> + one <c>GZipStream</c>, both scoped to
        /// the call. Cluster pushes arrive a few per second at most; the hot path (ping/pong, uncompressed message)
        /// allocates nothing.</para></summary>
        public static DealerMessage Parse(ReadOnlySpan<byte> utf8, byte[] scratch)
        {
            if (utf8.IsEmpty || scratch.Length < 64) return default;
            try
            {
                var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = 64, CommentHandling = JsonCommentHandling.Skip });
                var kind = DealerFrameKind.Unknown;
                ReadOnlySpan<byte> uri = default, key = default, ident = default, connectionId = default;
                bool gzip = false, truncated = false;
                int payloadLength = -1;

                while (reader.Read())
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;
                    if (reader.ValueTextEquals("type"u8))
                    {
                        reader.Read();
                        kind = reader.ValueTextEquals("ping"u8) ? DealerFrameKind.Ping
                             : reader.ValueTextEquals("pong"u8) ? DealerFrameKind.Pong
                             : reader.ValueTextEquals("message"u8) ? DealerFrameKind.Message
                             : reader.ValueTextEquals("request"u8) ? DealerFrameKind.Request
                             : DealerFrameKind.Unknown;
                    }
                    else if (reader.ValueTextEquals("uri"u8)) { reader.Read(); uri = Value(ref reader); }
                    else if (reader.ValueTextEquals("key"u8)) { reader.Read(); key = Value(ref reader); }
                    else if (reader.ValueTextEquals("message_ident"u8)) { reader.Read(); ident = Value(ref reader); }
                    else if (reader.ValueTextEquals("headers"u8)) { reader.Read(); ReadHeaders(ref reader, ref gzip, ref connectionId); }
                    else if (reader.ValueTextEquals("payloads"u8)) { reader.Read(); payloadLength = ReadPayloads(ref reader, scratch, ref truncated); }
                    else if (reader.ValueTextEquals("payload"u8)) { reader.Read(); payloadLength = ReadRequestPayload(ref reader, scratch, ref gzip, ref truncated); }
                }

                ReadOnlySpan<byte> payload = payloadLength > 0 ? scratch.AsSpan(0, payloadLength) : default;
                if (payloadLength > 0 && gzip)
                {
                    int inflated = Inflate(scratch, payloadLength, scratch, payloadLength, scratch.Length - payloadLength);
                    if (inflated < 0) { payload = default; truncated = true; }
                    else payload = scratch.AsSpan(payloadLength, inflated);
                }
                return new DealerMessage(kind, uri, ident, key, connectionId, payload, truncated);
            }
            catch (JsonException)
            {
                return default;   // malformed ⇒ Unknown, exactly as 0.2.9's parser answered
            }
        }

        /// <summary>The raw bytes of the string token the reader is on. The input is one contiguous span, so a value
        /// never arrives as a sequence.</summary>
        static ReadOnlySpan<byte> Value(scoped ref Utf8JsonReader reader)
            => reader.TokenType == JsonTokenType.String && !reader.HasValueSequence ? reader.ValueSpan : default;

        static void ReadHeaders(scoped ref Utf8JsonReader reader, ref bool gzip, ref ReadOnlySpan<byte> connectionId)
        {
            if (reader.TokenType != JsonTokenType.StartObject) return;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) return;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                bool isEncoding = reader.ValueTextEquals("Transfer-Encoding"u8) || reader.ValueTextEquals("transfer-encoding"u8);
                bool isConnection = reader.ValueTextEquals("Spotify-Connection-Id"u8) || reader.ValueTextEquals("spotify-connection-id"u8);
                reader.Read();
                if (isEncoding) { if (Value(ref reader).IndexOf("gzip"u8) >= 0) gzip = true; }
                else if (isConnection) connectionId = Value(ref reader);
            }
        }

        static int ReadPayloads(scoped ref Utf8JsonReader reader, byte[] scratch, ref bool truncated)
        {
            if (reader.TokenType != JsonTokenType.StartArray) return -1;
            int written = 0;
            bool any = false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) break;
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) { reader.Skip(); continue; }
                if (reader.TokenType != JsonTokenType.String) continue;
                var status = Base64.DecodeFromUtf8(Value(ref reader), scratch.AsSpan(written), out _, out int decoded);
                if (status == System.Buffers.OperationStatus.InvalidData) continue;              // a plain-JSON entry
                if (status == System.Buffers.OperationStatus.DestinationTooSmall) { truncated = true; break; }
                written += decoded;
                any = true;
            }
            return any ? written : -1;
        }

        static int ReadRequestPayload(scoped ref Utf8JsonReader reader, byte[] scratch, ref bool gzip, ref bool truncated)
        {
            if (reader.TokenType != JsonTokenType.StartObject) return -1;
            int written = -1;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                bool compressed = reader.ValueTextEquals("compressed"u8);
                reader.Read();
                if (!compressed || reader.TokenType != JsonTokenType.String) continue;
                var status = Base64.DecodeFromUtf8(Value(ref reader), scratch, out _, out int decoded);
                if (status == System.Buffers.OperationStatus.DestinationTooSmall) { truncated = true; continue; }
                if (status == System.Buffers.OperationStatus.InvalidData) continue;
                written = decoded;
                gzip = true;    // the compressed request payload is always gzip
            }
            return written;
        }

        /// <summary>Gunzip <paramref name="length"/> bytes of <paramref name="src"/> into <paramref name="dst"/> at
        /// <paramref name="dstOffset"/>. Returns the inflated length, or -1 when it did not fit / was not gzip.</summary>
        internal static int Inflate(byte[] src, int length, byte[] dst, int dstOffset, int dstLength)
        {
            if (dstLength <= 0) return -1;
            try
            {
                using var input = new MemoryStream(src, 0, length, writable: false);
                using var gz = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
                int total = 0;
                while (total < dstLength)
                {
                    int read = gz.Read(dst, dstOffset + total, dstLength - total);
                    if (read == 0) return total;
                    total += read;
                }
                return -1;   // more inflated bytes than the buffer holds
            }
            catch (InvalidDataException) { return -1; }
        }

        /// <summary>The pong the server's ping wants.</summary>
        public static int WritePong(Span<byte> dst) => Write("{\"type\":\"pong\"}"u8, dst);

        /// <summary>Our own 30 s keepalive ping (P10 names the timer).</summary>
        public static int WritePing(Span<byte> dst) => Write("{\"type\":\"ping\"}"u8, dst);

        /// <summary>The REQUEST ack. The key is escaped, never concatenated raw.</summary>
        public static int WriteReply(Span<byte> dst, ReadOnlySpan<byte> key, bool ok)
        {
            int n = Write("{\"type\":\"reply\",\"key\":\""u8, dst);
            if (n == 0) return 0;
            for (int i = 0; i < key.Length; i++)
            {
                byte b = key[i];
                if (b is (byte)'"' or (byte)'\\')
                {
                    if (n + 2 > dst.Length) return 0;
                    dst[n++] = (byte)'\\';
                    dst[n++] = b;
                }
                else if (b >= 0x20)
                {
                    if (n + 1 > dst.Length) return 0;
                    dst[n++] = b;
                }
            }
            int tail = Write(ok ? "\",\"payload\":{\"success\":true}}"u8 : "\",\"payload\":{\"success\":false}}"u8, dst[n..]);
            return tail == 0 ? 0 : n + tail;
        }

        static int Write(ReadOnlySpan<byte> literal, Span<byte> dst)
        {
            if (dst.Length < literal.Length) return 0;
            literal.CopyTo(dst);
            return literal.Length;
        }
    }

    // ── 9. the audio-key packets (0x0c / 0x0d / 0x0e) ────────────────────────────────────────────────────────────────

    /// <summary>The AP's key exchange, proto-free: request = file_id(20) ++ track_gid(16) ++ seq(u32 BE) ++ 00 00;
    /// the AP answers 0x0d = seq ++ key(16) or 0x0e = seq ++ code(u16).</summary>
    public static class AudioKey
    {
        public const int RequestLength = 42;
        public const int KeyLength = 16;

        public static int WriteRequest(ReadOnlySpan<byte> fileId, ReadOnlySpan<byte> trackGid, uint seq, Span<byte> dst)
        {
            int n = fileId.Length + trackGid.Length + 6;
            if (dst.Length < n) throw new ArgumentException("destination too small", nameof(dst));
            dst[..n].Clear();
            fileId.CopyTo(dst);
            trackGid.CopyTo(dst[fileId.Length..]);
            BinaryPrimitives.WriteUInt32BigEndian(dst[(fileId.Length + trackGid.Length)..], seq);
            return n;
        }

        /// <summary>0x0d: seq ++ key(16).</summary>
        public static bool TryReadKey(ReadOnlySpan<byte> payload, out uint seq, Span<byte> key16)
        {
            seq = 0;
            if (payload.Length < 20 || key16.Length < KeyLength) return false;
            seq = BinaryPrimitives.ReadUInt32BigEndian(payload);
            payload.Slice(4, KeyLength).CopyTo(key16);
            return true;
        }

        /// <summary>0x0e: seq ++ code(u16). A missing code answers -1 (0.2.9's shape).</summary>
        public static bool TryReadError(ReadOnlySpan<byte> payload, out uint seq, out int code)
        {
            seq = 0;
            code = -1;
            if (payload.Length < 4) return false;
            seq = BinaryPrimitives.ReadUInt32BigEndian(payload);
            if (payload.Length >= 6) code = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(4, 2));
            return true;
        }
    }

    // ── 10. the request fold ─────────────────────────────────────────────────────────────────────────────────────────
    //
    // 0.2.9 had ~20 `Spotify*Service` classes, each re-deciding a url, a verb and a header dictionary (three
    // `Dictionary<string,string>` allocations per call). Here a request is a VALUE folded from (session, kind, args):
    // `Spotify.Api.cs` picks the kind and fills the arguments, and the runner turns the value into an
    // `HttpRequestMessage`. The header SET is a bit field, so "which headers does this route need" is a constant
    // rather than a dictionary — and the captured desktop tuple the gateway gates its mutation routes on is ONE flag
    // rather than a copied dictionary literal in every caller.

    public enum Verb : byte { Get, Post, Put, Delete, Patch }

    /// <summary>Which host answers. The two variable ones (<see cref="ApiHost.Spclient"/>, <see cref="ApiHost.Dealer"/>)
    /// come from apresolve and live in the session; the rest are fixed names.</summary>
    public enum ApiHost : byte { Spclient = 0, SpclientWg, Pathfinder, Login5, ClientToken, ApResolve, Dealer }

    /// <summary>The headers a route needs. Bearer / client-token / identity are stamped by the runner from the
    /// session; the rest are constants the fold selects.</summary>
    [Flags]
    public enum HeaderSet : uint
    {
        None = 0,
        Bearer = 1 << 0,
        ClientToken = 1 << 1,
        /// <summary>App-Platform + Spotify-App-Version + User-Agent — the gateway's client-identity tuple.</summary>
        Identity = 1 << 2,
        AcceptLanguage = 1 << 3,
        AcceptProtobuf = 1 << 4,
        AcceptJson = 1 << 5,
        ContentProtobuf = 1 << 6,
        ContentJson = 1 << 7,
        /// <summary>x-www-form-urlencoded DESPITE a protobuf body — the playlist-v2 gateway quirk. Anything else
        /// routes to a passive read handler that 200-OKs without mutating (the silent no-op class).</summary>
        ContentForm = 1 << 8,
        /// <summary>The body is gzipped and carries <c>X-Transfer-Encoding: gzip</c>.</summary>
        GzipBody = 1 << 9,
        /// <summary>X-Spotify-Connection-Id, from the dealer's first frame.</summary>
        ConnectionId = 1 << 10,
        NoStore = 1 << 11,
        ApplyLenses = 1 << 12,
        AppliedLenses = 1 << 13,
        AcceptListItems = 1 << 14,
        AcceptGeoblock = 1 << 15,
        DsaMode = 1 << 16,
        Origin = 1 << 17,
        PathfinderDesktop = 1 << 18,
        PathfinderWeb = 1 << 19,
    }

    /// <summary>Which request. One per FAMILY, not one per caller: <c>Spotify.Api.cs</c>'s function per request picks
    /// the kind and fills the arguments.</summary>
    public enum RequestKind : byte
    {
        /// <summary>POST /extended-metadata/v0/extended-metadata — the 300-uri batch (P4).</summary>
        ExtendedMetadata = 0,
        /// <summary>POST the pathfinder persisted query (api-partner).</summary>
        Pathfinder,
        PlaylistRead, PlaylistDiff, PlaylistChanges, PlaylistCreate, PlaylistSignals, RootlistRead, RootlistChanges,
        RecentsPage, RecentsDiff,
        CollectionPage, CollectionDelta, CollectionWrite,
        ConnectStatePut, ConnectStateTransfer, ConnectStateCommand, ConnectStateVolume,
        ContextResolve, Autoplay, StorageResolve, ServerTime, Profile, Popcount,
        /// <summary>The escape hatch: <c>Args.Path</c> verbatim on <c>Args.Host</c> with <c>Args.Headers</c>. A new
        /// route lands here first and graduates to a kind when a second caller wants it.</summary>
        Custom,
    }

    /// <summary>What a request needs beyond the session. A <c>ref struct</c> with writable fields, so a caller builds
    /// it with an object initializer and nothing is copied.</summary>
    public ref struct RequestArgs
    {
        /// <summary>The primary id in the path (a playlist id, our device id, a file id hex, a username).</summary>
        public ReadOnlySpan<char> Id;
        /// <summary>The secondary id (the target device of a transfer / command / volume, a revision).</summary>
        public ReadOnlySpan<char> Id2;
        /// <summary>The request body, already encoded (protobuf or JSON).</summary>
        public ReadOnlySpan<byte> Body;
        /// <summary>The route's one number — a revision, a volume, a page size.</summary>
        public long Number;
        /// <summary>The route's one flag (autopodcast vs autoplay, web-player vs desktop pathfinder).</summary>
        public bool Flag;
        /// <summary><see cref="RequestKind.Custom"/> only: the verbatim path (must start with '/').</summary>
        public ReadOnlySpan<char> Path;
        /// <summary><see cref="RequestKind.Custom"/> only.</summary>
        public ApiHost Host;
        /// <summary><see cref="RequestKind.Custom"/> only.</summary>
        public Verb Verb;
        /// <summary><see cref="RequestKind.Custom"/> only.</summary>
        public HeaderSet Headers;
    }

    /// <summary>A folded request. <see cref="Request.Path"/> points into the buffer the caller passed to
    /// <see cref="Build"/> and <see cref="Request.Body"/> into the caller's body: nothing here allocates, and nothing
    /// outlives the call that sends it.</summary>
    public readonly ref struct Request
    {
        public readonly Verb Verb;
        public readonly ApiHost Host;
        public readonly ReadOnlySpan<char> Path;
        public readonly HeaderSet Headers;
        public readonly ReadOnlySpan<byte> Body;
        /// <summary>The <c>spotify-playlist-sync-reason</c> value, or empty. The gateway gates the playlist routes on
        /// it: CAk= edit, CAw= create, CA8QAQ== signals, CAwQAQ== cold list, CAEQAQ== list diff.</summary>
        public readonly ReadOnlySpan<char> SyncReason;

        internal Request(Verb verb, ApiHost host, ReadOnlySpan<char> path, HeaderSet headers,
            ReadOnlySpan<byte> body, ReadOnlySpan<char> syncReason = default)
        {
            Verb = verb;
            Host = host;
            Path = path;
            Headers = headers;
            Body = body;
            SyncReason = syncReason;
        }

        public bool IsEmpty => Path.IsEmpty;
    }

    /// <summary>Appends into the caller's path buffer. No string, no interpolation, no allocation.</summary>
    public ref struct PathWriter
    {
        readonly Span<char> _buffer;
        int _length;

        public PathWriter(Span<char> buffer)
        {
            _buffer = buffer;
            _length = 0;
        }

        public readonly ReadOnlySpan<char> Written => _buffer[.._length];

        public void Append(ReadOnlySpan<char> s)
        {
            if (_length + s.Length > _buffer.Length) throw new ArgumentException("path buffer too small");
            s.CopyTo(_buffer[_length..]);
            _length += s.Length;
        }

        public void Append(long value)
        {
            if (!value.TryFormat(_buffer[_length..], out int written)) throw new ArgumentException("path buffer too small");
            _length += written;
        }

        /// <summary>Percent-encode everything outside the unreserved set: an id goes into a path SEGMENT, and a
        /// user-minted playlist id or a device id is not guaranteed to be url-safe.</summary>
        public void AppendEscaped(ReadOnlySpan<char> s)
        {
            const string HexDigits = "0123456789ABCDEF";
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.' or '~')
                {
                    Append(s.Slice(i, 1));
                }
                else if (c < 0x80)
                {
                    Append("%");
                    Append(HexDigits.AsSpan(c >> 4, 1));
                    Append(HexDigits.AsSpan(c & 0xF, 1));
                }
                else
                {
                    Append("_");   // non-ASCII never appears in a Spotify id; fold it rather than mis-encode it
                }
            }
        }
    }

    /// <summary>THE fold: (session, kind, args) ⇒ the request. Pure — no clock, no socket, no allocation — so every
    /// route in <c>Spotify.Api.cs</c> is pinned by a test that asserts the verb, the path and the header set.</summary>
    public static Request Build(in Session s, RequestKind kind, in RequestArgs args, Span<char> path)
    {
        // The session is the RUNNER's business (which host answers, which bearer is stamped); the fold reads only the
        // kind and the args, which is what makes every route's test one line with no session to build.
        const HeaderSet Common = HeaderSet.Bearer | HeaderSet.ClientToken | HeaderSet.Identity | HeaderSet.AcceptLanguage;
        // The captured desktop tuple the playlist-v2 gateway gates its mutation routes on. A request missing it
        // 200-OKs against a PASSIVE handler that never mutates state — the silent no-op class 0.2.9 chased for a week.
        const HeaderSet Mutation = Common | HeaderSet.ContentForm | HeaderSet.ApplyLenses | HeaderSet.NoStore
                                 | HeaderSet.AcceptGeoblock | HeaderSet.DsaMode | HeaderSet.Origin;

        var w = new PathWriter(path);
        switch (kind)
        {
            case RequestKind.ExtendedMetadata:
                w.Append("/extended-metadata/v0/extended-metadata");
                return new Request(Verb.Post, ApiHost.Spclient, w.Written,
                    Common | HeaderSet.ContentProtobuf | HeaderSet.AcceptProtobuf, args.Body);

            case RequestKind.Pathfinder:
                w.Append("/pathfinder/v2/query");
                return new Request(Verb.Post, ApiHost.Pathfinder, w.Written,
                    Common | HeaderSet.ContentJson | HeaderSet.AcceptJson
                    | (args.Flag ? HeaderSet.PathfinderWeb : HeaderSet.PathfinderDesktop), args.Body);

            case RequestKind.PlaylistRead:
                w.Append("/playlist/v2/playlist/");
                w.AppendEscaped(args.Id);
                return new Request(Verb.Get, ApiHost.Spclient, w.Written, Common | HeaderSet.AcceptProtobuf, default);

            case RequestKind.PlaylistDiff:
                w.Append("/playlist/v2/playlist/");
                w.AppendEscaped(args.Id);
                w.Append("/diff?revision=");
                w.AppendEscaped(args.Id2);
                return new Request(Verb.Get, ApiHost.Spclient, w.Written, Common | HeaderSet.AcceptProtobuf, default);

            case RequestKind.PlaylistChanges:
                w.Append("/playlist/v2/playlist/");
                w.AppendEscaped(args.Id);
                w.Append("/changes");
                return new Request(Verb.Post, ApiHost.Spclient, w.Written, Mutation, args.Body, "CAk=");

            case RequestKind.PlaylistCreate:
                w.Append("/playlist/v2/playlist/");
                w.AppendEscaped(args.Id);
                w.Append("/changes");
                return new Request(Verb.Post, ApiHost.Spclient, w.Written, Mutation, args.Body, "CAw=");

            case RequestKind.PlaylistSignals:
                w.Append("/playlist/v2/playlist/");
                w.AppendEscaped(args.Id);
                w.Append("/signals");
                return new Request(Verb.Post, ApiHost.Spclient, w.Written,
                    Mutation | HeaderSet.AcceptProtobuf, args.Body, "CA8QAQ==");

            case RequestKind.RootlistRead:
                w.Append("/playlist/v2/user/");
                w.AppendEscaped(args.Id);
                w.Append("/rootlist?decorate=revision");
                return new Request(Verb.Get, ApiHost.Spclient, w.Written, Common | HeaderSet.AcceptProtobuf, default);

            case RequestKind.RootlistChanges:
                w.Append("/playlist/v2/user/");
                w.AppendEscaped(args.Id);
                w.Append("/rootlist/changes");
                return new Request(Verb.Post, ApiHost.Spclient, w.Written, Mutation, args.Body, "CAk=");

            case RequestKind.RecentsPage:
            case RequestKind.RecentsDiff:
                bool diff = kind == RequestKind.RecentsDiff;
                w.Append(diff ? "/playlist/v2/list/recents/page/diff" : "/playlist/v2/list/recents/page");
                return new Request(Verb.Get, ApiHost.Spclient, w.Written,
                    Common | HeaderSet.AcceptProtobuf | HeaderSet.ApplyLenses | HeaderSet.AcceptListItems
                    | (diff ? HeaderSet.AppliedLenses : HeaderSet.None),
                    default, diff ? "CAEQAQ==" : "CAwQAQ==");

            case RequestKind.CollectionPage:
                w.Append("/collection/v2/paging");
                return new Request(Verb.Post, ApiHost.Spclient, w.Written,
                    Common | HeaderSet.ContentProtobuf | HeaderSet.AcceptProtobuf, args.Body);

            case RequestKind.CollectionDelta:
                w.Append("/collection/v2/delta");
                return new Request(Verb.Post, ApiHost.Spclient, w.Written,
                    Common | HeaderSet.ContentProtobuf | HeaderSet.AcceptProtobuf, args.Body);

            case RequestKind.CollectionWrite:
                w.Append("/collection/v2/write");
                return new Request(Verb.Post, ApiHost.Spclient, w.Written,
                    Common | HeaderSet.ContentProtobuf | HeaderSet.AcceptProtobuf, args.Body);

            case RequestKind.ConnectStatePut:
                w.Append("/connect-state/v1/devices/");
                w.AppendEscaped(args.Id);
                return new Request(Verb.Put, ApiHost.Spclient, w.Written,
                    Common | HeaderSet.ContentProtobuf | HeaderSet.ConnectionId | HeaderSet.GzipBody, args.Body);

            case RequestKind.ConnectStateTransfer:
                w.Append("/connect-state/v1/connect/transfer/from/");
                w.AppendEscaped(args.Id);
                w.Append("/to/");
                w.AppendEscaped(args.Id2);
                return new Request(Verb.Post, ApiHost.Spclient, w.Written, Common | HeaderSet.ContentProtobuf, args.Body);

            case RequestKind.ConnectStateCommand:
                w.Append("/connect-state/v1/player/command/from/");
                w.AppendEscaped(args.Id);
                w.Append("/to/");
                w.AppendEscaped(args.Id2);
                return new Request(Verb.Post, ApiHost.Spclient, w.Written, Common | HeaderSet.ContentJson, args.Body);

            case RequestKind.ConnectStateVolume:
                w.Append("/connect-state/v1/connect/volume/from/");
                w.AppendEscaped(args.Id);
                w.Append("/to/");
                w.AppendEscaped(args.Id2);
                return new Request(Verb.Post, ApiHost.Spclient, w.Written, Common | HeaderSet.ContentJson, args.Body);

            case RequestKind.ContextResolve:
                w.Append("/context-resolve/v1/");
                w.AppendEscaped(args.Id);
                return new Request(Verb.Get, ApiHost.Spclient, w.Written, Common | HeaderSet.AcceptJson, default);

            case RequestKind.Autoplay:
                w.Append(args.Flag ? "/context-resolve/v1/autopodcast" : "/context-resolve/v1/autoplay");
                return new Request(Verb.Post, ApiHost.Spclient, w.Written,
                    Common | HeaderSet.ContentJson | HeaderSet.AcceptJson, args.Body);

            case RequestKind.StorageResolve:
                w.Append("/storage-resolve/files/audio/interactive/");
                w.AppendEscaped(args.Id);
                return new Request(Verb.Get, ApiHost.Spclient, w.Written, Common | HeaderSet.AcceptProtobuf, default);

            case RequestKind.ServerTime:
                w.Append("/melody/v1/time");
                return new Request(Verb.Get, ApiHost.Spclient, w.Written,
                    Common | HeaderSet.AcceptJson | HeaderSet.NoStore, default);

            case RequestKind.Profile:
                w.Append("/user-profile-view/v3/profile/");
                w.AppendEscaped(args.Id);
                return new Request(Verb.Get, ApiHost.Spclient, w.Written, Common | HeaderSet.AcceptJson, default);

            case RequestKind.Popcount:
                w.Append("/popcount/v2/playlist/");
                w.AppendEscaped(args.Id);
                w.Append("/count");
                return new Request(Verb.Get, ApiHost.Spclient, w.Written, Common | HeaderSet.AcceptJson, default);

            case RequestKind.Custom:
                w.Append(args.Path);
                return new Request(args.Verb, args.Host, w.Written, args.Headers, args.Body);

            default:
                return default;
        }
    }

    // ── 11. two small wire parses the session itself needs ───────────────────────────────────────────────────────────

    /// <summary>apresolve answers <c>{"accesspoint":["host:port",…],"spclient":[…],"dealer":[…]}</c>. Returns how many
    /// hosts were written, as RANGES into <paramref name="json"/> — no strings, and the caller keeps the order (the
    /// AP failover walks it, :4070 first).</summary>
    public static int ParseHosts(ReadOnlySpan<byte> json, ReadOnlySpan<byte> key, Span<Range> into)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 8 });
        int n = 0;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals(key)) continue;
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return 0;
            while (reader.Read() && reader.TokenType == JsonTokenType.String && n < into.Length)
            {
                int start = (int)reader.TokenStartIndex + 1;             // past the opening quote
                into[n++] = new Range(start, start + reader.ValueSpan.Length);
            }
            return n;
        }
        return 0;
    }

    /// <summary>Split a <c>host:port</c> entry. A missing or malformed port answers <paramref name="fallbackPort"/>.</summary>
    public static (int HostLength, int Port) SplitHostPort(ReadOnlySpan<byte> entry, int fallbackPort)
    {
        int colon = entry.IndexOf((byte)':');
        if (colon < 0) return (entry.Length, fallbackPort);
        int port = 0;
        for (int i = colon + 1; i < entry.Length; i++)
        {
            if (entry[i] is < (byte)'0' or > (byte)'9') { port = 0; break; }
            port = port * 10 + (entry[i] - '0');
        }
        return (colon, port == 0 ? fallbackPort : port);
    }

    /// <summary>The AP's ProductInfo push (cmd 0x50) is an XML blob. The facts the session wants out of it are element
    /// VALUES, so the whole parse is "find <c>&lt;name&gt;</c>, take bytes until <c>&lt;</c>" — no XML DOM, no
    /// reflection, no allocation — and a malformed blob answers empty instead of throwing.</summary>
    public static class ProductXml
    {
        public static ReadOnlySpan<byte> Value(ReadOnlySpan<byte> xml, ReadOnlySpan<byte> element)
        {
            for (int i = 0; i + element.Length + 2 < xml.Length; i++)
            {
                if (xml[i] != (byte)'<' || !xml[(i + 1)..].StartsWith(element)) continue;
                int close = i + 1 + element.Length;
                if (close >= xml.Length || xml[close] != (byte)'>') continue;
                int start = close + 1;
                int end = xml[start..].IndexOf((byte)'<');
                return end < 0 ? default : xml.Slice(start, end);
            }
            return default;
        }

        /// <summary>Premium (including premium_mini / _student / _family). Anything else is Free, and an UNREADABLE
        /// blob is <see cref="Tier.Unknown"/> — which the gate treats as Free, never optimistically as Premium.</summary>
        public static Tier TierOf(ReadOnlySpan<byte> xml)
        {
            var type = Value(xml, "type"u8);
            return type.IsEmpty ? Tier.Unknown : type.StartsWith("premium"u8) ? Tier.Premium : Tier.Free;
        }
    }
}
