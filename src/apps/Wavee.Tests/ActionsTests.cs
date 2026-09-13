// ── Wavee.Tests/ActionsTests.cs — the descriptor, the targeting matrix, the registry, the thirteen descriptors ────
//
// Wave 4's gate for `Platform/Actions.cs` (A7). Ported from _old/Wavee.Tests's WaveeExtensionRegistryTests +
// ActionRulesTests (G6), against the 0.3 shapes: the binding vocabulary is `Actions`' now (owner J's document DECODES
// into it rather than declaring a second one), and the route↔pin-id mapping is `Shell`'s, because a pin id IS a route
// key.
//
// The four rules the platform exists to guarantee, and what each one prevents:
//
//   FIRST WINS. A second registration under a live key is refused and RECORDED. Because the built-in table registers
//   first, no third-party extension can shadow a first-party action — which is why the policy is first-wins and not
//   last-wins.
//
//   NOTHING IS EVER UNREGISTERED. A stored binding always resolves to SOMETHING renderable; an unresolvable key is
//   `ActionMissing`, i.e. a visible-but-disabled row with a reason, never a vanishing one. A vanishing row makes the
//   user's own sidebar look broken.
//
//   THE ACCEPTED SET IS CHECKED FIRST. A mode the descriptor does not accept is `ModeNotSupported` even when the app
//   state would have satisfied it — so NARROWING a descriptor's accepted set can never WIDEN what an old binding does.
//
//   A CONFIRMATION-REQUIRED ACTION REFUSES TO RUN WITH NOWHERE TO CONFIRM. A null overlay must never degrade into an
//   unconfirmed run, and the refusal is produced by `Resolve` so the row's disabled state and `Execute`'s refusal
//   cannot disagree.

using Xunit;

namespace Wavee.Tests;

public class ActionKeyTests
{
    [Theory]
    [InlineData("wavee.play", true)]
    [InlineData("wavee.playNext", true)]           // camelCase is allowed and used
    [InlineData("wavee.artist.topTracks", true)]
    [InlineData("publisher.ext-1.refresh_2", true)]
    [InlineData("wavee", false)]                   // a single segment would squat the publisher namespace
    [InlineData("", false)]
    [InlineData(".wavee.play", false)]
    [InlineData("wavee..play", false)]
    [InlineData("wavee.play.", false)]
    [InlineData("wavee play", false)]
    [InlineData("1wavee.play", false)]             // a segment starts with a letter
    public void Key_validity(string key, bool valid) => Assert.Equal(valid, Actions.Key.IsValid(key));

    [Fact]
    public void A_key_is_bounded_because_it_is_persisted_inside_a_document()
        => Assert.False(Actions.Key.IsValid("wavee." + new string('a', Actions.Key.MaxLength)));

    [Fact]
    public void Compose_tolerates_a_document_that_already_stored_the_qualified_form()
    {
        Assert.Equal("wavee.play", Actions.Key.Compose("wavee", "play"));
        Assert.Equal("wavee.play", Actions.Key.Compose("wavee", "wavee.play"));
        Assert.Equal("", Actions.Key.Compose("wavee", ""));   // unresolvable, so the registry reports MISSING
    }

    [Fact]
    public void First_party_is_literally_the_extension_named_wavee()
    {
        Assert.Equal("wavee", Actions.Key.FirstPartyPublisher);
        Assert.True(Actions.Key.IsFirstParty("wavee.play"));
        Assert.False(Actions.Key.IsFirstParty("acme.play"));
    }
}

public class ActionTargetingTests
{
    const string AlbumUri = "spotify:album:1TSZDcvlPtAnekTaItI3qO";

    static ActionHostState Playing(string uri, string context = "")
        => new(EntityUri.Parse(uri), context.Length == 0 ? default : EntityUri.Parse(context),
               new Shell.Route(Shell.RouteKind.NotFound));

    [Fact]
    public void A_mode_the_descriptor_does_not_accept_is_refused_even_when_the_state_would_satisfy_it()
    {
        var r = Actions.ResolveTarget(ActionTargetMode.NowPlaying, null, ActionTargetModes.FixedEntity,
            Playing("spotify:track:4uLU6hMCjMI75M1A2tKUQC"));
        Assert.Equal(ActionUnavailable.ModeNotSupported, r.Reason);
    }

    [Fact]
    public void A_fixed_binding_with_no_key_says_so()
    {
        Assert.Equal(ActionUnavailable.MissingTargetKey,
            Actions.ResolveTarget(ActionTargetMode.FixedEntity, null, ActionTargetModes.All, ActionHostState.Empty).Reason);
        Assert.Equal(ActionUnavailable.MissingTargetKey,
            Actions.ResolveTarget(ActionTargetMode.FixedTrack, "", ActionTargetModes.All, ActionHostState.Empty).Reason);
    }

    [Fact]
    public void A_fixed_entity_resolves_from_either_spelling_of_the_stored_key()
    {
        // A key captured from a MENU (an entity uri) and one captured from a SIDEBAR ROW (a pin/route id) must resolve
        // to the same target.
        var fromUri = Actions.ResolveTarget(ActionTargetMode.FixedEntity, AlbumUri, ActionTargetModes.All, ActionHostState.Empty);
        var fromRoute = Actions.ResolveTarget(ActionTargetMode.FixedEntity, "album:" + AlbumUri, ActionTargetModes.All, ActionHostState.Empty);
        Assert.True(fromUri.Available);
        Assert.True(fromRoute.Available);
        Assert.Equal(fromUri.Uri, fromRoute.Uri);
        Assert.Equal(Shell.NameOf(fromUri.Route), Shell.NameOf(fromRoute.Route));
    }

    [Fact]
    public void An_unrecognised_key_is_still_handed_through_rather_than_being_silently_unbindable()
    {
        var r = Actions.ResolveTarget(ActionTargetMode.FixedEntity, "acme:thing:1", ActionTargetModes.All, ActionHostState.Empty);
        Assert.True(r.Available);   // a future/third-party scheme is not a reason to refuse a binding
    }

    [Fact]
    public void A_track_has_no_route_because_a_track_is_never_pinnable()
    {
        var r = Actions.ResolveTarget(ActionTargetMode.FixedTrack, "spotify:track:4uLU6hMCjMI75M1A2tKUQC",
            ActionTargetModes.All, ActionHostState.Empty);
        Assert.True(r.Available);
        Assert.Equal(Shell.RouteKind.NotFound, r.Route.Kind);
    }

    [Fact]
    public void Now_playing_and_active_route_answer_the_reason_when_there_is_nothing_there()
    {
        Assert.Equal(ActionUnavailable.NoNowPlaying,
            Actions.ResolveTarget(ActionTargetMode.NowPlaying, null, ActionTargetModes.All, ActionHostState.Empty).Reason);
        Assert.Equal(ActionUnavailable.NoActiveRoute,
            Actions.ResolveTarget(ActionTargetMode.ActiveRoute, null, ActionTargetModes.All, ActionHostState.Empty).Reason);
    }

    [Fact]
    public void Now_playing_carries_its_surrounding_context()
    {
        var r = Actions.ResolveTarget(ActionTargetMode.NowPlaying, null, ActionTargetModes.All,
            Playing("spotify:track:4uLU6hMCjMI75M1A2tKUQC", AlbumUri));
        Assert.True(r.Available);
        Assert.Equal(Shell.RouteKind.Album, r.Route.Kind);
        Assert.Equal(EntityUri.Parse(AlbumUri), r.ContextUri);
    }

    [Fact]
    public void An_unknown_future_mode_is_refused_rather_than_read_as_none()
    {
        Assert.Equal(ActionTargetModes.Nothing, Actions.Bit((ActionTargetMode)99));
        Assert.False(Actions.Accepts(ActionTargetModes.All, (ActionTargetMode)99));
    }

    [Fact]
    public void Every_reason_has_a_caption_and_available_has_none()
    {
        Assert.Null(Actions.ReasonLocKey(ActionUnavailable.None));
        for (byte i = 1; i <= (byte)ActionUnavailable.NotApplicable; i++)
            Assert.False(string.IsNullOrEmpty(Actions.ReasonLocKey((ActionUnavailable)i)));
    }
}

public class ActionRegistryTests
{
    static void Noop(ActionServices s, ActionBinding b, ActionResolution t) { }

    static ActionDescriptor Stub(string key, Action<ActionServices, ActionBinding, ActionResolution>? run = null)
        => new()
        {
            Key = key,
            LabelLocKey = "menu.open",
            IconKey = ActionIcons.Open,
            AcceptedTargets = ActionTargetModes.All,
            Run = run ?? Noop,
        };

    [Fact]
    public void First_wins_and_the_refusal_is_recorded()
    {
        var registry = new Actions.Registry();
        registry.Register("acme", r =>
        {
            r.RegisterAction(Stub("acme.thing"));
            r.RegisterAction(Stub("acme.thing"));
        });
        Assert.Single(registry.Actions);
        Assert.Single(registry.Diagnostics);
        Assert.Equal(Actions.RegisterOutcome.RejectedDuplicate, registry.Diagnostics[0].Outcome);
    }

    [Fact]
    public void An_invalid_or_null_contribution_is_a_diagnostic_not_a_crash()
    {
        var registry = new Actions.Registry();
        registry.Register("acme", r =>
        {
            r.RegisterAction(Stub("notnamespaced"));
            r.RegisterDataSource("also-bad", new object());
        });
        Assert.Empty(registry.Actions);
        Assert.Equal(2, registry.Diagnostics.Count);
    }

    [Fact]
    public void A_healthy_registration_leaves_no_diagnostics()
    {
        var registry = new Actions.Registry();
        registry.Register("acme", r => r.RegisterAction(Stub("acme.thing")));
        Assert.Empty(registry.Diagnostics);
        Assert.Equal(["acme"], registry.Extensions);
    }

    [Fact]
    public void A_binding_whose_key_resolves_to_nothing_is_action_missing_and_never_a_vanishing_row()
    {
        var registry = new Actions.Registry();
        var services = new ActionServices();
        var binding = new ActionBinding("acme", "gone", ActionTargetMode.None);
        Assert.Equal(ActionUnavailable.ActionMissing, registry.Resolve(services, in binding).Reason);
        Assert.Equal(ActionUnavailable.ActionMissing, registry.Execute(services, in binding));
    }

    [Fact]
    public void A_data_source_is_resolved_by_id_and_typed_at_the_consumption_site()
    {
        var registry = new Actions.Registry();
        var source = new object();
        registry.Register("acme", r => r.RegisterDataSource("acme.rows", source));
        Assert.True(registry.TryGetSource<object>("acme.rows", out var found));
        Assert.Same(source, found);
        Assert.False(registry.TryGetSource<string>("acme.rows", out _));   // wrong type ⇒ not found, never a cast throw
    }
}

public class ActionDescriptorTests
{
    static ActionServices Bag() => new() { CanConfirm = static () => true };

    static ActionDescriptor Confirming(Action onRun) => new()
    {
        Key = "acme.delete",
        LabelLocKey = "menu.open",
        IconKey = ActionIcons.Delete,
        AcceptedTargets = ActionTargetModes.None,
        Destructive = true,
        RequiresConfirmation = true,
        Run = (_, _, _) => onRun(),
    };

    [Fact]
    public void A_confirmation_required_action_refuses_to_run_with_nowhere_to_confirm()
    {
        bool ran = false;
        var descriptor = Confirming(() => ran = true);
        var services = new ActionServices();   // no Confirm seam at all
        var binding = new ActionBinding("acme", "delete", ActionTargetMode.None);

        Assert.Equal(ActionUnavailable.HostUnavailable, Actions.Resolve(descriptor, services, in binding).Reason);
        Assert.Equal(ActionUnavailable.HostUnavailable, Actions.Execute(descriptor, services, in binding));
        Assert.False(ran);
    }

    [Fact]
    public void A_confirmation_runs_nothing_until_the_user_confirms()
    {
        bool ran = false;
        ConfirmRequest? asked = null;
        var descriptor = Confirming(() => ran = true);
        var services = Bag();
        services.Confirm = req => asked = req;
        var binding = new ActionBinding("acme", "delete", ActionTargetMode.None);

        Assert.Equal(ActionUnavailable.None, Actions.Execute(descriptor, services, in binding));
        Assert.False(ran);
        Assert.NotNull(asked);
        asked!.Value.OnConfirm();
        Assert.True(ran);
    }

    [Fact]
    public void The_confirmation_copy_falls_back_to_the_label()
    {
        ConfirmRequest? asked = null;
        var services = Bag();
        services.Confirm = req => asked = req;
        var binding = new ActionBinding("acme", "delete", ActionTargetMode.None);
        Actions.Execute(Confirming(static () => { }), services, in binding);
        Assert.Equal("menu.open", asked!.Value.TitleLocKey);
        Assert.Equal("menu.open", asked.Value.BodyLocKey);
        Assert.Equal("menu.open", asked.Value.PrimaryLocKey);
    }

    [Fact]
    public void The_descriptors_own_veto_reads_as_not_applicable()
    {
        var descriptor = new ActionDescriptor
        {
            Key = "acme.x", LabelLocKey = "menu.open", IconKey = ActionIcons.Open,
            AcceptedTargets = ActionTargetModes.None,
            IsEnabled = static (_, _, _) => false,
            Run = static (_, _, _) => { },
        };
        var binding = new ActionBinding("acme", "x", ActionTargetMode.None);
        Assert.Equal(ActionUnavailable.NotApplicable, Actions.Resolve(descriptor, new ActionServices(), in binding).Reason);
    }

    [Fact]
    public void An_adapter_receives_the_already_resolved_target_so_it_cannot_re_resolve()
    {
        ActionResolution seen = default;
        var descriptor = new ActionDescriptor
        {
            Key = "acme.x", LabelLocKey = "menu.open", IconKey = ActionIcons.Open,
            AcceptedTargets = ActionTargetModes.FixedEntity,
            Run = (_, _, t) => seen = t,
        };
        var binding = new ActionBinding("acme", "x", ActionTargetMode.FixedEntity, "spotify:album:1TSZDcvlPtAnekTaItI3qO");
        Assert.Equal(ActionUnavailable.None, Actions.Execute(descriptor, new ActionServices(), in binding));
        Assert.Equal(Shell.RouteKind.Album, seen.Route.Kind);
    }
}

public class BuiltInActionTableTests
{
    static Actions.Registry BuildWith(ActionServices services)
    {
        var registry = new Actions.Registry();
        registry.Register(Actions.Builtins.ExtensionId, r => Actions.Builtins.RegisterAll(r, services));
        return registry;
    }

    [Fact]
    public void The_thirteen_first_party_descriptors_all_register_cleanly()
    {
        var registry = BuildWith(new ActionServices());
        Assert.Equal(13, registry.Actions.Count);
        Assert.Equal(13, Actions.Builtins.AllKeys.Length);
        Assert.Empty(registry.Diagnostics);
        foreach (string key in Actions.Builtins.AllKeys) Assert.True(registry.HasAction(key));
    }

    [Fact]
    public void Every_first_party_key_is_namespaced_under_wavee_and_wraps_a_legacy_verb()
    {
        var registry = BuildWith(new ActionServices());
        foreach (var descriptor in registry.Actions)
        {
            Assert.True(Actions.Key.IsFirstParty(descriptor.Key));
            Assert.NotEqual(ActionId.None, descriptor.LegacyId);
            Assert.NotEmpty(descriptor.RequiredPermissions);
            Assert.False(string.IsNullOrEmpty(descriptor.LabelLocKey));
            Assert.False(string.IsNullOrEmpty(descriptor.IconKey));
        }
    }

    [Fact]
    public void Pin_and_unpin_are_an_absolute_state_pair_not_one_toggle()
    {
        var registry = BuildWith(new ActionServices());
        Assert.True(registry.TryGetAction(Actions.Builtins.KeyPin, out var pin));
        Assert.True(registry.TryGetAction(Actions.Builtins.KeyUnpin, out var unpin));
        Assert.NotEqual(pin.LabelLocKey, unpin.LabelLocKey);
        Assert.NotEqual(pin.IconKey, unpin.IconKey);
        Assert.NotEqual(pin.LegacyId, unpin.LegacyId);
    }

    [Fact]
    public void A_descriptor_with_no_seam_renders_disabled_rather_than_throwing()
    {
        var registry = BuildWith(new ActionServices());
        var binding = new ActionBinding("wavee", "play", ActionTargetMode.FixedEntity, "spotify:album:1TSZDcvlPtAnekTaItI3qO");
        Assert.Equal(ActionUnavailable.NotApplicable, registry.Resolve(new ActionServices(), in binding).Reason);
    }

    [Fact]
    public void Play_dispatches_through_the_one_seam()
    {
        EntityUri played = default;
        var services = new ActionServices { Play = uri => played = uri };
        var registry = BuildWith(services);
        var binding = new ActionBinding("wavee", "play", ActionTargetMode.FixedEntity, "spotify:album:1TSZDcvlPtAnekTaItI3qO");
        Assert.Equal(ActionUnavailable.None, registry.Execute(services, in binding));
        Assert.Equal(EntityUri.Parse("spotify:album:1TSZDcvlPtAnekTaItI3qO"), played);
    }

    [Fact]
    public void Toggle_like_reads_and_writes_the_same_seam_pair()
    {
        var saved = new HashSet<string>(StringComparer.Ordinal);
        var services = new ActionServices
        {
            IsSaved = uri => saved.Contains(uri.Text),
            SetSaved = (uri, on) => { if (on) saved.Add(uri.Text); else saved.Remove(uri.Text); },
        };
        var registry = BuildWith(services);
        var binding = new ActionBinding("wavee", "toggleLike", ActionTargetMode.FixedTrack, "spotify:track:4uLU6hMCjMI75M1A2tKUQC");
        Assert.True(registry.TryGetAction(in binding, out var descriptor));

        Assert.False(Actions.Checked(descriptor, services, in binding));
        registry.Execute(services, in binding);
        Assert.True(Actions.Checked(descriptor, services, in binding));
        registry.Execute(services, in binding);
        Assert.False(Actions.Checked(descriptor, services, in binding));
    }

    [Fact]
    public void The_playlist_management_verbs_are_deliberately_not_bindable()
    {
        // Owner-only management (delete especially) stays context-menu-only; a one-click sidebar shortcut is the wrong
        // affordance. The confirmation gate exists for the day one of them IS bound anyway.
        var registry = BuildWith(new ActionServices());
        foreach (string key in new[] { "wavee.delete", "wavee.rename", "wavee.addToPlaylist", "wavee.viewCredits" })
            Assert.False(registry.HasAction(key));
    }

    [Fact]
    public void Copy_link_produces_the_web_form()
    {
        Assert.Equal("https://open.spotify.com/album/1TSZDcvlPtAnekTaItI3qO",
            Actions.WebLinkOf(EntityUri.Parse("spotify:album:1TSZDcvlPtAnekTaItI3qO")));
        Assert.Equal("", Actions.WebLinkOf(default));
    }
}
