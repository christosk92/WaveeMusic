# Wavee.Tests — core library tests

xUnit v3 test suite for the `Wavee` core library.

`net10.0-windows10.0.26100.0` · `OutputType=Exe` · `AnyCPU;x64;ARM64` · stack: **xUnit v3 + FluentAssertions + Moq** (with `DynamicProxyGenAssembly2` granted `InternalsVisibleTo` from `Wavee` for proxy-based mocks).

## Folder map

```
Wavee.Tests/
├── Audio/
├── Connect/
├── Core/
│   └── Crypto/         # Has its own README — see below
├── Helpers/
└── xunit.runner.json
```

## Crypto validation

[`Core/Crypto/README.md`](Core/Crypto/README.md) documents the **librespot validation** for cryptographic primitives — `ShannonCipher` (28 tests against librespot vectors) and `AudioDecryptStream` (9 tests). It includes instructions for regenerating test vectors from the librespot Rust source if you need to extend coverage.

## PlayPlay tests live elsewhere

PlayPlay tests are in `Wavee.PlayPlay.Tests` because they need an x64-only process and reference `Wavee.AudioHost` directly. Don't add PlayPlay tests here.

## Run

```bash
# Whole suite
dotnet test Wavee.Tests/Wavee.Tests.csproj

# Single namespace
dotnet test Wavee.Tests/Wavee.Tests.csproj --filter "FullyQualifiedName~Wavee.Tests.Connect"

# Single class
dotnet test Wavee.Tests/Wavee.Tests.csproj --filter "FullyQualifiedName~ShannonCipher"
```

## Project refs

`Wavee` (the system under test), `Wavee.Controls.Lyrics` (touches lyrics control surface in a few tests).
