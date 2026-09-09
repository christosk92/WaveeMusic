using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Backend.Persistence;

public sealed partial class SqliteColdStore : IVideoOverridePersistence
{
    ValueTask<IReadOnlyList<VideoOverride>> IVideoOverridePersistence.LoadAsync(CancellationToken ct)
        => ReadVideoOverridesAsync(ct);
    ValueTask IVideoOverridePersistence.WriteAsync(VideoOverride value, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); WriteVideoOverride(value); return ValueTask.CompletedTask; }
    ValueTask IVideoOverridePersistence.DeleteAsync(string uri, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); DeleteVideoOverride(uri); return ValueTask.CompletedTask; }
}
