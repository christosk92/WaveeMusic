using System;
using System.Collections.Generic;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>Page-published overrides for the shell masthead band. Route-keyed: an entry for a non-active route is
/// simply never rendered. No owner tokens — Publish is last-write-wins per route identity.
/// <para>Deps rule: any value a published closure captures must be a UseEffect dep (including paging cursors).</para></summary>
public sealed record ShellMastheadState(string? Title, string? Caption,
    bool ToolsVisible = false, bool ToolsLoading = false, Action? ToolsAction = null);

/// <summary>Bounded LRU of per-route masthead publications. One <see cref="Version"/> signal; the band reads
/// <see cref="For"/> for the active route.</summary>
public sealed class ShellMastheadStore
{
    public const int Capacity = 16;
    public readonly Signal<int> Version = new(0);

    readonly Dictionary<string, ShellMastheadState> _map = new(StringComparer.Ordinal);
    readonly List<string> _lru = [];

    static string KeyOf(string name, string? arg) => name + "\u001F" + (arg ?? "");

    public void Publish(string name, string? arg, ShellMastheadState state)
    {
        string k = KeyOf(name, arg);
        _lru.Remove(k);
        _lru.Add(k);
        _map[k] = state;
        while (_map.Count > Capacity && _lru.Count > 0)
        {
            string old = _lru[0];
            _lru.RemoveAt(0);
            _map.Remove(old);
        }
        Version.Value++;
    }

    public ShellMastheadState? For(string name, string? arg)
    {
        _ = Version.Value;
        return _map.TryGetValue(KeyOf(name, arg), out var s) ? s : null;
    }
}

/// <summary>The shell-owned MASTHEAD channel. The shell mounts ONE <c>ShellMastheadBand</c> above the content card
/// and provides this store; pages publish dynamics with one deps-leg effect.</summary>
public static class ShellMasthead
{
    public static readonly Context<ShellMastheadStore?> Slot = new(null);
}
