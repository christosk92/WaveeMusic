using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using Wavee.Core;
using Wavee.Core.Catalog;

namespace Wavee;

/// <summary>A visible artist portrait owns identity demand and observes later catalog changes.</summary>
sealed class ArtistPortrait : Component
{
    /// <summary>Callers (e.g. <c>LikedArtistsCard</c>) commonly re-create the <see cref="Artist"/> seed on every
    /// render (<c>LikedArtistsCard.Resolve</c> allocates a fresh <see cref="Artist"/> array each time), so the
    /// record default's member-wise <c>Equals</c> — which recurses into <c>Artist</c>'s own equality and would
    /// therefore compare its list-typed members (<c>TopAlbums</c>, <c>TopTracks</c>, …) by REFERENCE — would treat
    /// two content-identical seeds as different and force this component (and the identity query underneath it) to
    /// re-render for nothing. This overrides <c>Equals</c>/<c>GetHashCode</c> to compare only what actually decides
    /// what gets painted: the seed's <c>Uri</c> (the identity query's key), its <c>Name</c> and its
    /// <c>Image?.Url</c> (the fallback shown before the query resolves), plus <c>Size</c>.</summary>
    sealed record Props(Artist Seed, float Size)
    {
        public bool Equals(Props? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return Size == other.Size
                && string.Equals(Seed.Uri, other.Seed.Uri, StringComparison.Ordinal)
                && string.Equals(Seed.Name, other.Seed.Name, StringComparison.Ordinal)
                && string.Equals(Seed.Image?.Url, other.Seed.Image?.Url, StringComparison.Ordinal);
        }

        public override int GetHashCode()
            => HashCode.Combine(Seed.Uri, Seed.Name, Seed.Image?.Url, Size);
    }
    public static Element Create(Artist artist, float size)
        => Embed.Comp(new Props(artist, size), static () => new ArtistPortrait());

    public override Element Render()
    {
        var props = UseProps<Props>();
        var svc = UseContext(Services.Slot);
        var identity = QueryHooks.Use(Context, static (page, value) => page.SetReady(value), svc?.Queries,
            svc is not null && props.Seed.Uri.Length > 0 ? new ArtistIdentityQuery(svc.CatalogScope, props.Seed.Uri) : null,
            props.Seed, QueryDemand.Initial);
        var artist = identity.Binding.Value is null || !identity.Loadable.IsReady ? props.Seed : identity.Loadable.Value.Value;
        return PersonPicture.Create("", props.Size, displayName: artist.Name.Length > 0 ? artist.Name : props.Seed.Name,
            imageSourcePath: artist.Image?.Url ?? props.Seed.Image?.Url);
    }
}
