# Google Play release handoff

The production Play workflow is `.github/workflows/play-release.yml`.

It builds the signed AAB first, verifies package contents and native-library alignment, and can optionally publish that exact bundle to the Play internal testing track.

## Production environment values

Configure these on the GitHub `production` environment for `Sniperlyf3/MeowSSH`.

### Android upload signing secrets

- `ANDROID_KEYSTORE_BASE64`
- `ANDROID_KEY_ALIAS`
- `ANDROID_KEY_PASSWORD`
- `ANDROID_STORE_PASSWORD`

The keystore is the Play **upload** key. Play App Signing remains the distribution signing identity; its SHA-256 certificate fingerprint must also match the pin configured in MeowSSHAPI.

### Public release configuration

- `LICENSING_API_BASE_URL`
- `LICENSING_PUBLIC_KEY_SUBJECT_PUBLIC_KEY_INFO_BASE64`
- `PRIVACY_POLICY_URL`
- `SUPPORT_URL`

The workflow requires privacy/support to be public HTTPS URLs and performs a real HTTP fetch before release. `PRIVACY.md` must no longer contain the launch contact placeholder.

### Keyless Google Play publication

When `publish_internal=true`, configure:

- `GCP_WORKLOAD_IDENTITY_PROVIDER`
- `GCP_PLAY_PUBLISH_SERVICE_ACCOUNT`

Do **not** create or add `GOOGLE_PLAY_SERVICE_ACCOUNT_JSON`. The release workflow intentionally uses GitHub OIDC and Google Workload Identity Federation to create short-lived credentials.

The Workload Identity provider/pool must trust workflows from **`Sniperlyf3/MeowSSH`**. A provider whose attribute condition only permits `Sniperlyf3/MeowSSHAPI` will not work for app publication even if the provider resource name is otherwise valid.

The GitHub principal needs permission to impersonate `GCP_PLAY_PUBLISH_SERVICE_ACCOUNT` through the selected Workload Identity provider. Keep the service account dedicated to release automation when practical.

That same service-account email must be invited in Google Play Console and granted only the Play permissions required to upload and manage releases for `dev.sniperlyf3.meowssh`.

## First real publication proof

Before production launch:

1. Create/configure `dev.sniperlyf3.meowssh` in Play Console and enable Play App Signing.
2. Create the lifetime product exactly as documented in `docs/google-play-products.md`.
3. Deploy MeowSSHAPI and copy its production URL/public entitlement key into the GitHub production environment variables above.
4. Publish a signed build with `publish_internal=true`.
5. Install it from the Play internal testing track; sideloaded builds are not a substitute for validating Play Billing/Integrity behavior.
6. Complete a real `meowssh_pro_lifetime` purchase with a licensed internal tester.
7. Confirm server verification, acknowledgement, signed entitlement issuance and Pro unlock.
8. Confirm restore on a clean install/device.
9. Re-run the refund/revocation path against the real Play environment before widening rollout.

## Release invariants enforced by workflow

- production signing inputs must exist;
- public privacy/support destinations must exist and be reachable;
- the unresolved privacy contact placeholder blocks release;
- Pro Cloud new sales remain disabled;
- full-device Tailcat VPN is excluded unless explicitly requested;
- `arm64-v8a` and `x86_64` Meowshell/Tailcat libraries must be present;
- VPN-excluded bundles must not contain the HEV tunnel library;
- every packaged native `.so` must have `PT_LOAD` alignment of at least 16 KiB;
- third-party notices must remain packaged;
- Play publication uses short-lived Workload Identity credentials, not a service-account private key.
