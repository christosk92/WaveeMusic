// ── Themes/Zune/Structure/Views/Home/Board/ZuneHomeTileBack.cs — Zune's `Home.Tile.Options`: the back of a tile (spec 8.2 "Options") ──────────────
//
// "options" in the app bar turns the selected tile over (`TileMotion.TurnOver`, a horizontal squeeze with the face swapped edge-on; reduced motion
// cross-fades). Its back carries the tile's name and "done" at the top, then each option as a small label over its choices as WORDS (the 22/28 light
// voice, lowercase), the current one in the accent. A tap applies at once (the core's batch makes the visit one undo step). The tile grows to at least
// four cells wide and as tall as its words need (`ZuneHomeBoardRules.OpenExtent`); a back with more words than that scrolls inside the tile. "done",
// "options" again, Escape or selecting another tile turns it back.

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
                    R.Type.Heading(p.Title) with { Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
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

    /// <summary>A choice as a word: the 22/28 light voice, lowercase, the accent when it is the current one; a 40 target at least.</summary>
    static Element Word(string key, string text, bool lit, Action act, bool command = false) => new BoxEl
    {
        Key = key, Direction = 0, AlignItems = FlexAlign.Center, MinHeight = B.BackWordH, Shrink = 0f,
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
