# Wisp 2.7.1

Wisp 2.7.1 is a reliability update. Wisp runs on .NET 10, keeps going through gauge image and display driver failures, keeps listening during setup, and ends update loops.

[Download the latest Wisp release for Windows](https://github.com/Views2k/Wisp/releases/latest)

## Reliability

- A gauge image that fails to load is reported and drawn transparent instead of closing Wisp.
- A gauge window that was already closed no longer closes Wisp when its visibility changes.
- After a display driver failure ends a run, the dashboard draws in software mode on the next launch; the HUD and clips keep using the graphics card.
- When RivaTuner Statistics Server hooks Wisp, the dashboard explains how to exclude Wisp in RivaTuner.
- The HUD renderer is built again after the graphics device is removed, reset or hung, instead of staying off.
- The gauge update loop reports a failed pass and keeps going; after five failures in a row it stops and the dashboard says so.
- The telemetry listener restarts itself after an unexpected socket error, up to five times with growing delays.
- Settings are backed up on every save and restored from the last good copy when the file cannot be read.

## Setup and connection

- The setup wizard's Data Out test keeps listening and repeats its guidance instead of giving up after 45 seconds.
- When another app holds Wisp's UDP port at startup, Wisp listens on the next free port and says which port to set in Forza.

## Updates

- Update checks wait for the network when Wisp starts with Windows.
- A release whose installation keeps failing stops automatic attempts after three tries and points at the full installer.

## Diagnostics

- A Windows shutdown or sign-out that ends Wisp is recorded as a close request even while the dashboard is busy.
- Diagnostics tell a Windows shutdown that ended a run apart from a problem in Wisp.
- The Tune page names the unit system Forza reports when it is not imperial.
- A stalled dashboard thread is recorded with its length in the run's diagnostics.
- Crash reports carry breadcrumbs for HUD show and hide, tune reads, update handoffs and listener restarts.
- The HUD status for an access-denied game process says to run Wisp and Forza at the same privilege level.
- A locked program file names the process holding it in the local report.

## Runtime

- Wisp runs on .NET 10 and ships the .NET 10.0.13 runtime and Windows Desktop runtime.
