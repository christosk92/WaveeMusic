using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Wavee.Backend;
using Wavee.Backend.Realtime;

namespace Wavee.Tests;

/// <summary>
/// Replays a trimmed <see cref="Wavee.DealerArchive"/> capture (the <c>.idx.ndjson</c> + <c>.bin</c> pair) back through the
/// REAL frame parser, producing the exact <see cref="WireEvent"/> stream the live transport would have published.
/// <para>The archive index rows are <c>{t, typ, uri, handled, n, off}</c>; the bytes at <c>[off, off+n)</c> in the
/// <c>.bin</c> are the raw dealer WebSocket JSON (<c>{headers, payloads, type, uri}</c>) with <c>payloads[0]</c> a
/// base64 protobuf. The fixture under <c>Fixtures/dealer/</c> is a byte-exact <c>hm://playlist*</c> subset of a real
/// 2026-08-15 session, re-offset into its own <c>.bin</c>.</para>
/// </summary>
public static class DealerArchiveReplay
{
    /// <summary>One archived frame: the capture timestamp, its dealer topic, whether the live app claimed to handle it,
    /// and the raw frame JSON.</summary>
    public readonly record struct Row(long T, string Uri, bool Handled, byte[] Json);

    public static string FixtureIdxPath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "dealer", "playlist-20260815.idx.ndjson");

    public static string FixtureBinPath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "dealer", "playlist-20260815.bin");

    /// <summary>Load every index row plus its raw frame JSON, in capture order (the index is already ascending in
    /// <c>t</c>; ties keep their captured interleave, which is what makes the duplicated rootlist pair faithful).</summary>
    public static IReadOnlyList<Row> Load(string idxPath, string binPath)
    {
        var rows = new List<Row>();
        using var bin = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        foreach (var line in File.ReadLines(idxPath))
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("uri", out var uriEl)) continue;   // keepalive/dropped summaries carry no uri
            int n = root.GetProperty("n").GetInt32();
            long off = root.GetProperty("off").GetInt64();
            var json = new byte[n];
            bin.Position = off;
            int read = 0;
            while (read < n)
            {
                int got = bin.Read(json, read, n - read);
                if (got <= 0) throw new InvalidDataException($"short read at off={off} n={n}");
                read += got;
            }
            rows.Add(new Row(root.GetProperty("t").GetInt64(), uriEl.GetString() ?? "",
                root.TryGetProperty("handled", out var h) && h.GetBoolean(), json));
        }
        return rows;
    }

    public static IReadOnlyList<Row> Load() => Load(FixtureIdxPath, FixtureBinPath);

    /// <summary>Run every archived frame through <see cref="DealerFrameParser"/> and emit the MESSAGE pushes exactly as
    /// <c>LiveDealerTransport.ReceiveLoop</c> does: <c>new WireEvent(uri, f.Payload, f.Headers)</c> for a MESSAGE frame
    /// carrying a topic; ping/pong/request/topic-less frames are dropped, same as live.</summary>
    public static IEnumerable<WireEvent> Frames(string idxPath, string binPath)
    {
        foreach (var row in Load(idxPath, binPath))
        {
            var f = DealerFrameParser.Parse(row.Json);
            if (f.Type == DealerFrameType.Message && f.Uri is { Length: > 0 } uri)
                yield return new WireEvent(uri, f.Payload, f.Headers);
        }
    }

    public static IEnumerable<WireEvent> Frames() => Frames(FixtureIdxPath, FixtureBinPath);

    /// <summary>Push the whole capture into a <see cref="StubTransport"/> in capture order. <paramref name="onEach"/>
    /// runs BEFORE each push, so a scripted server can advance its state to the head the frame is about to announce.</summary>
    public static int PushAll(StubTransport dealer, Action<WireEvent>? onEach = null)
    {
        int n = 0;
        foreach (var e in Frames())
        {
            onEach?.Invoke(e);
            dealer.PushEvent(e);
            n++;
        }
        return n;
    }
}
