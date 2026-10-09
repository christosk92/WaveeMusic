// ── Shell/Sidebar.Accounts.cs ──────────────────────────────────────────────────────────────────────────────────────────
// the per-account half of the sidebar service: which account's pins are loaded, the safe swap, the account file's commit
//
// Role: SHELL
// Plan: docs/plans/wavee/sidebar-rework-implementation.md §P3.8

using System.Globalization;

namespace Wavee;

public static partial class Sidebar
{
    static SidebarAccountData s_account = SidebarAccountStore.Empty("");
    static SidebarFileStore? s_accountFile;
    static bool s_accountLoaded;
    static long s_newReleasesSeenMs;

    /// <summary>The account whose pins are loaded ("" = signed out: no pins, no "Pin to sidebar").</summary>
    public static string AccountKey => s_account.Key;

    /// <summary>The latch + guard the pin bridge is built with (Spotify.Library.Install).</summary>
    public static SidebarPinLatch PinLatch { get; } = new(
        static () => s_account.Key,
        static () => SidebarAccountKey.Of(Entities.Current.Key),
        static () => s_account.MigratedToServer,
        SetMigrated);

    /// <summary>Raised after an account swap, on the UI thread (P4: the undo ring clears and closes its toasts).</summary>
    internal static event Action? AccountSwapped;

    /// <summary>THE ONE ACCOUNT EDGE. Idempotent and cheap: a scope change that keeps the account (market, locale, tier)
    /// returns false at the key compare. Called by <see cref="Boot"/>, by the pane's ScopeEpoch effect, and — synchronously,
    /// before any pin of the new scope is converged — by <c>Spotify.Library.AfterPublish</c>'s scope-change branch.
    /// Returns true only when it swapped the loaded account.</summary>
    public static bool EnsureAccount(in CatalogScope scope)
    {
        string key = SidebarAccountKey.Of(scope);
        if (s_accountLoaded && string.Equals(key, s_account.Key, StringComparison.Ordinal)) return false;
        SwapAccount(key);
        return true;
    }

    static void SwapAccount(string key)
    {
        // 1 — the outgoing account's file is written from ITS store, now, before the store is reloaded: nothing of the new
        //     account can reach it (the bridge's guard refuses while the keys differ).
        if (s_accountLoaded)
        {
            CommitAccount();
            s_accountFile?.FlushNow();
        }
        // 2 — Edit mode and the undo ring never span accounts; the Library search is per session and per account.
        Editing.SetIfChanged(false);
        LibrarySearch.SetIfChanged("");
        LibrarySearchOpen.SetIfChanged(false);
        AccountSwapped?.Invoke();
        // 3 — load the incoming account (adopting a migration's pending pins on the first sign-in).
        s_accountFile = SidebarAccountKey.IsSignedOut(key) ? null : new SidebarFileStore(Path.Combine(s_profileDir, SidebarAccountStore.FileNameOf(key))) { WriteCompleted = OnWriteCompleted };
        s_account = LoadAccount(key, s_accountFile);
        s_accountLoaded = true;
        s_newReleasesSeenMs = s_account.NewReleasesSeenMs;
        Pins.LoadFrom(s_account.Pins);                         // silent: no OnLocalPinChanged, no write
        s_expandedFolders.Clear();
        for (int i = 0; i < s_account.ExpandedFolders.Count; i++) s_expandedFolders.Add(s_account.ExpandedFolders[i]);
        s_folderVersion.Value = s_folderVersion.Peek() + 1;
        s_firstSeen = s_account.FirstSeen.Count > 0 ? [.. s_account.FirstSeen] : null;
        Binder?.ResetFirstSeen();
        Log.Info("sidebar", "account.loaded key=" + (key.Length == 0 ? "signed-out" : SidebarAccountKey.Hash(key))
                            + " pins=" + Pins.Count.ToString(CultureInfo.InvariantCulture));
    }

    static SidebarAccountData LoadAccount(string key, SidebarFileStore? file)
    {
        if (file is null) return SidebarAccountStore.Empty("");
        // A migration that ran signed out parked the old global pins: the first account that signs in adopts them.
        string pending = Path.Combine(s_profileDir, SidebarAccountStore.PendingFileName);
        if (!File.Exists(file.FilePath) && File.Exists(pending))
        {
            try { File.Move(pending, file.FilePath); Log.Info("sidebar", "account.adopted_pending"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("sidebar", "account.adopt_failed", ex); }
        }
        var read = file.Read();
        if (read.Outcome == SidebarFileReadOutcome.Ok && SidebarAccountStore.TryParse(read.Bytes!, key, out var data)) return data;
        if (read.Outcome == SidebarFileReadOutcome.Missing) return SidebarAccountStore.Empty(key);
        // Corrupt (design corner case): set aside; the mirror's membership comes back through the next ApplyServer, the
        // latch starts false (the next converged walk pushes nothing and sweeps nothing until it latches).
        var bak = file.ReadBak();
        if (bak.Outcome == SidebarFileReadOutcome.Ok && SidebarAccountStore.TryParse(bak.Bytes!, key, out var recovered)) return recovered;
        file.MarkCorrupt();
        Log.Warn("sidebar", "account.file_corrupt key=" + SidebarAccountKey.Hash(key));
        return SidebarAccountStore.Empty(key);
    }

    /// <summary>Snapshot the account's pins, folders, watermark and first-seen stamps into its file (coalesced 300 ms,
    /// atomic, .bak). The pin store's OnChanged and every folder / first-seen / watermark write land here.</summary>
    static void CommitAccount()
    {
        s_commitPending = false;
        if (s_accountFile is null || !s_accountLoaded) return;
        var pins = new SidebarPin[Pins.Count];
        for (int i = 0; i < pins.Length; i++) pins[i] = Pins[i];
        var folders = new string[s_expandedFolders.Count];
        s_expandedFolders.CopyTo(folders);
        s_account = s_account with
        {
            Pins = pins,
            ExpandedFolders = folders,
            NewReleasesSeenMs = s_newReleasesSeenMs,
            FirstSeen = s_firstSeen ?? [],
        };
        s_accountFile.Commit(SidebarAccountStore.Serialize(s_account));
    }

    static void SetMigrated(bool migrated)
    {
        if (s_account.MigratedToServer == migrated) return;
        s_account = s_account with { MigratedToServer = migrated };
        CommitAccount();
    }

    /// <summary>The user looked at New releases (expanded it or opened a row): the badge counts from now.</summary>
    public static void MarkNewReleasesSeen()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now <= s_newReleasesSeenMs) return;
        s_newReleasesSeenMs = now;
        CommitAccount();
    }

    public static long NewReleasesSeenMs => s_newReleasesSeenMs;
}
