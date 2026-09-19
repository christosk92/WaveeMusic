// ── Spotify/Spotify.OAuth.cs ───────────────────────────────────────────────────────────────────────────────────────
// the interactive sign-in's wire decisions (PKCE loopback, device code) and the sign-in state value
//
// Role: CORE
// Owner: D
// Wave: 2 (pulled forward by decision D2 — gap batch B5, G-030)
// Budget: 300 lines
// Spec: gap register G-030; 0.2.9 `Backend/Spotify/Auth.cs` (DeviceCodeProvider) + `Auth.Loopback.cs` (LoopbackOAuthProvider)
//
// A NAMED PARTIAL OF `Spotify.cs`, split out because that file was already over its budget and the sign-in's folds would
// have taken it past the 30 % line. Same rules as its header (C1, C2, P8/P9) with one named exception: the parses below
// return STRINGS (a token, a pairing code, a url) — they run once per sign-in, never per frame, and the SHELL needs
// strings for the form bodies and the login credential anyway.
//
// A fresh profile has no reusable blob, so its first AP login presents an OAuth ACCESS TOKEN (`AuthenticationSpotifyToken`),
// obtained one of two ways (D2): the PKCE authorization-code flow over a 127.0.0.1 loopback redirect (primary — the
// browser opens accounts.spotify.com), or the device-authorization grant (fallback — a pairing code and a QR for a phone).
// Everything those flows DECIDE is here and pinned by `SpotifyCoreTests`: the authorize url, what one loopback request
// means, what the token endpoint answered, how the device poll backs off. The listener, the browser, the HTTP and the
// threads are `Spotify.Session.cs` §13. NOTHING here logs, and nothing here may ever print a token.

using System.Globalization;
using System.Text.Json;

namespace Wavee;

public static partial class Spotify
{
    /// <summary>The OAuth half of sign-in: endpoints, request shapes and response folds. PURE.</summary>
    public static class OAuth
    {
        public const string AuthorizeEndpoint = "https://accounts.spotify.com/authorize";
        public const string TokenEndpoint = "https://accounts.spotify.com/api/token";
        public const string DeviceAuthorizeEndpoint = "https://accounts.spotify.com/oauth2/device/authorize";
        public const string RedirectPath = "/login";

        /// <summary>0.2.9's scope set, verbatim (the AP's token login needs <c>streaming</c>).</summary>
        public const string Scopes = "streaming app-remote-control user-read-playback-state user-modify-playback-state "
                                   + "user-read-currently-playing playlist-read-private user-library-read user-read-email user-read-private";

        /// <summary>RFC 8628 §3.5: every <c>slow_down</c> adds five seconds to the poll interval, for the rest of the grant.</summary>
        public const int SlowDownStepMs = 5000;

        /// <summary>The loopback redirect for an OS-assigned port — the Spotify desktop client's own <c>/login</c> shape.</summary>
        public static string RedirectUri(int port) => "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + RedirectPath;

        /// <summary>The browser's destination: the authorization-code request with the PKCE S256 challenge and an anti-CSRF
        /// state nonce.</summary>
        public static string AuthorizeUrl(string clientId, string redirectUri, string challenge, string state)
            => AuthorizeEndpoint
             + "?client_id=" + Uri.EscapeDataString(clientId)
             + "&response_type=code"
             + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
             + "&code_challenge_method=S256&code_challenge=" + Uri.EscapeDataString(challenge)
             + "&state=" + Uri.EscapeDataString(state)
             + "&scope=" + Uri.EscapeDataString(Scopes);

        /// <summary>The authorization-code exchange's form body (RFC 7636 §4.5: the verifier proves the code is ours).</summary>
        public static string CodeExchangeForm(string code, string redirectUri, string clientId, string verifier)
            => "grant_type=authorization_code&code=" + Uri.EscapeDataString(code)
             + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
             + "&client_id=" + Uri.EscapeDataString(clientId)
             + "&code_verifier=" + Uri.EscapeDataString(verifier);

        /// <summary>The device-authorization request's form body.</summary>
        public static string DeviceAuthorizeForm(string clientId)
            => "client_id=" + Uri.EscapeDataString(clientId) + "&scope=" + Uri.EscapeDataString(Scopes);

        /// <summary>One device-grant poll's form body.</summary>
        public static string DevicePollForm(string deviceCode, string clientId)
            => "grant_type=" + Uri.EscapeDataString("urn:ietf:params:oauth:grant-type:device_code")
             + "&device_code=" + Uri.EscapeDataString(deviceCode)
             + "&client_id=" + Uri.EscapeDataString(clientId);

        // ── the loopback redirect ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>What one connection to the loopback listener was.</summary>
        public enum Redirect : byte
        {
            /// <summary>Not a request for the redirect path (a favicon, a stray probe): answer 404 and keep listening.</summary>
            NotOurs = 0,
            /// <summary>The authorization code, with OUR state.</summary>
            Code,
            /// <summary>The user refused (<c>error=…</c>) — with our state, so it is really this sign-in's answer.</summary>
            Denied,
            /// <summary>A redirect whose state is not ours (a replay, a forged request): ignore it and keep listening.</summary>
            StateMismatch,
            /// <summary>Our path and our state but neither a code nor an error.</summary>
            Incomplete,
        }

        /// <summary>The target of an HTTP request head's first line (<c>GET /login?code=… HTTP/1.1</c>), or empty when the head
        /// holds no complete GET request line (a pre-connect that sent nothing, a partial read, anything else).</summary>
        public static ReadOnlySpan<byte> RequestTarget(ReadOnlySpan<byte> head)
        {
            int eol = head.IndexOf((byte)'\n');
            if (eol < 0) return default;
            ReadOnlySpan<byte> line = head[..eol];
            if (!line.IsEmpty && line[^1] == (byte)'\r') line = line[..^1];
            if (!line.StartsWith("GET "u8)) return default;
            line = line[4..];
            int space = line.IndexOf((byte)' ');
            return space <= 0 ? default : line[..space];
        }

        /// <summary>Fold one request target against the state nonce this sign-in minted. The state is checked FIRST: an
        /// answer carrying somebody else's state is never believed, not even a refusal.</summary>
        public static Redirect ParseRedirect(ReadOnlySpan<byte> target, string expectedState, out string code)
        {
            code = "";
            int q = target.IndexOf((byte)'?');
            ReadOnlySpan<byte> path = q < 0 ? target : target[..q];
            if (!path.SequenceEqual("/login"u8)) return Redirect.NotOurs;
            string? gotCode = null, gotState = null;
            bool error = false;
            ReadOnlySpan<byte> query = q < 0 ? default : target[(q + 1)..];
            while (!query.IsEmpty)
            {
                int amp = query.IndexOf((byte)'&');
                ReadOnlySpan<byte> pair = amp < 0 ? query : query[..amp];
                query = amp < 0 ? default : query[(amp + 1)..];
                int eq = pair.IndexOf((byte)'=');
                if (eq <= 0) continue;
                ReadOnlySpan<byte> key = pair[..eq], value = pair[(eq + 1)..];
                if (key.SequenceEqual("code"u8)) gotCode = Unescape(value);
                else if (key.SequenceEqual("state"u8)) gotState = Unescape(value);
                else if (key.SequenceEqual("error"u8)) error = true;
            }
            if (gotState is null || !string.Equals(gotState, expectedState, StringComparison.Ordinal)) return Redirect.StateMismatch;
            if (error) return Redirect.Denied;
            if (string.IsNullOrEmpty(gotCode)) return Redirect.Incomplete;
            code = gotCode;
            return Redirect.Code;
        }

        static string Unescape(ReadOnlySpan<byte> value)
            => Uri.UnescapeDataString(System.Text.Encoding.ASCII.GetString(value).Replace('+', ' '));

        // ── the token endpoint ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>What the token endpoint said — the authorization-code exchange and every device-grant poll share the shape
        /// (RFC 6749 §5.1/§5.2, RFC 8628 §3.5).</summary>
        public enum TokenAnswer : byte
        {
            /// <summary>A 5xx, a proxy's HTML page, an empty body, an unknown error: not an answer — keep polling (0.2.9's
            /// resilience: one blip must not kill a multi-minute pairing).</summary>
            Transient = 0,
            Token,
            /// <summary><c>authorization_pending</c>: the user has not approved yet.</summary>
            Pending,
            /// <summary><c>slow_down</c>: poll less often.</summary>
            SlowDown,
            /// <summary><c>expired_token</c>: the pairing code lapsed — offer a new one, it is not an error.</summary>
            Expired,
            /// <summary><c>access_denied</c>: the user refused.</summary>
            Denied,
            /// <summary><c>invalid_grant</c> / <c>invalid_client</c> / …: the request itself was refused; retrying the same one
            /// cannot succeed.</summary>
            Refused,
        }

        /// <summary>Fold one token-endpoint response. <paramref name="accessToken"/> is set only for
        /// <see cref="TokenAnswer.Token"/>. A transport failure never reaches here — the shell maps it to Transient itself.</summary>
        public static TokenAnswer ParseToken(int status, ReadOnlySpan<byte> json, out string accessToken, out int expiresInSeconds)
        {
            accessToken = "";
            expiresInSeconds = 0;
            string? access = null, error = null;
            int expires = 3600;
            try
            {
                var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 16 });
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return TokenAnswer.Transient;
                while (reader.Read() && reader.CurrentDepth > 0)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;
                    if (reader.ValueTextEquals("access_token"u8)) access = ReadString(ref reader);
                    else if (reader.ValueTextEquals("error"u8)) error = ReadString(ref reader);
                    else if (reader.ValueTextEquals("expires_in"u8))
                    {
                        reader.Read();
                        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int e) && e > 0) expires = e;
                    }
                    else { reader.Read(); reader.Skip(); }
                }
            }
            catch (JsonException) { return TokenAnswer.Transient; }

            if (status == 200 && !string.IsNullOrEmpty(access))
            {
                accessToken = access;
                expiresInSeconds = expires;
                return TokenAnswer.Token;
            }
            return error switch
            {
                "authorization_pending" => TokenAnswer.Pending,
                "slow_down" => TokenAnswer.SlowDown,
                "expired_token" => TokenAnswer.Expired,
                "access_denied" => TokenAnswer.Denied,
                "invalid_grant" or "invalid_client" or "unauthorized_client" or "invalid_request" or "unsupported_grant_type"
                    => TokenAnswer.Refused,
                _ => TokenAnswer.Transient,
            };
        }

        // ── the device grant ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A device-authorization answer: the secret device code (POLLED, never shown), the user code and the two
        /// verification urls (SHOWN — the complete one goes into the QR).</summary>
        public readonly record struct DeviceCode(string Code, string UserCode, string VerificationUri,
                                                 string VerificationUriComplete, int ExpiresInSeconds, int IntervalSeconds);

        /// <summary>Fold the device-authorization response. False unless it is a 200 carrying a device code, a user code, a
        /// verification uri and a positive lifetime; a missing <c>verification_uri_complete</c> falls back to the plain uri
        /// and a missing interval to RFC 8628's five seconds.</summary>
        public static bool TryParseDeviceCode(int status, ReadOnlySpan<byte> json, out DeviceCode code)
        {
            code = default;
            if (status != 200) return false;
            string? device = null, user = null, uri = null, complete = null;
            int expires = 0, interval = 5;
            try
            {
                var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 16 });
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
                while (reader.Read() && reader.CurrentDepth > 0)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;
                    if (reader.ValueTextEquals("device_code"u8)) device = ReadString(ref reader);
                    else if (reader.ValueTextEquals("user_code"u8)) user = ReadString(ref reader);
                    else if (reader.ValueTextEquals("verification_uri"u8)) uri = ReadString(ref reader);
                    else if (reader.ValueTextEquals("verification_uri_complete"u8)) complete = ReadString(ref reader);
                    else if (reader.ValueTextEquals("expires_in"u8)) expires = ReadInt(ref reader, 0);
                    else if (reader.ValueTextEquals("interval"u8)) interval = ReadInt(ref reader, 5);
                    else { reader.Read(); reader.Skip(); }
                }
            }
            catch (JsonException) { return false; }
            if (string.IsNullOrEmpty(device) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(uri) || expires <= 0) return false;
            code = new DeviceCode(device, user, uri, string.IsNullOrEmpty(complete) ? uri : complete, expires, Math.Max(1, interval));
            return true;
        }

        /// <summary>The next poll interval after <paramref name="answer"/>: <c>slow_down</c> widens it for good, everything else
        /// keeps it.</summary>
        public static int NextPollIntervalMs(TokenAnswer answer, int intervalMs)
            => answer == TokenAnswer.SlowDown ? intervalMs + SlowDownStepMs : intervalMs;

        /// <summary>Does the grant keep polling after <paramref name="answer"/>? Only the three "not yet" answers do.</summary>
        public static bool KeepsPolling(TokenAnswer answer)
            => answer is TokenAnswer.Pending or TokenAnswer.SlowDown or TokenAnswer.Transient;

        static string? ReadString(scoped ref Utf8JsonReader reader)
        {
            reader.Read();
            return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        }

        static int ReadInt(scoped ref Utf8JsonReader reader, int fallback)
        {
            reader.Read();
            return reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int v) ? v : fallback;
        }
    }

    // ── the sign-in, as a value ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The two ways to sign in (D2): the browser (PKCE loopback, primary) and the pairing code / QR (device grant,
    /// fallback).</summary>
    public enum SignInMethod : byte { Browser = 0, DeviceCode = 1 }

    /// <summary>One flow's rung.</summary>
    public enum SignInStage : byte
    {
        Idle = 0,
        /// <summary>Opening the listener / asking for a pairing code.</summary>
        Starting,
        /// <summary>The browser is open, or the pairing code is on screen: waiting for the user.</summary>
        Waiting,
        /// <summary>The authorization code is being exchanged for a token.</summary>
        Exchanging,
        Failed,
        Denied,
        /// <summary>The pairing code lapsed, or the browser never came back inside its window.</summary>
        Expired,
    }

    /// <summary>Why a flow failed — the UI's copy, chosen as a value, never a sentence and never a server message.</summary>
    public enum SignInError : byte { None = 0, Network, BrowserUnavailable, Refused }

    /// <summary>THE sign-in state the surface renders. Written only on the UI thread (<c>Spotify.SignIn.State</c>). The
    /// strings are what the user is SHOWN (the pairing code and its urls); the device code and every token stay in the
    /// flow's own frame. <see cref="Handed"/> = a token was acquired and handed to the session — from there the session's
    /// own phase is the progress.</summary>
    public readonly record struct SignInState(
        SignInStage Browser, SignInStage Code, SignInError Error,
        string UserCode, string VerificationUri, string VerificationUriComplete, long CodeExpiresAtMs, bool Handed)
    {
        public static SignInState Idle => new(SignInStage.Idle, SignInStage.Idle, SignInError.None, "", "", "", 0, false);

        /// <summary>A pairing code is live and on screen.</summary>
        public bool HasChallenge => Code == SignInStage.Waiting && !string.IsNullOrEmpty(UserCode);

        /// <summary>This state with one flow moved to <paramref name="stage"/>.</summary>
        public SignInState With(SignInMethod method, SignInStage stage, SignInError error = SignInError.None)
            => method == SignInMethod.Browser ? this with { Browser = stage, Error = error } : this with { Code = stage, Error = error };
    }
}
