using System.Globalization;
using System.Text;

namespace Wavee.Core.Catalog;

// Catalog state replacement: these keys identify provider answers, not screens or the amount a screen can paint.
public sealed record CatalogScope(
    string Provider, string ProviderAccount, string Locale, string Market, string Catalogue,
    int Tier, bool ExplicitFilter, bool ContextKnown = true, string StorageAccount = "default");

public enum FacetKind
{
    TrackIdentity = 1, EpisodeIdentity = 2, AlbumIdentity = 3, ArtistIdentity = 4,
    PlaylistHeader = 5, ShowIdentity = 6, UserIdentity = 7,
    PlayCount = 10, Descriptors = 11, AudioAttributes = 12, Publishing = 13,
    Availability = 14, VideoAssociation = 15, VisualIdentity = 16,
    AlbumDetail = 20, ArtistOverview = 21, EpisodeDetail = 22,
    AlbumTracks = 30, ArtistDiscography = 31, ArtistPopular = 32, ArtistAppearsOn = 33,
    ShowEpisodes = 34, AlbumVersions = 35, ArtistRelated = 36,
    Home = 40, Search = 41, HomeSection = 42, SearchSuggestions = 43, PlaylistRevision = 44, ExtensionDocument = 45,
}

/// <summary>Finite, typed transport arguments. The persisted codec is versioned and collision-free.</summary>
public readonly record struct ResourceArguments(int Offset = 0, int Limit = 0, string? Cursor = null,
    string? Filter = null, string? Sort = null)
{
    public string ToStorageKey()
    {
        var text = new StringBuilder("1|");
        text.Append(Offset.ToString(CultureInfo.InvariantCulture)).Append('|');
        text.Append(Limit.ToString(CultureInfo.InvariantCulture)).Append('|');
        Append(text, Cursor); Append(text, Filter); Append(text, Sort);
        return text.ToString();
    }

    public static ResourceArguments FromStorageKey(string value)
    {
        int position = 0;
        int Number(char separator)
        {
            int end = value.IndexOf(separator, position);
            if (end < 0 || !int.TryParse(value.AsSpan(position, end - position), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int number)) throw new FormatException("Invalid resource arguments.");
            position = end + 1;
            return number;
        }
        string? Text()
        {
            int length = Number(':');
            if (length == -1) return null;
            if (length < 0 || length > value.Length - position) throw new FormatException("Invalid resource argument length.");
            var text = value.Substring(position, length);
            position += length;
            return text;
        }
        if (Number('|') != 1) throw new FormatException("Unsupported resource argument version.");
        int offset = Number('|'), limit = Number('|');
        var result = new ResourceArguments(offset, limit, Text(), Text(), Text());
        if (position != value.Length) throw new FormatException("Trailing resource argument data.");
        return result;
    }

    static void Append(StringBuilder text, string? value)
    {
        if (value is null) { text.Append("-1:"); return; }
        text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
    }
}

public readonly record struct ResourceKey(CatalogScope Scope, string Subject, FacetKind Facet,
    ResourceArguments Arguments = default);

public static class CatalogSubjects
{
    public const string Home = "wavee:catalog:home";
    public static string Search(string query) => "wavee:catalog:search:" + Uri.EscapeDataString(query);
}

public enum Knowledge : byte { Unknown, Present, Absent, Unsupported }
public enum ResourceActivity : byte { Idle, Queued, Fetching, Backoff, Offline }
public enum CatalogProvenance : byte { Provider, InlineSeed, ImportedUnknownContext }
public enum ResourcePriority : byte { Prefetch, Visible, Playback }
public enum ResourceErrorKind : byte { Transport, Decode, Forbidden, RateLimited, Persistence, InvalidResponse }

public sealed record ResourceError(ResourceErrorKind Kind, string Message, int? StatusCode = null);

public readonly record struct RequestStamp(CatalogScope Scope, string ActualAccount, long Epoch,
    long Generation, long RequestId);

/// <summary>A durable answer. Activity, errors, generations and epochs deliberately do not live on disk.</summary>
public sealed record CatalogRecord(ResourceKey Key, Knowledge Knowledge, CatalogValue? Value,
    CatalogProvenance Provenance, DateTimeOffset FetchedAt, DateTimeOffset ExpiresAt, long Revision);

public sealed record ResourceSnapshot(ResourceKey Key, Knowledge Knowledge, CatalogValue? Value,
    CatalogProvenance Provenance, ResourceActivity Activity, ResourceError? Error,
    DateTimeOffset FetchedAt, DateTimeOffset ExpiresAt, long Revision, long Generation,
    DateTimeOffset? RetryAt = null)
{
    public bool IsFresh(DateTimeOffset now) => Key.Scope.ContextKnown
        && Provenance == CatalogProvenance.Provider
        && Knowledge is Knowledge.Present or Knowledge.Absent && now < ExpiresAt;

    public static ResourceSnapshot Unknown(ResourceKey key, long generation = 0) => new(key, Knowledge.Unknown,
        null, CatalogProvenance.Provider, ResourceActivity.Idle, null, default, default, 0, generation);
}

public enum CatalogChangeKind : byte { Durable, ColdRead, Activity, Session }

/// <summary>One catalog publication. <c>Confirmed</c> is meaningful only for <see cref="CatalogChangeKind.Session"/>:
/// true when the installed session merely CONFIRMED the scope the app was already serving (same provider, provider
/// account and storage account, and an otherwise identical scope), so every resident answer stays valid and only the
/// keys this set actually names moved. A consumer that fans a Session change out to EVERYTHING it owns must do so only
/// while this is false.</summary>
public sealed record CatalogChangeSet(long Revision, IReadOnlyList<ResourceKey> Keys,
    CatalogChangeKind Kind = CatalogChangeKind.Durable, bool Confirmed = false);

public sealed record ResourceRequest(ResourceKey Key, RequestStamp Stamp, ResourcePriority Priority,
    string? ClientFeatureId = null, bool RequiresNetwork = true);

public enum ResourceFetchStatus : byte { Present, NotModified, Absent, Unsupported, Failed }

/// <summary>Only Absent is negative-cacheable; failure can coexist with the last good answer.</summary>
public sealed record ResourceFetchResult(ResourceFetchStatus Status, CatalogPatch? Patch = null,
    TimeSpan? FreshFor = null, ResourceError? Error = null, TimeSpan? RetryAfter = null)
{
    public static ResourceFetchResult Present(CatalogPatch patch, TimeSpan? freshFor = null)
        => new(ResourceFetchStatus.Present, patch, freshFor);
    public static ResourceFetchResult Absent(TimeSpan? freshFor = null)
        => new(ResourceFetchStatus.Absent, FreshFor: freshFor);
    public static ResourceFetchResult Failed(ResourceError error, TimeSpan? retryAfter = null)
        => new(ResourceFetchStatus.Failed, Error: error, RetryAfter: retryAfter);
}

public sealed record CatalogSeed(ResourceKey Key, CatalogPatch Patch);
public sealed record CatalogObservation(ResourceKey Key, CatalogPatch Patch, bool FillUnknownOnly = false);
public sealed record CatalogTransportRecord(CatalogScope Scope, string Subject, int ExtensionKind,
    string? Etag, byte[] Payload, DateTimeOffset StoredAt);

/// <summary>Raw conditional-request bytes only. This port never decides resource freshness.</summary>
public interface ICatalogTransportReader
{
    ValueTask<CatalogTransportRecord?> ReadTransportAsync(CatalogScope scope, string subject, int extensionKind,
        CancellationToken ct);
}

public sealed record ResourceResponse(ResourceRequest Request, ResourceFetchResult Result,
    IReadOnlyList<CatalogSeed>? Seeds = null, IReadOnlyList<CatalogTransportRecord>? Transports = null);

public interface ICatalogResourceProvider
{
    string Provider { get; }
    bool RequiresNetwork(ResourceKey key) => true;
    /// <summary>Only compatible requests share one worker turn. Unbatchable endpoints return a key-specific group.</summary>
    string BatchGroup(ResourceKey key) => "metadata";
    ValueTask<IReadOnlyList<ResourceResponse>> FetchAsync(IReadOnlyList<ResourceRequest> requests,
        CancellationToken ct);
}

public enum ResourceEnsureStatus : byte { Ready, Absent, Unsupported, Failed, Superseded, Deferred }
public sealed record ResourceEnsureResult(ResourceKey Key, ResourceEnsureStatus Status, ResourceSnapshot Snapshot);

/// <summary>Finite demand requests. Reading snapshots or subscribing to changes never invokes this port.</summary>
public interface IResourceCoordinator
{
    Task<IReadOnlyList<ResourceEnsureResult>> EnsureAsync(IReadOnlyList<ResourceKey> keys,
        ResourcePriority priority = ResourcePriority.Visible, bool force = false,
        CancellationToken ct = default, Func<ResourceKey, CancellationToken>? waiterCancellation = null);
    Task WaitForCapacityAsync(CancellationToken ct = default);
    Task InvalidateAsync(IReadOnlyList<ResourceKey> keys, CancellationToken ct = default);
}
