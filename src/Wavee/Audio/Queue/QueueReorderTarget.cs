namespace Wavee.Audio.Queue;

/// <summary>
/// Identifies which queue bucket a drag-reorder operates within. Reorders never
/// cross buckets — each value maps to one of <see cref="PlaybackQueue"/>'s three
/// lists.
/// </summary>
public enum QueueReorderTarget
{
    /// <summary>The user "Play Next" queue (<c>provider="queue"</c>, UID <c>q#</c>).</summary>
    UserQueue,

    /// <summary>The post-context "Add to Queue" bucket (UID <c>p#</c>).</summary>
    PostContextQueue,

    /// <summary>The upcoming portion of the loaded context (and its autoplay tail).</summary>
    ContextUpcoming,
}
