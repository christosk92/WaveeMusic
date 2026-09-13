// Engine-free except FluentGpu.Signals.Signal (Wavee.Tests has a value-cell shim). No Element/Component/Context.
using System;
using System.Collections.Generic;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Features.Browse;
using Wavee.Features.Concerts;

namespace Wavee;

/// <summary>Parent crumb captured at <c>Go</c> time — the journey answer, keyed by destination route identity.
/// Not part of <see cref="FluentGpu.Controls.Route"/> (that would fork keep-alive slot identity).</summary>
public readonly record struct NavOrigin(string Label, string RouteName, string? RouteArg);

/// <summary>Session-local origins, LRU 32, keyed <c>name␟arg</c>. Back/Forward do not write — the arrival's origin stands.</summary>
public sealed class NavOriginStore
{
    public const int Capacity = 32;
    public readonly Signal<int> Version = new(0);

    readonly Dictionary<string, NavOrigin> _map = new(StringComparer.Ordinal);
    readonly List<string> _lru = [];

    static string KeyOf(string name, string? arg) => name + "\u001F" + (arg ?? "");

    /// <summary>Latest arrival wins, including a null that clears the entry (deterministic overwrite).</summary>
    public void Write(string name, string? arg, NavOrigin? origin)
    {
        string k = KeyOf(name, arg);
        if (origin is null)
        {
            if (_map.Remove(k)) _lru.Remove(k);
        }
        else
        {
            Touch(k);
            _map[k] = origin.Value;
            while (_map.Count > Capacity && _lru.Count > 0)
            {
                string old = _lru[0];
                _lru.RemoveAt(0);
                _map.Remove(old);
            }
        }
        Version.Value++;
    }

    public NavOrigin? For(string name, string? arg)
    {
        _ = Version.Value;
        return _map.TryGetValue(KeyOf(name, arg), out var o) ? o : null;
    }

    /// <summary>Lookup without subscribing — session serialize / restore.</summary>
    public NavOrigin? Peek(string name, string? arg)
        => _map.TryGetValue(KeyOf(name, arg), out var o) ? o : null;

    public void Restore(string name, string? arg, NavOrigin? origin)
    {
        if (origin is null) return;
        string k = KeyOf(name, arg);
        Touch(k);
        _map[k] = origin.Value;
    }

    void Touch(string k)
    {
        _lru.Remove(k);
        _lru.Add(k);
    }
}

/// <summary>Route-family → static title + trail. The band's ONE predicate: unknown families collapse.
/// Concerts resolve here with zero page code (defect F).</summary>
static class ShellMastheadRegistry
{
    public static bool TryResolve(string name, string? arg, string? liveTitle, NavOrigin? origin,
        out string? title, out IReadOnlyList<DrillCrumb> trail)
    {
        title = null;
        trail = [];
        if (BrowseRoutes.IsHome(name) || BrowseRoutes.Is(name) || BrowseSectionRoutes.Is(name)
            || HomeSectionRoutes.Is(name) || ConcertRoutes.Is(name))
        {
            string? live = string.IsNullOrWhiteSpace(liveTitle) ? null : liveTitle.Trim();
            trail = DrillTrail.Of(name, arg, live, origin);
            title = live ?? (trail.Count > 0 ? trail[^1].Label : StaticTitle(name, arg));
            return !string.IsNullOrWhiteSpace(title);
        }
        return false;
    }

    public static string? StaticTitle(string name, string? arg)
    {
        if (BrowseRoutes.IsHome(name)) return Loc.Get(Strings.Browse.Title);
        if (ConcertRoutes.TryParse(name, out var c))
            return c.Kind switch
            {
                ConcertRouteKind.ArtistSchedule => arg is { Length: > 0 } ? arg : Loc.Get(Strings.Concerts.Title),
                ConcertRouteKind.Detail => arg is { Length: > 0 } ? arg : Loc.Get(Strings.Concerts.Title),
                _ => Loc.Get(Strings.Concerts.Title),
            };
        if (!string.IsNullOrWhiteSpace(arg)) return arg.Trim();
        if (BrowseRoutes.Is(name) || BrowseSectionRoutes.Is(name)) return Loc.Get(Strings.Browse.HomeTitle);
        if (HomeSectionRoutes.Is(name)) return Loc.Get(Strings.Nav.Home);
        return null;
    }
}
