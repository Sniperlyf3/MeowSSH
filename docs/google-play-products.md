# Google Play product contract

MeowSSH's paid Android catalog is intentionally small and must be created with these identifiers before production builds are published.

## One-time product

- Product ID: `meowssh_pro_lifetime`
- Type: one-time product
- Entitlement: `Pro`
- Do not consume this purchase. The licensing backend verifies and acknowledges it.

## Subscription

- Product ID: `meowssh_pro_cloud`
- Type: subscription
- Entitlement: `ProCloud`

Create two auto-renewing base plans under this one subscription product:

| Base plan ID | Billing period | App label |
| --- | --- | --- |
| `monthly` | 1 month | Monthly |
| `yearly` | 1 year | Yearly |

Pricing is not hard-coded in MeowSSH. The Android app queries Google Play and renders the localized eligible price returned for each base plan.

A subscription offer may exist under either base plan. The app prefers the base-plan offer when available and otherwise uses an eligible offer for that base plan. The selected offer token is supplied to Google Play's billing flow; the backend does not trust the base-plan choice as proof of entitlement.

## Backend contract

`MeowSSHAPI` should be configured with:

- `Licensing__ProLifetimeProductId=meowssh_pro_lifetime`
- `Licensing__ProCloudProductId=meowssh_pro_cloud`

The backend verifies active purchases with Google Play. Billing period and offer eligibility remain Google-owned state; a valid `meowssh_pro_cloud` subscription maps to `ProCloud` regardless of whether the active base plan is monthly or yearly.

## Play Integrity and signing

The Android package name is `dev.sniperlyf3.meowssh`.

Before enforcing certificate allow-listing in the backend, add the SHA-256 digest of the Play App Signing certificate to `Licensing__AllowedCertificateSha256Digests`. Keep `RequireDeviceIntegrity` disabled until the custom-ROM/root policy is deliberately chosen and tested.

The entitlement signing private key and Google service-account credentials are server-only. The app receives only the HTTPS licensing API URL and ECDSA public key through build properties.
