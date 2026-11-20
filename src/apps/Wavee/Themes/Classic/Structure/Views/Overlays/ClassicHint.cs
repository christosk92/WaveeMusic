// ── Themes/Classic/Structure/Views/Overlays/ClassicHint.cs — Classic's `Hint.Line` (settings rework phase 6, design 6.1-6.3 and 7.1) ──────────
//
// Classic draws every hint as ONE strip (C-InContext): the lightbulb, the line in the lyrics footer's 12/16 voice, one text action, a "⋯" whose
// menu offers "Don't show tips", and a close glyph ("×" is "not now": the hint's own snooze). Where it sits decides its plate and its rows:
//
//   in place     the lyrics pane's footer (the footer strip's own shape), the devices flyout under its menu, the bell panel under its header:
//                the surface's own plate, the subtle fill on the control radius, on the surface's gutter. A pane or a flyout is narrower than the
//                board's line, so the strip keeps the line and its close on the first row and gives the action and the "⋯" a second row under
//                the line (the same order, never squeezing the line to a word a row).
//   floating     by a control (the play button, the volume, the queue's Crossfade pill, the page head) and at the top of Home: Classic's notice
//                card (the toast card's plate the Settings home's suggestions wear), opaque with the flyout elevation, because it lies over the
//                page. A strip by a control floats in the overlay layer at that control (`CommonHintFloat`): the page under it never moves, and it
//                stays inside the surface the control lives in (the queue panel, under the Crossfade pair on its content column; the player bar,
//                above its top edge over the play button, the volume or the Lossless badge, never over the bar's title row; the page head's band,
//                on the page's gutter), as wide as that surface less Classic's inset at most, in the two-row form. Home's banner is one row at its
//                reading width (two at Compact).
//
// A hint with its own words for the dismiss (the Lossless badge's "Keep it") says them as a text action after its action, in the close glyph's
// place. A state line (the metered cap) keeps its "Change" and has no close. After an inline write: the check, the confirmation ("On · Normal"),
// its "Change" link and the close. Keys: `hint:<id>`, the parts `:act`, `:change`, `:more`, `:dismiss`. The entrance is the notification card's
// short rise and fade. At Compact every target is 40 at least.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Common;

namespace Wavee.Themes.Classic;

/// <summary>Classic's hint template (see the file header).</summary>
internal static class ClassicHint
{
    /// <summary>The registered template of <see cref="ViewKeys.Hint"/>.</summary>
    public static Element Template(HintProps p, in ViewContext ctx)
    {
        float target = CommonTiers.Target(ctx.Tier().Content);
        var band = ClassicOverlayTiers.For(ctx.Tier().Window);
        float viewport = ctx.Hooks.UseContextSignal(Viewport.Size).Value.Width;
        if (p.Target is not null)
            return CommonHintFloat.Create(p, PlaceOf(p.Surface, ctx), (int)ctx.Tier().Window,
                (h, width) => Stacked(h, target, floating: true, default) with { Width = width });
        return p.Surface switch
        {
            HintSurfaces.HomeBanner => Floating(p, target, band.PanelWidth(ClassicHintRules.BannerMaxWidth, viewport)),
            HintSurfaces.LyricsFooter => InPlace(p, target, ClassicHintRules.LyricsMargin),
            HintSurfaces.DevicePicker => InPlace(p, target, ClassicHintRules.FlyoutMargin),
            HintSurfaces.BellHeader => InPlace(p, target, ClassicHintRules.BellMargin),
            _ => CommonHint.Hint(p, ctx),   // a surface Classic does not draw (the backdrop is WinUI's): the neutral fallback
        };
    }

    /// <summary>Where a floating strip sits in its surface: under the queue's Crossfade pair on the panel's content column (it covers the list,
    /// not the pills), under the page head on the page's gutter; above the player bar's top edge (never over its rows: the Lossless badge sits
    /// under the title), centred on the play button or the badge, ending at the volume.</summary>
    static HintFloatPlace PlaceOf(string surface, in ViewContext ctx) => surface switch
    {
        HintSurfaces.QueuePanel => new(Below: true, HintFloatAlign.Start, ClassicHintRules.QueueInset, ClassicHintRules.FloatMaxWidth),
        HintSurfaces.PageHead => new(Below: true, HintFloatAlign.Start, ClassicTiers.PageOf(ctx.Tier()).Gutter, ClassicHintRules.FloatMaxWidth),
        HintSurfaces.PlayerVolume => new(Below: false, HintFloatAlign.End, ClassicHintRules.FloatInset, ClassicHintRules.FloatMaxWidth,
            Edge: true, EdgeGap: ClassicHintRules.FloatGap),
        _ => new(Below: false, HintFloatAlign.Center, ClassicHintRules.FloatInset, ClassicHintRules.FloatMaxWidth, Edge: true, EdgeGap: ClassicHintRules.FloatGap),
    };

    /// <summary>The strip on the notice card's plate, <paramref name="width"/> wide (its line wraps inside it): one row, two at Compact.</summary>
    static Element Floating(HintProps p, float target, float width)
    {
        if (target > 0f) return Stacked(p, target, floating: true, default) with { Width = width };
        var row = new List<Element>(6) { Glyph(p), Line(p) };
        Actions(p, target, row);
        Close(p, target, row);
        return Plate(p, floating: true, default) with
        {
            Width = width, Direction = 0, AlignItems = FlexAlign.Center, Gap = ClassicHintRules.StripGap, Children = [.. row],
        };
    }

    /// <summary>The strip on the surface's own plate: the glyph, the line and the close; the action and the "⋯" under the line.</summary>
    static Element InPlace(HintProps p, float target, Edges4 margin) => Stacked(p, target, floating: false, margin);

    /// <summary>The two-row strip: the glyph, the line and the close; the action and the "⋯" under the line.</summary>
    static BoxEl Stacked(HintProps p, float target, bool floating, Edges4 margin)
    {
        var top = new List<Element>(3) { Glyph(p), Line(p) };
        Close(p, target, top);
        var actions = new List<Element>(2);
        Actions(p, target, actions);
        var first = new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = ClassicHintRules.StripGap, MinWidth = 0f, Children = [.. top] };
        return Plate(p, floating, margin) with
        {
            Direction = 1, Gap = ClassicHintRules.RowGap,
            Children = actions.Count == 0
                ? [first]
                : [first, new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = ClassicHintRules.StripGap, Margin = ClassicHintRules.ActionsIndent,
                    Children = [.. actions],
                }],
        };
    }

    /// <summary>The keyed plate both forms share: the subtle fill in place, the notice card floating; the entrance.</summary>
    static BoxEl Plate(HintProps p, bool floating, Edges4 margin) => new()
    {
        Key = "hint:" + p.Id, MinWidth = 0f, Shrink = 0f, MinHeight = ClassicHintRules.StripMinHeight, Justify = FlexJustify.Center,
        Padding = ClassicHintRules.StripPadding, Margin = margin,
        Corners = floating ? Radii.OverlayAll : CornerRadius4.All(Radii.Control),
        Fill = floating ? Tok.FillSolidTertiary : Tok.FillSubtleSecondary,
        BorderWidth = floating ? ClassicHintRules.FloatBorder : 0f, BorderColor = Tok.StrokeSurfaceDefault,
        Shadow = floating ? Elevation.Flyout : null,
        Role = AutomationRole.InfoBar,
        Enter = new EnterExit(Dy: ClassicHintRules.EnterDy, Opacity: 0f, Active: true),
    };

    static TextEl Glyph(HintProps p)
        => Ui.Icon(p.Done ? Icons.Accept : SegoeGlyphs.Lightbulb, ClassicHintRules.Glyph, p.Done ? Tok.AccentTextPrimary : Tok.TextPrimary) with { Shrink = 0f };

    static TextEl Line(HintProps p) => new(p.Done ? p.DoneText ?? p.Text : p.Text)
    {
        Size = ClassicHintRules.TextSize, LineHeight = ClassicHintRules.TextLine, Color = Tok.TextPrimary,
        Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Wrap = TextWrap.Wrap,
    };

    /// <summary>The one text action (the confirmation's "Change" after an inline write), the dismiss in the hint's own words when it has them,
    /// and the "⋯".</summary>
    static void Actions(HintProps p, float target, List<Element> into)
    {
        string key = "hint:" + p.Id;
        if (p.Done)
        {
            if (p.Change is { } change && p.ChangeLabel is { } changeLabel) into.Add(TextAction(changeLabel, change, target) with { Key = key + ":change" });
            return;
        }
        if (p.Act is { } act && p.ActionLabel is { } label) into.Add(TextAction(label, act, target) with { Key = key + ":act" });
        if (Worded(p) && p.Dismiss is { } dismiss) into.Add(TextAction(p.DismissLabel!, dismiss, target) with { Key = key + ":dismiss" });
        if (p.NeverTips is { } never) into.Add(More(key, never, target));
    }

    /// <summary>The hint words its dismiss ("Keep it"): a text action, not the close glyph (one verb, one control).</summary>
    static bool Worded(HintProps p) => !p.Done && p.DismissLabel is not null;

    /// <summary>The close glyph ("not now"; on a confirmation it only closes it). A state line has none, nor a hint that words its dismiss.</summary>
    static void Close(HintProps p, float target, List<Element> into)
    {
        if (p.Dismiss is not { } dismiss || Worded(p)) return;
        into.Add(ToolTip.Wrap(IconButton.Create(Icons.Cancel, dismiss, target > 0f ? IconButton.DefaultStyle with { Size = target } : null)
            with { Key = "hint:" + p.Id + ":dismiss", Shrink = 0f }, Loc.Get(Strings.Common.Close)));
    }

    /// <summary>The strip's one text action: the subtle button (no plate at rest), the line's own ink, at the tier's target.</summary>
    static BoxEl TextAction(string label, Action act, float target)
        => Button.Create(label, act, ButtonAppearance.Subtle, target > 0f ? ControlSize.Large : ControlSize.Medium) with { Shrink = 0f };

    /// <summary>The "⋯" overflow: its menu offers "Don't show tips".</summary>
    static Element More(string key, Action never, float target)
        => new BoxEl
        {
            Key = key + ":more", Shrink = 0f, MinWidth = target, MinHeight = target, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children = [IconButton.Menu(Icons.More, () => [new MenuFlyoutItem(Loc.Get(Strings.Hints.NeverTips), default, true, never)],
                toolTip: Loc.Get(Strings.Common.More), size: target > 0f ? ControlSize.Large : ControlSize.Medium)],
        };
}
