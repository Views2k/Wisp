# Validation

## Release gate

A clean Windows Release build is Wisp's release boundary. The
gate covers source formatting, dependency auditing, the complete .NET solution,
the offline compatibility audit, Native asset identity, WPF layout checks, and
installer staging.

The repository does not treat an earlier run count as proof for a changed tree.
A release artifact is acceptable only when the following commands pass against
the exact source revision being packaged and the installer reports the version
declared by `src/Wisp.App/Wisp.App.csproj`:

```powershell
dotnet restore Wisp.sln --locked-mode -p:NuGetAudit=true -p:NuGetAuditMode=all
dotnet format Wisp.sln --verify-no-changes --no-restore --verbosity minimal
dotnet test Wisp.sln --configuration Release --no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false
dotnet build tools/Wisp.UiReview/Wisp.UiReview.csproj --configuration Release --no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false
python -m unittest discover -s tools/tests -p "test_*.py" -v
```

Packaging must additionally verify the staged executable version, PE identity,
update-helper identity, Native asset manifest, bundled .NET 8.0.30 notices, and
the installer/checksum pair before promotion. This document intentionally does
not claim that an artifact has been signed or published.

## Automated coverage

### Core

- driven-wheel selection for FWD, RWD, and AWD;
- initial and replacement tire-radius consensus;
- wheel-speed conversion, units, smoothing, and invalid-state handling;
- G-force scaling and over-range behavior;
- telemetry freshness, visibility, and state transitions.

### Telemetry

- exact packet offsets and 324-byte packet length;
- malformed, non-finite, and invalid enum data;
- physical gear semantics;
- loopback listener start, restart, backlog drain, bind failure, and disposal.

### Application

- settings defaults, normalization, migration, and durable replacement;
- first-install setup gating and completion persistence;
- controller and startup-companion lifecycle;
- WPF resources, responsive layout, bindings, and accessibility labels;
- Native asset manifest hashes, decoded pixels, geometry, and shader contracts;
- compositor lifetime, recorded tachometer traces, and bounded Native needle
  playback/reset behavior;
- process-memory permission boundaries and identity guards;
- compatibility contract, signature, cache, update, and failure behavior;
- latest-release parsing, semantic-version ordering, bounded downloads,
  redirect policy, byte-length checks, and SHA-256 rejection paths;
- staged update-request validation, exact parent-process binding, installer and
  installed-app identity checks, silent apply arguments, and restart ordering;
- fingerprint-gated EV digits, fade flags, gears, power/regeneration, needle,
  and invalid-field rejection;
- menu/loading/cutscene visibility while telemetry remains active;
- dashboard horsepower/torque formatting and unavailable-state behavior;
- installer staging, executable validation, promotion, and rollback contracts.

### Compatibility audit

The Python suite exercises strict PE and contract parsing, fingerprint checks,
bounded address validation, malformed input, and the read-only command-line
workflow. Capstone-dependent discovery tests are optional and remain separate
from the Wisp runtime.

## UI review

The opt-in review tool constructs the real compiled WPF pages with isolated
settings and deterministic sample state. The main matrix covers five pages at
four viewport sizes, with supplementary Native and Combined fixtures. Separate
bounded modes cover:

- all four setup steps at multiple viewport and DPI combinations;
- native-control render subscription and hidden-state behavior;
- scrolling geometry and transform stability;
- the loaded sidebar and accent/background resource combinations.

Reports check missing resources, binding failures, unexpected clipping, label
overflow, required control bounds, DPI behavior, and Native asset identity.

Software `RenderTargetBitmap` does not execute WPF PS 3.0 effects, so those PNGs
are layout evidence rather than proof of live shader output. Synthetic WPF
hosts also do not establish live FH6 offsets, GPU frame time, or exact visual
parity. Those boundaries are kept explicit in the report.

## CI and packaging

GitHub Actions runs on `windows-latest` and performs:

1. an audited NuGet restore;
2. `dotnet format --verify-no-changes`;
3. the complete Release .NET solution tests;
4. a locked Release build of the UI review harness;
5. the Python compatibility-audit tests.

The local packaging script adds a separate installer gate. It runs the Release
.NET suite, creates a self-contained untrimmed `win-x64` application publish and
a separate single-file update-helper publish, validates both PE files and their
versions, and invokes Inno Setup in a unique staging directory. The real Inno
artifact must then pass both runtime installer validators; TRX counters require
each exact filtered test to execute and pass once. Packaging generates the
installer checksum, creates and reopens the two-entry release archive, generates
the archive checksum, and promotes all four files as one recoverable release
transaction. A durable marker is written after all previous artifacts have
verified backups and before the first destination changes. A later packaging
run restores an interrupted transaction before doing new work.

Public packaging requires a clean Git worktree and a resolvable source commit.
`Build-Installer.ps1 -AllowDirty` exists only for private test artifacts and is
not an acceptable release path.

Before publication, the verified installer and its checksum are placed alone
in `Wisp-Setup-<version>.zip`. The archive is reopened before promotion; its entry
names, entry count, installer bytes, and inner checksum are validated. A
separate SHA-256 file covers the complete ZIP. The installer, inner checksum,
ZIP, and outer checksum retain verified recovery copies if any destination
cannot be replaced.
GitHub's source ZIP and TAR.GZ are generated from the same release tag.

For the in-application updater, the published release must use a strict `vX.Y.Z`
tag, be neither a draft nor a prerelease, and be immutable. It must contain
exactly one uploaded `Wisp-Setup-<version>.exe` asset whose GitHub metadata
includes the byte length and SHA-256 digest. This anonymous update path is
available only after the repository and release assets are public; before
publication, failure must remain non-destructive. Publishing an immutable
release is an external release operation, not part of the
local packaging script.

The installer job also runs the real Inno artifact through a lifecycle canary on
the ephemeral `windows-latest` user profile. It installs into a unique
`RUNNER_TEMP` directory, verifies the installed identities and current-user
registration, launches the installed application and waits for the real
`Wisp Setup` window, then closes it within a bounded timeout. The canary seeds a
valid completed-setup record, applies the same package with `/WISPUPDATE`, proves
that settings remain byte-identical and setup is not required again, runs the
real uninstaller, and verifies that the application and uninstall registration
are gone. It refuses to run outside GitHub Actions CI or over existing Wisp user
state. The installer is not Authenticode-signed; packaging emits a SHA-256 file
beside it.
