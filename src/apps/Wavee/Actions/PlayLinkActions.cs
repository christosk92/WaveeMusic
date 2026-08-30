using System;
using Wavee.Backend.Modules;
using Wavee.Sdk;

namespace Wavee;

// ── "Play ▸ Link…" — the DECISIONS behind the paste-a-link surface ────────────────────────────────────────────────────
// Everything the Play submenu and PlayLinkDialog decide lives here: which installed modules earn a menu row, what that
// row is called, what the field is prefilled with, what the status line says, and what a failure says out loud. The
// class is deliberately engine-free AND localization-free — it takes the localized strings it needs as arguments — so
// the whole surface is pinned by unit tests rather than by a screenshot (PlayLinkActionsTests).
//
// The one thing it does NOT do is talk to a module: matching is asynchronous, cancellable and lives in ModuleHost. The
// dialog owns that call; this owns every answer that is a rule rather than an I/O result.
public static class PlayLinkActions
{
    /// <summary>The ellipsis a menu row/dialog-opening label ends with, per the app's menu voice ("File…", "Link…",
    /// "YouTube…"): a row that opens a dialog says so.</summary>
    const string Ellipsis = "…";

    // ── The input ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The text as the router will see it: trimmed, never null. Pasted links routinely carry a trailing
    /// newline (copied from a chat line) or leading whitespace, and a module's url-pattern prefilter is a plain
    /// substring test — the trim is what stops "  https://…\n" from missing every pattern.</summary>
    public static string Normalize(string? input) => input is null ? "" : input.Trim();

    /// <summary>Is there anything to look up? The Play button is DISABLED (not hidden) while this is false: the button
    /// is the dialog's whole purpose, so removing it would leave a card with one action.</summary>
    public static bool CanSubmit(string? input) => Normalize(input).Length != 0;

    /// <summary>Does this text look like a web link? Used for one thing only — deciding whether the clipboard is worth
    /// prefilling into the field. Deliberately shallow (scheme + no inner whitespace): the modules own the real
    /// ownership question, and guessing harder here would only ever refuse a link some module could have played.</summary>
    public static bool LooksLikeUrl(string? text)
    {
        string s = Normalize(text);
        if (s.Length < 8) return false;
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (char ch in s) if (char.IsWhiteSpace(ch)) return false;
        // A bare scheme ("https://") is not a link: there must be a host to connect to.
        return Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Host.Length > 0;
    }

    /// <summary>What the field starts with. The overwhelmingly common gesture is "copy a link, open Wavee, play it", so
    /// a clipboard holding an http(s) url seeds the field — and a clipboard holding anything else (a track name, a
    /// paragraph, a file path) is left alone rather than pasted as junk the user has to clear first.</summary>
    public static string PrefillFrom(string? clipboardText)
        => LooksLikeUrl(clipboardText) ? Normalize(clipboardText) : "";

    // ── The menu ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Does this module earn a "Play ▸" row? Only modules that can be handed arbitrary text — a module that
    /// resolves but cannot match has nothing to put in a paste-a-link dialog. Capabilities are DECLARED, never probed
    /// (the composition rule), and the capability vocabulary has ONE owner (<see cref="ModuleCapabilities"/>) — this
    /// asks it rather than restating the token.</summary>
    public static bool DeclaresMatch(ModuleManifest? manifest)
        => manifest is not null && ModuleCapabilities.Declares(manifest, ModuleCapabilities.Match);

    /// <summary>The module's row label. The manifest's <c>menu.label</c> is authored copy and wins; without one the
    /// display name gets the dialog-opening ellipsis so the row still reads like the two rows above it.</summary>
    public static string MenuLabel(ModuleManifest manifest)
    {
        string? label = manifest.Menu?.Label;
        if (!string.IsNullOrWhiteSpace(label)) return label.Trim();
        string name = string.IsNullOrWhiteSpace(manifest.DisplayName) ? manifest.Id : manifest.DisplayName;
        return name.EndsWith(Ellipsis, StringComparison.Ordinal) ? name : name + Ellipsis;
    }

    /// <summary>The field's placeholder. A module opened by NAME gets to say what IT wants ("Paste a YouTube link");
    /// the generic "Link…" row and a module with no authored placeholder both fall back to the surface's own.</summary>
    public static string PlaceholderFor(ModuleManifest? manifest, string fallback)
    {
        string? p = manifest?.Menu?.Placeholder;
        return string.IsNullOrWhiteSpace(p) ? fallback : p.Trim();
    }

    // ── The status line ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The dot the status line joins its parts with — the app's own metadata separator ("Playlist · Private").
    /// </summary>
    public const string Separator = " · ";

    /// <summary>The status line for a match that came back: "&lt;Module&gt; · &lt;Title&gt; · LIVE". A missing title
    /// (a module that matched on the url shape alone) drops its segment rather than printing a placeholder, and a
    /// finite stream drops the LIVE segment — so the line only ever states facts.</summary>
    public static string MatchStatus(string moduleName, string? title, bool isLive, string liveWord)
    {
        var sb = new System.Text.StringBuilder(64);
        if (!string.IsNullOrWhiteSpace(moduleName)) sb.Append(moduleName.Trim());
        if (!string.IsNullOrWhiteSpace(title))
        {
            if (sb.Length != 0) sb.Append(Separator);
            sb.Append(title.Trim());
        }
        if (isLive && !string.IsNullOrWhiteSpace(liveWord))
        {
            if (sb.Length != 0) sb.Append(Separator);
            sb.Append(liveWord);
        }
        return sb.ToString();
    }

    // ── Failure ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The toast <c>DedupeKey</c> every "this play failed" card shares, whichever lane raised it.
    ///
    /// <para>One failed play is ONE card. Several lanes can each answer for the same failure — the paste-a-link card's
    /// <c>Failed</c> (the module's own words), the deep-link path, and <c>PlaybackBridge.NotifyPlaybackError</c> (the
    /// player's generic sentence) — and before this key they stacked two identical-looking error cards with nothing to
    /// act on. The key is the LANE and deliberately not the input: only one of those callers even knows the input, so
    /// a per-link key could never make them collapse. Coalescing keeps the FIRST (most specific) sentence and adopts
    /// the newer action, so the retry survives the merge.</para></summary>
    public const string FailureToastKey = "wavee.play.failed";

    /// <summary>Did the router come back with "nobody owns this"? That is not an error the user should see as a toast —
    /// it is the status line's own answer, so the dialog stays open and says so in place.</summary>
    public static bool IsNotOwned(Exception? ex)
        => ex is ModuleException { Code: ModuleErrorCode.NotOwned };

    /// <summary>A cancelled look-up (the user typed again, or closed the card) says nothing at all — it is not a
    /// failure, it is the previous question being withdrawn.</summary>
    public static bool IsCancelled(Exception? ex) => ex is OperationCanceledException;

    /// <summary>What a failure says out loud. A <see cref="ModuleException"/> carries a message written by the module
    /// that actually knows why ("YouTube is blocking this network", "subscriber-only"), so it is shown verbatim; every
    /// other failure — and a module that threw with no message — falls back to the surface's own honest sentence
    /// rather than leaking a stack-shaped string.</summary>
    public static string ErrorText(Exception? ex, string fallback)
    {
        if (ex is null || IsCancelled(ex)) return "";
        if (IsNotOwned(ex)) return fallback;
        string message = ex.Message.Trim();
        return message.Length == 0 ? fallback : message;
    }

    // ── The hand-off ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The module's declared form → the app's play FORM. <c>video</c> is an explicit one-play "show me this"
    /// (VideoActions.PlayAs lights the surface for THIS uri and lets it expire at the next track boundary); audio is
    /// <see cref="Wavee.Core.MediaForm.Default"/> rather than <c>Audio</c> on purpose — a radio stream must not turn a
    /// standing video intent OFF for the rest of the queue, it simply has no opinion.</summary>
    public static Wavee.Core.MediaForm FormFor(MediaForm form)
        => form == MediaForm.Video ? Wavee.Core.MediaForm.Video : Wavee.Core.MediaForm.Default;
}
