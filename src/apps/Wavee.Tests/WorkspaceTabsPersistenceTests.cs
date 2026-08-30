using Xunit;

namespace Wavee.Tests;

public class WorkspaceTabsPersistenceTests
{
    [Fact]
    public void RoundTrip_PreservesPinnedOrderDuplicatesAndSelection()
    {
        PersistedWorkspaceTab[] tabs =
        [
            new("album", "first"),
            new("home", null),
            new("album", "first"),
        ];

        var decoded = WorkspaceTabsPersistence.Decode(WorkspaceTabsPersistence.Encode(tabs, 2));

        Assert.Equal(2, decoded.LastSelected);
        Assert.Equal(tabs, decoded.Tabs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"version\":99,\"lastSelected\":0,\"tabs\":[]}")]
    public void Decode_InvalidOrUnknownDocuments_ReturnsEmpty(string? raw)
    {
        var decoded = WorkspaceTabsPersistence.Decode(raw);

        Assert.Empty(decoded.Tabs);
        Assert.Equal(-1, decoded.LastSelected);
    }

    [Fact]
    public void Decode_SkipsInvalidTabsAndRemapsSelection()
    {
        const string raw = """
            {"version":1,"lastSelected":2,"tabs":[
              {"route":"","arg":null},
              {"route":"home","arg":null},
              {"route":"artist","arg":"chosen"}
            ]}
            """;

        var decoded = WorkspaceTabsPersistence.Decode(raw);

        Assert.Equal(2, decoded.Tabs.Length);
        Assert.Equal("home", decoded.Tabs[0].Route);
        Assert.Equal("artist", decoded.Tabs[1].Route);
        Assert.Equal(1, decoded.LastSelected);
    }

    [Fact]
    public void Encode_ClampsSelectionToPersistedRange()
    {
        PersistedWorkspaceTab[] tabs = [new("home", null)];

        var decoded = WorkspaceTabsPersistence.Decode(WorkspaceTabsPersistence.Encode(tabs, 900));

        Assert.Equal(0, decoded.LastSelected);
    }
}
