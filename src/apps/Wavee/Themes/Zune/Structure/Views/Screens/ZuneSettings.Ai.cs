// ── Themes/Zune/Structure/Views/Screens/ZuneSettings.Ai.cs — the on-device AI lyrics rows (themes plan 6.3, Z8b) ────────────────────────────────
//
// Appearance › Lyrics, after the blur row. ONE component bound to `AiLyrics.Current`: it only reads signals and calls the module's verbs (no file, no task, no
// wait). The master switch is the module's own verb and the three behaviour switches are the document's rows (`aiWordSync`, `aiPlainText`, `aiBatterySaver`);
// the per-language and per-file verbs stay the module's (`InstallLanguage`, `RemoveLanguage`, `OpenFolder`, `RemoveFiles`).
//
// The phase decides the main row's words and controls: off / unavailable (the switch alone, greyed with its reason), the one-time download offer, downloading
// (the live line, the bar, pause and cancel), paused, preparing (the first compile, by weight), ready (the switch on, then the three behaviour switches, the
// languages and the files) and the failure. The rows of one phase are the same two nodes whatever the phase (the main row and the extras below it), so a phase
// change re-words the row in place and the ready extras come and go below it: nothing above or beside them moves.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using static FluentGpu.Dsl.Ui;
using AiS = Wavee.Strings.Settings.Lyrics.Ai;
using Tab = Wavee.Settings.Tab;
using Z = Wavee.Themes.Zune.ZuneSettingsRules;

namespace Wavee.Themes.Zune;

internal static partial class ZuneSettings
{
    /// <summary>The AI lyrics rows. Its own component: progress and phase changes re-render only these rows.</summary>
    sealed class AiLyricsRow : ZuneScreenComponent
    {
        static string[]? s_langOrder;
        static long[]? s_langBytes;

        readonly Signal<bool> _removeDeferred = new(false);
        readonly Signal<bool> _details = new(false);

        public override Element Render()
        {
            var k = new ZuneKit(UseLayout());
            // The module paints its status only while this card is visible, so it can stop work nobody is watching.
            UseEffect(static () =>
            {
                AiLyrics.SettingsCardVisible = true;
                return (Action?)(static () => AiLyrics.SettingsCardVisible = false);
            }, DepKey.Empty);
            UseActivation(onActivated: static () => AiLyrics.SettingsCardVisible = true, onDeactivated: static () => AiLyrics.SettingsCardVisible = false);
            _ = SettingsFacts.AiLyricsEpoch.Value;
            var st = AiLyrics.Current.Value;
            bool unknown = st == AiLyrics.Status.Unknown;
            var kids = new List<Element>(8);

            switch (st.Phase)
            {
                case AiLyrics.SetupPhase.NeedsSetup:
                    kids.Add(MainRow(k, Loc.Get(AiS.Setup.Title), AiS.Setup.Sub(AiLyrics.Rules.FormatBytes(st.PendingDownloadBytes)),
                        k.Cmd(Loc.Get(AiS.Setup.Action), () => DownloadThen(st.PendingDownloadBytes, static () => AiLyrics.StartDownload()), accent: true),
                        Master(k, true, true)));
                    break;
                case AiLyrics.SetupPhase.Downloading:
                    kids.Add(MainRow(k, st.Download.Label.Length > 0 ? AiS.Downloading.Title(st.Download.Label) : Loc.Get(AiS.Setup.Title), DownloadLine(st),
                        DownloadBar(st), k.Cmd(Loc.Get(AiS.Pause), static () => AiLyrics.PauseDownload()),
                        k.Cmd(Loc.Get(AiS.Cancel), static () => AiLyrics.CancelDownload())));
                    break;
                case AiLyrics.SetupPhase.Paused:
                    kids.Add(MainRow(k, Loc.Get(AiS.Paused.Title), AiS.Paused.Sub(AiLyrics.Rules.FormatBytes(st.Download.Done), AiLyrics.Rules.FormatBytes(st.Download.Total)),
                        ZuneKit.Bar(Fraction(st.Download.Done, st.Download.Total), Z.AiBarWidth, ProgressBarState.Paused),
                        k.Cmd(Loc.Get(AiS.Resume), () => DownloadThen(Remaining(st), static () => AiLyrics.ResumeDownload()), accent: true),
                        k.Cmd(Loc.Get(AiS.Cancel), static () => AiLyrics.CancelDownload())));
                    break;
                case AiLyrics.SetupPhase.Preparing:
                    kids.Add(MainRow(k, Loc.Get(AiS.Preparing.Title), PrepareLine(st), PrepareBar(st)));
                    break;
                case AiLyrics.SetupPhase.Ready:
                    kids.Add(MainRow(k, Loc.Get(AiS.Title),
                        st.NpuName.Length > 0 ? Loc.Get(AiS.Ready.Title) + " · " + AiS.Ready.Sub(st.NpuName, st.NpuDriver) : Loc.Get(AiS.Ready.Title),
                        Master(k, true, true)));
                    kids.Add(k.DocRow(D(Tab.Appearance, "aiWordSync"), k.Toggle(D(Tab.Appearance, "aiWordSync")), Loc.Get(AiS.WordSyncSub)));
                    kids.Add(k.DocRow(D(Tab.Appearance, "aiPlainText"), k.Toggle(D(Tab.Appearance, "aiPlainText")), Loc.Get(AiS.PlainTextSub)));
                    kids.Add(k.DocRow(D(Tab.Appearance, "aiBatterySaver"), k.Toggle(D(Tab.Appearance, "aiBatterySaver")), Loc.Get(AiS.BatterySaverSub)));
                    kids.Add(k.Row("zs:row:ai-languages", Loc.Get(AiS.Languages), AiS.LanguagesSub(AiLyrics.Rules.FormatBytes(AverageLanguageBytes())),
                        Languages(k, st.Languages)));
                    long installed = st.InstalledBytes;
                    kids.Add(k.Row("zs:row:ai-files", Loc.Get(AiS.Files), AiS.FilesSub(AiLyrics.Rules.FormatBytes(installed)),
                        new BoxEl
                        {
                            Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, Wrap = true, MinWidth = 0f,
                            Children =
                            [
                                k.Cmd(Loc.Get(AiS.OpenFolder), static () => AiLyrics.OpenFolder()),
                                k.Cmd(Loc.Get(AiS.Remove), () => ConfirmRemove(installed)),
                            ],
                        }));
                    break;
                case AiLyrics.SetupPhase.Error:
                    kids.Add(MainRow(k, Loc.Get(AiS.Title), Loc.Get(AiS.Sub), Master(k, true, true)));
                    kids.Add(ErrorBar(k, st));
                    break;
                default:
                    // Unknown (before the boot scan), Unavailable and Off share one shape: the switch only changes state.
                    bool unavailable = unknown || st.Phase == AiLyrics.SetupPhase.Unavailable;
                    kids.Add(MainRow(k, Loc.Get(AiS.Title),
                        unavailable ? Loc.Get(unknown ? AiS.Sub : AiLyrics.Rules.UnavailableKey(st.Availability)) : Loc.Get(AiS.Sub),
                        Master(k, !unavailable && st.Enabled, !unavailable)));
                    break;
            }

            if (_removeDeferred.Value && st.Phase is AiLyrics.SetupPhase.Off or AiLyrics.SetupPhase.NeedsSetup)
                kids.Add(ZuneKit.Notice(InfoBarSeverity.Informational, "", Loc.Get(AiS.RemoveDeferred)) with { Key = "zs:ai:deferred" });

            return new BoxEl
            {
                Key = "zs:ai:rows", Direction = 1, Gap = Z.RowGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Animate = ZuneMotion.ColumnCollapse,
                Children = kids.ToArray(),
            };
        }

        /// <summary>The main row: ONE node in every phase (its words and controls change).</summary>
        static Element MainRow(ZuneKit k, string title, string? hint, params Element[] controls)
            => k.Row("zs:row:ai", title, hint, new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, Wrap = true, MinWidth = 0f, Children = controls,
            });

        /// <summary>The master switch: the module's own verb. A fresh signal per render carries the live value.</summary>
        static Element Master(ZuneKit k, bool on, bool enabled)
            => k.Switch(on, enabled ? static v => AiLyrics.SetEnabled(v) : null, enabled);

        Element ErrorBar(ZuneKit k, in AiLyrics.Status st)
        {
            var error = st.Error;
            string detail = Detail(st.ErrorDetail);
            long installed = st.InstalledBytes;
            var actions = new List<Element>(3);
            if (detail.Length > 0) actions.Add(k.Cmd(Loc.Get(AiS.Details), () => _details.Value = !_details.Peek()));
            if (AiLyrics.Rules.ErrorOffersRemove(error)) actions.Add(k.Cmd(Loc.Get(AiS.Remove), () => ConfirmRemove(installed)));
            if (AiLyrics.Rules.ErrorOffersRetry(error))
                actions.Add(k.Cmd(Loc.Get(AiS.Retry), static () =>
                {
                    // a download error downloads again: the same metered question as Download (a model error does not)
                    if (AiLyrics.Rules.RetryLoadsModels(AiLyrics.Current.Peek().Error)) AiLyrics.Retry();
                    else DownloadThen(Remaining(AiLyrics.Current.Peek()), static () => AiLyrics.Retry());
                }, accent: true));
            return ZuneKit.Notice(InfoBarSeverity.Error, Loc.Get(AiLyrics.Rules.ErrorKey(error)),
                _details.Value && detail.Length > 0 ? AiS.Error.Detail(detail) : "",
                actions.Count == 0 ? null : new BoxEl { Direction = 0, Gap = Z.ControlGap, AlignItems = FlexAlign.Center, Children = actions.ToArray() })
                with { Key = "zs:ai:error" };
        }

        Element DownloadBar(in AiLyrics.Status st)
            => Indeterminate(st.Download) ? ProgressBar.Indeterminate(Z.AiBarWidth) : ZuneKit.Bar(Fraction(st.Download.Done, st.Download.Total), Z.AiBarWidth);

        static Element PrepareBar(in AiLyrics.Status st)
            => st.Prepare.Total > 0
                ? ZuneKit.Bar((float)AiLyrics.Rules.PrepareFraction(st.Prepare.WeightDone, st.Prepare.WeightTotal, st.Prepare.Done, st.Prepare.Total), Z.AiBarWidth)
                : ProgressBar.Indeterminate(Z.AiBarWidth);

        static string PrepareLine(in AiLyrics.Status st)
            => st.Prepare.Finishing
                ? Loc.Get(AiS.Preparing.Finishing)
                : st.Prepare.Recompile
                    ? Loc.Get(AiS.Preparing.Again) + " " + AiS.Preparing.Sub(st.Prepare.Done, st.Prepare.Total)
                    : AiS.Preparing.Sub(st.Prepare.Done, st.Prepare.Total);

        // ── languages ────────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>One line per aligner language in the pack: name, Installed or its size, and Download / Remove. The last installed language cannot be removed.</summary>
        Element Languages(ZuneKit k, IReadOnlyList<string> installed)
        {
            EnsureLanguages();
            var order = s_langOrder!;
            var bytes = s_langBytes!;
            var rows = new Element[order.Length];
            for (int i = 0; i < order.Length; i++)
            {
                string lang = order[i];
                long size = bytes[i];
                bool have = Contains(installed, lang);
                Element status = have
                    ? ZuneText.Meta(Loc.Get(AiS.Lang.Installed)) with { Color = Tok.AccentDefault }
                    : ZuneText.Meta(AiLyrics.Rules.FormatBytes(size));
                Element action = have
                    ? installed.Count > 1 ? k.Cmd(Loc.Get(AiS.Lang.Remove), () => AiLyrics.RemoveLanguage(lang)) : new BoxEl()
                    : k.Cmd(Loc.Get(AiS.Lang.Download), () => DownloadThen(size, () => AiLyrics.InstallLanguage(lang)), accent: true);
                rows[i] = new BoxEl
                {
                    Key = "zs:ai:lang:" + lang, Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, MinHeight = Z.AiLanguageRowHeight,
                    Children =
                    [
                        new BoxEl { Width = Z.AiLanguageNameWidth, Shrink = 0f, Children = [ZuneText.Label(LanguageName(lang)) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis }] },
                        new BoxEl { Width = Z.AiLanguageStatusWidth, Shrink = 0f, Children = [status] },
                        action,
                    ],
                };
            }
            return new BoxEl { Direction = 1, Gap = Z.AiLanguageGap, MinWidth = 0f, Children = rows };
        }

        static string LanguageName(string lang) => lang switch
        {
            "en" => Loc.Get(AiS.Lang.En),
            "es" => Loc.Get(AiS.Lang.Es),
            "nl" => Loc.Get(AiS.Lang.Nl),
            "ko" => Loc.Get(AiS.Lang.Ko),
            _ => lang.ToUpperInvariant(),
        };

        static bool Contains(IReadOnlyList<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>The pack's languages (English, Spanish, Dutch, Korean, then any other in code order) and each one's download size, read once from the embedded manifest.</summary>
        static void EnsureLanguages()
        {
            if (s_langOrder is not null) return;
            var langs = AiLyrics.PackManifest.Embedded.Languages;
            var order = new List<string>(langs.Count);
            foreach (string code in Z.AiLanguageOrder)
                if (langs.ContainsKey(code)) order.Add(code);
            var rest = new List<string>();
            foreach (var key in langs.Keys)
                if (!Contains(order, key)) rest.Add(key.ToLowerInvariant());
            rest.Sort(StringComparer.Ordinal);
            order.AddRange(rest);
            var bytes = new long[order.Count];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = AiLyrics.Pack.DownloadBytes(langs[order[i]]);
            s_langBytes = bytes;
            s_langOrder = order.ToArray();
        }

        static long AverageLanguageBytes()
        {
            EnsureLanguages();
            var bytes = s_langBytes!;
            if (bytes.Length == 0) return 0;
            long sum = 0;
            for (int i = 0; i < bytes.Length; i++) sum += bytes[i];
            return sum / bytes.Length;
        }

        // ── verbs with a question first ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>A download above the metered threshold asks first (the ONE confirm shape, Cancel the default).</summary>
        static void DownloadThen(long bytes, Action start)
        {
            if (AiLyrics.Rules.NeedsMeteredConfirm(Platform.Network.IsMetered, bytes))
                ConfirmThen(Loc.Get(AiS.Metered.Title), AiS.Metered.Body(AiLyrics.Rules.FormatBytes(bytes)), Loc.Get(AiS.Metered.Confirm), start);
            else
                start();
        }

        /// <summary>Remove confirms; files still in use go at the next start and the rows say so.</summary>
        void ConfirmRemove(long bytes)
            => ConfirmThen(Loc.Get(AiS.RemoveConfirm.Title), AiS.RemoveConfirm.Body(AiLyrics.Rules.FormatBytes(bytes)), Loc.Get(AiS.RemoveConfirm.Action),
                () => AiLyrics.RemoveFiles(deferred => { if (deferred) _removeDeferred.Value = true; }));

        /// <summary>What a resumed download still fetches: the paused progress when there is one, else the pending total.</summary>
        static long Remaining(in AiLyrics.Status st)
        {
            long left = st.Download.Total - st.Download.Done;
            return left > 0 ? left : st.PendingDownloadBytes;
        }

        // ── pure helpers ─────────────────────────────────────────────────────────────────────────────────────────────────

        static bool Indeterminate(in AiLyrics.DownloadProgress d)
            => d.Phase is AiLyrics.DownloadPhase.Checking or AiLyrics.DownloadPhase.Verifying || d.Total <= 0;

        static float Fraction(long done, long total) => total > 0 ? (float)Math.Clamp((double)done / total, 0, 1) : 0f;

        /// <summary>"12.3 / 84.0 MB · 4.2 MB/s · 18 s left", or the step's own word while it checks, verifies or installs.</summary>
        static string DownloadLine(in AiLyrics.Status s)
        {
            var d = s.Download;
            switch (d.Phase)
            {
                case AiLyrics.DownloadPhase.Checking: return Loc.Get(AiS.Checking);
                case AiLyrics.DownloadPhase.Verifying: return Loc.Get(AiS.Verifying);
                case AiLyrics.DownloadPhase.Installing: return Loc.Get(AiS.Installing);
            }
            string metric = AiS.Downloading.Metric(AiLyrics.Rules.FormatBytes(d.Done), AiLyrics.Rules.FormatBytes(d.Total),
                AiLyrics.Rules.FormatBytes((long)Math.Max(0, d.BytesPerSecond)));
            string etaKey = AiLyrics.Rules.EtaKey(AiLyrics.Rules.EtaSeconds(d.Done, d.Total, d.BytesPerSecond), out int n);
            return metric + " · " + (n > 0 ? Loc.Format(etaKey, ("n", n)) : Loc.Get(etaKey));
        }

        /// <summary>The host's raw detail ("1.2 GB|C:\" for a full disk) as one readable line.</summary>
        static string Detail(string raw)
            => string.IsNullOrWhiteSpace(raw) ? "" : string.Join(" · ", raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
