// ── Spotify/Spotify.Api.cs ─────────────────────────────────────────────────────────────────────────────────────────
// one function per request
//
// Role: SHELL
// Owner: F
// Wave: 2
// Budget: 1800 lines
// Spec: plan
//
// WHAT THIS REPLACES. 0.2.9 carried ~20 `Spotify*Service` classes, a `PathfinderClient`, a `PathfinderResource`, a
// four-layer middleware pipeline (auth · client-token · rate limit · pathfinder headers) and a `Resource<K,V>` cache
// per service — every one of them re-deciding a url, a verb and a `Dictionary<string,string>` of headers. Here a
// request is a VALUE: `Spotify.Build` (owner D, CORE, pure) folds (session, kind, args) into a `Request`, and this
// file is the RUNNER that turns that value into an `HttpRequestMessage`, plus the one-function-per-request calls that
// pick a kind and fill the arguments. No service classes, no interfaces, no DI and no per-service cache: an answer
// goes to `Spotify.Decode` and lands in the columns, and the columns ARE the cache (D16).
//
// THREADING (C9/C1). Every call here BLOCKS — `HttpClient.Send`, not `SendAsync`, exactly like `Spotify.Session.cs`,
// whose comment this file inherits: these paths exist to block. None of them may run on the UI thread. `Run` is the
// door: a bounded work queue (C8 — 256 deep, and a full queue REFUSES and says so rather than growing) drained by
// four named threads `wavee-spotify-api-0..3`. Nothing here touches a table, a signal or `Entities.Strings` (C1): an
// answer is decoded into a `Staging` on the worker thread and handed to the UI thread through `Spotify.Post`.
//
// EPOCHS (C4). The only stateful consumer here is the `Fetch` provider, and its epoch is the batch's: the staging is
// stamped with `batch.Epoch` and `Entities.Commit` drops the whole batch if the scope has moved. Nothing else in this
// file holds state across calls beyond the HTTP connection pool.
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
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee;

public static partial class Spotify
{
    /// <summary>Every Spotify HTTP request the app makes, one function each. SHELL: each one blocks.</summary>
    public static class Api
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

            public Result(int status, byte[] body, int retryAfterSeconds = 0)
            {
                Status = status;
                Body = body;
                RetryAfterSeconds = retryAfterSeconds;
            }

            public bool Ok => Status is >= 200 and < 300;
            /// <summary>A conditional read the server answered "unchanged".</summary>
            public bool NotModified => Status == 304;
            public ReadOnlySpan<byte> Bytes => Body;

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
        /// as <c>StringId</c>s, which a shell thread may not resolve (C1), so `App.cs` copies them here on the
        /// UI thread when the session goes Online. The empty default is deliberate: the service falls back to the
        /// bearer's own market, and a WRONG market is worse than none.</summary>
        public static string Market { get; set; } = "";

        /// <inheritdoc cref="Market"/>
        public static string Catalogue { get; set; } = "premium";

        /// <summary>Start the workers and register the <see cref="Fetch"/> provider. Idempotent; `App.cs` calls it
        /// once, after `Spotify.Boot`.</summary>
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

        static void WorkerLoop(int index)
        {
            foreach (Action work in Work.GetConsumingEnumerable())
            {
                try { work(); }
                catch (Exception ex) { Log.Error("spotify", "api worker " + index + " faulted", ex); }
            }
        }

        // ── 3. the runner ────────────────────────────────────────────────────────────────────────────────────────────

        static readonly HttpClient Client = new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxConnectionsPerServer = 8,
        })
        { Timeout = TimeSpan.FromSeconds(30) };

        const string PathfinderHost = "https://api-partner.spotify.com";
        const string SpclientWgHost = "https://spclient.wg.spotify.com";
        const string Login5Host = "https://login5.spotify.com";
        const string ClientTokenHost = "https://clienttoken.spotify.com";
        const string ApResolveHost = "https://apresolve.spotify.com";
        const string DealerFallbackHost = "https://dealer.spotify.com";
        const string ImageUploadHost = "https://image-upload.spotify.com";

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
            Verb verb = request.Verb;
            HeaderSet headers = request.Headers;
            string syncReason = request.SyncReason.IsEmpty ? "" : new string(request.SyncReason);

            for (int attempt = 0; ; attempt++)
            {
                Result result = SendOnce(verb, url, headers, kind, body, syncReason, ct);
                if (result.Status != 401 || attempt > 0) return result;
                // The bearer expired mid-flight (or the clock drifted). One forced mint, one retry, and then the 401
                // IS the answer — a loop here is how a revoked account burns a token bucket.
                if (AccessToken(force: true) is null) return result;
            }
        }

        static Result SendOnce(Verb verb, string url, HeaderSet headers, RequestKind kind, byte[] body,
            string syncReason, CancellationToken ct, string? contentType = null, string? contentEncoding = null)
        {
            try
            {
                using var message = new HttpRequestMessage(MethodOf(verb), url);
                Stamp(message, headers, kind, body, syncReason);
                if (message.Content is { } payload)
                {
                    if (contentType is not null) payload.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                    if (contentEncoding is not null) payload.Headers.ContentEncoding.Add(contentEncoding);
                }
                using HttpResponseMessage response = Client.Send(message, HttpCompletionOption.ResponseHeadersRead, ct);
                return new Result((int)response.StatusCode, ReadBody(response, ct), RetryAfter(response));
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
        {
            string url = BaseUrl(host) + new string(path);
            for (int attempt = 0; ; attempt++)
            {
                Result result = SendOnce(Verb.Post, url, headers, RequestKind.Custom, body, "", ct,
                    contentType, contentEncoding);
                if (result.Status != 401 || attempt > 0) return result;
                if (AccessToken(force: true) is null) return result;
            }
        }

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
                h.TryAddWithoutValidation("Spotify-App-Version", web ? Identity.WebPlayerAppVersion : Identity.ClientVersion);
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
            if ((headers & HeaderSet.AppliedLenses) != 0) h.TryAddWithoutValidation("spotify-applied-lenses", "auto");
            if ((headers & HeaderSet.AcceptListItems) != 0)
                h.TryAddWithoutValidation("x-accept-list-items", "audio-track, audio-episode, video-episode, audiobook");
            if ((headers & HeaderSet.AcceptGeoblock) != 0) h.TryAddWithoutValidation("spotify-accept-geoblock", "dummy");
            if ((headers & HeaderSet.DsaMode) != 0) h.TryAddWithoutValidation("spotify-dsa-mode-enabled", "false");
            if ((headers & HeaderSet.Origin) != 0) h.TryAddWithoutValidation("Origin", SpclientBaseUrl().TrimEnd('/'));
            if (syncReason.Length > 0) h.TryAddWithoutValidation("spotify-playlist-sync-reason", syncReason);

            const HeaderSet AnyContent = HeaderSet.ContentProtobuf | HeaderSet.ContentJson | HeaderSet.ContentForm;
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

        /// <summary>The catalogue trait one entity kind is fetched with. Exactly one per kind — extra traits are added
        /// by the caller, not by this map. Kinds the metadata service does not serve fold to
        /// <c>ExtensionKind.UnknownExtension</c>, which is never sent.</summary>
        public static Xm.ExtensionKind CatalogKindOf(EntityKind kind) => kind switch
        {
            EntityKind.Track => Xm.ExtensionKind.TrackV4,
            EntityKind.Episode => Xm.ExtensionKind.EpisodeV4,
            EntityKind.Album => Xm.ExtensionKind.AlbumV4,
            EntityKind.Artist => Xm.ExtensionKind.ArtistV4,
            EntityKind.Show => Xm.ExtensionKind.ShowV4,
            EntityKind.Playlist => Xm.ExtensionKind.ListMetadataV2,
            _ => Xm.ExtensionKind.UnknownExtension,
        };

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
            for (int attempt = 0; ; attempt++)
            {
                Result result = SendOnce(Verb.Post, url, headers, RequestKind.ExtendedMetadata, body, "", ct);
                if (result.Status != 401 || attempt > 0) return result;
                if (AccessToken(force: true) is null) return result;
            }
        }

        /// <summary>The catalogue read for a span of uris of ONE kind: build, POST, decode into <paramref name="into"/>.
        /// Returns the status, so a caller can tell "nothing there" from "we never asked".</summary>
        public static int Metadata(ReadOnlySpan<string> uris, EntityKind kind, Staging into, CancellationToken ct)
        {
            Xm.ExtensionKind ext = CatalogKindOf(kind);
            if (ext == Xm.ExtensionKind.UnknownExtension || uris.Length == 0) return 0;
            var kinds = new Xm.ExtensionKind[uris.Length];
            kinds.AsSpan().Fill(ext);
            return PostMetadata(MetadataBody(uris, kinds, Market, Catalogue), into, ct);
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
            var kinds = new Xm.ExtensionKind[userUris.Length];
            kinds.AsSpan().Fill(Xm.ExtensionKind.UserProfile);
            return PostMetadata(MetadataBody(userUris, kinds, Market, Catalogue), into, ct);
        }

        // ── 5. the fetch provider (the planner's transport, Wave 1's seam) ───────────────────────────────────────────

        /// <summary>`Entities.Ensure` → `Fetch.Plan` → here. One batch is one POST; the answer is decoded on the api
        /// thread into a pooled `Staging` and handed to the UI thread, which is C10 exactly.</summary>
        sealed class SpotifyFetchProvider : FetchProvider
        {
            public override EntityProvider Provider => EntityProvider.Spotify;

            public override void Start(FetchBatch batch)
            {
                uint ticket = batch.Ticket;
                if (!Run(() => Answer(batch, ticket))) Fetch.Failed(ticket, 0, 0);
            }

            static void Answer(FetchBatch batch, uint ticket)
            {
                Xm.ExtensionKind ext = batch.Extension != 0 ? (Xm.ExtensionKind)batch.Extension : CatalogKindOf(batch.Kind);   // kind 5 rides its own batch on the spotify:audio: entity (FLAC plan §5)
                if (ext == Xm.ExtensionKind.UnknownExtension)
                {
                    // Not a kind the metadata service serves. That is an ANSWER ("nothing for these"), not a failure:
                    // failing would clear the in-flight marks and the planner would ask again on the next mount.
                    Spotify.Post(() => Fetch.Answer(ticket, null));
                    return;
                }

                int count = batch.Count;
                var uris = new string[count];
                var kinds = new Xm.ExtensionKind[count];
                for (int i = 0; i < count; i++)
                {
                    uris[i] = batch.Uri(i);
                    kinds[i] = ext;
                }

                Result result = MetadataPost(MetadataBody(uris, kinds, Market, Catalogue), CancellationToken.None);
                if (!result.Ok)
                {
                    int status = result.Status;
                    int retryAfter = result.RetryAfterSeconds;
                    Spotify.Post(() => Fetch.Failed(ticket, status, retryAfter));
                    return;
                }

                Staging staging = Staging.Rent();
                staging.Epoch = batch.Epoch;
                try
                {
                    if (result.Body.Length > 0) Decode.ExtendedMetadata(result.Bytes, staging, batch);
                }
                catch (Exception ex)
                {
                    // A decoder that throws must not strand the batch: the planner would keep the in-flight marks for
                    // the life of the scope and those rows would never be asked for again.
                    Log.Error("spotify", "metadata decode faulted", ex);
                    Staging.Return(staging);
                    Spotify.Post(() => Fetch.Failed(ticket, 0, 0));
                    return;
                }
                Spotify.Post(() => Fetch.Answer(ticket, staging));
            }
        }

        // ── 6. pathfinder: the persisted-query table ─────────────────────────────────────────────────────────────────
        //
        // Every pathfinder call is POST /pathfinder/v2/query with the SAME body shape and a different (operationName,
        // sha256Hash, variables) triple. The hashes ARE the protocol — a stale one is a 400 — so they live in one
        // named table rather than inline at thirty call sites where a typo hides.

        /// <summary>One persisted query. <see cref="Web"/> selects the web-player identity tuple (the operations
        /// Spotify itself serves from the web bundle); everything else is the desktop client.</summary>
        public readonly record struct Query(string Op, string Hash, bool Web);

        /// <summary>The persisted-query table, verbatim from the captured 1.2.94 client.</summary>
        public static class Queries
        {
            public static readonly Query ArtistOverview = new("queryArtistOverview",
                "ae0e2958a4ab645b35ca19ac04d0495ae12d9c5d7b7286217674801a9aab281a", true);
            public static readonly Query Album = new("getAlbum",
                "b9bfabef66ed756e5e13f68a942deb60bd4125ec1f1be8cc42769dc0259b4b10", true);
            public static readonly Query Track = new("getTrack",
                "612585ae06ba435ad26369870deaae23b5c8800a256cd8a57e08eddc25a37294", true);
            public static readonly Query Home = new("home",
                "9052ac65ff42aefe6d39c45c184d9144cf8dbcc233ea1a76f8649264ad3e7896", false);
            /// <summary>The section query rides the SAME persisted hash as <see cref="Home"/> — the gateway keys on
            /// the operation NAME, and the two share a document.</summary>
            public static readonly Query HomeSection = new("homeSection",
                "9052ac65ff42aefe6d39c45c184d9144cf8dbcc233ea1a76f8649264ad3e7896", false);
            public static readonly Query BrowseAll = new("browseAll",
                "dbd8b55e09a58afc52eab438bc228ba28fd72ac2f2148c6c26354980e4579001", false);
            public static readonly Query BrowsePage = new("browsePage",
                "f5c4e6d668f5716464a231c1cc8b22c1cbf6ad68b09929fd7de813a30581298b", false);
            public static readonly Query BrowseSection = new("browseSection",
                "b13c1cccbfcb6947753c2613411b3566485c21fd5f36d80a80bb64be61ba2d51", false);
            public static readonly Query NpvArtist = new("queryNpvArtist",
                "b2cedf7ed0f29c713567d97ed69b848c8387294edfe58a0e439a3a5669cc27bb", false);
            public static readonly Query AlbumMerch = new("queryAlbumMerch",
                "3ef44ed6f17be67299538fe77faffab4075aeaf9e1085f10fc835592266711b5", false);
            public static readonly Query SimilarAlbums = new("similarAlbumsBasedOnThisTrack",
                "1d1f93a737498adca2c892c73af87fc0b052afe4e1a33c989540c32413dfae17", false);
            public static readonly Query WhatsNew = new("queryWhatsNewFeed",
                "d889c8c936ab192af8ced595427f5ba2acdf63478fdc0a181c8d477f8322630e", true);
            public static readonly Query UserTop = new("userTopContent",
                "49ee15704de4a7fdeac65a02db20604aa11e46f02e809c55d9a89f6db9754356", true);
            public static readonly Query Discography = new("queryArtistDiscographyAll",
                "5e07d323febb57b4a56a42abbf781490e58764aa45feb6e3dc0591564fc56599", true);

            public static readonly Query SearchTopResults = new("searchTopResultsList",
                "337d8b1b4f911fb12c60996623391703c2807550baccb51d95f5eabc8c8bdacd", true);
            public static readonly Query SearchTracks = new("searchTracks",
                "59ee4a659c32e9ad894a71308207594a65ba67bb6b632b183abe97303a51fa55", true);
            public static readonly Query SearchAlbums = new("searchAlbums",
                "64ae1fe6df380b038c0a65a2606d3361bc270de6870b2fdc99cf0848b1efa6d3", true);
            public static readonly Query SearchArtists = new("searchArtists",
                "270905851ba5c7faca81cfe053c2dbd8ceb4f156a0e0ef4b385af75ab69ffd13", true);
            public static readonly Query SearchPlaylists = new("searchPlaylists",
                "af1730623dc1248b75a61a18bad1f47f1fc7eff802fb0676683de88815c958d8", true);
            public static readonly Query SearchPodcasts = new("searchPodcasts",
                "0195d9f61b43606d490bca64c3456e3593528cea6cc05c7e822c7c42beed0f4e", true);
            public static readonly Query SearchUsers = new("searchUsers",
                "d3f7547835dc86a4fdf3997e0f79314e7580eaf4aaf2f4cb1e71e189c5dfcb1f", true);
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

        /// <summary>Decode a pathfinder answer through one of owner E's folds. The reader goes by reference, which is
        /// why this is a named delegate and not a <c>Func</c>.</summary>
        public delegate void JsonFold(ref Utf8JsonReader reader, Staging staging);

        /// <summary>Run <paramref name="fold"/> over an answer. False when there was nothing to read — the caller then
        /// knows the difference between "the server said no" and "the server said nothing".</summary>
        public static bool Fold(in Result result, JsonFold fold, Staging into)
        {
            if (!result.Ok || result.Body.Length == 0) return false;
            var reader = new Utf8JsonReader(result.Bytes);
            fold(ref reader, into);
            return true;
        }

        // ── 7. the pathfinder requests, one function each ────────────────────────────────────────────────────────────

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
            Result result = AlbumQuery(albumUri, 0, 50, ct);
            Fold(result, static (ref Utf8JsonReader r, Staging s) => Decode.GetAlbum(ref r, s), into);
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
            Fold(result, static (ref Utf8JsonReader r, Staging s) => Decode.GetTrack(ref r, s), into);
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
            Fold(result, static (ref Utf8JsonReader r, Staging s) => Decode.ArtistOverview(ref r, s), into);
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
            Fold(result, static (ref Utf8JsonReader r, Staging s) => Decode.Home(ref r, s), into);
            return result.Status;
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

        /// <summary>The artist page's full discography tab.</summary>
        public static Result Discography(string artistUri, int offset, int limit, CancellationToken ct)
        {
            var vars = new Vars(Queries.Discography);
            vars.W.WriteString("uri", artistUri);
            vars.W.WriteNumber("offset", offset);
            vars.W.WriteNumber("limit", limit);
            vars.W.WriteString("locale", "");
            return Pathfinder(Queries.Discography, vars.Finish(), ct);
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

        /// <summary>One search. <paramref name="limit"/> is clamped to the gateway's window (1..50) and the offset
        /// floored at zero, because a negative offset is a 500 rather than an empty page.</summary>
        public static Result Search(SearchFacet facet, string term, int offset, int limit, CancellationToken ct)
        {
            offset = Math.Max(0, offset);
            limit = Math.Clamp(limit, 1, 50);
            return Pathfinder(QueryFor(facet), SearchBody(facet, term, offset, limit), ct);
        }

        public static int Search(SearchFacet facet, string term, int offset, int limit, string subjectUri,
            Staging into, CancellationToken ct)
        {
            Result result = Search(facet, term, offset, limit, ct);
            if (!result.Ok || result.Body.Length == 0) return result.Status;
            var reader = new Utf8JsonReader(result.Bytes);
            Decode.Search(ref reader, Encoding.UTF8.GetBytes(subjectUri), into);
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
        // them ever was; `RequestKind.Custom` carries the ones D's fold has no kind for, which is exactly what the
        // escape hatch is documented to be ("a new route lands here first and graduates when a second caller wants it").

        const HeaderSet CommonJson = HeaderSet.Bearer | HeaderSet.ClientToken | HeaderSet.Identity
            | HeaderSet.AcceptLanguage | HeaderSet.AcceptJson;

        static Result Get(scoped ReadOnlySpan<char> path, ApiHost host, HeaderSet headers, CancellationToken ct)
        {
            var args = new RequestArgs { Path = path, Host = host, Verb = Verb.Get, Headers = headers };
            return Send(RequestKind.Custom, args, ct);
        }

        /// <summary>The account's "liked songs" chip set. 304 is an answer (unchanged); 404 means "no chip set", which
        /// is ordinary and not an error.</summary>
        public static Result LikedContentFilters(CancellationToken ct)
            => Get("/content-filter/v1/liked-songs?subjective=true&market=from_token", ApiHost.Spclient, CommonJson, ct);

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

        public static Result Popcount(string playlistId, CancellationToken ct)
        {
            var args = new RequestArgs { Id = playlistId };
            return Send(RequestKind.Popcount, args, ct);
        }

        /// <summary>The REST arm of profile resolution. Only 200 and 404 are ANSWERS: a 404 seals "no public profile",
        /// and every other status means we did not find out.</summary>
        public static Result Profile(string username, CancellationToken ct)
        {
            var args = new RequestArgs { Id = username };
            return Send(RequestKind.Profile, args, ct);
        }

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

        /// <summary>Autoplay for a context that ran out. <paramref name="podcast"/> picks the `/autopodcast` twin.</summary>
        public static Result Autoplay(byte[] body, bool podcast, CancellationToken ct)
        {
            var args = new RequestArgs { Body = body, Flag = podcast };
            return Send(RequestKind.Autoplay, args, ct);
        }

        // ── 11. playlists, the rootlist and recents ──────────────────────────────────────────────────────────────────

        /// <summary>A revision on the wire is <c>{counter},{hex}</c> — the 4-byte big-endian counter, a comma, and the
        /// 20-byte hash as lowercase hex. It goes into a QUERY STRING, so the comma must be percent-encoded or the
        /// gateway answers 509; passing it through <see cref="PathWriter.AppendEscaped"/> is what guarantees that.</summary>
        public static int FormatRevision(ReadOnlySpan<byte> revision, Span<char> into)
        {
            if (revision.Length < 5) return 0;
            int counter = (revision[0] << 24) | (revision[1] << 16) | (revision[2] << 8) | revision[3];
            if (!counter.TryFormat(into, out int written)) return 0;
            if (written + 1 + (revision.Length - 4) * 2 > into.Length) return 0;
            into[written++] = ',';
            written += Hex.Encode(revision[4..], into[written..]);
            return written;
        }

        /// <summary>The 8 bytes a CREATE sends as its base revision: four zeroes and ASCII "root". Never stored.</summary>
        public static ReadOnlySpan<byte> CreateBaseRevision => [0, 0, 0, 0, (byte)'r', (byte)'o', (byte)'o', (byte)'t'];

        /// <summary>A membership item id: 8 random bytes as 16 lowercase hex characters. Minted by the CLIENT, which
        /// is why a freshly added row already has an addressable uid before the server has heard of it.</summary>
        public static string NewItemId()
        {
            Span<byte> bytes = stackalloc byte[8];
            RandomNumberGenerator.Fill(bytes);
            Span<char> hex = stackalloc char[16];
            return new string(hex[..Hex.Encode(bytes, hex)]);
        }

        /// <summary>A client-minted playlist id: 16 random bytes as 22 base62 characters. There is no create ENDPOINT
        /// any more — a create is a `/changes` POST to an id we invented — so the uri exists before the request does.</summary>
        public static string NewPlaylistId()
        {
            Span<byte> bytes = stackalloc byte[16];
            RandomNumberGenerator.Fill(bytes);
            UInt128 value = UInt128.Zero;
            foreach (byte b in bytes) value = (value << 8) | b;
            Span<char> id = stackalloc char[Base62.GidChars];
            return new string(id[..Base62.Encode(value, id)]);
        }

        public static Result Playlist(string playlistId, CancellationToken ct)
        {
            var args = new RequestArgs { Id = playlistId };
            return Send(RequestKind.PlaylistRead, args, ct);
        }

        /// <summary>The revision-gated diff. 304 means "you are current"; 509 means the revision was mis-encoded and
        /// the caller must fall back to a full read.</summary>
        public static Result PlaylistDiff(string playlistId, ReadOnlySpan<byte> revision, CancellationToken ct)
        {
            Span<char> formatted = stackalloc char[64];
            int length = FormatRevision(revision, formatted);
            if (length == 0) return Playlist(playlistId, ct);
            var args = new RequestArgs { Id = playlistId, Id2 = formatted[..length] };
            return Send(RequestKind.PlaylistDiff, args, ct);
        }

        public static Result PlaylistChanges(string playlistId, byte[] body, CancellationToken ct)
        {
            var args = new RequestArgs { Id = playlistId, Body = body };
            return Send(RequestKind.PlaylistChanges, args, ct);
        }

        public static Result PlaylistCreate(string playlistId, byte[] body, CancellationToken ct)
        {
            var args = new RequestArgs { Id = playlistId, Body = body };
            return Send(RequestKind.PlaylistCreate, args, ct);
        }

        public static Result PlaylistSignals(string playlistId, byte[] body, CancellationToken ct)
        {
            var args = new RequestArgs { Id = playlistId, Body = body };
            return Send(RequestKind.PlaylistSignals, args, ct);
        }

        public static Result Rootlist(string username, CancellationToken ct)
        {
            var args = new RequestArgs { Id = username };
            return Send(RequestKind.RootlistRead, args, ct);
        }

        public static Result RootlistChanges(string username, byte[] body, CancellationToken ct)
        {
            var args = new RequestArgs { Id = username, Body = body };
            return Send(RequestKind.RootlistChanges, args, ct);
        }

        public static Result Recents(CancellationToken ct)
        {
            var args = default(RequestArgs);
            return Send(RequestKind.RecentsPage, args, ct);
        }

        public static Result RecentsDiff(CancellationToken ct)
        {
            var args = default(RequestArgs);
            return Send(RequestKind.RecentsDiff, args, ct);
        }

        /// <summary>Upload a playlist cover, hop one. The image service is not one of D's hosts and there is no kind
        /// for it — the ONE absolute url in this file, sent through the same stamping as everything else.</summary>
        public static Result CoverUpload(byte[] jpeg, CancellationToken ct)
        {
            const HeaderSet headers = HeaderSet.Bearer | HeaderSet.ClientToken | HeaderSet.Identity
                | HeaderSet.AcceptJson | HeaderSet.ContentJson;
            return SendOnce(Verb.Post, ImageUploadHost + "/v4/playlist", headers, RequestKind.Custom, jpeg, "", ct,
                contentType: "image/jpeg");
        }

        /// <summary>Hop two: register the upload token against the playlist.</summary>
        public static Result CoverRegister(string playlistId, string uploadToken, CancellationToken ct)
        {
            var buffer = new ArrayBufferWriter<byte>(96);
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteString("uploadToken", uploadToken);
                w.WriteEndObject();
            }
            Span<char> path = stackalloc char[128];
            var p = new PathWriter(path);
            p.Append("/playlist/v2/playlist/");
            p.AppendEscaped(playlistId);
            p.Append("/register-image");
            var args = new RequestArgs
            {
                Path = p.Written,
                Host = ApiHost.SpclientWg,
                Verb = Verb.Post,
                Headers = CommonJson | HeaderSet.ContentJson,
                Body = buffer.WrittenSpan,
            };
            return Send(RequestKind.Custom, args, ct);
        }

        /// <summary>A collaborator invite link. The permission object MUST be nested — a flat body is a 400.</summary>
        public static Result PlaylistInvite(string playlistId, long ttlMs, CancellationToken ct)
        {
            var buffer = new ArrayBufferWriter<byte>(96);
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteStartObject("permission");
                w.WriteString("permissionLevel", "CONTRIBUTOR");
                w.WriteEndObject();
                w.WriteNumber("ttlMs", ttlMs);
                w.WriteEndObject();
            }
            Span<char> path = stackalloc char[128];
            var p = new PathWriter(path);
            p.Append("/playlist-permission/v1/playlist/");
            p.AppendEscaped(playlistId);
            p.Append("/permission-grant");
            var args = new RequestArgs
            {
                Path = p.Written,
                Host = ApiHost.SpclientWg,
                Verb = Verb.Post,
                Headers = CommonJson | HeaderSet.ContentJson,
                Body = buffer.WrittenSpan,
            };
            return Send(RequestKind.Custom, args, ct);
        }

        /// <summary>Public/private. The body is two bytes: <c>08 01</c> BLOCKED (private), <c>08 02</c> VIEWER.</summary>
        public static Result PlaylistVisibility(string playlistId, bool isPublic, CancellationToken ct)
        {
            Span<char> path = stackalloc char[128];
            var p = new PathWriter(path);
            p.Append("/playlist-permission/v1/playlist/");
            p.AppendEscaped(playlistId);
            p.Append("/permission/base/level");
            ReadOnlySpan<byte> body = isPublic ? [0x08, 0x02] : [0x08, 0x01];
            var args = new RequestArgs
            {
                Path = p.Written,
                Host = ApiHost.Spclient,
                Verb = Verb.Post,
                Headers = HeaderSet.Bearer | HeaderSet.ClientToken | HeaderSet.Identity | HeaderSet.AcceptLanguage
                    | HeaderSet.ContentProtobuf | HeaderSet.AcceptProtobuf,
                Body = body,
            };
            return Send(RequestKind.Custom, args, ct);
        }

        // ── 12. the collection (liked songs, saved albums, followed artists, shows, pins) ─────────────────────────────

        /// <summary>The collection-v2 set name one library edge lives in. The service's own spelling, not ours: four
        /// of the five kinds share the name "collection" and are told apart by the item's uri.</summary>
        public static string WireSet(LibraryEdgeKind set) => set switch
        {
            LibraryEdgeKind.FollowedArtists => "artist",
            LibraryEdgeKind.SavedShows => "show",
            LibraryEdgeKind.Pins => "ylpin",
            _ => "collection",
        };

        /// <summary>The episodes set has no <see cref="LibraryEdgeKind"/> of its own (Wave 1 folds "listen later" into
        /// the show edges); the wire name is kept here so the page that wants it does not re-invent the string.</summary>
        public const string ListenLaterSet = "listenlater";

        public const int CollectionPageSize = 300;

        public static Result CollectionPage(byte[] body, CancellationToken ct)
        {
            var args = new RequestArgs { Body = body };
            return Send(RequestKind.CollectionPage, args, ct);
        }

        public static Result CollectionDelta(byte[] body, CancellationToken ct)
        {
            var args = new RequestArgs { Body = body };
            return Send(RequestKind.CollectionDelta, args, ct);
        }

        public static Result CollectionWrite(byte[] body, CancellationToken ct)
        {
            var args = new RequestArgs { Body = body };
            return Send(RequestKind.CollectionWrite, args, ct);
        }

        // ── 13. storage resolve (the CDN mirrors for one audio file) ─────────────────────────────────────────────────

        /// <summary>Where the bytes of <paramref name="fileIdHex"/> live. Format-agnostic — the file id IS the key —
        /// and the answer is a `StorageResolveResponse` with a mirror list and a TTL. <see cref="Audio"/> is the
        /// caller, and the only one.</summary>
        public static Result StorageResolve(string fileIdHex, CancellationToken ct)
        {
            var args = new RequestArgs { Id = fileIdHex };
            return Send(RequestKind.StorageResolve, args, ct);
        }
    }
}
