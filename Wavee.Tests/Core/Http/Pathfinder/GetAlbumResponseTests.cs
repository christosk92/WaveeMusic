using System.Text.Json;
using FluentAssertions;
using Wavee.Core.Http.Pathfinder;
using Xunit;

namespace Wavee.Tests.Core.Http.Pathfinder;

public sealed class GetAlbumResponseTests
{
    [Fact]
    public void Deserialize_WithStringPreReleaseEndDateTime_ShouldPreserveValue()
    {
        var json = """
        {
            "data": {
                "albumUnion": {
                    "preReleaseEndDateTime": "2026-05-01T00:00:00Z"
                }
            }
        }
        """;

        var response = JsonSerializer.Deserialize(json, GetAlbumJsonContext.Default.GetAlbumResponse);

        response!.Data!.AlbumUnion!.PreReleaseEndDateTime.Should().Be("2026-05-01T00:00:00Z");
    }

    [Fact]
    public void Deserialize_WithObjectPreReleaseEndDateTime_ShouldReadIsoString()
    {
        var json = """
        {
            "data": {
                "albumUnion": {
                    "preReleaseEndDateTime": {
                        "isoString": "2026-05-01T00:00:00Z",
                        "precision": "DAY"
                    }
                }
            }
        }
        """;

        var response = JsonSerializer.Deserialize(json, GetAlbumJsonContext.Default.GetAlbumResponse);

        response!.Data!.AlbumUnion!.PreReleaseEndDateTime.Should().Be("2026-05-01T00:00:00Z");
    }
}
