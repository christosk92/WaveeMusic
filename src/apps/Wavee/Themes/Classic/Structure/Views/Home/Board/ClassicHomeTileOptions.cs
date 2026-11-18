// ── Themes/Classic/Structure/Views/Home/Board/ClassicHomeTileOptions.cs — "Options": the tile opens in place (spec 8.3) ─────────────────────────
//
// The sidebar's "Section options" shape, inside the tile: its name and "Done" in the head, each option as a label over radio rows (a toggle is a
// switch), "Live updates" for a live source, then the feature's app-wide settings under their group's label (the daylist's Clock and "Tell me when it
// refreshes", "For every daylist"), each a label over radio rows. A long list ("which playlist", an Entity option) is a find box over the list, inside the tile (the Move to
// folder chooser's "Find a folder", moved out of its dialog). A choice applies at once; the tile grew to fit and the others slid aside (the grid packs it
// at `OpenExtent`). Done or Esc closes it; one visit is one undo step ("{name} updated" + Undo on the toast); focus returns to the tile.

using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Classic's options presenter.</summary>
internal static class ClassicHomeTileOptions
{
    /// <summary>A list this long or longer gets a find box over it.</summary>
    const int FindFrom = 7;

    /// <summary>The registered template of <see cref="ViewKeys.HomeTileOptions"/>.</summary>
    public static Element Template(HomeTileOptionsProps p, in ViewContext ctx)
        => Embed.Comp(new OptionsProps(p), static () => new Options()) with { Key = "home:options" };

    sealed record OptionsProps(HomeTileOptionsProps P);

    sealed class Options : Component
    {
        readonly Dictionary<string, Signal<string>> _find = new(StringComparer.Ordinal);

        public override Element Render()
        {
            var p = UseProps<OptionsProps>().P;
            var kids = new List<Element>(p.Options.Length * 2 + 2)
            {
                new BoxEl
                {
                    Key = "home:options:head", Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                    MinHeight = ClassicHomeBoardRules.OptionsHeadH,
                    Children =
                    [
                        Ui.BodyStrong(Strings.Theme.Classic.Home.Board.OptionsOf(name: p.Title)) with
                            { Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        ClassicEditBarParts.Done(p.Done) with { Key = "home:options:done", Shrink = 0f },
                    ],
                },
            };
            foreach (var o in p.Options)
            {
                var opt = o;
                string current = p.Value(opt.Key);
                string label = Loc.Get(opt.LabelKey);
                if (opt.Kind == OptionKind.Toggle)
                {
                    kids.Add(ToggleSwitch.Create(new Signal<bool>(current is "1" or "on" or "true"), on => p.Set(opt.Key, on ? "1" : "0"), header: label)
                        with { Key = "home:options:" + opt.Key });
                    continue;
                }
                kids.Add(Ui.Caption(label).Secondary() with { Key = "home:options:" + opt.Key + ":label", MaxLines = 1, MinWidth = 0f });
                var values = opt.Values;
                bool find = opt.Kind == OptionKind.Entity || values.Length >= FindFrom;
                string q = "";
                if (find)
                {
                    if (!_find.TryGetValue(opt.Key, out var text)) _find[opt.Key] = text = new Signal<string>("");
                    q = text.Value.Trim();
                    kids.Add(TextBox.Create(text, options: new TextBox.TextBoxOptions { Placeholder = label })
                        with { Key = "home:options:" + opt.Key + ":find" });
                }
                var shown = new List<OptionValue>(values.Length);
                foreach (var v in values)
                    if (q.Length == 0 || Loc.Get(v.LabelKey).Contains(q, StringComparison.CurrentCultureIgnoreCase)) shown.Add(v);
                var labels = new string[shown.Count];
                int at = -1;
                for (int i = 0; i < labels.Length; i++) { labels[i] = Loc.Get(shown[i].LabelKey); if (shown[i].Value == current) at = i; }
                kids.Add(RadioButtons.Create(labels, new Signal<int>(at), i => { if ((uint)i < (uint)shown.Count) p.Set(opt.Key, shown[i].Value); })
                    with { Key = "home:options:" + opt.Key });
            }
            // Live updates: its own switch only where the source's schema does not already carry the "live" option.
            bool liveInSchema = false;
            foreach (var o in p.Options) if (o.Key == "live") liveInSchema = true;
            if (p.Live && !liveInSchema && p.ToggleLive is { } toggle && p.Paused is { } paused)
                kids.Add(ToggleSwitch.Create(new Signal<bool>(!paused()), _ => toggle(), header: Loc.Get(Strings.Theme.Classic.Home.Board.LiveUpdates))
                    with { Key = "home:options:live" });
            AddDials(kids, p);
            return new BoxEl
            {
                Direction = 1, Grow = 1f, MinWidth = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Stretch,
                Padding = new Edges4(ClassicHomeBoardRules.CardPad, Spacing.XS, ClassicHomeBoardRules.CardPad, ClassicHomeBoardRules.CardPad),
                Children = kids.ToArray(),
            };
        }

        /// <summary>The app-wide settings after the tile's own (settings design 5.3), in the options' own grammar: the group's label ("For every
        /// daylist"), then each setting's label (in the primary ink, so it reads as the field under the group's) over its radio rows, bound to the value
        /// Settings shows and written through Settings' writer; under them, while one holds, the reason its pick does not act as it says.
        /// <see cref="ClassicHomeBoardRules.OpenExtent"/> counts them.</summary>
        static void AddDials(List<Element> kids, HomeTileOptionsProps p)
        {
            if (p.Dials.IsDefaultOrEmpty) return;
            if (p.DialsLabelKey is { } group)
                kids.Add(Ui.Caption(Loc.Get(group)).Secondary() with { Key = "home:options:dials", MaxLines = 1, MinWidth = 0f });
            foreach (var d in p.Dials)
            {
                var dial = d;
                kids.Add(Ui.Caption(Loc.Get(dial.LabelKey)).Primary() with { Key = "home:options:" + dial.Id + ":label", MaxLines = 1, MinWidth = 0f });
                var labels = new string[dial.ChoiceKeys.Length];
                for (int i = 0; i < labels.Length; i++) labels[i] = Loc.Get(dial.ChoiceKeys[i]);
                kids.Add(RadioButtons.Create(labels, new Signal<int>(dial.Value()), i => { if ((uint)i < (uint)labels.Length) dial.Choose(i); })
                    with { Key = "home:options:" + dial.Id });
                if (dial.NoteKey?.Invoke() is { } note)
                    kids.Add(Ui.Caption(Loc.Get(note)).Secondary() with { Key = "home:options:" + dial.Id + ":note", Wrap = TextWrap.Wrap, MinWidth = 0f });
            }
        }
    }
}
