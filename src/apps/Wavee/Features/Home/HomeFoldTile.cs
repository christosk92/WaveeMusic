using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using Wavee.Core;

namespace Wavee;

/// <summary>THE Fold tile — home-sections-v1-mica.html, the Blend tab: a WinUI card on mica, a Zune type crop running
/// under the art, and up to three square covers hanging off the right that FAN on hover. A static factory (same layer
/// as <c>HomeCards.*</c>), not a Component — no hooks, no OnRealized, no per-node state — so it costs nothing beyond an
/// element tree and can be built per-card inside <see cref="HomeModules.FoldDeck"/>'s virtualized shelf.
/// <para>Structure/idiom lifted from <c>SearchHero</c>: a token-plate <c>ZStack</c> root (Card fill/stroke/elevation),
/// an optional radial <c>GradientSpec</c> wash keyed off the section's own accent, off-card rotated art, and a copy
/// column painted LAST so it sits on top — matching the prototype, where <c>.copy</c> is z-index 2 over the
/// z-index-0 <c>.stack</c>. Hover changes only the card's FILL (<c>FillCardDefault</c> → <c>FillCardSecondary</c>) at
/// the SAME elevation — never a Material lift — so <c>MediaCard.ApplyCardPhysics</c>/<c>Interaction.Card</c>/
/// <c>Interaction.Subtle</c> play no part here; the fill swap plus the covers' own <c>WhileHover</c> fan IS the whole
/// hover state.</para></summary>
static class HomeFoldTile
{
    public static Element Create(HomeSection section, float cardW, string? eyebrow, Action<HomeSection> open)
    {
        var cards = section.Cards;

        // The wash's colour comes from the FIRST cover's own accent, never invented: 0 means the card carries none,
        // and a section with no cards has nothing to wash from either.
        uint accent = cards.Count > 0 ? cards[0].Meta?.Accent ?? 0 : 0;

        var children = new List<Element>(5);
        if (accent != 0)
        {
            ColorF c = WaveePalette.ToColor(accent);
            // SearchHero's idiom verbatim: an explicit Height alongside the Stretch pair — ZStack sizes a child to the
            // root only when it is EXPLICITLY sized; Stretch alone does not backfill the cross axis here.
            children.Add(new BoxEl
            {
                HitTestVisible = false, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                Height = HomeModuleLayout.FoldCardHeight,
                Gradient = new GradientSpec(GradientShape.Radial, 0f,
                [
                    new GradientStop(0f, c with { A = 0.22f }),
                    new GradientStop(0.72f, c with { A = 0f }),
                ])
                {
                    RadialCenter = new Point2(1.0f, 0.48f),
                    RadialRadius = new Point2(0.70f, 1.10f),
                },
            });
        }

        // Up to three covers, each a DIRECT ZStack child (no wrapper — FoldRest's offsets are already card-local
        // absolute, so a wrapper would just be a second coordinate space to keep in sync). A missing slot is omitted,
        // never padded with a repeated cover.
        int coverCount = Math.Min(3, cards.Count);
        for (int i = 0; i < coverCount; i++)
        {
            var card = cards[i];
            HomeModuleLayout.FoldRest(i, cardW, out float x, out float y, out float rot);
            HomeModuleLayout.FoldFan(i, out float dx, out float dy, out float drot);
            children.Add(new BoxEl
            {
                Width = HomeModuleLayout.FoldCover, Height = HomeModuleLayout.FoldCover,
                OffsetX = x, OffsetY = y, Rotation = rot,
                // WhileHover targets are DELTAS on this authored rest pose (offset/rotation ADD — the engine's
                // MotionTarget contract), so FoldFan carries only the fan's DIFFERENCE from rest, never the
                // prototype's replacement CSS numbers (see FoldFan's own comment).
                WhileHover = new MotionTarget { OffsetX = dx, OffsetY = dy, Rotation = drot },
                Transition = MotionTok.ControlNormal,
                // No `if (Motion.ReducedMotion)` branch anywhere in this file: MotionTok.ControlNormal already carries
                // the reduced-motion policy (KeepFade — see MotionTok.Get), which is the engine's reduced-motion-as-a-
                // value rule. Branching on the mutable global here would be a hook-order hazard on top of fighting
                // that rule, and this factory has no hooks to hazard in the first place.
                // KeepAlive park/un-park snaps Offset/Rotation back to this rest (SnapAuthoredPose). Hover must not
                // be what puts the stack on the right after Browse → Featured Charts → Back.
                HitTestVisible = false,
                Shadow = Elevation.Card, ClipToBounds = true,
                Corners = CornerRadius4.All(Radii.Control),
                Children = [Surfaces.Artwork(card.Image, SpotifyExportMapper.Hash(card.Uri),
                    HomeModuleLayout.FoldCover, HomeModuleLayout.FoldCover, Radii.Control, decodePx: 128)],
            });
        }

        string title = section.Title is { Length: > 0 } t ? t : Loc.Get(Strings.Home.Sections);

        var copyChildren = new List<Element>(2);
        if (eyebrow is { Length: > 0 })
            copyChildren.Add(WaveeType.Eyebrow(eyebrow) with
            {
                // Sentence case, the eyebrow's own casing — never .ToUpper() (see WaveeType.Eyebrow).
                Color = Tok.AccentTextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            });
        copyChildren.Add(WaveeType.FoldTitle(title) with
        {
            // The title keeps its OWN casing too — never lowercased.
            Wrap = TextWrap.Wrap, MaxLines = 3, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        });

        // HitTestVisible=false: the ROOT is the one hyperlink (Role/OnClick live there), so the copy column must not
        // steal the click/hover hit-test away from it.
        children.Add(new BoxEl
        {
            HitTestVisible = false, Direction = 1, Justify = FlexJustify.End, Gap = Spacing.XS,
            // Explicit Height: ZStack sizes a child to the card only when it is sized (the wash above uses the same
            // rule). Without it Justify=End cannot pin copy to the card bottom and the title sits under the fan.
            Height = HomeModuleLayout.FoldCardHeight, AlignSelf = FlexAlign.Stretch,
            // The prototype's `.copy { padding: 20px 12px 18px 20px }` (top right bottom left).
            Padding = new Edges4(Spacing.XL, Spacing.XL, Spacing.M, 18f),
            MaxWidth = cardW * HomeModuleLayout.FoldCopyMaxFrac,
            Grow = 1f, Basis = 0f, MinWidth = 0f,
            Children = copyChildren.ToArray(),
        });

        return new BoxEl
        {
            // Width is the fitted shelf cell: without it, Offset covers expand the tile's intrinsic box and paint
            // through the Charts header (Featured's stack sitting on top of "Charts" / "Featured").
            ZStack = true, Width = cardW > 0f ? cardW : float.NaN, Height = HomeModuleLayout.FoldCardHeight, MinWidth = 0f, ClipToBounds = true,
            Corners = Radii.CardAll, Fill = Tok.FillCardDefault, HoverFill = Tok.FillCardSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Shadow = Elevation.Card,
            OnClick = () => open(section), Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true,
            Key = "home-fold-tile:" + (section.Uri ?? "") + ":" + cardW.ToString(CultureInfo.InvariantCulture),
            Children = children.ToArray(),
        };
    }
}
