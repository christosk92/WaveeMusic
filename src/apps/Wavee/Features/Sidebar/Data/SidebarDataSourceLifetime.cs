using System;

namespace Wavee;

/// <summary>A source whose publications use the binder's UI dispatcher.</summary>
public interface ISidebarDataSourceLifecycle
{
    void Attach(Action<Action> post);
    void Detach();
}

/// <summary>Configured sections declare demand during one projection pass; removed sections release their handles.</summary>
public interface ISidebarDataSourceDemandLifecycle
{
    void BeginDemandPass();
    void EndDemandPass();
    void SetActive(bool active);
}
