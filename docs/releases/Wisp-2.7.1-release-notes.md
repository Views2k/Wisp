# Wisp 2.7.1

Wisp 2.7.1 is a reliability update. Wisp runs on .NET 10, keeps going through gauge image and display driver failures, keeps listening during setup, and ends update loops.

[Download the latest Wisp release for Windows](https://github.com/Views2k/Wisp/releases/latest)

## Reliability

- A gauge image that fails to load is reported and drawn transparent instead of closing Wisp.
- A gauge window that was already closed no longer closes Wisp when its visibility changes.
- After a display rendering failure ends a run, the dashboard draws in software mode on the next launch; the HUD and clips keep using the graphics card.
- When RivaTuner Statistics Server hooks Wisp, the dashboard explains how to exclude Wisp in RivaTuner.
- The HUD renderer is built again after the graphics device is removed, reset or hung, instead of staying off.
- The gauge update loop reports a failed pass and keeps going; after five failures in a row it stops and the dashboard says so.
- The telemetry listener restarts itself after an unexpected socket error, up to five times with growing delays.
- Settings are backed up on every save and restored from the last good copy when the file cannot be read.
- An update that cannot be staged on this PC is reported on the update card instead of closing Wisp.
- When a HUD window or the feature-control client cannot start, or the runtime start fails, the dashboard stays open and reports it.
- HUD windows are placed again after a monitor is unplugged, turned off or changes resolution: at their saved layout when there is one, otherwise inside the nearest display.
- Wisp closes within seconds when a shift capture or a tune read is still open.
- The clipping preference survives a launch where the recorder could not start, and clipping starts again when a pause is lifted.
- When the HUD update loop stops, the HUD is hidden; the HUD window is retried when a display or the game changes under it, and the renderer keeps retrying while the driver restarts.
- A gauge image shows again after a temporary read failure.
- The HUD works on spanned desktops wider than 8192 pixels.
- An unreadable settings file is kept beside the restored copy.
- Closing a notice or a published banner no longer brings another one back.
- Two weeks after a display driver failure switched the dashboard to software drawing, the graphics card draws it again.

## Setup and connection

- The setup wizard's Data Out test keeps listening and repeats its guidance instead of giving up after 45 seconds.
- When another app holds Wisp's UDP port at startup, Wisp listens on the next free port, says which port to set in Forza, and shows that port in Diagnostics.
- Setup finishes after a Data Out test that listened for more than 45 seconds before the car moved.

## Updates

- Update checks wait for the network when Wisp starts with Windows.
- A release whose installation keeps failing stops automatic attempts after three tries and points at the full installer; a newer release is tried again.
- A connection lost while reading the release service or downloading the installer is reported as a connection failure.
- Update checks and automatic installs start after the setup wizard has finished.

## Diagnostics

- A Windows shutdown or sign-out that ends Wisp is recorded as a close request even while the dashboard is busy.
- Diagnostics tell a Windows shutdown that ended a run apart from a problem in Wisp.
- The Tune page shows the game unit code Forza reports when it is not imperial.
- A stalled dashboard thread is recorded with its length in the run's diagnostics.
- Crash reports carry breadcrumbs for HUD show and hide, tune reads, update handoffs and listener restarts.
- The HUD status for an access-denied game process says to run Wisp and Forza at the same privilege level.
- A locked program file names the process holding it in the local report, looked up off the dashboard thread.
- A recovered failure that repeats is saved once per minute, so other failures keep their place in the crash store.
- A background startup task that fails is reported as soon as it fails, with its classification.
- A disposal that fails at exit is reported.
- A Windows shutdown that is cancelled withdraws its close request, and a shutdown counts as ending the run only within 90 seconds of the run's last checkpoint.

## Runtime

- Wisp runs on .NET 10 and ships the .NET 10.0.13 runtime and Windows Desktop runtime.
