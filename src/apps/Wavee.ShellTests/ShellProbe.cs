// ── Wavee.ShellTests/ShellProbe.cs — reading a mounted shell: keyed nodes, subtrees, playback ──────────────────────
//
// What the facts assert through: the engine's public scene and animation inspection (`SceneStore`, `AnimEngine`) and the
// node keys production already mounts. Nothing here adds a key, reads source text or compares pixels.

using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Scene;
using Wavee;
using Xunit;

namespace Wavee.ShellTests;

static class ShellProbe
{
    /// <summary>Run frames until <paramref name="ready"/> holds, failing after <paramref name="maxFrames"/>.</summary>
    public static void Until(Shell.HeadlessShell shell, Func<bool> ready, string what, int maxFrames = 240)
    {
        for (int i = 0; i < maxFrames; i++)
        {
            if (ready()) return;
            shell.Host.RunFrame();
        }
        Assert.Fail("never happened within " + maxFrames + " frames: " + what);
    }

    /// <summary>The one live node with <paramref name="key"/>, failing when it is not mounted.</summary>
    public static NodeHandle Node(Shell.HeadlessShell shell, string key)
    {
        var node = shell.Find(key);
        Assert.False(node.IsNull, "no mounted node keyed '" + key + "'");
        return node;
    }

    /// <summary>The one live node whose key starts with <paramref name="prefix"/>, failing unless exactly one is mounted.</summary>
    public static (NodeHandle Node, string Key) Single(Shell.HeadlessShell shell, string prefix)
    {
        var found = shell.FindPrefix(prefix);
        Assert.True(found.Count == 1, "expected one node keyed '" + prefix + "…', found " + found.Count + ": " + string.Join(", ", found.Select(f => f.Key)));
        return found[0];
    }

    /// <summary>The node and every live descendant.</summary>
    public static IEnumerable<NodeHandle> Subtree(SceneStore scene, NodeHandle root)
    {
        var stack = new Stack<NodeHandle>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.IsNull || !scene.IsLive(n)) continue;
            yield return n;
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) stack.Push(c);
        }
    }

    public static int CountUnder(Shell.HeadlessShell shell, NodeHandle root) => Subtree(shell.Host.Scene, root).Count();

    /// <summary>True when <paramref name="root"/> or a descendant carries live animation tracks.</summary>
    public static bool AnyTracksUnder(Shell.HeadlessShell shell, NodeHandle root)
        => Subtree(shell.Host.Scene, root).Any(shell.Host.Animation.HasTracks);

    /// <summary>The first scrolling descendant of <paramref name="root"/> (a virtual list's viewport).</summary>
    public static NodeHandle ScrollUnder(Shell.HeadlessShell shell, NodeHandle root)
    {
        var scene = shell.Host.Scene;
        foreach (var n in Subtree(scene, root))
            if (scene.HasScroll(n)) return n;
        return NodeHandle.Null;
    }

    /// <summary>Press and release <paramref name="vk"/> (no modifiers) through the window's input queue.</summary>
    public static void Key(Shell.HeadlessShell shell, int vk)
    {
        shell.Window.QueueInput(new InputEvent(InputKind.Key, default, 0, vk));
        shell.Window.QueueInput(new InputEvent(InputKind.KeyUp, default, 0, vk));
    }

    /// <summary>Play one fake track as its own context from <paramref name="fromMs"/> (the headless probe's single-track arm,
    /// Diagnostics.Probe.Play).</summary>
    public static void Play(string uri, int fromMs = 0)
    {
        Assert.True(EntityId.TryParse(uri.AsSpan(), out EntityId id), uri);
        EntityRef row = Entities.Ref(id);
        Span<EntityRef> refs = [row];
        Span<QueueEdge> rows = stackalloc QueueEdge[1];
        int n = Diagnostics.Headless.BuildContextQueue(refs, 0, refs, rows);
        Queue.Replace(refs[..n], rows[..n]);
        Playback.PlayNow(row, id, Queue.CursorOf(0), Playback.PlayableKind.Audio, fromMs);
    }
}
