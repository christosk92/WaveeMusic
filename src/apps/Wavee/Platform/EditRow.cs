// ── Platform/EditRow.cs ────────────────────────────────────────────────────────────────────────────────────────────
// The page chrome around an editable name or description (a playlist's hero, WinUI and Classic alike): the editable label (FluentGpu's
// `.Editable()`), Save and Cancel under it while it edits (the row's height eased, the house CardResize; why a name cannot be saved sits in the row),
// and the status ("Saving…", "Saved") as a chip over the label's end, which takes no room. The label itself never moves (EditableLabel's geometry).
//
// Role: SHELL (UI thread)

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static class EditRow
{
    const float Gap = 8f, HintSize = 12f, StatusH = 24f, StatusPad = 8f, StatusGlyph = 12f, StatusGap = 6f;

    /// <summary>The editable <paramref name="label"/> with its Save / Cancel row while <paramref name="editing"/> and its status chip
    /// (<paramref name="status"/>: 0 none, 1 saving, 2 saved). <paramref name="hint"/> says why Save is off while <paramref name="canSave"/> is false.</summary>
    public static Element Hero(Element label, bool editing, int status, Action save, Action cancel, bool canSave, string hint)
    {
        Element cell = status == 0 || editing
            ? label
            : new BoxEl { ZStack = true, MinWidth = 0f, Children = [label, Status(status == 2) with { AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.End }] };
        return new BoxEl
        {
            Key = "edit-row", Direction = 1, MinWidth = 0f, Animate = MotionRecipes.CardResize,
            Children =
            [
                cell,
                .. editing ? new Element[]
                {
                    new BoxEl
                    {
                        Key = "edit-actions", Direction = 0, Gap = Gap, AlignItems = FlexAlign.Center, Margin = new Edges4(0f, EditableLabel.BleedY + Gap, 0f, 0f),
                        Enter = new EnterExit(Opacity: 0f, Active: true),
                        Children =
                        [
                            new TextEl(canSave ? "" : hint) { Size = HintSize, Color = Tok.SystemFillCritical, Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                            Button.Create(Loc.Get(Strings.Detail.Edit.Save), save, ButtonAppearance.Accent, isEnabled: canSave) with { Key = "edit-save", Shrink = 0f },
                            Button.Create(Loc.Get(Strings.Detail.Edit.Cancel), cancel, ButtonAppearance.Standard) with { Key = "edit-cancel", Shrink = 0f },
                        ],
                    },
                } : [],
            ],
        };
    }

    /// <summary>"Saving…" with a ring, then "Saved" with the check.</summary>
    static BoxEl Status(bool saved) => new()
    {
        Key = "edit-status", Direction = 0, Gap = StatusGap, AlignItems = FlexAlign.Center, Height = StatusH,
        Padding = new Edges4(StatusPad, 0f, StatusPad, 0f), Corners = CornerRadius4.All(StatusH / 2f),
        Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault, HitTestVisible = false,
        Enter = new EnterExit(Opacity: 0f, Active: true),
        Children =
        [
            saved ? Icon(Icons.Check, StatusGlyph) with { Color = Tok.AccentTextPrimary } : ProgressRing.Indeterminate(StatusGlyph, foreground: Tok.TextSecondary),
            new TextEl(Loc.Get(saved ? Strings.Detail.Edit.Saved : Strings.Detail.Edit.Saving)) { Size = HintSize, Color = Tok.TextSecondary, MaxLines = 1 },
        ],
    };
}
