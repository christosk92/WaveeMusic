// ── Themes/Zune/Structure/Views/Screens/ZuneSettings.Results.cs — the "find a setting" results (settings rework phase 4, design 4.8, 7.3) ─────────────
//
// Boards: Z-Settings-Sections (the field over two results and "nothing else matches"), the settings concept's Z-Results. Zune's results are LINKS, not live rows
// (owner decision 3): the board's two-line row, the label in the body voice with the letters the query matched in the accent over the breadcrumb in the hint
// voice; a tap (or Enter in the pill) opens the dial in its category, where the arrival plays. The highlighted result (Down/Up in the pill) stands on the row
// wash. Nothing matching says so in the hint voice ("nothing matches “xyz”"), then "did you mean" with the words in the accent (each a new query) and "search the
// whole app" (the app's own search with the query).
//
// The ranking, the matched runs, the breadcrumb, the did-you-mean words and every verb are the core's (`SettingsSearchModel`); a keystroke re-renders only the
// lane's switch (`ZuneSettings.LaneSwitch`), which rebuilds this column.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Text;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using Z = Wavee.Themes.Zune.ZuneSettingsRules;

namespace Wavee.Themes.Zune;

internal static partial class ZuneSettings
{
    /// <summary>The results' scroller: the heading (from Small up; at Compact the panorama is the page's), the count, the rows, or the no-match state.</summary>
    static Element ResultsLane(ZuneKit k, SettingsSearchModel search, int highlight, float laneAir)
    {
        var lay = k.Lay;
        var kids = new List<Element>(search.Count + 3);
        if (!lay.ListIsStrip) kids.Add(SectionHeading(Loc.Get(Strings.Theme.Zune.Screens.SettingsResults)));
        if (search.NoMatch) kids.Add(NoMatch(k, search));
        else
        {
            string count = search.Close ? search.CloseLine : Strings.Theme.Zune.Screens.SettingsResultCount(search.Count, search.Ranked);
            kids.Add(ZuneText.Hint(count) with { Key = "zs:results:count", Margin = new Edges4(0f, 0f, 0f, Z.ResultCountGap) });
            for (int i = 0; i < search.Count; i++) kids.Add(ResultRow(k, search, i, i == highlight));
        }
        Element column = new BoxEl
        {
            Key = "zs:results", Direction = 1, AlignSelf = FlexAlign.Start, MinWidth = 0f,
            Basis = lay.ColumnW, MaxWidth = lay.ColumnW, Grow = lay.ColumnGrows ? 1f : 0f, Shrink = 1f,
            Children = kids.ToArray(),
        };
        // The scroll clip reaches a result's padding left of the column, so the highlighted row's wash bleeds past the column as the board draws it; on the
        // right the scroller runs over the page row's gutter to the page's edge, so its scrollbar sits on that edge (as the category lane's).
        return ScrollView(new BoxEl
        {
            Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Start, Padding = new Edges4(Z.ResultPadX, laneAir, Z.ResultPadX + lay.Gutter, Z.LaneBottom), Children = [column],
        }) with
        {
            Grow = 1f, Shrink = 1f, MinHeight = 0f, Margin = new Edges4(-Z.ResultPadX, 0f, -Z.ResultPadX - lay.Gutter, 0f), ScrollKey = "zs:results", Key = "zs:scroll:results",
        };
    }

    /// <summary>One result: the label (the matched letters in the accent) over its breadcrumb; a child of another result stands in under it. A link: a tap
    /// opens the dial in its category.</summary>
    static Element ResultRow(ZuneKit k, SettingsSearchModel search, int i, bool highlighted)
    {
        var lay = k.Lay;
        SettingsHit hit = search.Hit(i);
        // The label case keeps every UTF-16 index (`TextCaseMap`), so the matched runs index the lowered title as they index the title.
        string title = TextCaseMap.Apply(hit.Title, R.Case.For(LabelKind.Label));
        TextEl body = R.Type.TrackTitle("");
        float inset = hit.Under >= 0 ? Z.ResultUnderInset : 0f;
        int at = i;
        return new BoxEl
        {
            Key = SettingsKeys.Result(hit.Id), Direction = 1, Gap = Z.ResultLineGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, MinHeight = lay.Target,
            Padding = new Edges4(Z.ResultPadX + inset, lay.ResultPadY, Z.ResultPadX, lay.ResultPadY), Margin = new Edges4(-Z.ResultPadX, 0f, -Z.ResultPadX, 0f),
            Fill = highlighted ? R.Layer.RowHover : ColorF.Transparent, HoverFill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed,
            Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand, OnClick = () => search.Open(at),
            Children =
            [
                new SpanTextEl(Runs(title, search.Spans(i)))
                {
                    Size = body.Size, LineHeight = body.LineHeight, Weight = body.Weight, Color = Tok.TextPrimary, Wrap = TextWrap.WrapWholeWords, MinWidth = 0f,
                },
                ZuneText.Hint(hit.Place),
            ],
        };
    }

    /// <summary>The title as runs: the ink, with each matched run in the accent.</summary>
    static TextSpan[] Runs(string title, ReadOnlySpan<SettingsTextSpan> spans)
    {
        ColorF accent = R.Ink.AccentDecor;
        var runs = new List<TextSpan>(spans.Length * 2 + 1);
        int at = 0;
        foreach (var s in spans)
        {
            int start = Math.Clamp(s.Start, at, title.Length), end = Math.Clamp(s.Start + s.Length, start, title.Length);
            if (start > at) runs.Add(new TextSpan(title[at..start]));
            if (end > start) runs.Add(new TextSpan(title[start..end], Color: accent));
            at = end;
        }
        if (at < title.Length || runs.Count == 0) runs.Add(new TextSpan(title[at..]));
        return runs.ToArray();
    }

    /// <summary>Nothing matched: the line in the hint voice, then "did you mean" with the words in the accent (each a new query) and "search the whole app".</summary>
    static Element NoMatch(ZuneKit k, SettingsSearchModel search)
    {
        var words = new List<Element>(search.DidYouMeanCount + 2);
        if (search.DidYouMeanCount > 0)
        {
            words.Add(ZuneText.Cased(R.Type.DenseMeta(search.DidYouMeanLabel), LabelKind.Label) with { Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1 });
            for (int i = 0; i < search.DidYouMeanCount; i++)
            {
                string word = search.DidYouMean(i);
                words.Add(k.Cmd(word, () => search.Query.Value = word, accent: true) with { Key = "zs:results:mean:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            }
        }
        words.Add(k.Cmd(search.SearchAppLine(), search.SearchApp) with { Key = "zs:results:app" });
        return new BoxEl
        {
            Key = "zs:results:none", Direction = 1, Gap = Z.LabelGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Children =
            [
                ZuneText.Cased(R.Type.DenseMeta(Strings.Theme.Zune.Screens.SettingsNothingMatches(search.Ranked)), LabelKind.Label) with
                {
                    Color = Tok.TextTertiary, Wrap = TextWrap.WrapWholeWords, MinWidth = 0f,
                },
                new BoxEl { Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = words.ToArray() },
            ],
        };
    }
}
