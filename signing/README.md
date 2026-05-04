# Signing Wavee with Azure Artifact Signing

Wavee uses **Azure Artifact Signing** (formerly *Trusted Signing*) for
production MSIX signatures. Reputation builds against the verified
publisher identity, not the certificate, so renewals don't reset
SmartScreen.

For everyday local development, the manifest still ships
`Publisher="CN=ckara"` — keep using a self-signed cert for that. This
folder only matters when you're building a **release** MSIX.

---

## Quick path: one command to build, sign, install

Once everything in this folder is set up (see **First-time setup** below):

```pwsh
./signing/Sign-Release.ps1
```

That script encapsulates the entire procedure that was hand-verified on
2026-05-04. It:

1. Captures the source manifest publisher (`CN=ckara`) and swaps to the
   release subject (`CN=cproducts, O=cproducts, …`).
2. Builds a Release-configuration MSIX via msbuild — without
   `RuntimeIdentifier` (that flag triggers WMC9999 in the WinUI XAML
   compiler) and with `UapAppxPackageBuildMode=SideloadOnly`,
   `AppxBundle=Never`, `AppxPackageSigningEnabled=false`.
3. Signs the produced `.msix` with the Artifact Signing dlib + the
   metadata.json profile.
4. Verifies the chain via `signtool verify /pa`.
5. Installs the package locally via `Add-AppxPackage`.
6. **Restores the source manifest publisher even if you Ctrl-C** —
   the swap-restore is wrapped in a try/finally.

Pass `-Platform x64` to build the Intel variant; `-SkipInstall` to
leave the package on disk without registering it.

---

## First-time setup (one-time)

### 1. Azure account-side (manual, in the portal)

1. Create an Azure Artifact Signing **account** in a supported region
   (West Europe for NL):
   <https://learn.microsoft.com/azure/trusted-signing/quickstart>
2. Add an **Identity Validation** for yourself or your organisation.
   Wait for approval.
3. Create a **Certificate Profile** of type *Public Trust* bound to
   the approved identity. Note the **Subject Name** Azure assigns to
   the profile — that becomes your release publisher (currently
   `CN=cproducts, O=cproducts, L=Utrecht, S=Utrecht, C=NL`).
4. Create an **Entra App Registration** + client secret. Grant it
   the **Artifact Signing Certificate Profile Signer** role
   (formerly "Trusted Signing Certificate Profile Signer") scoped to
   the cert profile resource only — least privilege. Capture
   `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`.

### 2. Local toolchain (winget)

```pwsh
winget install -e --id Microsoft.AzureCLI
winget install -e --id Microsoft.Azure.ArtifactSigningClientTools

# SDK signtool (one-shot installer ships with the client tools above)
& "$env:LOCALAPPDATA\Microsoft\MicrosoftArtifactSigningClientTools\winsdksetup.exe" `
    /features OptionId.SigningTools /q /norestart
```

This drops:

- `az.exe` on PATH (Azure CLI) — for `az login` / `az ad …` provisioning
- `Azure.CodeSigning.Dlib.dll` at
  `%LOCALAPPDATA%\Microsoft\MicrosoftArtifactSigningClientTools\` —
  the Artifact Signing plug-in for SignTool
- `signtool.exe` at `C:\Program Files (x86)\Windows Kits\10\bin\<ver>\x64\` —
  Microsoft's official SDK signer

### 3. metadata.json

```pwsh
Copy-Item signing/metadata.template.json signing/metadata.json
# Edit signing/metadata.json with your account's values:
#   Endpoint               = https://weu.codesigning.azure.net/
#   CodeSigningAccountName = Wavee
#   CertificateProfileName = wavee-public-trust
```

`signing/metadata.json` is gitignored — never commit it.

### 4. Authenticate

Either:

```pwsh
az login    # interactive — uses the developer's user identity
```

…or for non-interactive runs (matches CI), set the SPN env vars:

```pwsh
$env:AZURE_TENANT_ID = '…'
$env:AZURE_CLIENT_ID = '…'
$env:AZURE_CLIENT_SECRET = '…'
```

The dlib's `DefaultAzureCredential` picks up env vars first, then falls
back to `az login`.

---

## CI: GitHub Actions (`release.yml`)

Tag push (`v*`) or manual dispatch fires `.github/workflows/release.yml`.
It does the same six steps as the local script, in parallel for x64 and
ARM64, and uploads signed `.msix` artefacts to a draft GitHub Release.

Required repository secrets (*Settings → Secrets and variables →
Actions*):

| Secret | Example |
|---|---|
| `AZURE_TENANT_ID` | `00000000-0000-0000-0000-000000000000` |
| `AZURE_CLIENT_ID` | `00000000-0000-0000-0000-000000000000` |
| `AZURE_CLIENT_SECRET` | (from app registration) |
| `AZURE_ARTIFACT_SIGNING_ENDPOINT` | `https://weu.codesigning.azure.net/` |
| `AZURE_ARTIFACT_SIGNING_ACCOUNT` | your account name |
| `AZURE_ARTIFACT_SIGNING_PROFILE` | your cert profile name |
| `RELEASE_PUBLISHER_SUBJECT` | full Subject Name from cert profile |

These are populated as of 2026-05-04 for `christosk92/WaveeMusic`.

---

## Why the manifest publisher gets swapped

`Package.appxmanifest` checked into source uses `CN=ckara` so anyone
with a self-signed dev cert can build and sideload locally. Artifact
Signing issues a different Subject Name; SignTool refuses to sign an
MSIX whose manifest publisher doesn't match the cert (fails with
`0x8007000B "publisher name does not match certificate"`).
`Sign-Release.ps1` rewrites the publisher in-place around msbuild, then
restores it via the try/finally — even on Ctrl-C, the working tree
returns to the dev publisher.

The CI workflow does the same swap (`signing/Set-Publisher.ps1`)
against a freshly cloned repo, so the swap never lands in a commit.

---

## ⚠ LAF token coupling

The Phi Silica LAF token in `Wavee.UI.WinUI/Services/AiCapabilities.cs:58`
is cryptographically bound to the **publisher hash** of the manifest's
`Identity.Publisher`. Two different publishers yield two different
Package Family Names → two different tokens.

| Manifest publisher | Publisher hash | PFN |
|---|---|---|
| `CN=ckara` (dev) | `s6dvdzhx5m6rm` | `4f5ca249-aa41-4d46-84ce-62b6098fdb06_s6dvdzhx5m6rm` |
| `CN=cproducts, O=cproducts, …` (release) | `43x7j183z9t4g` | `4f5ca249-aa41-4d46-84ce-62b6098fdb06_43x7j183z9t4g` |

The currently-shipped LAF token (`4+g4v/xx6B81Wc6Z0sO0bg==`) was issued
on 2026-05-01 against the **dev** PFN. Production-signed builds with
the release publisher will have `LimitedAccessFeatures.TryUnlockFeature`
return `Unavailable`; `AiCapabilities` swallows that and silently hides
AI affordances — the app still works, just without on-device lyrics
features.

To unblock AI in production builds: file a second LAF request with
Microsoft for the release publisher's PFN and either replace the const
or store both tokens and pick by `Package.Current.Id.Publisher` at
runtime. See `signing/laf-second-request.md` for the email draft.

Microsoft confirmed (2026-05-01, Mike Bowen) that LAF tokens are safe
to commit publicly — they don't unlock anywhere except for an MSIX
signed by the same publisher.

---

## SmartScreen reputation

A freshly signed Wavee installer will still show a SmartScreen warning
the first few weeks. That's normal — Artifact Signing earns reputation
against the verified identity over time, not instantly. Don't sign with
a different identity to "reset" — keep distributing the same signed
package across users to accumulate reputation.

See:

- <https://learn.microsoft.com/windows/msix/package/sign-msix-package-guide>
- <https://learn.microsoft.com/windows/security/threat-protection/microsoft-defender-smartscreen/microsoft-defender-smartscreen-overview>

---

## Files in this folder

| File | What |
|---|---|
| `Sign-Release.ps1` | The full pipeline. Default entry point. |
| `Sign-Msix.ps1` | Lower-level wrapper around just the signtool step. Useful when you've already got an unsigned MSIX. |
| `Set-Publisher.ps1` | Standalone publisher-swap helper. Used by CI. |
| `metadata.template.json` | Committed template for `metadata.json`. |
| `metadata.json` | **Gitignored.** Your account-specific values. |
| `laf-second-request.md` | Email draft for requesting a production LAF token. |
| `README.md` | This file. |
