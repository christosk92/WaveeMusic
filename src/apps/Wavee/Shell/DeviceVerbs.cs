// ── Shell/DeviceVerbs.cs ───────────────────────────────────────────────────────────────────────────────────────────
// what the device picker EXECUTES and the one setting it READS (themes plan C2, X7): a picker click as a plan plus its
// execution, the picker-opened side effect, and the asked-quality fact the verification row compares the player's echo against
//
// Role: CORE
// Spec: ch 20 §9; the rows and intents are `Shell.DevicePicker` (`Shell.DevicePicker.cs`), the rows' composition is
// `Shell.DevicePickerRows` (`Shell.cs`)
//
// Before this, the click executor (`RunDeviceIntent`), the settings read (`PickerAskedQuality`) and the picker-opened call
// lived in `Shell.PlayerBar.UI.cs` and the stage's "Playing on" flyout. A picker view of any theme maps rows to its own
// look and calls `DeviceVerbs.Run` / `DeviceVerbs.PickerOpened`; it never touches `Playback` or `Platform.Settings` itself.

namespace Wavee;

public static partial class Shell
{
    /// <summary>The facts the picker reads that are not the roster itself.</summary>
    public static class DevicePickerFacts
    {
        /// <summary>The Spotify quality SETTING the verification row compares the player's echo against (the clamped
        /// persisted choice; the row itself never echoes the setting, only the format the player actually opened).</summary>
        public static Spotify.Audio.Quality Quality => QualityOf(Platform.Settings.Get(Platform.Keys.PlaybackQuality));

        /// <summary>The persisted quality choice, clamped onto the ladder's rungs (a stale or hand-edited value never
        /// escapes the enum's range).</summary>
        public static Spotify.Audio.Quality QualityOf(int persisted)
            => (Spotify.Audio.Quality)Math.Clamp(persisted, Platform.Network.QualityMin, Platform.Network.QualityMax);
    }

    public static class DeviceVerbs
    {
        /// <summary>What executing a picker click does, as DATA (so the decision is a unit fact and the execution is two
        /// calls). <see cref="SelectLocal"/> persists the output (<see cref="LocalId"/> null = the system default); then
        /// <see cref="TakeOver"/> pulls playback here (a local claim; never a transfer to our own hash, which the reducer
        /// refuses). <see cref="TransferSlot"/> &gt;= 0 transfers to that roster slot.</summary>
        public readonly record struct Plan(bool SelectLocal, string? LocalId, bool TakeOver, int TransferSlot)
        {
            /// <summary>The click does nothing (a header, a quality echo, a connect id that left the roster).</summary>
            public static Plan Nothing => new(false, null, false, -1);
        }

        /// <summary>A picker click as a plan. A this-computer row persists the output (<c>""</c> = the system default,
        /// stored as null), then, if the intent says we are not the owner, takes playback over. A Connect row transfers to
        /// that device, its roster slot resolved NOW against <paramref name="connect"/> (the roster may have moved since the
        /// row was drawn).</summary>
        public static Plan PlanOf(DeviceIntent intent, ReadOnlySpan<Playback.Devices.Row> connect)
        {
            switch (intent.Kind)
            {
                case DeviceIntentKind.SelectLocal:
                    return new Plan(true, intent.LocalId.Length == 0 ? null : intent.LocalId, intent.TakeOver, -1);
                case DeviceIntentKind.TransferTo:
                {
                    int slot = DeviceRoster.SlotOfId(connect, intent.ConnectId);
                    return slot >= 0 ? new Plan(false, null, false, slot) : Plan.Nothing;
                }
                default:
                    return Plan.Nothing;
            }
        }

        /// <summary>Execute a picker click (see <see cref="PlanOf"/>).</summary>
        public static void Run(DeviceIntent intent)
        {
            var plan = PlanOf(intent, Playback.Devices.Rows);
            if (plan.SelectLocal)
            {
                Playback.Audio.Select(plan.LocalId);
                if (plan.TakeOver) Playback.TakeOver();
            }
            else if (plan.TransferSlot >= 0)
                Playback.TransferTo(plan.TransferSlot);
        }

        /// <summary>A device picker just opened: the cluster hears it, as the official client's picker does (a put-state
        /// with reason PICKER_OPENED and a wake of the other devices; rate-limited and silent without a connection). Every
        /// picker, of every theme, calls this once as it opens.</summary>
        public static void PickerOpened() => Playback.PickerOpened();
    }
}
