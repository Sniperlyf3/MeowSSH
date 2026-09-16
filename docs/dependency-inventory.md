# Dependency inventory and vulnerability evidence

MeowSSH release evidence should include a machine-readable record of the NuGet dependency graph used for the candidate source commit. This complements the packaged native-library checks and bundled third-party notices; it does not replace them.

`MeowSSH.slnx` intentionally contains the cross-platform/test projects but not the Android app head, so release evidence must inventory both the solution and `src/MeowSSH.App/MeowSSH.App.csproj`. The Android app scope captures the Maui, Google Play Billing, Play Integrity, ZXing, AndroidX, USB and other NuGet dependencies that actually contribute to the shipping app.

## Generate locally or in CI

Run:

```sh
bash scripts/generate-dependency-inventory.sh
```

The Android workload must be installed before running the generator because the shipping app targets `net10.0-android36.0`. The dedicated `Dependency Evidence` workflow installs that workload automatically.

By default the command writes ignored build evidence under `artifacts/dependency-inventory/`. A different output directory can be supplied as the first argument.

The generator records:

- `solution-nuget-dependencies.json` — top-level and transitive NuGet packages for `MeowSSH.slnx`;
- `solution-nuget-vulnerabilities.json` — the solution graph queried against NuGet vulnerability metadata;
- `android-app-nuget-dependencies.json` — top-level and transitive NuGet packages for the shipping Android app project;
- `android-app-nuget-vulnerabilities.json` — the Android app graph queried against NuGet vulnerability metadata;
- `dotnet-info.txt` — SDK/runtime information needed to interpret the dependency resolution environment;
- `source-commit.txt` — the exact Git commit used to generate the evidence;
- `SHA256SUMS` — hashes of all six evidence files above.

The JSON reports use .NET 10's `dotnet package list` command with `--include-transitive`, `--format json`, and output schema version 1. The vulnerability reports additionally use `--vulnerable`.

## CI validation

`.github/workflows/dependency-evidence.yml` runs when the generator, workflow, solution/project definitions, or shared build properties change. It:

1. installs the Android workload;
2. generates both dependency scopes;
3. validates every JSON report;
4. asserts that the Android app project is present in the shipping-app report;
5. verifies that the recorded source commit matches the checked-out commit;
6. verifies `SHA256SUMS`;
7. uploads the complete evidence directory for 30 days.

## Release use

For a production candidate, generate the inventory from the exact source SHA used to build the signed AAB and retain the output beside the AAB, merged manifest, Play declarations, policy revisions, and other release evidence.

A future production-workflow integration should invoke this script from the same checked-out commit as the AAB build and upload the resulting directory as a release artifact. Until the current release-workflow integration PR has landed, this script intentionally remains standalone so it does not create another overlapping edit to `.github/workflows/play-release.yml`.

## Interpretation

A vulnerability report is evidence of what the configured NuGet sources knew at generation time; it is not a permanent statement that the dependency set is vulnerability-free. Re-run it for every release candidate and whenever a relevant advisory is published.

The NuGet inventory does not enumerate bundled native binaries or Android Maven dependencies such as Meowshell, Tailcat, or the optional HEV tunnel. Those continue to be checked separately by Android package validation, VPN-exclusion assertions, 16 KiB native alignment checks, and the third-party notice bundle.
