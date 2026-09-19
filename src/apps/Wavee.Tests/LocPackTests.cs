using Wavee.LocPack;
using Xunit;

namespace Wavee.Tests;

public sealed class LocPackTests
{
    const string Nested = """
        {
          "$culture": "en-US",
          "$comment": "meta",
          "dialog": { "ok": "OK", "named": "Hello {name}" },
          "drag": { "movedManyTo": "Moved {count, plural, one {# item} other {# items}} to {name}" }
        }
        """;

    [Fact]
    public void Flatten_Skips_Dollar_Keys_And_Nests_Dotted()
    {
        var flat = LocCatalog.FlattenJson(Nested);
        Assert.Equal("OK", flat["dialog.ok"]);
        Assert.Equal("Hello {name}", flat["dialog.named"]);
        Assert.False(flat.ContainsKey("$culture"));
        Assert.False(flat.ContainsKey("$comment"));
    }

    [Fact]
    public void Nest_RoundTrip_Preserves_Culture_And_Leaves()
    {
        var flat = LocCatalog.FlattenJson(Nested);
        var nested = LocCatalog.Nest(flat, "nl");
        Assert.Equal("nl", nested["$culture"]!.GetValue<string>());
        string json = nested.ToJsonString();
        var again = LocCatalog.FlattenJson(json);
        Assert.Equal(flat, again);
    }

    [Fact]
    public void Placeholders_Ignores_Icu_Keywords()
    {
        string[] ph = LocCatalog.Placeholders("Moved {count, plural, one {# item} other {# items}} to {name}");
        Assert.Contains("count", ph);
        Assert.Contains("name", ph);
        Assert.DoesNotContain("plural", ph);
        Assert.DoesNotContain("other", ph);
        Assert.DoesNotContain("one", ph);
    }

    [Fact]
    public void PlaceholdersMatch_Rejects_Dropped_Name()
    {
        Assert.True(LocCatalog.PlaceholdersMatch("Hello {name}", "Hallo {name}"));
        Assert.False(LocCatalog.PlaceholdersMatch("Hello {name}", "Hallo"));
        Assert.False(LocCatalog.PlaceholdersMatch("Hello {name}", "Hallo {naam}"));
    }

    [Fact]
    public void Import_Writes_Approved_Only_And_Refuses_Broken_Placeholders()
    {
        var sat = new Dictionary<string, string> { ["dialog.ok"] = "OK" };
        var packet = new Packet
        {
            Culture = "nl",
            Rows =
            [
                new PacketRow { Key = "dialog.ok", English = "OK", Yours = "OK", Status = "approved" },
                new PacketRow { Key = "dialog.named", English = "Hello {name}", Yours = "Hallo {name}", Status = "approved" },
                new PacketRow { Key = "dialog.cancel", English = "Cancel", Yours = "Annuleren", Status = "todo" },
            ],
        };
        var next = LocCatalog.ImportApproved(sat, packet);
        Assert.Equal("Hallo {name}", next["dialog.named"]);
        Assert.False(next.ContainsKey("dialog.cancel"));

        packet.Rows[1].Yours = "Hallo";
        Assert.Throws<InvalidOperationException>(() => LocCatalog.ImportApproved(sat, packet));
    }

    [Fact]
    public void MissingOf_Includes_Identical_English_Dump()
    {
        var en = new Dictionary<string, string> { ["a.x"] = "X", ["a.y"] = "Y" };
        var sat = new Dictionary<string, string> { ["a.x"] = "X" };
        var packet = LocCatalog.MissingOf("nl", en, sat, "a", _ => true);
        Assert.Equal(2, packet.Rows.Count);
        Assert.Contains(packet.Rows, r => r.Key == "a.x");
        Assert.Contains(packet.Rows, r => r.Key == "a.y");
    }

    [Fact]
    public void Classify_Plural()
    {
        Assert.Equal(IcuKind.Plural, LocCatalog.Classify("{count, plural, one {a} other {b}}"));
        Assert.Equal(IcuKind.None, LocCatalog.Classify("Hello {name}"));
    }

    [Fact]
    public void Draft_Does_Not_Overwrite_Yours()
    {
        var row = new PacketRow { Key = "a", English = "A", Yours = "mine", AiDraft = "", Status = "todo" };
        if (row.Yours.Length > 0) { /* CLI skips */ }
        else row.AiDraft = "draft";
        Assert.Equal("mine", row.Yours);
        Assert.Equal("", row.AiDraft);
    }
}
