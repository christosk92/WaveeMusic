// ── Themes/Zune/Structure/Views/Overlays/ZuneProfileMenu.cs — Zune's `Overlay.ProfileMenu` (themes plan 6.3, P5 Z8a) ─────────────────────────────────
//
// Board Z-Overlays ("account menu · 304 wide · theme lives here, not in the title bar"): a round 40 avatar beside the name in the display voice at the light weight (20/28/300)
// and a tier line under it ("premium" in the accent, "free" in the secondary ink), then the engine's menu rows. The rows (which exist, their labels and what each does: every
// row closes the menu FIRST, then acts) are the core's; the light theme row is the core's App-mode row. An unknown identity keeps the header's height with quiet bars; an unknown
// tier prints no tier line. The popup around it (the plate, the shadow, the light dismiss) is the core's and the control table's.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

namespace Wavee.Themes.Zune;

/// <summary>Zune's account menu.</summary>
internal static class ZuneProfileMenu
{
    /// <summary>The registered template of <see cref="ViewKeys.OverlayProfileMenu"/>.</summary>
    public static Element Template(ProfileMenuProps props, in ViewContext ctx)
    {
        // Never wider than the window less the tier's margin (a 300-DIP window): the board's 304 otherwise.
        var layout = ZuneOverlayTiers.Of(ctx.Tier());
        float width = layout.PanelWidth(ZuneOverlayRules.ProfileMenuWidth, ctx.Hooks.UseContextSignal(Viewport.Size).Value.Width);
        return new BoxEl
        {
            Key = "zprofile:menu", Direction = 1, MinWidth = width, MaxWidth = width,
            Padding = ZuneOverlayRules.ProfileMenuPadding,
            Children =
            [
                AccountHeader(props),
                MenuFlyout.Create(Words(props.Items), props.Close, width, size: OverlayFit.MenuSize),
            ],
        };
    }

    /// <summary>The board's account menu (Z-Overlays) is words: no icon column, each row lowercase (a sub-menu's rows too).</summary>
    static IReadOnlyList<MenuFlyoutItem> Words(IReadOnlyList<MenuFlyoutItem> items)
    {
        var words = new MenuFlyoutItem[items.Count];
        for (int i = 0; i < words.Length; i++)
        {
            var item = items[i];
            words[i] = item.IsSeparator ? item : item with
            {
                Label = item.Label.ToLower(System.Globalization.CultureInfo.CurrentCulture), Icon = default,
                SubItems = item.SubItems is { } sub ? Words(sub) : null,
            };
        }
        return words;
    }

    /// <summary>A round avatar beside the name over the tier line.</summary>
    static Element AccountHeader(ProfileMenuProps props)
    {
        var lines = new List<Element>(2)
        {
            props.Known
                ? R.Type.Wordmark(props.Name) with
                {
                    Weight = ZuneOverlayRules.HeaderNameWeight, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                }
                : new BoxEl
                {
                    Width = ZuneOverlayRules.HeaderNameBarWidth, Height = ZuneOverlayRules.HeaderNameBarHeight, Margin = ZuneOverlayRules.HeaderNameBarMargin,
                    Shrink = 0f, Corners = CornerRadius4.All(ZuneOverlayRules.SkeletonBarRadius), Fill = Tok.FillSubtleSecondary,
                },
        };
        if (props.ShowsTier) lines.Add(TierLine(props.Premium));

        float avatar = ZuneOverlayRules.HeaderAvatar;
        return new BoxEl
        {
            Key = ZuneOverlayRules.ProfileHeaderKey, Direction = 0, Gap = ZuneOverlayRules.HeaderGap, AlignItems = FlexAlign.Center, Padding = ZuneOverlayRules.HeaderPadding,
            Children =
            [
                props.Known
                    ? PersonPicture.Create("", avatar, displayName: props.Name, imageSourcePath: props.AvatarUrl)
                    : ZuneOverlayParts.SkeletonCircle(avatar),
                new BoxEl
                {
                    Direction = 1, Gap = ZuneOverlayRules.HeaderLinesGap, Grow = 1f, Basis = 0f, MinWidth = 0f, ClipToBounds = true,
                    Children = lines.ToArray(),
                },
            ],
        };
    }

    /// <summary>"premium" with a star in the accent, or "free" in the secondary ink behind a spacer of the star's width (so both badges share one baseline).</summary>
    static Element TierLine(bool premium)
    {
        ColorF ink = premium ? Tok.AccentTextPrimary : Tok.TextSecondary;
        return new BoxEl
        {
            Direction = 0, Gap = ZuneOverlayRules.TierGap, AlignItems = FlexAlign.Center,
            Children =
            [
                premium ? Ui.Icon(Icons.FavoriteStar, ZuneOverlayRules.TierStar, ink) : new BoxEl { Width = ZuneOverlayRules.TierStar, Shrink = 0f },
                ZuneText.Cased(R.Type.DenseMeta(Loc.Get(premium ? Strings.Auth.PremiumBadge : Strings.Auth.FreeBadge)), LabelKind.Label) with { Color = ink },
            ],
        };
    }
}
