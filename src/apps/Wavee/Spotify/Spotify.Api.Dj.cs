// ── Spotify/Spotify.Api.Dj.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the AI DJ's text-to-speech fulfil: a narration's request body in, the signed CDN url of its MP3 out
//
// Role: SHELL (one blocking POST; api threads, or the inspector's thread under --fake)
// Spec: docs/plans/wavee/dj-implementation.md §1.4, §2.3 · dj-protocol.md §2.1
//
// THE REQUEST. `POST spclient /client-tts/v1/fulfill`. The body is protobuf `{2 ssml, 3 5, 5 1, 6 6, 7 44100}` (field meanings
// inferred from the CDN path it signs: 3 the provider, 5 the voice, 6 the codec MP3, 7 the sample rate); the caller hands it in
// already built (Spotify.DjTts: the narration item's `media.manifest` field 1.1.4 verbatim, else built from the narration keys).
// The headers are the official client's: `Content-Type: application/x-www-form-urlencoded` (sic: the body is protobuf, and that
// is what the service is sent), `Accept-Encoding: identity`, and the authed set every spclient call carries.
//
// THE ANSWER IS A REDIRECT, NOT A BODY. The service answers 303 with `Location:` = the signed CDN url of the MP3 (valid for 300 s).
// The api runner's client follows redirects (Spotify.Api.cs's `Client`, the .NET default), so a fulfil sent through it would come
// back as the MP3 bytes, and the client-token header would travel to the CDN host with it (.NET drops only Authorization on a
// redirect). This route therefore has its OWN client whose handler never follows one (`AllowAutoRedirect = false`): the status
// and the Location ARE the answer, read raw so the signed token is never re-encoded, and the download is Spotify.DjTts's own GET
// on the audio CDN client, with no Spotify credentials at all (the url is signed).
//
// ONE RETRY, ONLY FOR 401, exactly as `SendAuthed`: a rejected bearer is re-minted once. Every other failure comes back as a status
// (0 = never reached a server) for the caller's own retry policy (Spotify.DjTts: one more fulfil, then the narration is dropped).

using System.Net.Http.Headers;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Api
    {
        /// <summary>The fulfil route's path on spclient (the same path a narration's `media.manifest` names, field 1.1.3).</summary>
        public const string TtsFulfilPath = "/client-tts/v1/fulfill";

        /// <summary>The authed set plus the form content type the official client sends a protobuf body under.</summary>
        const HeaderSet TtsFulfilHeaders = HeaderSet.Bearer | HeaderSet.ClientToken | HeaderSet.Identity | HeaderSet.AcceptLanguage
                                         | HeaderSet.ContentForm;

        /// <summary>What one fulfil came back with: the status (0 = it never reached a server, 409 = the account changed under it) and
        /// the raw `Location` header (an absolute url; a relative one is resolved against the request), or null.</summary>
        public readonly record struct TtsAnswer(int Status, string? Location)
        {
            /// <summary>A redirect with an https url to follow: the answer the fulfil exists for.</summary>
            public bool Redirected => Status is 301 or 302 or 303 or 307 or 308
                                      && Location is { } url && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The fulfil's sockets: a handler that NEVER follows a redirect (the Location is the answer) and asks for no
        /// compression (the request says `Accept-Encoding: identity` itself).</summary>
        static readonly SocketsHttpHandler s_ttsSockets = new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxConnectionsPerServer = 2,
        };

        static readonly HttpClient s_tts = new(global::Wavee.Wire.Handler("api-tts", s_ttsSockets)) { Timeout = TimeSpan.FromSeconds(30) };

        /// <summary>Does the live fulfil client follow redirects? False, and the inspector prints it (`djtts fulfil`).</summary>
        public static bool TtsFollowsRedirects => s_ttsSockets.AllowAutoRedirect;

        /// <summary>POST <paramref name="body"/> to the fulfil route and answer the status and the raw `Location`. Blocks; never the
        /// UI thread (C9) outside the inspector's fake. <paramref name="via"/> replaces the live non-redirecting client with another
        /// transport (`--fake`'s in-process spclient, Spotify.DjTts.Fake.cs); everything else, the headers included, is the same.</summary>
        public static TtsAnswer TtsFulfil(byte[] body, CancellationToken ct, HttpMessageInvoker? via = null)
        {
            for (int attempt = 0; ; attempt++)
            {
                if (!AccountRequestIsCurrent()) return new TtsAnswer(409, null);
                TtsAnswer answer = TtsFulfilOnce(body, ct, via ?? s_tts, out string? sentBearer);
                if (answer.Status != 401 || attempt > 0) return answer;
                if (!AccountRequestIsCurrent()) return new TtsAnswer(409, null);
                if (AccessToken(force: true, rejected: sentBearer) is null) return answer;
            }
        }

        static TtsAnswer TtsFulfilOnce(byte[] body, CancellationToken ct, HttpMessageInvoker transport, out string? sentBearer)
        {
            sentBearer = null;
            string url = BaseUrl(ApiHost.Spclient) + TtsFulfilPath;
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, url);
                StampCause(message);
                Stamp(message, TtsFulfilHeaders, RequestKind.Custom, body, "", out sentBearer);
                message.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                if (!AccountRequestIsCurrent()) return new TtsAnswer(409, null);
                using HttpResponseMessage response = transport.Send(message, ct);
                return new TtsAnswer((int)response.StatusCode, LocationOf(response, url));
            }
            catch (OperationCanceledException) { return new TtsAnswer(0, null); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or ObjectDisposedException)
            {
                Log.Warn("dj", "tts.fulfil failed: " + ex.GetType().Name + " " + ex.Message);
                if (ex is HttpRequestException { StatusCode: null } && Uri.TryCreate(url, UriKind.Absolute, out Uri? failed))
                    NoteSpclientTransportFailure(failed.GetLeftPart(UriPartial.Authority));
                return new TtsAnswer(0, null);
            }
        }

        /// <summary>The `Location` header as the server wrote it (never <see cref="HttpResponseHeaders.Location"/>'s parsed and
        /// re-encoded <see cref="Uri"/>: the signed token's `~` and `=` must reach the CDN byte for byte), resolved against the request
        /// url when it is relative.</summary>
        static string? LocationOf(HttpResponseMessage response, string requestUrl)
        {
            if (!response.Headers.TryGetValues("Location", out IEnumerable<string>? values)) return null;
            string? raw = values.FirstOrDefault();
            if (string.IsNullOrEmpty(raw)) return null;
            if (raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return raw;
            return Uri.TryCreate(new Uri(requestUrl), raw, out Uri? resolved) ? resolved.AbsoluteUri : null;
        }
    }
}
