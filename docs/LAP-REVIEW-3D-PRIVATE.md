# Lap review 3D private test

The 2D review stays the default. 3D uses saved XYZ positions with the same scale
on every axis; the ground projection is a depth cue, not a terrain reconstruction.
Camera and cursor changes reuse frozen geometry. Scene preparation is cancellable
and runs off the UI thread. Dense display paths retain height and selected-channel
extrema, while cursor and analysis retain the original full-resolution samples.

All fourteen existing channels, wheel selection, Run B / benchmark comparison,
shared cursor, selected sections, readouts and graph handoff are retained.
Horsepower, torque and elevation are additional channels. PNG export includes
the current camera, channel legend and selected-point value, uses a bounded image,
and saves atomically without overwriting existing files.

## Contact evidence

The official FH6 Data Out layout documents SmashableVelDiff and SmashableMass:
https://support.forza.net/hc/en-us/articles/51744149102611-Forza-Horizon-6-Data-Out-Documentation

These fields report breakable-object velocity loss and mass, not a general
wall/car collision flag. Object contact requires a continuous observed
zero-to-positive impulse. Possible contact is a conservative movement estimate;
manual Contact markers remain available. No universal collision accuracy is claimed.
Historical runs discarded these fields and cannot retrospectively recover them.

New journals declare run schema 3 before recording samples, preventing older
readers from recovering a partial prefix and discarding newer contact data.
Legacy v1/v2 runs stay readable and retain their format when editing annotations.
This private build or newer is required to open newly recorded v3 runs.

## Platform assessment

Steam: saved-run visualization uses no game memory or executable-specific data.
Object telemetry uses FH6's documented 324-byte Data Out packet contract.
Xbox app / Microsoft Store: the same visualization and packet contract applies;
no platform-specific native reader was changed. No new live-game collision capture
was performed on either edition. Packet fixtures establish parsing, not real-world
coverage of walls, other cars, repeated impacts or dropped packets.

## Verification boundary

Focused checks cover existing channels and interactions, 3D projection/elevation,
color pixels, stale asynchronous scene protection, contact annotation persistence,
shutdown during save, schema recovery/import/export and PNG output. The private
installer retains the standard full packaging gates. Detailed results and source
revision are retained with the delivered installer.

The stored-lap performance check uses one existing run read-only and a synthetic
180,000-point size-limit case. CPU preparation and software-render timing do not
establish gameplay FPS or guarantee every machine's interaction performance.
