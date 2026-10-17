// ── Themes/Zune/Structure/Views/Profile/ZuneProfileList.cs — Zune's `Pages.ProfileList` (themes plan 6.3; P5 Z7) ──────────────────────────────
//
// NO BOARD (owner Q18): the Following / Followers list is built from Zune's parts. The two facets are the band's row 2 (the core host publishes
// them, each with its count: `ProfileLists.PageHost`), so the page is the body: the discovery heading row (the facet word, the person's name after it
// as a link back to the profile), the tools (Following has the All / Artists / People WORDS with their counts and the find box, Followers the find
// box alone) and the lettered grid of cards the core plans (`ProfileGridFit`: the columns, the cell width, the A-Z strip).
//
//  column  `zune-people-page`
//  ├─ HEAD (always real, outside the region)   `zune-people-head`
//  └─ BODY (the page gutters) ▸ the measured box (the host's body width, floored to 8 DIP) ▸ ONE SkelRegionEl (Soft; the bound list OWNS the scroll)
//          Pending     the derived shimmer (tool bones + two rows of circular seed cards)
//          Ready       [tools: words + find] over [ ZStack{ list · sticky letter } · jump strip ]
//          Empty / Hidden / Unavailable   a vacancy as CONTENT (no Retry); Failed   the error vacancy with Retry
//
// Every face is a KEYED CHILD of one stable root (a key on a single-child slot's root is inert), so a face change remounts. The delegates the region
// and the list were handed live in one cache per mounted template, built lazily, so the skeleton region is never re-armed by a fresh delegate. The
// cards and the slot are `ZuneProfileList.Cards.cs`; the letters are `ZuneProfileLetters.cs`.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using K = Wavee.Themes.Zune.ZuneDiscoveryKit;
using H = Wavee.Themes.Zune.ZuneHomeRules;
using Z = Wavee.Themes.Zune.ZuneProfileRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's profile list page.</summary>
internal static partial class ZuneProfileList
{
    static readonly string[] s_faceKeys =
    [
        Z.ListFacePrefix + "pending", Z.ListFacePrefix + "ready", Z.ListFacePrefix + "empty", Z.ListFacePrefix + "hidden",
        Z.ListFacePrefix + "unavailable", Z.ListFacePrefix + "failed",
    ];

    /// <summary>The registered template of <see cref="ViewKeys.ProfileListPage"/>.</summary>
    public static Element Template(ProfileListProps props, in ViewContext ctx)
    {
        var cache = ctx.Hooks.UseRef(new PageCache()).Value;
        var lay = ZuneProfileTiers.Of(ctx.Tier());
        cache.Tiers = ctx.TierReader();   // the tiers this page is drawn in, for the shimmer the region derives later
        cache.Props = props;
        cache.Target = lay.Target;
        cache.Prepare();
        if (!props.Parsed) return Controls.Vacancy(Controls.VacancyVoice.Error);

        Element region = new SkelRegionEl(
            Pending: props.Pending, Failed: props.Failed, Content: cache.Content!, ShimmerSource: cache.Shimmer!,
            OnFailed: cache.FailedPanel!, Reveal: SkelReveal.Soft, Style: SkeletonStyle.Default,
            Group: null, SmoothResize: false);
        Element body = new BoxEl
        {
            Key = Z.ListBodyKey, Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, MaxWidth = R.Space.PageMaxW,
            AlignSelf = FlexAlign.Stretch,
            Padding = new Edges4(lay.Gutter, 0f, lay.Gutter, Z.ListBottom),
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
            Key = Z.ListPageKey, Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Children = [Head(props.Head, lay), body],
        };
    }

    // ══ THE PER-INSTANCE CACHE ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What the old page kept in fields: the latest props, the stable delegates the region and the list were handed, and the elements built once
    /// (the sticky letter and the strip). Built lazily by <see cref="Prepare"/>; one per mounted template.</summary>
    sealed class PageCache
    {
        public ProfileListProps Props = null!;

        /// <summary>The page's tiers as a live reader (the facts its template is drawn in), for the builders that run outside the template.</summary>
        public Func<TierFacts> Tiers = static () => TierFacts.Unknown;

        /// <summary>The tier's hit target (40 at Compact): the words' height.</summary>
        public float Target = 32f;

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

    /// <summary>The facet's heading (the discovery heading voice) and the person's name after it: a link back to their profile. The facets themselves are
    /// the band's row 2.</summary>
    static Element Head(ProfileListHead head, ZuneProfileLayout lay)
    {
        TextEl name = R.Type.DenseMeta(head.Name) with
        {
            Color = Tok.TextSecondary, Case = TextCase.AsWritten, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        Element who = head.OpenProfile is { } go
            ? new BoxEl
            {
                Key = "zune-people-profile", Direction = 0, Shrink = 1f, MinWidth = 0f, MinHeight = lay.Target, AlignItems = FlexAlign.Center,
                Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, AllowFocusOnInteraction = false, OnClick = go,
                Children = [name with { HoverColor = Tok.TextPrimary }],
            }
            : new BoxEl { Direction = 0, Shrink = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, Children = [name] };
        return new BoxEl
        {
            Key = Z.ListHeadKey, Direction = 0, Gap = Z.ListHeadGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 0f,
            MaxWidth = R.Space.PageMaxW, AlignSelf = FlexAlign.Stretch,
            Padding = new Edges4(lay.Gutter, H.PageTop, lay.Gutter, Z.ListHeadBottom),
            Children = [K.HeadingText(head.Title), who],
        };
    }

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
            ? Controls.Vacancy(Controls.VacancyVoice.NoMatch) with { Key = Z.ListFacePrefix + "nomatch" }
            : ListBody(c, s);
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.S, Grow = 1f, MinWidth = 0f, MinHeight = 0f,
            Children = [Tools(c, s) with { Key = Z.ListToolsKey }, body],
        };
    }

    /// <summary>The tools: Following has the All / Artists / People words (each with its count) and the find box, Followers the find box alone. Wide, the
    /// words sit left and the find box right; on the list ladder's narrow arm (the host's plan) they stack. They stay mounted when a query narrows the
    /// list to nothing, so the query can be cleared.</summary>
    static Element Tools(PageCache c, ProfileListShape s)
    {
        string placeholder = Loc.Get(s.Facet == ProfileFacet.Followers ? Strings.Person.List.Find.Followers : Strings.Person.List.Find.Following);
        Element find = Controls.FindBox(c.Props.Query(), placeholder) with { Key = "zune-people-find" };
        bool stack = s.Plan.StackTools;
        var kids = new List<Element>(2);
        if (ProfileListFacets.HasChips(s.Facet))
        {
            var counts = s.Counts;
            var culture = CultureInfo.CurrentCulture;
            kids.Add(new BoxEl
            {
                Direction = 0, Gap = Z.WordGap, Shrink = 0f, AlignItems = FlexAlign.Center,
                Children =
                [
                    Word(c, ProfileChip.All, Strings.Person.List.Chip.All, counts.All, s.Chip, culture),
                    Word(c, ProfileChip.Artists, Strings.Person.List.Chip.Artists, counts.Artists, s.Chip, culture),
                    Word(c, ProfileChip.People, Strings.Person.List.Chip.People, counts.People, s.Chip, culture),
                ],
            });
        }
        kids.Add(find);
        return new BoxEl
        {
            Direction = stack ? (byte)1 : (byte)0, Gap = stack ? Spacing.S : Z.ToolsGap, MinWidth = 0f, Shrink = 0f,
            AlignItems = stack ? FlexAlign.Start : FlexAlign.Center,
            Justify = kids.Count > 1 ? FlexJustify.SpaceBetween : FlexJustify.End,
            Padding = new Edges4(0f, 0f, 0f, Z.ToolsBottom),
            Children = kids.ToArray(),
        };
    }

    /// <summary>One chip as a WORD: the selected one in the primary ink at 600, the rest secondary (disabled with no entries), the label in the style's case.</summary>
    static Element Word(PageCache c, ProfileChip chip, string wordKey, int count, ProfileChip selected, CultureInfo culture)
    {
        bool on = selected == chip, available = count > 0;
        var click = c.ChipClicks![(int)chip];
        string text = ProfileListFilter.WordCount(Loc.Get(wordKey), count, true, culture);
        TextEl word = R.Type.DenseTitle(text) with
        {
            Color = on ? Tok.TextPrimary : available ? Tok.TextSecondary : Tok.TextDisabled,
            HoverColor = available ? Tok.TextPrimary : Tok.TextDisabled,
            Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap, MinWidth = 0f,
        };
        if (on) word = word with { Weight = (ushort)R.Type.FacetCompactStyle.SelectedWeight };
        return new BoxEl
        {
            Key = "zune-people-word:" + (int)chip, Direction = 0, Shrink = 0f, MinHeight = c.Target, AlignItems = FlexAlign.Center,
            Cursor = available ? CursorId.Hand : CursorId.Arrow, Role = AutomationRole.Button, Focusable = available, IsEnabled = available,
            AllowFocusOnInteraction = false, OnClick = available ? click : null,
            Children = [word],
        };
    }

    /// <summary>The list: the bound view (a keyed child, remounted when its geometry changes) with the sticky letter over it, and the A-Z strip beside it
    /// when the body is wide enough. Rows scroll UNDER the pinned letter.</summary>
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
            Key = "zune-people-list-body", Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, ZStack = true, ClipToBounds = true,
            Children = [list, c.Sticky ??= ZuneProfileLetters.StickyLetter(p.StickyLetter, p.StickyPush)],
        };
        return new BoxEl
        {
            Key = "zune-people-list", Direction = 0, Gap = ProfileGridFit.StripGap, Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
            AlignItems = FlexAlign.Stretch,
            Children = s.Plan.Strip ? [zstack, c.Strip ??= ZuneProfileLetters.JumpStrip(p.Present, p.StickyLetter, p.Jump)] : [zstack],
        };
    }
}
