// ── Themes/Classic/Structure/Views/Screens/ClassicSetup.Runtime.cs — Classic's runtime provisioning card (themes plan 6.1, T11c) ─
//
// From `Screens\Setup.UI.Runtime.cs`: the 10-phase runtime provisioning card BODY (`Setup.RuntimeCard`'s two slots: the body and the command row of the standalone dialog) and the named
// pieces the wizard's Local playback page reuses (the status block, the metric row, the fact boxes, the version picker, the local-source rows) and the sign-in surfaces too.
// Every decision the card draws is core (`Setup.RuntimeRules`: which body arm and which footer a phase gets, where each Cancel lands, the byte / hash / signature wording) and the state is
// `Setup.RuntimeModel`; this file is layout and wiring. The dialog that hosts the slots (rung 2 of the modal width ladder, vetoed dismissal while busy) is core: `Setup.OpenRuntimeCard`.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

internal static partial class ClassicSetup
{
    /// <summary>The registered template of <see cref="ViewKeys.SetupRuntimeCard"/>: the dialog's body or its command row.</summary>
    public static Element RuntimeCardTemplate(SetupRuntimeCardProps props, in ViewContext ctx)
        => props.Part == SetupDialogPart.Footer ? RuntimeFooter(props) : RuntimeBody(props, ctx);

    // ── the body ────────────────────────────────────────────────────────────────────────────────────────────────────

    private static Element RuntimeBody(SetupRuntimeCardProps props, in ViewContext ctx)
    {
        var overlay = ctx.Hooks.UseContext(Overlay.Service);
        if (Setup.Runtime.Active.Value is not { } m) return RuntimeText(Loc.Get(Strings.Playback.Runtime.NotActive));
        return m.Phase.Value switch
        {
            RuntimePhase.Offer => RuntimeText(Loc.Get(Strings.Playback.Runtime.OfferBody)),
            RuntimePhase.FetchingCatalog => RuntimeCatalogWaiting(),
            RuntimePhase.Downloading => RuntimeDownloading(m),
            RuntimePhase.Verifying => RuntimeColumn(
                RuntimeLead(Loc.Get(Strings.Playback.Runtime.Verifying)),
                RuntimeMetricRow(Loc.Get(Strings.Playback.Runtime.VerifyingCaption), Setup.RuntimeRules.VerifySize(m.Total.Value)),
                ProgressBar.Indeterminate(Setup.RuntimeRules.ProgressWidth),
                RuntimeVerifyFacts(m)),
            RuntimePhase.Untrusted => RuntimeStatus(Icons.StatusWarning, Tok.SystemFillCaution,
                Loc.Get(Strings.Playback.Runtime.SignatureInvalid), Loc.Get(Strings.Playback.Runtime.UntrustedBody)),
            RuntimePhase.Ready => CardReady(m, overlay, props.OpenSignature),
            RuntimePhase.Failed => RuntimeStatus(Icons.StatusError, Tok.SystemFillCritical,
                Loc.Get(Strings.Playback.Runtime.Missing), m.Error.Value ?? Loc.Get(Strings.Playback.Runtime.NoPack)),
            RuntimePhase.Advanced => CardAdvanced(m),
            _ => new BoxEl(),
        };
    }

    private static Element CardReady(Setup.RuntimeModel m, IOverlayService overlay, Action<IOverlayService, Setup.RuntimeFacts> openSignature)
    {
        var facts = m.Status;
        var kids = new List<Element>(4) { RuntimeReadyBadge() };
        if (m.UpToDate.Value) kids.Add(RuntimeText(Loc.Get(Strings.Playback.Runtime.UpToDate)));
        kids.Add(RuntimeReadyFacts(facts, overlay, openSignature));
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

    private static Element CardAdvanced(Setup.RuntimeModel m)
    {
        var kids = new List<Element>(6) { RuntimeText(Loc.Get(Strings.Playback.Runtime.AdvancedBody)) };
        switch (Setup.RuntimeRules.AdvancedArm(m.Catalog.Value, m.SupportedPacks.Count))
        {
            case Setup.RuntimeAdvancedArm.Busy:
                kids.Add(RuntimeBusyRow(Loc.Get(Strings.Playback.Runtime.CheckingSupport)));
                break;
            case Setup.RuntimeAdvancedArm.Picker:
                kids.Add(RuntimeVersionPicker(m, Loc.Get(Strings.Playback.Runtime.ChooseVersion)));
                break;
            case Setup.RuntimeAdvancedArm.NoPack:
                kids.Add(RuntimeText(Loc.Get(Strings.Playback.Runtime.NoPack)));
                break;
            case Setup.RuntimeAdvancedArm.Unreachable:
                kids.Add(RuntimeText(Loc.Get(Strings.Playback.Runtime.CatalogUnreachable)));
                break;
            // NotFetched: no arm at all — no busy row, no copy (ported, W32).
        }
        kids.Add(new BoxEl { Height = SetupGeometry.SeparatorHeight, Fill = Tok.StrokeCardDefault, Margin = ClassicSetupRules.CardRuleMargin });
        kids.AddRange(RuntimeLocalSourceRows(m));
        return new BoxEl { Direction = 1, Gap = Spacing.S, Children = kids.ToArray() };
    }

    // ── the named pieces (the wizard and the sign-in door mount these too) ─────────────────────────────────────────

    private static Element RuntimeColumn(params Element[] kids) => new BoxEl { Direction = 1, Gap = Spacing.M, Children = kids };

    /// <summary>Body copy: 13, TextSecondary unless overridden, wrapping.</summary>
    internal static Element RuntimeText(string text, ColorF? color = null)
        => R.Type.DenseMeta(text) with { Color = color ?? Tok.TextSecondary, Wrap = TextWrap.Wrap };

    /// <summary>The lead sentence: 16 TextPrimary, wrapping.</summary>
    internal static Element RuntimeLead(string text)
        => R.Type.SheetTitle(text) with { Color = Tok.TextPrimary, Wrap = TextWrap.Wrap };

    /// <summary>An inline status block — an 18-DIP severity glyph beside a 14/600 heading over wrapped body copy
    /// (clean dialog content, not a box-in-a-box InfoBar). The Failed body may be an arbitrary sentence.</summary>
    internal static Element RuntimeStatus(string glyph, ColorF glyphColor, string heading, string body) => new BoxEl
    {
        Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Start,
        Children =
        [
            new TextEl(glyph) { Size = ClassicSetupRules.StatusGlyphSize, FontFamily = Tok.Definition.Fonts.Icon, Color = glyphColor, Shrink = 0f, Margin = ClassicSetupRules.StatusGlyphMargin },
            new BoxEl
            {
                Direction = 1, Gap = ClassicSetupRules.StatusTextGap, Grow = 1f, MinWidth = 0f,
                Children =
                [
                    R.Type.TrackTitle(heading) with { Wrap = TextWrap.Wrap },
                    R.Type.DenseMeta(body) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                ],
            },
        ],
    };

    private static Element RuntimeBusyRow(string message) => new BoxEl
    {
        Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinHeight = ClassicSetupRules.BusyRowMinHeight,
        Children = [ProgressRing.Indeterminate(), RuntimeText(message, Tok.TextPrimary)],
    };

    /// <summary>A label (13/600, one line) beside its value (12 TextSecondary, never shrinks).</summary>
    private static Element RuntimeMetricRow(string label, string value) => new BoxEl
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
    private static Element RuntimeCatalogWaiting(float barWidth = Setup.RuntimeRules.ProgressWidth) => RuntimeColumn(
        RuntimeLead(Loc.Get(Strings.Playback.Runtime.CheckingSupport)),
        RuntimeMetricRow(Loc.Get(Strings.Playback.Runtime.ReachingCatalog),
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()),
        ProgressBar.Indeterminate(barWidth));

    /// <summary>Downloading: the label, "12.3 / 84.0 MB", a determinate bar (indeterminate while the total is unknown)
    /// and the "you can't leave this step" caption.</summary>
    private static Element RuntimeDownloading(Setup.RuntimeModel m, float barWidth = Setup.RuntimeRules.ProgressWidth)
    {
        long received = m.Received.Value, total = m.Total.Value;
        Element bar = total > 0
            ? ProgressBar.Determinate(Setup.RuntimeRules.ProgressFraction(received, total), barWidth)
            : ProgressBar.Indeterminate(barWidth);
        return RuntimeColumn(
            RuntimeLead(Loc.Get(Strings.Playback.Runtime.Downloading)),
            RuntimeMetricRow(m.DownloadLabel.Value ?? Loc.Get(Strings.Playback.Runtime.Downloading),
                Setup.RuntimeRules.DownloadBytes(received, total)),
            bar,
            Ui.Caption(Loc.Get(Strings.Playback.Runtime.DownloadBlocking)) with { Color = Tok.TextTertiary, Wrap = TextWrap.Wrap });
    }

    private static BoxEl RuntimeFactBox(float pad, float gap, params Element[] rows) => new()
    {
        Direction = 1, Gap = gap, Padding = Edges4.All(pad),
        Fill = Tok.FillLayerAlt, Corners = CornerRadius4.All(Radii.Control),
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Children = rows,
    };

    /// <summary>A fact row: a 12 TextSecondary label in a fixed lane, a 12 TextPrimary one-line value ("—" when unknown).</summary>
    private static Element RuntimeFactRow(string label, string? value, float labelWidth = Setup.RuntimeRules.DetailLabelWidth) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
        Children =
        [
            Ui.Caption(label) with { Color = Tok.TextSecondary, Width = labelWidth, Shrink = 0f },
            Ui.Caption(Setup.RuntimeRules.OrDash(value)) with
            {
                Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                Grow = 1f, MinWidth = 0f,
            },
        ],
    };

    /// <summary>The Verifying (and wizard Untrusted) fact box: Version · Architecture · SHA-256 (4 + "…" + 4).</summary>
    private static Element RuntimeVerifyFacts(Setup.RuntimeModel m)
    {
        var entry = m.ActiveEntry;
        return RuntimeFactBox(Spacing.S, Spacing.XS,
            RuntimeFactRow(Loc.Get(Strings.Playback.Runtime.DetailVersion), entry?.Version),
            RuntimeFactRow(Loc.Get(Strings.Playback.Runtime.DetailArch),
                entry?.Arch ?? System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()),
            RuntimeFactRow(Loc.Get(Strings.Playback.Runtime.DetailSha256), entry is null ? null : Setup.RuntimeRules.ShortHash(entry.Sha256)));
    }

    /// <summary>"✓ Local playback is ready".</summary>
    private static Element RuntimeReadyBadge() => new BoxEl
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
    private static Element RuntimeReadyFacts(Setup.RuntimeFacts facts, IOverlayService overlay, Action<IOverlayService, Setup.RuntimeFacts> openSignature,
                                             float labelWidth = Setup.RuntimeRules.DetailLabelWidth)
    {
        var sigKids = new List<Element>(3)
        {
            Ui.Caption(Loc.Get(Strings.Playback.Runtime.DetailSignature)) with { Color = Tok.TextSecondary, Width = labelWidth, Shrink = 0f },
            Ui.Caption(Setup.RuntimeSignatureSummary(facts)) with
            {
                Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Grow = 1f, MinWidth = 0f,
            },
        };
        if (facts.Signature is not null)
            sigKids.Add(Button.Standard(Loc.Get(Strings.Playback.Runtime.DetailSignature), () => openSignature(overlay, facts)) with
            {
                MinWidth = ClassicSetupRules.SignatureButtonMinWidth, Height = ClassicSetupRules.SignatureButtonHeight, MinHeight = ClassicSetupRules.SignatureButtonHeight, Justify = FlexJustify.Center,
            });
        return RuntimeFactBox(ClassicSetupRules.ReadyFactPad, ClassicSetupRules.ReadyFactGap,
            RuntimeFactRow(Loc.Get(Strings.Playback.Runtime.DetailVersion), facts.Version, labelWidth),
            RuntimeFactRow(Loc.Get(Strings.Playback.Runtime.DetailArch), facts.Arch, labelWidth),
            new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = sigKids.ToArray() },
            RuntimeFactRow(Loc.Get(Strings.Playback.Runtime.DetailLocation), facts.Location, labelWidth));
    }

    /// <summary>The Advanced version list: "Spotify {v} · {arch}", the first carrying "  (Recommended)" (two spaces).</summary>
    private static Element RuntimeVersionPicker(Setup.RuntimeModel m, string? header)
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
    private static Element[] RuntimeLocalSourceRows(Setup.RuntimeModel m) =>
    [
        RuntimeSettingRow(Icons.Folder, Loc.Get(Strings.Playback.Runtime.InstallFromFolder),
            Loc.Get(Strings.Playback.Runtime.InstallFromFolderCaption), m.PickFolder),
        RuntimeSettingRow(Icons.MusicNote, Loc.Get(Strings.Playback.Runtime.UseInstalled),
            Loc.Get(Strings.Playback.Runtime.UseInstalledCaption), m.UseInstalled),
    ];

    private static Element RuntimeSettingRow(string glyph, string title, string caption, Action onClick) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = ClassicSetupRules.SettingRowGap, Padding = ClassicSetupRules.SettingRowPadding,
        Corners = CornerRadius4.All(Radii.Control), Role = AutomationRole.Button, Focusable = true,
        FocusVisualMargin = Design.FocusInsetRow, OnClick = onClick,
        Children =
        [
            new TextEl(glyph) { Size = ClassicSetupRules.SettingRowGlyphSize, FontFamily = Tok.Definition.Fonts.Icon, Color = Tok.TextSecondary, Shrink = 0f },
            new BoxEl
            {
                Direction = 1, Grow = 1f, MinWidth = 0f, Gap = 1f,
                Children =
                [
                    R.Type.TrackTitle(title),
                    Ui.Caption(caption) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                ],
            },
            new TextEl(Icons.ChevronRightMed) { Size = ClassicSetupRules.SettingRowChevronSize, FontFamily = Tok.Definition.Fonts.Icon, Color = Tok.TextTertiary, Shrink = 0f },
        ],
    }.Interactive(Interaction.Subtle);

    // ── the command row ─────────────────────────────────────────────────────────────────────────────────────────────

    private static Element RuntimeFooter(SetupRuntimeCardProps props)
    {
        if (Setup.Runtime.Active.Value is not { } m) return RuntimeRow(null, RuntimeBtn(Loc.Get(Strings.Common.Dismiss), () => { }, false));
        var footer = Setup.RuntimeRules.FooterFor(m.Phase.Value, m.Catalog.Value, m.SupportedPacks.Count, props.HasDiagnosticsPage());

        Element? left = footer.Link == Setup.RuntimeVerb.None ? null
            : footer.Link2 == Setup.RuntimeVerb.None ? RuntimeLink(m, footer.Link, flush: true)
            : new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center,
                // Only the FIRST link takes the flush-left margin; the rest keep their own 11-DIP padding.
                Children = [RuntimeLink(m, footer.Link, flush: true), RuntimeLink(m, footer.Link2, flush: false)],
            };
        var right = new List<Element>(2);
        if (footer.Standard != Setup.RuntimeVerb.None) right.Add(RuntimeBtn(VerbLabel(footer.Standard), VerbAction(m, footer.Standard), footer.StandardEnabled));
        if (footer.Accent != Setup.RuntimeVerb.None) right.Add(RuntimeAccentBtn(VerbLabel(footer.Accent), VerbAction(m, footer.Accent), footer.AccentEnabled));
        return RuntimeRow(left, right.ToArray());
    }

    private static string VerbLabel(Setup.RuntimeVerb verb) => Loc.Get(verb switch
    {
        Setup.RuntimeVerb.Advanced => Strings.Playback.Runtime.Advanced,
        Setup.RuntimeVerb.ViewDiagnostics => Strings.Playback.Runtime.ViewDiagnostics,
        Setup.RuntimeVerb.NotNow => Strings.Playback.Runtime.NotNow,
        Setup.RuntimeVerb.DownloadSetup => Strings.Playback.Runtime.DownloadSetup,
        Setup.RuntimeVerb.Cancel or Setup.RuntimeVerb.CancelUntrusted => Strings.Auth.Cancel,
        Setup.RuntimeVerb.LoadAnyway => Strings.Playback.Runtime.LoadAnyway,
        Setup.RuntimeVerb.CheckUpdate => Strings.Playback.Runtime.CheckUpdate,
        Setup.RuntimeVerb.Done => Strings.Playback.Runtime.Done,
        Setup.RuntimeVerb.TryAgain => Strings.Playback.Runtime.TryAgain,
        Setup.RuntimeVerb.Back => Strings.Playback.Runtime.Back,
        Setup.RuntimeVerb.Install => Strings.Playback.Runtime.Install,
        _ => Strings.Common.Dismiss,
    });

    private static Action VerbAction(Setup.RuntimeModel m, Setup.RuntimeVerb verb) => verb switch
    {
        Setup.RuntimeVerb.Advanced => m.ShowAdvanced,
        Setup.RuntimeVerb.ViewDiagnostics => m.OpenDiagnostics,
        Setup.RuntimeVerb.NotNow => m.NotNow,
        Setup.RuntimeVerb.DownloadSetup or Setup.RuntimeVerb.CheckUpdate => m.StartDownload,
        Setup.RuntimeVerb.TryAgain => m.Retry,
        Setup.RuntimeVerb.Cancel => m.Cancel,
        Setup.RuntimeVerb.CancelUntrusted => m.CancelUntrusted,
        Setup.RuntimeVerb.LoadAnyway => m.ConfirmUntrusted,
        Setup.RuntimeVerb.Back => m.Back,
        Setup.RuntimeVerb.Install => m.InstallSelected,
        _ => m.Close,
    };

    private static Element RuntimeLink(Setup.RuntimeModel m, Setup.RuntimeVerb verb, bool flush)
    {
        var link = HyperlinkButton.Create(VerbLabel(verb), VerbAction(m, verb));
        // The hyperlink carries its own 11-DIP inner padding; −11 puts the first link's TEXT flush with the copy.
        return flush ? link with { Margin = ClassicSetupRules.FlushLinkMargin } : link;
    }

    private static Element RuntimeBtn(string text, Action onClick, bool enabled) =>
        Button.Standard(text, onClick, isEnabled: enabled) with
        { MinWidth = ClassicSetupRules.DialogButtonMinWidth, Height = ClassicSetupRules.DialogButtonHeight, MinHeight = ClassicSetupRules.DialogButtonHeight, Justify = FlexJustify.Center };

    private static Element RuntimeAccentBtn(string text, Action onClick, bool enabled) =>
        Button.Accent(text, onClick, isEnabled: enabled) with
        { MinWidth = ClassicSetupRules.DialogButtonMinWidth, Height = ClassicSetupRules.DialogButtonHeight, MinHeight = ClassicSetupRules.DialogButtonHeight, Justify = FlexJustify.Center, TabIndex = 1 };

    /// <summary>A WRAPPING command row: [links?][buttons group, Grow 1, Justify End]. The group takes the line's leftover
    /// and right-aligns its buttons, so a footer that fits (Offer, Ready) is one line exactly as before; the Failed
    /// footer (2 links + 2 buttons, wider than the 412 slot) drops the buttons
    /// onto a second, right-aligned line instead of clipping "Try again". The engine's wrap line gap is the row's Gap.</summary>
    private static Element RuntimeRow(Element? left, params Element[] right)
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
