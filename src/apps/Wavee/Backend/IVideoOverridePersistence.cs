using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Backend;

/// <summary>Device curation, independent of provider metadata. Writes run through DataCommitQueue.</summary>
public interface IVideoOverridePersistence
{
    ValueTask<IReadOnlyList<VideoOverride>> LoadAsync(CancellationToken ct = default);
    ValueTask WriteAsync(VideoOverride value, CancellationToken ct = default);
    ValueTask DeleteAsync(string uri, CancellationToken ct = default);
}
