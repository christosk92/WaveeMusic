// ── Theming/Contracts/ViewKeys.FeedbackCrash.cs — the feedback and crash dialogs' bodies (themes plan 6.0, T11c) ─────────────────────────
//
// T11a owns the other nine dialogs; T11c owns these two (the plan's Dialogs row: `.Feedback`, `.Crash`). The dialog chrome stays core (the stock
// `ContentDialog`: title, buttons, the vetoable primary click); a key draws the BODY the core mounts as the dialog's content.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The report dialog's body: the subtitle, the kind switch and the form. Mounted by `Feedback.OpenDialog`. Props: <see cref="FeedbackDialogProps"/>.</summary>
    public static readonly ViewKey<FeedbackDialogProps> DialogFeedback = new("Dialog.Feedback");

    /// <summary>The crash / hang consent card's body. Mounted by `Crash.OpenPrompt`. Props: <see cref="CrashDialogProps"/>.</summary>
    public static readonly ViewKey<CrashDialogProps> DialogCrash = new("Dialog.Crash");
}
