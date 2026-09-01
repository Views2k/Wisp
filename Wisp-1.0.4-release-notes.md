# Wisp 1.0.4

Wisp 1.0.4 keeps the stable 1.0.3 application and simplifies its release
downloads.

## Changes

- The release contains one versioned installer and one installer ZIP.
- GitHub continues to provide the source ZIP and source tarball.
- Application behavior is unchanged from 1.0.3.

## Installation

Download `Wisp-Setup-1.0.4.exe`, or download and extract
`Wisp-Setup-1.0.4.zip`. The installer is self-contained, installs for the
current Windows user, and does not require administrator access or a separate
.NET runtime. It is not code-signed, so Windows may show an
unfamiliar-publisher warning.

## Application updates

The **Check for updates** action in Extras runs only when selected. Wisp accepts
only a non-draft, non-prerelease, immutable GitHub release whose versioned
installer has the expected byte length and SHA-256 digest. The staged helper
validates the installer again, waits for Wisp to exit, applies the current-user
package, verifies the installed version, and restarts Wisp.

## Compatibility

The bundled exact Native compatibility contract supports Steam FH6 build
`6.430.771.0`. Standard local Data Out telemetry continues to provide speed,
RPM, gear, G-force, wheel rotation, tire slip, and power. Native
process-derived values are enabled only when the executable fingerprint matches
the bundled contract.
