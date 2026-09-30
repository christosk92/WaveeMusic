// ── Wavee.Tests/DevicePickerRulesTests.cs — the shared device-picker items, the one skip rule, the failure toasts ──────
// (Shell/Shell.DevicePicker.cs). Pure: nothing here touches a signal, a flyout or the reducer.
//
//   ONE ITEM BUILDER. The bar's Devices flyout and the stage's "Playing on" flyout render `DevicePicker.Items`, so the
//   same roster + owner are the same rows, the same "remote" verdict and the same click intents.
//
//   A THIS-COMPUTER CLICK PULLS PLAYBACK HERE unless we already own it (`TakeOver`), and never asks Spotify to transfer
//   to our own hash.
//
//   ONE SKIP RULE. Previous/Next on the bar, the stage and the video overlay read the same `PlayerBarFacts`.

using Xunit;

using DeviceKind = Wavee.Spotify.Decode.DeviceKind;

namespace Wavee.Tests;

public class DevicePickerItemsTests
{
    static Playback.Devices.Row Row(string id, DeviceKind kind) => new() { Id = id, Name = id, Kind = kind };

    static readonly Playback.Devices.Row[] Roster = [Row("pc", DeviceKind.ThisDevice), Row("phone", DeviceKind.Phone)];

    static Playback.Audio.LocalAudioDevice Local(string id) => new(id, id, 0, IsDefault: false);

    static List<Shell.DevicePickerItem> Build(Playback.Owner owner, int activeSlot, bool supported = true)
        => Shell.DevicePicker.Items(owner, activeSlot, Roster, [Local("speakers")], null, supported, default, default);

    static Shell.DevicePickerItem Find(List<Shell.DevicePickerItem> items, Shell.DevicePickerRowKind kind)
        => items.Find(i => i.Row.Kind == kind);

    [Fact]
    public void The_same_inputs_give_identical_items_on_every_surface()
    {
        var bar = Build(Playback.Owner.Foreign, 1);
        var stage = Build(Playback.Owner.Foreign, 1);
        Assert.Equal(bar.Count, stage.Count);
        for (int i = 0; i < bar.Count; i++) Assert.Equal(bar[i], stage[i]);
    }

    [Fact]
    public void The_remote_verdict_is_the_roster_rule_for_every_owner()
    {
        Assert.True(Shell.DevicePicker.IsRemote(Playback.Owner.Foreign, 1, Roster));
        Assert.False(Shell.DevicePicker.IsRemote(Playback.Owner.Nobody, 1, Roster));
        Assert.False(Shell.DevicePicker.IsRemote(Playback.Owner.Us, 1, Roster));
        Assert.False(Shell.DevicePicker.IsRemote(Playback.Owner.Foreign, 0, Roster));   // this device is never remote
        Assert.False(Shell.DevicePicker.IsRemote(Playback.Owner.Foreign, 9, Roster));
    }

    [Fact]
    public void Remote_playback_checks_the_connect_row_not_this_computer()
    {
        var items = Build(Playback.Owner.Foreign, 1);
        Assert.False(Find(items, Shell.DevicePickerRowKind.LocalDefault).Row.IsChecked);
        Assert.True(Find(items, Shell.DevicePickerRowKind.ConnectDevice).Row.IsChecked);
    }

    [Fact]
    public void A_this_computer_click_while_a_foreign_device_owns_playback_takes_over()
    {
        var items = Build(Playback.Owner.Foreign, 1);
        foreach (var kind in new[] { Shell.DevicePickerRowKind.LocalDefault, Shell.DevicePickerRowKind.LocalDevice })
        {
            var intent = Find(items, kind).Intent;
            Assert.Equal(Shell.DeviceIntentKind.SelectLocal, intent.Kind);
            Assert.True(intent.TakeOver);
        }
        Assert.Equal("", Find(items, Shell.DevicePickerRowKind.LocalDefault).Intent.LocalId);
        Assert.Equal("speakers", Find(items, Shell.DevicePickerRowKind.LocalDevice).Intent.LocalId);
    }

    [Fact]
    public void A_this_computer_click_when_we_already_own_playback_only_selects_the_output()
    {
        var items = Build(Playback.Owner.Us, 0);
        Assert.Equal(Shell.DeviceIntentKind.SelectLocal, Find(items, Shell.DevicePickerRowKind.LocalDefault).Intent.Kind);
        Assert.False(Find(items, Shell.DevicePickerRowKind.LocalDefault).Intent.TakeOver);
        Assert.False(Find(items, Shell.DevicePickerRowKind.LocalDevice).Intent.TakeOver);
    }

    [Fact]
    public void Take_over_is_needed_unless_we_are_the_owner()
    {
        Assert.False(Shell.DevicePicker.NeedsTakeOver(Playback.Owner.Us));
        Assert.True(Shell.DevicePicker.NeedsTakeOver(Playback.Owner.Foreign));
        Assert.True(Shell.DevicePicker.NeedsTakeOver(Playback.Owner.Nobody));
    }

    [Fact]
    public void A_connect_row_transfers_by_id_and_inert_rows_do_nothing()
    {
        var items = Build(Playback.Owner.Us, 0);
        var connect = Find(items, Shell.DevicePickerRowKind.ConnectDevice).Intent;
        Assert.Equal(Shell.DeviceIntentKind.TransferTo, connect.Kind);
        Assert.Equal("phone", connect.ConnectId);
        Assert.Equal(Shell.DeviceIntentKind.None, Find(items, Shell.DevicePickerRowKind.Header).Intent.Kind);
        Assert.Equal(Shell.DeviceIntentKind.None, Find(items, Shell.DevicePickerRowKind.Quality).Intent.Kind);
        Assert.Equal(Shell.DeviceIntentKind.None, Find(items, Shell.DevicePickerRowKind.Separator).Intent.Kind);
    }

    [Fact]
    public void Unsupported_local_playback_leaves_the_this_computer_rows_inert()
    {
        var items = Build(Playback.Owner.Foreign, 1, supported: false);
        Assert.Equal(Shell.DeviceIntentKind.None, Find(items, Shell.DevicePickerRowKind.LocalDefault).Intent.Kind);
        Assert.Equal(Shell.DeviceIntentKind.None, Find(items, Shell.DevicePickerRowKind.LocalDevice).Intent.Kind);
    }
}

public class SkipRuleTests
{
    static Shell.PlayerBarFacts Facts(bool hasCurrent = true, Playback.Fault error = Playback.Fault.None,
        Playback.Phase phase = Playback.Phase.Playing, bool buffering = false, bool prev = true, bool next = true)
        => Shell.SkipRule.Facts(hasCurrent, error, phase, buffering, Playback.RecoveryKind.None, prev, next);

    [Fact]
    public void An_active_row_arms_skip_exactly_as_the_context_allows()
    {
        var f = Facts();
        Assert.True(f.PrevEnabled);
        Assert.True(f.NextEnabled);
        Assert.False(Facts(next: false).NextEnabled);
        Assert.False(Facts(prev: false).PrevEnabled);
        Assert.True(Facts(next: false).PrevEnabled);
    }

    [Fact]
    public void Skipping_off_a_dead_row_stays_armed_while_the_satellites_go_dark()
    {
        var f = Facts(error: Playback.Fault.Unavailable);
        Assert.True(f.PrevEnabled);
        Assert.True(f.NextEnabled);
        Assert.False(f.CanTransport);
    }

    [Fact]
    public void Nothing_on_the_deck_arms_nothing()
    {
        var f = Facts(hasCurrent: false);
        Assert.False(f.PrevEnabled);
        Assert.False(f.NextEnabled);
        Assert.False(f.CanTransport);
    }

    [Fact]
    public void Every_surface_reading_the_same_inputs_gets_the_same_facts()
    {
        // The bar, the stage and the video overlay all read `Shell.TransportFacts()`, which is this one fold.
        Assert.Equal(Facts(error: Playback.Fault.Network, next: false), Facts(error: Playback.Fault.Network, next: false));
        Assert.False(Facts(next: false).NextEnabled);   // a restricted context greys Next on all three, not just the bar
    }
}

public class PlaybackFailureToastsTests
{
    [Fact]
    public void An_unmoved_counter_says_nothing()
        => Assert.Equal(Shell.PlaybackFailure.None, Shell.PlaybackFailureToasts.Between(3, 3, 7, 7));

    [Fact]
    public void A_rejected_claim_is_the_take_over_toast()
        => Assert.Equal(Shell.PlaybackFailure.ClaimRejected, Shell.PlaybackFailureToasts.Between(3, 4, 7, 7));

    [Fact]
    public void A_failed_transfer_is_the_transfer_toast()
        => Assert.Equal(Shell.PlaybackFailure.TransferFailed, Shell.PlaybackFailureToasts.Between(3, 3, 7, 8));

    [Fact]
    public void A_claim_rejection_wins_when_both_move_in_one_frame()
        => Assert.Equal(Shell.PlaybackFailure.ClaimRejected, Shell.PlaybackFailureToasts.Between(3, 4, 7, 8));
}
