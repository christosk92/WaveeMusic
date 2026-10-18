// ── Themes/Zune/Structure/Views/Stage/ZuneStageIdentity.cs — Zune's `Stage.Identity` (P5 Z4b) ─────────────────────────────────────
//
// The ONE cover node (`stage:cover`) and its title column (`stage:titles`), placed for the arrangement (`ZuneStageGeometry.Arrange`):
//
//   PLATE    the allocator's rects for the look (Card's thumb in the 460 card, Large art and Centered beside or under the cover, a Compact
//            window's identity card), in Zune's voice: the display face at 300 (400 under 20 DIP), the meta line in the secondary ink
//            (the accent under the pointer), the chips flat and square
//   CARD     Z-NowPlaying-Card: the 220 cover in the card, beside it bottom-aligned "now playing" (label case), the 40/48 title at 300 and
//            the artist line in the accent (20/28)
//   COLUMN   Z-NowPlaying-LargeArt: the big cover at the left gutter, and at the column's foot the artist line in the accent (28/36 at 600)
//            over the title (120 at 200 on the board's column, scaled with it)
//
// The cover morphs between them with a Layout FLIP (the cover's spring, SizeMode.ScaleCorrect), the titles follow on a Position spring, so a
// layout or arrangement change never jumps. The covers are Zune's cover shape (2). The wrapper is pass-through: the meta link stays
// clickable. Right-clicking the titles opens the "…" menu.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

using G = Wavee.Themes.Zune.ZuneStageGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Zune;

/// <summary>Zune's stage identity: the morphing cover and its title column.</summary>
internal static class ZuneStageIdentity
{
    static readonly LayoutTransition s_flip = new(TransitionChannels.Bounds, TransitionDynamics.Spring(G.CoverResponse, G.CoverDamping), SizeMode.ScaleCorrect);

    static readonly Element[] s_none = [];

    static readonly LayoutTransition s_move = new(TransitionChannels.Position, TransitionDynamics.Spring(G.CoverResponse, G.CoverDamping));

    /// <summary>The registered template of <see cref="ViewKeys.StageIdentity"/>.</summary>
    public static Element Template(StageIdentityProps props, in ViewContext ctx)
    {
        var sc = props.Stage;
        var overlay = ctx.Hooks.UseContext(Overlay.Service);
        var L = sc.Layout.Value;
        _ = sc.Mode.Value;
        var set = sc.Accent.Value;
        var track = sc.RowValue();
        var k = sc.Look.Value;
        var tiers = ctx.Tier();
        var t = ZuneStageTiers.StageOf(in tiers);
        var a = ZuneStageKit.Arrangement(sc, in tiers);
        string url = track.IsValid ? Controls.ArtUrl(track.ImageId) ?? "" : "";
        string title = props.Title();
        int heroArtist = k.Eff == Stage.VizLayout.Artist && !k.HeroLyrics ? sc.HeroArtist.Value : 0;

        float coverX, coverY, size, titlesX, titlesY, titlesW, titlesH = float.NaN, gap;
        bool center = false, bottom = false;
        var kids = new List<Element>(4);
        Element? display = null;
        var v = a.Voice;
        switch (a.Form)
        {
            case ZuneStageForm.Card when v is not null:
            {
                (coverX, coverY, size) = (a.Cover.X, a.Cover.Y, a.Cover.W);
                (titlesX, titlesY, titlesW, titlesH) = (a.Titles.X, a.Titles.Y, a.Titles.W, a.Titles.H);
                gap = G.CardTextGap;
                bottom = true;
                kids.Add(new TextEl(Loc.Get(Strings.Stage.NowPlayingEyebrow)) { Size = v.EyebrowSize, LineHeight = v.EyebrowLine, Color = Ink.InkTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1 });
                kids.Add(Title(title, a.TitleSize, a.TitleLine, v.TitleWeight, v.TitleTracking));
                kids.Add(Meta(props, v.MetaSize, v.MetaLine, v.MetaWeight, R.Type.DisplayFace, set.Text, Ink.Ink, false, heroArtist));
                if (!a.Display.IsEmpty) display = Display(props, in a, title, set.Text, heroArtist);
                break;
            }
            case ZuneStageForm.Column when v is not null:
            {
                (coverX, coverY, size) = (a.Cover.X, a.Cover.Y, a.Cover.W);
                (titlesX, titlesY, titlesW, titlesH) = (a.Titles.X, a.Titles.Y, a.Titles.W, a.Titles.H);
                gap = 0f;
                bottom = true;
                kids.Add(Meta(props, v.MetaSize, v.MetaLine, v.MetaWeight, R.Type.DisplayFace, set.Text, Ink.Ink, false, heroArtist));
                kids.Add(Title(title, a.TitleSize, a.TitleLine, v.TitleWeight, v.TitleTracking));
                break;
            }
            case ZuneStageForm.Centered or ZuneStageForm.Stack when v is not null:
            {
                // Centered: the title and the accent artist line centred under the cover; Stack: left-aligned under the cover
                (coverX, coverY, size) = (a.Cover.X, a.Cover.Y, a.Cover.W);
                (titlesX, titlesY, titlesW, titlesH) = (a.Titles.X, a.Titles.Y, a.Titles.W, a.Titles.H);
                center = a.Form == ZuneStageForm.Centered;
                gap = 0f;
                var head = Title(title, a.TitleSize, a.TitleLine, v.TitleWeight, v.TitleTracking);
                kids.Add(center ? head with { MaxWidth = titlesW } : head);
                kids.Add(Meta(props, v.MetaSize, v.MetaLine, v.MetaWeight, R.Type.DisplayFace, set.Text, Ink.Ink, center, heroArtist));
                break;
            }
            case ZuneStageForm.Plate when G.LyricsSide(in L, in k, t) is { } side && t.LyricsSide is { } ls:
            {
                // Lyrics mode (Z-Lyrics): the cover on the gutter, under it the 26 title, the accent artist line and the quiet tag line
                (coverX, coverY, size) = (side.Cover.X, side.Cover.Y, side.Cover.W);
                (titlesX, titlesY, titlesW, titlesH) = (side.Titles.X, side.Titles.Y, side.Titles.W, side.Titles.H);
                gap = ls.ColGap;
                var head = Title(title, ls.TitleSize, ls.TitleLine, G.TitleLight, 0f) with { Margin = new Edges4(0f, ls.TitleTop, 0f, 0f) };
                kids.Add(head);
                kids.Add(Meta(props, ls.ArtistSize, ls.ArtistLine, G.TitleRegular, ls.ArtistDisplay ? R.Type.DisplayFace : null, set.Text, Ink.Ink, false, heroArtist));
                if (L.ShowChipsFor(in k)) kids.Add(Embed.Comp(new ZuneStageTags.Props(props.Chips, ls.TagSize, ls.TagLine, ls.TagTop), static () => new ZuneStageTags()) with { Key = "stage:chips" });
                break;
            }
            default:
            {
                size = L.CoverSize(in k);
                // the allocator's left-anchored parts move onto the tier's gutter (16 / 24 / 48 / 64 / 96): the cover, its titles, the card's edge
                float shift = G.EdgeShift(in L, in k, t);
                (coverX, coverY) = (L.CoverX(in k) + shift, L.CoverY(in k));
                (titlesX, titlesY, titlesW) = (L.TitleX(in k) + shift, L.TitleY(in k), G.PlateTitleWidth(in L, in k, t, shift));
                // a cover the allocator hung under its 48 bar moves down with the tier's taller head, its titles with it
                float drop = G.DropFor(coverY, t);
                coverY += drop;
                titlesY += drop;
                gap = L.TitleGapFor(in k);
                center = L.TitlesCentered(in k);
                var (ts, tl) = L.TitleFont(in k);
                var meta = L.MetaFont(in k);
                int lines = L.TitleMaxLines(in k);
                var head = new TextEl(title)
                {
                    Size = ts, LineHeight = tl, Weight = G.TitleWeight(ts), Color = Ink.Ink,
                    Wrap = lines > 1 ? TextWrap.Wrap : TextWrap.NoWrap, MaxLines = lines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    MaxWidth = center ? titlesW : float.NaN,
                };
                kids.Add(ts >= G.DisplayFrom ? head with { FontFamily = R.Type.DisplayFace } : head);
                // Lyrics mode's identity card (Z-Lyrics-Compact): the artist line rests in the accent
                bool lyricsCard = k.Mode == Stage.Mode.Lyrics && L.IdentityCardOn(k.Mode);
                kids.Add(Meta(props, meta.Size, meta.Line, G.TitleRegular, null, lyricsCard ? set.Text : Ink.InkSecondary, lyricsCard ? Ink.Ink : set.Text, center, heroArtist));
                if (L.ShowChipsFor(in k)) kids.Add(Embed.Comp(new ZuneStageChips.Props(props.Chips), static () => new ZuneStageChips()) with { Key = "stage:chips" });
                break;
            }
        }
        int decode = size > G.CoverLargeDecodeFrom ? G.CoverDecodeLarge : G.CoverDecodeSmall;
        float corners = R.Shape.Cover;
        var block = new BoxEl { Direction = 1, Gap = gap, MinWidth = 0f, AlignItems = center ? FlexAlign.Center : FlexAlign.Stretch, Children = kids.ToArray() }
            .WithContextMenu(overlay,
                () => { var m = props.NowPlayingMenu(); if (m is { } model && ContextMenu.HasAnyEnabled(model)) sc.MenuOpen(true); return m; },
                new ContextMenuOptions { OnClosed = _ => sc.MenuOpen(false) });
        return Stage.Layer with
        {
            Children =
            [
                new BoxEl
                {
                    Key = "stage:cover", Width = size, Height = size, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                    Margin = new Edges4(coverX, coverY, 0f, 0f), Corners = CornerRadius4.All(corners), ClipToBounds = true,
                    Layout = s_flip, HitTestVisible = false,
                    Children = [Controls.Artwork(url.Length > 0 ? url : null, size, size, corners, morphKey: Stage.Entry.MorphKey, decodePx: decode)],
                },
                // the placed column: pass-through; the block inside is content-height (bottom-aligned in the Card and Large art columns)
                new BoxEl
                {
                    Key = "stage:titles", Direction = 1, Width = titlesW, Height = titlesH, Justify = bottom ? FlexJustify.End : FlexJustify.Start,
                    AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                    Margin = new Edges4(titlesX, titlesY, 0f, 0f), Layout = s_move, HitTestPassThrough = true,
                    Children = [block],
                },
                .. (display is null ? s_none : [display]),
            ],
        };
    }

    /// <summary>A title in an arrangement's voice: the display face, one line.</summary>
    static TextEl Title(string title, float size, float line, ushort weight, float tracking) => new(title)
    {
        Size = size, LineHeight = line, Weight = weight, FontFamily = R.Type.DisplayFace, CharSpacing = tracking, Color = Ink.Ink,
        Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
    };

    /// <summary>The Card arrangement's display title over the face at Huge and Huge 4K (Z-NowPlaying-Huge: 150/160 at 200 in the ink at
    /// 0.92, the artist line 28/36 at 600 in the accent under it). Keyed: it fades in and out with the arrangement, never pops.</summary>
    static Element Display(StageIdentityProps props, in ZuneStageArrangement a, string title, ColorF accent, int heroArtist)
    {
        var fade = new EnterExit(Opacity: 0f, Active: true);
        return new BoxEl
        {
            Key = "stage:display", AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Width = a.Display.W, Height = a.Display.H,
            Margin = new Edges4(a.Display.X, a.Display.Y, 0f, 0f), Direction = 1, ClipToBounds = true, HitTestPassThrough = true,
            Enter = fade, Exit = fade, Transition = MotionTok.StandardEnter,
            Children =
            [
                new TextEl(title)
                {
                    Size = a.DisplaySize, LineHeight = a.DisplayLine, Weight = G.ColumnTitleWeight, FontFamily = R.Type.DisplayFace, CharSpacing = G.DisplayTracking,
                    Color = Ink.Ink with { A = G.DisplayInkA }, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
                Meta(props, a.DisplayMetaSize, a.DisplayMetaLine, G.ColumnMetaWeight, R.Type.DisplayFace, accent, Ink.Ink, false, heroArtist),
            ],
        };
    }

    static Element Meta(StageIdentityProps props, float size, float line, ushort weight, string? face, ColorF rest, ColorF hover, bool center, int heroArtist)
        => Embed.Comp(new ZuneStageMetaLink.Props(props.Meta, props.OpenArtist, size, line, weight, face, rest, hover, center, heroArtist), static () => new ZuneStageMetaLink());
}

/// <summary>"Artist · Album" in the arrangement's voice. Subscribed to its row through the core fact it calls; navigation closes the stage first.</summary>
internal sealed class ZuneStageMetaLink : Component
{
    public sealed record Props(Func<int, StageMetaFacts> Meta, Action<Action<string>?> Open, float Size, float Line, ushort Weight, string? Face,
        ColorF Rest, ColorF Hover, bool Center, int HeroArtist);

    readonly Action _go;
    Action<string>? _begin;
    Props? _props;

    public ZuneStageMetaLink() => _go = Go;

    public override Element Render()
    {
        _begin = UseContext(SharedTransition.Begin);
        var p = UseProps<Props>();
        _props = p;
        var hover = UseSignal(false);
        var facts = p.Meta(p.HeroArtist);
        if (facts.Line.Length == 0) return new BoxEl { Height = 0f, HitTestVisible = false };
        bool enabled = facts.Enabled;
        bool lit = enabled && hover.Value;
        var text = new TextEl(facts.Line) { Size = p.Size, LineHeight = p.Line, Weight = p.Weight, Color = lit ? p.Hover : p.Rest, Underline = lit, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f };
        return new BoxEl
        {
            MinWidth = 0f, Shrink = 0f, Direction = 0, ClipToBounds = true, Cursor = enabled ? CursorId.Hand : (CursorId?)null, OnClick = enabled ? _go : null,
            Justify = p.Center ? FlexJustify.Center : FlexJustify.Start,
            OnHoverMove = enabled ? _ => { if (!hover.Peek()) hover.Value = true; } : null, OnPointerExit = enabled ? () => { if (hover.Peek()) hover.Value = false; } : null,
            Role = enabled ? AutomationRole.Hyperlink : AutomationRole.Text, Focusable = enabled, AllowFocusOnInteraction = false,
            Children = [p.Face is { } face ? text with { FontFamily = face } : text],
        };
    }

    void Go() => _props?.Open(_begin);
}

/// <summary>Lyrics mode's tag line ("synced lyrics · 73 bpm"): the chips' facts as one quiet line of tertiary type, no plates (Z-Lyrics).</summary>
internal sealed class ZuneStageTags : Component
{
    public sealed record Props(Func<IReadOnlyList<StageChipFact>> Chips, float Size, float Line, float Top);

    public override Element Render()
    {
        var p = UseProps<Props>();
        var chips = p.Chips();
        var parts = new string[chips.Count];
        for (int i = 0; i < parts.Length; i++) parts[i] = chips[i].Text;
        return new TextEl(string.Join(" · ", parts))
        {
            Size = p.Size, LineHeight = p.Line, Color = Ink.InkTertiary, Case = R.Case.For(LabelKind.Label), Wrap = TextWrap.NoWrap, MaxLines = 1,
            Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Margin = new Edges4(0f, p.Top, 0f, 0f),
        };
    }
}

/// <summary>The format chip, "Synced lyrics" and BPM: flat, square, the label case. Its own component so the format / device / owner signals
/// re-render this row only.</summary>
internal sealed class ZuneStageChips : Component
{
    public sealed record Props(Func<IReadOnlyList<StageChipFact>> Chips);

    public override Element Render()
    {
        var chips = UseProps<Props>().Chips();
        var kids = new Element[chips.Count];
        for (int i = 0; i < kids.Length; i++) kids[i] = Chip(chips[i].Glyph, chips[i].Text);
        return new BoxEl { Direction = 0, Gap = ZuneStageGeometry.ChipGap, Wrap = true, Margin = new Edges4(0f, Stage.Layout.ChipsTop, 0f, 0f), HitTestVisible = false, Children = kids };
    }

    static BoxEl Chip(string? glyph, string text)
    {
        var label = new TextEl(text) { Size = ZuneStageGeometry.ChipTextSize, LineHeight = ZuneStageGeometry.ChipTextLine, Color = Ink.InkSecondary, Case = R.Case.For(LabelKind.Label) };
        return new BoxEl
        {
            Height = ZuneStageGeometry.ChipHeight, Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneStageGeometry.ChipGap,
            Padding = new Edges4(ZuneStageGeometry.ChipPadX, 0f, ZuneStageGeometry.ChipPadX, 0f), Shrink = 0f,
            Corners = R.Shape.All(ShapeRole.Control), Fill = Ink.Plate,
            Children = glyph is null
                ? [label]
                : [new TextEl(glyph) { Size = ZuneStageGeometry.ChipTextSize, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.InkSecondary }, label],
        };
    }
}
