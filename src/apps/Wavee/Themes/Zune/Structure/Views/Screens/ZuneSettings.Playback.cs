// ── Themes/Zune/Structure/Views/Screens/ZuneSettings.Playback.cs — the Playback tab (themes plan 6.3, Z8b) ──────────────────────────────────────
//
// Board Z-Settings-Playback: the runtime (ready / not set up / problem) · Playback (crossfade, autoplay, remember volume) · Audio (quality with the lossless rung, the metered
// cap with its live status line, normalization: the Audio AREA) · Sound (the equalizer and its curve: the Audio area; both it and crossfade are bound to the live DSP) · Video (quality, the metered cap, prepare ahead, the
// attached videos and their manager) · Player bar. The board's "downloads" rows are not built (owner Q29 / NQ4: no offline playback); the audio cache is Storage.
//
// THE LIVE DSP is the verbs'. Every equalizer and crossfade write persists and then pushes the WHOLE state to the live graph (`SettingsVerbs.SetEqBand`,
// `CommitCrossfade`), so the ear follows the curve while it is dragged; this view keeps only the mirrors of its frozen controls.

using System.Globalization;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using static FluentGpu.Dsl.Ui;
using Tab = Wavee.Settings.Tab;
using Z = Wavee.Themes.Zune.ZuneSettingsRules;

namespace Wavee.Themes.Zune;

internal static partial class ZuneSettings
{
    static readonly Func<string, string> s_locGet = Loc.Get;

    /// <summary>The parsed gain vector, re-parsed only when the stored string moves: a stable array keeps the curve's props record equal across an unrelated re-render.</summary>
    static string? s_eqGainsRaw;
    static float[] s_eqGains = new float[Settings.Eq.BandCount];
    static readonly Action<int, float> s_onEqBand = static (band, gain) => { if (SettingsVerbs.SetEqBand(band, gain)) Settings.Bump(); };

    static Named[] PlaybackTab(ZuneKit k)
    {
        var facts = SettingsFacts.Live;
        var quality = D(Tab.Playback, "audioQuality");
        var metered = D(Tab.Playback, "meteredQuality");
        var normMode = D(Tab.Playback, "normalizationMode");
        var equalizer = D(Tab.Playback, "equalizer");
        var preset = D(Tab.Playback, "eqPreset");
        var crossfade = D(Tab.Playback, "crossfade");
        var duration = D(Tab.Playback, "crossfadeDuration");
        bool eqOn = facts.EqualizerEnabled, crossOn = facts.CrossfadeEnabled;
        int qualityRungs = Settings.Quality.AudioRungCount(facts.CanDerive);
        int meteredRungs = Settings.Quality.MeteredRungCount(facts.CanDerive);
        int presetAt = Math.Clamp(preset.Current is int p ? p : 0, 0, Settings.Eq.PresetIds.Length - 1);
        string[] presetDescriptions = EqPresetDescriptions();

        var videoQuality = D(Tab.Playback, "videoQuality");
        var videoMetered = D(Tab.Playback, "videoMetered");

        return
        [
            new Named("Runtime", Embed.Comp(static () => new RuntimeBlock()) with { Key = "zs:runtime" }),

            // The board's playback section: what the queue does by itself. It is the area's own heading's body, so it has no heading of its own.
            Bare(k, Tab.Playback, "Playback",
                k.DocRow(crossfade, k.Toggle(crossfade)),
                k.DocRow(duration, k.Slide("crossfadeDuration", new ZuneSlide(0f, (float)Settings.Quality.CrossfadeMaxSeconds, Z.CrossfadeStep,
                    () => (float)SettingsFacts.CrossfadeSeconds(),
                    v => { if (SettingsVerbs.CommitCrossfade(v)) Settings.Bump(); },
                    static v => v.ToString("0.#", CultureInfo.InvariantCulture) + " s",
                    () => SettingsFacts.Live.CrossfadeEnabled)), null, crossOn),
                ToggleRow(k, Tab.Playback, "autoplay"), ToggleRow(k, Tab.Playback, "rememberVolume")),

            // Its title is the Audio area's own heading.
            Bare(k, Tab.Playback, "Audio",
                k.DocRow(quality, k.Choice(quality, QualityLabels(qualityRungs), chips: false, descriptions: QualityDescriptions(qualityRungs), width: Z.WideComboWidth),
                    QualityHint(quality, QualityDescriptions(qualityRungs))),
                // The description IS the detector's verdict: every state says something, "Unknown" included (never blank).
                k.DocRow(metered, k.Choice(metered.Id, new ZuneChoice(QualityLabels(meteredRungs),
                    () => Platform.Network.MeteredQualityCap.Value,
                    i => { if (metered.Writer.Choose!(i)) Settings.Bump(); },   // persist, then publish
                    Chips: false, Descriptions: QualityDescriptions(meteredRungs), Width: Z.WideComboWidth)), MeteredStatus(quality)),
                ToggleRow(k, Tab.Playback, "normalization"),
                k.DocRow(normMode, k.Choice(normMode,
                    [Loc.Get(Strings.Settings.Playback.NormQuiet), Loc.Get(Strings.Settings.Playback.NormNormal), Loc.Get(Strings.Settings.Playback.NormLoud)],
                    chips: true)),
                ToggleRow(k, Tab.Playback, "normalizationAlbum")),

            Group(k, Tab.Playback, "Sound",
                k.DocRow(equalizer, k.Toggle(equalizer)),
                k.DocRow(preset, k.Choice(preset, EqPresetLabels(), chips: false, descriptions: presetDescriptions, width: Z.ComboWidth, enabled: eqOn), presetDescriptions[presetAt], eqOn),
                EqCurveRow(k, eqOn)),

            Group(k, Tab.Playback, "Video",
                k.DocRow(videoQuality, k.Choice(videoQuality, [Loc.Get(Strings.Settings.Playback.VideoQualityAuto), "180p", "240p", "320p", "480p", "720p", "1080p"],
                    chips: false, width: Z.WideComboWidth)),
                k.DocRow(videoMetered, k.Choice(videoMetered, [Loc.Get(Strings.Settings.Playback.VideoQualityUnlimited), "480p", "720p", "1080p"], chips: false, width: Z.WideComboWidth)),
                ToggleRow(k, Tab.Playback, "videoPrepareAhead"),
                Embed.Comp(static () => new VideoOverridesRow()) with { Key = "zs:video-overrides" }),

            Group(k, Tab.Playback, "Player bar", ToggleRow(k, Tab.Playback, "playerBarRemaining")),
        ];
    }

    // ── audio ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

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
    static string QualityHint(SettingsRow row, string[] lines)
    {
        string own = row.DescriptionKey(SettingsFacts.Live) is { } key ? Loc.Get(key) : "";
        if (lines.Length == 0) return own;
        string picked = lines[Math.Clamp(row.Current is int at ? at : 0, 0, lines.Length - 1)];
        return own.Length == 0 ? picked : own + " · " + picked;
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
    static string MeteredStatus(SettingsRow quality)
    {
        bool capInEffect = Platform.Network.MeteredQualityCap.Value < (quality.Current is int q ? q : 0);
        return Settings.MeteredStatusLine.For(Platform.Network.Cost.Value, capInEffect).Render(s_locGet);
    }

    // ── sound ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

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

    /// <summary>The curve row: the draggable ten-band curve over the row's full width, and "reset curve to flat" under it. The row stacks at every tier (the curve
    /// fills the lane it is given and measures it).</summary>
    static Element EqCurveRow(ZuneKit k, bool eqOn)
    {
        var reset = D(Tab.Playback, "eqReset");
        return new BoxEl
        {
            Key = "zs:row:eq-curve", Direction = 1, Gap = Z.RowStackGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Opacity = eqOn ? 1f : Z.DisabledOpacity,
            Children =
            [
                ZuneText.Label(Loc.Get(Strings.Settings.Sound.Curve)),
                ZuneText.Hint(Loc.Get(eqOn ? Strings.Settings.Sound.CurveOn : Strings.Settings.Sound.CurveOff)),
                new BoxEl
                {
                    Direction = 1, Gap = Z.ControlGap, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
                    Children =
                    [
                        Controls.EqualizerCurve(EqGains(), s_onEqBand, eqOn),
                        new BoxEl
                        {
                            Direction = 0, Justify = FlexJustify.End,
                            Children = [k.Cmd(Loc.Get(Strings.Settings.Sound.ResetCurve), () => { if (reset.Writer.Run!()) Settings.Bump(); }, eqOn)],
                        },
                    ],
                },
            ],
        };
    }

    // ── the runtime ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Three shapes over `Setup.Runtime.Status`: never a skeleton, never blank. Ready (the facts and the doors), not set up (an informational bar with the
    /// setup action) and a problem (an error bar with the diagnostics doors and a retry). Nothing known yet and a runtime never set up are the same bar.</summary>
    sealed class RuntimeBlock : ZuneScreenComponent
    {
        public override Element Render()
        {
            var k = new ZuneKit(UseLayout());
            var facts = Setup.Runtime.Status.Value;
            Element Door(string text, Tab tab, string id) => k.Cmd(text, () => D(tab, id).Writer.Run!());

            if (facts.IsReady)
            {
                string version = facts.PackId is { Length: > 0 } pack ? facts.Version + " (" + pack + ")" : facts.Version ?? "";
                string signature = Setup.RuntimeSignatureSummary(facts);
                bool canView = facts.Signature is { FilePath: { Length: > 0 } path } && File.Exists(path);
                return Group(k, "zs:runtime:ready", Loc.Get(Strings.Settings.Common.Ready), Loc.Get(Strings.Settings.Playback.RuntimeReadySub),
                    k.Fact("zs:row:runtime-version", Loc.Get(Strings.Playback.Runtime.DetailVersion), version),
                    k.Fact("zs:row:runtime-arch", Loc.Get(Strings.Playback.Runtime.DetailArch), facts.Arch),
                    k.Row("zs:row:runtime-signature", Loc.Get(Strings.Playback.Runtime.DetailSignature), null,
                        new BoxEl
                        {
                            Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, Wrap = true, MinWidth = 0f,
                            Children =
                            [
                                R.Type.DenseMeta(Z.OrDash(signature)) with { Color = Tok.TextPrimary, MinWidth = 0f, Wrap = TextWrap.Wrap },
                                canView ? Door(Loc.Get(Strings.Playback.Runtime.ViewSignature), Tab.Playback, "runtimeSignature") : new BoxEl(),
                            ],
                        }),
                    k.Fact("zs:row:runtime-location", Loc.Get(Strings.Playback.Runtime.DetailLocation), facts.Location),
                    k.Row("zs:row:runtime-doors", Loc.Get("settings.playback.runtimeDiagnostics"), null,
                        new BoxEl
                        {
                            Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, Wrap = true, MinWidth = 0f,
                            Children =
                            [
                                Door(Loc.Get(Strings.Playback.Runtime.ViewDiagnostics), Tab.PrivacyDiagnostics, "playbackRuntime"),
                                Door(Loc.Get(Strings.Nav.ConnectDiagnostics), Tab.Devices, "connectDiagnostics"),
                                Door(Loc.Get(Strings.Settings.Common.Manage), Tab.Playback, "runtimeSetup"),
                            ],
                        }));
            }

            if (facts.Issue is Setup.RuntimeIssue.None or Setup.RuntimeIssue.Missing)
                return ZuneKit.Notice(InfoBarSeverity.Informational,
                    Loc.Get(Strings.Settings.Playback.RuntimeNotSetUp), Loc.Get(Strings.Settings.Playback.RuntimeNotSetUpSub),
                    k.Cmd(Loc.Get(Strings.Playback.Runtime.SetUp), static () => D(Tab.Playback, "runtimeSetup").Writer.Run!(), accent: true))
                    with { Key = "zs:runtime:setup" };

            // The typed issue's own sentence: the banner never shows one static line and drops the reason.
            return ZuneKit.Notice(InfoBarSeverity.Error,
                Loc.Get(Strings.Settings.Common.Problem), Loc.Get(Setup.RuntimeRules.BannerLocKey(facts.Issue)),
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, Wrap = true,
                    Children =
                    [
                        Door(Loc.Get(Strings.Playback.Runtime.ViewDiagnostics), Tab.PrivacyDiagnostics, "playbackRuntime"),
                        Door(Loc.Get(Strings.Nav.ConnectDiagnostics), Tab.Devices, "connectDiagnostics"),
                        k.Cmd(Loc.Get(Strings.Settings.Common.RetrySetup), static () => D(Tab.Playback, "runtimeSetup").Writer.Run!(), accent: true),
                    ],
                }) with { Key = "zs:runtime:problem" };
        }
    }

    // ── the attached videos ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The attached-videos summary: no roster store, the whole row greyed with no control; none attached, "no videos attached yet" and Manage; some, the
    /// count, Manage and Remove all. The roster is warm and synchronous, so there is no cold-load state, and the Manage wrapper never swaps out under an open flyout.</summary>
    sealed class VideoOverridesRow : ZuneScreenComponent
    {
        NodeHandle _anchor = NodeHandle.Null;
        OverlayHandle? _handle;

        public override Element Render()
        {
            var k = new ZuneKit(UseLayout());
            UseEffect(() => (Action?)(() => { if (_handle is { IsOpen: true } open) open.Close(); _handle = null; }), DepKey.Empty);
            UseActivation(onDeactivated: () => { if (_handle is { IsOpen: true } open) open.Close(); _handle = null; });
            _ = Video.Overrides.Epoch.Value;   // an attach or a remove anywhere (the track menu, an undo toast, the manager) re-reads
            string header = Loc.Get(Strings.VideoOverride.SettingsHeader);
            string sub = Loc.Get(Strings.VideoOverride.SettingsSub);   // the trust disclosure: device-wide, linked not copied
            if (!Video.Overrides.Present)
            {
                _anchor = NodeHandle.Null;
                return k.Row("zs:row:video-overrides", header, sub, null, enabled: false);
            }

            int count = Video.Overrides.Count;
            var controls = new List<Element>(3)
            {
                ZuneText.Meta(count > 0 ? Strings.VideoOverride.SettingsCount(count) : Loc.Get(Strings.VideoOverride.SettingsEmpty)) with { Shrink = 0f },
                new BoxEl
                {
                    Direction = 0, Shrink = 0f, OnRealized = h => _anchor = h,   // the realized wrapper is the anchor
                    Children = [k.Cmd(Loc.Get(Strings.VideoOverride.Manage), Toggle)],
                },
            };
            // Bulk detach is the ONE place a confirm earns its keep here: N links at once, no per-row undo.
            if (count > 0)
                controls.Add(k.Cmd(Loc.Get(Strings.VideoOverride.ClearAll), static () =>
                {
                    var clear = D(Tab.Playback, "videoOverrides");
                    ConfirmThen(Loc.Get(clear.Confirm!.TitleKey), Loc.Get(clear.Confirm.BodyKey), Loc.Get(clear.Confirm.PrimaryKey), () => clear.Writer.Run!());
                }));
            return k.Row("zs:row:video-overrides", header, sub,
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, Wrap = true, MinWidth = 0f, Children = controls.ToArray() });
        }

        /// <summary>"Manage" is a TOGGLE: a second press closes the flyout.</summary>
        void Toggle()
        {
            if (SettingsContext.Overlay is not { } overlay || !Video.Overrides.Present) return;
            if (_handle is { IsOpen: true } open) { open.Close(); return; }
            var handle = overlay.Open(() => _anchor, static () => Embed.Comp(static () => new VideoOverrideManager()), FlyoutPlacement.BottomEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup) { ConstrainToRootBounds = true });
            _handle = handle;
            handle.ClosedAction = () => { if (ReferenceEquals(_handle, handle)) _handle = null; };
        }
    }

    /// <summary>The manager flyout's body: the whole roster, each row with its repair verbs (replace, locate, show in explorer, remove). Presentational over
    /// `Video.OverrideUx` (every decision) and `Video.Overrides` (the warm roster); rebuilt only when the roster's epoch bumps.</summary>
    sealed class VideoOverrideManager : ZuneScreenComponent
    {
        int _epoch = -1;
        IReadOnlyList<Video.OverrideRow> _rows = [];

        public override Element Render()
        {
            var k = new ZuneKit(UseLayout());
            int epoch = Video.Overrides.Epoch.Value;
            if (epoch != _epoch)
            {
                _epoch = epoch;
                _rows = Video.OverrideUx.BuildRoster(Video.Overrides.All(), Video.Overrides.DecideRecord, Video.Overrides.DirectoryExists,
                    SettingsFacts.VideoOverrideTitle, SettingsFacts.VideoOverrideArtistLine);
            }
            Element list = _rows.Count == 0
                ? ZuneText.Prose(Loc.Get(Strings.VideoOverride.SettingsEmpty))
                : new ScrollEl
                {
                    ContentSized = true, MaxHeight = Z.ManagerMaxHeight,
                    Content = new BoxEl { Direction = 1, Gap = Z.ManagerRowGap, Children = Rows(k) },
                };
            return new BoxEl
            {
                Key = "zs:vo-manager", Direction = 1, Width = Z.ManagerWidth, Gap = Z.ControlGap, Padding = Edges4.All(Spacing.M), ClipToBounds = true,
                Children =
                [
                    ZuneText.Group(Loc.Get(Strings.VideoOverride.SettingsHeader)) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ZuneText.Meta(Strings.VideoOverride.SettingsCount(_rows.Count)),
                    list,
                ],
            };
        }

        Element[] Rows(ZuneKit k)
        {
            var kids = new Element[_rows.Count];
            for (int i = 0; i < kids.Length; i++) kids[i] = Row(k, _rows[i]);
            return kids;
        }

        /// <summary>The full row: title and status, the FULL path (it answers "is this the file I meant?") and the repair verbs. Replace and remove apply at once with a
        /// toast-undo, never a dialog.</summary>
        static Element Row(ZuneKit k, Video.OverrideRow row)
        {
            string sub = row.Subtitle is { Length: > 0 } artists ? artists + "  ·  " + row.Path : row.Path;
            string uri = row.Uri, path = row.Path, sourceKey = row.Override.SourceKey;
            var actions = new List<Element>(4)
            {
                k.Cmd(Loc.Get(Strings.VideoOverride.Replace), () => SettingsVerbs.PickVideoOverride(uri, sourceKey, Loc.Get(Strings.VideoOverride.PickTitle), null)),
            };
            if (row.CanLocate)
                actions.Add(k.Cmd(Loc.Get(Strings.VideoOverride.Locate), () =>
                {
                    string? start = Video.OverrideUx.NearestExistingAncestor(path, Video.Overrides.DirectoryExists);
                    SettingsVerbs.PickVideoOverride(uri, sourceKey, Loc.Get(Strings.VideoOverride.LocateTitle), start);
                }));
            if (row.CanReveal) actions.Add(k.Cmd(Loc.Get(Strings.VideoOverride.ShowInExplorer), () => SettingsVerbs.RevealInExplorer(path)));
            actions.Add(k.Cmd(Loc.Get(Strings.VideoOverride.Remove), () => SettingsVerbs.RemoveVideoOverride(uri, path, sourceKey)));
            return new BoxEl
            {
                Key = "zs:vo-row:" + uri, Direction = 1, Gap = Z.ManagerRowGap, MinWidth = 0f, Padding = Edges4.All(Spacing.S), Fill = Tok.FillSubtleSecondary,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
                        Children =
                        [
                            R.Type.TrackTitle(row.Title) with { Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                            ZuneText.Meta(StatusWord(row.Status)) with { Shrink = 0f, Color = StatusInk(row.Status) },
                        ],
                    },
                    ZuneText.Hint(sub) with { MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
                    new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f, Wrap = true, Children = actions.ToArray() },
                ],
            };
        }

        static string StatusWord(Video.OverrideStatus status) => Loc.Get(status switch
        {
            Video.OverrideStatus.Missing => Strings.VideoOverride.StatusMissing,
            Video.OverrideStatus.DriveOffline => Strings.VideoOverride.StatusDriveOffline,
            Video.OverrideStatus.Unplayable => Strings.VideoOverride.StatusUnplayable,
            _ => Strings.VideoOverride.StatusOk,
        });

        /// <summary>Ok is deliberately quiet; the two repairable states are caution; only a file that failed to play is critical.</summary>
        static ColorF StatusInk(Video.OverrideStatus status) => status switch
        {
            Video.OverrideStatus.Missing or Video.OverrideStatus.DriveOffline => Tok.SystemFillCaution,
            Video.OverrideStatus.Unplayable => Tok.SystemFillCritical,
            _ => Tok.TextSecondary,
        };
    }
}
