// ── Themes/WinUi/Structure/Views/Overlays/WinUiProfileChip.cs — WinUI's `Overlay.ProfileChip` (themes plan 6.1, T11a) ───────
//
// The trailing island's account chip (ch 19 W4-W7, section 6.2), moved from `Shell.Overlays.UI.cs`: the avatar (a quiet skeleton circle until
// the account's identity is KNOWN: 0.2.9 printed a dash; 0.3 must not), and in the named form the name beside it (a skeleton bar while unknown,
// capped at the name budget). The core chip host keeps the overlay handle (open, toggle, light dismiss, focus trap), the anchor capture, the
// chrome ladder's flags and the presentation-mode switch, and hands them in with the open verb.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

/// <summary>Wavee WinUI's account chip.</summary>
internal static class WinUiProfileChip
{
    /// <summary>The registered template of <see cref="ViewKeys.OverlayProfileChip"/>.</summary>
    public static Element Template(ProfileChipProps props, in ViewContext ctx)
    {
        if (props.Drawer) return DrawerRow(props);
        float target = WinUiOverlayTiers.For(ctx.Tier().Window).ChipTarget;   // Compact: the chip is a 40 target around the 24 avatar
        float avatar = WinUiOverlayRules.ChipAvatar;
        float nameCap = props.NameCap;

        // The skeleton chip until the account's identity is KNOWN.
        Element picture = props.Known
            ? PersonPicture.Create("", avatar, displayName: props.Name, imageSourcePath: props.AvatarUrl)
            : new BoxEl { Width = avatar, Height = avatar, Shrink = 0f, Corners = CornerRadius4.All(avatar / 2f), Fill = Tok.FillSubtleSecondary };

        Element[] children = !props.ShowName || props.Presenting
            ? [picture]
            : props.Known
                ? [picture, Caption(props.Name).Primary() with { MaxWidth = nameCap, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }]
                : [picture, new BoxEl
                    {
                        Width = nameCap, Height = WinUiOverlayRules.ChipNameBarHeight,
                        Corners = CornerRadius4.All(WinUiOverlayRules.SkeletonBarRadius), Fill = Tok.FillSubtleSecondary,
                    }];

        return new BoxEl
        {
            Direction = 0, Gap = WinUiOverlayRules.ChipGap, AlignItems = FlexAlign.Center, Height = MathF.Max(WinUiOverlayRules.ChipHeight, target), MinWidth = target, Shrink = 0f,
            Justify = FlexJustify.Center,
            // The named form carries 6 extra DIP of right padding — part of the budgeted ChromeProfileNameW.
            Padding = props.ShowName ? WinUiOverlayRules.ChipPaddingNamed : WinUiOverlayRules.ChipPaddingBare,
            Corners = CornerRadius4.All(Radii.Control),
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            OnClick = props.Open, OnRealized = props.Realized,
            Children = children,
        }.Interactive(Interaction.Subtle);
    }

    /// <summary>The drawer's account row form (TC2, C-Sidebar account="true"): the 32 avatar, the name over "Account and more", the row's
    /// width; a press opens the same account menu.</summary>
    private static Element DrawerRow(ProfileChipProps props)
    {
        float avatar = WinUiOverlayRules.DrawerChipAvatar;
        Element picture = props.Known
            ? PersonPicture.Create("", avatar, displayName: props.Name, imageSourcePath: props.AvatarUrl)
            : new BoxEl { Width = avatar, Height = avatar, Shrink = 0f, Corners = CornerRadius4.All(avatar / 2f), Fill = Tok.FillSubtleSecondary };
        Element name = props.Known && !props.Presenting
            ? new TextEl(props.Name)
            {
                Size = WinUiOverlayRules.DrawerNameSize, LineHeight = WinUiOverlayRules.DrawerNameLine, Weight = 600, Color = Tok.TextPrimary,
                MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            }
            : new BoxEl
            {
                Width = WinUiOverlayRules.HeaderNameBarWidth, Height = WinUiOverlayRules.ChipNameBarHeight,
                Corners = CornerRadius4.All(WinUiOverlayRules.SkeletonBarRadius), Fill = Tok.FillSubtleSecondary,
            };
        return new BoxEl
        {
            Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = WinUiOverlayRules.DrawerChipH,
            Direction = 0, AlignItems = FlexAlign.Center, Gap = WinUiOverlayRules.DrawerChipGap,
            Padding = WinUiOverlayRules.DrawerChipPadding, Corners = CornerRadius4.All(Radii.Control),
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
                        new TextEl(Loc.Get("shell.accountAndMore"))
                        {
                            Size = WinUiOverlayRules.DrawerSubSize, LineHeight = WinUiOverlayRules.DrawerSubLine, Color = Tok.TextSecondary,
                            MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                    ],
                },
            ],
        }.Interactive(Interaction.Subtle);
    }
}
