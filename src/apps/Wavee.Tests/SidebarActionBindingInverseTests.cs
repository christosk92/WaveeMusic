using Wavee;
using Xunit;

namespace Wavee.Tests;

// What the customizer's action picker commits (an ActionBinding) becomes the persisted wire record, and back — the
// two conversions are exact inverses, so a picked binding resolves to the descriptor the picker showed.
public class SidebarActionBindingInverseTests
{
    [Fact]
    public void APickedBindingRoundTrips()
    {
        var picked = new ActionBinding("wavee", "play", ActionTargetMode.FixedEntity, "spotify:album:1");
        var wire = SidebarActionBindings.FromActionBinding(in picked);
        Assert.Equal("wavee", wire.ProviderId);
        Assert.Equal("play", wire.ActionId);
        Assert.Equal(SidebarActionTargetMode.FixedEntity, wire.TargetMode);
        Assert.Equal("spotify:album:1", wire.TargetKey);
        Assert.Null(wire.Arguments);
        Assert.Equal(picked, wire.ToActionBinding());
    }

    [Fact]
    public void UnparseableArgumentsAreDroppedNotThrown()
    {
        var wire = SidebarActionBindings.FromActionBinding(new ActionBinding("wavee", "open", ActionTargetMode.NowPlaying, null, "{"));
        Assert.Null(wire.Arguments);
        var ok = SidebarActionBindings.FromActionBinding(new ActionBinding("wavee", "open", ActionTargetMode.None, null, "{\"a\":1}"));
        Assert.NotNull(ok.Arguments);
    }

    [Fact]
    public void EveryModeKeepsItsByte()
    {
        foreach (ActionTargetMode mode in Enum.GetValues<ActionTargetMode>())
        {
            var wire = SidebarActionBindings.FromActionBinding(new ActionBinding("p", "a", mode));
            Assert.Equal((byte)mode, (byte)wire.TargetMode);
        }
    }
}
