// ── Spotify/Spotify.Books.cs — the podcast/audiobook pages' GraphQL reads and the audiobook-hours allowance ──────────
//
// Role: SHELL (the sends, the caches, the watch) + CORE (the decoders)
// Lane: podcast-pages-data (2026-11-17). Wire: C:/WAVEE/scratch/abook/audiobook-api-map.md §4.6, §10.1–§10.5.
//
// 1. The web player's three audiobook GraphQL ops (pathfinder, the same host/auth as home and search):
//    - queryShowMetadataV2 → the CATALOG parts that are whole groups stage into the show's columns (Price, Access,
//      Authors, Genres, exactly what kinds 64/31/178/83 fill: Spotify.Decode.Book.cs). The rest is RETURN-ONLY
//      (BookMeta): the viewer's own rating and canRate, the narrators, saved, the trailer's playedState. Rating and Credits
//      are NOT staged: kind 37 / kind 52 own those groups whole, and a partial GraphQL row would blank the title split
//      or the listener's stars. NOTE: its `publisher.name` is the AUTHOR on a book (the capture: "Laurie Gilmore" both
//      places), the same mislabel as ShowV4.66, so it is never read as the publisher.
//    - queryBookChapters → per-chapter playability and playedState (return-only, BookChapters).
//    - similarAudiobooks → "More like this": thin show rows staged (title, cover, the author as byline, the audiobook
//      bit) plus the uri list (return-only, SimilarBooks).
//    Each is cached per (scope epoch, op, uri) for 5 minutes; `Changed` ticks when an answer lands.
//
// 2. capping-api PutConsumption — the AudiobookSubscription allowance (12 h a month, the period, the refill). The body is
//    the official 1.3.8.620 client's, byte for byte (capture sessions 72/628 and 587/597): feature ids
//    [02 01 03 07 05 06 0b] at session start, [01 07 05 06 0b] when an audiobook chapter starts, then `2 {1: 3600}` and
//    `4: 1`. Wavee sends it at those two moments only and never anything else: no consumption value of its own. The
//    answer carries the ALLOWANCE, never hours used or left (§10.2): nothing here estimates them.
//
// THREADING (C9): every send blocks and runs on Api.RunAsync/Run; signals are written on the UI thread via Spotify.Post.

using System.Text;
using System.Text.Json;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Api
    {
        /// <summary>The web player's audiobook ops (the bbbb.saz capture, 2026-11-17).</summary>
        public static class BookQueries
        {
            public static readonly Query ShowMetadata = new("queryShowMetadataV2",
                "b475447846f37cc426add800b995a7859c169c53c72b62de6338c0c40a5dacea", true);
            public static readonly Query Chapters = new("queryBookChapters",
                "8f342d1c624755901657fa65cbb80dd3bacbcca2f6d802f570ae3269d59a403e", true);
            public static readonly Query Similar = new("similarAudiobooks",
                "d62da1bbf5859e5a156da1f52ce21a73a642f925db717a179fd89626b27a80b8", true);

            /// <summary>The page size the web player asks.</summary>
            public const int ChapterPage = 50;

            public static Result ShowMetadataQuery(string showUri, CancellationToken ct)
            {
                var vars = new Vars(ShowMetadata);
                vars.W.WriteString("uri", showUri);
                vars.W.WriteBoolean("includeContentCapabilityTrait", false);
                vars.W.WriteBoolean("includeEpisodeContentRatingsV2", true);
                return Pathfinder(ShowMetadata, vars.Finish(), ct);
            }

            public static Result ChaptersQuery(string showUri, int offset, CancellationToken ct)
            {
                var vars = new Vars(Chapters);
                vars.W.WriteString("uri", showUri);
                vars.W.WriteNumber("offset", offset);
                vars.W.WriteNumber("limit", ChapterPage);
                return Pathfinder(Chapters, vars.Finish(), ct);
            }

            public static Result SimilarQuery(string showUri, CancellationToken ct)
            {
                var vars = new Vars(Similar);
                vars.W.WriteString("uri", showUri);
                return Pathfinder(Similar, vars.Finish(), ct);
            }
        }
    }

    /// <summary>Return-only book data and the allowance. Catalog facts still enter through Staging (file header).</summary>
    public static partial class Books
    {
        public sealed record BookMeta(string Uri, bool Saved, float RatingAverage, int RatingCount, bool ShowAverage,
            int MyRating, bool CanRate, string[] Narrators, bool Included, string Signifier, string TrailerUri,
            int TrailerPositionMs, string TrailerState, bool PreRelease, int Status)
        {
            public bool Ok => Status is >= 200 and < 300;
        }

        /// <summary>One chapter as queryBookChapters states it. <see cref="State"/> is the wire's
        /// (<c>NOT_STARTED</c>, <c>IN_PROGRESS</c>, <c>COMPLETED</c>); <see cref="Reason"/> e.g. <c>PAYMENT_REQUIRED</c>.</summary>
        public sealed record ChapterState(string Uri, string Name, int DurationMs, int PositionMs, string State,
            bool Playable, string Reason, bool Paywalled);

        public sealed record BookChapters(ChapterState[] Items, int Total, int Status)
        {
            public bool Ok => Status is >= 200 and < 300;
        }

        public sealed record SimilarBooks(string[] Uris, int Total, int Status)
        {
            public bool Ok => Status is >= 200 and < 300;
        }

        /// <summary>The AudiobookSubscription block of a PutConsumption answer. The ALLOWANCE for the period, never a balance.</summary>
        public sealed record Allowance(long AllowanceMs, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd,
            DateTimeOffset Refill, DateTimeOffset QuotaValidTo, string Entitlement);

        /// <summary>Ticks (UI thread) whenever a book answer lands or is dropped. Readers of the stores subscribe to it.</summary>
        public static readonly Signal<int> Changed = new(0) { DebugName = "books.changed" };

        /// <summary>The latest allowance (UI thread), null until an answer names the AudiobookSubscription feature.</summary>
        public static readonly Signal<Allowance?> AllowanceSignal = new(null) { DebugName = "books.allowance" };

        const long CacheMs = 5 * 60_000;
        static readonly Lock Gate = new();
        static readonly Dictionary<(uint Epoch, string Op, string Uri), (long At, object Value)> Cache = [];
        static readonly HashSet<(uint Epoch, string Op, string Uri)> InFlight = [];

        // ── the reads (UI thread; subscribe to Changed) ──

        public static BookMeta? MetaOf(string uri) => Peek<BookMeta>("meta", uri);
        public static BookChapters? ChaptersOf(string uri) => Peek<BookChapters>("chapters", uri);
        public static SimilarBooks? SimilarOf(string uri) => Peek<SimilarBooks>("similar", uri);

        static T? Peek<T>(string op, string uri) where T : class
        {
            _ = Changed.Value;
            uint epoch = Entities.ScopeEpoch.Value;
            lock (Gate) return Cache.TryGetValue((epoch, op, uri), out var hit) ? hit.Value as T : null;
        }

        /// <summary>Start the three GraphQL reads for a book page (idempotent: a fresh cache entry or a read in flight is
        /// not repeated). <paramref name="similar"/> false skips "more like this" (a podcast page).</summary>
        public static void Ask(string showUri, bool chapters = true, bool similar = true)
        {
            if (Platform.Args.Fake || string.IsNullOrEmpty(showUri)) return;
            Start("meta", showUri, static (uri, scope, ct) => FetchMeta(uri, scope, ct));
            if (chapters) Start("chapters", showUri, static (uri, scope, ct) => FetchChapters(uri, ct));
            if (similar) Start("similar", showUri, static (uri, scope, ct) => FetchSimilar(uri, scope, ct));
        }

        /// <summary>Put an answer in the store (the fixture loader's door, and the fetches' own landing). UI thread.</summary>
        public static void Put(string op, string uri, object value)
        {
            uint epoch = Entities.ScopeEpoch.Peek();
            lock (Gate)
            {
                if (Cache.Count >= 256) Cache.Clear();
                Cache[(epoch, op, uri)] = (Environment.TickCount64, value);
            }
            Changed.Value = Changed.Peek() + 1;
        }

        static void Start(string op, string uri, Func<string, Scope, CancellationToken, object> fetch)
        {
            Scope scope = Entities.Current;
            uint epoch = Entities.ScopeEpoch.Peek();
            var key = (epoch, op, uri);
            lock (Gate)
            {
                if (Cache.TryGetValue(key, out var hit) && Environment.TickCount64 - hit.At < CacheMs) return;
                if (!InFlight.Add(key)) return;
            }
            bool queued = Api.Run(() =>
            {
                object? value = null;
                try
                {
                    using var account = Api.ForAccount(scope.Key.Account);
                    if (ReferenceEquals(scope, Entities.Current)) value = fetch(uri, scope, CancellationToken.None);
                }
                catch (Exception ex) { Log.Warn("books", op + " failed", ex); }
                Post(() =>
                {
                    lock (Gate) InFlight.Remove(key);
                    if (value is not null && Entities.ScopeEpoch.Peek() == epoch) Put(op, uri, value);
                });
            });
            if (!queued) lock (Gate) InFlight.Remove(key);
        }

        static object FetchMeta(string uri, Scope scope, CancellationToken ct)
        {
            var result = Api.BookQueries.ShowMetadataQuery(uri, ct);
            if (!result.Ok) return new BookMeta(uri, false, 0, 0, false, 0, false, [], false, "", "", 0, "", false, result.Status);
            byte[] body = result.Body;
            Land(scope, s => Decode.BookShowMetadata(body, s));
            return DecodeMeta(body, uri);
        }

        static object FetchChapters(string uri, CancellationToken ct)
        {
            var all = new List<ChapterState>();
            int total = 0, status = 200;
            for (int offset = 0, guard = 0; guard < 40; guard++)
            {
                var result = Api.BookQueries.ChaptersQuery(uri, offset, ct);
                if (!result.Ok) { status = result.Status; break; }
                var page = DecodeChapters(result.Body);
                all.AddRange(page.Items);
                total = page.Total;
                if (page.Items.Length == 0 || all.Count >= total) break;
                offset = all.Count;
            }
            return new BookChapters(all.ToArray(), total, all.Count > 0 ? 200 : status);
        }

        static object FetchSimilar(string uri, Scope scope, CancellationToken ct)
        {
            var result = Api.BookQueries.SimilarQuery(uri, ct);
            if (!result.Ok) return new SimilarBooks([], 0, result.Status);
            byte[] body = result.Body;
            Land(scope, s => Decode.SimilarAudiobooks(body, s));
            return DecodeSimilar(body);
        }

        /// <summary>Decode into a pooled staging on this (api) thread, commit it on the UI thread, hand it to the store's
        /// write-behind like any fetch answer (the LandPreRelease shape).</summary>
        internal static void Land(Scope scope, Action<Staging> decode)
        {
            Staging s = Staging.Rent();
            s.Epoch = scope.Epoch;
            try { decode(s); }
            catch (Exception ex) { Log.Warn("books", "decode failed", ex); Staging.Return(s); return; }
            Post(() =>
            {
                bool owned = false;
                try
                {
                    Entities.Commit(s);
                    Entities.Publish();
                    owned = Store.WriteBehind(s);
                }
                finally { if (!owned) Staging.Return(s); }
            });
        }

        // ── the GraphQL decoders (pure; JSON) ──

        public static BookMeta DecodeMeta(byte[] json, string uri)
        {
            using var doc = JsonDocument.Parse(json);
            var b = Path(doc.RootElement, "data", "podcastUnionV2");
            if (b.ValueKind != JsonValueKind.Object) return new BookMeta(uri, false, 0, 0, false, 0, false, [], false, "", "", 0, "", false, 502);
            var rating = Path(b, "rating");
            var avg = Path(rating, "averageRating");
            var narrators = new List<string>();
            foreach (var n in Items(Path(b, "narrators")))
                if (Str(n, "name") is { Length: > 0 } name) narrators.Add(name);
            var trailer = Path(b, "trailerV2", "data");
            var played = Path(trailer, "playedState");
            return new BookMeta(
                Str(b, "uri") is { Length: > 0 } u ? u : uri,
                Bool(b, "saved"),
                (float)Num(avg, "average"), (int)Num(avg, "totalRatings"), Bool(avg, "showAverage"),
                (int)Num(Path(rating, "rating"), "rating"), Bool(rating, "canRate"),
                narrators.ToArray(),
                Bool(Path(b, "accessInfo"), "isUserMemberOfAtLeastOneGroup"),
                Str(Path(b, "accessInfo", "signifier"), "text"),
                Str(Path(b, "trailerV2"), "_uri") is { Length: > 0 } t ? t : Str(trailer, "uri"),
                (int)Num(played, "playPositionMilliseconds"), Str(played, "state"),
                Bool(b, "isPreRelease"), 200);
        }

        public static BookChapters DecodeChapters(byte[] json)
        {
            using var doc = JsonDocument.Parse(json);
            var page = Path(doc.RootElement, "data", "podcastUnionV2", "chaptersV2");
            var rows = new List<ChapterState>();
            foreach (var item in Items(Path(page, "items")))
            {
                var c = Path(item, "entity", "data");
                string uri = Str(c, "uri") is { Length: > 0 } u ? u : Str(Path(item, "entity"), "_uri");
                if (uri.Length == 0) continue;
                var played = Path(c, "playedState");
                var playability = Path(c, "playability");
                rows.Add(new ChapterState(uri, Str(c, "name"), (int)Num(Path(c, "duration"), "totalMilliseconds"),
                    (int)Num(played, "playPositionMilliseconds"), Str(played, "state"),
                    playability.ValueKind != JsonValueKind.Object || Bool(playability, "playable"),
                    Str(playability, "reason"), Bool(Path(c, "restrictions"), "paywallContent")));
            }
            return new BookChapters(rows.ToArray(), (int)Num(page, "totalCount"), 200);
        }

        public static SimilarBooks DecodeSimilar(byte[] json)
        {
            using var doc = JsonDocument.Parse(json);
            var page = Path(doc.RootElement, "data", "seoRecommendedAudiobook");
            var uris = new List<string>();
            foreach (var item in Items(Path(page, "items")))
                if (Str(Path(item, "data"), "uri") is { Length: > 0 } u) uris.Add(u);
            return new SimilarBooks(uris.ToArray(), (int)Num(page, "totalCount"), 200);
        }

        // ── the allowance: capping-api PutConsumption ──

        const string CappingRoute = "/capping-api/spotify.cappingapi.v1.CappingApi/PutConsumption";

        /// <summary>The two request bodies the official client sends, byte for byte (file header §2).</summary>
        public static readonly byte[] SessionBody = [0x0a, 0x07, 0x02, 0x01, 0x03, 0x07, 0x05, 0x06, 0x0b, 0x12, 0x03, 0x08, 0x90, 0x1c, 0x20, 0x01];
        public static readonly byte[] ChapterBody = [0x0a, 0x05, 0x01, 0x07, 0x05, 0x06, 0x0b, 0x12, 0x03, 0x08, 0x90, 0x1c, 0x20, 0x01];

        public enum CappingMoment : byte { SessionStart, ChapterStart }

        /// <summary>Send PutConsumption as the official client does at <paramref name="moment"/>, and publish the allowance.
        /// Fake mode never sends (its allowance comes from a fixture).</summary>
        public static void RefreshAllowance(CappingMoment moment)
        {
            if (Platform.Args.Fake) return;
            Scope scope = Entities.Current;
            if (scope is null || scope.Key.Account.Length == 0) return;
            byte[] body = moment == CappingMoment.SessionStart ? SessionBody : ChapterBody;
            Api.Run(() =>
            {
                Api.Result result;
                using (Api.ForAccount(scope.Key.Account))
                    result = Api.PostEncoded(CappingRoute, ApiHost.Spclient,
                        HeaderSet.Bearer | HeaderSet.ClientToken | HeaderSet.Identity | HeaderSet.AcceptLanguage | HeaderSet.AcceptProtobuf,
                        body, "application/x-protobuf", null, CancellationToken.None);
                if (!result.Ok) { Log.Info("books", "capping status=" + result.Status); return; }
                var allowance = DecodeAllowance(result.Body);
                Post(() => { if (ReferenceEquals(scope, Entities.Current)) AllowanceSignal.Value = allowance; });
            });
        }

        /// <summary>A PutConsumption answer → the AudiobookSubscription block (feature id 1 or its name), or null. Tolerates a
        /// gRPC 5-byte frame. Fields: <c>1 {1 feature id, 3 {1 s, 2 ns} refill, 4 {1 start, 2 end, 4 allowance ms,
        /// 6 entitlement}, 5 quota validTo, 8 name}</c> (map §4.6, §10.1).</summary>
        public static Allowance? DecodeAllowance(ReadOnlySpan<byte> body)
        {
            if (body.Length >= 5 && body[0] == 0 && (body[1] << 24 | body[2] << 16 | body[3] << 8 | body[4]) == body.Length - 5)
                body = body[5..];
            Allowance? found = null;
            for (var r = new Decode.ProtoReader(body); r.Next();)
            {
                if (r.Field != 1 || r.Wire != 2) { r.Skip(); continue; }
                long feature = 0, allowance = 0;
                DateTimeOffset refill = default, start = default, end = default, validTo = default;
                string name = "", entitlement = "";
                for (var f = r.Message(); f.Next();)
                {
                    switch (f.Field)
                    {
                        case 1 when f.Wire == 0: feature = (long)f.Varint(); break;
                        case 3 when f.Wire == 2: refill = Stamp(f.Message()); break;
                        case 4 when f.Wire == 2:
                            for (var q = f.Message(); q.Next();)
                            {
                                if (q.Field == 1 && q.Wire == 2) start = Stamp(q.Message());
                                else if (q.Field == 2 && q.Wire == 2) end = Stamp(q.Message());
                                else if (q.Field == 4 && q.Wire == 0) allowance = (long)q.Varint();
                                else if (q.Field == 6 && q.Wire == 2) entitlement = Encoding.UTF8.GetString(q.Bytes());
                                else q.Skip();
                            }
                            break;
                        case 5 when f.Wire == 2: validTo = Stamp(f.Message()); break;
                        case 8 when f.Wire == 2: name = Encoding.UTF8.GetString(f.Bytes()); break;
                        default: f.Skip(); break;
                    }
                }
                bool book = name == "Spotify.Feature.AudiobookSubscription" || (name.Length == 0 && feature == 1);
                if (book && allowance > 0) found = new Allowance(allowance, start, end, refill, validTo, entitlement);
            }
            return found;
        }

        static DateTimeOffset Stamp(Decode.ProtoReader ts)
        {
            long seconds = ts.Varint(1);
            long nanos = ts.Varint(2);
            if (seconds <= 0) return default;
            try { return DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(nanos / 100); }
            catch (ArgumentOutOfRangeException) { return default; }
        }

        // ── the moments: session start (Telemetry.HydrateProgress) and an audiobook chapter starting ──

        static ReactiveRuntime? s_runtime;
        static Effect? s_chapterWatch;   // never read: holds the app-lifetime watch
        static bool s_flushPosted;
        static EntityId s_lastChapter;
        static readonly Action s_flush = static () => { s_flushPosted = false; s_runtime?.Flush(); };

        /// <summary>Start the chapter-start watch. Idempotent; App.cs calls it beside Library.Install.</summary>
        internal static void Install()
        {
            if (s_runtime is not null) return;
            s_runtime = new ReactiveRuntime { FrameRequested = static () => { if (s_flushPosted) return; s_flushPosted = true; Post(s_flush); } };
            s_chapterWatch = new Effect(s_runtime, WatchChapter);
        }

        /// <summary>A new playable that is an audiobook chapter, playing (not idle) → the in-play PutConsumption. Once per
        /// chapter start: the same chapter again (a pause, a seek) sends nothing.</summary>
        static void WatchChapter()
        {
            var id = Playback.CurrentId.Value;
            var phase = Playback.PhaseSignal.Value;
            if (id.IsEmpty || phase == Playback.Phase.Idle) return;
            if (id.Equals(s_lastChapter)) return;
            var scope = Entities.Current;
            if (scope is null || !scope.Episodes.TryGetSlot(id, out int slot)) return;
            if ((scope.Episodes.Flags[slot] & (uint)EpisodeFlags.AudiobookChapter) == 0) return;
            s_lastChapter = id;
            RefreshAllowance(CappingMoment.ChapterStart);
        }

        /// <summary>Session start: the official client's first PutConsumption. Called once per scope by
        /// <c>Telemetry.HydrateProgress</c>.</summary>
        internal static void OnSessionStart(Scope scope)
        {
            if (!ReferenceEquals(scope, Entities.Current)) return;
            s_lastChapter = default;
            AllowanceSignal.Value = null;
            RefreshAllowance(CappingMoment.SessionStart);
        }

        // ── JSON helpers ──

        static JsonElement Path(JsonElement e, params ReadOnlySpan<string> names)
        {
            foreach (var n in names)
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(n, out e)) return default;
            }
            return e;
        }

        static JsonElement.ArrayEnumerator Items(JsonElement e)
            => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : default;

        static string Str(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        static bool Bool(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        static double Num(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    }

    public static partial class Decode
    {
        /// <summary>queryShowMetadataV2 → the groups it states WHOLE, staged exactly as kinds 64 / 31 / 178 / 83 stage them
        /// (Price, Access, Authors, Genres+Topics). Rating and Credits stay with their own kinds (Spotify.Books header).</summary>
        public static void BookShowMetadata(ReadOnlySpan<byte> json, Staging s)
        {
            var reader = new Utf8JsonReader(json);
            using var doc = JsonDocument.ParseValue(ref reader);
            if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("podcastUnionV2", out var b)
                || b.ValueKind != JsonValueKind.Object) return;
            string uri = b.TryGetProperty("uri", out var u) ? u.GetString() ?? "" : "";
            if (!uri.StartsWith("spotify:show:", StringComparison.Ordinal)) return;
            var id = Identity(s, Encoding.UTF8.GetBytes(uri));
            if (id.IsEmpty) return;

            if (b.TryGetProperty("price", out var price) && price.ValueKind == JsonValueKind.Object)
            {
                ref var row = ref s.Shows.RowFor(id, Authority.Full, (uint)ShowFields.Price);
                row.PriceFinal = s.AddText(Encoding.UTF8.GetBytes(MoneyOf(price, "finalPrice")));
                row.PriceList = s.AddText(Encoding.UTF8.GetBytes(MoneyOf(price, "finalListPrice")));
                s.Shows.Settle();
            }
            if (b.TryGetProperty("accessInfo", out var access) && access.ValueKind == JsonValueKind.Object
                && access.TryGetProperty("signifier", out var sig) && sig.ValueKind == JsonValueKind.Object)
            {
                ref var row = ref s.Shows.RowFor(id, Authority.Full, (uint)ShowFields.Access);
                row.Signifier = s.AddText(Encoding.UTF8.GetBytes(sig.TryGetProperty("text", out var t) ? t.GetString() ?? "" : ""));
                s.Shows.Settle();
            }
            if (b.TryGetProperty("authorsV2", out var authors) && authors.ValueKind == JsonValueKind.Array)
            {
                var lines = new StringBuilder();
                foreach (var a in authors.EnumerateArray())
                {
                    string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.Length == 0) continue;
                    string link = a.TryGetProperty("uri", out var au) && au.ValueKind == JsonValueKind.String ? au.GetString() ?? "" : "";
                    if (lines.Length > 0) lines.Append('\n');
                    lines.Append(Clean(name)).Append('\t').Append(Clean(link));
                }
                ref var row = ref s.Shows.RowFor(id, Authority.Full, (uint)ShowFields.Authors);
                row.AuthorLinks = Text(s, lines);
                s.Shows.Settle();
            }
            if (b.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
            {
                var list = new List<(int Level, string Name, int At)>();
                int at = 0;
                foreach (var g in genres.EnumerateArray())
                {
                    string name = g.TryGetProperty("shortName", out var sn) ? sn.GetString() ?? "" : "";
                    int depth = g.TryGetProperty("depth", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 1;
                    if (name.Length > 0) list.Add((Math.Max(1, depth), Clean(name), at++));
                }
                list.Sort(static (x, y) => x.Level != y.Level ? x.Level.CompareTo(y.Level) : x.At.CompareTo(y.At));
                ref var row = ref s.Shows.RowFor(id, Authority.Full,
                    (uint)(list.Count > 0 ? ShowFields.Genres | ShowFields.Topics : ShowFields.Genres));
                if (list.Count > 0)
                {
                    row.Topics = s.AddText(Encoding.UTF8.GetBytes(string.Join('\n', list.Select(g => g.Name))));
                    row.Genres = s.AddText(Encoding.UTF8.GetBytes(string.Join('\n',
                        list.Select(g => g.Level.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\t" + g.Name))));
                }
                s.Shows.Settle();
            }
        }

        static string MoneyOf(JsonElement price, string name)
        {
            if (!price.TryGetProperty(name, out var m) || m.ValueKind != JsonValueKind.Object) return "";
            double amount = m.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDouble() : 0;
            string currency = m.TryGetProperty("currency", out var c) ? c.GetString() ?? "" : "";
            string formatted = m.TryGetProperty("formattedPrice", out var f) ? f.GetString() ?? "" : "";
            return MoneyText(amount, currency, formatted);
        }

        /// <summary>similarAudiobooks → one THIN show row per book: title, 640 cover, the author as the byline (the
        /// <c>Publisher</c> column's meaning on a book, ShowV4.66), and the audiobook bit as a header fact.</summary>
        public static void SimilarAudiobooks(ReadOnlySpan<byte> json, Staging s)
        {
            var reader = new Utf8JsonReader(json);
            using var doc = JsonDocument.ParseValue(ref reader);
            if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("seoRecommendedAudiobook", out var page)
                || !page.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return;
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("data", out var b) || b.ValueKind != JsonValueKind.Object) continue;
                string uri = b.TryGetProperty("uri", out var u) ? u.GetString() ?? "" : "";
                if (!uri.StartsWith("spotify:show:", StringComparison.Ordinal)) continue;
                var id = Identity(s, Encoding.UTF8.GetBytes(uri));
                if (id.IsEmpty) continue;
                string title = b.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string author = "";
                if (b.TryGetProperty("authorsV2", out var authors) && authors.ValueKind == JsonValueKind.Array)
                    foreach (var a in authors.EnumerateArray())
                        if (a.TryGetProperty("name", out var an) && an.GetString() is { Length: > 0 } name) { author = name; break; }
                string image = "";
                int best = 0;
                if (b.TryGetProperty("coverArt", out var cover) && cover.TryGetProperty("sources", out var sources)
                    && sources.ValueKind == JsonValueKind.Array)
                    foreach (var src in sources.EnumerateArray())
                    {
                        int w = src.TryGetProperty("width", out var wv) && wv.ValueKind == JsonValueKind.Number ? wv.GetInt32() : 0;
                        if (w >= best && src.TryGetProperty("url", out var url) && url.GetString() is { Length: > 0 } link) { best = w; image = link; }
                    }
                uint known = (title.Length > 0 ? (uint)ShowFields.Title : 0) | (image.Length > 0 ? (uint)ShowFields.Image : 0)
                           | (author.Length > 0 ? (uint)ShowFields.Publisher : 0);
                ref var row = ref s.Shows.RowFor(id, Authority.Thin, known);
                row.Title = s.AddText(Encoding.UTF8.GetBytes(title));
                row.Image = s.AddText(Encoding.UTF8.GetBytes(image));
                row.Publisher = s.AddText(Encoding.UTF8.GetBytes(author));
                row.HeaderFacts = (uint)ShowFlags.Audiobook;
                s.Shows.Settle();
            }
        }
    }
}
