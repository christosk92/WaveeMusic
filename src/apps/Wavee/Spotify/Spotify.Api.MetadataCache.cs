using Google.Protobuf;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Api
    {
        static readonly MetadataCache s_metadataCache = new();

        public static void InvalidateMetadata(string uri) => s_metadataCache.Invalidate(uri);

        /// <summary>Account-scoped extension cache. The gate coalesces concurrent overlapping requests;
        /// each response is rebuilt with authoritative payloads, including per-entity 304 answers.</summary>
        public sealed class MetadataCache
        {
            public const int MaxEntitiesPerRequest = 300;
            const int Capacity = 4096;
            readonly object _gate = new();
            readonly Dictionary<(string Uri, Xm.ExtensionKind Kind), Entry> _entries = new();
            string _scope = "";
            sealed record Entry(Xm.EntityExtensionData Data, Xm.EntityExtensionDataArrayHeader? Header, long FreshUntil);

            public void Invalidate(string uri)
            {
                lock (_gate)
                    foreach (var key in _entries.Keys.Where(key => key.Uri == uri).ToArray())
                        _entries.Remove(key);
            }

            public Result Execute(byte[] body, string account, long nowMs, Func<byte[], Result> send)
            {
                var request = Xm.BatchedEntityRequest.Parser.ParseFrom(body);
                string scope = account + "\n" + request.Header?.Country + "\n" + request.Header?.Catalogue;
                lock (_gate)
                {
                    if (_scope != scope) { _entries.Clear(); _scope = scope; }
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
                            if (_entries.TryGetValue(key, out Entry? cached) && cached.FreshUntil > nowMs)
                                Add(query.ExtensionKind, cached.Data, cached.Header);
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
                        Result result = send(batch.ToByteArray());
                        if (!result.Ok) return result;
                        var returned = Xm.BatchedExtensionResponse.Parser.ParseFrom(result.Body);
                        foreach (var group in returned.ExtendedMetadata)
                        {
                            foreach (var item in group.ExtensionData)
                            {
                                var key = (item.EntityUri, group.ExtensionKind);
                                int status = item.Header is { HasStatusCode: true } h ? h.StatusCode : 200;
                                var data = item;
                                if (status == 304)
                                {
                                    if (!_entries.TryGetValue(key, out Entry? held)) continue;
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
                                    _entries[key] = new Entry(data.Clone(), group.Header?.Clone(), freshness);
                                }
                                Add(group.ExtensionKind, data, group.Header);
                            }
                        }
                    }
                    while (_entries.Count > Capacity)
                    {
                        foreach (var key in _entries.Keys) { _entries.Remove(key); break; }
                    }
                    return new Result(200, answer.ToByteArray());

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
