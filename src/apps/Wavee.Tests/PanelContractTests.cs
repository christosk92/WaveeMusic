// ── Wavee.Tests/PanelContractTests.cs — the decisions behind the panel's views (themes plan 6.1, T6b) ──────────────────────────
// (Shell/Rail.cs, Shell/Shell.DevicePicker.cs.) Pure: nothing here touches a signal, a flyout, a mounted tree or the video model.
//
//   THE VOCABULARY    `PanelMode` is the rail's mode under the contract's name: same members, values and order (the player bar's
//                     toggles write it and the session document persists it).
//   THE PINNED SLOT   which occupant the now-playing arm's one slot has: the tile, the docked video, or the tile with the Art | Video
//                     switch over its corner.
//   THE PICKER        the items as the contract's entries: a separator, information rows (headers, the quality echo, "no devices"),
//                     this computer's commands (inert while local playback is unsupported) and Connect radios (checked when active),
//                     each with the glyph and the verb the roster rules give it.
//   THE TRANSPORT     the seek rail's, the chapter decoration's, the readout's and the time label's keys (leftovers P3) are published under
//                     their ids and Classic draws each; the credits line's empty value is safe to read.

using FluentGpu.Controls;
using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

using DeviceKind = Wavee.Spotify.Decode.DeviceKind;

namespace Wavee.Tests;

public class PanelModeTests
{
    [Fact]
    public void The_panel_mode_mirrors_the_rail_mode_member_for_member()
    {
        Assert.Equal(Enum.GetNames<Shell.RailMode>(), Enum.GetNames<PanelMode>());
        foreach (var name in Enum.GetNames<Shell.RailMode>())
            Assert.Equal((byte)Enum.Parse<Shell.RailMode>(name), (byte)Enum.Parse<PanelMode>(name));
    }

    [Theory]
    [InlineData(true, false, true, PanelPinned.Art)]                    // the page stage hosts the video: the tile alone
    [InlineData(true, true, true, PanelPinned.Art)]                     // ... even when the placement still resolves to Docked
    [InlineData(false, true, false, PanelPinned.DockedVideo)]           // a docked video IS the slot
    [InlineData(false, true, true, PanelPinned.DockedVideo)]
    [InlineData(false, false, false, PanelPinned.Art)]                  // nothing to dock: the tile alone, no switch
    [InlineData(false, false, true, PanelPinned.ArtWithVideoToggle)]    // the video could be docked here: the tile with the switch
    public void The_pinned_slot_has_exactly_one_occupant(bool stageHosts, bool dockedResolved, bool dockAllowed, PanelPinned expected)
        => Assert.Equal(expected, Rail.VideoCoupling.PinnedFor(stageHosts, dockedResolved, dockAllowed));
}

public class DevicePickerEntriesTests
{
    static Playback.Devices.Row Row(string id, DeviceKind kind) => new() { Id = id, Name = id, Kind = kind };

    static readonly Playback.Devices.Row[] Roster = [Row("pc", DeviceKind.ThisDevice), Row("phone", DeviceKind.Phone)];

    static Playback.Audio.LocalAudioDevice Local(string id) => new(id, id, 0, IsDefault: false);

    static DevicePickerEntry[] Build(Playback.Owner owner, int activeSlot, bool supported = true, bool withConnect = true)
        => Shell.DevicePicker.Entries(Shell.DevicePicker.Items(owner, activeSlot, withConnect ? Roster : [], [Local("speakers")], null, supported, default, default));

    static DevicePickerEntry First(DevicePickerEntry[] entries, DevicePickerEntryKind kind) => Array.Find(entries, e => e.Kind == kind);

    [Fact]
    public void There_is_one_entry_per_item_in_menu_order()
    {
        var items = Shell.DevicePicker.Items(Playback.Owner.Us, 0, Roster, [Local("speakers")], null, true, default, default);
        var entries = Shell.DevicePicker.Entries(items);
        Assert.Equal(items.Count, entries.Length);
        for (int i = 0; i < items.Count; i++) Assert.Equal(items[i].Row.Label, entries[i].Label);
    }

    [Fact]
    public void Headers_the_quality_echo_and_the_separator_are_not_choosable()
    {
        var entries = Build(Playback.Owner.Us, 0);
        Assert.Single(entries, e => e.Kind == DevicePickerEntryKind.Separator);
        foreach (var e in entries.Where(e => e.Kind is DevicePickerEntryKind.Separator or DevicePickerEntryKind.Info))
        {
            Assert.False(e.Enabled);
            Assert.Null(e.Invoke);
        }
        Assert.Contains(entries, e => e.Kind == DevicePickerEntryKind.Info);
    }

    [Fact]
    public void This_computers_rows_are_commands_with_the_roster_glyphs_and_a_verb()
    {
        var entries = Build(Playback.Owner.Us, 0);
        var commands = entries.Where(e => e.Kind == DevicePickerEntryKind.Command).ToArray();
        Assert.Equal(2, commands.Length);                                         // the system default and the one output
        Assert.Equal(Icons.ThisPc, commands[0].Glyph);
        Assert.Equal(Shell.DeviceRoster.LocalGlyph(0), commands[1].Glyph);
        foreach (var c in commands)
        {
            Assert.True(c.Enabled);
            Assert.False(c.IsChecked);                                            // a command, never a radio: no row claims to be "the endpoint in use"
            Assert.NotNull(c.Invoke);
            Assert.Null(c.Accelerator);
        }
    }

    [Fact]
    public void Unsupported_local_playback_leaves_this_computers_rows_disabled_with_a_note_and_no_verb()
    {
        var commands = Build(Playback.Owner.Foreign, 1, supported: false).Where(e => e.Kind == DevicePickerEntryKind.Command).ToArray();
        Assert.Equal(2, commands.Length);
        foreach (var c in commands)
        {
            Assert.False(c.Enabled);
            Assert.Null(c.Invoke);
            Assert.False(string.IsNullOrEmpty(c.Accelerator));                    // "Unavailable"
        }
    }

    [Fact]
    public void A_remote_device_is_the_checked_radio_and_this_computer_is_not()
    {
        var entries = Build(Playback.Owner.Foreign, 1);
        var radio = First(entries, DevicePickerEntryKind.Radio);
        Assert.Equal("phone", radio.Label);
        Assert.True(radio.IsChecked);
        Assert.True(radio.Enabled);
        Assert.NotNull(radio.Invoke);
        Assert.Equal(Shell.DeviceRoster.ConnectGlyph(DeviceKind.Phone), radio.Glyph);
    }

    [Fact]
    public void Local_playback_leaves_the_connect_device_unchecked()
    {
        var radio = First(Build(Playback.Owner.Us, 0), DevicePickerEntryKind.Radio);
        Assert.False(radio.IsChecked);
        Assert.NotNull(radio.Invoke);                                             // choosing it transfers playback
    }

    [Fact]
    public void An_empty_roster_states_there_are_no_devices_and_offers_no_radio()
    {
        var entries = Build(Playback.Owner.Nobody, 0, withConnect: false);
        Assert.DoesNotContain(entries, e => e.Kind == DevicePickerEntryKind.Radio);
        // After the Connect header: two disabled rows (no devices, and the hint).
        var tail = entries.Skip(Array.FindLastIndex(entries, e => e.Kind == DevicePickerEntryKind.Separator) + 1).ToArray();
        Assert.Equal(3, tail.Length);
        Assert.All(tail, e => Assert.Equal(DevicePickerEntryKind.Info, e.Kind));
    }
}

public class PlayerTransportContractTests
{
    [Theory]
    [InlineData("Player.SeekRail", "classic.player.seek-rail")]
    [InlineData("Player.SeekChapters", "classic.player.seek-chapters")]
    [InlineData("Player.ChapterTip", "classic.player.chapter-tip")]
    [InlineData("Player.SeekReadout", "classic.player.seek-readout")]
    [InlineData("Player.TimeLabel", "classic.player.time-label")]
    public void Classic_draws_every_transport_piece(string id, string template)
        => Assert.Equal(template, ClassicTheme.Create().Registry.TemplateIdOf(id));

    [Fact]
    public void The_keys_carry_their_published_ids()
    {
        Assert.Equal("Player.SeekRail", ViewKeys.SeekRail.Id);
        Assert.Equal("Player.SeekChapters", ViewKeys.SeekChapters.Id);
        Assert.Equal("Player.ChapterTip", ViewKeys.ChapterTip.Id);
        Assert.Equal("Player.SeekReadout", ViewKeys.SeekReadout.Id);
        Assert.Equal("Player.TimeLabel", ViewKeys.TimeLabel.Id);
    }

    [Fact]
    public void The_empty_credits_line_is_safe_to_read()
    {
        var none = PlayerCredits.None;
        Assert.Null(none.Show);
        Assert.Empty(none.Artists);
        Assert.Equal("", none.OverflowKey);
        Assert.Empty(none.OverflowRows());
    }

    [Theory]
    [InlineData(true, true, true)]      // the right slot while live: the LIVE mark / GO LIVE
    [InlineData(true, false, false)]    // the right slot of a track: digits
    [InlineData(false, true, false)]    // the elapsed slot is digits even while live (since tune-in)
    public void Only_the_right_slot_turns_into_the_live_slot(bool right, bool live, bool expected)
        => Assert.Equal(expected, Shell.TimeLabel.RightSlotIsLive(right, live));
}
