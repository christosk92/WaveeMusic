using System;
using System.Collections.Generic;
using Wavee.Backend;
using Wavee.Backend.Catalog;
using Wavee.Backend.Persistence;

namespace Wavee.Tests;

/// <summary>Owns every worker used by synchronous curation decision fixtures.</summary>
public sealed class VideoOverrideTestHost : IDisposable
{
    public DataCommitQueue Queue { get; } = new();
    public MemoryVideoOverridePersistence Persistence { get; } = new();
    public VideoOverrideService Service(IReadOnlyList<VideoOverride>? initial = null)
        => new(Queue, Persistence, initial ?? Persistence.LoadAsync().AsTask().GetAwaiter().GetResult()) { FileExists = _ => true };
    public static VideoOverride Attach(VideoOverrideService service, string uri, string path)
        => service.AttachAsync(uri, path).GetAwaiter().GetResult();
    public static bool Remove(VideoOverrideService service, string uri) => service.RemoveAsync(uri).GetAwaiter().GetResult();
    public static void Reload(VideoOverrideService service) => service.ReloadAsync().GetAwaiter().GetResult();
    public void Dispose() => Queue.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
