using System;
using System.Globalization;
using System.IO;
using System.Text;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Backend.Audio;
using Wavee.SpotifyLive.Audio;
using Wavee.SpotifyLive.Audio.Runtime;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

sealed partial class SettingsPage
{
    static readonly string[] s_eqPresetIds = ["flat", "bass", "treble", "vocal", "radio", "proof"];
    static readonly float[][] s_eqPresetGains =
    [
        [0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        [6, 5, 4, 2, 0, 0, 0, 0, 0, 0],
        [0, 0, 0, 0, 0, 1, 2, 3, 4, 5],
        [-2, -1, 0, 2, 4, 4, 2, 0, -1, -2],
        [0, 2, -2, 0, 0, 2, 4, 2, 2, 2],
        [12, -12, 12, -12, 12, -12, 12, -12, 12, -12],
    ];

    readonly Signal<int> _quality = new(2);
    readonly Signal<int> _videoQuality = new(0);
    readonly Signal<int> _meteredVideoQuality = new(1);
    static readonly int[] s_videoQualityHeights = [0, 180, 240, 320, 480, 720, 1080];
    static readonly int[] s_meteredVideoHeights = [0, 480, 720, 1080];
    readonly Signal<int> _eqPreset = new(0);
    readonly Signal<double> _crossSecs = new(5.0);
    // The unified Slider.Create takes a FloatSignal; mirror _crossSecs (a Signal<double>, shared with the NumberBox)
    // into it at both write sites so the thumb tracks settings-load AND NumberBox edits (signal-bound, no re-render).
    readonly FloatSignal _crossSlider = new(5f);

    static string[] EqPresetLabels() =>
    [
        Loc.Get(Strings.Settings.Sound.Presets.Flat),
        Loc.Get(Strings.Settings.Sound.Presets.Bass),
        Loc.Get(Strings.Settings.Sound.Presets.Treble),
        Loc.Get(Strings.Settings.Sound.Presets.Vocal),
        Loc.Get(Strings.Settings.Sound.Presets.Radio),
        Loc.Get(Strings.Settings.Sound.Presets.Proof),
    ];

    static string[] EqPresetDescriptions() =>
    [
        Loc.Get(Strings.Settings.Sound.Presets.FlatSub),
        Loc.Get(Strings.Settings.Sound.Presets.BassSub),
        Loc.Get(Strings.Settings.Sound.Presets.TrebleSub),
        Loc.Get(Strings.Settings.Sound.Presets.VocalSub),
        Loc.Get(Strings.Settings.Sound.Presets.RadioSub),
        Loc.Get(Strings.Settings.Sound.Presets.ProofSub),
    ];

    Element PlaybackTab(Services? svc)
    {
        var settings = svc?.Settings;
        bool eqOn = settings?.Get(WaveeSettings.EqualizerEnabled) ?? false;
        bool crossOn = settings?.Get(WaveeSettings.CrossfadeEnabled) ?? false;
        float[] gains = PlaybackDsp.ReadEqGains(settings);
        int preset = Math.Clamp(_eqPreset.Value, 0, s_eqPresetIds.Length - 1);

        Element Toggle(SettingKey<bool> key, bool pushDsp = false, bool bumpPlayerBar = false, bool bumpPlayback = false) =>
            ToggleSwitch.Create(new Signal<bool>(settings?.Get(key) ?? true), onChange: _ =>
            {
                if (settings is null) return;
                settings.Set(key, !settings.Get(key));
                if (pushDsp) PlaybackDsp.Push(svc);
                if (bumpPlayerBar) PlayerBarPrefs.Bump();
                if (bumpPlayback) PlaybackPrefs.Bump();
                Bump();
            }, style: SettingsCard.CompactToggleStyle());

        return SettingsTabStack(
            RuntimeCard(svc),
            SettingsSectionHeader(Loc.Get(Strings.Settings.Playback.AudioTitle),
                SettingsGlyphs.Section(SettingsTab.Playback, "Audio")),
            SettingsRow(Loc.Get(Strings.Settings.Playback.AudioQuality), Loc.Get(Strings.Settings.Playback.AudioSub),
                QualityCombo(svc), SettingsGlyphs.Row(SettingsTab.Playback, "audioQuality")),
            SettingsRow(Loc.Get(Strings.Settings.Playback.MeteredQuality), MeteredQualitySub(),
                MeteredQualityCombo(svc), SettingsGlyphs.Row(SettingsTab.Playback, "meteredQuality")),
            SettingsRow(Loc.Get(Strings.Settings.Playback.RememberVolume), Loc.Get(Strings.Settings.Playback.RememberVolumeSub),
                Toggle(WaveeSettings.RememberVolume), SettingsGlyphs.Row(SettingsTab.Playback, "rememberVolume")),
            SettingsRow(Loc.Get(Strings.Settings.Playback.Autoplay), Loc.Get(Strings.Settings.Playback.AutoplaySub),
                Toggle(WaveeSettings.AutoplayEnabled, bumpPlayback: true), SettingsGlyphs.Row(SettingsTab.Playback, "autoplay")),
            SettingsSectionHeader(Loc.Get(Strings.Settings.Sound.Title), SettingsGlyphs.Section(SettingsTab.Playback, "Sound")),
            EqualizerGroup(svc, settings, eqOn, gains, preset),
            CrossfadeGroup(svc, settings, crossOn),
            SettingsSectionHeader(Loc.Get(Strings.VideoOverride.SettingsTitle), SettingsGlyphs.Section(SettingsTab.Playback, "Video")),
            SettingsRow(Loc.Get(Strings.Settings.Playback.VideoQuality), Loc.Get(Strings.Settings.Playback.VideoQualitySub),
                VideoQualityCombo(svc), SettingsGlyphs.Row(SettingsTab.Playback, "videoQuality")),
            SettingsRow(Loc.Get(Strings.Settings.Playback.VideoMeteredQuality), Loc.Get(Strings.Settings.Playback.VideoMeteredQualitySub),
                MeteredVideoQualityCombo(svc), SettingsGlyphs.Row(SettingsTab.Playback, "videoMetered")),
            VideoOverridesGroup(svc),
            SettingsSectionHeader(Loc.Get(Strings.Settings.Playback.PlayerBar), SettingsGlyphs.Section(SettingsTab.Playback, "Player bar")),
            SettingsRow(Loc.Get(Strings.Settings.Playback.ShowRemaining), Loc.Get(Strings.Settings.Playback.ShowRemainingSub),
                Toggle(WaveeSettings.PlayerBarShowRemaining, bumpPlayerBar: true), SettingsGlyphs.Row(SettingsTab.Playback, "playerBarRemaining")));
    }

    Element RuntimeCard(Services? svc)
    {
        var status = svc?.Playback.RuntimeStatus.Value ?? PlaybackRuntimeStatus.NotApplicable;

        void OpenSetup()
        {
            if (svc is { } s) s.Playback.OpenPlaybackRuntimeSetup.Value = s.Playback.OpenPlaybackRuntimeSetup.Peek() + 1;
        }

        if (status.IsReady)
        {
            return SettingsExpander.Create(new SettingsExpander.Options
            {
                Header = Loc.Get(Strings.Settings.Common.Ready),
                Description = Loc.Get(Strings.Settings.Playback.RuntimeReadySub),
                HeaderIcon = Icons.StatusSuccess,
                Content = Button.Standard(Loc.Get(Strings.Settings.Common.Manage), OpenSetup),
                InitiallyExpanded = false,
                Items =
                [
                    RuntimeInfoItem(Loc.Get(Strings.Playback.Runtime.DetailVersion),
                        status.PackId is { Length: > 0 } pack ? status.SpotifyVersion + " (" + pack + ")" : status.SpotifyVersion),
                    RuntimeInfoItem(Loc.Get(Strings.Playback.Runtime.DetailArch), status.Arch?.ToString()),
                    RuntimeSignatureItem(status),
                    RuntimeInfoItem(Loc.Get(Strings.Playback.Runtime.DetailLocation), status.RuntimePath),
                ],
            });
        }

        if (status.Outcome == ProvisioningOutcome.NeverAttempted)
        {
            return InfoBar.Create(InfoBarSeverity.Informational,
                Loc.Get(Strings.Settings.Playback.RuntimeNotSetUp),
                Loc.Get(Strings.Settings.Playback.RuntimeNotSetUpSub),
                isClosable: false,
                actionButton: Button.Accent(Loc.Get(Strings.Playback.Runtime.SetUp), OpenSetup));
        }

        // The typed outcome PLUS the provisioner's own detail line — the banner used to show one static sentence and
        // drop every reason the provisioner had already computed.
        string detail = status.Outcome.ToUserMessage(status.Detail);
        var nav = _nav;
        return InfoBar.Create(InfoBarSeverity.Error,
            Loc.Get(Strings.Settings.Common.Problem), detail,
            isClosable: false,
            actionButton: new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                Children =
                [
                    HyperlinkButton.Create(Loc.Get(Strings.Playback.Runtime.ViewDiagnostics),
                        () => nav?.Invoke(PlaybackRuntimeDiagnosticsPage.Route, null)),
                    Button.Accent(Loc.Get(Strings.Settings.Common.RetrySetup), OpenSetup),
                ],
            });
    }

    Element EqualizerGroup(Services? svc, IAppSettings? settings, bool eqOn, float[] gains, int preset)
    {
        var toggle = ToggleSwitch.Create(new Signal<bool>(eqOn), onChange: _ =>
        {
            if (settings is null) return;
            settings.Set(WaveeSettings.EqualizerEnabled, !settings.Get(WaveeSettings.EqualizerEnabled));
            PlaybackDsp.Push(svc);
            Bump();
        }, style: SettingsCard.CompactToggleStyle());

        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Loc.Get(Strings.Settings.Sound.Equalizer),
            Description = Loc.Get(Strings.Settings.Sound.EqualizerSub),
            HeaderIcon = SettingsGlyphs.Row(SettingsTab.Playback, "equalizer"),
            Content = toggle,
            InitiallyExpanded = eqOn,
            Items =
            [
                SettingsExpander.Item(Loc.Get(Strings.Settings.Sound.Preset), EqPresetDescriptions()[preset],
                    ComboBox.Create(EqPresetLabels(), _eqPreset, width: 200f, itemDescriptions: EqPresetDescriptions(),
                        isEnabled: eqOn && settings is not null,
                        onChange: i => ApplyEqPreset(svc, settings, i))),
                SettingsExpander.Item(Loc.Get(Strings.Settings.Sound.Curve),
                    eqOn ? Loc.Get(Strings.Settings.Sound.CurveOn) : Loc.Get(Strings.Settings.Sound.CurveOff),
                    new BoxEl
                    {
                        Direction = 1, Gap = Spacing.S,
                        Children =
                        [
                            WaveeEqualizerCurve.Create(gains, (band, gain) => SetEqBand(svc, settings, band, gain), eqOn && settings is not null),
                            new BoxEl
                            {
                                Direction = 0, Justify = FlexJustify.End,
                                Children =
                                [
                                    HyperlinkButton.Create(Loc.Get(Strings.Settings.Sound.ResetCurve),
                                        () => ResetEqCurve(svc, settings), isEnabled: eqOn && settings is not null),
                                ],
                            },
                        ],
                    },
                    alignment: SettingsCard.ContentAlignment.Vertical),
            ],
        });
    }

    Element CrossfadeGroup(Services? svc, IAppSettings? settings, bool crossOn)
    {
        var toggle = ToggleSwitch.Create(new Signal<bool>(crossOn), onChange: _ =>
        {
            if (settings is null) return;
            settings.Set(WaveeSettings.CrossfadeEnabled, !settings.Get(WaveeSettings.CrossfadeEnabled));
            PlaybackDsp.Push(svc);
            Bump();
        }, style: SettingsCard.CompactToggleStyle());

        void Commit(double seconds)
        {
            if (settings is null) return;
            int ms = (int)MathF.Round((float)Math.Clamp(seconds, 0, 12) * 1000f);
            settings.Set(WaveeSettings.CrossfadeMs, ms);
            _crossSecs.Value = ms / 1000.0;
            _crossSlider.Value = (float)(ms / 1000.0);
            PlaybackDsp.Push(svc);
            Bump();
        }

        var durationRow = new BoxEl
        {
            // NumberBox is a mounted component whose constructor fields intentionally freeze. Remount this small row when
            // the toggle flips so its enabled state follows crossfade immediately instead of staying at its first value.
            Key = crossOn ? "crossfade-duration-on" : "crossfade-duration-off",
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Children =
            [
                Slider.Create(_crossSlider, v => Commit(v),
                    new Slider.SliderOptions
                    {
                        Min = 0f, Max = 12f, Step = 0.5f, TickFrequency = 2f, IsThumbToolTipEnabled = true,
                        ThumbToolTipValueConverter = v => v.ToString("0.#", CultureInfo.InvariantCulture) + " s",
                    },
                    length: 220f, isEnabled: crossOn && settings is not null),
                NumberBox.Create(value: _crossSecs,
                    options: new NumberBox.NumberBoxOptions
                    {
                        Minimum = 0, Maximum = 12, SmallChange = 0.5,
                        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 96f,
                        Formatter = v => v.ToString("0.#", CultureInfo.InvariantCulture) + " s",
                        IsEnabled = crossOn && settings is not null,
                    },
                    onChange: v => Commit(v)),
            ],
        };

        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Loc.Get(Strings.Settings.Sound.Crossfade),
            Description = Loc.Get(Strings.Settings.Sound.CrossfadeSub),
            HeaderIcon = SettingsGlyphs.Row(SettingsTab.Playback, "crossfade"),
            Content = toggle,
            InitiallyExpanded = crossOn,
            Items =
            [
                SettingsExpander.Item(Loc.Get(Strings.Settings.Sound.CrossfadeDuration),
                    Strings.Settings.Sound.Seconds(((float)_crossSecs.Value).ToString("0.#", CultureInfo.InvariantCulture)),
                    durationRow),
            ],
        });
    }

    static Element RuntimeInfoItem(string label, string? value) => SettingsItem(label,
        string.IsNullOrWhiteSpace(value) ? null : value,
        isEnabled: !string.IsNullOrWhiteSpace(value));

    static Element RuntimeSignatureItem(PlaybackRuntimeStatus status)
    {
        string summary = SetupBody.SignatureSummary(status);
        string? dllPath = status.SignatureInfo?.FilePath;
        bool canView = OperatingSystem.IsWindows()
                    && !string.IsNullOrWhiteSpace(dllPath)
                    && File.Exists(dllPath);

        Element? link = canView
            ? HyperlinkButton.Create(Loc.Get(Strings.Playback.Runtime.ViewSignature),
                () =>
                {
                    if (!PeAndSignature.TryShowNativeSignatureDialog(dllPath!, FluentApp.WindowHandle))
                        Toast.Show(Loc.Get(Strings.Playback.Runtime.ViewSignatureFailed), new ToastOptions { Severity = InfoBarSeverity.Warning });
                })
            : null;

        return SettingsExpander.Item(
            Loc.Get(Strings.Playback.Runtime.DetailSignature),
            string.IsNullOrWhiteSpace(summary) ? null : summary,
            link,
            alignment: SettingsCard.ContentAlignment.Right);
    }

    Element QualityCombo(Services? svc)
    {
        var settings = svc?.Settings;
        // THREE rungs, not four. Lossless (AudioQualityPreference.Lossless = 3) still exists in the enum because the
        // stored int is persisted, but it is not OFFERED: a permanently-disabled fourth row labelled "Coming soon" is
        // an advert, and the picker is the wrong place to make a promise. Re-offering it later is one entry per array.
        string[] labels =
        [
            Loc.Get(Strings.Settings.Playback.QualityNormal),
            Loc.Get(Strings.Settings.Playback.QualityHigh),
            Loc.Get(Strings.Settings.Playback.QualityVeryHigh),
        ];
        string[] descriptions =
        [
            Loc.Get(Strings.Settings.Playback.QualityNormalSub),
            Loc.Get(Strings.Settings.Playback.QualityHighSub),
            Loc.Get(Strings.Settings.Playback.QualityVeryHighSub),
        ];
        return ComboBox.Create(labels, _quality, width: 280f,
            itemDescriptions: descriptions,
            isEnabled: settings is not null,
            onChange: i =>
            {
                if (settings is null || i < 0 || i > 2) return;
                settings.Set(WaveeSettings.PlaybackQuality, i);
                Bump();
            });
    }

    // The card's description IS the detector's verdict — every state says something (a silent card once hid a detector
    // that only spoke when metered). Reactive: the cost, cap and quality signals are read inside render, so an NLM
    // CostChanged push or either combo re-renders the line. "Cap in effect" is only claimed when the cap is actually
    // lower than the chosen quality.
    string MeteredQualitySub()
    {
        bool capInEffect = NetworkPolicy.MeteredQualityCap.Value < _quality.Value;
        return MeteredStatusLine.For(NetworkPolicy.Cost.Value, capInEffect).Render(Loc.Get);
    }

    Element MeteredQualityCombo(Services? svc)
    {
        var settings = svc?.Settings;
        string[] labels =
        [
            Loc.Get(Strings.Settings.Playback.QualityNormal),
            Loc.Get(Strings.Settings.Playback.QualityHigh),
            Loc.Get(Strings.Settings.Playback.QualityVeryHigh),
        ];
        string[] descriptions =
        [
            Loc.Get(Strings.Settings.Playback.QualityNormalSub),
            Loc.Get(Strings.Settings.Playback.QualityHighSub),
            Loc.Get(Strings.Settings.Playback.QualityVeryHighSub),
        ];
        return ComboBox.Create(labels, NetworkPolicy.MeteredQualityCap, width: 280f,
            itemDescriptions: descriptions,
            isEnabled: settings is not null,
            onChange: i =>
            {
                if (settings is null || i < 0 || i > 2) return;
                settings.Set(WaveeSettings.MeteredQualityCap, i);
                Bump();
            });
    }

    Element VideoQualityCombo(Services? svc)
    {
        var settings = svc?.Settings;
        string[] labels =
        [
            Loc.Get(Strings.Settings.Playback.VideoQualityAuto), "180p", "240p", "320p", "480p", "720p", "1080p",
        ];
        return ComboBox.Create(labels, _videoQuality, width: 280f,
            isEnabled: settings is not null,
            onChange: i =>
            {
                if (settings is null || (uint)i >= (uint)s_videoQualityHeights.Length) return;
                int height = s_videoQualityHeights[i];
                settings.Set(WaveeSettings.VideoQuality, height);
                svc?.LiveHost?.Connect.SetVideoQualityPreference(height);
                Bump();
            });
    }

    Element MeteredVideoQualityCombo(Services? svc)
    {
        var settings = svc?.Settings;
        string[] labels = [Loc.Get(Strings.Settings.Playback.VideoQualityUnlimited), "480p", "720p", "1080p"];
        return ComboBox.Create(labels, _meteredVideoQuality, width: 280f,
            isEnabled: settings is not null,
            onChange: i =>
            {
                if (settings is null || (uint)i >= (uint)s_meteredVideoHeights.Length) return;
                int height = s_meteredVideoHeights[i];
                settings.Set(WaveeSettings.VideoMeteredMaxHeight, height);
                NetworkPolicy.MeteredVideoMaxHeight.Value = height;
                svc?.LiveHost?.Connect.RefreshVideoQualityPolicy();
                Bump();
            });
    }

    static int VideoQualityIndex(int height)
    {
        for (int i = 0; i < s_videoQualityHeights.Length; i++)
            if (s_videoQualityHeights[i] == height) return i;
        return 0;
    }

    static int MeteredVideoQualityIndex(int height)
    {
        for (int i = 0; i < s_meteredVideoHeights.Length; i++)
            if (s_meteredVideoHeights[i] == height) return i;
        return 1;
    }


    static string SerializeEqGains(float[] gains)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 10; i++)
        {
            if (i > 0) sb.Append(',');
            float gain = i < gains.Length ? Math.Clamp(gains[i], -12f, 12f) : 0f;
            sb.Append(gain.ToString("0.#", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    static int EqPresetIndex(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        for (int i = 0; i < s_eqPresetIds.Length; i++)
            if (string.Equals(name, s_eqPresetIds[i], StringComparison.OrdinalIgnoreCase))
                return i;
        for (int i = 0; i < EqPresetLabels().Length; i++)
            if (string.Equals(name, EqPresetLabels()[i], StringComparison.OrdinalIgnoreCase))
                return i;
        return 0;
    }

    void ApplyEqPreset(Services? svc, IAppSettings? settings, int index)
    {
        if (settings is null) return;
        int idx = Math.Clamp(index, 0, s_eqPresetIds.Length - 1);
        _eqPreset.Value = idx;
        settings.Set(WaveeSettings.EqualizerPreset, s_eqPresetIds[idx]);
        settings.Set(WaveeSettings.EqualizerGains, SerializeEqGains(s_eqPresetGains[idx]));
        PlaybackDsp.Push(svc);
        Bump();
    }

    void ResetEqCurve(Services? svc, IAppSettings? settings)
    {
        if (settings is null) return;
        settings.Set(WaveeSettings.EqualizerGains, SerializeEqGains(s_eqPresetGains[0]));
        _eqPreset.Value = 0;
        settings.Set(WaveeSettings.EqualizerPreset, s_eqPresetIds[0]);
        PlaybackDsp.Push(svc);
        Bump();
    }

    void SetEqBand(Services? svc, IAppSettings? settings, int band, float gain)
    {
        if (settings is null || band < 0 || band >= 10) return;
        var gains = PlaybackDsp.ReadEqGains(settings);
        gains[band] = Math.Clamp(gain, -12f, 12f);
        settings.Set(WaveeSettings.EqualizerGains, SerializeEqGains(gains));
        PlaybackDsp.Push(svc);
        Bump();
    }

}
