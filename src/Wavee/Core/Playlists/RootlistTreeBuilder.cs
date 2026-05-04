using Microsoft.Extensions.Logging;

namespace Wavee.Core.Playlists;

public static class RootlistTreeBuilder
{
    /// <summary>
    /// Builds a hierarchical tree from the flat rootlist stream.
    /// Each <see cref="RootlistNode"/>'s <see cref="RootlistNode.Children"/> preserves
    /// the **arrival order** of folders and playlists at that level — a folder appearing
    /// between two playlists stays between them in the output.
    ///
    /// Self-heals against malformed input: mismatched end-groups pop by ID (not blindly),
    /// stray end-groups without a matching open folder are ignored, unclosed folders at
    /// end-of-list are auto-closed. Each anomaly is logged at Warning level when a logger
    /// is supplied so server-side misbehaviour is visible.
    /// </summary>
    public static RootlistTree Build(IReadOnlyList<RootlistEntry> items, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        var root = new MutableNode(id: null, name: null);
        var stack = new Stack<MutableNode>();
        stack.Push(root);

        foreach (var item in items)
        {
            switch (item)
            {
                case RootlistPlaylist playlist:
                    stack.Peek().Children.Add(new RootlistChildPlaylist(playlist.Uri));
                    break;

                case RootlistFolderStart folderStart:
                    var folder = new MutableNode(folderStart.Id, folderStart.Name);
                    stack.Peek().Children.Add(folder); // placeholder — replaced with frozen RootlistChildFolder later
                    stack.Push(folder);
                    break;

                case RootlistFolderEnd folderEnd:
                    ApplyFolderEnd(stack, folderEnd.Id, logger);
                    break;
            }
        }

        // Self-heal: close any folders left open at EOF. Balanced input is a no-op.
        while (stack.Count > 1)
        {
            var unclosed = stack.Pop();
            logger?.LogWarning(
                "Rootlist folder '{Name}' (id={Id}) was not closed; auto-closing at end of list",
                unclosed.Name ?? "<unnamed>", unclosed.Id ?? "<null>");
        }

        return new RootlistTree { Root = Freeze(root) };
    }

    /// <summary>
    /// Pops folders off the stack until the one whose Id matches <paramref name="endId"/>.
    /// Pops inclusive of the match. If no match exists, the end-group is a stray and we
    /// do nothing (aside from logging) — this keeps the tree consistent when the server
    /// emits an inconsistent stream.
    /// </summary>
    private static void ApplyFolderEnd(Stack<MutableNode> stack, string endId, ILogger? logger)
    {
        var matchDepth = -1;
        var depth = 0;
        foreach (var node in stack)
        {
            if (node.Id != null && string.Equals(node.Id, endId, StringComparison.Ordinal))
            {
                matchDepth = depth;
                break;
            }
            depth++;
        }

        if (matchDepth < 0)
        {
            logger?.LogWarning(
                "Stray rootlist end-group id={Id} — no matching open folder; ignoring",
                endId);
            return;
        }

        for (var i = 0; i < matchDepth; i++)
        {
            var unclosed = stack.Pop();
            logger?.LogWarning(
                "Rootlist folder '{Name}' (id={Id}) not closed before end-group {EndId}; auto-closing",
                unclosed.Name ?? "<unnamed>", unclosed.Id ?? "<null>", endId);
        }
        stack.Pop();
    }

    private static RootlistNode Freeze(MutableNode node)
    {
        var frozenChildren = new List<RootlistChild>(node.Children.Count);
        foreach (var child in node.Children)
        {
            frozenChildren.Add(child switch
            {
                MutableNode subFolder => new RootlistChildFolder(Freeze(subFolder)),
                RootlistChildPlaylist playlist => playlist,
                _ => throw new InvalidOperationException($"Unexpected child type: {child.GetType().Name}")
            });
        }

        return new RootlistNode
        {
            Id = node.Id,
            Name = node.Name,
            Children = frozenChildren
        };
    }

    /// <summary>
    /// Mutable scratch node used during the build pass. Inherits from <see cref="RootlistChild"/>
    /// so it can sit in <see cref="Children"/> alongside <see cref="RootlistChildPlaylist"/> entries
    /// while preserving arrival order; <see cref="Freeze"/> later swaps each one for the immutable
    /// <see cref="RootlistChildFolder"/> wrapper. Declared as a sealed record because
    /// <see cref="RootlistChild"/> is an abstract record (CS8865: only records may inherit records).
    /// </summary>
    private sealed record MutableNode : RootlistChild
    {
        public MutableNode(string? id, string? name)
        {
            Id = id;
            Name = name;
        }

        public string? Id { get; }
        public string? Name { get; }
        public List<RootlistChild> Children { get; } = [];
    }
}
