// ── Themes/WinUi/Structure/Views/Discovery/Home/WinUiHomeShelf.cs — WinUI's Spine Shelf ("Because you like") ─────────────────────────────
//
// The concept (scratch/sphome/spine-shelf.html, WinUI skin): a spine is the cover's colour washing down into the card fill, the number,
// the small cover, the title and artist turned to read upward, the artist's avatar at the foot; the open card is the same wash at a slant,
// a slim head (cover 56, the reason with the avatar and the artist as an accent link, the title in the module header voice, the meta, Play
// as the accent button and the save heart) over the five song tiles (core). Mechanics and motion are core (Home/SpineShelf); numbers are
// WinUiShelfRules.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using X = Wavee.Themes.WinUi.WinUiShelfRules;

namespace Wavee.Themes.WinUi;

internal static class WinUiHomeShelf
{
    /// <summary>The shelf body of a SpineShelf zone at the tier of <paramref name="layout"/>.</summary>
    public static Element Body(Zone zone, WinUiHomeLayout layout, ShelfController? controller)
        => SpineShelfView.Of(zone, X.Of(layout.Tier), Skin, controller, Icons.ChevronLeft, Icons.ChevronRight);

    /// <summary>The body's height (the chapter's extent under its header).</summary>
    public static float Extent(Zone zone, WinUiHomeLayout layout) => SpineShelfView.Extent(zone, X.Of(layout.Tier));

    static readonly SpineShelfSkin Skin = new(Spine, Head, Caption, SpineWash, SpineHoverWash, FaceWash,
        static () => Tok.AccentDefault, static () => R.OnMedia.Ink, static () => Tok.FillSubtleSecondary, static () => R.OnMedia.ScrimRest,
        WinUiDiscoveryMotion.SwapFade, WinUiDiscoveryMotion.SwapFadeMotion);

    // ── the spine ──

    static Element Spine(ShelfItem item, int index, SpineShelfGeometry g)
    {
        var title = new BoxEl
        {
            Direction = 0, Gap = X.SpineGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children =
            [
                Ui.BodyStrong(item.Title) with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
                item.Reason is { } r
                    ? Ui.Caption(r.Name).Secondary() with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f }
                    : new BoxEl(),
            ],
        };
        var number = Ui.Caption(item.Number) with { Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f };
        Element avatar = item.Reason is { } a
            ? Controls.Artwork(a.Image, X.Avatar, X.Avatar, X.Avatar / 2f)
            : new BoxEl { Width = X.Avatar, Height = X.Avatar, Shrink = 0f };
        if (g.Standing)
            return new BoxEl
            {
                Direction = 0, Gap = X.SpineGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Height = g.RowH,
                Padding = new Edges4(X.RowPadStart, 0f, X.RowPadEnd, 0f),
                Children =
                [
                    number,
                    Controls.Artwork(item.Header.ImageUrl, X.RowCover, X.RowCover, X.SpineCoverRadius),
                    new BoxEl { Direction = 0, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = [title] },
                    avatar,
                ],
            };
        float cover = g.SpineW - X.SpineCoverInset;
        float run = g.Height - X.SpineTop - X.SpineBottom - X.NumberLine - cover - X.Avatar - 3f * X.SpineGap;
        return new BoxEl
        {
            Direction = 1, Gap = X.SpineGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Width = g.SpineW, Height = g.Height,
            Padding = new Edges4(0f, X.SpineTop, 0f, X.SpineBottom),
            Children =
            [
                new BoxEl { Height = X.NumberLine, Shrink = 0f, AlignItems = FlexAlign.Center, Children = [number] },
                Controls.Artwork(item.Header.ImageUrl, cover, cover, X.SpineCoverRadius),
                SpineShelfView.Sideways(title, run, X.TitleLine),
                avatar,
            ],
        };
    }

    // ── the open card's head ──

    static Element Head(ShelfItem item, SpineShelfGeometry g)
    {
        var h = item.Header;
        float cover = g.Standing ? X.HeadCoverStanding : X.HeadCover;
        var words = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center,
            Children =
            [
                Reason(item),
                new BoxEl
                {
                    Direction = 0, MinWidth = 0f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true,
                    OnClick = () => HomeCardNav.Open(in h),
                    Children = [R.Type.ModuleHeader(item.Title) with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f }],
                },
                Ui.Caption(item.Meta) with { Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
            ],
        };
        var actions = new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 0f, Height = X.ActionH(g),
            Children =
            [
                Button.Create(Loc.Get(Strings.Home.Play), () => HomeCardNav.Play(in h), ButtonAppearance.Accent, ControlSize.Medium, Icons.Play) with { Shrink = 0f, Height = X.ActionH(g) },
                Controls.Named(Embed.Comp(() => new Controls.SaveButton { Uri = h.Uri, Name = h.Title, Box = X.ActionH(g) }) with { Key = "save:" + h.Uri }, Loc.Get(Strings.Home.Save)),
            ],
        };
        var art = new BoxEl
        {
            Shrink = 0f, Cursor = CursorId.Hand, OnClick = () => HomeCardNav.Open(in h),
            Children = [Controls.Artwork(h.ImageUrl, cover, cover, Radii.Control)],
        };
        if (g.Standing)
            return new BoxEl
            {
                Direction = 1, Gap = Spacing.S, MinWidth = 0f, Grow = 1f,
                Children = [new BoxEl { Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = [art, words] }, actions],
            };
        return new BoxEl { Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f, Grow = 1f, Children = [art, words, actions] };
    }

    /// <summary>Why the item is here: the artist's avatar, the lead words and the artist as an accent link; or the reason alone.</summary>
    static Element Reason(ShelfItem item)
    {
        if (item.Reason is not { } r)
            return Ui.Caption(item.Over).Secondary() with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
        string uri = r.Uri, name = r.Name;
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children =
            [
                Controls.Artwork(r.Image, X.ReasonAvatar, X.ReasonAvatar, X.ReasonAvatar / 2f),
                Ui.Caption(r.Lead).Secondary() with { MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f },
                new BoxEl
                {
                    Direction = 0, MinWidth = 0f, Shrink = 1f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true,
                    OnClick = () => HomeVerbs.OpenEntity(uri, name),
                    Children = [Ui.Caption(name) with
                    {
                        Color = Tok.AccentTextPrimary, HoverColor = Tok.AccentTextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap,
                        Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                    }],
                },
            ],
        };
    }

    // ── a song tile's words ──

    static Element Caption(ShelfSong song, int index, SpineShelfGeometry g) => new BoxEl
    {
        Direction = 1, Grow = 1f, MinWidth = 0f, Justify = FlexJustify.SpaceBetween, Padding = Edges4.All(X.CaptionPad),
        Children =
        [
            Ui.Caption((index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)) with { Color = Prop.Of(static () => R.OnMedia.InkSecondary) },
            new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Children =
                [
                    Ui.BodyStrong(song.Title) with
                    {
                        Color = Prop.Of(static () => R.OnMedia.Ink), MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                    Ui.Caption(song.Artist.Length > 0 ? song.Artist : " ") with
                    {
                        Color = Prop.Of(static () => R.OnMedia.InkSecondary), MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                ],
            },
        ],
    };

    // ── the washes ──

    static GradientSpec SpineWash(ShelfItem item) => Wash(item.Accent, X.SpineAngle, X.SpineMix, X.SpineFadeAt);
    static GradientSpec SpineHoverWash(ShelfItem item) => Wash(item.Accent, X.SpineAngle, X.SpineHoverMix, X.SpineHoverFadeAt);
    static GradientSpec FaceWash(ShelfItem item) => Wash(item.Accent, X.FaceAngle, X.FaceMix, X.FaceFadeAt);

    static GradientSpec Wash(uint accent, float angle, float mix, float fadeAt)
    {
        var card = Tok.FillSolidBase;
        var head = accent != 0u ? ColorF.Lerp(card, ColorScience.ToColor(accent), mix) : card;
        return new GradientSpec(GradientShape.Linear, angle, [new GradientStop(0f, head), new GradientStop(fadeAt, card), new GradientStop(1f, card)]);
    }
}
