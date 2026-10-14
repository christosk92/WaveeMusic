// ── Themes/Classic/Structure/Views/Overlays/ClassicProfileChip.cs — Classic's `Overlay.ProfileChip` (themes plan 6.1, T11a) ───────
//
// The trailing island's account chip (ch 19 W4-W7, section 6.2), moved from `Shell.Overlays.UI.cs`: the avatar (a quiet skeleton circle until
// the account's identity is KNOWN: 0.2.9 printed a dash; 0.3 must not), and in the named form the name beside it (a skeleton bar while unknown,
// capped at the name budget). The core chip host keeps the overlay handle (open, toggle, light dismiss, focus trap), the anchor capture, the
// chrome ladder's flags and the presentation-mode switch, and hands them in with the open verb.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's account chip.</summary>
internal static class ClassicProfileChip
{
    /// <summary>The registered template of <see cref="ViewKeys.OverlayProfileChip"/>.</summary>
    public static Element Template(ProfileChipProps props, in ViewContext ctx)
    {
        float avatar = ClassicOverlayRules.ChipAvatar;
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
                        Width = nameCap, Height = ClassicOverlayRules.ChipNameBarHeight,
                        Corners = CornerRadius4.All(ClassicOverlayRules.SkeletonBarRadius), Fill = Tok.FillSubtleSecondary,
                    }];

        return new BoxEl
        {
            Direction = 0, Gap = ClassicOverlayRules.ChipGap, AlignItems = FlexAlign.Center, Height = ClassicOverlayRules.ChipHeight, Shrink = 0f,
            // The named form carries 6 extra DIP of right padding — part of the budgeted ChromeProfileNameW.
            Padding = props.ShowName ? ClassicOverlayRules.ChipPaddingNamed : ClassicOverlayRules.ChipPaddingBare,
            Corners = CornerRadius4.All(Radii.Control),
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            OnClick = props.Open, OnRealized = props.Realized,
            Children = children,
        }.Interactive(Interaction.Subtle);
    }
}
