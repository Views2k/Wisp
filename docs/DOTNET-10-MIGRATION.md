# .NET 10 migration plan

Status: planned; Wisp currently targets .NET 8 and ships its serviced runtime.
This plan does not change the application's target framework or release version.

.NET 8 support ends on **November 10, 2026**. .NET 10 is the next LTS target and
is supported through November 14, 2028. Complete the migration and release
validation before the .NET 8 deadline. These dates come from
[Microsoft's support policy](https://dotnet.microsoft.com/en-us/platform/support/policy),
checked September 10, 2026.

## Scope and sequence

Project and SDK changes are small, but Windows presentation and installer
validation must establish that the runtime change preserves behavior.

1. Prepare a focused migration pull request by October 13, 2026. Select the
   current supported .NET 10 SDK and runtime patch from
   [Microsoft's release downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).
   Review the .NET and WPF compatibility notes and supported Windows versions
   before choosing the exact pins. Record the versions and any relevant changes
   in the pull request.
2. Update `global.json`, every production, test, and UI-review target framework,
   and both self-contained runtime pins together. Regenerate lockfiles with an
   audited restore. Review transitive changes and address actual analyzer,
   compiler, or API failures with focused fixes. Preserve x64 packaging and the
   native renderer ABI.
3. Replace the bundled runtime notices with the exact new package notices,
   update their hashes and packaging references, and align current build guides.
   Preserve historical release notes. Do not combine feature work, asset changes,
   telemetry changes, or a native renderer redesign with this migration.
4. Complete the acceptance gates below and prepare a release candidate by
   October 27, 2026, leaving time for corrections before November 10. Use the
   existing protected pull-request and release workflows. Assign the application
   release version and publish only after validation and release approval are complete.

These are planning targets, not a scheduled automation or proof that a release
has passed validation. If a gate is blocked, record the blocker and retain the
last verified release; do not bypass the gate to meet a date.

## Acceptance gates

Record the exact source commit, commands, results, and any outstanding manual
checks in the migration pull request. The existing
[release gate](VALIDATION.md#release-gate) remains required.

- Audited locked restore, source formatting, complete Release solution tests,
  UI-review harness build, and offline compatibility-audit tests pass with the
  selected .NET 10 SDK. Existing repository checks remain enabled.
- Settings and HUD profiles from the current release load without losing
  placement, units, colors, or enabled states. Tire calibration, saved Runs,
  imported run files, setup completion, and update validation retain their
  existing compatibility and failure behavior.
- The unchanged native renderer builds with the supported x64 C++ toolchain.
  Run `Build-NativeRenderer.ps1 -RunContractTests` on a Windows desktop with a
  hardware GPU and retain the result separately from CI. Verify native GPU and
  CPU rendering, WPF fallback, hidden/resumed lifetimes, and managed/native
  resource cleanup; a successful DLL build does not satisfy this gate.
- Existing UI-review modes pass at their supported viewports and DPI settings.
  Complete the live Windows/FH6 checks needed to validate actual shader output,
  overlay placement, focus transitions, and rendering behavior. Synthetic WPF
  captures alone do not establish live presentation parity.
- A clean self-contained installer build includes the intended runtime and
  notices, validates executable versions and architecture, and passes the
  installer lifecycle canary. Verify an in-place update from the latest public
  Wisp release preserves settings and completed setup, while a fresh install
  still requires the setup wizard. Verify uninstallation and failed-update
  recovery retain their current boundaries.

If changing the runtime would narrow Windows support, resolve that explicitly
before merging rather than silently changing the documented requirements.
