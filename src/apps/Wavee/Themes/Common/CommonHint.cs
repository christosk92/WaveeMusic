// ── Themes/Common/CommonHint.cs — the plain stand-ins for the hint, the explanation badge and the in-context dials (contract 1.115) ────
//
// Settings rework phase 3 (handoff/settings-2026-11-18/DESIGN.md 5.2, 6). `CommonBlocks.Register` registers each for a structure that draws
// none of its own (the badge and the page since phase 4, the panels since phase 5, the hints since phase 6: every shipped theme now draws
// its own hint, and its template hands a surface it does not draw to `Hint` here). Plain on purpose: the stock controls at the tier's hit
// target, an opaque plate (a hint may float over the page), no motion of its own, no look a theme would call its own.
//
//   Hint          a card keyed `hint:<id>`: the line, then its button (`hint:<id>:act`) and "Not now" (`:dismiss`); after an inline
//                 write, the confirmation and "Change" (`:change`); a state line keeps its link and has no "Not now".
//   Explainer     nothing without a clip (a badge shows only where its clip exists); with one, the caption line keyed `explain:<dialId>`.
//   SettingsDials a column keyed `dials:<surface>`: the label, then each dial's title over its value, read from the registry.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Common;

/// <summary>The stand-in templates (see the file header).</summary>
internal static class CommonHint
{
    static readonly BoxEl s_nothing = new() { Width = 0f, Height = 0f, Grow = 0f, Shrink = 0f, HitTestVisible = false };

    /// <summary>The hint stand-in.</summary>
    public static Element Hint(HintProps p, in ViewContext ctx)
    {
        float target = CommonTiers.Target(ctx.Tier().Content);
        var actions = new List<Element>(2);
        if (p.Done)
        {
            if (p.Change is { } change && p.ChangeLabel is { } changeLabel) actions.Add(CommonTouch.Link(changeLabel, change, target) with { Key = "hint:" + p.Id + ":change" });
        }
        else
        {
            if (p.Act is { } act && p.ActionLabel is { } label)
                actions.Add(CommonTouch.Button(label, act, target, accent: p.Action == HintAction.Inline) with { Key = "hint:" + p.Id + ":act" });
            if (p.Dismiss is { } dismiss) actions.Add(CommonTouch.Link(p.DismissLabel ?? Loc.Get(Strings.Hints.NotNow), dismiss, target) with { Key = "hint:" + p.Id + ":dismiss" });
        }
        var line = Ui.Body(p.Done ? p.DoneText ?? p.Text : p.Text) with { Shrink = 1f, MinWidth = 0f };
        return new BoxEl
        {
            Key = "hint:" + p.Id, Direction = 1, Gap = Spacing.S, MinWidth = 0f, Shrink = 1f, MaxWidth = CommonHintRules.MaxWidth,
            Margin = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),
            Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),
            Fill = Tok.FillSolidTertiary, Corners = CornerRadius4.All(Radii.Control),   // opaque: it floats over the page
            Children = actions.Count == 0
                ? [line]
                : [line, new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = [.. actions] }],
        };
    }

    /// <summary>The badge stand-in: nothing without a clip; the caption with one.</summary>
    public static Element Explainer(ExplainerProps p, in ViewContext ctx)
        => p.ClipPath is null ? s_nothing : new BoxEl { Key = SettingsKeys.Explain(p.DialId), MinWidth = 0f, Children = [Ui.Caption(p.Caption) with { Shrink = 1f, MinWidth = 0f }] };

    /// <summary>The in-context dials stand-in: each dial's title over its current value.</summary>
    public static Element SettingsDials(SettingsDialsProps p, in ViewContext ctx)
    {
        var rows = new List<Element>(p.DialIds.Length + 1);
        if (p.Label is { } label) rows.Add(Ui.Caption(label));
        foreach (string id in p.DialIds)
        {
            if (!SettingsSpec.TryDial(id, out SettingsDial dial)) continue;
            rows.Add(new BoxEl
            {
                Key = "dials:" + p.Surface + ":" + id, Direction = 1, MinWidth = 0f,
                Children = [Ui.Body(Loc.Get(dial.TitleKey)) with { Shrink = 1f, MinWidth = 0f }, Ui.Caption(ValueText(dial.Current))],
            });
        }
        return new BoxEl { Key = "dials:" + p.Surface, Direction = 1, Gap = Spacing.S, MinWidth = 0f, Children = [.. rows] };
    }

    static string ValueText(object? value) => value switch
    {
        bool on => Loc.Get(on ? Strings.Settings.Value.On : Strings.Settings.Value.Off),
        null => "",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };
}
