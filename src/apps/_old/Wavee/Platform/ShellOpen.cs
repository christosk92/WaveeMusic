using System;
using System.Diagnostics;
using System.IO;

namespace Wavee;

static class ShellOpen
{
    /// <summary>Is this text a web link the shell may be handed? Absolute, <c>http</c>/<c>https</c> only, with a host —
    /// nothing else.
    ///
    /// <para>This guard is the whole reason <see cref="OpenUrl"/> exists. The strings it opens come from a MODULE — a
    /// full-trust child process, but one whose page document is data crossing a pipe — and
    /// <c>UseShellExecute = true</c> on arbitrary text is a shell-execute injection: it happily launches
    /// <c>file:</c>, a UNC path, an executable, or a registered protocol handler. So the answer is a whitelist of two
    /// schemes rather than a blacklist of the bad ones, and a refused string opens NOTHING rather than falling back
    /// to some other launch.</para></summary>
    /// <param name="url">The candidate text (may be null).</param>
    public static bool IsWebUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        return parsed.Host.Length > 0;
    }

    /// <summary>May <see cref="OpenUrl"/> hand this text to the shell? <see cref="IsWebUrl"/> plus exactly one more
    /// allowlisted scheme: <c>ms-windows-store:</c>, the Store app's own deep link — the "Get it from the Microsoft
    /// Store" highlight card and the Store build's "Open Store page" both open the listing through it, and the strings
    /// are built by <c>StoreLinks</c>, never received over a pipe. Still an allowlist, never "any registered
    /// protocol": <c>ms-settings:</c>, <c>file:</c> and friends stay refused.</summary>
    public static bool IsOpenableUrl(string? url)
    {
        if (IsWebUrl(url)) return true;
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)) return false;
        return string.Equals(parsed.Scheme, "ms-windows-store", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Open a link with the user's registered handler (the browser, or the Store app for
    /// <c>ms-windows-store:</c>). Silently refuses anything <see cref="IsOpenableUrl"/> rejects, and is best-effort by
    /// design: a missing browser or a denied launch must never throw into the UI thread that invoked a page action.</summary>
    /// <param name="url">The link to open.</param>
    /// <returns>True when the launch was attempted (the string passed the guard).</returns>
    public static bool OpenUrl(string? url)
    {
        if (!IsOpenableUrl(url)) return false;
        try { Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true }); }
        catch { }
        return true;
    }

    public static void OpenFolderOf(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(dir)) return;
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch { }
    }

    /// <summary>Open Explorer with <paramref name="path"/> SELECTED (the Win11 "Show in folder" affordance), falling back
    /// to just opening the containing folder when the file is gone. Best-effort by design: a missing Explorer, a denied
    /// path or an offline share must never throw into the UI thread that invoked a menu row.</summary>
    public static void RevealInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
            {
                // /select, takes ONE argument and it must be quoted as a whole — the comma is part of the switch.
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = false });
                return;
            }
        }
        catch { /* fall through to the folder open below */ }
        OpenFolderOf(path);
    }
}

