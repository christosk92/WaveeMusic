using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Backend.Persistence;

public sealed class MemoryVideoOverridePersistence : IVideoOverridePersistence
{
    readonly object _gate = new();
    readonly Dictionary<string, VideoOverride> _rows = new(StringComparer.Ordinal);
    public ValueTask<IReadOnlyList<VideoOverride>> LoadAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult<IReadOnlyList<VideoOverride>>([.. _rows.Values]); }
    public ValueTask WriteAsync(VideoOverride value, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) _rows[value.Uri] = value; return ValueTask.CompletedTask; }
    public ValueTask DeleteAsync(string uri, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) _rows.Remove(uri); return ValueTask.CompletedTask; }
}
