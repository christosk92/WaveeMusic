// ── Wavee.Tests/ProfilePageContractTests.cs — the profile page's published key and Classic's registration (themes plan 6.0, T10c2) ──
//
// The profile page is the structure's picture since T10c2; `Profile.PageHost` keeps the demands, the load faces, the section plan and each section's body verdict, the hero
// metrics, the accent, the scroll spy and the verbs. What is pure about the contract: the id (a published id never changes), Classic registering the key under its own
// template id with a complete registry, and the value types the host hands over. The plan, the stats and the hero tiers are core rules (`ProfilePageRulesTests`).

using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class ProfilePageContractTests
{
    [Fact]
    public void The_published_id_is_the_plans()
        => Assert.Equal("Pages.User", ViewKeys.ProfilePage.Id);

    [Fact]
    public void Classic_registers_the_profile_page_under_its_own_template_id()
    {
        var classic = ClassicTheme.Create();
        Assert.Equal("classic.profile.page", classic.Registry.TemplateIdOf(ViewKeys.ProfilePage.Id));
        Assert.NotEqual(classic.Registry.TemplateIdOf(ViewKeys.ArtistPage.Id), classic.Registry.TemplateIdOf(ViewKeys.ProfilePage.Id));
        Assert.Empty(classic.Validate());
    }

    [Fact]
    public void A_section_slot_compares_by_its_members()
    {
        Action go = static () => { };
        Action<FluentGpu.Scene.NodeHandle> anchor = static _ => { };
        ProfileCard[] cards = [];
        Assert.Equal(new ProfileSectionSlot(ProfileSection.Playlists, ProfileSectionBody.Empty, cards, null, go, go, anchor),
            new ProfileSectionSlot(ProfileSection.Playlists, ProfileSectionBody.Empty, cards, null, go, go, anchor));
        Assert.NotEqual(new ProfileSectionSlot(ProfileSection.Playlists, ProfileSectionBody.Empty, cards, null, go, go, anchor),
            new ProfileSectionSlot(ProfileSection.Following, ProfileSectionBody.Empty, cards, null, go, go, anchor));
    }

    [Fact]
    public void The_placeholder_hero_is_a_representative_shape()
    {
        var own = ProfileHeroText.PlaceholderFor(own: true);
        Assert.True(own.Placeholder);
        Assert.True(own.Own);
        Assert.False(ProfileHeroText.PlaceholderFor(own: false).Own);
        Assert.NotEmpty(own.Name);
    }

    [Fact]
    public void The_account_link_is_spotifys()
        => Assert.Equal("https://www.spotify.com/account", ProfileVerbs.AccountUrl);
}
