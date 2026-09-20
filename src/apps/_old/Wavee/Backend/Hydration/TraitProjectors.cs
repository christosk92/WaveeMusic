using System;
using System.Collections.Generic;
using Wavee.Backend.Hydration.Projectors;
using Wavee.SpotifyLive;
using Wavee.SpotifyLive.Hydration;

namespace Wavee.Backend.Hydration;

// ── THE projector registry (design §2.4) ─────────────────────────────────────────────────────────────────────────────
// One list, in one place, so "which facets does this app know how to project?" has a single answer — and so adding one
// is a single line rather than a service, a cap, a memo and a caller list. Registration ORDER is the order the pipeline
// plans, requests and tallies in, which is what makes a traits.batch log line diffable between runs.

/// <summary>Builds the pipeline's projector list.</summary>
public static class TraitProjectors
{
    /// <param name="reader">The display-only extension reader. Only the video projector needs it — its canonical-alias
    /// recovery is a follow-up READ (TRACK_V4/212), not a row trait, so it belongs on the reader's shared-load path
    /// rather than costing the batch a second POST arm.</param>
    /// <param name="plane">The cover-colour plane, as a Func because the plane is installed at go-live while the
    /// registry is built once — and because 179 is image-keyed, so a null plane simply means "nothing to tint yet"
    /// rather than a missing dependency.</param>
    public static IReadOnlyList<ITraitProjector> Default(IExtensionReader reader, Func<CoverColorPlane?> plane)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(plane);
        return
        [
            // The row bundle first — the four facets a list surface paints, and the ones every page asks for.
            new VideoProjector(reader),
            new AudioAttributesProjector(),
            new DescriptorProjector(),
            new VisualIdentityProjector(plane),
            // Then the surface-specific ones.
            new PlayCountProjector(),
            new PublishingProjector(),
            // And 178/220: asked for wire fidelity with the desktop client, projected into nothing.
            new IdentityTraitsProjector(),
        ];
    }
}
