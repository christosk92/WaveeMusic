using Wavee;
using Xunit;

namespace Wavee.Tests;

// ONE enum treatment in the property surface (ch 26 §0.13): decided from the RESOLVED labels, never from the field.
public class SidebarChoiceTreatmentTests
{
    [Fact]
    public void ShortChoicesAreSegmented()
    {
        Assert.True(SidebarChoiceTreatment.UsesSegmented(["Compact", "Cozy", "Comfortable"]));
        Assert.True(SidebarChoiceTreatment.UsesSegmented(["List", "Grid"]));
        Assert.True(SidebarChoiceTreatment.UsesSegmented(["123456789012"]));   // exactly the 12-char budget
    }

    [Fact]
    public void ALongLabelOrTooManyChoicesDemoteToADropdown()
    {
        Assert.False(SidebarChoiceTreatment.UsesSegmented(
            ["Recommended", "Hide empty content", "Show a compact hint", "Show an action card"]));
        Assert.False(SidebarChoiceTreatment.UsesSegmented(["1234567890123"]));
        Assert.False(SidebarChoiceTreatment.UsesSegmented(["a", "b", "c", "d", "e"]));
        Assert.False(SidebarChoiceTreatment.UsesSegmented([]));
    }
}
