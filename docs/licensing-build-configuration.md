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

If either property is omitted, the existing licensing provider remains fail-closed and does not mint or infer a paid entitlement locally.

After deploying MeowSSHAPI, obtain the public key from `GET /v1/entitlements/public-key` and use the returned `subjectPublicKeyInfoBase64` value for the app build. The API URL must be HTTPS.

For GitHub Actions release builds, store these values as repository/environment **variables**, not secrets: they are intentionally embedded in the APK and therefore are not confidential. The API signing private key and Google credentials must never be supplied to the app build.
