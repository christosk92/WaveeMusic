using System;
using Wavee.Core.Catalog;

namespace Wavee.Backend.Catalog;

/// <summary>
/// The engine-free half of "a network session just installed": did it CONFIRM the catalog the app is already serving,
/// or REPLACE it?
///
/// <para>A launch that recalls the account and scope of its last session (see <c>LocalSessionScopeStore</c>) serves the
/// user's cached library immediately, under exactly the keys the live session will use. When the AP welcome then names
/// the same account and the same context, nothing about the data changed — only the protocol epoch moved and the
/// connection opened. Resetting on that (reloading every replica from SQLite, re-keying every resident answer, re-joining
/// every leased query node) is pure loss: it blanks a correct sidebar for the length of a reload. Every rule that has to
/// tell the two cases apart lives here, so it is decided once, the same way, and unit-tested without a catalog.</para>
/// </summary>
public static class SessionInstallRules
{
    /// <summary>True when the installing session belongs to the SAME owner as the one already in place — the same
    /// provider, provider account and storage account, both actually named. That is exactly the set of fields the
    /// durable replicas are keyed by, so the resident replica baselines stay this owner's and may be re-stamped with the
    /// new generation instead of reloaded. Result-shaping fields (locale, market, catalogue, tier, explicit filter) may
    /// differ: they never move ownership.</summary>
    /// <param name="online">Whether the install brings the catalog online. Only an install that DOES can confirm
    /// anything: a same-scope install that takes the catalog offline changes the reachability of every key it owns,
    /// which is a real transition and must travel like one.</param>
    public static bool ConfirmsOwner(CatalogScope previous, string previousAccount, CatalogScope next, string nextAccount,
        bool online)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        return online
            && previousAccount is { Length: > 0 } && nextAccount is { Length: > 0 }
            && string.Equals(previousAccount, nextAccount, StringComparison.Ordinal)
            && string.Equals(previous.Provider, next.Provider, StringComparison.Ordinal)
            && string.Equals(previous.ProviderAccount, next.ProviderAccount, StringComparison.Ordinal)
            && string.Equals(previous.StorageAccount, next.StorageAccount, StringComparison.Ordinal);
    }

    /// <summary>True when the installing session confirms the owner AND lands on the very same scope — so every resident
    /// <c>ResourceKey</c> (the scope is part of it) still addresses the same answer and nothing has to be re-read. A
    /// session that keeps the owner but reshapes the context (the user's market or tier changed since the last launch)
    /// is NOT this: those rows live under different keys and every node must re-join.</summary>
    public static bool ConfirmsScope(CatalogScope previous, string previousAccount, CatalogScope next, string nextAccount,
        bool online)
        => ConfirmsOwner(previous, previousAccount, next, nextAccount, online) && previous == next;
}

/// <summary>
/// How far one catalog publication travels through the query graph. A <see cref="CatalogChangeKind.Session"/> change
/// normally re-joins EVERY leased node without consulting its dependencies, because a replaced session invalidates
/// answers a node cannot know it was holding. A CONFIRMING session invalidates nothing: it names only the keys that
/// actually moved (an error cleared, an "unsupported" answer that is now askable), so the ordinary dependency test is
/// both sufficient and — with a warm cache, where that key set is usually empty — free.
/// </summary>
public static class SessionFanOutRules
{
    /// <summary>Whether this publication must re-join every leased node regardless of what it depends on.</summary>
    public static bool RecomputesEveryNode(CatalogChangeKind kind, bool confirmed)
        => kind == CatalogChangeKind.Session && !confirmed;

    /// <summary>Whether ONE node re-joins for this publication. <paramref name="dependsOnKeys"/> is the node's own
    /// answer to "do I read any of the keys this set names?".</summary>
    public static bool Recomputes(CatalogChangeKind kind, bool confirmed, bool dependsOnKeys)
        => RecomputesEveryNode(kind, confirmed) || dependsOnKeys;
}
