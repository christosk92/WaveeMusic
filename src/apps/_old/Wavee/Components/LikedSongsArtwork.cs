using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

static class LikedSongsArtwork
{
    /// <summary>The canonical Liked Songs uri. Owned by <see cref="LikedCoverRules"/> — the engine-free rules file the
    /// tests pin — so the string exists once.</summary>
    public const string Uri = LikedCoverRules.CollectionUri;
    static readonly string CoverPath = System.IO.Path.Combine(
        System.AppContext.BaseDirectory, "assets", "covers", "liked-songs-300.png");

    /// <summary>Is this the liked collection, in any of its wire spellings? <see cref="LikedCoverRules.IsLikedCollection"/>
    /// owns the decision (the user-namespaced form included); this is the render-side name for it.</summary>
    public static bool IsLikedUri(string? uri) => LikedCoverRules.IsLikedCollection(uri);

    public static Element Cover(float size, float radius, string? morphKey = null)
        // Spotify's stock collection cover, bundled so Liked Songs is correct offline and never depends on a render-time
        // network request. Keep the existing caller-owned size/corners/shared-element tag.
        => Image(CoverPath, size, size, radius, Surfaces.ArtworkPlaceholder,
                 transition: ImageTransition.None) with { MorphId = morphKey };

    /// <summary>The DYNAMIC collection cover: the user's persisted treatment, composed from their newest likes, and
    /// degrading to <see cref="Cover"/> — the bundled PNG above, byte for byte — whenever the library cannot feed the
    /// chosen style, the list has not loaded, or the persisted value is one this build does not know
    /// (<c>LikedCoverRules.Effective</c> owns that ladder; <see cref="LikedCoverArt"/> owns the composition).
    ///
    /// <para>Size-adaptive: full treatments at the rail/header/hero, a flat 2x2 collection mosaic below 140 DIP, so a
    /// sidebar row or a 64-DIP quick-pick tile never pays for an ambient loop it is too small to show.</para>
    ///
    /// <para>KEYED BY GEOMETRY (the <c>PlaylistInlineEdit.Cover</c> precedent): the component freezes size / radius /
    /// morph key at mount, so a rail-width drag has to remount it rather than leave it composing at the old edge.</para></summary>
    public static Element Dynamic(float size, float radius, string? morphKey = null)
        => Embed.Comp(() => new LikedCoverArt(size, radius, morphKey)) with
        {
            Key = "liked-cover:" + (int)size + ":" + (int)radius + ":" + morphKey,
        };

    /// <summary>The dynamic cover in a slot of ANY aspect. Square slots (almost every one) get
    /// <see cref="Dynamic"/> verbatim; a letterbox slot composes the treatment at its LONGER edge and centre-crops it
    /// (<c>LikedCoverRules.FitSide</c>), which is what <c>Surfaces.Artwork</c> already does to every other cover in a
    /// non-square frame. Letterboxing it inside bands instead would make the liked collection the one card on the page
    /// whose art does not fill its slot.</summary>
    public static Element Fitted(float width, float height, float radius, string? morphKey = null)
    {
        if (LikedCoverRules.IsSquare(width, height)) return Dynamic(width, radius, morphKey);
        float side = LikedCoverRules.FitSide(width, height);
        return new BoxEl
        {
            Width = width, Height = height, Shrink = 0f, ClipToBounds = true,
            Corners = CornerRadius4.All(radius),
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            // The composed square carries NO corners of its own — the frame above rounds the slot, and rounding both
            // would notch the crop.
            Children = [Dynamic(side, 0f, morphKey)],
        };
    }

    /// <summary>THE app-wide funnel: the liked collection's artwork for this slot, or <c>null</c> when the uri is not
    /// the liked collection (the caller then draws its ordinary artwork).
    ///
    /// <para><b>The provider's cover is deliberately not consulted.</b> Every backend that has an image for this
    /// collection hands back the SAME stock artwork — Spotify's recents feed literally serves
    /// <c>misc.scdn.co/liked-songs/liked-songs-300.png</c> — so gating on "the caller had no cover" made the user's
    /// chosen treatment lose to a CDN copy of the very PNG it replaces. Once a style is picked it is the collection's
    /// artwork everywhere, at every size; <see cref="LikedCoverArt"/> alone decides when it must degrade back.</para></summary>
    public static Element? For(string? uri, float width, float height, float radius, string? morphKey = null)
        => IsLikedUri(uri) ? Fitted(width, height, radius, morphKey) : null;

    /// <summary>The width-AGNOSTIC arm of <see cref="For"/>, for the fill cells that have no size to hand us
    /// (<c>MediaCard.GridCard</c>, whose cover is a <c>Surfaces.ArtworkFill</c> aspect-1 image). The treatment needs a
    /// concrete edge — it composes on a 304 canvas and scales — so the cell's measured width is read once through the
    /// engine's own responsive box and handed straight in; the square it returns is the same shape the aspect-1 image
    /// it replaces would have self-sized to.
    ///
    /// <para>Null when this is not the liked collection, so a call site reads exactly like <see cref="For"/>.</para></summary>
    public static Element? Fill(string? uri, float radius, string? morphKey = null, float fallback = 160f)
        => IsLikedUri(uri)
            ? Responsive.Of(w => Dynamic(w >= 1f ? w : fallback, radius, morphKey), fallback)
            : null;
}
