using System;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Core.Home;

namespace Wavee;

/// <summary>The single owner of the Home layout document. ONE reference-stable instance created in
/// <c>Services</c> and provided at the app root via <see cref="Slot"/>. Load is fail-soft: a corrupt file is
/// never overwritten until the first successful save after <see cref="DiscardCorrupt"/>.</summary>
public sealed class HomePreferences
{
    public static readonly Context<HomePreferences?> Slot = new(null);

    readonly HomeLayoutStore _store;
    readonly Signal<int> _layoutVersion = new(0);

    HomeLayoutDoc _layout;
    HomeLayoutWireCarry _carry = HomeLayoutWireCarry.Empty;
    bool _loaded;

    public HomePreferences(HomeLayoutStore store)
    {
        _store = store;
        _layout = HomeLayoutDoc.Default;
        LoadDocument();
    }

    public HomeLayoutDoc Layout => _layout;
    public IReadSignal<int> LayoutVersion => _layoutVersion;
    public HomeLayoutLoadFault Fault { get; private set; }
    public string? FaultDetail { get; private set; }
    public bool WritesBlocked => _store.WritesBlocked;
    public string FilePath => _store.FilePath;

    public HomeLayoutRejectReason Dispatch(HomeLayoutCommand command)
    {
        var result = HomeLayoutReducer.Apply(_layout, command);
        if (!result.Changed) return result.Reason;

        _layout = result.Layout;
        _layoutVersion.Value = _layoutVersion.Peek() + 1;
        Commit();
        return HomeLayoutRejectReason.None;
    }

    public void DiscardCorrupt()
    {
        _store.DiscardCorrupt();
        _layout = HomeLayoutDoc.Default;
        _carry = HomeLayoutWireCarry.Empty;
        Fault = HomeLayoutLoadFault.None;
        FaultDetail = null;
        _layoutVersion.Value = _layoutVersion.Peek() + 1;
        _loaded = true;
        Commit();
    }

    public bool WaitForWrites(int timeoutMs = 5000) => _store.WaitForWrites(timeoutMs);

    void LoadDocument()
    {
        var load = _store.Load();
        Fault = load.Fault;
        FaultDetail = load.Detail;
        if (load.Doc is { } dto)
        {
            var read = HomeLayoutWire.Read(dto);
            _layout = read.Layout;
            _carry = read.Carry;
            _carry.CaptureDoc(dto);
        }
        else
        {
            _layout = HomeLayoutDoc.Default;
            _carry = HomeLayoutWireCarry.Empty;
        }
        _loaded = true;
    }

    void Commit()
    {
        if (!_loaded) return;
        var snapshot = HomeLayoutWire.Write(_layout, _carry);
        _carry.ReattachDoc(snapshot);
        _store.Commit(snapshot);
    }
}
