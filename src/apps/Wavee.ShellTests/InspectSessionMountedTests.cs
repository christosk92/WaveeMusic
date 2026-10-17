// ── Wavee.ShellTests/InspectSessionMountedTests.cs — the `--inspect` verbs on the real shell (agent-inspect plan, lane A2) ─────────
//
// The server's UI-thread half (`Diagnostics.InspectSession`) driven on this process's booted host, through the same JSON lines an agent
// sends: mount Zune Home at 400 x 860, read the tiers, find the band's pivots and dump one of them down to its text, with the sizes the
// typography lint (lane A3) reads. The mount asks `gpu=none`: DirectWrite text over the recording device, so the fact needs no D3D12.

using System.Text.Json;
using Wavee;
using Wavee.Themes.Zune;
using Xunit;

namespace Wavee.ShellTests;

public sealed class InspectSessionMountedTests(ShellHost host)
{
    static JsonElement Reply(Diagnostics.InspectSession session, string line)
    {
        string reply = session.Execute(line, out _);
        Assert.False(string.IsNullOrEmpty(reply), "no reply to " + line);
        using var doc = JsonDocument.Parse(reply);
        JsonElement root = doc.RootElement.Clone();
        Assert.True(root.GetProperty("ok").GetBoolean(), line + " -> " + reply);
        return root;
    }

    [Fact]
    public void Zune_Home_at_400x860_mounts_and_its_band_pivots_dump_with_text_sizes()
        => host.Run(() =>
        {
            var session = new Diagnostics.InspectSession(realText: true);
            try
            {
                JsonElement mount = Reply(session, """{"cmd":"mount","args":{"w":400,"h":860,"theme":"zune","kind":"dark","gpu":"none","route":"home"},"id":1}""");
                Assert.Equal(1, mount.GetProperty("id").GetInt32());
                Assert.Equal("zune", mount.GetProperty("theme").GetString());
                Assert.Equal("Compact", mount.GetProperty("tiers").GetProperty("window").GetString());
                Assert.False(mount.TryGetProperty("networkLeaks", out _), "the mount reached the network");

                JsonElement found = Reply(session, """{"cmd":"find","args":{"prefix":"zune-band-pivot:"},"id":2}""");
                Assert.True(found.GetProperty("count").GetInt32() >= 1, "no band pivot at 400 x 860: " + found);
                string pivot = found.GetProperty("nodes")[0].GetProperty("key").GetString()!;
                Assert.StartsWith(ZuneBandRules.PivotKeyPrefix, pivot);

                JsonElement dump = Reply(session, "dump root=" + pivot);
                Assert.True(dump.GetProperty("count").GetInt32() >= 1);
                bool sawText = false;
                foreach (JsonElement node in dump.GetProperty("nodes").EnumerateArray())
                {
                    Assert.True(node.TryGetProperty("rect", out JsonElement rect));
                    Assert.True(rect.GetProperty("w").GetDouble() > 0);
                    if (!node.TryGetProperty("text", out JsonElement text)) continue;
                    if (text.GetProperty("string").GetString() is not { Length: > 0 }) continue;
                    Assert.True(text.GetProperty("sizeDip").GetDouble() > 0, "a pivot label without a size: " + node);
                    Assert.True(text.TryGetProperty("weight", out _));
                    Assert.True(text.TryGetProperty("trimmed", out _));
                    Assert.True(text.TryGetProperty("natural", out _));
                    sawText = true;
                }
                Assert.True(sawText, "the pivot has no text node: " + dump);

                JsonElement tiers = Reply(session, """{"cmd":"tiers","id":4}""");
                Assert.Equal("Compact", tiers.GetProperty("tiers").GetProperty("window").GetString());

                JsonElement render = JsonDocument.Parse(session.Execute("""{"cmd":"render","args":{"path":"x.png"},"id":5}""", out _)).RootElement.Clone();
                Assert.False(render.GetProperty("ok").GetBoolean(), "gpu=none has no offscreen capture");
                Assert.True(render.TryGetProperty("why", out _));
            }
            finally
            {
                session.Dispose();
                ZuneHost.Restore();
            }
            ShellHost.AssertNoNetwork();
        });

    [Fact]
    public void Zune_Home_at_400x860_lints_with_the_reply_shape_and_no_gutter_error()
        => host.Run(() =>
        {
            var session = new Diagnostics.InspectSession(realText: true);
            try
            {
                Reply(session, """{"cmd":"mount","args":{"w":400,"h":860,"theme":"zune","kind":"dark","gpu":"none","route":"home"},"id":1}""");

                JsonElement lint = Reply(session, """{"cmd":"lint","id":2}""");
                Assert.Equal(2, lint.GetProperty("id").GetInt32());
                Assert.Equal("lint", lint.GetProperty("cmd").GetString());
                Assert.Equal("zune", lint.GetProperty("theme").GetString());
                Assert.Equal("Compact", lint.GetProperty("tier").GetString());
                Assert.True(lint.GetProperty("guide").GetBoolean(), "Zune registers a lint guide: " + lint);
                Assert.Equal(400d, lint.GetProperty("size").GetProperty("w").GetDouble());

                JsonElement summary = lint.GetProperty("summary");
                Assert.True(summary.GetProperty("nodes").GetInt32() > 0, "the snapshot is empty: " + lint);
                foreach (string severity in new[] { "error", "warn", "info" }) Assert.True(summary.TryGetProperty(severity, out _), severity);

                // Every rule ran and says how much it looked at; the type scale is always there (the page has text).
                JsonElement rules = lint.GetProperty("rules");
                foreach (string rule in Diagnostics.InspectLint.RuleNames) Assert.True(rules.TryGetProperty(rule, out _), "rule " + rule + " is missing: " + rules);
                Assert.True(rules.GetProperty("type").GetProperty("checked").GetInt32() > 0, "no text was looked at: " + rules);
                Assert.True(rules.GetProperty("gutter").GetProperty("checked").GetInt32() >= 1, "no gutter edge was on screen (the band's pivots should be): " + rules);

                int counted = 0;
                foreach (JsonElement finding in lint.GetProperty("findings").EnumerateArray())
                {
                    counted++;
                    string rule = finding.GetProperty("rule").GetString()!;
                    string severity = finding.GetProperty("severity").GetString()!;
                    Assert.Contains(rule, Diagnostics.InspectLint.RuleNames);
                    Assert.Contains(severity, new[] { "error", "warn", "info" });
                    Assert.False(string.IsNullOrEmpty(finding.GetProperty("message").GetString()));
                    Assert.Equal(JsonValueKind.Array, finding.GetProperty("keys").ValueKind);
                    Assert.False(rule == "gutter" && severity == "error", "a gutter error on Zune Home at 400 x 860: " + finding);
                }
                Assert.True(counted > 0, "the type scale alone is info findings: " + lint);
                Assert.Equal(counted, summary.GetProperty("error").GetInt32() + summary.GetProperty("warn").GetInt32() + summary.GetProperty("info").GetInt32());

                // The arguments: a rule subset on a subtree, a severity floor.
                JsonElement band = Reply(session, """{"cmd":"lint","args":{"rules":"gutter,type","root":"zune-band","min":"warn"},"id":3}""");
                Assert.True(band.GetProperty("summary").GetProperty("nodes").GetInt32() < summary.GetProperty("nodes").GetInt32(), "root= did not narrow the lint");
                Assert.False(band.GetProperty("rules").TryGetProperty("clip", out _));
                Assert.All(band.GetProperty("findings").EnumerateArray(), f => Assert.NotEqual("info", f.GetProperty("severity").GetString()));

                JsonElement refused = JsonDocument.Parse(session.Execute("""{"cmd":"lint","args":{"rules":"wobble"},"id":4}""", out _)).RootElement.Clone();
                Assert.False(refused.GetProperty("ok").GetBoolean());
                Assert.Contains("wobble", refused.GetProperty("error").GetString());
            }
            finally
            {
                session.Dispose();
                ZuneHost.Restore();
            }
            ShellHost.AssertNoNetwork();
        });

    [Fact]
    public void Zune_Home_crossing_1440x960_to_1440x400_remounts_nothing_and_lints_the_settled_page()
        => host.Run(() =>
        {
            var session = new Diagnostics.InspectSession(realText: true);
            try
            {
                JsonElement crossing = Reply(session, """{"cmd":"crossing","args":{"from":{"w":1440,"h":960},"to":{"w":1440,"h":400},"theme":"zune","kind":"dark","gpu":"none","route":"home"},"id":1}""");
                Assert.Equal("crossing", crossing.GetProperty("cmd").GetString());
                Assert.True(crossing.GetProperty("narrowing").GetBoolean());
                Assert.Equal("Regular", crossing.GetProperty("tierBefore").GetProperty("height").GetString());
                Assert.Equal("Short", crossing.GetProperty("tierAfter").GetProperty("height").GetString());
                Assert.True(crossing.GetProperty("committed").GetBoolean(), "the height edge did not commit: " + crossing);
                Assert.True(crossing.GetProperty("heldOldTier").GetBoolean(), "the resized frames already read the new tier: " + crossing);

                JsonElement counts = crossing.GetProperty("counts");
                Assert.True(counts.GetProperty("tracked").GetInt32() > 0, "no keyed node was followed: " + crossing);
                // Known (round G): the one-row daylist moves its play ring into the strip's end slot, a new parent, so it remounts. Anything else is new.
                var remounted = crossing.GetProperty("remounted").EnumerateArray().Select(r => r.GetProperty("key").GetString()).Where(k => k != "zune-daylist-play").ToArray();
                Assert.True(remounted.Length == 0, "nodes were remounted on the crossing: " + crossing.GetProperty("remounted"));
                foreach (string list in new[] { "vanished", "appeared", "recounted", "snapped", "jumped" })
                    Assert.Equal(JsonValueKind.Array, crossing.GetProperty(list).ValueKind);
                Assert.True(crossing.GetProperty("settle").GetProperty("settled").GetBoolean(), "the page did not settle: " + crossing.GetProperty("settle"));

                JsonElement lint = crossing.GetProperty("lint");
                Assert.Equal("zune", lint.GetProperty("theme").GetString());
                Assert.True(lint.GetProperty("summary").GetProperty("nodes").GetInt32() > 0);
                Assert.Equal(JsonValueKind.Array, lint.GetProperty("findings").ValueKind);

                // The mount is left at the to-size, and a second crossing reuses it (widening back).
                Assert.Equal(400d, Reply(session, """{"cmd":"status","id":2}""").GetProperty("size").GetProperty("h").GetDouble());
                JsonElement back = Reply(session, """{"cmd":"crossing","args":{"from":{"w":1440,"h":400},"to":{"w":1440,"h":960}},"id":3}""");
                Assert.False(back.GetProperty("narrowing").GetBoolean());
                Assert.All(back.GetProperty("remounted").EnumerateArray(), r => Assert.Equal("zune-daylist-play", r.GetProperty("key").GetString()));   // the same known round-G remount

                JsonElement refused = JsonDocument.Parse(session.Execute("""{"cmd":"crossing","args":{"from":{"w":1440,"h":960}},"id":4}""", out _)).RootElement.Clone();
                Assert.False(refused.GetProperty("ok").GetBoolean());
            }
            finally
            {
                session.Dispose();
                ZuneHost.Restore();
            }
            ShellHost.AssertNoNetwork();
        });
}
