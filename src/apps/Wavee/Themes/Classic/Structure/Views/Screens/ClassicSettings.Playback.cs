// ── Themes/Classic/Structure/Views/Screens/ClassicSettings.Playback.cs — Sound's and Playback's own cards (settings rework 7.1) ────────────────────
//
// From `Screens\Settings.UI.Playback.cs`. Sound: the audio quality (the D7 lossless rung, each rung's sentence), the metered cap and its live status line, the
// loudness level's combo, the equalizer (the curve bound to the live DSP, the preset, Reset). Playback: crossfade (its slider and number box in lock-step), the
// video quality and metered cap combos, the playback runtime card (ready expander / not-set-up bar / problem bar). The attached videos' row and its "Manage"
// flyout are `ClassicSettings.LinkedVideos.cs`; every other Sound and Playback dial is the generic grammar.
//
// THE LIVE DSP is the verbs'. Every equalizer / crossfade write persists and then pushes the WHOLE state to the live graph (`SettingsVerbs.SetEqBand`,
// `CommitCrossfade`), so the ear follows the curve while it is dragged; this view keeps only the mirrors of its frozen controls (a combo's selection signal).

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

internal static partial class ClassicSettings
{
    // ══ 1. STABLE CONTROL STATE (0.2.9 `SettingsPage.Playback.cs:31-41`, `LinkedVideos.cs:30-40`) ═════════════════

    // ComboBox / NumberBox / Slider bind ONE signal instance for their life: these are seeded per page mount.
    static readonly Signal<int> s_quality = new(Platform.Keys.PlaybackQuality.Default);
    static readonly Signal<int> s_normMode = new(Platform.Keys.NormalizationMode.Default);
    static readonly Signal<int> s_videoQuality = new(0);
    static readonly Signal<int> s_meteredVideo = new(1);
    static readonly Signal<int> s_eqPreset = new(0);
    static readonly Signal<double> s_crossSecs = new(5.0);
    static readonly FloatSignal s_crossSlider = new(5f);   // mirrors s_crossSecs at both write sites

    /// <summary>The parsed gain vector, re-parsed only when the stored string moves — a stable array keeps the curve's
    /// props record equal across an unrelated page re-render, so its 240 nodes are not rebuilt.</summary>
    static string? s_eqGainsRaw;
    static float[] s_eqGains = new float[Settings.Eq.BandCount];
    static readonly Action<int, float> s_onEqBand = SetEqBand;
    static readonly Func<string, string> s_locGet = Loc.Get;

    static readonly Slider.SliderOptions s_crossSliderOptions = new()
    {
        Min = 0f, Max = (float)Settings.Quality.CrossfadeMaxSeconds, Step = 0.5f, TickFrequency = 2f, IsThumbToolTipEnabled = true,
        ThumbToolTipValueConverter = static v => v.ToString("0.#", CultureInfo.InvariantCulture) + " s",
    };
    static readonly NumberBox.NumberBoxOptions s_crossBoxOn = new()
    {
        Minimum = 0, Maximum = Settings.Quality.CrossfadeMaxSeconds, SmallChange = 0.5,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = ClassicSettingsRules.CrossBoxWidth,
        Formatter = static v => v.ToString("0.#", CultureInfo.InvariantCulture) + " s",
    };
    static readonly NumberBox.NumberBoxOptions s_crossBoxOff = s_crossBoxOn with { IsEnabled = false };

    // The "Manage" flyout: its handle and the anchor (a wrapper around the button — Button owns its own root props).
    // ROOT-LIFETIME (RootScope.cs): the flyout and its anchor belong to the mounted root's tree and overlay host.
    static readonly RootState<OverlayHandle?> s_voHandleState = new("Settings.LinkedVideos.Handle");
    static readonly RootState<NodeHandle> s_voAnchorState = new("Settings.LinkedVideos.Anchor");
    static OverlayHandle? s_voHandle { get => s_voHandleState.Value; set => s_voHandleState.Value = value; }
    static NodeHandle s_voAnchor { get => s_voAnchorState.Value; set => s_voAnchorState.Value = value; }

    /// <summary>The page mount: every frozen control's signal from its dial's value.</summary>
    static void SeedPlayback()
    {
        if (Dial("sound.quality").Current is int quality) s_quality.Value = quality;
        if (Dial("sound.normalization.mode").Current is int normMode) s_normMode.Value = normMode;
        if (Dial("playback.video.quality").Current is int videoQuality) s_videoQuality.Value = videoQuality;
        if (Dial("playback.video.meteredQuality").Current is int meteredVideo) s_meteredVideo.Value = meteredVideo;
        if (Dial("sound.equalizer.preset").Current is int eqPreset) s_eqPreset.Value = eqPreset;
        double seconds = SettingsFacts.CrossfadeSeconds();
        s_crossSecs.Value = seconds;
        s_crossSlider.Value = (float)seconds;
    }

    /// <summary>Leaving Playback closes the manager and drops the (about-to-be-destroyed) anchor.</summary>
    static void EnterPlayback(bool active)
    {
        if (active) return;
        s_voAnchor = NodeHandle.Null;
        if (s_voHandle is { IsOpen: true } open) open.Close();
        s_voHandle = null;
    }

    // ══ 3. THE RUNTIME CARD (W7 ready · W10 problem · the not-set-up bar) ═══════════════════════════════════════════

    /// <summary>Three rendered shapes over <see cref="Setup.Runtime.Status"/> — never a skeleton, never blank. Nothing known
    /// yet (<c>NotApplicable</c>) and a runtime that was never set up (<c>Missing</c>) are the Informational "Not set up"
    /// bar; the other issues are the Error bar with the diagnostics doors.</summary>
    static Element RuntimeCard(SettingsBodyRow row)
    {
        var facts = Setup.Runtime.Status.Value;
        if (facts.IsReady)
        {
            return SettingsExpander.Create(new SettingsExpander.Options
            {
                Header = Loc.Get(Strings.Settings.Common.Ready),
                Description = Loc.Get(Strings.Settings.Playback.RuntimeReadySub),
                HeaderIcon = Icons.StatusSuccess,
                Content = StdButton(Loc.Get(Strings.Settings.Common.Manage), static () => Dial("playback.runtime").Writer.Run!()),
                IsExpanded = ExpandCell(row.Id, false),
                Items =
                [
                    RuntimeFact(Loc.Get(Strings.Playback.Runtime.DetailVersion),
                        facts.PackId is { Length: > 0 } pack ? facts.Version + " (" + pack + ")" : facts.Version),
                    RuntimeFact(Loc.Get(Strings.Playback.Runtime.DetailArch), facts.Arch),
                    RuntimeSignatureItem(row, facts),
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
                actionButton: AccentButton(Loc.Get(Strings.Playback.Runtime.SetUp), static () => Dial("playback.runtime").Writer.Run!()))
                with { Key = "playback.runtime:setup" };

        // The typed issue's own sentence — the banner used to show one static line and drop the reason.
        var links = DiagnosticsLinks();
        return InfoBar.Create(InfoBarSeverity.Error,
            Loc.Get(Strings.Settings.Common.Problem), Loc.Get(Setup.RuntimeRules.BannerLocKey(facts.Issue)),
            isClosable: false,
            actionButton: new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                Children = [links, AccentButton(Loc.Get(Strings.Settings.Common.RetrySetup), static () => Dial("playback.runtime").Writer.Run!())],
            }) with { Key = "playback.runtime:problem" };
    }

    /// <summary>"View diagnostics" (the runtime report) + "Connect diagnostics" (who owns playback — orthogonal to whether
    /// local playback is provisioned, so it rides the same row in every state). The two doors are Help & about's Diagnostics rows.</summary>
    static Element DiagnosticsLinks() => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
        Children =
        [
            LinkButton(Loc.Get(Strings.Playback.Runtime.ViewDiagnostics),
                static () => Dial("help.tools.playbackRuntime").Writer.Run!()),
            LinkButton(Loc.Get(Strings.Nav.ConnectDiagnostics),
                static () => Dial("help.connectDiagnostics").Writer.Run!()),
        ],
    };

    /// <summary>A fact the provisioner does not have is a GREYED row with no description, never a blank one (35c).</summary>
    static Element RuntimeFact(string label, string? value)
    {
        bool known = !string.IsNullOrWhiteSpace(value);
        return Item(label, known ? value : null, isEnabled: known);
    }

    /// <summary>The signature summary, plus "View signature" ONLY while the signed file still exists on disk (the runtime's child dial, keyed as its row).</summary>
    static Element RuntimeSignatureItem(SettingsBodyRow row, Setup.RuntimeFacts facts)
    {
        string summary = Setup.RuntimeSignatureSummary(facts);
        bool canView = facts.Signature is { FilePath: { Length: > 0 } path } && File.Exists(path);
        Element? link = canView
            ? LinkButton(Loc.Get(Strings.Playback.Runtime.ViewSignature), static () => Dial("playback.runtime.signature").Writer.Run!())
            : null;
        Element item = Item(Loc.Get(Strings.Playback.Runtime.DetailSignature), summary.Length > 0 ? summary : null, link);
        return ChildRow(row, "playback.runtime.signature") is { } signature ? Framed(signature, item, item: true) : item;
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
    /// permanently-dead rung is an advert. The combo's items freeze at mount, so the key carries the rung count. The combo is on
    /// <paramref name="selected"/> (the page's signal, or the setup wizard's own, seeded from the dial).</summary>
    static Element QualityCombo(Signal<int> selected)
    {
        int count = Settings.Quality.AudioRungCount(SettingsFacts.Live.CanDerive);
        return Combo(QualityLabels(count), selected, width: ClassicSettingsRules.WideComboWidth, itemDescriptions: QualityDescriptions(count),
            onChange: static i =>
            {
                if (Dial("sound.quality").Writer.Choose!(i)) Settings.Bump();
            }) with { Key = count == Settings.Quality.AudioRungCount(true) ? "playback.quality:lossless" : "playback.quality" };
    }

    /// <summary>Reactive: the live cost, the live cap and the chosen quality are all read here, so an NLM push or either
    /// combo re-words the line. "Cap in effect" is claimed only when the cap is actually below the chosen quality.</summary>
    static string MeteredStatus()
    {
        bool capInEffect = Platform.Network.MeteredQualityCap.Value < s_quality.Value;
        return Settings.MeteredStatusLine.For(Platform.Network.Cost.Value, capInEffect).Render(s_locGet);
    }

    /// <summary>Bound STRAIGHT to the live cap signal (33b), not a page mirror. The same ladder as the streaming combo
    /// (flac plan §5.4), Lossless offered under the same D7 deriver gate; the key carries the rung count because the
    /// combo's items freeze at mount.</summary>
    static Element MeteredQualityCombo()
    {
        int count = Settings.Quality.MeteredRungCount(SettingsFacts.Live.CanDerive);
        return Combo(QualityLabels(count), Platform.Network.MeteredQualityCap, width: ClassicSettingsRules.WideComboWidth,
            itemDescriptions: QualityDescriptions(count), onChange: static i =>
            {
                if (Dial("sound.meteredQuality").Writer.Choose!(i)) Settings.Bump();   // persist, then publish
            }) with { Key = count == Settings.Quality.AudioRungCount(true) ? "playback.meteredCap:lossless" : "playback.meteredCap" };
    }

    // ══ 5. SOUND — THE EQUALIZER (W7, W8, N10) ════════════════════════════════════════════════════════════════════════

    /// <summary>The loudness level (Quiet · Normal · Loud) as the normalization expander's item: each level's sentence in the list.</summary>
    static Element NormalizationModeItem(SettingsBodyRow row)
        => Item(row.Label, row.Description, Badged(row,
            Combo([Loc.Get(Strings.Settings.Playback.NormQuiet), Loc.Get(Strings.Settings.Playback.NormNormal), Loc.Get(Strings.Settings.Playback.NormLoud)],
                s_normMode, width: ClassicSettingsRules.NarrowComboWidth, isEnabled: row.Enabled,
                itemDescriptions: [Loc.Get(Strings.Settings.Playback.NormQuietSub), Loc.Get(Strings.Settings.Playback.NormNormalSub), Loc.Get(Strings.Settings.Playback.NormLoudSub)],
                onChange: static i => Dial("sound.normalization.mode").Writer.Choose!(i))), isEnabled: row.Enabled, icon: GlyphOf(row.Dial));

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

    /// <summary>The expander opens with the EQ on the first time the page asks (33c): turning the EQ off leaves the open expander open.
    /// The curve item is <c>Vertical</c> at every width — the full 898-DIP lane at card 1000 (parity 11a, 24).
    /// <para>The card's content slot is a ROW that shrink-wraps its child (<c>SettingsCard.BuildContent</c>), so the
    /// curve's column claims the lane with <c>Grow 1 / Basis 0 / MinWidth 0</c>. Without them the column takes its
    /// CONTENT's width — and the curve, which measures the slot it is handed, measured its own guess back and held it
    /// (the chart overran the card, and "Reset curve to flat" was cut off at the card's right edge).</para></summary>
    static Element EqualizerGroup(SettingsBodyRow row)
    {
        bool eqOn = SettingsFacts.Live.EqualizerEnabled;
        int preset = Math.Clamp(s_eqPreset.Value, 0, Settings.Eq.PresetIds.Length - 1);
        string[] descriptions = EqPresetDescriptions();
        // The item's own description is the SELECTED preset's sentence.
        Element presetItem = Item(Loc.Get(Strings.Settings.Sound.Preset), descriptions[preset],
            Combo(EqPresetLabels(), s_eqPreset, width: ClassicSettingsRules.NarrowComboWidth, itemDescriptions: descriptions,
                isEnabled: eqOn, onChange: static i => ApplyEqPreset(i)));
        Element reset = LinkButton(Loc.Get(Strings.Settings.Sound.ResetCurve), static () => ResetEqCurve(), isEnabled: eqOn);
        Element curveItem = Item(Loc.Get(Strings.Settings.Sound.Curve),
            Loc.Get(eqOn ? Strings.Settings.Sound.CurveOn : Strings.Settings.Sound.CurveOff),
            new BoxEl
            {
                Direction = 1, Gap = Spacing.S, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                Children =
                [
                    Controls.EqualizerCurve(EqGains(), s_onEqBand, eqOn),
                    new BoxEl
                    {
                        Direction = 0, Justify = FlexJustify.End,
                        Children = [ChildRow(row, "sound.equalizer.reset") is { } resetRow ? Framed(resetRow, reset) : reset],
                    },
                ],
            },
            align: SettingsCard.ContentAlignment.Vertical);
        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = row.Label,
            Description = row.Description,
            HeaderIcon = GlyphOf(row.Dial),
            Content = Badged(row, RowToggle(row.Dial)),
            Style = ToggleHeaderStyle(RowToggle(row.Dial)),
            IsExpanded = ExpandCell(row.Id, eqOn),
            Items =
            [
                ChildRow(row, "sound.equalizer.preset") is { } presetRow ? Framed(presetRow, presetItem, item: true) : presetItem,
                ChildRow(row, "sound.equalizer.curve") is { } curveRow ? Framed(curveRow, curveItem, item: true) : curveItem,
            ],
        }) with { Key = "playback.equalizer" };
    }

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

    /// <summary>One band from the curve (drag, click or keys). The curve reports every pointer sample; a sample that lands
    /// on the band's current half-decibel writes nothing (the verb says so).</summary>
    static void SetEqBand(int band, float gain)
    {
        if (SettingsVerbs.SetEqBand(band, gain)) Settings.Bump();
    }

    /// <summary>A preset: the verb persists and pushes the whole state to the live graph; this view keeps its combo's mirror.</summary>
    static void ApplyEqPreset(int index)
    {
        int idx = Math.Clamp(index, 0, Settings.Eq.PresetIds.Length - 1);
        s_eqPreset.Value = idx;
        Dial("sound.equalizer.preset").Writer.Choose!(idx);
        Settings.Bump();
    }

    static void ResetEqCurve() => ApplyEqPreset(0);

    // ══ 6. SOUND — CROSSFADE (W9) ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The slider and the NumberBox stay in lock-step through ONE commit. The duration row is re-keyed on the
    /// switch (ch 27 §1.2's documented NumberBox cure), so both controls follow the toggle on the same frame.</summary>
    static Element CrossfadeGroup(SettingsBodyRow row)
    {
        bool crossOn = SettingsFacts.Live.CrossfadeEnabled;
        string seconds = s_crossSecs.Value.ToString("0.#", CultureInfo.InvariantCulture);
        SettingsBodyRow? durationRow = ChildRow(row, "playback.crossfade.duration");
        Element durationItem = Item(Loc.Get(Strings.Settings.Sound.CrossfadeDuration), Strings.Settings.Sound.Seconds(seconds), new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Wrap = true,
            Children =
            [
                durationRow?.Explain is not null ? Badged(durationRow, null)!.Value : new BoxEl { Width = 0f, Height = 0f },
                new BoxEl
                {
                    Key = crossOn ? "crossfade-duration-on" : "crossfade-duration-off",
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Wrap = true,
                    Children =
                    [
                        SliderControl(s_crossSlider, static v => CommitCrossfade(v), s_crossSliderOptions, length: ClassicSettingsRules.CrossSliderLength, isEnabled: crossOn),
                        NumberField(s_crossSecs, static v => CommitCrossfade(v), crossOn ? s_crossBoxOn : s_crossBoxOff, "crossfade-seconds"),
                    ],
                },
            ],
        });
        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = row.Label,
            Description = row.Description,
            HeaderIcon = GlyphOf(row.Dial),
            Content = Badged(row, RowToggle(row.Dial)),
            Style = ToggleHeaderStyle(RowToggle(row.Dial)),
            IsExpanded = ExpandCell(row.Id, crossOn),
            Items = [durationRow is { } d ? Framed(d, durationItem, item: true) : durationItem],
        }) with { Key = "playback.crossfade" };
    }

    /// <summary>Both controls commit here: <c>round(clamp(s, 0, 12) × 1000)</c> ms, mirrored back into BOTH signals, then
    /// persisted and pushed only when the millisecond value actually moved (a slider drag reports every sample).</summary>
    static void CommitCrossfade(double seconds)
    {
        double snapped = Settings.Quality.CrossfadeMs(seconds) / 1000.0;
        s_crossSecs.Value = snapped;
        s_crossSlider.Value = (float)snapped;
        if (SettingsVerbs.CommitCrossfade(seconds)) Settings.Bump();
    }

    // ══ 7. VIDEO (W11) ════════════════════════════════════════════════════════════════════════════════════════════════

    static Element VideoQualityCombo()
        => Combo([Loc.Get(Strings.Settings.Playback.VideoQualityAuto), "180p", "240p", "320p", "480p", "720p", "1080p"],
            s_videoQuality, width: ClassicSettingsRules.WideComboWidth, onChange: static i =>
            {
                // Stored as a height (0 = Auto); the verb persists, then pins live: the ABR ceiling + the engine's selection.
                if (Dial("playback.video.quality").Writer.Choose!(i)) Settings.Bump();
            });

    /// <summary>A stored height that matches nothing reads as 480p (index 1). Persisted and published through the
    /// network policy; the video session folds it on its next quality decision.</summary>
    static Element MeteredVideoCombo()
        => Combo([Loc.Get(Strings.Settings.Playback.VideoQualityUnlimited), "480p", "720p", "1080p"],
            s_meteredVideo, width: ClassicSettingsRules.WideComboWidth, onChange: static i =>
            {
                if (Dial("playback.video.meteredQuality").Writer.Choose!(i)) Settings.Bump();
            });
}
