using System.Text.Json.Serialization;

namespace Wavee.LocPack;

public sealed class Packet
{
    [JsonPropertyName("culture")] public string Culture { get; set; } = "";
    [JsonPropertyName("packet")] public string PacketId { get; set; } = "";
    [JsonPropertyName("surface")] public string Surface { get; set; } = "";
    [JsonPropertyName("screenshot")] public string Screenshot { get; set; } = "";
    [JsonPropertyName("glossary")] public string[] Glossary { get; set; } = [];
    [JsonPropertyName("rows")] public List<PacketRow> Rows { get; set; } = [];
}

public sealed class PacketRow
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("english")] public string English { get; set; } = "";
    [JsonPropertyName("current")] public string Current { get; set; } = "";
    [JsonPropertyName("ai_draft")] public string AiDraft { get; set; } = "";
    [JsonPropertyName("yours")] public string Yours { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "todo";
    [JsonPropertyName("where")] public string Where { get; set; } = "";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("placeholders")] public string[] Placeholders { get; set; } = [];
    [JsonPropertyName("icu")] public string Icu { get; set; } = "none";
    [JsonPropertyName("example")] public string Example { get; set; } = "";
    [JsonPropertyName("keep_english")] public bool KeepEnglish { get; set; }
}
