// ── Themes/Classic/Structure/Views/Pages/ClassicProfileHero.cs — Classic's profile hero (themes plan 6.1, T10c2) ────────────────
//
// From `Entities\Profile.UI.cs` §1 (`HeroBanner`, `Avatar`, `Identity`, `Stats`, `Stat`, `StatLabel`, `HeroActions`): the round-avatar hero and its collapse into the
// 56-DIP band (`ClassicArtistHero`'s pinning, verbatim). Horizontal tiers put avatar | copy bottom-aligned; stacked tiers put avatar over copy. Content sits on the hero's
// bottom edge inside a fixed tier height, so the sticky collapse is exact.
//
// The hero is built by the page view for the live page AND for the derived shimmer (the skeleton deriver cannot run a view slot); a null `acts` is the shimmer arm
// (shapes only). The sub-tier is the artist hero's; what each lays out per window tier is Classic's tier records (`Rules\Tiers.Profile.cs`, TC6: the core latches
// the metrics through them); the stat plan (`ProfileStats`) and the collapse arithmetic (`ProfileHeroLayout`) are core.
//
// TYPE (Design.cs "THE TYPE RAMP" — no new alias, no raw TextEl size): the name is ArtistDisplay / ArtistTitle / ArtistCompactTitle by tier; stats and ranks are
// StatHero (the sanctioned 350 cut); stat captions are Caption at 600 (Track.Drawer.Stat's idiom). The boards' "Profile" eyebrow (12/600, secondary) sits over
// the name where the form shows it (Compact, Small, Huge; the tuned Standard forms keep none). Sentence case everywhere.

using System.Globalization;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's profile hero.</summary>
internal static class ClassicProfileHero
{
    static readonly Func<Element> s_followShape = static () => Controls.FollowToggle.SkeletonShape();
    static readonly Action s_noop = static () => { };

    /// <summary>The round-avatar hero and its collapse into the 56-DIP band, in its tier's <paramref name="form"/> (`Rules\Tiers.Profile.cs`): the row arm puts
    /// avatar | copy bottom-aligned (the actions under the stats or at the row's right end), the stacked arm the avatar over the copy on the left, the centred arm
    /// (C-Profile-Compact) the avatar over the copy, centred. The pinned root's height tweens on a tier crossing.</summary>
    public static Element Build(in ProfileHeroText t, string uri, float width, in ProfileHeroMetrics m, ClassicProfileHeroForm form, float pageMax,
                                bool compactCanHit, ProfileHeroActs? acts, Element? band)
    {
        float w = MathF.Max(1f, width);
        float collapse = ProfileHeroLayout.CollapseDistance(in m);
        ScrollEffectSpec[] collapseEffects =
        [
            new(ScrollEffect.Parallax(0.0, collapse, 0f, -collapse)),
            new(ScrollEffect.Fade(ArtistHeroLayout.ExpandedFadeStart(collapse), collapse, 1f, 0f)),
        ];

        Element avatar = Avatar(in t, m.Avatar);
        Element copy = CopyBlock(in t, uri, in m, form, acts);
        Element content = form.Arm switch
        {
            ClassicProfileHeroArm.Centred => new BoxEl
            {
                Direction = 1, Gap = form.AvatarGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children = [avatar, copy],
            },
            ClassicProfileHeroArm.Stacked => new BoxEl
            {
                Direction = 1, Gap = form.AvatarGap, AlignItems = FlexAlign.Start, MinWidth = 0f,
                Children = [avatar, copy],
            },
            _ => new BoxEl
            {
                Direction = 0, Gap = form.AvatarGap, AlignItems = FlexAlign.End, MinWidth = 0f,
                Children = [avatar, copy],
            },
        };
        // The expanded presentation eases its height with the hero's (its content stands on its foot, so the copy rides the eased edge).
        Element expanded = new BoxEl
        {
            Width = w, Height = m.Height, Direction = 0, Justify = FlexJustify.Center, Animate = ClassicTierMotion.HeightReflow,
            HitTestVisible = !compactCanHit, ScrollEffects = collapseEffects,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Justify = FlexJustify.End, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    MaxWidth = pageMax,   // aligns with the magazine's measure
                    Padding = new Edges4(m.Gutter, m.TopPad, m.Gutter, m.BottomPad),
                    Children = [content],
                },
            ],
        };
        // Pinned at the viewport top and COLLAPSING into the compact band (`ClassicArtistHero`'s contract): the Leading
        // collapse cuts the expanded presentation at that edge by itself — paint and input — so no ClipToBounds here.
        // A tier crossing eases the hero's height THROUGH REAL LAYOUT (the artist hero's B2c recipe, 280 ms SmoothOut), so the collapse edge, which
        // cuts the presentation at the laid-out height, and the magazine under it move with it. A revealed height (the presented extent easing over a
        // layout already at its new height) cut the presentation at the new height at once and opened a void over the magazine still gliding to it.
        return new BoxEl
        {
            Direction = 1, Height = m.Height, ZStack = true, Animate = ClassicTierMotion.HeightReflow,
            Children = band is null ? [expanded] : [expanded, band.Value],
        }.Sticky(0f).Collapse(collapse, ArtistHeroLayout.CompactIdentityHeight, CollapseAnchor.Leading);
    }

    /// <summary>The photo in a clipped circle (`ClassicArtistPick`'s pick-avatar precedent: <c>Controls.Artwork</c>, so the tile is
    /// the WATCHED placeholder, not PersonPicture's frozen fill); no photo → PersonPicture initials on the person's own
    /// colour.</summary>
    static Element Avatar(in ProfileHeroText t, float edge)
    {
        if (t.Placeholder)
            return new BoxEl { Width = edge, Height = edge, Shrink = 0f, Corners = Radii.Circle(edge), Fill = Tok.FillSubtleSecondary };
        if (t.AvatarUrl is { Length: > 0 } url)
            return new BoxEl
            {
                Width = edge, Height = edge, Shrink = 0f, ZStack = true, ClipToBounds = true,
                Corners = Radii.Circle(edge), BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Shadow = Elevation.Card,
                Children = [Controls.Artwork(url, edge, edge, edge / 2f, decodePx: ProfileHeroLayout.AvatarDecodePx)],
            };
        ColorF? fill = t.PersonArgb != 0 ? ColorScience.ToColor(t.PersonArgb) : null;
        return PersonPicture.Create("", edge, displayName: t.Name, fill: fill) with { Shrink = 0f, Shadow = Elevation.Card };
    }

    /// <summary>The eyebrow (where the form shows it) · name (the form's Artist alias, one line) · stats · actions (when they sit under the copy).</summary>
    static BoxEl Identity(in ProfileHeroText t, string uri, in ProfileHeroMetrics m, ClassicProfileHeroForm form, ProfileHeroActs? acts)
    {
        bool centred = form.Arm == ClassicProfileHeroArm.Centred;
        TextEl name = (form.Name switch
        {
            ClassicArtistName.Display => R.Type.ArtistDisplay(t.Name),
            ClassicArtistName.Title => R.Type.ArtistTitle(t.Name),
            _ => R.Type.ArtistCompactTitle(t.Name),
        }) with
        {
            Color = Tok.TextPrimary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        var kids = new List<Element>(4);
        if (form.Eyebrow)
            kids.Add(new BoxEl
            {
                Direction = 0, MinWidth = 0f, Height = ClassicProfileTiers.EyebrowLine, Margin = new Edges4(0f, 0f, 0f, form.EyebrowGap),
                AlignItems = FlexAlign.Center, Justify = centred ? FlexJustify.Center : FlexJustify.Start,
                Children = [R.Type.Eyebrow(Loc.Get(Strings.Search.TypeUser)) with { Color = Tok.TextSecondary, MaxLines = 1 }],
            });
        // One line at every tier (the hero is sized for it — no empty band over a short name); a long name
        // ellipsises and shows in full in the trim tooltip.
        kids.Add(Controls.TrimmedTitle(name, t.Name, centred: centred));
        if (form.StatRows > 0)
            kids.Add(new BoxEl
            {
                Direction = 1, MinWidth = 0f, Margin = new Edges4(0f, form.NameToStats, 0f, 0f),
                AlignItems = centred ? FlexAlign.Center : FlexAlign.Stretch,
                Children = [Stats(in t, form, acts)],
            });
        // Beside the actions (a row block) the copy takes the row's free width; over them (a column block) its own height.
        bool beside = form.Actions && form.ActionsBeside;
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, Shrink = 1f,
            Grow = beside ? 1f : 0f, Basis = beside ? 0f : float.NaN,
            AlignSelf = beside ? FlexAlign.Auto : FlexAlign.Stretch,
            AlignItems = centred ? FlexAlign.Center : FlexAlign.Stretch,
            MaxWidth = m.CopyMaxWidth,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The copy and the action row in ONE block at every form, so the row keeps its node when a crossing moves it (1008, 1920): beside the
    /// copy at its right end (a row block, the Small and Huge forms) or under the stats (a column block). A form without actions (the Short strip)
    /// keeps the holder, hidden.</summary>
    static Element CopyBlock(in ProfileHeroText t, string uri, in ProfileHeroMetrics m, ClassicProfileHeroForm form, ProfileHeroActs? acts)
    {
        bool centred = form.Arm == ClassicProfileHeroArm.Centred;
        bool row = form.Arm == ClassicProfileHeroArm.Row;
        bool beside = form.Actions && form.ActionsBeside;
        var actions = new BoxEl
        {
            Key = "profile-hero-actions-slot", Direction = 0, MinWidth = 0f, Shrink = 0f, Visible = form.Actions,
            Margin = beside ? default : new Edges4(0f, form.StatsToActions, 0f, 0f),
            Justify = centred ? FlexJustify.Center : FlexJustify.Start,
            Children = [ActionRow(in t, uri, acts, form)],
        };
        return new BoxEl
        {
            Direction = (byte)(beside ? 0 : 1), Gap = beside ? form.AvatarGap : 0f, MinWidth = 0f, Shrink = 1f,
            AlignItems = beside ? FlexAlign.End : FlexAlign.Stretch,
            // In the row arm the block is the elastic lane beside the avatar; stacked or centred it spans the column.
            Grow = row ? 1f : 0f, Basis = row ? 0f : float.NaN, AlignSelf = row ? FlexAlign.Auto : FlexAlign.Stretch,
            Children = [Identity(in t, uri, in m, form, acts), actions],
        };
    }

    /// <summary>The numerals — explicit rows (a two-row budget splits 2 + 1), never a wrap, so the height the form budgets is the height laid out.</summary>
    static Element Stats(in ProfileHeroText t, ClassicProfileHeroForm form, ProfileHeroActs? acts)
    {
        Span<ProfileStatCell> cells = stackalloc ProfileStatCell[ProfileStats.Max];
        int n = ProfileStats.Plan(t.Own, t.ShowFollows, t.Followers, t.Following, t.Playlists, cells);
        int perRow = ProfileStats.PerRow(n, form.StatRows);
        int rows = (n + perRow - 1) / perRow;
        bool centred = form.Arm == ClassicProfileHeroArm.Centred;
        var lines = new Element[rows];
        for (int r = 0; r < rows; r++)
        {
            int start = r * perRow, count = Math.Min(perRow, n - start);
            var kids = new Element[count];
            for (int i = 0; i < count; i++) kids[i] = Stat(cells[start + i], acts, form);
            lines[r] = new BoxEl
            {
                Direction = 0, Gap = form.StatGap, AlignItems = FlexAlign.Start, MinWidth = 0f,
                Justify = centred ? FlexJustify.Center : FlexJustify.Start, Children = kids,
            };
        }
        return rows == 1 ? lines[0] : new BoxEl { Direction = 1, Gap = ClassicProfileTiers.StatRowGap, MinWidth = 0f, Children = lines };
    }

    /// <summary>A StatHero numeral over its sentence-case caption (Track.Drawer.Stat). A cell that links (the cell's
    /// own rule: never Public playlists, never a zero) is a subtle-plated button into the list route; the ±8/±4 margin
    /// keeps the TEXT on the copy's edge while the plate overhangs it. At Compact a cell is a 96 x 56 target, centred (C-Profile-Compact).</summary>
    static Element Stat(in ProfileStatCell c, ProfileHeroActs? acts, ClassicProfileHeroForm form)
    {
        bool target = form.StatMinH > 0f;
        var cell = new BoxEl
        {
            Direction = 1, Shrink = 1f, MinWidth = target ? form.StatMinW : 0f, MinHeight = form.StatMinH, Corners = Radii.ControlAll,
            AlignItems = target ? FlexAlign.Center : FlexAlign.Start, Justify = target ? FlexJustify.Center : FlexJustify.Start,
            Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
            Margin = target ? default : new Edges4(-Spacing.S, -Spacing.XS, -Spacing.S, -Spacing.XS),
            Children =
            [
                R.Type.StatHero(ProfileStats.Numeral(c.Value, CultureInfo.CurrentCulture), null),
                Caption(StatLabel(in c)) with
                {
                    Weight = 600, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
            ],
        };
        if (!c.Links || acts is null) return cell;
        var open = acts.OpenStat;
        var kind = c.Kind;
        return (cell with { Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = () => open(kind) })
            .Interactive(Interaction.Subtle);
    }

    /// <summary>The caption under a numeral, plural-aware. The followers key goes through its KEY constant: the typed
    /// accessor the loc generator emits for a plural whose branches are single bare words
    /// (<c>one {Follower} other {Followers}</c>) reads each word as another argument, so only <c>count</c> is bound here.</summary>
    static string StatLabel(in ProfileStatCell c) => c.Kind switch
    {
        ProfileStat.Followers => Loc.Format(Strings.Person.Stat.FollowersKey, ("count", c.Value)),
        ProfileStat.Following => Loc.Get(Strings.Person.Stat.Following),
        _ => Strings.Person.Stat.Playlists(c.Value),
    };

    /// <summary>[FollowToggle (others only)] [Share] [⋯] — the normal grammar (`ClassicArtistHero`'s action family), at the form's target (40 at Compact).
    /// Never skeleton content: <c>Skeletonized(false)</c> leaves a same-size blank in the shimmer. Keyed, so the tier tests find the row.</summary>
    static Element ActionRow(in ProfileHeroText t, string uri, ProfileHeroActs? acts, ClassicProfileHeroForm form)
    {
        string name = t.Name;
        float edge = form.Target;
        var size = edge >= ClassicArtistTiers.CompactTarget ? ControlSize.Large : ControlSize.Medium;
        var kids = new List<Element>(4);
        // Your own profile: the account lives on spotify.com — a stock button whose glyph says it leaves the app
        // (replaces the account flyout's old "Account" row, #161).
        if (t.Own)
            kids.Add(Button.Create(Loc.Get(Strings.Person.OpenAccount), ProfileVerbs.OpenAccount, ButtonAppearance.Standard, size: size,
                glyph: Icons.OpenInNewWindow));
        if (!t.Own)
            kids.Add(acts is null || uri.Length == 0
                ? Controls.FollowToggle.SkeletonShape()
                // Keyed on the uri: FollowToggle carries its uri as a mount-frozen field.
                : Embed.Comp(() => new Controls.FollowToggle { Uri = uri, Name = name, Size = size })
                    with { Key = "profile-follow:" + uri, SkeletonProxy = s_followShape });
        kids.Add(Controls.Named(Controls.IconAction(Icons.Share, acts?.Share ?? s_noop, size: edge), Loc.Get(Strings.Menu.Share)));
        kids.Add(acts is null
            ? new BoxEl { Width = edge, Height = edge, Corners = Radii.ControlAll }
            : ClassicDetailKit.MoreButton(acts.Menu, edge) with { Key = "profile-more:" + uri });
        return new BoxEl
        {
            Key = "profile-hero-actions", Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Height = edge, Shrink = 0f,
            Children = kids.ToArray(),
        }.Skeletonized(false);
    }
}
