// ── Themes/Classic/Structure/Views/Pages/ClassicProfileHero.cs — Classic's profile hero (themes plan 6.1, T10c2) ────────────────
//
// From `Entities\Profile.UI.cs` §1 (`HeroBanner`, `Avatar`, `Identity`, `Stats`, `Stat`, `StatLabel`, `HeroActions`): the round-avatar hero and its collapse into the
// 56-DIP band (`ClassicArtistHero`'s pinning, verbatim). Horizontal tiers put avatar | copy bottom-aligned; stacked tiers put avatar over copy. Content sits on the hero's
// bottom edge inside a fixed tier height, so the sticky collapse is exact.
//
// The hero is built by the page view for the live page AND for the derived shimmer (the skeleton deriver cannot run a view slot); a null `acts` is the shimmer arm
// (shapes only). The tiers, the heights and the stat plan (`ProfileHeroLayout`, `ProfileStats`) are core: the artist hero's tiers decide them.
//
// TYPE (Design.cs "THE TYPE RAMP" — no new alias, no raw TextEl size): the name is ArtistDisplay / ArtistTitle / ArtistCompactTitle by tier; stats and ranks are
// StatHero (the sanctioned 350 cut); stat captions are Caption at 600 (Track.Drawer.Stat's idiom). No eyebrow over the name. Sentence case everywhere.

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

    /// <summary>The round-avatar hero and its collapse into the 56-DIP band: horizontal tiers put avatar | copy bottom-aligned; stacked tiers put avatar over copy.</summary>
    public static Element Build(in ProfileHeroText t, string uri, float width, in ProfileHeroMetrics m, bool compactCanHit,
                                ProfileHeroActs? acts, Element? band)
    {
        float w = MathF.Max(1f, width);
        float collapse = ProfileHeroLayout.CollapseDistance(in m);
        ScrollEffectSpec[] collapseEffects =
        [
            new(ScrollEffect.Parallax(0.0, collapse, 0f, -collapse)),
            new(ScrollEffect.Fade(ArtistHeroLayout.ExpandedFadeStart(collapse), collapse, 1f, 0f)),
        ];

        Element avatar = Avatar(in t, m.Avatar);
        Element copy = Identity(in t, uri, in m, acts);
        Element content = m.Stacked
            ? new BoxEl
            {
                Direction = 1, Gap = ProfileHeroLayout.StackedAvatarGap, AlignItems = FlexAlign.Start, MinWidth = 0f,
                Children = [avatar, copy],
            }
            : new BoxEl
            {
                Direction = 0, Gap = ProfileHeroLayout.AvatarGap, AlignItems = FlexAlign.End, MinWidth = 0f,
                Children = [avatar, copy],
            };
        Element expanded = new BoxEl
        {
            Width = w, Height = m.Height, Direction = 0, Justify = FlexJustify.Center,
            HitTestVisible = !compactCanHit, ScrollEffects = collapseEffects,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Justify = FlexJustify.End, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    MaxWidth = R.Space.PageMaxW,   // aligns with the magazine's 1600 measure
                    Padding = new Edges4(m.Gutter, m.TopPad, m.Gutter, m.BottomPad),
                    Children = [content],
                },
            ],
        };
        // Pinned at the viewport top and COLLAPSING into the compact band (`ClassicArtistHero`'s contract): the Leading
        // collapse cuts the expanded presentation at that edge by itself — paint and input — so no ClipToBounds here.
        return new BoxEl
        {
            Direction = 1, Height = m.Height, ZStack = true,
            Children = band is null ? [expanded] : [expanded, band],
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

    /// <summary>Name (the tier's Artist alias, line budget from the metrics) · stats · actions. No eyebrow.</summary>
    static BoxEl Identity(in ProfileHeroText t, string uri, in ProfileHeroMetrics m, ProfileHeroActs? acts)
    {
        TextEl name = (m.Tier switch
        {
            ArtistHeroTier.Wide => R.Type.ArtistDisplay(t.Name),
            ArtistHeroTier.Medium => R.Type.ArtistTitle(t.Name),
            _ => R.Type.ArtistCompactTitle(t.Name),
        }) with
        {
            Color = Tok.TextPrimary, Wrap = m.NameLines > 1 ? TextWrap.Wrap : TextWrap.NoWrap, MaxLines = m.NameLines,
            Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, Shrink = 1f,
            Grow = m.Stacked ? 0f : 1f, Basis = m.Stacked ? float.NaN : 0f,
            AlignSelf = m.Stacked ? FlexAlign.Stretch : FlexAlign.Auto,
            MaxWidth = m.CopyMaxWidth,
            Children =
            [
                // One line at every tier (the hero is sized for it — no empty band over a short name); a long name
                // ellipsises and shows in full in the trim tooltip.
                Controls.TrimmedTitle(name, t.Name),
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, Margin = new Edges4(0f, ProfileHeroLayout.NameToStats(in m), 0f, 0f),
                    Children = [Stats(in t, in m, acts)],
                },
                new BoxEl
                {
                    Direction = 0, MinWidth = 0f, Margin = new Edges4(0f, ProfileHeroLayout.StatsToActions(in m), 0f, 0f),
                    Children = [ActionRow(in t, uri, acts)],
                },
            ],
        };
    }

    /// <summary>The numerals — explicit rows (Narrow's 2-row budget splits 2 + 1), never a wrap, so the height the
    /// metrics budget is the height laid out.</summary>
    static Element Stats(in ProfileHeroText t, in ProfileHeroMetrics m, ProfileHeroActs? acts)
    {
        Span<ProfileStatCell> cells = stackalloc ProfileStatCell[ProfileStats.Max];
        int n = ProfileStats.Plan(t.Own, t.ShowFollows, t.Followers, t.Following, t.Playlists, cells);
        int perRow = ProfileStats.PerRow(n, m.StatRows);
        int rows = (n + perRow - 1) / perRow;
        var lines = new Element[rows];
        for (int r = 0; r < rows; r++)
        {
            int start = r * perRow, count = Math.Min(perRow, n - start);
            var kids = new Element[count];
            for (int i = 0; i < count; i++) kids[i] = Stat(cells[start + i], acts);
            lines[r] = new BoxEl
            {
                Direction = 0, Gap = ProfileHeroLayout.StatGap, AlignItems = FlexAlign.Start, MinWidth = 0f, Children = kids,
            };
        }
        return rows == 1 ? lines[0] : new BoxEl { Direction = 1, Gap = ProfileHeroLayout.StatRowGap, MinWidth = 0f, Children = lines };
    }

    /// <summary>A StatHero numeral over its sentence-case caption (Track.Drawer.Stat). A cell that links (the cell's
    /// own rule: never Public playlists, never a zero) is a subtle-plated button into the list route; the ±8/±4 margin
    /// keeps the TEXT on the copy's edge while the plate overhangs it.</summary>
    static Element Stat(in ProfileStatCell c, ProfileHeroActs? acts)
    {
        var cell = new BoxEl
        {
            Direction = 1, Shrink = 1f, MinWidth = 0f, Corners = Radii.ControlAll,
            Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
            Margin = new Edges4(-Spacing.S, -Spacing.XS, -Spacing.S, -Spacing.XS),
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

    /// <summary>[FollowToggle (others only)] [Share] [⋯] — the normal grammar (`ClassicArtistHero`'s action family). Never
    /// skeleton content: <c>Skeletonized(false)</c> leaves a same-size blank in the shimmer.</summary>
    static Element ActionRow(in ProfileHeroText t, string uri, ProfileHeroActs? acts)
    {
        string name = t.Name;
        var kids = new List<Element>(4);
        // Your own profile: the account lives on spotify.com — a stock button whose glyph says it leaves the app
        // (replaces the account flyout's old "Account" row, #161).
        if (t.Own)
            kids.Add(Button.Create(Loc.Get(Strings.Person.OpenAccount), ProfileVerbs.OpenAccount, ButtonAppearance.Standard,
                glyph: Icons.OpenInNewWindow));
        if (!t.Own)
            kids.Add(acts is null || uri.Length == 0
                ? Controls.FollowToggle.SkeletonShape()
                // Keyed on the uri: FollowToggle carries its uri as a mount-frozen field.
                : Embed.Comp(() => new Controls.FollowToggle { Uri = uri, Name = name })
                    with { Key = "profile-follow:" + uri, SkeletonProxy = s_followShape });
        kids.Add(ClassicDetailKit.CommandCircle(Icons.Share, Loc.Get(Strings.Menu.Share), acts?.Share ?? s_noop));
        kids.Add(acts is null
            ? new BoxEl { Width = Controls.IconButtonSize, Height = Controls.IconButtonSize, Corners = Radii.ControlAll }
            : ClassicDetailKit.MoreButton(acts.Menu, Controls.IconButtonSize) with { Key = "profile-more:" + uri });
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Height = ProfileHeroLayout.ActionRow,
            Children = kids.ToArray(),
        }.Skeletonized(false);
    }
}
