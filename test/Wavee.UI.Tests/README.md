# Wavee.UI.Tests — UI service-layer tests

xUnit v3 test suite for the framework-neutral UI service layer (`Wavee.UI`). Doesn't touch WinUI.

`net10.0-windows10.0.26100.0` (matches `Wavee.UI`'s TFM, which itself matches `Wavee.Controls.Lyrics`) · `OutputType=Exe` · `AnyCPU;x86;x64;ARM64`. Stack: **xUnit v3 + FluentAssertions + Moq**.

## Why this is separate from `Wavee.Tests`

`Wavee.UI` declares `InternalsVisibleTo` for this project, so tests can exercise `internal` types directly. `Wavee.Tests` doesn't need that surface and would just inherit unrelated WinUI-flavored TFMs if it did.

## Folder map

```
Wavee.UI.Tests/
├── Helpers/
└── Services/
```

## Run

```bash
dotnet test Wavee.UI.Tests/Wavee.UI.Tests.csproj
```

## Project refs

Only `Wavee.UI`. Deliberately no `Wavee.UI.WinUI` reference — that would drag MSIX / packaging / RID complexity into the test build.
