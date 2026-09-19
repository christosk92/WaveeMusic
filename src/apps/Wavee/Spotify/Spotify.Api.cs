// ── Spotify/Spotify.Api.cs ─────────────────────────────────────────────────────────────────────────────────────────
// one function per request, and the fetch provider that routes the planner's batches through them
//
// Role: SHELL (the runner, the provider) + CORE (the request builders, the route walk, the outcome fold)
// Owner: F
// Wave: 2; gap batch B1b (G-040 provider half, G-041 pathfinder provider, G-042 routes, G-033, G-053, G-055, G-008)
// Budget: 1800 lines
// Spec: plan; gap register §2.3. Named partial: Spotify.Api.Library.cs (the playlist4 lists, the collection, zstd)
//
// WHAT THIS REPLACES. 0.2.9 carried ~20 `Spotify*Service` classes, a `PathfinderClient`, a `PathfinderResource`, a
// four-layer middleware pipeline (auth · client-token · rate limit · pathfinder headers) and a `Resource<K,V>` cache
// per service — every one of them re-deciding a url, a verb and a `Dictionary<string,string>` of headers. Here a
// request is a VALUE: `Spotify.Build` (owner D, CORE, pure) folds (session, kind, args) into a `Request`, a `Route`
// (Spotify.Api.Library.cs) carries the routes whose captured spelling the fold does not have, and this file is the
// RUNNER that turns either value into an `HttpRequestMessage`, plus the one-function-per-request calls that pick a
// kind and fill the arguments. No service classes, no interfaces, no DI and no per-service cache: an answer goes to
// `Spotify.Decode` and lands in the columns, and the columns ARE the cache (D16).
//
// THREADING (C9/C1). Every call here BLOCKS — `HttpClient.Send`, not `SendAsync`, exactly like `Spotify.Session.cs`,
// whose comment this file inherits: these paths exist to block. None of them may run on the UI thread. `Run` is the
// door: a bounded work queue (C8 — 256 deep, and a full queue REFUSES and says so rather than growing) drained by
// four named threads `wavee-spotify-api-0..3`. Nothing here touches a table, a signal or `Entities.Strings` (C1): an
// answer is decoded into a `Staging` on the worker thread and handed to the UI thread through `Spotify.Post`.
//
// THE PROVIDER (§5, gap batch B1b). The planner hands over a batch that shares a NEED — (subject, kind, groups) for
// rows, (relation, offset) for an edge — and the routing table (`FetchRoutes`, Entities/Fetch.Routes.cs) says which
// transport fills each group. The provider walks that table minus the routes this build cannot DECODE:
//
//     batch ──RoutesFor──▶ Metadata kinds ──▶ ONE BatchedEntityRequest, every kind under one EntityRequest per uri
//                     └──▶ Pathfinder ops ──▶ per subject (home, search, browse, album, track, artist overview …)
//                     └──▶ Spclient routes ─▶ per subject (playlist v2 read, permission/base, popcount, top tracks)
//           sealed = need & ~served ── answered WITHOUT: the groups stay asked for the scope, never a failure
//
//     edge ──ForEdge──▶ one route per parent: rootlist / recents (revision-gated /diff), collection paging, members …
//
// and folds every status into ONE verdict (`FetchOutcome`): an answer — `Fetch.Answer(ticket, staging)` through
// `Spotify.Post` — unless a retryable failure is worth another attempt, or nothing answered at all.
//
// EPOCHS (C4). The provider's epoch is the batch's: the staging is stamped with `batch.Epoch` and `Entities.Commit`
// drops the whole batch if the scope has moved. `Abandon` records the new epoch so a batch still walking its subjects
// stops sending the moment its scope is gone.
//
// ALLOCATION, HONESTLY. A network call allocates: one path string, one `HttpRequestMessage`, one response `byte[]`,
// and for the metadata POST one uri string per row (`FetchBatch.Uri`, the door Wave 1 wrote for exactly this, free
// for a text-form row). P8's "zero allocations after warm-up" is a CORE rule and a shell that opens a socket is not
// on that path. What this file refuses is the 0.2.9 shape: three dictionaries and a `StringBuilder` per call, on
// every call, for headers that are a compile-time constant.

using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Md = Wavee.Protocol.Metadata;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee;

public static partial class Spotify
{
    /// <summary>Every Spotify HTTP request the app makes, one function each. SHELL: each one blocks.</summary>
    public static partial class Api
    {
        // ── 1. the answer ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>What a request came back with. A VALUE — the body is the whole response, already read, because a
        /// stream that outlives the call is a stream somebody forgets to dispose.
        ///
        /// <para><see cref="Status"/> 0 means the request never reached a server (DNS, socket, timeout, cancellation).
        /// <see cref="Fetch.Retryable"/> already treats 0 as retryable, which is why a transport failure is a status
        /// here and not an exception.</para></summary>
        public readonly struct Result
        {
            public readonly int Status;
            /// <summary>The response body. Never null; empty for a 204/304 or a transport failure.</summary>
            public readonly byte[] Body;
            /// <summary>The server's <c>Retry-After</c> in seconds, or 0. Handed to <see cref="Fetch.Failed"/>.</summary>
            public readonly int RetryAfterSeconds;
            /// <summary>The server's <c>ETag</c>, VERBATIM and never parsed (the content-filter one is
            /// <c>{iso}#{int}#{int}#{int}</c>, which a typed header parse rejects), or null. Sent back as
            /// <c>If-None-Match</c> by the conditional reads that keep one (<see cref="LikedContentFilters"/>, G-053).</summary>
            public readonly string? ETag;
            public readonly int CacheMaxAgeSeconds;
            public readonly bool CacheNoStore;
            public readonly bool CacheHasMaxAge;

            public Result(int status, byte[] body, int retryAfterSeconds = 0, string? etag = null, int cacheMaxAgeSeconds = 0, bool cacheNoStore = false, bool cacheHasMaxAge = false)
            {
                Status = status;
                Body = body;
                RetryAfterSeconds = retryAfterSeconds;
                ETag = etag;
                CacheMaxAgeSeconds = Math.Max(0, cacheMaxAgeSeconds);
                CacheNoStore = cacheNoStore;
                CacheHasMaxAge = cacheHasMaxAge || cacheMaxAgeSeconds > 0;
            }

            public bool Ok => Status is >= 200 and < 300;
            /// <summary>A conditional read the server answered "unchanged".</summary>
            public bool NotModified => Status == 304;
            public ReadOnlySpan<byte> Bytes => Body;

            /// <summary>The same answer with its body replaced — how a zstd frame becomes the message it wraps.</summary>
            public Result WithBody(byte[] body) => new(Status, body, RetryAfterSeconds, ETag, CacheMaxAgeSeconds, CacheNoStore, CacheHasMaxAge);

            public static Result Transport => new(0, []);
        }

        // ── 2. the worker pool (C8/C9) ───────────────────────────────────────────────────────────────────────────────
        //
        // Four threads, not the thread pool and not `Task.Run`: the pool's threads are the engine's too, and a blocking
        // `HttpClient.Send` on one of them is a frame the loop did not get. Four is `Fetch.MaxInFlight` — the planner
        // never has more than four batches out, so a fifth thread would idle by construction.

        const int Workers = 4;
        const int QueueDepth = 256;

        static readonly BlockingCollection<Action> Work = new(QueueDepth);
        static readonly SpotifyFetchProvider Transport = new();
        static int s_booted;
        static int s_dropped;

        /// <summary>How many work items the queue has refused. Non-zero means the app asked for more network than it
        /// can run; a number the diagnostics page can show, rather than a silence (C8).</summary>
        public static int Dropped => Volatile.Read(ref s_dropped);

        /// <summary>The market and catalogue the metadata service wants alongside the uris. They come off the session
        /// as <c>StringId</c>s, which a shell thread may not resolve (C1), so the session's <c>Welcome</c> effect
        /// (<c>Spotify.Apply</c>, UI thread) copies the account's country here the moment the welcome lands. The empty
        /// default is deliberate: the service falls back to the bearer's own market, and a WRONG market is worse than
        /// none.</summary>
        public static string Market { get; set; } = "";

        /// <inheritdoc cref="Market"/>
        public static string Catalogue { get; set; } = "premium";

        /// <summary>Start the workers and register the <see cref="Fetch"/> provider. Idempotent and public: the GUI's
        /// composition and the headless host both call it once, after <c>Spotify.Boot</c>, and <see cref="Run"/> performs
        /// the same boot lazily for a caller that did not.</summary>
        public static void Boot()
        {
            if (Interlocked.CompareExchange(ref s_booted, 1, 0) != 0) return;
            for (int i = 0; i < Workers; i++)
            {
                int index = i;
                new Thread(() => WorkerLoop(index)) { IsBackground = true, Name = "wavee-spotify-api-" + index }.Start();
            }
            Fetch.Register(Transport);
        }

        /// <summary>Run <paramref name="work"/> on an api thread. Returns false when the queue is full — a caller must
        /// treat that as a failure it answers for (the fetch provider calls <see cref="Fetch.Failed"/>), never as a
        /// request that is merely slow.</summary>
        public static bool Run(Action work)
        {
            if (Volatile.Read(ref s_booted) == 0) Boot();
            if (Work.TryAdd(work)) return true;
            Interlocked.Increment(ref s_dropped);
            Log.Warn("spotify", "api queue full (" + QueueDepth + ") — request refused");
            return false;
        }

        /// <summary>Run a blocking call on an api thread and hand its value back as a task — the door for a caller that
        /// lives in async code (the palette's filler, the lyrics stack) and must not park a pool thread on a socket. A
        /// full queue FAULTS the task (C8); it is never queued somewhere else.</summary>
        public static Task<T> RunAsync<T>(Func<T> work)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool queued = Run(() =>
            {
                try { done.TrySetResult(work()); }
                catch (Exception ex) { done.TrySetException(ex); }
            });
            if (!queued) done.TrySetException(new InvalidOperationException("api queue full (" + QueueDepth + ")"));
            return done.Task;
        }

        static void WorkerLoop(int index)
        {
            foreach (Action work in Work.GetConsumingEnumerable())
            {
                try { work(); }
                catch (Exception ex) { Log.Error("spotify", "api worker " + index + " faulted", ex); }
            }
        }

        // ── 3. the runner ────────────────────────────────────────────────────────────────────────────────────────────

        static readonly HttpClient Client = new(global::Wavee.Wire.Handler("api", new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxConnectionsPerServer = 8,
        }))
        { Timeout = TimeSpan.FromSeconds(30) };

        const string PathfinderHost = "https://api-partner.spotify.com";
        const string SpclientWgHost = "https://spclient.wg.spotify.com";
        const string Login5Host = "https://login5.spotify.com";
        const string ClientTokenHost = "https://clienttoken.spotify.com";
        const string ApResolveHost = "https://apresolve.spotify.com";
        const string DealerFallbackHost = "https://dealer.spotify.com";
        const string ImageUploadHost = "https://image-upload.spotify.com";
        const string XpuiOrigin = "https://xpui.app.spotify.com";

        /// <summary>Which origin answers a host. <see cref="ApiHost.Spclient"/> is the resolved one and the only one
        /// that changes between sessions.</summary>
        public static string BaseUrl(ApiHost host) => host switch
        {
            ApiHost.Spclient => SpclientBaseUrl(),
            ApiHost.SpclientWg => SpclientWgHost,
            ApiHost.Pathfinder => PathfinderHost,
            ApiHost.Login5 => Login5Host,
            ApiHost.ClientToken => ClientTokenHost,
            ApiHost.ApResolve => ApResolveHost,
            ApiHost.Dealer => DealerFallbackHost,
            _ => SpclientBaseUrl(),
        };

        static HttpMethod MethodOf(Verb verb) => verb switch
        {
            Verb.Get => HttpMethod.Get,
            Verb.Post => HttpMethod.Post,
            Verb.Put => HttpMethod.Put,
            Verb.Delete => HttpMethod.Delete,
            Verb.Patch => HttpMethod.Patch,
            _ => HttpMethod.Get,
        };

        // The desktop identity the gateway's mutation routes are gated on rides `HeaderSet.Identity`; the pathfinder
        // gateway wants the WEB-PLAYER pair for the operations Spotify itself serves from the web bundle, which is
        // what `PathfinderWeb` selects. Both are captured constants, not guesses.
        const string WebPlayerUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/147.0.0.0 Safari/537.36";
        static readonly string DesktopPathfinderUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.7680.179 Spotify/"
            + Identity.ClientVersion + " Safari/537.36";

        /// <summary>THE request. Blocks; api threads only (C9).
        ///
        /// <para>One retry, and only one, and only for 401: <c>AccessToken(force: true)</c> re-mints and the request
        /// goes again. Everything else — 429, 5xx, a socket that died — comes back as a status, because the retry
        /// policy belongs to the caller that knows whether a retry buys anything (<see cref="Fetch.Backoff"/> for a
        /// catalog batch; a dropped flush for gabo).</para></summary>
        public static Result Send(RequestKind kind, scoped in RequestArgs args, CancellationToken ct)
        {
            Span<char> buffer = stackalloc char[512];
            Request request = Build(Current, kind, args, buffer);
            if (request.IsEmpty) return Result.Transport;

            string url = BaseUrl(request.Host) + new string(request.Path);
            byte[] body = request.Body.IsEmpty ? [] : request.Body.ToArray();
            string syncReason = request.SyncReason.IsEmpty ? "" : new string(request.SyncReason);
            return SendAuthed(request.Verb, url, request.Headers, kind, body, syncReason, ct);
        }

        /// <summary>A <see cref="Route"/> value → the answer, with the same single 401 retry. A route marked
        /// <see cref="Route.Zstd"/> comes back UNWRAPPED; a zstd body that does not decode is a transport failure
        /// (status 0), never an empty 200 a decoder would read as "the list is empty".</summary>
        public static Result Send(in Route route, byte[] body, CancellationToken ct, string? ifNoneMatch = null)
        {
            Result result = SendAuthed(route.Verb, BaseUrl(route.Host) + route.Path, route.Headers, route.Kind, body,
                route.SyncReason, ct, ifNoneMatch: ifNoneMatch);
            if (!route.Zstd || !IsZstd(result.Body)) return result;
            return Unzstd(result.Body) is { } unwrapped ? result.WithBody(unwrapped) : new Result(0, [], 0, result.ETag);
        }

        /// <summary>The ONE bearer-retry loop every send shares: a 401 re-mints once and goes again, and the second 401
        /// IS the answer — a loop here is how a revoked account burns a token bucket.</summary>
        static Result SendAuthed(Verb verb, string url, HeaderSet headers, RequestKind kind, byte[] body, string syncReason,
            CancellationToken ct, string? contentType = null, string? contentEncoding = null, string? ifNoneMatch = null,
            ClientIdentity? identity = null)
        {
            for (int attempt = 0; ; attempt++)
            {
                if (!AccountRequestIsCurrent()) return new Result(409, []);
                Result result = SendOnce(verb, url, headers, kind, body, syncReason, ct, contentType, contentEncoding, ifNoneMatch, identity);
                if (result.Status != 401 || attempt > 0) return result;
                if (!AccountRequestIsCurrent()) return new Result(409, []);
                if (AccessToken(force: true) is null) return result;
            }
        }

        static Result SendOnce(Verb verb, string url, HeaderSet headers, RequestKind kind, byte[] body,
            string syncReason, CancellationToken ct, string? contentType, string? contentEncoding, string? ifNoneMatch,
            ClientIdentity? identity = null)
        {
            try
            {
                using var message = new HttpRequestMessage(MethodOf(verb), url);
                Stamp(message, headers, kind, body, syncReason);
                if (identity is { } client)
                {
                    message.Headers.TryAddWithoutValidation("App-Platform", client.AppPlatform);
                    message.Headers.TryAddWithoutValidation("Spotify-App-Version", client.AppVersion);
                    message.Headers.TryAddWithoutValidation("User-Agent", client.UserAgent);
                }
                if (ifNoneMatch is { Length: > 0 }) message.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
                if (message.Content is { } payload)
                {
                    if (contentType is not null) payload.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                    if (contentEncoding is not null) payload.Headers.ContentEncoding.Add(contentEncoding);
                }
                // Stamp may mint credentials; re-check after that boundary before any authenticated bytes leave.
                if (!AccountRequestIsCurrent()) return new Result(409, []);
                using HttpResponseMessage response = Client.Send(message, HttpCompletionOption.ResponseHeadersRead, ct);
                return new Result((int)response.StatusCode, ReadBody(response, ct), RetryAfter(response), ETagOf(response), (int)Math.Clamp(response.Headers.CacheControl?.MaxAge?.TotalSeconds ?? 0, 0, int.MaxValue), response.Headers.CacheControl?.NoStore == true, response.Headers.CacheControl?.MaxAge is not null);
            }
            catch (OperationCanceledException)
            {
                return Result.Transport;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or ObjectDisposedException)
            {
                Log.Warn("spotify", "api " + verb + " failed: " + ex.GetType().Name + " " + ex.Message);
                return Result.Transport;
            }
        }

        /// <summary>A POST whose media type and content encoding are the ROUTE's, not the header set's. Two services
        /// want a spelling `HeaderSet` has no bit for — gabo's `application/x-protobuf` + `Content-Encoding: gzip`, and
        /// herodotus's bare `application/x-protobuf` — and one parameter is a smaller lie than two more flags on a
        /// value type that belongs to another file.</summary>
        public static Result PostEncoded(scoped ReadOnlySpan<char> path, ApiHost host, HeaderSet headers, byte[] body,
            string contentType, string? contentEncoding, CancellationToken ct)
            => SendAuthed(Verb.Post, BaseUrl(host) + new string(path), headers, RequestKind.Custom, body, "", ct,
                contentType, contentEncoding);

        /// <summary>A client-identity tuple a request presents INSTEAD of <see cref="Identity"/>'s pin.</summary>
        public readonly record struct ClientIdentity(string AppPlatform, string AppVersion, string UserAgent);

        /// <summary>An spclient POST that presents its own client identity: the local-playback license route, whose token
        /// belongs to a runtime of a DIFFERENT Spotify build than <see cref="Identity"/> pins (the private assembly passes
        /// that build's tuple). Bearer, client-token and Accept-Language as every spclient call carries them; the body's
        /// media type is <paramref name="contentType"/>. Blocks; api threads only (C9).</summary>
        public static Result PostAsClient(string path, byte[] body, string contentType, in ClientIdentity identity, CancellationToken ct)
            => SendAuthed(Verb.Post, BaseUrl(ApiHost.Spclient) + path,
                HeaderSet.Bearer | HeaderSet.ClientToken | HeaderSet.AcceptLanguage, RequestKind.Custom, body, "", ct,
                contentType, null, null, identity);

        static byte[] ReadBody(HttpResponseMessage response, CancellationToken ct)
        {
            if (response.Content.Headers.ContentLength == 0) return [];
            using Stream stream = response.Content.ReadAsStream(ct);
            using var buffer = new MemoryStream(4096);
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }

        static int RetryAfter(HttpResponseMessage response)
        {
            RetryConditionHeaderValue? value = response.Headers.RetryAfter;
            if (value is null) return 0;
            if (value.Delta is { } delta) return (int)delta.TotalSeconds;
            if (value.Date is { } date) return (int)Math.Max(0, (date - DateTimeOffset.UtcNow).TotalSeconds);
            return 0;
        }

        /// <summary>The raw <c>ETag</c> value, or null. Raw on purpose — see <see cref="Result.ETag"/>.</summary>
        static string? ETagOf(HttpResponseMessage response)
            => response.Headers.TryGetValues("ETag", out IEnumerable<string>? values) ? values.FirstOrDefault() : null;

        /// <summary>The two-letter language every `Accept-Language` carries. `Platform.Locale` is decided once at boot
        /// and is what `Spotify.Session` itself publishes to the AP (`Spotify.Session.cs`'s `Boot` and its 0x74 locale
        /// frame both read it), so the header and the account's own preferred locale can never disagree. Reading it is
        /// a struct copy of two string references — no allocation, and safe from an api thread (C1: it is not a
        /// `StringId` and not a table).</summary>
        static string AcceptLanguage => Platform.Locale.SpotifyLanguage;

        /// <summary>Turn the header BIT FIELD into headers. This is the whole of 0.2.9's middleware stack: the set is a
        /// constant the fold chose, so "which headers does this route need" is answered without a dictionary.</summary>
        static void Stamp(HttpRequestMessage message, HeaderSet headers, RequestKind kind, byte[] body, string syncReason)
        {
            HttpRequestHeaders h = message.Headers;
            bool web = (headers & HeaderSet.PathfinderWeb) != 0;
            bool pathfinder = web || (headers & HeaderSet.PathfinderDesktop) != 0;

            if ((headers & HeaderSet.Bearer) != 0 && AccessToken() is { Length: > 0 } bearer)
                h.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
            if ((headers & HeaderSet.ClientToken) != 0 && ClientToken() is { Length: > 0 } clientToken)
                h.TryAddWithoutValidation("client-token", clientToken);

            if (pathfinder)
            {
                h.TryAddWithoutValidation("App-Platform", web ? "WebPlayer" : Identity.AppPlatform);
                h.TryAddWithoutValidation("Spotify-App-Version", web ? Identity.WebPlayerAppVersion : Identity.DesktopSemver);
                h.TryAddWithoutValidation("User-Agent", web ? WebPlayerUserAgent : DesktopPathfinderUserAgent);
            }
            else if ((headers & HeaderSet.Identity) != 0)
            {
                h.TryAddWithoutValidation("App-Platform", Identity.AppPlatform);
                h.TryAddWithoutValidation("Spotify-App-Version", Identity.AppVersion);
                h.TryAddWithoutValidation("User-Agent", Identity.UserAgent);
            }

            if ((headers & HeaderSet.AcceptLanguage) != 0) h.TryAddWithoutValidation("Accept-Language", AcceptLanguage);
            if ((headers & HeaderSet.AcceptProtobuf) != 0) h.TryAddWithoutValidation("Accept", AcceptFor(kind));
            if ((headers & HeaderSet.AcceptJson) != 0) h.TryAddWithoutValidation("Accept", "application/json");
            if ((headers & HeaderSet.ConnectionId) != 0 && ConnectionId() is { Length: > 0 } connectionId)
                h.TryAddWithoutValidation("X-Spotify-Connection-Id", connectionId);
            if ((headers & HeaderSet.NoStore) != 0) h.TryAddWithoutValidation("Cache-Control", "no-store");
            if ((headers & HeaderSet.ApplyLenses) != 0) h.TryAddWithoutValidation("spotify-apply-lenses", "auto");
            if ((headers & HeaderSet.AppliedLenses) != 0) h.TryAddWithoutValidation("spotify-applied-lenses", "auto: YXV0bw==");
            if ((headers & HeaderSet.AcceptListItems) != 0)
                h.TryAddWithoutValidation("x-accept-list-items", "audio-track, audio-episode, video-episode, audiobook");
            if ((headers & HeaderSet.AcceptGeoblock) != 0) h.TryAddWithoutValidation("spotify-accept-geoblock", "dummy");
            if ((headers & HeaderSet.DsaMode) != 0) h.TryAddWithoutValidation("spotify-dsa-mode-enabled", "false");
            if ((headers & HeaderSet.Origin) != 0) h.TryAddWithoutValidation("Origin", SpclientBaseUrl().TrimEnd('/'));
            if ((headers & HeaderSet.XpuiOrigin) != 0) { h.TryAddWithoutValidation("Origin", XpuiOrigin); h.TryAddWithoutValidation("Referer", XpuiOrigin + "/"); }
            if ((headers & HeaderSet.AcceptAny) != 0) h.TryAddWithoutValidation("Accept", "*/*");
            if ((headers & HeaderSet.SoapLicense) != 0) h.TryAddWithoutValidation("SOAPAction", "\"http://schemas.microsoft.com/DRM/2007/03/protocols/AcquireLicense\"");
            if (syncReason.Length > 0) h.TryAddWithoutValidation("spotify-playlist-sync-reason", syncReason);

            const HeaderSet AnyContent = HeaderSet.ContentProtobuf | HeaderSet.ContentJson | HeaderSet.ContentForm | HeaderSet.SoapLicense;
            if (body.Length == 0 && (headers & AnyContent) == 0) return;

            bool gzip = (headers & HeaderSet.GzipBody) != 0;
            var content = new ByteArrayContent(gzip ? Gzip(body) : body);
            // The gateway quirk that costs a week when it is wrong: the playlist-v2 mutation routes want
            // `x-www-form-urlencoded` DESPITE a protobuf body, and a request that says `application/protobuf` there
            // reaches a PASSIVE read handler which 200-OKs without mutating anything. `ContentForm` is that fact.
            if ((headers & HeaderSet.ContentForm) != 0)
                content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
            else if ((headers & HeaderSet.ContentProtobuf) != 0)
                content.Headers.ContentType = new MediaTypeHeaderValue(ContentTypeFor(kind));
            else if ((headers & HeaderSet.ContentJson) != 0)
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            else if ((headers & HeaderSet.SoapLicense) != 0)
                content.Headers.ContentType = new MediaTypeHeaderValue("text/xml") { CharSet = "utf-8" };
            if (gzip)
            {
                // `X-Transfer-Encoding`, NOT `Content-Encoding`: the connect-state and playlist gateways read the
                // transfer header, and a `Content-Encoding: gzip` on those routes is answered with a 400.
                // extended-metadata is the one exception and wants the real content encoding.
                if (kind == RequestKind.ExtendedMetadata) content.Headers.ContentEncoding.Add("gzip");
                else h.TryAddWithoutValidation("X-Transfer-Encoding", "gzip");
            }
            message.Content = content;
        }

        // The media types that are NOT `application/protobuf`, expressed as a switch over the kind rather than as two
        // more header bits: the collection-v2 service answers only to its own vendor type, and the playlist-signals
        // route wants the `x-protobuf` spelling. Both are capture-verified 0.2.9 constants.
        const string CollectionMediaType = "application/vnd.collection-v2.spotify.proto";

        static string AcceptFor(RequestKind kind) => kind switch
        {
            RequestKind.CollectionPage or RequestKind.CollectionDelta or RequestKind.CollectionWrite => CollectionMediaType,
            RequestKind.PlaylistSignals => "application/x-protobuf",
            _ => "application/protobuf",
        };

        static string ContentTypeFor(RequestKind kind) => kind switch
        {
            RequestKind.CollectionPage or RequestKind.CollectionDelta or RequestKind.CollectionWrite => CollectionMediaType,
            _ => "application/protobuf",
        };

        internal static byte[] Gzip(ReadOnlySpan<byte> data)
        {
            using var output = new MemoryStream(data.Length / 2 + 64);
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(data);
            return output.ToArray();
        }

        // ── 4. extended metadata: the 300-uri batch (P4) ─────────────────────────────────────────────────────────────
        //
        // ONE POST answers up to 300 uris and every trait each of them wants. The batching rule is not "300 queries"
        // but "300 URIS, and a uri's kinds never split across two requests" — the response envelope is keyed by
        // (kind, uri) and a half-answered uri reads as an answered one to the decoder, which is how a track ends up
        // permanently missing its descriptors.

        /// <summary>Split <paramref name="uriCount"/> uris into POST-sized runs. PURE, and the reason it is separate
        /// from the send: 700 uris must be 300 + 300 + 100, and a test says so without a socket.</summary>
        public static int Batches(int uriCount, Span<Range> into)
        {
            int n = 0;
            for (int start = 0; start < uriCount && n < into.Length; start += Fetch.MaxUrisPerRequest)
                into[n++] = new Range(start, Math.Min(start + Fetch.MaxUrisPerRequest, uriCount));
            return n;
        }

        /// <summary>Build one <c>BatchedEntityRequest</c>. Every uri gets ONE <c>EntityRequest</c> carrying all of its
        /// kinds, in first-seen order — the wire shape <see cref="Decode.ExtendedMetadata"/> reads back.</summary>
        public static byte[] MetadataBody(ReadOnlySpan<string> uris, ReadOnlySpan<Xm.ExtensionKind> kinds,
            string country, string catalogue)
        {
            Xm.BatchedEntityRequest request = NewBatch(country, catalogue);
            var seen = new Dictionary<string, Xm.EntityRequest>(uris.Length, StringComparer.Ordinal);
            for (int i = 0; i < uris.Length; i++)
            {
                string uri = uris[i];
                if (uri.Length == 0) continue;
                Xm.ExtensionKind kind = i < kinds.Length ? kinds[i] : Xm.ExtensionKind.UnknownExtension;
                if (kind == Xm.ExtensionKind.UnknownExtension) continue;
                if (!seen.TryGetValue(uri, out Xm.EntityRequest? entity))
                {
                    entity = new Xm.EntityRequest { EntityUri = uri };
                    seen[uri] = entity;
                    request.EntityRequest.Add(entity);
                }
                entity.Query.Add(new Xm.ExtensionQuery { ExtensionKind = kind });
            }
            return request.EntityRequest.Count == 0 ? [] : request.ToByteArray();
        }

        /// <summary>One entity uri, one or more traits — the "give me the video associations for this track" call the
        /// detail surfaces make outside the planner's batch.</summary>
        public static byte[] MetadataBody(string uri, ReadOnlySpan<Xm.ExtensionKind> kinds, string country, string catalogue)
        {
            Xm.BatchedEntityRequest request = NewBatch(country, catalogue);
            var entity = new Xm.EntityRequest { EntityUri = uri };
            foreach (Xm.ExtensionKind kind in kinds)
                if (kind != Xm.ExtensionKind.UnknownExtension)
                    entity.Query.Add(new Xm.ExtensionQuery { ExtensionKind = kind });
            if (entity.Query.Count == 0) return [];
            request.EntityRequest.Add(entity);
            return request.ToByteArray();
        }

        /// <summary>THE provider's body (G-040): every uri of a batch, EVERY kind of the batch's need under that uri's one
        /// <c>EntityRequest</c> — the mixed-kind POST. A repeated uri folds into the request already there; an empty uri
        /// (a slot recycled between plan and send) and a kind ≤ 0 are skipped; nothing askable is an empty body.</summary>
        public static byte[] BatchBody(ReadOnlySpan<string> uris, ReadOnlySpan<int> kinds, string country, string catalogue)
        {
            Xm.BatchedEntityRequest request = NewBatch(country, catalogue);
            HashSet<string>? seen = null;
            for (int i = 0; i < uris.Length; i++)
            {
                string uri = uris[i];
                if (uri.Length == 0 || !(seen ??= new HashSet<string>(uris.Length, StringComparer.Ordinal)).Add(uri)) continue;
                var entity = new Xm.EntityRequest { EntityUri = uri };
                foreach (int kind in kinds)
                    if (kind > 0) entity.Query.Add(new Xm.ExtensionQuery { ExtensionKind = (Xm.ExtensionKind)kind });
                if (entity.Query.Count > 0) request.EntityRequest.Add(entity);
            }
            return request.EntityRequest.Count == 0 ? [] : request.ToByteArray();
        }

        static Xm.BatchedEntityRequest NewBatch(string country, string catalogue)
        {
            Span<byte> taskId = stackalloc byte[16];
            RandomNumberGenerator.Fill(taskId);
            return new Xm.BatchedEntityRequest
            {
                Header = new Xm.BatchedEntityRequestHeader
                {
                    Country = country,
                    Catalogue = catalogue,
                    TaskId = ByteString.CopyFrom(taskId),
                },
            };
        }

        /// <summary>POST a prepared <c>BatchedEntityRequest</c>, gzipped.</summary>
        public static Result MetadataPost(byte[] body, CancellationToken ct)
        {
            if (body.Length == 0) return Result.Transport;
            var args = new RequestArgs { Body = body };
            Span<char> buffer = stackalloc char[64];
            Request request = Build(Current, RequestKind.ExtendedMetadata, args, buffer);
            string url = BaseUrl(request.Host) + new string(request.Path);
            HeaderSet headers = request.Headers | HeaderSet.GzipBody;
            return s_metadataCache.Execute(body, Current.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), payload =>
                    SendAuthed(Verb.Post, url, headers, RequestKind.ExtendedMetadata, payload, "", ct));
        }

        /// <summary>One entity, one trait — the door every "extra" below goes through.</summary>
        public static int Extension(string uri, Xm.ExtensionKind kind, Staging into, CancellationToken ct)
        {
            Xm.ExtensionKind[] one = [kind];
            return PostMetadata(MetadataBody(uri, one, Market, Catalogue), into, ct);
        }

        /// <summary>One entity, several traits, ONE request — the track drawer's "everything about this row" call
        /// (0.2.9 `SpotifyTrackExpansionService`, which made four).</summary>
        public static int Extensions(string uri, ReadOnlySpan<Xm.ExtensionKind> kinds, Staging into, CancellationToken ct)
            => PostMetadata(MetadataBody(uri, kinds, Market, Catalogue), into, ct);

        static int PostMetadata(byte[] body, Staging into, CancellationToken ct)
        {
            if (body.Length == 0) return 0;
            Result result = MetadataPost(body, ct);
            if (result.Ok && result.Body.Length > 0) Decode.ExtendedMetadata(result.Bytes, into);
            return result.Status;
        }

        /// <summary>The four traits the track drawer opens with.</summary>
        public static int TrackExpansion(string trackUri, Staging into, CancellationToken ct)
        {
            Xm.ExtensionKind[] kinds =
            [
                Xm.ExtensionKind.VideoAssociations,
                Xm.ExtensionKind.AudioAssociations,
                Xm.ExtensionKind.AudioFiles,
                Xm.ExtensionKind.ThreebandWaveforms,
            ];
            return Extensions(trackUri, kinds, into, ct);
        }

        public static int TrackCredits(string trackUri, Staging into, CancellationToken ct)
            => Extension(trackUri, Xm.ExtensionKind.CreditsV2Trait, into, ct);

        public static int PreRelease(string uri, Staging into, CancellationToken ct)
            => Extension(uri, Xm.ExtensionKind.Prerelease, into, ct);

        public static int Descriptors(string trackUri, Staging into, CancellationToken ct)
            => Extension(trackUri, Xm.ExtensionKind.TrackDescriptor, into, ct);

        /// <summary>Kind 185 — the per-track play counts an album page shows. The proto's own name for 185 is
        /// <c>ON_PLATFORM_REPUTATION_TRAIT</c>; <see cref="Decode.Ext.PlayCount"/> is what it carries.</summary>
        public static int PlayCounts(string albumUri, Staging into, CancellationToken ct)
            => Extension(albumUri, Xm.ExtensionKind.OnPlatformReputationTrait, into, ct);

        public static int Publishing(string trackUri, Staging into, CancellationToken ct)
            => Extension(trackUri, Xm.ExtensionKind.PublishingMetadataTrait, into, ct);

        public static int AudioAttributes(string trackUri, Staging into, CancellationToken ct)
            => Extension(trackUri, Xm.ExtensionKind.AudioAttributesV2, into, ct);

        public static int VisualIdentity(string uri, Staging into, CancellationToken ct)
            => Extension(uri, Xm.ExtensionKind.VisualIdentityTrait, into, ct);

        public static int AlbumRecommendations(string albumUri, Staging into, CancellationToken ct)
            => Extension(albumUri, Xm.ExtensionKind.RecommendedPlaylists, into, ct);

        /// <summary>The batched half of profile resolution: kind 15 for canonical <c>spotify:user:</c> uris. Whatever
        /// it leaves unresolved goes to <see cref="Profile"/> one at a time.</summary>
        public static int UserProfiles(ReadOnlySpan<string> userUris, Staging into, CancellationToken ct)
        {
            if (userUris.Length == 0) return 0;
            ReadOnlySpan<int> kind = [FetchRoutes.UserProfile];
            return PostMetadata(BatchBody(userUris, kind, Market, Catalogue), into, ct);
        }

        /// <summary>The <c>spotify:user:</c> uris of a <c>BatchedExtensionResponse</c> that kind <paramref name="kind"/>
        /// ANSWERED — a 2xx entity header with the extension data present. PURE; the profile fallback's "who is left"
        /// (G-033), read with the same rule <see cref="Decode.ExtendedMetadata"/> stages by.</summary>
        public static HashSet<string> AnsweredUris(ReadOnlySpan<byte> response, int kind)
        {
            var answered = new HashSet<string>(StringComparer.Ordinal);
            var r = new Decode.ProtoReader(response);
            while (r.Next())
            {
                if (r.Field != 2 || r.Wire != 2) { r.Skip(); continue; }
                var array = r.Message();
                int arrayKind = 0;
                List<string>? hits = null;
                while (array.Next())
                {
                    if (array.Field == 2 && array.Wire == 0) { arrayKind = array.Int32(); continue; }
                    if (array.Field != 3 || array.Wire != 2) { array.Skip(); continue; }
                    var entity = array.Message();
                    ReadOnlySpan<byte> uri = default;
                    int status = 200;
                    bool data = false;
                    while (entity.Next())
                    {
                        if (entity.Field == 1 && entity.Wire == 2) status = (int)entity.Message().Varint(1, 200);
                        else if (entity.Field == 2 && entity.Wire == 2) uri = entity.Bytes();
                        else if (entity.Field == 3 && entity.Wire == 2) { entity.Skip(); data = true; }
                        else entity.Skip();
                    }
                    if (data && status is >= 200 and < 300 && !uri.IsEmpty) (hits ??= []).Add(Encoding.UTF8.GetString(uri));
                }
                if (arrayKind == kind && hits is not null)
                    foreach (string hit in hits) answered.Add(hit);
            }
            return answered;
        }

        // ── 5. the fetch provider (the planner's transport, G-040/G-041/G-042) ───────────────────────────────────────

        /// <summary>Can this build DECODE what a route answers? A route with no fold behind it is never SENT — a round
        /// trip whose answer lands nowhere is a request loop with extra steps — and its groups seal instead. Pure; the
        /// list moves only when a fold lands (the gaps are reported beside each exclusion):
        /// <c>fetchPlaylist</c> has no persisted hash; the content-filter and friend-feed answers have no staged column.</summary>
        public static bool Serves(in FetchRoute route) => route.Transport switch
        {
            RouteTransport.Metadata => route.Extension > 0,
            RouteTransport.Pathfinder => route.Op is PathfinderOp.EpisodeDetail or PathfinderOp.GetAlbum or PathfinderOp.GetTrack
                or PathfinderOp.ArtistOverview or PathfinderOp.Discography or PathfinderOp.Home or PathfinderOp.HomeSection
                or PathfinderOp.BrowseAll or PathfinderOp.BrowsePage or PathfinderOp.BrowseSection or PathfinderOp.Search or PathfinderOp.SearchGenres or PathfinderOp.SearchSuggestions
                or PathfinderOp.AlbumMerch or PathfinderOp.SimilarAlbums
                or PathfinderOp.DiscographyAlbums or PathfinderOp.DiscographySingles or PathfinderOp.DiscographyCompilations
                or PathfinderOp.Concert or PathfinderOp.ArtistConcerts,
            RouteTransport.Spclient => route.Rest is SpclientRoute.ShowRead or SpclientRoute.PlaylistRead or SpclientRoute.LikedContentFilters or SpclientRoute.PermissionBase
                or SpclientRoute.Popcount or SpclientRoute.ArtistTopTracksExtended or SpclientRoute.Rootlist
                or SpclientRoute.CollectionPage or SpclientRoute.Recents,
            _ => false,
        };

        /// <summary>THE ROUTE WALK the provider sends (G-040's <c>Api.RoutesFor</c>): <see cref="FetchRoutes.For(FetchSubject,EntityKind,uint,Span{FetchRoute},out uint)"/>'s
        /// rule — a route is taken while the need still has one of its primary groups unserved — over the table MINUS
        /// the routes this build does not <see cref="Serves"/>. Skipping those BEFORE they count as served is the point:
        /// a playlist asking <c>Identity | Daylist</c> must still get its kind-205 POST for Identity when the
        /// <c>fetchPlaylist</c> route that would have carried both cannot be sent. <paramref name="sealedGroups"/> is
        /// what nothing sent will fill.</summary>
        public static int RoutesFor(FetchSubject subject, EntityKind kind, uint need, Span<FetchRoute> into, out uint sealedGroups)
        {
            ReadOnlySpan<FetchRoute> routes = FetchRoutes.Of(subject, kind);
            uint served = 0;
            int n = 0;
            for (int i = 0; i < routes.Length && n < into.Length; i++)
            {
                ref readonly FetchRoute route = ref routes[i];
                if ((need & route.Primary & ~served) == 0 || !Serves(in route)) continue;
                into[n++] = route;
                served |= route.Groups;
            }
            sealedGroups = need & ~served;
            return n;
        }

        /// <summary>The extension kinds of the batch's ONE mixed-kind POST: the metadata routes' kinds, deduped, in table
        /// order — or the batch's own DERIVED kind alone (<see cref="FetchBatch.Extension"/>, kind 5 on the
        /// <c>spotify:audio:</c> entity), which never rides beside another.</summary>
        public static int MetadataKinds(ReadOnlySpan<FetchRoute> routes, int derivedExtension, Span<int> into)
        {
            if (into.IsEmpty) return 0;
            if (derivedExtension > 0) { into[0] = derivedExtension; return 1; }
            int n = 0;
            for (int i = 0; i < routes.Length && n < into.Length; i++)
            {
                if (routes[i].Transport != RouteTransport.Metadata || routes[i].Extension <= 0) continue;
                if (into[..n].IndexOf(routes[i].Extension) >= 0) continue;
                into[n++] = routes[i].Extension;
            }
            return n;
        }

        /// <summary>Can this build answer a relation at this page? Its route must be one the build <see cref="Serves"/>,
        /// AND the fold behind it must land that relation at that offset: <c>getAlbum</c> lands its tracklist at offset 0
        /// whatever it was asked, the playlist v2 read has no page parameter here, the search fold rewrites the whole
        /// list, and <c>getAlbum</c> carries no more-by run (both reported). Pins (G-062, B2b) route through the same
        /// collection-v2 paging as Liked/SavedAlbums/etc and land through <c>Decode.LibrarySet</c>'s pins arm, so they
        /// answer like any other collection edge. A relation answered "no" is answered WITHOUT a request, which the door
        /// records as a vacancy.</summary>
        public static bool ServesEdge(FetchEdge edge, int offset) => edge switch
        {
            FetchEdge.None => false,
            // getAlbum lands a later tracklist page at its own offset; at offset 0 it is ALWAYS the "more by" prefetch
            // (AlbumV4 owns the first tracklist page — FetchEdge.AlbumTracks routes there at offset 0, never here),
            // so its answer must decode with landTracks: false (Decode.AlbumAnswer) rather than fold tracksV2 again
            FetchEdge.PlaylistTracks
                => offset <= 0 && Serves(FetchRoutes.ForEdge(edge, 0)),
            _ => Serves(FetchRoutes.ForEdge(edge, offset)),
        };

        /// <summary>What a batch's requests came back with, folded into the ONE verdict the planner takes. PURE.
        ///
        /// <list type="bullet">
        /// <item>2xx, 304 and 404 are ANSWERS (404 is "nothing there", which seals rather than re-asks).</item>
        /// <item>A retryable failure (0, 429, 5xx) FAILS the batch while another attempt remains, so the backoff re-runs
        /// it with its marks kept. On the LAST attempt whatever did answer is committed — a secondary endpoint that keeps
        /// failing must not cost the rows the primary one already delivered.</item>
        /// <item>A terminal failure (any other 4xx) fails the batch only when nothing answered: the terminal path
        /// un-asks, and the next mount really retries.</item>
        /// <item>A request that was never sent is neither: a batch with nothing sendable is answered empty (sealed).</item>
        /// <item>A route that did NOT answer beside one that did leaves its groups <see cref="Unfilled"/>: the batch is
        /// delivered as an answer (what landed is committed), and the planner un-asks exactly those groups so the next
        /// mount — or a Retry — really asks again. Without it a 401 on the top-tracks REST beside a 200 overview sealed
        /// <c>ArtistFields.Chart</c> for the scope and the chart shimmered forever.</item>
        /// </list></summary>
        public struct FetchOutcome
        {
            int _answered, _retryStatus, _retryAfter, _terminalStatus;
            uint _unfilled;
            bool _retryable, _terminal, _faulted;

            /// <summary>How many requests came back with an answer.</summary>
            public readonly int Answered => _answered;

            /// <summary>The field groups of every route that did not answer (a failure of any kind — never a 404, which
            /// IS an answer). What <see cref="Fetch.Answer"/> takes as its <c>unfilled</c>: the groups it may un-ask when
            /// the batch is delivered as an answer regardless. A route noted without groups adds nothing here.</summary>
            public readonly uint Unfilled => _unfilled;

            public void Note(int status, int retryAfterSeconds = 0) => Note(status, retryAfterSeconds, 0);

            /// <summary>Note one request's status; <paramref name="groups"/> are the field groups its route fills, so a
            /// non-answer can report them <see cref="Unfilled"/>.</summary>
            public void Note(int status, int retryAfterSeconds, uint groups)
            {
                if (status is >= 200 and < 300 or 304 or 404) { _answered++; return; }
                _unfilled |= groups;
                if (Fetch.Retryable(status))
                {
                    if (_retryable) return;
                    _retryable = true;
                    _retryStatus = status;
                    _retryAfter = retryAfterSeconds;
                    return;
                }
                if (_terminal) return;
                _terminal = true;
                _terminalStatus = status;
            }

            public void Note(in Result result) => Note(result.Status, result.RetryAfterSeconds, 0);

            /// <inheritdoc cref="Note(int,int,uint)"/>
            public void Note(in Result result, uint groups) => Note(result.Status, result.RetryAfterSeconds, groups);

            /// <summary>A decoder threw: the staging is not trustworthy and the batch fails as a transport error would.</summary>
            public void Fault() => _faulted = true;

            /// <summary>Does batch attempt <paramref name="attempt"/> (0-based) fail? The status and retry-after are what
            /// <see cref="Fetch.Failed"/> takes.</summary>
            public readonly bool Fails(int attempt, out int status, out int retryAfterSeconds)
            {
                status = 0;
                retryAfterSeconds = 0;
                if (_faulted) return true;
                bool last = attempt + 1 >= Fetch.MaxAttempts;
                if (_retryable && (!last || _answered == 0))
                {
                    status = _retryStatus;
                    retryAfterSeconds = _retryAfter;
                    return true;
                }
                if (_terminal && _answered == 0)
                {
                    status = _terminalStatus;
                    return true;
                }
                return false;
            }
        }

        /// <summary>`Entities.Ensure` / `EnsureEdge` → `Fetch.Plan` → here. `Start` runs on the UI thread and only
        /// queues; the batch is answered on an api thread and posted back (C10).</summary>
        sealed class SpotifyFetchProvider : FetchProvider
        {
            public override EntityProvider Provider => EntityProvider.Spotify;

            public override void Start(FetchBatch batch)
            {
                // The planner only ever starts a batch of ITS current epoch, so a start is also the news of which epoch
                // is live — the one a re-Boot (which resets epochs without an Abandon) would otherwise leave stale.
                Volatile.Write(ref s_liveEpoch, batch.Epoch);
                if (!Run(() => Execute(batch))) Fetch.Failed(batch.Ticket, 0, 0);
            }

            public override void Abandon(uint epoch) => Volatile.Write(ref s_liveEpoch, epoch);
        }

        /// <summary>The epoch of the scope the planner is on (the last <c>Abandon</c> or started batch);
        /// <see cref="uint.MaxValue"/> before either. A batch of any other epoch is walking for a table set nobody
        /// holds, and stops sending.</summary>
        static uint s_liveEpoch = uint.MaxValue;

        static bool Stale(FetchBatch batch)
        {
            uint live = Volatile.Read(ref s_liveEpoch);
            return live != uint.MaxValue && live != batch.Epoch;
        }

        /// <summary>Answer one batch, exactly once. The batch is the planner's pooled object: everything it holds is
        /// read BEFORE the post, and nothing after it.</summary>
        static void Execute(FetchBatch batch)
        {
            uint ticket = batch.Ticket;
            int attempt = batch.Attempt;
            Staging staging = Staging.Rent();
            staging.Epoch = batch.Epoch;
            var outcome = new FetchOutcome();
            try
            {
                if (batch.Subject == FetchSubject.Edge) AnswerEdge(batch, staging, ref outcome);
                else AnswerRows(batch, staging, ref outcome);
            }
            catch (Exception ex)
            {
                // A decoder that throws must not strand the batch: the planner would keep the in-flight marks for the
                // life of the scope and those rows would never be asked for again.
                Log.Error("spotify", "fetch provider faulted (" + batch.Subject + " " + batch.Kind + " " + batch.Edge + ")", ex);
                outcome.Fault();
            }

            if (Stale(batch))
            {
                Staging.Return(staging);
                Spotify.Post(() => Fetch.Answer(ticket, null));
                return;
            }
            if (outcome.Fails(attempt, out int status, out int retryAfter))
            {
                Staging.Return(staging);
                Spotify.Post(() => Fetch.Failed(ticket, status, retryAfter));
                return;
            }
            // An answer beside a route that did not answer: the planner un-asks the groups that route would have filled.
            uint unfilled = outcome.Unfilled;
            Spotify.Post(() => Fetch.Answer(ticket, staging, unfilled));
        }

        /// <summary>A row batch: the one mixed-kind POST, then every other route per subject.</summary>
        static void AnswerRows(FetchBatch batch, Staging s, ref FetchOutcome outcome)
        {
            Span<FetchRoute> routes = stackalloc FetchRoute[FetchRoutes.MaxRoutes];
            int n = RoutesFor(batch.Subject, batch.Kind, batch.Wanted, routes, out _);
            Span<int> kinds = stackalloc int[FetchRoutes.MaxRoutes];
            int k = MetadataKinds(routes[..n], batch.Extension, kinds);
            // The one POST answers for every metadata route at once, so its groups are their union: a refused POST
            // leaves all of them unfilled, an answered one leaves none.
            uint metadataGroups = 0;
            for (int r = 0; r < n; r++)
                if (routes[r].Transport == RouteTransport.Metadata) metadataGroups |= routes[r].Groups;
            if (k > 0) MetadataFor(batch, kinds[..k], s, ref outcome, metadataGroups);

            for (int r = 0; r < n; r++)
            {
                FetchRoute route = routes[r];
                if (route.Transport == RouteTransport.Metadata) continue;
                for (int i = 0; i < batch.Count; i++)
                {
                    if (Stale(batch)) return;
                    string uri = batch.Uri(i);
                    if (uri.Length == 0) continue;
                    if (route.Transport == RouteTransport.Pathfinder) AnswerQuery(route.Op, uri, 0, s, ref outcome, route.Groups);
                    else AnswerRest(route.Rest, uri, s, ref outcome, route.Groups);
                }
            }
        }

        /// <summary>The batch's uris, all of <paramref name="kinds"/> each, in ONE POST (P4), decoded with the batch in
        /// hand (kind 5's answer is keyed by the derived uri only the batch can map back). For a user batch the REST arm
        /// then resolves whoever kind 15 left unanswered (G-033). <paramref name="groups"/> are what the POST's routes
        /// fill, reported <see cref="FetchOutcome.Unfilled"/> when it does not answer.</summary>
        static void MetadataFor(FetchBatch batch, ReadOnlySpan<int> kinds, Staging s, ref FetchOutcome outcome, uint groups = 0)
        {
            var uris = new string[batch.Count];
            for (int i = 0; i < uris.Length; i++) uris[i] = batch.Uri(i);
            byte[] body = BatchBody(uris, kinds, Market, Catalogue);
            if (body.Length == 0) return;

            Result result = MetadataPost(body, CancellationToken.None);
            outcome.Note(in result, groups);
            if (!result.Ok || result.Body.Length == 0) return;
            Decode.ExtendedMetadata(result.Bytes, s, batch);

            if (batch.Subject == FetchSubject.Entity && batch.Kind == EntityKind.User && kinds.IndexOf(FetchRoutes.UserProfile) >= 0)
                ProfileFallback(uris, result.Body, s);
        }

        /// <summary>The REST arm of profile resolution, one user at a time, for the users kind 15 did not answer. Only
        /// 200 and 404 are answers, and neither decides the batch: kind 15 already did, and a REST outage must not re-POST
        /// the batch that succeeded — an unresolved user simply stays sealed for the scope.</summary>
        static void ProfileFallback(string[] uris, byte[] response, Staging s)
        {
            HashSet<string>? answered = null;
            foreach (string uri in uris)
            {
                if (!uri.StartsWith(UserPrefix, StringComparison.Ordinal)) continue;
                answered ??= AnsweredUris(response, FetchRoutes.UserProfile);
                if (answered.Contains(uri)) continue;
                Result rest = Profile(UsernameOf(uri), CancellationToken.None);
                if (rest.Status == 200 && rest.Body.Length > 0) Decode.Profile(rest.Bytes, Encoding.UTF8.GetBytes(uri), s);
            }
        }

        /// <summary>One pathfinder operation for one subject at one page, decoded into <paramref name="s"/>. The answers
        /// that carry their own <c>data.*</c> root go through <see cref="Decode.Export"/> — the dispatch the offline
        /// fixture path also uses, and the fix for the folds that were handed a root-positioned reader (B1's report).</summary>
        static void AnswerQuery(PathfinderOp op, string uri, int offset, Staging s, ref FetchOutcome outcome, uint groups = 0)
        {
            CancellationToken ct = CancellationToken.None;
            offset = Math.Max(0, offset);
            Result result;
            switch (op)
            {
                case PathfinderOp.EpisodeDetail:
                    result = PodcastQueries.EpisodeQuery(uri, ct);
                    if (result.Ok) Decode.EpisodeDetail(result.Body, uri, s);
                    break;
                case PathfinderOp.GetAlbum:
                    // NEVER lands the offset-0 tracklist. `FetchRoutes.ForEdge` sends AlbumTracks at offset <= 0 to
                    // Metadata(AlbumV4), so getAlbum reaching here at offset 0 can only be the AlbumMoreBy prefetch —
                    // and its ClosePage(AlbumTracks) used to REWRITE the tracklist V4 had already landed (one real row
                    // plus stale rows, because ReplacePage never shrank). Identity, billed artists, more-by and other
                    // versions from this same answer are still wanted, so only the tracks run is suppressed (it is
                    // popped, not skipped — the edges are already on the pending stack). A later page (offset > 0) is
                    // the real AlbumTracks paging route and takes the AlbumTracksPage arm, which ignores the flag.
                    result = AlbumQuery(uri, offset, AlbumPageSize, ct);
                    if (result.Ok) Decode.AlbumAnswer(result.Bytes, offset, s, landTracks: false);
                    break;
                case PathfinderOp.AlbumMerch:
                    result = AlbumMerch(uri, ct);
                    if (result.Ok) Decode.AlbumMerch(result.Bytes, Encoding.UTF8.GetBytes(uri), s);
                    break;
                case PathfinderOp.GetTrack:
                    result = TrackQuery(uri, ct);
                    if (result.Ok) Decode.Export(result.Bytes, s);
                    break;
                case PathfinderOp.ArtistOverview:
                    // The WHOLE overview (facets, extras, pick, pre-release, latest), not Export's identity-and-runs fold.
                    result = ArtistPageAnswer(uri, s);
                    break;
                case PathfinderOp.Discography:
                    result = Discography(uri, offset, DiscographyPageSize, ct);
                    if (result.Ok) Decode.DiscographyAll(result.Bytes, Encoding.UTF8.GetBytes(uri), offset, s);
                    break;
                case PathfinderOp.Home:
                    if (HomeFacetOf(uri) is not { } homeFacet) return;
                    result = HomeQuery(homeFacet, LocalTimeZone, ct);
                    if (result.Ok) Decode.HomeFeed(result.Bytes, Encoding.UTF8.GetBytes(uri), s);
                    break;
                case PathfinderOp.HomeSection:
                    result = HomeSection(uri, LocalTimeZone, offset, ct);
                    if (result.Ok) Decode.HomeSection(result.Bytes, offset, s);
                    break;
                case PathfinderOp.BrowseAll:
                    result = BrowseAll(ct);
                    if (result.Ok) Decode.BrowseAll(result.Bytes, s);
                    break;
                case PathfinderOp.BrowsePage:
                    result = BrowsePage(uri, offset, ct);
                    if (result.Ok) Decode.BrowsePage(result.Bytes, Encoding.UTF8.GetBytes(uri), offset, s);
                    break;
                case PathfinderOp.BrowseSection:
                    result = BrowseSection(uri, offset, ct);
                    if (result.Ok) Decode.BrowseSection(result.Bytes, offset, s);
                    break;
                case PathfinderOp.Search:
                    if (!TryParseSearchSubject(uri, out SearchFacet facet, out string term) || term.Length == 0) return;
                    result = Search(facet, term, offset, SearchPageSize(facet), ct);
                    if (result.Ok) Decode.SearchPage(result.Bytes, Encoding.UTF8.GetBytes(uri), offset, s);
                    break;
                case PathfinderOp.SearchGenres:
                    if (!TryParseSearchSubject(uri, out _, out string genreTerm) || genreTerm.Length == 0) return;
                    result = Search(SearchFacet.Genres, genreTerm, 0, SearchPageSize(SearchFacet.Genres), ct);
                    if (result.Ok) Decode.SearchGenres(result.Bytes, Encoding.UTF8.GetBytes(uri), s);
                    break;
                case PathfinderOp.SearchSuggestions:
                    if (!TryParseSearchSubject(uri, out _, out string relatedTerm) || relatedTerm.Length == 0) return;
                    result = Search(SearchFacet.Suggestions, relatedTerm, 0, 30, ct);
                    if (result.Ok) Decode.SearchRelated(result.Bytes, Encoding.UTF8.GetBytes(uri), s);
                    break;
                case PathfinderOp.DiscographyAlbums:
                    result = DiscographyFacetAnswer(uri, DiscoFacet.Albums, offset, s);
                    break;
                case PathfinderOp.DiscographySingles:
                    result = DiscographyFacetAnswer(uri, DiscoFacet.Singles, offset, s);
                    break;
                case PathfinderOp.DiscographyCompilations:
                    result = DiscographyFacetAnswer(uri, DiscoFacet.Compilations, offset, s);
                    break;
                case PathfinderOp.Concert:
                    result = ConcertAnswer(uri, s);
                    break;
                case PathfinderOp.ArtistConcerts:
                    result = ArtistConcertsAnswer(uri, offset, s);
                    break;
                default:
                    return;                                           // not served: nothing was sent, nothing to note
            }
            outcome.Note(in result, groups);
        }

        /// <summary>One spclient route for one subject, decoded into <paramref name="s"/>.</summary>
        static void AnswerRest(SpclientRoute rest, string uri, Staging s, ref FetchOutcome outcome, uint groups = 0)
        {
            CancellationToken ct = CancellationToken.None;
            Result result;
            switch (rest)
            {
                case SpclientRoute.PlaylistRead:
                    result = Playlist(IdOf(uri), ct);
                    if (result.Ok && result.Body.Length > 0) PlaylistAnswer(result.Bytes, uri, s);
                    break;
                case SpclientRoute.PermissionBase:
                    result = PlaylistPermissionBase(IdOf(uri), ct);
                    if (result.Ok) Decode.PermissionBase(result.Bytes, Encoding.UTF8.GetBytes(uri), s);
                    break;
                case SpclientRoute.Popcount:
                    result = Popcount(IdOf(uri), ct);
                    if (result.Ok) Decode.Popcount(result.Bytes, Encoding.UTF8.GetBytes(uri), s);
                    break;
                case SpclientRoute.ArtistTopTracksExtended:
                    result = ArtistTopTracksExtended(uri, ct);
                    if (result.Ok) Decode.ArtistTopTracks(result.Bytes, Encoding.UTF8.GetBytes(uri), s);
                    break;
                case SpclientRoute.LikedContentFilters:
                    ContentFiltersAnswer(uri, s, ref outcome);
                    return;
                default:
                    return;
            }
            outcome.Note(in result, groups);
        }

        const string UserPrefix = "spotify:user:";

        /// <summary>The username a <c>spotify:user:</c> uri names (the path segment the rootlist, the collection and the
        /// profile routes take), unescaped when the uri carried an escape. Anything else is returned as it is.</summary>
        public static string UsernameOf(string userUri)
        {
            if (!userUri.StartsWith(UserPrefix, StringComparison.Ordinal)) return userUri;
            ReadOnlySpan<char> tail = userUri.AsSpan(UserPrefix.Length);
            int colon = tail.IndexOf(':');
            string name = new(colon < 0 ? tail : tail[..colon]);
            return name.Contains('%') ? Uri.UnescapeDataString(name) : name;
        }

        /// <summary>The trailing id of a uri (<c>spotify:playlist:x</c> → <c>x</c>), as the string a path segment needs.</summary>
        static string IdOf(string uri) => new(EntityUri.IdOf(uri.AsSpan()));

        // ── 6. pathfinder: the persisted-query table ─────────────────────────────────────────────────────────────────
        //
        // Every pathfinder call is POST /pathfinder/v2/query with the SAME body shape and a different (operationName,
        // sha256Hash, variables) triple. The hashes ARE the protocol — a stale one is a 400 — so they live in one
        // named table rather than inline at thirty call sites where a typo hides.

        /// <summary>One persisted query. <see cref="Web"/> selects the web-player identity tuple (the operations
        /// Spotify itself serves from the web bundle); everything else is the desktop client.</summary>
        public readonly record struct Query(string Op, string Hash, bool Web);

        /// <summary>The persisted-query table. Verified 2026-09-19 against the official 1.2.96.518 client's own table (152
        /// declarations extracted from its xpui bundle; every hash its captured traffic sent matched it byte-for-byte). Rotated
        /// then vs the 1.2.94 capture: home + homeSection (one document), searchPlaylists, searchUsers, queryNpvArtist,
        /// getTrack — the old documents still answered 200 that day but are no longer what the client sends. Audit:
        /// wavee-captures/fresh-client-2026-09-19/findings-pathfinder.md (outside every repo).</summary>
        public static class Queries
        {
            public static readonly Query ArtistOverview = new("queryArtistOverview",
                "ae0e2958a4ab645b35ca19ac04d0495ae12d9c5d7b7286217674801a9aab281a", true);
            public static readonly Query Album = new("getAlbum",
                "b9bfabef66ed756e5e13f68a942deb60bd4125ec1f1be8cc42769dc0259b4b10", true);
            public static readonly Query Track = new("getTrack",
                "1a2f0cce77c90a4a5b1730beecc4da7e34290d684324c16663bf09a268ebce48", true);
            public static readonly Query Home = new("home",
                "76243c78b0e20ecdbe41b794dec8cbe73f75e585b0a7201b8d2e84578412847a", false);
            /// <summary>The section query rides the SAME persisted hash as <see cref="Home"/> — the gateway keys on
            /// the operation NAME, and the two share a document.</summary>
            public static readonly Query HomeSection = new("homeSection",
                "76243c78b0e20ecdbe41b794dec8cbe73f75e585b0a7201b8d2e84578412847a", false);
            public static readonly Query BrowseAll = new("browseAll",
                "dbd8b55e09a58afc52eab438bc228ba28fd72ac2f2148c6c26354980e4579001", false);
            public static readonly Query BrowsePage = new("browsePage",
                "f5c4e6d668f5716464a231c1cc8b22c1cbf6ad68b09929fd7de813a30581298b", false);
            public static readonly Query BrowseSection = new("browseSection",
                "b13c1cccbfcb6947753c2613411b3566485c21fd5f36d80a80bb64be61ba2d51", false);
            public static readonly Query NpvArtist = new("queryNpvArtist",
                "4ac064f555c9f57803a4e8c2e200cc9e2d3e942a3afdaca8884883a1051f695e", false);
            public static readonly Query AlbumMerch = new("queryAlbumMerch",
                "3ef44ed6f17be67299538fe77faffab4075aeaf9e1085f10fc835592266711b5", false);
            public static readonly Query SimilarAlbums = new("similarAlbumsBasedOnThisTrack",
                "1d1f93a737498adca2c892c73af87fc0b052afe4e1a33c989540c32413dfae17", false);
            public static readonly Query WhatsNew = new("queryWhatsNewFeed",
                "d889c8c936ab192af8ced595427f5ba2acdf63478fdc0a181c8d477f8322630e", true);
            public static readonly Query UserTop = new("userTopContent",
                "49ee15704de4a7fdeac65a02db20604aa11e46f02e809c55d9a89f6db9754356", true);
            /// <summary>The paged discography. One hash hosts four operation names (…All/Albums/Singles/Compilations),
            /// so the NAME is what selects the facet; the DESKTOP identity, because it is not a web-player surface
            /// (docs/plans/wavee/discography-pagination-fix-proposal.md).</summary>
            public static readonly Query Discography = new("queryArtistDiscographyAll",
                "5e07d323febb57b4a56a42abbf781490e58764aa45feb6e3dc0591564fc56599", false);
            /// <summary>The cover grader (G-055): <c>spotify:image:</c> uris in, both themes' role sets out. The desktop
            /// identity, as 0.2.9's <c>CoverColorFiller</c> sent it.</summary>
            public static readonly Query DynamicColors = new("getDynamicColorsByUris",
                "f0f112945d6d745bd8ff790317bbf8d310036da75df33130490e9d6dc96c59d9", false);

            public static readonly Query SearchTopResults = new("searchTopResultsList",
                "337d8b1b4f911fb12c60996623391703c2807550baccb51d95f5eabc8c8bdacd", true);
            public static readonly Query SearchTracks = new("searchTracks",
                "59ee4a659c32e9ad894a71308207594a65ba67bb6b632b183abe97303a51fa55", true);
            public static readonly Query SearchAlbums = new("searchAlbums",
                "64ae1fe6df380b038c0a65a2606d3361bc270de6870b2fdc99cf0848b1efa6d3", true);
            public static readonly Query SearchArtists = new("searchArtists",
                "270905851ba5c7faca81cfe053c2dbd8ceb4f156a0e0ef4b385af75ab69ffd13", true);
            public static readonly Query SearchPlaylists = new("searchPlaylists",
                "d520014e748f9ea44f7707d8df1819867ac1205e8b7f3e28f22fe5fc858921b1", true);
            public static readonly Query SearchPodcasts = new("searchPodcasts",
                "0195d9f61b43606d490bca64c3456e3593528cea6cc05c7e822c7c42beed0f4e", true);
            public static readonly Query SearchUsers = new("searchUsers",
                "8f358dd82e62f61dd4ceaa9f8cd0889e644c9b707f1b724fbfb356a757cb7e5a", true);
            public static readonly Query SearchAuthors = new("searchAuthors",
                "4a9d403a7cbc7e19da5520d619a865472b35382b043bfa458154e73a5c6f46bd", true);
            public static readonly Query SearchAudiobooks = new("searchAudiobooks",
                "e05ac765d02c084f8783d3c1572b23d57761c43f47eb8b87ce2f9ccced3fa068", true);
            public static readonly Query SearchEpisodes = new("searchFullEpisodes",
                "d54e35fafe7520cb53883b86d012911cbad75c14ac079a917951c24cdb07c60f", true);
            public static readonly Query SearchGenres = new("searchGenres",
                "9e1c0e056c46239dd1956ea915b988913c87c04ce3dadccdb537774490266f46", true);
            public static readonly Query SearchSuggestions = new("searchSuggestions",
                "23f33ca50a0f4153dafc5cd1b4d1370db01b72130c2994bd0ffd07d5a7fee8f0", true);

            public static readonly Query ArtistConcerts = new("ArtistConcerts",
                "ef53c43b865496b9890b7167eab1dc614a8949ef9451b3c41184ea888de8bd2b", true);
            public static readonly Query ArtistConcertsPageLocation = new("ArtistConcertsPageLocation",
                "320698465a352f0d0247ec8ed02471244106d4199820f99de4d0a785561c2b03", true);
            public static readonly Query UserLocation = new("userLocation",
                "079939378ca79b67c6d047be9152ea940d21f10bbfa2f5d4cf4d8320d87774c2", true);
            public static readonly Query InferredUserLocation = new("inferredUserLocation",
                "5db4c507ea735d2a1f37bd1166eca2c1a0e3387bb875ebca5d6031b6eccceeba", true);
            public static readonly Query ConcertConcepts = new("concertConcepts",
                "a409c1eb39b6345e7993d424d2408b65a6699bafc2b8a03217033e517cd76b72", true);
            public static readonly Query ConcertFeed = new("concertFeed",
                "9cae2dbee3f47904c60bab45256260b3ddb9844d5ef25038c17112619d14ce9a", true);
            public static readonly Query ConcertCount = new("concertCount",
                "29be9d486e073a49268e13ed9e2d2180187e669fcb7a19b98011aca7ab61b141", true);
            public static readonly Query ConcertLocationDetails = new("concertLocationDetails",
                "b13f195349f188fee25480ae889d782852d68663bf07743c654244454750d681", true);
            public static readonly Query SearchConcertLocations = new("searchConcertLocations",
                "43ededefcba8b3f519fd0c2d6c025dfeec9f742cf47d04a3c3711d95b27deda3", true);
            public static readonly Query ConcertLocationsByLatLon = new("concertLocationsByLatLon",
                "8a059d072a17a1199feb21fe846271f1680eda87010c832852ced0c55c6c7c96", true);
            public static readonly Query SaveLocation = new("saveLocation",
                "5502351e9f201ae29014ca55d3b24b755ba261a1a9eb35fb498cb4c7df419353", true);
            public static readonly Query Concert = new("concert",
                "21afefc1c7f9e38cbf7c60d03f5c8b6e602b7a91e04f2c2e0aa7d1743052768e", true);
        }

        /// <summary>Writes a pathfinder body without a DTO and without a DOM: open it, write the variables, close it.
        /// The property ORDER is the captured one (<c>variables</c>, then <c>operationName</c>, then
        /// <c>extensions</c>) — the gateway does not care, but a diff against a capture does.</summary>
        public ref struct Vars
        {
            readonly ArrayBufferWriter<byte> _buffer;
            readonly string _op;
            readonly string _hash;
            /// <summary>The open writer, positioned inside <c>variables</c>. Write properties; write nothing else.</summary>
            public readonly Utf8JsonWriter W;

            public Vars(in Query query)
            {
                _buffer = new ArrayBufferWriter<byte>(512);
                _op = query.Op;
                _hash = query.Hash;
                W = new Utf8JsonWriter(_buffer);
                W.WriteStartObject();
                W.WriteStartObject("variables");
            }

            /// <summary>Close the body and hand back the bytes.</summary>
            public readonly byte[] Finish()
            {
                W.WriteEndObject();                                   // variables
                W.WriteString("operationName", _op);
                W.WriteStartObject("extensions");
                W.WriteStartObject("persistedQuery");
                W.WriteNumber("version", 1);
                W.WriteString("sha256Hash", _hash);
                W.WriteEndObject();
                W.WriteEndObject();
                W.WriteEndObject();                                   // root
                W.Flush();
                byte[] bytes = _buffer.WrittenSpan.ToArray();
                W.Dispose();
                return bytes;
            }
        }

        /// <summary>POST a pathfinder body. A 400 means the persisted hash has gone stale — logged BY NAME, because
        /// that is the one failure whose fix is an edit to the table above.</summary>
        public static Result Pathfinder(in Query query, byte[] body, CancellationToken ct)
        {
            var args = new RequestArgs { Body = body, Flag = query.Web };
            Result result = Send(RequestKind.Pathfinder, args, ct);
            if (result.Status == 400)
                Log.Warn("spotify", "pathfinder " + query.Op + " rejected (400) — the persisted hash is probably stale");
            return result;
        }

        // ── 7. the pathfinder requests, one function each ────────────────────────────────────────────────────────────
        //
        // The `…(…, Staging into, ct)` forms decode through `Decode.Export`, which dispatches on the answer's own
        // `data.*` root; the folds underneath expect a reader positioned INSIDE that root, and handing them the
        // document root decoded nothing at all (B1's report).

        /// <summary>The album page's track window per request.</summary>
        public const int AlbumPageSize = 50;

        /// <summary>The discography page the client sends (captured: 20, <c>DATE_DESC</c>).</summary>
        public const int DiscographyPageSize = 20;

        public static Result AlbumQuery(string albumUri, int offset, int limit, CancellationToken ct)
        {
            var vars = new Vars(Queries.Album);
            vars.W.WriteString("uri", albumUri);
            vars.W.WriteString("locale", "");
            vars.W.WriteNumber("offset", offset);
            vars.W.WriteNumber("limit", limit);
            return Pathfinder(Queries.Album, vars.Finish(), ct);
        }

        /// <summary>The album page's first read: identity, other versions, the detailed artists and the first track
        /// page, decoded straight into <paramref name="into"/>.</summary>
        public static int Album(string albumUri, Staging into, CancellationToken ct)
        {
            Result result = AlbumQuery(albumUri, 0, AlbumPageSize, ct);
            if (result.Ok) Decode.Export(result.Bytes, into);
            return result.Status;
        }

        public static Result TrackQuery(string trackUri, CancellationToken ct)
        {
            var vars = new Vars(Queries.Track);
            vars.W.WriteString("uri", trackUri);
            return Pathfinder(Queries.Track, vars.Finish(), ct);
        }

        public static int Track(string trackUri, Staging into, CancellationToken ct)
        {
            Result result = TrackQuery(trackUri, ct);
            if (result.Ok) Decode.Export(result.Bytes, into);
            return result.Status;
        }

        public static Result ArtistOverviewQuery(string artistUri, CancellationToken ct)
        {
            var vars = new Vars(Queries.ArtistOverview);
            vars.W.WriteString("uri", artistUri);
            vars.W.WriteString("locale", "");
            vars.W.WriteBoolean("preReleaseV2", true);
            return Pathfinder(Queries.ArtistOverview, vars.Finish(), ct);
        }

        public static int ArtistOverview(string artistUri, Staging into, CancellationToken ct)
        {
            Result result = ArtistOverviewQuery(artistUri, ct);
            if (result.Ok) Decode.Export(result.Bytes, into);
            return result.Status;
        }

        public static Result HomeQuery(string facet, string timeZone, CancellationToken ct)
        {
            var vars = new Vars(Queries.Home);
            vars.W.WriteString("homeEndUserIntegration", "INTEGRATION_DESKTOP");
            vars.W.WriteString("timeZone", timeZone);
            vars.W.WriteString("sp_t", "");
            vars.W.WriteString("facet", facet);
            vars.W.WriteNumber("sectionItemsLimit", 10);
            vars.W.WriteBoolean("includeEpisodeContentRatingsV2", true);
            return Pathfinder(Queries.Home, vars.Finish(), ct);
        }

        public static int Home(string facet, string timeZone, Staging into, CancellationToken ct)
        {
            Result result = HomeQuery(facet, timeZone, ct);
            if (result.Ok) Decode.HomeFeed(result.Bytes, Encoding.UTF8.GetBytes(facet.Length == 0 ? Wavee.Home.FeedUri : Wavee.Home.FacetPrefix + facet), into);
            return result.Status;
        }

        /// <summary>The facet a Home subject names: <c>""</c> for the feed itself (<c>wavee:home</c>), the tail of
        /// <c>wavee:home:&lt;facet&gt;</c>, or null for a uri that is not a Home subject. PURE.</summary>
        public static string? HomeFacetOf(string uri)
        {
            if (uri == Wavee.Home.FeedUri) return "";
            return uri.StartsWith(Wavee.Home.FacetPrefix, StringComparison.Ordinal) ? uri[Wavee.Home.FacetPrefix.Length..] : null;
        }

        /// <summary>What <c>timeZone</c> says when the local zone has no IANA spelling (this app runs globalization-
        /// invariant, where the Windows→IANA table is not available): a real zone, so the server still answers.</summary>
        public const string FallbackTimeZone = "Etc/UTC";

        /// <summary>The zone the Home feed buckets its greeting and time-of-day shelves by, as an IANA id (0.2.9
        /// <c>SpotifyTimeZone.LocalIana</c>).</summary>
        public static string LocalTimeZone
        {
            get
            {
                string id;
                try { id = TimeZoneInfo.Local.Id; }
                catch (Exception) { id = ""; }                     // a corrupt registry zone throws rather than answering
                return IanaZone(id);
            }
        }

        /// <summary>A local zone id → the IANA id the gateway wants: an id that already has an area ("Europe/Amsterdam")
        /// is itself, a Windows id converts when the runtime can, and anything else is <see cref="FallbackTimeZone"/>.
        /// A Windows id sent verbatim would be silently wrong, which is why this never passes one through. PURE.</summary>
        public static string IanaZone(string localId)
        {
            if (localId.Contains('/')) return localId;
            try
            {
                if (localId.Length > 0 && TimeZoneInfo.TryConvertWindowsIdToIanaId(localId, out string? iana) && iana is { Length: > 0 })
                    return iana;
            }
            catch (Exception) { /* invariant globalization throws rather than answering false */ }
            return FallbackTimeZone;
        }

        public static Result HomeSection(string sectionUri, string timeZone, int offset, CancellationToken ct)
        {
            var vars = new Vars(Queries.HomeSection);
            vars.W.WriteString("uri", sectionUri);
            vars.W.WriteString("homeEndUserIntegration", "INTEGRATION_DESKTOP");
            vars.W.WriteString("timeZone", timeZone);
            vars.W.WriteString("sp_t", "");
            vars.W.WriteNumber("sectionItemsOffset", offset);
            vars.W.WriteNumber("sectionItemsLimit", 20);
            vars.W.WriteBoolean("includeEpisodeContentRatingsV2", true);
            return Pathfinder(Queries.HomeSection, vars.Finish(), ct);
        }

        public static Result BrowseAll(CancellationToken ct)
        {
            var vars = new Vars(Queries.BrowseAll);
            WritePage(vars, "pagePagination", 0, 10);
            WritePage(vars, "sectionPagination", 0, 99);
            vars.W.WriteString("browseEndUserIntegration", "INTEGRATION_DESKTOP");
            return Pathfinder(Queries.BrowseAll, vars.Finish(), ct);
        }

        public static Result BrowsePage(string pageUri, int sectionOffset, CancellationToken ct)
        {
            var vars = new Vars(Queries.BrowsePage);
            WritePage(vars, "pagePagination", sectionOffset, 10);
            WritePage(vars, "sectionPagination", 0, 10);
            vars.W.WriteString("uri", pageUri);
            vars.W.WriteString("browseEndUserIntegration", "INTEGRATION_DESKTOP");
            vars.W.WriteBoolean("includeEpisodeContentRatingsV2", true);
            return Pathfinder(Queries.BrowsePage, vars.Finish(), ct);
        }

        public static Result BrowseSection(string sectionUri, int offset, CancellationToken ct)
        {
            var vars = new Vars(Queries.BrowseSection);
            WritePage(vars, "pagination", offset, 20);
            vars.W.WriteString("uri", sectionUri);
            vars.W.WriteString("browseEndUserIntegration", "INTEGRATION_DESKTOP");
            vars.W.WriteBoolean("includeEpisodeContentRatingsV2", true);
            return Pathfinder(Queries.BrowseSection, vars.Finish(), ct);
        }

        static void WritePage(in Vars vars, string name, int offset, int limit)
        {
            vars.W.WriteStartObject(name);
            vars.W.WriteNumber("offset", offset);
            vars.W.WriteNumber("limit", limit);
            vars.W.WriteEndObject();
        }

        /// <summary>The now-playing "About the artist" panel.</summary>
        public static Result NpvArtist(string artistUri, string trackUri, CancellationToken ct)
        {
            var vars = new Vars(Queries.NpvArtist);
            vars.W.WriteString("artistUri", artistUri);
            vars.W.WriteString("trackUri", trackUri);
            vars.W.WriteNumber("contributorsLimit", 10);
            vars.W.WriteNumber("contributorsOffset", 0);
            vars.W.WriteBoolean("enableRelatedVideos", true);
            vars.W.WriteBoolean("enableRelatedAudioTracks", true);
            return Pathfinder(Queries.NpvArtist, vars.Finish(), ct);
        }

        public static Result AlbumMerch(string albumUri, CancellationToken ct)
        {
            var vars = new Vars(Queries.AlbumMerch);
            vars.W.WriteString("uri", albumUri);
            return Pathfinder(Queries.AlbumMerch, vars.Finish(), ct);
        }

        public static Result SimilarAlbums(string seedTrackUri, int limit, CancellationToken ct)
        {
            var vars = new Vars(Queries.SimilarAlbums);
            vars.W.WriteString("uri", seedTrackUri);
            vars.W.WriteNumber("limit", limit);
            vars.W.WriteBoolean("albumsOnly", true);
            return Pathfinder(Queries.SimilarAlbums, vars.Finish(), ct);
        }

        public static Result WhatsNew(int offset, int limit, bool onlyUnplayed, CancellationToken ct)
        {
            var vars = new Vars(Queries.WhatsNew);
            vars.W.WriteNumber("offset", offset);
            vars.W.WriteNumber("limit", limit);
            vars.W.WriteBoolean("onlyUnPlayedItems", onlyUnplayed);
            vars.W.WriteStartArray("includedContentTypes");
            vars.W.WriteEndArray();
            vars.W.WriteBoolean("includeEpisodeContentRatingsV2", true);
            return Pathfinder(Queries.WhatsNew, vars.Finish(), ct);
        }

        public static Result UserTop(string timeRange, int limit, CancellationToken ct)
        {
            var vars = new Vars(Queries.UserTop);
            vars.W.WriteBoolean("includeTopArtists", true);
            WriteTopInput(vars, "topArtistsInput", limit, timeRange);
            vars.W.WriteBoolean("includeTopTracks", true);
            WriteTopInput(vars, "topTracksInput", limit, timeRange);
            return Pathfinder(Queries.UserTop, vars.Finish(), ct);
        }

        static void WriteTopInput(in Vars vars, string name, int limit, string timeRange)
        {
            vars.W.WriteStartObject(name);
            vars.W.WriteNumber("offset", 0);
            vars.W.WriteNumber("limit", limit);
            vars.W.WriteString("sortBy", "AFFINITY");
            vars.W.WriteString("timeRange", timeRange);
            vars.W.WriteEndObject();
        }

        /// <summary>The artist page's full discography, one page: the CAPTURED variables — <c>{uri, offset, limit,
        /// order: "DATE_DESC"}</c> — which is what makes the answer a paged list rather than the overview's capped one.
        /// PURE.</summary>
        public static byte[] DiscographyBody(string artistUri, int offset, int limit)
        {
            var vars = new Vars(Queries.Discography);
            vars.W.WriteString("uri", artistUri);
            vars.W.WriteNumber("offset", Math.Max(0, offset));
            vars.W.WriteNumber("limit", limit);
            vars.W.WriteString("order", "DATE_DESC");
            return vars.Finish();
        }

        public static Result Discography(string artistUri, int offset, int limit, CancellationToken ct)
            => Pathfinder(Queries.Discography, DiscographyBody(artistUri, offset, limit), ct);

        /// <summary>The cover grader's body: <c>{"imageUris":[…]}</c>, the uris verbatim and in order — the answer is
        /// POSITIONAL (no uri is echoed), so the order is the contract. PURE.</summary>
        public static byte[] DynamicColorsBody(ReadOnlySpan<string> imageUris)
        {
            var vars = new Vars(Queries.DynamicColors);
            vars.W.WriteStartArray("imageUris");
            foreach (string uri in imageUris) vars.W.WriteStringValue(uri);
            vars.W.WriteEndArray();
            return vars.Finish();
        }

        /// <summary>THE PALETTE'S FILLER (G-055) — install as <c>Palette.Filler = Spotify.Api.GradeCovers</c> once signed
        /// in. Runs on an api thread; the grading comes back index-parallel with <paramref name="imageUris"/>
        /// (<see cref="Palette.ParseDynamicColors"/>). A non-answer FAULTS the task, which is the palette's "transport
        /// failure" (it frees the rows to re-queue) as opposed to a null entry, which is a real "no colours".</summary>
        public static Task<Palette.Graded?[]> GradeCovers(string[] imageUris, CancellationToken ct)
            => RunAsync(() => Grade(imageUris, ct));

        static Palette.Graded?[] Grade(string[] imageUris, CancellationToken ct)
        {
            if (imageUris.Length == 0) return [];
            Result result = Pathfinder(Queries.DynamicColors, DynamicColorsBody(imageUris), ct);
            if (!result.Ok || result.Body.Length == 0)
                throw new HttpRequestException("getDynamicColorsByUris answered " + result.Status);
            using JsonDocument document = JsonDocument.Parse(result.Body);
            return Palette.ParseDynamicColors(document.RootElement, imageUris.Length);
        }

        // ── 8. search ────────────────────────────────────────────────────────────────────────────────────────────────
        //
        // Twelve operations over three body shapes. The facet picks the query; the shared shape is the one nine of
        // them use, and the two that differ say so in their own branch rather than behind a flag in a shared builder.

        /// <summary>Which search the omnibar or the search page is running. The order is the chip order.</summary>
        public enum SearchFacet : byte
        {
            Top, Tracks, Albums, Artists, Playlists, Podcasts, Episodes, Users, Authors, Audiobooks, Genres, Suggestions,
        }

        public static Query QueryFor(SearchFacet facet) => facet switch
        {
            SearchFacet.Top => Queries.SearchTopResults,
            SearchFacet.Tracks => Queries.SearchTracks,
            SearchFacet.Albums => Queries.SearchAlbums,
            SearchFacet.Artists => Queries.SearchArtists,
            SearchFacet.Playlists => Queries.SearchPlaylists,
            SearchFacet.Podcasts => Queries.SearchPodcasts,
            SearchFacet.Episodes => Queries.SearchEpisodes,
            SearchFacet.Users => Queries.SearchUsers,
            SearchFacet.Authors => Queries.SearchAuthors,
            SearchFacet.Audiobooks => Queries.SearchAudiobooks,
            SearchFacet.Genres => Queries.SearchGenres,
            _ => Queries.SearchSuggestions,
        };

        /// <summary>The page's facet (<see cref="Wavee.SearchFacet"/>, the entity layer's chip order) → the operation
        /// family here. The two enums order differently and Profiles is Users on the wire; a cast would be wrong for
        /// seven of the eleven. PURE.</summary>
        public static SearchFacet FacetOf(Wavee.SearchFacet facet) => facet switch
        {
            Wavee.SearchFacet.Tracks => SearchFacet.Tracks,
            Wavee.SearchFacet.Albums => SearchFacet.Albums,
            Wavee.SearchFacet.Playlists => SearchFacet.Playlists,
            Wavee.SearchFacet.Audiobooks => SearchFacet.Audiobooks,
            Wavee.SearchFacet.Podcasts => SearchFacet.Podcasts,
            Wavee.SearchFacet.Artists => SearchFacet.Artists,
            Wavee.SearchFacet.Episodes => SearchFacet.Episodes,
            Wavee.SearchFacet.Profiles => SearchFacet.Users,
            Wavee.SearchFacet.Genres => SearchFacet.Genres,
            Wavee.SearchFacet.Authors => SearchFacet.Authors,
            _ => SearchFacet.Top,
        };

        /// <summary>A search subject's uri (<c>wavee:search:&lt;NN&gt;:&lt;query&gt;</c>, <c>Entities.Search</c>) → its
        /// operation facet and the query VERBATIM (colons included). False for anything else, or a facet index the
        /// entity layer does not define. PURE.</summary>
        public static bool TryParseSearchSubject(string uri, out SearchFacet facet, out string query)
        {
            facet = SearchFacet.Top;
            query = "";
            string prefix = Wavee.Search.Prefix;
            if (!uri.StartsWith(prefix, StringComparison.Ordinal) || uri.Length < prefix.Length + 3) return false;
            char tens = uri[prefix.Length], ones = uri[prefix.Length + 1];
            if (tens is < '0' or > '9' || ones is < '0' or > '9' || uri[prefix.Length + 2] != ':') return false;
            var index = (Wavee.SearchFacet)((tens - '0') * 10 + (ones - '0'));
            if (!Enum.IsDefined(index)) return false;
            facet = FacetOf(index);
            query = uri[(prefix.Length + 3)..];
            return true;
        }

        /// <summary>How many hits a subject asks for: the top-results list is a short mixed shelf; a facet is a page.</summary>
        public static int SearchPageSize(SearchFacet facet) => facet == SearchFacet.Top ? 10 : 30;

        /// <summary>Build one search body. PURE, and public because the facet-to-shape mapping is the thing worth
        /// pinning: three shapes, twelve operations, and <c>includePreReleases</c> true for exactly two of them.</summary>
        public static byte[] SearchBody(SearchFacet facet, string term, int offset, int limit)
        {
            var vars = new Vars(QueryFor(facet));
            switch (facet)
            {
                case SearchFacet.Top:
                    vars.W.WriteString("query", term);
                    vars.W.WriteNumber("limit", limit);
                    vars.W.WriteNumber("offset", offset);
                    vars.W.WriteNumber("numberOfTopResults", 50);
                    vars.W.WriteBoolean("includeArtistHasConcertsField", false);
                    vars.W.WriteBoolean("includeAudiobooks", true);
                    vars.W.WriteBoolean("includeAuthors", true);
                    vars.W.WriteBoolean("includePreReleases", true);
                    vars.W.WriteBoolean("includeAlbumPreReleases", false);
                    vars.W.WriteBoolean("includeEpisodeContentRatingsV2", true);
                    vars.W.WriteNull("isPrefix");
                    vars.W.WriteStartArray("sectionFilters");
                    vars.W.WriteStringValue("GENERIC");
                    vars.W.WriteStringValue("VIDEO_CONTENT");
                    vars.W.WriteEndArray();
                    break;

                case SearchFacet.Episodes:
                    vars.W.WriteString("searchTerm", term);
                    vars.W.WriteNumber("offset", offset);
                    vars.W.WriteNumber("limit", limit);
                    vars.W.WriteBoolean("includeEpisodeContentRatingsV2", true);
                    break;

                case SearchFacet.Suggestions:
                    vars.W.WriteString("query", term);
                    vars.W.WriteNumber("limit", 30);
                    vars.W.WriteNumber("numberOfTopResults", 30);
                    vars.W.WriteNumber("offset", 0);
                    vars.W.WriteBoolean("includeAuthors", true);
                    vars.W.WriteBoolean("includeAlbumPreReleases", false);
                    vars.W.WriteBoolean("includeEpisodeContentRatingsV2", true);
                    break;

                default:
                    // The shared shape. `includePreReleases` is true for audiobooks ONLY, and genres is the one facet
                    // with a fixed top-result count — the two asymmetries in the family, which is why this is a switch
                    // rather than a loop over a table.
                    vars.W.WriteBoolean("includePreReleases", facet == SearchFacet.Audiobooks);
                    vars.W.WriteBoolean("includeAlbumPreReleases", facet != SearchFacet.Genres);
                    vars.W.WriteNumber("numberOfTopResults", facet == SearchFacet.Genres ? 20 : limit);
                    vars.W.WriteString("searchTerm", term);
                    vars.W.WriteNumber("offset", offset);
                    vars.W.WriteNumber("limit", limit);
                    vars.W.WriteBoolean("includeAudiobooks", true);
                    vars.W.WriteBoolean("includeAuthors", true);
                    vars.W.WriteBoolean("includeEpisodeContentRatingsV2", true);
                    break;
            }
            return vars.Finish();
        }

        /// <summary>A search the user is WAITING on gets eight seconds (0.2.9 <c>FetchSearchAsync</c>'s linked
        /// <c>CancelAfter</c>, G-053), not the client's thirty: past that the omnibar is showing a spinner for an answer
        /// the user has already typed past. A timeout is a transport status (0), which the planner retries.</summary>
        public const int SearchDeadlineMs = 8_000;

        /// <summary>One search. <paramref name="limit"/> is clamped to the gateway's window (1..50) and the offset
        /// floored at zero, because a negative offset is a 500 rather than an empty page.</summary>
        public static Result Search(SearchFacet facet, string term, int offset, int limit, CancellationToken ct)
        {
            offset = Math.Max(0, offset);
            limit = Math.Clamp(limit, 1, 50);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(SearchDeadlineMs);
            Result result = Pathfinder(QueryFor(facet), SearchBody(facet, term, offset, limit), deadline.Token);
            if (result.Status == 0 && deadline.IsCancellationRequested && !ct.IsCancellationRequested)
                Log.Warn("spotify", "search " + facet + " timed out after " + SearchDeadlineMs + " ms");
            return result;
        }

        public static int Search(SearchFacet facet, string term, int offset, int limit, string subjectUri,
            Staging into, CancellationToken ct)
        {
            Result result = Search(facet, term, offset, limit, ct);
            if (result.Ok) Decode.Export(result.Bytes, into, Encoding.UTF8.GetBytes(subjectUri));
            return result.Status;
        }

        // ── 9. concerts ──────────────────────────────────────────────────────────────────────────────────────────────

        public static Result ArtistConcerts(string artistUri, string? geoHash, bool includeNearby, CancellationToken ct)
        {
            var vars = new Vars(Queries.ArtistConcerts);
            vars.W.WriteString("artistUri", artistUri);
            WriteStringOrNull(vars, "geoHash", geoHash);
            vars.W.WriteBoolean("includeNearby", includeNearby);
            return Pathfinder(Queries.ArtistConcerts, vars.Finish(), ct);
        }

        public static Result Concert(string concertUri, bool authenticated, CancellationToken ct)
        {
            var vars = new Vars(Queries.Concert);
            vars.W.WriteString("uri", concertUri);
            vars.W.WriteBoolean("authenticated", authenticated);
            return Pathfinder(Queries.Concert, vars.Finish(), ct);
        }

        public static Result ConcertConcepts(string geoHash, string? conceptUri, CancellationToken ct)
        {
            var vars = new Vars(Queries.ConcertConcepts);
            vars.W.WriteString("geohash", geoHash);              // lowercase 'h' here, and only here
            WriteStringOrNull(vars, "conceptUri", conceptUri);
            return Pathfinder(Queries.ConcertConcepts, vars.Finish(), ct);
        }

        /// <summary>The concerts feed. <paramref name="geoHash"/> is written only when there is no place id — the two
        /// are alternatives, and sending both narrows the feed to nothing.</summary>
        public static Result ConcertFeed(string? geoHash, string? geonameId, DateOnly? from, DateOnly? to,
            ReadOnlySpan<string> conceptUris, double? radiusKm, string? paginationKey, CancellationToken ct)
        {
            var vars = new Vars(Queries.ConcertFeed);
            WriteStringOrNull(vars, "geoHash", geonameId is null ? geoHash : null);
            WriteStringOrNull(vars, "geonameId", geonameId);
            WriteDateRange(vars, from, to);
            WriteUriArrayOrNull(vars, "conceptUris", conceptUris);
            if (radiusKm is { } radius) vars.W.WriteNumber("radiusInKm", radius); else vars.W.WriteNull("radiusInKm");
            WriteStringOrNull(vars, "paginationKey", paginationKey);
            return Pathfinder(Queries.ConcertFeed, vars.Finish(), ct);
        }

        public static Result ConcertCount(string? geonameId, double? radiusKm, DateOnly? from, DateOnly? to,
            ReadOnlySpan<string> conceptUris, CancellationToken ct)
        {
            var vars = new Vars(Queries.ConcertCount);
            WriteStringOrNull(vars, "geonameId", geonameId);
            if (radiusKm is { } radius) vars.W.WriteNumber("radiusInKm", radius); else vars.W.WriteNull("radiusInKm");
            WriteDateRange(vars, from, to);
            WriteUriArrayOrNull(vars, "conceptUris", conceptUris);
            return Pathfinder(Queries.ConcertCount, vars.Finish(), ct);
        }

        public static Result ConcertLocationDetails(string? geonameId, bool anonymous, CancellationToken ct)
        {
            var vars = new Vars(Queries.ConcertLocationDetails);
            WriteStringOrNull(vars, "geonameId", geonameId);
            vars.W.WriteBoolean("isAnonymous", anonymous);
            return Pathfinder(Queries.ConcertLocationDetails, vars.Finish(), ct);
        }

        public static Result SearchConcertLocations(string query, CancellationToken ct)
        {
            var vars = new Vars(Queries.SearchConcertLocations);
            vars.W.WriteString("query", query);
            return Pathfinder(Queries.SearchConcertLocations, vars.Finish(), ct);
        }

        public static Result ConcertLocationsByLatLon(double lat, double lon, CancellationToken ct)
        {
            var vars = new Vars(Queries.ConcertLocationsByLatLon);
            vars.W.WriteNumber("lat", lat);
            vars.W.WriteNumber("lon", lon);
            return Pathfinder(Queries.ConcertLocationsByLatLon, vars.Finish(), ct);
        }

        public static Result SaveConcertLocation(string geonameId, CancellationToken ct)
        {
            var vars = new Vars(Queries.SaveLocation);
            vars.W.WriteString("geonameId", geonameId);
            return Pathfinder(Queries.SaveLocation, vars.Finish(), ct);
        }

        public static Result UserLocation(CancellationToken ct)
            => Pathfinder(Queries.UserLocation, new Vars(Queries.UserLocation).Finish(), ct);

        public static Result InferredUserLocation(CancellationToken ct)
            => Pathfinder(Queries.InferredUserLocation, new Vars(Queries.InferredUserLocation).Finish(), ct);

        public static Result ArtistConcertsPageLocation(CancellationToken ct)
            => Pathfinder(Queries.ArtistConcertsPageLocation, new Vars(Queries.ArtistConcertsPageLocation).Finish(), ct);

        static void WriteStringOrNull(in Vars vars, string name, string? value)
        {
            if (value is null) vars.W.WriteNull(name); else vars.W.WriteString(name, value);
        }

        static void WriteDateRange(in Vars vars, DateOnly? from, DateOnly? to)
        {
            if (from is null || to is null) { vars.W.WriteNull("dateRange"); return; }
            vars.W.WriteStartObject("dateRange");
            vars.W.WriteString("from", from.Value.ToString("yyyy-MM-dd"));
            vars.W.WriteString("to", to.Value.ToString("yyyy-MM-dd"));
            vars.W.WriteEndObject();
        }

        static void WriteUriArrayOrNull(in Vars vars, string name, ReadOnlySpan<string> values)
        {
            if (values.Length == 0) { vars.W.WriteNull(name); return; }
            vars.W.WriteStartArray(name);
            foreach (string value in values) vars.W.WriteStringValue(value);
            vars.W.WriteEndArray();
        }

        // ── 10. the spclient one-offs ────────────────────────────────────────────────────────────────────────────────
        //
        // Each of these was a `Spotify*Service` class. A route, a verb, a header set and a status rule is all any of
        // them ever was; `RequestKind.Custom` carries the ones D's fold has no kind for, and a `Route` value carries the
        // ones whose captured spelling (a query string, an Accept) the fold's kind does not have.

        const HeaderSet CommonJson = HeaderSet.Bearer | HeaderSet.ClientToken | HeaderSet.Identity
            | HeaderSet.AcceptLanguage | HeaderSet.AcceptJson;
        const HeaderSet CommonProtobuf = HeaderSet.Bearer | HeaderSet.ClientToken | HeaderSet.Identity
            | HeaderSet.AcceptLanguage | HeaderSet.AcceptProtobuf;

        static Result Get(scoped ReadOnlySpan<char> path, ApiHost host, HeaderSet headers, CancellationToken ct)
        {
            var args = new RequestArgs { Path = path, Host = host, Verb = Verb.Get, Headers = headers };
            return Send(RequestKind.Custom, args, ct);
        }

        /// <summary><c>/content-filter/v1/liked-songs</c>, the account's Liked Songs chip set. PURE.</summary>
        public static Route LikedContentFiltersRoute
            => new(Verb.Get, ApiHost.Spclient, "/content-filter/v1/liked-songs?subjective=true&market=from_token", CommonJson);

        /// <summary>The account's "liked songs" chip set, CONDITIONAL (G-053): pass the <see cref="Result.ETag"/> of the
        /// body you hold and a 304 says it still stands (0.2.9 restamped its 6 h cache on exactly that). 404 means "no
        /// chip set", which is ordinary and not an error.</summary>
        public static Result LikedContentFilters(string? etag, CancellationToken ct)
            => Send(LikedContentFiltersRoute, [], ct, etag);

        /// <summary>The friend-activity seed, per dealer connection id. A 404 is an EMPTY feed, not a failure — the
        /// service answers 404 for an account that follows nobody.</summary>
        public static Result FriendFeed(string connectionId, CancellationToken ct)
        {
            Span<char> path = stackalloc char[256];
            var w = new PathWriter(path);
            w.Append("/presence-view/v2/init-friend-feed/");
            w.AppendEscaped(connectionId);
            return Get(w.Written, ApiHost.Spclient, CommonJson, ct);
        }

        /// <summary>One friend's presence, fetched when the dealer pushes <c>hm://presence2/user/{id}</c>. 403 and 404
        /// both mean "drop this row", never "retry".</summary>
        public static Result FriendPresence(string userId, CancellationToken ct)
        {
            Span<char> path = stackalloc char[256];
            var w = new PathWriter(path);
            w.Append("/presence-view/v1/user/");
            w.AppendEscaped(userId);
            return Get(w.Written, ApiHost.Spclient, CommonJson, ct);
        }

        public static Result Notifications(int limit, CancellationToken ct)
        {
            Span<char> path = stackalloc char[128];
            var w = new PathWriter(path);
            w.Append("/gander/v2/GetNotifications?locale=");
            w.AppendEscaped(AcceptLanguage);
            w.Append("&limit=");
            w.Append(limit);
            return Get(w.Written, ApiHost.Spclient, CommonJson, ct);
        }

        /// <summary>A playlist's save count (<c>popcount.proto</c>). The SUPPRESS cap is the caller's rule: the service
        /// answers with a sentinel rather than an error for the editorial lists, and anything at or above
        /// <see cref="ImplausibleSaveCount"/> is not a number to render.</summary>
        public const long ImplausibleSaveCount = 100_000_000;

        /// <summary><c>/popcount/v2/playlist/&lt;id&gt;/count</c> — a protobuf answer, so a protobuf Accept. PURE.</summary>
        public static Route PopcountRoute(string playlistId)
            => new(Verb.Get, ApiHost.Spclient, "/popcount/v2/playlist/" + Escaped(playlistId) + "/count", CommonProtobuf,
                   RequestKind.Popcount);

        public static Result Popcount(string playlistId, CancellationToken ct) => Send(PopcountRoute(playlistId), [], ct);

        /// <summary><c>/playlist-permission/v1/playlist/&lt;id&gt;/permission/base</c> (G-053): the base link-share
        /// level and its revision, polled after every mutation in the capture. PURE.</summary>
        public static Route PermissionBaseRoute(string playlistId)
            => new(Verb.Get, ApiHost.Spclient, "/playlist-permission/v1/playlist/" + Escaped(playlistId) + "/permission/base",
                   CommonProtobuf);

        public static Result PlaylistPermissionBase(string playlistId, CancellationToken ct)
            => Send(PermissionBaseRoute(playlistId), [], ct);

        /// <summary><c>/user-profile-view/v3/profile/&lt;username&gt;?market=from_token</c> — the captured spelling, market
        /// included. PURE.</summary>
        public static Route ProfileRoute(string username)
            => new(Verb.Get, ApiHost.Spclient, "/user-profile-view/v3/profile/" + Escaped(username) + "?market=from_token",
                   CommonJson);

        /// <summary>The REST arm of profile resolution (G-033). Only 200 and 404 are ANSWERS: a 404 seals "no public
        /// profile", and every other status means we did not find out.</summary>
        public static Result Profile(string username, CancellationToken ct) => Send(ProfileRoute(username), [], ct);

        /// <summary>The artist page's extended top-track list — a JSON body of bare uris the pathfinder overview does
        /// not carry. The FULL artist uri is the path tail, escaped. The list is capped at
        /// <see cref="ArtistTopTracksCap"/> rows.</summary>
        public const int ArtistTopTracksCap = 50;

        public static Result ArtistTopTracksExtended(string artistUri, CancellationToken ct)
        {
            Span<char> path = stackalloc char[256];
            var w = new PathWriter(path);
            w.Append("/artistplaycontext/v1/page/spotify/artist-top-tracks-extensions/");
            w.AppendEscaped(artistUri);
            return Get(w.Written, ApiHost.Spclient, CommonJson, ct);
        }

        /// <summary>The server clock, for the ownership fence.</summary>
        public static Result ServerTime(CancellationToken ct)
        {
            var args = default(RequestArgs);
            return Send(RequestKind.ServerTime, args, ct);
        }

        public static Result ContextResolve(string contextUri, CancellationToken ct)
        {
            var args = new RequestArgs { Id = contextUri };
            return Send(RequestKind.ContextResolve, args, ct);
        }

        /// <summary>"Start radio" (G-251): the seed's radio PLAYLIST, <c>GET /inspiredby-mix/v2/seed_to_playlist/&lt;seed&gt;</c>
        /// — <paramref name="seedUri"/> is the literal <c>spotify:track:…</c> or <c>spotify:artist:…</c>. The JSON answer's
        /// <c>mediaItems[0].uri</c> is read by <see cref="Decode.RadioPlaylistUri"/>; a 404 is "this seed has no radio".</summary>
        public static Result RadioSeed(string seedUri, CancellationToken ct)
        {
            var args = new RequestArgs { Id = seedUri };
            return Send(RequestKind.RadioSeed, args, ct);
        }

        /// <summary>Autoplay for a context that ran out. <paramref name="podcast"/> picks the `/autopodcast` twin.</summary>
        public static Result Autoplay(byte[] body, bool podcast, CancellationToken ct)
        {
            if (podcast) return PostEncoded("/context-resolve/v1/autopodcast", ApiHost.Spclient,
                CommonProtobuf | HeaderSet.ContentForm, body, "application/x-www-form-urlencoded", null, ct);
            var args = new RequestArgs { Body = body, Flag = podcast };
            return Send(RequestKind.Autoplay, args, ct);
        }

        /// <summary>A path segment, percent-escaped through <see cref="PathWriter.AppendEscaped"/> — the one escape
        /// every route here shares.</summary>
        static string Escaped(string segment)
        {
            // Every character escapes to at most three: a buffer of 3× can never overflow the writer.
            Span<char> buffer = segment.Length <= 256 ? stackalloc char[768] : new char[segment.Length * 3];
            var w = new PathWriter(buffer);
            w.AppendEscaped(segment);
            return new string(w.Written);
        }

        // ── 11. the absolute-url text GET (G-008) ────────────────────────────────────────────────────────────────────

        /// <summary>May a bearer go to <paramref name="url"/>? https on <c>spotify.com</c> or a subdomain of it — the
        /// resolved spclient host is one — and nothing else, ever: this is the gate between the account's token and a
        /// url a caller composed. PURE.</summary>
        public static bool IsSpotifyUrl(string url)
            => Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
               && parsed.Scheme == Uri.UriSchemeHttps
               && (parsed.Host.Equals("spotify.com", StringComparison.OrdinalIgnoreCase)
                   || parsed.Host.EndsWith(".spotify.com", StringComparison.OrdinalIgnoreCase));

        /// <summary>A bearer GET of an ABSOLUTE Spotify url, answered as text (null on a miss, a non-2xx or a refused
        /// url) — the lyrics stack's Spotify-native source (G-008). BLOCKS; api threads only.
        ///
        /// <para>The header recipe is 0.2.9's proven color-lyrics one: <c>App-Platform: Android</c> with the desktop
        /// app version and NO client token. The Android platform is what lets that CDN serve without a client token; a
        /// desktop or web-player platform needs one and 403s without it — which is why this does not go through the
        /// header-set runner, whose Identity bit stamps the desktop platform. One forced re-mint on a 401, as every
        /// other send.</para></summary>
        public static string? GetText(string url, CancellationToken ct)
        {
            if (!IsSpotifyUrl(url))
            {
                Log.Warn("spotify", "GetText refused a url outside spotify.com");
                return null;
            }
            for (int attempt = 0; ; attempt++)
            {
                Result result = GetTextOnce(url, ct);
                if (result.Status == 401 && attempt == 0 && AccessToken(force: true) is not null) continue;
                return result.Ok && result.Body.Length > 0 ? Encoding.UTF8.GetString(result.Body) : null;
            }
        }

        /// <summary><see cref="GetText"/> on an api thread, as the task the lyrics stack takes — install as
        /// <c>Lyrics.Boot(resolve, Spotify.Api.GetTextAsync, Spotify.SpclientBaseUrl)</c>.</summary>
        public static Task<string?> GetTextAsync(string url, CancellationToken ct) => RunAsync(() => GetText(url, ct));

        static Result GetTextOnce(string url, CancellationToken ct)
        {
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, url);
                HttpRequestHeaders h = message.Headers;
                if (AccessToken() is { Length: > 0 } bearer) h.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
                h.TryAddWithoutValidation("App-Platform", "Android");
                h.TryAddWithoutValidation("Spotify-App-Version", Identity.AppVersion);
                h.TryAddWithoutValidation("Accept", "application/json");
                using HttpResponseMessage response = Client.Send(message, HttpCompletionOption.ResponseHeadersRead, ct);
                return new Result((int)response.StatusCode, ReadBody(response, ct));
            }
            catch (OperationCanceledException)
            {
                return Result.Transport;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or ObjectDisposedException)
            {
                Log.Warn("spotify", "GetText failed: " + ex.GetType().Name);
                return Result.Transport;
            }
        }

        // ── 12. storage resolve (the CDN mirrors for one audio file) ─────────────────────────────────────────────────

        /// <summary>Where the bytes of <paramref name="fileIdHex"/> live: the mirror list and the TTL, as a
        /// `StorageResolveResponse`. <see cref="Audio"/> is the caller, and the only one.
        ///
        /// <para>NOT format-agnostic, which is what the v1 route pretended. The service signs a url per (format, file
        /// id) pair, so <paramref name="wireFormat"/> — the catalogue's own `AudioFile.Format` for the very file this
        /// id names — goes in the path as its own segment. Resolving a FLAC id without it signs a url into the Ogg
        /// object namespace and every byte of the answer 404s.</para></summary>
        public static Result StorageResolve(string fileIdHex, Md.AudioFile.Types.Format wireFormat, CancellationToken ct)
        {
            var args = new RequestArgs { Id = fileIdHex, Number = (long)wireFormat };
            return Send(RequestKind.StorageResolve, args, ct);
        }
    }
}
