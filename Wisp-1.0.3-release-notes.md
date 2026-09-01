# Wisp 1.0.3

Wisp 1.0.3 adds a short trajectory trail to the G-force meter.

## Changes

- The standard and Native G-force meters now draw a connected path through
  recent movement, making direction changes easier to read at a glance.
- The trail retains eight meaningful samples, fades and tapers older movement,
  ignores small positional jiggle, and clears when live telemetry stops.

## Installation

The release package is named `Wisp-Setup-1.0.3.zip`. Extract it, keep the
installer beside its `.sha256` file, verify the checksum, and run
`Wisp-Setup-1.0.3.exe`.

The installer is self-contained, installs for the current Windows user, and
does not require administrator access or a separate .NET runtime. It is not
code-signed, so Windows may show an unfamiliar-publisher warning.

## Application updates

The **Check for updates** action in Extras runs only when selected. Wisp accepts
only a non-draft, non-prerelease, immutable GitHub release whose versioned
installer has the expected byte length and SHA-256 digest. The staged helper
validates the installer again, waits for Wisp to exit, applies the current-user
package, verifies the installed version, and restarts Wisp.

## Compatibility

The bundled exact Native compatibility contract supports Steam FH6 build
`6.430.771.0`. Standard local Data Out telemetry continues to provide speed,
RPM, gear, G-force, wheel rotation, tire slip, and power. Native process-derived
values are enabled only when the executable fingerprint matches the bundled
contract.
