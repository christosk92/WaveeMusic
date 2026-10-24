// ── Themes/WinUi/Structure/Views/Overlays/WinUiProfileMenu.cs — WinUI's `Overlay.ProfileMenu` (themes plan 6.1, T11a) ───────
//
// The account flyout (ch 19 W4-W7), moved from `Shell.Overlays.UI.cs`: the header over stock WinUI menu rows. The header is a 40-DIP avatar
// beside the name (14/600) and a tier line (a 10-DIP star and the badge at 12, in the premium ink for Premium; an invisible 10-DIP spacer for
// Free, so both badges share one baseline); the email line is omitted (0.3 carries no email column, which is also what 0.2.9 did when it had
// none). An unknown identity keeps the header's height with quiet bars; an unknown tier hides its row rather than printing "Spotify Free"
// speculatively. The rows (which exist, their labels and what each does: every row closes the menu FIRST, then acts) are the core's.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using FluentGpu.Hooks;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

/// <summary>Wavee WinUI's account menu.</summary>
internal static class WinUiProfileMenu
{
    /// <summary>The registered template of <see cref="ViewKeys.OverlayProfileMenu"/>.</summary>
    public static Element Template(ProfileMenuProps props, in ViewContext ctx)
    {
        // Never wider than the window less the band's margin (a 300-DIP window): the menu's own 304 otherwise.
        float width = WinUiOverlayTiers.For(ctx.Tier().Window).PanelWidth(WinUiOverlayRules.ProfileMenuWidth, ctx.Hooks.UseContextSignal(Viewport.Size).Value.Width);
        return new BoxEl
        {
            Direction = 1, MinWidth = width, MaxWidth = width,
            Padding = WinUiOverlayRules.ProfileMenuPadding,
            Children =
            [
                AccountHeader(props),
                new BoxEl { Height = 1f, Margin = WinUiOverlayRules.ProfileMenuDividerMargin, Fill = Tok.StrokeDividerDefault },
                MenuFlyout.Create(props.Items, props.Close, width, size: OverlayFit.MenuSize),
            ],
        };
    }

    /// <summary>A 40-DIP avatar beside name · tier line.</summary>
    static Element AccountHeader(ProfileMenuProps props)
    {
        var lines = new List<Element>(2)
        {
            props.Known
                ? new TextEl(props.Name)
                {
                    Size = WinUiOverlayRules.HeaderNameSize, LineHeight = WinUiOverlayRules.HeaderNameLine, Weight = 600, Color = Tok.TextPrimary,
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                }
                : new BoxEl
                {
                    Width = WinUiOverlayRules.HeaderNameBarWidth, Height = WinUiOverlayRules.HeaderNameBarHeight,
                    Margin = WinUiOverlayRules.HeaderNameBarMargin, Corners = CornerRadius4.All(WinUiOverlayRules.SkeletonBarRadius),
                    Fill = Tok.FillSubtleSecondary,
                },
        };
        if (props.ShowsTier) lines.Add(TierLine(props.Premium));

        float avatar = WinUiOverlayRules.HeaderAvatar;
        return new BoxEl
        {
            Direction = 0, Gap = WinUiOverlayRules.HeaderGap, AlignItems = FlexAlign.Center, Padding = WinUiOverlayRules.HeaderPadding,
            Children =
            [
                props.Known
                    ? PersonPicture.Create("", avatar, displayName: props.Name, imageSourcePath: props.AvatarUrl)
                    : new BoxEl { Width = avatar, Height = avatar, Shrink = 0f, Corners = CornerRadius4.All(WinUiOverlayRules.HeaderAvatarRadius), Fill = Tok.FillSubtleSecondary },
                new BoxEl
                {
                    Direction = 1, Gap = WinUiOverlayRules.HeaderLinesGap, Grow = 1f, Basis = 0f, MinWidth = 0f, ClipToBounds = true,
                    Children = lines.ToArray(),
                },
            ],
        };
    }

    /// <summary>A 10-DIP star (or an invisible 10-DIP spacer for Free, so both badges share one baseline) + the badge at 12.</summary>
    static Element TierLine(bool premium)
    {
        ColorF ink = premium ? WinUiOverlayRules.PremiumInk : Tok.TextSecondary;
        return new BoxEl
        {
            Direction = 0, Gap = WinUiOverlayRules.TierGap, AlignItems = FlexAlign.Center,
            Children =
            [
                premium ? Icon(Icons.FavoriteStar, WinUiOverlayRules.TierStar, ink) : new BoxEl { Width = WinUiOverlayRules.TierStar, Shrink = 0f },
                new TextEl(Loc.Get(premium ? Strings.Auth.PremiumBadge : Strings.Auth.FreeBadge))
                    { Size = WinUiOverlayRules.TierTextSize, LineHeight = WinUiOverlayRules.TierTextLine, Color = ink },
            ],
        };
    }
}
