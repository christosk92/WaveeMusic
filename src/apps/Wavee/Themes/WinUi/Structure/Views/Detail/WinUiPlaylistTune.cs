// ── Themes/WinUi/Structure/Views/Detail/WinUiPlaylistTune.cs — WinUI's Tune strip (owner, 2026-11-18: "not a dialog but a nicer experience") ────────────
//
// Inline under the list bar while Tune is open, in place of the old centred dialog: a card on the layer wearing the AI stroke (`WinUiTuneRules`), its head
// (the sparkle tile; "Tune this mix" over the line that says what the mix follows, that it is rebuilding, or that the last pick failed; Reset while tuned;
// the close), then the directions as the stock filter chips. A chip applies at once: the mix's own direction is on, and while one applies that one is on,
// the others wait and the head's ring turns. The strip stays open after a pick, so the next direction is one click away.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using T = Wavee.Themes.WinUi.WinUiTuneRules;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's Tune strip.</summary>
internal static class WinUiPlaylistTune
{
    /// <summary>Compact's chip: the 40 touch target (the Liked chip bar's parts).</summary>
    static readonly TemplateParts s_compact = new()
    {
        [ToggleButton.PartRoot] = static e => e with { Shrink = 0f, Height = WinUiDetailRules.CompactTarget, MinHeight = WinUiDetailRules.CompactTarget, MinWidth = WinUiDetailRules.CompactTarget },
    };

    /// <summary>The registered template of <see cref="ViewKeys.PlaylistTune"/>.</summary>
    public static Element Template(PlaylistTuneProps p, in ViewContext ctx)
    {
        var choices = PlaylistTuneMenuModel.VisibleChoices(p.Options);
        var reset = PlaylistTuneMenuModel.ResetOption(p.Options, p.Selected);
        bool busy = p.Applying is not null;
        var pick = p.Pick;
        // Compact's chips take the 40 touch target, as Liked's do.
        TemplateParts? parts = ctx.Tier().Window == WidthTier.Compact ? s_compact : null;

        var chips = new Element[choices.Count];
        for (int i = 0; i < choices.Count; i++)
        {
            string id = choices[i].Identifier;
            bool on = busy ? id == p.Applying : id == p.Selected;
            chips[i] = Controls.Chip(choices[i].DisplayName ?? "", on, !busy, busy ? null : () => pick(id), parts) with { Key = "tune:" + id };
        }

        var head = new List<Element>(5) { Tile(), Lines(p, choices, busy), new BoxEl { Grow = 1f, MinWidth = 0f } };
        if (reset is { } r && !busy)
        {
            string resetId = r.Identifier;
            head.Add(HyperlinkButton.Create(Loc.Get(Strings.Detail.Tuning.Reset), () => pick(resetId)) with { Key = "tune:reset", Shrink = 0f });
        }
        head.Add(ToolTip.Wrap(Controls.IconAction(Icons.ChromeClose, p.Close, size: T.CloseEdge) with { Key = "tune:close" }, Loc.Get(Strings.Detail.Tuning.Close)));

        return new BoxEl
        {
            Key = "tune-strip", Direction = 1, Gap = T.StripGap, Padding = Edges4.All(T.StripPad), MinWidth = 0f,
            Corners = Radii.CardAll, Fill = Prop.Of(static () => Tok.FillCardDefault), BorderBrush = T.Stroke, BorderWidth = T.StrokeWidth,
            Children =
            [
                new BoxEl { Key = "tune-strip:head", Direction = 0, Gap = T.HeadGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = head.ToArray() },
                new BoxEl { Key = "tune-strip:chips", Direction = 0, Wrap = true, Gap = T.ChipGap, MinWidth = 0f, Children = chips },
            ],
        };
    }

    /// <summary>The sparkle tile: the AI wash under the sparkle.</summary>
    static Element Tile() => new BoxEl
    {
        Width = T.TileEdge, Height = T.TileEdge, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.ControlAll, Gradient = T.LitWash,
        Children = [Icon(Icons.RefineSparkle, T.TileGlyph, T.Sparkle)],
    };

    /// <summary>"Tune this mix" over its line: the failure, the rebuild (with the ring), the direction the mix follows, or the invitation.</summary>
    static Element Lines(PlaylistTuneProps p, List<TuneOption> choices, bool busy)
    {
        string line = p.Failed ? Loc.Get(Strings.Detail.Tuning.ApplyFailed)
            : busy ? Loc.Get(Strings.Detail.Tuning.Applying)
            : NameOf(choices, p.Selected) is { } name ? Strings.Detail.Tuning.Tuned(direction: name)
            : Loc.Get(Strings.Detail.Tuning.Subtitle);
        var text = Caption(line) with
        {
            Color = p.Failed ? Prop.Of(static () => Tok.SystemFillCritical) : Prop.Of(static () => Tok.TextSecondary),
            Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
        };
        Element row = busy
            ? new BoxEl { Direction = 0, Gap = T.RingGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = [ProgressRing.Create(size: T.RingSize), text] }
            : text;
        return new BoxEl
        {
            Direction = 1, Gap = T.LineGap, Shrink = 1f, MinWidth = 0f,
            Children = [BodyStrong(Loc.Get(Strings.Detail.Tuning.Title)) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }, row],
        };
    }

    static string? NameOf(List<TuneOption> choices, string? id)
    {
        if (id is null) return null;
        foreach (var c in choices) if (c.Identifier == id) return c.DisplayName;
        return null;
    }
}
