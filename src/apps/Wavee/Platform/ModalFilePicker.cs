// ── Platform/ModalFilePicker.cs ──────────────────────────────────────────────────────────────────────────────────────
// SHELL (Windows): the shell open-file / pick-folder / save-file dialogs, modal to the Wavee window, WITHOUT the Wavee
// window's thread (#155). EVERY system file dialog Wavee shows goes through here (Pickers.Pick on the UI thread).
//
// WHY NOT `FilePicker.OpenFile` ON THE UI THREAD. `IModalWindow.Show` is a blocking nested modal loop. Called from a click
// handler it runs INSIDE the engine's frame on the thread that also renders: it disables the owner window, builds the
// dialog and navigates its shell view (shell extensions, cloud-file providers, brokered calls) and only then shows it.
// Until it returns no Wavee frame is produced at all, and when anything before the dialog becomes visible stalls, the
// user is left with a disabled window that answers every click with the "ding" and no dialog anywhere — 0.2.10's
// "Change cover" report, verbatim.
//
// THE SHAPE HERE (the one Chromium's shell dialogs use): a DEDICATED STA thread per dialog, the Wavee HWND as the owner
// (the dialog is still owned and modal — `Show` disables the owner from its thread), and a Task the caller continues on
// the UI thread through `Spotify.Post`. The UI thread never waits on the dialog, so it keeps pumping, painting and
// playing; the cross-thread SendMessages `Show` makes to the owner (WM_CANCELMODE, WM_ENABLE, WM_ACTIVATE) are answered
// by the frame loop's pump. One picker at a time (`PickerGate`), and a watchdog (`PlaylistCoverRules.Watch`) hands the
// owner back if the dialog never becomes visible.

using System.Runtime.InteropServices;
using FluentGpu.WindowsApi.Dialogs;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace Wavee;

/// <summary>Run a blocking function on a NEW single-threaded-apartment thread and complete a task with its answer. The
/// caller never blocks; the task completes on that thread (continuations run asynchronously).</summary>
public static class StaThread
{
    public static Task<T> Run<T>(Func<T> work, string name)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.TrySetResult(work()); }
            catch (Exception ex) { done.TrySetException(ex); }
        })
        {
            Name = name,
            IsBackground = true,   // an open dialog never holds the process up at exit
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}

/// <summary>The shell's open-file, pick-folder and save-file dialogs, each on its own STA thread, owned by (and modal
/// to) the window handle it is given. Callers on the UI thread go through <see cref="Pickers.Pick"/>, which adds the
/// UI-thread answer, the busy toast and the watchdog.</summary>
public static unsafe class ModalFilePicker
{
    static readonly PickerGate s_gate = new();
    static int s_dialogThread;   // the native id of the thread a dialog is running on; 0 = none
    static int s_generation;

    /// <summary>Is a picker open (or still coming up)?</summary>
    public static bool IsOpen => s_gate.IsHeld;

    /// <summary>Bumped by every picker that opens: a watchdog armed for one picker must not judge the next.</summary>
    public static int Generation => Volatile.Read(ref s_generation);

    /// <summary>THE SEAM: what runs on the dialog thread — the shell dialog by default (<see cref="ShowShellDialog"/>).
    /// Tests replace it to pin the threading contract without a window.</summary>
    public static Func<nint, PickerRequest, string?> Dialog { get; set; } = ShowShellDialog;

    /// <summary>Show <paramref name="request"/>. Null when a picker is already open (the gate refused); otherwise a task
    /// that completes with the chosen path, or null on cancel, and faults when the dialog itself failed. Never blocks
    /// the caller.</summary>
    public static Task<string?>? ShowAsync(nint owner, PickerRequest request)
    {
        if (!s_gate.TryEnter()) return null;
        Interlocked.Increment(ref s_generation);
        var dialog = Dialog;
        try
        {
            return StaThread.Run(() =>
            {
                Volatile.Write(ref s_dialogThread, (int)GetCurrentThreadId());
                try { return dialog(owner, request); }
                finally
                {
                    Volatile.Write(ref s_dialogThread, 0);
                    s_gate.Exit();   // before the task completes: a continuation may open the next picker at once
                }
            }, "wavee-file-picker");
        }
        catch
        {
            s_gate.Exit();
            throw;
        }
    }

    /// <inheritdoc cref="ShowAsync"/>
    public static Task<string?>? OpenFileAsync(nint owner, string title, params (string Name, string Spec)[] filters)
        => ShowAsync(owner, PickerRequest.Open(title, filters));

    /// <inheritdoc cref="ShowAsync"/>
    public static Task<string?>? PickFolderAsync(nint owner, string title) => ShowAsync(owner, PickerRequest.Folder(title));

    /// <inheritdoc cref="ShowAsync"/>
    public static Task<string?>? SaveFileAsync(nint owner, string title, string defaultFileName, params (string Name, string Spec)[] filters)
        => ShowAsync(owner, PickerRequest.Save(title, defaultFileName, filters));

    /// <summary>The engine's blocking shell dialogs — called ONLY on the picker's own STA thread.</summary>
    static string? ShowShellDialog(nint owner, PickerRequest r) => r.Kind switch
    {
        PickerKind.Folder => FilePicker.PickFolder(owner, r.Title),
        PickerKind.SaveFile => FilePicker.SaveFile(owner, r.Title, r.DefaultFileName, r.Filters),
        _ => FilePicker.OpenFile(owner, r.Title, r.Filters),
    };

    /// <summary>Is a window of the dialog thread on screen? (The watchdog's question.)</summary>
    public static bool DialogVisible()
    {
        int thread = Volatile.Read(ref s_dialogThread);
        if (thread == 0) return false;
        int found = 0;
        EnumThreadWindows((uint)thread, &AnyVisible, (LPARAM)(nint)(&found));
        return found != 0;
    }

    [UnmanagedCallersOnly]
    static BOOL AnyVisible(HWND hwnd, LPARAM state)
    {
        if (!IsWindowVisible(hwnd)) return TRUE;
        *(int*)(nint)state = 1;
        return FALSE;   // stop enumerating
    }

    public static bool IsEnabled(nint hwnd) => hwnd != 0 && IsWindowEnabled((HWND)hwnd);

    /// <summary>Hand a window the dialog disabled back to the user (the watchdog's remedy). The dialog re-enables it
    /// again itself when it closes, so this is idempotent with it.</summary>
    public static void Release(nint hwnd)
    {
        if (hwnd != 0) EnableWindow((HWND)hwnd, TRUE);
    }
}
