# MeowSSH licensing API contract

The Android app never treats Google Play purchase state as proof of premium access. It sends active non-pending purchase tokens to the licensing service and accepts only a signed entitlement grant.

## Verify Google Play purchases

`POST /v1/entitlements/google-play/verify`

Request:

```json
{
  "packageName": "dev.sniperlyf3.meowssh",
  "purchases": [
    {
      "productId": "meowssh_pro_lifetime",
      "purchaseToken": "..."
    }
  ]
}
```

The server must verify each token with the Google Play Developer API, derive the highest valid tier, acknowledge eligible purchases as required by Google Play, and sign the resulting grant with the private ECDSA P-256 key.

Response:

```json
{
  "payloadBase64": "...",
  "signatureBase64": "..."
}
```

The decoded payload is UTF-8 JSON:

```json
{
  "grantId": "...",
  "packageName": "dev.sniperlyf3.meowssh",
  "tier": 1,
  "issuedAtUtc": "2026-09-15T12:00:00Z",
  "validUntilUtc": "2026-09-22T12:00:00Z"
}
```

The signature is ECDSA P-256/SHA-256 over the exact decoded payload bytes. The app contains only the public SPKI key and rejects invalid signatures, wrong package names, future-issued grants, expired grants, and invalid paid tiers.

The signing private key must never be shipped in the app or committed to this repository.
