// ── Themes/Zune/Structure/Views/Profile/ZuneProfile.cs — Zune's `Pages.User` (themes plan 6.3; P5 Z7) ───────────────────────────────────
//
// Boards Z-Profile, -Compact, -Small, -Huge. The page's head is the band's row 2 (the core host publishes it: the person's name as the title, the
// sections as the views, "open account" and "share" as the actions), so the page is the body: the hero, then one chapter per section.
//
//   Pages.User
//   └─ Ctx.Provide(Design.AccentCtx = the page accent) ▸ Ctx.Provide(LazyScroll = the scroll offset)
//      └─ Column  `zune-profile-page`  OnBoundsChanged = the host's measure ▸ [the shell-tint claim, ScrollView `zune-profile-scroll:<route>`]
//         └─ column `zune-profile-column` (the frame's gutter, 8 under the band, capped at the style's page width)
//            └─ keyed child `zune-profile-body:<scope epoch>` ▸ SkelRegionEl (Soft)
//               ├─ Content  the "isn't available" vacancy, or [hero · the sections]
//               └─ Shimmer  the SAME hero over a placeholder, two seed shelves at the page's real width
//
//   THE HERO  the round avatar beside the copy: the eyebrow, the name (the tier's voice: 40 / 64 / 120), the stats as one line of words ("12 followers ·
//   48 following · 6 public playlists": the two people words link into their list), the actions row (Follow for another person, "open account" and the
//   share ring where the tier keeps them in the page, the "..." menu). ONE tree at every tier: the avatar, the name and the row are live props.
//   THE SECTIONS  each is ITS OWN skeleton region (a late list shimmers in place, never holding the hero), keyed by its section so the host's anchors
//   (the band's views scroll to them, the scroll spy lights them) register once. Top artists are ranked rows in the tier's columns, the rest shelves of
//   154 cards (`PagedShelf`: its card band is live, so a crossing keeps the shelf).
//
// What the view does NOT own: the demands, the load faces and the reveal latch, the section plan and each section's body verdict and cards, the
// accent, the scroll spy and its anchors, the tint claim and every verb (`Profile.PageHost`). The hero's collapse into a context band is Classic's:
// Zune's band is outside the page, so the hero does not pin or collapse and the core's hero metrics are unread.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using H = Wavee.Themes.Zune.ZuneHomeRules;
using K = Wavee.Themes.Zune.ZuneDiscoveryKit;
using Z = Wavee.Themes.Zune.ZuneProfileRules;

namespace Wavee.Themes.Zune;

/// <summary>The page view's latched state: plain fields that outlive a render (the view's hook cell). The delegates are built ONCE: a fresh delegate
/// would re-arm a skeleton region.</summary>
internal sealed class ZuneProfileLatch
{
    /// <summary>The props of the latest render (the derived skeleton and the failed panels read the live width and own flag through it).</summary>
    public ProfilePageProps P = null!;

    /// <summary>The content tier's record for the latest render.</summary>
    public ZuneProfileLayout Layout = ZuneProfileTiers.For(WidthTier.Standard);

    /// <summary>The page body of this render (null while it is not composed).</summary>
    public Element? Body;

    /// <summary>Each section's body verdict and element, latched per compose; the regions' delegates read these.</summary>
    public readonly ProfileSectionBody[] Verdict = new ProfileSectionBody[ProfileSections.Count];
    public readonly Element?[] Els = new Element?[ProfileSections.Count];
    public readonly Action[] Retry = new Action[ProfileSections.Count];

    public readonly Func<Element> Content, Shimmer, FailedPanel;
    public readonly Func<bool>[] SecPending = new Func<bool>[ProfileSections.Count], SecFailed = new Func<bool>[ProfileSections.Count];
    public readonly Func<Element>[] SecContent = new Func<Element>[ProfileSections.Count], SecShimmer = new Func<Element>[ProfileSections.Count],
        SecFailedPanel = new Func<Element>[ProfileSections.Count];

    public ZuneProfileLatch()
    {
        Content = () => Body ?? new BoxEl();
        Shimmer = () => ZuneProfile.Shimmer(this);
        FailedPanel = () => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: P.Retry);
        for (int i = 0; i < ProfileSections.Count; i++)
        {
            int k = i;
            var s = (ProfileSection)i;
            SecPending[k] = () => Verdict[k] == ProfileSectionBody.Seed;
            SecFailed[k] = () => Verdict[k] == ProfileSectionBody.Error;
            SecContent[k] = () => Els[k] ?? new BoxEl();
            SecShimmer[k] = () => ZuneProfileSections.SeedShelf(s, ZuneProfile.InnerWidth(P, Layout), Layout.CardW);
            SecFailedPanel[k] = () => ZuneProfileSections.SectionError(s, Retry[k]);
        }
    }
}

/// <summary>Zune's profile page.</summary>
internal static class ZuneProfile
{
    static readonly string[] s_secKeys = SectionKeys(Z.SectionPrefix);
    static readonly string[] s_anchorKeys = SectionKeys(Z.SectionPrefix + "anchor:");

    static string[] SectionKeys(string prefix)
    {
        var keys = new string[ProfileSections.Count];
        for (int i = 0; i < keys.Length; i++) keys[i] = prefix + ProfileSections.Key((ProfileSection)i);
        return keys;
    }

    /// <summary>The registered template of <see cref="ViewKeys.ProfilePage"/>.</summary>
    public static Element Template(ProfilePageProps p, in ViewContext ctx)
    {
        var lay = ZuneProfileTiers.Of(ctx.Tier());
        var latchCell = ctx.Hooks.UseRef<ZuneProfileLatch?>(null);
        var latch = latchCell.Value ??= new ZuneProfileLatch();
        latch.P = p;
        latch.Layout = lay;
        latch.Body = p.Unavailable ? ZuneProfileSections.Unavailable()
                   : p.BodyReady ? Compose(p, latch)
                   : null;

        // ONE page region; the shimmer is DERIVED from Shimmer (never mounted). Soft: the complete tree rises as one.
        Element region = new SkelRegionEl(
            Pending: p.Pending, Failed: p.Failed, Content: latch.Content, ShimmerSource: latch.Shimmer,
            OnFailed: latch.FailedPanel, Reveal: SkelReveal.Soft, Style: SkeletonStyle.Default,
            Group: null, SmoothResize: false);

        // The page column (A1): capped at 2400 + the gutters and centred, so the content shares the band's x at every width.
        Element column = ZuneFrameRules.PageRow(new BoxEl
        {
            Key = Z.ColumnKey, Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
            AlignItems = FlexAlign.Stretch,
            Padding = new Edges4(0f, H.PageTop, 0f, Z.PageBottom),
            // Keyed CHILD: a scope switch remounts the sections, whose anchors re-register after the reset.
            Children = [new BoxEl { Key = Z.BodyPrefix + p.ScopeKey, Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = [region] }],
        }, lay.Gutter);

        Element scroll = Ui.ScrollView(new BoxEl { Direction = 0, MinWidth = 0f, Children = [column] }) with
        {
            Key = Z.ScrollPrefix + p.RouteKey, Grow = 1f, MinWidth = 0f, ScrollKey = p.ScrollKey,
            OnRealized = p.CaptureViewport, Handle = p.Scroll,
        };

        // The page PROVIDES its accent (Design.AccentCtx): the heart and every other ambient consumer read it.
        return Ctx.Provide(Design.AccentCtx.Slot, p.PageAccent,
            Ctx.Provide(LazyScroll.Slot, (IReadSignal<float>?)p.ScrollY, new BoxEl
            {
                Key = Z.PageKey, Grow = 1f, Direction = 1, OnBoundsChanged = p.Measure,
                Children = [p.Tint, scroll],
            }));
    }

    /// <summary>The page's measure inside its gutters: the width less the gutters, capped at the page column's 2400 (A1).</summary>
    internal static float InnerWidth(ProfilePageProps p, ZuneProfileLayout lay)
        => MathF.Max(1f, MathF.Min(p.Width - 2f * lay.Gutter, ZuneFrameRules.PageCap));

    // ── the loaded page ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>[hero · the sections]. Plan and anchors were built by the host in ONE pass so they cannot disagree.</summary>
    static Element Compose(ProfilePageProps p, ZuneProfileLatch latch)
    {
        var lay = latch.Layout;
        var plan = p.Sections;
        var kids = new Element[plan.Count + 1];
        kids[0] = Hero(p.Hero, p.Uri, p.HeroActs, lay);
        for (int i = 0; i < plan.Count; i++)
        {
            var slot = plan[i];
            var s = slot.Section;
            int k = (int)s;
            latch.Verdict[k] = slot.Body;
            latch.Retry[k] = slot.Retry;
            latch.Els[k] = slot.Body switch
            {
                ProfileSectionBody.Cards => ZuneProfileSections.Shelf(s, slot.Cards, slot.SeeAll, lay),
                ProfileSectionBody.Empty => ZuneProfileSections.EmptyShelf(s, p.Own),
                _ => null,
            };
            // Each section is ITS OWN region: a late list shimmers in place, never holding the hero (Group null).
            Element region = new SkelRegionEl(
                Pending: latch.SecPending[k], Failed: latch.SecFailed[k], Content: latch.SecContent[k], ShimmerSource: latch.SecShimmer[k],
                OnFailed: latch.SecFailedPanel[k], Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default,
                Group: null, SmoothResize: false);
            // EVERY section keyed: sections appear mid-stream as data lands.
            kids[i + 1] = new BoxEl
            {
                Key = s_anchorKeys[k], Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, OnRealized = slot.Anchor,
                Children = [new BoxEl { Key = s_secKeys[k], Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = [region] }],
            };
        }
        return new BoxEl { Direction = 1, Gap = lay.SectionGap, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = kids };
    }

    // ── the skeleton ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A ShimmerSource, never mounted: the SAME hero over a placeholder (the action row a Skeletonized(false) spacer), and two seed shelves at the
    /// page's real width: own: Top artists + Public playlists; other: Public playlists + Recently played artists.</summary>
    internal static Element Shimmer(ZuneProfileLatch latch)
    {
        var p = latch.P;
        var lay = latch.Layout;
        float inner = InnerWidth(p, lay);
        var first = p.Own ? ProfileSection.TopArtists : ProfileSection.Playlists;
        var second = p.Own ? ProfileSection.Playlists : ProfileSection.RecentArtists;
        return new BoxEl
        {
            Direction = 1, Gap = lay.SectionGap, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Children =
            [
                Hero(ProfileHeroText.PlaceholderFor(p.Own), "", null, lay),
                ZuneProfileSections.SeedShelf(first, inner, lay.CardW),
                ZuneProfileSections.SeedShelf(second, inner, lay.CardW),
            ],
        };
    }

    // ── the hero ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The avatar beside the copy. Built for the live page AND for the derived shimmer (the deriver cannot run a view slot); a null
    /// <paramref name="acts"/> is the shimmer arm (shapes only).</summary>
    static Element Hero(in ProfileHeroText t, string uri, ProfileHeroActs? acts, ZuneProfileLayout lay)
    {
        Element avatar = Avatar(in t, lay.Avatar);
        Element copy = Copy(in t, uri, acts, lay);
        return new BoxEl
        {
            Key = Z.HeroKey, Direction = 0, Gap = lay.HeroGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children = [avatar, copy],
        };
    }

    /// <summary>The photo in a clipped circle; no photo: the initials on the person's own colour. The edge is a live prop and scales on a crossing.</summary>
    static Element Avatar(in ProfileHeroText t, float edge)
    {
        var round = CornerRadius4.All(edge / 2f);
        if (t.Placeholder)
            return new BoxEl { Key = "zune-profile-avatar", Width = edge, Height = edge, Shrink = 0f, Corners = round, Fill = Tok.FillSubtleSecondary, Layout = ZuneMotion.TitleScale };
        if (t.AvatarUrl is { Length: > 0 } url)
            return new BoxEl
            {
                Key = "zune-profile-avatar", Width = edge, Height = edge, Shrink = 0f, ZStack = true, ClipToBounds = true, Corners = round,
                BorderWidth = Z.AvatarBorder, BorderColor = Tok.StrokeCardDefault, Layout = ZuneMotion.TitleScale,
                Children = [Controls.Artwork(url, edge, edge, edge / 2f, decodePx: ProfileHeroLayout.AvatarDecodePx)],
            };
        ColorF? fill = t.PersonArgb != 0 ? ColorScience.ToColor(t.PersonArgb) : null;
        return new BoxEl
        {
            Key = "zune-profile-avatar", Width = edge, Height = edge, Shrink = 0f, Layout = ZuneMotion.TitleScale,
            Children = [PersonPicture.Create("", edge, displayName: t.Name, fill: fill) with { Shrink = 0f }],
        };
    }

    /// <summary>The eyebrow, the name, the stats, the actions.</summary>
    static Element Copy(in ProfileHeroText t, string uri, ProfileHeroActs? acts, ZuneProfileLayout lay)
    {
        // A name is a name: never re-cased, wrapped by whole words over up to NameLines lines (the box hugs them), the full name in the trim tooltip.
        TextEl name = (lay.Name switch
        {
            ZuneProfileName.Panorama => R.Type.Panorama(t.Name),
            ZuneProfileName.Hero => R.Type.DetailHero(t.Name),
            _ => R.Type.PageHero(t.Name),
        }) with
        {
            Color = Tok.TextPrimary, Case = TextCase.AsWritten, Wrap = TextWrap.WrapWholeWords, MaxLines = Z.NameLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, LineStacking = LineStacking.BlockLineHeight,
        };
        var kids = new List<Element>(4)
        {
            R.Type.DenseMeta(Loc.Get(Strings.Search.TypeUser)) with
            {
                Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
            // The box reserves the larger voice's line, so the word scales on a crossing and nothing under it moves.
            new BoxEl
            {
                Key = "zune-profile-name", Direction = 1, Justify = FlexJustify.Center, MinHeight = lay.NameLine, MinWidth = 0f, Layout = ZuneMotion.TitleScale,
                Children = [Controls.TrimmedTitle(name, t.Name)],
            },
            Stats(in t, acts, lay),
            Actions(in t, uri, acts, lay),
        };
        return new BoxEl
        {
            Direction = 1, Gap = Z.CopyGap, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center,
            Children = kids.ToArray(),
        };
    }

    // ── the stats: one line of words ─────────────────────────────────────────────────────────────────────────────────

    static Element Stats(in ProfileHeroText t, ProfileHeroActs? acts, ZuneProfileLayout lay)
    {
        Span<ProfileStatCell> cells = stackalloc ProfileStatCell[ProfileStats.Max];
        int n = ProfileStats.Plan(t.Own, t.ShowFollows, t.Followers, t.Following, t.Playlists, cells);
        var kids = new List<Element>(n * 2);
        for (int i = 0; i < n; i++)
        {
            if (i > 0) kids.Add(R.Type.DenseTitle("·") with { Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f });
            kids.Add(Stat(cells[i], acts, lay));
        }
        return new BoxEl
        {
            Direction = 0, Wrap = true, Gap = Z.StatGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = kids.ToArray(),
        };
    }

    /// <summary>"12 followers": a word (the style's label case). A cell that links (never Public playlists, never a zero) is a hyperlink into its list.</summary>
    static Element Stat(in ProfileStatCell c, ProfileHeroActs? acts, ZuneProfileLayout lay)
    {
        string text = ProfileStats.Numeral(c.Value, CultureInfo.CurrentCulture) + " " + StatLabel(in c);
        bool links = c.Links && acts is not null;
        TextEl word = R.Type.DenseTitle(text) with
        {
            Color = links ? Tok.TextPrimary : Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap,
            Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        if (!links) return new BoxEl { Direction = 0, Shrink = 1f, MinWidth = 0f, MinHeight = lay.StatH, AlignItems = FlexAlign.Center, Children = [word] };
        var open = acts!.OpenStat;
        var kind = c.Kind;
        return new BoxEl
        {
            Key = "zune-profile-stat:" + (int)kind, Direction = 0, Shrink = 1f, MinWidth = 0f, MinHeight = lay.StatH, AlignItems = FlexAlign.Center,
            Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, AllowFocusOnInteraction = false, OnClick = () => open(kind),
            Children = [word with { HoverColor = Tok.TextSecondary }],
        };
    }

    /// <summary>The caption of a stat, plural-aware (the followers key goes through its KEY constant: only <c>count</c> is bound).</summary>
    static string StatLabel(in ProfileStatCell c) => c.Kind switch
    {
        ProfileStat.Followers => Loc.Format(Strings.Person.Stat.FollowersKey, ("count", c.Value)),
        ProfileStat.Following => Loc.Get(Strings.Person.Stat.Following),
        _ => Strings.Person.Stat.Playlists(c.Value),
    };

    // ── the actions ──────────────────────────────────────────────────────────────────────────────────────────────────

    static readonly Func<Element> s_followShape = static () => new BoxEl();
    static readonly Action s_noop = static () => { };

    /// <summary>[open account (own, where the tier keeps it here)] [Follow (another person)] [share (own, same)] [the "..." menu]. Never skeleton
    /// content: <c>Skeletonized(false)</c> leaves a same-size blank in the shimmer. The row's height is the tier's target and is always there, so a
    /// crossing moves nothing under it.</summary>
    static Element Actions(in ProfileHeroText t, string uri, ProfileHeroActs? acts, ZuneProfileLayout lay)
    {
        float edge = lay.ActionsH;
        string name = t.Name;
        var kids = new List<Element>(4);
        if (acts is not null)
        {
            if (t.Own && lay.OwnVerbsInHero)
                kids.Add(Pill("zune-profile-open-account", Loc.Get(Strings.Person.OpenAccount), ProfileVerbs.OpenAccount, edge));
            if (!t.Own && uri.Length > 0)
                // Keyed on the uri: FollowTextAction carries its uri as a mount-frozen field.
                kids.Add(Embed.Comp(() => new ZuneArtistHero.FollowPill { Uri = uri, Name = name, Height = edge })
                    with { Key = "zune-profile-follow:" + uri, SkeletonProxy = s_followShape });
            if (t.Own && lay.OwnVerbsInHero)
                kids.Add(Controls.Named(Ring("zune-profile-share", Icons.Share, acts.Share, edge), Loc.Get(Strings.Menu.Share)));
            kids.Add(Embed.Comp(new MoreProps(edge, acts.Menu), static () => new MoreHost()) with { Key = "zune-profile-more" });
        }
        return new BoxEl
        {
            // A row that wraps (a floor, not a height): beside a 96 avatar a 300 window leaves 156 for "open account", share and "...", so the last ring
            // takes a second 40 row instead of running off the page. Wider windows keep the one row, and the shimmer's blank is the same one-row floor.
            Key = Z.ActionsKey, Direction = 0, Wrap = true, Gap = Z.ActionsGap, AlignItems = FlexAlign.Center, MinHeight = edge, Shrink = 0f, MinWidth = 0f,
            Margin = new Edges4(0f, Z.ActionsTop, 0f, 0f), Children = kids.ToArray(),
        }.Skeletonized(false);
    }

    /// <summary>An outlined word pill in the accent (the band's pill, at the hero's size).</summary>
    static Element Pill(string key, string label, Action go, float height) => new BoxEl
    {
        Key = key, Direction = 0, Height = height, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(Z.PillPadX, 0f, Z.PillPadX, 0f), Corners = CornerRadius4.All(height / 2f),
        BorderWidth = Z.PillStroke, BorderColor = Tok.AccentDefault, HoverFill = R.Layer.ChromeHover, PressedFill = R.Layer.ChromePressed,
        Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, OnClick = go,
        Children =
        [
            R.Type.DenseMeta(label) with
            {
                Color = R.Ink.AccentDecor, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap, MinWidth = 0f,
            },
        ],
    };

    /// <summary>A 2-dip outline ring with a glyph.</summary>
    static BoxEl Ring(string key, string glyph, Action go, float edge) => new()
    {
        Key = key, Width = edge, Height = edge, Shrink = 0f, Corners = CornerRadius4.All(edge / 2f), Direction = 0,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, BorderWidth = Z.RingStroke, BorderColor = Tok.TextPrimary,
        HoverFill = R.Layer.ChromeHover, PressedFill = R.Layer.ChromePressed,
        Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, BlocksDragArm = true, OnClick = go ?? s_noop,
        Children = [new TextEl(glyph) { Size = Z.RingGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Tok.TextPrimary }],
    };

    /// <summary>The "..." ring: the icon action re-enters the context funnel, so the menu attached to the ring opens anchored at it. Its menu is built
    /// lazily AT OPEN from the host's factory (a stable instance).</summary>
    sealed record MoreProps(float Size, Func<ContextMenuModel?> Menu)
    {
        public bool Equals(MoreProps? o) => o is not null && Size == o.Size;
        public override int GetHashCode() => Size.GetHashCode();
    }

    static readonly ContextMenuOptions s_moreMenu = new()
    {
        PointerPlacement = FlyoutPlacement.BottomEdgeAlignedLeft,
        KeyboardPlacement = FlyoutPlacement.BottomEdgeAlignedLeft,
    };

    sealed class MoreHost : Component
    {
        Func<ContextMenuModel?>? _menu;
        Func<ContextMenuModel?>? _factory;

        public override Element Render()
        {
            var p = UseProps<MoreProps>();
            _menu = p.Menu;
            _factory ??= () => _menu?.Invoke();
            var overlay = UseContext(Overlay.Service);
            float inner = p.Size - 2f * Z.RingStroke;
            BoxEl ring = new()
            {
                Width = p.Size, Height = p.Size, Shrink = 0f, Corners = CornerRadius4.All(p.Size / 2f), Direction = 0,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, BorderWidth = Z.RingStroke, BorderColor = Tok.TextPrimary,
                HoverFill = R.Layer.ChromeHover, PressedFill = R.Layer.ChromePressed, BlocksDragArm = true,
                Children = [Controls.IconAction(Icons.More, null, requestsContext: true, size: inner)],
            };
            if (!Controls.IsNullOverlay(overlay)) ring = ContextSheets.Attach(ring, overlay, _factory, s_moreMenu);
            return ToolTip.Wrap(ring, Loc.Get(Strings.Common.More));
        }
    }
}
