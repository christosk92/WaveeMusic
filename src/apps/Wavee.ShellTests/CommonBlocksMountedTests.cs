// ── Wavee.ShellTests/CommonBlocksMountedTests.cs — the Common blocks and Classic's dialog bodies, mounted (themes plan 6.5, T11c) ─────────────
//
// `Themes\Common\` holds the views every theme shares (the history page, the Home customizer, the frame-rate overlay, diagnostics, logs; the not-found page is the
// content state's block), and Classic registers them with one call; the feedback and crash dialogs' bodies are Classic's own views over core halves
// (`IFeedbackForm`, the crash consent flow). What only a mounted tree can say:
//
//   registered   Classic's registry carries the Common blocks under their `common.*` template ids and the two dialog bodies under `classic.dialog.*`
//   history      the page asks the core for its projection under the view's own states and again whenever the log's version moves; an empty projection
//                mounts less than a listed one
//   feedback     the card opens its form once, starts the compose, installs its submit while mounted and drops it on unmount
//   crash        the card prepares the compose on the UI thread, installs its submit, and a Send before the preview exists says "preparing" and stays open

using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class CommonBlocksMountedTests(ShellHost host)
{
    static ViewHarnessOptions UnderClassic() => new() { Composition = ClassicTheme.Create() };

    static int Count(ViewHarness harness) => CountUnder(harness.Shell, harness.Shell.Host.Scene.Root);

    // ── registered ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_registers_the_common_blocks_and_its_dialog_bodies_under_their_template_ids()
    {
        var classic = ClassicTheme.Create();
        Assert.Equal("common.history", classic.Registry.TemplateIdOf(ViewKeys.HistoryPage.Id));
        Assert.Equal("common.customize", classic.Registry.TemplateIdOf(ViewKeys.HomeCustomize.Id));
        Assert.Equal("common.fps", classic.Registry.TemplateIdOf(ViewKeys.ShellFps.Id));
        Assert.Equal("classic.dialog.feedback", classic.Registry.TemplateIdOf(ViewKeys.DialogFeedback.Id));
        Assert.Equal("classic.dialog.crash", classic.Registry.TemplateIdOf(ViewKeys.DialogCrash.Id));
    }

    // ── history ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    static HistoryRowFacts Row(string title, bool reachable, Action? remove = null)
        => new(title, Icons.Clock, "Page", null, "12:00", 0, false, reachable, static () => { }, remove ?? (static () => { }));

    static HistoryPageModel Listed() => new()
    {
        TotalVisits = 2, UniqueRoutes = 2, Body = HistoryBodyKind.DateGroups,
        Sections = [new HistorySection("Today", "2 visits", [Row("Home", reachable: true), Row("Gone", reachable: false)])],
    };

    static HistoryPageModel Nothing() => new()
    {
        TotalVisits = 0, UniqueRoutes = 0, Body = HistoryBodyKind.Empty, Sections = [],
        EmptyTitle = "No history", EmptyCaption = "Start navigating",
    };

    [Fact]
    public void The_history_page_projects_under_its_own_states_and_again_when_the_log_moves()
    {
        var version = new Signal<int>(0);
        var asked = new List<(string Query, int Filter, int Sort)>();
        var props = new HistoryPageProps
        {
            Version = version,
            Project = (query, filter, sort) => { asked.Add((query, filter, sort)); return Listed(); },
        };
        ViewHarness.With(host, ViewKeys.HistoryPage, props,
            harness =>
            {
                harness.RunFrames(3);
                Assert.NotEmpty(asked);
                Assert.Equal(("", 0, 0), asked[0]);
                int before = asked.Count;

                version.Value++;
                harness.RunFrames(3);
                Assert.True(asked.Count > before, "the log changed: the page asks for its projection again");
            }, UnderClassic());
    }

    [Fact]
    public void An_empty_projection_mounts_the_vacancy_instead_of_the_cards()
    {
        int listed = 0, empty = 0;
        ViewHarness.With(host, ViewKeys.HistoryPage,
            new HistoryPageProps { Version = new Signal<int>(0), Project = static (_, _, _) => Listed() },
            harness => { harness.RunFrames(3); listed = Count(harness); }, UnderClassic());
        ViewHarness.With(host, ViewKeys.HistoryPage,
            new HistoryPageProps { Version = new Signal<int>(0), Project = static (_, _, _) => Nothing() },
            harness => { harness.RunFrames(3); empty = Count(harness); }, UnderClassic());
        Assert.True(listed > empty, "two rows in a card mount more than the vacancy");
    }

    // ── feedback ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    sealed class FixtureForm(Feedback.ReportKind kind) : IFeedbackForm
    {
        public int Composes, Cancels;
        readonly Signal<Feedback.ComposedReport?> _composed = new(null);

        public Feedback.ReportKind Kind => kind;
        public string InitialTitle => "";
        public bool IncludeLogsByDefault => true;
        public string[] Whens => ["On launch"];
        public string[] Reproduces => ["Every time"];
        public string[] Areas => ["playback", "Not sure"];
        public int DefaultArea => 1;
        public Feedback.ReportLabels Labels => new("Title", "When", "Reproduces", "Doing", "Happened", "Steps", "Expected", "Area", "Problem", "Proposal", "Alternatives", "Body");
        public int LogLineCount => 200;
        public IReadSignal<Feedback.ComposedReport?> Composed => _composed;
        public Action? BeginCompose(Action<Action> post) { Composes++; return () => Cancels++; }
        public string Preview(Feedback.ComposedReport? composed, in Feedback.ReportDraft draft, bool includeLogs) => "preview";
        public (string Message, InfoBarSeverity Severity) CrashNotice(Feedback.ComposedReport? composed) => ("reading", InfoBarSeverity.Informational);
        public bool Submit(in Feedback.ReportDraft draft, bool includeLogs, FluentGpu.Hooks.InputHooks hooks) => false;
        public void Copy(in Feedback.ReportDraft draft, bool includeLogs, FluentGpu.Hooks.InputHooks hooks) { }
        public void Save(in Feedback.ReportDraft draft, bool includeLogs) { }
    }

    sealed class FixtureDialog : IFeedbackDialog
    {
        public readonly List<FixtureForm> Forms = [];
        public Func<bool>? Submit;
        public void InstallSubmit(Func<bool>? submit) => Submit = submit;
        public IFeedbackForm OpenForm(Feedback.ReportKind kind) { var form = new FixtureForm(kind); Forms.Add(form); return form; }
    }

    [Fact]
    public void The_feedback_card_opens_one_form_starts_its_compose_and_holds_the_submit_while_mounted()
    {
        var dialog = new FixtureDialog();
        ViewHarness.With(host, ViewKeys.DialogFeedback, new FeedbackDialogProps { InitialKind = Feedback.ReportKind.Bug, Dialog = dialog },
            harness =>
            {
                harness.RunFrames(3);
                var form = Assert.Single(dialog.Forms);
                Assert.Equal(Feedback.ReportKind.Bug, form.Kind);
                Assert.Equal(1, form.Composes);
                Assert.NotNull(dialog.Submit);
                Assert.False(dialog.Submit!(), "the fixture form refuses: the dialog stays open");
            }, UnderClassic());
        Assert.Null(dialog.Submit);   // unmount drops it
    }

    // ── crash ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_crash_card_prepares_its_compose_and_waits_for_the_preview_before_it_sends()
    {
        var gate = new ManualResetEventSlim(false);
        Func<bool>? submit = null;
        int prepared = 0, preparing = 0;
        var sent = new List<(bool Always, bool Dump)>();
        var props = new CrashDialogProps
        {
            FromList = false, HeaderMessage = "10:00 · Crash", Lead = "Wavee closed unexpectedly", HasDump = true, IncludeDumpLabel = "Include a dump (1.0 MB)",
            IncludeDumpDefault = false, Configured = true, PrivacyUrl = "https://example.invalid/privacy",
            PrepareCompose = () => { prepared++; return () => { gate.Wait(5000); return "scrubbed"; }; },
            InstallSubmit = s => submit = s,
            Submit = (always, dump) => sent.Add((always, dump)),
            NotifyPreparing = () => preparing++,
            ReportOnGithub = static () => { },
        };
        ViewHarness.With(host, ViewKeys.DialogCrash, props,
            harness =>
            {
                harness.RunFrames(3);
                Assert.Equal(1, prepared);
                Assert.NotNull(submit);
                Assert.False(submit!(), "the preview has not composed: Send waits");
                Assert.Equal(1, preparing);
                Assert.Empty(sent);

                gate.Set();
                UntilBackground(harness.Shell, () => submit!(), "the scrubbed preview arrives and Send goes through");
                Assert.Single(sent);
                Assert.Equal((false, false), sent[0]);
            }, UnderClassic());
        Assert.Null(submit);   // unmount drops it
    }
}
