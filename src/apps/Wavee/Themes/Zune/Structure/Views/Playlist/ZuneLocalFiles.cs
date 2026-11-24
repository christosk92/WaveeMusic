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
            new BoxEl
            {
                Direction = 0, Wrap = true, Gap = L.WordGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                // The first word's text stands on the inset: the words' own padding hangs into the gutter.
                Margin = new Edges4(-L.WordPad, L.VerbsGap, 0f, 0f), Children = verbs.ToArray(),
            },
        };
        if (p.MusicFolderCaption.Length > 0)
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

    /// <summary>A verb as a word (Zune's text verbs): the accent word for the first, the secondary ink for the rest; the tier's target tall.</summary>
    static BoxEl Word(string key, string label, bool accent, Action onClick, float target) => new()
    {
        Key = key, Direction = 0, Height = target, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(L.WordPad, 0f, L.WordPad, 0f),
        HoverFill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
        Children =
        [
            R.Type.TrackTitle(label) with
            {
                Color = accent ? Tok.AccentTextPrimary : Tok.TextSecondary, HoverColor = accent ? Tok.AccentTextPrimary : Tok.TextPrimary,
                Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap,
            },
        ],
    };
}
