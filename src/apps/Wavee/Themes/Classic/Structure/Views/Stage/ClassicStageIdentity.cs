// ── Themes/Classic/Structure/Views/Stage/ClassicStageIdentity.cs — Classic's `Stage.Identity` (themes plan 6.1, T7a2) ─────────
//
// From `Shell\Stage.UI.cs`: the Hero component (the morphing cover and its title column), its MetaLink ("Artist · Album") and Chips leaves.
// The core allocator decides where the cover and the titles sit for the look it chose (Card's thumb, Large art, Centered, the Artist
// hero's identity row: `Stage.Layout.CoverX/Y/Size`, `TitleX/Y/W/Font`, in every aspect class); this template draws them.
//
//   ONE cover node (`stage:cover`) morphs between the layouts with a Layout FLIP (the bounds spring, SizeMode.ScaleCorrect); the title
//   column (`stage:titles`) follows on a Position spring. The wrapper is PASS-THROUGH, not hit-invisible: the meta link inside must stay
//   clickable (V-U8). The layer is FULL HEIGHT (Grow + Basis 0): content-sized, this ZStack was only as tall as the cover, so the
//   titles' slot (H - TitleY) arranged to 0 and the meta link - the one Shrink = 1 child of that column - shrank to nothing.
//
// The title, the meta line and the chips are core facts the template (and its two leaves) call inside their own renders, so the format /
// device / owner signals re-render the chips row only (V-U50) and a track change re-renders what read the track. Elements, keys,
// transitions and bound channels are exactly what the stage built before the move.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

using G = Wavee.Themes.Classic.ClassicStageLayerGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's stage identity: the morphing cover and its title column.</summary>
internal static class ClassicStageIdentity
{
    /// <summary>The registered template of <see cref="ViewKeys.StageIdentity"/>.</summary>
    public static Element Template(StageIdentityProps props, in ViewContext ctx)
    {
        var sc = props.Stage;
        var overlay = ctx.Hooks.UseContext(Overlay.Service);
        var L = sc.Layout.Value;
        _ = sc.Mode.Value;                            // the identity re-renders on a mode change, as the core component did
        var set = sc.Accent.Value;
        var track = sc.RowValue();
        var k = sc.Look.Value;                       // what is drawn: Card (the thumb) or a layout's cover + titles
        string url = track.IsValid ? Controls.ArtUrl(track.ImageId) ?? "" : "";
        float size = L.CoverSize(in k);
        float corners = CoverCorners(in k, !k.IsLayout && L.IdentityCardOn(k.Mode));
        int decode = size > 400f ? 1024 : 512;    // Spotify's largest cover is 640: the big covers decode at 1024, the rest at 512
        var flip = new LayoutTransition(TransitionChannels.Bounds, TransitionDynamics.Spring(0.45f, 0.90f), SizeMode.ScaleCorrect);
        var move = new LayoutTransition(TransitionChannels.Position, TransitionDynamics.Spring(0.45f, 0.90f));
        var (ts, tl) = L.TitleFont(in k);
        var meta = L.MetaFont(in k);
        int titleLines = L.TitleMaxLines(in k);       // two under the hero when the block clears the transport, one in the card
        bool center = L.TitlesCentered(in k);
        float titleW = L.TitleW(in k);
        int heroArtist = k.Eff == Stage.VizLayout.Artist && !k.HeroLyrics ? sc.HeroArtist.Value : 0;
        string shown = props.Title();
        ushort titleWeight = k.Eff is Stage.VizLayout.LargeArt or Stage.VizLayout.Centered ? (ushort)700 : (ushort)600;
        // a one-line, left-aligned title is a marquee under the marquee preference (the play bar's look) instead of ellipsising (audit B3, Vr)
        bool marquee = titleLines == 1 && !center && props.Marquee?.Invoke() == true;
        var titleKids = new List<Element>(3)
        {
            marquee
                ? new BoxEl
                {
                    Direction = 0, MinWidth = 0f,   // the row is the text's own height: its line box holds the descenders
                    Children =
                    [
                        Marquee.Of(shown, new Marquee.Style
                        {
                            FontSize = ts, LineHeight = tl, Weight = titleWeight, FontFamily = G.DisplayFace, Foreground = Ink.Ink,
                            Speed = ClassicPlayerBarLayout.MarqueeSpeed, FadeBand = ClassicPlayerBarLayout.MarqueeFade,
                        }),
                    ],
                }
                : new TextEl(shown)
                {
                    Size = ts, LineHeight = tl, Weight = titleWeight, FontFamily = G.DisplayFace, Color = Ink.Ink,
                    Wrap = titleLines > 1 ? TextWrap.Wrap : TextWrap.NoWrap, MaxLines = titleLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    MaxWidth = center ? titleW : float.NaN,
                },
            Embed.Comp(new ClassicStageMetaLink.Props(props.Meta, props.OpenArtist, meta.Size, meta.Line, set.Text, center, heroArtist), static () => new ClassicStageMetaLink()),
        };
        if (L.ShowChipsFor(in k)) titleKids.Add(Embed.Comp(new ClassicStageChips.Props(props.Chips), static () => new ClassicStageChips()) with { Key = "stage:chips" });
        return Stage.Layer with
        {
            Children =
            [
                new BoxEl
                {
                    Key = "stage:cover", Width = size, Height = size, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                    Margin = new Edges4(L.CoverX(in k), L.CoverY(in k), 0f, 0f), Corners = CornerRadius4.All(corners), Shadow = Elevation.Dialog, ClipToBounds = true,
                    Layout = flip, HitTestVisible = false,
                    Children = [Controls.Artwork(url.Length > 0 ? url : null, size, size, corners, morphKey: Stage.Entry.MorphKey, decodePx: decode)],
                },
                // The placed column fills its slot down to the stage's foot (a Start-aligned auto-height ZStack child does), so it
                // is PASS-THROUGH; the identity block itself is the content-height inner column — title · meta · chips FLOW,
                // so a two-line title pushes the meta and chips down instead of overlapping them.
                new BoxEl
                {
                    Key = "stage:titles", Direction = 1, Width = titleW, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                    Margin = new Edges4(L.TitleX(in k), L.TitleY(in k), 0f, 0f), Layout = move, HitTestPassThrough = true,
                    Children =
                    [
                        new BoxEl { Direction = 1, Gap = L.TitleGapFor(in k), MinWidth = 0f, AlignItems = center ? FlexAlign.Center : FlexAlign.Stretch, Children = titleKids.ToArray() }
                            .WithContextMenu(overlay,                                        // right-click the identity = the "…" menu, as the old stage had
                                () => { var m = props.NowPlayingMenu(); if (m is { } model && ContextMenu.HasAnyEnabled(model)) sc.MenuOpen(true); return m; },
                                new ContextMenuOptions { OnClosed = _ => sc.MenuOpen(false) }),
                    ],
                },
            ],
        };
    }

    /// <summary>The cover's corner radius per look: the board's 14 (Large art), 18 (Centered), 8 / 6 (the Artist hero's identity row); Card keeps
    /// the Mode's (the thumb's control radius, the hero's card radius), and the identity card's thumb is a thumb in every mode (C-Lyrics-Compact).</summary>
    static float CoverCorners(in Stage.Look k, bool card) => k.Eff switch
    {
        Stage.VizLayout.LargeArt => Radii.Card,
        Stage.VizLayout.Centered => G.CenteredCorners,
        Stage.VizLayout.Artist => k.HeroLyrics ? G.ArtistLyricsCorners : G.ArtistCorners,
        _ => card || k.Mode == Stage.Mode.Visualizer ? Radii.Control : Radii.Card,
    };
}

/// <summary>"Artist · Album" (the previous stage's MetaLink with re-pushed type + colour). Subscribed to ITS row through the core fact it calls
/// (the track key, the album and artist tables). Navigation CLOSES the stage first (V-U29).</summary>
internal sealed class ClassicStageMetaLink : Component
{
    /// <summary><paramref name="Center"/> centres the line (a TextEl has no text-align); <paramref name="HeroArtist"/> = the artist SLOT the
    /// Artist hero shows as its big name — the line then lists the OTHER billed artists ("with Cheat Codes · Sex"), 0 = every artist.</summary>
    public sealed record Props(Func<int, StageMetaFacts> Meta, Action<Action<string>?> Open, float Size, float Line, ColorF Accent, bool Center = false, int HeroArtist = 0);

    readonly Action _go;
    Action<string>? _begin;
    Props? _props;

    public ClassicStageMetaLink() => _go = Go;

    public override Element Render()
    {
        _begin = UseContext(SharedTransition.Begin);
        var p = UseProps<Props>();
        _props = p;
        var hover = UseSignal(false);
        var facts = p.Meta(p.HeroArtist);                 // FIRST read: the scope, the track key, the album and artist tables
        if (facts.Line.Length == 0) return new BoxEl { Height = 0f, HitTestVisible = false };
        bool enabled = facts.Enabled;
        bool lit = enabled && hover.Value;
        return new BoxEl
        {
            // Shrink 0: this box sits in the identity COLUMN, where Shrink is VERTICAL — Shrink 1 let a short slot squeeze the
            // artist line to zero height under ClipToBounds. The width is the column's (cross-axis stretch); the TEXT shrinks.
            MinWidth = 0f, Shrink = 0f, Direction = 0, ClipToBounds = true, Cursor = enabled ? CursorId.Hand : (CursorId?)null, OnClick = enabled ? _go : null,
            Justify = p.Center ? FlexJustify.Center : FlexJustify.Start,
            OnHoverMove = enabled ? _ => { if (!hover.Peek()) hover.Value = true; } : null, OnPointerExit = enabled ? () => { if (hover.Peek()) hover.Value = false; } : null,
            Role = enabled ? AutomationRole.Hyperlink : AutomationRole.Text, Focusable = enabled, AllowFocusOnInteraction = false,
            Children = [new TextEl(facts.Line) { Size = p.Size, LineHeight = p.Line, Color = lit ? p.Accent : Ink.InkSecondary, Underline = lit, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f }],
        };
    }

    void Go() => _props?.Open(_begin);
}

/// <summary>The format chip, "Synced lyrics", and (outside Visualizer mode) BPM from the Audio group. Its own component so the format / device /
/// owner signals re-render THIS row only (V-U50).</summary>
internal sealed class ClassicStageChips : Component
{
    public sealed record Props(Func<IReadOnlyList<StageChipFact>> Chips);

    public override Element Render()
    {
        var chips = UseProps<Props>().Chips();
        var kids = new Element[chips.Count];
        for (int i = 0; i < kids.Length; i++) kids[i] = Chip(chips[i].Glyph, chips[i].Text);
        return new BoxEl { Direction = 0, Gap = Spacing.S, Wrap = true, Margin = new Edges4(0f, Spacing.S, 0f, 0f), HitTestVisible = false, Children = kids };
    }

    static BoxEl Chip(string? glyph, string text) => new()
    {
        Height = G.ChipHeight, Direction = 0, AlignItems = FlexAlign.Center, Gap = G.ChipGap, Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Shrink = 0f,
        Corners = Radii.ControlAll, Fill = Ink.GlassRest, BorderWidth = 1f, BorderColor = Ink.Stroke,
        Children = glyph is null
            ? [new TextEl(text) { Size = G.ChipTextSize, LineHeight = G.ChipTextLine, Color = Ink.InkSecondary }]
            : [new TextEl(glyph) { Size = G.ChipTextSize, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.InkSecondary }, new TextEl(text) { Size = G.ChipTextSize, LineHeight = G.ChipTextLine, Color = Ink.InkSecondary }],
    };
}
