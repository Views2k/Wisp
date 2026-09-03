# Wisp 1.0.7

Wisp 1.0.7 is a focused hotfix for HUD responsiveness and stale gallery images.

## Fixed

- Live telemetry delivery no longer depends on WPF's presentation callback. This prevents system-specific background compositor throttling from reducing Wisp's HUD update rate.
- Smoothed and rate-limited the dashboard horsepower readout so rapidly changing power telemetry remains readable without altering the underlying telemetry.
- Gallery screenshots now use new image identities so browsers load the current captures instead of cached copies.

## Install

Download `Wisp-Setup-1.0.7.exe`, or download and extract
`Wisp-Setup-1.0.7.zip`. The installer is self-contained, installs for the
current Windows user, and does not require a separate .NET runtime.

The installer is not code-signed, so Windows may show an unknown-publisher
warning. Verify the SHA-256 file supplied with the release before running it.
