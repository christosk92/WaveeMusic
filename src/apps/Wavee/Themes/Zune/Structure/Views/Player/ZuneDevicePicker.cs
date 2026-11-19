// ── Themes/Zune/Structure/Views/Player/ZuneDevicePicker.cs — Zune's `Player.DevicePicker`: where Wavee plays (the 2026-11-19 redesign) ──────
//
// Boards: handoff/device-picker-2026-11-19/boards/Z-Here.dc.html (playing on this PC) and Z-Elsewhere.dc.html (playing on a Connect device).
// Large lowercase words and no boxes: "playing on" over the device in use in the accent at display size (a long name wraps; Zune type is never
// shrunk), its note (how it is connected and what plays: the door to the quality dial), the volume line; while another device plays the
// accent plate back to this PC. Then the sections as light words ("this pc": the outputs, then match windows; "other devices": Spotify
// Connect, or one line saying nothing else is nearby; the other devices first while one of them plays), each device a word with its note at
// the end, the one in use in the accent; and the footer line (the switches at rest, "sound settings"), the hint slot under it. The bar's
// `ToggleDevicePicker` owns the overlay; the answer is the core's (`DevicePickerView`); a chosen row runs its verb and closes the picker.

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
using Wavee.Themes.Common;

using D = Wavee.Themes.Zune.ZuneDevicePickerRules;
using P = Wavee.Strings.Player.DevicePicker;

namespace Wavee.Themes.Zune;

/// <summary>Zune's device picker.</summary>
internal static class ZuneDevicePicker
{
    /// <summary>The node key of the picker's column.</summary>
    public const string Key = ZunePlayerLayout.PickerKey;

    /// <summary>The registered template of <see cref="ViewKeys.DevicePicker"/>.</summary>
    public static Element Template(DevicePickerProps props, in ViewContext ctx)
    {
        var view = props.Read();
        var hooks = ctx.Hooks;
        Size2 vp = hooks.UseContextSignal(Viewport.Size).Value;
        var tiers = ctx.Tier();
        float target = CommonTiers.Target(tiers.Content);
        float w = ZuneOverlayTiers.Of(tiers).PanelWidth(D.PanelW, vp.Width);
        var cell = props.VolumeCell;
        var volume = props.Volume;
        hooks.UseSignalEffect(() => { if (cell is not null && volume is not null) cell.SetIfChanged(volume.Value); });
        var lower = R.Case.For(LabelKind.Label);
        string? match = view.WindowsDefault is { } windowsDefault ? P.NowDevice(windowsDefault) : null;

        var kids = new List<Element>(16);
        Head(kids, view, props, w, target, lower);
        if (view.OthersFirst)
        {
            Section(kids, "others", Loc.Get(P.OtherDevices), view.Others, props.Close, null, lower, nothingNearby: true);
            Section(kids, "this-pc", Loc.Get(P.ThisPc), ThisPcOrder(view.ThisPc), props.Close, match, lower, nothingNearby: false);
        }
        else
        {
            Section(kids, "this-pc", Loc.Get(P.ThisPc), ThisPcOrder(view.ThisPc), props.Close, match, lower, nothingNearby: false);
            Section(kids, "others", Loc.Get(P.OtherDevices), view.Others, props.Close, null, lower, nothingNearby: true);
        }
        if (view.Footer is { } footer) kids.Add(Footer(footer, props.Close, target, lower));
        // The devices flyout's hint (design 6.3: Lossless available, the metered cap's state line) under the footer: it arrives without moving a row.
        kids.Add(new BoxEl { Key = "devices:hint", Direction = 1, Children = [Hints.Slot(HintSurfaces.DevicePicker, props.Close)] });

        return new BoxEl
        {
            Key = Key, Width = w, Direction = 1,
            Children =
            [
                new ScrollEl
                {
                    MaxHeight = props.MaxHeight(vp.Height), ContentSized = true, AutoEdgeFade = true, ScrollKey = "devices",
                    Content = new BoxEl { Direction = 1, Padding = D.Pad, Children = kids.ToArray() },
                },
            ],
        };
    }

    /// <summary>The board lists this PC's outputs first and match windows after them.</summary>
    static IReadOnlyList<DevicePickerEntry> ThisPcOrder(IReadOnlyList<DevicePickerEntry> rows)
    {
        if (rows.Count < 2 || rows[0].Id != "match-windows") return rows;
        var ordered = new DevicePickerEntry[rows.Count];
        for (int i = 1; i < rows.Count; i++) ordered[i - 1] = rows[i];
        ordered[^1] = rows[0];
        return ordered;
    }

    /// <summary>"playing on", the device in the accent, its note, the volume line, and while another device plays the way back.</summary>
    static void Head(List<Element> into, DevicePickerView view, DevicePickerProps props, float w, float target, TextCase lower)
    {
        var card = view.Card;
        if (card is null) return;
        Action close = props.Close;
        var eyebrow = new List<Element>(2)
        {
            new TextEl(Loc.Get(P.PlayingOn)) { Size = D.SmallSize, LineHeight = D.SmallLine, Color = Tok.TextSecondary, Case = lower, MaxLines = 1, MinWidth = 0f, Shrink = 1f },
        };
        if (props.Playing is { } playing) eyebrow.Add(new BoxEl { Shrink = 0f, Children = [Controls.Equalizer(playing, static () => Tok.AccentDefault, D.MeterH)] });
        into.Add(new BoxEl { Key = "devices:eyebrow", Direction = 0, AlignItems = FlexAlign.Center, Gap = D.MeterGap, Children = eyebrow.ToArray() });
        into.Add(new TextEl(card.Name)
        {
            Key = "devices:name", Size = D.NameSize, LineHeight = D.NameLine, Weight = D.NameWeight, CharSpacing = D.NameTracking, Color = Tok.AccentTextPrimary,
            Case = lower, Wrap = TextWrap.Wrap, MinWidth = 0f, Margin = new Edges4(0f, D.NameTop, 0f, 0f),
        });

        string connection = card.Connection.Length > 0 ? card.Connection : Loc.Get(P.ThisPc);
        if (card.Remote) connection = P.There(connection);
        if (card.Unresponsive) connection = P.Joined(connection, Loc.Get(P.NotResponding));
        var note = new TextEl(card.Quality is { } quality ? P.Joined(connection, quality) : connection)
        {
            Size = D.NoteSize, LineHeight = D.NoteLine, Color = Tok.TextSecondary, Case = lower, Wrap = TextWrap.Wrap, MinWidth = 0f, Shrink = 1f,
        };
        into.Add(card.Quality is not null && view.Footer?.OpenQuality is { } openQuality
            ? new BoxEl
            {
                Key = "devices:quality", Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Role = AutomationRole.Hyperlink, Focusable = true,
                Cursor = CursorId.Hand, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent, OnClick = () => { openQuality(); close(); },
                Children = [note with { HoverColor = Tok.TextPrimary }],
            }
            : note with { Key = "devices:note" });

        if (props.VolumeCell is { } cell && props.SetVolume is { } set)
        {
            var label = new TextEl(Loc.Get(P.Volume))
            {
                Size = D.NoteSize, LineHeight = D.NoteLine, Color = Tok.TextSecondary, Case = lower, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                Width = D.VolumeLabelW, Shrink = 0f,
            };
            float length = MathF.Max(1f, w - D.Pad.Left - D.Pad.Right - D.VolumeLabelW - D.VolumeValueW - 2f * D.VolumeGap);
            into.Add(new BoxEl
            {
                Key = "devices:volume", Direction = 0, AlignItems = FlexAlign.Center, Gap = D.VolumeGap, Margin = new Edges4(0f, D.VolumeTop, 0f, 0f),
                Children =
                [
                    label,
                    VolumeSlider.Create(cell, set, new Slider.SliderOptions { IsThumbToolTipEnabled = false }, length, D.VolumeThickness),
                    new TextEl("")
                    {
                        Text = Prop.Of(() => FormatCache.Int((int)MathF.Round(cell.Value * 100f))), Size = D.NoteSize, LineHeight = D.NoteLine,
                        Color = Tok.TextPrimary, Width = D.VolumeValueW, TextAlign = TextAlign.End, MaxLines = 1,
                    },
                ],
            });
        }
        if (card.PlayHere is { } playHere)
        {
            var parts = new TemplateParts();
            parts.Set<TextEl>(Button.PartLabel, t => ZuneText.Cased(t, LabelKind.Command));
            into.Add(new BoxEl
            {
                Direction = 0, Margin = new Edges4(0f, D.ActionTop, 0f, 0f),
                Children =
                [
                    Button.Create(Loc.Get(P.PlayOnThisPc), () => { playHere(); close(); }, ButtonAppearance.Accent, parts: parts) with
                    {
                        Key = "devices:play-here", Height = MathF.Max(D.ActionH, target), Shrink = 0f,
                    },
                ],
            });
        }
    }

    /// <summary>A section: its light title, then its words, or one line saying nothing else is nearby.</summary>
    static void Section(List<Element> into, string id, string title, IReadOnlyList<DevicePickerEntry> rows, Action close, string? match, TextCase lower,
        bool nothingNearby)
    {
        if (rows.Count == 0 && !nothingNearby) return;
        into.Add(new TextEl(title)
        {
            Key = "devices:section:" + id, Size = D.SectionSize, LineHeight = D.SectionLine, Weight = D.SectionWeight, Color = Tok.TextPrimary, Case = lower,
            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Margin = new Edges4(0f, D.SectionTop, 0f, D.SectionGap),
        });
        if (rows.Count == 0)
        {
            into.Add(new TextEl(P.Joined(Loc.Get(P.NothingNearby), Loc.Get(P.NothingNearbyHint)))
            {
                Key = "devices:nothing-nearby", Size = D.NoteSize, LineHeight = D.NoteLine, Color = Tok.TextSecondary, Case = lower, Wrap = TextWrap.Wrap, MinWidth = 0f,
            });
            return;
        }
        for (int i = 0; i < rows.Count; i++) into.Add(Row(rows[i], close, match, lower));
    }

    /// <summary>A device as a word with its note at the end; the one in use in the accent.</summary>
    static BoxEl Row(DevicePickerEntry e, Action close, string? match, TextCase lower)
    {
        bool on = e.IsChecked;
        var invoke = e.Invoke;
        bool enabled = e.Enabled && invoke is not null;
        string? note = e.Accelerator ?? (e.Word && e.Kind == DevicePickerEntryKind.Command ? match : e.Detail);
        var kids = new List<Element>(2)
        {
            new TextEl(e.Label)
            {
                Size = D.RowNameSize, LineHeight = D.RowNameLine, Weight = on ? D.RowOnWeight : D.RowWeight, Case = lower,
                Color = !enabled ? Tok.TextDisabled : on ? Tok.AccentTextPrimary : Tok.TextPrimary, HoverColor = Tok.AccentTextPrimary,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Grow = 1f, Shrink = 1f, MinWidth = 0f,
            },
        };
        if (note is { Length: > 0 })
            kids.Add(new TextEl(note)
            {
                Size = D.RowNoteSize, LineHeight = D.RowNoteLine, Color = Tok.TextSecondary, Case = lower, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
            });
        return new BoxEl
        {
            Key = "devices:row:" + e.Id, Direction = 0, AlignItems = FlexAlign.Center, Gap = D.RowGap, MinHeight = D.RowMinH,
            HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
            Role = e.Kind == DevicePickerEntryKind.Radio ? AutomationRole.RadioButton : AutomationRole.Button,
            Focusable = enabled, IsEnabled = enabled, Cursor = enabled ? CursorId.Hand : null,
            OnClick = enabled ? () => { invoke!(); close(); } : null,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The footer line: the switches at rest (or who sets the quality while another device plays) and "sound settings".</summary>
    static BoxEl Footer(DevicePickerFooter f, Action close, float target, TextCase lower)
    {
        var openSound = f.OpenSound;
        return new BoxEl
        {
            Key = "devices:footer", Direction = 0, AlignItems = FlexAlign.Center, Gap = D.FooterGap, Margin = new Edges4(0f, D.FooterTop, 0f, 0f),
            Children =
            [
                new TextEl(f.Quality is null ? f.QualitySetBy ?? "" : f.Sound)
                {
                    Size = D.FooterSize, LineHeight = D.FooterLine, Color = Tok.TextSecondary, Case = lower, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
                new BoxEl
                {
                    Key = "devices:sound", Shrink = 0f, MinHeight = target, AlignItems = FlexAlign.Center, Role = AutomationRole.Hyperlink, Focusable = true,
                    Cursor = CursorId.Hand, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent, OnClick = () => { openSound(); close(); },
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Player.SoundSettings))
                        {
                            Size = D.FooterSize, LineHeight = D.FooterLine, Color = Tok.AccentTextPrimary, HoverColor = Tok.TextPrimary, Case = lower, MaxLines = 1,
                        },
                    ],
                },
            ],
        };
    }
}
