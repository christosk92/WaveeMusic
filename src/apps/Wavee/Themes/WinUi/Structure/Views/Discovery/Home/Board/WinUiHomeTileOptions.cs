// ── Themes/WinUi/Structure/Views/Discovery/Home/Board/WinUiHomeTileOptions.cs — "Customize widget": the card becomes its settings (spec 8.1) ──
//
// The widget's customization state (Learn, widget states): the card is titled "Customize {widget}", one field per option (vertical RadioButtons under
// five values, a ComboBox from five, a ToggleSwitch for a toggle; live updates is the schema's own "live" toggle), then the feature's app-wide settings
// under a caption label (the daylist's Clock and "Tell me when it refreshes", "For every daylist"), "Done" in the accent. Each choice applies at once; the card grew to
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
                case OptionKind.Toggle when opt.Values.Length >= 2:
                {
                    // A toggle's first value is its "on", the second its "off" (the schema's order).
                    string onValue = opt.Values[0].Value, offValue = opt.Values[1].Value;
                    kids.Add(ToggleSwitch.Create(new Signal<bool>(current == onValue), on => p.Set(opt.Key, on ? onValue : offValue), header: label)
                        with { Key = "home:options:" + opt.Key });
                    break;
                }
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
        AddDials(kids, p);
        return new BoxEl
        {
            Key = "home:options", Direction = 1, Grow = 1f, MinWidth = 0f, Gap = Spacing.M, AlignItems = FlexAlign.Stretch,
            Padding = new Edges4(WinUiHomeBoardRules.CardPad, Spacing.S, WinUiHomeBoardRules.CardPad, WinUiHomeBoardRules.CardPad),
            Children = kids.ToArray(),
        };
    }

    /// <summary>The app-wide settings after the tile's own options (settings design 5.3): a caption label ("For every daylist"), then each setting as a
    /// field like an option (vertical RadioButtons under five values, a ComboBox from five), bound to the value Settings shows and written through
    /// Settings' writer; under it, while one holds, the reason its pick does not act as it says. <see cref="WinUiHomeBoardRules.OpenExtent"/> counts them.</summary>
    static void AddDials(List<Element> kids, HomeTileOptionsProps p)
    {
        if (p.Dials.IsDefaultOrEmpty) return;
        if (p.DialsLabelKey is { } label)
            kids.Add(Ui.Caption(Loc.Get(label)).Secondary() with { Key = "home:options:dials", MaxLines = 1, MinWidth = 0f });
        foreach (var d in p.Dials)
        {
            var dial = d;
            var labels = new string[dial.ChoiceKeys.Length];
            for (int i = 0; i < labels.Length; i++) labels[i] = Loc.Get(dial.ChoiceKeys[i]);
            var at = new Signal<int>(dial.Value());
            Action<int> choose = i => { if ((uint)i < (uint)labels.Length) dial.Choose(i); };
            string header = Loc.Get(dial.LabelKey);
            kids.Add((labels.Length < WinUiHomeBoardRules.RadioMax
                ? RadioButtons.Create(labels, at, choose, header: header)
                : ComboBox.Create(labels, at, onChange: choose, header: header)) with { Key = "home:options:" + dial.Id });
            if (dial.NoteKey?.Invoke() is { } note)
                kids.Add(Ui.Caption(Loc.Get(note)).Secondary() with { Key = "home:options:" + dial.Id + ":note", Wrap = TextWrap.Wrap, MinWidth = 0f });
        }
    }
}
