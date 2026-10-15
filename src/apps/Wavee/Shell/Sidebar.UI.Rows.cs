// ── Shell/Sidebar.UI.Rows.cs ───────────────────────────────────────────────────────────────────────────────────────
// the sidebar primitives the pages and the structures share: covers, the rotating chevron, and the pane's text / loc / glyph tables.
// (T5b: the 40-px section header, the separator, the "+" create button and the quiet count are Classic's:
// Themes/Classic/Structure/Views/Nav/ClassicNavHead.cs. T5f: the one entity row, the selection pill and the pending row are
// Classic's too -- ClassicNavEntityRow.cs, ClassicNavRowParts.cs -- because they are drawn on the row ladder.)
//
// Role: UI
// Owner: J
// Wave: 4
// Budget: 1700 lines
// Spec: ch 25 §0, §2 (W1, W1b, W2, W4, W9, W10, W17, W18, W19), §3, §4, §5, §6, §9
//
// NAMED PARTIAL of Sidebar.UI.cs (J1): the cover / chrome primitives every plan row, rail tile, card and page builds from.
//
// Everything here is either a PURE STATIC over a value (a Component per row would cost a mount per slot in a 10k
// virtualized list) or a small Component that owns exactly the hooks a static cannot: a node ref + an animation seed
// (Chevron). Props freeze at mount, so every changing input is a Func probe, never a value.

using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Sidebar
{
    // ══ 2. COVERS ════════════════════════════════════════════════════════════════════════════════════════════════════
    //
    // The ONE leading-visual factory for every row, tile and card: the radius ladder, circular-for-artists, the folder /
    // route glyph tiles, the mosaic hand-off and the rail size. The image pipeline itself (decode, placeholder
    // tint, shimmer) is Controls.Artwork's — a sidebar cover and a grid card share one decode cache.

    internal static class Cover
    {
        /// <summary>The comfortable / rail art size: the grid's smallest cell.</summary>
        public const float S40 = 40f;

        /// <summary>4 (≤28) → 6 (≤40) → 8; circular is always half the size.</summary>
        public static float Radius(float size, bool circular)
            => circular ? size * 0.5f : size <= 28f ? 4f : size <= 40f ? 6f : 8f;

        /// <summary>The glyph inside a tile: 16 from 28 up, else 12.</summary>
        public static float GlyphSize(float size) => size >= 28f ? 16f : 12f;

        /// <summary>Bucketed decode: without it a thumb decodes at its LAYOUT size (blurry on any >1x display), and the
        /// buckets make a 36-DIP rail tile and a 32-DIP row share one cache entry.</summary>
        static int DecodeBucket(float size) => size <= 32f ? 64 : size <= 64f ? 128 : 256;

        /// <summary>The art slot for a projected entry — the ONE call a surface makes: liked collection → its cover ·
        /// folder → folder tile · app route → the route's own glyph tile · artist / Circular → circle · else cover art.</summary>
        public static Element ForEntry(in SidebarLibraryEntry e, float size)
        {
            if (IsLiked(e.Id, e.Uri)) return Liked(size);
            return e.Kind switch
            {
                SidebarEntryKind.Folder => Folder(size),
                SidebarEntryKind.AppRoute => RouteGlyph(e.Id, size),
                _ => Art(e.Cover, e.MosaicTiles, size, e.Circular || e.Kind == SidebarEntryKind.Artist),
            };
        }

        /// <summary>The two spellings of the liked collection: the "liked" ROUTE pin id, or the collection uri.</summary>
        static bool IsLiked(string? id, string? uri)
            => string.Equals(id, "liked", StringComparison.Ordinal)
               || (uri is { Length: > 0 } && EntityUri.IsLikedCollection(uri));

        /// <summary>Liked Songs at sidebar scale: a 2×2 mosaic of the newest likes' covers, one cover when fewer than four
        /// distinct ones exist, a heart tile when the library has none yet. The one place the "no component per art slot"
        /// rule bends — a sidebar shows Liked at most twice — and it is wrapped in the same hard-sized clipped box every
        /// other arm returns, or a Liked row measures taller than its section (one height per section).</summary>
        public static Element Liked(float size)
        {
            float r = Radius(size, false);
            return new BoxEl
            {
                Width = size, Height = size, Shrink = 0f, Corners = CornerRadius4.All(r), ClipToBounds = true,
                // Keyed by geometry: the component freezes its size at mount.
                Children = [Embed.Comp(() => new LikedArt(size, r)) with { Key = "liked-cover:" + FormatCache.Int((int)size) }],
            };
        }

        /// <summary>Cover art: the image when there is one; for a cover-less entry a 2×2 mosaic of ≥4 tiles, else its first
        /// tile; else the neutral placeholder tile (the tint comes from the url's graded colour, and a url-less slot takes
        /// the opaque neutral tile).</summary>
        public static Element Art(StringId cover, IReadOnlyList<StringId>? mosaicTiles, float size, bool circular = false)
        {
            float radius = Radius(size, circular);
            string? url = Controls.ArtUrl(cover);
            if (url is null && mosaicTiles is { Count: > 0 } tiles)
            {
                if (tiles.Count >= 4
                    && Controls.ArtUrl(tiles[0]) is { } t0 && Controls.ArtUrl(tiles[1]) is { } t1
                    && Controls.ArtUrl(tiles[2]) is { } t2 && Controls.ArtUrl(tiles[3]) is { } t3)
                    return Controls.Mosaic(new[] { t0, t1, t2, t3 }, size, size, radius);
                url = Controls.ArtUrl(tiles[0]);
            }
            return Controls.Artwork(url, size, size, radius, decodePx: DecodeBucket(size));
        }

        /// <summary>A neutral tile carrying a glyph — what a folder / route / unavailable entity wears, so the leading
        /// column is always the same width whatever it holds.</summary>
        public static Element Glyph(string glyph, float size, bool circular = false, ColorF? color = null)
            => new BoxEl
            {
                Width = size, Height = size, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(Radius(size, circular)),
                Fill = Tok.FillSubtleSecondary,
                Children = [Icon(glyph, GlyphSize(size), color ?? Tok.TextSecondary)],
            };

        /// <summary>A playlist folder; <paramref name="expanded"/> swaps to the open-folder mark (reads as open even where
        /// the chevron is clipped away).</summary>
        public static Element Folder(float size, bool expanded = false)
            => Glyph(expanded ? Icons.FolderOpen : Icons.Folder, size);

        /// <summary>An app-route tile wearing the route's own glyph (a pinned Liked shows a heart, not a square).</summary>
        public static Element RouteGlyph(string routeKey, float size)
            => Glyph(Shell.Dest(Shell.Parse(routeKey)).Glyph, size);

        /// <summary>The liked-collection composition. Subscribes to the liked relation and the track table — inside a
        /// value gate (W2-A2): the memo's value is the first four distinct liked cover ids, so the mosaic is
        /// re-composed when a new like or a landed cover CHANGES those four, not on every publication of the track
        /// table while a page's covers stream in.</summary>
        sealed class LikedArt : Component
        {
            readonly float _size, _radius;
            readonly Func<NavLikedCovers> _stamp;
            readonly string[] _tiles = new string[4];

            public LikedArt(float size, float radius)
            {
                _size = size;
                _radius = radius;
                _stamp = Sidebar.Model.ReadLikedCovers;   // the first four distinct liked covers: the model's read
            }

            public override Element Render()
            {
                var s = UseComputed(_stamp).Value;
                if (s.Found == 0) return Glyph(Icons.Heart, _size);
                string? first = Controls.ArtUrl(s.A);
                if (s.Found < 4 || first is null) return Controls.Artwork(first, _size, _size, _radius, decodePx: DecodeBucket(_size));
                _tiles[0] = first;
                _tiles[1] = Controls.ArtUrl(s.B) ?? first;
                _tiles[2] = Controls.ArtUrl(s.C) ?? first;
                _tiles[3] = Controls.ArtUrl(s.D) ?? first;
                return Controls.Mosaic(_tiles, _size, _size, _radius);
            }
        }
    }

    // ══ 3. THE CHEVRON ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE one disclosure chevron: ONE glyph whose Rotation rides <c>MotionTokenId.Reveal</c> — the SAME spring as the band
    /// it discloses, so the glyph and the rows land together — through <c>SeedValue</c>, so the token owns the dynamics AND
    /// the reduced-motion policy. Never a glyph swap — a swap teleports while the section beside it animates.
    ///
    /// <para>A Component because the rotation needs a node ref and an edge-triggered effect — hooks a recycling slot must
    /// not grow conditionally. <c>open</c> and <c>identity</c> are read inside the effect, never in Render, so their signal
    /// reads subscribe the effect alone: a recycle or an open flip re-runs the seeding and renders nothing.</para>
    ///
    /// <para>A first mount SEEDS the resting angle with no motion. <c>identity</c> (optional) extends that to a RECYCLE:
    /// when the probe's value changes (the slot's index, a section id hash) the new angle is seeded, never animated — a
    /// row scrolled onto a different section must not visibly spin.</para>
    /// </summary>
    internal sealed class Chevron : Component
    {
        readonly Func<bool> _open;
        readonly Func<int>? _identity;
        readonly string _glyph;
        readonly float _size, _openDeg;
        readonly bool _accent;

        Chevron(Func<bool> open, Func<int>? identity, string glyph, float size, float openDeg, bool accent)
        {
            _open = open; _identity = identity; _glyph = glyph; _size = size; _openDeg = openDeg; _accent = accent;
        }

        /// <summary>A SECTION header's chevron: ChevronDown at rest, rotated 180° when the section is open.
        /// <paramref name="accentWhenOpen"/> inks it secondary at rest and accent while open (the track table's versions
        /// chevron, tuned Classic L1).</summary>
        public static Element Section(Func<bool> open, float size = 10f, Func<int>? identity = null, bool accentWhenOpen = false)
            => Embed.Comp(() => new Chevron(open, identity, Icons.ChevronDown, size, 180f, accentWhenOpen));

        /// <summary>A DISCLOSURE chevron (a folder row / tree row): ChevronRight at rest, rotated 90° when expanded.</summary>
        public static Element Disclosure(Func<bool> open, float size = 10f, Func<int>? identity = null)
            => Embed.Comp(() => new Chevron(open, identity, Icons.ChevronRight, size, 90f, accent: false));

        public override Element Render()
        {
            var node = UseRef<NodeHandle>(default);
            var seeded = UseRef(false);
            var seededIdentity = UseRef(0);
            var last = UseRef(0f);

            // The item is read HERE, in an auto-tracked effect, never in Render: an open flip, a recycle onto another
            // item or any other change behind `open` re-runs this body and renders nothing (a bound row's recycle stays
            // a rebind).
            UseEffect(() =>
            {
                float target = _open() ? _openDeg : 0f;
                int identity = _identity?.Invoke() ?? 0;
                var anim = Context.Anim;
                var scene = Context.Scene;
                if (anim is null || scene is null || node.Value.IsNull || !scene.IsLive(node.Value)) return;
                if (!seeded.Value || seededIdentity.Value != identity)
                {
                    seeded.Value = true;
                    seededIdentity.Value = identity;
                    last.Value = target;
                    anim.SeedValue(node.Value, AnimChannel.Rotation, target, MotionTokenId.Reveal, from: target);
                    return;
                }
                if (last.Value == target) return;   // a change that moved neither the angle nor the item
                last.Value = target;
                // A mid-flight toggle retargets from the LIVE angle instead of restarting.
                anim.SeedValue(node.Value, AnimChannel.Rotation, target, MotionTokenId.Reveal);
            });

            return new BoxEl
            {
                Width = _size, Height = _size, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                HitTestVisible = false,
                OnRealized = h => node.Value = h,
                Children = [Icon(_glyph, _size) with { Color = _accent ? Prop.Of(() => _open() ? Tok.AccentTextPrimary : Tok.TextSecondary) : (Prop<ColorF>)Tok.TextTertiary }],
            };
        }
    }

    // ══ 8. THE PANE'S TEXT, LOC AND GLYPH TABLES ═════════════════════════════════════════════════════════════════════

    /// <summary>The renderer's display rules: section titles, per-kind subtitles, icon fallbacks and the
    /// "never render a blank row" degradations — split out so the row builders stay about LAYOUT.
    /// <para>Public, not internal: this assembly has no <c>InternalsVisibleTo</c> (see <c>Playlist.UI.cs</c>'s own
    /// note on the same pattern), so <c>Wavee.Tests</c> can only pin <see cref="SubtitleOf"/>'s bug A1 contract — an
    /// unknown COUNT renders no subtitle, never "0 songs" — by calling the real method.</para></summary>
    public static class PaneText
    {
        /// <summary>The section's title: its kind's loc key (a section has no rename).</summary>
        public static string TitleOf(SidebarSection section)
            => SidebarCatalogue.TitleKeyOf(section.Kind) is { } key ? Loc.Get(key) : "";

        /// <summary>The subtitle text: the grammar is <see cref="SidebarSubtitleRules.Of"/> (data), the words are the loc table's.
        /// Playlist → "N songs" (or "N items" with an episode, "N episodes" for Your Episodes) · album → "Album · first artist" · artist → "Artist" · show →
        /// "Podcast · publisher" · folder → "N items" · track → its artist · route → a concert's venue (its Creator). An
        /// UNKNOWN count is null, never "0 songs" (bug A1).</summary>
        public static string? SubtitleOf(in SidebarLibraryEntry e)
        {
            var s = SidebarSubtitleRules.Of(in e);
            return s.Kind switch
            {
                SidebarSubtitleKind.Songs => Strings.Sidebar.SongCount(s.Count),
                SidebarSubtitleKind.Items => Strings.Sidebar.V3.ItemCount(s.Count),
                SidebarSubtitleKind.Episodes => Strings.Podcast.EpisodeCount(s.Count),
                SidebarSubtitleKind.Album => s.Detail.Length > 0
                    ? Loc.Get(Strings.Sidebar.V3.Kind.Album) + " · " + s.Detail
                    : Loc.Get(Strings.Sidebar.V3.Kind.Album),
                SidebarSubtitleKind.Podcast => s.Detail.Length > 0
                    ? Loc.Get(Strings.Sidebar.V3.Kind.Show) + " · " + s.Detail
                    : Loc.Get(Strings.Sidebar.V3.Kind.Show),
                SidebarSubtitleKind.Artist => Loc.Get(Strings.Sidebar.V3.Kind.Artist),
                SidebarSubtitleKind.Text => s.Detail,
                _ => null,
            };
        }

        /// <summary>The EMPTY copy for a section that resolved to zero rows, per kind (never a borrowed debug string).</summary>
        public static string EmptyText(SidebarSectionKind kind) => kind switch
        {
            SidebarSectionKind.NewReleases => Loc.Get(PaneLoc.NewReleasesEmpty),
            _ => Loc.Get(PaneLoc.SectionEmpty),
        };
    }

    /// <summary>The pane renderer's loc KEYS as literals, in one place. Every key exists in <c>assets/loc/en-US.json</c>;
    /// a typo renders loudly as <c>[key]</c>.</summary>
    internal static class PaneLoc
    {
        public const string NewReleasesEmpty = "sidebar.newReleases.empty";
        public const string SectionEmpty = "sidebar.section.empty";
        public const string LibraryEmpty = "sidebar.v3.empty.library";
        public const string SearchEmpty = "sidebar.v3.empty.search";
        /// <summary>A section header's ⋯ button (its tooltip / accessible name).</summary>
        public const string SectionOptions = "sidebar.header.options";
    }

    /// <summary>The compact-rail tile glyphs (design V.9).</summary>
    internal static class PaneIcon
    {
        /// <summary>A collapsed section's compact-rail tile glyph (design V.9), by the section's kind.</summary>
        public static string SectionGlyph(SidebarSectionKind kind) => kind switch
        {
            SidebarSectionKind.Pinned => Icons.Pin,
            SidebarSectionKind.Collections => Icons.Library,
            SidebarSectionKind.Playlists => Icons.MusicNote,
            SidebarSectionKind.Recent => Icons.History,
            SidebarSectionKind.NewReleases => Icons.Album,
            _ => Icons.List,
        };
    }
}
