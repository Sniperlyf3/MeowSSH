#!/usr/bin/env bash
# Runs scripts/play-store-bootstrap.sh twice in a temporary directory and
# checks what it leaves behind. Needs keytool and openssl; no network (the
# release gates it reports on are expected to be open here).
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
script="$repo_root/scripts/play-store-bootstrap.sh"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
out="$tmp/out"

fail() { echo "play-store bootstrap test: $*" >&2; exit 1; }
run() { bash "$script" --out-dir "$out" "$@" 2>&1 | sed 's/\x1b\[[0-9;]*m//g'; }

first="$(run --api-url https://api.meowssh.invalid --licensing-public-key TUZrd0V3 --ci-signing-key)"

for name in meowssh-upload.p12 meowssh-upload.properties meowssh-ci.p12 github-production-secrets.env \
    github-production-variables.env github-ci-secrets.env; do
    [[ "$(stat -c %a "$out/$name")" == 600 ]] || fail "$name is missing or not mode 600"
done
[[ "$(stat -c %a "$out")" == 700 ]] || fail "the output directory is not mode 700"

password="$(sed -n 's/^storePassword=//p' "$out/meowssh-upload.properties")"
keytool -list -keystore "$out/meowssh-upload.p12" -storepass "$password" -alias meowssh-upload >/dev/null 2>&1 \
    || fail "the upload keystore does not open with its recorded password"
keytool -list -v -keystore "$out/meowssh-upload.p12" -storepass "$password" 2>/dev/null | grep -q 'RSA.*4096\|4096-bit RSA' \
    || fail "the upload key is not RSA 4096"

# What the workflow decodes must be the keystore itself.
sed -n 's/^ANDROID_KEYSTORE_BASE64=//p' "$out/github-production-secrets.env" | base64 -d | cmp -s - "$out/meowssh-upload.p12" \
    || fail "ANDROID_KEYSTORE_BASE64 does not decode to the upload keystore"
grep -qx "ANDROID_STORE_PASSWORD=$password" "$out/github-production-secrets.env" || fail "the store password secret is wrong"
ci_password="$(sed -n 's/^storePassword=//p' "$out/meowssh-ci.properties")"
[[ "$ci_password" != "$password" ]] || fail "the CI key must not share the upload key's password"
grep -q '^CI_ANDROID_KEYSTORE_BASE64=' "$out/github-ci-secrets.env" || fail "CI secrets are missing"

grep -qx 'LICENSING_API_BASE_URL=https://api.meowssh.invalid/' "$out/github-production-variables.env" || fail "the API URL was not normalized"
grep -qx 'TAILCAT_DERP_MAP_URL=https://api.meowssh.invalid/v1/derp/map' "$out/github-production-variables.env" \
    || fail "the relay map should default to the API's own"
grep -q 'TODO licensing API' <<<"$first" || fail "an unreachable API should be reported as an open gate"
grep -q 'TERMS.md placeholders\|TERMS.md has no placeholders' <<<"$first" || fail "the TERMS.md gate did not run"

# Second run: nothing passed again, nothing may change.
before="$(sha256sum "$out/meowssh-upload.p12" "$out/meowssh-ci.p12")"
second="$(run --privacy-url https://privacy.meowssh.invalid)"
after="$(sha256sum "$out/meowssh-upload.p12" "$out/meowssh-ci.p12")"
[[ "$before" == "$after" ]] || fail "a rerun replaced a keystore"
grep -q 'kept:' <<<"$second" || fail "a rerun should say it kept the upload key"
grep -qx 'LICENSING_API_BASE_URL=https://api.meowssh.invalid/' "$out/github-production-variables.env" || fail "a rerun lost the API URL"
grep -qx 'PRIVACY_POLICY_URL=https://privacy.meowssh.invalid' "$out/github-production-variables.env" || fail "a rerun did not record the new URL"

echo "play-store bootstrap test: ok"
