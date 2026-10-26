// ── Theming/Contracts/FeedbackCrashProps.cs — the props of the feedback and crash dialogs' bodies (themes plan 6.0, T11c) ─────────────────
//
// The two dialogs' CHROME (the stock `ContentDialog`: title, the Send / Not now buttons, the veto) stays core; the BODY is the key's picture.
//
// Feedback (`Dialog.Feedback`): the subtitle, the kind switch (never in crash mode) and the form for the kind shown. The form's fields (the title, the
// kind's three free-text boxes, the three dropdown indices, the include-logs box) are the VIEW's own signals; what the core keeps is everything that
// decides or does: composing the redacted report off the UI thread (`IFeedbackForm.BeginCompose`), the live preview text, the submit gate (still
// composing / no title), and the three doors (open the prefilled GitHub form, copy, save as).
//
// Crash (`Dialog.Crash`): the consent card of a crash or hang bundle (also Settings > Privacy & diagnostics > Saved reports' per-row "Send..."): the
// header bar, the "what is sent" box with its off-thread scrubbed preview, the dump and always-send boxes, the privacy and GitHub links. The consent
// flow (the launch decision, the upload queue, the outcome toasts) is core; the view hands its choices to `Submit`.

using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.DialogFeedback"/>. The core builds one per open dialog; the dialog keys the body by the kind shown.</summary>
public sealed record FeedbackDialogProps
{
    /// <summary>The kind the dialog opened on (a crash kind or a crash-report prefill forces the fixed Crash form).</summary>
    public required Feedback.ReportKind InitialKind { get; init; }

    /// <summary>The open dialog: the veto handshake with the chrome and the form factory.</summary>
    public required IFeedbackDialog Dialog { get; init; }
}

/// <summary>One open feedback dialog (core): the handshake with the dialog chrome and the factory of the forms the body mounts.</summary>
public interface IFeedbackDialog
{
    /// <summary>The mounted card installs its submit here (null on unmount): the dialog's primary button asks it, and a false answer keeps the
    /// dialog open (the card said why with a toast).</summary>
    void InstallSubmit(Func<bool>? submit);

    /// <summary>The form of one kind: the view asks once per card mount (a kind switch remounts the card, so a fresh form).</summary>
    IFeedbackForm OpenForm(Feedback.ReportKind kind);
}

/// <summary>One report form of one fixed kind (core): the compose, the preview, the submit gate and the three doors.</summary>
public interface IFeedbackForm
{
    /// <summary>The form's kind.</summary>
    Feedback.ReportKind Kind { get; }

    /// <summary>The title box's starting text (a prefill's title, else empty).</summary>
    string InitialTitle { get; }

    /// <summary>The include-logs box's starting value (Crash and Bug only).</summary>
    bool IncludeLogsByDefault { get; }

    /// <summary>The "when" dropdown's options (verbatim issue-form strings).</summary>
    string[] Whens { get; }

    /// <summary>The "reproduces" dropdown's options.</summary>
    string[] Reproduces { get; }

    /// <summary>The "area" dropdown's options.</summary>
    string[] Areas { get; }

    /// <summary>The "area" dropdown's starting index ("Not sure").</summary>
    int DefaultArea { get; }

    /// <summary>The field labels of this form (live localization).</summary>
    Feedback.ReportLabels Labels { get; }

    /// <summary>How many log lines the report carries (the include-logs box names it).</summary>
    int LogLineCount { get; }

    /// <summary>The composed, redacted report (null while composing). A subscribing read.</summary>
    IReadSignal<Feedback.ComposedReport?> Composed { get; }

    /// <summary>Starts the off-thread compose (file I/O and the whole-text redaction pass); returns the cancel for the effect's cleanup.</summary>
    Action? BeginCompose(Action<Action> post);

    /// <summary>The preview text for the answers so far ("Preparing..." while composing).</summary>
    string Preview(Feedback.ComposedReport? composed, in Feedback.ReportDraft draft, bool includeLogs);

    /// <summary>The crash form's header bar: the message and severity of the three honest states (still reading, a report with its summary, no report).</summary>
    (string Message, InfoBarSeverity Severity) CrashNotice(Feedback.ComposedReport? composed);

    /// <summary>The primary button: false (and a toast) while composing or when the title is missing; otherwise the clipboard, the file beside the
    /// logs, the prefilled form in the browser and the "paste it" toast, and true.</summary>
    bool Submit(in Feedback.ReportDraft draft, bool includeLogs, InputHooks hooks);

    /// <summary>Copy the full report.</summary>
    void Copy(in Feedback.ReportDraft draft, bool includeLogs, InputHooks hooks);

    /// <summary>Save the full report as a text file (the picker runs on its own thread).</summary>
    void Save(in Feedback.ReportDraft draft, bool includeLogs);
}

/// <summary>The props of <see cref="ViewKeys.DialogCrash"/>: the crash / hang consent card's body. Built per open dialog; the dialog keys it by report id.</summary>
public sealed record CrashDialogProps
{
    /// <summary>The Saved reports list's manual send: the bundle may be days old, so the header shows its date and there is no lead and no "always send" box.</summary>
    public required bool FromList { get; init; }

    /// <summary>The header bar's message (when, the exception type and message).</summary>
    public required string HeaderMessage { get; init; }

    /// <summary>The lead line under the header (empty from the list).</summary>
    public required string Lead { get; init; }

    /// <summary>The bundle carries a memory dump.</summary>
    public required bool HasDump { get; init; }

    /// <summary>The include-dump box's label (names the dump's size).</summary>
    public required string IncludeDumpLabel { get; init; }

    /// <summary>The include-dump box's starting value (the user's stored choice, when there is a dump).</summary>
    public required bool IncludeDumpDefault { get; init; }

    /// <summary>The upload service is configured: false disables nothing here but adds the "not configured" line (the dialog chrome disables Send).</summary>
    public required bool Configured { get; init; }

    /// <summary>The privacy page the first link opens.</summary>
    public required string PrivacyUrl { get; init; }

    /// <summary>UI thread: reads the live account and device seams and returns the pool-thread function that composes the scrubbed preview text.</summary>
    public required Func<Func<string>> PrepareCompose { get; init; }

    /// <summary>The dialog chrome's veto handshake: the card installs its submit (null on unmount); a false answer keeps the dialog open.</summary>
    public required Action<Func<bool>?> InstallSubmit { get; init; }

    /// <summary>Send with the card's choices: (always send, include the dump).</summary>
    public required Action<bool, bool> Submit { get; init; }

    /// <summary>The "preparing" toast (the preview has not finished composing: Send waits).</summary>
    public required Action NotifyPreparing { get; init; }

    /// <summary>"Report on GitHub instead": the manual report dialog for this bundle, and this dialog closes.</summary>
    public required Action ReportOnGithub { get; init; }
}
