// ── Wavee.Tests/SidebarActionBindingsTests.cs — the document binding → the action platform's binding ─────────────────
//
// Stage B (J1): `sidebar-layout.json` keeps its own `SidebarActionBinding` record (byte-identical wire, its reducer and
// wire tests), and the pane resolves/executes every bound row through owner I's `ActionBinding`. This pins the ONE
// conversion: every target mode maps to the same persisted byte, the key halves and the target survive, and the opaque
// arguments travel as raw JSON text. Pure: no scope, no engine.

using System.Text.Json;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public sealed class SidebarActionBindingsTests
{
    [Theory]
    [InlineData(SidebarActionTargetMode.None, ActionTargetMode.None)]
    [InlineData(SidebarActionTargetMode.FixedEntity, ActionTargetMode.FixedEntity)]
    [InlineData(SidebarActionTargetMode.FixedTrack, ActionTargetMode.FixedTrack)]
    [InlineData(SidebarActionTargetMode.NowPlaying, ActionTargetMode.NowPlaying)]
    [InlineData(SidebarActionTargetMode.ActiveRoute, ActionTargetMode.ActiveRoute)]
    public void Every_target_mode_maps_to_the_same_persisted_value(SidebarActionTargetMode doc, ActionTargetMode expected)
    {
        var binding = new SidebarActionBinding("wavee", "play", doc, "spotify:album:1", null);
        Assert.Equal(expected, binding.ToActionBinding().TargetMode);
    }

    [Fact]
    public void Key_halves_and_target_survive_and_compose_to_the_same_registry_key()
    {
        var binding = SidebarActionBinding.Fixed("wavee", "playNext", "spotify:track:abc", track: true);
        var converted = binding.ToActionBinding();
        Assert.Equal("wavee", converted.ProviderId);
        Assert.Equal("playNext", converted.ActionId);
        Assert.Equal("spotify:track:abc", converted.TargetKey);
        Assert.Equal(binding.ActionKey, Actions.KeyOf(in converted));
    }

    [Fact]
    public void Arguments_travel_as_raw_json_and_absent_arguments_stay_null()
    {
        using var doc = JsonDocument.Parse("{\"count\":3}");
        var withArgs = new SidebarActionBinding("pub", "ext.go", SidebarActionTargetMode.None, null, doc.RootElement.Clone());
        Assert.Equal("{\"count\":3}", withArgs.ToActionBinding().Arguments);

        var none = SidebarActionBinding.Simple("wavee", "open");
        Assert.Null(none.ToActionBinding().Arguments);

        var undefined = new SidebarActionBinding("wavee", "open", SidebarActionTargetMode.None, null, default(JsonElement));
        Assert.Null(undefined.ToActionBinding().Arguments);
    }

    [Fact]
    public void A_missing_half_converts_to_an_unresolvable_key_rather_than_throwing()
    {
        var half = new SidebarActionBinding("", "wavee.play", SidebarActionTargetMode.None, null, null);
        var converted = half.ToActionBinding();
        Assert.Equal("wavee.play", Actions.KeyOf(in converted));
        var empty = new SidebarActionBinding("wavee", "", SidebarActionTargetMode.None, null, null);
        Assert.Equal("", Actions.KeyOf(empty.ToActionBinding()));
    }
}
