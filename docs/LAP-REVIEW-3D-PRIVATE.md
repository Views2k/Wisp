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

## Private 07 audit fixes

Verified defects and fixes from the private 06 audit and follow-up checks:

- Unmatched Both mode updated B only through the slider. All A selection paths
  now update paired progress; selecting B works symmetrically. Independent A/B
  keeps the other cursor fixed. Relative progress does not enable timing matches.
- Matched B keyboard steps could map back to the same A sample. Directional
  navigation now advances through valid matches and preserves missing-match gaps.
- Default tooltips used a light system background with Wisp's light text. Tooltips
  now use themed colors, wrapping and bounded width; the full-map tooltip is removed.
- Shared-space geometry could be reused after switching to a single track.
  Preparation now includes layout mode in its identity before reusing a scene.
- Track picking ignored depth. Segment picking prefers the visible overpass;
  foreground annotations retain hit priority. Empty space focuses without selection.
- Distant world positions distorted shared-space scale. Each lap is horizontally
  centered with one physical scale and unchanged recorded elevation.
- Map height was lost on reopen; it is now bounded and saved when resizing ends.
  Compact upward resizing no longer jumps to a larger minimum. Home restores auto size.
- Elevation values and legends use feet with mph, meters otherwise. Geometry keeps
  equal physical scale on all axes.
- Filled and hollow contact stars distinguish reported/saved evidence from estimates.
  Unused manual-editor commands, storage method and advice have been removed.

Wheel zoom still requires map focus so page scrolling works naturally. Right-click
or click empty map space to focus without changing the lap point; this is explained
beside the controls. Contact-format corrections and evidence limits are below.

## Contact evidence

The official FH6 Data Out layout documents SmashableVelDiff and SmashableMass:
https://support.forza.net/hc/en-us/articles/51744149102611-Forza-Horizon-6-Data-Out-Documentation

These fields report breakable-object velocity loss and mass, not a general
wall/car collision flag. Object contact requires a continuous observed
zero-to-positive impulse. A repeated game tick is accepted only with unchanged
sample time, identity and position (within 1 cm), without a gap or clock rewind;
repeated lap-clock values do not discard an otherwise continuous object impulse.
Possible contact is a conservative movement estimate. Existing Contact annotations
in saved or imported runs remain visible; the manual contact editor has been removed.
No universal collision accuracy is claimed.
Historical runs discarded these fields and cannot retrospectively recover them.

New journals declare run schema 3 before recording samples, preventing older
readers from recovering a partial prefix and discarding newer contact data.
Finalized recordings and recovered journals with no positive object field omit
the optional fields and use schema 1 or 2, preserving Wisp 2.6.4 readability.
If either field is positive, schema 3 retains every object sample, including zero
baselines, and requires this private build or newer. Existing finalized files and
imports retain their format and data during metadata edits; no migration is performed.

## Platform assessment

Steam: saved-run visualization uses no game memory or executable-specific data.
Object telemetry uses FH6's documented 324-byte Data Out packet contract.
Xbox app / Microsoft Store: the same visualization and packet contract applies;
no platform-specific native reader was changed. No new live-game collision capture
was performed on either edition. Packet fixtures establish parsing, not real-world
coverage of walls, other cars, repeated impacts or dropped packets.

## Verification boundary

Focused checks cover existing channels and interactions, 3D projection/elevation,
color pixels, stale asynchronous scene protection, retained contact annotations,
repeated-clock object impulses, schema recovery/import/export and PNG output. The private
installer retains the standard full packaging gates. Detailed results and source
revision are retained with the delivered installer.

The stored-lap performance check uses one existing run read-only and a synthetic
180,000-point size-limit case. CPU preparation and software-render timing do not
establish gameplay FPS or guarantee every machine's interaction performance.
