// ── Themes/Classic/Structure/Views/Home/ClassicHomeSpotlight.cs — Classic's Release Spotlight ("From artists you follow") ─────────────────
//
// Drawn as Wavee Classic draws its own Home card and its album head, never as a web component: the card is Classic's Fluent card frame
// (Ui.Card: its fill, stroke, corners and resting shadow, no padding, the layers clipped to it), the cover's colour runs into it from the left
// the way Classic's artist hero veils its photo; the cover sits at the album page's corner; the words are the daylist card's voices (the eyebrow
// in the secondary body voice with the artist's round photo and the artist strong as the Spine Shelf's reason names it, the 40/52 hero title,
// the meta caption) and the action row is the daylist card's (the Accent Play with its label at the primary floor, Controls.SaveButton, the
// round sound button); an EP's first songs are Classic track rows (the number in its column, the title, the duration). The canvas stands as a
// tall tile on the right. Mechanics, preview and motion are core (Home/ReleaseSpotlight); numbers are ClassicSpotlightRules.

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
using X = Wavee.Themes.Classic.ClassicSpotlightRules;

namespace Wavee.Themes.Classic;

internal static class ClassicHomeSpotlight
{
    /// <summary>The spotlight body of a ReleaseSpotlight zone at the tier of <paramref name="layout"/>.</summary>
    public static Element Body(Zone zone, ClassicHomeLayout layout, ShelfController? controller)
        => ReleaseSpotlightView.Of(zone, X.Of(layout.Tier), Skin, controller, Icons.ChevronLeft, Icons.ChevronRight);

    /// <summary>The body's height (the chapter's extent under its header).</summary>
    public static float Extent(Zone zone, ClassicHomeLayout layout) => ReleaseSpotlightView.Extent(zone, X.Of(layout.Tier));

    static readonly ReleaseSpotlightSkin Skin = new(Frame, Cover, Copy, TrackRow, NextCover,
        static () => Tok.FillSubtleSecondary, static () => Tok.AccentDefault, ClassicTierMotion.SwapFade, ClassicTierMotion.SwapFadeMotion,
        ClassicTierMotion.SizeReflow with { Axes = SizeAxes.Height });

    static ClassicSpotlightTier TierOf(ReleaseSpotlightGeometry g) => g.Tier is ClassicSpotlightTier t ? t : default;

    // ── the card ──

    /// <summary>Classic's card frame (Ui.Card: fill, stroke, corners, shadow) with no padding, the layers clipped to its corners, its fill
    /// carrying the cover's colour from the left edge.</summary>
    static Element Frame(SpotlightItem item, ReleaseSpotlightGeometry g, Element content)
        => Ui.Card(content) with
        {
            Padding = default, ClipToBounds = true, Direction = 1, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Gradient = Wash(item.Accent),
        };

    static GradientSpec Wash(uint accent)
    {
        var solid = Tok.FillSolidBase;
        var c = accent != 0u ? ColorScience.ToColor(accent) : solid;
        return new GradientSpec(GradientShape.Linear, 0f,
            [new GradientStop(0f, ColorF.Lerp(solid, c, X.WashMix)), new GradientStop(X.WashMidAt, ColorF.Lerp(solid, c, X.WashMid)), new GradientStop(X.WashFadeAt, solid), new GradientStop(1f, solid)]);
    }

    static Element Cover(SpotlightItem item, ReleaseSpotlightGeometry g)
    {
        var c = item.Card;
        float h = g.Narrow ? g.NarrowCoverH : g.Cover;
        return new BoxEl
        {
            Shrink = 0f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, OnClick = item.IsBlank ? null : () => HomeCardNav.Open(in c),
            Children = [Controls.Artwork(item.Cover, g.Cover, h, g.CoverRadius)],
        };
    }

    // ── the words ──

    static Element Copy(SpotlightItem item, SpotlightFacts facts, ReleaseSpotlightGeometry g, Element? tracks)
    {
        var t = TierOf(g);
        var c = item.Card;
        var title = t.HeroTitle ? R.Type.HeroTitle(item.Title) : R.Type.DetailHero(item.Title) with { Weight = X.TitleWeight };
        var kids = new List<Element>(6)
        {
            Eyebrow(facts),
            new BoxEl
            {
                Direction = 0, MinWidth = 0f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = !item.IsBlank,
                OnClick = item.IsBlank ? null : () => HomeCardNav.Open(in c),
                Children = [title with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f }],
            },
            Ui.Caption(facts.Meta).Secondary() with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
        };
        if (tracks is { } list) kids.Add(new BoxEl { Direction = 1, MinWidth = 0f, Margin = new Edges4(0f, Spacing.S, 0f, 0f), Children = [list] });
        kids.Add(Actions(item, t));
        return new BoxEl { Direction = 1, Gap = Spacing.XS, MinWidth = 0f, Children = kids.ToArray() };
    }

    /// <summary>The eyebrow: the artist's round photo, the lead words in the secondary body voice, the artist strong in the primary ink as a link
    /// (the Spine Shelf's reason line), the tail words.</summary>
    static Element Eyebrow(SpotlightFacts f)
    {
        string uri = f.ArtistUri, name = f.Artist;
        var kids = new List<Element>(4) { Controls.Artwork(f.ArtistImage, X.Avatar, X.Avatar, X.Avatar / 2f) };
        if (f.Lead.Length > 0) kids.Add(Ui.Body(f.Lead.TrimEnd()).Secondary() with { MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f });
        kids.Add(new BoxEl
        {
            Direction = 0, MinWidth = 0f, Shrink = 1f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = uri.Length > 0,
            OnClick = uri.Length > 0 ? () => HomeVerbs.OpenEntity(uri, name) : null,
            Children = [Ui.Body(name) with
            {
                Color = Tok.TextPrimary, HoverColor = Tok.TextSecondary, Weight = X.ArtistWeight, MaxLines = 1, Wrap = TextWrap.NoWrap,
                Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            }],
        });
        if (f.Tail.Length > 0) kids.Add(Ui.Body(f.Tail.TrimStart()).Secondary() with { MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f });
        return new BoxEl { Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = kids.ToArray() };
    }

    /// <summary>The daylist card's action row: the Accent Play with its label at the primary floor, the heart, the round sound button.</summary>
    static Element Actions(SpotlightItem item, ClassicSpotlightTier t)
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
                Button.Create(Loc.Get(Strings.Home.Play), () => HomeCardNav.Play(in c), ButtonAppearance.Accent, t.Buttons, Icons.Play)
                    with { Shrink = 0f, MinWidth = Controls.PrimaryMinWidth },
                item.IsBlank
                    ? new BoxEl { Width = h, Height = h, Shrink = 0f }
                    : Controls.Named(Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = title, Box = h }) with { Key = "save:" + uri }, Loc.Get(Strings.Home.Save)),
                item.SongCount == 0 ? new BoxEl { Visible = false } : ClassicHomeShelf.Sound(h, "spot:sound:" + uri),
            ],
        };
    }

    /// <summary>A listed song as Classic's track list draws one: the number right-aligned in its column, the title, the duration at the end.</summary>
    static Element TrackRow(ShelfSong song, int index, string duration, ReleaseSpotlightGeometry g) => new BoxEl
    {
        Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f, Grow = 1f, Padding = new Edges4(X.RowPad, 0f, X.RowPad, 0f),
        HitTestVisible = false,
        Children =
        [
            new BoxEl
            {
                Width = X.NumberW, Shrink = 0f, Justify = FlexJustify.End, Direction = 0,
                Children = [R.Type.TrackMeta((index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)) with { Color = Tok.TextSecondary, MaxLines = 1 }],
            },
            R.Type.TrackTitle(song.IsBlank ? SpotlightModel.Hold(SpotlightModel.SongHold) : song.Title) with
            {
                MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f, Grow = 1f,
            },
            R.Type.TrackMeta(duration.Length > 0 ? duration : " ") with { Color = Tok.TextSecondary, MaxLines = 1, Shrink = 0f },
        ],
    };

    static Element NextCover(SpotlightItem item, ReleaseSpotlightGeometry g)
        => Controls.Artwork(item.Cover, g.NextCover, g.NextCover, X.NextRadius);
}
