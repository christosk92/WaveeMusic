// ── Themes/WinUi/Structure/Views/Discovery/Home/Board/WinUiHomeTileOptions.cs — "Customize widget": the card becomes its settings (spec 8.1) ──
//
// The widget's customization state (Learn, widget states): the card is titled "Customize {widget}", one field per option (vertical RadioButtons under
// five values, a ComboBox from five, a ToggleSwitch for a toggle and for live updates), "Done" in the accent. Each choice applies at once; the card grew to
// fit and the others slid aside (the grid packs it at `OpenExtent`). Done, Esc or opening another card closes it; one visit is one undo step; focus
// returns to the card's ⋯.

using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's options presenter.</summary>
internal static class WinUiHomeTileOptions
{
    /// <summary>The registered template of <see cref="ViewKeys.HomeTileOptions"/>.</summary>
    public static Element Template(HomeTileOptionsProps p, in ViewContext ctx)
    {
        var kids = new List<Element>(p.Options.Length + 2)
        {
            new BoxEl
            {
                Key = "home:options:head", Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                MinHeight = WinUiHomeBoardRules.OptionsHeadH,
                Children =
                [
                    Ui.BodyStrong(Strings.Theme.Winui.Home.Board.CustomizeTitle(name: p.Title)) with
                        { Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    Button.Accent(Loc.Get(Strings.Theme.Winui.Home.Board.Done), p.Done) with { Key = "home:options:done", Shrink = 0f },
                ],
            },
        };
        foreach (var o in p.Options)
        {
            var opt = o;
            string current = p.Value(opt.Key);
            string label = Loc.Get(opt.LabelKey);
            switch (opt.Kind)
            {
                case OptionKind.Toggle:
                    kids.Add(ToggleSwitch.Create(new Signal<bool>(current is "1" or "on" or "true"), on => p.Set(opt.Key, on ? "on" : "off"), header: label)
                        with { Key = "home:options:" + opt.Key });
                    break;
                default:
                {
                    var labels = new string[opt.Values.Length];
                    int at = 0;
                    for (int i = 0; i < labels.Length; i++) { labels[i] = Loc.Get(opt.Values[i].LabelKey); if (opt.Values[i].Value == current) at = i; }
                    var values = opt.Values;
                    Action<int> pick = i => { if ((uint)i < (uint)values.Length) p.Set(opt.Key, values[i].Value); };
                    kids.Add((labels.Length < WinUiHomeBoardRules.RadioMax
                        ? RadioButtons.Create(labels, new Signal<int>(at), pick, header: label)
                        : ComboBox.Create(labels, new Signal<int>(at), onChange: pick, header: label)) with { Key = "home:options:" + opt.Key });
                    break;
                }
            }
        }
        if (p.Live && p.ToggleLive is { } toggle && p.Paused is { } paused)
            kids.Add(ToggleSwitch.Create(new Signal<bool>(!paused()), _ => toggle(), header: Loc.Get(Strings.Theme.Winui.Home.Board.LiveUpdates))
                with { Key = "home:options:live" });
        return new BoxEl
        {
            Key = "home:options", Direction = 1, Grow = 1f, MinWidth = 0f, Gap = Spacing.M, AlignItems = FlexAlign.Stretch,
            Padding = new Edges4(WinUiHomeBoardRules.CardPad, Spacing.S, WinUiHomeBoardRules.CardPad, WinUiHomeBoardRules.CardPad),
            Children = kids.ToArray(),
        };
    }
}
