// ── Themes/WinUi/Structure/Views/Overlays/WinUiHint.cs — WinUI's `Hint.Line` (settings rework phase 6, design 6.1-6.3 and 7.2) ─────────────
//
// WinUI draws a hint in WinUI's own two controls, never a ContentDialog:
//
//   InfoBar      where the hint lives IN a panel (the lyrics pane's footer, on an opaque base because the full screen's lyrics lie over art;
//                the devices flyout under its menu, the bell panel under its header, the top of Home): Informational, closable (the ✕ is "not
//                now": the hint's own snooze; a hint with its own words for the dismiss has a button of them beside the action instead), its one
//                action as the InfoBar's button (an inline switch the accent button, a link the standard one, a state line's "Change" a hyperlink
//                and no ✕), and a "⋯" whose menu
//                offers "Don't show tips". A two-sentence line puts its first sentence in the title (W-InContext: "Want every word timed as it is
//                sung?" over "Word-by-word lyrics run on this PC."). On Home it floats on an opaque card with the flyout elevation (it lies over
//                the page, as the runtime banner does). After an inline write: the confirmation ("On · Normal") and its "Change" link.
//   TeachingTip  where the hint POINTS at one control (the play button, the volume slider, the Lossless badge, the queue's Crossfade switch,
//                the back button, the title bar): targeted, light dismiss, no focus taken (the engine tip has no focus trap). Compact, as WinUI's
//                own: the title, the one line, then ONE row: the action and the dismiss as buttons at their own width (an inline switch's action the
//                accent one; the dismiss in the hint's own words, "Keep it", else "Not now"), the "⋯" at the row's end. The dismiss is the row's
//                button, so the tip's header ✕ is not drawn (one verb, one control). A click elsewhere sets it aside for the session; "Not now"
//                snoozes it. After an inline write the tip is replaced by the confirmation tip (its title, "On", "Change" and the header ✕).
//                Over the player bar the tip floats wholly ABOVE the bar, never over its rows (the Lossless badge sits under the title, so a tip
//                hung from the badge itself covered the title and the heart): it hangs from the bar's top edge, its tail on the control's
//                centre, WinUI's flyout gap between (`BarTopOverlay`).
//
// Keys: the InfoBar's host and the tip's bubble are `hint:<id>`, the parts `hint:<id>:act`, `:dismiss`, `:change`, `:more` (the tip's included).
// The entrance is the notification card's (a short rise and fade); the tip's is the engine's TeachingTip expand. Nothing here moves the page:
// an in-place slot eases its own height (core), the Home banner and the tip float.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Common;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's hint template (see the file header).</summary>
internal static class WinUiHint
{
    static readonly BoxEl s_zero = new() { Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false };

    /// <summary>The registered template of <see cref="ViewKeys.Hint"/>.</summary>
    public static Element Template(HintProps p, in ViewContext ctx)
    {
        float target = CommonTiers.Target(ctx.Tier().Content);
        if (p.Target is not null) return Embed.Comp(new TipProps(p, PlacementOf(p.Surface), target), static () => new Tip());
        switch (p.Surface)
        {
            case HintSurfaces.HomeBanner:
            {
                float width = WinUiOverlayTiers.For(ctx.Tier().Window).PanelWidth(WinUiHintRules.BannerMaxWidth, ctx.Hooks.UseContextSignal(Viewport.Size).Value.Width);
                // Under the title bar (its height with or without the tab strip), never over its search field.
                var facts = TierContext.DrawnIn(ctx.Hooks.UseContext(ThemeFactsContext.Signal));
                float top = WinUiHintRules.BannerTop(WinUiTiers.FrameOf(ctx.Tier()).TitleBarH(facts.Value.Capabilities.ShowsTabStrip));
                return Host(p, new BoxEl
                {
                    Direction = 1, MaxWidth = width, Shrink = 0f, Fill = Tok.FillSolidBase, Corners = CornerRadius4.All(Radii.Control),
                    Shadow = Elevation.Flyout, ClipToBounds = true,
                    Children = [Bar(p, target)],
                }, new Edges4(0f, top, 0f, 0f));
            }
            case HintSurfaces.LyricsFooter:
                // The lyrics may lie over the cover's art (the full screen): an opaque base under the InfoBar's translucent tint, as the
                // runtime banner has, so its line and glyph read on any picture.
                return Host(p, new BoxEl
                {
                    Direction = 1, Shrink = 0f, MinWidth = 0f, Fill = Tok.FillSolidBase, Corners = CornerRadius4.All(Radii.Control), ClipToBounds = true,
                    Children = [Bar(p, target)],
                }, WinUiHintRules.LyricsMargin);
            case HintSurfaces.DevicePicker or HintSurfaces.BellHeader:
                return Host(p, Bar(p, target), MarginOf(p.Surface));
            default:
                return CommonHint.Hint(p, ctx);   // a surface WinUI does not draw (none on this line): the neutral fallback
        }
    }

    /// <summary>The keyed host the hint enters with (the notification card's rise and fade).</summary>
    static BoxEl Host(HintProps p, Element body, Edges4 margin) => new()
    {
        Key = "hint:" + p.Id, Direction = 1, MinWidth = 0f, Shrink = 0f, Margin = margin,
        Enter = new EnterExit(Dy: WinUiHintRules.EnterDy, Opacity: 0f, Active: true),
        Children = [body],
    };

    static Edges4 MarginOf(string surface) => surface switch
    {
        HintSurfaces.BellHeader => WinUiHintRules.BellMargin,
        _ => WinUiHintRules.FlyoutMargin,
    };

    static TeachingTip.PlacementMode PlacementOf(string surface) => surface switch
    {
        HintSurfaces.QueuePanel => TeachingTip.PlacementMode.Left,          // the pane sits on the right: the tip opens over the page, pointing in
        HintSurfaces.PageHead => TeachingTip.PlacementMode.BottomRight,     // under the back button, its tail on the button
        HintSurfaces.TitleBar => TeachingTip.PlacementMode.Bottom,          // under the bar, its tail on the bar's middle
        _ => TeachingTip.PlacementMode.Top,                                 // the player bar's controls: above them
    };

    // ── the InfoBar ─────────────────────────────────────────────────────────────────────────────────────────────────

    static Element Bar(HintProps p, float target)
    {
        string key = "hint:" + p.Id;
        var (title, message) = Lead(p.Done ? p.DoneText ?? p.Text : p.Text);
        bool worded = !p.Done && p.Dismiss is not null && p.DismissLabel is not null;
        var actions = new List<Element>(3);
        if (p.Done)
        {
            if (p.Change is { } change && p.ChangeLabel is { } changeLabel) actions.Add(CommonTouch.Link(changeLabel, change, target) with { Key = key + ":change" });
        }
        else
        {
            if (p.Act is { } act && p.ActionLabel is { } label)
                actions.Add(p.Action == HintAction.State
                    ? CommonTouch.Link(label, act, target) with { Key = key + ":act" }
                    : CommonTouch.Button(label, act, target, accent: p.Action == HintAction.Inline) with { Key = key + ":act" });
            // A dismiss in the hint's own words is a button beside the action, in the ✕'s place (the bar is then not closable).
            if (worded) actions.Add(CommonTouch.Button(p.DismissLabel!, p.Dismiss!, target) with { Key = key + ":dismiss" });
            if (p.NeverTips is { } never) actions.Add(More(key, never, target));
        }
        Element? action = actions.Count switch
        {
            0 => null,
            1 => actions[0],
            _ => new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = WinUiHintRules.ActionGap, Children = [.. actions] },
        };
        // The close button carries the hint's key (`:dismiss`) and, at Compact, the 40 target.
        var parts = new TemplateParts();
        parts[InfoBar.PartCloseButton] = target > 0f
            ? b => b with { Key = key + ":dismiss", MinWidth = target, MinHeight = target }
            : b => b with { Key = key + ":dismiss" };
        return InfoBar.Create(InfoBarSeverity.Informational, title, message, onClose: worded ? null : p.Dismiss, isClosable: !worded && p.Dismiss is not null,
            actionButton: action, parts: parts);
    }

    /// <summary>The "⋯" overflow: its menu offers "Don't show tips".</summary>
    static Element More(string key, Action never, float target)
        => new BoxEl
        {
            Key = key + ":more", Shrink = 0f, MinWidth = target, MinHeight = target, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children = [IconButton.Menu(Icons.More, () => [new MenuFlyoutItem(Loc.Get(Strings.Hints.NeverTips), default, true, never)],
                toolTip: Loc.Get(Strings.Common.More), size: target > 0f ? ControlSize.Large : ControlSize.Medium)],
        };

    /// <summary>A two-sentence line's first sentence (its title) and the rest (its message); a one-sentence line is all title.</summary>
    static (string Title, string Message) Lead(string line)
    {
        for (int i = 0; i < line.Length - 2; i++)
            if (line[i] is '?' or '.' or '!' && line[i + 1] == ' ') return (line[..(i + 1)], line[(i + 2)..]);
        return (line, "");
    }

    // ── the TeachingTip ─────────────────────────────────────────────────────────────────────────────────────────────

    sealed record TipProps(HintProps Hint, TeachingTip.PlacementMode Placement, float Target);

    /// <summary>Opens the tip at the hint's target while mounted (again when the offer becomes its confirmation); closes it on unmount.</summary>
    sealed class Tip : Component
    {
        public override Element Render()
        {
            var p = UseProps<TipProps>();
            var overlay = UseContext(Overlay.Service);
            // Again for another hint on the same slot, and when the offer becomes its confirmation.
            UseEffect(() => Open(overlay, p), DepKey.From(p.Hint.Id.GetHashCode(), p.Hint.Done ? 1 : 0));
            return s_zero;
        }

        Action? Open(IOverlayService overlay, TipProps p)
        {
            var hint = p.Hint;
            if (hint.Target is not { } target) return null;
            // Above a surface (the player bar): the tip hangs from the surface's top edge, never over its rows (see the file header).
            if (p.Placement == TeachingTip.PlacementMode.Top && hint.Bounds is { } bounds && Context.Scene is { } scene)
                overlay = new BarTopOverlay(overlay, () => BarTop(scene, target, bounds));
            string key = "hint:" + hint.Id;
            Element? actions = Actions(hint, p.Target);
            // The row says the dismiss in words: no header ✕ beside it. A confirmation keeps the ✕ (its dismiss only closes it).
            bool rowDismisses = !hint.Done && hint.Dismiss is not null;
            bool ours = false;   // our own close (unmount, the offer turning into its confirmation) is no answer
            OverlayHandle handle = TeachingTip.Show(overlay, target, t =>
            {
                t.Title = hint.Title ?? "";
                t.Body = hint.Done ? hint.DoneText ?? hint.Text : hint.Text;
                t.IsLightDismissEnabled = true;
                t.PreferredPlacement = p.Placement;
                // The buttons are the row's (WinUI's footer as one compact row with the "⋯"), not the engine's split footer: no action or
                // close content, so the footer collapses and the row takes its place under the line.
                if (actions is not null)
                {
                    t.HeroContent = actions;
                    t.HeroContentPlacement = TeachingTip.HeroPlacement.Bottom;
                }
                var parts = new TemplateParts();
                parts[TeachingTip.PartBubble] = b => b with { Key = key };
                if (rowDismisses) parts[TeachingTip.PartCloseButton] = b => b with { Visible = false };
                t.Parts = parts;
                t.Closed = reason =>
                {
                    if (ours) return;
                    if (reason == TeachingTip.CloseReason.CloseButton) hint.Dismiss?.Invoke();   // the ✕ of a confirmation
                    else hint.SetAside?.Invoke();                                                  // a click elsewhere, Escape
                };
            });
            return () =>
            {
                ours = true;
                handle.Close();
            };
        }

        /// <summary>The rect a tip above its surface hangs from: the control's own span across (the tail on its centre), from the surface's top
        /// edge down to the control's foot (a tip that has no room above flips below the control, never over it). Read on every placement, so
        /// it follows the bar. Without a live control it stands on the surface itself (the host closes the tip once its control, the owner, is
        /// gone); with neither, the empty rect, and the host closes it.</summary>
        static RectF BarTop(FluentGpu.Scene.SceneStore scene, Func<NodeHandle> target, Func<NodeHandle> bounds)
        {
            var node = target();
            var surface = bounds();
            bool control = !node.IsNull && scene.IsLive(node);
            bool bar = !surface.IsNull && scene.IsLive(surface);
            if (!control) return bar ? scene.AbsoluteRect(surface) : default;
            RectF a = scene.AbsoluteRect(node);
            float top = bar ? MathF.Min(a.Y, scene.AbsoluteRect(surface).Y) : a.Y;
            return new RectF(a.X, top, a.W, a.Y + a.H - top);
        }

        /// <summary>The tip's one row: the action and the dismiss as buttons at their own width from the content's start (WinUI's compact
        /// footer; the action the accent button for an inline switch), the "⋯" at the row's end; on a confirmation its "Change". Null with
        /// nothing to offer.</summary>
        static Element? Actions(HintProps hint, float target)
        {
            string key = "hint:" + hint.Id;
            var buttons = new List<Element>(2);
            if (hint.Done)
            {
                if (hint.Change is { } change && hint.ChangeLabel is { } changeLabel) buttons.Add(CommonTouch.Button(changeLabel, change, target) with { Key = key + ":change" });
            }
            else
            {
                if (hint.Act is { } act && hint.ActionLabel is { } label)
                    buttons.Add(CommonTouch.Button(label, act, target, accent: hint.Action == HintAction.Inline) with { Key = key + ":act" });
                if (hint.Dismiss is { } dismiss)
                    buttons.Add(CommonTouch.Button(hint.DismissLabel ?? Loc.Get(Strings.Hints.NotNow), dismiss, target) with { Key = key + ":dismiss" });
            }
            Element? more = !hint.Done && hint.NeverTips is { } never ? More(key, never, target) : null;
            if (buttons.Count == 0 && more is null) return null;
            var group = new BoxEl
            {
                Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = WinUiHintRules.TipButtonGap, Shrink = 1f, MinWidth = 0f,
                Children = [.. buttons],
            };
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Gap = WinUiHintRules.TipButtonGap,
                Grow = 1f, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Padding = WinUiHintRules.TipRowPadding,
                Children = more is { } m ? [group, m] : [group],
            };
        }
    }

    /// <summary>The overlay a tip above its surface opens through. <c>TeachingTip.Show</c> anchors to a node; this hands the host, in that
    /// node's place, the rect the tip hangs from (<c>Tip.BarTop</c>), live on every placement, and keeps the node as the owner, so the tip
    /// still closes with its control and nests its "⋯" menu as before. Everything else is the host's own.</summary>
    sealed class BarTopOverlay(IOverlayService inner, Func<RectF> rect) : IOverlayService
    {
        public OverlayHandle Open(Func<NodeHandle> anchor, Func<Element> content, FlyoutPlacement placement = FlyoutPlacement.BottomLeft)
            => inner.OpenAt(rect, content, placement, default, anchor);

        public OverlayHandle Open(Func<NodeHandle> anchor, Func<Element> content, FlyoutPlacement placement, PopupOptions options)
            => inner.OpenAt(rect, content, placement, options, anchor);

        public OverlayHandle OpenAt(Func<RectF> anchorRect, Func<Element> content, FlyoutPlacement placement, PopupOptions options = default, Func<NodeHandle>? owner = null)
            => inner.OpenAt(anchorRect, content, placement, options, owner);

        public OverlayHandle OpenAtLocal(Func<NodeHandle> owner, Point2 local, Func<Element> content, FlyoutPlacement placement = FlyoutPlacement.BottomEdgeAlignedLeft, PopupOptions options = default)
            => inner.OpenAtLocal(owner, local, content, placement, options);

        public void CloseTop() => inner.CloseTop();
        public void CloseAll() => inner.CloseAll();
        public bool AnyOpen => inner.AnyOpen;
        public bool IsAnchorPinned(NodeHandle scope) => inner.IsAnchorPinned(scope);
        public FluentGpu.Signals.IReadSignal<int> PinEpoch => inner.PinEpoch;
    }
}
