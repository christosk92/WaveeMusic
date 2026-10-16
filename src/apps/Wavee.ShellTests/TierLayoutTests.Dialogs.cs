// ── Wavee.ShellTests/TierLayoutTests.Dialogs.cs — the report and crash dialogs at every tier size and threshold edge (themes plan 3.5; TC8) ────────
//
// The REAL bodies (`Dialog.Feedback`, `Dialog.Crash`, Classic's views) in the REAL card: opened through `OverlayFit.ShowForm`, the door both core openers use, into a
// headless overlay host sized to each plan 3.5 window and each threshold edge of Classic's ladder (the tier facts are resolved from the size, with the direction the
// edge is reached from, as a launch or a navigation does). For each, on the records and the node geometry, never on pixels:
//
//   · the card's width: the window less 16 where it fills (Compact) or is narrower than its own 548, the board's 1040 two-pane card at Huge;
//   · the record in use: the second pane (divider) exists only at Huge, the folded "Report preview" row only at Compact, and the body is as wide as the record says;
//   · the card lies inside the window and the body never runs past the card (no horizontal overflow);
//   · at Compact every hit target in the card is at least 40 x 40 (the lifted command row included: the engine's own are 32);
//   · at 330 tall the card clamps to the window and its command row stays on screen, under the body that scrolls.
//
// The dialogs are opened by core code from an event handler, outside any render: the harness publishes the tiers the opener reads (as `ClassicOverlayTierTests` does).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed partial class TierLayoutTests
{
    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Hands the test the overlay service the headless host provides to the tree.</summary>
    sealed class DialogOverlayCapture(Action<IOverlayService> got) : Component
    {
        public override Element Render()
        {
            got(UseContext(Overlay.Service));
            return new BoxEl();
        }
    }

    /// <summary>A report form with a long preview (so the body is taller than a short window) and no composed report.</summary>
    sealed class FixtureReportForm(Feedback.ReportKind kind) : IFeedbackForm
    {
        readonly Signal<Feedback.ComposedReport?> _composed = new(null);

        public Feedback.ReportKind Kind => kind;
        public string InitialTitle => "";
        public bool IncludeLogsByDefault => kind is Feedback.ReportKind.Bug or Feedback.ReportKind.Crash;
        public string[] Whens => ["On launch", "While playing"];
        public string[] Reproduces => ["Every time", "Sometimes"];
        public string[] Areas => ["Player bar", "Not sure"];
        public int DefaultArea => 1;
        public Feedback.ReportLabels Labels => new("Title", "When", "Reproduces", "What were you doing", "What happened", "Steps to reproduce", "Expected behaviour",
            "Area", "Problem", "Proposal", "Alternatives", "Body");
        public int LogLineCount => 200;
        public IReadSignal<Feedback.ComposedReport?> Composed => _composed;
        public Action? BeginCompose(Action<Action> post) => null;
        public string Preview(Feedback.ComposedReport? composed, in Feedback.ReportDraft draft, bool includeLogs)
            => string.Join("\n", Enumerable.Repeat("### Bug: the queue disappears from the player bar when the window is narrow", 30));
        public (string Message, InfoBarSeverity Severity) CrashNotice(Feedback.ComposedReport? composed) => ("Reading the crash report", InfoBarSeverity.Informational);
        public bool Submit(in Feedback.ReportDraft draft, bool includeLogs, InputHooks hooks) => true;
        public void Copy(in Feedback.ReportDraft draft, bool includeLogs, InputHooks hooks) { }
        public void Save(in Feedback.ReportDraft draft, bool includeLogs) { }
    }

    sealed class FixtureReportDialog : IFeedbackDialog
    {
        public void InstallSubmit(Func<bool>? submit) { }
        public IFeedbackForm OpenForm(Feedback.ReportKind kind) => new FixtureReportForm(kind);
    }

    static CrashDialogProps CrashPrompt() => new()
    {
        FromList = false,
        HeaderMessage = "9:41 PM · System.InvalidOperationException: the queue changed while it was being enumerated",
        Lead = "Wavee closed unexpectedly. Sending the report helps fix it.",
        HasDump = true,
        IncludeDumpLabel = "Include a memory snapshot (12.4 MB)",
        IncludeDumpDefault = false,
        Configured = true,
        PrivacyUrl = "https://example.invalid/privacy",
        PrepareCompose = static () => static () => string.Join("\n", Enumerable.Repeat("Wavee closed unexpectedly: [EXCEPTION TYPE] at [STACK FRAME]", 30)),
        InstallSubmit = static _ => { },
        Submit = static (_, _) => { },
        NotifyPreparing = static () => { },
        ReportOnGithub = static () => { },
    };

    // ── the helper every dialog test runs ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The tier facts of <paramref name="size"/>: a fresh resolve, or the edge reached from its <c>From</c> width.</summary>
    static TierFacts FactsOf(TierSize size)
        => size.From is { } from
            ? WindowTiers.Resolve(ClassicTiers.Policy, size.Width, size.Height, size.Width,
                previous: WindowTiers.Resolve(ClassicTiers.Policy, from, size.Height, from))
            : WindowTiers.Resolve(ClassicTiers.Policy, size.Width, size.Height, size.Width);

    /// <summary>Open a form dialog at <paramref name="sizeName"/> through <c>OverlayFit.ShowForm</c> and run the assertions every form dialog holds.</summary>
    /// <param name="show">Opens the dialog the way its core opener does.</param>
    /// <param name="bodyKey">The body's root key (its width is the record's).</param>
    /// <param name="dividerKey">The key of the divider between the two panes (present at Huge only).</param>
    /// <param name="foldKey">The key of the folded preview row (present at Compact only), or null where the dialog has none.</param>
    void AssertFormDialog(string sizeName, Action<IOverlayService> show, string bodyKey, string dividerKey, string? foldKey)
    {
        var size = Sizes[sizeName];
        host.Run(() =>
        {
            TierFacts tiers = FactsOf(size);
            ThemeHost.PublishTiers(in tiers);
            IOverlayService? overlay = null;
            using var shell = Shell.Headless.Mount(() => OverlayHost.Create(Embed.Comp(() => new DialogOverlayCapture(s => overlay = s))), size.Width, size.Height);
            shell.RunFrames(3);
            Assert.NotNull(overlay);
            show(overlay!);
            shell.RunFrames(40);

            var scene = shell.Host.Scene;
            RectF Rect(NodeHandle n) => scene.PresentedAbsoluteRect(n);
            var record = ClassicScreenDialogTiers.Of(tiers);
            var fit = ClassicOverlayTiers.For(size.Window).Core.Dialog;
            bool huge = size.Window == WidthTier.Huge;
            bool compact = size.Window == WidthTier.Compact;

            // The card: the widest node narrower than the window (a full-window scrim lies under it).
            var all = Subtree(scene, scene.Root).ToList();
            var card = all.Select(Rect).Where(r => r.W < size.Width - 0.5f && r.W > 0f).OrderByDescending(r => r.W).First();
            float cardWidth = MathF.Min(huge ? ClassicScreenDialogTiers.HugeCardWidth : 548f, size.Width - 32f);
            Assert.True(MathF.Abs(card.W - cardWidth) <= 1f, $"{size.Name}: the card is {card.W}, not {cardWidth}");
            Assert.True(card.X >= -0.5f && card.Right <= size.Width + 0.5f && card.Y >= -0.5f && card.Bottom <= size.Height + 0.5f,
                $"{size.Name}: the card {card.X:0.#},{card.Y:0.#} {card.W:0.#}x{card.H:0.#} leaves the window");
            Assert.True(card.H <= fit.MaxHeightFor(size.Height) + 0.5f, $"{size.Name}: the card ({card.H:0.#}) is taller than its cap {fit.MaxHeightFor(size.Height)}");

            // The record in use: the second pane at Huge only, the folded row at Compact only.
            Assert.Equal(huge, record.TwoPane);
            Assert.Equal(huge, !shell.Find(dividerKey).IsNull);
            if (foldKey is not null) Assert.Equal(compact, !shell.Find(foldKey).IsNull);

            // The body is as wide as the record says and nothing in it runs past the card.
            var body = Node(shell, bodyKey);
            float bodyWidth = ClassicScreenDialogTiers.BodyWidth(record, size.Width);
            Assert.True(MathF.Abs(Rect(body).W - bodyWidth) <= 1f, $"{size.Name}: the body is {Rect(body).W}, not the record's {bodyWidth}");
            foreach (var n in Subtree(scene, body))
            {
                if (scene.InCollapsedSubtree(n)) continue;
                var r = Rect(n);
                if (r.W <= 0f) continue;
                Assert.True(r.X >= card.X - 0.5f && r.Right <= card.Right + 0.5f, $"{size.Name}: a part {r.X:0.#}..{r.Right:0.#} runs outside the card {card.X:0.#}..{card.Right:0.#}");
            }

            // The command row: two buttons under the body, inside the card, whatever the window's height.
            var commands = all.Where(n => scene.Interaction(n).Role == AutomationRole.Button).Select(Rect)
                .Where(r => r.Bottom >= card.Bottom - 100f && r.Bottom <= card.Bottom + 0.5f && r.X >= card.X - 0.5f && r.Right <= card.Right + 0.5f).ToList();
            Assert.True(commands.Count >= 2, $"{size.Name}: the card keeps its two commands on screen (found {commands.Count})");
            Assert.True(commands.All(r => r.Y >= card.Y - 0.5f), $"{size.Name}: a command is pushed out of the card");

            if (compact) AssertCardTargets(scene, all, card, size);
            overlay!.CloseAll();
            shell.RunFrames(5);
        });
    }

    /// <summary>At Compact every hit target in the card (the lifted command row, the fields, the check boxes, the links, the pickers) is at least 40 x 40.</summary>
    static void AssertCardTargets(SceneStore scene, List<NodeHandle> all, RectF card, TierSize size)
    {
        int targets = 0;
        foreach (var n in all)
        {
            if (scene.InCollapsedSubtree(n)) continue;
            var flags = scene.Flags(n);
            if ((flags & NodeFlags.Focusable) == 0 || (flags & NodeFlags.Visible) == 0) continue;
            var r = scene.PresentedAbsoluteRect(n);
            if (r.W <= 0f || r.H <= 0f || r.X < card.X - 0.5f || r.Right > card.Right + 0.5f || r.Y < card.Y - 0.5f || r.Bottom > card.Bottom + 0.5f) continue;
            targets++;
            Assert.True(r.W >= 40f - 0.5f && r.H >= 40f - 0.5f, $"{size.Name}: a dialog target is {r.W:0.#} x {r.H:0.#}, under 40 x 40");
        }
        // At the 330-tall minimum the body scrolls inside the card, so only the command row (two targets) is certain to be on screen.
        int least = size.Height <= TierPolicy.WindowMinHeight ? 2 : 3;
        Assert.True(targets >= least, $"{size.Name}: the Compact card offers its targets (found {targets})");
    }

    // ── the report dialog ────────────────────────────────────────────────────────────────────────────────────────────────

    static void ShowReport(IOverlayService overlay, Feedback.ReportKind kind)
        => OverlayFit.ShowForm(overlay, 548f, d =>
        {
            d.Title = "Report a problem";
            d.Content = Slots.View(ViewKeys.DialogFeedback, new FeedbackDialogProps { InitialKind = kind, Dialog = new FixtureReportDialog() })
                with { Key = "report-body:" + (int)kind };
            d.PrimaryText = "Open GitHub";
            d.CloseText = "Cancel";
            d.DefaultButton = ContentDialog.DefaultBtn.Primary;
            d.PrimaryButtonClick = static _ => { };
        });

    [Theory, MemberData(nameof(PageSizes))]
    public void The_report_dialog_holds_its_record_at_every_tier(string size)
        => AssertFormDialog(size, o => ShowReport(o, Feedback.ReportKind.Bug), "report:body", "report:divider", "report:fold");

    [Theory, MemberData(nameof(PageSizes))]
    public void The_crash_report_form_holds_its_record_at_every_tier(string size)
        => AssertFormDialog(size, o => ShowReport(o, Feedback.ReportKind.Crash), "report:body", "report:divider", "report:fold");

    // ── the crash prompt ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(PageSizes))]
    public void The_crash_prompt_holds_its_record_at_every_tier(string size)
        => AssertFormDialog(size, overlay => OverlayFit.ShowForm(overlay, 548f, d =>
        {
            d.Title = "Wavee closed unexpectedly";
            d.Content = Slots.View(ViewKeys.DialogCrash, CrashPrompt()) with { Key = "crash-prompt:test" };
            d.PrimaryText = "Send report";
            d.CloseText = "Not now";
            d.DefaultButton = ContentDialog.DefaultBtn.Primary;
            d.PrimaryButtonClick = static _ => { };
        }), "crash:body", "crash:divider", foldKey: null);
}
