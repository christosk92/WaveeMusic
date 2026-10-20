// ── Themes/Zune/Structure/Views/Liked/ZuneLikedChipBar.cs — Zune's Liked chip rail (themes plan 6.3; P5 Z6b1) ──────────────────────────────────────
//
// Boards Z-Liked (+ -Small, -Compact, -Huge): "all" first and the curated and derived chips after it, FLAT rectangles 8 apart (a wash at rest, the accent fill with
// the on-accent ink when on), 32 tall and 40 at Compact (a hit target), ONE line that scrolls under the rail's edge fade. The chip is drawn here (not the engine's
// toggle) so the word "all" takes the label case role, which the toggle's style has no slot for. Selection is EXCLUSIVE (all + at most one), re-tapping the active
// chip clears it, and an un-evidenced curated chip is DISABLED as one contiguous tail (the evidence is a prefix, so the order IS the state). 0 DIP when there is no set. Under the page head (no rail) the line rides the page bar between its lead and its
// tools instead of a row of its own.
//
// The core host keeps the table's live filter (it is read under the table's provider), the selected tag and the two verbs; the view draws what it is handed. The
// chips' words are tags (names of genres and moods): never re-cased; "all" is a word the theme writes (the label case role lowers it).
//
// Numbers: `Rules\ZunePlaylistRules.cs` and `ZunePlaylistTiers`.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using Z = Wavee.Themes.Zune.ZunePlaylistRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's Liked chip rail.</summary>
internal static class ZuneLikedChipBar
{
    /// <summary>The registered template of <see cref="ViewKeys.LikedChipBar"/>.</summary>
    public static Element Template(LikedChipBarProps p, in ViewContext ctx)
    {
        if (p.Titles.Length == 0) return new BoxEl { Key = Z.LikedChipsKey + ":off", HitTestVisible = false };

        float target = ZunePlaylistTiers.Of(ctx.Tier()).Target;
        var selectAll = p.SelectAll;
        var select = p.Select;
        string? selected = p.Selected;
        var kids = new Element[p.Titles.Length + 1];
        // AllChip, not AllTracks: a chip beside "pop" and "k-pop", where the short word reads as one of the set.
        kids[0] = Chip("chip:all", Loc.Get(Strings.Detail.Filter.AllChip), word: true, selected is null, true, selectAll, target);
        for (int i = 0; i < p.Titles.Length; i++)
        {
            string tag = p.Titles[i];
            bool on = string.Equals(tag, selected, StringComparison.OrdinalIgnoreCase);
            bool available = i < p.Evidenced;
            kids[i + 1] = Chip("chip:" + tag, tag, word: false, on, available, available ? () => select(on ? null : tag) : null, target);
        }
        if (!p.Inline) return Controls.ChipRail(kids, p.ScrollKey) with { Key = Z.LikedChipsKey };

        // INLINE: the same one scrolling line inside the page bar, filling the room its lead and tools leave, its end fading where more chips wait.
        return Ui.ScrollView(new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
            // The viewport clips at its edge: the inset keeps the first and last chip's focus ring whole.
            Padding = new Edges4(Controls.ChipRailInsetX, 0f, Controls.ChipRailInsetX, 0f),
            Children = kids,
        }, horizontal: true) with
        {
            Key = Z.LikedChipsKey, Grow = 1f, MinWidth = 0f, AutoEdgeFade = true, AutoEdgeFadeBand = Z.InlineChipFade,
            SuppressScrollBar = true, ScrollKey = p.ScrollKey,
        };
    }

    /// <summary>One chip: a flat rectangle (a wash at rest, the accent fill with the on-accent ink when on), <paramref name="target"/> tall, Shrink 0 (on a non-wrapping
    /// row flex would otherwise compress every chip and ellipsise the labels instead of letting the rail overflow). An un-evidenced chip is SHOWN AND DISABLED.</summary>
    static Element Chip(string key, string label, bool word, bool on, bool available, Action? click, float target) => new BoxEl
    {
        // At least as wide as it is tall: a one-word chip ("All") keeps the touch floor's 40 at Compact.
        Key = key, Direction = 0, Height = target, MinWidth = target, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(Z.ChipPadX, 0f, Z.ChipPadX, 0f),
        Fill = on ? Tok.AccentDefault : R.Layer.RowHover,
        HoverFill = on ? Tok.AccentSecondary : R.Layer.ChromeHover, PressedFill = on ? Tok.AccentTertiary : R.Layer.RowPressed,
        Opacity = available ? 1f : ZuneBandRules.DisabledOpacity,
        Role = AutomationRole.ToggleButton, Focusable = available, AllowFocusOnInteraction = false, IsEnabled = available,
        Cursor = available ? CursorId.Hand : (CursorId?)null, OnClick = click, BrushTransitionMs = Design.Motion.Faster,
        Children =
        [
            R.Type.DenseTitle(label) with
            {
                Color = on ? Tok.TextOnAccentPrimary : Tok.TextPrimary, Case = word ? R.Case.For(LabelKind.Command) : TextCase.AsWritten,
                MaxLines = 1, Wrap = TextWrap.NoWrap, MinWidth = 0f,
            },
        ],
    };
}
