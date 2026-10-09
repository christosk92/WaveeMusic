// ── Platform/Actions.Rules.cs ──────────────────────────────────────────────────────────────────────────────────────
// The named partial of Actions.cs for the PURE rules behind the menu vocabulary, the two pickers, the profile menu and
// the play-a-link dialog — the decisions owner I's two UI files (Actions.UI.cs, +Shell.Overlays.UI.cs) draw, pulled
// out of the component bodies so a test can pin them (stage-B rule: a new pure decision goes to CORE with a test).
//
// Role: CORE
// Owner: I
// Wave: 4
// Budget: 330 lines (a NAMED partial: Actions.cs + Actions.Table.cs already sit >30 % past their shared 950, so these
//         rules could not be added to either without breaking the plan's partial rule)
// Spec: ch 29 §2 W12 + §9.1 #12-13 (the picker and the bound row), ch 01 §6.4 (the deposit submenu, Share ▸),
//       ch 19 §6.2 (the profile menu's row table) + §8 (PlayLinkActions)
//
// WHERE THE PLAY-LINK RULES LIVE, AND WHY IT IS A THIRD ANSWER. Ch 19 §8 files 0.2.9's `PlayLinkActions` under
// `Platform/Modules.cs` CORE (owner T, Wave 6). The Wave-4 play-a-link dialog needs them now and `Modules.cs` is not in
// this owner's brief, so they land here as `Actions.PlayLinkRules` — 0.2.9 had them in `Actions/` too. Owner T may move
// the class in Wave 6; nothing else refers to its file.
//
// Everything here is engine-free apart from the loc-KEY constants, allocation-light (menus are built at human rate,
// never per frame) and test-visible.

using System;
using System.Collections.Generic;
using System.Text;

namespace Wavee;

public static partial class Actions
{
    // ══ 1. THE MENU GRAMMAR'S PURE HALF ═════════════════════════════════════════════════════════════════════════════

    public static class MenuRules
    {
        /// <summary>The "Add to playlist ▸" / "Move to playlist ▸" submenu lists at most this many playlists inline;
        /// the rest are one "More playlists…" row away (ch 01 §6.4, <c>Menus.cs:49</c>).</summary>
        public const int MaxInlinePlaylists = 10;

        /// <summary>How many deposit targets render inline.</summary>
        public static int InlineCount(int available) => available <= 0 ? 0 : Math.Min(available, MaxInlinePlaylists);

        /// <summary>Does a new group need a separator in front of it? Only when there are rows above AND the last one
        /// is not already a separator — a menu never renders a stray or doubled divider.</summary>
        public static bool NeedsSeparator(int rowCount, bool lastIsSeparator) => rowCount > 0 && !lastIsSeparator;

        /// <summary>Where a surface's layout extras (Move up / Move down / Remove) are inserted: IN FRONT of a trailing
        /// [separator, destructive verb] pair, so "Move up" never lands under "Delete playlist"; at the end otherwise
        /// (<c>Menus.WithLayoutExtras</c>).</summary>
        public static int ExtrasInsertIndex(int rowCount, bool secondToLastIsSeparator)
            => rowCount >= 2 && secondToLastIsSeparator ? rowCount - 2 : rowCount;

        /// <summary>Is this uri one a Share ▸ "Copy Spotify URI" / "Open in Spotify Web" row can act on?</summary>
        public static bool IsShareable(EntityUri uri) => uri.IsValid && uri.Provider == EntityProvider.Spotify;

        /// <summary>The ONE shareable Spotify uri of a target, or <c>default</c> when the target is a multi-selection
        /// or not a Spotify entity — the URI / web-player variants are single-target (<c>SpotifyLink.SingleUri</c>).</summary>
        public static EntityUri SingleShareableUri(in ActionTarget target)
        {
            if (target.Count > 1) return default;
            var uri = target.Count == 1 ? target.Tracks[0].Uri : target.Uri;
            return IsShareable(uri) ? uri : default;
        }
    }

    // ══ 2. THE PROFILE MENU'S ROW TABLE (ch 19 §6.2) ════════════════════════════════════════════════════════════════

    /// <summary>One row slot of the profile menu, in order.</summary>
    public enum ProfileRow : byte { Settings, Play, Separator, Notifications, Friends, Theme, LogOut, Profile }

    public static class ProfileRules
    {
        /// <summary>The name caption's hard cap (issue #88): the chip's name budget less its 8-DIP gap and the named
        /// form's extra 6 DIP of right padding. 90 → 76.</summary>
        public static float NameCap(float nameBudget) => nameBudget - 8f - 6f;

        /// <summary>The rows, in order. <c>Profile</c> first when the account has a user row to open
        /// (<paramref name="hasProfile"/> — ABSENT otherwise, never disabled; #161) — the web account page is the own
        /// profile's "Open account" button now, not a row — then Settings always; <c>Play ▸</c> only
        /// when the build can play something of its own (ABSENT, never disabled); Notifications and Friends only once the
        /// chrome ladder has folded them out of the trailing island (never both places, never neither), behind a
        /// separator that exists only for them; then Theme and Log out, each behind its own separator.</summary>
        public static ProfileRow[] Rows(bool canPlay, bool actionsInMenu, bool hasNotifications, bool hasProfile)
        {
            var rows = new List<ProfileRow>(10);
            if (hasProfile) rows.Add(ProfileRow.Profile);
            rows.Add(ProfileRow.Settings);
            if (canPlay) rows.Add(ProfileRow.Play);
            bool notifications = actionsInMenu && hasNotifications;
            if (notifications || actionsInMenu) rows.Add(ProfileRow.Separator);
            if (notifications) rows.Add(ProfileRow.Notifications);
            if (actionsInMenu) rows.Add(ProfileRow.Friends);
            rows.Add(ProfileRow.Separator);
            rows.Add(ProfileRow.Theme);
            rows.Add(ProfileRow.Separator);
            rows.Add(ProfileRow.LogOut);
            return rows.ToArray();
        }

        /// <summary>The theme row names the TARGET theme: a dark app offers "Light theme" (Sun), a light one "Dark
        /// theme" (Moon).</summary>
        public static bool OffersLightTheme(bool currentlyDark) => currentlyDark;

        /// <summary>The tier row renders only once the tier is KNOWN — 0.3 never prints "Spotify Free" speculatively
        /// (ch 19 §7; 0.2.9 did).</summary>
        public static bool ShowsTier(Spotify.Tier tier) => tier != Spotify.Tier.Unknown;
    }

    // ══ 3. THE DESTINATION PICKER'S PURE HALF ═══════════════════════════════════════════════════════════════════════

    public static class PickRules
    {
        /// <summary>The destination picker's live filter: a PINNED row (Top level, New playlist) is never filtered away
        /// — a user who typed and changed their mind must keep the way back — and everything else is a
        /// case-insensitive contains.</summary>
        public static bool Matches(string? query, string label, bool pinned)
            => pinned || string.IsNullOrEmpty(query) || label.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    // ══ 4. "PLAY ▸ LINK…" — THE PASTE-A-LINK DIALOG'S DECISIONS (0.2.9 PlayLinkActions) ════════════════════════════

    public static class PlayLinkRules
    {
        const string Ellipsis = "…";

        /// <summary>The status line's joiner — the app's metadata separator ("Playlist · Private").</summary>
        public const string Separator = " · ";

        /// <summary>The toast key every "this play failed" card shares, whichever lane raised it (ch 19 §0 #10): one
        /// failed play is ONE card; coalescing keeps the first, most specific sentence and adopts the newer action.</summary>
        public const string FailureToastKey = "wavee.play.failed";

        /// <summary>The text as the router will see it: trimmed, never null (a pasted link carries a trailing newline).</summary>
        public static string Normalize(string? input) => input is null ? "" : input.Trim();

        /// <summary>Play is DISABLED, not hidden, while this is false.</summary>
        public static bool CanSubmit(string? input) => Normalize(input).Length != 0;

        /// <summary>Scheme + no inner whitespace + a host. Deliberately shallow: the modules own the real ownership
        /// question.</summary>
        public static bool LooksLikeUrl(string? text)
        {
            string s = Normalize(text);
            if (s.Length < 8) return false;
            if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
            foreach (char ch in s) if (char.IsWhiteSpace(ch)) return false;
            return Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Host.Length > 0;
        }

        /// <summary>The field's seed: a clipboard http(s) link, anything else left alone.</summary>
        public static string PrefillFrom(string? clipboardText) => LooksLikeUrl(clipboardText) ? Normalize(clipboardText) : "";

        /// <summary>The browser escape hatch's guard: http/https with a host, nothing else (<c>ShellOpen.IsWebUrl</c>).
        /// Untrusted pasted text never reaches the shell without passing it.</summary>
        public static bool IsWebUrl(string? text) => LooksLikeUrl(text);

        /// <summary>A module's "Play ▸" row label: authored copy wins; else its display name (or id) with the
        /// dialog-opening ellipsis, never doubled.</summary>
        public static string MenuLabel(string? authoredLabel, string? displayName, string id)
        {
            if (!string.IsNullOrWhiteSpace(authoredLabel)) return authoredLabel.Trim();
            string name = string.IsNullOrWhiteSpace(displayName) ? id : displayName;
            return name.EndsWith(Ellipsis, StringComparison.Ordinal) ? name : name + Ellipsis;
        }

        /// <summary>The field placeholder: the module's own when it authored one, else the surface's.</summary>
        public static string PlaceholderFor(string? authored, string fallback)
            => string.IsNullOrWhiteSpace(authored) ? fallback : authored.Trim();

        /// <summary>"&lt;Module&gt; · &lt;Title&gt; · LIVE" — a missing segment drops out, so the line only states facts.</summary>
        public static string MatchStatus(string? moduleName, string? title, bool isLive, string liveWord)
        {
            var sb = new StringBuilder(64);
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

        /// <summary>A withdrawn look-up (the card closed, the user typed again) says nothing at all.</summary>
        public static bool IsCancelled(Exception? ex) => ex is OperationCanceledException;

        /// <summary>What a failure says out loud: the module's own words verbatim when it gave any, else the surface's
        /// honest fallback; a cancellation says nothing.</summary>
        public static string ErrorText(Exception? ex, string fallback)
        {
            if (ex is null || IsCancelled(ex)) return "";
            string message = ex.Message.Trim();
            return message.Length == 0 ? fallback : message;
        }
    }
}
