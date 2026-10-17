// ── Entities/PlaylistDepositPref.cs ────────────────────────────────────────────────────────────────────────────────────
// the "recently filed into" playlist list as a typed pref: the MRU the deposit menus order their targets by, read and
// written through `Platform.Keys.PlaylistDepositRecents` here and nowhere else
//
// Role: CORE
// Spec: docs/themes/plan/technical-plan.md §5.1 C16 (X41); ch 01 item 42 / ch 06 §0.14
//
// The ORDER and the codec are `PlaylistDepositTargets`' (Playlist.cs, pure); this is only the settings door the playlist
// page's deposit adapter used to open by hand (`Playlist.Page.cs` `DepositTargetsFor` / `RememberDeposit`).

namespace Wavee;

public static class PlaylistDepositPref
{
    /// <summary>The persisted MRU, most recently filed first (<see cref="PlaylistDepositTargets.Parse"/> of the stored string).</summary>
    public static List<string> Recents()
        => PlaylistDepositTargets.Parse(Platform.Settings.Get(Platform.Keys.PlaylistDepositRecents));

    /// <summary>Record a successful deposit in the MRU — written only when the serialized list actually changed, so
    /// re-filing into the front playlist costs no settings write (ch 06 §6.3). Returns whether it wrote.</summary>
    public static bool Remember(string uri)
    {
        string current = Platform.Settings.Get(Platform.Keys.PlaylistDepositRecents) ?? "";
        string next = PlaylistDepositTargets.Serialize(PlaylistDepositTargets.Remember(PlaylistDepositTargets.Parse(current), uri));
        if (string.Equals(current, next, StringComparison.Ordinal)) return false;
        Platform.Settings.Set(Platform.Keys.PlaylistDepositRecents, next);
        return true;
    }
}
