// ── Wavee.ShellTests/TierLayoutTests.LibraryPicker.cs — the library head's sort button <-> word rail swap across a tier crossing (C-Library-Small, C-Library) ──
//
// The Albums library's second head row is the sort BUTTON at Small (C-Library-Small) and the word RAIL at Standard and Huge: two different trees. A crossing between them is
// the shared tier cross-fade (`ClassicTierMotion.SwapFade`) in ONE slot (`lib:picker`) that reserves the row's height, so the head never jumps while the old form fades out
// beside the new one. Held on the RECORD, never on a hard-coded edge: after every crossing the form on screen is the one `ClassicLibraryTiers.Of(...)` says for the tier the
// page published, the slot is the same node, and its height is the record's `ControlH` in both forms.
//
// Crossings: 800 <-> 1440 (the page is Small, then Standard), and the window edges 1007 / 1008 / 1047 (a widening holds 40 past 1008: Standard from 1048).

using FluentGpu.Foundation;
using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed partial class TierLayoutTests
{
    public static TheoryData<float, float> LibraryPickerCrossings() => new()
    {
        { 800f, 1440f }, { 1440f, 800f },
        { 1007f, 1008f }, { 1008f, 1007f },
        { 1007f, 1047f }, { 1007f, 1048f }, { 1048f, 1007f },
    };

    /// <summary>The form the head's second row shows is the record's for the published tiers: the sort button, or the word rail; one form once the cross-fade has run out.</summary>
    static void AssertPickerForm(Shell.HeadlessShell shell, string what)
    {
        bool single = !shell.Find("lib:collapsed").IsNull;
        var layout = ClassicLibraryTiers.Of(Tiers, single);
        bool button = layout.SortButton;   // the Albums library: neither Artists nor Podcasts
        Assert.Equal(button, !shell.Find("lib:sort").IsNull);
        Assert.False(shell.Find("lib:picker").IsNull, $"{what}: the head's picker slot is not mounted");
        Assert.Equal(button, !shell.Find("lib:picker:sort").IsNull);
        Assert.Equal(!button, !shell.Find("lib:picker:rail").IsNull);
    }

    [Theory, MemberData(nameof(LibraryPickerCrossings))]
    public void The_library_sort_button_and_word_rail_swap_in_one_reserved_slot(float from, float to)
        => WithLibraryState("albums", view: 1, () => OnPage(LibraryAlbums.Route, LibraryAlbums.ReadyKeyPrefix, from, shell =>
        {
            bool narrowing = to < from;
            string what = $"Library {from} -> {to}";
            var scene = shell.Host.Scene;
            AssertPickerForm(shell, what + " (before)");
            var slot = PartOf(shell, "lib:picker");
            var toolbar = PartOf(shell, "lib:toolbar");
            var rowsBefore = shell.Find("lib:collapsed").IsNull ? ClassicLibraryTiers.Of(Tiers, single: false).ControlH : ClassicLibraryTiers.Of(Tiers, single: true).ControlH;
            Assert.Equal(rowsBefore, slot.Local.H, 0.5f);

            ResizedFrame(shell, to, narrowing);
            CrossingFrame(shell, narrowing);

            // The slot is the same node and its height never left the record's: both forms share it while they overlap.
            AssertSameNode(shell, slot, "lib:picker", what);
            AssertSameNode(shell, toolbar, "lib:toolbar", what);
            var layout = ClassicLibraryTiers.Of(Tiers, single: !shell.Find("lib:collapsed").IsNull);
            Assert.Equal(layout.ControlH, scene.Bounds(slot.Node).H, 0.5f);

            // Once the cross-fade has run out exactly the record's form is mounted, and the slot is still the record's height.
            shell.RunFrames(60);
            AssertPickerForm(shell, what + " (after)");
            AssertSameNode(shell, slot, "lib:picker", what);
            Assert.Equal(ClassicLibraryTiers.Of(Tiers, single: !shell.Find("lib:collapsed").IsNull).ControlH, scene.Bounds(slot.Node).H, 0.5f);
        }));
}
