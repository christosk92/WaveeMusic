// ── Themes/Classic/Structure/Views/Pages/ClassicProfileList.Cards.cs — the profile list's rows, cards and shimmer (themes plan 6.1, T10c2) ──────
//
// From `Entities\Profile.Lists.Page.cs` (`BuildSlot`, `Cell`, `FollowLine`, `Shimmer`, `FollowBone`, `RowSlot`): the flat item → a letter header, or a PINNED-height row of cards (the seed the
// layout used and the measured height are the same number), each cell a fixed-width column holding the surface card and — for an artist in Following — its Follow toggle on a 40-DIP centred
// line; and the shimmer source: tool bones + two rows of circular seed cards.
//
// `CreateBound` provides no `ItemsView.SlotRow`, so every card owns its own click, focus and menu. What a card opens, plays or puts in its menu is `HomeCardNav` (the grammar every other card
// wears) and `ProfileListVerbs`; the entries arrive as values (`ProfileEntry`) through the props.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

internal static partial class ClassicProfileList
{
    static readonly Func<Element> s_followShape = static () => Controls.FollowToggle.SkeletonShape();

    // ══ THE SLOT: one flat item → a letter header, or a pinned-height row of cards ═══════════════════════════════════

    /// <summary>A BOUND slot: the template runs once per recycled slot and rebinds through the scope's item signal. It
    /// subscribes to exactly what it paints — the bound item (an epoch bump re-skins it when data lands or the width moves)
    /// and its index (the pinned extent).</summary>
    sealed class RowSlot(PageCache cache, BoundItemScope<ProfileRowItem> scope) : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            var it = scope.Item.Value;
            int index = scope.Index.Value;
            return BuildSlot(cache.Props, in it, index, overlay);
        }
    }

    static Element BuildSlot(ProfileListProps p, in ProfileRowItem it, int index, IOverlayService? overlay)
    {
        // An out-of-range slot (a shrink in flight) resolves to the default item: draw nothing, not a "#".
        if (it.Epoch == 0u) return new BoxEl();
        if (it.IsHeader)
            return new BoxEl
            {
                Height = ProfileLetterRows.HeaderExtent, Direction = 0, AlignItems = FlexAlign.End, HitTestVisible = false,
                Padding = new Edges4(Spacing.S, 0f, Spacing.S, Spacing.XS),
                Children = [ClassicLetterKit.LetterPlate(ClassicLetterKit.LetterText(it.Letter))],
            };
        var s = p.Shape.Peek();
        // A stale item (the filter shrank under a slot that has not rebound yet) never reads past the visible set.
        int n = Math.Min(it.Count, p.VisibleCount() - it.Start);
        if (n <= 0) return new BoxEl();
        var cells = new Element[n];
        for (int k = 0; k < cells.Length; k++)
        {
            var e = p.VisibleAt(it.Start + k);
            cells[k] = Cell(s, in e, overlay);
        }
        // PINNED to the projection's extent: the seed the layout used and the measured height are the same number.
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.Card, AlignItems = FlexAlign.Start, Height = p.ExtentOf(index), Shrink = 0f,
            Children = cells,
        };
    }

    /// <summary>One cell: a fixed-width column holding the surface card, and — for an artist in Following — its Follow toggle
    /// on a 40-DIP centred line. The toggle's uri freezes at mount, so it is keyed on it.</summary>
    static Element Cell(ProfileListShape s, in ProfileEntry e, IOverlayService? overlay)
    {
        float w = s.Plan.CellWidth;
        string name = e.Name;
        if (e.IsArtist)
        {
            var a = new Artist(e.Slot);
            string uri = a.Uri.Text;
            var hc = new HomeCard(new EntityRef(EntityKind.Artist, e.Slot));
            var menu = HomeCardNav.MenuOf(in hc);
            bool hasMenu = menu is not null && !Controls.IsNullOverlay(overlay);
            var data = new Controls.CardData(uri, name, Sub(ProfileListVerbs.ArtistSubtitle(e.Followers)), Controls.ArtUrl(a.ImageId),
                () => HomeCardNav.Open(in hc, null), () => HomeCardNav.Play(in hc),
                Circular: true, Drag: HomeCardNav.DragOf(in hc), ShowMenu: hasMenu) { Menu = hasMenu ? menu : null };
            Element card = Controls.Surface(data, global::Wavee.Shape.Grid) with { Key = "card:" + uri };
            Element[] kids = s.Facet == ProfileFacet.Following
                ? [card, FollowLine(uri, name)]
                : [card];
            return new BoxEl { Key = "cell:" + uri, Width = w, Shrink = 0f, Direction = 1, Children = kids };
        }

        var person = new User(e.Slot);
        string puri = person.Uri.Text;
        string? art = person.Image;
        var open = ProfileListVerbs.OpenPerson(e.Slot);
        // No avatar: the initials in the card's own inner width (its 8-DIP plate padding either side), not a blank hole.
        Element? cover = art is null
            ? PersonPicture.Create("", MathF.Max(1f, w - 2f * SurfaceGeometry.ShelfPlatePad), displayName: name)
            : null;
        var pdata = new Controls.CardData(puri, name, Sub(ProfileListVerbs.PersonSubtitle(e.Followers)), art,
            open, null, Circular: true, ShowMenu: false) { CoverOverride = cover };
        Element pcard = Controls.Surface(pdata, global::Wavee.Shape.Grid) with { Key = "card:" + puri };
        return new BoxEl { Key = "cell:" + puri, Width = w, Shrink = 0f, Direction = 1, Children = [pcard] };
    }

    static Element FollowLine(string uri, string name) => new BoxEl
    {
        Height = ProfileGridFit.ActionLine, Shrink = 0f, Direction = 0, Justify = FlexJustify.Center, AlignItems = FlexAlign.Start,
        Padding = new Edges4(0f, Spacing.S, 0f, 0f),
        Children =
        [
            Embed.Comp(() => new Controls.FollowToggle { Uri = uri, Name = name })
                with { Key = "follow:" + uri, SkeletonProxy = s_followShape },
        ],
    };

    static Element Sub(string text) => R.Type.TrackMeta(text) with
        { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };

    // ══ THE SHIMMER SOURCE: tool bones + two rows of circular seed cards ═════════════════════════════════════════════

    /// <summary>Derived once per pending→loaded edge; stateful leaves (the Follow toggle, the find box, the list) never
    /// mount in it. The tools are childless declared-size boxes (the deriver turns them into bars); each cell is the
    /// surface's own SEED face (already bones — handed to the deriver as-is, because derived, its aspect-only cover would
    /// collapse) over a Follow bone on every other cell of Following.</summary>
    static Element ShimmerOf(PageCache c)
    {
        var p = c.Props;
        bool chips = ProfileListFacets.HasChips(p.Facet());
        var plan = ProfileGridFit.For(p.BodyWidth(), Spacing.Card);
        int cols = plan.Columns > 0 ? plan.Columns : ClassicProfileListRules.ShimmerFallbackCols;

        var toolKids = new List<Element>(2);
        if (chips)
        {
            var bones = new Element[ClassicProfileListRules.ChipBones];
            for (int i = 0; i < bones.Length; i++)
                bones[i] = new BoxEl { Width = ClassicProfileListRules.ChipBoneW, Height = Controls.ChipHeight, Shrink = 0f, Corners = Radii.ControlAll };
            toolKids.Add(new BoxEl { Direction = 0, Gap = Spacing.S, Shrink = 0f, Children = bones });
        }
        toolKids.Add(new BoxEl { Width = Controls.FindBoxWidth, Height = ClassicProfileListRules.FindBoneH, Shrink = 0f, Corners = Radii.ControlAll });
        Element tools = new BoxEl
        {
            Direction = plan.StackTools ? (byte)1 : (byte)0, Gap = plan.StackTools ? Spacing.S : Spacing.M, MinWidth = 0f,
            AlignItems = plan.StackTools ? FlexAlign.Start : FlexAlign.Center,
            Justify = toolKids.Count > 1 ? FlexJustify.SpaceBetween : FlexJustify.End,
            Padding = new Edges4(0f, 0f, 0f, Spacing.XS),
            Children = toolKids.ToArray(),
        };

        var rowsEl = new Element[ClassicProfileListRules.ShimmerRows];
        for (int r = 0; r < rowsEl.Length; r++)
        {
            var cells = new Element[cols];
            for (int col = 0; col < cols; col++)
            {
                bool toggle = chips && (r + col) % 2 == 0;
                Element cell = new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children = toggle
                        ? [SurfaceParts.Seed(global::Wavee.Shape.Grid, float.NaN, circular: true), FollowBone()]
                        : [SurfaceParts.Seed(global::Wavee.Shape.Grid, float.NaN, circular: true)],
                };
                cells[col] = cell.Skel(cell);
            }
            rowsEl[r] = new BoxEl { Direction = 0, Gap = Spacing.Card, AlignItems = FlexAlign.Start, Children = cells };
        }
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.S, Grow = 1f, MinWidth = 0f, MinHeight = 0f,
            Children = [tools, new BoxEl { Direction = 1, Gap = Spacing.Card, MinWidth = 0f, Children = rowsEl }],
        };
    }

    /// <summary>A Follow toggle's bone, in its action line (the verbatim seed is not derived, so the bone is explicit).</summary>
    static Element FollowBone() => new BoxEl
    {
        Height = ProfileGridFit.ActionLine, Shrink = 0f, Direction = 0, Justify = FlexJustify.Center, AlignItems = FlexAlign.Start,
        Padding = new Edges4(0f, Spacing.S, 0f, 0f), IsEnabled = false,
        Children =
        [
            new BoxEl
            {
                Width = ClassicProfileListRules.FollowBoneW, Height = ClassicProfileListRules.FollowBoneH, Shrink = 0f,
                Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary,
            },
        ],
    };
}
