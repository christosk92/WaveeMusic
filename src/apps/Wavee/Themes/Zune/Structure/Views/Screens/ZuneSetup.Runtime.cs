// ── Themes/Zune/Structure/Views/Screens/ZuneSetup.Runtime.cs — Zune's runtime provisioning card (themes plan 6.3, P5 Z8b) ─────────────────────────
//
// The 10-phase runtime provisioning card BODY (`Setup.RuntimeCard`'s two slots: the body and the command row of the standalone dialog) and the named pieces the wizard's Local
// playback page reuses (the status block, the metric row, the fact boxes, the version picker, the local-source rows) and the sign-in surfaces too. Every decision the card
// draws is core (`Setup.RuntimeRules`: which body arm and which footer a phase gets, where each Cancel lands, the byte / hash / signature wording) and the state is
// `Setup.RuntimeModel`; this file is layout and wiring. The dialog that hosts the slots (vetoed dismissal while busy) is core: `Setup.OpenRuntimeCard`; its plate is the dialog
// style's (a flat plate on the dialog ground, 24 in, the commands as words, the primary as the round accent button).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Themes.Common;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using Z = Wavee.Themes.Zune.ZuneSetupRules;

namespace Wavee.Themes.Zune;

internal static partial class ZuneSetup
{
    /// <summary>The registered template of <see cref="ViewKeys.SetupRuntimeCard"/>: the dialog's body or its command row.</summary>
    public static Element RuntimeCardTemplate(SetupRuntimeCardProps props, in ViewContext ctx)
    {
        var layout = ZuneSetupTiers.Of(ctx.Tier());   // the dialog opens inside the window's tier: its commands and rows are the tier's height
        return props.Part == SetupDialogPart.Footer ? RuntimeFooter(props, layout) : RuntimeBody(props, ctx, layout);
    }

    // ── the body ────────────────────────────────────────────────────────────────────────────────────────────────────

    private static Element RuntimeBody(SetupRuntimeCardProps props, in ViewContext ctx, ZuneSetupLayout layout)
    {
        var overlay = ctx.Hooks.UseContext(Overlay.Service);
        // The bars are the dialog body's width: the stock card's, the window less its margins where the card fills (Compact).
        float bar = OverlayFit.Layout.Dialog.BodyWidth(Setup.RuntimeRules.ProgressWidth, ctx.Hooks.UseContextSignal(Viewport.Size).Value.Width);
        if (Setup.Runtime.Active.Value is not { } m) return RuntimeText(Loc.Get(Strings.Playback.Runtime.NotActive));
        return m.Phase.Value switch
        {
            RuntimePhase.Offer => RuntimeText(Loc.Get(Strings.Playback.Runtime.OfferBody)),
            RuntimePhase.FetchingCatalog => RuntimeCatalogWaiting(bar),
            RuntimePhase.Downloading => RuntimeDownloading(m, bar),
            RuntimePhase.Verifying => RuntimeColumn(
                RuntimeLead(Loc.Get(Strings.Playback.Runtime.Verifying)),
                RuntimeMetricRow(Loc.Get(Strings.Playback.Runtime.VerifyingCaption), Setup.RuntimeRules.VerifySize(m.Total.Value)),
                ProgressBar.Indeterminate(bar),
                RuntimeVerifyFacts(m)),
            RuntimePhase.Untrusted => RuntimeStatus(Icons.StatusWarning, Tok.SystemFillCaution,
                Loc.Get(Strings.Playback.Runtime.SignatureInvalid), Loc.Get(Strings.Playback.Runtime.UntrustedBody)),
            RuntimePhase.Ready => CardReady(m, overlay, props.OpenSignature, layout),
            RuntimePhase.Failed => RuntimeStatus(Icons.StatusError, Tok.SystemFillCritical,
                Loc.Get(Strings.Playback.Runtime.Missing), m.Error.Value ?? Loc.Get(Strings.Playback.Runtime.NoPack)),
            RuntimePhase.Advanced => CardAdvanced(m, layout),
            _ => new BoxEl(),
        };
    }

    private static Element CardReady(Setup.RuntimeModel m, IOverlayService overlay, Action<IOverlayService, Setup.RuntimeFacts> openSignature, ZuneSetupLayout layout)
    {
        var k = KitOf(layout);
        var facts = m.Status;
        var kids = new List<Element>(4) { RuntimeReadyBadge() };
        if (m.UpToDate.Value) kids.Add(RuntimeText(Loc.Get(Strings.Playback.Runtime.UpToDate)));
        kids.Add(RuntimeReadyFacts(facts, overlay, openSignature, layout));
        kids.Add(new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Children = [k.Cmd(Loc.Get(Strings.Playback.Runtime.Replace), m.ShowAdvanced), k.Cmd(Loc.Get(Strings.Playback.Runtime.Remove), m.Remove)],
        });
        return new BoxEl { Direction = 1, Gap = Spacing.M, Children = kids.ToArray() };
    }

    private static Element CardAdvanced(Setup.RuntimeModel m, ZuneSetupLayout layout)
    {
        var kids = new List<Element>(6) { RuntimeText(Loc.Get(Strings.Playback.Runtime.AdvancedBody)) };
        switch (Setup.RuntimeRules.AdvancedArm(m.Catalog.Value, m.SupportedPacks.Count))
        {
            case Setup.RuntimeAdvancedArm.Busy:
                kids.Add(RuntimeBusyRow(Loc.Get(Strings.Playback.Runtime.CheckingSupport)));
                break;
            case Setup.RuntimeAdvancedArm.Picker:
                kids.Add(RuntimeVersionPicker(m, Loc.Get(Strings.Playback.Runtime.ChooseVersion), layout));
                break;
            case Setup.RuntimeAdvancedArm.NoPack:
                kids.Add(RuntimeText(Loc.Get(Strings.Playback.Runtime.NoPack)));
                break;
            case Setup.RuntimeAdvancedArm.Unreachable:
                kids.Add(RuntimeText(Loc.Get(Strings.Playback.Runtime.CatalogUnreachable)));
                break;
            // NotFetched: no arm at all: no busy row, no copy.
        }
        kids.Add(new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault, Margin = Z.CardRuleMargin });
        kids.AddRange(RuntimeLocalSourceRows(m));
        return new BoxEl { Direction = 1, Gap = Spacing.S, Children = kids.ToArray() };
    }

    // ── the named pieces (the wizard and the sign-in door mount these too) ─────────────────────────────────────────

    private static Element RuntimeColumn(params Element[] kids) => new BoxEl { Direction = 1, Gap = Spacing.M, Children = kids };

    /// <summary>Body copy: 13, secondary unless overridden, wrapping.</summary>
    internal static Element RuntimeText(string text, ColorF? color = null)
        => R.Type.DenseMeta(text) with { Color = color ?? Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f };

    /// <summary>The lead sentence: the group voice, wrapping.</summary>
    internal static Element RuntimeLead(string text)
        => R.Type.SheetTitle(text) with { Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MinWidth = 0f };

    /// <summary>An inline status block: a severity glyph beside a heading over wrapped body copy (clean dialog content, not a box in a box). The Failed body may be any sentence.</summary>
    internal static Element RuntimeStatus(string glyph, ColorF glyphColor, string heading, string body) => new BoxEl
    {
        Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Start,
        Children =
        [
            new TextEl(glyph) { Size = Z.StatusGlyphSize, FontFamily = Tok.Definition.Fonts.Icon, Color = glyphColor, Shrink = 0f, Margin = Z.StatusGlyphMargin },
            new BoxEl
            {
                Direction = 1, Gap = Z.StatusTextGap, Grow = 1f, MinWidth = 0f,
                Children =
                [
                    R.Type.TrackTitle(heading) with { Wrap = TextWrap.Wrap, MinWidth = 0f },
                    R.Type.DenseMeta(body) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f },
                ],
            },
        ],
    };

    private static Element RuntimeBusyRow(string message) => new BoxEl
    {
        Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinHeight = Z.BusyRowMinHeight,
        Children = [ProgressRing.Indeterminate(), RuntimeText(message, Tok.TextPrimary)],
    };

    /// <summary>A label (one line) beside its value (secondary, never shrinks).</summary>
    private static Element RuntimeMetricRow(string label, string value) => new BoxEl
    {
        Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center,
        Children =
        [
            R.Type.DenseTitle(label) with { Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            R.Type.TrackMeta(value) with { Shrink = 0f },
        ],
    };

    /// <summary>FetchingCatalog: one calm status line, the process architecture, the real indeterminate bar.</summary>
    private static Element RuntimeCatalogWaiting(float barWidth) => RuntimeColumn(
        RuntimeLead(Loc.Get(Strings.Playback.Runtime.CheckingSupport)),
        RuntimeMetricRow(Loc.Get(Strings.Playback.Runtime.ReachingCatalog), System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()),
        ProgressBar.Indeterminate(barWidth));

    /// <summary>Downloading: the label, "12.3 / 84.0 MB", a determinate bar (indeterminate while the total is unknown) and the "you can't leave this step" line.</summary>
    private static Element RuntimeDownloading(Setup.RuntimeModel m, float barWidth)
    {
        long received = m.Received.Value, total = m.Total.Value;
        Element bar = total > 0
            ? ProgressBar.Determinate(Setup.RuntimeRules.ProgressFraction(received, total), barWidth)
            : ProgressBar.Indeterminate(barWidth);
        return RuntimeColumn(
            RuntimeLead(Loc.Get(Strings.Playback.Runtime.Downloading)),
            RuntimeMetricRow(m.DownloadLabel.Value ?? Loc.Get(Strings.Playback.Runtime.Downloading), Setup.RuntimeRules.DownloadBytes(received, total)),
            bar,
            ZuneText.Hint(Loc.Get(Strings.Playback.Runtime.DownloadBlocking)));
    }

    private static BoxEl RuntimeFactBox(float pad, float gap, params Element[] rows) => new()
    {
        Direction = 1, Gap = gap, Padding = Edges4.All(pad), Fill = Tok.FillSolidTertiary, Children = rows,
    };

    /// <summary>A fact row: a label in a fixed lane, a one-line value ("—" when unknown).</summary>
    private static Element RuntimeFactRow(string label, string? value, float labelWidth = Setup.RuntimeRules.DetailLabelWidth) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
        Children =
        [
            ZuneText.Hint(label) with { Width = labelWidth, Shrink = 0f },
            R.Type.MicroMeta(Setup.RuntimeRules.OrDash(value)) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Grow = 1f, MinWidth = 0f },
        ],
    };

    /// <summary>The Verifying (and wizard Untrusted) fact box: Version, Architecture, SHA-256 (4 + "…" + 4).</summary>
    private static Element RuntimeVerifyFacts(Setup.RuntimeModel m)
    {
        var entry = m.ActiveEntry;
        return RuntimeFactBox(Spacing.S, Spacing.XS,
            RuntimeFactRow(Loc.Get(Strings.Playback.Runtime.DetailVersion), entry?.Version),
            RuntimeFactRow(Loc.Get(Strings.Playback.Runtime.DetailArch), entry?.Arch ?? System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()),
            RuntimeFactRow(Loc.Get(Strings.Playback.Runtime.DetailSha256), entry is null ? null : Setup.RuntimeRules.ShortHash(entry.Sha256)));
    }

    /// <summary>"✓ Local playback is ready".</summary>
    private static Element RuntimeReadyBadge() => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
        Children = [InfoBadge.Icon(InfoBadgeSeverity.Success), R.Type.TrackTitle(Loc.Get(Strings.Playback.Runtime.Ready))],
    };

    /// <summary>The Ready fact box: Version, Architecture, Signature (+ the signature command when details exist), Location.</summary>
    private static Element RuntimeReadyFacts(Setup.RuntimeFacts facts, IOverlayService overlay, Action<IOverlayService, Setup.RuntimeFacts> openSignature,
                                             ZuneSetupLayout layout, float labelWidth = Setup.RuntimeRules.DetailLabelWidth)
    {
        var k = KitOf(layout);
        var sigKids = new List<Element>(3)
        {
            ZuneText.Hint(Loc.Get(Strings.Playback.Runtime.DetailSignature)) with { Width = labelWidth, Shrink = 0f },
            R.Type.MicroMeta(Setup.RuntimeSignatureSummary(facts)) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Grow = 1f, MinWidth = 0f },
        };
        if (facts.Signature is not null)
            sigKids.Add(k.Cmd(Loc.Get(Strings.Playback.Runtime.DetailSignature), () => openSignature(overlay, facts)));
        return RuntimeFactBox(Z.ReadyFactPad, Z.ReadyFactGap,
            RuntimeFactRow(Loc.Get(Strings.Playback.Runtime.DetailVersion), facts.Version, labelWidth),
            RuntimeFactRow(Loc.Get(Strings.Playback.Runtime.DetailArch), facts.Arch, labelWidth),
            new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = sigKids.ToArray() },
            RuntimeFactRow(Loc.Get(Strings.Playback.Runtime.DetailLocation), facts.Location, labelWidth));
    }

    /// <summary>The Advanced version list: "Spotify {v} · {arch}", the first carrying "  (Recommended)" (two spaces).</summary>
    private static Element RuntimeVersionPicker(Setup.RuntimeModel m, string? header, ZuneSetupLayout layout)
    {
        var packs = m.SupportedPacks;
        var labels = new string[packs.Count];
        for (int i = 0; i < packs.Count; i++)
        {
            labels[i] = Strings.Runtime.Sig.PackLabel(packs[i].Version, packs[i].Arch);
            if (i == 0) labels[i] += "  (" + Loc.Get(Strings.Playback.Runtime.Recommended) + ")";
        }
        return TouchRadios(labels, m.SelectedPack, layout.Target, header: header);
    }

    /// <summary>The two offline sources under Advanced: "Choose a Spotify.dll…" and "Use installed Spotify".</summary>
    private static Element[] RuntimeLocalSourceRows(Setup.RuntimeModel m) =>
    [
        SetupText.Card(Loc.Get(Strings.Playback.Runtime.InstallFromFolder), Loc.Get(Strings.Playback.Runtime.InstallFromFolderCaption), Icons.Folder, null, m.PickFolder),
        SetupText.Card(Loc.Get(Strings.Playback.Runtime.UseInstalled), Loc.Get(Strings.Playback.Runtime.UseInstalledCaption), Icons.MusicNote, null, m.UseInstalled),
    ];

    // ── the command row ─────────────────────────────────────────────────────────────────────────────────────────────

    private static Element RuntimeFooter(SetupRuntimeCardProps props, ZuneSetupLayout layout)
    {
        var k = KitOf(layout);
        if (Setup.Runtime.Active.Value is not { } m) return RuntimeRow(null, k.Cmd(Loc.Get(Strings.Common.Dismiss), static () => { }, false));
        var footer = Setup.RuntimeRules.FooterFor(m.Phase.Value, m.Catalog.Value, m.SupportedPacks.Count, props.HasDiagnosticsPage());

        Element? left = footer.Link == Setup.RuntimeVerb.None ? null
            : footer.Link2 == Setup.RuntimeVerb.None ? k.Cmd(VerbLabel(footer.Link), VerbAction(m, footer.Link))
            : new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                Children = [k.Cmd(VerbLabel(footer.Link), VerbAction(m, footer.Link)), k.Cmd(VerbLabel(footer.Link2), VerbAction(m, footer.Link2))],
            };
        var right = new List<Element>(2);
        if (footer.Standard != Setup.RuntimeVerb.None)
            right.Add(k.Cmd(VerbLabel(footer.Standard), VerbAction(m, footer.Standard), footer.StandardEnabled));
        if (footer.Accent != Setup.RuntimeVerb.None)
            right.Add(k.Go(VerbLabel(footer.Accent), VerbAction(m, footer.Accent), footer.AccentEnabled));
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

    /// <summary>A WRAPPING command row: [links?][commands group, Grow 1, Justify End]. The group takes the line's leftover and right-aligns its commands, so a footer that fits is
    /// one line; the Failed footer (two links and two commands, wider than the slot) drops the commands onto a second, right-aligned line instead of clipping.</summary>
    private static Element RuntimeRow(Element? left, params Element[] right)
    {
        var kids = new List<Element>(2);
        if (left is not null) kids.Add(left.Value);
        kids.Add(new BoxEl { Direction = 0, Grow = 1f, Gap = Z.FooterActionGap, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Children = right });
        return new BoxEl { Direction = 0, Grow = 1f, Gap = Spacing.S, AlignItems = FlexAlign.Center, Wrap = true, Children = kids.ToArray() };
    }
}
