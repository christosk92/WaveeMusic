// ── Themes/Zune/Structure/Views/Playlist/ZuneLocalFiles.cs — Zune's Local Files empty state (#240 UX S4; boards240 Onboarding, ThemesLocal) ──
//
// Zune's voice for `LocalFiles.Empty` (registered through `CommonBlocks.Register`; the scan line, the pill and the review are Common's and read Zune's
// roles and case): the empty playlist lead's shape (`ZunePlaylistExtras.EmptyLead`) on the list's inset, left-aligned, lowercase:
//
//   your music on this pc                              ← the lead's heading (the large one from Small up), never shrunk
//   add a folder and wavee plays everything in it…     ← the line, secondary, 560 measure, wrapping
//   add my music folder   choose a folder…             ← the verbs as words (the accent word first), the tier's target tall
//   your music folder is c:\users\…\music              ← the caption, while the Music folder is on offer
//   ┌ full-width band ───────────────────────────────┐ ← the drop band (Zune's banners are full-width bands): its glyph and two lines
//
// With no music folder, "find music on this pc" (#240, 0dm; `Find`, also Settings' empty music folders group) stands where the verbs and the
// caption were, flat on the inset (no plate: Zune draws no cards): its words, one line per folder (the tick and the folder's name in the track
// title's type, its count after it, the path under it), then the verbs as words ("add selected", "choose another folder…"). The count keeps one
// line from "counting…" to its number; at Compact it goes under the name. A folder with no song is greyed. No type is ever shrunk.
//
// The words, the states and the verbs are the core's (`LocalFilesList`); numbers in `Rules\ZuneLocalFilesRules.cs` and the playlist record.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;
using L = Wavee.Themes.Zune.ZuneLocalFilesRules;
using Z = Wavee.Themes.Zune.ZunePlaylistRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's Local Files empty state (see the file header).</summary>
internal static class ZuneLocalFiles
{
    /// <summary>The registered template of <see cref="ViewKeys.LocalFilesEmpty"/> in Zune.</summary>
    public static Element Empty(LocalFilesEmptyProps p, in ViewContext ctx)
    {
        var lay = ZunePlaylistTiers.Of(ctx.Tier());
        float target = lay.Target;
        TextEl title = lay.LeadLarge ? R.Type.HeroTitle(p.Title) : R.Type.Heading(p.Title);
        var verbs = new List<Element>(2);
        if (p.AddMusic is { } add && p.AddMusicLabel.Length > 0) verbs.Add(Word(L.AddMusicKey, p.AddMusicLabel, accent: true, add, target));
        verbs.Add(Word(L.ChooseKey, p.ChooseLabel, accent: verbs.Count == 0, p.Choose, target));

        var kids = new List<Element>(6)
        {
            title with
            {
                Key = L.TitleKey, Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Heading), MaxLines = 2, Wrap = TextWrap.WrapWholeWords,
                Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
            R.Type.DenseMeta(p.Body) with
            {
                Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), Wrap = TextWrap.WrapWholeWords, MinWidth = 0f, MaxWidth = L.Measure,
                Margin = new Edges4(0f, L.LineGap, 0f, 0f),
            },
            // No music folder: "find music on this pc" stands where the verbs and the caption were.
            p.Find is { } find
                ? new BoxEl { Direction = 1, MinWidth = 0f, MaxWidth = L.Measure, Margin = new Edges4(0f, L.FindGap, 0f, 0f), Children = [find] }
                : new BoxEl
                {
                    Direction = 0, Wrap = true, Gap = L.WordGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    // The first word's text stands on the inset: the words' own padding hangs into the gutter.
                    Margin = new Edges4(-L.WordPad, L.VerbsGap, 0f, 0f), Children = verbs.ToArray(),
                },
        };
        if (p.Find is null && p.MusicFolderCaption.Length > 0)
            kids.Add(R.Type.DenseMeta(p.MusicFolderCaption) with
            {
                Key = L.CaptionKey, Color = Tok.TextTertiary, Wrap = TextWrap.WrapWholeWords, MinWidth = 0f, Margin = new Edges4(0f, L.CaptionGap, 0f, 0f),
            });
        kids.Add(new BoxEl
        {
            Key = L.DropKey, Direction = 1, Justify = FlexJustify.Center, Gap = L.DropLineGap, MinWidth = 0f, Height = L.DropHeight, Shrink = 0f,
            Margin = new Edges4(0f, L.DropGap, 0f, 0f), Padding = new Edges4(L.DropPadX, 0f, L.DropPadX, 0f),
            Fill = Prop.Of(static () => R.Layer.FloatingPane),
            Children =
            [
                Icon(Icons.Download, L.DropGlyph, R.Ink.AccentDecor),
                R.Type.TrackTitle(p.DropTitle) with { Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                R.Type.DenseMeta(p.DropSub) with { Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
            ],
        });
        return new BoxEl
        {
            Key = L.Key, Direction = 1, MinWidth = 0f, Grow = 1f,
            Padding = new Edges4(Z.RecsInset, lay.LeadTop, Z.RecsInset, L.Bottom),
            Children = kids.ToArray(),
        };
    }

    /// <summary>The registered template of <see cref="ViewKeys.LocalFilesFind"/> in Zune (see the file header).</summary>
    public static Element Find(LocalFindProps p, in ViewContext ctx)
    {
        var lay = ZunePlaylistTiers.Of(ctx.Tier());
        float target = lay.Target;
        bool compact = lay.Target > L.FindStackTarget;
        var kids = new List<Element>(p.Rows.Count + 3)
        {
            R.Type.TrackTitle(p.Title) with { Key = L.FindTitleKey, Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Heading), Wrap = TextWrap.WrapWholeWords, MinWidth = 0f },
            R.Type.DenseMeta(p.Line) with
            {
                Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), Wrap = TextWrap.WrapWholeWords, MinWidth = 0f, Margin = new Edges4(0f, L.LineGap, 0f, 0f),
            },
        };
        var rows = new Element[p.Rows.Count];
        for (int i = 0; i < rows.Length; i++) rows[i] = FindRow(p.Rows[i], target, compact);
        kids.Add(new BoxEl { Key = L.FindRowsKey, Direction = 1, Gap = L.FindRowGap, MinWidth = 0f, Margin = new Edges4(0f, L.FindRowsGap, 0f, 0f), Children = rows });
        var verbs = new List<Element>(2) { Word(L.FindAddKey, p.AddLabel, accent: true, p.Add, target, enabled: p.CanAdd) };
        if (p.Choose is { } choose && p.ChooseLabel.Length > 0) verbs.Add(Word(L.FindChooseKey, p.ChooseLabel, accent: false, choose, target));
        kids.Add(new BoxEl
        {
            Direction = 0, Wrap = true, Gap = L.WordGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Margin = new Edges4(-L.WordPad, L.VerbsGap, 0f, 0f), Children = verbs.ToArray(),
        });
        return new BoxEl { Key = L.FindKey, Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = kids.ToArray() };
    }

    /// <summary>One folder: the tick with the folder's name (the track title's type), its count after it (under it at Compact), the path under the name.</summary>
    static Element FindRow(LocalFindRow r, float target, bool compact)
    {
        // The folder's name in Zune's case (lowercase words), as every Zune label.
        var parts = new TemplateParts();
        parts.Set<TextEl>(CheckBox.PartLabel, t => t with { Case = R.Case.For(LabelKind.Label) });
        var box = CheckBox.Create(r.Name, r.Ticked, isEnabled: !r.Empty, style: CheckBox.DefaultStyle with { MinHeight = target }, parts: parts);
        TextEl count = R.Type.DenseMeta(r.Count) with
        {
            Key = L.FindCountKey + r.Key, Color = r.Empty || r.Counting ? Tok.TextTertiary : Tok.TextSecondary, Case = R.Case.For(LabelKind.Label),
            MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, Grow = 1f, Shrink = 1f, MinWidth = 0f,   // fills its lane: a new count keeps the box
        };
        TextEl path = R.Type.DenseMeta(r.Path) with
        {
            Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
        };
        Element head = compact
            ? box
            : new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = L.FindCountGap, MinWidth = 0f,
                Children = [new BoxEl { Direction = 0, Shrink = 0f, Children = [box] }, count],
            };
        var lines = new List<Element>(2);
        if (compact) lines.Add(count);
        lines.Add(path);
        return new BoxEl
        {
            Key = L.FindRowKey + r.Key, Direction = 1, MinWidth = 0f,
            Children = [head, new BoxEl { Direction = 1, MinWidth = 0f, Margin = new Edges4(L.FindPathInset, 0f, 0f, 0f), Children = lines.ToArray() }],
        };
    }

    /// <summary>A verb as a word (Zune's text verbs): the accent word for the first, the secondary ink for the rest; the tier's target tall. A
    /// disabled word keeps its place in the disabled ink.</summary>
    static BoxEl Word(string key, string label, bool accent, Action onClick, float target, bool enabled = true) => new()
    {
        Key = key, Direction = 0, Height = target, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(L.WordPad, 0f, L.WordPad, 0f),
        HoverFill = enabled ? R.Layer.RowHover : default, PressedFill = enabled ? R.Layer.RowPressed : default,
        Role = AutomationRole.Button, Focusable = enabled, Cursor = enabled ? CursorId.Hand : CursorId.Arrow, OnClick = enabled ? onClick : null,
        Children =
        [
            R.Type.TrackTitle(label) with
            {
                Color = !enabled ? Tok.TextDisabled : accent ? Tok.AccentTextPrimary : Tok.TextSecondary,
                HoverColor = !enabled ? Tok.TextDisabled : accent ? Tok.AccentTextPrimary : Tok.TextPrimary,
                Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap,
            },
        ],
    };
}
