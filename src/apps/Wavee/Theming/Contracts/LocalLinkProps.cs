// ── Theming/Contracts/LocalLinkProps.cs — what "Your files and Spotify" hands a theme (#240 after 0.4, phase 1; board frames 28-30) ──────
//
// The core (`Playback/LocalLink*.cs`, `Shell/LocalLinkWords.cs`) owns the matching, the decisions and every sentence; a theme draws three
// views, each one picture for every theme (`Themes/Common/CommonLocalLink.cs`, registered by `CommonBlocks`), placed by the On this PC page:
//   · the offer, once, on the page's Home (F-A step 1): "Wavee can add covers and correct names from Spotify to 42 albums" · Show me · Not now;
//   · the opt-in dialog's body (F-A step 2): what is searched, what never leaves the PC, that the files stay untouched, and the
//     "Accept exact matches automatically" box (the dialog's title and its two buttons are the dialog's own);
//   · the review (F-A step 5, F-B): every group with its candidates and plain evidence, filtered by confidence, with Accept, Not this album,
//     Pick, Search and Not on Spotify. It replaces the page's body while `LinkView`'s `Reviewing` is set.
//
// Contract additions (declared, not numbered): `ViewKeys.LocalLinkOffer`, `ViewKeys.LocalLinkOptIn`, `ViewKeys.LocalLinkReview`, the three
// props records below, and `LocalCollectionPageProps.Link` (optional: a host that leaves it null shows none of it).

using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The offer on the page's Home (board 28 ①). Null props: no offer.</summary>
/// <param name="Text">"Wavee can add covers and correct names from Spotify to 42 albums".</param>
/// <param name="Sub">"You see every change before it applies".</param>
/// <param name="Before">A cover of the collection as the files show it (a file uri), or null (the calm tile).</param>
/// <param name="ShowMe">Opens the opt-in dialog over the page (the overlay the theme's host has).</param>
public sealed record LocalLinkOfferProps(string Text, string Sub, string? Before, string ShowMeLabel, string NotNowLabel,
    Action<FluentGpu.Controls.IOverlayService?> ShowMe, Action NotNow);

/// <summary>One line of the opt-in's explainer: a glyph, a lead and its sentence.</summary>
public sealed record LocalLinkOptInLine(string Glyph, string Title, string Body);

/// <summary>The opt-in dialog's body (board 28 ②).</summary>
/// <param name="SetAutoExact">The box's change ("Accept exact matches automatically"; unticked when the dialog opens).</param>
public sealed record LocalLinkOptInProps(string Body, IReadOnlyList<LocalLinkOptInLine> Lines, string AutoExactLabel, string AutoExactSub, Action<bool> SetAutoExact);

/// <summary>The review (board 29, 30): the live model, the page it belongs to, and the verbs. Words come from <c>Wavee.LocalLinkWords</c>.</summary>
public sealed record LocalLinkReviewProps
{
    /// <summary>The groups and the counts (a new value per change).</summary>
    public required IReadSignal<LinkView> View { get; init; }
    /// <summary>"" every group; else the one group the review was opened for ("Spotify match…" on a song).</summary>
    public required IReadSignal<string?> Focus { get; init; }
    /// <summary>"On this PC": the breadcrumb's first step, which closes the review.</summary>
    public required string PageTitle { get; init; }
    public required Action Close { get; init; }
    public required Action<string> Accept { get; init; }
    public required Action<string, int> Pick { get; init; }
    public required Action AcceptAllExact { get; init; }
    public required Action<string> NotThis { get; init; }
    public required Action<string> NotOnSpotify { get; init; }
    /// <summary>Undo a decision: the group is proposed again.</summary>
    public required Action<string> Undo { get; init; }
    public required Action<string, bool> SetSpotifyWins { get; init; }
    /// <summary>Search Spotify for a group with the user's own words: its candidates are replaced as the answer lands.</summary>
    public required Action<string, string> Search { get; init; }
    /// <summary>Show every group again after a focused open.</summary>
    public required Action ShowAll { get; init; }
    /// <summary>The page's route key (the review's scroll position is kept under it).</summary>
    public required string RouteKey { get; init; }
}

/// <summary>What the On this PC page hands a theme for "Your files and Spotify".</summary>
public sealed record LocalLinkPageProps
{
    /// <summary>The offer (tracked): null when it does not show (matching on, "Not now" pressed, nothing to match).</summary>
    public required Func<LocalLinkOfferProps?> Offer { get; init; }
    /// <summary>The review is open (tracked): the page draws <see cref="Review"/> in place of its body.</summary>
    public required IReadSignal<string?> Reviewing { get; init; }
    public required LocalLinkReviewProps Review { get; init; }
}

/// <summary>One field of the rename dialog: its label, what the song shows now (the box starts with it), and the file's own value (the box's placeholder, and
/// what "Use the file's names" puts back).</summary>
/// <param name="Set">The box's change.</param>
public sealed record LocalRenameField(string Label, string Initial, string Own, Action<string> Set);

/// <summary>The rename dialog's body ("Your files and Spotify" phase 2; contract addition, declared): its sentence, the title, artist and album
/// fields, and the "Use the file's names" link. The dialog's title and its two buttons are the dialog's own.</summary>
public sealed record LocalRenameProps(string Body, IReadOnlyList<LocalRenameField> Fields, string ResetLabel);
