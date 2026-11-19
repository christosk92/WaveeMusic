// ── Themes/WinUi/Structure/Rules/WinUiHintRules.cs — the numbers of WinUI's in-app hints (settings rework phase 6, design 7.2) ──────────
//
// Data, not decisions: `Views\Overlays\WinUiHint.cs` reads them (WVTH003 allows literals only here, in `Rules\**`). The InfoBar and the
// TeachingTip bring their own WinUI metrics (the 38 close button, the 320-336 tip); these are only where a hint sits around them.

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.WinUi;

/// <summary>Where WinUI's hints sit and how they arrive.</summary>
public static class WinUiHintRules
{
    /// <summary>The lyrics pane's InfoBar: on the lyrics' own gutter (the core insets the slot by the surface's side pad), 12 above the pane's
    /// foot (W-InContext), none above (the footer's line sits there).</summary>
    public static readonly Edges4 LyricsMargin = new(0f, 0f, 0f, 12f);

    /// <summary>The devices flyout's InfoBar, under the menu: inset to the menu rows' own plate.</summary>
    public static readonly Edges4 FlyoutMargin = new(4f, 2f, 4f, 4f);

    /// <summary>The bell panel's InfoBar, under its header: on the header's start inset (its padding's left) and the feed's right.</summary>
    public static readonly Edges4 BellMargin = new(14f, 0f, 8f, 8f);

    /// <summary>The Home banner never grows wider than this (the runtime banner's lane cell, `WinUiOverlayRules.BannerMaxWidth`).</summary>
    public const float BannerMaxWidth = WinUiOverlayRules.BannerMaxWidth;

    /// <summary>The Home banner's air under the title bar.</summary>
    public const float BannerGap = 8f;

    /// <summary>How far the Home banner drops below the overlay lane's own top pad so it starts under the title bar (88 with the tab strip, 48
    /// without) rather than over its search field.</summary>
    public static float BannerTop(float titleBarH) => MathF.Max(0f, titleBarH + BannerGap - Setup.RuntimeRules.BannerTopPad);

    /// <summary>The entrance: the InfoBar rises this far as it fades in (the notification card's own rise).</summary>
    public const float EnterDy = WinUiNotificationRules.CardEnterDy;

    /// <summary>The gap between the InfoBar's action button and its overflow.</summary>
    public const float ActionGap = 4f;

    /// <summary>The TeachingTip's foot (its "⋯"): inset as the tip's own content margin (12), the button flush right.</summary>
    public static readonly Edges4 TipFootPadding = new(12f, 0f, 8f, 8f);
}
