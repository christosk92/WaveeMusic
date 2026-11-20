// ── Theming/Contracts/RateNudgeProps.cs — the props of the rate & star card (rate-nudge-implementation.md §5.1) ─────────────────
//
// The STATE stays core (`Screens/RateNudge*.cs`): whether an ask is due (every rule in `RateNudge.cs`), the card's clock (hover or
// keyboard focus holds it, an untouched card tucks away), what each button does (the Store dialog, the repository link, the ledger)
// and the words, already resolved in the user's language. A theme draws the card it is handed, binds `View` (null: no card), and
// calls the verbs; it never keeps a value of its own and never reaches the store (WVTH002).

using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>One card's resolved words. <c>Meta</c> is the star count line (a GitHub card with a known count, else null); <c>Big</c>,
/// <c>BigCaption</c> and <c>BigStats</c> are the milestone ("100 hours", "of music played in Wavee since July 2", "on 41 listening
/// days"), null on a card that is not a milestone's.</summary>
public sealed record RateNudgeView(
    string Title, string Body, string? Meta,
    string? Big, string? BigCaption, string? BigStats,
    string Primary, string Later, string Never,
    RateNudgeGlyph Glyph);

/// <summary>The primary button's glyph: the Store's bag (a rating) or a star (GitHub).</summary>
public enum RateNudgeGlyph : byte { Store, Star }

/// <summary>The props of <see cref="ViewKeys.ShellRateNudge"/>: the corner card over the page, above the player bar.</summary>
public sealed record RateNudgeCardProps
{
    /// <summary>The card on screen; null: no card (the template draws nothing but its pass-through lane).</summary>
    public required IReadSignal<RateNudgeView?> View { get; init; }

    /// <summary>Rate in the Store, or Star on GitHub. The card leaves as the dialog or the browser arrives.</summary>
    public required Action Primary { get; init; }

    /// <summary>Not now.</summary>
    public required Action Later { get; init; }

    /// <summary>Don't ask again (turns the Settings switch off).</summary>
    public required Action Never { get; init; }

    /// <summary>The pointer is over the card or keyboard focus is within it (true), or neither any more (false): holds the card's clock.</summary>
    public required Action<bool> Hold { get; init; }
}
