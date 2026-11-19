// ── Themes/Common/CommonDevicePicker.cs — the device picker WinUI and Classic share: where Wavee plays (the 2026-11-19 redesign) ──────────
//
// Boards: handoff/device-picker-2026-11-19/boards/Main.dc.html (playing on this PC) and W-Elsewhere.dc.html (playing on a Connect device). The
// owner chose this one picker for Classic too (2026-11-19: "Classic's device picker should be the same as WinUI's"), so it lives here and both
// structures register it (`Register`); each paints it with its own tokens (colours, the card's and the controls' corner radii, the radio's
// style, the accent). A Windows quick-settings flyout: "Playing on" with a refresh button, the card (the device's tile in the accent, its plain
// name over how it is connected, the playing bars, the volume slider; while another device plays the accent button that brings playback back),
// then the sections as radio rows ("This PC": Match Windows and the outputs; "Other devices": Spotify Connect, or one line saying nothing else
// is nearby; the other devices first while one of them plays), a full-bleed divider and the footer (the quality chip, a door to its dial, and
// "Sound settings" over the switches at rest), the hint slot under it. The bar's `ToggleDevicePicker` owns the overlay (placement, light
// dismiss, the plate); the answer is the core's (`DevicePickerView`): this view only draws it, and a chosen row runs its verb and closes the picker.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

using D = Wavee.Themes.Common.CommonDevicePickerRules;
using P = Wavee.Strings.Player.DevicePicker;

namespace Wavee.Themes.Common;

/// <summary>The device picker WinUI and Classic share: a quick-settings flyout.</summary>
internal static class CommonDevicePicker
{
    /// <summary>Register the picker as <paramref name="templateId"/>'s <see cref="ViewKeys.DevicePicker"/>.</summary>
    public static void Register(ViewRegistryBuilder views, string templateId) => views.Add(ViewKeys.DevicePicker, Template, templateId);

    /// <summary>The template of <see cref="ViewKeys.DevicePicker"/>.</summary>
    public static Element Template(DevicePickerProps props, in ViewContext ctx)
    {
        var view = props.Read();
        var hooks = ctx.Hooks;
        Size2 vp = hooks.UseContextSignal(Viewport.Size).Value;
        var tiers = ctx.Tier();
        float target = CommonTiers.Target(tiers.Content);
        float w = D.WidthAt(tiers.Window, vp.Width);
        var cell = props.VolumeCell;
        var volume = props.Volume;
        hooks.UseSignalEffect(() => { if (cell is not null && volume is not null) cell.SetIfChanged(volume.Value); });

        var side = new Edges4(D.Pad, 0f, D.Pad, 0f);
        var kids = new List<Element>(9) { Head(props, target) with { Margin = side }, Card(view, props, w, target) with { Margin = side } };
        if (view.OthersFirst)
        {
            Section(kids, "others", Loc.Get(P.OtherDevices), view.Others, props.Close, side, nothingNearby: true);
            Section(kids, "this-pc", Loc.Get(P.ThisPc), view.ThisPc, props.Close, side, nothingNearby: false);
        }
        else
        {
            Section(kids, "this-pc", Loc.Get(P.ThisPc), view.ThisPc, props.Close, side, nothingNearby: false);
            Section(kids, "others", Loc.Get(P.OtherDevices), view.Others, props.Close, side, nothingNearby: true);
        }
        kids.Add(new BoxEl { Key = "devices:divider", Height = 1f, Shrink = 0f, Margin = D.DividerMargin, Fill = Tok.StrokeDividerDefault });
        if (view.Footer is { } footer) kids.Add(Footer(footer, props.Close, target) with { Margin = side });
        // The devices flyout's hint (design 6.3: Lossless available, the metered cap's state line) under the footer: it arrives without moving a row.
        kids.Add(new BoxEl { Key = "devices:hint", Direction = 1, Margin = side, Children = [Hints.Slot(HintSurfaces.DevicePicker, props.Close)] });

        return new BoxEl
        {
            Key = D.Key, Width = w, Direction = 1,
            Children =
            [
                new ScrollEl
                {
                    MaxHeight = props.MaxHeight(vp.Height), ContentSized = true, AutoEdgeFade = true, ScrollKey = "devices",
                    Content = new BoxEl { Direction = 1, Gap = D.Gap, Padding = new Edges4(0f, D.Pad, 0f, D.Pad), Children = kids.ToArray() },
                },
            ],
        };
    }

    /// <summary>"Playing on" and the refresh button (look for devices: this PC's outputs again, the other devices woken).</summary>
    static BoxEl Head(DevicePickerProps props, float target)
    {
        var kids = new List<Element>(2)
        {
            new TextEl(Loc.Get(P.PlayingOn))
            {
                Size = D.SectionSize, LineHeight = D.SectionLine, Color = Tok.TextSecondary, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        };
        if (props.LookForDevices is { } look)
        {
            float box = MathF.Max(D.HeadButton, target);
            kids.Add(ToolTip.Wrap(new BoxEl
            {
                Key = "devices:look", Width = box, Height = box, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = Radii.ControlAll, HoverFill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed,
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = look,
                Children = [Ui.Icon(Icons.Refresh, D.HeadGlyph, Tok.TextSecondary)],
            }, Loc.Get(P.LookForDevices)));
        }
        return new BoxEl { Key = "devices:head", Direction = 0, AlignItems = FlexAlign.Center, Padding = D.HeadPad, Children = kids.ToArray() };
    }

    /// <summary>The "Playing on" card.</summary>
    static BoxEl Card(DevicePickerView view, DevicePickerProps props, float w, float target)
    {
        var card = view.Card;
        if (card is null) return new BoxEl { Key = "devices:card" };
        string note = card.Remote ? P.There(card.Connection) : card.Connection.Length > 0 ? P.Here(card.Connection) : Loc.Get(P.ThisPc);
        if (card.Unresponsive) note = P.Joined(note, Loc.Get(P.NotResponding));

        var head = new List<Element>(3)
        {
            new BoxEl
            {
                Width = D.TileSize, Height = D.TileSize, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = Radii.CardAll, Fill = Tok.AccentDefault with { A = Tok.AccentDefault.A * D.TileAlpha },
                Children = [Ui.Icon(card.Glyph, D.TileGlyph, Tok.AccentTextPrimary)],
            },
            new BoxEl
            {
                Direction = 1, Gap = D.CardTextGap, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                Children =
                [
                    new TextEl(card.Name)
                    {
                        Size = D.CardNameSize, LineHeight = D.CardNameLine, Weight = D.CardNameWeight, Color = Tok.TextPrimary,
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
                    },
                    new TextEl(note)
                    {
                        Size = D.CardNoteSize, LineHeight = D.CardNoteLine, Color = card.Unresponsive ? Tok.TextTertiary : Tok.TextSecondary,
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
                    },
                ],
            },
        };
        if (props.Playing is { } playing) head.Add(new BoxEl { Shrink = 0f, Children = [Controls.Equalizer(playing, static () => Tok.AccentDefault, D.MeterH)] });

        var rows = new List<Element>(3) { new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = D.CardGap, Children = head.ToArray() } };
        if (props.VolumeCell is { } cell && props.SetVolume is { } set)
        {
            float length = MathF.Max(1f, w - 2f * D.Pad - 2f * D.CardPad - D.VolumeGlyph - D.VolumeValueW - 2f * D.CardGap);
            rows.Add(new BoxEl
            {
                Key = "devices:volume", Direction = 0, AlignItems = FlexAlign.Center, Gap = D.CardGap,
                Children =
                [
                    Ui.Icon(Icons.Volume, D.VolumeGlyph, Tok.TextPrimary),
                    VolumeSlider.Create(cell, set, new Slider.SliderOptions { IsThumbToolTipEnabled = false }, length, D.VolumeThickness),
                    new TextEl("")
                    {
                        Text = Prop.Of(() => FormatCache.Int((int)MathF.Round(cell.Value * 100f))), Size = D.VolumeTextSize, Color = Tok.TextPrimary,
                        Width = D.VolumeValueW, TextAlign = TextAlign.End, MaxLines = 1,
                    },
                ],
            });
        }
        if (card.PlayHere is { } playHere)
        {
            string label = card.HereName is { Length: > 0 } here ? P.PlayOnInstead(here) : Loc.Get(P.PlayOnThisPc);
            Action close = props.Close;
            rows.Add(Button.Create(label, () => { playHere(); close(); }, ButtonAppearance.Accent, glyph: card.HereGlyph) with
            {
                Key = "devices:play-here", Height = MathF.Max(D.ActionH, target), Shrink = 0f,
            });
        }
        return new BoxEl
        {
            Key = "devices:card", Direction = 1, Gap = D.CardGap, Padding = Edges4.All(D.CardPad), Corners = Radii.CardAll,
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Children = rows.ToArray(),
        };
    }

    /// <summary>A section: its header, then its rows (radio rows), or "Nothing else nearby" for an empty "Other devices".</summary>
    static void Section(List<Element> into, string id, string title, IReadOnlyList<DevicePickerEntry> rows, Action close, Edges4 side, bool nothingNearby)
    {
        if (rows.Count == 0 && !nothingNearby) return;
        into.Add(new BoxEl
        {
            Key = "devices:section:" + id, Padding = D.SectionPad, Margin = side,
            Children = [new TextEl(title) { Size = D.SectionSize, LineHeight = D.SectionLine, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f }],
        });
        if (rows.Count == 0)
        {
            into.Add(NothingNearby() with { Margin = side });
            return;
        }
        for (int i = 0; i < rows.Count; i++) into.Add(Row(rows[i], close) with { Margin = side });
    }

    /// <summary>One device row: the radio ring, the glyph, the name over its second line; the one in use filled.</summary>
    static BoxEl Row(DevicePickerEntry e, Action close)
    {
        var radio = RadioButton.DefaultStyle;
        bool on = e.IsChecked;
        var invoke = e.Invoke;
        bool enabled = e.Enabled && invoke is not null;
        var text = new List<Element>(2)
        {
            new TextEl(e.Label)
            {
                Size = D.RowNameSize, LineHeight = D.RowNameLine, Color = enabled ? Tok.TextPrimary : Tok.TextDisabled,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
            },
        };
        if ((e.Detail ?? e.Accelerator) is { Length: > 0 } note)
            text.Add(new TextEl(note)
            {
                Size = D.RowNoteSize, LineHeight = D.RowNoteLine, Color = e.Unresponsive ? Tok.TextTertiary : Tok.TextSecondary,
                Wrap = TextWrap.Wrap, MaxLines = D.RowNoteLines, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
            });
        return new BoxEl
        {
            Key = "devices:row:" + e.Id, Direction = 0, AlignItems = FlexAlign.Center, Gap = D.RowGap, MinHeight = D.RowMinH, Padding = D.RowPad,
            Corners = Radii.ControlAll, Fill = on ? Tok.FillSubtleSecondary : ColorF.Transparent,
            HoverFill = enabled ? R.Layer.RowHover : ColorF.Transparent, PressedFill = enabled ? R.Layer.RowPressed : ColorF.Transparent,
            Role = AutomationRole.RadioButton, Focusable = enabled, IsEnabled = enabled, Cursor = enabled ? CursorId.Hand : null,
            OnClick = enabled ? () => { invoke!(); close(); } : null,
            Children =
            [
                new BoxEl
                {
                    Width = D.Ring, Height = D.Ring, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.Circle(D.Ring),
                    Fill = on ? radio.OnRing : radio.OffFill, BorderWidth = on ? 0f : 1f, BorderColor = radio.OffBorder,
                    Children = on ? [new BoxEl { Width = D.RingDot, Height = D.RingDot, Corners = Radii.Circle(D.RingDot), Fill = radio.Dot }] : [],
                },
                Ui.Icon(e.Glyph, D.RowGlyph, enabled ? Tok.TextPrimary : Tok.TextDisabled),
                new BoxEl { Direction = 1, Gap = D.RowTextGap, Grow = 1f, Shrink = 1f, MinWidth = 0f, Children = text.ToArray() },
            ],
        };
    }

    /// <summary>"Nothing else nearby" and how to change that, on the rows' text column, beside the devices glyph.</summary>
    static BoxEl NothingNearby() => new()
    {
        Key = "devices:nothing-nearby", Direction = 0, AlignItems = FlexAlign.Center, Gap = D.RowGap, Padding = D.EmptyPad,
        Children =
        [
            new BoxEl { Width = D.Ring, Shrink = 0f },
            Ui.Icon(Icons.Devices, D.RowGlyph, Tok.TextSecondary),
            new BoxEl
            {
                Direction = 1, Gap = D.RowTextGap, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                Children =
                [
                    new TextEl(Loc.Get(P.NothingNearby)) { Size = D.RowNameSize, LineHeight = D.RowNameLine, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f },
                    new TextEl(Loc.Get(P.NothingNearbyHint)) { Size = D.RowNoteSize, LineHeight = D.RowNoteLine, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, Shrink = 1f, MinWidth = 0f },
                ],
            },
        ],
    };

    /// <summary>The footer: the quality chip (a door to the quality dial) or who sets the quality, then "Sound settings" over the switches.</summary>
    static BoxEl Footer(DevicePickerFooter f, Action close, float target)
    {
        float h = MathF.Max(D.ChipH, target);
        Element lead = f.Quality is { } quality && f.OpenQuality is { } openQuality
            ? new BoxEl
            {
                Key = "devices:quality", Direction = 0, AlignItems = FlexAlign.Center, Gap = D.ChipGap, Height = h, Padding = new Edges4(D.ChipPadX, 0f, D.ChipPadX, 0f),
                Shrink = 1f, MinWidth = 0f, Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary,
                HoverFill = Tok.FillSubtleTertiary, PressedFill = Tok.FillSubtleSecondary,
                Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand, OnClick = () => { openQuality(); close(); },
                Children =
                [
                    Ui.Icon(Icons.Settings, D.ChipGlyph, Tok.AccentTextPrimary),
                    new TextEl(quality) { Size = D.ChipSize, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f },
                ],
            }
            : new BoxEl
            {
                Key = "devices:quality-set-by", Direction = 0, AlignItems = FlexAlign.Center, Height = h, Shrink = 1f, MinWidth = 0f,
                Children = [new TextEl(f.QualitySetBy ?? "") { Size = D.ChipSize, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f }],
            };
        var sound = new List<Element>(2)
        {
            new TextEl(Loc.Get(Strings.Player.SoundSettings) + " ›") { Size = D.SoundSize, Color = Tok.TextPrimary, MaxLines = 1, TextAlign = TextAlign.End },
        };
        if (f.Quality is not null)   // while another device plays (it sets its own sound) the board drops the state line
            sound.Add(new TextEl(f.Sound) { Size = D.SoundNoteSize, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f, TextAlign = TextAlign.End });
        var openSound = f.OpenSound;
        return new BoxEl
        {
            Key = "devices:footer", Direction = 0, AlignItems = FlexAlign.Center, Gap = D.FooterGap, Padding = D.FooterPad,
            Children =
            [
                lead,
                new BoxEl { Grow = 1f, MinWidth = 0f },
                new BoxEl
                {
                    Key = "devices:sound", Direction = 1, AlignItems = FlexAlign.End, Justify = FlexJustify.Center, Gap = D.SoundGap, MinHeight = h, Shrink = 1f, MinWidth = 0f,
                    HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
                    Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand, OnClick = () => { openSound(); close(); },
                    Children = sound.ToArray(),
                },
            ],
        };
    }
}
