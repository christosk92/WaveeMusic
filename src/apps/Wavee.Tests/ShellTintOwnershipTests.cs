using Wavee.SpotifyLive;
using Xunit;

namespace Wavee.Tests;

// The shell-material hand-over/single-owner rule (audit findings S1 #1 "the neutral flash" and S1 #2 "the
// two-publisher race", batch D). Engine-free by construction (object/bool/enum only) so it is driven directly
// against production code, with no window, page or signal graph.
public class ShellTintOwnershipTests
{
    static readonly object PageA = new();
    static readonly object PageB = new();

    // ── claiming (a page becoming current: its own first mount, or a KeepAlive reactivation) ──────────────────────

    [Fact]
    public void Claim_WithAKnownColour_WritesIt()
    {
        var outcome = ShellTintOwnership.Resolve(currentOwner: null,
            new ShellTintOwnership.Request(PageA, IsClaim: true, Definite: false, HasColor: true));
        Assert.Equal(ShellTintOwnership.Outcome.WriteKnownColor, outcome);
    }

    [Fact]
    public void Claim_WithNoColourKnownYet_HandsOverInsteadOfClearing()
    {
        // S1 #1: the new page does not yet know its own colour (still loading / not graded) — it must NOT blank the
        // slot to neutral. It takes ownership silently and the caller keeps painting whatever is already there.
        var outcome = ShellTintOwnership.Resolve(currentOwner: PageA,
            new ShellTintOwnership.Request(PageB, IsClaim: true, Definite: false, HasColor: false));
        Assert.Equal(ShellTintOwnership.Outcome.WriteHeldColor, outcome);
    }

    [Fact]
    public void Claim_OnABootstrapEmptySlot_AlsoHandsOver()
    {
        // No prior owner at all (app just started) behaves exactly like handing over from a real page — the caller
        // falls back to the neutral ground because there is nothing to hold, not because this rule special-cased it.
        var outcome = ShellTintOwnership.Resolve(currentOwner: null,
            new ShellTintOwnership.Request(PageA, IsClaim: true, Definite: false, HasColor: false));
        Assert.Equal(ShellTintOwnership.Outcome.WriteHeldColor, outcome);
    }

    [Fact]
    public void Claim_ThatIsDefinitelyColourless_WritesNeutral()
    {
        // Colour washes off / this layout does not apply a tint: a DECIDED answer, not "unknown" — goes straight to
        // neutral rather than holding the outgoing page's colour forever.
        var outcome = ShellTintOwnership.Resolve(currentOwner: PageA,
            new ShellTintOwnership.Request(PageB, IsClaim: true, Definite: true, HasColor: false));
        Assert.Equal(ShellTintOwnership.Outcome.WriteNeutral, outcome);
    }

    [Fact]
    public void Claim_DefiniteButStillCarryingAColour_PrefersTheColour()
    {
        // Defensive case (the production caller never asks Definite+HasColor together, since Disabled/!Apply force
        // the colour to null before calling in) — documents that a real colour always outranks "neutral by default".
        var outcome = ShellTintOwnership.Resolve(currentOwner: PageA,
            new ShellTintOwnership.Request(PageB, IsClaim: true, Definite: true, HasColor: true));
        Assert.Equal(ShellTintOwnership.Outcome.WriteKnownColor, outcome);
    }

    // ── ordinary refresh from the recorded owner (a re-render, not a claim) ─────────────────────────────────────

    [Fact]
    public void Refresh_FromTheCurrentOwner_WithANewColour_Writes()
    {
        var outcome = ShellTintOwnership.Resolve(currentOwner: PageA,
            new ShellTintOwnership.Request(PageA, IsClaim: false, Definite: false, HasColor: true));
        Assert.Equal(ShellTintOwnership.Outcome.WriteKnownColor, outcome);
    }

    [Fact]
    public void Refresh_FromTheCurrentOwner_BecomingDefinitelyColourless_WritesNeutral()
    {
        // e.g. DetailShell toggling Apply off mid-session (a layout-tier change) while it is still the front page.
        var outcome = ShellTintOwnership.Resolve(currentOwner: PageA,
            new ShellTintOwnership.Request(PageA, IsClaim: false, Definite: true, HasColor: false));
        Assert.Equal(ShellTintOwnership.Outcome.WriteNeutral, outcome);
    }

    [Fact]
    public void Refresh_FromTheCurrentOwner_WithNothingNewToSay_DoesNotWrite()
    {
        // Still the owner, still no colour known — nothing changed; writing again would be a redundant no-op write.
        var outcome = ShellTintOwnership.Resolve(currentOwner: PageA,
            new ShellTintOwnership.Request(PageA, IsClaim: false, Definite: false, HasColor: false));
        Assert.Equal(ShellTintOwnership.Outcome.NoWrite, outcome);
    }

    // ── the stray/superseded publisher (S1 #2) ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Refresh_FromAPageThatIsNoLongerTheOwner_NeverWrites(bool definite, bool hasColor)
    {
        // The exact S1 #2 shape: PageA is still mounted and drawing (a still-settling exit transition, or a signal
        // it watches bumping while backgrounded) and its ordinary effect fires again — but PageB has already claimed
        // the slot. Regardless of what PageA thinks its own colour is, the write must not land.
        var outcome = ShellTintOwnership.Resolve(currentOwner: PageB,
            new ShellTintOwnership.Request(PageA, IsClaim: false, Definite: definite, HasColor: hasColor));
        Assert.Equal(ShellTintOwnership.Outcome.NoWrite, outcome);
    }

    [Fact]
    public void OwnershipIsReferenceIdentity_NotValueEquality()
    {
        // Two distinct token instances must never be treated as the same owner even if a caller boxed an
        // "equal-looking" value — the whole contract rests on reference identity (ArtistPage/DetailShell each mint a
        // `new object()` per page instance).
        object first = new();
        object second = new();
        var outcome = ShellTintOwnership.Resolve(currentOwner: first,
            new ShellTintOwnership.Request(second, IsClaim: false, Definite: false, HasColor: true));
        Assert.Equal(ShellTintOwnership.Outcome.NoWrite, outcome);
    }

    [Fact]
    public void ANewOwnerReclaimingAfterASupersededStrayWrite_StillWorks()
    {
        // The sequence a KeepAlive exit-overlap race actually produces: B claims, A's stray refresh is dropped
        // (previous test), and B's OWN next ordinary refresh (still the owner) must still land normally.
        var strayFromA = ShellTintOwnership.Resolve(currentOwner: PageB,
            new ShellTintOwnership.Request(PageA, IsClaim: false, Definite: false, HasColor: true));
        Assert.Equal(ShellTintOwnership.Outcome.NoWrite, strayFromA);

        var refreshFromB = ShellTintOwnership.Resolve(currentOwner: PageB,
            new ShellTintOwnership.Request(PageB, IsClaim: false, Definite: false, HasColor: true));
        Assert.Equal(ShellTintOwnership.Outcome.WriteKnownColor, refreshFromB);
    }
}
