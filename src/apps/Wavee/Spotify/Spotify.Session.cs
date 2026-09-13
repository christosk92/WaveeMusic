// ── Spotify/Spotify.Session.cs ─────────────────────────────────────────────────────────────────────────────────────
// AP socket, login5, tokens, dealer loop, audio keys
//
// Role: SHELL
// Owner: D
// Wave: 2
// Budget: 1600 lines
// Spec: plan
//
// THE SOCKETS. Everything in this file is I/O: two named threads (the AP channel and the dealer websocket), a third
// that serves the 30 s keepalive and the server-clock re-probe, the three HTTP mints (apresolve, clienttoken,
// login5), and the audio-key request table. Every DECISION they need is in `Spotify.cs` and is pure.
//
// The rules this file is written under (C1-C10):
//   C1  this file never writes a table, an edge or a signal, and never touches `Entities.Strings`, from a shell
//       thread. Everything it learns becomes a `SessionEvent` handed to `Post`; `Apply` runs on the UI thread, folds
//       it with `Step`, publishes the new session and writes the signals. The ONE place text is interned is inside a
//       posted action — i.e. on the UI thread — which is why `PublishWelcome` takes `string`s and not `StringId`s.
//   C4  every thread runs for ONE epoch. A drop bumps `Session.Epoch`, the epoch's `CancellationTokenSource` is
//       cancelled and its sockets are closed; a late answer from the old epoch is dropped, never applied.
//   C8  bounded everywhere: 32 audio-key slots (a full table refuses rather than queues), one dealer frame at a time,
//       a fixed 256 KiB dealer scratch buffer.
//   C9  no shell call ever blocks the UI thread. `Login`, `Logout` and `Reply` return immediately; `AccessToken` and
//       `RequestAudioKey` BLOCK and are for shell threads only (their doc comments say so).
//   P10 two timers, both named: the dealer keepalive (30 s) and the server-clock re-sync (10 min). Nothing polls.
//
// ASYNC IS NOT USED, ON PURPOSE. These threads exist to block: a socket read, a key wait, a 30 s tick. `HttpClient`
// has a synchronous `Send`, and the one API that does not (`ClientWebSocket`) is awaited with `GetAwaiter().GetResult()`
// ON THE DEALER'S OWN THREAD. That keeps the shell readable as one procedure per connection and keeps the app free of
// async state machines outside the engine (P9 bans them in core; here they would simply buy nothing).
//
// WHAT IS NOT HERE: the metadata decode (owner E, `Spotify.Decode*.cs`), the request functions and the audio stream
// (owner F, `Spotify.Api.cs` / `Spotify.Audio.cs`), and the Connect glue (owner F, `Spotify.Connect.cs`) — the dealer
// loop hands every non-protocol frame to `Connect.OnDealer` and knows nothing about what it means.
//
// WHAT COMES FROM `Platform`, DIRECTLY (no seam, no delegate): the protected credential slot
// (`TryLoadCredential`/`SaveCredential`/`ClearCredential`), the launch-stable `DeviceId`, the launch locale
// (`Locale.SpotifyLanguage`), the account redaction (`Redact`) and the always-on log (`Log.Info/Warn`). `Platform.Boot`
// runs before `Spotify.Boot`, and the dependency is one-way: Platform never calls into Spotify.

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentGpu.Signals;
using Google.Protobuf;

namespace Wavee;

public static partial class Spotify
{
    // ── 1. the post seam, the credential map, the log ─────────────────────────────────────────────────────────────────

    /// <summary>THE marshaller back to the UI thread (the same contract as <c>Store.Post</c>): <c>App.cs</c> sets it to
    /// <c>AppHost.Post</c>, a test leaves it, and the default runs the action inline — which is what makes every unit
    /// test in this file's suite single-threaded and deterministic.</summary>
    public static Action<Action> Post { get; set; } = static a => a();

    // LOGGING is `Platform`'s always-on stream (`Log.Info/Warn/Error`), under the category "spotify". There is no
    // sink of our own and no level switch: CLAUDE.md's "always-on logs, no environment switches" is the rule, and a
    // second logger would be a second place to look. NOTHING here may carry a token, a credential or a key — the byte
    // COUNT is the most that is ever printed, and an account name goes through `Platform.Redact`.

    /// <summary>How a credential was obtained, and therefore how the AP wants it presented.</summary>
    public enum CredentialKind : byte
    {
        None = 0,
        /// <summary>The reusable blob the APWelcome handed back — base64 in the store, raw bytes on the wire.</summary>
        ReusableBlob = 1,
        /// <summary>A fresh OAuth access token (the first login, before a blob exists).</summary>
        OAuthToken = 2,
    }

    /// <summary>What a login needs. The secret is never logged and never leaves this file.</summary>
    public readonly record struct Credential(CredentialKind Kind, string Username, string Secret)
    {
        public bool IsEmpty => Kind == CredentialKind.None || string.IsNullOrEmpty(Secret);
    }

    /// <summary>The persisted credential, from <c>Platform</c>'s protected slot. The two types are deliberately
    /// separate — <c>Platform.Credential</c> is what is on DISK (it also carries login5's unread <c>Refresh</c>) and
    /// this is what a LOGIN takes — and the kinds are numbered to match, so the map is a cast and not a switch that
    /// can drift (<c>Platform/Platform.cs</c> §5 says so from its side).
    /// <para>Empty when the slot was never opened (a unit test) or holds nothing: the fold answers
    /// <see cref="SessionFault.NoCredential"/> and no socket is opened.</para></summary>
    static Credential LoadCredential()
        => Platform.TryLoadCredential(out var stored) && !stored.IsEmpty
            ? new Credential((CredentialKind)(byte)stored.Kind, stored.Username, stored.Secret)
            : default;

    // ── 2. the client identity ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The desktop client identity Spotify's gateway checks. ONE definition — the spclient headers, login5,
    /// the client-token attestation and the Connect device all present the same id, or the account sees mismatched
    /// clients. Pinned to Spotify 1.2.94.583, both values observed on the wire. No environment overrides: 0.3 has no
    /// environment switches for behaviour (CLAUDE.md), so a pin moves by editing this block.</summary>
    public static class Identity
    {
        /// <summary>Spotify's public desktop ("keymaster") client id.</summary>
        public const string ClientId = "65b708073fc0480ea92a077233ca87bd";
        public const string AppVersion = "129400583";
        public const string ClientVersion = "1.2.94.583.g60394bd5";
        /// <summary>The web-player build token the two web-bundle pathfinder operations send. Not a semver, observed
        /// verbatim, and it does not move when the desktop pin moves.</summary>
        public const string WebPlayerAppVersion = "896000000";

        public static string AppPlatform { get; } =
            RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "Win32_ARM64" : "Win32_x86_64";

        /// <summary>spclient / pathfinder User-Agent.</summary>
        public static string UserAgent { get; } = "Spotify/" + AppVersion + " " + AppPlatform + "/" + OsDescriptor();

        /// <summary>clienttoken.spotify.com uses a shorter OS stub.</summary>
        public static string ClientTokenUserAgent { get; } = "Spotify/" + AppVersion + " " + AppPlatform + "/0 (PC laptop)";

        static string OsDescriptor()
        {
            if (!OperatingSystem.IsWindows()) return Environment.OSVersion.VersionString;
            var v = Environment.OSVersion.Version;
            string arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "ARM",
                Architecture.X64 => "x64",
                _ => RuntimeInformation.OSArchitecture.ToString(),
            };
            return "Windows " + v.Major + " (" + v + "; " + arch + ")";
        }
    }

    // ── 3. the text arena ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>APPEND-ONLY UTF-8 storage for the session's text (tokens, hosts, the connection id, the device id).
    /// Written by whichever shell thread learnt the value, read as a span by any thread, forever: an entry is never
    /// moved or overwritten, and growth copies into a LARGER array while leaving the old one intact, so a reader
    /// holding the old array keeps reading the same bytes at the same offsets. That is what lets the HTTP shell read a
    /// bearer with no lock, no interner and no string (file header of `Spotify.cs`).</summary>
    static class Text
    {
        static readonly Lock Gate = new();
        static byte[] _buffer = new byte[8192];
        static int _length;

        public static TokenRef Add(ReadOnlySpan<byte> utf8)
        {
            if (utf8.IsEmpty) return default;
            lock (Gate)
            {
                if (_length + utf8.Length > _buffer.Length)
                {
                    int size = Math.Max(_length + utf8.Length, _buffer.Length * 2);
                    var grown = new byte[size];
                    _buffer.AsSpan(0, _length).CopyTo(grown);
                    Volatile.Write(ref _buffer, grown);
                }
                utf8.CopyTo(_buffer.AsSpan(_length));
                var r = new TokenRef(_length, utf8.Length);
                _length += utf8.Length;
                return r;
            }
        }

        public static TokenRef Add(string s) => s.Length == 0 ? default : Add(Encoding.UTF8.GetBytes(s));

        public static ReadOnlySpan<byte> Span(TokenRef r)
            => r.IsEmpty ? default : Volatile.Read(ref _buffer).AsSpan(r.Offset, r.Length);
    }

    /// <summary>The bytes behind a session <see cref="TokenRef"/>. Safe from any thread (see <see cref="Text"/>).</summary>
    public static ReadOnlySpan<byte> Utf8(TokenRef r) => Text.Span(r);

    /// <summary>The text behind a session <see cref="TokenRef"/> as a string. ALLOCATES — for a header value or a url,
    /// not for a per-row path.</summary>
    public static string TextOf(TokenRef r) => r.IsEmpty ? string.Empty : Encoding.UTF8.GetString(Text.Span(r));

    // ── 4. the published session ─────────────────────────────────────────────────────────────────────────────────────

    sealed class Box
    {
        public readonly Session Value;
        public Box(in Session value) => Value = value;
    }

    static Box s_box = new(default);

    /// <summary>The session, as of the last fold. A struct copy out of an IMMUTABLE box, so a reader on any thread
    /// sees a whole transition or none of it.</summary>
    public static Session Current => Volatile.Read(ref s_box).Value;

    /// <summary>The phase, as a signal, for the shell's connection chrome. UI THREAD (it is written inside the post).</summary>
    public static Signal<SessionPhase> Status { get; } = new(SessionPhase.Offline);

    /// <summary>The last fault, as a signal — what the "can't connect" card reads.</summary>
    public static Signal<SessionFault> Fault { get; } = new(SessionFault.None);

    /// <summary>Hand an event to the UI thread (C1). Every shell thread reports through here and nowhere else.</summary>
    static void Publish(SessionEvent e) => Post(() => Apply(e));

    /// <summary>UI THREAD: fold the event, publish the session, write the signals, run the effects that are not part
    /// of a running thread's own procedure.
    /// <para>Which effects are executed here and which are a RECORD: the AP thread is one straight-line procedure
    /// (resolve → connect → handshake → login → mint → pump), so <c>OpenAp</c> and <c>Mint*</c> describe what that
    /// procedure is already doing and are asserted by the tests rather than dispatched twice. Everything that
    /// CROSSES a thread is executed: <c>ResolveHosts</c> starts the AP thread (one per session), <c>Backoff</c> arms
    /// the one-shot retry timer, <c>OpenDealer</c> starts the websocket thread, <c>CloseAll</c> abandons the epoch,
    /// and the two credential effects call the store seam.</para></summary>
    internal static void Apply(in SessionEvent e)
    {
        Session s = Current;
        SessionEffects fx = Step(ref s, e);
        Volatile.Write(ref s_box, new Box(s));
        Status.Value = s.Phase;
        Fault.Value = s.Fault;

        if ((fx & SessionEffects.CloseAll) != 0) CloseEpoch();
        if ((fx & SessionEffects.ClearCredential) != 0) { Platform.ClearCredential(); Log.Info("spotify", "stored credential cleared"); }
        if ((fx & SessionEffects.SaveCredential) != 0) SaveWelcomeCredential();
        if ((fx & SessionEffects.ResolveHosts) != 0) StartAp(s.Epoch);
        if ((fx & SessionEffects.OpenDealer) != 0) StartDealer(s.Epoch);
        if ((fx & SessionEffects.Backoff) != 0) ArmRetry(BackoffMs(s));
    }

    // ── 5. boot, login, logout ───────────────────────────────────────────────────────────────────────────────────────

    static readonly Lock BootGate = new();
    static bool s_booted;

    /// <summary>Statics and signals, once. NO login and no socket: <c>App.cs</c> calls this during composition, and a
    /// session only starts when <see cref="Login"/> is called (or <c>--fake</c> never calls either).</summary>
    public static void Boot()
    {
        lock (BootGate)
        {
            if (s_booted) return;
            s_booted = true;
            string device = Platform.DeviceId;
            string language = Platform.Locale.SpotifyLanguage;
            var s = new Session
            {
                DeviceId = Text.Add(device),
                ClientId = Text.Add(Identity.ClientId),
                Locale = Text.Add(language),
            };
            Volatile.Write(ref s_box, new Box(s));
            for (int i = 0; i < s_keys.Length; i++) s_keys[i] = new KeySlot();
            Log.Info("spotify", "session boot (device " + (device.Length >= 8 ? device[..8] : "none")
                + "…, language " + language + ")");
        }
    }

    /// <summary>Start a session with the stored credential. Returns immediately (C9): the work happens on the AP
    /// thread, and the caller watches <see cref="Status"/>.</summary>
    public static void Login()
    {
        Boot();
        Publish(new SessionEvent(SessionEventKind.Login, Flag: !LoadCredential().IsEmpty));
    }

    /// <summary>Sign out: tear every socket down, forget the tokens, wipe the stored credential.</summary>
    public static void Logout() => Publish(new SessionEvent(SessionEventKind.Logout));

    // ── 6. epochs and threads ────────────────────────────────────────────────────────────────────────────────────────

    static readonly Lock ThreadGate = new();
    static CancellationTokenSource s_epochCts = new();
    static int s_apRunning, s_dealerRunning, s_keepaliveRunning;
    static Timer? s_retryTimer;

    /// <summary>The token every thread of the CURRENT epoch watches. Replaced whole by <see cref="CloseEpoch"/>.</summary>
    static CancellationToken EpochToken => Volatile.Read(ref s_epochCts).Token;

    /// <summary>UI THREAD: abandon the epoch (C4). Cancels the token, closes the sockets (which unblocks the two
    /// reading threads), and fails every pending audio-key waiter so its caller can retry on the next connection.</summary>
    static void CloseEpoch()
    {
        CancellationTokenSource old;
        lock (ThreadGate)
        {
            old = s_epochCts;
            s_epochCts = new CancellationTokenSource();
        }
        // NOT disposed: the threads of that epoch still hold its token, and `ct.WaitHandle` — which the keepalive
        // tick waits on — throws once the source is disposed. One dead CTS per epoch is cheaper than that race, and
        // an epoch is a login, not a frame.
        try { old.Cancel(); } catch (ObjectDisposedException) { }
        CloseApSocket();
        CloseDealerSocket();
        FailAllKeys();
    }

    /// <summary>Start the AP thread for this epoch, once. The guard is a flag the thread itself clears in its
    /// <c>finally</c> rather than <c>Thread.IsAlive</c>, because a retry can arrive while the previous thread is
    /// still unwinding and "alive" would refuse exactly the restart the session is waiting for.</summary>
    static void StartAp(uint epoch)
    {
        lock (ThreadGate)
        {
            if (Interlocked.CompareExchange(ref s_apRunning, 1, 0) != 0) return;
            var ct = s_epochCts.Token;
            new Thread(() => ApLoop(epoch, ct)) { IsBackground = true, Name = "wavee-spotify-ap" }.Start();
        }
    }

    static void StartDealer(uint epoch)
    {
        lock (ThreadGate)
        {
            if (Interlocked.CompareExchange(ref s_dealerRunning, 1, 0) != 0) return;
            var ct = s_epochCts.Token;
            new Thread(() => DealerLoop(epoch, ct)) { IsBackground = true, Name = "wavee-spotify-dealer" }.Start();
        }
    }

    /// <summary>UI THREAD: arm the one-shot reconnect. The ladder is <see cref="BackoffMs"/> — pure, and therefore
    /// tested; this only holds the timer (P10: a named one-shot, never a polling loop).</summary>
    static void ArmRetry(int delayMs)
    {
        s_retryTimer?.Dispose();
        s_retryTimer = new Timer(static _ => Publish(new SessionEvent(SessionEventKind.Retry)), null,
            delayMs, Timeout.Infinite);
        Log.Info("spotify", "reconnecting in " + (delayMs / 1000) + "s");
    }

    // ── 7. HTTP (the three mints and the clock probe) ────────────────────────────────────────────────────────────────

    static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    })
    { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>A synchronous protobuf POST. Shell threads only — it blocks (C9).</summary>
    static byte[] PostProto(string url, byte[] body, string? clientToken, string? userAgent, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-protobuf"));
        if (!string.IsNullOrEmpty(clientToken)) msg.Headers.TryAddWithoutValidation("client-token", clientToken);
        if (!string.IsNullOrEmpty(userAgent)) msg.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        using var resp = Http.Send(msg, ct);
        resp.EnsureSuccessStatusCode();
        using var stream = resp.Content.ReadAsStream(ct);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    static byte[] Get(string url, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, url);
        msg.Headers.TryAddWithoutValidation("User-Agent", Identity.UserAgent);
        using var resp = Http.Send(msg, ct);
        resp.EnsureSuccessStatusCode();
        using var stream = resp.Content.ReadAsStream(ct);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    // ── 8. the AP thread ─────────────────────────────────────────────────────────────────────────────────────────────
    //
    // ONE socket serves login AND audio keys: the handshake's socket and its negotiated codec are kept and become the
    // persistent channel (0.2.9 opened a second one; the AP tracks connections per account and a second handshake is
    // both slower and noisier). The pump answers the AP's pings, routes 0x0d/0x0e to the key table, and reads the two
    // post-login trailers (0x1b country, 0x50 product).

    sealed class ApLogin
    {
        public string Username = "";
        public string Country = "";
        public string Product = "";
        public Tier Tier;
        public byte[] Reusable = [];
    }

    /// <summary>The AP credential was rejected — final for this credential, and the ONLY case that clears the stored
    /// one. A transport failure is a plain exception and merely fails over to the next access point.</summary>
    sealed class ApRejectedException(string message) : Exception(message);

    /// <summary>The AP asked us to connect elsewhere (login_failed = TryAnotherAP): retry the NEXT access point.</summary>
    sealed class ApTryAnotherException(string message) : Exception(message);

    static TcpClient? s_apSocket;
    static NetworkStream? s_apStream;
    static ApCodec s_apCodec;
    static readonly Lock ApSendGate = new();
    static ApLogin? s_login;

    static void CloseApSocket()
    {
        try { s_apStream?.Dispose(); } catch (IOException) { }
        try { s_apSocket?.Dispose(); } catch (SocketException) { }
        s_apStream = null;
        s_apSocket = null;
    }

    /// <summary>ONE attempt, start to finish. It never loops: a failure is published as <c>Dropped</c>, the fold
    /// answers <c>CloseAll | Backoff</c>, and the retry timer starts a fresh thread. That keeps the ladder in one
    /// place (a pure function plus a timer) instead of two.</summary>
    static void ApLoop(uint epoch, CancellationToken ct)
    {
        try
        {
            Credential cred = LoadCredential();
            if (cred.IsEmpty)
            {
                Publish(new SessionEvent(SessionEventKind.Login, Flag: false));
                return;
            }

            ResolveAndPublishHosts(ct, out var accessPoints);
            ConnectAndLogin(cred, accessPoints, ct);
            MintTokens(ct);
            PumpAp(ct);                                              // returns only when the channel drops
            if (ct.IsCancellationRequested) return;
            throw new IOException("the AP channel closed");
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }                          // the epoch closed the socket under us
        catch (ApRejectedException ex)
        {
            Log.Warn("spotify", "the AP rejected the stored credential: " + ex.Message);
            Publish(new SessionEvent(SessionEventKind.AuthRejected));
        }
        catch (Exception ex)
        {
            Log.Warn("spotify", "ap channel failed (epoch " + epoch + ")", ex);
            if (!ct.IsCancellationRequested)
                Publish(new SessionEvent(SessionEventKind.Dropped, Number: (long)SessionFault.Network));
        }
        finally
        {
            CloseApSocket();
            Volatile.Write(ref s_apRunning, 0);
        }
    }

    /// <summary>apresolve, once per attempt: the access points to try (":4070 first", then the rest — the failover
    /// order 0.2.9 settled on), plus the spclient and dealer hosts the session carries.</summary>
    static void ResolveAndPublishHosts(CancellationToken ct, out List<(string Host, int Port)> accessPoints)
    {
        byte[] json = Get("https://apresolve.spotify.com/?type=accesspoint&type=spclient&type=dealer", ct);
        accessPoints = [];
        var rest = new List<(string, int)>();
        Span<Range> ranges = stackalloc Range[32];

        int n = ParseHosts(json, "accesspoint"u8, ranges);
        for (int i = 0; i < n; i++)
        {
            var entry = json.AsSpan(ranges[i]);
            var (hostLength, port) = SplitHostPort(entry, 4070);
            var pair = (Encoding.UTF8.GetString(entry[..hostLength]), port);
            if (port == 4070) accessPoints.Add(pair); else rest.Add(pair);
        }
        accessPoints.AddRange(rest);
        if (accessPoints.Count == 0) throw new IOException("apresolve returned no access points");

        n = ParseHosts(json, "spclient"u8, ranges);
        TokenRef spclient = n > 0 ? Text.Add(FirstHost(json, ranges[0])) : default;
        n = ParseHosts(json, "dealer"u8, ranges);
        TokenRef dealer = n > 0 ? Text.Add(FirstHost(json, ranges[0])) : Text.Add("dealer.spotify.com");
        Publish(new SessionEvent(SessionEventKind.Hosts, Text: spclient, Text2: dealer));

        static string FirstHost(byte[] json, Range r)
        {
            var entry = json.AsSpan(r);
            var (hostLength, _) = SplitHostPort(entry, 443);
            return Encoding.UTF8.GetString(entry[..hostLength]);
        }
    }

    static void ConnectAndLogin(in Credential cred, List<(string Host, int Port)> accessPoints, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var (host, port) in accessPoints)
        {
            ct.ThrowIfCancellationRequested();
            TcpClient? tcp = null;
            try
            {
                tcp = new TcpClient { NoDelay = true };
                tcp.Connect(host, port);
                var stream = tcp.GetStream();
                stream.ReadTimeout = 90_000;                          // longer than the AP's ping interval
                Publish(new SessionEvent(SessionEventKind.Connected));

                var codec = Negotiate(stream, ct);
                Publish(new SessionEvent(SessionEventKind.HandshakeOk));

                var login = Authenticate(stream, ref codec, cred, ct);
                s_apSocket = tcp;
                s_apStream = stream;
                s_apCodec = codec;
                s_login = login;
                tcp = null;                                           // the session owns it now
                PublishWelcome(login);
                PublishPreferredLocale();
                return;
            }
            catch (ApRejectedException) { tcp?.Dispose(); throw; }
            catch (OperationCanceledException) { tcp?.Dispose(); throw; }
            catch (Exception ex)
            {
                last = ex;
                Log.Info("spotify", "access point " + host + " failed (" + ex.Message + ") — trying the next");
                tcp?.Dispose();
            }
        }
        throw last ?? new IOException("all access points failed");
    }

    /// <summary>The DH exchange and the key derivation: ClientHello → APResponse (RSA-verified) → ClientResponse. The
    /// crypto is <c>Spotify.cs</c>'s; this is the socket half.</summary>
    static ApCodec Negotiate(NetworkStream stream, CancellationToken ct)
    {
        Span<byte> priv = stackalloc byte[Handshake.PrivateKeySize];
        RandomNumberGenerator.Fill(priv);
        Span<byte> pub = stackalloc byte[Handshake.ModulusSize];
        Handshake.PublicKey(priv, pub);

        Span<byte> nonce = stackalloc byte[16];
        RandomNumberGenerator.Fill(nonce);
        var hello = new Wavee.Protocol.ClientHello
        {
            BuildInfo = new Wavee.Protocol.BuildInfo
            {
                Product = Wavee.Protocol.Product.Client,
                Platform = Wavee.Protocol.Platform.Win32X86,
                Version = 124200447,
            },
            CryptosuitesSupported = { Wavee.Protocol.Cryptosuite.Shannon },
            LoginCryptoHello = new Wavee.Protocol.LoginCryptoHelloUnion
            {
                DiffieHellman = new Wavee.Protocol.LoginCryptoDiffieHellmanHello
                {
                    Gc = ByteString.CopyFrom(pub),
                    ServerKeysKnown = 1,
                },
            },
            ClientNonce = ByteString.CopyFrom(nonce),
            Padding = ByteString.CopyFrom(new byte[] { 0x1e }),
        };

        byte[] helloProto = hello.ToByteArray();
        var accumulator = new MemoryStream(helloProto.Length + 1024);
        byte[] frame = new byte[6 + helloProto.Length];
        Handshake.WriteHelloFrame(helloProto, frame);
        accumulator.Write(frame, 0, frame.Length);
        stream.Write(frame, 0, frame.Length);

        Span<byte> size4 = stackalloc byte[4];
        stream.ReadExactly(size4);
        accumulator.Write(size4);
        int bodyLength = Handshake.ApResponseBodyLength(size4);
        if (bodyLength <= 0) throw new InvalidDataException("APResponse frame size out of range");
        byte[] body = new byte[bodyLength];
        stream.ReadExactly(body);
        accumulator.Write(body, 0, body.Length);
        ct.ThrowIfCancellationRequested();

        var response = Wavee.Protocol.APResponseMessage.Parser.ParseFrom(body);
        if (response.Challenge is null)
        {
            if (response.LoginFailed?.ErrorCode == Wavee.Protocol.ErrorCode.TryAnotherAp)
                throw new ApTryAnotherException("the AP asked for another access point");
            throw new InvalidDataException("the AP sent no challenge");
        }

        var dh = response.Challenge.LoginCryptoChallenge.DiffieHellman;
        byte[] gs = dh.Gs.ToByteArray();
        // SECURITY: raw TCP — verify the server's signature over gs BEFORE deriving keys, or an active MITM
        // negotiates the channel and reads the login packet (which carries the credential).
        if (!Handshake.VerifyGs(gs, dh.GsSignature.Span))
            throw new InvalidDataException("AP server signature verification failed — aborting handshake");

        Span<byte> secret = stackalloc byte[Handshake.ModulusSize];
        Handshake.SharedSecret(priv, gs, secret);
        Span<byte> challenge = stackalloc byte[20];
        Span<byte> sendKey = stackalloc byte[32];
        Span<byte> receiveKey = stackalloc byte[32];
        Handshake.DeriveKeys(secret, accumulator.GetBuffer().AsSpan(0, (int)accumulator.Length), challenge, sendKey, receiveKey);

        var plain = new Wavee.Protocol.ClientResponsePlaintext
        {
            LoginCryptoResponse = new Wavee.Protocol.LoginCryptoResponseUnion
            {
                DiffieHellman = new Wavee.Protocol.LoginCryptoDiffieHellmanResponse { Hmac = ByteString.CopyFrom(challenge) },
            },
            PowResponse = new Wavee.Protocol.PoWResponseUnion(),
            CryptoResponse = new Wavee.Protocol.CryptoResponseUnion(),
        };
        byte[] plainProto = plain.ToByteArray();
        byte[] plainFrame = new byte[4 + plainProto.Length];
        Handshake.WriteResponseFrame(plainProto, plainFrame);
        stream.Write(plainFrame, 0, plainFrame.Length);

        return new ApCodec(sendKey, receiveKey);
    }

    /// <summary>Present the credential over the Shannon channel and read the welcome plus the two trailers the AP
    /// pushes on success. Bounded: a missing trailer must not hang the login, so the product/country wait is short and
    /// a missing product reads as <see cref="Tier.Unknown"/> (treated as Free, never optimistically as Premium).</summary>
    static ApLogin Authenticate(NetworkStream stream, ref ApCodec codec, in Credential cred, CancellationToken ct)
    {
        var credentials = new Wavee.Protocol.LoginCredentials
        {
            Typ = cred.Kind == CredentialKind.ReusableBlob
                ? Wavee.Protocol.AuthenticationType.AuthenticationStoredSpotifyCredentials
                : Wavee.Protocol.AuthenticationType.AuthenticationSpotifyToken,
            AuthData = ByteString.CopyFrom(cred.Kind == CredentialKind.ReusableBlob
                ? Convert.FromBase64String(cred.Secret)
                : Encoding.UTF8.GetBytes(cred.Secret)),
        };
        if (cred.Kind == CredentialKind.ReusableBlob && !string.IsNullOrEmpty(cred.Username)) credentials.Username = cred.Username;

        var login = new Wavee.Protocol.ClientResponseEncrypted
        {
            LoginCredentials = credentials,
            SystemInfo = new Wavee.Protocol.SystemInfo
            {
                CpuFamily = Wavee.Protocol.CpuFamily.CpuX8664,
                Os = Wavee.Protocol.Os.Windows,
                SystemInformationString = "wavee-fluentgpu",
                DeviceId = Platform.DeviceId,
            },
        };

        byte[] proto = login.ToByteArray();
        byte[] frame = new byte[ApCodec.FrameLength(proto.Length)];
        codec.Encode(Handshake.CmdLogin, proto, frame);
        stream.Write(frame, 0, frame.Length);

        var result = new ApLogin();
        byte[] buffer = new byte[64 * 1024];
        bool welcome = false;
        int trailers = 0;
        int previousTimeout = stream.ReadTimeout;
        stream.ReadTimeout = 15_000;
        try
        {
            while (!welcome || trailers < 2)
            {
                ct.ThrowIfCancellationRequested();
                byte cmd = ReadPacket(stream, ref codec, ref buffer, out int payloadLength);
                var payload = buffer.AsSpan(0, payloadLength);
                switch (cmd)
                {
                    case Handshake.CmdApWelcome:
                        var w = Wavee.Protocol.APWelcome.Parser.ParseFrom(payload);
                        result.Username = w.CanonicalUsername;
                        result.Reusable = w.ReusableAuthCredentials.ToByteArray();
                        welcome = true;
                        stream.ReadTimeout = 3_000;                 // a brief window for the trailers, then proceed
                        break;
                    case Handshake.CmdAuthFailure:
                        string detail;
                        try
                        {
                            var f = Wavee.Protocol.APLoginFailed.Parser.ParseFrom(payload);
                            detail = f.ErrorCode + " " + f.ErrorDescription;
                        }
                        catch (InvalidProtocolBufferException) { detail = payload.Length + " bytes"; }
                        throw new ApRejectedException(detail);
                    case Handshake.CmdCountryCode:
                        result.Country = Encoding.UTF8.GetString(payload);
                        trailers++;
                        break;
                    case Handshake.CmdProductInfo:
                        result.Tier = ProductXml.TierOf(payload);
                        var type = ProductXml.Value(payload, "type"u8);
                        result.Product = type.IsEmpty ? "unknown" : Encoding.UTF8.GetString(type);
                        trailers++;
                        break;
                    case Handshake.CmdPing:
                        SendAp(stream, ref codec, Handshake.CmdPong, [0, 0, 0, 0]);
                        break;
                }
            }
        }
        catch (IOException) when (welcome)
        {
            // The trailer window elapsed. Proceed with what arrived (an absent 0x50 stays Tier.Unknown).
        }
        finally { stream.ReadTimeout = previousTimeout; }

        if (!welcome) throw new InvalidDataException("no APWelcome and no AuthFailure after login");
        return result;
    }

    /// <summary>Read one AP packet into <paramref name="buffer"/> (grown if the payload needs it) and return the
    /// command; the plaintext is <c>buffer[..length]</c> (a tuple cannot carry a span). A failed MAC is fatal: a Shannon
    /// channel cannot resynchronise.</summary>
    static byte ReadPacket(NetworkStream stream, ref ApCodec codec, ref byte[] buffer, out int length)
    {
        Span<byte> header = stackalloc byte[3];
        stream.ReadExactly(header);
        (byte cmd, length) = codec.BeginDecode(header);
        if (buffer.Length < length + 4) buffer = new byte[Math.Max(length + 4, buffer.Length * 2)];
        stream.ReadExactly(buffer.AsSpan(0, length + 4));
        if (!codec.EndDecode(buffer.AsSpan(0, length), buffer.AsSpan(length, 4)))
            throw new InvalidDataException("AP packet MAC check failed");
        return cmd;
    }

    /// <summary>Send one AP packet. Serialised: the pump thread and an audio-key caller share the socket, and Shannon
    /// counts packets — two encodes racing would desync the channel.</summary>
    static void SendAp(NetworkStream stream, ref ApCodec codec, byte cmd, ReadOnlySpan<byte> payload)
    {
        int n = ApCodec.FrameLength(payload.Length);
        byte[]? heap = n > 1024 ? new byte[n] : null;                 // a packet this client sends is ~50 bytes
        Span<byte> frame = heap is not null ? heap : stackalloc byte[n];
        lock (ApSendGate)
        {
            codec.Encode(cmd, payload, frame);
            stream.Write(frame[..n]);
        }
    }

    /// <summary>Send on the SESSION's channel (the audio-key path and the locale publish). False when there is none.</summary>
    static bool SendOnChannel(byte cmd, ReadOnlySpan<byte> payload)
    {
        var stream = s_apStream;
        if (stream is null) return false;
        try { SendAp(stream, ref s_apCodec, cmd, payload); return true; }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { return false; }
    }

    /// <summary>Spotify wants the 0x0f random preamble immediately before the 0x74 key/value packet.</summary>
    static void PublishPreferredLocale()
    {
        Span<byte> preamble = stackalloc byte[20];
        RandomNumberGenerator.Fill(preamble);
        if (!SendOnChannel(Handshake.CmdLocalePreamble, preamble)) return;
        string spotifyLanguage = Platform.Locale.SpotifyLanguage;
        ReadOnlySpan<char> language = spotifyLanguage.AsSpan(0, Math.Min(2, spotifyLanguage.Length));
        Span<byte> ascii = stackalloc byte[2];
        for (int i = 0; i < language.Length; i++) ascii[i] = (byte)language[i];
        Span<byte> body = stackalloc byte[32];
        int n = Handshake.WritePreferredLocale(ascii[..language.Length], body);
        SendOnChannel(Handshake.CmdPreferredLocale, body[..n]);
    }

    /// <summary>The persistent channel: answer pings, route key replies, ignore everything else (mercury and the
    /// legacy packets are not used by this client). Returns when the channel drops.</summary>
    static void PumpAp(CancellationToken ct)
    {
        var stream = s_apStream ?? throw new IOException("no AP channel");
        byte[] buffer = new byte[64 * 1024];
        Span<byte> key = stackalloc byte[AudioKey.KeyLength];
        while (!ct.IsCancellationRequested)
        {
            byte cmd = ReadPacket(stream, ref s_apCodec, ref buffer, out int payloadLength);
            var payload = buffer.AsSpan(0, payloadLength);
            switch (cmd)
            {
                case Handshake.CmdAesKey:
                    if (AudioKey.TryReadKey(payload, out uint okSeq, key)) CompleteKey(okSeq, key, 0);
                    break;
                case Handshake.CmdAesKeyError:
                    if (AudioKey.TryReadError(payload, out uint badSeq, out int code)) CompleteKey(badSeq, default, code == 0 ? -1 : code);
                    break;
                case Handshake.CmdPing:
                    SendAp(stream, ref s_apCodec, Handshake.CmdPong, [0, 0, 0, 0]);   // librespot answers 0x00000000
                    break;
                case Handshake.CmdPongAck:
                    break;
            }
        }
    }

    static void PublishWelcome(ApLogin login)
    {
        // The interning happens INSIDE the post — i.e. on the UI thread, the only thread that may touch the
        // interner (C1, and `Entities/Store.cs`'s "the store thread NEVER resolves").
        Post(() =>
        {
            var e = new SessionEvent(SessionEventKind.Welcome,
                Id: Entities.Intern(Encoding.UTF8.GetBytes(login.Username)),
                Id2: Entities.Intern(Encoding.UTF8.GetBytes(login.Country)),
                Id3: Entities.Intern(Encoding.UTF8.GetBytes(login.Product)),
                Number: (long)login.Tier);
            Apply(e);
        });
        Log.Info("spotify", "logged in (" + Platform.Redact(login.Username) + ", " + login.Product + ", "
            + login.Country + ", " + login.Reusable.Length + "-byte reusable credential)");
    }

    /// <summary>UI THREAD (a <see cref="SessionEffects.SaveCredential"/> effect): persist the reusable blob the welcome
    /// carried. The bytes are never logged, only their count.</summary>
    static void SaveWelcomeCredential()
    {
        var login = s_login;
        if (login is null || login.Reusable.Length == 0) return;
        Platform.SaveCredential(new Wavee.Credential(Wavee.CredentialKind.ReusableBlob, login.Username,
            Convert.ToBase64String(login.Reusable), Refresh: null));
    }

    // ── 9. the tokens (client-token attestation, then login5) ────────────────────────────────────────────────────────

    static readonly Lock TokenGate = new();
    static string? s_clientToken, s_accessToken;
    static long s_clientTokenExpiresAtMs, s_accessExpiresAtMs;

    static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    static void MintTokens(CancellationToken ct)
    {
        string? clientToken = MintClientToken(ct);
        Publish(new SessionEvent(SessionEventKind.ClientTokenMinted,
            Text: Text.Add(clientToken ?? string.Empty), Number: s_clientTokenExpiresAtMs));

        string access = MintAccessToken(force: true, ct);
        Publish(new SessionEvent(SessionEventKind.AccessTokenMinted,
            Text: Text.Add(access), Number: s_accessExpiresAtMs));
    }

    /// <summary>The attestation clienttoken.spotify.com hands out. Best effort: a missing one surfaces downstream as a
    /// 403 the caller can report, which is more useful than failing the whole login here.</summary>
    static string? MintClientToken(CancellationToken ct)
    {
        try
        {
            var request = new Wavee.Protocol.ClientToken.ClientTokenRequest
            {
                RequestType = Wavee.Protocol.ClientToken.ClientTokenRequestType.RequestClientDataRequest,
                ClientData = new Wavee.Protocol.ClientToken.ClientDataRequest
                {
                    ClientVersion = Identity.ClientVersion,
                    ClientId = Identity.ClientId,
                    ConnectivitySdkData = BuildConnectivity(),
                },
            };

            for (int attempt = 0; attempt < 3; attempt++)
            {
                byte[] bytes = PostProto("https://clienttoken.spotify.com/v1/clienttoken",
                    request.ToByteArray(), null, Identity.ClientTokenUserAgent, ct);
                var response = Wavee.Protocol.ClientToken.ClientTokenResponse.Parser.ParseFrom(bytes);
                switch (response.ResponseType)
                {
                    case Wavee.Protocol.ClientToken.ClientTokenResponseType.ResponseGrantedTokenResponse:
                        var granted = response.GrantedToken;
                        lock (TokenGate)
                        {
                            s_clientToken = granted.Token;
                            s_clientTokenExpiresAtMs = NowMs + 1000L * (granted.RefreshAfterSeconds > 0 ? granted.RefreshAfterSeconds : 7200);
                        }
                        Log.Info("spotify", "client-token minted");
                        return granted.Token;
                    case Wavee.Protocol.ClientToken.ClientTokenResponseType.ResponseChallengesResponse:
                        request = SolveClientTokenChallenge(response.Challenges);
                        break;
                    default:
                        return null;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidProtocolBufferException or IOException)
        {
            Log.Warn("spotify", "client-token failed — continuing without one (spclient may 403)", ex);
        }
        return null;
    }

    static Wavee.Protocol.ClientToken.ConnectivitySdkData BuildConnectivity()
    {
        // device_id here is the local-machine SID prefix on desktop, NOT the Spotify Connect device id.
        var data = new Wavee.Protocol.ClientToken.ConnectivitySdkData
        {
            DeviceId = MachineSid() ?? Platform.DeviceId,
            PlatformSpecificData = new Wavee.Protocol.ClientToken.PlatformSpecificData(),
        };
        if (OperatingSystem.IsWindows())
        {
            data.PlatformSpecificData.DesktopWindows = new Wavee.Protocol.ClientToken.NativeDesktopWindowsData
            {
                OsVersion = 10,
                OsBuild = Environment.OSVersion.Version.Build,
                PlatformId = 2,
                UnknownValue5 = 12,           // wire-observed on a 1.2.93.667 ARM64 desktop
                UnknownValue6 = 12,
                PeMachine = RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.X86 => 332,
                    Architecture.X64 => 34404,
                    Architecture.Arm => 452,
                    Architecture.Arm64 => 43620,
                    _ => 34404,
                },
                // image_file_machine + unknown_value_10 are intentionally omitted (genuine-client signature).
            };
        }
        return data;
    }

    /// <summary>The local machine SID prefix (S-1-5-21-…) the desktop client sends as the attestation's device id.
    /// THE ONE Windows-registry read left in this file: <c>Platform</c> has no member for it (it persists nothing and
    /// nothing else in the app wants it), so a seam would be a public surface with exactly one caller. Null on any
    /// failure and on a non-Windows run — the caller then sends the Connect device id, which the 0.2.9 client also
    /// did whenever the read failed.</summary>
    static string? MachineSid()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var profiles = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (profiles is null) return null;
            foreach (string name in profiles.GetSubKeyNames())
            {
                if (!name.StartsWith("S-1-5-21-", StringComparison.Ordinal)) continue;
                int lastDash = name.LastIndexOf('-');
                if (lastDash <= "S-1-5-21".Length) continue;
                return name[..lastDash];
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // A locked-down profile: the attestation takes the device id instead.
        }
        return null;
    }

    static Wavee.Protocol.ClientToken.ClientTokenRequest SolveClientTokenChallenge(Wavee.Protocol.ClientToken.ChallengesResponse challenges)
    {
        var request = new Wavee.Protocol.ClientToken.ClientTokenRequest
        {
            RequestType = Wavee.Protocol.ClientToken.ClientTokenRequestType.RequestChallengeAnswersRequest,
            ChallengeAnswers = new Wavee.Protocol.ClientToken.ChallengeAnswersRequest { State = challenges.State },
        };
        Span<byte> suffix = stackalloc byte[16];
        Span<char> hex = stackalloc char[32];
        foreach (var challenge in challenges.Challenges)
        {
            if (challenge.Type != Wavee.Protocol.ClientToken.ChallengeType.ChallengeHashCash) continue;
            var parameters = challenge.EvaluateHashcashParameters;
            if (parameters is null) continue;
            RandomNumberGenerator.Fill(suffix);
            Hashcash.Solve([], Convert.FromHexString(parameters.Prefix), parameters.Length, suffix);
            Hex.Encode(suffix, hex);
            request.ChallengeAnswers.Answers.Add(new Wavee.Protocol.ClientToken.ChallengeAnswer
            {
                ChallengeType = Wavee.Protocol.ClientToken.ChallengeType.ChallengeHashCash,
                HashCash = new Wavee.Protocol.ClientToken.HashCashAnswer { Suffix = new string(hex).ToUpperInvariant() },
            });
        }
        return request;
    }

    /// <summary>login5 exchanges the reusable blob for the bearer spclient accepts (the OAuth token is a Web-API
    /// audience and spclient refuses it). Solves the hashcash if challenged — this variant hashes context =
    /// login_context with a RAW-byte suffix, where the client-token variant uses an empty context and hex.</summary>
    static string MintAccessToken(bool force, CancellationToken ct)
    {
        lock (TokenGate)
        {
            if (!force && s_accessToken is { Length: > 0 } && NowMs < s_accessExpiresAtMs - 120_000) return s_accessToken;

            var login = s_login ?? throw new InvalidOperationException("login5 before a welcome");
            var request = new Wavee.Protocol.Login.LoginRequest
            {
                ClientInfo = new Wavee.Protocol.Login.ClientInfo { ClientId = Identity.ClientId, DeviceId = Platform.DeviceId },
                StoredCredential = new Wavee.Protocol.Login.StoredCredential
                {
                    Username = login.Username,
                    Data = ByteString.CopyFrom(login.Reusable),
                },
            };

            for (int attempt = 0; attempt < 4; attempt++)
            {
                byte[] bytes = PostProto("https://login5.spotify.com/v3/login", request.ToByteArray(),
                    s_clientToken, Identity.UserAgent, ct);
                var response = Wavee.Protocol.Login.LoginResponse.Parser.ParseFrom(bytes);
                if (response.Ok is { } ok)
                {
                    s_accessToken = ok.AccessToken;
                    s_accessExpiresAtMs = NowMs + 1000L * (ok.AccessTokenExpiresIn > 0 ? ok.AccessTokenExpiresIn : 3600);
                    Log.Info("spotify", "access token minted (expires in "
                        + (ok.AccessTokenExpiresIn > 0 ? ok.AccessTokenExpiresIn : 3600) + "s)");
                    return ok.AccessToken;
                }
                if (response.Challenges is { } challenges && challenges.Challenges_.Count > 0)
                {
                    SolveLogin5Challenges(request, response);
                    continue;
                }
                if (response.Error != Wavee.Protocol.Login.LoginError.UnknownError)
                    throw new InvalidOperationException("login5 error: " + response.Error);
            }
            throw new InvalidOperationException("login5: no access token after retries");
        }
    }

    static void SolveLogin5Challenges(Wavee.Protocol.Login.LoginRequest request, Wavee.Protocol.Login.LoginResponse response)
    {
        var solutions = new Wavee.Protocol.Login.ChallengeSolutions();
        Span<byte> suffix = stackalloc byte[16];
        foreach (var challenge in response.Challenges.Challenges_)
        {
            if (challenge.Hashcash is not { } hashcash) continue;
            RandomNumberGenerator.Fill(suffix);
            long started = Stopwatch.GetTimestamp();
            Hashcash.Solve(response.LoginContext.Span, hashcash.Prefix.Span, hashcash.Length, suffix);
            var elapsed = Stopwatch.GetElapsedTime(started);
            solutions.Solutions.Add(new Wavee.Protocol.Login.ChallengeSolution
            {
                Hashcash = new Wavee.Protocol.Login.HashcashSolution
                {
                    Suffix = ByteString.CopyFrom(suffix),
                    Duration = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(elapsed),
                },
            });
        }
        request.ChallengeSolutions = solutions;
        request.LoginContext = response.LoginContext;
    }

    /// <summary>The bearer for an spclient / pathfinder request. SHELL THREADS ONLY — it blocks while a refresh is in
    /// flight (C9). <paramref name="force"/> is the 401 path: a cached provider would hand back the rejected token.
    /// Returns null when there is no session to mint from.</summary>
    public static string? AccessToken(bool force = false)
    {
        if (s_login is null) return null;
        try
        {
            string token = MintAccessToken(force, EpochToken);
            if (force)
            {
                Publish(new SessionEvent(SessionEventKind.AccessTokenMinted,
                    Text: Text.Add(token), Number: s_accessExpiresAtMs));
            }
            return token;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
        {
            Log.Warn("spotify", "access-token refresh failed", ex);
            return null;
        }
    }

    /// <summary>The attestation header value, or null. Cheap: minted with the session and refreshed with it.</summary>
    public static string? ClientToken()
    {
        lock (TokenGate) return s_clientToken;
    }

    /// <summary>The spclient base url the request runner prefixes onto a folded path.</summary>
    public static string SpclientBaseUrl()
    {
        var host = Current.SpclientHost;
        return host.IsEmpty ? "https://spclient.wg.spotify.com" : "https://" + TextOf(host);
    }

    /// <summary>The dealer's connection id — the <c>X-Spotify-Connection-Id</c> a PutState must carry.</summary>
    public static string ConnectionId() => TextOf(Current.ConnectionId);

    // ── 10. audio keys ───────────────────────────────────────────────────────────────────────────────────────────────

    public enum AudioKeyResult : byte { Ok = 0, Offline, Timeout, Rejected, Busy }

    /// <summary>One outstanding 0x0c. Bounded table (C8): 32 slots, and a full table REFUSES rather than queues — the
    /// caller (the audio stream) has a fallback path and a queue would only add latency to a failure.</summary>
    sealed class KeySlot
    {
        public readonly ManualResetEventSlim Done = new(false);
        public readonly byte[] Key = new byte[AudioKey.KeyLength];
        public uint Seq;
        public bool InUse;
        public int Error;
    }

    static readonly KeySlot?[] s_keys = new KeySlot?[32];
    static readonly Lock KeyGate = new();
    static uint s_keySeq;

    /// <summary>Fetch the 16-byte AES key for a file over the persistent AP channel. BLOCKS up to
    /// <paramref name="timeoutMs"/> — a shell thread's call (C9), never the UI thread's.</summary>
    public static AudioKeyResult RequestAudioKey(ReadOnlySpan<byte> fileId, ReadOnlySpan<byte> trackGid,
        Span<byte> key16, int timeoutMs = 5000)
    {
        if (key16.Length < AudioKey.KeyLength) throw new ArgumentException("needs 16 bytes", nameof(key16));
        if (s_apStream is null) return AudioKeyResult.Offline;

        KeySlot? slot = null;
        uint seq;
        lock (KeyGate)
        {
            for (int i = 0; i < s_keys.Length; i++)
            {
                var candidate = s_keys[i];
                if (candidate is null || candidate.InUse) continue;
                slot = candidate;
                break;
            }
            if (slot is null) return AudioKeyResult.Busy;
            seq = s_keySeq++;
            slot.InUse = true;
            slot.Seq = seq;
            slot.Error = 0;
            slot.Done.Reset();
        }

        try
        {
            Span<byte> body = stackalloc byte[AudioKey.RequestLength];
            int n = AudioKey.WriteRequest(fileId, trackGid, seq, body);
            if (!SendOnChannel(Handshake.CmdAesKeyRequest, body[..n])) return AudioKeyResult.Offline;
            if (!slot.Done.Wait(timeoutMs)) return AudioKeyResult.Timeout;
            if (slot.Error != 0) return AudioKeyResult.Rejected;
            slot.Key.CopyTo(key16);
            return AudioKeyResult.Ok;
        }
        finally
        {
            lock (KeyGate) slot.InUse = false;
        }
    }

    /// <summary>AP PUMP THREAD: hand a reply to whoever is waiting for that sequence.</summary>
    static void CompleteKey(uint seq, ReadOnlySpan<byte> key, int error)
    {
        lock (KeyGate)
        {
            for (int i = 0; i < s_keys.Length; i++)
            {
                var slot = s_keys[i];
                if (slot is null || !slot.InUse || slot.Seq != seq) continue;
                if (error == 0 && key.Length >= AudioKey.KeyLength) key[..AudioKey.KeyLength].CopyTo(slot.Key);
                slot.Error = error;
                slot.Done.Set();
                return;
            }
        }
    }

    /// <summary>The channel dropped: release every waiter so its caller can retry on the next connection.</summary>
    static void FailAllKeys()
    {
        lock (KeyGate)
        {
            for (int i = 0; i < s_keys.Length; i++)
            {
                var slot = s_keys[i];
                if (slot is null || !slot.InUse) continue;
                slot.Error = -1;
                slot.Done.Set();
            }
        }
    }

    // ── 11. the dealer websocket ─────────────────────────────────────────────────────────────────────────────────────
    //
    // ONE firehose: cluster pushes, library/playlist events and the Connect REQUEST commands all arrive here. This
    // loop owns the protocol half only — ping/pong, the connection id, the half-open watchdog and the reconnect — and
    // hands every other frame to `Spotify.Connect.OnDealer` (owner F), which decides what it means.

    /// <summary>The keepalive interval (P10 names this timer).</summary>
    public const int DealerPingIntervalMs = 30_000;

    /// <summary>No frame at all — not even a pong to our pings — for this long means the TCP socket is dead but not
    /// closed ("half open"): abort it so the blocked receive throws and the loop reconnects.</summary>
    public const int DealerDeadAfterMs = 70_000;

    /// <summary>Big enough for a full cluster push plus its inflated form (the parse decodes into the head and
    /// inflates into the tail).</summary>
    const int DealerScratchBytes = 256 * 1024;

    static ClientWebSocket? s_dealer;
    static long s_lastDealerTick;
    static readonly Lock DealerSendGate = new();

    static void CloseDealerSocket()
    {
        var ws = s_dealer;
        s_dealer = null;
        try { ws?.Abort(); } catch (ObjectDisposedException) { }
        ws?.Dispose();
    }

    static void DealerLoop(uint epoch, CancellationToken ct)
    {
        try { DealerLoopCore(epoch, ct); }
        finally { Volatile.Write(ref s_dealerRunning, 0); }
    }

    static void DealerLoopCore(uint epoch, CancellationToken ct)
    {
        byte[] scratch = new byte[DealerScratchBytes];
        byte[] receive = new byte[64 * 1024];
        var frame = new MemoryStream(64 * 1024);
        bool forceToken = false;
        bool retried = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                string? token = AccessToken(forceToken);
                forceToken = false;
                if (token is null) throw new IOException("no access token for the dealer");

                string host = TextOf(Current.DealerHost);
                if (host.Length == 0) host = "dealer.spotify.com";

                var ws = new ClientWebSocket();
                s_dealer = ws;
                try
                {
                    ws.ConnectAsync(new Uri("wss://" + host + "/?access_token=" + Uri.EscapeDataString(token)), ct)
                      .GetAwaiter().GetResult();
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    // A failed wss handshake is indistinguishable from a rejected (expired) token at this layer, and
                    // the plain provider only re-mints near expiry: force one for the next attempt.
                    forceToken = true;
                    throw;
                }

                Volatile.Write(ref s_lastDealerTick, Environment.TickCount64);
                Log.Info("spotify", "dealer connected (" + host + ")");
                StartKeepalive(ws, ct);
                Receive(ws, frame, receive, scratch, ct);
                throw new IOException("the dealer closed the socket");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Warn("spotify", "dealer dropped (epoch " + epoch + ")", ex);
                CloseDealerSocket();
                if (ct.IsCancellationRequested) return;
                if (forceToken && !retried)
                {
                    // The wss handshake failed and the token is the likeliest reason: ONE retry with a force-minted
                    // bearer before the whole epoch is torn down (0.2.9's G6 fix, kept).
                    retried = true;
                    continue;
                }
                Publish(new SessionEvent(SessionEventKind.Dropped, Number: (long)SessionFault.Network));
                return;   // the epoch closes; the AP loop's backoff owns the next attempt
            }
        }
    }

    static void Receive(ClientWebSocket ws, MemoryStream frame, byte[] receive, byte[] scratch, CancellationToken ct)
    {
        var segment = new ArraySegment<byte>(receive);
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            frame.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = ws.ReceiveAsync(segment, ct).GetAwaiter().GetResult();
                if (result.MessageType == WebSocketMessageType.Close) return;
                frame.Write(receive, 0, result.Count);
            }
            while (!result.EndOfMessage);

            Volatile.Write(ref s_lastDealerTick, Environment.TickCount64);   // any frame means the link is alive
            var utf8 = frame.GetBuffer().AsSpan(0, (int)frame.Length);
            Dispatch(utf8, scratch);
        }
    }

    /// <summary>Protocol frames are answered here; everything else goes to `Connect.OnDealer` (owner F), which parses
    /// it again with the same pure parser. The re-parse is deliberate: it keeps this loop ignorant of what a topic
    /// means, and a frame the Connect glue cares about arrives a few times a second at most.</summary>
    static void Dispatch(ReadOnlySpan<byte> utf8, byte[] scratch)
    {
        var message = DealerFrame.Parse(utf8, scratch);
        switch (message.Kind)
        {
            case DealerFrameKind.Ping:
                SendDealerText(DealerPong);
                return;
            case DealerFrameKind.Pong:
            case DealerFrameKind.Unknown:
                return;
        }

        if (!message.ConnectionId.IsEmpty && message.Uri.StartsWith("hm://pusher/"u8))
        {
            // The pusher's hello, and the ONE frame that carries the connection id a PutState must quote. It has no
            // payload anyone folds, so it never reaches the Connect glue.
            Publish(new SessionEvent(SessionEventKind.DealerOnline, Text: Text.Add(message.ConnectionId)));
            return;
        }

        Connect.OnDealer(utf8);
    }

    static readonly byte[] DealerPing = "{\"type\":\"ping\"}"u8.ToArray();
    static readonly byte[] DealerPong = "{\"type\":\"pong\"}"u8.ToArray();

    static void SendDealerText(ReadOnlySpan<byte> utf8)
    {
        var ws = s_dealer;
        if (ws is null || ws.State != WebSocketState.Open) return;
        byte[] copy = utf8.ToArray();   // the async send outlives the span
        lock (DealerSendGate)
        {
            try { ws.SendAsync(copy.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
            {
                // A failed send drops the connection: the receive loop exits and the epoch reconnects.
            }
        }
    }

    /// <summary>Ack a dealer REQUEST. Called by <c>Spotify.Connect</c> (owner F) after it has folded the command.</summary>
    public static void Reply(ReadOnlySpan<byte> key, bool ok)
    {
        Span<byte> buffer = stackalloc byte[256];
        int n = DealerFrame.WriteReply(buffer, key, ok);
        if (n > 0) SendDealerText(buffer[..n]);
    }

    /// <summary>The 30 s keepalive plus the half-open watchdog plus the 10-minute server-clock re-sync. One named
    /// thread for all three, because all three are "wake up on a schedule and do one small thing" (P10).</summary>
    static void StartKeepalive(ClientWebSocket ws, CancellationToken ct)
    {
        lock (ThreadGate)
        {
            if (Interlocked.CompareExchange(ref s_keepaliveRunning, 1, 0) != 0) return;
            new Thread(() => KeepaliveLoop(ws, ct)) { IsBackground = true, Name = "wavee-spotify-keepalive" }.Start();
        }
    }

    static void KeepaliveLoop(ClientWebSocket ws, CancellationToken ct)
    {
        try { KeepaliveTicks(ws, ct); }
        finally { Volatile.Write(ref s_keepaliveRunning, 0); }
    }

    static void KeepaliveTicks(ClientWebSocket ws, CancellationToken ct)
    {
        long nextClockSync = Environment.TickCount64;   // the first probe happens on the first tick
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            if (ct.WaitHandle.WaitOne(DealerPingIntervalMs)) return;
            if (Environment.TickCount64 - Volatile.Read(ref s_lastDealerTick) > DealerDeadAfterMs)
            {
                Log.Warn("spotify", "dealer half-open (no traffic) — forcing a reconnect");
                try { ws.Abort(); } catch (ObjectDisposedException) { }
                return;
            }
            SendDealerText(DealerPing);
            if (Environment.TickCount64 >= nextClockSync)
            {
                SyncServerClock(ct);
                nextClockSync = Environment.TickCount64 + ServerClockResyncMs;
            }
        }
    }

    // ── 12. the server clock ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>How often the clock is re-probed (P10 names this timer).</summary>
    public const int ServerClockResyncMs = 10 * 60 * 1000;

    const int ClockSamplesPerRound = 3;

    /// <summary>One NTP-style round over <c>/melody/v1/time</c>, folded by <see cref="ObserveClockProbe"/>. A total
    /// failure is a no-op: the previous offset (or the passive bootstrap) stands.</summary>
    static void SyncServerClock(CancellationToken ct)
    {
        Session s = Current;
        long bestRtt = long.MaxValue;
        for (int i = 0; i < ClockSamplesPerRound && !ct.IsCancellationRequested; i++)
        {
            try
            {
                long t1 = NowMs;
                long serverMs = FetchServerTimeMs(ct);
                long t2 = NowMs;
                ObserveClockProbe(ref s, t1, serverMs, t2, ref bestRtt);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
            {
                Log.Warn("spotify", "server-clock probe failed", ex);
            }
        }
        if (bestRtt == long.MaxValue) return;
        Session folded = s;
        Post(() =>
        {
            Session current = Current;
            current.ClockOffsetMs = folded.ClockOffsetMs;
            current.ClockRttMs = folded.ClockRttMs;
            current.ClockSynced = true;
            current.ClockProbed = true;
            Volatile.Write(ref s_box, new Box(current));
        });
        Log.Info("spotify", "server-clock synced offset=" + folded.ClockOffsetMs + "ms rtt=" + folded.ClockRttMs + "ms");
    }

    static long FetchServerTimeMs(CancellationToken ct)
    {
        Span<char> path = stackalloc char[64];
        var request = Build(Current, RequestKind.ServerTime, new RequestArgs(), path);
        byte[] body = Get(SpclientBaseUrl() + new string(request.Path), ct);
        var reader = new Utf8JsonReader(body, new JsonReaderOptions { MaxDepth = 4 });
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            bool wanted = reader.ValueTextEquals("timestamp"u8);
            reader.Read();
            if (wanted && reader.TokenType == JsonTokenType.Number) return reader.GetInt64();
        }
        return 0;
    }

    /// <summary>A free clock sample: every cluster push carries the server's emit time. Called by
    /// <c>Spotify.Connect</c> (owner F) on the dealer thread; the fold is pure and the write is posted (C1).</summary>
    public static void ObserveClusterTimestamp(long serverTimestampMs)
    {
        if (serverTimestampMs <= 0) return;
        long now = NowMs;
        Post(() =>
        {
            Session s = Current;
            bool drift = ObservePassiveClock(ref s, serverTimestampMs, now);
            Volatile.Write(ref s_box, new Box(s));
            if (drift) Log.Info("spotify", "server-clock drift — the next keepalive tick re-probes");
        });
    }
}
