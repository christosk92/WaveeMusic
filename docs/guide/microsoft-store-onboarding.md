# Microsoft Store onboarding (cproducts.dev)

Decision (2026-08-30): Wavee goes on the Microsoft Store as a **Company** listing under cproducts. The risk
assessment lives in `docs/plans/wavee/marketing-plan.md` §6; this is the how-to, taken from the current
(Sep 2025 policies / Jul 2026 docs) Partner Center flow.

## 1. The account — free, Company type, verified through cproducts.dev

Start ONLY at <https://storedeveloper.microsoft.com> ("Get started" → **Company account**). Any other entry point
(Partner Center directly, Visual Studio) shows the legacy paid flow. A Company account is required, not optional:
policy 10.14 — "cproducts" reads as a business, and the KvK/BTW numbers are on the site. Individual → Company
cannot be converted later.

Have ready before you start:

| Item | Use |
|---|---|
| Work mailbox on the domain, e.g. `christos@cproducts.dev` (or `hello@cproducts.dev`) | sign-in / "email for verification" — personal mailboxes are refused; the domain must match the business |
| KvK uittreksel (KvK 98954148) — or a DUNS number | business verification; DUNS is automatic, the KvK extract goes to manual review (2–5 business days) |
| Legal name + address exactly as on the KvK record (Western characters) | must match the registry |
| Passport or ID of the primary contact (you) | mandatory due diligence — blocking step |
| Support contact shown on the listing: `hello@cproducts.dev`, `https://cproducts.dev/contact` | policy 10.14 |
| Privacy policy URL `https://cproducts.dev/privacy` | policy 10.5.1 — required for every Desktop Bridge app |

Sign in with either a personal MSA or an Entra ID work account on the cproducts.dev tenant (tenant-wide
onboarding; only the onboarding user gets Owner). The verification summary shows due diligence → business →
employment; each allows three appeals, and editing name/address/domain restarts it. After "Publish to Store" the
Apps & Games workspace appears (allow ~30 min).

## 2. Reserve the name and read the identity

Partner Center → Apps & games → New product → MSIX → reserve **"Wavee"** (name reservations are unique
Store-wide; have "Wavee Music" ready as a fallback). Then Product management → **View app identity details**:

- `Package/Identity/Name` — e.g. `cproducts.Wavee` (the Store assigns it; it may equal ours).
- `Package/Identity/Publisher` — `CN=<GUID>`. **Not** our signing subject.
- `Package/Properties/PublisherDisplayName` — `cproducts`.

The Store re-signs every MSIX with Microsoft's certificate, so the Store package carries the Store publisher. Two
consequences:

1. **The Store build is a different package family from the sideload build.** Same Name + different Publisher =
   Windows treats them as two apps; they install side by side and the Store cannot update a sideloaded install.
   Existing testers keep the `.appinstaller` feed; Store users are a new population. The app should import the
   sideload build's LocalCache (settings, library.db, sidebar layout) on first run of the Store build when present.
2. **Version rule.** Store packages must be `M.m.p.0` with the fourth field 0 (the Store owns it) and the first
   field ≥ 1. Ours is `0.2.1.<WaveeBuild>`. The Store submission needs its own quad — decide once: either move
   Wavee to `1.x` for the Store, or map `0.m.p.b` → `1.m.(p*100+b).0`. The feed quad and `AppUpdateVersion` are
   untouched by this; it is a pack-time parameter.

## 3. The package

`pack-wavee-msix.ps1` already takes `-IdentityName`, `-Publisher`, `-NoSign`. A Store pack per architecture:

```powershell
# from the release commit, PlayPlay junction present, one call per arch
powershell -File ops\build\pack-wavee-msix.ps1 -Arch x64   -Quad 1.2.1.0 -Semver 0.2.1 -Channel store `
  -IdentityName <Identity/Name from Partner Center> -Publisher "CN=<GUID from Partner Center>" -NoSign
powershell -File ops\build\pack-wavee-msix.ps1 -Arch arm64 -Quad 1.2.1.0 -Semver 0.2.1 -Channel store `
  -IdentityName <...> -Publisher "CN=<GUID>" -NoSign
```

Upload both `.msix` files in one submission (same version, different `ProcessorArchitecture` — allowed), or wrap
them in one `.msixbundle` with `makeappx bundle`. Unsigned is fine — the Store signs. Run the Windows App
Certification Kit on each package before uploading; it catches manifest/asset problems that otherwise cost a
certification round.

Manifest changes for the `store` channel (`ops/build/Wavee.AppxManifest.xml` + the pack script):

- **Drop `<rescap:Capability Name="packageManagement" />`.** It exists for the in-app "Update now"
  (`PackageManager.AddPackageByAppInstallerFileAsync`). It is a *restricted* capability: Store submissions must
  justify it on the Submission options page and testers approve or fail the submission. In the Store the update is
  the Store's job, so the capability has no justification. `runFullTrust` stays — it is the normal packaged-Win32
  capability and is approved for ordinary accounts (state in the notes that Wavee is a packaged desktop app).
- Keep `uap:Protocol wavee`, the toast COM activator and the startup task; they are fine.
- `Channel = store` must reach the app (it already ships as `AssemblyMetadata Channel`).

App behaviour when `Channel == "store"` (or `Package.Current.SignatureKind == Store`):

- No `.appinstaller` feed polling, no in-app apply; Settings › About shows "Updates come from the Microsoft Store"
  with a link `ms-windows-store://pdp/?productid=<StoreId>`; the update toasts and `AppUpdateToasts` states for
  the feed path are never armed.
- What's new still opens on the first launch of a new version (it reads the bundled `whatsnew.json`).
- Crash reports/symbols: unchanged — keep uploading the symbols zips to the GitHub release of the same commit; the
  Store's own crash analytics only work with an `.appxsym`, which our pack does not produce.

## 4. The submission

**Pricing & availability**: free; leave all markets (Windows 11 only is expressed by the manifest `MinVersion` —
`10.0.19041.0` today; raise to `10.0.22000.0` if Windows 10 installs are unwanted).

**Properties**: category *Music*; privacy policy `https://cproducts.dev/privacy`; website `https://cproducts.dev`;
support `https://cproducts.dev/contact`. Declare "This app accesses the internet". Age rating: IARC questionnaire
(a music client showing other users' playlists → expect PEGI 3 / ESRB E with "users interact").

**Store listing** (per language; start with en-US):

- Name: `Wavee`. Short description and description that say, in the first lines, *third-party client for your
  own Spotify Premium account — not made by, endorsed by, or affiliated with Spotify AB*. Use "for Spotify" only
  nominatively; never the Spotify logo, green, or wordmark in icon, screenshots or tiles (policy 11.2 — this is
  exactly the trademark complaint that removed Spotimo).
- Screenshots: PNG, ≥1366×768 (1920×1080 or 2560×1440 native captures from
  `ops/release/tools/Capture-WaveeWindow.ps1`), 4–8 of them: home, artist page with lyrics, playlist with facts,
  library, queue + video, settings. Keep key content in the top two-thirds; no marketing text on them.
- Store logos: **1:1 app tile 300×300** (strongly recommended — otherwise the Store takes the package logo),
  optional 16:9 super-hero 1920×1080 with no text and no UI (needed for a trailer to sit at the top).
- Trailer (optional, later): MP4 H.264 1920×1080 ≤60 s + 1920×1080 PNG thumbnail.

**Submission options → Notes for certification** (policy 10.3.1 — testers need to sign in):

- A Spotify Premium test account (email + password) that is *not* your personal one, with the note that Premium
  is required and that a free account shows the sign-in error by design.
- "Packaged desktop application (runFullTrust). Third-party Spotify client; streams the signed-in user's own
  Premium content; no purchases; privacy policy at cproducts.dev/privacy."
- Restricted capabilities: none besides runFullTrust once `packageManagement` is dropped.

Certification usually takes 1–3 business days; failures come back as a report citing the policy number. Appeals
and questions: reportapp@microsoft.com.

## 5. After publish

- The Store product page gets a **Store ID** (`9N…`); put the official "Get it from Microsoft" badge
  (<https://apps.microsoft.com/store/app-badge/>) beside the installer buttons in the README and on the site.
- Every later release: pack the two `store`-channel packages from the same commit as the feed release and submit
  them as a new submission; the Store distributes to its own users, the feed to sideloaded ones.
- Watch the Partner Center *Health* and *Reviews* pages; a rights-holder complaint arrives as a removal notice
  by email with an appeal window — respond within it.

## References

- Open a developer account (free flow, Company verification): learn.microsoft.com/windows/apps/publish/partner-center/open-a-developer-account
- Store Policies v7.19 (10.3.1 test account, 10.5.1 privacy, 10.14 account type, 11.2 IP): learn.microsoft.com/windows/apps/publish/store-policies
- App package requirements (Store signing, version rules, bundles): learn.microsoft.com/windows/apps/publish/publish-your-app/msix/app-package-requirements
- Screenshots and images: learn.microsoft.com/windows/apps/publish/publish-your-app/msix/screenshots-and-images
- Restricted capabilities (packageManagement, runFullTrust): learn.microsoft.com/windows/uwp/packaging/app-capability-declarations
