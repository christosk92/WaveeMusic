// ── Themes/Zune/Structure/Views/Overlays/ZuneProfileChip.cs — Zune's `Overlay.ProfileChip` (themes plan 6.3, P5 Z8a) ──────────────────────────────────
//
// The trailing island's account chip: a round avatar (a quiet circle until the account's identity is KNOWN: an unknown name is never printed as a dash), and in the named
// form the name beside it in the body voice (a skeleton bar while unknown, capped at the name budget). Zune's controls are square, so the chip's hover plate is too; the
// avatar is the one round thing. The chip is 32 tall in the title bar and a 40-DIP target at Compact. The core chip host keeps the overlay handle (open, toggle, light dismiss,
// focus trap), the anchor capture, the chrome ladder's flags and the presentation-mode switch, and hands them in with the open verb.
//
// The drawer's account ROW (`props.Drawer`) is a form Zune's frame never mounts (no drawer, no nav pane); it is kept so the key is total.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

namespace Wavee.Themes.Zune;

/// <summary>Zune's account chip.</summary>
internal static class ZuneProfileChip
{
    /// <summary>The key the chip is mounted under.</summary>
    public const string ChipKey = "zchip";

    /// <summary>The registered template of <see cref="ViewKeys.OverlayProfileChip"/>.</summary>
    public static Element Template(ProfileChipProps props, in ViewContext ctx)
    {
        var layout = ZuneOverlayTiers.Of(ctx.Tier());
        if (props.Drawer) return DrawerRow(props);
        float avatar = ZuneOverlayRules.ChipAvatar;
        // The name's cap: the title bar's on the pill tiers (160, the T1 audit's A9; Zune's field budget charges the same), else the core's.
        float nameCap = ZuneFrameRules.AccountNameCap(ZuneFrameRules.TitleBarOf(ctx.Tier()), props.NameCap);

        // The skeleton chip until the account's identity is KNOWN.
        Element picture = props.Known
            ? PersonPicture.Create("", avatar, displayName: props.Name, imageSourcePath: props.AvatarUrl)
            : ZuneOverlayParts.SkeletonCircle(avatar);

        Element[] children = !props.ShowName || props.Presenting
            ? [picture]
            : props.Known
                ? [picture, R.Type.DenseMeta(props.Name) with { Color = Tok.TextPrimary, MaxWidth = nameCap, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }]
                : [picture, ZuneOverlayParts.Skeleton(nameCap, ZuneOverlayRules.ChipNameBarHeight)];

        return new BoxEl
        {
            Key = ChipKey, Direction = 0, Gap = ZuneOverlayRules.ChipGap, AlignItems = FlexAlign.Center, Height = layout.ChipHeight, Shrink = 0f,
            MinWidth = layout.ChipMinWidth, Justify = FlexJustify.Center,
            // The named form carries 6 extra DIP of right padding: part of the budgeted name width.
            Padding = props.ShowName ? ZuneOverlayRules.ChipPaddingNamed : ZuneOverlayRules.ChipPaddingBare,
            Corners = R.Shape.All(ShapeRole.Control),
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            OnClick = props.Open, OnRealized = props.Realized,
            Children = children,
        }.Interactive(Interaction.Subtle);
    }

    /// <summary>The drawer's account row form: the 32 avatar, the name over "account and more", the row's width; a press opens the same account menu.</summary>
    static Element DrawerRow(ProfileChipProps props)
    {
        float avatar = ZuneOverlayRules.DrawerChipAvatar;
        Element picture = props.Known
            ? PersonPicture.Create("", avatar, displayName: props.Name, imageSourcePath: props.AvatarUrl)
            : ZuneOverlayParts.SkeletonCircle(avatar);
        Element name = props.Known && !props.Presenting
            ? R.Type.TrackTitle(props.Name) with { Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }
            : ZuneOverlayParts.Skeleton(ZuneOverlayRules.HeaderNameBarWidth, ZuneOverlayRules.ChipNameBarHeight);
        return new BoxEl
        {
            Key = ChipKey, Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = ZuneOverlayRules.DrawerChipH,
            Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneOverlayRules.DrawerChipGap,
            Padding = ZuneOverlayRules.DrawerChipPadding, Corners = R.Shape.All(ShapeRole.Control),
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            OnClick = props.Open, OnRealized = props.Realized,
            Children =
            [
                picture,
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, ClipToBounds = true,
                    Children =
                    [
                        name,
                        ZuneText.Meta(Loc.Get("shell.accountAndMore")),
                    ],
                },
            ],
        }.Interactive(Interaction.Subtle);
    }
}
