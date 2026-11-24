// ── Themes/Zune/Structure/Views/Band/ZuneBandActivity.cs — the band's library activity word (#240 UX S1 in the Zune voice) ───────────────
//
// Zune has no sidebar and no "Local files" row: the band is the shell, so the chip is a quiet word at the end of row 1, before the pins: a mark (a
// ring while a scan runs, a tick after a background change, the amber dot when something needs the user) and the core's line in the band's small
// grey ink, cased as a label ("scanning · 1,930 of 6,016"). Compact and Small keep the mark alone (the 40-DIP target). Its slot's width is reserved
// while there is a music folder and closed with none, easing like the band's insets (a size channel): a state change never moves the pivots or
// the pins. Idle: no word (it fades; the slot stays). A click opens the activity flyout (Common's), under the word.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Themes.Common;
using Wavee.Theming;
using Wavee.Theming.Roles;
using A = Wavee.Themes.Zune.ZuneActivityRules;

namespace Wavee.Themes.Zune;

/// <summary>The band's library activity word (see the file header).</summary>
internal sealed class ZuneBandActivity : CommonActivityChipBase
{
    /// <summary>The band's record (its row height, whether it is narrow) and its small words' live ink.</summary>
    public sealed record Props(ZuneBandLayout Band, Func<ColorF> Ink);

    protected override FlyoutPlacement Placement => FlyoutPlacement.BottomEdgeAlignedRight;

    public override Element Render()
    {
        var p = UseProps<Props>();
        var chip = UseChip();
        var rec = p.Band;
        bool open = chip.Kind != LibraryActivity.ChipKind.Hidden;
        bool words = open && chip.Kind != LibraryActivity.ChipKind.Idle;
        Func<ColorF> ink = p.Ink;

        var kids = new List<Element>(1);
        if (words)
        {
            var parts = new List<Element>(2) { CommonActivityChipBase.Mark(chip.Kind) };
            if (!rec.Narrow)
                parts.Add(R.Type.MicroMeta(chip.Text) with
                {
                    Color = Prop.Of(ink), Case = R.Case.For(LabelKind.Label), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
                });
            kids.Add(new BoxEl
            {
                Key = A.Key, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Gap = A.Gap,
                Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = rec.Row1H,
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = Toggle, OnRealized = Realize,
                Enter = CommonActivityRules.Fade, Exit = CommonActivityRules.Fade, Transition = CommonActivityRules.FadeMotion,
                Children = parts.ToArray(),
            });
        }
        return new BoxEl
        {
            Key = A.SlotKey, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Height = rec.Row1H, Shrink = 0f, MinWidth = 0f,
            Width = open ? (rec.Narrow ? A.NarrowWidth : A.Width) : 0f, ClipToBounds = true,
            Animate = Design.Reduced ? null : ZuneBandTiers.InsetMotion,
            Children = kids.ToArray(),
        };
    }
}
