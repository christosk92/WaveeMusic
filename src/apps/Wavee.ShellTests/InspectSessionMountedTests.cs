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
}
