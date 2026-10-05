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
        /// of the extension cache. Returns the estimated bytes released.</summary>
        public static long TrimMetadata(long targetBytes) => s_metadataCache.Trim(targetBytes);

        /// <summary>Account-scoped extension cache. The gate coalesces concurrent overlapping requests;
        /// each response is rebuilt with authoritative payloads, including per-entity 304 answers.
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

            /// <summary>What an entry costs beyond its payload: the key tuple, the entry and node objects, the parsed
            /// message shells and the uri string's header.</summary>
            const int EntryOverheadBytes = 256;

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

            /// <summary>Estimated bytes held (payloads plus <see cref="EntryOverheadBytes"/> each).</summary>
            public long Bytes { get { lock (_gate) return _bytes; } }

            /// <summary>Entries held.</summary>
            public int Count { get { lock (_gate) return _entries.Count; } }

            public void Invalidate(string uri)
            {
                lock (_gate)
                    foreach (var key in _entries.Keys.Where(key => key.Uri == uri).ToArray())
                        Remove(key);
            }

            /// <summary>Shed the least recently used entries until at most <paramref name="targetBytes"/> remain (the
            /// memory governor's call). Returns the estimated bytes released. An evicted entry only costs a full answer
            /// instead of a 304 the next time it is asked for; nothing on screen reads this cache.</summary>
            public long Trim(long targetBytes)
            {
                lock (_gate)
                {
                    long before = _bytes;
                    EvictTo(Math.Max(0, targetBytes));
                    return before - _bytes;
                }
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
                long bytes = data.CalculateSize() + (header?.CalculateSize() ?? 0) + key.Uri.Length * 2L + EntryOverheadBytes;
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
                lock (_gate)
                {
                    if (_scope != scope) { Clear(); _scope = scope; }
                    var answer = new Xm.BatchedExtensionResponse();
                    var groups = new Dictionary<Xm.ExtensionKind, Xm.EntityExtensionDataArray>();
                    var missing = new List<Xm.EntityRequest>();
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
                                Add(query.ExtensionKind, cached.Data, cached.Header);
                            }
                            else
                            {
                                var condition = query.Clone();
                                if (cached?.Data.Header?.Etag is { Length: > 0 } etag) condition.Etag = etag;
                                ask.Query.Add(condition);
                            }
                        }
                        if (ask.Query.Count > 0) missing.Add(ask);
                    }
                    for (int offset = 0; offset < missing.Count; offset += MaxEntitiesPerRequest)
                    {
                        var batch = new Xm.BatchedEntityRequest { Header = request.Header?.Clone() };
                        int end = Math.Min(missing.Count, offset + MaxEntitiesPerRequest);
                        for (int i = offset; i < end; i++) batch.EntityRequest.Add(missing[i]);
                        Xm.BatchedExtensionResponse returned;
                        using (Bodies.Lend())
                        {
                            Result result = send(batch.ToByteArray());
                            if (!result.Ok) return result.Owned();
                            returned = Xm.BatchedExtensionResponse.Parser.ParseFrom(result.Bytes);
                        }
                        foreach (var group in returned.ExtendedMetadata)
                        {
                            foreach (var item in group.ExtensionData)
                            {
                                var key = (item.EntityUri, group.ExtensionKind);
                                int status = item.Header is { HasStatusCode: true } h ? h.StatusCode : 200;
                                var data = item;
                                if (status == 304)
                                {
                                    if (!TryGet(key, out Entry? held)) continue;
                                    data = held.Data.Clone();
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
                                Add(group.ExtensionKind, data, group.Header);
                            }
                        }
                    }
                    // After the answer is built (it holds its own clones), so a batch bigger than the budget still
                    // answers in full and only the cache forgets.
                    EvictTo(_budget);
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
