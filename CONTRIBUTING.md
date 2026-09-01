# Contributing to Wisp

Wisp is a proprietary, source-available project. Contributions are reviewed at
the sole discretion of the repository owner. Opening an issue or pull request
does not guarantee acceptance, and only `@Views2k` may approve a change for
merge.

## Acceptable contributions

A contribution must have a clear, verifiable benefit in at least one of these
areas:

- usability, accessibility, visual consistency, or interaction quality;
- existing functionality, performance, reliability, or maintainability;
- a reproducible bug, security issue, or compatibility problem;
- replacement of an obsolete, deprecated, or vulnerable dependency.

Speculative redesigns, unrelated features, scope expansion, duplicate update
loops, additional telemetry collection, network services, game injection, and
unreviewed binary or Native HUD assets will not be accepted.

Before preparing any source, documentation, automation, or asset change for
submission, open an issue and obtain written owner approval for that specific
scope. Bug reports and feature requests do not require advance approval. Keep
each pull request focused on one approved change.

Dependency updates opened by repository-owner-configured Dependabot are deemed
invited only within the update scope configured in `.github/dependabot.yml`.
They remain subject to the same review, validation, and owner-only merge rules.

## Engineering requirements

- Preserve Wisp's loopback-only telemetry, read-only FH6 access, and
  fail-closed compatibility checks.
- Keep live HUD animation attached to WPF's compositor lifecycle. Do not add
  timer loops or duplicate telemetry-driven gauge updates.
- Add regression coverage for behavior changes and update existing contracts
  when an intentional interface changes.
- Keep settings backward-compatible. New settings require defaults,
  normalization, persistence tests, and migration coverage.
- Update `CHANGELOG.md` for user-visible changes.
- Do not commit generated output, local settings, credentials, account data,
  machine-specific paths, game executables, save files, or private telemetry
  captures.

## Validation

Wisp requires Windows, the .NET 8 SDK selected by `global.json`, and Python
3.12 or later. CI pins Python 3.14.7. Inno Setup 6 is required only for
installer packaging.

```powershell
dotnet restore Wisp.sln --locked-mode -p:NuGetAudit=true -p:NuGetAuditMode=all
dotnet format Wisp.sln --verify-no-changes --no-restore --verbosity minimal
dotnet test Wisp.sln --configuration Release --no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false
python -m unittest discover -s tools/tests -p "test_*.py" -v
```

The UI review harness and its bounded validation modes are documented in
[`tools/Wisp.UiReview/README.md`](tools/Wisp.UiReview/README.md).

## Native HUD assets

The PNG files under `src/Wisp.App/Assets/Native` remain Microsoft Game Content
and are not covered by Wisp's source license. Do not add, replace, transform,
or redistribute third-party assets without prior owner approval and a documented
provenance and distribution basis. Approved asset changes must record their
provenance, role, dimensions, and SHA-256 in `ASSET-MANIFEST.csv` and preserve
`THIRD-PARTY-NOTICE.txt`.

## Pull requests

Every pull request must explain:

- the user problem or maintenance need;
- the verified root cause;
- the smallest appropriate solution;
- the automated tests run;
- any live FH6 or visual validation still outstanding.

All required checks must pass, review conversations must be resolved, and only
`@Views2k` may approve or merge a pull request. Screenshots support visual
review but do not replace layout, binding, lifecycle, or telemetry tests.

## Contributor rights

Submit only work you created or have the right to contribute. By opening a
pull request, you confirm that you accept these terms and grant Views2k a
perpetual, worldwide, irrevocable,
non-exclusive, royalty-free license to use, reproduce, modify, distribute,
sublicense, and relicense that contribution as part of Wisp. You retain
ownership of your original contribution. This grant does not change the
proprietary license applied to Wisp as a whole.
