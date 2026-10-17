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
        var a = ZuneStageKit.Arrangement(sc, ctx.Tier());
        string url = track.IsValid ? Controls.ArtUrl(track.ImageId) ?? "" : "";
        string title = props.Title();
        int heroArtist = k.Eff == Stage.VizLayout.Artist && !k.HeroLyrics ? sc.HeroArtist.Value : 0;

        float coverX, coverY, size, titlesX, titlesY, titlesW, titlesH = float.NaN, gap;
        bool center = false, bottom = false;
        var kids = new List<Element>(4);
        switch (a.Form)
        {
            case ZuneStageForm.Card:
            {
                (coverX, coverY, size) = (a.Cover.X, a.Cover.Y, a.Cover.W);
                (titlesX, titlesY, titlesW, titlesH) = (a.Titles.X, a.Titles.Y, a.Titles.W, a.Titles.H);
                gap = G.CardTextGap;
                bottom = true;
                kids.Add(new TextEl(Loc.Get(Strings.Stage.NowPlayingEyebrow)) { Size = G.CardEyebrowSize, LineHeight = G.CardEyebrowLine, Color = Ink.InkTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1 });
                kids.Add(new TextEl(title)
                {
                    Size = a.TitleSize, LineHeight = a.TitleLine, Weight = G.TitleLight, FontFamily = R.Type.DisplayFace, CharSpacing = G.CardTitleTracking, Color = Ink.Ink,
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                });
                kids.Add(Meta(props, G.CardMetaSize, G.CardMetaLine, G.TitleRegular, R.Type.DisplayFace, set.Text, Ink.Ink, false, heroArtist));
                break;
            }
            case ZuneStageForm.Column:
            {
                (coverX, coverY, size) = (a.Cover.X, a.Cover.Y, a.Cover.W);
                (titlesX, titlesY, titlesW, titlesH) = (a.Titles.X, a.Titles.Y, a.Titles.W, a.Titles.H);
                gap = 0f;
                bottom = true;
                kids.Add(Meta(props, G.ColumnMetaSize, G.ColumnMetaLine, G.ColumnMetaWeight, R.Type.DisplayFace, set.Text, Ink.Ink, false, heroArtist));
                kids.Add(new TextEl(title)
                {
                    Size = a.TitleSize, LineHeight = a.TitleLine, Weight = G.ColumnTitleWeight, FontFamily = R.Type.DisplayFace, CharSpacing = G.ColumnTitleTracking, Color = Ink.Ink,
                    Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                });
                break;
            }
            default:
            {
                size = L.CoverSize(in k);
                (coverX, coverY) = (L.CoverX(in k), L.CoverY(in k));
                (titlesX, titlesY, titlesW) = (L.TitleX(in k), L.TitleY(in k), L.TitleW(in k));
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
                kids.Add(Meta(props, meta.Size, meta.Line, G.TitleRegular, null, Ink.InkSecondary, set.Text, center, heroArtist));
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
