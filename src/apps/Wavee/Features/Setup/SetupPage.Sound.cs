using System;
using System.Globalization;
using System.IO;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Backend.Audio;
using static FluentGpu.Dsl.Ui;
using Wavee.Sdk.Streams;

namespace Wavee;

/// <summary>Page 6 · Sound &amp; storage (<c>data-step="6"</c>). Seven dense <see cref="SetupCompact.Row"/>s (no
/// section headers — the stage's three cards carry the "what does this actually do" narrative instead): streaming
/// quality, the metered cap, crossfade, the equalizer, the cache budget mode, the cache location, and the metadata
/// budget. The stage is a genuine live readout — streaming kbps/GB-per-hour, the EQ curve itself (with a "Reset curve"
/// link), and the cache's drive-share <c>ProgressBar</c> — built from <see cref="SetupSoundFacts"/>'s pure arithmetic,
/// never from a directory walk (<c>AudioBodyCache.Status()</c> is a scan; the stage's drive total comes from a
/// try/caught <see cref="DriveInfo"/> read instead).
///
/// <para>Every ComboBox/ToggleSwitch signal here is owned via <c>UseSignal</c> once and re-synced from settings by a
/// single post-render <c>UseEffect</c> keyed on <see cref="_epoch"/> — <see cref="ComboBox.Create"/> freezes its
/// <c>SelectedIndex</c> signal identity at mount, so a per-render <c>new Signal&lt;int&gt;(x)</c> (the page's old
/// shape) goes stale the moment settings change from elsewhere (e.g. the stage's "Reset curve" link rewriting the
/// preset). <see cref="SetupCompact.Segmented"/> reads its selection prop live every render by construction, so the
/// cache-budget mode needs no owned signal of its own.</para></summary>
sealed class SetupSoundPage : Component
{
    static readonly string[] s_metaBudgetLabels = ["32 MB", "64 MB", "128 MB", "256 MB"];
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

    readonly Signal<int> _epoch = new(0);
    void Bump() => _epoch.Value = _epoch.Peek() + 1;

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var settings = svc?.Settings;
        var post = UsePost();
        var viewport = UseContextSignal(Viewport.Size);
        _ = _epoch.Value;   // subscribes THIS render — a Bump() must recompute the stage's fresh-from-settings text too

        // ── every ComboBox/ToggleSwitch signal, owned ONCE (SetupLayout.md §0's signal-ownership rule) ────────────────
        var qualitySig = UseSignal(Math.Clamp(settings?.Get(WaveeSettings.PlaybackQuality) ?? 2, 0, 2));
        var meteredSig = UseSignal(Math.Clamp(settings?.Get(WaveeSettings.MeteredQualityCap) ?? 1, 0, 2));
        var crossfadeOnSig = UseSignal(settings?.Get(WaveeSettings.CrossfadeEnabled) ?? false);
        var crossfadeSecSig = UseSignal(SetupSoundFacts.SecondsIndex(
            Math.Clamp((settings?.Get(WaveeSettings.CrossfadeMs) ?? 5000) / 1000.0, 0, 12)));
        var eqOnSig = UseSignal(settings?.Get(WaveeSettings.EqualizerEnabled) ?? false);
        var eqPresetSig = UseSignal(EqPresetIndex(settings?.Get(WaveeSettings.EqualizerPreset)));
        var metaBudgetSig = UseSignal(SetupSoundFacts.MetaBudgetIndex(
            settings?.Get(WaveeSettings.MetadataCacheBudgetBytes) ?? SetupSoundFacts.MetaBudgetBytes[1]));

        UseEffect(() =>
        {
            _ = _epoch.Value;   // re-run this sync whenever a handler below bumps it
            if (settings is null) return;
            qualitySig.SetIfChanged(Math.Clamp(settings.Get(WaveeSettings.PlaybackQuality), 0, 2));
            meteredSig.SetIfChanged(Math.Clamp(settings.Get(WaveeSettings.MeteredQualityCap), 0, 2));
            crossfadeOnSig.SetIfChanged(settings.Get(WaveeSettings.CrossfadeEnabled));
            crossfadeSecSig.SetIfChanged(SetupSoundFacts.SecondsIndex(Math.Clamp(settings.Get(WaveeSettings.CrossfadeMs) / 1000.0, 0, 12)));
            eqOnSig.SetIfChanged(settings.Get(WaveeSettings.EqualizerEnabled));
            eqPresetSig.SetIfChanged(EqPresetIndex(settings.Get(WaveeSettings.EqualizerPreset)));
            metaBudgetSig.SetIfChanged(SetupSoundFacts.MetaBudgetIndex(settings.Get(WaveeSettings.MetadataCacheBudgetBytes)));
        });

        // ── fresh-from-settings reads for display/logic (correct the instant a handler writes + Bump()s; only the
        // controls above need an OWNED signal to avoid the ComboBox/ToggleSwitch mount-time freeze) ───────────────────
        int quality = Math.Clamp(settings?.Get(WaveeSettings.PlaybackQuality) ?? 2, 0, 2);
        int meteredCap = Math.Clamp(settings?.Get(WaveeSettings.MeteredQualityCap) ?? 1, 0, 2);
        bool crossfadeOn = settings?.Get(WaveeSettings.CrossfadeEnabled) ?? false;
        bool eqOn = settings?.Get(WaveeSettings.EqualizerEnabled) ?? false;
        float[] eqGains = PlaybackDsp.ReadEqGains(settings);
        int budgetMode = Math.Clamp(settings?.Get(WaveeSettings.AudioBodyCacheBudgetMode) ?? (int)AudioCacheBudgetMode.DriveShare, 0, 2);
        long fixedBudgetBytes = settings?.Get(WaveeSettings.AudioBodyCacheBudgetBytes) ?? (32L << 30);
        int storedPercent = settings?.Get(WaveeSettings.AudioBodyCacheBudgetPercent) ?? 0;
        string audioDir = svc?.AudioBodyCache?.CurrentDirectory
            ?? AudioBodyDiskCache.ResolveDirectory(settings?.Get(WaveeSettings.AudioBodyCacheBasePath));

        float plateW = SetupLayout.PlateWidth(viewport.Value.Width);
        var tierSig = UseSignal(SetupLayout.NominalTierFor(plateW));
        UseEffect(() =>
        {
            var current = tierSig.Peek();
            var next = SetupLayout.TierFor(plateW, current);
            if (next != current) tierSig.Value = next;
        }, plateW);
        bool wide = SetupLayout.ShowsHero(tierSig.Value);

        Element[] rows =
        [
            SetupCompact.Row(Loc.Get(Strings.Settings.Playback.AudioQuality), QualityCombo(settings, qualitySig)),
            SetupCompact.Row(Loc.Get(Strings.Settings.Playback.MeteredQuality), MeteredCombo(settings, meteredSig)),
            SetupCompact.Row(Loc.Get(Strings.Settings.Sound.Crossfade),
                CrossfadeControls(settings, svc, crossfadeOnSig, crossfadeSecSig, crossfadeOn)),
            SetupCompact.Row(Loc.Get(Strings.Settings.Sound.Equalizer),
                EqualizerControls(settings, svc, eqOnSig, eqPresetSig, eqOn)),
            SetupCompact.Row(Loc.Get(Strings.Settings.Storage.BodyBudget),
                BudgetSegmented(settings, svc, budgetMode)),
            SetupCompact.Row(Loc.Get(Strings.Settings.Storage.CacheLocation),
                CacheLocationButton(settings, svc, post),
                sub: SetupSoundFacts.ShortPath(audioDir)),
            SetupCompact.Row(Loc.Get(Strings.Settings.Storage.MetadataBudget),
                MetaBudgetCombo(settings, svc, metaBudgetSig)),
        ];

        // ── stage: a live streaming/EQ/cache readout, never a directory walk ────────────────────────────────────────
        int kbps = SetupSoundFacts.Kbps(quality);
        int meteredKbps = SetupSoundFacts.Kbps(meteredCap);
        string qualityLabel = QualityLabel(quality);
        string meteredLabel = QualityLabel(meteredCap);
        string gbPerHour = SetupSoundFacts.FormatGb(SetupSoundFacts.GbPerHour(kbps));
        string meteredGbPerHour = SetupSoundFacts.FormatGb(SetupSoundFacts.GbPerHour(meteredKbps));

        Element cardA = SetupStage.Card(
            SetupStage.CardTitle(Strings.Setup.Sound.StreamingTitle(kbps, qualityLabel)),
            SetupStage.CardLine(Strings.Setup.Sound.StreamingPerHour(gbPerHour)),
            SetupStage.CardLine(Strings.Setup.Sound.StreamingMetered(meteredLabel, meteredGbPerHour)));

        Element cardB = SetupStage.Card(
            WaveeEqualizerCurve.Create(eqGains, (band, gain) =>
            {
                if (settings is null) return;
                SetupWrites.SetEqualizerBand(band, gain, settings, svc);
                Bump();
            }, eqOn && settings is not null, height: 150f),
            new BoxEl
            {
                Direction = 0, Justify = FlexJustify.End,
                Children =
                [
                    HyperlinkButton.Create(Loc.Get(Strings.Settings.Sound.ResetCurve), () =>
                    {
                        if (settings is null) return;
                        SetupWrites.SetEqualizerPreset(s_eqPresetIds[0], s_eqPresetGains[0], settings, svc);
                        Bump();
                    }, isEnabled: eqOn && settings is not null),
                ],
            });

        // Never AudioBodyCache.Status() (a directory walk) on the render path — the drive total is a try/caught
        // DriveInfo read; a failure (an unmounted drive, a UNC location DriveInfo can't resolve) hides the bar rather
        // than showing a fraction of an unknown whole.
        long? driveTotal = null;
        try
        {
            string? root = Path.GetPathRoot(audioDir);
            if (!string.IsNullOrEmpty(root)) driveTotal = new DriveInfo(root).TotalSize;
        }
        catch { driveTotal = null; }

        string driveName = SetupSoundFacts.DriveLabel(audioDir) is { Length: > 0 } label ? label : Loc.Get(Strings.Setup.Sound.ThisDrive);
        string cacheReadout = (AudioCacheBudgetMode)budgetMode switch
        {
            AudioCacheBudgetMode.FixedBytes => Strings.Setup.Sound.CacheFixed(FormatBytesShort(fixedBudgetBytes), driveName),
            AudioCacheBudgetMode.Unlimited => Strings.Setup.Sound.CacheUnlimited(driveName),
            _ => Strings.Setup.Sound.CacheDriveShare(SetupSoundFacts.EffectivePercent(storedPercent), driveName),
        };

        Element[] cardCChildren = driveTotal is null
            ?
            [
                SetupStage.CardTitle(Loc.Get(Strings.Setup.Sound.CacheTitle)),
                SetupStage.CardLine(cacheReadout),
            ]
            :
            [
                SetupStage.CardTitle(Loc.Get(Strings.Setup.Sound.CacheTitle)),
                SetupStage.CardLine(cacheReadout),
                ProgressBar.Determinate((float)SetupSoundFacts.CacheShare((AudioCacheBudgetMode)budgetMode, fixedBudgetBytes, storedPercent, driveTotal), width: 296f),
            ];
        Element cardC = SetupStage.Card(cardCChildren);

        Element body = wide
            ? SetupCompact.Column(rows)
            : SetupCompact.Column([.. rows, cardA, cardB, cardC]);

        Element? stage = wide
            ? SetupStage.Column(cardA, cardB, cardC, SetupStage.Spacer(),
                SetupStage.Caption(Loc.Get(Strings.Setup.Sound.StageCaptionTitle), Loc.Get(Strings.Setup.Sound.StageCaptionSub)))
            : null;

        return SetupPageHost.Frame(SetupPage.Sound, Loc.Get(Strings.Setup.Eyebrow.Sound), Loc.Get(Strings.Setup.Sound.Title), body,
            lead: Loc.Get(Strings.Setup.Sound.Lead), leadMaxLines: 2, stage: stage, scrollBody: !wide);
    }

    static string QualityLabel(int index) => index switch
    {
        0 => Loc.Get(Strings.Settings.Playback.QualityNormal),
        1 => Loc.Get(Strings.Settings.Playback.QualityHigh),
        _ => Loc.Get(Strings.Settings.Playback.QualityVeryHigh),
    };

    static string FormatBytesShort(long bytes)
    {
        const double gib = 1024.0 * 1024 * 1024;
        const double mib = 1024.0 * 1024;
        if (bytes >= (long)gib) return (bytes / gib).ToString("0.#", CultureInfo.InvariantCulture) + " GB";
        if (bytes >= (long)mib) return (bytes / mib).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        return bytes.ToString(CultureInfo.InvariantCulture) + " B";
    }

    Element QualityCombo(IAppSettings? settings, Signal<int> sig)
    {
        // THREE rungs — the same offer Settings ▸ Playback makes. Lossless stays in AudioQualityPreference (the stored
        // int is persisted) but is not offered here either; the wizard must not promise a tier the app cannot play.
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
        return ComboBox.Create(labels, sig, width: 200f,
            itemDescriptions: descriptions, isEnabled: settings is not null,
            onChange: i =>
            {
                if (settings is null) return;
                SetupWrites.SetPlaybackQuality(i, settings);
                Bump();
            });
    }

    Element MeteredCombo(IAppSettings? settings, Signal<int> sig)
    {
        string[] labels =
        [
            Loc.Get(Strings.Settings.Playback.QualityNormal),
            Loc.Get(Strings.Settings.Playback.QualityHigh),
            Loc.Get(Strings.Settings.Playback.QualityVeryHigh),
        ];
        return ComboBox.Create(labels, sig, width: 200f, isEnabled: settings is not null,
            onChange: i =>
            {
                if (settings is null) return;
                SetupWrites.SetMeteredQualityCap(i, settings);
                Bump();
            });
    }

    Element CrossfadeControls(IAppSettings? settings, Services? svc, Signal<bool> onSig, Signal<int> secSig, bool crossfadeOn)
    {
        Element toggle = ToggleSwitch.Create(onSig, onChange: on =>
        {
            if (settings is null) return;
            SetupWrites.SetCrossfadeEnabled(on, settings, svc);
            Bump();
        }, style: SetupCompact.RowToggleStyle);

        Element duration = ComboBox.Create(["2 s", "5 s", "8 s", "12 s"], secSig,
            width: 100f, isEnabled: crossfadeOn && settings is not null, onChange: i =>
            {
                if (settings is null) return;
                double[] options = [2, 5, 8, 12];
                SetupWrites.SetCrossfadeSeconds(options[Math.Clamp(i, 0, options.Length - 1)], settings, svc);
                Bump();
            });

        return SetupCompact.Controls(toggle, duration);
    }

    Element EqualizerControls(IAppSettings? settings, Services? svc, Signal<bool> onSig, Signal<int> presetSig, bool eqOn)
    {
        Element toggle = ToggleSwitch.Create(onSig, onChange: on =>
        {
            if (settings is null) return;
            SetupWrites.SetEqualizerEnabled(on, settings, svc);
            Bump();
        }, style: SetupCompact.RowToggleStyle);

        Element preset = ComboBox.Create(EqPresetLabels(), presetSig, width: 160f,
            itemDescriptions: EqPresetDescriptions(), isEnabled: eqOn && settings is not null,
            onChange: i =>
            {
                if (settings is null) return;
                int next = Math.Clamp(i, 0, s_eqPresetIds.Length - 1);
                SetupWrites.SetEqualizerPreset(s_eqPresetIds[next], s_eqPresetGains[next], settings, svc);
                Bump();
            });

        return SetupCompact.Controls(toggle, preset);
    }

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

    static int EqPresetIndex(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return 0;
        for (int i = 0; i < s_eqPresetIds.Length; i++)
            if (string.Equals(id, s_eqPresetIds[i], StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    Element BudgetSegmented(IAppSettings? settings, Services? svc, int budgetMode)
    {
        SegmentedItem[] items =
        [
            new(Loc.Get(Strings.Settings.Storage.FixedSize)),
            new(Loc.Get(Strings.Settings.Storage.DriveShare)),
            new(Loc.Get(Strings.Settings.Storage.Unlimited)),
        ];
        return SetupCompact.Segmented(items, budgetMode, mode =>
        {
            if (settings is null) return;
            SetupWrites.SetAudioBodyCacheBudgetMode(mode, settings, svc);
            Bump();
        }, width: 246f);
    }

    Element CacheLocationButton(IAppSettings? settings, Services? svc, Action<Action> post)
        => Button.Create(Loc.Get(Strings.Setup.Sound.Choose), () =>
        {
            if (settings is null) return;
            SetupWrites.ChooseCacheLocation(svc, settings, post);
        }, ButtonAppearance.Standard, ControlSize.Small, isEnabled: svc?.AudioBodyCache != null);

    Element MetaBudgetCombo(IAppSettings? settings, Services? svc, Signal<int> sig)
        => ComboBox.Create(s_metaBudgetLabels, sig, width: 100f, isEnabled: settings is not null,
            onChange: i =>
            {
                if (settings is null || i < 0 || i >= SetupSoundFacts.MetaBudgetBytes.Length) return;
                SetupWrites.SetMetadataCacheBudgetBytes(SetupSoundFacts.MetaBudgetBytes[i], settings, svc);
                Bump();
            });
}
