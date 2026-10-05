# AI lyrics pack: hosting runbook

On-device AI lyrics sync downloads a one-time pack: the ONNX Runtime and Qualcomm QNN runtime (two PyPI wheels) and the
separator and aligner models (about 700 MB for English). This guide covers where the models live, how to publish a new
pack, how the app's embedded manifest is generated, and how to check the hosting. Nothing in it is secret: the URLs are
public, the objects are immutable, and every hash is already compiled into the signed app.

The private `C:\WAVEE\wavee-dev-helpers` repo is not involved. It owns the PlayPlay runtime service only; the AI bucket
is not part of its CLI or catalog.

## Where things live

| What | Where |
|---|---|
| Bucket | Cloudflare R2 `wavee-ai-models` |
| Public origin | `https://models.cproducts.dev` (R2 custom domain) |
| Objects | `lyrics/v<N>/<file>`, or `lyrics/v<N>/<file>.partNN` for files over 64 MiB |
| Runtime wheels | PyPI (`files.pythonhosted.org`), pinned by exact URL, size and SHA-256 |
| Manifest (source of truth) | `ops/ai/lyrics-pack.v1.json` |
| Embedded manifest (generated) | `src/apps/Wavee/AiLyrics/AiLyrics.PackManifest.cs` |
| Cache key for alignment results | `AiLyrics.PackVersion` in `src/apps/Wavee/AiLyrics/AiLyrics.cs` |

**Parts.** A file larger than 64 MiB (67,108,864 bytes) is split into `<name>.part00`, `<name>.part01`, ... Every part
except the last is exactly 64 MiB. A smaller file is one object under its own name. The manifest lists each file with
its total size and SHA-256, and each part with its own size and SHA-256. The app downloads the parts with HTTP Range
resume, checks every part and then the whole file.

**Object metadata.** Every object is uploaded with:

- `Content-Type: application/octet-stream` (`.onnx` and parts) or `application/json` (`.json`)
- `Cache-Control: public, max-age=31536000, immutable`

## The immutability rule

An object under `lyrics/v<N>/` is never overwritten or deleted. Shipped apps carry its hash, and Cloudflare and clients
may cache it for a year. Any change to a model is a new pack:

1. Upload the new files under a new prefix, `lyrics/v<N+1>/`. Files that did not change are uploaded again under
   the new prefix; never point a manifest at another version's folder.
2. Add `ops/ai/lyrics-pack.v<N+1>.json` with `"version": N+1` and `"base": "https://models.cproducts.dev/lyrics/v<N+1>/"`.
3. Bump `AiLyrics.PackVersion` to `N+1`. It must equal the manifest's `version`; it also invalidates saved alignment
   results, which new models would time differently.
4. Regenerate the embedded manifest from the new file (below) and ship it in an app update.

Keep `lyrics/v<N>/` online for as long as any supported release embeds it.

A runtime-only change (a new ONNX Runtime or QNN wheel) does not touch the bucket. Edit the `runtime` entry in the
current manifest with the wheel's exact URL, `bytes` and `sha256` from PyPI
(`https://pypi.org/pypi/<package>/<version>/json`, field `urls[].digests.sha256` and `size`, the `win_arm64` wheel),
regenerate, and ship an app update. The app never resolves "latest" from PyPI.

## Publish a pack

Wrangler is installed in `C:\WAVEE\wavee-dev-helpers` and uses the operator's existing `wrangler login` (OAuth) session.
No token goes in this repo or in a command line.

1. Put the final objects (already split into parts) in one folder and record each file, part, size and SHA-256 in the
   new manifest. `Get-FileHash -Algorithm SHA256` gives the hash; the manifest stores it in lowercase.
2. Check that the new prefix is empty. This must answer `404`:

   ```powershell
   curl.exe -sI https://models.cproducts.dev/lyrics/v2/separator.onnx
   ```

3. Upload every object:

   ```powershell
   Set-Location C:\WAVEE\wavee-dev-helpers
   $src = 'C:\path\to\pack\lyrics\v2'          # the split objects
   Get-ChildItem $src -File | ForEach-Object {
       $type = if ($_.Extension -eq '.json') { 'application/json' } else { 'application/octet-stream' }
       bunx wrangler r2 object put "wavee-ai-models/lyrics/v2/$($_.Name)" --file $_.FullName --remote `
           --content-type $type --cache-control 'public, max-age=31536000, immutable'
       if ($LASTEXITCODE -ne 0) { throw "upload failed: $($_.Name)" }
   }
   ```

4. Run the full smoke check against the new manifest (below) before generating the app's manifest from it.

## Regenerate the embedded manifest

```powershell
powershell -File ops/ai/New-AiLyricsPackManifest.ps1 -Manifest ops/ai/lyrics-pack.v1.json -Out src/apps/Wavee/AiLyrics/AiLyrics.PackManifest.cs
```

The script embeds the JSON verbatim as a C# raw string under a fixed header. Its output is deterministic: a second run
prints `up to date` and writes nothing. Never edit the `.cs` by hand; the Pester tests fail when it differs from what the
script produces.

Offline tests (manifest schema, part layout, generator round trip):

```powershell
Invoke-Pester -Path ops/ai/tests
```

These are separate from `ops/release/tests` and are not a release gate.

## Smoke check

```powershell
powershell -File ops/ai/Test-AiLyricsPack.ps1          # seconds: HEAD + a 1 KB Range GET per object
powershell -File ops/ai/Test-AiLyricsPack.ps1 -Full    # about 1.4 GB: also SHA-256 of every part, file and wheel
```

For every model part it checks `HEAD` answers 200 with `Content-Length` equal to the manifest's size and a
`Cache-Control` that contains `immutable`, and that `Range: bytes=0-1023` answers 206 with the right `Content-Range`.
For every wheel it checks `HEAD` answers 200 with the right size. `-Full` streams everything and verifies the SHA-256 of
each part, each reassembled file and each wheel. It prints a table and exits 1 on any mismatch. It only reads; it never
changes the bucket. Pass `-Manifest` to check another pack version.

## End-to-end check

`Wavee.LyricsLab --ai` loads the installed pack on the NPU and aligns real tracks with it, printing load, prepare and
alignment timings. It runs the headless session against a copied profile and refuses the live one:

```powershell
dotnet run --project src/apps/Wavee.LyricsLab -c Release -- --headless --profile <scratch dir with a copy of store.json> `
    --out <output dir> --tracks <id1,id2,...> --ai <ai dir>
```

`<ai dir>` is a folder laid out like the app's `ai\lyrics\` (`runtime\` and `models\`, with both English and Spanish
installed), for example from a scratch-profile run of Wavee that downloaded the pack. Run it on a Snapdragon Copilot+ PC; there is no CPU or GPU path.
