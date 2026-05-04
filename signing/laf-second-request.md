# LAF token request: production publisher

Send this to **`lafaccessrequests@microsoft.com`**, referencing the
existing case TrackingID **#2605010040005804** in the subject. Reply to
the original thread if you can — keeps Mike Bowen / Tom Jebo's context.

The values below are pre-computed for Wavee's manifest. The publisher
hash is derived from `Identity.Publisher` in `Package.appxmanifest`
using SHA-256 + the standard MSIX base32 encoding (verified on
2026-05-04 against the existing dev PFN).

---

## Subject

```
Re: LAF Access Request: ckarapasias@outlook.com — second token for production publisher (Case #2605010040005804)
```

## Body

```
Hi Mike,

Thanks again for the original LAF token issued on 2026-05-01 — it's
been working great for our development builds. I'm following up to
request a second token for the same Phi Silica feature, against our
new production publisher.

Wavee is now being signed with Azure Artifact Signing (Trusted
Signing) for distribution. The Artifact Signing certificate profile
issues a different Subject Name than the self-signed cert we used in
development, so the Package Family Name changes — which means the
existing token (4+g4v/xx6B81Wc6Z0sO0bg==) won't unlock for production-
signed packages. Per your earlier guidance that LAF tokens are
cryptographically bound to the publisher signature, I assume the
correct path is to request a fresh token for the new PFN.

Details:

  Request type:        Additional access request (existing customer)
  Original case:       TrackingID #2605010040005804
  Requester name:      Christos Karapasias
  Organization:        cproducts
  Email:               ckarapasias@outlook.com

  API:                 Phi Silica API (Packaged App Only)
  Feature ID:          com.microsoft.windows.ai.languagemodel
  Doc URL:             https://learn.microsoft.com/windows/ai/apis/phi-silica
  App type:            Packaged

  Production publisher (Artifact Signing Subject Name):
    CN=cproducts, O=cproducts, L=Utrecht, S=Utrecht, C=NL

  Production Package Family Name:
    4f5ca249-aa41-4d46-84ce-62b6098fdb06_43x7j183z9t4g

  (For reference — the original dev PFN that already has a token:
   4f5ca249-aa41-4d46-84ce-62b6098fdb06_s6dvdzhx5m6rm)

Use case is unchanged from the original request: optional, on-device
lyrics assistance on Copilot+ PCs (line-meaning explanation and short
"lyrics meaning" interpretations) using
Microsoft.Windows.AI.Text.LanguageModel. The feature stays opt-in,
nothing leaves the device, no lyric text or model output is sent to
cloud services.

Happy to provide the Artifact Signing account details, the Azure
subscription ID, or a signed test MSIX if it helps verify the
publisher identity.

Thanks!
Christos
```

---

## After Microsoft replies with the new token

The reply will look like the original — a token value plus a
suggested usage block. Once you have it:

1. **Don't replace** the existing token. Keep both.
2. Update `Wavee.UI.WinUI/Services/AiCapabilities.cs` so
   `TryUnlockLanguageModelLafCore` picks the right token at runtime
   based on `Package.Current.Id.Publisher`:

   ```csharp
   private const string LanguageModelLafTokenDev =
       "4+g4v/xx6B81Wc6Z0sO0bg==";  // CN=ckara (dev publisher)
   private const string LanguageModelLafTokenProd =
       "<NEW TOKEN HERE>";          // CN=cproducts, ... (Artifact Signing)

   private const string LanguageModelLafAttributionDev =
       "s6dvdzhx5m6rm has registered their use of …";
   private const string LanguageModelLafAttributionProd =
       "<NEW ATTRIBUTION HERE>";    // Microsoft assigns this in the reply

   private static (string token, string attribution) ResolveLafCredentials()
   {
       var publisher = Package.Current.Id.Publisher;  // matches manifest
       if (publisher.StartsWith("CN=cproducts", StringComparison.Ordinal))
           return (LanguageModelLafTokenProd, LanguageModelLafAttributionProd);
       return (LanguageModelLafTokenDev, LanguageModelLafAttributionDev);
   }
   ```

   Then call `ResolveLafCredentials()` from `TryUnlockLanguageModelLafCore`
   instead of using the consts directly.

3. Both tokens may be committed to source per Microsoft's confirmation
   on 2026-05-01 (Mike Bowen): tokens are bound to the publisher
   signature, so a fork can't reuse them with a different cert.

4. Verify after deployment:
   - Install the production-signed MSIX
   - Open Settings → On-device AI
   - The toggle should be available; flipping it on should unlock
     `LanguageModel.CreateAsync()` without throwing.
   - If `TryUnlockFeature` still returns `Unavailable`, double-check
     that the manifest's `Identity.Publisher` matches what Mike used
     to issue the token.
