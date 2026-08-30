using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The window-chrome strips shared by every setup-wizard live-preview MINIATURE — Appearance's shell diagram
/// (<see cref="AppearanceStageView"/>) today, the Sidebar chooser's own stage tomorrow. Kept deliberately generic (no
/// page-specific state, no settings reads of its own): a caller passes the one <see cref="WaveePicker.Ink"/> pair its
/// miniature is already tinting everything else with, so a second miniature reuses these verbatim instead of hand-
/// copying the dot/caption-button/player-bar grammar a second time.</summary>
static class SetupMiniChrome
{
    public const float TitleBarHeight = 28f;
    public const float PlayerBarHeight = 36f;

    /// <summary>The miniature's title bar: an ink "traffic light" dot, a short bar standing in for the window title,
    /// and the three chrome glyphs (minimize/maximize/close) at the trailing edge — the same grammar the deleted
    /// <c>SetupAppearancePage.PreviewWindow</c> drew, promoted so it isn't redrawn per miniature.</summary>
    public static Element TitleBar(WaveePicker.Ink ink) => new BoxEl
    {
        Height = TitleBarHeight, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center,
        Padding = new Edges4(Spacing.S, 0f, Spacing.XS, 0f),
        Children =
        [
            new BoxEl { Width = Spacing.S, Height = Spacing.S, Shrink = 0f, Corners = Radii.ControlAll, Fill = ink.Block },
            new BoxEl
            {
                Width = Spacing.XXXL, Height = Spacing.XS, Shrink = 0f,
                Margin = new Edges4(Spacing.S, 0f, 0f, 0f), Corners = Radii.PillAll, Fill = ink.Faint,
            },
            new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f },
            Icon(Icons.ChromeMinimize, 10f, Tok.TextTertiary),
            new BoxEl { Width = Spacing.XXS },
            Icon(Icons.ChromeMaximize, 10f, Tok.TextTertiary),
            new BoxEl { Width = Spacing.XXS },
            Icon(Icons.ChromeClose, 10f, Tok.TextTertiary),
        ],
    };

    /// <summary>The miniature's player bar: a square "now playing" art tile, two identity bars, and a 4-DIP dot that
    /// lights up (accent) while <paramref name="lyricsMotion"/> is on — the drifting-lyrics-backdrop flag has no other
    /// visible surface in a shell diagram this small, so the dot IS its whole representation. The dot is always
    /// mounted (opacity toggles, not presence) so the bar's own width never reflows when the flag flips.</summary>
    public static Element PlayerBar(bool lyricsMotion, WaveePicker.Ink ink) => new BoxEl
    {
        Height = PlayerBarHeight, Shrink = 0f, Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
        Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
        Children =
        [
            new BoxEl { Width = Spacing.XXL, Height = Spacing.XXL, Shrink = 0f, Corners = Radii.ControlAll, Fill = ink.Block },
            new BoxEl
            {
                Direction = 1, Gap = Spacing.XXS, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center,
                Children = [WaveePicker.Bar(1f, ink, strong: true), WaveePicker.Bar(0.6f, ink)],
            },
            new BoxEl
            {
                Width = 4f, Height = 4f, Shrink = 0f, Corners = Radii.Circle(4f),
                Fill = Tok.AccentDefault, Opacity = lyricsMotion ? 1f : 0f,
            },
        ],
    };
}
