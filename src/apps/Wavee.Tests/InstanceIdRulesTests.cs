// ── Wavee.Tests/InstanceIdRulesTests.cs — the profile-scoped single instance (Platform/InstanceIdRules.cs) ──────────
//
// evidence-diagnostics §A.7: the default profile is "Wavee" and owns the OS registrations; a --profile run is its own
// instance, stable for one folder however it is spelled, distinct per folder, and never touches the OS registrations.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class InstanceIdRulesTests
{
    [Fact]
    public void The_default_profile_is_Wavee_and_owns_the_OS_integration()
    {
        Assert.Equal("Wavee", InstanceIdRules.For(""));
        Assert.Equal("Wavee", InstanceIdRules.For(null));
        Assert.Equal("Wavee", InstanceIdRules.For("   "));
        Assert.True(InstanceIdRules.OwnsOsIntegration(""));
    }

    [Fact]
    public void A_profile_folder_is_its_own_instance_however_it_is_spelled()
    {
        string a = InstanceIdRules.For(@"C:\wavee\verify-profile");
        Assert.StartsWith("Wavee.", a);
        Assert.Equal(14, a.Length);   // "Wavee." + 8 hex digits
        Assert.Equal(a, InstanceIdRules.For(@"c:\WAVEE\verify-profile\"));
        Assert.Equal(a, InstanceIdRules.For(@"C:/wavee/verify-profile"));
        Assert.NotEqual(a, InstanceIdRules.For(@"C:\wavee\other-profile"));
        Assert.NotEqual("Wavee", a);
    }

    [Fact]
    public void A_profile_run_never_owns_the_OS_integration()
        => Assert.False(InstanceIdRules.OwnsOsIntegration(@"C:\wavee\verify-profile"));
}
