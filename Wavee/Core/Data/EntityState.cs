using System;

namespace Wavee.Core.Data;

public enum Freshness
{
    Fresh,
    Stale
}

// Closed hierarchy — only the nested sealed records are valid states.
public abstract record EntityState<T>
{
    private EntityState() { }

    public sealed record Initial : EntityState<T>;

    public sealed record Loading(T? Previous) : EntityState<T>;

    public sealed record Ready(T Value, DateTimeOffset Stamp, Freshness Freshness) : EntityState<T>;

    public sealed record Error(Exception Exception, T? Previous) : EntityState<T>;
}
