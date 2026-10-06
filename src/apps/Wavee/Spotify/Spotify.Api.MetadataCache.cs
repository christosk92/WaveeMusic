using Google.Protobuf;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Api
    {
        static readonly MetadataCache s_metadataCache = new();

        public static void InvalidateMetadata(string uri) => s_metadataCache.Invalidate(uri);

        /// <summary>The memory governor's shed (Platform/Residency.Pins.cs): keep at most <paramref name="targetBytes"/>
        /// of the extension cache. Returns the estimated bytes released. Never blocks (see <see cref="MetadataCache.Trim"/>).</summary>
        public static long TrimMetadata(long targetBytes) => s_metadataCache.Trim(targetBytes);

        /// <summary>Account-scoped extension cache. One request at a time runs through it (the send gate), so concurrent
        /// overlapping requests coalesce: the second finds what the first fetched. Each response is rebuilt with
        /// authoritative payloads, including per-entity 304 answers.
        ///
        /// <para><b>TWO LOCKS.</b> The send gate is held across the network round trip; the entry gate only around reads
        /// and writes of the table. The governor's <see cref="Trim"/> runs on the UI thread and takes only the entry gate
        /// (and never waits for it), so a slow answer can never freeze the window.</para>
        ///
        /// <para>BODIES (Platform/Bodies.cs). The wire answer is parsed and dropped right here, so it is always LENT and
        /// never becomes an array: these answers run to 2.2 MB (the logged <c>fetch.xm</c> max, an artist batch). The
        /// rebuilt answer is lent too when the caller holds a scope (the fetch provider does) — the caller only decodes
        /// it — and is one exact array otherwise.</para></summary>
        public sealed class MetadataCache
        {
            public const int MaxEntitiesPerRequest = 300;

            /// <summary>The bytes the cache may hold. It is a second copy of answers the entity graph already decoded
            /// (and the disk store persisted), worth keeping for the ETag round trip and the short TTL reuse — not worth
            /// the 4,096 parsed entries the count cap allowed, which ran from 4 MB of tracks to well past 100 MB of artist
            /// pages (an artist answer is 3-70 KB, and every one past 85 KB was its own large-object array).</summary>
            public const long DefaultBudgetBytes = 20L << 20;

            /// <summary>What a PARSED entry holds beyond its wire size, measured (MetadataCacheTests'
            /// <c>The_estimate_covers_what_a_parsed_entry_really_holds</c>): the message, header and Any objects, the
            /// ByteString and its array header, and the uri / etag / type-url strings at two bytes a character instead of
            /// one. A constant ~400 B at every payload size from 100 B to 70 KB, with a 36-character uri; the uri's own
            /// second byte per character is charged separately. So a small track answer really costs ~3x its wire size
            /// and an artist page ~1x.</summary>
            public const int ParsedShellBytes = 368;

            /// <summary>What the cache's own bookkeeping costs per entry: the entry record, the LRU node, the dictionary
            /// slot (at its load factor) and the cloned group header.</summary>
            public const int TableEntryBytes = 224;

            /// <summary>THE per-entry charge: the wire size plus <see cref="ParsedShellBytes"/>, the uri's UTF-16 second
            /// byte and <see cref="TableEntryBytes"/>.</summary>
            public static long EstimateBytes(Xm.EntityExtensionData data, Xm.EntityExtensionDataArrayHeader? header)
                => data.CalculateSize() + (header?.CalculateSize() ?? 0) + (data.EntityUri?.Length ?? 0) + ParsedShellBytes + TableEntryBytes;

            readonly object _sendGate = new();
            readonly object _gate = new();
            readonly Dictionary<(string Uri, Xm.ExtensionKind Kind), LinkedListNode<Entry>> _entries = new();
            // Least recently USED at the tail: a fresh hit, a 304 refresh and a new answer all move an entry to the head,
            // and eviction takes from the tail. The old count cap removed whatever `Dictionary` enumerated first, which
            // after the first removal is a REUSED slot: the newest entry went first and the oldest ones stayed forever.
            readonly LinkedList<Entry> _lru = new();
            readonly long _budget;
            long _bytes;
            string _scope = "";
            sealed record Entry((string Uri, Xm.ExtensionKind Kind) Key, Xm.EntityExtensionData Data,
                Xm.EntityExtensionDataArrayHeader? Header, long FreshUntil, long Bytes);

            public MetadataCache(long budgetBytes = DefaultBudgetBytes) => _budget = Math.Max(0, budgetBytes);

            /// <summary>Estimated bytes held (<see cref="EstimateBytes"/> per entry).</summary>
            public long Bytes { get { lock (_gate) return _bytes; } }

            /// <summary>Entries held.</summary>
            public int Count { get { lock (_gate) return _entries.Count; } }

            /// <summary>The charge recomputed from the entries themselves — what <see cref="Bytes"/> must always equal.</summary>
            public long RecountBytes()
            {
                lock (_gate)
                {
                    long sum = 0;
                    foreach (var entry in _lru) sum += entry.Bytes;
                    return sum;
                }
            }

            /// <summary>Is the entity's answer for this kind held, and is it the most recently used entry?</summary>
            public bool Holds(string uri, Xm.ExtensionKind kind, out bool mostRecent)
            {
                lock (_gate)
                {
                    bool held = _entries.TryGetValue((uri, kind), out var node);
                    mostRecent = held && ReferenceEquals(_lru.First, node);
                    return held;
                }
            }

            public void Invalidate(string uri)
            {
                lock (_gate)
                    foreach (var key in _entries.Keys.Where(key => key.Uri == uri).ToArray())
                        Remove(key);
            }

            /// <summary>Shed the least recently used entries until at most <paramref name="targetBytes"/> remain (the
            /// memory governor's call). Returns the estimated bytes released. An evicted entry only costs a full answer
            /// instead of a 304 the next time it is asked for; nothing on screen reads this cache.
            /// <para>NEVER WAITS: it runs on the UI thread, so when a request is touching the table right now it returns 0
            /// and the next poll sheds instead.</para></summary>
            public long Trim(long targetBytes)
            {
                if (!Monitor.TryEnter(_gate, 0)) return 0;
                try
                {
                    long before = _bytes;
                    EvictTo(Math.Max(0, targetBytes));
                    return before - _bytes;
                }
                finally { Monitor.Exit(_gate); }
            }

            void EvictTo(long target)
            {
                while (_bytes > target && _lru.Last is { } coldest) Remove(coldest.Value.Key);
            }

            void Remove((string Uri, Xm.ExtensionKind Kind) key)
            {
                if (!_entries.Remove(key, out var node)) return;
                _lru.Remove(node);
                _bytes -= node.Value.Bytes;
            }

            void Put((string Uri, Xm.ExtensionKind Kind) key, Xm.EntityExtensionData data, Xm.EntityExtensionDataArrayHeader? header, long freshUntil)
            {
                Remove(key);
                long bytes = EstimateBytes(data, header);
                _entries[key] = _lru.AddFirst(new Entry(key, data, header, freshUntil, bytes));
                _bytes += bytes;
            }

            bool TryGet((string Uri, Xm.ExtensionKind Kind) key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Entry? entry)
            {
                if (_entries.TryGetValue(key, out var node)) { entry = node.Value; return true; }
                entry = null;
                return false;
            }

            void Touch((string Uri, Xm.ExtensionKind Kind) key)
            {
                if (!_entries.TryGetValue(key, out var node) || ReferenceEquals(_lru.First, node)) return;
                _lru.Remove(node);
                _lru.AddFirst(node);
            }

            void Clear()
            {
                _entries.Clear();
                _lru.Clear();
                _bytes = 0;
            }

            public Result Execute(byte[] body, string account, long nowMs, Func<byte[], Result> send)
            {
                var request = Xm.BatchedEntityRequest.Parser.ParseFrom(body);
                string scope = account + "\n" + request.Header?.Country + "\n" + request.Header?.Catalogue;
                lock (_sendGate)
                {
                    var answer = new Xm.BatchedExtensionResponse();
                    var groups = new Dictionary<Xm.ExtensionKind, Xm.EntityExtensionDataArray>();
                    var missing = new List<Xm.EntityRequest>();
                    var hits = new List<(Xm.ExtensionKind Kind, Xm.EntityExtensionData Data, Xm.EntityExtensionDataArrayHeader? Header)>();
                    // The payload every conditional ask leans on, captured with its ETag: a 304 is rebuilt from THIS, so an
                    // eviction (the governor's trim) between the ask and the answer cannot drop the entity from the answer.
                    var held = new Dictionary<(string Uri, Xm.ExtensionKind Kind), Entry>();
                    lock (_gate)
                    {
                        if (_scope != scope) { Clear(); _scope = scope; }
                        foreach (var entity in request.EntityRequest)
                        {
                            var ask = new Xm.EntityRequest { EntityUri = entity.EntityUri };
                            var seen = new HashSet<Xm.ExtensionKind>();
                            foreach (var query in entity.Query)
                            {
                                if (!seen.Add(query.ExtensionKind)) continue;
                                var key = (entity.EntityUri, query.ExtensionKind);
                                if (TryGet(key, out Entry? cached) && cached.FreshUntil > nowMs)
                                {
                                    Touch(key);
                                    hits.Add((query.ExtensionKind, cached.Data, cached.Header));
                                }
                                else
                                {
                                    var condition = query.Clone();
                                    if (cached?.Data.Header?.Etag is { Length: > 0 } etag)
                                    {
                                        condition.Etag = etag;
                                        held[key] = cached;
                                    }
                                    ask.Query.Add(condition);
                                }
                            }
                            if (ask.Query.Count > 0) missing.Add(ask);
                        }
                    }
                    foreach (var hit in hits) Add(hit.Kind, hit.Data, hit.Header);

                    for (int offset = 0; offset < missing.Count; offset += MaxEntitiesPerRequest)
                    {
                        var batch = new Xm.BatchedEntityRequest { Header = request.Header?.Clone() };
                        int end = Math.Min(missing.Count, offset + MaxEntitiesPerRequest);
                        for (int i = offset; i < end; i++) batch.EntityRequest.Add(missing[i]);
                        Xm.BatchedExtensionResponse returned;
                        using (Bodies.Lend())
                        {
                            Result result = send(batch.ToByteArray());   // outside the entry gate: Trim never waits on the wire
                            if (!result.Ok) return result.Owned();
                            returned = Xm.BatchedExtensionResponse.Parser.ParseFrom(result.Bytes);
                        }
                        var answered = new List<(Xm.ExtensionKind Kind, Xm.EntityExtensionData Data, Xm.EntityExtensionDataArrayHeader? Header)>();
                        lock (_gate)
                        {
                            foreach (var group in returned.ExtendedMetadata)
                            {
                                foreach (var item in group.ExtensionData)
                                {
                                    var key = (item.EntityUri, group.ExtensionKind);
                                    int status = item.Header is { HasStatusCode: true } h ? h.StatusCode : 200;
                                    var data = item;
                                    if (status == 304)
                                    {
                                        if (!held.TryGetValue(key, out Entry? basis) && !TryGet(key, out basis)) continue;
                                        data = basis.Data.Clone();
                                        if (item.Header is { } changed)
                                        {
                                            data.Header ??= new Xm.EntityExtensionDataHeader();
                                            if (changed.HasEtag) data.Header.Etag = changed.Etag;
                                            if (changed.HasCacheTtlInSeconds) data.Header.CacheTtlInSeconds = changed.CacheTtlInSeconds;
                                        }
                                        data.Header!.StatusCode = 200;
                                    }
                                    long ttl = item.Header is { HasCacheTtlInSeconds: true } entityHeader
                                        ? entityHeader.CacheTtlInSeconds : group.Header?.CacheTtlInSeconds ?? 0;
                                    if ((status is >= 200 and < 300 || status == 304) && data.ExtensionData is not null)
                                    {
                                        long freshness = nowMs + Math.Clamp(ttl, 0, (long.MaxValue - nowMs) / 1000) * 1000;
                                        Put(key, data.Clone(), group.Header?.Clone(), freshness);
                                    }
                                    answered.Add((group.ExtensionKind, data, group.Header));
                                }
                            }
                        }
                        foreach (var a in answered) Add(a.Kind, a.Data, a.Header);
                    }
                    // After the answer is built (it holds its own clones), so a batch bigger than the budget still
                    // answers in full and only the cache forgets.
                    lock (_gate) EvictTo(_budget);
                    return Bodies.Lending ? Result.Lent(200, answer) : new Result(200, answer.ToByteArray());

                    void Add(Xm.ExtensionKind kind, Xm.EntityExtensionData data, Xm.EntityExtensionDataArrayHeader? header)
                    {
                        if (!groups.TryGetValue(kind, out var group))
                        {
                            group = new Xm.EntityExtensionDataArray { ExtensionKind = kind, Header = header?.Clone() };
                            groups.Add(kind, group); answer.ExtendedMetadata.Add(group);
                        }
                        group.ExtensionData.Add(data.Clone());
                    }
                }
            }
        }
    }
}
