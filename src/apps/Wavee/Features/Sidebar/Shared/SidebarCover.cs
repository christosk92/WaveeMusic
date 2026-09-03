using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// F.1.4 — the ONE leading-visual factory for every sidebar row and rail tile, in every design.
//
// WHY IT IS SHARED. "A pinned artist is a circle, a pinned folder is a glyph tile, a cover-less playlist is a 2×2 mosaic"
// is a set of rules three modes would otherwise each re-derive (and drift on). Everything visual about an entity's art
// slot is decided HERE: the corner radius ladder, the circular-for-artists rule, the folder/route glyph tiles, the
// mosaic hand-off, and the six canonical sizes.
//
// It is a pure static factory (no Component, no hooks): every call site already knows its size and its entity, and a
// component per art slot would cost a mount per row in a virtualized list. The actual image pipeline (decode target,
// blurhash placeholder, breathing shimmer, mosaic composition) is Surfaces.Artwork's — this file only chooses its
// arguments, so a sidebar cover and a grid card cover share one decode cache.

static class SidebarCover
{
    // ── the six canonical sizes (F.1.4). A caller passes one of these; nothing else is a supported sidebar art size. ──
    /// <summary>Inline/caption art (a subtitle-line thumbnail).</summary>
    public const float S20 = 20f;
    /// <summary>Compact-density list art.</summary>
    public const float S28 = 28f;
    /// <summary>The Classic / Cozy list art — the size every existing sidebar row uses.</summary>
    public const float S32 = 32f;
    /// <summary>Comfortable-density list art, and the compact rail's TILE box.</summary>
    public const float S40 = 40f;
    /// <summary>Grid-cell art (compact grid).</summary>
    public const float S48 = 48f;
    /// <summary>Grid-cell art (grid) / an entity-spotlight card cover.</summary>
    public const float S64 = 64f;

    /// <summary>The corner-radius ladder. Circular (artist avatars) is always a full half-size radius; everything else
    /// steps 4 → 6 → 8 with the art size, matching the radii the landed sidebar/card surfaces already use (32→6, 40→6).</summary>
    public static float Radius(float size, bool circular)
        => circular ? size * 0.5f : size <= 28f ? 4f : size <= 40f ? 6f : 8f;

    /// <summary>Glyph size inside a glyph TILE (folder / route / monogram-less placeholder). 16 is the sidebar's standard
    /// row glyph; smaller art slots step down so the mark still has breathing room.</summary>
    public static float GlyphSize(float size) => size >= 28f ? 16f : 12f;

    // ── entity art ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The art slot for a projected entry — the ONE call a mode surface makes. Dispatches on
    /// <see cref="SidebarLibraryEntry.Kind"/>: folders get the folder tile, app routes get their <c>ShellNav</c> glyph
    /// tile, tracks and entities get cover art (circular when the entry says so).</summary>
    public static Element ForEntry(in SidebarLibraryEntry e, float size)
        => IsLiked(e.Id, e.Uri) ? Liked(size)
        : e.Kind switch
        {
            SidebarEntryKind.Folder => Folder(size),
            SidebarEntryKind.AppRoute => RouteGlyph(e.Id, size),
            _ => Art(e.Cover, e.MosaicTiles, e.Id, size, e.Circular || e.Kind == SidebarEntryKind.Artist),
        };

    /// <summary>The liked collection, in either of the two shapes a sidebar entry spells it: the <c>"liked"</c> ROUTE
    /// id the pin store canonicalizes to (<c>SidebarPinId</c>), or the collection uri itself on a projected entry.</summary>
    static bool IsLiked(string? id, string? uri)
        => string.Equals(id, "liked", StringComparison.Ordinal) || LikedSongsArtwork.IsLikedUri(uri);

    /// <summary>Liked Songs' art slot: the user's chosen treatment, at sidebar scale.
    ///
    /// <para>This is the ONE place the file's "pure static factory, no component per art slot" rule bends, and
    /// deliberately: a sidebar shows Liked Songs at most once or twice (a pin, a recently-played row), so this costs a
    /// bounded couple of mounts, not one per virtualized row. It is worth them — a heart GLYPH tile in a list where
    /// every neighbour shows cover art made the collection the odd row out, and it ignored the treatment the user
    /// picked. Sidebar sizes are 20–64, all below the treatment floor, so what actually paints is the flat 2x2 mosaic
    /// of their newest likes (or the stock PNG when the library cannot feed even that).</para>
    ///
    /// <para>The <c>Route(… "Heart")</c> row in <c>SidebarBuiltInDocuments</c> is unaffected: an authored
    /// <c>IconOverride</c> is resolved by <c>SidebarPaneSlot.LeadingArt</c> BEFORE this factory is asked, so the
    /// built-in Liked NAV row keeps its heart mark and only library-list appearances take the cover.</para></summary>
    public static Element Liked(float size) => LikedSongsArtwork.Dynamic(size, Radius(size, false));

    /// <summary>Cover art: the image when there is one, a 2×2 mosaic when the playlist is cover-less but carries ≥4
    /// tiles, else the seeded placeholder tile. <paramref name="seedKey"/> is the entity's stable id/uri (never an index)
    /// so a re-sorted list keeps each row's placeholder tint.</summary>
    public static Element Art(Image? cover, IReadOnlyList<string>? mosaicTiles, string seedKey, float size, bool circular = false)
    {
        // Surfaces.Artwork already mosaics from Image.MosaicTiles; lift a bare tile list onto an Image so both shapes
        // (an entry that carries tiles separately, a PlaylistSummary whose Image carries them) take one path.
        Image? image = cover;
        if ((image is null || image.Url.Length == 0) && mosaicTiles is { Count: > 0 })
            image = new Image("", BlurHash: image?.BlurHash, MosaicTiles: mosaicTiles);
        // Bucketed decode ladder: without a decodePx hint the image decodes at its LAYOUT size (36-DIP rail tiles,
        // 32-DIP rows) with no DPI multiply — visibly blurry on any >1x display. Buckets also mean the 36-DIP rail
        // tile and the 32-DIP row share one cache entry instead of two near-identical decodes.
        int decodePx = size <= 32f ? 64 : size <= 64f ? 128 : 256;
        return Surfaces.Artwork(image, SeedFrom(seedKey), size, size, Radius(size, circular), decodePx: decodePx);
    }

    // ── glyph tiles ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A neutral tile carrying a glyph — the shape a folder / app route / unavailable entity wears in an art
    /// slot, so a row's leading column is always the same width whatever it holds.</summary>
    public static Element Glyph(string glyph, float size, bool circular = false, ColorF? color = null)
        => new BoxEl
        {
            Width = size, Height = size, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(Radius(size, circular)),
            Fill = Tok.FillSubtleSecondary,
            Children = [Icon(glyph, GlyphSize(size), color ?? Tok.TextSecondary)],
        };

    /// <summary>A playlist folder. <paramref name="expanded"/> swaps to the open-folder mark so a disclosed folder reads
    /// as open even where the chevron is clipped away (the rail).</summary>
    public static Element Folder(float size, bool expanded = false)
        => Glyph(expanded ? Icons.FolderOpen : Icons.Folder, size);

    /// <summary>An app-route tile wearing the route's own <c>ShellNav</c> glyph, so a pinned "Liked Songs" shows a heart
    /// and not a generic square. An unresolvable route key degrades to the music-note mark (§3.1.7) — never blank.
    /// (Named <c>RouteGlyph</c>, not <c>Route</c>, so it can never be confused with <c>FluentGpu.Controls.Route</c>.)</summary>
    public static Element RouteGlyph(string routeKey, float size)
        => Glyph(ShellNav.Dest(routeKey).Glyph, size);

    /// <summary>Monogram fallback: the entity's first letter over the neutral tile. Used where a placeholder tint alone
    /// is not enough identity (a cover-less row in a grid cell); list rows keep the plain seeded tile.</summary>
    public static Element Monogram(string name, float size, bool circular = false)
        => new BoxEl
        {
            Width = size, Height = size, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(Radius(size, circular)),
            Fill = Tok.FillSubtleSecondary,
            Children =
            [
                new TextEl(Initial(name))
                {
                    Size = size * 0.42f, Weight = 600, Color = Tok.TextSecondary,
                },
            ],
        };

    static string Initial(string name)
    {
        for (int i = 0; i < name.Length; i++)
            if (!char.IsWhiteSpace(name[i])) return char.ToUpperInvariant(name[i]).ToString();
        return "?";
    }

    /// <summary>The stable placeholder-tint seed for a key (the same hash the landed sidebar rows use, so no row's
    /// placeholder colour changes as call sites move onto this file).</summary>
    public static int SeedFrom(string key)
    {
        int h = 17;
        foreach (char c in key) h = h * 31 + c;
        return h & 0x7fffffff;
    }
}
