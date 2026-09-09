using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.Catalog;

namespace Wavee.Backend.Catalog;

/// <summary>Stable source registration; session installation replaces only the transport target.</summary>
public sealed class SwitchableCatalogResourceProvider : ICatalogResourceProvider
{
    ICatalogResourceProvider _target;
    public string Provider { get; }
    public SwitchableCatalogResourceProvider(string provider, ICatalogResourceProvider initial)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _target = Validate(initial);
    }
    public void SetTarget(ICatalogResourceProvider target) => Volatile.Write(ref _target, Validate(target));
    public bool TrySetTarget(ICatalogResourceProvider expected, ICatalogResourceProvider replacement)
        => ReferenceEquals(Interlocked.CompareExchange(ref _target, Validate(replacement), expected), expected);
    public bool RequiresNetwork(ResourceKey key) => Volatile.Read(ref _target).RequiresNetwork(key);
    public string BatchGroup(ResourceKey key) => Volatile.Read(ref _target).BatchGroup(key);
    public ValueTask<IReadOnlyList<ResourceResponse>> FetchAsync(IReadOnlyList<ResourceRequest> requests, CancellationToken ct)
        => Volatile.Read(ref _target).FetchAsync(requests, ct);
    ICatalogResourceProvider Validate(ICatalogResourceProvider target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Provider != Provider) throw new ArgumentException("A source target cannot change provider identity.", nameof(target));
        return target;
    }
}

public sealed class OfflineCatalogResourceProvider(string provider) : ICatalogResourceProvider
{
    public string Provider { get; } = provider;
    public ValueTask<IReadOnlyList<ResourceResponse>> FetchAsync(IReadOnlyList<ResourceRequest> requests, CancellationToken ct)
        => ValueTask.FromResult<IReadOnlyList<ResourceResponse>>(requests.Select(request => new ResourceResponse(request,
            ResourceFetchResult.Failed(new(ResourceErrorKind.Transport, "The provider session is offline.")))).ToArray());
}
