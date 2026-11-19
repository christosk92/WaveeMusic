// ── Themes/WinUi/Structure/Views/Screens/WinUiSettings.Body.Sound.cs — Sound's WinUI cards: quality, normalization level, the equalizer, the output ────────
//
// Quality and the metered cap carry each rung's sentence in their list and offer Lossless only with a key deriver (D7: a permanently dead rung is an advert;
// the combo's items freeze at mount, so the key carries the rung count). The metered cap is bound STRAIGHT to the live cap signal (33b) and its description
// is the detector's verdict (a live sentence, never blank). The equalizer is ONE expander: its switch in the header, the preset (its sentence under it) and
// the curve, bound to the live DSP (the verbs persist and push the whole state while the curve is dragged). The output device lists "System default" and
// the endpoints (the dial's live words).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

internal static partial class WinUiSettings
{
    static readonly Func<string, string> s_locGet = Loc.Get;

    // ── quality ──────────────────────────────────────────────────────────────────────────────────────────────────

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

    /// <summary>The streaming quality, or the metered cap (bound to the live cap signal, its description the detector's verdict).</summary>
    static Element QualityCard(RowCtx x, bool metered)
    {
        var row = x.Row;
        if (!metered) return QualityCard(row, BaseCard(x, null));

        var dial = row.Dial;
        int count = Settings.Quality.MeteredRungCount(SettingsFacts.Live.CanDerive);
        string[] labels = QualityLabels(count);
        string[] descriptions = QualityDescriptions(count);
        Element combo = Combo(labels, Platform.Network.MeteredQualityCap, width: WinUiSettingsRules.WideComboWidth, itemDescriptions: descriptions, onChange: i =>
        {
            if (dial.Writer.Choose is { } choose && choose(i)) Settings.Bump();   // persist, then publish
        }) with { Key = count == Settings.Quality.AudioRungCount(true) ? "playback.meteredCap:lossless" : "playback.meteredCap" };
        return DialCard(BaseCard(x, combo) with { Description = MeteredStatus() });
    }

    /// <summary>The streaming quality card over <paramref name="card"/> (the page's base card, or a card outside the page: <see cref="QualityRow"/>).</summary>
    static Element QualityCard(SettingsBodyRow row, Card card)
    {
        int count = Settings.Quality.AudioRungCount(SettingsFacts.Live.CanDerive);
        return DialCard(card with { Control = DialCombo(row, QualityLabels(count), WinUiSettingsRules.WideComboWidth, QualityDescriptions(count)) });
    }

    /// <summary>Reactive: the live cost, the live cap and the chosen quality are read here, so an NLM push or either combo re-words the line. "Cap in effect" is
    /// claimed only when the cap is actually below the chosen quality.</summary>
    static string MeteredStatus()
    {
        int quality = Dial("sound.quality").Current is int q ? q : 0;
        bool capInEffect = Platform.Network.MeteredQualityCap.Value < quality;
        return Settings.MeteredStatusLine.For(Platform.Network.Cost.Value, capInEffect).Render(s_locGet);
    }

    /// <summary>The normalization level: Quiet · Normal · Loud, each with its sentence in the list.</summary>
    static Element NormalizationModeCard(RowCtx x)
    {
        var row = x.Row;
        string[] labels = [Loc.Get(Strings.Settings.Playback.NormQuiet), Loc.Get(Strings.Settings.Playback.NormNormal), Loc.Get(Strings.Settings.Playback.NormLoud)];
        string[] descriptions = [Loc.Get(Strings.Settings.Playback.NormQuietSub), Loc.Get(Strings.Settings.Playback.NormNormalSub), Loc.Get(Strings.Settings.Playback.NormLoudSub)];
        return DialCard(BaseCard(x, DialCombo(row, labels, WinUiSettingsRules.NarrowComboWidth, descriptions)));
    }

    // ── the equalizer (W7, W8, N10) ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The parsed gain vector, re-parsed only when the stored string moves — a stable array keeps the curve's props record equal across an unrelated
    /// page re-render, so its nodes are not rebuilt.</summary>
    static string? s_eqGainsRaw;
    static float[] s_eqGains = new float[Settings.Eq.BandCount];
    static readonly Action<int, float> s_onEqBand = static (band, gain) =>
    {
        if (SettingsVerbs.SetEqBand(band, gain)) Settings.Bump();
    };

    static string[] EqPresetLabels() =>
    [
        Loc.Get(Strings.Settings.Sound.Presets.Flat), Loc.Get(Strings.Settings.Sound.Presets.Bass), Loc.Get(Strings.Settings.Sound.Presets.Treble),
        Loc.Get(Strings.Settings.Sound.Presets.Vocal), Loc.Get(Strings.Settings.Sound.Presets.Radio), Loc.Get(Strings.Settings.Sound.Presets.Proof),
    ];

    static string[] EqPresetDescriptions() =>
    [
        Loc.Get(Strings.Settings.Sound.Presets.FlatSub), Loc.Get(Strings.Settings.Sound.Presets.BassSub), Loc.Get(Strings.Settings.Sound.Presets.TrebleSub),
        Loc.Get(Strings.Settings.Sound.Presets.VocalSub), Loc.Get(Strings.Settings.Sound.Presets.RadioSub), Loc.Get(Strings.Settings.Sound.Presets.ProofSub),
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

    /// <summary>The equalizer's expander: its switch in the header; the preset (the selected preset's sentence as its description); the curve, Vertical at every
    /// width (the full lane of the card), with "Reset curve to flat" under it. The curve's column claims the lane with Grow 1 / Basis 0 / MinWidth 0 (without
    /// them the chart measures its own guess back and overruns the card).</summary>
    static Element EqualizerCard(RowCtx x)
    {
        var row = x.Row;
        bool eqOn = row.Value is true;
        var presetDial = Child(x, "sound.equalizer.preset")?.Dial;
        int preset = Math.Clamp(presetDial?.Current is int p ? p : 0, 0, Settings.Eq.PresetIds.Length - 1);
        string[] descriptions = EqPresetDescriptions();
        var presetRow = Child(x, "sound.equalizer.preset");
        Element presetItem = presetRow is null ? new BoxEl() : DialCard(ItemCard(presetRow.Label, descriptions[preset],
            DialCombo(presetRow, EqPresetLabels(), WinUiSettingsRules.NarrowComboWidth, descriptions), presetRow.Enabled));
        var curveRow = Child(x, "sound.equalizer.curve");
        var resetRow = Child(x, "sound.equalizer.reset");
        Element reset = new BoxEl
        {
            Direction = 0, Justify = FlexJustify.End, AlignSelf = FlexAlign.Stretch,
            Children = [LinkButton(resetRow?.Label ?? Loc.Get(Strings.Settings.Sound.ResetCurve), () => { if (resetRow is not null) Press(resetRow.Dial); }, isEnabled: eqOn)],
        };
        Element curve = new BoxEl
        {
            Direction = 1, Gap = Spacing.S, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
            Children = [Controls.EqualizerCurve(EqGains(), s_onEqBand, eqOn), HostedItem(x, "sound.equalizer.reset", reset)],
        };
        Element curveItem = DialCard(ItemCard(curveRow?.Label ?? Loc.Get(Strings.Settings.Sound.Curve),
            Loc.Get(eqOn ? Strings.Settings.Sound.CurveOn : Strings.Settings.Sound.CurveOff), curve) with { Form = CardForm.Vertical });
        var items = new List<Element>(2);
        if (presetRow is not null) items.Add(ChildHost(x, presetRow, presetItem));
        if (curveRow is not null) items.Add(ChildHost(x, curveRow, curveItem));
        else items.Add(curveItem);
        return Expander(BaseCard(x, DialToggle(row.Dial, row.Enabled)), items, x.Expanded) with { Key = "playback.equalizer" };
    }

    // ── the output ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The output device: "System default", then the endpoints in roster order (the dial's live words; a plugged headset re-reads the page).</summary>
    static Element OutputDeviceCard(RowCtx x) => DialCard(BaseCard(x, DialCombo(x.Row, x.Row.Choices, WinUiSettingsRules.WideComboWidth)));
}
