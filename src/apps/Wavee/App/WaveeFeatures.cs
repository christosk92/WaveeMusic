namespace Wavee;

/// <summary>
/// The ONE registry of Wavee's build-time feature gates: a surface that exists in the tree but is deliberately
/// withheld from the shipped UI while it is unfinished. Every gate is a <c>const bool</c> here, documented with what
/// it hides, why it is off, and the condition under which it must be DELETED (a flag with no removal condition is a
/// permanent branch, which is what this file exists to prevent).
///
/// <para><b>What belongs here.</b> Only "this feature is not ready to be seen yet". Nothing else:</para>
/// <list type="bullet">
/// <item><b>A user's choice</b> is a setting — <c>WaveeSettings</c> + <c>SettingsCatalog</c>, not a gate here. If the
///   user is meant to turn it on, it is a preference; if the user must not see it at all, it is a gate.</item>
/// <item><b>Diagnostics</b> are always on (CLAUDE.md: "No environment-variable switches for behaviour or
///   verification"). A gate here must never decide whether something is measured or logged.</item>
/// <item><b>A source/packaging split</b> is an MSBuild property (<c>WaveeSkipPrivateSources</c> →
///   <c>WAVEE_PLAYPLAY_LOCAL</c>), because it decides what COMPILES, not what renders.</item>
/// </list>
///
/// <para><b>Why <c>const</c> and not a setting.</b> A const is a single, greppable owner and lets the compiler drop
/// the dead branch, so a withheld surface cannot leak through a code path someone forgot to check. Flipping one is a
/// deliberate edit + rebuild, which is the right cost for "we are shipping this now". The inventory that explains
/// every gate to a human lives in <c>docs/guide/feature-flags.md</c>; this file is its single source of truth.</para>
/// </summary>
static class WaveeFeatures
{
    /// <summary>
    /// The Now Playing header's <b>Cover | ‹Player›</b> presentation switcher (<c>NpvHeaderRow</c>'s
    /// <c>SelectorBar</c>).
    ///
    /// <para><b>OFF</b> — withheld 2026-09-10 at the user's request while the player-style work
    /// (<c>docs/plans/wavee/npv-player-styles-implementation.md</c>) is in flight. The switcher is the only thing
    /// this hides: the gear beside it still opens <c>PlayerStyleFlyout</c>, and Settings › Appearance, the command
    /// palette and the artwork context menu still reach the same two prefs — so nothing is stranded, and a stored
    /// <c>npv.presentation = Player</c> keeps rendering its deck. Hiding those other entry points was NOT asked for
    /// and would be a bigger, separate decision.</para>
    ///
    /// <para><b>Delete this flag</b> (and the branch in <c>NpvHeaderRow</c>) once the player styles ship — the
    /// switcher is the feature's primary affordance, so the flag has no reason to outlive it.</para>
    /// </summary>
    public const bool NpvPresentationSwitcher = false;
}
