#!/usr/bin/env bash
# Gets MeowSSH ready for Google Play: generates the Android upload key,
# writes every GitHub secret and variable the "Play Release" workflow
# reads, checks the same release gates that workflow checks, and walks you
# through the Play Console steps nothing can script.
#
#   scripts/play-store-bootstrap.sh \
#       --api-url https://api.example.com/ \
#       --licensing-public-key <printed by MeowSSHAPI's deploy/single-node/bootstrap.sh> \
#       --privacy-url https://example.com/privacy --support-url https://example.com/support \
#       --terms-url https://example.com/terms \
#       [--github-repo Sniperlyf3/MeowSSH --apply] [--gcp-project my-project]
#
# Safe to rerun: an existing keystore is never replaced (a lost or changed
# upload key needs a reset request to Google), and values from the previous
# run are reused for anything not passed again.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out_dir="${HOME}/meowssh-release-secrets"
api_url=""
public_key=""
privacy_url=""
support_url=""
terms_url=""
derp_map_url=""
github_repo=""
gcp_project=""
apply=false
ci_key=false
package_name="dev.sniperlyf3.meowssh"
key_alias="meowssh-upload"

usage() {
    sed -n '2,18p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
    cat <<'EOF'
Options:
  --out-dir DIR                where keys and generated files go (default ~/meowssh-release-secrets)
  --api-url URL                the MeowSSHAPI base URL, https
  --licensing-public-key B64   the API's entitlement public key (SubjectPublicKeyInfo, Base64)
  --privacy-url, --support-url, --terms-url URL   public https pages
  --derp-map-url URL           relay map (default <api-url>/v1/derp/map)
  --github-repo OWNER/NAME     where the Play Release workflow runs
  --apply                      set the secrets and variables with gh (needs gh auth login)
  --gcp-project ID             create the GitHub-to-Google publishing identity with gcloud
  --ci-signing-key             also make a separate, non-production CI signing key
  -h, --help                   this text
EOF
}

while (($#)); do
    case "$1" in
        --out-dir) out_dir="$2"; shift 2 ;;
        --api-url) api_url="$2"; shift 2 ;;
        --licensing-public-key) public_key="$2"; shift 2 ;;
        --privacy-url) privacy_url="$2"; shift 2 ;;
        --support-url) support_url="$2"; shift 2 ;;
        --terms-url) terms_url="$2"; shift 2 ;;
        --derp-map-url) derp_map_url="$2"; shift 2 ;;
        --github-repo) github_repo="$2"; shift 2 ;;
        --gcp-project) gcp_project="$2"; shift 2 ;;
        --apply) apply=true; shift ;;
        --ci-signing-key) ci_key=true; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1 (see --help)" >&2; exit 2 ;;
    esac
done

bold() { printf '\033[1m%s\033[0m\n' "$*"; }
note() { printf '  %s\n' "$*"; }
warn() { printf '\033[33mWARNING:\033[0m %s\n' "$*" >&2; }
fail() { printf '\033[31mERROR:\033[0m %s\n' "$*" >&2; exit 1; }

mkdir -p "$out_dir"
chmod 700 "$out_dir"
umask 077
vars_file="$out_dir/github-production-variables.env"
secrets_file="$out_dir/github-production-secrets.env"

previous() {
    [[ -f "$vars_file" ]] || return 0
    sed -n "s/^$1=//p" "$vars_file" | tail -n 1
}
api_url=${api_url:-$(previous LICENSING_API_BASE_URL)}
public_key=${public_key:-$(previous LICENSING_PUBLIC_KEY_SUBJECT_PUBLIC_KEY_INFO_BASE64)}
privacy_url=${privacy_url:-$(previous PRIVACY_POLICY_URL)}
support_url=${support_url:-$(previous SUPPORT_URL)}
terms_url=${terms_url:-$(previous TERMS_OF_SERVICE_URL)}
derp_map_url=${derp_map_url:-$(previous TAILCAT_DERP_MAP_URL)}
wif_provider="$(previous GCP_WORKLOAD_IDENTITY_PROVIDER)"
publish_account="$(previous GCP_PLAY_PUBLISH_SERVICE_ACCOUNT)"
if [[ -n "$api_url" ]]; then
    api_url="${api_url%/}/"
    derp_map_url=${derp_map_url:-${api_url}v1/derp/map}
fi

command -v keytool >/dev/null || fail "keytool is required (it ships with any JDK, e.g. Temurin 21)."

random_password() { openssl rand -base64 30 | tr -d '/+=\n' | cut -c1-32; }

# One keystore file, one alias. PKCS12 uses the store password for the key
# too, so the two passwords are the same value on purpose.
make_keystore() {
    local file="$1" alias="$2" props="$3" dname="$4"
    if [[ -s "$file" ]]; then
        echo kept
        return
    fi
    local password; password="$(random_password)"
    keytool -genkeypair -keystore "$file" -storetype PKCS12 -alias "$alias" \
        -keyalg RSA -keysize 4096 -validity 10000 -dname "$dname" \
        -storepass "$password" -keypass "$password" >/dev/null 2>&1
    chmod 600 "$file"
    printf 'alias=%s\nstorePassword=%s\nkeyPassword=%s\n' "$alias" "$password" "$password" >"$props"
    chmod 600 "$props"
    echo created
}

prop() { sed -n "s/^$2=//p" "$1"; }

fingerprint() {
    local file="$1" props="$2"
    keytool -list -v -keystore "$file" -storepass "$(prop "$props" storePassword)" -alias "$(prop "$props" alias)" 2>/dev/null \
        | sed -n 's/^[[:space:]]*SHA256: //p' | head -n 1
}

bold "Upload key"
keystore="$out_dir/meowssh-upload.p12"
keystore_props="$out_dir/meowssh-upload.properties"
state="$(make_keystore "$keystore" "$key_alias" "$keystore_props" "CN=MeowSSH upload key, O=MeowSSH")"
note "$state: $keystore (passwords in $keystore_props)"
upload_fingerprint="$(fingerprint "$keystore" "$keystore_props")"
note "upload certificate SHA-256: $upload_fingerprint"
note "This is the key you sign uploads with. Google re-signs what users install with the"
note "Play App Signing key, whose fingerprint (not this one) goes to the API as PLAY_CERT_SHA256."

{
    printf 'ANDROID_KEYSTORE_BASE64=%s\n' "$(base64 <"$keystore" | tr -d '\n')"
    printf 'ANDROID_KEY_ALIAS=%s\n' "$(prop "$keystore_props" alias)"
    printf 'ANDROID_KEY_PASSWORD=%s\n' "$(prop "$keystore_props" keyPassword)"
    printf 'ANDROID_STORE_PASSWORD=%s\n' "$(prop "$keystore_props" storePassword)"
} >"$secrets_file"

if [[ "$ci_key" == true ]]; then
    ci_keystore="$out_dir/meowssh-ci.p12"
    ci_props="$out_dir/meowssh-ci.properties"
    state="$(make_keystore "$ci_keystore" meowssh-ci "$ci_props" "CN=MeowSSH CI (not for release), O=MeowSSH")"
    note "$state: $ci_keystore -- CI builds only; never upload anything signed with it"
    ci_secrets_file="$out_dir/github-ci-secrets.env"
    {
        printf 'CI_ANDROID_KEYSTORE_BASE64=%s\n' "$(base64 <"$ci_keystore" | tr -d '\n')"
        printf 'CI_ANDROID_KEY_ALIAS=%s\n' "$(prop "$ci_props" alias)"
        printf 'CI_ANDROID_KEY_PASSWORD=%s\n' "$(prop "$ci_props" keyPassword)"
        printf 'CI_ANDROID_STORE_PASSWORD=%s\n' "$(prop "$ci_props" storePassword)"
    } >"$ci_secrets_file"
fi

# GitHub Actions -> Google Play publishing, without a long-lived JSON key:
# a workload identity pool trusting only this repository, and a service
# account it may impersonate. Play Console access is then granted to that
# account by hand (step 6 below).
if [[ -n "$gcp_project" && -z "$wif_provider" ]]; then
    command -v gcloud >/dev/null || fail "--gcp-project needs gcloud, signed in with gcloud auth login."
    [[ -n "$github_repo" ]] || fail "--gcp-project needs --github-repo, to limit who may publish."
    bold "Creating the publishing identity in $gcp_project"
    number="$(gcloud projects describe "$gcp_project" --format='value(projectNumber)')"
    gcloud services enable iamcredentials.googleapis.com androidpublisher.googleapis.com --project "$gcp_project"
    gcloud iam workload-identity-pools describe github --location global --project "$gcp_project" >/dev/null 2>&1 \
        || gcloud iam workload-identity-pools create github --location global --project "$gcp_project" --display-name "GitHub Actions"
    gcloud iam workload-identity-pools providers describe github --workload-identity-pool github --location global --project "$gcp_project" >/dev/null 2>&1 \
        || gcloud iam workload-identity-pools providers create-oidc github --workload-identity-pool github --location global \
            --project "$gcp_project" --issuer-uri https://token.actions.githubusercontent.com \
            --attribute-mapping 'google.subject=assertion.sub,attribute.repository=assertion.repository,attribute.environment=assertion.environment' \
            --attribute-condition "assertion.repository == '$github_repo' && assertion.environment == 'production'"
    publish_account="meowssh-play-publish@$gcp_project.iam.gserviceaccount.com"
    gcloud iam service-accounts describe "$publish_account" --project "$gcp_project" >/dev/null 2>&1 \
        || gcloud iam service-accounts create meowssh-play-publish --project "$gcp_project" --display-name "MeowSSH Play publishing (GitHub Actions)"
    gcloud iam service-accounts add-iam-policy-binding "$publish_account" --project "$gcp_project" \
        --role roles/iam.workloadIdentityUser \
        --member "principalSet://iam.googleapis.com/projects/$number/locations/global/workloadIdentityPools/github/attribute.repository/$github_repo" >/dev/null
    wif_provider="projects/$number/locations/global/workloadIdentityPools/github/providers/github"
fi

{
    printf 'LICENSING_API_BASE_URL=%s\n' "$api_url"
    printf 'LICENSING_PUBLIC_KEY_SUBJECT_PUBLIC_KEY_INFO_BASE64=%s\n' "$public_key"
    printf 'PRIVACY_POLICY_URL=%s\n' "$privacy_url"
    printf 'SUPPORT_URL=%s\n' "$support_url"
    printf 'TERMS_OF_SERVICE_URL=%s\n' "$terms_url"
    printf 'TAILCAT_DERP_MAP_URL=%s\n' "$derp_map_url"
    printf 'GCP_WORKLOAD_IDENTITY_PROVIDER=%s\n' "$wif_provider"
    printf 'GCP_PLAY_PUBLISH_SERVICE_ACCOUNT=%s\n' "$publish_account"
} >"$vars_file"

echo
bold "GitHub (repository -> Settings -> Environments -> production)"
note "secrets:   $secrets_file"
note "variables: $vars_file"
[[ "$ci_key" == true ]] && note "CI-only repository secrets: $ci_secrets_file"
if [[ "$apply" == true ]]; then
    command -v gh >/dev/null || fail "--apply needs the GitHub CLI (gh), signed in with gh auth login."
    [[ -n "$github_repo" ]] || fail "--apply needs --github-repo OWNER/NAME."
    gh api -X PUT "repos/$github_repo/environments/production" >/dev/null
    gh secret set -f "$secrets_file" --env production --repo "$github_repo"
    grep -v '=$' "$vars_file" >"$vars_file.set" || true
    gh variable set -f "$vars_file.set" --env production --repo "$github_repo"
    rm -f "$vars_file.set"
    [[ "$ci_key" == true ]] && gh secret set -f "$ci_secrets_file" --repo "$github_repo"
    note "applied to $github_repo (empty variables skipped)"
else
    note "Apply with --apply --github-repo OWNER/NAME, or by hand:"
    note "  gh secret set -f $secrets_file --env production"
    note "  gh variable set -f $vars_file --env production"
fi

# The same gates the Play Release workflow enforces, so they fail here and
# not twenty minutes into a release build.
echo
bold "Release gates"
blockers=0
gate() { if [[ "$1" == ok ]]; then note "OK   $2"; else note "TODO $2${3:+ -- $3}"; blockers=$((blockers + 1)); fi; }
reachable() { curl -fsSL --max-time 20 -o /dev/null "$1" 2>/dev/null; }

if [[ -z "$api_url" ]]; then
    gate todo "licensing API" "pass --api-url (MeowSSHAPI's bootstrap.sh prints it)"
elif [[ "$api_url" != https://* ]]; then
    gate todo "licensing API" "$api_url must be https"
elif reachable "${api_url}readyz"; then
    gate ok "licensing API ready at ${api_url}readyz"
    served="$(curl -fsSL --max-time 20 "${api_url}v1/entitlements/public-key" 2>/dev/null | sed -n 's/.*"subjectPublicKeyInfoBase64":"\([^"]*\)".*/\1/p')"
    if [[ -z "$public_key" ]]; then
        gate todo "licensing public key" "pass --licensing-public-key; the API serves: ${served:-nothing}"
    elif [[ "$served" == "$public_key" ]]; then
        gate ok "the app's public key matches the one the API signs with"
    else
        gate todo "licensing public key" "does not match what ${api_url} serves"
    fi
else
    gate todo "licensing API" "${api_url}readyz does not answer 200 yet (MeowSSHAPI bootstrap.sh --check says why)"
fi

for pair in "privacy policy:$privacy_url" "support page:$support_url" "terms of service:$terms_url"; do
    label="${pair%%:*}"; url="${pair#*:}"
    if [[ -z "$url" ]]; then gate todo "$label URL" "pass it (a public https page)"
    elif [[ "$url" != https://* ]]; then gate todo "$label URL" "must be https"
    elif reachable "$url"; then gate ok "$label reachable"
    else gate todo "$label URL" "$url is not reachable"; fi
done

if [[ -n "$derp_map_url" ]]; then
    if [[ "$derp_map_url" != "https://tailcat.dev/derpmap.json" ]] && ! grep -Fq "$derp_map_url" "$repo_root/PRIVACY.md"; then
        gate todo "relay map disclosed" "PRIVACY.md must name $derp_map_url before release"
    else
        gate ok "relay map disclosed in PRIVACY.md"
    fi
fi

if grep -q '\[LEGAL_ENTITY\]\|\[GOVERNING_LAW_AND_VENUE\]\|\[PUBLIC_LEGAL_AND_SUPPORT_CONTACT\]\|\[LEGAL_REVIEW_REQUIRED' "$repo_root/TERMS.md"; then
    gate todo "TERMS.md placeholders" "fill in [LEGAL_ENTITY], governing law, contact and the liability section"
else
    gate ok "TERMS.md has no placeholders"
fi

if [[ -n "$wif_provider" && -n "$publish_account" ]]; then
    gate ok "publishing identity ($publish_account)"
else
    gate todo "publishing identity" "only needed to publish from GitHub; pass --gcp-project and --github-repo, or upload by hand"
fi

echo
bold "Play Console (play.google.com/console) -- in this order"
cat <<EOF
  1. Create app -> name "MeowSSH", package $package_name, app, free (in-app purchases).
  2. Test and release -> Testing -> Internal testing -> Testers: add your Google accounts.
     Also Settings -> License testing: add the same accounts, so test purchases cost nothing.
  3. Build the first bundle: GitHub -> Actions -> "Play Release" -> Run workflow with
     publish_internal=false. Download the signed AAB artifact.
  4. Internal testing -> Create new release -> accept Play App Signing -> upload that AAB.
     (Google needs the very first upload by hand; after that the workflow can publish.)
  5. Test and release -> App integrity -> App signing -> copy "App signing key certificate"
     SHA-256 (not the upload key's, which is $upload_fingerprint), then on the API host:
       deploy/single-node/bootstrap.sh --play-cert-sha256 <that fingerprint> --start
  6. Users and permissions -> Invite new users:
       - the API's service account (from MeowSSHAPI bootstrap.sh / google-adc.json):
         "View financial data" and "Manage orders and subscriptions"
       - ${publish_account:-the publishing service account}: "Release apps to testing tracks"
  7. Test and release -> App integrity -> Play Integrity API -> link the Google Cloud project
     that owns the API's service account.
  8. Monetize -> Products -> In-app products -> create "meowssh_pro_lifetime" (one-time).
     Leave "meowssh_pro_cloud" and "meowssh_team" subscriptions unpublished for now
     (docs/google-play-products.md explains why and how to turn them on later).
  9. Policy -> App content: privacy policy URL, ads (none), data safety (answers in
     docs/play-data-safety.md), content rating, target audience, and the foreground
     service declaration.
 10. Grow -> Store presence -> Main store listing: title, descriptions, icon, feature
     graphic, screenshots, contact email. Do not advertise Pro Cloud, Team or Ask AI yet.
 11. Install MeowSSH from the internal testing link (not a sideload), buy Pro with a
     licence tester, and check Settings -> Plan shows it. Then promote the release.
EOF

echo
bold "Back up off this machine: $out_dir"
note "The upload keystore and its passwords are the one thing here that is painful to lose:"
note "Google can reset an upload key, but only after a support request and a waiting period."
if ((blockers)); then
    echo
    warn "$blockers release gate(s) still open (TODO above). Rerun this script after fixing them."
fi
