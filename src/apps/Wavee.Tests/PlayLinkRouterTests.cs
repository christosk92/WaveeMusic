// ── Wavee.Tests/PlayLinkRouterTests.cs — "play this link" with no dialog (Shell/PlayLinkRouter.cs, PR C8) ──────────
//
// `wavee://play?link=` used to be a method of `Shell.Overlays.UI.cs` that called the module router, the UI poster and the
// toast door itself. `PlayLinkRouter.RunAsync` is that flow with every effect injected, so these tests run the real decision:
//
//   A MISS IS A SENTENCE, A FAILURE IS A RETRY. Nobody owning the link is an Informational toast; a throwing module is the
//   shared-key Error toast with "Try again" (one failed play is ONE card); a cancellation says nothing at all.
//
//   NOTHING TOUCHES THE UI ON THE POOL THREAD. The play verb and every toast are handed to the poster, never run inline.

using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using Xunit;

namespace Wavee.Tests;

public class PlayLinkRouterTests
{
    static readonly PlayLinkTexts Texts = new("nobody owns it", "could not play", "try again");

    sealed class Harness
    {
        public readonly List<Action> Posted = new();
        public readonly List<PlayLinkToast> Toasts = new();
        public int Retries;

        public Task Run(Func<string, string?, CancellationToken, Task<LinkMatch?>> match, string input = "https://example.test/x")
            => PlayLinkRouter.RunAsync(input, match, Posted.Add, Toasts.Add, Texts, () => Retries++);

        public void Drain()
        {
            foreach (var action in Posted.ToArray()) action();
            Posted.Clear();
        }
    }

    static Func<string, string?, CancellationToken, Task<LinkMatch?>> Owns(Action play, string? expectInput = null)
        => (text, pinned, _) =>
        {
            if (expectInput is not null) Assert.Equal(expectInput, text);
            Assert.Null(pinned);   // the no-dialog path never pins a module
            return Task.FromResult<LinkMatch?>(new LinkMatch("Demo", "A title", false, play));
        };

    [Fact]
    public async Task A_match_plays_on_the_poster_and_says_nothing()
    {
        var h = new Harness();
        int played = 0;

        await h.Run(Owns(() => played++, expectInput: "https://example.test/x"));

        Assert.Equal(0, played);              // handed to the poster, not run on the router's thread
        h.Drain();
        Assert.Equal(1, played);
        Assert.Empty(h.Toasts);
    }

    [Fact]
    public async Task Nobody_owning_the_link_is_an_informational_toast()
    {
        var h = new Harness();

        await h.Run((_, _, _) => Task.FromResult<LinkMatch?>(null));

        Assert.Empty(h.Toasts);               // posted, not said inline
        h.Drain();
        var toast = Assert.Single(h.Toasts);
        Assert.Equal(Texts.NoOwner, toast.Text);
        Assert.Equal(InfoBarSeverity.Informational, toast.Severity);
        Assert.Null(toast.OnAction);
    }

    [Fact]
    public async Task A_failing_module_is_the_shared_key_error_toast_with_a_retry_that_runs_the_retry()
    {
        var h = new Harness();
        var failure = new InvalidOperationException("the module said no");

        await h.Run((_, _, _) => Task.FromException<LinkMatch?>(failure));
        h.Drain();

        var toast = Assert.Single(h.Toasts);
        Assert.Equal(InfoBarSeverity.Error, toast.Severity);
        Assert.Equal(Actions.PlayLinkRules.ErrorText(failure, Texts.Failed), toast.Text);
        Assert.Equal(Texts.TryAgain, toast.ActionLabel);
        Assert.Equal(Actions.PlayLinkRules.FailureToastKey, toast.DedupeKey);

        Assert.Equal(0, h.Retries);
        toast.OnAction!.Invoke();
        Assert.Equal(1, h.Retries);
    }

    [Fact]
    public async Task A_failure_with_no_words_of_its_own_says_the_generic_sentence()
    {
        var h = new Harness();
        var failure = new InvalidOperationException("");

        await h.Run((_, _, _) => Task.FromException<LinkMatch?>(failure));
        h.Drain();

        Assert.Equal(Actions.PlayLinkRules.ErrorText(failure, Texts.Failed), Assert.Single(h.Toasts).Text);
    }

    [Fact]
    public async Task A_cancellation_says_nothing_and_posts_nothing()
    {
        var h = new Harness();

        await h.Run((_, _, ct) => Task.FromCanceled<LinkMatch?>(new CancellationToken(true)));

        Assert.Empty(h.Posted);
        Assert.Empty(h.Toasts);
    }

    [Fact]
    public async Task The_router_is_asked_with_the_normalized_text_it_was_given()
    {
        var h = new Harness();
        string? asked = null;

        await h.Run((text, _, _) =>
        {
            asked = text;
            return Task.FromResult<LinkMatch?>(null);
        }, input: "spotify:track:abc");

        Assert.Equal("spotify:track:abc", asked);
    }
}
