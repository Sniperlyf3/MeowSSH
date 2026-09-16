# Dependency inventory and vulnerability evidence

MeowSSH release evidence should include a machine-readable record of the NuGet dependency graph used for the candidate source commit. This complements the packaged native-library checks and bundled third-party notices; it does not replace them.

## Generate locally or in CI

Run:

```sh
bash scripts/generate-dependency-inventory.sh
```

By default the command writes ignored build evidence under `artifacts/dependency-inventory/`. A different output directory can be supplied as the first argument.

The generator records:

- `nuget-dependencies.json` — top-level and transitive NuGet packages for `MeowSSH.slnx`;
- `nuget-vulnerabilities.json` — the same solution queried against NuGet vulnerability metadata;
- `dotnet-info.txt` — SDK/runtime information needed to interpret the dependency resolution environment;
- `source-commit.txt` — the exact Git commit used to generate the evidence;
- `SHA256SUMS` — hashes of the four evidence files above.

The JSON reports use .NET 10's `dotnet package list` command with `--include-transitive`, `--format json`, and output schema version 1. The vulnerability report additionally uses `--vulnerable`.

## Release use

For a production candidate, generate the inventory from the exact source SHA used to build the signed AAB and retain the output beside the AAB, merged manifest, Play declarations, policy revisions, and other release evidence.

A future production-workflow integration should invoke this script from the same checked-out commit as the AAB build and upload the resulting directory as a release artifact. Until the current release-workflow integration PR has landed, this script intentionally remains standalone so it does not create another overlapping edit to `.github/workflows/play-release.yml`.

## Interpretation

A vulnerability report is evidence of what the configured NuGet sources knew at generation time; it is not a permanent statement that the dependency set is vulnerability-free. Re-run it for every release candidate and whenever a relevant advisory is published.

The NuGet inventory also does not enumerate bundled native binaries such as Meowshell or Tailcat. Those continue to be checked separately by Android package validation and the third-party notice bundle.
