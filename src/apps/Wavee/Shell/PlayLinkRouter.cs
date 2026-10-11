// ── Shell/PlayLinkRouter.cs — "play this link": the module router seam and the no-dialog path ──────────────────────
//
// Role: CORE (themes plan §5.1, PR C8; extraction X22). The play-a-link router used to be three static seams and one
// method of `Shell.Overlays.UI.cs`. They are here: the module host (`Modules.UI.cs`) installs `Match` and `Modules`, the
// dialog card (still a view) asks `Match`, and `wavee://play?link=` runs `Direct`.
//
// `RunAsync` is the whole `Direct` decision with every effect injected (the router, the UI poster, the toast door, the
// texts), so the tests drive the real flow: a miss is an Informational toast, a failure the shared-key Error toast with
// "Try again", a cancellation says nothing.

using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Localization;

namespace Wavee;

/// <summary>One installed module that declares the `match` capability, as its `Play ▸` row shows it (labels already
/// folded through <see cref="Actions.PlayLinkRules.MenuLabel"/> / <see cref="Actions.PlayLinkRules.PlaceholderFor"/>).</summary>
public readonly record struct LinkModule(string Id, string MenuLabel, string Placeholder);

/// <summary>A link some module claimed: its name, the resolved title (null when it matched on shape alone), live-ness,
/// and the one play verb (it lights the video surface first when the module says the playable is video).</summary>
public sealed record LinkMatch(string ModuleName, string? Title, bool IsLive, Action Play);

/// <summary>A toast the router asks for.</summary>
public readonly record struct PlayLinkToast(string Text, InfoBarSeverity Severity, string? ActionLabel = null,
    Action? OnAction = null, string? DedupeKey = null);

/// <summary>The router's sentences, already localized.</summary>
public readonly record struct PlayLinkTexts(string NoOwner, string Failed, string TryAgain);

/// <summary>The play-a-link router.</summary>
public static class PlayLinkRouter
{
    /// <summary>The router: (text, pinned module id or null, token) → the match, or null when nobody owns it. A module's own
    /// failure THROWS with its own words (owner T).</summary>
    public static Func<string, string?, CancellationToken, Task<LinkMatch?>>? Match;

    /// <summary>The match-capable installed modules, in install order (owner T).</summary>
    public static Func<IReadOnlyList<LinkModule>>? Modules;

    /// <summary>`wavee://play?link=` — the same router, play verb and failure sentences as the dialog card, with no status
    /// line: a miss is an Informational toast, a failure the shared-key Error toast with "Try again".</summary>
    public static void Direct(string link)
    {
        string input = Actions.PlayLinkRules.Normalize(link);
        if (!Actions.PlayLinkRules.CanSubmit(input)) return;
        var texts = new PlayLinkTexts(Loc.Get(Strings.Play.NoOwner), Loc.Get(Strings.Play.Failed), Loc.Get(Strings.Play.TryAgain));
        if (Match is not { } match)
        {
            Say(new PlayLinkToast(texts.NoOwner, InfoBarSeverity.Informational));
            return;
        }
        Action<Action> post = Actions.Services.Post ?? (static a => a());
        _ = Task.Run(() => RunAsync(input, match, post, Say, texts, () => Direct(link)));
    }

    static void Say(PlayLinkToast toast)
        => Notify.Say(toast.Text, toast.Severity, toast.ActionLabel, toast.OnAction, toast.DedupeKey);

    /// <summary>Ask <paramref name="match"/> and act on the answer on the UI poster. <paramref name="input"/> is the
    /// normalized text the router sees; <paramref name="retry"/> is what "Try again" runs.</summary>
    public static async Task RunAsync(string input, Func<string, string?, CancellationToken, Task<LinkMatch?>> match,
        Action<Action> post, Action<PlayLinkToast> say, PlayLinkTexts texts, Action retry)
    {
        try
        {
            var found = await match(input, null, CancellationToken.None).ConfigureAwait(false);
            post(() =>
            {
                if (found is null) { say(new PlayLinkToast(texts.NoOwner, InfoBarSeverity.Informational)); return; }
                found.Play();
            });
        }
        catch (Exception ex)
        {
            if (Actions.PlayLinkRules.IsCancelled(ex)) return;
            post(() => say(new PlayLinkToast(Actions.PlayLinkRules.ErrorText(ex, texts.Failed), InfoBarSeverity.Error,
                texts.TryAgain, retry, Actions.PlayLinkRules.FailureToastKey)));
        }
    }
}
