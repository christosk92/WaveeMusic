using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using Wavee.Core;

namespace Wavee;

// ── "Play ▸ Link…" — paste a link, play it ────────────────────────────────────────────────────────────────────────────
// The app's FIRST text-input dialog. It is a modal card in the ConfirmCard chrome (the logout confirm's shell: solid
// base, surface stroke, dialog elevation, 24 padding) over one TextBox and a status line, opened through the shell's
// already-mounted Overlay.Service with Modal chrome — no new host, no new dismissal rules.
//
// The card names no source. It hands the trimmed text to ModuleHost, which prefilters the installed modules by their
// manifest url-patterns and asks the survivors; a "YouTube…" row is the same card with that module PINNED, so a link
// nobody else claims still reaches the module the user named. Everything the card DECIDES (prefill, the status
// sentence, which failure is a toast and which is a status line) lives in PlayLinkActions, engine-free and pinned by
// PlayLinkActionsTests — this file is the surface only.
static class PlayLink
{
    /// <summary>Open the paste-a-link card. <paramref name="pinnedModuleId"/> non-null = the user picked a specific
    /// module's row, so only that module is asked (and its manifest placeholder names what it wants). The clipboard is
    /// read HERE, once, at open time: it is an open-time fact, and a component that re-read it each render would fight
    /// the user's own edits.</summary>
    public static void Open(IOverlayService? overlay, ActionServices? actions, string? pinnedModuleId, string placeholder)
    {
        if (overlay is null || actions is null) return;
        string seed = actions.Clipboard is { } clip && clip.TryGetText(out string clipText)
            ? PlayLinkActions.PrefillFrom(clipText)
            : "";

        OverlayHandle? h = null;
        h = overlay.Open(
            () => NodeHandle.Null,
            () => Embed.Comp(() => new PlayLinkDialog
            {
                Actions = actions,
                PinnedModuleId = pinnedModuleId,
                Placeholder = placeholder,
                Seed = seed,
                Close = () => h?.Close(),
            }),
            FlyoutPlacement.BottomCenter,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.Modal, Chrome: PopupChrome.Modal));
    }

    /// <summary>Play a link with no card: the <c>wavee://play?link=…</c> deep link. The same router, the same play
    /// verb and the same failure sentences as the card — only the status line has no home, so a miss is a toast.
    /// Marshals back through <see cref="ActionServices.Post"/> because the play verb touches signals.</summary>
    public static void PlayDirect(ActionServices? actions, string link)
    {
        if (actions is null) return;
        string input = PlayLinkActions.Normalize(link);
        if (!PlayLinkActions.CanSubmit(input)) return;
        if (actions.Svc?.Modules is not { } host)
        {
            Toast.Show(Loc.Get(Strings.Play.NoOwner), new ToastOptions { Severity = InfoBarSeverity.Informational });
            return;
        }
        Action<Action> post = actions.Post ?? (a => a());
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var match = await host.MatchAsync(input, null, System.Threading.CancellationToken.None).ConfigureAwait(false);
                post(() =>
                {
                    if (match is null)
                    {
                        Toast.Show(Loc.Get(Strings.Play.NoOwner), new ToastOptions { Severity = InfoBarSeverity.Informational });
                        return;
                    }
                    VideoActions.PlayAs(actions.Svc?.Player, actions.Playback, match.Track,
                        PlayLinkActions.FormFor(match.Resolved.Form));
                });
            }
            catch (Exception ex)
            {
                // Same card as the dialog's failure (the shared FailureToastKey), and the same recoverable shape: the
                // deep link is still in hand, so "Try again" is literally this call again rather than a dead sentence.
                post(() => Toast.Show(PlayLinkActions.ErrorText(ex, Loc.Get(Strings.Play.Failed)),
                    new ToastOptions
                    {
                        Severity = InfoBarSeverity.Error,
                        DedupeKey = PlayLinkActions.FailureToastKey,
                        ActionLabel = Loc.Get(Strings.Play.TryAgain),
                        OnAction = () => PlayDirect(actions, link),
                    }));
            }
        });
    }
}

/// <summary>The card body. Every prop is an OPEN-TIME constant (the reference-stable services bag, the pinned module
/// id, the placeholder, the clipboard seed, the close verb), so freezing them at mount — which is what
/// <c>Embed.Comp</c> does — is the correct contract and not the props-freeze trap: nothing about this card changes
/// while it is open except its own state, which is signals.</summary>
sealed class PlayLinkDialog : Component
{
    public required ActionServices Actions;
    public required string Placeholder;
    public required string Seed;
    public required Action Close;
    public string? PinnedModuleId;

    const float CardWidth = 420f;
    const float FieldWidth = CardWidth - 48f;   // the card's 24px padding on both sides
    const float StatusHeight = 18f;             // reserved, so the card does not jump when the line appears

    public override Element Render()
    {
        var text = UseSignal(Seed);
        var status = UseSignal("");
        // The input that FAILED, kept so the card can offer the browser escape hatch for it. It is a separate signal
        // from `text` on purpose: the user is free to edit the field after a failure, and the escape hatch must open
        // the link that actually failed — not whatever half-typed text is in the box when they reach for it.
        var failedInput = UseSignal("");
        // Cancel-on-unmount: closing the card withdraws the question. The command's own UI-thread post is the shell's
        // (UsePost is what ActionServices.Post is wired from), so the completion lands on the thread that owns the
        // signals below.
        var lookup = UseAsyncCommand(cancelOnUnmount: true);
        var post = UsePost();

        bool busy = lookup.IsRunning;              // subscribe → the button disables and the line says "Looking up…"
        string line = busy ? Loc.Get(Strings.Play.LookingUp) : status.Value;
        bool canPlay = !busy && PlayLinkActions.CanSubmit(text.Value);

        void Submit()
        {
            if (lookup.IsRunningNow) return;
            string input = PlayLinkActions.Normalize(text.Peek());
            if (input.Length == 0) return;

            if (Actions.Svc?.Modules is not { } host)
            {
                // No module host at all (a build with none, or before the composition root ran) — the honest answer is
                // the same one an unowned link gets, in place, rather than a toast about internals.
                status.Value = Loc.Get(Strings.Play.NoOwner);
                return;
            }

            status.Value = "";
            failedInput.Value = "";     // a new attempt retires the previous failure's escape hatch
            string? pinned = PinnedModuleId;
            lookup.Restart(
                async ct =>
                {
                    var match = await host.MatchAsync(input, pinned, ct).ConfigureAwait(false);
                    post(() => Matched(match));
                },
                Failed);
        }

        void Matched(Wavee.Backend.Modules.ModuleMatch? match)
        {
            if (match is null)
            {
                status.Value = Loc.Get(Strings.Play.NoOwner);
                return;
            }

            // The line the user actually sees settle: "YouTube · Claude FM · LIVE". Set BEFORE the play so the card's
            // last painted state is the fact it acted on.
            status.Value = PlayLinkActions.MatchStatus(
                match.Module.Manifest.DisplayName, match.Resolved.Title, match.Resolved.IsLive,
                Loc.Get(Strings.Play.Live));

            // One verb, one ordering: PlayAs lights the video surface for THIS uri before the play command when the
            // module says the playable is video, and leaves the standing intent alone when it says audio.
            VideoActions.PlayAs(Actions.Svc?.Player, Actions.Playback, match.Track,
                PlayLinkActions.FormFor(match.Resolved.Form));
            Close();
        }

        void Failed(Exception ex)
        {
            if (PlayLinkActions.IsCancelled(ex)) return;
            if (PlayLinkActions.IsNotOwned(ex)) { status.Value = Loc.Get(Strings.Play.NoOwner); return; }
            status.Value = "";
            failedInput.Value = PlayLinkActions.Normalize(text.Peek());
            // The module wrote the message ("YouTube is blocking this network", "subscriber-only"), so it is shown
            // verbatim; anything else falls back to the surface's own sentence rather than leaking an exception shape.
            //
            // A failure the user can do nothing about is half a message, so the card carries the retry: the dialog
            // still holds the input, so "Try again" is the SAME Submit, one re-entry guard and all. The shared
            // FailureToastKey is what stops this card and PlaybackBridge's generic one from stacking two cards for one
            // failed play — and what makes a second failed attempt refresh this card rather than pile onto it.
            Toast.Show(PlayLinkActions.ErrorText(ex, Loc.Get(Strings.Play.Failed)),
                new ToastOptions
                {
                    Severity = InfoBarSeverity.Error,
                    DedupeKey = PlayLinkActions.FailureToastKey,
                    ActionLabel = Loc.Get(Strings.Play.TryAgain),
                    OnAction = Submit,
                });
        }

        // The escape hatch: when the thing that failed was a web link, the user's next move is usually "just open it".
        // ShellOpen.IsWebUrl is the whitelist guard (http/https + a host, nothing else) — the input is untrusted text
        // the user pasted, so it never reaches the shell without passing it, and a refused string simply has no button.
        string escape = failedInput.Value;
        bool canOpenExternally = ShellOpen.IsWebUrl(escape);

        Element cancelButton = Button.Standard(Loc.Get(Strings.Auth.Cancel), Close) with { MinWidth = 96f };
        Element playButton = Button.Accent(Loc.Get(Strings.Play.Start), Submit, isEnabled: canPlay) with { MinWidth = 96f };
        Element[] buttons = canOpenExternally
            ? [Button.Standard(Loc.Get(Strings.Play.OpenInBrowser), () => ShellOpen.OpenUrl(escape)), cancelButton, playButton]
            : [cancelButton, playButton];

        return new BoxEl
        {
            Direction = 1, Width = CardWidth, MinWidth = 360f, MaxWidth = 480f,
            Corners = Radii.OverlayAll, Fill = Tok.FillSolidBase, BorderColor = Tok.StrokeSurfaceDefault, BorderWidth = 1f,
            Shadow = Elevation.Dialog, Padding = Edges4.All(24f), Gap = Spacing.M,
            Children =
            [
                new TextEl(Loc.Get(Strings.Play.Title))
                {
                    Size = 20f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap,
                },
                // Enter commits: EditableText raises OnCommit on Enter for a single-line field, so the card's primary
                // action needs no key handler of its own (and Escape stays the overlay's modal dismissal).
                TextBox.Create(text, null, new TextBox.TextBoxOptions
                {
                    Placeholder = Placeholder,
                    Width = FieldWidth,
                    OnCommit = _ => Submit(),
                }),
                new BoxEl
                {
                    MinHeight = StatusHeight, Direction = 0, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new TextEl(line)
                        {
                            Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                    ],
                },
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, Justify = FlexJustify.End, Margin = new Edges4(0, Spacing.S, 0, 0),
                    Children = buttons,
                },
            ],
        };
    }
}
