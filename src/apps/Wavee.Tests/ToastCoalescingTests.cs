using FluentGpu.Controls;
using Wavee;
using Xunit;

namespace Wavee.Tests;

// The toast de-duplication RULE (FluentGpu.Controls/ToastCoalescing.cs, source-included). The defect it exists to stop
// is a real one that shipped: one failed play raised TWO identical error cards — ToastController.Show had no notion of
// identity at all, and several independent lanes each answered for the same failure. So the rule is pinned as VALUES
// here rather than as a screenshot: the controller's own half (restart the countdown, adopt the newer action) is
// mechanical once the answer to "is this the same notification?" is fixed.
//
// Not tested here: ToastController itself. It is internal to FluentGpu.Controls and its Show/Close path needs a mounted
// host (a HostTimerQueue for the auto-dismiss countdown and an OverlayHost lane to render into) — this assembly
// deliberately does not reference FluentGpu.Controls, and faking a queue would pin the fake, not the control. The
// controller consults ToastCoalescing.IsDuplicate for the whole decision, which is what these tests own.
public class ToastCoalescingTests
{
    // ── the message IS the key when there is no key ──────────────────────────────────────────────────────────────

    /// <summary>The accidental double-Show — the same sentence twice — is one notification. This is the case that
    /// needs no caller to have thought about de-duplication at all, which is why the message is the fallback key.</summary>
    [Fact]
    public void SameMessage_NoKeys_IsDuplicate()
        => Assert.True(ToastCoalescing.IsDuplicate(null, "That link couldn't be played.",
                                                   null, "That link couldn't be played."));

    [Fact]
    public void DifferentMessages_NoKeys_AreNotDuplicates()
        => Assert.False(ToastCoalescing.IsDuplicate(null, "That link couldn't be played.",
                                                    null, "Couldn't reach the server."));

    /// <summary>Ordinal, case-sensitive: two spellings are two notifications. A case-insensitive compare would silently
    /// swallow a card the caller meant to show.</summary>
    [Fact]
    public void MessagesDifferingOnlyInCase_AreNotDuplicates()
        => Assert.False(ToastCoalescing.IsDuplicate(null, "Playback failed", null, "playback failed"));

    // ── an explicit key beats the message ────────────────────────────────────────────────────────────────────────

    /// <summary>The whole reason the key exists: two lanes answer for ONE failure in different words (the module's own
    /// sentence vs the player's generic one), and only a key they share can say they are the same card.</summary>
    [Fact]
    public void SameKey_DifferentMessages_IsDuplicate()
        => Assert.True(ToastCoalescing.IsDuplicate("wavee.play.failed", "YouTube is blocking this network.",
                                                   "wavee.play.failed", "Playback failed."));

    /// <summary>The mirror: identical text under DIFFERENT keys is two notifications. The key wins over the message in
    /// both directions, or "keyed" would only ever mean "extra coalescing".</summary>
    [Fact]
    public void DifferentKeys_SameMessage_AreNotDuplicates()
        => Assert.False(ToastCoalescing.IsDuplicate("wavee.play.failed", "Playback failed.",
                                                    "wavee.device.failed", "Playback failed."));

    /// <summary>A keyed card never swallows an unrelated unkeyed one: the unkeyed toast is identified by its own
    /// sentence, which is compared against the KEY, not against the keyed card's text.
    ///
    /// <para>The corollary is the reason keys are dotted machine tokens ("wavee.play.failed") and never prose: an
    /// unkeyed toast whose MESSAGE happened to be spelled exactly like a key would compare equal to it. No authored
    /// sentence looks like that, and the alternative — a second identity dimension — buys nothing.</para></summary>
    [Fact]
    public void KeyedCard_DoesNotSwallowAnUnkeyedOne()
    {
        Assert.False(ToastCoalescing.IsDuplicate("wavee.play.failed", "Playback failed.",
                                                 null, "Playback failed."));
        Assert.False(ToastCoalescing.IsDuplicate("wavee.play.failed", "Playback failed.",
                                                 null, "Added to Liked Songs"));
    }

    // ── null / empty / whitespace ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A whitespace-only key identifies nothing, so it falls back to the message rather than coalescing every
    /// toast that was handed a blank key.</summary>
    [Fact]
    public void WhitespaceKey_FallsBackToTheMessage()
    {
        Assert.True(ToastCoalescing.IsDuplicate("   ", "Playback failed.", null, "Playback failed."));
        Assert.False(ToastCoalescing.IsDuplicate("   ", "Playback failed.", "  ", "Something else."));
    }

    /// <summary>A key IS trimmed before comparison — the same constant reached through different call sites must not
    /// fail to match on stray padding.</summary>
    [Fact]
    public void KeysAreTrimmedBeforeComparing()
        => Assert.True(ToastCoalescing.IsDuplicate(" wavee.play.failed ", "a", "wavee.play.failed", "b"));

    /// <summary>No key and no message = no identity. Two such toasts are NOT merged: nothing says they are the same,
    /// and swallowing an unrelated card is worse than showing two.</summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NoIdentityAtAll_NeverCoalesces(string? key, string? message)
        => Assert.False(ToastCoalescing.IsDuplicate(key, message, key, message));

    [Fact]
    public void EmptyIncoming_NeverMatchesALiveCard()
        => Assert.False(ToastCoalescing.IsDuplicate(null, "Playback failed.", null, ""));

    // ── the effective key ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EffectiveKey_PrefersTheKeyThenTheMessage()
    {
        Assert.Equal("k", ToastCoalescing.EffectiveKey("k", "message"));
        Assert.Equal("message", ToastCoalescing.EffectiveKey(null, "message"));
        Assert.Equal("message", ToastCoalescing.EffectiveKey(" ", "message"));
        Assert.Equal("", ToastCoalescing.EffectiveKey(null, null));
    }

    // ── the app's own shared key ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The app-side half of the contract: the paste-a-link card, the deep-link path and PlaybackBridge all
    /// raise their failure under ONE key, so the two-cards-for-one-failure defect cannot come back by wording drift.
    /// The key is a constant and not derived from the input precisely because only one of those lanes knows the input.</summary>
    [Fact]
    public void PlayFailureLanes_ShareOneCard()
    {
        Assert.True(ToastCoalescing.IsDuplicate(
            PlayLinkActions.FailureToastKey, "YouTube is blocking this network. Try again later.",
            PlayLinkActions.FailureToastKey, "That link couldn't be played."));
        Assert.NotEqual("", PlayLinkActions.FailureToastKey);
    }
}
