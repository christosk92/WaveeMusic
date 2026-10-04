// ── AiLyrics/AiLyrics.UI.Settings.cs ─────────────────────────────────────────────────────────────────────────────────
// AiLyrics.SettingsCard, Settings.AiLyricsCardView (the Appearance › Lyrics card for on-device AI lyrics)
//
// Role: UI (plan docs/plans/wavee/ai-lyrics-sync-implementation.md §4.3)
//
// ONE component bound to AiLyrics.Current. It only reads signals and calls the host's verbs: no file, no task, no wait.
// The view is nested in `Settings` so it builds with the tab's own row grammar (Item, RowGlyph, ConfirmThen, CardSpacing)
// and the catalog's glyphs; the mount point is `AiLyrics.SettingsCard()`.
//
// THREE RATES, THREE BINDINGS (a 10 Hz download never re-renders the page, and never even re-renders this card):
//   • the card's SHAPE (phase, error, sizes, the file label…) is a UseComputed over Current: an equality-gated memo, so
//     the card re-renders only when what it draws as structure changes;
//   • the bars bind FloatSignals written by a passive effect (compositor/relayout only, the fill eased by a reflow
//     PartFill, Setup.UI.cs LoginStepBar precedent);
//   • the "12.3 / 84.0 MB · 4.2 MB/s · 18 s left" line is a TextEl bound to a static Func (relayout of one run).
//
// NO LAYOUT JUMPS: every phase is ONE SettingsCard (Ready: ONE SettingsExpander) in a keyed arm that fades in (Dy 4) and
// out, inside a column whose height reflows (MotionTok.ControlNormal) so the sections below glide instead of jumping.
// While Ready the column drops its reflow: the expander animates its own disclosure, and a second reflow on the parent
// would chase it frame by frame.

using FluentGpu;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;
using AiS = Wavee.Strings.Settings.Lyrics.Ai;

namespace Wavee;

public static partial class AiLyrics
{
    /// <summary>The Settings card (Appearance › Lyrics, right after the blur row). Its own component: progress and phase
    /// changes re-render only the card.</summary>
    public static Element SettingsCard()
        => Embed.Comp(static () => new Settings.AiLyricsCardView()) with { Key = "appearance.ai-lyrics" };
}

public static partial class Settings
{
    /// <summary>The card. Every static it needs lives here, so nothing runs in Settings' own type initializer.</summary>
    internal sealed class AiLyricsCardView : Component
    {
        const float AiBarWidth = 180f;

        // ── stable statics (no per-render delegate or spec allocation) ─────────────────────────────────────────────────

        static readonly EnterExit s_aiEnter = new(Dy: 4f, Opacity: 0f, Active: true);
        static readonly EnterExit s_aiExit = new(Opacity: 0f, Active: true);

        static readonly LayoutTransition s_aiHeightReflow = new(TransitionChannels.Size, MotionTok.ControlNormal.ToDynamics(),
            Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Height);

        /// <summary>The determinate fill eases between progress writes instead of stepping (LoginStepBar precedent).</summary>
        static readonly TemplateParts s_aiFillEase = new()
        {
            [ProgressBar.PartFill] = b => b with
            {
                Layout = new LayoutTransition(TransitionChannels.Size, MotionTok.ControlNormal.ToDynamics(),
                    Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Width),
            },
        };

        /// <summary>The download line, bound live: the card's description re-reads Current on every progress write without
        /// re-rendering anything.</summary>
        static readonly Func<string> s_aiDownloadLine = static () => AiDownloadLine(AiLyrics.Current.Value);

        static readonly TemplateParts s_aiLiveDescription = AiLiveDescription();

        static readonly Func<AiCardShape> s_aiShapeOf = static () => AiCardShape.Of(AiLyrics.Current.Value);

        static readonly Func<Action?> s_aiMarkVisible = static () =>
        {
            AiLyrics.SettingsCardVisible = true;
            return s_aiMarkHidden;
        };
        static readonly Action s_aiMarkHidden = static () => AiLyrics.SettingsCardVisible = false;
        static readonly Action s_aiOnActivated = static () => AiLyrics.SettingsCardVisible = true;

        /// <summary>Some files were in use at Remove and go at the next start: say so until then (or until dismissed).</summary>
        static readonly Signal<bool> s_aiRemoveDeferred = new(false);

        static readonly Action<bool> s_aiSetEnabled = static on => AiLyrics.SetEnabled(on);
        static readonly Action s_aiStartDownload = static () => AiLyrics.StartDownload();
        static readonly Action<bool> s_aiOnRemoved = static deferred => { if (deferred) s_aiRemoveDeferred.Value = true; };
        static readonly Action s_aiDismissDeferred = static () => s_aiRemoveDeferred.Value = false;

        /// <summary>The Ready expander's open state survives a remount (a language download swaps the arm out and back).</summary>
        static readonly Signal<bool> s_aiExpanded = new(false);

        static string[]? s_aiLangOrder;
        static long[]? s_aiLangBytes;

        static TemplateParts AiLiveDescription()
        {
            var parts = new TemplateParts();
            parts.Set<TextEl>(SettingsCard.PartDescription, static t => t with { Text = Prop.Of(s_aiDownloadLine) });
            return parts;
        }

        /// <summary>What the card draws as STRUCTURE. Progress bytes, speed and prepare weights are deliberately absent: they
        /// reach the screen through bindings, so a progress write leaves this value equal and the memo silent.</summary>
        readonly record struct AiCardShape(
            AiLyrics.SetupPhase Phase, bool Unknown, AiLyrics.Availability Availability, bool Enabled,
            string NpuName, string NpuDriver, AiLyrics.SetupError Error, string ErrorDetail,
            long InstalledBytes, IReadOnlyList<string> Languages, long PendingDownloadBytes,
            string DownloadLabel, bool DownloadIndeterminate,
            int PrepareDone, int PrepareTotal, bool Recompile, bool Finishing)
        {
            public static AiCardShape Of(in AiLyrics.Status s) => new(
                s.Phase, s == AiLyrics.Status.Unknown, s.Availability, s.Enabled,
                s.NpuName, s.NpuDriver, s.Error, s.ErrorDetail,
                s.InstalledBytes, s.Languages, s.PendingDownloadBytes,
                s.Phase == AiLyrics.SetupPhase.Downloading ? s.Download.Label : "",
                s.Phase == AiLyrics.SetupPhase.Downloading && AiIndeterminate(s.Download),
                s.Prepare.Done, s.Prepare.Total, s.Prepare.Recompile, s.Prepare.Finishing);
        }

        public override Element Render()
        {
            // UNCONDITIONAL hooks, fixed order.
            UseEffect(s_aiMarkVisible, DepKey.Empty);
            UseActivation(onActivated: s_aiOnActivated, onDeactivated: s_aiMarkHidden);
            var shape = UseComputed(s_aiShapeOf).Value;
            var st0 = AiLyrics.Current.Peek();
            var download = UseFloatSignal(AiDownloadFraction(st0));
            var prepare = UseFloatSignal(AiPrepareFraction(st0));
            UseEffect(() =>
            {
                var s = AiLyrics.Current.Value;
                download.Value = AiDownloadFraction(s);
                prepare.Value = AiPrepareFraction(s);
            });
            var (details, setDetails) = UseState(false);
            _ = Prefs.AiLyrics.Epoch.Value;
            bool deferred = s_aiRemoveDeferred.Value;

            // The full status for the values the shape leaves out (read without subscribing: the shape is the trigger).
            var st = AiLyrics.Current.Peek();

            Element arm = shape.Phase switch
            {
                AiLyrics.SetupPhase.NeedsSetup => AiArm("ai:setup", NeedsSetupCard(st)),
                AiLyrics.SetupPhase.Downloading => AiArm("ai:download", DownloadingCard(shape, st, download)),
                AiLyrics.SetupPhase.Paused => AiArm("ai:paused", PausedCard(st, download)),
                AiLyrics.SetupPhase.Preparing => AiArm("ai:prepare", PreparingCard(shape, prepare)),
                AiLyrics.SetupPhase.Ready => AiArm("ai:ready", ReadyExpander(st)),
                AiLyrics.SetupPhase.Error => AiArm("ai:error", ErrorBar(st, details, setDetails), MasterCard(st, on: true)),
                // Unknown (before the boot scan), Unavailable and Off share one shape: the switch only changes state.
                _ => AiArm("ai:off", MasterCard(st, on: false)),
            };

            bool showDeferred = deferred && shape.Phase is AiLyrics.SetupPhase.Off or AiLyrics.SetupPhase.NeedsSetup;
            var kids = new Element[showDeferred ? 2 : 1];
            kids[0] = arm;
            if (showDeferred)
                kids[1] = AiArm("ai:deferred", InfoBar.Create(InfoBarSeverity.Informational, "", Loc.Get(AiS.RemoveDeferred),
                    onClose: s_aiDismissDeferred, isClosable: true));

            return new BoxEl
            {
                Direction = 1, Gap = CardSpacing, AlignSelf = FlexAlign.Stretch,
                Layout = shape.Phase == AiLyrics.SetupPhase.Ready ? null : (LayoutTransition?)s_aiHeightReflow,
                Children = kids,
            };
        }

        // ── the arms ────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Unknown / Unavailable / Off (switch off; disabled with the reason when the PC cannot run it), and the
        /// card under the Error bar (switch on, so the feature can still be turned off from there).</summary>
        static Element MasterCard(in AiLyrics.Status st, bool on)
        {
            bool unknown = st == AiLyrics.Status.Unknown;
            if (!on && (unknown || st.Phase == AiLyrics.SetupPhase.Unavailable))
                return AiCard(Loc.Get(AiS.Title),
                    Loc.Get(unknown ? AiS.Sub : AiLyrics.Rules.UnavailableKey(st.Availability)),
                    AiSwitch(false, enabled: false));
            return AiCard(Loc.Get(AiS.Title), Loc.Get(AiS.Sub), AiSwitch(on || st.Enabled, enabled: true));
        }

        /// <summary>The one-time download offer: the size, Download (asking first on a metered link), the switch still on.</summary>
        static Element NeedsSetupCard(in AiLyrics.Status st) => AiCard(
            Loc.Get(AiS.Setup.Title),
            AiS.Setup.Sub(AiLyrics.Rules.FormatBytes(st.PendingDownloadBytes)),
            AiRow(
                Button.Accent(Loc.Get(AiS.Setup.Action),
                    static () => AiDownloadThen(AiLyrics.Current.Peek().PendingDownloadBytes, s_aiStartDownload)),
                AiSwitch(true, enabled: true)));

        /// <summary>The file being fetched, the live "done / total · speed/s · eta" line, the bar (sweeping while the host
        /// checks or verifies), Pause and Cancel.</summary>
        static Element DownloadingCard(in AiCardShape shape, in AiLyrics.Status st, FloatSignal fraction)
        {
            Element bar = shape.DownloadIndeterminate
                ? ProgressBar.Indeterminate(AiBarWidth)
                : ProgressBar.Create(fraction, AiBarWidth, ProgressBarState.Normal, s_aiFillEase);
            string header = shape.DownloadLabel.Length > 0
                ? AiS.Downloading.Title(shape.DownloadLabel)
                : Loc.Get(AiS.Setup.Title);
            return AiCard(header, AiDownloadLine(st),
                AiRow(bar,
                    HyperlinkButton.Create(Loc.Get(AiS.Pause), static () => AiLyrics.PauseDownload()),
                    HyperlinkButton.Create(Loc.Get(AiS.Cancel), static () => AiLyrics.CancelDownload())),
                s_aiLiveDescription);
        }

        /// <summary>Paused: what is already here, the bar held at its place in the caution colour, Resume and Cancel.</summary>
        static Element PausedCard(in AiLyrics.Status st, FloatSignal fraction) => AiCard(
            Loc.Get(AiS.Paused.Title),
            AiS.Paused.Sub(AiLyrics.Rules.FormatBytes(st.Download.Done), AiLyrics.Rules.FormatBytes(st.Download.Total)),
            AiRow(
                ProgressBar.Create(fraction, AiBarWidth, ProgressBarState.Paused, s_aiFillEase),
                Button.Standard(Loc.Get(AiS.Resume), static () => AiLyrics.ResumeDownload()),
                HyperlinkButton.Create(Loc.Get(AiS.Cancel), static () => AiLyrics.CancelDownload())));

        /// <summary>The first NPU compile: "x of y" (why, when a stale cache is rebuilt), the bar by compile weight.</summary>
        static Element PreparingCard(in AiCardShape shape, FloatSignal fraction)
        {
            string sub = shape.Finishing
                ? Loc.Get(AiS.Preparing.Finishing)
                : shape.Recompile
                    ? Loc.Get(AiS.Preparing.Again) + " " + AiS.Preparing.Sub(shape.PrepareDone, shape.PrepareTotal)
                    : AiS.Preparing.Sub(shape.PrepareDone, shape.PrepareTotal);
            Element bar = shape.PrepareTotal > 0
                ? ProgressBar.Create(fraction, AiBarWidth, ProgressBarState.Normal, s_aiFillEase)
                : ProgressBar.Indeterminate(AiBarWidth);
            return AiCard(Loc.Get(AiS.Preparing.Title), sub, bar);
        }

        /// <summary>Ready: the switch on, and inside the three behaviour switches, the languages and the files.</summary>
        static Element ReadyExpander(in AiLyrics.Status st)
        {
            string ready = Loc.Get(AiS.Ready.Title);
            string description = st.NpuName.Length > 0
                ? ready + " · " + AiS.Ready.Sub(st.NpuName, st.NpuDriver)
                : ready;
            long installed = st.InstalledBytes;
            return SettingsExpander.Create(new SettingsExpander.Options
            {
                Header = Loc.Get(AiS.Title),
                Description = description,
                HeaderIcon = RowGlyph(Tab.Appearance, "aiLyrics"),
                Content = AiSwitch(true, enabled: true),
                IsExpanded = s_aiExpanded,
                Items =
                [
                    Item(Loc.Get(AiS.WordSync), Loc.Get(AiS.WordSyncSub),
                        AiPrefSwitch(Platform.Keys.AiLyricsWordSync, Prefs.AiLyrics.WordSync()),
                        icon: RowGlyph(Tab.Appearance, "aiWordSync")),
                    Item(Loc.Get(AiS.PlainText), Loc.Get(AiS.PlainTextSub),
                        AiPrefSwitch(Platform.Keys.AiLyricsPlainText, Prefs.AiLyrics.PlainText()),
                        icon: RowGlyph(Tab.Appearance, "aiPlainText")),
                    Item(Loc.Get(AiS.BatterySaver), Loc.Get(AiS.BatterySaverSub),
                        AiPrefSwitch(Platform.Keys.AiLyricsOnBatterySaver, Prefs.AiLyrics.OnBatterySaver()),
                        icon: RowGlyph(Tab.Appearance, "aiBatterySaver")),
                    Item(Loc.Get(AiS.Languages), AiS.LanguagesSub(AiLyrics.Rules.FormatBytes(AiAverageLanguageBytes())),
                        AiLanguages(st.Languages), icon: RowGlyph(Tab.Appearance, "aiLanguages")),
                    Item(Loc.Get(AiS.Files), AiS.FilesSub(AiLyrics.Rules.FormatBytes(installed)),
                        AiRow(
                            HyperlinkButton.Create(Loc.Get(AiS.OpenFolder), static () => AiLyrics.OpenFolder()),
                            Button.Standard(Loc.Get(AiS.Remove), () => AiConfirmRemove(installed))),
                        icon: RowGlyph(Tab.Appearance, "aiFiles")),
                ],
            });
        }

        /// <summary>The failure: its one sentence, the raw detail behind "Details", and the recovery the kind allows.</summary>
        static Element ErrorBar(in AiLyrics.Status st, bool details, Action<bool> setDetails)
        {
            var error = st.Error;
            string detail = AiDetail(st.ErrorDetail);
            long installed = st.InstalledBytes;
            var actions = new List<Element>(3);
            if (detail.Length > 0)
                actions.Add(HyperlinkButton.Create(Loc.Get(AiS.Details), () => setDetails(!details)));
            if (AiLyrics.Rules.ErrorOffersRemove(error))
                actions.Add(Button.Standard(Loc.Get(AiS.Remove), () => AiConfirmRemove(installed)));
            if (AiLyrics.Rules.ErrorOffersRetry(error))
                actions.Add(Button.Accent(Loc.Get(AiS.Retry), static () => AiLyrics.Retry()));
            return InfoBar.Create(InfoBarSeverity.Error,
                Loc.Get(AiLyrics.Rules.ErrorKey(error)),
                details && detail.Length > 0 ? AiS.Error.Detail(detail) : "",
                isClosable: false,
                actionButton: actions.Count == 0 ? null : AiRow(actions.ToArray()));
        }

        // ── small builders ──────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A keyed arm: the phase's one card fades in (rising 4 DIP) and the previous one fades out under it.</summary>
        static Element AiArm(string key, params Element[] body) => new BoxEl
        {
            Key = key, Direction = 1, Gap = CardSpacing, AlignSelf = FlexAlign.Stretch,
            Enter = s_aiEnter, Exit = s_aiExit, Transition = MotionTok.StandardEnter,
            Children = body,
        };

        /// <summary>The card shape every non-Ready phase shares; the header icon is always the Windows AI sparkle.</summary>
        static Element AiCard(string header, string? description, Element? content, TemplateParts? parts = null)
            => SettingsCard.Create(new SettingsCard.Options
            {
                Header = header,
                Description = description,
                HeaderIcon = RowGlyph(Tab.Appearance, "aiLyrics"),
                Content = content,
                IsActionIconVisible = false,
                Parts = parts,
            });

        static Element AiRow(params Element[] children) => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Children = children,
        };

        /// <summary>The master switch. Each arm mounts its own (the arms are keyed per phase), so the fresh signal's value
        /// is the truth at mount; a gesture writes it and the host's answer swaps the arm.</summary>
        static Element AiSwitch(bool on, bool enabled)
            => ToggleSwitch.Create(new Signal<bool>(on), onChange: enabled ? s_aiSetEnabled : null, isEnabled: enabled,
                style: SettingsCard.CompactToggleStyle());

        /// <summary>A behaviour switch: written through <c>Prefs.AiLyrics.Set</c> so the driver's epoch re-evaluates the song.</summary>
        static Element AiPrefSwitch(SettingKey<bool> key, bool value)
            => ToggleSwitch.Create(new Signal<bool>(value), onChange: on => Prefs.AiLyrics.Set(key, on),
                style: SettingsCard.CompactToggleStyle());

        /// <summary>One row per aligner language in the pack: name, Installed (✓) or its size, and Download / Remove. The last
        /// installed language cannot be removed (the host refuses it too).</summary>
        static Element AiLanguages(IReadOnlyList<string> installed)
        {
            AiEnsureLanguages();
            var order = s_aiLangOrder!;
            var bytes = s_aiLangBytes!;
            var rows = new Element[order.Length];
            for (int i = 0; i < order.Length; i++)
                rows[i] = AiLanguageRow(order[i], bytes[i], AiContains(installed, order[i]), installed.Count > 1);
            return new BoxEl { Direction = 1, Gap = Spacing.XXS, AlignItems = FlexAlign.End, Children = rows };
        }

        static Element AiLanguageRow(string lang, long bytes, bool isInstalled, bool canRemove)
        {
            Element status = isInstalled
                ? AiRow(Icon(Icons.Accept, 12f, Tok.AccentDefault), Caption(Loc.Get(AiS.Lang.Installed)))
                : Caption(AiLyrics.Rules.FormatBytes(bytes));
            Element action = isInstalled
                ? canRemove ? HyperlinkButton.Create(Loc.Get(AiS.Lang.Remove), () => AiLyrics.RemoveLanguage(lang)) : new BoxEl()
                : Button.Standard(Loc.Get(AiS.Lang.Download), () => AiDownloadThen(bytes, () => AiLyrics.InstallLanguage(lang)));
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinHeight = 32f,
                Children =
                [
                    new BoxEl
                    {
                        Width = 88f, Shrink = 0f,
                        Children = [new TextEl(AiLanguageName(lang)) { Size = 14f, LineHeight = 20f, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
                    },
                    new BoxEl { Width = 84f, Shrink = 0f, Children = [status] },
                    new BoxEl { Width = 96f, Shrink = 0f, Direction = 0, Justify = FlexJustify.End, Children = [action] },
                ],
            };
        }

        // ── verbs with a question first ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>A download above the metered threshold asks first (the ONE confirm shape, Cancel the default).</summary>
        static void AiDownloadThen(long bytes, Action start)
        {
            if (AiLyrics.Rules.NeedsMeteredConfirm(Platform.Network.IsMetered, bytes))
                ConfirmThen(Loc.Get(AiS.Metered.Title), AiS.Metered.Body(AiLyrics.Rules.FormatBytes(bytes)),
                    Loc.Get(AiS.Metered.Confirm), start);
            else
                start();
        }

        /// <summary>N11: Remove confirms; files still in use go at the next start and the card says so.</summary>
        static void AiConfirmRemove(long bytes)
            => ConfirmThen(Loc.Get(AiS.RemoveConfirm.Title), AiS.RemoveConfirm.Body(AiLyrics.Rules.FormatBytes(bytes)),
                Loc.Get(AiS.RemoveConfirm.Action), static () => AiLyrics.RemoveFiles(s_aiOnRemoved));

        // ── pure helpers ────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The bar sweeps while the total is unknown or the host is checking / verifying. Installing keeps the
        /// determinate fill (it follows each file, so switching would flicker the bar once per file).</summary>
        static bool AiIndeterminate(in AiLyrics.DownloadProgress d)
            => d.Phase is AiLyrics.DownloadPhase.Checking or AiLyrics.DownloadPhase.Verifying || d.Total <= 0;

        static float AiDownloadFraction(in AiLyrics.Status s)
            => s.Download.Total > 0 ? (float)Math.Clamp((double)s.Download.Done / s.Download.Total, 0, 1) : 0f;

        static float AiPrepareFraction(in AiLyrics.Status s)
            => (float)AiLyrics.Rules.PrepareFraction(s.Prepare.WeightDone, s.Prepare.WeightTotal, s.Prepare.Done, s.Prepare.Total);

        /// <summary>"12.3 / 84.0 MB · 4.2 MB/s · 18 s left", or the step's own word while it checks, verifies or installs.</summary>
        static string AiDownloadLine(in AiLyrics.Status s)
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
        static string AiDetail(string raw)
            => string.IsNullOrWhiteSpace(raw)
                ? ""
                : string.Join(" · ", raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        static string AiLanguageName(string lang) => lang switch
        {
            "en" => Loc.Get(AiS.Lang.En),
            "es" => Loc.Get(AiS.Lang.Es),
            "nl" => Loc.Get(AiS.Lang.Nl),
            "ko" => Loc.Get(AiS.Lang.Ko),
            _ => lang.ToUpperInvariant(),
        };

        static bool AiContains(IReadOnlyList<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>The pack's languages (English, Spanish, then any other in code order) and each one's download size —
        /// read once from the embedded manifest (in memory; no disk).</summary>
        static void AiEnsureLanguages()
        {
            if (s_aiLangOrder is not null) return;
            var langs = AiLyrics.PackManifest.Embedded.Languages;
            var order = new List<string>(langs.Count);
            if (langs.ContainsKey("en")) order.Add("en");
            if (langs.ContainsKey("es")) order.Add("es");
            if (langs.ContainsKey("nl")) order.Add("nl");
            if (langs.ContainsKey("ko")) order.Add("ko");
            var rest = new List<string>();
            foreach (var key in langs.Keys)
                if (!AiContains(order, key)) rest.Add(key.ToLowerInvariant());
            rest.Sort(StringComparer.Ordinal);
            order.AddRange(rest);
            var bytes = new long[order.Count];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = AiLyrics.Pack.DownloadBytes(langs[order[i]]);
            s_aiLangBytes = bytes;
            s_aiLangOrder = order.ToArray();
        }

        static long AiAverageLanguageBytes()
        {
            AiEnsureLanguages();
            var bytes = s_aiLangBytes!;
            if (bytes.Length == 0) return 0;
            long sum = 0;
            for (int i = 0; i < bytes.Length; i++) sum += bytes[i];
            return sum / bytes.Length;
        }
    }
}
