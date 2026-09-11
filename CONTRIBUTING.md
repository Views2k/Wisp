# Contributing to Wisp

Wisp is a proprietary, source-available project. Acceptance is discretionary.
Opening an issue or pull request does not guarantee acceptance.

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
submission, open an issue and obtain written approval for that specific scope.
Bug reports and feature requests do not require advance approval. Keep each
pull request focused on one approved change.

Updates opened by the repository's configured Dependabot are deemed invited
only within the update scope configured in `.github/dependabot.yml`. They
remain subject to the same review, validation, and merge-approval rules.

## Engineering requirements

- Preserve Wisp's loopback-only telemetry, read-only FH6 access, and
  fail-closed compatibility checks.
- Keep each live HUD attached to its renderer's lifecycle. The native analogue
  renderer uses the DXGI frame-latency wait handle; WPF views use their existing
  compositor lifecycle. Do not add polling timers or duplicate gauge updates.
- Add regression coverage for behavior changes and update existing contracts
  when an intentional interface changes.
- Keep settings backward-compatible. New settings require defaults,
  normalization, persistence tests, and migration coverage.
- Update `CHANGELOG.md` for user-visible changes.
- Do not commit generated output, local settings, credentials, account data,
  machine-specific paths, game executables, save files, or private telemetry
  captures. The checked-in shader bytecode is an exception: approved shader
  changes must include matching source and bytecode as described in
  [Shader maintenance](docs/SHADERS.md).

## Validation

Wisp requires Windows, the .NET 8 SDK selected by `global.json`, and Python
3.12 or later. CI pins Python 3.14.7. Inno Setup 6 is required only for
installer packaging.

The analogue renderer also requires Visual Studio 2022
C++ Build Tools with the x64 MSVC toolchain and a Windows 10/11 SDK (including
`fxc.exe`). The application build compiles the native backend automatically.
`Wisp.NativeRenderer.dll` must remain beside `Wisp.exe`; installer packaging
checks its architecture and records its hash in diagnostic provenance.

The runtime test checks hardware support before exercising either DirectComposition
or the WPF fallback. An unsupported hardware adapter must still produce a working
gauge and the exact fallback diagnostic; other initialization errors fail the test.
The native pixel and presentation contracts require a hardware GPU and remain
a separate manual check on a Windows desktop. They are required for the
[release gate](docs/VALIDATION.md#release-gate), but normal CI and installer
packaging do not run them. Building the native DLL does not execute these
contracts; record the source commit and result separately before a release:

```[WINDOWS POWERSHELL]
./src/Wisp.NativeRenderer/Build-NativeRenderer.ps1 -RunContractTests
```

Run the commands below from the repository checkout. Installer packaging also
requires Git and a clean checkout with a resolvable source commit; GitHub source
archives do not contain that Git history. See the
[packaging requirements](docs/VALIDATION.md#ci-and-packaging) for private test
builds with uncommitted changes.

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
or redistribute third-party assets without prior approval and a documented
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
