using System;
using Wavee.Core;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Backend.Hydration.Projectors;

// ── Kinds 178 IDENTITY_TRAIT + 220 ENTITY_TYPE_TRAIT — wire fidelity, no projection (design §2.4) ────────────────────
// The desktop client asks for these next to 179 on its recents viewport, and nothing in the payloads is anything we
// render: 178 is the entity's own uri/name echo, 220 its type discriminant — both of which the row already knows from
// the catalogue projection that put it in the store. They exist here so our request SHAPE matches the capture (an
// unattributed request missing two kinds the real client always sends is a fingerprint), not because a surface waits
// on them.
//
// Consequently there is no store write, no mark, and exactly one useful outcome: after the first pass the pipeline's
// negative memo has both kinds for the uri and the ask stops. That is the whole lifecycle.

/// <summary>The 178/220 projector: asks, decodes nothing, writes nothing, and memoizes itself out of the plan.</summary>
public sealed class IdentityTraitsProjector : ITraitProjector
{
    static readonly Xm.ExtensionKind[] CompanionKinds = [Xm.ExtensionKind.EntityTypeTrait];

    public TraitSet Trait => TraitSet.IdentityTraits;
    public Xm.ExtensionKind Kind => Xm.ExtensionKind.IdentityTrait;
    public ReadOnlySpan<Xm.ExtensionKind> Companions => CompanionKinds;

    public bool AppliesTo(EntityKind kind) => TraitApplicability.Applies(Kind, kind);

    /// <summary>No mark exists: nothing is projected, so nothing on the row can say "already asked". The memo is what
    /// makes this once-per-session, and it is the pipeline's to keep.</summary>
    public bool AlreadyHas(IStore store, string uri, DateTimeOffset now) => false;

    public TraitOutcome Project(TraitBatch batch, string uri, in TraitPayloads payloads)
    {
        // Both kinds an explicit 404 ⇒ this entity has no identity plane at all; anything else ⇒ the ask was answered.
        // Both outcomes memoize, because there is nothing a second ask could produce that the first did not — the two
        // are distinguished only so the batch tally can report which it was.
        return payloads.Missing(Kind) && payloads.Missing(Xm.ExtensionKind.EntityTypeTrait)
            ? TraitOutcome.Negative
            : TraitOutcome.Unchanged;
    }
}
