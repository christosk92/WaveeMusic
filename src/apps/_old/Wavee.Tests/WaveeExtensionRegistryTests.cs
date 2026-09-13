using System;
using System.Collections.Generic;
using Wavee.Core;
using Wavee.Core.Sidebar;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// M1 (plan REVISION 2 item 2) — the extension platform's ACTION-REGISTRY contracts, driven against production code.
///
/// What is exercised here is the engine-free core of the registry: <c>WaveeRegistryTable&lt;T&gt;</c> (namespaced-key
/// validation, the first-wins duplicate policy, ordered enumeration, rejection diagnostics),
/// <c>WaveeExtensionKey</c> (the key vocabulary a stored <c>SidebarActionBinding</c> composes),
/// <c>WaveeActionTargets</c> (the whole target-mode matrix, including every visible-but-disabled reason), and
/// <c>PinRowRule</c> (which of the pin pair a menu shows, plus the kill-switch arm).
///
/// What is deliberately NOT here: <c>WaveeActionDescriptor</c> / <c>WaveeExtensionRegistry</c> / <c>PinActions</c> /
/// <c>Menus</c> themselves. Their delegates are typed over <c>ActionServices</c>, whose transitive graph is the whole app
/// (PlaybackBridge / LibraryBridge / Services / FluentGpu.Controls' overlay + toast), so this assembly cannot source-include
/// them — the same reason <c>AppAction.cs</c> has never been includable while <c>ActionRules.cs</c> always has been. That
/// is precisely why the policy, the matrix and the pin rule live in their own engine-free files: the shells over them are
/// thin, and the decisions are pinned here.
/// </summary>
public class WaveeExtensionRegistryTests
{
    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarActionBinding Binding(string provider = "wavee", string action = "play",
        SidebarActionTargetMode mode = SidebarActionTargetMode.FixedEntity, string? key = "spotify:album:a1")
        => new(provider, action, mode, key, null);

    static WaveeActionHostState Host(string? track = null, string? context = null, string? route = null,
        string? fixedName = null, string? trackName = null, string? routeName = null)
        => new(track, context, route, fixedName, trackName, routeName);

    static WaveeActionTargetResolution Resolve(SidebarActionBinding b,
        WaveeActionTargetModes accepted = WaveeActionTargetModes.All, WaveeActionHostState host = default)
        => WaveeActionTargets.Resolve(b, accepted, in host);

    // ── the key vocabulary ───────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("wavee.play")]
    [InlineData("wavee.playNext")]
    [InlineData("wavee.artist.topTracks")]
    [InlineData("publisher.extension.refresh")]
    [InlineData("my-pub.my_action")]
    public void ValidKeysAreAccepted(string key) => Assert.True(WaveeExtensionKey.IsValid(key));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("play")]                 // single segment — would squat the publisher namespace
    [InlineData(".play")]
    [InlineData("wavee.")]
    [InlineData("wavee..play")]
    [InlineData("wavee.1play")]          // a segment must start with a letter
    [InlineData("wavee play")]
    [InlineData("wavee.play ")]
    [InlineData("wavee.pl$y")]
    public void InvalidKeysAreRejected(string? key) => Assert.False(WaveeExtensionKey.IsValid(key));

    [Fact]
    public void OverlongKeyIsRejected()
        => Assert.False(WaveeExtensionKey.IsValid("wavee." + new string('a', WaveeExtensionKey.MaxLength)));

    [Fact]
    public void ComposeJoinsProviderAndAction()
        => Assert.Equal("wavee.play", WaveeExtensionKey.Compose("wavee", "play"));

    [Fact]
    public void ComposeDoesNotDoublePrefixAnAlreadyQualifiedActionId()
        => Assert.Equal("wavee.play", WaveeExtensionKey.Compose("wavee", "wavee.play"));

    [Fact]
    public void ComposeKeepsANestedContributionId()
        => Assert.Equal("wavee.artist.topTracks", WaveeExtensionKey.Compose("wavee", "artist.topTracks"));

    [Fact]
    public void ComposeWithoutAnActionIdIsUnresolvable()
        => Assert.Equal("", WaveeExtensionKey.Compose("wavee", null));

    [Fact]
    public void PublisherAndFirstPartyDetection()
    {
        Assert.Equal("wavee", WaveeExtensionKey.PublisherOf("wavee.play"));
        Assert.Equal("acme", WaveeExtensionKey.PublisherOf("acme.ext.do"));
        Assert.Equal("", WaveeExtensionKey.PublisherOf("play"));
        Assert.True(WaveeExtensionKey.IsFirstParty("wavee.play"));
        Assert.False(WaveeExtensionKey.IsFirstParty("acme.play"));
        Assert.False(WaveeExtensionKey.IsFirstParty(null));
    }

    // ── registration / duplicate policy / lookup ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void RegistrationIsLookedUpByNamespacedKey()
    {
        var table = new WaveeRegistryTable<string>();
        Assert.Equal(WaveeRegisterOutcome.Registered, table.Add("wavee.play", "first-party play"));

        Assert.True(table.TryGet("wavee.play", out string found));
        Assert.Equal("first-party play", found);
        Assert.True(table.Contains("wavee.play"));
        Assert.False(table.TryGet("wavee.pause", out _));
        Assert.False(table.Contains(null));
    }

    [Fact]
    public void DuplicateKeyIsRejectedAndTheFirstRegistrationWins()
    {
        var table = new WaveeRegistryTable<string>();
        table.Add("wavee.play", "first-party");
        var outcome = table.Add("wavee.play", "third-party shadow");

        Assert.Equal(WaveeRegisterOutcome.RejectedDuplicate, outcome);
        Assert.Equal(1, table.Count);
        Assert.True(table.TryGet("wavee.play", out string found));
        Assert.Equal("first-party", found);          // the shadow never lands — that is the security property

        var diag = Assert.Single(table.Diagnostics);
        Assert.Equal("wavee.play", diag.Key);
        Assert.Equal(WaveeRegisterOutcome.RejectedDuplicate, diag.Outcome);
        Assert.NotEqual("", diag.Detail);
    }

    [Fact]
    public void InvalidKeyAndNullContributionAreRejectedWithDiagnostics()
    {
        var table = new WaveeRegistryTable<string>();
        Assert.Equal(WaveeRegisterOutcome.RejectedInvalidKey, table.Add("play", "x"));
        Assert.Equal(WaveeRegisterOutcome.RejectedNull, table.Add("wavee.play", null));
        Assert.Equal(WaveeRegisterOutcome.RejectedNull, table.Add("", "x"));

        Assert.Equal(0, table.Count);
        Assert.Equal(3, table.Diagnostics.Count);
    }

    [Fact]
    public void AHealthyRegistrationRecordsNoDiagnostic()
    {
        var table = new WaveeRegistryTable<string>();
        table.Add("wavee.play", "a");
        table.Add("wavee.open", "b");
        Assert.Empty(table.Diagnostics);
    }

    [Fact]
    public void EnumerationPreservesRegistrationOrderForThePicker()
    {
        var table = new WaveeRegistryTable<string>();
        table.Add("wavee.play", "a");
        table.Add("acme.ext.do", "b");
        table.Add("wavee.open", "c");
        table.Add("wavee.play", "dup");          // rejected — must not disturb the order

        Assert.Equal(new List<string> { "a", "b", "c" }, new List<string>(table.Items));
        Assert.Equal(new List<string> { "wavee.play", "acme.ext.do", "wavee.open" }, new List<string>(table.Keys));
        Assert.Equal("acme.ext.do", table.KeyAt(1));
        Assert.Equal("b", table.ItemAt(1));
        Assert.Equal(2, table.IndexOf("wavee.open"));
        Assert.Equal(-1, table.IndexOf("wavee.missing"));
    }

    // ── binding round-trip (the stored document → a registry key → a descriptor) ──────────────────────────────────────

    [Fact]
    public void AStoredBindingRoundTripsOntoItsRegistrationKey()
    {
        var table = new WaveeRegistryTable<string>();
        table.Add("wavee.play", "play");

        var binding = Binding(provider: "wavee", action: "play");
        Assert.True(table.TryGet(WaveeExtensionKey.Compose(binding.ProviderId, binding.ActionId), out string found));
        Assert.Equal("play", found);
    }

    [Fact]
    public void ABindingCarryingTheFullyQualifiedFormStillResolves()
    {
        var table = new WaveeRegistryTable<string>();
        table.Add("wavee.play", "play");

        var binding = Binding(provider: "wavee", action: "wavee.play");
        Assert.True(table.TryGet(WaveeExtensionKey.Compose(binding.ProviderId, binding.ActionId), out _));
    }

    [Fact]
    public void ABindingForAnUnregisteredActionResolvesToNothing()
    {
        var table = new WaveeRegistryTable<string>();
        var binding = Binding(provider: "ghost", action: "vanished");
        Assert.False(table.TryGet(WaveeExtensionKey.Compose(binding.ProviderId, binding.ActionId), out _));
    }

    [Fact]
    public void BindingFieldsSurviveConstruction()
    {
        var binding = new SidebarActionBinding("acme", "refresh", SidebarActionTargetMode.NowPlaying, null, null);
        Assert.Equal("acme", binding.ProviderId);
        Assert.Equal("refresh", binding.ActionId);
        Assert.Equal(SidebarActionTargetMode.NowPlaying, binding.TargetMode);
        Assert.Null(binding.TargetKey);
        Assert.Null(binding.Arguments);
    }

    // ── target-mode resolution ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NoneModeNeedsNoTarget()
    {
        var r = Resolve(Binding(mode: SidebarActionTargetMode.None, key: null));
        Assert.True(r.Available);
        Assert.Equal("", r.Uri);
        Assert.Null(r.RouteKey);
        Assert.Null(r.ReasonLocKey);
    }

    [Fact]
    public void FixedEntityResolvesAnEntityUriToItsUriAndRoute()
    {
        var r = Resolve(Binding(mode: SidebarActionTargetMode.FixedEntity, key: "spotify:album:a1"),
            host: Host(fixedName: "Album one"));
        Assert.True(r.Available);
        Assert.Equal("spotify:album:a1", r.Uri);
        Assert.Equal("album:spotify:album:a1", r.RouteKey);
        Assert.Equal("Album one", r.Name);
    }

    [Fact]
    public void FixedEntityAlsoAcceptsThePinIdFormAndResolvesToTheSameTarget()
    {
        var fromUri = Resolve(Binding(mode: SidebarActionTargetMode.FixedEntity, key: "spotify:album:a1"));
        var fromPinId = Resolve(Binding(mode: SidebarActionTargetMode.FixedEntity, key: "album:spotify:album:a1"));

        Assert.True(fromPinId.Available);
        Assert.Equal(fromUri.Uri, fromPinId.Uri);
        Assert.Equal(fromUri.RouteKey, fromPinId.RouteKey);
    }

    [Fact]
    public void FixedEntityAcceptsAnAllowListedAppRouteKey()
    {
        var r = Resolve(Binding(mode: SidebarActionTargetMode.FixedEntity, key: "liked"));
        Assert.True(r.Available);
        Assert.Equal("liked", r.RouteKey);
        Assert.Equal(SidebarPinId.LikedSongsUri, r.Uri);
    }

    [Fact]
    public void AnUnknownEntitySchemePassesThroughAsABareUri()
    {
        var r = Resolve(Binding(mode: SidebarActionTargetMode.FixedEntity, key: "acme:widget:7"));
        Assert.True(r.Available);
        Assert.Equal("acme:widget:7", r.Uri);
        Assert.Null(r.RouteKey);
    }

    [Theory]
    [InlineData(SidebarActionTargetMode.FixedEntity)]
    [InlineData(SidebarActionTargetMode.FixedTrack)]
    public void AFixedModeWithNoTargetKeyIsDisabledWithAReason(SidebarActionTargetMode mode)
    {
        var r = Resolve(Binding(mode: mode, key: null));
        Assert.False(r.Available);
        Assert.Equal(WaveeActionUnavailable.MissingTargetKey, r.Reason);
        Assert.Equal(WaveeActionTargets.LocKeyMissingTargetKey, r.ReasonLocKey);
    }

    [Fact]
    public void FixedTrackCarriesTheTrackUriAndNoRoute()
    {
        var r = Resolve(Binding(mode: SidebarActionTargetMode.FixedTrack, key: "spotify:track:t1"),
            host: Host(fixedName: "Track one"));
        Assert.True(r.Available);
        Assert.Equal("spotify:track:t1", r.Uri);
        Assert.Null(r.RouteKey);        // a track is never pinnable and has no detail route of its own
        Assert.Equal("Track one", r.Name);
    }

    [Fact]
    public void NowPlayingResolvesTheLiveTrackAndItsContextRoute()
    {
        var r = Resolve(Binding(mode: SidebarActionTargetMode.NowPlaying, key: null),
            host: Host(track: "spotify:track:t1", context: "spotify:playlist:p1", trackName: "Track one"));

        Assert.True(r.Available);
        Assert.Equal("spotify:track:t1", r.Uri);
        Assert.Equal("spotify:playlist:p1", r.ContextUri);
        Assert.Equal("pl:spotify:playlist:p1", r.RouteKey);
        Assert.Equal("Track one", r.Name);
    }

    [Fact]
    public void NowPlayingWithNothingPlayingIsDisabledWithAReason()
    {
        var r = Resolve(Binding(mode: SidebarActionTargetMode.NowPlaying, key: null));
        Assert.False(r.Available);
        Assert.Equal(WaveeActionUnavailable.NoNowPlaying, r.Reason);
        Assert.Equal(WaveeActionTargets.LocKeyNoNowPlaying, r.ReasonLocKey);
    }

    [Fact]
    public void ActiveRouteResolvesTheCurrentPageAndTheEntityBehindIt()
    {
        var r = Resolve(Binding(mode: SidebarActionTargetMode.ActiveRoute, key: null),
            host: Host(route: "album:spotify:album:a1", routeName: "Album one"));

        Assert.True(r.Available);
        Assert.Equal("album:spotify:album:a1", r.RouteKey);
        Assert.Equal("spotify:album:a1", r.Uri);
        Assert.Equal("Album one", r.Name);
    }

    [Fact]
    public void ActiveRouteWithNoRouteProviderIsDisabledWithAReason()
    {
        // The host supplied no route resolver (ActionServices.CurrentRoute null) — resolve unavailable, never guess.
        var r = Resolve(Binding(mode: SidebarActionTargetMode.ActiveRoute, key: null));
        Assert.False(r.Available);
        Assert.Equal(WaveeActionUnavailable.NoActiveRoute, r.Reason);
        Assert.Equal(WaveeActionTargets.LocKeyNoActiveRoute, r.ReasonLocKey);
    }

    [Fact]
    public void AModeTheDescriptorDoesNotAcceptIsRefusedEvenWhenTheStateWouldSatisfyIt()
    {
        var r = Resolve(Binding(mode: SidebarActionTargetMode.NowPlaying, key: null),
            accepted: WaveeActionTargetModes.FixedEntity,
            host: Host(track: "spotify:track:t1"));

        Assert.False(r.Available);
        Assert.Equal(WaveeActionUnavailable.ModeNotSupported, r.Reason);
        Assert.Equal(WaveeActionTargets.LocKeyModeNotSupported, r.ReasonLocKey);
    }

    [Fact]
    public void AFutureModeValueIsRefusedRatherThanTreatedAsNone()
    {
        var r = Resolve(Binding(mode: (SidebarActionTargetMode)99, key: null));
        Assert.False(r.Available);
        Assert.Equal(WaveeActionUnavailable.ModeNotSupported, r.Reason);
        Assert.Equal(WaveeActionTargetModes.Nothing, WaveeActionTargets.Bit((SidebarActionTargetMode)99));
    }

    [Fact]
    public void AcceptsMatchesExactlyTheDeclaredModes()
    {
        var accepted = WaveeActionTargetModes.FixedEntity | WaveeActionTargetModes.ActiveRoute;
        Assert.True(WaveeActionTargets.Accepts(accepted, SidebarActionTargetMode.FixedEntity));
        Assert.True(WaveeActionTargets.Accepts(accepted, SidebarActionTargetMode.ActiveRoute));
        Assert.False(WaveeActionTargets.Accepts(accepted, SidebarActionTargetMode.None));
        Assert.False(WaveeActionTargets.Accepts(accepted, SidebarActionTargetMode.FixedTrack));
        Assert.False(WaveeActionTargets.Accepts(accepted, SidebarActionTargetMode.NowPlaying));
        Assert.False(WaveeActionTargets.Accepts(WaveeActionTargetModes.Nothing, SidebarActionTargetMode.None));
    }

    [Fact]
    public void EveryUnavailableReasonCarriesAnExplanationAndAvailableCarriesNone()
    {
        Assert.Null(WaveeActionTargets.LocKeyOf(WaveeActionUnavailable.None));
        foreach (WaveeActionUnavailable reason in Enum.GetValues<WaveeActionUnavailable>())
        {
            if (reason == WaveeActionUnavailable.None) continue;
            Assert.False(string.IsNullOrEmpty(WaveeActionTargets.LocKeyOf(reason)));
        }
    }

    [Fact]
    public void OutOfMatrixReasonsAreShapedAsAResolutionToo()
    {
        var r = WaveeActionTargets.Unavailable(SidebarActionTargetMode.FixedEntity,
            WaveeActionUnavailable.ActionMissing);
        Assert.False(r.Available);
        Assert.Equal(WaveeActionUnavailable.ActionMissing, r.Reason);
        Assert.Equal(WaveeActionTargets.LocKeyActionMissing, r.ReasonLocKey);
        Assert.Equal("", r.Uri);
    }

    // ── the pin row rule (F.5.2/F.5.3) ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void APinnableUnpinnedTargetShowsPin()
        => Assert.Equal(PinRowKind.Pin, PinRowRule.Decide(hasStore: true, "album:spotify:album:a1", isPinned: false));

    [Fact]
    public void APinnedTargetShowsUnpin()
        => Assert.Equal(PinRowKind.Unpin, PinRowRule.Decide(hasStore: true, "album:spotify:album:a1", isPinned: true));

    [Fact]
    public void NoPinStoreMeansNoRowAtAll()
    {
        // The kill switch: ActionServices.Sidebar null ⇒ the row is OMITTED, not rendered disabled.
        Assert.Equal(PinRowKind.None, PinRowRule.Decide(hasStore: false, "album:spotify:album:a1", isPinned: false));
        Assert.Equal(PinRowKind.None, PinRowRule.Decide(hasStore: false, "album:spotify:album:a1", isPinned: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnUnpinnableTargetShowsNoRow(string? pinId)
        => Assert.Equal(PinRowKind.None, PinRowRule.Decide(hasStore: true, pinId, isPinned: false));

    [Theory]
    [InlineData("spotify:track:t1")]
    [InlineData("spotify:episode:e1")]
    [InlineData("")]
    public void TracksAndEpisodesAreNeverPinnableSoTheyNeverGetARow(string uri)
    {
        string? pinId = SidebarPinId.FromUri(uri);
        Assert.Null(pinId);
        Assert.Equal(PinRowKind.None, PinRowRule.Decide(hasStore: true, pinId, isPinned: false));
    }

    [Fact]
    public void ATrackActionTargetYieldsNoPinRow()
    {
        var target = ActionTarget.ForTracks(new[]
        {
            new Track("t1", "spotify:track:t1", "Song", Array.Empty<ArtistRef>(),
                new AlbumRef("a1", "spotify:album:a1", "Album"), 1000L, false, null),
        });
        Assert.Null(SidebarPinId.FromTarget(in target));
        Assert.Equal(PinRowKind.None, PinRowRule.Decide(hasStore: true, SidebarPinId.FromTarget(in target), false));
    }

    [Fact]
    public void APinnedThenUnpinnedTargetFlipsTheRowBackThroughTheRealStore()
    {
        var store = new SidebarPinStore();
        const string uri = "spotify:playlist:p1";
        string id = SidebarPinId.FromUri(uri)!;

        Assert.Equal(PinRowKind.Pin, PinRowRule.Decide(true, id, store.IsPinned(id)));
        Assert.True(store.Pin(new SidebarPin(id, SidebarPinId.KindOf(id), uri, "P", 0L)));
        Assert.Equal(PinRowKind.Unpin, PinRowRule.Decide(true, id, store.IsPinned(id)));

        int removedAt = store.Unpin(id);
        Assert.Equal(0, removedAt);
        Assert.Equal(PinRowKind.Pin, PinRowRule.Decide(true, id, store.IsPinned(id)));
    }
}
