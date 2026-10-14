// ── Theming/Contracts/EpisodeRowStyle.cs — what a structure supplies for the podcast episode row (themes plan 6.1, T8c) ─────────
//
// The episode row is the show reader's row (W3): art, the title line with its badges, the two-line blurb, the date / "23 min left" /
// "played" meta line, the 3-DIP progress rule, the hover cluster (the "..." menu and the tone disc) and the now-playing tone bar.
// It is built ONCE per slot (a bound list slot, re-run only when the slot's kind or the width arm flips) or once over a fixed scope (the
// visit head's few eager rows), with every per-episode value a bind over the slot's equality-gated `Episode.RowItem`, so a recycle
// re-binds and allocates nothing. The structure registers an `EpisodeRowStyle` under `StyleKeys.EpisodeRow`; the three places that
// mount an episode row (the show reader and its head, the playlist table's episode kind, the cold skeletons) resolve it from the
// ambient registry once per host and hand it to `Episode.ReaderRow` / `Episode.SeedReaderRow`.
//
// WHAT THE CORE KEEPS. The reveal gate of the row (`Episode.ReaderRow`: a `SkelRegionEl` over `ReaderLoadState`: pending shows the seed,
// failed the failure row, ready the content), every decision the row reads (`Episode.PlayedOf`, `InProgressOf`, `LeftOf`, `IsNew`,
// `ShownTitle`, `NumeralOf`, ... and the one completion rule `ReaderPct`: a completed episode reads 1, so "played", the rule and the
// host's filter and ledger never disagree), the verbs (`Episode.RowContext`: the tone, the disc's play, the row's open, the menu, the
// selection seam), the host that builds the context and the retry (`Episode.RetryRow`). A template draws and never names `Playback`,
// `Entities`, `Store` or `Prefs` (WVTH002).

using FluentGpu.Controls;
using FluentGpu.Dsl;

namespace Wavee.Theming.Contracts;

/// <summary>Draws the row of a bound slot: every per-episode value bound over <paramref name="item"/>, the verbs resolved at
/// invocation from the slot's CURRENT item. Called once per slot per width arm.</summary>
internal delegate Element EpisodeRowContentTemplate(in BoundItemScope<Episode.RowItem> item, Episode.RowContext ctx, bool narrow);

/// <summary>Draws the cold skeleton's row: figure-space title and blurb bars, a representative date and length and the art tile, in
/// the real row's geometry (the seed is as tall as the live row, so a hydrating row never re-measures its list). No cluster, no rule.</summary>
internal delegate Element EpisodeRowSeedTemplate(bool narrow);

/// <summary>Draws the row of an episode whose ask failed: the art, the unavailable copy and a Retry that re-asks
/// (<c>Episode.RetryRow</c>).</summary>
internal delegate Element EpisodeRowFailureTemplate(Episode episode, bool narrow);

/// <summary>The structure's style for the episode row (<c>StyleKeys.EpisodeRow</c>).</summary>
public sealed class EpisodeRowStyle
{
    internal EpisodeRowStyle(EpisodeRowContentTemplate content, EpisodeRowSeedTemplate seed, EpisodeRowFailureTemplate failure)
    {
        Content = content;
        Seed = seed;
        Failure = failure;
    }

    /// <summary>The row of a bound slot.</summary>
    internal EpisodeRowContentTemplate Content { get; }

    /// <summary>The cold skeleton's row.</summary>
    internal EpisodeRowSeedTemplate Seed { get; }

    /// <summary>The failed episode's row.</summary>
    internal EpisodeRowFailureTemplate Failure { get; }
}
