// ── Platform/Pickers.cs ──────────────────────────────────────────────────────────────────────────────────────────────
// SHELL: the ONE way a UI-thread caller shows a system file dialog (open a file, pick a folder, save a file). The
// dialog runs on its own STA thread (ModalFilePicker), modal to the Wavee window; the answer comes back to the UI thread
// through Spotify.Post; a second dialog while one is open is refused with a toast; and the watchdog
// (PickerRules.Watch) hands the window back when a dialog never becomes visible. Never call FilePicker.* directly from
// the UI thread: its modal loop runs inside the engine's frame and Wavee stops drawing until it returns (#155).

using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Localization;

namespace Wavee;

public static class Pickers
{
    /// <summary>Show <paramref name="request"/> modal to the Wavee window, without blocking the UI thread. On the UI
    /// thread, later: <paramref name="done"/> with the chosen path (null when the user cancelled), or
    /// <paramref name="failed"/> when the dialog could not be shown. False when another picker is already open (the
    /// user has been told). <paramref name="slowMessage"/> replaces the watchdog's generic sentence.</summary>
    public static bool Pick(PickerRequest request, Action<string?> done, Action<Exception> failed, string? slowMessage = null)
    {
        nint owner = FluentApp.WindowHandle;
        Task<string?>? showing;
        try { showing = ModalFilePicker.ShowAsync(owner, request); }
        catch (Exception ex)
        {
            failed(ex);
            return true;
        }
        if (showing is null)
        {
            Notify.Say(Loc.Get(Strings.FilePicker.AlreadyOpen), InfoBarSeverity.Informational, dedupeKey: "file-picker");
            return false;
        }
        Arm(owner, Environment.TickCount64, ModalFilePicker.Generation, slowMessage, PickerRules.WatchdogMs);
        showing.ContinueWith(t => Spotify.Post(() =>
        {
            if (t.IsFaulted) failed(t.Exception!.GetBaseException());
            else done(t.Result);
        }), TaskScheduler.Default);
        return true;
    }

    static void Arm(nint owner, long started, int generation, string? slowMessage, int delayMs)
        => _ = Task.Delay(delayMs).ContinueWith(_ => Spotify.Post(() => Watch(owner, started, generation, slowMessage)),
            TaskScheduler.Default);

    static void Watch(nint owner, long started, int generation, string? slowMessage)
    {
        bool open = PickerRules.SamePicker(ModalFilePicker.IsOpen, ModalFilePicker.Generation, generation);
        var verdict = PickerRules.Watch(open, ModalFilePicker.DialogVisible(), ModalFilePicker.IsEnabled(owner),
            Environment.TickCount64 - started);
        if (verdict == PickerRules.Verdict.Wait) { Arm(owner, started, generation, slowMessage, PickerRules.WatchdogRecheckMs); return; }
        if (verdict != PickerRules.Verdict.ReleaseOwner) return;
        Log.Warn("ui", "file picker still invisible after " + PickerRules.WatchdogMs + " ms; window re-enabled");
        ModalFilePicker.Release(owner);
        Notify.Say(slowMessage ?? Loc.Get(Strings.FilePicker.Slow), InfoBarSeverity.Warning, dedupeKey: "file-picker");
    }
}
