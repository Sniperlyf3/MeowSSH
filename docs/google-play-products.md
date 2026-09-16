# Google Play product contract

MeowSSH's paid Android catalog is intentionally small and must use these identifiers.

## One-time product — launch product

- Product ID: `meowssh_pro_lifetime`
- Type: one-time product
- Entitlement: `Pro`
- Do not consume this purchase. The licensing backend verifies and acknowledges it.

This is the paid product intended for the first public launch.

## Subscription — provision now, do not sell yet

- Product ID: `meowssh_pro_cloud`
- Type: subscription
- Entitlement: `ProCloud`

Create two auto-renewing base plans under this one subscription product when preparing the future cloud tier:

| Base plan ID | Billing period | App label |
| --- | --- | --- |
| `monthly` | 1 month | Monthly |
| `yearly` | 1 year | Yearly |

**Do not make Pro Cloud a launch offer until the cloud sync/backup product is actually shipping.** The Android production build currently fails closed with `EnableProCloudSales=false`: it does not query/display new Pro Cloud offers and rejects a direct Pro Cloud purchase attempt. It still queries existing subscription purchases during restore so test/legacy entitlements are not orphaned.

When cloud functionality is ready, enabling subscription sales must be a deliberate product change with privacy/Data Safety updates, cloud lifecycle tests and a release-workflow change. Do not activate it merely by changing Play Console state.

Pricing is not hard-coded in MeowSSH. When Pro Cloud sales are eventually enabled, the Android app queries Google Play and renders the localized eligible price returned for each base plan.

A subscription offer may exist under either base plan. The app prefers the base-plan offer when available and otherwise uses an eligible offer for that base plan. The selected offer token is supplied to Google Play's billing flow; the backend does not trust the base-plan choice as proof of entitlement.

## Backend contract

`MeowSSHAPI` should be configured with:

- `Licensing__ProLifetimeProductId=meowssh_pro_lifetime`
- `Licensing__ProCloudProductId=meowssh_pro_cloud`

The backend verifies active purchases with Google Play. Billing period and offer eligibility remain Google-owned state; a valid `meowssh_pro_cloud` subscription maps to `ProCloud` regardless of whether the active base plan is monthly or yearly.

Keeping backend support for Pro Cloud does not make the tier sellable in the Android client. This separation allows existing/test subscriptions to restore correctly while new sales remain disabled.

## Play Integrity and signing

The Android package name is `dev.sniperlyf3.meowssh`.

Before enforcing certificate allow-listing in the backend, add the SHA-256 fingerprint of the **Play App Signing** certificate to `Licensing__AllowedCertificateSha256Digests`, not the local upload certificate. MeowSSHAPI accepts the human-readable hexadecimal SHA-256 fingerprint copied from Play Console and normalizes it to the URL-safe Base64 digest returned by Play Integrity.

Keep `RequireDeviceIntegrity` disabled until the custom-ROM/root policy is deliberately chosen and tested.

The entitlement signing private key and Google service-account credentials are server-only. The app receives only the HTTPS licensing API URL and ECDSA public key through build properties.
