using System.Text.Json.Serialization;

namespace Wavee.UI.WinUI.Data.Models;

/// <summary>
/// User-selectable caching aggressiveness level. Scales LRU and hot-cache
/// capacities across the whole app to trade memory against network refetches
/// and browsing latency.
///
/// <para>
/// <b>Medium</b> matches the legacy hard-coded defaults exactly, so users who
/// do not change the setting see no behavioural change from the previous build.
/// </para>
///
/// <para>
/// Serialized as a string ("Low" / "Medium" / "High" / "VeryAggressive") in
/// settings.json via <see cref="JsonStringEnumConverter{T}"/>, so users who
/// open the file see readable values.
/// </para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CachingProfile>))]
public enum CachingProfile
{
    /// <summary>Aggressive memory savings. Caches are ~20% of Medium.</summary>
    Low = 0,

    /// <summary>Legacy defaults. ~120 MB of cache capacity across the app.</summary>
    Medium = 1,

    /// <summary>Prefer cache hits. ~280 MB of cache capacity.</summary>
    High = 2,

    /// <summary>Maximum cache hit rate. ~650 MB of cache capacity. Recommended for 16 GB+ machines.</summary>
    VeryAggressive = 3,
}
