# Wavee wiring discipline — no silent optional dependencies

## Rule (binding)

**Never make a required runtime dependency optional and swallow the miss.**

Forbidden patterns:

```csharp
// BAD — silent success when the backend is not wired
readonly IFoo? _foo;
public Task DoAsync(...) => _foo?.DoAsync(...) ?? Task.CompletedTask;

// BAD — optional ctor param that hides a missing seam
public Bar(IFoo? foo = null) { _foo = foo; }

// BAD — optional Func that defaults to a no-op / empty value on the hot path
public Baz(Func<string>? baseUrl = null) => _baseUrl = baseUrl ?? (() => "");
```

Required patterns:

```csharp
// GOOD — dependency is required; every construction site passes a real impl
readonly IFoo _foo;
public Bar(IFoo foo) => _foo = foo;
public Task DoAsync(...) => _foo.DoAsync(...);

// GOOD — explicit environment-specific impls (fail loud where unsupported)
// Fake backend: LocalPlaylistMutationSource (local playlists only; Spotify throws)
// Real backend: PlaylistMutationSource (wired on go-live)

// GOOD — offline / test-only stubs that throw or no-op WITH INTENT, named as such
// e.g. StubTransport, LocalPlaylistMutationSource — not nullable fields on the bridge
```

## Why

Optional nullable deps + `?? Task.CompletedTask` / `?? ""` produce **silent false success**: the UI shows toasts, dialogs close, and nothing reaches Spotify. This is worse than an exception because it erodes trust and is hard to debug.

## Checklist before merging seam work

1. Is every `IPlaylistMutationSource` / `IMutationSource` / transport dependency **required** in the bridge/composition root?
2. Does the fake backend use a **named** stub (`LocalPlaylistMutationSource`), not `null`?
3. Are go-live hooks (`ScheduleDrain`, `SetHttp`, `SpclientBaseUrlHolder`) set in `LiveSessionHost` and cleared in `GoOffline`?
4. Do tests pass an explicit spclient base URL to `OpRebaseStrategy`, not an empty default?
