// ── Themes/Zune/Structure/Views/Home/Board/ZuneHomeTileBack.cs — Zune's `Home.Tile.Options`: the back of a tile (spec 8.2 "Options") ──────────────
//
// "options" in the app bar turns the selected tile over (`TileMotion.TurnOver`, a horizontal squeeze with the face swapped edge-on; reduced motion
// cross-fades); a band (the daylist strip) turns over too. Outside customize (off for the release) a held press or a right-click on a tile with options
// selects it for them, and the bar comes up with "options" and "done" (owner, 2026-11-18). Its back carries the tile's name and "done" at the top, then
// each option as a small label over its choices as WORDS (the 22/28 light voice, lowercase), the current one in the accent, then the feature's app-wide
// settings the same way under "for every daylist" (the daylist's clock and "tell me when it refreshes"). A tap applies at once (the core's batch makes
// the visit one undo step). The tile grows to at least four cells wide and as tall as its words need (`ZuneHomeBoardRules.OpenExtent`); a back with more
// words than that scrolls inside the tile. "done", "options" again, Escape or selecting another tile turns it back.

using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using B = Wavee.Themes.Zune.ZuneHomeBoardRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's options presenter: the tile's back.</summary>
internal static class ZuneHomeTileBack
{
    /// <summary>The registered template of <see cref="ViewKeys.HomeTileOptions"/>.</summary>
    public static Element Template(HomeTileOptionsProps p, in ViewContext ctx)
    {
        var kids = new List<Element>(p.Options.Length + 1)
        {
            new BoxEl
            {
                Key = "home:options:head", Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinHeight = B.BackHeadH,
                Children =
                [
                    R.Type.Heading(p.Title) with { Case = R.Case.For(LabelKind.Heading), Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    Word("home:options:done", Loc.Get(Strings.Theme.Zune.Home.Done), lit: false, p.Done, command: true),
                ],
            },
        };
        foreach (var o in p.Options)
        {
            var opt = o;
            string current = p.Value(opt.Key);
            var words = new List<Element>(opt.Values.Length);
            foreach (var v in opt.Values)
            {
                var value = v;
                bool on = value.Value == current || opt.Kind == OptionKind.Toggle && IsOn(current) == IsOn(value.Value);
                words.Add(Word("home:options:" + opt.Key + ":" + value.Value, Loc.Get(value.LabelKey), on, () => p.Set(opt.Key, value.Value)));
            }
            kids.Add(new BoxEl
            {
                Key = "home:options:" + opt.Key, Direction = 1, MinWidth = 0f, Margin = new Edges4(0f, B.BackOptionGap, 0f, 0f),
                Children =
                [
                    R.Type.DenseMeta(Loc.Get(opt.LabelKey)) with
                    {
                        Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, MinHeight = B.BackLabelH, MinWidth = 0f,
                    },
                    new BoxEl { Direction = 0, Wrap = true, Gap = B.BackWordGap, MinWidth = 0f, Children = words.ToArray() },
                ],
            });
        }
        AddDials(kids, p);
        var column = new BoxEl
        {
            Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Padding = new Edges4(B.BackPadX, B.BackPadTop, B.BackPadX, B.BackPadBottom),
            Children = kids.ToArray(),
        };
        return new BoxEl
        {
            Key = B.BackKey, Direction = 1, Grow = 1f, MinWidth = 0f, Fill = R.Layer.ContentSurface,
            BorderColor = Tok.TextPrimary, BorderWidth = B.SelectedRing,
            Children = [Ui.ScrollView(column) with { MinWidth = 0f }],
        };
    }

    static bool IsOn(string v) => v is "1" or "on" or "true";

    /// <summary>The app-wide settings after the tile's own options (settings design 5.3): "for every daylist" in the micro voice, then each setting as an
    /// option is drawn, its label over its choices as words (the current one in the accent), bound to the value Settings shows and written through
    /// Settings' writer; under them, while one holds, the reason its pick does not act as it says. Never smaller type: words that outgrow the back
    /// wrap, and the back scrolls (`ZuneHomeBoardRules.OpenExtent` counts a line of words per three).</summary>
    static void AddDials(List<Element> kids, HomeTileOptionsProps p)
    {
        if (p.Dials.IsDefaultOrEmpty) return;
        if (p.DialsLabelKey is { } group)
            kids.Add(R.Type.MicroMeta(Loc.Get(group)) with
            {
                Key = "home:options:dials", Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, MinHeight = B.BackMicroH, MinWidth = 0f,
                Margin = new Edges4(0f, B.BackOptionGap, 0f, 0f),
            });
        foreach (var d in p.Dials)
        {
            var dial = d;
            int current = dial.Value();
            var words = new List<Element>(dial.ChoiceKeys.Length);
            for (int i = 0; i < dial.ChoiceKeys.Length; i++)
            {
                int index = i;
                words.Add(Word("home:options:" + dial.Id + ":" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), Loc.Get(dial.ChoiceKeys[i]),
                               i == current, () => dial.Choose(index)));
            }
            var block = new List<Element>(3)
            {
                R.Type.DenseMeta(Loc.Get(dial.LabelKey)) with
                {
                    Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, MinHeight = B.BackLabelH, MinWidth = 0f,
                },
                new BoxEl { Direction = 0, Wrap = true, Gap = B.BackWordGap, MinWidth = 0f, Children = words.ToArray() },
            };
            if (dial.NoteKey?.Invoke() is { } note)
                block.Add(R.Type.DenseMeta(Loc.Get(note)) with
                {
                    Key = "home:options:" + dial.Id + ":note", Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f,
                });
            kids.Add(new BoxEl
            {
                Key = "home:options:" + dial.Id, Direction = 1, MinWidth = 0f, Margin = new Edges4(0f, B.BackOptionGap, 0f, 0f),
                Children = block.ToArray(),
            });
        }
    }

    /// <summary>A choice as a word: the 22/28 light voice, lowercase, the accent when it is the current one. Its box is its ink, so a line's first word
    /// starts on the label column; its hit reaches half the words' gap past each side (<see cref="ZuneHomeBoardRules.BackWordSlop"/>), 40 tall.</summary>
    static Element Word(string key, string text, bool lit, Action act, bool command = false) => new BoxEl
    {
        Key = key, Direction = 0, AlignItems = FlexAlign.Center, MinHeight = B.BackWordH, Shrink = 0f,
        HitSlop = new Edges4(B.BackWordSlop, 0f, B.BackWordSlop, 0f),
        Focusable = true, Role = AutomationRole.Button, Cursor = CursorId.Hand, OnClick = act,
        Children =
        [
            (command ? R.Type.CardTitle(text) : R.Type.SheetTitle(text)) with
            {
                Color = lit ? Tok.AccentTextPrimary : command ? Tok.TextPrimary : Tok.TextSecondary,
                HoverColor = lit ? Tok.AccentTextPrimary : Tok.TextPrimary,
                Case = R.Case.For(command ? LabelKind.Command : LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap,
            },
        ],
    };
}
