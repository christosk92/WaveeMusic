using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The shared page frame every real setup page uses — Rise's own <c>SetupPageContent</c>: an icon column
/// (192-wide Lottie, centred) beside [a <c>TitleTextBlockStyle</c> header over a scrolling body], the icon column
/// dropping out ENTIRELY below the 770-DIP viewport breakpoint (a single on/off switch — Rise's own
/// <c>AdaptiveTrigger</c>, no hysteresis band).
///
/// <para><see cref="Frame"/> defers to a <see cref="Component"/> (<see cref="SetupPageFrame"/>) purely so the
/// icon-column drop can read <c>Viewport.Size</c> LIVE — a window resize re-evaluates it without remounting the page
/// body underneath, the same reason <c>ContentHost.PageFor</c> (Features/Shell/ContentHost.cs) wraps every real page
/// in its own <c>Embed.Comp</c> rather than reading context itself: a bare static function has no hook context of
/// its own to read <c>Viewport.Size</c> from.</para></summary>
static class SetupPageHost
{
    /// <param name="page">Selects the Lottie hero (<see cref="WaveeLottie.For"/>) and whether the back-button spacer
    /// can apply at all (<see cref="SetupGating.BackSpacerApplies"/>).</param>
    /// <param name="header">The page title — <c>Ui.Title</c> (28/600), Rise's <c>TitleTextBlockStyle</c>.</param>
    /// <param name="body">The page's own content column — build it with <see cref="SetupText"/>.</param>
    /// <param name="backAutoPadding">Rise's own per-page <c>PaddingRectangle</c> declaration: whether THIS page ever
    /// wants the 42-DIP back-button spacer reserved beside its header when the icon column has dropped. Terms/SignIn
    /// never show a back button anyway (<see cref="SetupGating.ShowsBack"/>), so this is a no-op for them; it exists
    /// so every page states its own intent explicitly, the same way Rise's XAML does per page.</param>
    public static Element Frame(SetupPage page, string header, Element body, bool backAutoPadding = true)
        => Embed.Comp(new SetupPageFrame.Props(page, header, body, backAutoPadding), () => new SetupPageFrame())
            with { Key = "setup:frame:" + (int)page };
}

/// <summary>The live-responsive half of <see cref="SetupPageHost.Frame"/>. The frame receives its slots through
/// pushed props: a page identity is stable inside one KeepAlive entry, but its body is rebuilt when page-local
/// signals change (notably when Spotify publishes a pairing challenge, or the runtime model advances a phase).
/// Passing those through the constructor would freeze the first tree forever, because a reused <see cref="Component"/>
/// never re-runs its factory.</summary>
sealed class SetupPageFrame : Component
{
    internal sealed record Props(SetupPage Page, string Header, Element Body, bool BackAutoPadding);

    public override Element Render()
    {
        var p = UseProps<Props>();
        var viewport = UseContextSignal(Viewport.Size);
        bool iconShown = SetupLayout.ShowsIcon(viewport.Value.Width);
        bool spacer = p.BackAutoPadding && SetupGating.BackSpacerApplies(p.Page, iconShown);

        var headerChildren = spacer
            ? new Element[] { new BoxEl { Width = SetupLayout.BackSpacerWidth, Shrink = 0f }, HeaderTitle(p.Header) }
            : [HeaderTitle(p.Header)];

        Element header = new BoxEl
        {
            Direction = 0, Shrink = 0f, Margin = new Edges4(0f, -SetupLayout.HeaderTopPull, 0f, SetupLayout.HeaderBottomGap),
            Children = headerChildren,
        };

        Element bodyEl = ScrollView(p.Body) with
        {
            Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f,
            // The ScrollViewer Margin 0,0,-24,0 / Padding 0,0,24,0 trick: the scrollbar rides in the plate's own
            // outer 24-DIP padding rather than eating into the content column, so a wide scrollbar never narrows text.
            Margin = new Edges4(0f, 0f, -SetupLayout.ScrollGutter, 0f),
            Padding = new Edges4(0f, 0f, SetupLayout.ScrollGutter, 0f),
            EdgeCues = ScrollEdgeCues.None,
            // Rise's ScrollViewer is VerticalScrollBarVisibility=Auto: whenever the body overflows the lane, the thin
            // WinUI rail stays visible (hover still expands it), so a cut-off page always SAYS it scrolls. The engine
            // default reveals the rail only on hover/scroll — on a page a user has never scrolled that is invisible.
            AlwaysShowScrollbar = true,
        };

        Element content = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Children = [header, bodyEl],
        };

        if (!iconShown)
            return new BoxEl
            {
                Key = "setup:layout:compact", Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                Children = [content],
            };

        Element iconColumn = new BoxEl
        {
            Width = SetupLayout.IconColumnWidth, Shrink = 0f, AlignSelf = FlexAlign.Stretch,
            Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
            Children = [LottieView.Create(WaveeLottie.For(p.Page), SetupLayout.IconColumnWidth, WaveeLottie.Options)],
        };

        return new BoxEl
        {
            Key = "setup:layout:wide", Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Gap = SetupLayout.IconColumnGap,
            Children = [iconColumn, content],
        };
    }

    static Element HeaderTitle(string header) =>
        Title(header) with { Grow = 1f, Basis = 0f, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.WordEllipsis };
}
