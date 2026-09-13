using System;
using System.Collections.Generic;
using Wavee.Sdk;

namespace Wavee.Backend.Modules;

// ── "WHO CAN PLAY THIS LINK?" — the cheap prefilter in front of the playback/match RPC ───────────────────────────────
// Spawning a process to ask is expensive; the manifest's `urlPatterns` (host substrings) answer for free. The order the
// prefilter produces IS the ask order, and the first module that returns a MatchResult wins — the same
// "registration order is the routing table" rule MediaProviderRegistry uses.
//
// Pure and static so the whole policy is unit-testable with hand-built manifests and no processes.

/// <summary>Capability tokens a manifest may declare, spelled once.</summary>
public static class ModuleCapabilities
{
    /// <summary>The module resolves playables (every playback module declares it).</summary>
    public const string Playback = "playback";

    /// <summary>The module answers <c>playback/match</c> for pasted text.</summary>
    public const string Match = "match";

    /// <summary>The module pushes live metadata corrections.</summary>
    public const string Metadata = "metadata";

    /// <summary>The module is a catch-all for links nothing else claims — it is always asked LAST.</summary>
    public const string Fallback = "fallback";

    /// <summary>The prefix a manifest uses to declare a host-service permission, e.g. <c>permission:storage.private</c>.
    /// <para>The SDK's <c>ModuleManifest</c> has no separate <c>permissions</c> array, so the permission vocabulary
    /// rides the capability list under this prefix — one declared list, one gate, no second schema to keep in step.</para></summary>
    public const string PermissionPrefix = "permission:";

    /// <summary>Does a manifest declare this capability?</summary>
    /// <param name="manifest">The module's manifest.</param>
    /// <param name="capability">The token to look for.</param>
    public static bool Declares(ModuleManifest manifest, string capability)
    {
        string[] caps = manifest.Capabilities ?? [];
        for (int i = 0; i < caps.Length; i++)
            if (string.Equals(caps[i], capability, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Has this module been granted a host-service permission by its manifest?</summary>
    /// <param name="manifest">The module's manifest.</param>
    /// <param name="permission">The permission name, e.g. <c>storage.private</c> or <c>auth.spotify</c>.</param>
    public static bool HasPermission(ModuleManifest manifest, string permission)
        => Declares(manifest, PermissionPrefix + permission);
}

/// <summary>Turns pasted text into "this module, this playable" — prefilter, ask, resolve, build the Track.</summary>
public sealed class ModuleRouter
{
    /// <summary>
    /// The ask order for one input. Rules, in order:
    /// <list type="number">
    /// <item>a pinned module (the user picked its row in the Play ▸ menu) is the ONLY candidate;</item>
    /// <item>modules whose <c>urlPatterns</c> hit the input's host, in catalog order;</item>
    /// <item>if nothing hit, every module that declares <c>match</c> — with <c>fallback</c> modules last, so the
    ///       catch-all radio module never steals a link a specific module would have claimed.</item>
    /// </list>
    /// </summary>
    /// <param name="modules">The installed modules, in catalog order.</param>
    /// <param name="input">The trimmed user input.</param>
    /// <param name="pinnedModuleId">The module the user pinned, or null.</param>
    public static IReadOnlyList<InstalledModule> Prefilter(
        IReadOnlyList<InstalledModule> modules, string? input, string? pinnedModuleId)
    {
        if (modules is null || modules.Count == 0 || string.IsNullOrWhiteSpace(input)) return [];
        string text = input!.Trim();

        if (pinnedModuleId is { Length: > 0 })
        {
            for (int i = 0; i < modules.Count; i++)
                if (string.Equals(modules[i].Id, pinnedModuleId, StringComparison.OrdinalIgnoreCase)
                    && ModuleCapabilities.Declares(modules[i].Manifest, ModuleCapabilities.Match))
                    return [modules[i]];
            return [];
        }

        string host = HostOf(text);
        var hits = new List<InstalledModule>();
        for (int i = 0; i < modules.Count; i++)
        {
            InstalledModule m = modules[i];
            if (!ModuleCapabilities.Declares(m.Manifest, ModuleCapabilities.Match)) continue;
            if (MatchesPattern(m.Manifest.UrlPatterns, host, text)) hits.Add(m);
        }

        if (hits.Count > 0)
        {
            SortFallbackLast(hits);
            return hits;
        }

        var all = new List<InstalledModule>(modules.Count);
        for (int i = 0; i < modules.Count; i++)
            if (ModuleCapabilities.Declares(modules[i].Manifest, ModuleCapabilities.Match)) all.Add(modules[i]);
        SortFallbackLast(all);
        return all;
    }

    static void SortFallbackLast(List<InstalledModule> list)
    {
        // A stable partition, not a sort: catalog order inside each half is the routing table.
        var head = new List<InstalledModule>(list.Count);
        var tail = new List<InstalledModule>(2);
        for (int i = 0; i < list.Count; i++)
        {
            if (ModuleCapabilities.Declares(list[i].Manifest, ModuleCapabilities.Fallback)) tail.Add(list[i]);
            else head.Add(list[i]);
        }

        list.Clear();
        list.AddRange(head);
        list.AddRange(tail);
    }

    static bool MatchesPattern(string[]? patterns, string host, string text)
    {
        if (patterns is null || patterns.Length == 0) return false;
        for (int i = 0; i < patterns.Length; i++)
        {
            string p = patterns[i];
            if (string.IsNullOrEmpty(p)) continue;
            if (host.Length > 0 && host.Contains(p, StringComparison.OrdinalIgnoreCase)) return true;
            if (host.Length == 0 && text.Contains(p, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>The host of an http(s) url, lower-cased and without credentials/port; "" for anything else. Public so
    /// the prefilter tests can state the exact parsing rule.</summary>
    /// <param name="text">The trimmed input.</param>
    public static string HostOf(string? text)
    {
        if (text is not { Length: > 0 }) return "";
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)) return "";
        if (uri.Scheme is not ("http" or "https")) return "";
        return uri.Host.ToLowerInvariant();
    }

    /// <summary>Is this a bare http(s) url? The Play ▸ "Link…" dialog uses it to decide whether to even try.</summary>
    /// <param name="text">The trimmed input.</param>
    public static bool IsHttpUrl(string? text) => HostOf(text).Length > 0;
}
