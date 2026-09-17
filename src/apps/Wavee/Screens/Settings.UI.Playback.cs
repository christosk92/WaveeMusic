// ── Screens/Settings.UI.Playback.cs ────────────────────────────────────────────────────────────────────────────────
// the Playback tab: the runtime card (ready expander / not-set-up bar / problem bar) · Audio (quality with the D7 lossless
// rung, the metered cap + its live status line, remember volume, autoplay, normalization) · Sound (the equalizer curve,
// crossfade — both bound to the live DSP) · Video (quality, the metered cap, the attached-videos row + its "Manage"
// flyout anchor) · Player bar
//
// Role: UI
// Owner: R
// Wave: 6
// Budget: 800 lines
// Spec: DERIVED (ch 27 §9.3); ch 27 §0 N3-N5, N10, N11, §1.2 props-freeze map, W7-W11, §3, §4.3, §6.1, §7, §9.1-9.2,
// §9.6 (Connect diagnostics offered from the READY expander too), §10 items 21-35d; D7; gaps G-130, G-132
//
// THE LIVE DSP. Every equalizer / crossfade write persists and then pushes the WHOLE state to `Playback.Audio`
// (`SetEqualizer`, `SetCrossfade`), so the ear follows the curve while it is dragged; normalization goes through the
// `ApplyNormalization` seam below until the audio host exposes its own setter.
//
// SEAMS: `Settings.ApplyNormalization` (owner B4 assigns `Playback.Audio.SetNormalization`); `Settings.OpenVideoOverrides()`
// is the "Manage" deep-link door a missing/unplayable-video toast calls.

using System.Globalization;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Settings
{
    // ══ 1. SEAMS, STABLE CONTROL STATE (0.2.9 `SettingsPage.Playback.cs:31-41`, `VideoOverrides.cs:30-40`) ══════════

    /// <summary>Owner B4 assigns <c>Playback.Audio.SetNormalization</c> (G-132). Null → the key is still written and the
    /// audio open reads it, so the change lands from the next track — which is what the row's own copy promises.</summary>
    public static Action<bool>? ApplyNormalization;

    // ComboBox / NumberBox / Slider bind ONE signal instance for their life: these are seeded per page mount.
    static readonly Signal<int> s_quality = new(Platform.Keys.PlaybackQuality.Default);
    static readonly Signal<int> s_videoQuality = new(0);
    static readonly Signal<int> s_meteredVideo = new(1);
    static readonly Signal<int> s_eqPreset = new(0);
    static readonly Signal<double> s_crossSecs = new(5.0);
    static readonly FloatSignal s_crossSlider = new(5f);   // mirrors s_crossSecs at both write sites

    /// <summary>The parsed gain vector, re-parsed only when the stored string moves — a stable array keeps the curve's
    /// props record equal across an unrelated page re-render, so its 240 nodes are not rebuilt.</summary>
    static string? s_eqGainsRaw;
    static float[] s_eqGains = new float[Eq.BandCount];
    static readonly Action<int, float> s_onEqBand = SetEqBand;
    static readonly Func<string, string> s_locGet = Loc.Get;

    static readonly Slider.SliderOptions s_crossSliderOptions = new()
    {
        Min = 0f, Max = (float)Quality.CrossfadeMaxSeconds, Step = 0.5f, TickFrequency = 2f, IsThumbToolTipEnabled = true,
        ThumbToolTipValueConverter = static v => v.ToString("0.#", CultureInfo.InvariantCulture) + " s",
    };
    static readonly NumberBox.NumberBoxOptions s_crossBoxOn = new()
    {
        Minimum = 0, Maximum = Quality.CrossfadeMaxSeconds, SmallChange = 0.5,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 96f,
        Formatter = static v => v.ToString("0.#", CultureInfo.InvariantCulture) + " s",
    };
    static readonly NumberBox.NumberBoxOptions s_crossBoxOff = s_crossBoxOn with { IsEnabled = false };

    // The "Manage" flyout: its handle, the anchor (a wrapper around the button — Button owns its own root props) and the
    // deep link's deferred open.
    static OverlayHandle? s_voHandle;
    static NodeHandle s_voAnchor;
    static bool s_voOpenPending;

    static partial void SeedPlayback()
    {
        var s = Platform.Settings;
        s_quality.Value = Quality.AudioIndex(s.Get(Platform.Keys.PlaybackQuality), Spotify.Audio.CanDerive);
        s_videoQuality.Value = Quality.VideoIndex(s.Get(Platform.Keys.VideoQuality));
        s_meteredVideo.Value = Quality.MeteredVideoIndex(s.Get(Platform.Keys.VideoMeteredMaxHeight));
        s_eqPreset.Value = Eq.PresetIndex(s.Get(Platform.Keys.EqualizerPreset));
        double seconds = Quality.CrossfadeSeconds(s.Get(Platform.Keys.CrossfadeMs));
        s_crossSecs.Value = seconds;
        s_crossSlider.Value = (float)seconds;
    }

    /// <summary>Leaving the tab closes the manager and drops the (about-to-be-destroyed) anchor, but KEEPS an armed deep
    /// link — that request is what flips the page back to this tab.</summary>
    static partial void EnterPlayback(bool active)
    {
        if (active) return;
        s_voAnchor = NodeHandle.Null;
        if (s_voHandle is { IsOpen: true } open) open.Close();
        s_voHandle = null;
    }

    // ══ 2. THE TAB ════════════════════════════════════════════════════════════════════════════════════════════════════

    private static partial Element PlaybackTab() => TabStack(
        RuntimeCard(),

        SectionHeader(Loc.Get(Strings.Settings.Playback.AudioTitle), SectionGlyph(Tab.Playback, "Audio")),
        Row(Loc.Get(Strings.Settings.Playback.AudioQuality), Loc.Get(Strings.Settings.Playback.AudioSub),
            QualityCombo(), RowGlyph(Tab.Playback, "audioQuality")),
        // The description IS the detector's verdict: every state says something, "Unknown" included (never blank).
        Row(Loc.Get(Strings.Settings.Playback.MeteredQuality), MeteredStatus(),
            MeteredQualityCombo(), RowGlyph(Tab.Playback, "meteredQuality")),
        Row(Loc.Get(Strings.Settings.Playback.RememberVolume), Loc.Get(Strings.Settings.Playback.RememberVolumeSub),
            Toggle(Platform.Keys.RememberVolume), RowGlyph(Tab.Playback, "rememberVolume")),
        Row(Loc.Get(Strings.Settings.Playback.Autoplay), Loc.Get(Strings.Settings.Playback.AutoplaySub),
            Toggle(Platform.Keys.AutoplayEnabled), RowGlyph(Tab.Playback, "autoplay")),
        // G-132: the switch 0.2.9 persisted but never showed.
        Row(Loc.Get(Strings.Settings.Playback.Normalization), Loc.Get(Strings.Settings.Playback.NormalizationSub),
            Toggle(Platform.Keys.NormalizationEnabled, afterWrite: static on => ApplyNormalization?.Invoke(on)),
            RowGlyph(Tab.Playback, "normalization")),

        SectionHeader(Loc.Get(Strings.Settings.Sound.Title), SectionGlyph(Tab.Playback, "Sound")),
        EqualizerGroup(),
        CrossfadeGroup(),

        SectionHeader(Loc.Get(Strings.VideoOverride.SettingsTitle), SectionGlyph(Tab.Playback, "Video")),
        Row(Loc.Get(Strings.Settings.Playback.VideoQuality), Loc.Get(Strings.Settings.Playback.VideoQualitySub),
            VideoQualityCombo(), RowGlyph(Tab.Playback, "videoQuality")),
        Row(Loc.Get(Strings.Settings.Playback.VideoMeteredQuality), Loc.Get(Strings.Settings.Playback.VideoMeteredQualitySub),
            MeteredVideoCombo(), RowGlyph(Tab.Playback, "videoMetered")),
        VideoOverridesRow(),

        SectionHeader(Loc.Get(Strings.Settings.Playback.PlayerBar), SectionGlyph(Tab.Playback, "Player bar")),
        Row(Loc.Get(Strings.Settings.Playback.ShowRemaining), Loc.Get(Strings.Settings.Playback.ShowRemainingSub),
            Toggle(Platform.Keys.PlayerBarShowRemaining, afterWrite: static _ => Prefs.PlayerBar.Bump()),
            RowGlyph(Tab.Playback, "playerBarRemaining")));

    // ══ 3. THE RUNTIME CARD (W7 ready · W10 problem · the not-set-up bar) ═══════════════════════════════════════════

    /// <summary>Three rendered shapes over <see cref="Setup.Runtime.Status"/> — never a skeleton, never blank. Nothing known
    /// yet (<c>NotApplicable</c>) and a runtime that was never set up (<c>Missing</c>) are the Informational "Not set up"
    /// bar; the other issues are the Error bar with the diagnostics doors.</summary>
    static Element RuntimeCard()
    {
        var facts = Setup.Runtime.Status.Value;
        if (facts.IsReady)
        {
            return SettingsExpander.Create(new SettingsExpander.Options
            {
                Header = Loc.Get(Strings.Settings.Common.Ready),
                Description = Loc.Get(Strings.Settings.Playback.RuntimeReadySub),
                HeaderIcon = Icons.StatusSuccess,
                Content = Button.Standard(Loc.Get(Strings.Settings.Common.Manage), static () => Setup.Runtime.RequestOpen()),
                Items =
                [
                    RuntimeFact(Loc.Get(Strings.Playback.Runtime.DetailVersion),
                        facts.PackId is { Length: > 0 } pack ? facts.Version + " (" + pack + ")" : facts.Version),
                    RuntimeFact(Loc.Get(Strings.Playback.Runtime.DetailArch), facts.Arch),
                    RuntimeSignatureItem(facts),
                    RuntimeFact(Loc.Get(Strings.Playback.Runtime.DetailLocation), facts.Location),
                    // ch 27 §9.6: Connect diagnostics must not be reachable ONLY from a failure banner.
                    Item(Loc.Get("settings.playback.runtimeDiagnostics"), null, DiagnosticsLinks()),
                ],
            }) with { Key = "playback.runtime:ready" };
        }

        if (facts.Issue is Setup.RuntimeIssue.None or Setup.RuntimeIssue.Missing)
            return InfoBar.Create(InfoBarSeverity.Informational,
                Loc.Get(Strings.Settings.Playback.RuntimeNotSetUp), Loc.Get(Strings.Settings.Playback.RuntimeNotSetUpSub),
                isClosable: false,
                actionButton: Button.Accent(Loc.Get(Strings.Playback.Runtime.SetUp), static () => Setup.Runtime.RequestOpen()))
                with { Key = "playback.runtime:setup" };

        // The typed issue's own sentence — the banner used to show one static line and drop the reason.
        var links = DiagnosticsLinks();
        return InfoBar.Create(InfoBarSeverity.Error,
            Loc.Get(Strings.Settings.Common.Problem), Loc.Get(Setup.RuntimeRules.BannerLocKey(facts.Issue)),
            isClosable: false,
            actionButton: new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                Children = [links, Button.Accent(Loc.Get(Strings.Settings.Common.RetrySetup), static () => Setup.Runtime.RequestOpen())],
            }) with { Key = "playback.runtime:problem" };
    }

    /// <summary>"View diagnostics" (the runtime report) + "Connect diagnostics" (who owns playback — orthogonal to whether
    /// local playback is provisioned, so it rides the same row in every state).</summary>
    static Element DiagnosticsLinks() => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
        Children =
        [
            HyperlinkButton.Create(Loc.Get(Strings.Playback.Runtime.ViewDiagnostics),
                static () => Shell.GoTo(new Shell.Route(Shell.RouteKind.PlaybackDiagnostics))),
            HyperlinkButton.Create(Loc.Get(Strings.Nav.ConnectDiagnostics),
                static () => Shell.GoTo(new Shell.Route(Shell.RouteKind.ConnectDiagnostics))),
        ],
    };

    /// <summary>A fact the provisioner does not have is a GREYED row with no description, never a blank one (35c).</summary>
    static Element RuntimeFact(string label, string? value)
    {
        bool known = !string.IsNullOrWhiteSpace(value);
        return Item(label, known ? value : null, isEnabled: known);
    }

    /// <summary>The signature summary, plus "View signature" ONLY while the signed file still exists on disk.</summary>
    static Element RuntimeSignatureItem(Setup.RuntimeFacts facts)
    {
        string summary = Setup.RuntimeSignatureSummary(facts);
        bool canView = facts.Signature is { FilePath: { Length: > 0 } path } && File.Exists(path);
        Element? link = canView
            ? HyperlinkButton.Create(Loc.Get(Strings.Playback.Runtime.ViewSignature), static () =>
            {
                if (s_overlay is { } overlay) Shell.OpenSignatureDialog(overlay, Setup.Runtime.Status.Peek());
            })
            : null;
        return Item(Loc.Get(Strings.Playback.Runtime.DetailSignature), summary.Length > 0 ? summary : null, link);
    }

    // ══ 4. AUDIO (G-130: the D7 rung, the live metered cap) ═══════════════════════════════════════════════════════════

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

    /// <summary>D7: Lossless is OFFERED only when a key deriver is installed — without one it cannot play, and a
    /// permanently-dead rung is an advert. The combo's items freeze at mount, so the key carries the rung count.</summary>
    static Element QualityCombo()
    {
        int count = Quality.AudioRungCount(Spotify.Audio.CanDerive);
        return ComboBox.Create(QualityLabels(count), s_quality, width: 280f, itemDescriptions: QualityDescriptions(count),
            onChange: static i =>
            {
                if (!Quality.IsWritableAudio(i, Spotify.Audio.CanDerive)) return;
                Platform.Settings.Set(Platform.Keys.PlaybackQuality, i);
                Bump();
            }) with { Key = count == Quality.AudioRungCount(true) ? "playback.quality:lossless" : "playback.quality" };
    }

    /// <summary>Reactive: the live cost, the live cap and the chosen quality are all read here, so an NLM push or either
    /// combo re-words the line. "Cap in effect" is claimed only when the cap is actually below the chosen quality.</summary>
    static string MeteredStatus()
    {
        bool capInEffect = Platform.Network.MeteredQualityCap.Value < s_quality.Value;
        return MeteredStatusLine.For(Platform.Network.Cost.Value, capInEffect).Render(s_locGet);
    }

    /// <summary>Bound STRAIGHT to the live cap signal (33b), not a page mirror. The same ladder as the streaming combo
    /// (flac plan §5.4), Lossless offered under the same D7 deriver gate; the key carries the rung count because the
    /// combo's items freeze at mount.</summary>
    static Element MeteredQualityCombo()
    {
        int count = Quality.MeteredRungCount(Spotify.Audio.CanDerive);
        return ComboBox.Create(QualityLabels(count), Platform.Network.MeteredQualityCap, width: 280f,
            itemDescriptions: QualityDescriptions(count), onChange: static i =>
            {
                if ((uint)i >= (uint)Quality.MeteredRungCount(Spotify.Audio.CanDerive)) return;
                Platform.Network.SetMeteredQualityCap(i);   // persist, then publish
                Bump();
            }) with { Key = count == Quality.AudioRungCount(true) ? "playback.meteredCap:lossless" : "playback.meteredCap" };
    }

    // ══ 5. SOUND — THE EQUALIZER (W7, W8, N10) ════════════════════════════════════════════════════════════════════════

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

    /// <summary><c>InitiallyExpanded = eqOn</c> is read at MOUNT (33c): turning the EQ off leaves the open expander open.
    /// The curve item is <c>Vertical</c> at every width — the full 898-DIP lane at card 1000 (parity 11a, 24).</summary>
    static Element EqualizerGroup()
    {
        bool eqOn = Platform.Settings.Get(Platform.Keys.EqualizerEnabled);
        int preset = Math.Clamp(s_eqPreset.Value, 0, Eq.PresetIds.Length - 1);
        string[] descriptions = EqPresetDescriptions();
        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Loc.Get(Strings.Settings.Sound.Equalizer),
            Description = Loc.Get(Strings.Settings.Sound.EqualizerSub),
            HeaderIcon = RowGlyph(Tab.Playback, "equalizer"),
            Content = Toggle(Platform.Keys.EqualizerEnabled, afterWrite: static _ => PushEqualizer()),
            InitiallyExpanded = eqOn,
            Items =
            [
                // The item's own description is the SELECTED preset's sentence.
                Item(Loc.Get(Strings.Settings.Sound.Preset), descriptions[preset],
                    ComboBox.Create(EqPresetLabels(), s_eqPreset, width: 200f, itemDescriptions: descriptions,
                        isEnabled: eqOn, onChange: static i => ApplyEqPreset(i))),
                Item(Loc.Get(Strings.Settings.Sound.Curve),
                    Loc.Get(eqOn ? Strings.Settings.Sound.CurveOn : Strings.Settings.Sound.CurveOff),
                    new BoxEl
                    {
                        Direction = 1, Gap = Spacing.S,
                        Children =
                        [
                            Controls.EqualizerCurve(EqGains(), s_onEqBand, eqOn),
                            new BoxEl
                            {
                                Direction = 0, Justify = FlexJustify.End,
                                Children = [HyperlinkButton.Create(Loc.Get(Strings.Settings.Sound.ResetCurve), static () => ResetEqCurve(), isEnabled: eqOn)],
                            },
                        ],
                    },
                    align: SettingsCard.ContentAlignment.Vertical),
            ],
        }) with { Key = "playback.equalizer" };
    }

    static float[] EqGains()
    {
        string raw = Platform.Settings.Get(Platform.Keys.EqualizerGains);
        if (!string.Equals(raw, s_eqGainsRaw, StringComparison.Ordinal))
        {
            s_eqGainsRaw = raw;
            s_eqGains = Eq.ParseGains(raw);   // a NEW array: the curve's props gate sees the change
        }
        return s_eqGains;
    }

    /// <summary>The whole EQ state to the live graph: a gain-only change ramps; an on/off flip changes the topology.</summary>
    static void PushEqualizer()
        => Playback.Audio.SetEqualizer(Platform.Settings.Get(Platform.Keys.EqualizerEnabled), EqGains());

    /// <summary>One band from the curve (drag, click or keys). The curve reports every pointer sample; a sample that lands
    /// on the band's current half-decibel writes nothing.</summary>
    static void SetEqBand(int band, float gain)
    {
        if ((uint)band >= (uint)Eq.BandCount) return;
        var gains = Eq.ReadGains(Platform.Settings);
        float snapped = Eq.SnapGain(gain);
        if (gains[band] == snapped) return;
        gains[band] = snapped;
        Platform.Settings.Set(Platform.Keys.EqualizerGains, Eq.SerializeGains(gains));
        PushEqualizer();
        Bump();
    }

    static void ApplyEqPreset(int index)
    {
        int idx = Math.Clamp(index, 0, Eq.PresetIds.Length - 1);
        s_eqPreset.Value = idx;
        Platform.Settings.Set(Platform.Keys.EqualizerPreset, Eq.PresetIds[idx]);
        Platform.Settings.Set(Platform.Keys.EqualizerGains, Eq.SerializeGains(Eq.PresetGains[idx]));
        PushEqualizer();
        Bump();
    }

    static void ResetEqCurve() => ApplyEqPreset(0);

    // ══ 6. SOUND — CROSSFADE (W9) ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The slider and the NumberBox stay in lock-step through ONE commit. The duration row is re-keyed on the
    /// switch (ch 27 §1.2's documented NumberBox cure), so both controls follow the toggle on the same frame.</summary>
    static Element CrossfadeGroup()
    {
        bool crossOn = Platform.Settings.Get(Platform.Keys.CrossfadeEnabled);
        string seconds = s_crossSecs.Value.ToString("0.#", CultureInfo.InvariantCulture);
        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Loc.Get(Strings.Settings.Sound.Crossfade),
            Description = Loc.Get(Strings.Settings.Sound.CrossfadeSub),
            HeaderIcon = RowGlyph(Tab.Playback, "crossfade"),
            Content = Toggle(Platform.Keys.CrossfadeEnabled, afterWrite: static _ => PushCrossfade()),
            InitiallyExpanded = crossOn,
            Items =
            [
                Item(Loc.Get(Strings.Settings.Sound.CrossfadeDuration), Strings.Settings.Sound.Seconds(seconds), new BoxEl
                {
                    Key = crossOn ? "crossfade-duration-on" : "crossfade-duration-off",
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                    Children =
                    [
                        Slider.Create(s_crossSlider, static v => CommitCrossfade(v), s_crossSliderOptions, length: 220f, isEnabled: crossOn),
                        NumberBox.Create(s_crossSecs, static v => CommitCrossfade(v), crossOn ? s_crossBoxOn : s_crossBoxOff),
                    ],
                }),
            ],
        }) with { Key = "playback.crossfade" };
    }

    /// <summary>Both controls commit here: <c>round(clamp(s, 0, 12) × 1000)</c> ms, mirrored back into BOTH signals, then
    /// persisted and pushed only when the millisecond value actually moved (a slider drag reports every sample).</summary>
    static void CommitCrossfade(double seconds)
    {
        int ms = Quality.CrossfadeMs(seconds);
        double snapped = ms / 1000.0;
        s_crossSecs.Value = snapped;
        s_crossSlider.Value = (float)snapped;
        if (Platform.Settings.Get(Platform.Keys.CrossfadeMs) == ms) return;
        Platform.Settings.Set(Platform.Keys.CrossfadeMs, ms);
        PushCrossfade();
        Bump();
    }

    static void PushCrossfade()
        => Playback.Audio.SetCrossfade(Platform.Settings.Get(Platform.Keys.CrossfadeEnabled), Platform.Settings.Get(Platform.Keys.CrossfadeMs));

    // ══ 7. VIDEO (W11) ════════════════════════════════════════════════════════════════════════════════════════════════

    static Element VideoQualityCombo()
        => ComboBox.Create([Loc.Get(Strings.Settings.Playback.VideoQualityAuto), "180p", "240p", "320p", "480p", "720p", "1080p"],
            s_videoQuality, width: 280f, onChange: static i =>
            {
                if ((uint)i >= (uint)Quality.VideoHeights.Length) return;
                int height = Quality.VideoHeights[i];   // stored as a height; 0 = Auto
                Platform.Settings.Set(Platform.Keys.VideoQuality, height);
                Playback.Video.SetPreferredHeight(height);   // the live pin: the ABR ceiling + the engine's selection
                Bump();
            });

    /// <summary>A stored height that matches nothing reads as 480p (index 1). Persisted and published through the
    /// network policy; the video session folds it on its next quality decision.</summary>
    static Element MeteredVideoCombo()
        => ComboBox.Create([Loc.Get(Strings.Settings.Playback.VideoQualityUnlimited), "480p", "720p", "1080p"],
            s_meteredVideo, width: 280f, onChange: static i =>
            {
                if ((uint)i >= (uint)Quality.MeteredVideoHeights.Length) return;
                Platform.Network.SetMeteredVideoMaxHeight(Quality.MeteredVideoHeights[i]);
                Bump();
            });

    /// <summary>The attached-videos SUMMARY (ch 27 W11's states): no roster store → the whole row greyed with NO control;
    /// 0 rows → "No videos attached yet" + Manage and no "Remove all"; N rows → the count + Manage + Remove all. The roster
    /// is warm and synchronous (<see cref="Video.Overrides"/>), so there is no cold-load spinner to show — and the Manage
    /// wrapper node never swaps out under an open flyout.</summary>
    static Element VideoOverridesRow()
    {
        _ = Video.Overrides.Epoch.Value;   // an attach/remove anywhere (the track menu, an undo toast, the flyout) re-reads
        string header = Loc.Get(Strings.VideoOverride.SettingsHeader);
        string sub = Loc.Get(Strings.VideoOverride.SettingsSub);   // the trust disclosure: device-wide, linked not copied
        string glyph = RowGlyph(Tab.Playback, "videoOverrides");
        if (!Video.Overrides.Present)
        {
            s_voAnchor = NodeHandle.Null;
            return Row(header, sub, null, glyph, isEnabled: false);
        }

        int count = Video.Overrides.Count;
        var kids = new Element[count > 0 ? 3 : 2];
        kids[0] = new TextEl(count > 0 ? Strings.VideoOverride.SettingsCount(count) : Loc.Get(Strings.VideoOverride.SettingsEmpty))
        {
            Size = 12f, Color = Tok.TextSecondary,
        };
        kids[1] = new BoxEl
        {
            Direction = 0, Shrink = 0f,
            // The realized wrapper is the anchor, and its realization is what satisfies a deep link that arrived first.
            OnRealized = static h =>
            {
                s_voAnchor = h;
                if (s_voOpenPending) s_post(TryOpenPendingVideoOverrides);
            },
            Children = [Button.Standard(Loc.Get(Strings.VideoOverride.Manage), static () => ToggleVideoOverrideManager())],
        };
        if (count > 0)
            // Bulk detach is the ONE place a confirm earns its keep here: N links at once, no per-row undo (N11).
            kids[2] = Button.Standard(Loc.Get(Strings.VideoOverride.ClearAll), static () => ConfirmThen(
                Loc.Get(Strings.VideoOverride.ClearAll), Loc.Get(Strings.VideoOverride.ClearAllBody),
                Loc.Get(Strings.VideoOverride.ClearAll), ClearAllVideoOverrides));

        return Row(header, sub, new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Children = kids }, glyph);
    }

    /// <summary>"Manage" is a TOGGLE (35d): a second press closes the flyout. The body is owner K's.</summary>
    static void ToggleVideoOverrideManager()
    {
        if (s_overlay is not { } overlay || !Video.Overrides.Present) return;
        if (s_voHandle is { IsOpen: true } open) { open.Close(); return; }
        var handle = overlay.Open(
            static () => s_voAnchor,
            static () => VideoOverridesBody(),
            FlyoutPlacement.BottomEdgeAlignedLeft,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
            {
                ConstrainToRootBounds = true,
            });
        s_voHandle = handle;
        handle.ClosedAction = () => { if (ReferenceEquals(s_voHandle, handle)) s_voHandle = null; };
    }

    /// <summary>The "Manage" deep link (a missing / unplayable attachment toast's action): land on Settings ▸ Playback AND
    /// open the manager. The open is deferred — the button is not realized on the frame the tab flips — and whichever of
    /// the posted retry or the button's realization sees a live anchor first wins, exactly once.</summary>
    public static void OpenVideoOverrides()
    {
        s_voOpenPending = true;
        Open(Tab.Playback);
        s_post(TryOpenPendingVideoOverrides);
    }

    static void TryOpenPendingVideoOverrides()
    {
        if (!s_voOpenPending || s_voAnchor.IsNull || s_overlay is null || !Video.Overrides.Present) return;
        s_voOpenPending = false;
        if (s_voHandle is { IsOpen: true }) return;   // already showing — the request is satisfied
        ToggleVideoOverrideManager();
    }

    static void ClearAllVideoOverrides()
    {
        var all = Video.Overrides.All();
        int removed = 0;
        for (int i = 0; i < all.Count; i++)
            if (Video.Overrides.Remove(all[i].Uri)) removed++;
        Log.Event(WaveeLogLevel.Info, "ui", "override.settings.clear_all", "detached every attached video",
            fields: [WaveeLogField.Of("count", removed)]);
        Notify.Say(Loc.Get(Strings.VideoOverride.ClearedAll), InfoBarSeverity.Success);
    }
}
