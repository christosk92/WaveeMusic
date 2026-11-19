// ── Themes/WinUi/Structure/Views/Discovery/Home/WinUiHomeSpotlight.cs — WinUI's Release Spotlight ("From artists you follow") ──────────────
//
// The concept's WinUI skin, drawn with WinUI's own album head (WinUiDetail: the Eyebrow over the DetailHero title, the artist as an accent
// link beside its round photo, the meta in the caption voice, the Accent Play button) and its Spine Shelf open card (the cover's colour washed
// from the card's corner into the card fill, Controls.SaveButton, the subtle square sound button): the card washed in the cover's own colour
// from its top-left corner, the cover on the left, the words beside it, an EP's first songs as WinUI list rows (number, title, duration), the
// canvas as a tall tile on the right. Mechanics, preview and motion are core (Home/ReleaseSpotlight); numbers are WinUiSpotlightRules.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using X = Wavee.Themes.WinUi.WinUiSpotlightRules;

namespace Wavee.Themes.WinUi;

internal static class WinUiHomeSpotlight
{
    /// <summary>The spotlight body of a ReleaseSpotlight zone at the tier of <paramref name="layout"/>.</summary>
    public static Element Body(Zone zone, WinUiHomeLayout layout, ShelfController? controller)
        => ReleaseSpotlightView.Of(zone, X.Of(layout.Tier), Skin, controller, Icons.ChevronLeft, Icons.ChevronRight);

    /// <summary>The body's height (the chapter's extent under its header).</summary>
    public static float Extent(Zone zone, WinUiHomeLayout layout) => ReleaseSpotlightView.Extent(zone, X.Of(layout.Tier));

    static readonly ReleaseSpotlightSkin Skin = new(Frame, Cover, Copy, TrackRow, NextCover,
        static () => Tok.FillSubtleSecondary, static () => Tok.AccentDefault, WinUiDiscoveryMotion.SwapFade, WinUiDiscoveryMotion.SwapFadeMotion,
        WinUiDiscoveryMotion.SizeReflow with { Axes = SizeAxes.Height });

    static WinUiSpotlightTier TierOf(ReleaseSpotlightGeometry g) => g.Tier is WinUiSpotlightTier t ? t : default;

    // ── the card ──

    /// <summary>The card: the card fill under the cover's colour washed from the top-left corner (the concept's radial at 0 0), 8 corners.</summary>
    static Element Frame(SpotlightItem item, ReleaseSpotlightGeometry g, Element content) => new BoxEl
    {
        Direction = 1, Grow = 1f, MinWidth = 0f, ClipToBounds = true, Corners = CornerRadius4.All(g.Radius),
        Fill = Tok.FillSolidBase, Gradient = Wash(item.Accent),
        Children = [content],
    };

    static GradientSpec Wash(uint accent)
    {
        var card = Tok.FillSolidBase;
        var c = accent != 0u ? ColorScience.ToColor(accent) : card;
        return new GradientSpec(GradientShape.Radial, 0f,
            [new GradientStop(0f, ColorF.Lerp(card, c, X.WashMix)), new GradientStop(X.WashMidAt, ColorF.Lerp(card, c, X.WashMid)), new GradientStop(X.WashFadeAt, card), new GradientStop(1f, card)])
        {
            RadialCenter = new Point2(0f, 0f), RadialRadius = new Point2(1f, 1f),
        };
    }

    static Element Cover(SpotlightItem item, ReleaseSpotlightGeometry g)
    {
        var c = item.Card;
        float w = g.Narrow ? g.Cover : g.Cover, h = g.Narrow ? g.NarrowCoverH : g.Cover;
        return new BoxEl
        {
            Shrink = 0f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, OnClick = item.IsBlank ? null : () => HomeCardNav.Open(in c),
            Children = [Controls.Artwork(item.Cover, w, h, g.CoverRadius)],
        };
    }

    // ── the words ──

    static Element Copy(SpotlightItem item, SpotlightFacts facts, ReleaseSpotlightGeometry g, Element? tracks)
    {
        var t = TierOf(g);
        var c = item.Card;
        var kids = new List<Element>(6)
        {
            Eyebrow(facts),
            new BoxEl
            {
                Direction = 0, MinWidth = 0f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = !item.IsBlank,
                OnClick = item.IsBlank ? null : () => HomeCardNav.Open(in c),
                Children = [(t.HeroTitle ? R.Type.DetailHero(item.Title) : R.Type.ModuleHeader(item.Title)) with
                {
                    MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                }],
            },
            Ui.Caption(facts.Meta) with { Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
        };
        if (tracks is { } list) kids.Add(new BoxEl { Direction = 1, MinWidth = 0f, Margin = new Edges4(0f, Spacing.S, 0f, 0f), Children = [list] });
        kids.Add(Actions(item, g, t));
        return new BoxEl { Direction = 1, Gap = Spacing.XS, MinWidth = 0f, Children = kids.ToArray() };
    }

    /// <summary>"New single from" beside the artist's round photo, the artist as an accent link (WinUI's album head names its artists so).</summary>
    static Element Eyebrow(SpotlightFacts f)
    {
        string uri = f.ArtistUri, name = f.Artist;
        var kids = new List<Element>(4)
        {
            Controls.Artwork(f.ArtistImage, X.Avatar, X.Avatar, X.Avatar / 2f),
        };
        if (f.Lead.Length > 0) kids.Add(R.Type.Eyebrow(f.Lead.TrimEnd()) with { Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f });
        kids.Add(new BoxEl
        {
            Direction = 0, MinWidth = 0f, Shrink = 1f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = uri.Length > 0,
            OnClick = uri.Length > 0 ? () => HomeVerbs.OpenEntity(uri, name) : null,
            Children = [R.Type.Eyebrow(name) with
            {
                Color = Tok.AccentTextPrimary, HoverColor = Tok.AccentTextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap,
                Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            }],
        });
        if (f.Tail.Length > 0) kids.Add(R.Type.Eyebrow(f.Tail.TrimStart()) with { Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f });
        return new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f, Height = X.Avatar, Children = kids.ToArray() };
    }

    static Element Actions(SpotlightItem item, ReleaseSpotlightGeometry g, WinUiSpotlightTier t)
    {
        var c = item.Card;
        string uri = item.Key, title = item.Title;
        float h = t.ActionH;
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 0f, Height = h,
            Margin = new Edges4(0f, Spacing.S, 0f, 0f),
            Children =
            [
                Button.Create(Loc.Get(Strings.Home.Play), () => HomeCardNav.Play(in c), ButtonAppearance.Accent,
                    t.Standing ? ControlSize.Large : ControlSize.Medium, Icons.Play) with { Shrink = 0f, Height = h },
                item.IsBlank
                    ? new BoxEl { Width = h, Height = h, Shrink = 0f }
                    : Controls.Named(Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = title, Box = h }) with { Key = "save:" + uri }, Loc.Get(Strings.Home.Save)),
                item.SongCount == 0 ? new BoxEl { Visible = false } : WinUiHomeShelf.Sound(h, "spot:sound:" + uri),
            ],
        };
    }

    /// <summary>A listed song: WinUI's list row (the number in the tertiary caption, the title, the duration at the end).</summary>
    static Element TrackRow(ShelfSong song, int index, string duration, ReleaseSpotlightGeometry g) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f, Grow = 1f, Padding = new Edges4(X.RowPad, 0f, X.RowPad, 0f),
        HitTestVisible = false,
        Children =
        [
            new BoxEl
            {
                Width = X.NumberW, Shrink = 0f, Justify = FlexJustify.End, Direction = 0,
                Children = [Ui.Caption((index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)) with { Color = Tok.TextTertiary, MaxLines = 1 }],
            },
            R.Type.TrackTitle(song.IsBlank ? SpotlightModel.Hold(SpotlightModel.SongHold) : song.Title) with
            {
                MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f, Grow = 1f,
            },
            R.Type.TrackMeta(duration.Length > 0 ? duration : " ") with { Color = Tok.TextTertiary, MaxLines = 1, Shrink = 0f },
        ],
    };

    static Element NextCover(SpotlightItem item, ReleaseSpotlightGeometry g)
        => Controls.Artwork(item.Cover, g.NextCover, g.NextCover, X.NextRadius);
}
