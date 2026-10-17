// ── Wavee.Tests/ProfileListContractTests.cs — the profile list page's published key and Classic's registration (themes plan 6.0, T10c2) ───────
//
// The Following / Followers list page is the structure's picture since T10c2; the host keeps the demand, the pooled model, the load rule, the grid plan and the letter projection. What is
// pure about the contract: the id (a published id never changes), Classic registering the key, the props record comparing by reference (a host rebuilds one per render, and a data-equal
// record that carried newer closures would keep the old ones) and the shape's value equality (a re-derivation that changed nothing writes nothing).

using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class ProfileListContractTests
{
    [Fact]
    public void The_published_id_is_the_plans()
        => Assert.Equal("Pages.ProfileList", ViewKeys.ProfileListPage.Id);

    [Fact]
    public void Classic_registers_the_profile_list_key()
    {
        var classic = ClassicTheme.Create();
        Assert.Equal("classic.profile.list", classic.Registry.TemplateIdOf(ViewKeys.ProfileListPage.Id));
        Assert.Empty(classic.Validate());
    }

    [Fact]
    public void A_shape_compares_by_value()
    {
        var a = new ProfileListShape(ProfileListLoad.Ready, ProfileFacet.Followers, 3, 3, ProfileChip.All, default, default, 5, "k", 2u);
        Assert.Equal(a, a with { });
        Assert.NotEqual(a, a with { Epoch = 3u });
    }

    [Fact]
    public void The_pending_seed_has_no_columns()
    {
        Assert.Equal(ProfileListLoad.Pending, ProfileListShape.Pending.Load);
        Assert.Equal(0, ProfileListShape.Pending.Plan.Columns);
    }
}
