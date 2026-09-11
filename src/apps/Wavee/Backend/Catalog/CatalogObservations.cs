using System;
using System.Linq;
using Wavee.Core;
using Wavee.Core.Catalog;

namespace Wavee.Backend.Catalog;

/// <summary>Full protocol snapshots have authority over exactly the header fields the protocol carries.</summary>
public static class CatalogObservations
{
    public static CatalogObservation PlaylistHeader(CatalogScope scope, Playlist playlist)
        => new(new ResourceKey(scope, playlist.Uri, FacetKind.PlaylistHeader), new PlaylistHeaderPatch(
            Name: Set<string?>(playlist.Name), Description: Set(playlist.Description), Cover: Set(playlist.Cover),
            OwnerUri: Set(playlist.Owner is { Id.Length: > 0 } owner ? OwnerUri(owner.Id) : null),
            TrackCount: Set<int?>(playlist.TrackCount), IsPublic: Set<bool?>(playlist.IsPublic),
            NextUpdateAt: Set(Instant(playlist.DaylistExpiresAtMs)), OwnerName: Set<string?>(playlist.OwnerName),
            Capabilities: playlist.Capabilities.Known ? Set<PlaylistCapabilities?>(playlist.Capabilities) : default,
            Format: Set(playlist.Format), Source: Set(playlist.Source),
            BasePermissionRevision: Set(playlist.BasePermissionRevision), Tuning: Set(playlist.Tuning),
            CollaboratorUris: Set<System.Collections.Generic.IReadOnlyList<string>?>(playlist.Collaborators?
                .Select(owner => OwnerUri(owner.Id)).ToArray()), CreatedAt: Set(Instant(playlist.DaylistCreatedAtMs)),
            DeletedByOwner: Set<bool?>(playlist.DeletedByOwner), ChartNewEntries: Set<int?>(playlist.ChartNewEntries),
            ChartUpdatedAt: Set(Instant(playlist.ChartUpdatedAtMs)), ChartRankType: Set(playlist.ChartRankType)));

    /// <summary>Protocol edits own only fields changed from their captured baseline, never unrelated cached metadata.</summary>
    public static CatalogObservation PlaylistHeaderChanges(CatalogScope scope, Playlist? before, Playlist after)
    {
        var observation = PlaylistHeader(scope, after);
        if (before is null) return observation;
        var old = (PlaylistHeaderValue)PlaylistHeader(scope, before).Patch.Apply(null);
        var patch = (PlaylistHeaderPatch)observation.Patch;
        var next = (PlaylistHeaderValue)patch.Apply(null);
        return observation with { Patch = patch with
        {
            Name = Equal(old.Name, next.Name) ? default : patch.Name,
            Description = Equal(old.Description, next.Description) ? default : patch.Description,
            Cover = Equal(old.Cover, next.Cover) ? default : patch.Cover,
            OwnerUri = Equal(old.OwnerUri, next.OwnerUri) ? default : patch.OwnerUri,
            TrackCount = Equal(old.TrackCount, next.TrackCount) ? default : patch.TrackCount,
            IsPublic = Equal(old.IsPublic, next.IsPublic) ? default : patch.IsPublic,
            Edition = Equal(old.Edition, next.Edition) ? default : patch.Edition,
            NextUpdateAt = Equal(old.NextUpdateAt, next.NextUpdateAt) ? default : patch.NextUpdateAt,
            OwnerName = Equal(old.OwnerName, next.OwnerName) ? default : patch.OwnerName,
            Capabilities = Equal(old.Capabilities, next.Capabilities) ? default : patch.Capabilities,
            Format = Equal(old.Format, next.Format) ? default : patch.Format,
            Source = Equal(old.Source, next.Source) ? default : patch.Source,
            BasePermissionRevision = Equal(old.BasePermissionRevision, next.BasePermissionRevision) ? default : patch.BasePermissionRevision,
            Tuning = Equal(old.Tuning, next.Tuning) ? default : patch.Tuning,
            CollaboratorUris = Equal(old.CollaboratorUris, next.CollaboratorUris) ? default : patch.CollaboratorUris,
            CreatedAt = Equal(old.CreatedAt, next.CreatedAt) ? default : patch.CreatedAt,
            DeletedByOwner = Equal(old.DeletedByOwner, next.DeletedByOwner) ? default : patch.DeletedByOwner,
            ChartNewEntries = Equal(old.ChartNewEntries, next.ChartNewEntries) ? default : patch.ChartNewEntries,
            ChartUpdatedAt = Equal(old.ChartUpdatedAt, next.ChartUpdatedAt) ? default : patch.ChartUpdatedAt,
            ChartRankType = Equal(old.ChartRankType, next.ChartRankType) ? default : patch.ChartRankType,
        } };
    }

    public static bool HasFields(CatalogObservation observation) => observation.Patch is not PlaylistHeaderPatch patch
        || patch.Name.IsSpecified || patch.Description.IsSpecified || patch.Cover.IsSpecified || patch.OwnerUri.IsSpecified || patch.TrackCount.IsSpecified || patch.IsPublic.IsSpecified || patch.Edition.IsSpecified || patch.NextUpdateAt.IsSpecified || patch.OwnerName.IsSpecified || patch.Capabilities.IsSpecified || patch.Format.IsSpecified || patch.Source.IsSpecified || patch.BasePermissionRevision.IsSpecified || patch.Tuning.IsSpecified || patch.CollaboratorUris.IsSpecified || patch.CreatedAt.IsSpecified || patch.DeletedByOwner.IsSpecified || patch.ChartNewEntries.IsSpecified || patch.ChartUpdatedAt.IsSpecified || patch.ChartRankType.IsSpecified;
    static bool Equal<T>(T a, T b) => System.Collections.Generic.EqualityComparer<T>.Default.Equals(a, b);


    static string OwnerUri(string id) => id.StartsWith("spotify:user:", StringComparison.Ordinal) ? id : "spotify:user:" + id;
    static DateTimeOffset? Instant(long milliseconds) => milliseconds > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : null;
    static FieldChange<T> Set<T>(T value) => FieldChange<T>.Set(value);
}
