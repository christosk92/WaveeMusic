// ── Themes/Classic/Structure/Views/Pages/ClassicProfileList.cs — Classic's `Pages.ProfileList` (themes plan 6.1, T10c2) ─────────────────────────
//
// From `Entities\Profile.Lists.Page.cs` (`PageHost.Render`, `Head`, `Content`, `ReadyFace`, `Tools`, `ListBody`, `Shimmer`): the Following / Followers list page. The host
// (`ProfileLists.PageHost`, a `[CoreHost]`) keeps the demand, the pooled entry model, the load rule that picks the face, the grid plan, the letter projection with its pinned extents,
// the sticky-letter watch and the list's mount cache. The cards and the slot are `ClassicProfileList.Cards.cs`.
//
// ── THE SHAPE ────────────────────────────────────────────────────────────────────────────────────────────────────────
//
//  column
//  ├─ HEAD (always real, outside the region): the trail [name] over the 40/52 title (facet) · SelectorBar (a change NAVIGATES — the route is the truth, not the control;
//  │        Discography's head)
//  └─ BODY (PageWide gutters) └─ measured box (the host's body width, floored to 8 DIP) └─ ONE SkelRegionEl (Soft; no outer ScrollView — the bound list OWNS the scroll)
//          Pending     → the derived shimmer (tool bones + two rows of circular seed cards; stateful leaves never mount)
//          Ready       → [tools: chips + find] over [ ZStack{ list · StickyLetter } · JumpStrip ]
//          Empty / Hidden / Unavailable → Controls.Vacancy as CONTENT (no Retry); Failed → the error vacancy with Retry
//
// Every face is a KEYED CHILD of one stable root (rule 14: a key on a single-child slot's root is inert), so a face change remounts instead of morphing one tree into another. The
// delegates the old component kept as fields (the region's content, shimmer and failure, the row template, the chip clicks, the sticky letter and the strip) live in one cache per
// mounted template (`ctx.Hooks.UseRef`), built lazily, so the skeleton region is never re-armed by a fresh delegate.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's profile list page.</summary>
internal static partial class ClassicProfileList
{
    /// <summary>Registers the profile list page.</summary>
    internal static void Register(ViewRegistryBuilder views)
        => views.Add(ViewKeys.ProfileListPage, Template, "classic.profile.list");

    static readonly string[] s_faceKeys =
        ["people:pending", "people:ready", "people:empty", "people:hidden", "people:unavailable", "people:failed"];

    /// <summary>The registered template of <see cref="ViewKeys.ProfileListPage"/>.</summary>
    public static Element Template(ProfileListProps props, in ViewContext ctx)
    {
        var cache = ctx.Hooks.UseRef(new PageCache()).Value;
        cache.Props = props;
        cache.Prepare();
        if (!props.Parsed) return Controls.Vacancy(Controls.VacancyVoice.Error);

        Element head = Head(props.Head);
        Element region = new SkelRegionEl(
            Pending: props.Pending, Failed: props.Failed, Content: cache.Content!, ShimmerSource: cache.Shimmer!,
            OnFailed: cache.FailedPanel!, Reveal: SkelReveal.Soft, Style: SkeletonStyle.Default,
            Group: null, SmoothResize: false);
        Element body = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
            Padding = new Edges4(Spacing.PageWide, 0f, Spacing.PageWide, Spacing.L),
            Children =
            [
                // The measured box is INSIDE the gutters: its width is the list's, not the page's.
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
                    OnBoundsChanged = props.OnBodyBounds, Children = [region],
                },
            ],
        };
        return new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Children = [head, body],
        };
    }

    // ══ THE PER-INSTANCE CACHE ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What the old page kept in fields: the latest props, the stable delegates the region and the list were handed, and the elements built once (the sticky letter and the
    /// strip). Built lazily by <see cref="Prepare"/>; one per mounted template.</summary>
    sealed class PageCache
    {
        public ProfileListProps Props = null!;
        public Func<Element>? Content, Shimmer, FailedPanel;
        public Func<BoundItemScope<ProfileRowItem>, Element>? RowT;
        public Action[]? ChipClicks;
        public Element? Sticky, Strip;

        public void Prepare()
        {
            Content ??= () => ContentOf(this);
            Shimmer ??= () => ShimmerOf(this);
            FailedPanel ??= () => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: Props.Retry);
            RowT ??= scope => Embed.Comp(() => new RowSlot(this, scope));
            ChipClicks ??=
            [
                () => Props.SelectChip(ProfileChip.All),
                () => Props.SelectChip(ProfileChip.Artists),
                () => Props.SelectChip(ProfileChip.People),
            ];
        }
    }

    // ══ THE HEAD (always real) ═══════════════════════════════════════════════════════════════════════════════════════

    static Element Head(ProfileListHead head)
        => new BoxEl
        {
            Direction = 1, Gap = Spacing.S, MinWidth = 0f, Shrink = 0f,
            Padding = new Edges4(Spacing.PageWide, Spacing.XXL, Spacing.PageWide, Spacing.M),
            Children =
            [
                // Breadcrumb B: the profile is the one ancestor, the list is the title (never repeated in the trail).
                new BoxEl
                {
                    Direction = 1, Gap = ClassicPageHeadRules.TrailTitleGap, MinWidth = 0f,
                    Children =
                    [
                        ClassicPageHead.Trail([new PageCrumb(head.Name, head.OpenProfile)]),
                        R.Type.SurfaceDisplay(head.Title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                    ],
                },
                // A FRESH signal per render: the ROUTE, not the control, is the truth; a change navigates and the next
                // route re-seeds it (Discography's head).
                SelectorBar.Create(head.Labels, new Signal<int>(ProfileListFacets.IndexOf(head.Facet)), onChange: head.PickFacet),
            ],
        };

    // ══ THE REGION'S FACES ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Content (everything but Pending / Failed). Every face is a KEYED CHILD of one stable root.</summary>
    static Element ContentOf(PageCache c)
    {
        var p = c.Props;
        var s = p.Shape.Value;
        Element face = s.Load switch
        {
            ProfileListLoad.Ready => ReadyFace(c, s),
            ProfileListLoad.Empty => Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Page,
                Loc.Get(s.Facet == ProfileFacet.Followers ? Strings.Person.List.Empty.Followers : Strings.Person.List.Empty.Following), ""),
            ProfileListLoad.Hidden => Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Page,
                Loc.Get(Strings.Person.List.Hidden.Title), Strings.Person.List.Hidden.Subtitle(p.DisplayName())),
            ProfileListLoad.Unavailable => Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Page,
                Loc.Get(Strings.Person.List.Unavailable.Title), Loc.Get(Strings.Person.List.Unavailable.Subtitle)),
            _ => new BoxEl(),
        };
        return new BoxEl
        {
            Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f,
            Children = [face with { Key = s_faceKeys[(int)s.Load] }],
        };
    }

    static Element ReadyFace(PageCache c, ProfileListShape s)
    {
        Element body = s.Visible == 0
            ? Controls.Vacancy(Controls.VacancyVoice.NoMatch) with { Key = "people:nomatch" }
            : ListBody(c, s);
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.S, Grow = 1f, MinWidth = 0f, MinHeight = 0f,
            Children = [Tools(c, s) with { Key = "people:tools" }, body],
        };
    }

    /// <summary>The tools: Following has the All / Artists / People chips and the find box, Followers the find box alone.
    /// Wide, chips sit left and find right; below <see cref="ProfileGridFit.ToolsStackBelow"/> they stack. They stay mounted
    /// when a query narrows the list to nothing, so the query can be cleared.</summary>
    static Element Tools(PageCache c, ProfileListShape s)
    {
        string placeholder = Loc.Get(s.Facet == ProfileFacet.Followers ? Strings.Person.List.Find.Followers : Strings.Person.List.Find.Following);
        Element find = Controls.FindBox(c.Props.Query(), placeholder) with { Key = "people:find" };
        bool stack = s.Plan.StackTools;
        var kids = new List<Element>(2);
        if (ProfileListFacets.HasChips(s.Facet))
        {
            var counts = s.Counts;
            var culture = CultureInfo.CurrentCulture;
            kids.Add(new BoxEl
            {
                Direction = 0, Gap = Spacing.S, Shrink = 0f, AlignItems = FlexAlign.Center,
                Children =
                [
                    Chip(c, ProfileChip.All, Strings.Person.List.Chip.All, counts.All, s.Chip, culture),
                    Chip(c, ProfileChip.Artists, Strings.Person.List.Chip.Artists, counts.Artists, s.Chip, culture),
                    Chip(c, ProfileChip.People, Strings.Person.List.Chip.People, counts.People, s.Chip, culture),
                ],
            });
        }
        kids.Add(find);
        return new BoxEl
        {
            Direction = stack ? (byte)1 : (byte)0, Gap = stack ? Spacing.S : Spacing.M, MinWidth = 0f, Shrink = 0f,
            AlignItems = stack ? FlexAlign.Start : FlexAlign.Center,
            Justify = kids.Count > 1 ? FlexJustify.SpaceBetween : FlexJustify.End,
            Padding = new Edges4(0f, 0f, 0f, Spacing.XS),
            Children = kids.ToArray(),
        };
    }

    static Element Chip(PageCache c, ProfileChip chip, string wordKey, int count, ProfileChip selected, CultureInfo culture)
        => Controls.Chip(ProfileListFilter.WordCount(Loc.Get(wordKey), count, true, culture), selected == chip, count > 0,
                         c.ChipClicks![(int)chip]);

    /// <summary>The list: the bound view (a keyed child, remounted when its geometry changes) with the sticky letter over
    /// it, and the A–Z strip beside it when the body is wide enough. Rows scroll UNDER the pinned letter: clipped at exactly
    /// its height with a feather.</summary>
    static Element ListBody(PageCache c, ProfileListShape s)
    {
        var p = c.Props;
        var mount = p.Mount(s.MountKey);
        Element list = new BoxEl
        {
            Key = s.MountKey, Grow = 1f, Direction = 1, MinHeight = 0f,
            Children = [ItemsView.CreateBound(p.Rows, c.RowT!, mount.Layout, mount.Options)],
        };
        Element zstack = new BoxEl
        {
            Key = "people:body", Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, ZStack = true, ClipToBounds = true,
            Children = [list, c.Sticky ??= ClassicLetterKit.StickyLetter(p.StickyLetter, p.StickyPush)],
        };
        return new BoxEl
        {
            Key = "people:list", Direction = 0, Gap = ProfileGridFit.StripGap, Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
            AlignItems = FlexAlign.Stretch,
            Children = s.Plan.Strip ? [zstack, c.Strip ??= ClassicLetterKit.JumpStrip(p.Present, p.StickyLetter, p.Jump)] : [zstack],
        };
    }
}
