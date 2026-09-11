using System;
using System.Collections;
using System.Collections.Generic;

namespace Wavee.Backend.Queries;

/// <summary>Immutable row vectors. A metadata change copies only affected 256-row chunks.
/// <para><b>Projector contract.</b> <see cref="Project"/> tests a row for change by REFERENCE, never structurally: a
/// projector MUST hand back the previous instance for a row that did not change. Every projector in the app does —
/// <see cref="CatalogReadView"/> caches one joined instance per entity and returns that same object while its facts
/// hold — and a structural test bought nothing for them: it ran <c>EqualityComparer&lt;Track&gt;.Default.Equals</c>
/// twice per row over a 1.5k-row playlist on every catalog publication to re-derive an answer reference identity
/// already had. A projector that returns an equal-but-new instance stays CORRECT (the vector holds what it returned)
/// but costs a chunk copy and the vector's identity, which republishes the whole page.</para></summary>
public sealed class ChunkedRows<T> : IReadOnlyList<T> where T : class
{
    public const int ChunkSize = 256;
    readonly T[][] _chunks;
    public int Count { get; }
    ChunkedRows(T[][] chunks, int count) => (_chunks, Count) = (chunks, count);
    public T this[int index] => (uint)index < (uint)Count ? _chunks[index / ChunkSize][index % ChunkSize]
        : throw new ArgumentOutOfRangeException(nameof(index));
    public static ChunkedRows<T> Project(ChunkedRows<T>? previous, int count, Func<int, T> project)
    {
        T[][]? changed = null;
        int chunkCount = (count + ChunkSize - 1) / ChunkSize;
        if (previous is null || previous.Count != count) changed = new T[chunkCount][];
        for (int c = 0; c < chunkCount; c++)
        {
            int size = Math.Min(ChunkSize, count - c * ChunkSize);
            T[]? old = previous is not null && c < previous._chunks.Length ? previous._chunks[c] : null;
            T[]? next = old?.Length == size ? null : new T[size];
            for (int i = 0; i < size; i++)
            {
                T value = project(c * ChunkSize + i);
                if (next is not null) next[i] = value;
                else if (!ReferenceEquals(old![i], value))
                { next = (T[])old.Clone(); next[i] = value; }
            }
            if (next is not null)
            {
                changed ??= (T[][])previous!._chunks.Clone();
                changed[c] = next;
            }
            else if (changed is not null) changed[c] = old!;
        }
        return changed is null ? previous! : new(changed, count);
    }
    public IEnumerator<T> GetEnumerator()
    { foreach (var chunk in _chunks) foreach (var item in chunk) yield return item; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
