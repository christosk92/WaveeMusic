// ── Platform/ModalFilePicker.Rules.cs ────────────────────────────────────────────────────────────────────────────────
// CORE, PURE: the system file dialogs' request vocabulary and threading contract (#155) — what is asked (open a file,
// pick a folder, save a file), the one-at-a-time gate and the watchdog's verdict. The impure halves are
// Platform/ModalFilePicker.cs (the dedicated STA thread and the shell dialog) and Platform/Pickers.cs (the UI-thread
// answer, the toasts, the watchdog's timer); Wavee.Tests/ModalFilePickerTests.cs pins this file.

namespace Wavee;

/// <summary>Which shell dialog.</summary>
public enum PickerKind : byte { OpenFile = 0, Folder, SaveFile }

/// <summary>One dialog request, as a value: what the dialog thread is handed (<see cref="ModalFilePicker.Dialog"/>).
/// <paramref name="Filters"/> are <c>(Name, Spec)</c> pairs, e.g. <c>("Audio", "*.mp3;*.flac")</c>; a folder picker
/// takes none. <paramref name="DefaultFileName"/> pre-fills a save dialog only.</summary>
public sealed record PickerRequest(PickerKind Kind, string Title, (string Name, string Spec)[] Filters, string DefaultFileName = "")
{
    public static PickerRequest Open(string title, params (string Name, string Spec)[] filters)
        => new(PickerKind.OpenFile, title, filters ?? []);

    public static PickerRequest Folder(string title) => new(PickerKind.Folder, title, []);

    public static PickerRequest Save(string title, string defaultFileName, params (string Name, string Spec)[] filters)
        => new(PickerKind.SaveFile, title, filters ?? [], defaultFileName ?? "");
}

public static class PickerRules
{
    /// <summary>How long a picker may stay invisible before the owner window is handed back (see <see cref="Watch"/>).</summary>
    public const int WatchdogMs = 8000;

    /// <summary>How often the watchdog looks again while a picker is still coming up.</summary>
    public const int WatchdogRecheckMs = 1000;

    public enum Verdict : byte { Done = 0, Wait, ReleaseOwner }

    /// <summary>THE WATCHDOG (the #155 symptom, made survivable). <c>IModalWindow.Show</c> disables the owner FIRST and
    /// shows the dialog only once the shell view behind it has navigated; when that stalls, the window is dead to every
    /// click (the "ding") with nothing on screen. Pickers run on their own thread, so Wavee keeps drawing — and once the
    /// dialog has stayed invisible for <see cref="WatchdogMs"/>, the owner is enabled again.</summary>
    public static Verdict Watch(bool pickerOpen, bool dialogVisible, bool ownerEnabled, long elapsedMs)
    {
        if (!pickerOpen || dialogVisible) return Verdict.Done;
        if (elapsedMs < WatchdogMs) return Verdict.Wait;
        return ownerEnabled ? Verdict.Done : Verdict.ReleaseOwner;
    }

    /// <summary>Is this watch still about the picker it was armed for? A later picker (that one closed, another opened)
    /// is not its business.</summary>
    public static bool SamePicker(bool open, int generationNow, int generationArmed) => open && generationNow == generationArmed;
}

/// <summary>ONE picker at a time, process-wide: a second dialog while one is up (or still coming up) must not stack
/// another modal on the first. Thread-safe; held from the click until the dialog thread returns.</summary>
public sealed class PickerGate
{
    int _held;

    public bool IsHeld => Volatile.Read(ref _held) != 0;

    public bool TryEnter() => Interlocked.CompareExchange(ref _held, 1, 0) == 0;

    public void Exit() => Volatile.Write(ref _held, 0);
}
