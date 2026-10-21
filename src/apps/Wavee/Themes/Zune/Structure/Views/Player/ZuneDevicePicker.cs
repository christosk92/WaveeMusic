// ── Themes/Zune/Structure/Views/Player/ZuneDevicePicker.cs — Zune's `Player.DevicePicker` (P5 Z4a) ──────────────────────────────────
//
// Board Z-PlayBar-Huge ("play on"): the body of the picker the bar's device word and its "playing on" line open (the core owns the overlay:
// upward, light dismiss, the picker-opened side effect). A 340 column: the "play on" head, then the core's decided entries as Zune rows,
// a device row 52 tall with an 8 dot (the accent on the device in use), its name (14/20, the accent in use; a name keeps its own casing) over
// its note (12/16), an information row (a section's header, the quality echo, "no devices") as a small cased line, a divider between the
// sections. It scrolls well before the window edge (`props.MaxHeight`) and re-renders while open, so a transfer landing moves the accent in
// place. Choosing a row runs its verb and closes the picker.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

using P = Wavee.Themes.Zune.ZunePanelBodies;

namespace Wavee.Themes.Zune;

/// <summary>Zune's device picker.</summary>
internal static class ZuneDevicePicker
{
    /// <summary>The node key of the picker's column (the mounted tests find it).</summary>
    public const string Key = ZunePlayerLayout.PickerKey;

    /// <summary>The registered template of <see cref="ViewKeys.DevicePicker"/>.</summary>
    public static Element Template(DevicePickerProps props, in ViewContext ctx)
    {
        var view = props.Read();
        float windowH = ctx.Hooks.UseContextSignal(Viewport.Size).Value.Height;
        var entries = view.Entries;
        var rows = new List<Element>(entries.Count + 1) { Small(Loc.Get(Strings.Theme.Zune.Player.PlayOn), P.PickerHeadH) };
        for (int i = 0; i < entries.Count; i++) rows.Add(Row(entries[i], props.Close, i));
        return new BoxEl
        {
            Key = Key, Width = P.PickerW, Direction = 1,
            Children =
            [
                new ScrollEl
                {
                    MaxHeight = props.MaxHeight(windowH), ContentSized = true, AutoEdgeFade = true,
                    Content = new BoxEl { Direction = 1, Padding = new Edges4(0f, P.PickerPadY, 0f, P.PickerPadY), Children = rows.ToArray() },
                },
            ],
        };
    }

    static Element Row(in DevicePickerEntry e, Action close, int index) => e.Kind switch
    {
        DevicePickerEntryKind.Separator => new BoxEl { Key = "sep:" + index, Height = 1f, Margin = P.PickerDividerMargin, Fill = Tok.StrokeDividerDefault },
        DevicePickerEntryKind.Info => Small(e.Label, P.PickerHeadH) with { Key = "info:" + index },
        _ => Device(e, close) with { Key = "dev:" + index },
    };

    /// <summary>A small cased line in the tertiary ink (the head, a section's header, an information row).</summary>
    static BoxEl Small(string text, float height) => new()
    {
        Height = height, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Padding = new Edges4(P.PickerPadX, 0f, P.PickerPadX, 0f),
        Children =
        [
            new TextEl(text)
            {
                Size = P.PickerSmallSize, LineHeight = P.PickerSmallLine, Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label),
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
        ],
    };

    /// <summary>A device or an output: the dot, the name over its note; the accent while it is the one in use.</summary>
    static BoxEl Device(DevicePickerEntry e, Action close)
    {
        bool on = e.Kind == DevicePickerEntryKind.Radio && e.IsChecked;
        bool enabled = e.Enabled && e.Invoke is not null;
        var invoke = e.Invoke;
        var text = new List<Element>(2)
        {
            new TextEl(e.Label)
            {
                Size = P.PickerNameSize, LineHeight = P.PickerNameLine, Color = !e.Enabled ? Tok.TextDisabled : on ? Tok.AccentTextPrimary : Tok.TextPrimary,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
        };
        if (e.Accelerator is { Length: > 0 } note)
            text.Add(new TextEl(note) { Size = P.PickerSmallSize, LineHeight = P.PickerSmallLine, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
        return new BoxEl
        {
            Height = P.PickerRowH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Gap = P.PickerGap,
            Padding = new Edges4(P.PickerPadX, 0f, P.PickerPadX, 0f),
            HoverFill = enabled ? R.Layer.RowHover : ColorF.Transparent, PressedFill = enabled ? R.Layer.RowPressed : ColorF.Transparent,
            Role = e.Kind == DevicePickerEntryKind.Radio ? AutomationRole.RadioButton : AutomationRole.Button,
            Focusable = enabled, IsEnabled = enabled, Cursor = enabled ? CursorId.Hand : null,
            OnClick = enabled ? () => { invoke!(); close(); } : null,
            Children =
            [
                new BoxEl
                {
                    Width = P.PickerDot, Height = P.PickerDot, Shrink = 0f, Corners = Radii.Circle(P.PickerDot),
                    Fill = on ? Tok.AccentDefault : Tok.TextPrimary with { A = P.PickerDotRestA },
                },
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, Children = text.ToArray() },
            ],
        };
    }
}
