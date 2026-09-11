using System;
using FluentGpu.Signals;
using Wavee.Core.Catalog;

namespace Wavee;

/// <summary>Engine-free scope-publication rule extracted from <see cref="Services"/>'s constructor observer
/// (findings 4.2) so it is unit-testable without a UI or a live catalog. <see cref="Publish"/> reads the
/// repository's CURRENT scope at the moment of a <c>CatalogChangeKind.Session</c> publication — never a captured
/// one, because the repository is always ahead of the UI — sets the UI-facing signal, rebinds the scope-scoped UI
/// projections, and logs the flip. Idempotent: a re-publish of the already-current scope is a no-op, so a caller
/// need not de-dupe Session publications itself.
///
/// <para>A launch that recalled its last session's scope is ALREADY serving that scope when the AP welcome lands, so
/// the welcome publishes nothing new — and the one thing a silent no-op would cost is the evidence that the confirm
/// happened at all. The epoch is therefore tracked alongside the scope: an unchanged scope under a new epoch logs
/// <c>confirmed</c> and rebinds nothing (the whole point — the sidebar's handles, the library cells and every leased
/// query stay exactly where they are), while a changed scope logs <c>published</c> and rebinds. The rebind callback
/// receives BOTH scopes so it can tell an account switch from a mere reshaping.</para></summary>
public sealed class CatalogScopePublisher(
    Func<CatalogScope> current, Func<long> epoch, Signal<CatalogScope> signal,
    Action<CatalogScope, CatalogScope> rebind, Action<string, string> log)
{
    public const string PublishedEvent = "catalog.scope.published";
    public const string ConfirmedEvent = "catalog.scope.confirmed";

    long _publishedEpoch = -1;

    public void Publish()
    {
        var scope = current();
        long generation = epoch();
        var previous = signal.Value;
        if (previous == scope)
        {
            // Same scope, new epoch: the network session confirmed what the app was already serving. Say so once per
            // epoch (a Session publication can arrive more than once for one install) and touch nothing.
            if (_publishedEpoch == generation) return;
            _publishedEpoch = generation;
            log(ConfirmedEvent, $"catalog scope confirmed account={scope.ProviderAccount} locale={scope.Locale} " +
                $"epoch={generation} previousAccount={previous.ProviderAccount} rebound=false");
            return;
        }
        _publishedEpoch = generation;
        signal.Value = scope;
        rebind(previous, scope);
        log(PublishedEvent, $"catalog scope published account={scope.ProviderAccount} locale={scope.Locale} " +
            $"epoch={generation} previousAccount={previous.ProviderAccount} " +
            $"reacquire={ScopeRebindRules.RequiresReacquire(previous, scope)}");
    }
}
