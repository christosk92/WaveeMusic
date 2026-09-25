// ── Screens/Recovery.Win32.cs ──────────────────────────────────────────────────────────────────────────────────────
// Engine-free recovery mode's WIN32 half (WP-D): `TaskDialogIndirect` (comctl32 v6, made available by the new
// `app.manifest`'s `Microsoft.Windows.Common-Controls` dependency) draws the dialog; a `MessageBoxW` chain is the
// fallback when `TaskDialogIndirect` itself is unavailable or fails (comctl32 v5, a manifest that did not load, an
// OS quirk). `Recovery.Run` is the ONLY public surface; everything else is a private helper. Runs on the calling STA
// thread from `App.Main`, BEFORE `Platform.Boot` and before any window, D3D device or `Signal<T>` exists — this file
// references neither FluentGpu.Windows nor a single engine type, by design: recovery must still work when whatever
// broke the last three launches is the engine itself.
//
// Every native call here is best-effort: a failed dialog, a failed notepad/explorer launch, a failed clipboard write
// degrade to "try the next thing" or the MessageBoxW fallback, never to an unhandled exception — this screen is the
// user's last resort, so it is not allowed to crash itself.
//
// Role: PLATFORM (engine-free, Win32)
// Owner: WP-D
// Spec: docs/plans/wavee/crash-diagnostics-implementation.md §B.8, §D, §F "D · Recovery", §G "Boot loop", §I "WP-D"

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Wavee;

public static partial class Recovery
{
    /// <summary>Entered from <c>App.Main</c>: <c>--recovery</c> (<see cref="Reason.Switch"/>), a boot loop
    /// (<see cref="Reason.BootLoop"/>, <paramref name="bootFailures"/> from the <c>logs\crashootfailures</c> file), or
    /// <c>Platform.Boot()</c> itself throwing (<see cref="Reason.BootFailed"/>). Loops until the user picks Start or
    /// Quit; every other action (View/Open folder/Copy/Send/Reset-cancelled) re-shows the main dialog.</summary>
    public static Outcome Run(Reason reason, int bootFailures, Crash.BundleInfo? latest, string logFolder)
    {
        bool hasBundle = latest is not null;
        bool hasIngest;
        try { hasIngest = Crash.Uploader.Configured; }
        catch { hasIngest = false; }   // the uploader must never be why recovery mode itself fails to show

        while (true)
        {
            var actions = Actions.For(reason, hasBundle, hasIngest);
            ActionKind picked = ShowMain(reason, bootFailures, latest, actions, hasIngest);

            switch (picked)
            {
                case ActionKind.Start:
                    return Outcome.StartNormally;

                case ActionKind.View:
                    if (latest is not null) OpenInNotepad(latest.ReportTxt);
                    continue;

                case ActionKind.OpenFolder:
                    OpenFolder(latest?.Dir ?? Crash.Files.Root(logFolder));
                    continue;

                case ActionKind.Copy:
                    if (latest is not null) CopyBundleToClipboard(latest);
                    continue;

                case ActionKind.Send:
                    if (latest is not null) DoSend(latest);
                    continue;

                case ActionKind.Reset:
                    if (ConfirmReset() && DoReset()) return Outcome.Quit;
                    continue;

                case ActionKind.Quit:
                default:
                    return Outcome.Quit;
            }
        }
    }

    // ── 1. the main dialog ──────────────────────────────────────────────────────────────────────────────────────────

    static ActionKind ShowMain(Reason reason, int bootFailures, Crash.BundleInfo? latest, IReadOnlyList<ActionKind> actions, bool hasIngest)
    {
        string heading = Text.Title;
        string body = Text.BodyFor(reason, bootFailures);
        if (latest is not null) body += "\n\n" + Text.LastReport(latest);

        var buttons = new List<(int Id, string Title, string? Subtitle)>(3);
        foreach (var a in actions)
        {
            switch (a)
            {
                case ActionKind.Start: buttons.Add((ButtonIds.Start, Text.StartTitle, Text.StartSubtitle)); break;
                case ActionKind.Send: buttons.Add((ButtonIds.Send, Text.SendTitle, Text.SendSubtitle)); break;
                case ActionKind.Reset: buttons.Add((ButtonIds.Reset, Text.ResetTitle, Text.ResetSubtitle)); break;
            }
        }
        // The footer row (View/Open folder/Copy/Quit) has no small-push-button equivalent in TaskDialogIndirect once
        // TDF_USE_COMMAND_LINKS is set (every entry in pButtons renders the same way; there is no native way to mix
        // command-link-styled and plain-push-styled buttons in one call) — they ride in the SAME button array,
        // styled as command links too. See the WP-D report's "contract deviation" note.
        foreach (var a in actions)
        {
            switch (a)
            {
                case ActionKind.View: buttons.Add((ButtonIds.View, Text.FooterView, null)); break;
                case ActionKind.OpenFolder: buttons.Add((ButtonIds.OpenFolder, Text.FooterOpenFolder, null)); break;
                case ActionKind.Copy: buttons.Add((ButtonIds.Copy, Text.FooterCopy, null)); break;
                case ActionKind.Quit: buttons.Add((ButtonIds.Quit, Text.FooterQuit, null)); break;
            }
        }

        string footer = "<A HREF=\"" + Text.PrivacyUrl + "\">" + Text.PrivacyLink + "</A>";
        string? verification = hasIngest ? Text.AlwaysSend : null;

        if (TryShowTaskDialog(heading, body, buttons, footer, verification, warningIcon: true, useCommandLinks: true,
                out int buttonId, out bool verificationChecked))
        {
            if (verificationChecked) TrySetAlwaysSend();
            return ActionFor(buttonId) ?? ActionKind.Quit;
        }

        return ShowMainFallback(reason, bootFailures, latest, actions);
    }

    /// <summary>The <c>MessageBoxW</c> chain used when <c>TaskDialogIndirect</c> itself is unavailable: Yes = Start,
    /// No = a second box offering Send (when allowed) then Reset, Cancel/closed = Quit — simple but working, per §I.</summary>
    static ActionKind ShowMainFallback(Reason reason, int bootFailures, Crash.BundleInfo? latest, IReadOnlyList<ActionKind> actions)
    {
        string body = Text.BodyFor(reason, bootFailures);
        if (latest is not null) body += "\n\n" + Text.LastReport(latest);
        body += "\n\nYes = " + Text.StartTitle + "   No = more options   Cancel = " + Text.FooterQuit;

        int r = MessageBoxW(0, body, Text.Title, MbYesNoCancel | MbIconWarning);
        switch (r)
        {
            case IdYes: return ActionKind.Start;
            case IdCancel: return ActionKind.Quit;
            case IdNo:
                bool canSend = actions.Contains(ActionKind.Send) && latest is not null;
                string subBody = canSend
                    ? "Yes = " + Text.SendTitle + "   No = " + Text.ResetTitle
                    : "Yes = " + Text.ResetTitle + "   No = back";
                int r2 = MessageBoxW(0, subBody, Text.Title, MbYesNoCancel | MbIconWarning);
                if (r2 == IdCancel) return ActionKind.Quit;
                if (canSend) return r2 == IdYes ? ActionKind.Send : ActionKind.Reset;
                return r2 == IdYes ? ActionKind.Reset : ActionKind.Quit;
            default:
                return ActionKind.Quit;
        }
    }

    static void TrySetAlwaysSend()
    {
        try { Platform.Settings.Set(Platform.Keys.CrashReporting, 2); }
        catch { /* settings may be unavailable this early (Reason.BootFailed); the checkbox is best-effort */ }
    }

    // ── 2. View / Open folder / Copy ────────────────────────────────────────────────────────────────────────────────

    static void OpenInNotepad(string path)
    {
        try { Process.Start(new ProcessStartInfo("notepad.exe", QuoteArg(path)) { UseShellExecute = false }); }
        catch { }
    }

    static void OpenFolder(string dir)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", QuoteArg(dir)) { UseShellExecute = false }); }
        catch { }
    }

    static string QuoteArg(string s) => "\"" + s + "\"";

    static void CopyBundleToClipboard(Crash.BundleInfo bundle)
    {
        string raw;
        try { raw = File.ReadAllText(bundle.ReportTxt); }
        catch { return; }
        string scrubbed = Feedback.ReportRedactor.Redact(raw, Feedback.RedactionRules.None);
        CopyToClipboard(scrubbed);
    }

    // ── 3. Send ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static void DoSend(Crash.BundleInfo bundle)
    {
        bool confirmed = ShowSendConfirm(bundle, out bool includeDump);
        if (!confirmed) return;

        Crash.SendRecord result;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            result = Crash.Uploader.SendNow(bundle, includeDump, cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            result = new Crash.SendRecord(Crash.SendState.Failed, null, ex.Message, 1, includeDump);
        }

        ShowSendResult(result, bundle.Summary.ReportId);
    }

    static bool ShowSendConfirm(Crash.BundleInfo bundle, out bool includeDump)
    {
        includeDump = false;
        var buttons = new List<(int, string, string?)> { (1, Text.SendYes, null), (0, Text.SendNo, null) };
        string? verification = bundle.HasDump ? Text.IncludeDump : null;

        if (TryShowTaskDialog(Text.SendConfirmTitle, Text.SendConfirmBody + "\n\n" + Text.LastReport(bundle), buttons,
                footer: null, verification, warningIcon: false, useCommandLinks: false,
                out int buttonId, out bool verificationChecked))
        {
            includeDump = verificationChecked;
            return buttonId == 1;
        }

        int r = MessageBoxW(0, Text.SendConfirmBody, Text.SendConfirmTitle, MbYesNo | MbIconInformation);
        return r == IdYes;
    }

    static void ShowSendResult(Crash.SendRecord result, string reportId)
    {
        string text = result.State == Crash.SendState.Sent
            ? Text.SentResult(reportId)
            : Text.FailedResult(result.Error ?? "");
        var buttons = new List<(int, string, string?)> { (1, "OK", null) };
        if (!TryShowTaskDialog(Text.SendConfirmTitle, text, buttons, footer: null, verification: null,
                warningIcon: result.State != Crash.SendState.Sent, useCommandLinks: false, out _, out _))
            MessageBoxW(0, text, Text.SendConfirmTitle, MbOk | (result.State == Crash.SendState.Sent ? MbIconInformation : MbIconWarning));
    }

    // ── 4. Reset ─────────────────────────────────────────────────────────────────────────────────────────────────────

    static bool ConfirmReset()
    {
        var buttons = new List<(int, string, string?)> { (0, "Cancel", null), (1, "Reset", null) };
        if (TryShowTaskDialog(Text.ResetConfirmTitle, Text.ResetConfirmBody, buttons, footer: null, verification: null,
                warningIcon: true, useCommandLinks: false, out int buttonId, out _))
            return buttonId == 1;

        int r = MessageBoxW(0, Text.ResetConfirmBody, Text.ResetConfirmTitle, MbYesNo | MbIconWarning);
        return r == IdYes;
    }

    /// <summary>Writes the factory-reset marker directly (the engine/settings host is not necessarily up — this is
    /// exactly what <c>Platform.RequestFactoryResetAndRelaunch</c> would do, minus the log call and the
    /// <c>--relaunch-after</c> broker: recovery mode's own process is about to exit anyway, so a plain relaunch with
    /// no args is enough) and relaunches. Returns false (never armed) on any I/O failure, after telling the user.</summary>
    static bool DoReset()
    {
        try
        {
            var lines = FactoryResetPlan.MarkerLines(null, Platform.FactoryResetDefaultRoots());
            string marker = Platform.FactoryResetMarkerPath;
            string? dir = Path.GetDirectoryName(marker);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllLines(marker, lines);
        }
        catch (Exception ex)
        {
            ShowSimpleError(Text.ResetFailedTitle, ex.Message);
            return false;
        }

        try
        {
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
        }
        catch { /* the marker is already on disk; a manual relaunch still applies it */ }

        return true;
    }

    static void ShowSimpleError(string title, string message)
    {
        var buttons = new List<(int, string, string?)> { (1, "OK", null) };
        if (!TryShowTaskDialog(title, message, buttons, footer: null, verification: null,
                warningIcon: true, useCommandLinks: false, out _, out _))
            MessageBoxW(0, message, title, MbOk | MbIconError);
    }

    // ── 5. clipboard ─────────────────────────────────────────────────────────────────────────────────────────────────

    static unsafe void CopyToClipboard(string text)
    {
        try
        {
            int chars = text.Length + 1;
            nint hGlobal = GlobalAlloc(GmemMoveable, (nuint)(chars * 2));
            if (hGlobal == 0) return;

            nint p = GlobalLock(hGlobal);
            if (p == 0) { GlobalFree(hGlobal); return; }
            try
            {
                var dest = new Span<char>((void*)p, chars);
                text.AsSpan().CopyTo(dest);
                dest[text.Length] = '\0';
            }
            finally { GlobalUnlock(hGlobal); }

            if (OpenClipboard(0))
            {
                try
                {
                    EmptyClipboard();
                    if (SetClipboardData(CfUnicodeText, hGlobal) == 0) GlobalFree(hGlobal);
                    // On success the clipboard now owns hGlobal — it must NOT be freed here.
                }
                finally { CloseClipboard(); }
            }
            else GlobalFree(hGlobal);
        }
        catch { }
    }

    // ── 6. TaskDialogIndirect ────────────────────────────────────────────────────────────────────────────────────────

    const uint TdfEnableHyperlinks = 0x0001, TdfAllowDialogCancellation = 0x0008, TdfUseCommandLinks = 0x0010,
        TdfSizeToContent = 0x01000000;
    const nint TdWarningIcon = 0xFFFF;   // MAKEINTRESOURCEW(-1)
    const uint TdnHyperlinkClicked = 3;
    const uint SwShowNormal = 1;

    /// <summary>Builds and shows one `TaskDialogIndirect` call; every native (unmanaged) allocation it makes is freed
    /// in a <c>finally</c>. Returns false on ANY failure (missing comctl32 v6, a thrown marshalling error, an
    /// HRESULT failure) so the caller can fall back to <see cref="MessageBoxW"/> — never throws.</summary>
    static bool TryShowTaskDialog(string heading, string content, IReadOnlyList<(int Id, string Title, string? Subtitle)> buttons,
        string? footer, string? verification, bool warningIcon, bool useCommandLinks,
        out int buttonId, out bool verificationChecked)
    {
        buttonId = 0;
        verificationChecked = false;
        var natives = new List<nint>();
        try
        {
            nint pTitle = Alloc("Wavee", natives);
            nint pInstruction = Alloc(heading, natives);
            nint pContent = Alloc(content, natives);
            nint pFooter = footer is null ? 0 : Alloc(footer, natives);
            nint pVerification = verification is null ? 0 : Alloc(verification, natives);

            int n = buttons.Count;
            nint pButtonsArray = 0;
            int buttonStructSize = Marshal.SizeOf<TASKDIALOG_BUTTON>();
            if (n > 0)
            {
                pButtonsArray = Marshal.AllocHGlobal(buttonStructSize * n);
                natives.Add(pButtonsArray);
                for (int i = 0; i < n; i++)
                {
                    string label = buttons[i].Subtitle is { Length: > 0 } sub ? buttons[i].Title + "\n" + sub : buttons[i].Title;
                    nint pLabel = Alloc(label, natives);
                    var btn = new TASKDIALOG_BUTTON { nButtonID = buttons[i].Id, pszButtonText = pLabel };
                    Marshal.StructureToPtr(btn, pButtonsArray + i * buttonStructSize, false);
                }
            }

            uint flags = TdfAllowDialogCancellation | TdfSizeToContent;
            if (useCommandLinks) flags |= TdfUseCommandLinks;
            if (footer is not null) flags |= TdfEnableHyperlinks;

            var cfg = new TASKDIALOGCONFIG
            {
                cbSize = (uint)Marshal.SizeOf<TASKDIALOGCONFIG>(),
                hwndParent = 0,
                hInstance = 0,
                dwFlags = flags,
                dwCommonButtons = 0,
                pszWindowTitle = pTitle,
                MainIcon = warningIcon ? TdWarningIcon : 0,
                pszMainInstruction = pInstruction,
                pszContent = pContent,
                cButtons = (uint)n,
                pButtons = pButtonsArray,
                nDefaultButton = n > 0 ? buttons[0].Id : 0,
                cRadioButtons = 0,
                pRadioButtons = 0,
                nDefaultRadioButton = 0,
                pszVerificationText = pVerification,
                pszExpandedInformation = 0,
                pszExpandedControlText = 0,
                pszCollapsedControlText = 0,
                FooterIcon = 0,
                pszFooter = pFooter,
                pfCallback = footer is not null ? GetCallbackPtr() : 0,
                lpCallbackData = 0,
                cxWidth = 0,
            };

            int hr = TaskDialogIndirect(in cfg, out int outButton, out _, out int outVerification);
            if (hr != 0) return false;

            buttonId = outButton;
            verificationChecked = outVerification != 0;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            foreach (nint p in natives) Marshal.FreeHGlobal(p);
        }
    }

    static nint Alloc(string s, List<nint> tracked)
    {
        nint p = Marshal.StringToHGlobalUni(s);
        tracked.Add(p);
        return p;
    }

    static unsafe nint GetCallbackPtr() => (nint)(delegate* unmanaged<nint, uint, nint, nint, nint, int>)&TaskDialogCallback;

    /// <summary>Fires for every `TaskDialogIndirect` notification; the only one this screen acts on is a hyperlink
    /// click (the footer's "Privacy" link) — everything else returns S_OK and lets the dialog keep going.
    /// `UnmanagedCallersOnly` (NativeAOT: this is the only supported way to hand a managed method to native code as
    /// a raw function pointer). Never throws past this boundary — a native caller cannot observe a managed exception.</summary>
    [UnmanagedCallersOnly]
    static int TaskDialogCallback(nint hwnd, uint msg, nint wParam, nint lParam, nint lpRefData)
    {
        try
        {
            if (msg == TdnHyperlinkClicked && lParam != 0)
            {
                string? href = Marshal.PtrToStringUni(lParam);
                if (!string.IsNullOrEmpty(href)) ShellExecuteW(0, null, href, null, null, SwShowNormal);
            }
        }
        catch { }
        return 0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct TASKDIALOG_BUTTON
    {
        public int nButtonID;
        uint _pad;
        public nint pszButtonText;
    }

    /// <summary>The native `TASKDIALOGCONFIG` (commctrl.h), laid out by hand field-for-field with EXPLICIT padding
    /// (<c>Pack = 1</c> + the <c>_pad*</c> fields) rather than the CLR's automatic struct layout, so the byte offsets
    /// are identical and verifiable on both x64 and arm64 (both LLP64 Windows ABIs: 4-byte fields align to 4, every
    /// pointer/`LONG_PTR`/function-pointer field aligns to 8 — the <c>_pad*</c> fields below are exactly the gaps a
    /// C compiler would insert for that alignment, made explicit instead of assumed). Every field is `uint`/`int`/
    /// `nint` — fully blittable, so <see cref="TaskDialogIndirect"/> can take it by <c>in</c> with no custom
    /// marshaller. The two icon fields double as their union's other member (`pszMainIcon`/`pszFooterIcon`): this
    /// screen never sets `TDF_USE_HICON_MAIN`/`_FOOTER`, so they always hold either 0 or a `MAKEINTRESOURCEW`
    /// constant (<see cref="TdWarningIcon"/>), never a real `HICON`.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct TASKDIALOGCONFIG
    {
        public uint cbSize;
        uint _pad0;
        public nint hwndParent;
        public nint hInstance;
        public uint dwFlags;
        public uint dwCommonButtons;
        public nint pszWindowTitle;
        public nint MainIcon;
        public nint pszMainInstruction;
        public nint pszContent;
        public uint cButtons;
        uint _pad1;
        public nint pButtons;
        public int nDefaultButton;
        public uint cRadioButtons;
        public nint pRadioButtons;
        public int nDefaultRadioButton;
        uint _pad2;
        public nint pszVerificationText;
        public nint pszExpandedInformation;
        public nint pszExpandedControlText;
        public nint pszCollapsedControlText;
        public nint FooterIcon;
        public nint pszFooter;
        public nint pfCallback;
        public nint lpCallbackData;
        public uint cxWidth;
        uint _pad3;   // the C compiler rounds the struct's own size up to its 8-byte alignment; cbSize must match that
    }

    [LibraryImport("comctl32.dll")]
    private static partial int TaskDialogIndirect(in TASKDIALOGCONFIG pTaskConfig, out int pnButton, out int pnRadioButton, out int pfVerificationFlagChecked);

    // ── 7. MessageBoxW fallback ──────────────────────────────────────────────────────────────────────────────────────

    const uint MbOk = 0x0, MbYesNo = 0x4, MbYesNoCancel = 0x3;
    const uint MbIconWarning = 0x30, MbIconInformation = 0x40, MbIconError = 0x10;
    const int IdOk = 1, IdCancel = 2, IdYes = 6, IdNo = 7;

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint hWnd, string lpText, string lpCaption, uint uType);

    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint ShellExecuteW(nint hwnd, string? lpOperation, string lpFile, string? lpParameters, string? lpDirectory, uint nShowCmd);

    const uint GmemMoveable = 0x0002;
    const uint CfUnicodeText = 13;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint hWndNewOwner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    private static partial nint SetClipboardData(uint uFormat, nint hMem);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalLock(nint hMem);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint hMem);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalFree(nint hMem);
}
