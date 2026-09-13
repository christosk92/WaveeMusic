using Wavee.Backend.Spotify;
using Xunit;

namespace Wavee.Tests;

public class ProductInfoTests
{
    const string Premium =
        "<products><product><type>premium</type><catalogue>premium</catalogue><country>NL</country>" +
        "<head-files-url>https://audio-fa.scdn.co/audio/{0}</head-files-url></product></products>";

    [Fact]
    public void Parse_Premium_IsPremium_WithFields()
    {
        var p = ProductInfo.Parse(Premium);
        Assert.True(p.IsPremium);
        Assert.Equal("premium", p.Type);
        Assert.Equal("premium", p.Catalogue);
        Assert.Equal("NL", p.Country);
        Assert.Equal("https://audio-fa.scdn.co/audio/{0}", p.Attributes["head-files-url"]);
    }

    [Theory]
    [InlineData("free")]
    [InlineData("open")]
    public void Parse_NonPremium_IsNotPremium(string type)
    {
        var p = ProductInfo.Parse($"<products><product><type>{type}</type></product></products>");
        Assert.False(p.IsPremium);
        Assert.Equal(type, p.Type);
    }

    [Fact]
    public void Parse_PremiumVariant_IsPremium()
        => Assert.True(ProductInfo.Parse("<products><product><type>premium_student</type></product></products>").IsPremium);

    [Fact]
    public void Parse_Malformed_IsUnknown_NotPremium()
    {
        var p = ProductInfo.Parse("<not-valid xml");
        Assert.False(p.IsPremium);
        Assert.Equal("unknown", p.Type);
    }

    [Fact]
    public void Parse_MissingType_IsUnknown()
        => Assert.Equal("unknown", ProductInfo.Parse("<products><product><catalogue>premium</catalogue></product></products>").Type);
}
