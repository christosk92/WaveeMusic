// ── Home/Board/Hosts/GridPager.cs: one pager surface for the themes' page controls (a shelf's controller, or the shortcuts grid's own pages) ─────
//
// The themes draw a pager (WinUI's buttons and pips, Classic's "1 of 2" and buttons, Zune's "01/02") over IPager: a PagedShelf's ShelfController
// through ShelfPagerSource, or the shortcuts grid's GridPager (its pages are not a scroller: a cell dragged sideways inside one must move the cell,
// never pan a strip).

using FluentGpu.Controls;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>What a pager control reads and calls.</summary>
public interface IPager
{
    IReadSignal<int> Page { get; }
    IReadSignal<int> PageCount { get; }
    IReadSignal<bool> CanPrev { get; }
    IReadSignal<bool> CanNext { get; }
    void GoTo(int page);
    void Prev();
    void Next();
}

/// <summary>A PagedShelf's external controller as a pager.</summary>
public sealed class ShelfPagerSource(ShelfController controller) : IPager
{
    public IReadSignal<int> Page => controller.Page;
    public IReadSignal<int> PageCount => controller.PageCount;
    public IReadSignal<bool> CanPrev => controller.CanPrev;
    public IReadSignal<bool> CanNext => controller.CanNext;
    public void GoTo(int page) => controller.GoTo(page);
    public void Prev() => controller.Prev();
    public void Next() => controller.Next();
}

/// <summary>The shortcuts grid's pages: the page shown and how many there are (the grid publishes the count; the page clamps to it).</summary>
public sealed class GridPager : IPager
{
    readonly Signal<int> _page = new(0), _count = new(1);
    readonly Signal<bool> _canPrev = new(false), _canNext = new(false);
    readonly string? _memoryKey;
    int _wanted = -1;   // the remembered page, applied once the grid has that many pages

    /// <summary>The pages grids built with a memory key were left on (in memory, for the process).</summary>
    static readonly Dictionary<string, int> s_memory = new(StringComparer.Ordinal);

    /// <summary>A pager; with <paramref name="memoryKey"/> it remembers its page under that key, and a pager made under the same key starts on it
    /// (the shortcuts grid comes back on the page it was left on).</summary>
    public GridPager(string? memoryKey = null)
    {
        _memoryKey = memoryKey;
        if (memoryKey is not null && s_memory.TryGetValue(memoryKey, out int page) && page > 0) _wanted = page;
    }

    public IReadSignal<int> Page => _page;
    public IReadSignal<int> PageCount => _count;
    public IReadSignal<bool> CanPrev => _canPrev;
    public IReadSignal<bool> CanNext => _canNext;

    /// <summary>The page a grid of <paramref name="count"/> pages shows first: the remembered one while it is pending, else the current one.</summary>
    public int FirstPage(int count) => _wanted >= 0 && _wanted < count ? _wanted : Math.Clamp(_page.Peek(), 0, Math.Max(0, count - 1));

    public void GoTo(int page) { _wanted = -1; Set(Math.Clamp(page, 0, Math.Max(0, _count.Peek() - 1)), _count.Peek()); }
    public void Prev() => GoTo(_page.Peek() - 1);
    public void Next() => GoTo(_page.Peek() + 1);

    /// <summary>The grid's page count changed (a width or the cells): the page stays where it is, within the new count (a remembered page lands
    /// as soon as the grid has it).</summary>
    public void Publish(int count)
    {
        count = Math.Max(1, count);
        if (_wanted >= 0 && _wanted < count) { int w = _wanted; _wanted = -1; Set(w, count); return; }
        Set(Math.Clamp(_page.Peek(), 0, count - 1), count);
    }

    void Set(int page, int count)
    {
        _count.SetIfChanged(count);
        _page.SetIfChanged(page);
        _canPrev.SetIfChanged(page > 0);
        _canNext.SetIfChanged(page < count - 1);
        // Only while it pages at all, and not while a remembered page waits: a one-page fit never writes over the memory.
        if (_memoryKey is not null && count > 1 && _wanted < 0) s_memory[_memoryKey] = page;
    }
}
