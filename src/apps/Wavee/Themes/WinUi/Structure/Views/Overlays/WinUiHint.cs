// ── Themes/WinUi/Structure/Views/Overlays/WinUiHint.cs — WinUI's `Hint.Line` (settings rework phase 6, design 6.1-6.3 and 7.2) ─────────────
//
// WinUI draws a hint in WinUI's own two controls, never a ContentDialog:
//
//   InfoBar      where the hint lives IN a panel (the lyrics pane's footer, on an opaque base because the full screen's lyrics lie over art;
//                the devices flyout under its menu, the bell panel under its header, the top of Home): Informational, closable (the ✕ is "not now": the hint's own snooze), its one action as the InfoBar's button
//                (an inline switch the accent button, a link the standard one, a state line's "Change" a hyperlink and no ✕), and a "⋯" whose menu
//                offers "Don't show tips". A two-sentence line puts its first sentence in the title (W-InContext: "Want every word timed as it is
//                sung?" over "Word-by-word lyrics run on this PC."). On Home it floats on an opaque card with the flyout elevation (it lies over
//                the page, as the runtime banner does). After an inline write: the confirmation ("On · Normal") and its "Change" link.
//   TeachingTip  where the hint POINTS at one control (the play button, the volume slider, the queue's Crossfade switch, the back button, the
//                title bar): targeted, light dismiss, no focus taken (the engine tip has no focus trap); the dial's name as its title, the line as
//                its body, the action button and "Not now" in its footer, the "⋯" in its foot. A click elsewhere sets it aside for the session;
//                "Not now" snoozes it. After an inline write the tip is replaced by the confirmation tip (its title, "On", "Change").
//
// Keys: the InfoBar's host and the tip's bubble are `hint:<id>`, the parts `hint:<id>:act`, `:change`, `:more`. The entrance is the notification
// card's (a short rise and fade); the tip's is the engine's TeachingTip expand. Nothing here moves the page: an in-place slot eases its own
// height (core), the Home banner and the tip float.

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
        var actions = new List<Element>(2);
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
        return InfoBar.Create(InfoBarSeverity.Informational, title, message, onClose: p.Dismiss, isClosable: p.Dismiss is not null,
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

        static Action? Open(IOverlayService overlay, TipProps p)
        {
            var hint = p.Hint;
            if (hint.Target is not { } target) return null;
            bool ours = false;   // our own close (unmount, the offer turning into its confirmation) is no answer
            OverlayHandle handle = TeachingTip.Show(overlay, target, t =>
            {
                t.Title = hint.Title ?? "";
                t.Body = hint.Done ? hint.DoneText ?? hint.Text : hint.Text;
                t.IsLightDismissEnabled = true;
                t.PreferredPlacement = p.Placement;
                if (hint.Done)
                {
                    if (hint.Change is { } change && hint.ChangeLabel is { } changeLabel) { t.ActionButtonContent = changeLabel; t.ActionButtonClick = change; }
                }
                else
                {
                    if (hint.Act is { } act && hint.ActionLabel is { } label)
                    {
                        t.ActionButtonContent = label;
                        t.ActionButtonClick = act;
                        t.ActionButtonIsAccent = hint.Action == HintAction.Inline;
                    }
                    t.CloseButtonContent = Loc.Get(Strings.Hints.NotNow);
                    if (hint.NeverTips is { } never)
                    {
                        t.HeroContent = new BoxEl
                        {
                            Direction = 0, Justify = FlexJustify.End, Grow = 1f, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                            Padding = WinUiHintRules.TipFootPadding,
                            Children = [More("hint:" + hint.Id, never, p.Target)],
                        };
                        t.HeroContentPlacement = TeachingTip.HeroPlacement.Bottom;
                    }
                }
                var parts = new TemplateParts();
                parts[TeachingTip.PartBubble] = b => b with { Key = "hint:" + hint.Id };
                t.Parts = parts;
                t.Closed = reason =>
                {
                    if (ours) return;
                    if (reason == TeachingTip.CloseReason.CloseButton) hint.Dismiss?.Invoke();   // "Not now" (the ✕ of a confirmation)
                    else hint.SetAside?.Invoke();                                                  // a click elsewhere, Escape
                };
            });
            return () =>
            {
                ours = true;
                handle.Close();
            };
        }
    }
}
