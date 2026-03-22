using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

/// <summary>
/// Variables for the Pathfinder "home" GraphQL query.
/// </summary>
public sealed class HomeVariables
{
    [JsonPropertyName("homeEndUserIntegration")]
    public string HomeEndUserIntegration { get; set; } = "INTEGRATION_WEB_PLAYER";

    [JsonPropertyName("timeZone")]
    public string TimeZone { get; set; } = TimeZoneInfo.Local.Id;

    [JsonPropertyName("sp_t")]
    public string SpT { get; set; } = "";

    [JsonPropertyName("facet")]
    public string Facet { get; set; } = "";

    [JsonPropertyName("sectionItemsLimit")]
    public int SectionItemsLimit { get; set; } = 10;
}
