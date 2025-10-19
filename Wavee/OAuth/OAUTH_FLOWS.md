# Spotify OAuth 2.0 Authentication Flows - Technical Analysis

This document provides a detailed technical analysis of the two OAuth 2.0 flows supported by Spotify for obtaining access tokens.

## Table of Contents

1. [Overview](#overview)
2. [Authorization Code Flow with PKCE](#authorization-code-flow-with-pkce)
3. [Device Code Flow](#device-code-flow)
4. [Comparison](#comparison)
5. [Token Refresh](#token-refresh)
6. [Security Considerations](#security-considerations)

---

## Overview

Both flows ultimately provide the same result: an **OAuth token** containing:
- **access_token**: Bearer token for API authentication (expires in 1 hour)
- **refresh_token**: Long-lived token for obtaining new access tokens
- **token_type**: "Bearer"
- **expires_in**: Token lifetime in seconds (typically 3600)
- **scope**: Space-separated list of granted permissions

### Common Endpoints

**Authorization**: `https://accounts.spotify.com/authorize`
**Token Exchange**: `https://accounts.spotify.com/api/token`
**Device Authorization**: `https://accounts.spotify.com/oauth2/device/authorize`

---

## Authorization Code Flow with PKCE

### What is it?

OAuth 2.0 Authorization Code Flow enhanced with **PKCE** (Proof Key for Code Exchange, RFC 7636). This is the standard web-based OAuth flow adapted for native/desktop applications.

### Flow Diagram

```
┌─────────┐                                  ┌──────────┐
│  Your   │                                  │ Spotify  │
│   App   │                                  │  Server  │
└────┬────┘                                  └────┬─────┘
     │                                            │
     │  1. Generate PKCE verifier & challenge    │
     │     verifier = random(43-128 chars)       │
     │     challenge = SHA256(verifier)          │
     │                                            │
     │  2. Open browser to authorization URL     │
     │────────────────────────────────────────>  │
     │  ?client_id=xxx                            │
     │  &response_type=code                       │
     │  &redirect_uri=http://localhost:8898       │
     │  &code_challenge=xxx                       │
     │  &code_challenge_method=S256               │
     │  &scope=streaming user-read-playback...   │
     │                                            │
     │           [User authorizes in browser]     │
     │                                            │
     │  3. Redirect to callback with code         │
     │  <────────────────────────────────────────│
     │  http://localhost:8898/callback?code=xxx   │
     │                                            │
     │  4. Exchange code for token                │
     │────────────────────────────────────────>  │
     │  POST /api/token                           │
     │  grant_type=authorization_code             │
     │  code=xxx                                  │
     │  redirect_uri=http://localhost:8898        │
     │  client_id=xxx                             │
     │  code_verifier=<original verifier>         │
     │                                            │
     │  5. Receive tokens                         │
     │  <────────────────────────────────────────│
     │  {                                         │
     │    "access_token": "...",                  │
     │    "refresh_token": "...",                 │
     │    "expires_in": 3600                      │
     │  }                                         │
     │                                            │
```

### Step-by-Step Process

#### Step 1: Generate PKCE Parameters

**Purpose**: Prevent authorization code interception attacks

**Code Verifier**:
- Random string: 43-128 characters
- Characters: `[A-Z][a-z][0-9]-._~`
- Example: `dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk`

**Code Challenge**:
- SHA256 hash of verifier, base64url encoded
- Example: `E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM`

#### Step 2: Authorization Request

**URL Format**:
```
https://accounts.spotify.com/authorize
  ?client_id=65b708073fc0480ea92a077233ca87bd
  &response_type=code
  &redirect_uri=http://localhost:8898/callback
  &code_challenge_method=S256
  &code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM
  &scope=streaming user-read-playback-state user-modify-playback-state
  &state=<random-csrf-token>
```

**Required Parameters**:
- `client_id`: Your Spotify application client ID
- `response_type`: Must be "code"
- `redirect_uri`: Must match registered URI (typically localhost for desktop apps)
- `code_challenge`: Base64url-encoded SHA256 hash of verifier
- `code_challenge_method`: Must be "S256"
- `scope`: Space-separated list of permissions

**Optional Parameters**:
- `state`: Random string for CSRF protection (recommended)
- `show_dialog`: Force consent screen even if previously authorized

**Action**: Open this URL in the user's default browser

#### Step 3: User Authorization

**What happens**:
1. User logs into Spotify (if not already)
2. Consent screen shows requested permissions
3. User clicks "Authorize" or "Deny"
4. Spotify redirects to your `redirect_uri` with authorization code

**Success Redirect**:
```
http://localhost:8898/callback
  ?code=AQBPj_JvEe...
  &state=<same-csrf-token>
```

**Error Redirect**:
```
http://localhost:8898/callback
  ?error=access_denied
  &error_description=The+user+denied+the+request
  &state=<same-csrf-token>
```

#### Step 4: HTTP Callback Server

**Requirements**:
- Your app must run a local HTTP server on the redirect URI port
- Listen for incoming connection on specified port (e.g., 8898)
- Parse query string to extract `code` parameter
- Return HTML success page to browser
- Shutdown server after receiving code

#### Step 5: Token Exchange

**Request**:
```http
POST /api/token HTTP/1.1
Host: accounts.spotify.com
Content-Type: application/x-www-form-urlencoded

grant_type=authorization_code
&code=AQBPj_JvEe...
&redirect_uri=http://localhost:8898/callback
&client_id=65b708073fc0480ea92a077233ca87bd
&code_verifier=dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk
```

**Success Response** (200 OK):
```json
{
  "access_token": "BQD...very-long-token...xyz",
  "token_type": "Bearer",
  "expires_in": 3600,
  "refresh_token": "AQC...refresh-token...xyz",
  "scope": "streaming user-read-playback-state user-modify-playback-state"
}
```

**Error Response** (400 Bad Request):
```json
{
  "error": "invalid_grant",
  "error_description": "Authorization code expired"
}
```

### Advantages

✅ Industry standard OAuth 2.0 flow
✅ PKCE provides strong security without client secret
✅ Works with any browser
✅ Immediate feedback (browser redirect)

### Disadvantages

❌ Requires HTTP server on localhost
❌ Port binding can fail (already in use, permissions)
❌ Browser must be on same machine
❌ Firewall/antivirus may block
❌ User might close browser before authorizing

### When to Use

- Desktop applications with browser access
- Development/testing scenarios
- Apps running on user's local machine
- When immediate authorization is needed

---

## Device Code Flow

### What is it?

OAuth 2.0 Device Authorization Grant (RFC 8628). Designed for devices with limited input capabilities (smart TVs, IoT) but works perfectly for console/terminal applications.

### Flow Diagram

```
┌─────────┐                                  ┌──────────┐
│  Your   │                                  │ Spotify  │
│   App   │                                  │  Server  │
└────┬────┘                                  └────┬─────┘
     │                                            │
     │  1. Request device code                    │
     │────────────────────────────────────────>  │
     │  POST /oauth2/device/authorize             │
     │  client_id=xxx                             │
     │  scope=streaming...                        │
     │                                            │
     │  2. Receive codes and verification URI     │
     │  <────────────────────────────────────────│
     │  {                                         │
     │    "device_code": "MlpHVlRKLDM2...",       │
     │    "user_code": "2ZGVTJ",                  │
     │    "verification_uri": "spotify.com/pair", │
     │    "expires_in": 3599,                     │
     │    "interval": 5                           │
     │  }                                         │
     │                                            │
     │  3. Display code to user                   │
     │  ┌──────────────────────────────┐          │
     │  │ Visit: spotify.com/pair      │          │
     │  │ Enter code: 2ZGVTJ           │          │
     │  └──────────────────────────────┘          │
     │                                            │
     │  [User visits URL on ANY device]           │
     │  [User enters code and authorizes]         │
     │                                            │
     │  4. Poll for token (every 5 seconds)       │
     │────────────────────────────────────────>  │
     │  POST /api/token                           │
     │  grant_type=urn:ietf:params:oauth:...     │
     │  device_code=MlpHVlRKLDM2...               │
     │  client_id=xxx                             │
     │                                            │
     │  While pending: 400 authorization_pending  │
     │  <────────────────────────────────────────│
     │  { "error": "authorization_pending" }      │
     │                                            │
     │  ... wait 5 seconds ...                    │
     │                                            │
     │  4. Poll again                             │
     │────────────────────────────────────────>  │
     │                                            │
     │  When authorized: 200 OK with tokens       │
     │  <────────────────────────────────────────│
     │  {                                         │
     │    "access_token": "...",                  │
     │    "refresh_token": "...",                 │
     │    "expires_in": 3600                      │
     │  }                                         │
     │                                            │
```

### Step-by-Step Process

#### Step 1: Request Device Code

**Request**:
```http
POST /oauth2/device/authorize HTTP/1.1
Host: accounts.spotify.com
Content-Type: application/x-www-form-urlencoded
User-Agent: Spotify/127400477 Win32_x86_64/0 (PC desktop)
Client-Token: AADMC6MH9Evb/rWKJgvy6+cf2+qTmWMqg...

client_id=65b708073fc0480ea92a077233ca87bd
&scope=streaming user-read-playback-state user-modify-playback-state
&creation_point=https://login.app.spotify.com/
&intent=login
```

**Important Headers**:
- `User-Agent`: Identifies your application platform
- `Client-Token`: Application-specific token (optional but recommended)
- `Spotify-Installation-Id`: Unique device identifier (optional)

**Required Parameters**:
- `client_id`: Your Spotify application client ID
- `scope`: Space-separated or comma-separated permissions

**Response** (200 OK):
```json
{
  "device_code": "MlpHVlRKLDM2Zjg0YmUzLTAwYWQtNDQ4ZC1hMmI0LWY4Njc2M2NlNjc4OA==",
  "user_code": "2ZGVTJ",
  "verification_uri": "https://spotify.com/pair",
  "verification_uri_complete": "https://spotify.com/pair?code=2ZGVTJ",
  "expires_in": 3599,
  "interval": 5
}
```

**Response Fields**:
- `device_code`: Opaque token for your app (don't show to user)
- `user_code`: Short code for user to enter (e.g., "2ZGVTJ")
- `verification_uri`: URL where user goes to authorize
- `verification_uri_complete`: Direct link with code pre-filled (perfect for QR codes!)
- `expires_in`: Seconds until code expires (typically ~1 hour)
- `interval`: Minimum seconds between polling requests (typically 5)

#### Step 2: Display to User

**Console Output Example**:
```
┌─────────────────────────────────────┐
│  Spotify Authorization Required     │
├─────────────────────────────────────┤
│  Visit: https://spotify.com/pair    │
│  Enter code: 2ZGVTJ                 │
│                                     │
│  Waiting for authorization...       │
│  (Code expires in 59:59)            │
└─────────────────────────────────────┘
```

**UI App with QR Code**:
- Generate QR code from `verification_uri_complete`
- Display QR code + user code
- Show countdown timer using `expires_in`
- User scans QR with phone → automatically authorized

#### Step 3: Poll for Token

**Polling Rules**:
- Wait `interval` seconds BEFORE first poll
- Continue polling until success, error, or expiration
- Respect `interval` (default 5 seconds)
- Increase interval if server returns `slow_down` error

**Request**:
```http
POST /api/token HTTP/1.1
Host: accounts.spotify.com
Content-Type: application/x-www-form-urlencoded
User-Agent: Spotify/127400477 Win32_x86_64/0 (PC desktop)
Client-Token: AADMC6MH9Evb/rWKJgvy6+cf2+qTmWMqg...

grant_type=urn:ietf:params:oauth:grant-type:device_code
&device_code=MlpHVlRKLDM2Zjg0YmUzLTAwYWQtNDQ4ZC1hMmI0LWY4Njc2M2NlNjc4OA==
&client_id=65b708073fc0480ea92a077233ca87bd
```

**Pending Response** (400 Bad Request):
```json
{
  "error": "authorization_pending"
}
```
**Action**: Wait `interval` seconds, poll again

**Slow Down Response** (400 Bad Request):
```json
{
  "error": "slow_down"
}
```
**Action**: Increase interval by 5 seconds, continue polling

**Expired Response** (400 Bad Request):
```json
{
  "error": "expired_token"
}
```
**Action**: Code expired, request new device code (step 1)

**Denied Response** (400 Bad Request):
```json
{
  "error": "access_denied"
}
```
**Action**: User declined authorization, abort

**Success Response** (200 OK):
```json
{
  "access_token": "BQBW2DKCfMLVGmiZNk2sBvJG9tV9DnSxUKul7e0rJX_Bv...",
  "token_type": "Bearer",
  "expires_in": 3600,
  "refresh_token": "AQCggQrwtjALSAHAvQ9Qw6yVuV0x_BFegDV4qtRU7mo...",
  "scope": "streaming user-read-playback-state user-modify-playback-state"
}
```
**Action**: Authorization complete! Save tokens.

### Advantages

✅ No HTTP server needed
✅ No port binding issues
✅ User can authorize from ANY device (phone, tablet, other computer)
✅ Perfect for console/terminal applications
✅ Great for headless/SSH scenarios
✅ Easy QR code integration for UI apps
✅ Works behind firewalls/proxies
✅ More reliable than browser-based flow

### Disadvantages

❌ Requires polling (network overhead)
❌ Slightly longer auth time (user must switch devices)
❌ Code can expire if user is slow

### When to Use

- Console/terminal applications
- Headless servers (SSH access)
- Apps where user prefers mobile authorization
- Scenarios with firewall restrictions
- Cross-device authorization (authorize on phone, use on PC)
- Applications with QR code UI

---

## Comparison

| Feature | Authorization Code + PKCE | Device Code |
|---------|---------------------------|-------------|
| **Complexity** | Medium | Low |
| **HTTP Server** | Required | Not needed |
| **Port Binding** | Yes (can fail) | No |
| **Browser** | Must be on same machine | Any device |
| **Network** | Single request + redirect | Polling (multiple requests) |
| **Auth Time** | ~5-10 seconds | ~15-30 seconds |
| **QR Code** | Not suitable | Perfect |
| **Headless** | Difficult | Excellent |
| **Firewall** | May block | No issues |
| **UX** | Browser popup | Display code |
| **Security** | PKCE | Device code binding |

### Recommended Usage

**Use Authorization Code when**:
- Building desktop GUI application
- User expects immediate browser-based auth
- Running on user's local machine with browser

**Use Device Code when**:
- Building console/CLI application
- User might authorize from different device
- Running on headless server
- Need QR code functionality
- Firewall/network restrictions
- Simplicity is important

---

## Token Refresh

Both flows return a **refresh_token** that can be used to obtain new access tokens without re-authorization.

### Refresh Request

```http
POST /api/token HTTP/1.1
Host: accounts.spotify.com
Content-Type: application/x-www-form-urlencoded

grant_type=refresh_token
&refresh_token=AQCggQrwtjALSAHAvQ9Qw6yVuV0x_BFeg...
&client_id=65b708073fc0480ea92a077233ca87bd
```

### Refresh Response

**Success** (200 OK):
```json
{
  "access_token": "BQBW2DKCfMLVGmiZNk2sBvJG...",
  "token_type": "Bearer",
  "expires_in": 3600,
  "scope": "streaming user-read-playback-state"
}
```

**Note**: New refresh tokens are typically NOT returned. Reuse the original refresh_token.

**Error** (400 Bad Request):
```json
{
  "error": "invalid_grant",
  "error_description": "Refresh token revoked"
}
```

### Refresh Token Lifetime

- Refresh tokens do not expire
- Can be revoked by user at accounts.spotify.com
- Revoked if not used for extended period (months)
- Store securely (encrypted on disk)

### Best Practices

1. **Always refresh proactively**: Refresh before access token expires (e.g., at 50 minutes)
2. **Handle refresh failures**: If refresh fails, fall back to full authorization
3. **Secure storage**: Encrypt refresh tokens, never commit to source control
4. **Single refresh token**: Each user session has one refresh token
5. **Retry logic**: Network failures during refresh should retry

---

## Security Considerations

### Authorization Code Flow

**PKCE (Proof Key for Code Exchange)**:
- Prevents authorization code interception
- Code verifier proves you initiated the request
- Even if code is stolen, attacker can't exchange it without verifier

**State Parameter**:
- Random value sent in authorization request
- Must match in callback response
- Prevents CSRF attacks

**Redirect URI Validation**:
- Must exactly match registered URI
- Prevents authorization code theft via redirect manipulation

**Localhost Binding**:
- Use `http://localhost` not `http://127.0.0.1`
- Register all possible ports if dynamic port assignment

### Device Code Flow

**Code Uniqueness**:
- User codes are short but unique within validity period
- Device codes are long and cryptographically secure

**Code Lifetime**:
- Codes expire after ~1 hour
- Minimizes window for brute force attacks

**Rate Limiting**:
- Polling too frequently triggers `slow_down`
- Failed authorization attempts may temporarily block device

### Both Flows

**Access Token Security**:
- Short-lived (1 hour)
- Bearer token - anyone with token has access
- Never log or expose in URLs
- Use HTTPS for all API calls

**Refresh Token Security**:
- Long-lived, more powerful than access token
- Store encrypted on disk
- Rotate periodically if possible
- Revoke immediately if compromise suspected

**Client ID Security**:
- Not secret (safe to include in code)
- But should not be widely publicized
- Use different client IDs for different apps/platforms

**Scope Principle**:
- Request minimum scopes needed
- User can see all requested permissions
- More scopes = more likely user will deny

---

## Error Handling

### Common Errors

| Error Code | Description | Action |
|------------|-------------|--------|
| `invalid_client` | Invalid client_id | Check client ID configuration |
| `invalid_grant` | Code expired/invalid | Restart authorization flow |
| `unauthorized_client` | Client not allowed | Check app settings in dashboard |
| `invalid_scope` | Scope not allowed | Remove invalid scopes |
| `access_denied` | User declined | Respect user choice, don't retry immediately |
| `server_error` | Spotify internal error | Retry with exponential backoff |

### Retry Strategy

**Transient Errors** (network, server_error):
- Retry with exponential backoff
- Max 3 retries
- Delays: 1s, 2s, 4s

**Permanent Errors** (invalid_client, invalid_scope):
- Don't retry
- Log error and notify developer

**User Errors** (access_denied):
- Don't retry automatically
- Let user choose to retry manually

---

## Testing

### Test Client IDs

Spotify provides test client IDs for development. Check documentation for current test credentials.

### Testing Device Code Flow

1. Request device code
2. Note the `user_code` and `verification_uri`
3. Open incognito browser to verification URI
4. Enter code and authorize
5. Verify polling succeeds

### Testing Authorization Code Flow

1. Start local HTTP server on redirect URI port
2. Open authorization URL
3. Authorize in browser
4. Verify callback received
5. Exchange code for token

### Mock Servers

For unit testing, mock Spotify endpoints:
- `/oauth2/device/authorize` → Return test device code
- `/api/token` → Return test access token
- Simulate errors (expired_token, slow_down, etc.)

---

## References

- [OAuth 2.0 RFC 6749](https://tools.ietf.org/html/rfc6749)
- [PKCE RFC 7636](https://tools.ietf.org/html/rfc7636)
- [Device Code RFC 8628](https://tools.ietf.org/html/rfc8628)
- [Spotify Authorization Guide](https://developer.spotify.com/documentation/general/guides/authorization-guide/)
