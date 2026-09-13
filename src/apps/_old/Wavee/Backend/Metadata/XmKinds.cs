// CoreKind, not `EntityKind`: this file lives in Wavee.Backend.Metadata, whose OWN EntityKind is the persisted
// transport enum. The alias makes the routing-vs-transport distinction impossible to misread (and impossible to
// mis-bind — a namespace member always shadows a compilation-unit alias).
using CoreKind = Wavee.Core.EntityKind;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Backend.Metadata;

// ── THE routing kind → catalogue extension kind map (design §2.2) ────────────────────────────────────────────────────
// The inventory found TWO "KindFor"s (MetadataService's and ExtendedMetadataSource's) plus a hand-rolled prefix test in
// the paged hydrator, all answering the same question with slightly different coverage. There is now exactly one body,
// and it is keyed on Wavee.Core.EntityKind — the ROUTING vocabulary the whole façade speaks — rather than on the
// transport's own persisted enum, so a ladder never has to translate twice.

/// <summary>Which extended-metadata kind carries an entity's CATALOGUE facts.</summary>
public static class XmKinds
{
    /// <summary>The catalogue kind for a routing kind, or <see cref="Xm.ExtensionKind.UnknownExtension"/> when the
    /// transport has none (a user, a collection, a prerelease, a concert). Callers SKIP the unknowns rather than
    /// sending them: <c>GzipExtensionRequest</c> would drop them anyway, and a request nobody can answer is waste.
    /// <para>A playlist's header is LIST_METADATA_V2 (205), not a V4 — the one asymmetry, and the reason a playlist
    /// pointer used to be dropped before a query was even written.</para></summary>
    public static Xm.ExtensionKind CatalogKindOf(CoreKind kind) => kind switch
    {
        CoreKind.Track => Xm.ExtensionKind.TrackV4,
        CoreKind.Episode => Xm.ExtensionKind.EpisodeV4,
        CoreKind.Album => Xm.ExtensionKind.AlbumV4,
        CoreKind.Artist => Xm.ExtensionKind.ArtistV4,
        CoreKind.Show => Xm.ExtensionKind.ShowV4,
        CoreKind.Playlist => Xm.ExtensionKind.ListMetadataV2,
        _ => Xm.ExtensionKind.UnknownExtension,
    };

    /// <summary>Is this a kind <c>ExtendedMetadataSource.ProjectResponse</c> can actually PROJECT? Everything else
    /// (a fused trait kind, the 178/220 wire-fidelity kinds) is cached for its next conditional request but has no
    /// entity to write, so re-serializing it into a projection pass would be pure allocation.</summary>
    public static bool IsCatalogKind(Xm.ExtensionKind kind) => kind
        is Xm.ExtensionKind.TrackV4 or Xm.ExtensionKind.EpisodeV4 or Xm.ExtensionKind.AlbumV4
        or Xm.ExtensionKind.ArtistV4 or Xm.ExtensionKind.ShowV4 or Xm.ExtensionKind.ListMetadataV2;
}
