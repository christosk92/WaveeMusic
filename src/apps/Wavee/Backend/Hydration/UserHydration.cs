using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend.Hydration;

// ── the user ladder (design §2.3) ────────────────────────────────────────────────────────────────────────────────────
// The shortest ladder in the app, and the one that deleted the most: a profile is a name + an avatar, so Identity IS
// every rung and there is no second transport above it. What it replaces is `IUserProfileService` — a service that
// owned a private Owner dictionary, a private in-flight map and a `Changed` event, which a READ source then subscribed
// to so it could `store.Bump()` the playlists that referenced the owner. That was a read path writing to the store to
// fake a change notification for data the store did not hold. Now the owner IS store data: `UpsertOwner` bumps its own
// canonical uri under the batch's bulk scope, and every byline repaints off `IStore.Changes` like everything else.
//
// The transport half is `IUserProfileFetch` (kind-15 batch + the per-user REST remainder) — one seam, one parser, and
// the negative memo behind the batch arm is the shared one, so a user the wire has already said "no" for stops costing
// a slot in every later page's prefetch.
public sealed class UserHydration : IKindHydration
{
    readonly IStore _store;
    readonly IUserProfileFetch _fetch;

    public UserHydration(IStore store, IUserProfileFetch fetch)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
    }

    public EntityKind Kind => EntityKind.User;

    /// <summary>A resident, named owner is Full — <see cref="HydrationLevels.Of(Owner?)"/> collapses every rung,
    /// because there is nothing above "we know who this is".</summary>
    public HydrationLevel LevelOf(string uri) => HydrationLevels.Of(_store.GetOwner(uri));

    /// <summary>Nothing: a user has no catalogue V4 (<c>XmKinds.CatalogKindOf</c> answers UnknownExtension), so step 0
    /// skips these uris entirely and this ladder owns the whole fetch.</summary>
    public void ExtraCatalogKinds(in EntityUri uri, HydrationLevel level, List<(string Uri, int Kind)> into) { }

    public async Task ContinueAsync(IReadOnlyList<EntityUri> uris, HydrationLevel level, HydrationOptions opts,
                                    HydrationContext ctx, CancellationToken ct)
    {
        if (uris.Count == 0) return;

        var ids = new List<string>(uris.Count);
        for (int i = 0; i < uris.Count; i++)
            if (UserProfileIds.Normalize(uris[i].Uri) is { } canonical) ids.Add(canonical);
        if (ids.Count == 0) return;

        IReadOnlyDictionary<string, Owner?> resolved;
        try
        {
            resolved = await _fetch.ResolveAsync(ids, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Best-effort, like every ladder step — but REPORT it, so the hydrator seals on the short exhausted window
            // instead of treating a dead socket as "these accounts genuinely have no profile".
            ctx.Log.Info("user profile resolve: " + ex.Message);
            for (int i = 0; i < ids.Count; i++) ctx.ReportTransient(ids[i]);
            return;
        }

        // ONE bulk scope for the whole page of owners: a 10k playlist's added-by set is hundreds of writes and the
        // library grid must repaint once, not once per contributor. Opened LAZILY, so a page whose every id 404s
        // publishes no store change at all (the same rule TraitBatch follows).
        IDisposable? bulk = null;
        try
        {
            foreach (var (key, owner) in resolved)
            {
                if (owner is null) continue;   // a 404 is a real answer: no row, and the ledger seals the re-ask away
                // Key the row by the uri we ASKED with. Every later GetOwner uses that spelling (it is what the
                // playlist header / membership row carries), so letting the payload's own uri win would file the
                // answer under an id nobody looks up — and the page would re-ask forever.
                var id = UserProfileIds.BareId(key);
                bulk ??= _store.BeginBulk();
                _store.UpsertOwner(string.Equals(owner.Id, id, StringComparison.Ordinal) ? owner : owner with { Id = id });
            }
        }
        finally { bulk?.Dispose(); }
    }
}
