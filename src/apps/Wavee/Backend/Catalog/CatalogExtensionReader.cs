using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Core.Catalog;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Backend.Catalog;

/// <summary>Finite document reads through catalog scheduling and its sole retained transport bytes.
/// Parsing has no memo, negative table, scheduler, or write side effect.</summary>
public sealed class CatalogExtensionReader
{
    readonly CatalogRepository _catalog;
    readonly IResourceCoordinator _resources;
    readonly ICatalogTransportReader _transport;
    readonly Func<string, CatalogScope> _scope;

    public CatalogExtensionReader(CatalogRepository catalog, IResourceCoordinator resources,
        ICatalogTransportReader transport, Func<string, CatalogScope> scope)
    { _catalog = catalog; _resources = resources; _transport = transport; _scope = scope; }

    public async Task<T?> ReadAsync<T>(string uri, Xm.ExtensionKind kind, Func<ByteString, T?> parse,
        CancellationToken ct = default) where T : class
    {
        var documents = await ReadDocumentsAsync([(uri, kind)], ct).ConfigureAwait(false);
        return documents.TryGetValue((uri, kind), out var payload) ? parse(payload) : null;
    }

    public async Task<IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), ByteString>> ReadDocumentsAsync(
        IReadOnlyList<(string Uri, Xm.ExtensionKind Kind)> requested, CancellationToken ct = default,
        IReadOnlyList<ResourceKey>? dependencies = null)
    {
        long epoch = _catalog.Epoch;
        var pairs = requested.Distinct().Select(item => (Item: item, Key: Key(item.Uri, item.Kind))).ToArray();
        await _resources.EnsureAsync(pairs.Select(pair => pair.Key).Concat(dependencies ?? []).Distinct().ToArray(), ct: ct).ConfigureAwait(false);
        if (_catalog.Epoch != epoch) throw new OperationCanceledException("Catalog session changed during document read.");
        var result = new Dictionary<(string Uri, Xm.ExtensionKind Kind), ByteString>();
        foreach (var pair in pairs)
        {
            var snapshot = _catalog.Peek(pair.Key);
            if (snapshot.Knowledge is Knowledge.Absent or Knowledge.Unsupported) continue;
            if (snapshot.Knowledge != Knowledge.Present)
                throw new InvalidOperationException(snapshot.Error?.Message ?? "Catalog document is unavailable.");
            var bytes = await _transport.ReadTransportAsync(pair.Key.Scope, pair.Item.Uri, (int)pair.Item.Kind, ct).ConfigureAwait(false);
            if (_catalog.Epoch != epoch) throw new OperationCanceledException("Catalog session changed during document read.");
            if (bytes is null) throw new InvalidOperationException("Catalog document has no retained transport payload.");
            if (snapshot.Value is ExtensionDocumentValue pointer && !string.Equals(pointer.Etag, bytes.Etag, StringComparison.Ordinal))
                throw new InvalidOperationException("Catalog document changed during read; retry the read.");
            result.Add(pair.Item, ByteString.CopyFrom(bytes.Payload));
        }
        return result;
    }

    ResourceKey Key(string uri, Xm.ExtensionKind kind)
    {
        FacetKind? facet = kind switch
        {
            Xm.ExtensionKind.TrackV4 => FacetKind.TrackIdentity,
            Xm.ExtensionKind.EpisodeV4 => FacetKind.EpisodeIdentity,
            Xm.ExtensionKind.AlbumV4 => FacetKind.AlbumIdentity,
            Xm.ExtensionKind.ArtistV4 => FacetKind.ArtistIdentity,
            Xm.ExtensionKind.ShowV4 => FacetKind.ShowIdentity,
            Xm.ExtensionKind.VideoAssociations => FacetKind.VideoAssociation,
            Xm.ExtensionKind.AudioAttributesV2 => FacetKind.AudioAttributes,
            _ => null,
        };
        return new(_scope(uri), uri, facet ?? FacetKind.ExtensionDocument,
            facet is null ? new ResourceArguments(Filter: ((int)kind).ToString(CultureInfo.InvariantCulture)) : default);
    }
}
