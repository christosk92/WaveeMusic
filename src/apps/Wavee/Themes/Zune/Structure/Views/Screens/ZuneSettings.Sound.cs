// ── Themes/Zune/Structure/Views/Screens/ZuneSettings.Sound.cs — Sound's sections: quality, the equalizer, the output device (settings rework phase 4) ────
//
// Board Z-Settings-Playback (quality with the lossless rung, the metered cap with its live status line, the equalizer and its curve). The quality combos carry
// each rung's line (the picked one's under the label, the chips having no per-item tip); the metered cap's description IS the detector's verdict (a live
// sentence). The equalizer's curve is the draggable ten-band curve over the row's full width with "reset curve to flat" under it; every band write persists and
// pushes the whole state to the live graph (`SettingsVerbs.SetEqBand`), so the ear follows the curve while it is dragged. The output device is the device picker's
// this-computer list (the dial's live words: the system default, then the endpoints), written through the picker's own door.

using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Z = Wavee.Themes.Zune.ZuneSettingsRules;

namespace Wavee.Themes.Zune;

internal static partial class ZuneSettings
{
    static readonly Func<string, string> s_locGet = Loc.Get;

    /// <summary>The parsed gain vector, re-parsed only when the stored string moves: a stable array keeps the curve's props record equal across an unrelated re-render.</summary>
    static string? s_eqGainsRaw;
    static float[] s_eqGains = new float[Settings.Eq.BandCount];
    static readonly Action<int, float> s_onEqBand = static (band, gain) => { if (SettingsVerbs.SetEqBand(band, gain)) Settings.Bump(); };

    /// <summary>The quality (or the metered cap): a wide combo whose items carry their rung's line; the quality's hint adds the picked rung's line, the cap's is
    /// the detector's live verdict.</summary>
    static Element QualityRow(Paint d, SettingsBodyRow row, bool metered)
    {
        int rungs = metered ? Settings.Quality.MeteredRungCount(d.Facts.CanDerive) : Settings.Quality.AudioRungCount(d.Facts.CanDerive);
        string[] lines = QualityDescriptions(rungs);
        SettingsDial dial = row.Dial;
        var writer = row.Writer;
        var choice = new ZuneChoice(QualityLabels(rungs), metered ? static () => Platform.Network.MeteredQualityCap.Value : () => dial.Current is int at ? at : 0,
            i => { if (writer.Choose?.Invoke(i) == true) Settings.Bump(); }, Chips: false, Descriptions: lines, Width: Z.WideComboWidth);
        return DialRow(d, row, d.K.Choice(row.Id, choice), metered ? MeteredStatus() : QualityHint(row, lines));
    }

    static string[] QualityLabels(int count)
    {
        var labels = new string[count];
        for (int i = 0; i < count; i++)
            labels[i] = Loc.Get(i switch
            {
                0 => Strings.Settings.Playback.QualityNormal,
                1 => Strings.Settings.Playback.QualityHigh,
                2 => Strings.Settings.Playback.QualityVeryHigh,
                _ => Strings.Settings.Playback.QualityLossless,
            });
        return labels;
    }

    /// <summary>The row's own hint, then the picked rung's line: the chips carry no per-item tooltip, so the picked one says what it is here.</summary>
    static string QualityHint(SettingsBodyRow row, string[] lines)
    {
        string own = row.Description ?? "";
        if (lines.Length == 0) return own;
        string picked = lines[Math.Clamp(row.Value is int at ? at : 0, 0, lines.Length - 1)];
        return own.Length == 0 ? picked : own + SummarySeparator + picked;
    }

    static string[] QualityDescriptions(int count)
    {
        var lines = new string[count];
        for (int i = 0; i < count; i++)
            lines[i] = Loc.Get(i switch
            {
                0 => Strings.Settings.Playback.QualityNormalSub,
                1 => Strings.Settings.Playback.QualityHighSub,
                2 => Strings.Settings.Playback.QualityVeryHighSub,
                _ => Strings.Settings.Playback.QualityLosslessSub,
            });
        return lines;
    }

    /// <summary>The live cost, the live cap and the chosen quality are all read here, so a network push or either combo re-words the line. "Cap in effect" is claimed
    /// only when the cap is actually below the chosen quality.</summary>
    static string MeteredStatus()
    {
        bool capInEffect = Platform.Network.MeteredQualityCap.Value < (SettingsSpec.Dial(QualityDial).Current is int q ? q : 0);
        return Settings.MeteredStatusLine.For(Platform.Network.Cost.Value, capInEffect).Render(s_locGet);
    }

    const string QualityDial = "sound.quality";

    /// <summary>The equalizer's preset: a combo whose items carry their lines, the picked one's under the label; greyed (silently) while the equalizer is off.</summary>
    static Element PresetRow(Paint d, SettingsBodyRow row)
    {
        string[] descriptions = EqPresetDescriptions();
        int at = Math.Clamp(row.Value is int p ? p : 0, 0, descriptions.Length - 1);
        SettingsDial dial = row.Dial;
        var writer = row.Writer;
        var choice = new ZuneChoice(ChoiceLabels(row), () => dial.Current is int i ? i : 0, i => { if (writer.Choose?.Invoke(i) == true) Settings.Bump(); },
            Chips: false, Enabled: row.Enabled ? null : s_disabled, Descriptions: descriptions, Width: Z.ComboWidth);
        return DialRow(d, row, d.K.Choice(row.Id, choice), descriptions[at]);
    }

    static string[] EqPresetDescriptions() =>
    [
        Loc.Get(Strings.Settings.Sound.Presets.FlatSub),
        Loc.Get(Strings.Settings.Sound.Presets.BassSub),
        Loc.Get(Strings.Settings.Sound.Presets.TrebleSub),
        Loc.Get(Strings.Settings.Sound.Presets.VocalSub),
        Loc.Get(Strings.Settings.Sound.Presets.RadioSub),
        Loc.Get(Strings.Settings.Sound.Presets.ProofSub),
    ];

    static float[] EqGains()
    {
        string raw = SettingsFacts.EqualizerGainsRaw();
        if (!string.Equals(raw, s_eqGainsRaw, StringComparison.Ordinal))
        {
            s_eqGainsRaw = raw;
            s_eqGains = Settings.Eq.ParseGains(raw);   // a NEW array: the curve's props gate sees the change
        }
        return s_eqGains;
    }

    /// <summary>The curve row: its label and hint, the draggable ten-band curve over the row's full width, and "reset curve to flat" (the reset dial, keyed as
    /// its row) under it. The row stacks at every tier (the curve fills the lane it is given and measures it).</summary>
    static Element EqCurveRow(Paint d, SettingsBodyRow row)
    {
        bool on = row.Enabled;
        Element reset = SettingsSpec.TryDial(EqResetDial, out var resetDial)
            ? new BoxEl
            {
                Key = SettingsKeys.ZuneRow(EqResetDial), Direction = 0, Shrink = 0f, OnRealized = NoteRow(EqResetDial),
                Children = [d.K.Cmd(Loc.Get(resetDial.TitleKey), () => { if (resetDial.Writer.Run?.Invoke() == true) Settings.Bump(); }, on)],
            }
            : s_none;
        return new BoxEl
        {
            Key = SettingsKeys.ZuneRow(row.Id), Direction = 1, Gap = Z.RowStackGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, OnRealized = NoteRow(row.Id),
            Opacity = on ? 1f : Z.DisabledOpacity,
            Children =
            [
                LabelOf(d.Focus, row.Id, ZuneText.Label(row.Label)),
                ZuneText.Hint(row.Description ?? ""),
                new BoxEl
                {
                    Direction = 1, Gap = Z.ControlGap, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
                    Children =
                    [
                        Controls.EqualizerCurve(EqGains(), s_onEqBand, on),
                        new BoxEl { Direction = 0, Justify = FlexJustify.End, Children = [reset] },
                    ],
                },
            ],
        };
    }

    const string EqResetDial = "sound.equalizer.reset";

    /// <summary>The output device: the system default, then this computer's endpoints (the dial's live words, the device picker's list), always the combo (a list
    /// that grows when a headset is plugged in); a pick is the picker's own door (the dial's writer).</summary>
    static Element OutputDeviceRow(Paint d, SettingsBodyRow row)
    {
        SettingsDial dial = row.Dial;
        var writer = row.Writer;
        return DialRow(d, row, d.K.Choice(row.Id, new ZuneChoice(row.Choices, () => dial.Current is int at ? at : 0,
            i => { if (writer.Choose?.Invoke(i) == true) Settings.Bump(); }, Chips: false, Width: Z.WideComboWidth, Combo: true)), row.Description);
    }

}
