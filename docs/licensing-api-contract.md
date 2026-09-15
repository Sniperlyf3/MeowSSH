# MeowSSH licensing API contract

The Android app never treats Google Play purchase state as proof of premium access. It sends active non-pending purchase tokens plus a Google Play Integrity token to the licensing service and accepts only a signed entitlement grant.

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
  ],
  "requestId": "fresh-random-base64url-value",
  "integrityNonce": "sha256-binding-as-base64url",
  "integrityToken": "opaque-google-play-integrity-token"
}
```

The server must fail closed unless all of the following succeed:

1. Recompute `integrityNonce` using the same length-prefixed canonicalization implemented by `LicensingApiGrantProvider.CreateIntegrityNonce` over package name, request ID, and sorted product/purchase-token pairs.
2. Decrypt and verify `integrityToken` using the Google Play Integrity server API. The decoded request nonce must exactly equal `integrityNonce`.
3. Require the expected package/application identity and acceptable app/device integrity verdicts. For production paid grants, require a genuine Play-recognized app installation unless an explicit support policy says otherwise.
4. Reject stale/replayed integrity responses and request IDs. The backend should store recently consumed request IDs for at least the accepted attestation freshness window.
5. Verify each purchase token with the Google Play Developer API and require the token to belong to `dev.sniperlyf3.meowssh` and the submitted product ID.
6. Derive the highest currently valid entitlement tier from the verified purchases. Never trust a tier supplied by the client.
7. Acknowledge eligible purchases where Google Play requires acknowledgement.
8. Sign the resulting grant with the private ECDSA P-256 key.

Play Integrity verdicts are opaque on-device. Decryption and policy decisions belong on the licensing backend; no response-encryption or signing secrets are shipped in MeowSSH.

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

The signing private key and Google service-account credentials must never be shipped in the app or committed to this repository.
