# Licensing build configuration

MeowSSH treats the licensing API URL and entitlement verification public key as public build configuration. The entitlement signing private key remains server-only.

The Android app accepts these MSBuild properties:

- `LicensingApiBaseUrl`
- `LicensingPublicKeySubjectPublicKeyInfoBase64`

Example release build:

```sh
dotnet publish src/MeowSSH.App/MeowSSH.App.csproj \
  -c Release -f net10.0-android36.0 \
  -p:RuntimeIdentifier=android-arm64 \
  -p:LicensingApiBaseUrl=https://license.example.com/ \
  -p:LicensingPublicKeySubjectPublicKeyInfoBase64='<base64-spki>'
```

If either property is omitted, the existing licensing provider remains fail-closed and does not mint or infer a paid entitlement locally. The client also treats a non-HTTPS API URI as unconfigured, so paid entitlement verification cannot be sent over plaintext HTTP.

After deploying MeowSSHAPI, obtain the public key from `GET /v1/entitlements/public-key` and use the returned `subjectPublicKeyInfoBase64` value for the app build. The API URL must be HTTPS.

The production `Play Release` workflow applies stricter release-time gates before the signed AAB is built:

- `LICENSING_API_BASE_URL` must use HTTPS;
- `${LICENSING_API_BASE_URL}/readyz` must respond successfully;
- `${LICENSING_API_BASE_URL}/v1/entitlements/public-key` must report `ECDSA_P256_SHA256`;
- its `subjectPublicKeyInfoBase64` must exactly match `LICENSING_PUBLIC_KEY_SUBJECT_PUBLIC_KEY_INFO_BASE64`.

This prevents a production bundle from being signed with a URL that the app would later reject, a licensing service that is not ready, or a stale/mistyped entitlement verification key that cannot validate the backend's signed grants.

For GitHub Actions release builds, store these values as repository/environment **variables**, not secrets: they are intentionally embedded in the APK and therefore are not confidential. The API signing private key and Google credentials must never be supplied to the app build.
