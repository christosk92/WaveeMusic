// ── Shell/Shell.DevicePicker.cs ────────────────────────────────────────────────────────────────────────────────────
// the device picker's ITEMS as one pure rule: the player bar's Devices flyout and the stage's "Playing on" flyout both
// render `DevicePicker.Items`, so the same roster + owner give the same rows, the same "remote" verdict and the same
// click intents on both surfaces
//
// Role: CORE
//
// Before this, each surface decided "are we remote?" on its own (the bar by `DeviceRoster.RemoteSlot`, the stage by a bare
// owner read) and each mapped a click on its own (the bar pulled home through a roster probe that asked Spotify to
// transfer to OUR OWN hash — the reducer refuses that — while the stage never tried). Now a click is a DATA intent the
// surface only executes (`Shell.DeviceVerbs.Run`): choosing a this-computer endpoint persists the output and, unless we
// already own playback, takes playback over (`Playback.TakeOver`, a local claim — never a transfer to ourselves).

using System.Collections.Generic;

using FluentGpu.Controls;

using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class Shell
{
    public enum DeviceIntentKind : byte
    {
        None,          // a header / separator / quality echo / disabled row
        SelectLocal,   // a this-computer row: persist the output, then (TakeOver) pull playback here
        TransferTo,    // a Spotify Connect row: transfer to that device
    }

    /// <summary>What a picker row does when clicked. <see cref="LocalId"/> is "" for the system default.
    /// <see cref="TakeOver"/> is decided HERE from the owner (true iff we are not already the owner), never re-derived
    /// by a surface. <see cref="ConnectId"/> is resolved to a roster slot at CLICK time (the roster may have moved).</summary>
    public readonly record struct DeviceIntent(
        DeviceIntentKind Kind, string LocalId = "", bool TakeOver = false, string ConnectId = "");

    /// <summary>One picker row plus what it does.</summary>
    public readonly record struct DevicePickerItem(DevicePickerRow Row, DeviceIntent Intent);

    public static class DevicePicker
    {
        /// <summary>The ONE "playback is on another Connect device" verdict both surfaces read (owner-gated, valid slot,
        /// never this device) — <see cref="DeviceRoster.RemoteSlot"/> &gt;= 0.</summary>
        public static bool IsRemote(Playback.Owner owner, int activeSlot, ReadOnlySpan<Playback.Devices.Row> connect)
            => DeviceRoster.RemoteSlot(owner, activeSlot, connect) >= 0;

        /// <summary>A click on "This computer" pulls playback here unless we already own it.</summary>
        public static bool NeedsTakeOver(Playback.Owner owner) => owner != Playback.Owner.Us;

        /// <summary>The picker's items. <c>Row.IsChecked</c> on a LOCAL row reports the PERSISTED output choice (nothing
        /// more: the engine does not yet re-route a live session to it), and only while we play here.</summary>
        public static List<DevicePickerItem> Items(
            Playback.Owner owner,
            int activeSlot,
            ReadOnlySpan<Playback.Devices.Row> connect,
            ReadOnlySpan<Playback.Audio.LocalAudioDevice> local,
            string? selectedLocalId,
            bool localSupported,
            Playback.Audio.Opened observed,
            Spotify.Audio.Quality askedQuality)
        {
            bool remote = IsRemote(owner, activeSlot, connect);
            string? activeId = (uint)activeSlot < (uint)connect.Length ? connect[activeSlot].Id : null;
            var rows = DevicePickerRows(local, selectedLocalId, localSupported, weAreActiveOutput: !remote, connect, activeId,
                observed, askedQuality);
            bool takeOver = NeedsTakeOver(owner);

            var items = new List<DevicePickerItem>(rows.Count);
            foreach (var row in rows)
            {
                var intent = row.Kind switch
                {
                    DevicePickerRowKind.LocalDefault or DevicePickerRowKind.LocalDevice when row.Enabled
                        => new DeviceIntent(DeviceIntentKind.SelectLocal, LocalId: row.DeviceId, TakeOver: takeOver),
                    DevicePickerRowKind.ConnectDevice
                        => new DeviceIntent(DeviceIntentKind.TransferTo, ConnectId: row.DeviceId),
                    _ => default,
                };
                items.Add(new DevicePickerItem(row, intent));
            }
            return items;
        }

        /// <summary>The items as the contract's entries (<c>Player.DevicePicker</c>, T6b): a theme draws a separator, an information row, a
        /// command (a this-computer output: choosing it persists the output and pulls playback here) or a radio (a Connect device, checked
        /// when it is the active one), each with the glyph the roster rules give it and the verb that executes its intent. A disabled
        /// command row stands in as the section header (a menu has no header kind); the quality echo rides the same shape: informational,
        /// never a radio. The roster, owner and outputs are read by the caller; nothing here subscribes.</summary>
        public static DevicePickerEntry[] Entries(IReadOnlyList<DevicePickerItem> items)
        {
            var entries = new DevicePickerEntry[items.Count];
            for (int i = 0; i < entries.Length; i++)
            {
                var row = items[i].Row;
                var intent = items[i].Intent;
                entries[i] = row.Kind switch
                {
                    DevicePickerRowKind.Separator => new DevicePickerEntry(DevicePickerEntryKind.Separator, "", "", false, false, null, null),
                    DevicePickerRowKind.LocalDefault => new DevicePickerEntry(DevicePickerEntryKind.Command, row.Label, Icons.ThisPc,
                        row.Enabled, row.IsChecked, row.Accelerator, row.Enabled ? () => DeviceVerbs.Run(intent) : null) { Word = true },
                    DevicePickerRowKind.LocalDevice => new DevicePickerEntry(DevicePickerEntryKind.Command, row.Label,
                        DeviceRoster.LocalGlyph(row.LocalKind),
                        row.Enabled, row.IsChecked, row.Accelerator, row.Enabled ? () => DeviceVerbs.Run(intent) : null),
                    DevicePickerRowKind.ConnectDevice => new DevicePickerEntry(DevicePickerEntryKind.Radio, row.Label,
                        DeviceRoster.ConnectGlyph(row.ConnectKind), true, row.IsChecked, null, () => DeviceVerbs.Run(intent)),
                    _ => new DevicePickerEntry(DevicePickerEntryKind.Info, row.Label, "", false, false, null, null),
                };
            }
            return entries;
        }
    }

    // ── the playback-failure toasts ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Which toast a bump of the model's two failure counters earns. A counter that has not MOVED since the
    /// last look says nothing (a remount re-reads the same values); a counter that moved says it once, however far.</summary>
    public enum PlaybackFailure : byte { None, ClaimRejected, TransferFailed }

    public static class PlaybackFailureToasts
    {
        /// <summary>The failure (if any) between the previous counters and the current ones. A rejected claim wins a tie:
        /// it is the newer, more specific story when both moved in one frame.</summary>
        public static PlaybackFailure Between(uint previousClaimRejected, uint claimRejected,
                                              uint previousTransferFailed, uint transferFailed)
            => claimRejected != previousClaimRejected ? PlaybackFailure.ClaimRejected
             : transferFailed != previousTransferFailed ? PlaybackFailure.TransferFailed
             : PlaybackFailure.None;
    }

    // ── one Previous/Next rule for the bar, the stage and the video overlay ──────────────────────────────────────────

    /// <summary>The skip enablement, read by EVERY transport surface from the same fact set
    /// (<see cref="PlayerBarRules.Fold"/>): armed while playable OR errored (skipping off a dead row is the way out),
    /// ANDed with the context's own Next/Previous permission. The stage and the video overlay used to keep their own
    /// (`hasTrack &amp;&amp; !error`, `!Current.IsNone`), so they armed buttons the reducer then refused.</summary>
    public static class SkipRule
    {
        public static PlayerBarFacts Facts(bool hasCurrent, Playback.Fault error, Playback.Phase phase, bool buffering,
                                           Playback.RecoveryKind recovery, bool prevAllowed, bool nextAllowed)
            => PlayerBarRules.Fold(hasCurrent, error, phase, buffering, recovery, prevAllowed, nextAllowed);
    }
}
