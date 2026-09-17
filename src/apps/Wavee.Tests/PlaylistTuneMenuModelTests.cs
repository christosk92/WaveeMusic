// ── Wavee.Tests/PlaylistTuneMenuModelTests.cs — WP-5.O stream A ──────────────────────────────────────────────────────
// 0.2.9's PlaylistSignalsTests.MenuModel_HidesUnlabelledChoices_AndShowsResetOnlyWhenSelected, verbatim over
// `TuneOption` (the store-backed facts of that file exercised 0.2.9's PlaylistFetcher; their 0.3 twins are the
// format-attributes facts in PlaylistDecodeTests). Pure.

using System.Collections.Generic;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class PlaylistTuneMenuModelTests
{
    const string ChoiceA = "session_control_display$mix$more_discovery";
    const string ChoiceB = "session_control_display$mix$soft_pop:nl_genre";
    const string Reset = "session-control-reset";

    static List<TuneOption> Options() =>
    [
        new(ChoiceA, "More discovery tracks", TuningOptionKind.Choice),
        new(ChoiceB, null, TuningOptionKind.Choice),
        new(Reset, null, TuningOptionKind.Reset),
    ];

    [Fact]
    public void MenuModel_HidesUnlabelledChoices_AndShowsResetOnlyWhenSelected()
    {
        var options = Options();

        Assert.True(PlaylistTuneMenuModel.IsEligible(options, sourceAvailable: true));
        Assert.False(PlaylistTuneMenuModel.IsEligible(options, sourceAvailable: false));
        Assert.Equal(ChoiceA, Assert.Single(PlaylistTuneMenuModel.VisibleChoices(options)).Identifier);
        Assert.Null(PlaylistTuneMenuModel.ResetOption(options, selectedIdentifier: null));
        Assert.Equal(Reset, PlaylistTuneMenuModel.ResetOption(options, selectedIdentifier: ChoiceA)!.Value.Identifier);
    }

    /// <summary>Item 56's other half: a list whose only named options are blank is not a Tune command at all.</summary>
    [Fact]
    public void NoNamedChoice_IsNotEligible()
    {
        Assert.False(PlaylistTuneMenuModel.IsEligible(null, sourceAvailable: true));
        Assert.False(PlaylistTuneMenuModel.IsEligible([new TuneOption(ChoiceB, "  ", TuningOptionKind.Choice), new TuneOption(Reset, "Reset", TuningOptionKind.Reset)], true));
    }

    [Fact]
    public void AnEmptySelection_IsUntuned()
        => Assert.Null(PlaylistTuneMenuModel.ResetOption(Options(), selectedIdentifier: ""));
}
