// ── Shell/Shell.DevicePicker.cs ────────────────────────────────────────────────────────────────────────────────────
// where Wavee plays, as one pure rule: the player bar's Devices flyout (`Player.DevicePicker`) and the full screen's device menu both
// read `DevicePicker.Compose`, so the same roster + owner + outputs give the same card, the same rows, the same "remote" verdict and
// the same click intents on every surface
//
// Role: CORE
//
// The redesign (2026-11-19, the owner's complaint: "slop", "most of these audio outputs are unusable"): the picker showed Windows'
// raw endpoint names, a headphones icon on speakers, a "System default" row that duplicated a device, the quality line as if it
// were a device and three grey rows for an empty Spotify Connect section. Now it answers with a "Playing on" card (the device in use,
// named by `Playback.AudioDeviceNaming`, its connection, what plays, the volume; while another device plays, the way back), the two
// sections ("This PC": Match Windows, then the outputs; "Other devices": Spotify Connect, or "Nothing else nearby"), the other
// devices first while one of them plays, and a footer (the quality as a door to its dial, "Sound settings" with the switches at rest).
//
// A click is a DATA intent the surface only executes (`Shell.DeviceVerbs.Run`): choosing a this-PC output persists it and, unless we
// already own playback, takes playback over (`Playback.TakeOver`, a local claim, never a transfer to ourselves); a Connect device
// transfers.

using System.Collections.Generic;

using FluentGpu.Controls;
using FluentGpu.Localization;

using Wavee.Theming.Contracts;

using P = Wavee.Strings.Player.DevicePicker;

namespace Wavee;

public static partial class Shell
{
    public enum DeviceIntentKind : byte
    {
        None,          // nothing happens
        SelectLocal,   // a this-PC row: persist the output, then (TakeOver) pull playback here
        TransferTo,    // a Spotify Connect row: transfer to that device
    }

    /// <summary>What a picker row does when clicked. <see cref="LocalId"/> is "" for Match Windows.
    /// <see cref="TakeOver"/> is decided HERE from the owner (true iff we are not already the owner), never re-derived
    /// by a surface. <see cref="ConnectId"/> is resolved to a roster slot at CLICK time (the roster may have moved).</summary>
    public readonly record struct DeviceIntent(
        DeviceIntentKind Kind, string LocalId = "", bool TakeOver = false, string ConnectId = "");

    public static class DevicePicker
    {
        /// <summary>The ONE "playback is on another Connect device" verdict every surface reads (owner-gated, valid slot,
        /// never this device) — <see cref="DeviceRoster.RemoteSlot"/> &gt;= 0.</summary>
        public static bool IsRemote(Playback.Owner owner, int activeSlot, ReadOnlySpan<Playback.Devices.Row> connect)
            => DeviceRoster.RemoteSlot(owner, activeSlot, connect) >= 0;

        /// <summary>A click on a this-PC row pulls playback here unless we already own it.</summary>
        public static bool NeedsTakeOver(Playback.Owner owner) => owner != Playback.Owner.Us;

        /// <summary>The quality chip's link: Settings at the audio quality dial (the deep link highlights it).</summary>
        public static readonly Action OpenQuality = static () => InContextDoors.OpenSettings("sound.quality");

        /// <summary>"Sound settings": Settings at the Sound category.</summary>
        public static readonly Action OpenSound = static () => InContextDoors.OpenSettings("sound");

        /// <summary>The identity of Match Windows' row.</summary>
        public const string MatchWindowsId = "match-windows";

        /// <summary>The output this PC plays on (<c>Playback.Audio.OpenOutput</c>'s rule): the chosen one while it is in the roster and opens,
        /// else the Windows default, else the first; -1 with no outputs. <paramref name="chosen"/> is the chosen one's index, -1 when it is Match
        /// Windows or the chosen one left the roster (unplugged).</summary>
        public static int InUse(ReadOnlySpan<Playback.Audio.LocalAudioDevice> local, string? selectedLocalId, string? unresponsiveId,
            out int chosen, out int windowsDefault)
        {
            chosen = windowsDefault = -1;
            for (int i = 0; i < local.Length; i++)
            {
                if (chosen < 0 && !string.IsNullOrEmpty(selectedLocalId) && string.Equals(local[i].Id, selectedLocalId, StringComparison.OrdinalIgnoreCase)) chosen = i;
                if (windowsDefault < 0 && local[i].IsDefault) windowsDefault = i;
            }
            bool stuck = chosen >= 0 && Same(local[chosen].Id, unresponsiveId);
            return chosen >= 0 && !stuck ? chosen : windowsDefault >= 0 ? windowsDefault : local.Length > 0 ? 0 : -1;
        }

        static bool Same(string? a, string? b) => a is { Length: > 0 } && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>Where Wavee plays, decided (see the file header). The roster, owner and outputs are read by the caller; nothing here
        /// subscribes. <paramref name="stamp"/> is the caller's fingerprint of the inputs.</summary>
        public static DevicePickerView Compose(
            Playback.Owner owner,
            int activeSlot,
            ReadOnlySpan<Playback.Devices.Row> connect,
            ReadOnlySpan<Playback.Audio.LocalAudioDevice> local,
            string? selectedLocalId,
            string? selectedName,
            bool localSupported,
            string? unresponsiveId,
            Playback.Audio.Opened observed,
            Spotify.Audio.Quality asked,
            SoundState sound,
            int stamp)
        {
            int remoteSlot = DeviceRoster.RemoteSlot(owner, activeSlot, connect);
            bool remote = remoteSlot >= 0;
            bool takeOver = NeedsTakeOver(owner);
            string? unavailable = localSupported ? null : Loc.Get(Strings.Player.Unavailable);
            int inUse = InUse(local, selectedLocalId, unresponsiveId, out int chosen, out int windowsDefault);
            string? defaultName = windowsDefault >= 0 ? local[windowsDefault].Name : null;
            // The chosen output left the roster (unplugged, disabled): it stays the choice and keeps its row, saying so, while the default plays.
            bool chosenGone = chosen < 0 && !string.IsNullOrEmpty(selectedLocalId);
            bool matchWindows = chosen < 0 && !chosenGone;

            // ── This PC: Match Windows (while this PC plays), then the outputs ──
            var thisPc = new List<DevicePickerEntry>(local.Length + 1);
            if (!remote)
            {
                var follow = new DeviceIntent(DeviceIntentKind.SelectLocal, LocalId: "", TakeOver: takeOver);
                thisPc.Add(new DevicePickerEntry(DevicePickerEntryKind.Command, Loc.Get(P.MatchWindows), Icons.ThisPc, localSupported,
                    localSupported && matchWindows, unavailable, localSupported ? () => DeviceVerbs.Run(follow) : null)
                {
                    Word = true, Chosen = localSupported && matchWindows, Id = MatchWindowsId,
                    Detail = defaultName is null ? null : P.FollowsWindows(defaultName),
                });
            }
            for (int i = 0; i < local.Length; i++)
            {
                ref readonly var d = ref local[i];
                bool unresponsive = unresponsiveId is { Length: > 0 } && string.Equals(d.Id, unresponsiveId, StringComparison.OrdinalIgnoreCase);
                var pick = new DeviceIntent(DeviceIntentKind.SelectLocal, LocalId: d.Id, TakeOver: takeOver);
                string? detail = Join(ConnectionWords(d.Connection), remote && d.IsDefault ? Loc.Get(P.WindowsDefault) : null,
                    unresponsive ? Loc.Get(P.NotResponding) : null);
                thisPc.Add(new DevicePickerEntry(DevicePickerEntryKind.Command, CapLabel(d.Name), DeviceRoster.LookGlyph(d.Look), localSupported,
                    localSupported && !remote && i == chosen, unavailable, localSupported ? () => DeviceVerbs.Run(pick) : null)
                {
                    Chosen = localSupported && i == chosen, Detail = detail, Unresponsive = unresponsive, Id = d.Id,
                });
            }
            if (chosenGone)
            {
                var retry = new DeviceIntent(DeviceIntentKind.SelectLocal, LocalId: selectedLocalId!, TakeOver: takeOver);
                thisPc.Add(new DevicePickerEntry(DevicePickerEntryKind.Command, CapLabel(selectedName is { Length: > 0 } ? selectedName : selectedLocalId!),
                    Icons.Speakers, localSupported, localSupported && !remote, unavailable, localSupported ? () => DeviceVerbs.Run(retry) : null)
                {
                    Chosen = localSupported, Detail = Loc.Get(P.NotResponding), Unresponsive = true, Id = selectedLocalId!,
                });
            }

            // ── Other devices: Spotify Connect, this device filtered out ──
            var others = new List<DevicePickerEntry>(connect.Length);
            string connectWord = Loc.Get(Strings.Player.SpotifyConnect);
            for (int i = 0; i < connect.Length; i++)
            {
                ref readonly var d = ref connect[i];
                if (d.Kind == Spotify.Decode.DeviceKind.ThisDevice) continue;
                var transfer = new DeviceIntent(DeviceIntentKind.TransferTo, ConnectId: d.Id ?? "");
                others.Add(new DevicePickerEntry(DevicePickerEntryKind.Radio, CapLabel(d.Name ?? ""), DeviceRoster.ConnectGlyph(d.Kind), true,
                    i == remoteSlot, null, () => DeviceVerbs.Run(transfer))
                {
                    Detail = connectWord, Id = d.Id ?? "",
                });
            }

            // ── the card ──
            DevicePickerCard card;
            if (remote)
            {
                ref readonly var r = ref connect[remoteSlot];
                var home = new DeviceIntent(DeviceIntentKind.SelectLocal, LocalId: selectedLocalId ?? "", TakeOver: true);
                card = new DevicePickerCard
                {
                    Name = r.Name ?? "", Glyph = DeviceRoster.ConnectGlyph(r.Kind), Connection = connectWord, Remote = true,
                    PlayHere = localSupported ? () => DeviceVerbs.Run(home) : null,
                    HereName = inUse >= 0 ? local[inUse].Name : null,
                    HereGlyph = inUse >= 0 ? DeviceRoster.LookGlyph(local[inUse].Look) : null,
                };
            }
            else if (inUse >= 0)
            {
                ref readonly var d = ref local[inUse];
                card = new DevicePickerCard
                {
                    Name = d.Name, Glyph = DeviceRoster.LookGlyph(d.Look), Connection = ConnectionWords(d.Connection),
                    Quality = string.IsNullOrEmpty(observed.Label) ? null : ObservedQualityWords(observed),
                    Unresponsive = Same(d.Id, unresponsiveId),
                };
            }
            else card = new DevicePickerCard { Name = Loc.Get(P.ThisPc), Glyph = Icons.Laptop, Connection = "" };

            // ── the footer ──
            var footer = remote
                ? new DevicePickerFooter { QualitySetBy = QualitySetBy(connect[remoteSlot].Kind), Sound = sound.Words, OpenSound = OpenSound }
                : new DevicePickerFooter { Quality = QualityChip(observed, asked), OpenQuality = OpenQuality, Sound = sound.Words, OpenSound = OpenSound };

            // ── the same answer as one flat menu (a menu-shaped surface) ──
            var entries = new List<DevicePickerEntry>(thisPc.Count + others.Count + 6);
            if (remote) { Others(entries, others); entries.Add(Separator); ThisPc(entries, thisPc); }
            else { ThisPc(entries, thisPc); entries.Add(Separator); Others(entries, others); }
            entries.Add(Separator);
            entries.Add(new DevicePickerEntry(DevicePickerEntryKind.Link, Loc.Get(Strings.Player.SoundSettings), Icons.Equalizer, true, false,
                sound.Words, OpenSound) { Word = true });

            return new DevicePickerView(entries, stamp)
            {
                Card = card, ThisPc = thisPc, Others = others, OthersFirst = remote, WindowsDefault = defaultName, Footer = footer,
            };
        }

        static readonly DevicePickerEntry Separator = new(DevicePickerEntryKind.Separator, "", "", false, false, null, null);

        static DevicePickerEntry Header(string key) => new(DevicePickerEntryKind.Info, Loc.Get(key), "", false, false, null, null) { Word = true };

        static void ThisPc(List<DevicePickerEntry> into, List<DevicePickerEntry> rows)
        {
            into.Add(Header(P.ThisPc));
            into.AddRange(rows);
        }

        static void Others(List<DevicePickerEntry> into, List<DevicePickerEntry> rows)
        {
            into.Add(Header(P.OtherDevices));
            if (rows.Count > 0) into.AddRange(rows);
            else into.Add(new DevicePickerEntry(DevicePickerEntryKind.Info, Loc.Get(P.NothingNearby), "", false, false, null, null)
            {
                Word = true, Detail = Loc.Get(P.NothingNearbyHint),
            });
        }

        /// <summary>The parts that have something to say, joined by " · " (null when none does).</summary>
        static string? Join(string? a, string? b, string? c)
        {
            string? text = null;
            foreach (string? part in (ReadOnlySpan<string?>)[a, b, c])
                if (!string.IsNullOrEmpty(part)) text = text is null ? part : P.Joined(text, part);
            return text;
        }

        /// <summary>How an output reaches this PC, in words ("Bluetooth", "USB", "HDMI", "This PC"), or "" when the OS did not say.</summary>
        public static string ConnectionWords(Playback.OutputConnection connection) => connection switch
        {
            Playback.OutputConnection.BuiltIn => Loc.Get(P.ThisPc),
            Playback.OutputConnection.Bluetooth => Loc.Get(P.Bluetooth),
            Playback.OutputConnection.Usb => Loc.Get(P.Usb),
            Playback.OutputConnection.Hdmi => Loc.Get(P.Hdmi),
            Playback.OutputConnection.DisplayPort => Loc.Get(P.DisplayPort),
            Playback.OutputConnection.Display => Loc.Get(P.Display),
            _ => "",
        };

        /// <summary>The quality chip while this PC plays: what the player opened in the setting's words, with the asked rung named when the
        /// open sits below it ("Very high · 320 kbps · Lossless unavailable": the verification the old quality row gave); "Quality" before
        /// anything opened. Never a loopback of the setting.</summary>
        static string QualityChip(Playback.Audio.Opened observed, Spotify.Audio.Quality asked)
        {
            if (string.IsNullOrEmpty(observed.Label)) return Loc.Get(Strings.Player.QualityMenu);
            int observedRung = Spotify.Audio.Rung(observed.Format);
            string quality = ObservedQualityWords(observed);
            return observedRung >= 0 && observedRung < Spotify.Audio.TargetRung(asked)
                ? P.QualityBelow(quality, Loc.Get(AskedQualityLabel(asked)))
                : quality;
        }

        /// <summary>Who sets the quality while a Connect device plays, by its kind.</summary>
        static string QualitySetBy(Spotify.Decode.DeviceKind kind) => Loc.Get(kind switch
        {
            Spotify.Decode.DeviceKind.Tv => P.QualitySetByTv,
            Spotify.Decode.DeviceKind.Speaker => P.QualitySetBySpeaker,
            Spotify.Decode.DeviceKind.Phone => P.QualitySetByPhone,
            _ => P.QualitySetByDevice,
        });
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
