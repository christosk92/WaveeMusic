// ── Screens/Setup.UI.Runtime.cs ────────────────────────────────────────────────────────────────────────────────────
// the 10-phase runtime provisioning card BODY (PlaybackRuntimeSetupCard 1,030), rendered by the shell's banner in
// Wave 4 and by the wizard's Local-playback page in Wave 6 — written by I in Wave 4, mounted by R in Wave 6 (the
// Settings.UI.Video.cs precedent, inverted)
//
// Role: UI
// Owner: I
// Wave: 4
// Budget: 1100 lines
// Spec: ch 19 §9.4 (which budgets it 1,100 under Screens/Setup.UI.cs)
//
// WHAT IS HERE. The body arms for every phase, the one command row, and the dialog that hosts them at rung 2 of the
// modal width ladder. The card's state holder (`Setup.RuntimeModel`, one per open, shared by the body and the footer), the
// process-wide cells the banner gate and the other doors read (`Setup.Runtime`) and the host SEAM the SHELL half fills
// (`Setup.RuntimeHost` — owner R's `Setup.Host.cs`, Wave 6) moved to `RuntimeModel.cs` (C14, engine-free). Every decision the card draws is `Setup.RuntimeRules`
// in `Setup.cs` (CORE) — this file is layout and wiring. (The nested signature sheet is plan §2's: the overlay file.)
//
// WHAT IS NOT HERE, ON PURPOSE. The host seam speaks only the card's own UI vocabulary — a catalog of packs with a
// version/arch/hash/size, a download with bytes, a verify verdict, a signature summary. It names no runtime internals:
// how a pack is fetched, verified or loaded is the SHELL half's business, reached only through these delegates. With no
// host installed (a public-only build, or before Wave 6 wires it) every verb answers exactly as 0.2.9's
// `provisioner is null` path did: the Failed phase with "Local audio is not active.".
//
// THE MOUNT POINTS. `Setup.RuntimeCard()` (the body) and `Setup.RuntimeCardFooter()` (the command row) read the active
// model from `Setup.Runtime.Active`; `Setup.OpenRuntimeCard(overlay, post)` is the standalone dialog the shell banner
// opens. The wizard (Wave 6) calls `Setup.Runtime.Begin(post, onClose, onWizardExit)` and mounts the body and the
// named pieces (`RuntimeVersionPicker`, `RuntimeVerifyFacts`, `RuntimeReadyFacts`, `RuntimeLocalSourceRows`, …).

using System.Threading;
using System.Threading.Tasks;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Setup
{
    // ══ 4. THE DIALOG + THE MOUNT POINTS ════════════════════════════════════════════════════════════════════════════

    /// <summary>The standalone setup dialog (the banner's "Set up", the other doors through
    /// <see cref="Runtime.OpenRequest"/>). ContentDialog at rung 2; its built-in buttons are cleared and the Footer owns
    /// the ONE command row, so a phase swap never re-opens the dialog; dismissal is vetoed while busy.</summary>
    public static OverlayHandle OpenRuntimeCard(IOverlayService overlay, Action<Action> post)
    {
        Log.Event(WaveeLogLevel.Info, "ui", "runtime.setup.open", "Playback runtime setup opened", null, -1, null,
            WaveeLogField.Of("issue", Runtime.Status.Peek().Issue.ToString()));
        var model = Runtime.Begin(post);
        var handle = ContentDialog.Show(overlay, d =>
        {
            d.Title = Loc.Get(Strings.Playback.Runtime.Title);
            d.DialogWidth = RuntimeRules.DialogWidth;
            d.PrimaryText = "";                  // PrimaryText defaults to "OK": clear all three or a phantom OK appears
            d.SecondaryText = "";
            d.CloseText = "";
            d.Content = RuntimeCard();
            d.Footer = RuntimeCardFooter();
            d.Closing = a => { if (model.IsBusy) a.Cancel = true; };
            d.Closed = _ => Runtime.End(model);
        });
        model.CloseHost = handle.Close;
        return handle;
    }

    /// <summary>The card BODY over the active model.</summary>
    // MOUNT POINT (stage B contract)
    public static Element RuntimeCard() => Embed.Comp(() => new RuntimeBody());

    /// <summary>The card's ONE command row over the active model.</summary>
    // MOUNT POINT (stage B contract)
    public static Element RuntimeCardFooter() => Embed.Comp(() => new RuntimeFooterView());

    // ══ 5. THE BODY ════════════════════════════════════════════════════════════════════════════════════════════════

    sealed class RuntimeBody : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            if (Runtime.Active.Value is not { } m) return RuntimeText(Loc.Get(Strings.Playback.Runtime.NotActive));
            return m.Phase.Value switch
            {
                RuntimePhase.Offer => RuntimeText(Loc.Get(Strings.Playback.Runtime.OfferBody)),
                RuntimePhase.FetchingCatalog => RuntimeCatalogWaiting(),
                RuntimePhase.Downloading => RuntimeDownloading(m),
                RuntimePhase.Verifying => RuntimeColumn(
                    RuntimeLead(Loc.Get(Strings.Playback.Runtime.Verifying)),
                    RuntimeMetricRow(Loc.Get(Strings.Playback.Runtime.VerifyingCaption), RuntimeRules.VerifySize(m.Total.Value)),
                    ProgressBar.Indeterminate(RuntimeRules.ProgressWidth),
                    RuntimeVerifyFacts(m)),
                RuntimePhase.Untrusted => RuntimeStatus(Icons.StatusWarning, Tok.SystemFillCaution,
                    Loc.Get(Strings.Playback.Runtime.SignatureInvalid), Loc.Get(Strings.Playback.Runtime.UntrustedBody)),
                RuntimePhase.Ready => Ready(m, overlay),
                RuntimePhase.Failed => RuntimeStatus(Icons.StatusError, Tok.SystemFillCritical,
                    Loc.Get(Strings.Playback.Runtime.Missing), m.Error.Value ?? Loc.Get(Strings.Playback.Runtime.NoPack)),
                RuntimePhase.Advanced => Advanced(m),
                _ => new BoxEl(),
            };
        }

        static Element Ready(RuntimeModel m, IOverlayService overlay)
        {
            var facts = m.Status;
            var kids = new List<Element>(4) { RuntimeReadyBadge() };
            if (m.UpToDate.Value) kids.Add(RuntimeText(Loc.Get(Strings.Playback.Runtime.UpToDate)));
            kids.Add(RuntimeReadyFacts(facts, overlay));
            kids.Add(new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                Children =
                [
                    HyperlinkButton.Create(Loc.Get(Strings.Playback.Runtime.Replace), m.ShowAdvanced),
                    HyperlinkButton.Create(Loc.Get(Strings.Playback.Runtime.Remove), m.Remove),
                ],
            });
            return new BoxEl { Direction = 1, Gap = Spacing.M, Children = kids.ToArray() };
        }

        static Element Advanced(RuntimeModel m)
        {
            var kids = new List<Element>(6) { RuntimeText(Loc.Get(Strings.Playback.Runtime.AdvancedBody)) };
            switch (RuntimeRules.AdvancedArm(m.Catalog.Value, m.SupportedPacks.Count))
            {
                case RuntimeAdvancedArm.Busy:
                    kids.Add(RuntimeBusyRow(Loc.Get(Strings.Playback.Runtime.CheckingSupport)));
                    break;
                case RuntimeAdvancedArm.Picker:
                    kids.Add(RuntimeVersionPicker(m, Loc.Get(Strings.Playback.Runtime.ChooseVersion)));
                    break;
                case RuntimeAdvancedArm.NoPack:
                    kids.Add(RuntimeText(Loc.Get(Strings.Playback.Runtime.NoPack)));
                    break;
                case RuntimeAdvancedArm.Unreachable:
                    kids.Add(RuntimeText(Loc.Get(Strings.Playback.Runtime.CatalogUnreachable)));
                    break;
                // NotFetched: no arm at all — no busy row, no copy (ported, W32).
            }
            kids.Add(new BoxEl { Height = 1f, Fill = Tok.StrokeCardDefault, Margin = new Edges4(0f, 4f, 0f, 4f) });
            kids.AddRange(RuntimeLocalSourceRows(m));
            return new BoxEl { Direction = 1, Gap = Spacing.S, Children = kids.ToArray() };
        }
    }

    // ── the named pieces (the wizard mounts these too) ─────────────────────────────────────────────────────────────

    static Element RuntimeColumn(params Element[] kids) => new BoxEl { Direction = 1, Gap = Spacing.M, Children = kids };

    /// <summary>Body copy: 13, TextSecondary unless overridden, wrapping.</summary>
    public static Element RuntimeText(string text, ColorF? color = null)
        => R.Type.DenseMeta(text) with { Color = color ?? Tok.TextSecondary, Wrap = TextWrap.Wrap };

    /// <summary>The lead sentence: 16 TextPrimary, wrapping.</summary>
    public static Element RuntimeLead(string text)
        => R.Type.SheetTitle(text) with { Color = Tok.TextPrimary, Wrap = TextWrap.Wrap };

    /// <summary>An inline status block — an 18-DIP severity glyph beside a 14/600 heading over wrapped body copy
    /// (clean dialog content, not a box-in-a-box InfoBar). The Failed body may be an arbitrary sentence.</summary>
    public static Element RuntimeStatus(string glyph, ColorF glyphColor, string heading, string body) => new BoxEl
    {
        Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Start,
        Children =
        [
            new TextEl(glyph) { Size = 18f, FontFamily = Tok.Definition.Fonts.Icon, Color = glyphColor, Shrink = 0f, Margin = new Edges4(0f, 1f, 0f, 0f) },
            new BoxEl
            {
                Direction = 1, Gap = 4f, Grow = 1f, MinWidth = 0f,
                Children =
                [
                    R.Type.TrackTitle(heading) with { Wrap = TextWrap.Wrap },
                    R.Type.DenseMeta(body) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                ],
            },
        ],
    };

    static Element RuntimeBusyRow(string message) => new BoxEl
    {
        Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinHeight = 48f,
        Children = [ProgressRing.Indeterminate(), RuntimeText(message, Tok.TextPrimary)],
    };

    /// <summary>A label (13/600, one line) beside its value (12 TextSecondary, never shrinks).</summary>
    static Element RuntimeMetricRow(string label, string value) => new BoxEl
    {
        Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center,
        Children =
        [
            R.Type.DenseTitle(label) with
            {
                Color = Tok.TextPrimary,
                Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
            R.Type.TrackMeta(value) with { Shrink = 0f },
        ],
    };

    /// <summary>FetchingCatalog: one calm status line, the process architecture, the real indeterminate bar.</summary>
    public static Element RuntimeCatalogWaiting(float barWidth = RuntimeRules.ProgressWidth) => RuntimeColumn(
        RuntimeLead(Loc.Get(Strings.Playback.Runtime.CheckingSupport)),
        RuntimeMetricRow(Loc.Get(Strings.Playback.Runtime.ReachingCatalog),
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()),
        ProgressBar.Indeterminate(barWidth));

    /// <summary>Downloading: the label, "12.3 / 84.0 MB", a determinate bar (indeterminate while the total is unknown)
    /// and the "you can't leave this step" caption.</summary>
    public static Element RuntimeDownloading(RuntimeModel m, float barWidth = RuntimeRules.ProgressWidth)
    {
        long received = m.Received.Value, total = m.Total.Value;
        Element bar = total > 0
            ? ProgressBar.Determinate(RuntimeRules.ProgressFraction(received, total), barWidth)
            : ProgressBar.Indeterminate(barWidth);
        return RuntimeColumn(
            RuntimeLead(Loc.Get(Strings.Playback.Runtime.Downloading)),
            RuntimeMetricRow(m.DownloadLabel.Value ?? Loc.Get(Strings.Playback.Runtime.Downloading),
                RuntimeRules.DownloadBytes(received, total)),
            bar,
            Ui.Caption(Loc.Get(Strings.Playback.Runtime.DownloadBlocking)) with { Color = Tok.TextTertiary, Wrap = TextWrap.Wrap });
    }

    static BoxEl RuntimeFactBox(float pad, float gap, params Element[] rows) => new()
    {
        Direction = 1, Gap = gap, Padding = Edges4.All(pad),
        Fill = Tok.FillLayerAlt, Corners = CornerRadius4.All(Radii.Control),
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Children = rows,
    };

    /// <summary>A fact row: a 12 TextSecondary label in a fixed lane, a 12 TextPrimary one-line value ("—" when unknown).</summary>
    public static Element RuntimeFactRow(string label, string? value, float labelWidth = RuntimeRules.DetailLabelWidth) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
        Children =
        [
            Ui.Caption(label) with { Color = Tok.TextSecondary, Width = labelWidth, Shrink = 0f },
            Ui.Caption(RuntimeRules.OrDash(value)) with
            {
                Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                Grow = 1f, MinWidth = 0f,
            },
        ],
    };

    /// <summary>The Verifying (and wizard Untrusted) fact box: Version · Architecture · SHA-256 (4 + "…" + 4).</summary>
    public static Element RuntimeVerifyFacts(RuntimeModel m)
    {
        var entry = m.ActiveEntry;
        return RuntimeFactBox(Spacing.S, Spacing.XS,
            RuntimeFactRow(Loc.Get(Strings.Playback.Runtime.DetailVersion), entry?.Version),
            RuntimeFactRow(Loc.Get(Strings.Playback.Runtime.DetailArch),
                entry?.Arch ?? System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()),
            RuntimeFactRow(Loc.Get(Strings.Playback.Runtime.DetailSha256), entry is null ? null : RuntimeRules.ShortHash(entry.Sha256)));
    }

    /// <summary>"✓ Local playback is ready".</summary>
    public static Element RuntimeReadyBadge() => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
        Children =
        [
            InfoBadge.Icon(InfoBadgeSeverity.Success),
            R.Type.TrackTitle(Loc.Get(Strings.Playback.Runtime.Ready)),
        ],
    };

    /// <summary>The Ready fact box: Version · Architecture · Signature (+ the [Signature] button when details exist) ·
    /// Location.</summary>
    public static Element RuntimeReadyFacts(RuntimeFacts facts, IOverlayService overlay,
                                            float labelWidth = RuntimeRules.DetailLabelWidth)
    {
        var sigKids = new List<Element>(3)
        {
            Ui.Caption(Loc.Get(Strings.Playback.Runtime.DetailSignature)) with { Color = Tok.TextSecondary, Width = labelWidth, Shrink = 0f },
            Ui.Caption(RuntimeSignatureSummary(facts)) with
            {
                Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Grow = 1f, MinWidth = 0f,
            },
        };
        if (facts.Signature is not null)
            sigKids.Add(Button.Standard(Loc.Get(Strings.Playback.Runtime.DetailSignature), () => Shell.OpenSignatureDialog(overlay, facts)) with
            {
                MinWidth = 86f, Height = 28f, MinHeight = 28f, Justify = FlexJustify.Center,
            });
        return RuntimeFactBox(12f, 6f,
            RuntimeFactRow(Loc.Get(Strings.Playback.Runtime.DetailVersion), facts.Version, labelWidth),
            RuntimeFactRow(Loc.Get(Strings.Playback.Runtime.DetailArch), facts.Arch, labelWidth),
            new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = sigKids.ToArray() },
            RuntimeFactRow(Loc.Get(Strings.Playback.Runtime.DetailLocation), facts.Location, labelWidth));
    }

    /// <summary>The signature summary sentence (the ladder is <see cref="RuntimeRules.SignatureLine"/>; the wording is
    /// localized — 0.2.9 hard-coded it, ch 19 §6.10).</summary>
    public static string RuntimeSignatureSummary(RuntimeFacts facts)
    {
        string subject = facts.Signature?.Subject ?? "";
        return RuntimeRules.SignatureLine(facts) switch
        {
            RuntimeSignatureLine.SignedTrusted => Strings.Runtime.Sig.SignedBy(subject),
            RuntimeSignatureLine.SignedPinned => Strings.Runtime.Sig.SignedByPinned(subject),
            RuntimeSignatureLine.SignedOther => Strings.Runtime.Sig.SignedByTrust(subject,
                Loc.Get(RuntimeRules.TrustLocKey(facts.Signature?.Trust ?? RuntimeTrust.Unknown))),
            RuntimeSignatureLine.PinnedFingerprint => Loc.Get(Strings.Runtime.Sig.TrustedByFingerprint),
            RuntimeSignatureLine.VerifiedFingerprint => Loc.Get(Strings.Runtime.Sig.VerifiedFingerprint),
            RuntimeSignatureLine.NotTrustedOverride => Loc.Get(Strings.Runtime.Sig.NotTrustedOverride),
            RuntimeSignatureLine.CheckUnavailable => Loc.Get(Strings.Runtime.Sig.CheckUnavailable),
            _ => Loc.Get(Strings.Runtime.Sig.SignatureUnknown),
        };
    }

    /// <summary>The Advanced version list: "Spotify {v} · {arch}", the first carrying "  (Recommended)" (two spaces).</summary>
    public static Element RuntimeVersionPicker(RuntimeModel m, string? header)
    {
        var packs = m.SupportedPacks;
        var labels = new string[packs.Count];
        for (int i = 0; i < packs.Count; i++)
        {
            labels[i] = Strings.Runtime.Sig.PackLabel(packs[i].Version, packs[i].Arch);
            if (i == 0) labels[i] += "  (" + Loc.Get(Strings.Playback.Runtime.Recommended) + ")";
        }
        return RadioButtons.Create(labels, m.SelectedPack, header: header);
    }

    /// <summary>The two offline sources under Advanced: "Choose a Spotify.dll…" and "Use installed Spotify".</summary>
    public static Element[] RuntimeLocalSourceRows(RuntimeModel m) =>
    [
        RuntimeSettingRow(Icons.Folder, Loc.Get(Strings.Playback.Runtime.InstallFromFolder),
            Loc.Get(Strings.Playback.Runtime.InstallFromFolderCaption), m.PickFolder),
        RuntimeSettingRow(Icons.MusicNote, Loc.Get(Strings.Playback.Runtime.UseInstalled),
            Loc.Get(Strings.Playback.Runtime.UseInstalledCaption), m.UseInstalled),
    ];

    static Element RuntimeSettingRow(string glyph, string title, string caption, Action onClick) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = 13f, Padding = new Edges4(12f, 9f, 12f, 9f),
        Corners = CornerRadius4.All(Radii.Control), Role = AutomationRole.Button, Focusable = true,
        FocusVisualMargin = Design.FocusInsetRow, OnClick = onClick,
        Children =
        [
            new TextEl(glyph) { Size = 16f, FontFamily = Tok.Definition.Fonts.Icon, Color = Tok.TextSecondary, Shrink = 0f },
            new BoxEl
            {
                Direction = 1, Grow = 1f, MinWidth = 0f, Gap = 1f,
                Children =
                [
                    R.Type.TrackTitle(title),
                    Ui.Caption(caption) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                ],
            },
            new TextEl(Icons.ChevronRightMed) { Size = 12f, FontFamily = Tok.Definition.Fonts.Icon, Color = Tok.TextTertiary, Shrink = 0f },
        ],
    }.Interactive(Interaction.Subtle);

    // ══ 6. THE COMMAND ROW ═════════════════════════════════════════════════════════════════════════════════════════
    //
    // (The nested digital-signature sheet the Ready view's [Signature] button opens is `Shell.OpenSignatureDialog` in
    // `+Shell.Overlays.UI.cs` — plan §2 files the dialog there, beside the banner that leads to it.)

    sealed class RuntimeFooterView : Component
    {
        const float BtnMinW = 96f, BtnH = 32f;

        public override Element Render()
        {
            if (Runtime.Active.Value is not { } m) return Row(null, Btn(Loc.Get(Strings.Common.Dismiss), () => { }, false));
            var footer = RuntimeRules.FooterFor(m.Phase.Value, m.Catalog.Value, m.SupportedPacks.Count,
                Shell.PageFor(new Shell.Route(Shell.RouteKind.PlaybackDiagnostics)) is not null);

            Element? left = footer.Link == RuntimeVerb.None ? null
                : footer.Link2 == RuntimeVerb.None ? Link(m, footer.Link, flush: true)
                : new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center,
                    // Only the FIRST link takes the flush-left margin; the rest keep their own 11-DIP padding.
                    Children = [Link(m, footer.Link, flush: true), Link(m, footer.Link2, flush: false)],
                };
            var right = new List<Element>(2);
            if (footer.Standard != RuntimeVerb.None) right.Add(Btn(Label(footer.Standard), Verb(m, footer.Standard), footer.StandardEnabled));
            if (footer.Accent != RuntimeVerb.None) right.Add(AccentBtn(Label(footer.Accent), Verb(m, footer.Accent), footer.AccentEnabled));
            return Row(left, right.ToArray());
        }

        static string Label(RuntimeVerb verb) => Loc.Get(verb switch
        {
            RuntimeVerb.Advanced => Strings.Playback.Runtime.Advanced,
            RuntimeVerb.ViewDiagnostics => Strings.Playback.Runtime.ViewDiagnostics,
            RuntimeVerb.NotNow => Strings.Playback.Runtime.NotNow,
            RuntimeVerb.DownloadSetup => Strings.Playback.Runtime.DownloadSetup,
            RuntimeVerb.Cancel or RuntimeVerb.CancelUntrusted => Strings.Auth.Cancel,
            RuntimeVerb.LoadAnyway => Strings.Playback.Runtime.LoadAnyway,
            RuntimeVerb.CheckUpdate => Strings.Playback.Runtime.CheckUpdate,
            RuntimeVerb.Done => Strings.Playback.Runtime.Done,
            RuntimeVerb.TryAgain => Strings.Playback.Runtime.TryAgain,
            RuntimeVerb.Back => Strings.Playback.Runtime.Back,
            RuntimeVerb.Install => Strings.Playback.Runtime.Install,
            _ => Strings.Common.Dismiss,
        });

        static Action Verb(RuntimeModel m, RuntimeVerb verb) => verb switch
        {
            RuntimeVerb.Advanced => m.ShowAdvanced,
            RuntimeVerb.ViewDiagnostics => m.OpenDiagnostics,
            RuntimeVerb.NotNow => m.NotNow,
            RuntimeVerb.DownloadSetup or RuntimeVerb.CheckUpdate => m.StartDownload,
            RuntimeVerb.TryAgain => m.Retry,
            RuntimeVerb.Cancel => m.Cancel,
            RuntimeVerb.CancelUntrusted => m.CancelUntrusted,
            RuntimeVerb.LoadAnyway => m.ConfirmUntrusted,
            RuntimeVerb.Back => m.Back,
            RuntimeVerb.Install => m.InstallSelected,
            _ => m.Close,
        };

        static Element Link(RuntimeModel m, RuntimeVerb verb, bool flush)
        {
            var link = HyperlinkButton.Create(Label(verb), Verb(m, verb));
            // The hyperlink carries its own 11-DIP inner padding; −11 puts the first link's TEXT flush with the copy.
            return flush ? link with { Margin = new Edges4(-11f, 0f, 0f, 0f) } : link;
        }

        static Element Btn(string text, Action onClick, bool enabled) =>
            Button.Standard(text, onClick, isEnabled: enabled) with
            { MinWidth = BtnMinW, Height = BtnH, MinHeight = BtnH, Justify = FlexJustify.Center };

        static Element AccentBtn(string text, Action onClick, bool enabled) =>
            Button.Accent(text, onClick, isEnabled: enabled) with
            { MinWidth = BtnMinW, Height = BtnH, MinHeight = BtnH, Justify = FlexJustify.Center, TabIndex = 1 };

        /// <summary>A WRAPPING command row: [links?][buttons group, Grow 1, Justify End]. The group takes the line's leftover
        /// and right-aligns its buttons, so a footer that fits (Offer, Ready) is one line exactly as before; the Failed
        /// footer (2 links + 2 buttons, wider than the 412 slot: <see cref="RuntimeRules.FooterWraps"/>) drops the buttons
        /// onto a second, right-aligned line instead of clipping "Try again". The engine's wrap line gap is the row's Gap.</summary>
        static Element Row(Element? left, params Element[] right)
        {
            var kids = new List<Element>(2);
            if (left is not null) kids.Add(left);
            kids.Add(new BoxEl
            {
                Direction = 0, Grow = 1f, Gap = Spacing.S, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
                Children = right,
            });
            return new BoxEl { Direction = 0, Grow = 1f, Gap = Spacing.S, AlignItems = FlexAlign.Center, Wrap = true, Children = kids.ToArray() };
        }
    }
}
