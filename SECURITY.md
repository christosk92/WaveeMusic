# Security Policy

## Supported versions

Wavee auto-updates every install to the latest release, so only the current minor line is supported.

| Version | Supported |
|---|---|
| Latest `0.2.x` (Microsoft Store) | ✔ |
| Latest `0.2.x` (direct download / sideload) | ✔ |
| Anything older | ✘ |

## Reporting a vulnerability

Use GitHub's private vulnerability reporting:
[github.com/christosk92/WaveeMusic/security/advisories/new](https://github.com/christosk92/WaveeMusic/security/advisories/new).

Do not open a public issue for a security report.

Please include:

- The Wavee version and build quad (Settings › About), e.g. `0.2.5.6`.
- Install source (Microsoft Store or direct/sideload) and architecture (x64 or ARM64).
- Steps to reproduce.

You should get an acknowledgement within 7 days.

## What not to send

Don't include Spotify credentials, OAuth tokens, `credentials.json`, or a full unredacted `wavee.log` in a
report. For a crash, a stack trace plus the version quad is enough — every release ships with a matching
symbols zip, so we can symbolicate from that alone.

## Scope

Wavee talks to Spotify's own services using the user's Premium account. Vulnerabilities in Spotify's services
themselves are out of scope for this repository. Issues in the rendering/window/input layer belong to the
FluentGpu engine's own security policy, not this one.

## Package signing

Releases are MSIX packages signed with Azure Trusted Signing. Verify the signature on the `.msix` before
sideloading a manually downloaded package.
